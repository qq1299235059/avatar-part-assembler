using System;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Resolves the live objects a finished plan must be written into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The core's <see cref="ValidationContext"/> deliberately holds no Unity objects: it captures values, not
    /// references (section 43.10). That is what makes planning immutable and testable without the Editor, and it
    /// means the assembly layer has to resolve the identities the plan recorded back to live objects on the
    /// clone it is mutating. This type is that step, and it is deliberately separable from the mutation itself:
    /// everything that can fail is resolved here, before a single property is written.
    /// </para>
    /// <para>
    /// Every identity is an avatar-root-relative path (<see cref="ApaAvatarPath"/>), resolved with the same rule
    /// the rest of the pipeline uses: the root token <c>"."</c> is the avatar root, the empty string is "missing"
    /// and never resolved, and anything else goes to <c>Transform.Find</c>. That is the same rule the core used
    /// when it resolved the target renderer from the profile's recorded path, so a path that was found then is
    /// found now.
    /// </para>
    /// </remarks>
    internal static class ApaBuildTargets
    {
        /// <summary>Everything one successful plan must be written into.</summary>
        /// <remarks>
        /// Exactly one of <see cref="Skinned"/> and <see cref="MeshFilter"/> is non-null for a skinned target and
        /// an unskinned one respectively, and <see cref="Bones"/> is non-null only when the plan carries a bone
        /// table. The set is produced before mutation and never revised during it.
        /// </remarks>
        internal sealed class MutationTarget
        {
            internal Renderer Renderer;
            internal SkinnedMeshRenderer Skinned;
            internal MeshFilter MeshFilter;
            internal Transform[] Bones;
        }

        /// <summary>
        /// Resolves the target renderer and, when the plan is skinned, every bone transform, in final table order.
        /// </summary>
        /// <returns>True when the whole plan can be written; false with one blocking issue when it cannot.</returns>
        internal static bool TryResolve(
            GameObject avatarRoot,
            ValidationContext context,
            MeshAssemblyPlan plan,
            out MutationTarget target,
            out ValidationIssue issue)
        {
            target = null;
            issue = null;

            if (avatarRoot == null || context == null || context.Base == null || plan == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "The assembly layer was asked to resolve a target without an avatar root, a context, or a plan.",
                    detail: "reason=null-resolve-input");
                return false;
            }

            var rendererPath = context.Base.RendererPath;
            var transform = ResolveTransform(avatarRoot, rendererPath);
            var renderer = transform != null ? transform.GetComponent<Renderer>() : null;

            if (renderer == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target body renderer recorded as '" + rendererPath + "' no longer resolves on the build " +
                    "clone, so the assembled mesh has nowhere to go.",
                    detail: "reason=target-renderer-unresolved; renderer=" + rendererPath);
                return false;
            }

            var resolved = new MutationTarget
            {
                Renderer = renderer,
                Skinned = renderer as SkinnedMeshRenderer
            };

            if (plan.RequiresSkinning)
            {
                // Skinning is written as bone weights plus a bones array plus bind poses. A non-skinned renderer
                // cannot carry the first two, and silently assembling a skinned mesh into a MeshRenderer would
                // ship vertices that never deform.
                if (resolved.Skinned == null)
                {
                    issue = ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "The plan is skinned but the target renderer '" + renderer.name + "' is not a " +
                        "SkinnedMeshRenderer, so the generated bone weights and bind poses cannot be applied.",
                        detail: "reason=target-renderer-not-skinned; renderer=" + rendererPath);
                    return false;
                }

                if (!TryResolveBones(avatarRoot, context, plan.BoneTable, out var bones, out issue)) return false;
                resolved.Bones = bones;
            }
            else if (resolved.Skinned == null)
            {
                resolved.MeshFilter = renderer.GetComponent<MeshFilter>();
                if (resolved.MeshFilter == null)
                {
                    issue = ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "The target renderer '" + renderer.name + "' has no MeshFilter, so an unskinned generated " +
                        "mesh cannot be assigned.",
                        detail: "reason=target-mesh-filter-missing; renderer=" + rendererPath);
                    return false;
                }
            }

            target = resolved;
            return true;
        }

        /// <summary>
        /// Resolves every final bone to its live transform, in final table order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bone table's order is the order of <c>SkinnedMeshRenderer.bones</c> and of the bind pose array, so
        /// one unresolved bone would shift no index but would leave one joint silently unbound. The whole
        /// assignment is refused instead, and the diagnostic names the bone and its index.
        /// </para>
        /// <para>
        /// <b>Paths are resolved under the armature they were recorded against (M10).</b> A bone identity is its
        /// path relative to the selected target armature, and by the time this runs Modular Avatar has already
        /// merged the part's armature into that one, so every final bone — body-owned, merged, or a part bone the
        /// body did not have — is reachable from there. Resolving against the avatar root instead would leave
        /// every bone unresolved on any avatar whose armature is not the root.
        /// </para>
        /// <para>
        /// A context with no recorded scope resolves against the avatar root, which is the pre-M10 form and what
        /// a hand-built context means. A recorded scope that no longer resolves blocks rather than falling back:
        /// a silent fallback would mis-resolve every bone at once.
        /// </para>
        /// </remarks>
        internal static bool TryResolveBones(
            GameObject avatarRoot,
            ValidationContext context,
            FinalBoneTable table,
            out Transform[] bones,
            out ValidationIssue issue)
        {
            bones = null;
            issue = null;

            if (table == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "The plan requires skinning but carries no bone table.",
                    detail: "reason=missing-bone-table");
                return false;
            }

            var scopePath = context?.Base?.BoneScopePath ?? string.Empty;
            Transform scope;
            if (ApaAvatarPath.HasIdentity(scopePath))
            {
                scope = ResolveTransform(avatarRoot, scopePath);
                if (scope == null)
                {
                    issue = ValidationIssue.Error(
                        ApaErrorCode.ArmatureSelectionInvalid,
                        ApaIssuePhase.Attributes,
                        "The armature the profile's bone identities are relative to ('" + scopePath +
                        "') does not resolve on the build clone, so no final bone can be located. Re-select the " +
                        "target armature in the Part Authoring window; the hierarchy it was recorded against has " +
                        "changed.",
                        detail: "reason=bone-scope-unresolved; scope=" + scopePath);
                    return false;
                }
            }
            else
            {
                scope = avatarRoot != null ? avatarRoot.transform : null;
            }

            var resolved = new Transform[table.Count];
            for (var i = 0; i < table.Count; i++)
            {
                var bone = table.Bones[i];
                var transform = bone != null ? ResolveTransform(scope, bone.Path) : null;
                if (transform == null)
                {
                    var path = bone != null ? bone.Path : string.Empty;
                    issue = ValidationIssue.Error(
                        ApaErrorCode.TargetBoneNotFound,
                        ApaIssuePhase.Attributes,
                        "The final bone '" + path + "' does not resolve on the build clone, so the generated mesh " +
                        "would be skinned to a joint that does not exist.",
                        bone != null ? bone.OwnerPartId : string.Empty,
                        i,
                        detail: "reason=bone-not-found; bone=" + path +
                                (ApaAvatarPath.HasIdentity(scopePath) ? "; scope=" + scopePath : string.Empty));
                    return false;
                }

                resolved[i] = transform;
            }

            bones = resolved;
            return true;
        }

        /// <summary>Resolves a path recorded against a GameObject root to a live transform, or null.</summary>
        internal static Transform ResolveTransform(GameObject avatarRoot, string path)
        {
            return avatarRoot != null ? ResolveTransform(avatarRoot.transform, path) : null;
        }

        /// <summary>
        /// Resolves a path recorded against a transform root to a live transform, or null.
        /// </summary>
        /// <remarks>
        /// <c>Transform.Find</c> reserves <c>"."</c>, so the root token is handled before the lookup rather than
        /// passed through it, and the empty string is "missing" rather than a lookup. Since M10 the token and the
        /// path are relative to the root the caller passes, which is the selected armature rather than necessarily
        /// the avatar root.
        /// </remarks>
        internal static Transform ResolveTransform(Transform root, string path)
        {
            if (root == null || !ApaAvatarPath.HasIdentity(path)) return null;

            // Transform.Find reserves ".", so the root token is handled explicitly; a path that is not the root
            // token is looked up exactly as the core looks it up when it resolves a compatibility signature.
            return ApaAvatarPath.IsRoot(path) ? root : root.Find(path);
        }
    }
}

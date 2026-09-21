using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// Registers every input that can change the preview output with an NDMF <see cref="ComputeContext"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class exists as its own seam because "which inputs are observed" is the part of a preview filter that
    /// is hardest to review and easiest to get subtly wrong. Everything the core reads is listed here once, and
    /// both discovery and node construction call the same method, so an input cannot be observed at one stage and
    /// forgotten at the other.
    /// </para>
    /// <para>
    /// <b>Two observation styles, for two different kinds of change.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>ctx.Observe(obj)</c> subscribes to NDMF's change stream: it fires for Undo-recorded edits, for
    /// destruction, and for hierarchy changes. It is the right tool for an asset whose serialized content cannot
    /// be summarized cheaply (a profile, a material, a mesh).
    /// </description></item>
    /// <item><description>
    /// <c>ctx.Observe(obj, extract)</c> additionally polls <c>extract</c> through NDMF's property monitor, which
    /// is what catches changes made without Undo. The extractors used here return small value tuples of scalars
    /// and references, because the poll runs once per registered object per editor frame inside NDMF's 2 ms time
    /// slice; a content hash of a mesh in that position would be a per-frame stall, not a preview.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Content is covered by the capture, not by the poll.</b> Mesh vertices, blend shape deltas, bind data,
    /// and profile-derived data are hashed into the request fingerprint by
    /// <see cref="ApaPreviewFingerprint.OfContext"/> when discovery captures them, and compared again when a node
    /// verifies itself during refresh. The poll extractors therefore only have to notice that <i>something</i>
    /// changed — a different mesh reference, a different set of bone or material identities, a different
    /// transform — for the fingerprint to be recomputed and compared.
    /// </para>
    /// <para>
    /// <b>Bone pose is not a rebuild input.</b> The generated proxy keeps live bone references, so a local TRS edit
    /// must deform the current mesh instead of rebuilding bind poses from the edited state. Bone hierarchy/name
    /// changes remain observed because they change path resolution.
    /// </para>
    /// <para>
    /// <b>Each object is registered once per call.</b> Several inputs overlap by construction: an installer's
    /// explicit target renderer is usually the resolved body renderer, and two parts authored against the same
    /// armature share their bone transforms. Instance ids are deduplicated across the whole call so that a bone is
    /// polled once rather than once per part that references it.
    /// </para>
    /// <para>
    /// <b>A transform is observed with its whole ancestor path.</b> The core captures world matrices, so a
    /// container between the avatar root and a renderer or bone is as much an input as the renderer itself; see
    /// <see cref="ObserveTransform(ComputeContext, Transform)"/> for why the leaf alone is not enough.
    /// </para>
    /// <para>
    /// <b>Blend shape weights are deliberately not observed.</b> They are read from the source renderers on every
    /// frame and written to the proxy, so a weight change changes the picture without changing the assembled
    /// mesh. Observing them would invalidate the pipeline on every animation-mode scrub for no benefit.
    /// </para>
    /// </remarks>
    public static class ApaPreviewInputObserver
    {
        /// <summary>
        /// Observes an avatar and everything the preview would read from it.
        /// </summary>
        /// <param name="context">The compute context to register with.</param>
        /// <param name="avatarRoot">The avatar root.</param>
        /// <param name="allInstallers">Every installer under the root, enabled or not.</param>
        /// <param name="targetRenderer">The resolved target body renderer, or null when unresolved.</param>
        public static void Observe(
            ComputeContext context,
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> allInstallers,
            Renderer targetRenderer)
        {
            Observe(
                context,
                avatarRoot,
                allInstallers,
                targetRenderer != null ? new[] { targetRenderer } : System.Array.Empty<Renderer>());
        }

        /// <summary>
        /// Observes an avatar and every target renderer of its target groups.
        /// </summary>
        /// <param name="context">The compute context to register with.</param>
        /// <param name="avatarRoot">The avatar root.</param>
        /// <param name="allInstallers">Every installer under the root, active or not.</param>
        /// <param name="targetRenderers">
        /// Every group's resolved target renderer. Each one is observed; duplicates are collapsed.
        /// </param>
        public static void Observe(
            ComputeContext context,
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> allInstallers,
            IReadOnlyList<Renderer> targetRenderers)
        {
            if (context == null || avatarRoot == null) return;

            var scope = new ObservationScope();

            // The root itself: its own properties and its children. A reparent or a rename changes every
            // avatar-root-relative path the core records, so the root's path must invalidate as well.
            context.Observe(avatarRoot);
            ObserveTransform(context, avatarRoot.transform, scope);

            if (allInstallers != null)
            {
                for (var i = 0; i < allInstallers.Count; i++)
                {
                    ObserveInstaller(context, allInstallers[i], scope);
                }
            }

            if (targetRenderers != null)
            {
                for (var i = 0; i < targetRenderers.Count; i++)
                {
                    ObserveRenderer(context, targetRenderers[i], scope);
                }
            }
        }

        /// <summary>Observes one installer, its profile, its part root, and the renderers it points at.</summary>
        public static void ObserveInstaller(ComputeContext context, AvatarPartInstaller installer)
        {
            ObserveInstaller(context, installer, new ObservationScope());
        }

        /// <summary>Observes a renderer: its state, mesh, materials, bones, and transform.</summary>
        public static void ObserveRenderer(ComputeContext context, Renderer renderer)
        {
            ObserveRenderer(context, renderer, new ObservationScope());
        }

        /// <summary>Observes a mesh: change-stream events plus a cheap structural revision.</summary>
        public static void ObserveMesh(ComputeContext context, Mesh mesh)
        {
            ObserveMesh(context, mesh, new ObservationScope());
        }

        /// <summary>
        /// Observes a transform and every node of its ancestor path: local position, rotation, and scale, the
        /// parent, and the name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The leaf alone is not enough.</b> The core captures world matrices — the part-to-target
        /// <c>sourceToTarget</c> transform, the body's <c>rendererLocalToWorld</c>, every bone's world-to-local —
        /// so moving a container that sits between the avatar root and a renderer or a bone changes the assembled
        /// result exactly as much as moving the renderer itself. A child's transform edit does not notify its
        /// parent (<c>ShadowGameObject</c> fires the changed object), and <c>ObservePath</c> only monitors
        /// reparenting, so a path node that is read but never polled is a stale preview. NDMF's own
        /// <c>ObserveTransformPosition</c> therefore walks the path and polls every node, and so does this.
        /// </para>
        /// <para>
        /// The name and the parent are part of the token for every node, not just the leaf: avatar-root-relative
        /// paths are names, so renaming an intermediate container changes which transform a recorded bone or
        /// renderer path resolves to. The comparison is exact for the same reason the core's ordering is exact: a
        /// preview that is "almost" the build is the failure mode this package exists to avoid.
        /// </para>
        /// </remarks>
        public static void ObserveTransform(ComputeContext context, Transform transform)
        {
            ObserveTransform(context, transform, new ObservationScope());
        }

        private static void ObserveInstaller(
            ComputeContext context,
            AvatarPartInstaller installer,
            ObservationScope scope)
        {
            if (context == null || installer == null) return;

            if (scope.Objects.Add(installer.GetInstanceID()))
            {
                // Component-level observation: adding, removing, or Undo-editing the component.
                context.Observe(installer);

                // Poll-level observation of the fields discovery reads. The profile and part-root references are
                // included as references; their contents are covered below and by the capture fingerprint.
                context.Observe(installer, InstallerToken);
            }

            ObserveTransform(context, installer.transform, scope);

            var profile = installer.Profile;
            if (profile != null && scope.Objects.Add(profile.GetInstanceID()))
            {
                // Serialized profile data, including nested arrays. Asset property changes are reported by NDMF's
                // change stream, which is why this object is observed without a content extractor.
                context.Observe(profile);
                context.Observe(profile, ProfileToken);
            }

            // A protected part's geometry lives in a payload asset, not in a mesh, so editing that asset has to
            // invalidate the preview exactly like editing a mesh would. The change stream covers an Undo-recorded
            // edit; the content token covers a write made without Undo, which is how a replaced payload usually
            // arrives. The token is the payload's cheap content revision, not a hash of the ciphertext on every
            // frame: the asset caches the full hash and only recomputes it when its sample of the bytes changes.
            var protectedMesh = installer.ProtectedMesh;
            if (protectedMesh != null && scope.Objects.Add(protectedMesh.GetInstanceID()))
            {
                context.Observe(protectedMesh);
                context.Observe(protectedMesh, ProtectedMeshToken);
            }

            ObserveRenderer(context, RendererOf(installer.TargetRendererObject), scope);

            var partRoot = installer.ResolvePartRoot();
            if (partRoot != null && scope.Objects.Add(partRoot.GetInstanceID()))
            {
                context.Observe(partRoot);
            }

            // The first renderer under the part root is the one the core captures geometry from, so it is the one
            // whose mesh, materials, bones, and transform can change the output.
            ObserveRenderer(context, partRoot != null ? partRoot.GetComponentInChildren<Renderer>(true) : null, scope);

            // ObserveRenderer registers the part armature's structural path first. If the part root is itself a
            // bone, this ordering prevents the generic part-root transform observation below from reintroducing
            // live bone TRS as a rebuild input.
            if (partRoot != null) ObserveTransform(context, partRoot.transform, scope);
        }

        private static void ObserveRenderer(ComputeContext context, Renderer renderer, ObservationScope scope)
        {
            if (context == null || renderer == null) return;
            if (!scope.Objects.Add(renderer.GetInstanceID())) return;

            context.Observe(renderer);
            context.Observe(renderer, RendererToken);

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                // Register the structural path before the renderer path. A renderer or its ancestor can also be
                // an armature node; in that case the generic transform observer must not poll live bone TRS.
                ObserveBoneStructure(context, skinned.rootBone, scope);

                var bones = skinned.bones;
                if (bones != null)
                {
                    for (var i = 0; i < bones.Length; i++)
                    {
                        ObserveBoneStructure(context, bones[i], scope);
                    }
                }
            }

            ObserveTransform(context, renderer.transform, scope);

            ObserveMesh(context, SharedMeshOf(renderer), scope);

            var materials = renderer.sharedMaterials;
            if (materials != null)
            {
                for (var i = 0; i < materials.Length; i++)
                {
                    // A material asset's own properties (shader keywords, textures, colors) are observed so that
                    // editing one refreshes the preview. Materials are never written to or destroyed here.
                    if (materials[i] != null && scope.Objects.Add(materials[i].GetInstanceID()))
                    {
                        context.Observe(materials[i]);
                    }
                }
            }

        }

        private static void ObserveMesh(ComputeContext context, Mesh mesh, ObservationScope scope)
        {
            if (context == null || mesh == null) return;
            if (!scope.Objects.Add(mesh.GetInstanceID())) return;

            context.Observe(mesh);
            context.Observe(mesh, MeshToken);
        }

        private static void ObserveTransform(ComputeContext context, Transform transform, ObservationScope scope)
        {
            if (context == null || transform == null) return;

            // ObservePath registers the reparenting monitor and returns the leaf followed by every ancestor, which
            // is exactly the set of transforms whose local TRS decides the world matrices the core captures.
            foreach (var node in context.ObservePath(transform))
            {
                if (node == null) continue;
                if (!scope.Transforms.Add(node.GetInstanceID())) continue;

                // A skinned renderer may sit on or below an armature node. Its path still needs structural
                // monitoring, but the node's local TRS is live pose state and must not be registered through the
                // generic transform token when ObserveBoneStructure has already claimed it.
                if (scope.BoneStructures.Contains(node.GetInstanceID())) continue;

                context.Observe(node);
                context.Observe(node, TransformToken);
            }
        }

        private static void ObserveBoneStructure(ComputeContext context, Transform transform, ObservationScope scope)
        {
            if (context == null || transform == null) return;

            foreach (var node in context.ObservePath(transform))
            {
                if (node == null) continue;
                if (!scope.BoneStructures.Add(node.GetInstanceID())) continue;

                // Do not poll localPosition/localRotation/localScale here. Those values are the avatar's live
                // pose and are consumed by Unity skinning through the proxy's bones array. Parent/name changes
                // still alter armature-relative identity and therefore remain cache inputs.
                context.Observe(node, BoneStructureToken);
            }
        }

        /// <summary>
        /// The per-call deduplication of what this observer registers.
        /// </summary>
        /// <remarks>
        /// Transforms are tracked separately from other objects because <see cref="ObserveTransform"/> registers a
        /// whole ancestor path: a renderer that is also an ancestor of a bone must still get its own explicit
        /// observation, and a transform reached through two different paths must be polled once.
        /// </remarks>
        private sealed class ObservationScope
        {
            /// <summary>Installers, profiles, part roots, renderers, meshes, and materials.</summary>
            internal readonly HashSet<int> Objects = new HashSet<int>();

            /// <summary>Transforms, including every ancestor of every observed transform.</summary>
            internal readonly HashSet<int> Transforms = new HashSet<int>();

            /// <summary>Bone hierarchy nodes observed for identity changes, independent of pose observations.</summary>
            /// <remarks>
            /// A node can also be a renderer/part-root transform. Keeping this set separate ensures that an
            /// earlier generic transform observation cannot accidentally re-enable live bone TRS invalidation.
            /// </remarks>
            internal readonly HashSet<int> BoneStructures = new HashSet<int>();
        }

        private static Renderer RendererOf(GameObject gameObject)
        {
            return gameObject == null ? null : gameObject.GetComponent<Renderer>();
        }

        private static Mesh SharedMeshOf(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        // --- Cheap poll extractors -------------------------------------------------------------------------
        //
        // Each extractor returns only scalars and object references. Value tuples compare structurally with the
        // default equality comparer, so every field must be a value whose equality means "reads the same value":
        // a *collection* is represented by a content-stable hash of its element identities, never by the array
        // itself. Unity's sharedMaterials and bones getters allocate a new array on every access, so a token that
        // carried the arrays would compare unequal on every poll and invalidate the whole preview pipeline once
        // per editor frame. The hash is over element instance ids only, which is exactly the change the poll has
        // to notice: editing a material's contents is covered by the material observation, and editing mesh
        // contents is covered by the capture fingerprint.

        private static (bool enabledForBuild, ApaPartProfile profile, GameObject partRoot, GameObject target) InstallerToken(
            AvatarPartInstaller installer)
        {
            if (installer == null) return (false, null, null, null);
            return (installer.EnabledForBuild, installer.Profile, installer.PartRoot, installer.TargetRendererObject);
        }

        private static (int schemaVersion, string partId, bool supported) ProfileToken(
            ApaPartProfile profile)
        {
            if (profile == null) return (-1, null, false);

            // IdentityOrNull, never Identity: the profile is a shared authoring asset and the materializing
            // getter would assign a new nested object into it. This poll runs from a preview path, which is
            // forbidden from writing an asset (see ApaPartProfile.EnsureInitialized).
            var identity = profile.IdentityOrNull;
            return (
                profile.SchemaVersion,
                identity != null ? identity.PartId : null,
                profile.IsSchemaSupported);
        }

        /// <summary>
        /// The poll token of a protected payload: whether it has one, and the revision of its bytes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="ApaProtectedMeshAsset.ContentRevision"/> is a cached hash of the ciphertext, invalidated by
        /// a cheap sample of the array's identity, length, and first and last bytes. Reading it is therefore a
        /// handful of byte comparisons on an unchanged payload, and one full hash pass when the payload actually
        /// changed — which is the correct price for noticing a change that was made without Undo.
        /// </para>
        /// <para>
        /// The declared plaintext length and the salt/IV/tag lengths are part of the token as well: a payload
        /// whose ciphertext happens to hash the same while its envelope was rebuilt is a different payload, and
        /// the decode cache keys on the same values.
        /// </para>
        /// </remarks>
        private static (bool present, int revision, int plaintextLength, int envelopeLength) ProtectedMeshToken(
            ApaProtectedMeshAsset asset)
        {
            if (asset == null) return (false, 0, 0, 0);

            return (
                true,
                asset.ContentRevision,
                asset.PlaintextLength,
                asset.CiphertextLength + ApaProtectedMeshAsset.SaltLength +
                ApaProtectedMeshAsset.IvLength + ApaProtectedMeshAsset.TagLength);
        }

        private static (Mesh mesh, int materialsHash, int bonesHash, Transform rootBone, bool enabled,
            Bounds bounds) RendererToken(Renderer renderer)
        {
            if (renderer == null) return (null, -1, -1, null, false, default);

            var skinned = renderer as SkinnedMeshRenderer;
            return (
                SharedMeshOf(renderer),
                ElementIdentityHash(renderer.sharedMaterials),
                ElementIdentityHash(skinned != null ? skinned.bones : null),
                skinned != null ? skinned.rootBone : null,
                renderer.enabled,
                renderer.localBounds);
        }

        /// <summary>
        /// A content-stable hash of an object array's element identities, or -1 when the array itself is null.
        /// </summary>
        /// <remarks>
        /// Two arrays with the same elements in the same order hash equal even though they are different array
        /// instances, which is what makes the poll token comparable without allocating a comparison. A null
        /// array (-1) is told apart from an empty one (the seed value), so "no bones" and "no bone array" stay
        /// distinct changes for the same reason.
        /// </remarks>
        private static int ElementIdentityHash<T>(T[] items) where T : Object
        {
            if (items == null) return -1;

            unchecked
            {
                var hash = 17;
                for (var i = 0; i < items.Length; i++)
                {
                    hash = hash * 31 + (items[i] != null ? items[i].GetInstanceID() : 0);
                }

                return hash;
            }
        }

        private static (Vector3 position, Quaternion rotation, Vector3 scale, Transform parent, string name)
            TransformToken(Transform transform)
        {
            if (transform == null) return (default, default, default, null, null);
            return (transform.localPosition, transform.localRotation, transform.localScale, transform.parent,
                transform.name);
        }

        private static (Transform parent, string name) BoneStructureToken(Transform transform)
        {
            if (transform == null) return (null, null);
            return (transform.parent, transform.name);
        }

        private static (int vertexCount, int subMeshCount, int blendShapeCount, Bounds bounds, int indexFormat)
            MeshToken(Mesh mesh)
        {
            if (mesh == null) return (-1, -1, -1, default, -1);
            return (mesh.vertexCount, mesh.subMeshCount, mesh.blendShapeCount, mesh.bounds, (int)mesh.indexFormat);
        }
    }
}

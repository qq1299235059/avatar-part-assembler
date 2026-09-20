using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// Substitutes a resolved bone table's part-side bones with hidden shadow bones parented under their avatar
    /// counterparts, so the preview follows avatar motion the way the build's merge does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At build time the armature merge reparents each matched part bone under its avatar counterpart with its
    /// world pose preserved, and everything hanging under a reparented bone moves with it. The preview draws the
    /// authoring hierarchy, where the part armature is still a sibling of the avatar armature, so a part bone in
    /// the proxy's bone table follows the part's own hierarchy and ignores avatar motion — the drawn mesh and the
    /// visible skeleton drift apart the moment an avatar bone is posed.
    /// </para>
    /// <para>
    /// A shadow bone closes that gap the same way the build does. For a merge-matched bone it is created at the
    /// bone's current world pose and then parented under the avatar bone the merge would give it, so the hierarchy
    /// moves it exactly as the merged bone would move. A bone the merge does not match itself is mirrored under
    /// its nearest shadowed ancestor, recreating plain hierarchy nodes in between so the local chain is preserved.
    /// The part's own transforms are never touched, every created object is reported to
    /// <see cref="ObjectRegistry"/>, and each one is handed back to the caller, which owns its lifetime.
    /// </para>
    /// </remarks>
    internal static class ApaPreviewShadowBones
    {
        /// <summary>
        /// Returns a bone table that follows avatar motion, creating shadow bones for the bones the build's merge
        /// would reparent.
        /// </summary>
        /// <param name="source">The bone map resolved from the plan, or null.</param>
        /// <param name="created">
        /// Every created GameObject is added to this list, so the owning node can destroy them. Never null.
        /// </param>
        /// <returns>
        /// A substituted map, or <paramref name="source"/> itself when nothing needed substituting — no skinning,
        /// no merge information, or no bone with a shadowed ancestor.
        /// </returns>
        internal static ApaPreviewBoneMap Attach(ApaPreviewBoneMap source, List<GameObject> created)
        {
            if (source == null || created == null) return source;
            if (source.MergeParents == null) return source;

            var live = source.Bones;
            if (live == null || live.Length == 0) return source;

            var shadows = new Dictionary<Transform, Transform>();
            Transform[] resolved = null;

            // First pass: every bone the merge would reparent gets a shadow under its avatar counterpart, at the
            // bone's current world pose — the same pose the merged bone keeps at build time.
            for (var i = 0; i < live.Length; i++)
            {
                var bone = live[i];
                var mergeParent = source.MergeParents[i];
                if (bone == null || mergeParent == null) continue;

                if (resolved == null) resolved = (Transform[])live.Clone();
                resolved[i] = ShadowOf(bone, mergeParent, shadows, created);
            }

            if (resolved == null) return source;

            // Second pass: a bone the merge does not match itself still follows the matched ancestor it hangs
            // under — reparenting a bone moves its whole subtree with it. Mirror each remaining entry under its
            // nearest shadowed ancestor; a chain with no shadowed ancestor is one the build leaves where it is,
            // so it stays live here too.
            for (var i = 0; i < live.Length; i++)
            {
                var bone = live[i];
                if (bone == null || !ReferenceEquals(resolved[i], bone)) continue;

                var shadow = ShadowOf(bone, null, shadows, created);
                if (shadow != null) resolved[i] = shadow;
            }

            return ApaPreviewBoneMap.Create(resolved, source.RootBone, source.MissingPaths);
        }

        /// <summary>
        /// Returns the shadow of a bone, creating it when it does not exist yet.
        /// </summary>
        /// <param name="bone">The live bone to mirror.</param>
        /// <param name="mergeParent">
        /// The avatar bone to parent the shadow under for a merge-matched bone, or null to parent it under the
        /// shadow of the bone's live parent instead.
        /// </param>
        /// <param name="shadows">Live transform to shadow, accumulated across the whole substitution.</param>
        /// <param name="created">The caller's list every created GameObject is recorded in.</param>
        /// <returns>The shadow transform, or null when the bone has no shadowed ancestor.</returns>
        private static Transform ShadowOf(
            Transform bone,
            Transform mergeParent,
            Dictionary<Transform, Transform> shadows,
            List<GameObject> created)
        {
            if (shadows.TryGetValue(bone, out var existing)) return existing;

            Transform parent;
            if (mergeParent != null)
            {
                parent = mergeParent;
            }
            else
            {
                var liveParent = bone.parent;
                if (liveParent == null) return null;

                parent = ShadowOf(liveParent, null, shadows, created);
                if (parent == null) return null;
            }

            var shadow = NewHiddenObject("APA Shadow " + bone.name, created);
            if (mergeParent != null)
            {
                // World pose first, then reparent with world preservation: the merged bone keeps its world pose
                // at build time and follows its new parent from there.
                shadow.transform.SetPositionAndRotation(bone.position, bone.rotation);
                shadow.transform.localScale = bone.lossyScale;
                shadow.transform.SetParent(parent, true);
            }
            else
            {
                // The build moves an unmatched bone with its parent, so its local chain is what has to survive.
                shadow.transform.SetParent(parent, false);
                shadow.transform.localPosition = bone.localPosition;
                shadow.transform.localRotation = bone.localRotation;
                shadow.transform.localScale = bone.localScale;
            }

            var shadowTransform = shadow.transform;
            shadows[bone] = shadowTransform;
            ObjectRegistry.RegisterReplacedObject(bone.gameObject, shadow);
            return shadowTransform;
        }

        /// <summary>Creates a hidden, transient GameObject the caller records for destruction.</summary>
        private static GameObject NewHiddenObject(string name, List<GameObject> created)
        {
            var shadow = new GameObject(name);
            shadow.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;
            created.Add(shadow);
            return shadow;
        }
    }
}

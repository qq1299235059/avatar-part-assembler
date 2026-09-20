using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// The strict predicate that decides whether a consumed source object may be removed from the build clone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assembly consumes a part renderer by destroying its <i>component</i> only, deliberately leaving the
    /// object and everything under it alone, because the generated mesh may be skinned to bones that live there.
    /// What is left on the renderer's own object is often nothing but a Transform, and an object that draws
    /// nothing, holds nothing, and owns nothing is dead weight in the uploaded avatar. This predicate is the
    /// boundary between "dead weight" and "still the author's": it answers true only when the object is provably
    /// empty.
    /// </para>
    /// <para>
    /// <b>Empty means exactly this.</b> The object is alive, it is not the avatar root, it has a parent, it has no
    /// children, and every component it still carries is a <see cref="Transform"/>. A missing (unresolvable)
    /// component entry is treated as a component, so an object carrying a broken script reference is <i>not</i>
    /// empty: the safe direction is to keep an object, never to destroy something that might still mean
    /// something. A constraint, a PhysBone, a menu installer, an authoring component, a leftover Modular Avatar
    /// component — any of them makes the object non-empty and it is kept.
    /// </para>
    /// <para>
    /// <b>Why the check is separate from the removal.</b> It is the part of the feature a caller can test without
    /// a build, and it is the part a reviewer has to be able to read: the removal step destroys an object only
    /// when this method says so, and nothing else in the package decides whether a source object may be deleted.
    /// </para>
    /// </remarks>
    public static class ApaSourceObjectCleanup
    {
        /// <summary>
        /// True when a consumed source object is genuinely empty and may be removed.
        /// </summary>
        /// <param name="candidate">The object the assembly consumed a renderer from. May be null or destroyed.</param>
        /// <param name="avatarRoot">
        /// The build clone's root. The root itself is never empty even if it carries nothing but a Transform, so
        /// it is refused explicitly. May be null, in which case only the other rules apply.
        /// </param>
        /// <remarks>
        /// The parent rule refuses a scene root and a detached object: both are outside the hierarchy the build
        /// owns, and a build must not delete either. The child rule is what keeps a part root with bones or other
        /// geometry under it alive, which is exactly the case the generated mesh's skinning depends on.
        /// </remarks>
        public static bool IsEmptySourceObject(GameObject candidate, GameObject avatarRoot)
        {
            if (candidate == null) return false;

            // The avatar root is the build's own root object: it always carries the NDMF avatar-root component
            // (and the author's descriptor), so the component rule below already refuses it. The explicit check
            // stays because "never delete the root" must not depend on another component being present.
            if (avatarRoot != null && candidate == avatarRoot) return false;

            var transform = candidate.transform;
            if (transform == null) return false;

            // A scene root has no parent, so it is not part of the hierarchy this build may prune; the same rule
            // refuses an object that something already detached.
            if (transform.parent == null) return false;

            // Anything under the object may be a bone of the generated mesh, another renderer, or an author's
            // object. A non-empty subtree is never removed.
            if (transform.childCount != 0) return false;

            var components = candidate.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];

                // A null entry is a missing script: the object is not provably empty, so it is kept.
                if (component == null) return false;

                if (component is Transform) continue;

                return false;
            }

            return true;
        }
    }
}

using System;
using System.Collections.Generic;

namespace AvatarPartAssembler
{
    /// <summary>
    /// Hierarchical paths of a renderer's bones, in bone order, relative to the armature selected for that
    /// renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bone table is recorded as paths rather than as object references because a signature has to survive
    /// serialization and be reproducible on another machine. Paths are also what decides whether a bone remap
    /// authored against one armature still means the same thing on the avatar it is applied to.
    /// </para>
    /// <para>
    /// <b>The paths are armature-relative since M10.</b> Each renderer's bones are recorded relative to the
    /// armature selected for its own side — the target armature for the body, the part armature for a part — so
    /// two bones whose relative paths are byte-identical name the same joint. A hand-built signature that names
    /// no scope is the pre-M10 avatar-root-relative form.
    /// </para>
    /// <para>
    /// This type lives in the Runtime assembly because the serialized compatibility profile exposes it
    /// (<see cref="ApaAvatarCompatibilityProfile.Bones"/>). A Runtime type must not depend on Editor code, and
    /// the Editor pipeline must not define a second type with the same name: two same-named types in different
    /// namespaces is exactly how a profile ends up being written through one definition and read through
    /// another.
    /// </para>
    /// <para>
    /// Paths are compared exactly, with <see cref="StringComparison.Ordinal"/> and without trimming. A bone
    /// path is a hierarchy path, not an author-facing semantic name, so the semantic normalization rule
    /// (<see cref="ApaSemanticName"/>) deliberately does not apply: trimming would fold two distinct GameObjects
    /// whose names differ only by surrounding whitespace into one bone identity.
    /// </para>
    /// <para>
    /// Two entries carry a meaning of their own. A bone that <i>is</i> the armature its paths are recorded
    /// against records <see cref="ApaAvatarPath.Root"/> (<c>"."</c>) and is a valid identity. A null bone entry
    /// records the empty string, which means <b>missing</b> and has no identity to remap a weight onto; the final
    /// bone table refuses it. The two must never be conflated — see <see cref="ApaAvatarPath"/>.
    /// </para>
    /// </remarks>
    public sealed class BoneSignature
    {
        private readonly string[] _paths;

        /// <summary>
        /// Bone paths relative to the armature selected for the owning renderer, in bone order. Empty when the
        /// renderer has no bones.
        /// </summary>
        public IReadOnlyList<string> Paths => _paths;

        /// <summary>Number of paths.</summary>
        public int Count => _paths.Length;

        /// <summary>True when no bone path was recorded.</summary>
        public bool IsEmpty => _paths.Length == 0;

        /// <summary>Creates a signature.</summary>
        public BoneSignature(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                _paths = Array.Empty<string>();
                return;
            }

            var copy = new string[paths.Count];
            for (var i = 0; i < paths.Count; i++) copy[i] = paths[i] ?? string.Empty;
            _paths = copy;
        }

        /// <summary>
        /// The path at an index, or an empty string when the index is out of range.
        /// </summary>
        /// <remarks>
        /// Out of range returns an empty string rather than throwing because an empty path already means "no
        /// usable identity", and callers must handle that case anyway.
        /// </remarks>
        public string PathAt(int index)
        {
            if (index < 0 || index >= _paths.Length) return string.Empty;
            return _paths[index] ?? string.Empty;
        }

        /// <summary>
        /// True when the path at an index is a usable identity: a real armature-relative hierarchy path, or the
        /// armature root's <see cref="ApaAvatarPath.Root"/> token.
        /// </summary>
        /// <remarks>
        /// Only the empty string is "missing" (a null bone entry, or a bone outside the selected armature), and
        /// it is never an identity. The armature root is a real joint with a real transform, so it is identified
        /// by the token rather than by an empty path.
        /// </remarks>
        public bool HasIdentityAt(int index)
        {
            return ApaAvatarPath.HasIdentity(PathAt(index));
        }

        /// <summary>A copy of the paths, for callers that must not alias the signature.</summary>
        public string[] ToArray()
        {
            var copy = new string[_paths.Length];
            Array.Copy(_paths, copy, _paths.Length);
            return copy;
        }

        /// <summary>An empty signature, used for a mesh without skinning.</summary>
        public static readonly BoneSignature Empty = new BoneSignature(Array.Empty<string>());
    }
}

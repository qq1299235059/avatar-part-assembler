using System;

namespace AvatarPartAssembler
{
    /// <summary>
    /// The canonical avatar-root-relative path vocabulary shared by the whole pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A path is relative to the avatar root and is produced by one routine
    /// (<c>MeshSnapshotFactory.RelativePath</c>). Two values carry a meaning of their own and must never be
    /// conflated:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Root"/> (<c>"."</c>) is the avatar root itself: a renderer, a bone, or an installer that sits
    /// on the root object. It is a <b>valid identity</b> and resolves to the root transform.
    /// </description></item>
    /// <item><description>
    /// The empty string means <b>missing</b>: a null transform, or a null bone entry in a renderer's bone list.
    /// It is not an identity, and an empty path must keep blocking wherever an identity is required. A legacy
    /// profile that recorded the avatar root as an empty renderer path therefore stays unsupported: empty
    /// cannot be told apart from "no path recorded" without guessing.
    /// </description></item>
    /// </list>
    /// <para>
    /// The token is written into serialized authoring data — the compatibility profile's <c>RendererPath</c> and
    /// <c>BonePaths</c> — so it is part of the on-disk contract rather than an Editor implementation detail.
    /// That is why this type lives in the Runtime assembly: the Editor pipeline and the Runtime profile must
    /// agree on one definition.
    /// </para>
    /// <para>
    /// <b>Reserved token.</b> <c>Transform.Find</c> reserves <c>"."</c>, so a caller that turns a path back into
    /// a hierarchy lookup must handle <see cref="Root"/> explicitly instead of passing it to <c>Find</c>. A
    /// GameObject literally named <c>"."</c> directly under the avatar root would produce the same string; the
    /// token is reserved, so such a child is read as the avatar root. Naming a GameObject <c>"."</c> is
    /// unsupported.
    /// </para>
    /// </remarks>
    public static class ApaAvatarPath
    {
        /// <summary>
        /// The canonical avatar-root-relative path of the avatar root itself.
        /// </summary>
        /// <remarks>
        /// It never means "missing" and is never produced for a null transform.
        /// </remarks>
        public const string Root = ".";

        /// <summary>True when a path is exactly the canonical avatar-root token, compared ordinally.</summary>
        public static bool IsRoot(string path)
        {
            return string.Equals(path, Root, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when a path is a usable identity: anything except the empty string.
        /// </summary>
        /// <remarks>
        /// The avatar root's token <i>is</i> an identity. Only the empty string means "missing", and it keeps
        /// blocking wherever an identity is required — a null bone entry has no transform to remap a weight
        /// onto, and no hierarchy lookup may invent one for it.
        /// </remarks>
        public static bool HasIdentity(string path)
        {
            return !string.IsNullOrEmpty(path);
        }
    }
}

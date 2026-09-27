using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The seam through which a capture reads a renderer's mesh and materials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a seam and not a direct read.</b> The build reads the renderer it is about to replace, and nothing
    /// else: on the NDMF build clone the renderer's own mesh is the geometry the author authored. The Scene View
    /// preview is the one caller where that is not true. NDMF hands a render filter the <i>proxy</i> an earlier
    /// filter has already written to, and Modular Avatar's preview filters are exactly such earlier filters: its
    /// Mesh Cutter replaces the body's mesh, its Shape Changer replaces blend-shape weights, and its Material
    /// Setter replaces materials. A capture that read the author's original renderer would silently show the
    /// uncut body and defeat all three.
    /// </para>
    /// <para>
    /// <b>The default is the live read.</b> <see cref="Live"/> returns exactly what the direct read returned
    /// before this seam existed, so every existing caller — the NDMF build pass, the authoring layer, a direct
    /// core call in a test — captures byte-identically. Only a caller that has a substitute to offer supplies
    /// one, and only the preview does.
    /// </para>
    /// <para>
    /// <b>A source reads; it never writes.</b> Every member returns a value the caller then snapshots. Nothing
    /// here assigns a mesh, a material, or an asset, and nothing destroys anything: the substitute's objects
    /// belong to the pipeline that created them.
    /// </para>
    /// </remarks>
    public abstract class ApaCaptureSource
    {
        private static readonly Material[] s_noMaterials = new Material[0];

        /// <summary>The default source: every read returns the renderer's own live value.</summary>
        public static ApaCaptureSource Live { get; } = new ApaLiveCaptureSource();

        /// <summary>
        /// Reads a renderer's mesh the way the build always has: the skinned mesh, or the mesh filter's mesh.
        /// </summary>
        /// <remarks>
        /// Shared by the default source and by a substitute that wants the live value as its fallback, so the
        /// "what is a renderer's mesh" rule exists once. Returns null when the renderer carries no mesh, which is
        /// a legitimate state for a protected part's renderer.
        /// </remarks>
        public static Mesh ReadLiveMesh(Renderer renderer)
        {
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>Reads a renderer's material list, never null.</summary>
        public static Material[] ReadLiveMaterials(Renderer renderer)
        {
            if (renderer == null) return s_noMaterials;
            return renderer.sharedMaterials ?? s_noMaterials;
        }

        /// <summary>
        /// The mesh a capture must use for a renderer, given the renderer's own live mesh.
        /// </summary>
        /// <param name="renderer">The renderer being captured.</param>
        /// <param name="liveMesh">Its live mesh, as read by <see cref="ReadLiveMesh"/>.</param>
        public virtual Mesh MeshFor(Renderer renderer, Mesh liveMesh)
        {
            return liveMesh;
        }

        /// <summary>
        /// The material list a capture must use for a renderer, given the renderer's own live list.
        /// </summary>
        /// <param name="renderer">The renderer being captured.</param>
        /// <param name="liveMaterials">Its live materials, as read by <see cref="ReadLiveMaterials"/>.</param>
        public virtual Material[] MaterialsFor(Renderer renderer, Material[] liveMaterials)
        {
            return liveMaterials;
        }

        /// <summary>
        /// The renderer that stands in for the given original in this source, or null when there is none.
        /// </summary>
        /// <remarks>
        /// The frame path needs this rather than a mesh read: a blend-shape weight is read from a renderer, not
        /// from a mesh, and the weight the earlier stage set lives on the substitute. Null means "read the
        /// original", which is the answer for every source that has no substitute for this renderer.
        /// </remarks>
        public virtual Renderer SubstitutedRendererFor(Renderer renderer)
        {
            return null;
        }

        /// <summary>
        /// Why a capture of this renderer read something other than its live state, or null when it did not.
        /// </summary>
        /// <remarks>
        /// The value is a stable <c>reason=</c> token, not a sentence: it is what a diagnostic, a test, and a log
        /// compare. Null is the ordinary case and means "the live renderer was captured".
        /// </remarks>
        public virtual string SubstitutionReasonFor(Renderer renderer)
        {
            return null;
        }
    }

    /// <summary>
    /// The default capture source: every read returns the live renderer's own value.
    /// </summary>
    /// <remarks>
    /// A distinct type rather than a flag on <see cref="ApaCaptureSource"/>, so "the live read" is one object
    /// with one behavior and a caller cannot accidentally construct a second, subtly different default.
    /// </remarks>
    internal sealed class ApaLiveCaptureSource : ApaCaptureSource
    {
    }
}

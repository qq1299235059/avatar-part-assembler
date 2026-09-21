using System;
using System.Collections.Generic;
using System.Globalization;
using AvatarPartAssembler.Editor.Localization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The outcome of resolving the seam candidates of one side of a weld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A result rather than an exception or a bare array, because the window has to show what happened: how many
    /// vertices are candidates, which color was looked for, and — when no candidate can be resolved — the
    /// blocking diagnostic that says why. A failure never carries a fallback index list: the whole point of the
    /// contract is that "no candidate" is refused instead of quietly becoming "every vertex".
    /// </para>
    /// <para>
    /// <b>Two kinds of candidate set, one type.</b> The <b>part</b> side is a <i>color filter</i>: its candidates
    /// are exactly the vertices whose stored <c>Mesh.colors32</c> entry equals the selected color
    /// (<see cref="ApaSeamVertexColorCandidates"/>). The <b>target</b> side is <i>spatial</i>: every vertex of the
    /// body mesh is a candidate and the world-position matcher decides which of them pair
    /// (<see cref="ApaSeamSpatialTargetCandidates"/>), so a body mesh without vertex colors is a normal body mesh
    /// rather than a blocked one. <see cref="IsSpatial"/> says which kind a result is; the two are never mixed,
    /// and a spatial result is never a fallback for a color that failed to resolve.
    /// </para>
    /// </remarks>
    public sealed class ApaSeamColorCandidateResult
    {
        /// <summary>True when the side resolved to a usable candidate policy.</summary>
        public bool Succeeded => Issue == null;

        /// <summary>The blocking diagnostic, or null on success.</summary>
        public ValidationIssue Issue { get; }

        /// <summary>
        /// The candidate vertex indices, strictly ascending and duplicate-free. Empty on failure, and empty for a
        /// spatial result, whose candidate set is "every vertex" and therefore has no list of its own.
        /// </summary>
        public int[] Indices { get; }

        /// <summary>
        /// True when every vertex of the mesh is a candidate and the matcher restricts the pairing by position
        /// alone. Only the target side is spatial; the part side is always a resolved color filter.
        /// </summary>
        public bool IsSpatial { get; }

        /// <summary>Vertex count of the mesh the candidates were resolved against, or 0 when there was no mesh.</summary>
        public int VertexCount { get; }

        /// <summary>
        /// Number of stored vertex colors the mesh carried, or 0 when it carried none. Always 0 for a spatial
        /// result, which never reads the color channel.
        /// </summary>
        public int ColorCount { get; }

        /// <summary>The selected candidate color the stored colors were compared against.</summary>
        public Color32 Color { get; }

        /// <summary>
        /// The candidate list to hand to <see cref="ApaSeamWorldMatcher"/>: <see cref="Indices"/> for a color
        /// filter, and <c>null</c> — the matcher's documented "all vertices" argument — for a spatial result.
        /// </summary>
        /// <remarks>
        /// Passing an empty list instead of null would mean "no candidate at all" to the matcher, which is the
        /// opposite of what a spatial result says. Exposing the translation here keeps every caller from having to
        /// remember that, and is the only place the two representations meet.
        /// </remarks>
        public int[] MatcherCandidateIndices => IsSpatial ? null : Indices;

        private ApaSeamColorCandidateResult(
            ValidationIssue issue,
            int[] indices,
            int vertexCount,
            int colorCount,
            Color32 color,
            bool isSpatial)
        {
            Issue = issue;
            Indices = indices ?? Array.Empty<int>();
            VertexCount = vertexCount;
            ColorCount = colorCount;
            Color = color;
            IsSpatial = isSpatial;
        }

        internal static ApaSeamColorCandidateResult Failure(
            ValidationIssue issue,
            int vertexCount,
            int colorCount,
            Color32 color)
        {
            return new ApaSeamColorCandidateResult(issue, null, vertexCount, colorCount, color, false);
        }

        internal static ApaSeamColorCandidateResult Success(
            int[] indices,
            int vertexCount,
            int colorCount,
            Color32 color)
        {
            return new ApaSeamColorCandidateResult(null, indices, vertexCount, colorCount, color, false);
        }

        /// <summary>
        /// A resolved spatial candidate set: every vertex of a target mesh of <paramref name="vertexCount"/>
        /// vertices may pair, and position decides which ones do.
        /// </summary>
        internal static ApaSeamColorCandidateResult Spatial(int vertexCount)
        {
            return new ApaSeamColorCandidateResult(
                null,
                null,
                vertexCount,
                0,
                default(Color32),
                true);
        }

        /// <summary>A one-line summary of the resolved candidates, for the window's seam block.</summary>
        public string Describe()
        {
            if (!Succeeded) return ApaLocalization.Tr("no seam candidate color");

            if (IsSpatial)
            {
                return ApaLocalization.TrFormat(
                    "{0} spatial target vertex(es) (paired by world position, not by color)",
                    VertexCount);
            }

            return ApaLocalization.TrFormat(
                "{0} seam candidate vertex(es) carrying the color {1}",
                Indices.Length,
                ApaSeamVertexColorCandidates.Format(Color));
        }
    }

    /// <summary>
    /// Resolves the seam candidate vertices of the <b>part</b> mesh from its stored vertex colors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The contract, in full.</b> Automatic seam generation considers only the vertices whose stored color is
    /// the <i>selected candidate color</i>. The color is an authoring input the window exposes, and a part vertex
    /// is a candidate exactly when its <see cref="Mesh.colors32"/> entry equals the selected color — see
    /// <see cref="Matches"/> for the comparison rule.
    /// </para>
    /// <para>
    /// <b>One side only.</b> The filter applies to the <b>part</b> mesh. The target body mesh is not required to
    /// carry vertex colors at all: its candidate set is spatial — every target vertex — and the world-position
    /// matcher decides which of them pair (<see cref="ApaSeamSpatialTargetCandidates"/>). Requiring a body to be
    /// painted was wrong in both directions: it blocked a perfectly ordinary body, and it asked the author to
    /// author the same paint on two meshes. Only the part is nominated by color, because only the part's vertices
    /// are the ones the author chooses to weld.
    /// </para>
    /// <para>
    /// <b>Why vertex colors.</b> Unity's <see cref="Mesh"/> has no generic named vertex-group API: a vertex group
    /// authored in a modelling tool survives an FBX import only as skinning, and import pipelines routinely drop
    /// non-bone groups. Vertex colors are a mesh attribute the importer preserves, they can be painted in the
    /// modelling tool or written by an import script, and they are inspectable in the Editor. The previous
    /// <c>merge vertex</c> component/bone representation is therefore gone: nothing in the seam workflow reads a
    /// component or a bone name any more.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> The returned indices are strictly ascending and duplicate-free: the mesh is visited in
    /// ascending vertex order once. The matcher turns the list into a mask and visits part vertices in ascending
    /// order, so the generated pairing depends only on the two meshes, the tolerance, the selected color, and the
    /// transforms — never on the order anything was authored in.
    /// </para>
    /// <para>
    /// <b>No invented encoding and no fallback.</b> Nothing here reads a UV channel, a bone weight, or a
    /// component to guess a candidate set, and there is no second color to try. A part mesh that carries no vertex
    /// color, a color array whose length disagrees with the vertex count, and a color that matches no vertex are
    /// all refused with <c>APA052 SEAM_CANDIDATE_COLOR_INVALID</c>, because the only alternative would be to pair
    /// vertices the author never nominated. That refusal is never answered by widening the search to every part
    /// vertex.
    /// </para>
    /// </remarks>
    public static class ApaSeamVertexColorCandidates
    {
        /// <summary>The stable detail token for the target side.</summary>
        public const string TargetSide = "target";

        /// <summary>The stable detail token for the part side.</summary>
        public const string PartSide = "part";

        /// <summary>
        /// The color a fresh window looks for: opaque black <c>#000000</c>, the color a freshly imported mesh
        /// carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only a default. The candidate color is window state, never profile data, so changing it changes which
        /// vertices the <i>next</i> generation may pair and never rewrites an already authored seam.
        /// </para>
        /// <para>
        /// Opaque black is the deliberate choice: an imported mesh that carries a color channel but was never
        /// painted stores black, so the default selection is the one an author sees something happen with, and
        /// repainting the seam ring is an explicit act rather than a value that happened to be preselected. Alpha
        /// is opaque, because a color written by a modelling tool's default is opaque and because the comparison
        /// includes alpha.
        /// </para>
        /// </remarks>
        public static readonly Color32 DefaultColor = new Color32(0, 0, 0, 255);

        /// <summary>
        /// True when a stored vertex color is a candidate for the selected color.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rule: exact channel equality.</b> All four <see cref="Color32"/> channels — red, green, blue, and
        /// alpha — must be equal. There is deliberately no tolerance: a vertex color is authored data, the
        /// selected color is picked from the same 8-bit space, and a "close enough" comparison would make a
        /// candidate set depend on how far a paint tool drifted rather than on what the author chose. An exact
        /// rule is also the only one that can be documented in one sentence and tested without a tolerance table.
        /// </para>
        /// <para>
        /// The alpha channel participates, so a color painted as <c>(0, 255, 0, 0)</c> is <i>not</i> the default
        /// <c>(0, 255, 0, 255)</c>. The window shows the selected color's four channels so the author can see
        /// exactly what is being matched, and the color code it is edited through accepts an explicit alpha
        /// (<c>#RRGGBBAA</c>) for that reason.
        /// </para>
        /// </remarks>
        public static bool Matches(Color32 stored, Color32 selected)
        {
            return stored.r == selected.r
                   && stored.g == selected.g
                   && stored.b == selected.b
                   && stored.a == selected.a;
        }

        /// <summary>
        /// Resolves the candidate vertices of one renderer from its mesh's stored vertex colors.
        /// </summary>
        /// <param name="renderer">The renderer whose mesh is being paired.</param>
        /// <param name="mesh">The renderer's mesh, as the authoring selection resolved it.</param>
        /// <param name="color">The selected candidate color.</param>
        /// <param name="side">
        /// <see cref="TargetSide"/> or <see cref="PartSide"/>. Used for the stable <c>side=</c> detail token and
        /// for the two messages that mirror the matcher's missing-mesh diagnostics. The window applies this filter
        /// to the <b>part</b> side only; the target side is spatial
        /// (<see cref="ApaSeamSpatialTargetCandidates"/>), and this overload remains for explicit seam consumers
        /// that want the color filter on either side.
        /// </param>
        public static ApaSeamColorCandidateResult Resolve(Renderer renderer, Mesh mesh, Color32 color, string side)
        {
            var isPart = string.Equals(side, PartSide, StringComparison.Ordinal);

            if (renderer == null || mesh == null)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        isPart
                            ? "Select a part renderer with a mesh before generating the seam."
                            : "Select a target body renderer with a mesh before generating the seam: the seam pairs " +
                              "the part's vertices with the body's.",
                        detail: "reason=missing-" + (isPart ? PartSide : TargetSide) + "-mesh"),
                    0,
                    0,
                    color);
            }

            if (!mesh.isReadable)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ApaCompatibilityCapture.NotReadableIssue(mesh), mesh.vertexCount, 0, color);
            }

            return FromColors(mesh.colors32, mesh.vertexCount, color, side);
        }

        /// <summary>
        /// Resolves the <b>part</b> mesh's candidate vertices from the selected color. The window's entry point.
        /// </summary>
        /// <remarks>
        /// Named rather than spelled <c>Resolve(..., PartSide)</c> so that the one side the color filter applies
        /// to is visible at the call site, and so a reader cannot mistake it for the target-side policy.
        /// </remarks>
        public static ApaSeamColorCandidateResult ResolvePartCandidates(
            Renderer renderer,
            Mesh mesh,
            Color32 color)
        {
            return Resolve(renderer, mesh, color, PartSide);
        }

        /// <summary>
        /// The pure half of the contract: filters a stored color array down to the candidate indices.
        /// </summary>
        /// <remarks>
        /// Public and free of Unity mesh reads so that the malformed cases — no colors at all, a color array of
        /// the wrong length, a color no vertex carries — can be tested without building a mesh that carries them,
        /// and so that the rule has exactly one implementation shared by the window and the Scene View overlay.
        /// </remarks>
        /// <param name="colors">The mesh's stored vertex colors, as <c>Mesh.colors32</c> returned them.</param>
        /// <param name="vertexCount">Vertex count of the mesh the colors belong to.</param>
        /// <param name="color">The selected candidate color.</param>
        /// <param name="side"><see cref="TargetSide"/> or <see cref="PartSide"/>, for the stable detail token.</param>
        public static ApaSeamColorCandidateResult FromColors(
            Color32[] colors,
            int vertexCount,
            Color32 color,
            string side)
        {
            if (vertexCount <= 0)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.SeamCandidateColorInvalid,
                        ApaIssuePhase.Seam,
                        "The " + side + " mesh has no vertex, so it declares no seam candidate.",
                        detail: "side=" + side + "; reason=empty-mesh; vertexCount=" + vertexCount),
                    vertexCount,
                    0,
                    color);
            }

            var colorCount = colors != null ? colors.Length : 0;

            if (colorCount == 0)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.SeamCandidateColorInvalid,
                        ApaIssuePhase.Seam,
                        "The " + side + " mesh carries no vertex color, so no vertex is eligible for seam pairing. " +
                        "Paint the seam vertices with the candidate color (" +
                        Format(color) + ") in the modelling tool, or write Mesh.colors32 before generating the seam.",
                        detail: "side=" + side + "; reason=vertex-color-missing; color=" + Format(color) +
                                "; vertexCount=" + vertexCount),
                    vertexCount,
                    0,
                    color);
            }

            if (colorCount != vertexCount)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.SeamCandidateColorInvalid,
                        ApaIssuePhase.Seam,
                        "The " + side + " mesh carries " + colorCount + " vertex color(s) for " + vertexCount +
                        " vertex(es), so its colors cannot be indexed by vertex and the candidate color cannot be " +
                        "read. Re-import the mesh so every vertex carries a color.",
                        detail: "side=" + side + "; reason=vertex-color-count-mismatch; colors=" + colorCount +
                                "; vertexCount=" + vertexCount),
                    vertexCount,
                    colorCount,
                    color);
            }

            var indices = new List<int>();
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                if (!Matches(colors[vertex], color)) continue;
                indices.Add(vertex);
            }

            if (indices.Count == 0)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.SeamCandidateColorInvalid,
                        ApaIssuePhase.Seam,
                        "No vertex of the " + side + " mesh carries the candidate color " + Format(color) +
                        ", so no vertex is eligible for seam pairing. Paint the seam vertices with exactly that " +
                        "color, or select the color the mesh actually stores.",
                        detail: "side=" + side + "; reason=no-vertex-color-candidates; color=" + Format(color) +
                                "; vertexCount=" + vertexCount),
                    vertexCount,
                    colorCount,
                    color);
            }

            return ApaSeamColorCandidateResult.Success(indices.ToArray(), vertexCount, colorCount, color);
        }

        /// <summary>The stable rendering of a color, used by every diagnostic and status line.</summary>
        /// <remarks>
        /// <c>RGBA(r, g, b, a)</c> with invariant byte values, so a report, a log line, and a test assertion are
        /// comparable and no culture can renumber the channels. It is the diagnostic spelling of a color; the
        /// authoring surface's editable spelling is <see cref="ApaSeamColorCode"/>.
        /// </remarks>
        public static string Format(Color32 color)
        {
            return "RGBA(" +
                   color.r.ToString(CultureInfo.InvariantCulture) + ", " +
                   color.g.ToString(CultureInfo.InvariantCulture) + ", " +
                   color.b.ToString(CultureInfo.InvariantCulture) + ", " +
                   color.a.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}

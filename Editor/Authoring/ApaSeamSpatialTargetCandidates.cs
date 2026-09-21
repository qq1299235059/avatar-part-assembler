using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Resolves the seam candidate set of the <b>target body</b> mesh: every vertex, restricted only by world
    /// position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The target side is spatial, not painted.</b> The selected candidate color filters the <i>part</i> mesh
    /// (<see cref="ApaSeamVertexColorCandidates"/>); the body is not required to carry vertex colors at all. Every
    /// target vertex is a candidate, and <see cref="ApaSeamWorldMatcher"/> decides which of them pair by world
    /// distance within the tolerance — exactly the rule that was always applied to the target side, now stated as
    /// the target side's only candidate policy.
    /// </para>
    /// <para>
    /// <b>Why the asymmetry is correct.</b> The candidate color answers "which of my part's vertices weld?" — a
    /// question only the part's author can answer, because only those vertices are the ones being welded. The
    /// body is the search space those welds land in; requiring it to be painted would block an ordinary body and
    /// would ask the author to maintain the same paint on two meshes, which is precisely the kind of duplicated
    /// correspondence the world-position matcher exists to remove.
    /// </para>
    /// <para>
    /// <b>Nothing is read from the color channel.</b> This resolver never touches <c>Mesh.colors32</c>: the color
    /// channel of a real body mesh is a full per-vertex copy, and the authoring window resolves this side on every
    /// change. A mesh with colors, without colors, and with a malformed color array therefore all resolve to the
    /// same spatial candidate set — there is no target-side <c>APA052</c> for a missing color, because no color is
    /// consulted.
    /// </para>
    /// <para>
    /// <b>It can still refuse, for reasons that are not about color.</b> A missing renderer or mesh, an
    /// unreadable mesh, and a mesh with no vertex are refused with the same codes, messages, and
    /// <c>reason=</c> tokens the matcher uses for the same conditions, so the window and the generate action
    /// describe one defect one way.
    /// </para>
    /// </remarks>
    public static class ApaSeamSpatialTargetCandidates
    {
        /// <summary>
        /// Resolves the spatial candidate set of the target body mesh: all of its vertices.
        /// </summary>
        /// <param name="renderer">The target body renderer, used only for the missing-mesh diagnostic.</param>
        /// <param name="mesh">The target body mesh, in its rest pose.</param>
        /// <returns>
        /// A successful result whose <see cref="ApaSeamColorCandidateResult.IsSpatial"/> is true and whose
        /// <see cref="ApaSeamColorCandidateResult.MatcherCandidateIndices"/> is null, or the blocking diagnostic
        /// that stops the target side from being used at all.
        /// </returns>
        public static ApaSeamColorCandidateResult Resolve(Renderer renderer, Mesh mesh)
        {
            if (renderer == null || mesh == null)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Select a target body renderer with a mesh before generating the seam: the seam pairs " +
                        "the part's vertices with the body's.",
                        detail: "reason=missing-" + ApaSeamVertexColorCandidates.TargetSide + "-mesh"),
                    0,
                    0,
                    default(Color32));
            }

            if (!mesh.isReadable)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ApaCompatibilityCapture.NotReadableIssue(mesh), mesh.vertexCount, 0, default(Color32));
            }

            if (mesh.vertexCount <= 0)
            {
                return ApaSeamColorCandidateResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.SeamCandidateColorInvalid,
                        ApaIssuePhase.Seam,
                        "The " + ApaSeamVertexColorCandidates.TargetSide +
                        " mesh has no vertex, so it declares no seam candidate.",
                        detail: "side=" + ApaSeamVertexColorCandidates.TargetSide +
                                "; reason=empty-mesh; vertexCount=" + mesh.vertexCount),
                    mesh.vertexCount,
                    0,
                    default(Color32));
            }

            return ApaSeamColorCandidateResult.Spatial(mesh.vertexCount);
        }
    }
}

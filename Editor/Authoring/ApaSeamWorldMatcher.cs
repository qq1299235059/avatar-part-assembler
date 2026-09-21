using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The outcome of one world-position seam generation.
    /// </summary>
    /// <remarks>
    /// A result rather than an exception or a bare array, because the window has to show what happened: the
    /// tolerance that was used, how many pairs were written, and — when the action could not run at all — the
    /// stable diagnostic that says why.
    /// </remarks>
    public sealed class ApaSeamWorldMatchResult
    {
        /// <summary>True when at least one pair was produced.</summary>
        public bool Succeeded => Issue == null;

        /// <summary>The blocking diagnostic, or null on success.</summary>
        public ValidationIssue Issue { get; }

        /// <summary>Base seam vertices, in pair order. <c>BaseIndices[i]</c> pairs with <c>PartIndices[i]</c>.</summary>
        public int[] BaseIndices { get; }

        /// <summary>Part seam vertices, in pair order.</summary>
        public int[] PartIndices { get; }

        /// <summary>Number of pairs written.</summary>
        public int PairCount => PartIndices.Length;

        /// <summary>Number of vertices the target mesh contributed to the search space.</summary>
        public int BaseVertexCount { get; }

        /// <summary>Number of part vertices that were looked up.</summary>
        public int PartVertexCount { get; }

        /// <summary>Part vertices with no unclaimed target vertex inside the tolerance.</summary>
        public int UnmatchedPartVertexCount { get; }

        /// <summary>The world-space tolerance the match ran with.</summary>
        public float Tolerance { get; }

        private ApaSeamWorldMatchResult(
            ValidationIssue issue,
            int[] baseIndices,
            int[] partIndices,
            int baseVertexCount,
            int partVertexCount,
            int unmatchedPartVertexCount,
            float tolerance)
        {
            Issue = issue;
            BaseIndices = baseIndices ?? Array.Empty<int>();
            PartIndices = partIndices ?? Array.Empty<int>();
            BaseVertexCount = baseVertexCount;
            PartVertexCount = partVertexCount;
            UnmatchedPartVertexCount = unmatchedPartVertexCount;
            Tolerance = tolerance;
        }

        internal static ApaSeamWorldMatchResult Failure(ValidationIssue issue, float tolerance)
        {
            return new ApaSeamWorldMatchResult(issue, null, null, 0, 0, 0, tolerance);
        }

        internal static ApaSeamWorldMatchResult Success(
            int[] baseIndices,
            int[] partIndices,
            int baseVertexCount,
            int partVertexCount,
            int unmatchedPartVertexCount,
            float tolerance)
        {
            return new ApaSeamWorldMatchResult(
                null,
                baseIndices,
                partIndices,
                baseVertexCount,
                partVertexCount,
                unmatchedPartVertexCount,
                tolerance);
        }

        /// <summary>A one-line summary of the run, for the window's status line.</summary>
        public string Describe()
        {
            if (!Succeeded) return Localization.ApaLocalization.Tr("seam generation failed");

            return Localization.ApaLocalization.TrFormat(
                "{0} seam pair(s) within a world tolerance of {1}; {2} of {3} part vertex(es) had a free " +
                "counterpart.",
                PairCount,
                Format(Tolerance),
                PartVertexCount - UnmatchedPartVertexCount,
                PartVertexCount);
        }

        private static string Format(float value)
        {
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Generates the seam correspondence from world-coincident vertex positions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it reads.</b> Each mesh's <c>sharedMesh.vertices</c> transformed by its renderer's
    /// <c>transform.localToWorldMatrix</c> — the bind/rest mesh, never <c>SkinnedMeshRenderer.BakeMesh</c>. A
    /// baked mesh is the <i>posed</i> geometry, so two seams that coincide in the rest pose would move apart as
    /// soon as an animation or a blend shape is applied, and the pairing would depend on whatever pose the
    /// author happened to be looking at. The rest pose is the only pose the assembled mesh is built from.
    /// </para>
    /// <para>
    /// <b>The tolerance is stated in world units</b> and shown in the window, because that is the quantity the
    /// author can reason about: two vertices either occupy the same point in space or they do not. An
    /// avatar-root-local epsilon would silently change meaning with every scale on the path from the avatar root
    /// down to the geometry, which is exactly the ambiguity M10 removes.
    /// </para>
    /// <para>
    /// <b>Cost.</b> A uniform spatial hash over the target mesh, then a 27-cell probe per part vertex, so the
    /// run is sub-quadratic. The comparison is on squared distances, so no square root is taken per candidate.
    /// The probe allocates nothing per vertex: it tracks the best candidate in a local variable rather than
    /// materializing a candidate list.
    /// </para>
    /// <para>
    /// <b>Determinism and one-to-one.</b> Part vertices are visited in ascending index order and each takes the
    /// nearest still-free target vertex, ties broken by the lower target index. A target vertex is claimed by at
    /// most one pair, so the result is a one-to-one matching that depends only on the two meshes and the
    /// tolerance — never on instance ids, hierarchy order, or dictionary enumeration order.
    /// </para>
    /// </remarks>
    public static class ApaSeamWorldMatcher
    {
        /// <summary>
        /// Default world-space tolerance: 0.1 mm.
        /// </summary>
        /// <remarks>
        /// A welded seam is authored by placing two rings at the same coordinates, so the differences that
        /// survive an FBX round trip are at the precision limit of single-precision data in metres. A tenth of a
        /// millimetre accepts those and is still far below any distance that could pair two distinct vertices of
        /// a real avatar.
        /// </remarks>
        public const float DefaultTolerance = 1e-4f;

        /// <summary>Smallest tolerance the window offers. Below this, single-precision noise dominates.</summary>
        public const float MinimumTolerance = 1e-7f;

        /// <summary>
        /// Largest tolerance the window offers.
        /// </summary>
        /// <remarks>
        /// A millimetre would already pair vertices that are visibly apart on a real avatar, so the slider stops
        /// well short of a value that could silently weld unrelated geometry.
        /// </remarks>
        public const float MaximumTolerance = 1e-3f;

        /// <summary>
        /// Generates pairs from all vertices. The Part Authoring window uses the candidate overload after resolving
        /// the selected <c>Mesh.colors32</c> value, while this all-vertices overload remains public for explicit
        /// seam consumers and compatibility tests that already provide their own candidate policy.
        /// </summary>
        public static ApaSeamWorldMatchResult Match(
            Renderer targetRenderer,
            Mesh targetMesh,
            Renderer partRenderer,
            Mesh partMesh,
            float tolerance)
        {
            return Match(targetRenderer, targetMesh, partRenderer, partMesh, tolerance, null, null);
        }

        /// <summary>
        /// Matches the part mesh's vertices against the target mesh's vertices by world position.
        /// </summary>
        /// <param name="targetRenderer">The target body renderer whose transform defines the target's world space.</param>
        /// <param name="targetMesh">The target body mesh, in its rest pose.</param>
        /// <param name="partRenderer">The part renderer whose transform defines the part's world space.</param>
        /// <param name="partMesh">The part mesh, in its rest pose.</param>
        /// <param name="tolerance">World-space tolerance. Values outside the documented range are refused.</param>
        /// <param name="targetCandidateIndices">Optional explicit target seam candidates; null means all vertices.</param>
        /// <param name="partCandidateIndices">Optional explicit part seam candidates; null means all vertices.</param>
        public static ApaSeamWorldMatchResult Match(
            Renderer targetRenderer,
            Mesh targetMesh,
            Renderer partRenderer,
            Mesh partMesh,
            float tolerance,
            IReadOnlyList<int> targetCandidateIndices,
            IReadOnlyList<int> partCandidateIndices)
        {
            if (!ApaNumericPolicy.IsFinite(tolerance) || tolerance <= 0f)
            {
                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.InvalidEpsilon,
                        ApaIssuePhase.Configuration,
                        "The seam tolerance must be a positive finite number of world units, but was " +
                        Format(tolerance) + ".",
                        detail: "reason=invalid-seam-tolerance; tolerance=" + Format(tolerance)),
                    tolerance);
            }

            if (tolerance > MaximumTolerance)
            {
                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.InvalidEpsilon,
                        ApaIssuePhase.Configuration,
                        "The seam tolerance is " + Format(tolerance) + " world unit(s), above the safe maximum of " +
                        Format(MaximumTolerance) + ". Restrict pairing to explicitly authored seam candidates " +
                        "or place the seam vertices closer together.",
                        detail: "reason=seam-tolerance-too-large; tolerance=" + Format(tolerance) +
                                "; maximum=" + Format(MaximumTolerance)),
                    tolerance);
            }

            if (targetRenderer == null || targetMesh == null)
            {
                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Select a target body renderer with a mesh before generating the seam: the seam pairs " +
                        "the part's vertices with the body's.",
                        detail: "reason=missing-target-mesh"),
                    tolerance);
            }

            if (partRenderer == null || partMesh == null)
            {
                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Select a part renderer with a mesh before generating the seam.",
                        detail: "reason=missing-part-mesh"),
                    tolerance);
            }

            if (!targetMesh.isReadable) return ApaSeamWorldMatchResult.Failure(
                ApaCompatibilityCapture.NotReadableIssue(targetMesh), tolerance);

            if (!partMesh.isReadable) return ApaSeamWorldMatchResult.Failure(
                ApaCompatibilityCapture.NotReadableIssue(partMesh), tolerance);

            var targetVertices = targetMesh.vertices;
            var partVertices = partMesh.vertices;

            if (targetVertices == null || targetVertices.Length == 0)
            {
                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "The target mesh '" + targetMesh.name + "' has no vertices, so no seam can be matched " +
                        "against it.",
                        detail: "reason=empty-target-mesh; mesh=" + targetMesh.name),
                    tolerance);
            }

            if (partVertices == null || partVertices.Length == 0)
            {
                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "The part mesh '" + partMesh.name + "' has no vertices, so it declares no seam.",
                        detail: "reason=empty-part-mesh; mesh=" + partMesh.name),
                    tolerance);
            }

            if (!TryBuildCandidateMask(targetCandidateIndices, targetVertices.Length, "target",
                    out var targetCandidates, out var targetCandidateCount, out var targetIssue))
                return ApaSeamWorldMatchResult.Failure(targetIssue, tolerance);
            if (!TryBuildCandidateMask(partCandidateIndices, partVertices.Length, "part",
                    out var partCandidates, out var partCandidateCount, out var partIssue))
                return ApaSeamWorldMatchResult.Failure(partIssue, tolerance);

            var targetToWorld = targetRenderer.transform.localToWorldMatrix;
            var partToWorld = partRenderer.transform.localToWorldMatrix;

            var worldTargets = new Vector3[targetVertices.Length];
            for (var i = 0; i < targetVertices.Length; i++)
            {
                worldTargets[i] = targetToWorld.MultiplyPoint3x4(targetVertices[i]);
                if (IsFinite(worldTargets[i])) continue;

                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.NonFiniteValue,
                        ApaIssuePhase.Seam,
                        "Target vertex " + i + " of mesh '" + targetMesh.name + "' has a non-finite world " +
                        "position, so no seam can be matched against it.",
                        sourceIndex: i,
                        detail: "side=target; vertex=" + i + "; reason=non-finite-world-position"),
                    tolerance);
            }

            var lookup = new WorldCellIndex(worldTargets, tolerance);
            var claimed = new bool[worldTargets.Length];
            var baseIndices = new List<int>(partVertices.Length);
            var partIndices = new List<int>(partVertices.Length);
            var unmatched = 0;

            var toleranceSquared = tolerance * tolerance;

            for (var p = 0; p < partVertices.Length; p++)
            {
                if (!partCandidates[p]) continue;
                var world = partToWorld.MultiplyPoint3x4(partVertices[p]);
                if (!IsFinite(world))
                {
                    return ApaSeamWorldMatchResult.Failure(
                        ValidationIssue.Error(
                            ApaErrorCode.NonFiniteValue,
                            ApaIssuePhase.Seam,
                            "Part vertex " + p + " of mesh '" + partMesh.name + "' has a non-finite world " +
                            "position, so it cannot be matched.",
                            sourceIndex: p,
                            detail: "side=part; vertex=" + p + "; reason=non-finite-world-position"),
                        tolerance);
                }

                if (!lookup.TryFindNearestFree(world, toleranceSquared, claimed, targetCandidates, out var best))
                {
                    unmatched++;
                    continue;
                }

                claimed[best] = true;
                baseIndices.Add(best);
                partIndices.Add(p);
            }

            if (partIndices.Count == 0)
            {
                return ApaSeamWorldMatchResult.Failure(
                    ValidationIssue.Error(
                        ApaErrorCode.SeamPositionMismatch,
                        ApaIssuePhase.Seam,
                        "No part vertex lies within " + Format(tolerance) + " world unit(s) of a target body " +
                        "vertex, so nothing was matched. The two rings must occupy the same positions in world " +
                        "space in their rest pose: adjust the tolerance, or place the part so its seam coincides " +
                        "with the body's.",
                        detail: "reason=no-world-coincident-vertices; tolerance=" + Format(tolerance) +
                                "; partVertices=" + partCandidateCount +
                                "; targetVertices=" + targetCandidateCount),
                    tolerance);
            }

            return ApaSeamWorldMatchResult.Success(
                baseIndices.ToArray(),
                partIndices.ToArray(),
                worldTargets.Length,
                partCandidateCount,
                unmatched,
                tolerance);
        }

        private static bool TryBuildCandidateMask(
            IReadOnlyList<int> candidates,
            int vertexCount,
            string side,
            out bool[] mask,
            out int candidateCount,
            out ValidationIssue issue)
        {
            mask = new bool[vertexCount];
            issue = null;
            if (candidates == null)
            {
                for (var i = 0; i < vertexCount; i++) mask[i] = true;
                candidateCount = vertexCount;
                return true;
            }

            candidateCount = candidates.Count;
            for (var i = 0; i < candidates.Count; i++)
            {
                var index = candidates[i];
                if (index < 0 || index >= vertexCount)
                {
                    issue = ValidationIssue.Error(
                        ApaErrorCode.InvalidSeamSelection,
                        ApaIssuePhase.Seam,
                        "The " + side + " seam candidate index " + index + " is outside the mesh vertex range.",
                        sourceIndex: index,
                        detail: "side=" + side + "; reason=invalid-candidate-index; index=" + index +
                                "; vertexCount=" + vertexCount);
                    return false;
                }

                if (mask[index])
                {
                    issue = ValidationIssue.Error(
                        ApaErrorCode.InvalidSeamSelection,
                        ApaIssuePhase.Seam,
                        "The " + side + " seam candidate list contains vertex " + index + " more than once.",
                        sourceIndex: index,
                        detail: "side=" + side + "; reason=duplicate-candidate-index; index=" + index);
                    return false;
                }

                mask[index] = true;
            }

            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return ApaNumericPolicy.IsFinite(value.x)
                   && ApaNumericPolicy.IsFinite(value.y)
                   && ApaNumericPolicy.IsFinite(value.z);
        }

        private static string Format(float value)
        {
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A uniform spatial hash over one point set, with a nearest-free-point query.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The cell size is the tolerance, so every point within the tolerance of a query position is in one of
        /// the 27 cells around it and no candidate can be missed by the accelerator. The hash only narrows the
        /// search: acceptance is the squared-distance comparison against the tolerance, exactly as the
        /// documentation promises.
        /// </para>
        /// <para>
        /// Cell keys are packed exactly (21 bits per axis, see <see cref="Combine"/>) rather than hashed, so
        /// distinct cells cannot collide. Buckets are plain lists in ascending point index order, which is what
        /// makes the tie-break "lowest index wins" reproducible without sorting anything.
        /// </para>
        /// </remarks>
        private sealed class WorldCellIndex
        {
            private readonly Vector3[] _points;
            private readonly float _cellSize;
            private readonly Dictionary<long, List<int>> _cells;

            public WorldCellIndex(Vector3[] points, float tolerance)
            {
                _points = points;
                _cellSize = tolerance > 0f ? tolerance : 1f;
                _cells = new Dictionary<long, List<int>>(points.Length);

                for (var i = 0; i < points.Length; i++)
                {
                    var key = Combine(
                        CellCoord(points[i].x),
                        CellCoord(points[i].y),
                        CellCoord(points[i].z));

                    if (!_cells.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>(2);
                        _cells.Add(key, bucket);
                    }

                    bucket.Add(i);
                }
            }

            /// <summary>
            /// The nearest unclaimed point within the tolerance, or false when there is none.
            /// </summary>
            /// <param name="position">Query position in world space.</param>
            /// <param name="toleranceSquared">Squared tolerance, so no square root is taken per candidate.</param>
            /// <param name="claimed">Points already taken by an earlier pair.</param>
            /// <param name="best">The chosen point index.</param>
            public bool TryFindNearestFree(
                Vector3 position,
                float toleranceSquared,
                bool[] claimed,
                bool[] eligible,
                out int best)
            {
                best = -1;
                var bestDistance = 0f;

                var cx = CellCoord(position.x);
                var cy = CellCoord(position.y);
                var cz = CellCoord(position.z);

                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dz = -1; dz <= 1; dz++)
                        {
                            if (!_cells.TryGetValue(Combine(cx + dx, cy + dy, cz + dz), out var bucket)) continue;

                            for (var b = 0; b < bucket.Count; b++)
                            {
                                var index = bucket[b];
                                if (eligible != null && !eligible[index]) continue;
                                if (claimed[index]) continue;

                                var delta = _points[index] - position;
                                var distance = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;

                                // Acceptance is the configured tolerance, never the bucket.
                                if (distance > toleranceSquared) continue;

                                // Nearest wins; an exact tie is broken by the lower index. The index comparison
                                // is explicit rather than left to iteration order, because the 27 cells are
                                // visited in a fixed but index-unrelated order, so "first one found" is not the
                                // same as "lowest index".
                                if (best >= 0)
                                {
                                    if (distance > bestDistance) continue;
                                    if (distance == bestDistance && index > best) continue;
                                }

                                best = index;
                                bestDistance = distance;
                            }
                        }
                    }
                }

                return best >= 0;
            }

            private int CellCoord(float value)
            {
                // A non-finite coordinate is rejected before the index is built; this only keeps a future caller
                // that skipped that check from producing an undefined int conversion. The clamp keeps a neighbour
                // step (cx ± 1) inside the 21 bits the packing reserves per axis, so a far-away coordinate can
                // never wrap onto a real cell.
                if (!ApaNumericPolicy.IsFinite(value)) return 0;

                var cell = value / _cellSize;
                if (cell >= CellCoordinateLimit) return CellCoordinateLimit;
                if (cell <= -CellCoordinateLimit) return -CellCoordinateLimit;
                return Mathf.FloorToInt(cell);
            }

            /// <summary>
            /// Packs a cell coordinate triple into a single 64-bit key.
            /// </summary>
            /// <remarks>
            /// The packing is exact rather than hashed: each axis contributes 21 bits, so distinct cells within
            /// ±1,048,576 cells of the origin get distinct keys and cannot collide.
            /// </remarks>
            private static long Combine(int x, int y, int z)
            {
                unchecked
                {
                    const long mask = (1L << CellsPerAxisBits) - 1L;
                    var packed = ((long)x & mask) << (CellsPerAxisBits * 2);
                    packed |= ((long)y & mask) << CellsPerAxisBits;
                    packed |= (long)z & mask;
                    return packed;
                }
            }
        }

        /// <summary>Bits of cell coordinate packed per axis; 21 bits covers ±1,048,576 cells.</summary>
        private const int CellsPerAxisBits = 21;

        /// <summary>
        /// Largest cell coordinate magnitude, leaving one cell of headroom for the 27-cell neighbour probe.
        /// </summary>
        private const int CellCoordinateLimit = (1 << (CellsPerAxisBits - 1)) - 2;
    }
}

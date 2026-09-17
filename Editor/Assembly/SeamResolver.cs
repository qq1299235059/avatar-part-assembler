using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// One established correspondence between a part seam vertex and the base seam vertex it welds onto.
    /// </summary>
    public struct SeamMatch
    {
        /// <summary>Index into the part mesh's vertex list.</summary>
        public int PartVertex;

        /// <summary>Index into the base mesh's vertex list.</summary>
        public int BaseVertex;

        /// <summary>
        /// Distance between the two paired positions in avatar-root local space, for diagnostics.
        /// </summary>
        /// <remarks>
        /// Since M10 the pairing is the author's explicit decision rather than a nearest-neighbour result, so this
        /// value is <i>reported</i>, never used to accept or reject a pair: a pair the author made is a pair even
        /// when the two vertices are not exactly coincident, and the world-space tolerance that decided it lives
        /// in the authoring generator (<c>ApaSeamWorldMatcher</c>), where it is stated in world units and shown in
        /// the window.
        /// </remarks>
        public float Distance;

        /// <summary>
        /// Always zero since M10. Retained so that a caller written against the M2 type keeps compiling; the
        /// quantization error it used to carry described a position-hash candidate search that no longer exists.
        /// </summary>
        public float HashError;

        /// <summary>Creates a match.</summary>
        public SeamMatch(int partVertex, int baseVertex, float distance, float hashError = 0f)
        {
            PartVertex = partVertex;
            BaseVertex = baseVertex;
            Distance = distance;
            HashError = hashError;
        }
    }

    /// <summary>
    /// The complete seam correspondence for one part.
    /// </summary>
    public sealed class SeamResolution
    {
        /// <summary>The matches, ordered by <see cref="SeamMatch.PartVertex"/>. Deterministic by construction.</summary>
        public IReadOnlyList<SeamMatch> Matches { get; }

        /// <summary>Number of matched vertices; equals the seam cardinality when the seam is valid.</summary>
        public int Count => Matches.Count;

        /// <summary>True when the seam was fully matched without ambiguity.</summary>
        public bool IsComplete { get; }

        private SeamResolution(IReadOnlyList<SeamMatch> matches, bool isComplete)
        {
            var copy = new SeamMatch[matches?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = matches[i];
            Matches = Array.AsReadOnly(copy);
            IsComplete = isComplete;
        }

        /// <summary>An empty resolution for a part that declares no seam.</summary>
        public static readonly SeamResolution Empty = new SeamResolution(Array.Empty<SeamMatch>(), true);

        internal static SeamResolution Create(List<SeamMatch> matches, bool isComplete)
        {
            matches.Sort(CompareMatches);
            return new SeamResolution(matches, isComplete);
        }

        private static int CompareMatches(SeamMatch a, SeamMatch b)
        {
            var c = a.PartVertex.CompareTo(b.PartVertex);
            if (c != 0) return c;
            return a.BaseVertex.CompareTo(b.BaseVertex);
        }

        /// <summary>
        /// Looks up the base vertex a part vertex welds onto. Returns -1 when the part vertex is not a seam
        /// vertex.
        /// </summary>
        public int BaseVertexFor(int partVertex)
        {
            for (var i = 0; i < Matches.Count; i++)
            {
                if (Matches[i].PartVertex == partVertex) return Matches[i].BaseVertex;
            }

            return -1;
        }
    }

    /// <summary>
    /// Consumes the explicit, position-by-position seam pairing a profile declares and verifies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pairing is author data, not a search result (M10).</b> The authoring window generates the
    /// correspondence once, from world-coincident vertex positions and a stated world tolerance, and writes it
    /// into <see cref="ApaSeamProfile"/> with <see cref="ApaSeamProfile.ExplicitPairingVersion"/>. This resolver
    /// reads position <c>i</c> of each list as one weld and never looks for a partner.
    /// </para>
    /// <para>
    /// <b>Why the old position search is gone.</b> It ran in avatar-root local space, so the tolerance it applied
    /// was scaled by every transform between the avatar root and the geometry: the same epsilon meant a different
    /// world distance on a scaled armature level, and re-deriving the pairing at build time could disagree with
    /// what the author saw in the Scene View. It was also a <i>guess</i> whenever two base vertices were close
    /// together. Both problems disappear when the correspondence is decided once, in world space, and stored.
    /// </para>
    /// <para>
    /// <b>What is still verified.</b> The index sets (range and duplicates, per side), the cardinality, the
    /// pairing version, and the finiteness of every paired position. A duplicate base claim — two part vertices
    /// welded onto one base vertex — is refused with <c>APA003</c>, because the weld would then have to give one
    /// base vertex two different vertices' worth of provenance, and the generator never produces it.
    /// </para>
    /// <para>
    /// <b>The space matrices are still accepted</b> so that every existing caller keeps compiling and so that the
    /// finiteness check can be stated in the space the pipeline actually works in. They are deliberately not used
    /// for matching: using them for matching is the defect this design removes.
    /// </para>
    /// </remarks>
    public static class SeamResolver
    {
        /// <summary>
        /// Resolves the seam for one part against the base mesh.
        /// </summary>
        /// <param name="part">The part being welded.</param>
        /// <param name="baseMesh">The base body mesh.</param>
        /// <param name="partToAvatarLocal">
        /// Maps part-local positions into avatar-root local space. Used for the finiteness check and the reported
        /// pair distance only; the pairing comes from the profile.
        /// </param>
        /// <param name="baseToAvatarLocal">Maps base-local positions into avatar-root local space. See above.</param>
        /// <param name="policy">
        /// Numeric tolerances. <b>Deliberately not consulted for matching</b> — the position epsilon is an
        /// avatar-root-local quantity and the correspondence is now decided in world space at authoring time — but
        /// still accepted so that every caller and the numeric-policy plumbing stay unchanged.
        /// </param>
        /// <param name="issues">Receives diagnostics.</param>
        /// <returns>
        /// A resolution on success, or null when the seam could not be resolved. A null result always comes with
        /// at least one error issue.
        /// </returns>
        public static SeamResolution Resolve(
            PartSnapshot part,
            MeshSnapshot baseMesh,
            Matrix4x4 partToAvatarLocal,
            Matrix4x4 baseToAvatarLocal,
            ApaNumericPolicy policy,
            List<ValidationIssue> issues)
        {
            if (part == null || baseMesh == null) return null;

            var seam = part.Seam;
            if (seam == null) return SeamResolution.Empty;

            var baseIndices = seam.Base != null ? seam.Base.VertexIndices : Array.Empty<int>();
            var partIndices = seam.Part != null ? seam.Part.VertexIndices : Array.Empty<int>();

            if (baseIndices.Length == 0 && partIndices.Length == 0) return SeamResolution.Empty;

            // A seam written before M10 is two unordered sets. Reading them positionally would weld every vertex
            // to whatever happened to be at the same list position, so it is refused with the one action that
            // fixes it rather than re-derived.
            if (!seam.HasExplicitPairing)
            {
                issues.Add(SeamPairingRequiredIssue(
                    part.PartId, baseIndices.Length, partIndices.Length, seam.PairingVersion));
                return null;
            }

            if (baseIndices.Length != partIndices.Length)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.SeamVertexCountMismatch,
                    ApaIssuePhase.Seam,
                    "Base seam has " + baseIndices.Length + " vertex(es) but the part seam has " +
                    partIndices.Length + ". The strict seam contract requires equal counts.",
                    part.PartId,
                    detail: "baseCount=" + baseIndices.Length + "; partCount=" + partIndices.Length));
                return null;
            }

            var cardinality = baseIndices.Length;

            // Seam index sets must be unique and in range on both sides. A duplicate index would make the pairing
            // ambiguous in a way the author cannot see, so it is rejected here.
            if (!ValidateSeamIndices(baseIndices, baseMesh.VertexCount, "base", part.PartId, issues)) return null;
            if (!ValidateSeamIndices(partIndices, part.Mesh.VertexCount, "part", part.PartId, issues)) return null;

            var matches = new List<SeamMatch>(cardinality);
            var claimedBase = new HashSet<int>();

            for (var i = 0; i < cardinality; i++)
            {
                var baseIndex = baseIndices[i];
                var partIndex = partIndices[i];

                var basePosition = baseToAvatarLocal.MultiplyPoint3x4(baseMesh.Vertices[baseIndex]);
                var partPosition = partToAvatarLocal.MultiplyPoint3x4(part.Mesh.Vertices[partIndex]);

                if (!IsFinite(basePosition))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.NonFiniteValue,
                        ApaIssuePhase.Seam,
                        "Base seam vertex " + baseIndex + " has a non-finite position after transform.",
                        part.PartId,
                        baseIndex,
                        detail: "side=base; vertex=" + baseIndex));
                    return null;
                }

                if (!IsFinite(partPosition))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.NonFiniteValue,
                        ApaIssuePhase.Seam,
                        "Part seam vertex " + partIndex + " has a non-finite position after transform.",
                        part.PartId,
                        partIndex,
                        detail: "side=part; vertex=" + partIndex));
                    return null;
                }

                if (!claimedBase.Add(baseIndex))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.SeamDuplicatePositionMatch,
                        ApaIssuePhase.Seam,
                        "Base seam vertex " + baseIndex + " is claimed by more than one part seam vertex. The " +
                        "correspondence must be one-to-one, so a generated or hand-edited pair list may not weld " +
                        "two part vertices onto one base vertex.",
                        part.PartId,
                        partIndex,
                        baseIndex,
                        detail: "side=base; vertex=" + baseIndex + "; claimedBy=" + partIndex +
                                "; pair=" + i + "; reason=duplicate-base-claim"));
                    return null;
                }

                var pairDistance = (basePosition - partPosition).magnitude;
                matches.Add(new SeamMatch(partIndex, baseIndex, pairDistance, 0f));
            }

            return SeamResolution.Create(matches, matches.Count == cardinality);
        }

        /// <summary>
        /// The blocking diagnostic for a seam whose two lists are not explicit pairs.
        /// </summary>
        /// <remarks>
        /// One definition, shared with the authoring window's <c>ApaSeamSelection.Validate</c>, so the authoring
        /// report and the build report for the same condition are byte-identical and the remedy sentence is
        /// written once. It lives in the core rather than in the authoring layer because the build is the
        /// authority for the rule.
        /// </remarks>
        public static ValidationIssue SeamPairingRequiredIssue(
            string partId,
            int baseCount,
            int partCount,
            int pairingVersion)
        {
            return ValidationIssue.Error(
                ApaErrorCode.SeamPairingRequired,
                ApaIssuePhase.Seam,
                "This seam was authored before explicit pairing: its base and part lists are two unordered " +
                "sets, and the build no longer re-derives the correspondence from positions. Reading them " +
                "positionally could weld every vertex to an unrelated one, so the seam is refused instead. " +
                "Open the Part Authoring window and generate the seam from world positions; that writes the " +
                "pairing the build consumes.",
                partId,
                detail: "reason=seam-pairing-required; baseCount=" + baseCount + "; partCount=" + partCount +
                        "; pairingVersion=" + pairingVersion);
        }

        private static bool ValidateSeamIndices(
            int[] indices,
            int vertexCount,
            string side,
            string partId,
            List<ValidationIssue> issues)
        {
            var seen = new HashSet<int>();
            var ok = true;

            for (var i = 0; i < indices.Length; i++)
            {
                var index = indices[i];

                if (index < 0 || index >= vertexCount)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSeamSelection,
                        ApaIssuePhase.Seam,
                        "The " + side + " seam vertex index " + index + " is outside the mesh's " +
                        vertexCount + " vertex(es).",
                        partId,
                        index,
                        detail: "side=" + side + "; vertex=" + index + "; vertexCount=" + vertexCount));
                    ok = false;
                    continue;
                }

                if (!seen.Add(index))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSeamSelection,
                        ApaIssuePhase.Seam,
                        "The " + side + " seam declares vertex index " + index + " more than once.",
                        partId,
                        index,
                        detail: "side=" + side + "; vertex=" + index + "; reason=duplicate"));
                    ok = false;
                }
            }

            return ok;
        }

        private static bool IsFinite(Vector3 v)
        {
            return ApaNumericPolicy.IsFinite(v.x) && ApaNumericPolicy.IsFinite(v.y) && ApaNumericPolicy.IsFinite(v.z);
        }

    }
}

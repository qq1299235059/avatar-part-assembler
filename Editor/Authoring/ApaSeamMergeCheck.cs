using System;
using System.Collections.Generic;
using System.Globalization;
using AvatarPartAssembler.Editor.Localization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The prospective pairing of the selected seam candidates, as the merge-check overlay shows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read-only by construction.</b> Every array here is a fresh copy: the overlay never writes the profile,
    /// the draft, or the stored seam, so enabling the mode cannot change what will be assembled. The classification
    /// is exactly "what would <see cref="ApaSeamWorldMatcher"/> pair right now", which is why the mode answers the
    /// author's real question — "will generating the seam produce the weld I expect?" — without generating it.
    /// </para>
    /// <para>
    /// <b>Two diagnostics, because there are two outcomes.</b> <see cref="Issue"/> means no classification exists
    /// at all: a side's candidate policy could not be resolved (the <c>APA052</c> contract for the part, or the
    /// missing-mesh conditions for the target), so there is nothing to match. <see cref="MatchIssue"/> means the
    /// classification <i>does</i> exist but the matcher refused the run — typically <c>APA002</c>, no part
    /// candidate is within the tolerance of a target vertex. That state is still worth drawing: every part
    /// candidate is unmatched, and seeing them all red is the diagnosis.
    /// </para>
    /// <para>
    /// <b>The two sides are classified by different rules, deliberately.</b> The part side is a color filter, so
    /// every candidate it declares is either matched or unmatched, and an unmatched part candidate is a defect the
    /// author can see. The target side is spatial (every body vertex is a candidate), so an unclaimed body vertex
    /// is not a defect at all — it is simply not near a part seam vertex. A spatial target therefore reports an
    /// empty <see cref="UnmatchedTargetIndices"/>, and drawing the body red because most of it is not welded would
    /// be a false alarm on every part.
    /// </para>
    /// </remarks>
    public sealed class ApaSeamMergeCheckResult
    {
        /// <summary>True when a classification was produced (the matcher itself may still have refused).</summary>
        public bool Succeeded => Issue == null;

        /// <summary>
        /// The blocking diagnostic that prevented any classification, or null. Set when a side's candidate policy
        /// could not be resolved, or when the selection itself is unusable.
        /// </summary>
        public ValidationIssue Issue { get; }

        /// <summary>
        /// The matcher's diagnostic when the prospective run produced no pair, or null. The classification is
        /// still valid when this is set: every candidate is unmatched.
        /// </summary>
        public ValidationIssue MatchIssue { get; }

        /// <summary>Target vertices the prospective run paired, in pair order.</summary>
        public int[] MatchedTargetIndices { get; }

        /// <summary>Part candidate vertices the prospective run paired, in pair order.</summary>
        public int[] MatchedPartIndices { get; }

        /// <summary>
        /// Target vertices with no counterpart within the tolerance, ascending. Always empty when
        /// <see cref="TargetIsSpatial"/>: the target side is a search space, so an unclaimed body vertex is not a
        /// defect and is never drawn as one.
        /// </summary>
        public int[] UnmatchedTargetIndices { get; }

        /// <summary>Part candidate vertices with no counterpart within the tolerance, ascending.</summary>
        public int[] UnmatchedPartIndices { get; }

        /// <summary>Number of pairs the prospective run produced.</summary>
        public int PairCount => MatchedPartIndices.Length;

        /// <summary>
        /// True when the target side is spatial: every target vertex is a candidate and position decides the
        /// pairing. The window and the Scene View label use this to describe the target side as spatial instead of
        /// reporting a color-filtered candidate count.
        /// </summary>
        public bool TargetIsSpatial { get; }

        /// <summary>
        /// Number of target vertices the run considered: the spatial search space when
        /// <see cref="TargetIsSpatial"/>, otherwise the size of the resolved target candidate list.
        /// </summary>
        public int TargetCandidateCount { get; }

        /// <summary>Number of part candidate vertices the run considered.</summary>
        public int PartCandidateCount { get; }

        /// <summary>The world-space tolerance the run used.</summary>
        public float Tolerance { get; }

        private ApaSeamMergeCheckResult(
            ValidationIssue issue,
            ValidationIssue matchIssue,
            int[] matchedTargetIndices,
            int[] matchedPartIndices,
            int[] unmatchedTargetIndices,
            int[] unmatchedPartIndices,
            bool targetIsSpatial,
            int targetCandidateCount,
            int partCandidateCount,
            float tolerance)
        {
            Issue = issue;
            MatchIssue = matchIssue;
            MatchedTargetIndices = matchedTargetIndices ?? Array.Empty<int>();
            MatchedPartIndices = matchedPartIndices ?? Array.Empty<int>();
            UnmatchedTargetIndices = unmatchedTargetIndices ?? Array.Empty<int>();
            UnmatchedPartIndices = unmatchedPartIndices ?? Array.Empty<int>();
            TargetIsSpatial = targetIsSpatial;
            TargetCandidateCount = targetCandidateCount;
            PartCandidateCount = partCandidateCount;
            Tolerance = tolerance;
        }

        internal static ApaSeamMergeCheckResult Failure(ValidationIssue issue, float tolerance)
        {
            return new ApaSeamMergeCheckResult(
                issue, null, null, null, null, null, false, 0, 0, tolerance);
        }

        internal static ApaSeamMergeCheckResult Success(
            ValidationIssue matchIssue,
            int[] matchedTargetIndices,
            int[] matchedPartIndices,
            int[] unmatchedTargetIndices,
            int[] unmatchedPartIndices,
            bool targetIsSpatial,
            int targetCandidateCount,
            int partCandidateCount,
            float tolerance)
        {
            return new ApaSeamMergeCheckResult(
                null,
                matchIssue,
                matchedTargetIndices,
                matchedPartIndices,
                unmatchedTargetIndices,
                unmatchedPartIndices,
                targetIsSpatial,
                targetCandidateCount,
                partCandidateCount,
                tolerance);
        }

        /// <summary>A one-line summary of the prospective pairing, for the window and the Scene View label.</summary>
        public string Describe()
        {
            if (!Succeeded) return ApaLocalization.Tr("merge check unavailable");

            if (TargetIsSpatial)
            {
                return ApaLocalization.TrFormat(
                    "{0} matched pair(s); the target side is spatial ({1} target vertex(es)); {2} of {3} part " +
                    "candidate(s) unmatched at a world tolerance of {4}",
                    PairCount,
                    TargetCandidateCount,
                    UnmatchedPartIndices.Length,
                    PartCandidateCount,
                    Tolerance.ToString("G9", CultureInfo.InvariantCulture));
            }

            return ApaLocalization.TrFormat(
                "{0} matched pair(s); {1} of {2} target and {3} of {4} part candidate(s) unmatched at a world " +
                "tolerance of {5}",
                PairCount,
                UnmatchedTargetIndices.Length,
                TargetCandidateCount,
                UnmatchedPartIndices.Length,
                PartCandidateCount,
                Tolerance.ToString("G9", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Computes the prospective world-position pairing of the resolved seam candidates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One matcher, not two.</b> The run goes through <see cref="ApaSeamWorldMatcher.Match"/> with both
    /// candidate policies — the same overload, the same tolerance validation, the same spatial hash, the same
    /// nearest-free tie-break — so the overlay cannot drift from what the generate action would write. The only
    /// thing this type adds is the classification of the matcher's result into matched and unmatched sets, because
    /// the matcher reports the unmatched <i>count</i> rather than the indices.
    /// </para>
    /// <para>
    /// <b>The target side is spatial and the part side is a color filter.</b> The target result contributes its
    /// candidate policy, not a list: a spatial target passes the matcher's documented "all vertices" argument, so
    /// a body mesh with no vertex colors is evaluated exactly like a painted one. Only the part side's candidates
    /// restrict the pairing, which is the same rule <c>Generate From World Positions</c> applies.
    /// </para>
    /// <para>
    /// <b>Nothing is written.</b> The result carries fresh arrays; no profile, draft, or seam object is touched,
    /// which is what makes the mode safe to leave enabled.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> The matched lists keep the matcher's pair order (part vertices ascending, ties to the
    /// lower target index); the unmatched lists are the candidates the matcher did not claim, visited in the
    /// ascending order the candidate contract already guarantees. The same two meshes, transforms, colors, and
    /// tolerance therefore always produce the same classification.
    /// </para>
    /// </remarks>
    public static class ApaSeamMergeCheck
    {
        /// <summary>
        /// Runs the prospective pairing for the resolved candidate policies of both sides.
        /// </summary>
        /// <param name="targetRenderer">The target body renderer.</param>
        /// <param name="targetMesh">The target body mesh, in its rest pose.</param>
        /// <param name="partRenderer">The part renderer.</param>
        /// <param name="partMesh">The part mesh, in its rest pose.</param>
        /// <param name="tolerance">World-space tolerance, exactly as seam generation uses it.</param>
        /// <param name="targetCandidates">
        /// The resolved target candidates — spatial, or an explicit list — or null when they could not be resolved.
        /// </param>
        /// <param name="partCandidates">The resolved part candidates, or null when they could not be resolved.</param>
        public static ApaSeamMergeCheckResult Evaluate(
            Renderer targetRenderer,
            Mesh targetMesh,
            Renderer partRenderer,
            Mesh partMesh,
            float tolerance,
            ApaSeamColorCandidateResult targetCandidates,
            ApaSeamColorCandidateResult partCandidates)
        {
            if (targetCandidates == null || !targetCandidates.Succeeded)
            {
                return ApaSeamMergeCheckResult.Failure(
                    CandidateIssue(targetCandidates, ApaSeamVertexColorCandidates.TargetSide), tolerance);
            }

            if (partCandidates == null || !partCandidates.Succeeded)
            {
                return ApaSeamMergeCheckResult.Failure(
                    CandidateIssue(partCandidates, ApaSeamVertexColorCandidates.PartSide), tolerance);
            }

            var match = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                tolerance,
                targetCandidates.MatcherCandidateIndices,
                partCandidates.Indices);

            var matchedPart = new bool[partCandidates.VertexCount];

            for (var i = 0; i < match.PartIndices.Length; i++)
            {
                var index = match.PartIndices[i];
                if (index >= 0 && index < matchedPart.Length) matchedPart[index] = true;
            }

            var unmatchedPart = CollectUnmatched(partCandidates.Indices, matchedPart);

            // A spatial target has no nominated candidate list, so there is no such thing as an "unmatched target
            // candidate": the body vertices the run did not claim are simply not welded, which is true of every
            // part. Only a color-filtered target side — which the window no longer produces, but an explicit seam
            // consumer can still build — is classified candidate by candidate.
            int[] unmatchedTarget;
            if (targetCandidates.IsSpatial)
            {
                unmatchedTarget = Array.Empty<int>();
            }
            else
            {
                var matchedTarget = new bool[targetCandidates.VertexCount];
                for (var i = 0; i < match.BaseIndices.Length; i++)
                {
                    var index = match.BaseIndices[i];
                    if (index >= 0 && index < matchedTarget.Length) matchedTarget[index] = true;
                }

                unmatchedTarget = CollectUnmatched(targetCandidates.Indices, matchedTarget);
            }

            return ApaSeamMergeCheckResult.Success(
                match.Succeeded ? null : match.Issue,
                match.BaseIndices,
                match.PartIndices,
                unmatchedTarget,
                unmatchedPart,
                targetCandidates.IsSpatial,
                targetCandidates.IsSpatial ? targetCandidates.VertexCount : targetCandidates.Indices.Length,
                partCandidates.Indices.Length,
                tolerance);
        }

        /// <summary>
        /// The blocking diagnostic of a side whose candidates could not be resolved.
        /// </summary>
        /// <remarks>
        /// A null result is reported as the same <c>APA052</c> condition the resolver would have produced, so a
        /// caller that never resolved the side gets a blocking, searchable diagnostic rather than an exception or
        /// an empty classification.
        /// </remarks>
        private static ValidationIssue CandidateIssue(ApaSeamColorCandidateResult candidates, string side)
        {
            if (candidates != null && candidates.Issue != null) return candidates.Issue;

            return ValidationIssue.Error(
                ApaErrorCode.SeamCandidateColorInvalid,
                ApaIssuePhase.Seam,
                "The " + side + " seam candidate color has not been resolved, so no prospective pairing exists.",
                detail: "side=" + side + "; reason=vertex-color-candidates-unresolved");
        }

        /// <summary>
        /// The candidate indices the matcher did not claim, in the ascending order the candidate list already has.
        /// </summary>
        private static int[] CollectUnmatched(int[] candidates, bool[] matched)
        {
            var unmatched = new List<int>();
            for (var i = 0; i < candidates.Length; i++)
            {
                var index = candidates[i];
                if (index >= 0 && index < matched.Length && matched[index]) continue;
                unmatched.Add(index);
            }

            return unmatched.ToArray();
        }
    }
}

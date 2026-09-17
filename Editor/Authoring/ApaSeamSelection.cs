using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The editable paired seam loops of one part: the retained base body vertices and the part vertices that
    /// will be welded onto them, paired by position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the authoring-side editor state for <see cref="ApaSeamProfile"/>. Since M10 the two lists are
    /// <b>pairs</b>: <c>BaseIndices[i]</c> and <c>PartIndices[i]</c> are one weld, decided by the author through
    /// the world-position generator in the Part Authoring window. The build consumes exactly that
    /// correspondence; it never re-derives one.
    /// </para>
    /// <para>
    /// <see cref="PairingVersion"/> mirrors <see cref="ApaSeamProfile.PairingVersion"/> and is the only thing
    /// that says whether the two lists may be read as pairs. A profile written before M10 carries two unordered
    /// sets, and reading them positionally would weld every vertex to an unrelated one, so such a selection
    /// reports <see cref="ApaErrorCode.SeamPairingRequired"/> until it is regenerated.
    /// </para>
    /// <para>
    /// <b>Author data is preserved, never repaired.</b> A duplicate index, or a negative index, that somehow
    /// reaches a list is kept and reported by <see cref="Validate"/> with the same code and detail tokens the
    /// assembly core uses (<c>APA018</c>, <c>APA001</c>). Silently de-duplicating it would hide a mistake the
    /// author has to fix, and would make the editor's report disagree with the build's.
    /// </para>
    /// <para>
    /// The per-side editing helpers (<see cref="SetBase"/>, <see cref="TryAddBase"/>, and their peers) exist for
    /// reading and repairing older data and for tests. They deliberately <i>downgrade</i> the selection to the
    /// legacy, unpaired state, because editing one side of a pair cannot preserve a correspondence the editor no
    /// longer knows. Only <see cref="SetPaired"/> writes the paired state.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaSeamSelection
    {
        [SerializeField] private int[] _baseIndices = Array.Empty<int>();
        [SerializeField] private int[] _partIndices = Array.Empty<int>();
        [SerializeField] private int _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;

        /// <summary>
        /// Whether the two lists are pairs by position. Mirrors <see cref="ApaSeamProfile.PairingVersion"/>.
        /// </summary>
        public int PairingVersion => _pairingVersion;

        /// <summary>True when the two lists are explicit, position-by-position pairs.</summary>
        public bool IsPaired => _pairingVersion == ApaSeamProfile.ExplicitPairingVersion;

        /// <summary>Number of selected base seam vertices.</summary>
        public int BaseCount => _baseIndices != null ? _baseIndices.Length : 0;

        /// <summary>Number of selected part seam vertices.</summary>
        public int PartCount => _partIndices != null ? _partIndices.Length : 0;

        /// <summary>True when both loops are empty, which means "this part declares no seam".</summary>
        public bool IsEmpty => BaseCount == 0 && PartCount == 0;

        /// <summary>True when the two loops have the same cardinality.</summary>
        public bool HasEqualCounts => BaseCount == PartCount;

        /// <summary>
        /// True when this selection can be consumed as a seam: empty, or paired with equal counts.
        /// </summary>
        /// <remarks>
        /// A legacy, unpaired selection is not consumable at all, which is what
        /// <see cref="ApaErrorCode.SeamPairingRequired"/> reports rather than a mismatch of counts.
        /// </remarks>
        public bool IsConsumable => IsEmpty || (IsPaired && HasEqualCounts);

        /// <summary>A copy of the base loop, in pair order.</summary>
        public int[] GetBaseIndices()
        {
            return (int[])(_baseIndices ?? Array.Empty<int>()).Clone();
        }

        /// <summary>A copy of the part loop, in pair order.</summary>
        public int[] GetPartIndices()
        {
            return (int[])(_partIndices ?? Array.Empty<int>()).Clone();
        }

        /// <summary>
        /// Replaces both lists with an explicit pairing, in the order given.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The only writer of the paired state, and the only setter that does not sort. Sorting either side would
        /// destroy the correspondence the caller just computed, which is exactly the bug the paired form exists
        /// to make impossible.
        /// </para>
        /// <para>
        /// The two arrays are copied, and a length mismatch is preserved rather than padded: the mismatch is
        /// author data that validation reports as <c>APA001</c>, not something to repair by inventing a partner
        /// for the extra index.
        /// </para>
        /// </remarks>
        public void SetPaired(int[] baseIndices, int[] partIndices)
        {
            _baseIndices = Copy(baseIndices);
            _partIndices = Copy(partIndices);
            _pairingVersion = ApaSeamProfile.ExplicitPairingVersion;
        }

        /// <summary>
        /// Replaces the base loop with author-supplied indices, sorted ascending.
        /// </summary>
        /// <remarks>
        /// A per-side edit, so the result is the <b>legacy, unpaired</b> state: one side of a pairing has been
        /// rewritten and the correspondence is no longer known. The seam rule then requires a regeneration
        /// rather than guessing, which is the same rule a legacy profile gets.
        /// </remarks>
        public void SetBase(IEnumerable<int> indices)
        {
            _baseIndices = Canonicalize(indices);
            _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;
        }

        /// <summary>Replaces the part loop with author-supplied indices, sorted ascending. See <see cref="SetBase"/>.</summary>
        public void SetPart(IEnumerable<int> indices)
        {
            _partIndices = Canonicalize(indices);
            _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;
        }

        /// <summary>
        /// Adds one index to the base loop when it is not already selected.
        /// </summary>
        /// <returns>True when the index was added; false when it was already present.</returns>
        public bool TryAddBase(int vertexIndex)
        {
            var added = TryAdd(ref _baseIndices, vertexIndex);
            if (added) _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;
            return added;
        }

        /// <summary>Adds one index to the part loop when it is not already selected.</summary>
        public bool TryAddPart(int vertexIndex)
        {
            var added = TryAdd(ref _partIndices, vertexIndex);
            if (added) _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;
            return added;
        }

        /// <summary>Removes every occurrence of an index from the base loop.</summary>
        /// <returns>True when at least one entry was removed.</returns>
        public bool RemoveBase(int vertexIndex)
        {
            var removed = RemoveAll(ref _baseIndices, vertexIndex);
            if (removed) _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;
            return removed;
        }

        /// <summary>Removes every occurrence of an index from the part loop.</summary>
        /// <returns>True when at least one entry was removed.</returns>
        public bool RemovePart(int vertexIndex)
        {
            var removed = RemoveAll(ref _partIndices, vertexIndex);
            if (removed) _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;
            return removed;
        }

        /// <summary>Toggles one index in the base loop.</summary>
        /// <returns>True when the index is selected after the call.</returns>
        public bool ToggleBase(int vertexIndex)
        {
            if (RemoveBase(vertexIndex)) return false;
            TryAddBase(vertexIndex);
            return true;
        }

        /// <summary>Toggles one index in the part loop.</summary>
        /// <returns>True when the index is selected after the call.</returns>
        public bool TogglePart(int vertexIndex)
        {
            if (RemovePart(vertexIndex)) return false;
            TryAddPart(vertexIndex);
            return true;
        }

        /// <summary>Clears one side, or both when <paramref name="side"/> is null.</summary>
        /// <remarks>
        /// Clearing both sides is the documented "this part declares no seam" state and resets the pairing
        /// version. Clearing one side leaves an unpaired selection, which validation refuses; the pairing version
        /// is downgraded so that refusal is the reported reason rather than a silent half-seam.
        /// </remarks>
        public void Clear(ApaSeamSideKind? side = null)
        {
            if (side == null || side == ApaSeamSideKind.Base) _baseIndices = Array.Empty<int>();
            if (side == null || side == ApaSeamSideKind.Part) _partIndices = Array.Empty<int>();

            _pairingVersion = ApaSeamProfile.LegacyUnpairedVersion;
        }

        /// <summary>An independent copy.</summary>
        public ApaSeamSelection Clone()
        {
            return new ApaSeamSelection
            {
                _baseIndices = (int[])(_baseIndices ?? Array.Empty<int>()).Clone(),
                _partIndices = (int[])(_partIndices ?? Array.Empty<int>()).Clone(),
                _pairingVersion = _pairingVersion
            };
        }

        /// <summary>
        /// Builds the serialized profile form, preserving the pairing version.
        /// </summary>
        /// <remarks>
        /// Indices are written in pair order and duplicates are deliberately preserved: the profile is only
        /// written after validation has passed, so a duplicate that reaches here is a bug the validation report
        /// has to keep showing rather than a value to quietly collapse.
        /// </remarks>
        public ApaSeamProfile ToSeamProfile()
        {
            return new ApaSeamProfile
            {
                Base = new ApaSeamSide(GetBaseIndices()),
                Part = new ApaSeamSide(GetPartIndices()),
                PairingVersion = _pairingVersion
            };
        }

        /// <summary>Reads the loops and the pairing version out of a serialized profile.</summary>
        public static ApaSeamSelection FromSeamProfile(ApaSeamProfile profile)
        {
            var selection = new ApaSeamSelection();
            if (profile == null) return selection;

            selection._baseIndices = Copy(profile.Base != null ? profile.Base.VertexIndices : Array.Empty<int>());
            selection._partIndices = Copy(profile.Part != null ? profile.Part.VertexIndices : Array.Empty<int>());
            selection._pairingVersion = profile.PairingVersion;
            return selection;
        }

        /// <summary>
        /// Checks the loops against the selected meshes, reporting the same codes and detail tokens the assembly
        /// core reports.
        /// </summary>
        /// <param name="baseVertexCount">Vertex count of the target body mesh, or -1 when unknown.</param>
        /// <param name="partVertexCount">Vertex count of the part mesh, or -1 when unknown.</param>
        /// <param name="partId">Part identity used in the diagnostics.</param>
        /// <remarks>
        /// <para>
        /// The order and wording mirror <see cref="SeamResolver"/>, which remains the authoritative
        /// implementation: a legacy, unpaired selection is reported alone (there is nothing to check pairs
        /// against), then a cardinality mismatch is reported alone, because matching indices against the wrong
        /// loop length is meaningless, and index-set defects follow, base side first. For a given defect the
        /// code, message, and detail tokens are identical, so an author who fixes what the window reports sees
        /// the build agree.
        /// </para>
        /// <para>
        /// One deliberate difference: the core stops after the first unusable side, because it cannot match
        /// indices it does not trust, while this method keeps checking the other side so the author sees every
        /// bad index in one pass. The authoring report is therefore a superset of the build's for this defect
        /// class, never a contradiction.
        /// </para>
        /// </remarks>
        public List<ValidationIssue> Validate(int baseVertexCount, int partVertexCount, string partId)
        {
            var issues = new List<ValidationIssue>();

            if (IsEmpty) return issues;

            if (!IsPaired)
            {
                issues.Add(SeamResolver.SeamPairingRequiredIssue(partId, BaseCount, PartCount, _pairingVersion));
                return issues;
            }

            if (BaseCount != PartCount)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.SeamVertexCountMismatch,
                    ApaIssuePhase.Seam,
                    "Base seam has " + BaseCount + " vertex(es) but the part seam has " + PartCount +
                    ". The strict seam contract requires equal counts.",
                    partId,
                    detail: "baseCount=" + BaseCount + "; partCount=" + PartCount));
                return issues;
            }

            ValidateIndices(_baseIndices, "base", baseVertexCount, partId, issues);
            ValidateIndices(_partIndices, "part", partVertexCount, partId, issues);
            return issues;
        }

        /// <summary>
        /// The blocking diagnostic for a seam whose two lists are not explicit pairs.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="SeamResolver.SeamPairingRequiredIssue"/>, which owns the wording: the core is
        /// the authority for the rule, and one definition is what keeps the window's report and a build's report
        /// for the same condition identical.
        /// </remarks>
        public static ValidationIssue SeamPairingRequiredIssue(
            string partId,
            int baseCount,
            int partCount,
            int pairingVersion)
        {
            return SeamResolver.SeamPairingRequiredIssue(partId, baseCount, partCount, pairingVersion);
        }

        /// <summary>A short status line: the pair count, or the legacy state.</summary>
        public string Describe()
        {
            if (IsEmpty) return Tr("no seam pairs");
            if (!IsPaired) return Tr("unpaired legacy seam");

            return TrFormat("{0} seam pair(s)", BaseCount < PartCount ? BaseCount : PartCount);
        }

        /// <summary>A short preview of the first pairs, for the window's summary line.</summary>
        /// <param name="maxPairs">Most pairs to render.</param>
        public string DescribePairs(int maxPairs)
        {
            if (IsEmpty || !IsPaired) return Describe();

            var pairs = BaseCount < PartCount ? BaseCount : PartCount;
            var limit = Mathf.Min(pairs, Mathf.Max(maxPairs, 0));
            if (limit == 0) return Describe();

            var bases = new int[limit];
            var parts = new int[limit];
            for (var i = 0; i < limit; i++)
            {
                bases[i] = _baseIndices[i];
                parts[i] = _partIndices[i];
            }

            return TrFormat(
                "{0} seam pair(s); first: {1} -> {2}",
                pairs,
                FormatIndexList(bases),
                FormatIndexList(parts));
        }

        /// <summary>
        /// The number of pairs the selection holds, which is the smaller of the two cardinalities.
        /// </summary>
        public int PairCount => BaseCount < PartCount ? BaseCount : PartCount;

        /// <summary>
        /// Parses the numeric fallback the picker cannot replace: a list of vertex indices such as
        /// <c>0, 1, 2</c>, with inclusive ranges such as <c>0-7</c> or <c>0..7</c>.
        /// </summary>
        /// <param name="text">Author input. Commas, semicolons, and any whitespace separate entries.</param>
        /// <param name="indices">
        /// On success, the parsed indices in input order. Duplicates are preserved, because a duplicate is author
        /// data that validation has to report rather than a value to collapse.
        /// </param>
        /// <param name="error">
        /// On failure, a stable token: <c>not-a-number:&lt;token&gt;</c>, <c>invalid-range:&lt;token&gt;</c>, or
        /// <c>too-large:&lt;token&gt;</c>.
        /// </param>
        /// <returns>
        /// True when the text parsed. Empty or whitespace-only input succeeds and yields an empty list, because
        /// clearing a loop by emptying its field is a legitimate author action.
        /// </returns>
        public static bool TryParseIndexList(string text, out int[] indices, out string error)
        {
            indices = Array.Empty<int>();
            error = string.Empty;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return true;

            var tokens = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<int>(tokens.Length);

            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i].Trim();
                if (token.Length == 0) continue;

                if (TryParseRange(token, out var rangeStart, out var rangeEnd, out var rangeError))
                {
                    if (rangeError.Length > 0)
                    {
                        error = rangeError;
                        indices = Array.Empty<int>();
                        return false;
                    }

                    for (var value = rangeStart; value <= rangeEnd; value++) result.Add(value);
                    continue;
                }

                if (!int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var single))
                {
                    error = "not-a-number:" + token;
                    indices = Array.Empty<int>();
                    return false;
                }

                result.Add(single);
            }

            indices = result.ToArray();
            return true;
        }

        /// <summary>Renders an index list in the same form <see cref="TryParseIndexList"/> accepts.</summary>
        public static string FormatIndexList(IReadOnlyList<int> indices)
        {
            if (indices == null || indices.Count == 0) return string.Empty;

            var parts = new string[indices.Count];
            for (var i = 0; i < indices.Count; i++)
            {
                parts[i] = indices[i].ToString(CultureInfo.InvariantCulture);
            }

            return string.Join(", ", parts);
        }

        private static void ValidateIndices(
            int[] indices,
            string side,
            int vertexCount,
            string partId,
            List<ValidationIssue> issues)
        {
            if (indices == null) return;

            var seen = new HashSet<int>();
            for (var i = 0; i < indices.Length; i++)
            {
                var index = indices[i];

                if (vertexCount >= 0 && (index < 0 || index >= vertexCount))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSeamSelection,
                        ApaIssuePhase.Seam,
                        "The " + side + " seam vertex index " + index + " is outside the mesh's " +
                        vertexCount + " vertex(es).",
                        partId,
                        index,
                        detail: "side=" + side + "; vertex=" + index + "; vertexCount=" + vertexCount));
                    continue;
                }

                if (vertexCount < 0 && index < 0)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSeamSelection,
                        ApaIssuePhase.Seam,
                        "The " + side + " seam declares the negative vertex index " + index + ", which cannot " +
                        "name a vertex.",
                        partId,
                        index,
                        detail: "side=" + side + "; vertex=" + index + "; reason=negative-index"));
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
                }
            }
        }

        /// <summary>
        /// Sorts ascending but keeps duplicates. See <see cref="ToSeamProfile"/> for why de-duplication is not
        /// performed here.
        /// </summary>
        private static int[] Canonicalize(IEnumerable<int> indices)
        {
            if (indices == null) return Array.Empty<int>();

            var list = new List<int>();
            foreach (var index in indices) list.Add(index);
            if (list.Count == 0) return Array.Empty<int>();

            list.Sort();
            return list.ToArray();
        }

        /// <summary>Copies an index array, mapping null to an empty array.</summary>
        /// <remarks>
        /// A copy rather than the caller's array: the paired form is the one place the two lists must stay in
        /// lockstep, and aliasing a caller-owned array would let one side be rewritten behind the selection's
        /// back. The order is preserved exactly, which is what makes the pair positions meaningful.
        /// </remarks>
        private static int[] Copy(int[] indices)
        {
            if (indices == null || indices.Length == 0) return Array.Empty<int>();
            var copy = new int[indices.Length];
            Array.Copy(indices, copy, indices.Length);
            return copy;
        }

        private static bool TryAdd(ref int[] indices, int vertexIndex)
        {
            if (indices == null) indices = Array.Empty<int>();

            var existing = Array.BinarySearch(indices, vertexIndex);
            if (existing >= 0) return false;

            var insertAt = ~existing;
            var result = new int[indices.Length + 1];
            for (var i = 0; i < insertAt; i++) result[i] = indices[i];
            result[insertAt] = vertexIndex;
            for (var i = insertAt; i < indices.Length; i++) result[i + 1] = indices[i];
            indices = result;
            return true;
        }

        private static bool RemoveAll(ref int[] indices, int vertexIndex)
        {
            if (indices == null || indices.Length == 0) return false;

            var kept = new List<int>(indices.Length);
            for (var i = 0; i < indices.Length; i++)
            {
                if (indices[i] != vertexIndex) kept.Add(indices[i]);
            }

            if (kept.Count == indices.Length) return false;
            indices = kept.ToArray();
            return true;
        }

        /// <summary>
        /// Parses a range token. A token without a range separator, and a token that begins with the sign of a
        /// negative number, are not ranges.
        /// </summary>
        private static bool TryParseRange(string token, out int start, out int end, out string error)
        {
            start = 0;
            end = 0;
            error = string.Empty;

            var separator = token.IndexOf("..", StringComparison.Ordinal);
            var separatorLength = 2;

            if (separator < 0)
            {
                separator = token.IndexOf('-', 1);
                separatorLength = 1;
            }

            if (separator <= 0) return false;

            var left = token.Substring(0, separator).Trim();
            var right = token.Substring(separator + separatorLength).Trim();
            if (left.Length == 0 || right.Length == 0) return false;

            if (!int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start)
                || !int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out end))
            {
                error = "invalid-range:" + token;
                return true;
            }

            if (end < start)
            {
                error = "invalid-range:" + token;
                return true;
            }

            // A range is expanded in memory, so an absurd span is refused rather than allocated. The bound is
            // far above any real seam loop and far below an allocation that would hang the editor.
            if ((long)end - start > 100000L)
            {
                error = "too-large:" + token;
                return true;
            }

            return true;
        }
    }

    /// <summary>Which side of a seam loop an edit applies to.</summary>
    public enum ApaSeamSideKind
    {
        /// <summary>The retained base body loop.</summary>
        Base = 0,

        /// <summary>The part loop that welds onto the base.</summary>
        Part = 1
    }
}

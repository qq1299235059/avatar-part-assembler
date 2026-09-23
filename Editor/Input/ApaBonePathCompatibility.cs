using System;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The narrow bone-path compatibility policy used after Marshmallow PB inserts a same-named wrapper around
    /// the selected breast bone.
    /// </summary>
    /// <remarks>
    /// A recorded source path is compatible with a live target path only when the live path appends one or more
    /// copies of the recorded final segment. A different parent, a different inserted name, or a missing segment
    /// remains a mismatch. The relation is deliberately directional: the authored path is the expected identity,
    /// while the post-plugin target path is the live hierarchy. Keeping the predicate in one place prevents
    /// compatibility validation, seam safety, and final bone remapping from disagreeing.
    /// </remarks>
    internal static class ApaBonePathCompatibility
    {
        /// <summary>
        /// True when a recorded source path is identical to the live target path, or the live path is the source
        /// path followed by one or more repeated copies of its final segment.
        /// </summary>
        internal static bool MatchesLiveTarget(string recordedPath, string livePath)
        {
            if (string.Equals(recordedPath, livePath, StringComparison.Ordinal)) return true;
            return HasOnlyRepeatedFinalSegmentAppended(recordedPath, livePath);
        }

        /// <summary>
        /// True when <paramref name="actualPath"/> is <paramref name="expectedPath"/> followed by one or more
        /// segments that exactly repeat the expected path's final segment.
        /// </summary>
        internal static bool HasOnlyRepeatedFinalSegmentAppended(string expectedPath, string actualPath)
        {
            if (string.IsNullOrEmpty(expectedPath) || string.IsNullOrEmpty(actualPath)) return false;

            var separator = expectedPath.LastIndexOf('/');
            var finalSegment = separator >= 0 ? expectedPath.Substring(separator + 1) : expectedPath;
            if (string.IsNullOrEmpty(finalSegment)) return false;

            var prefix = expectedPath + "/";
            if (!actualPath.StartsWith(prefix, StringComparison.Ordinal)) return false;

            var suffixStart = prefix.Length;
            while (suffixStart < actualPath.Length)
            {
                var nextSeparator = actualPath.IndexOf('/', suffixStart);
                var segmentEnd = nextSeparator >= 0 ? nextSeparator : actualPath.Length;
                if (segmentEnd - suffixStart != finalSegment.Length
                    || string.CompareOrdinal(actualPath, suffixStart, finalSegment, 0, finalSegment.Length) != 0)
                {
                    return false;
                }

                if (nextSeparator < 0) return true;
                suffixStart = nextSeparator + 1;
                if (suffixStart == actualPath.Length) return false;
            }

            return false;
        }
    }
}

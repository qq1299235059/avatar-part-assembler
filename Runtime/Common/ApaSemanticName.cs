using System;

namespace AvatarPartAssembler
{
    /// <summary>
    /// Normalization and comparison rules for every author-facing name that participates in matching:
    /// UV semantics, material slot semantics, part slot names, and blendshape names.
    /// </summary>
    /// <remarks>
    /// The rule is deliberately narrow and applies identically everywhere:
    /// trim leading and trailing whitespace, reject empty, then compare with <see cref="StringComparison.Ordinal"/>.
    /// Names are never case-folded. Case-folding would create collisions that the plugin would then have to
    /// guess about, and the product principle forbids guessing.
    /// </remarks>
    public static class ApaSemanticName
    {
        /// <summary>
        /// Trims a name. Returns null when the input is null; returns an empty string when the input is
        /// whitespace-only, so that callers can distinguish "absent" from "present but invalid".
        /// </summary>
        public static string Normalize(string name)
        {
            return name == null ? null : name.Trim();
        }

        /// <summary>True when the name is non-null and non-empty after trimming.</summary>
        public static bool IsValid(string name)
        {
            var normalized = Normalize(name);
            return !string.IsNullOrEmpty(normalized);
        }

        /// <summary>Ordinal, case-sensitive equality over normalized names.</summary>
        public static bool AreEqual(string a, string b)
        {
            return string.CompareOrdinal(Normalize(a), Normalize(b)) == 0;
        }

        /// <summary>
        /// Ordinal comparer over normalized names, suitable for sorting into a deterministic order.
        /// </summary>
        public static int Compare(string a, string b)
        {
            return string.CompareOrdinal(Normalize(a), Normalize(b));
        }

        /// <summary>
        /// A friendly description of the rule, used verbatim in diagnostics so that authors learn the contract
        /// from the error message.
        /// </summary>
        public const string RuleDescription =
            "semantic names are trimmed of surrounding whitespace and compared ordinally (case-sensitive)";
    }
}

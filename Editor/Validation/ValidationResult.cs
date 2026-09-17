using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The outcome of validation: an ordered, deduplicated list of issues plus a fast "is it blocked" answer.
    /// </summary>
    /// <remarks>
    /// Issue order is part of the contract. <see cref="Build"/> sorts by the deterministic key defined on
    /// <see cref="ValidationIssue"/> and removes exact duplicates, so two runs over equivalent inputs produce
    /// byte-identical reports.
    /// </remarks>
    public sealed class ValidationResult
    {
        private readonly List<ValidationIssue> _issues;
        private readonly ReadOnlyCollection<ValidationIssue> _issueView;

        /// <summary>An empty, successful result.</summary>
        public static readonly ValidationResult Empty = new ValidationResult(new List<ValidationIssue>());

        private ValidationResult(List<ValidationIssue> issues)
        {
            _issues = issues ?? new List<ValidationIssue>();
            _issueView = _issues.AsReadOnly();
        }

        /// <summary>The issues, in deterministic order.</summary>
        public IReadOnlyList<ValidationIssue> Issues => _issueView;

        /// <summary>True when at least one error was reported.</summary>
        public bool HasErrors
        {
            get
            {
                for (var i = 0; i < _issues.Count; i++)
                {
                    if (_issues[i].IsBlocking) return true;
                }

                return false;
            }
        }

        /// <summary>True when nothing blocked. Warnings may still be present.</summary>
        public bool IsValid => !HasErrors;

        /// <summary>Count of issues at the given severity.</summary>
        public int CountOf(ApaSeverity severity)
        {
            var count = 0;
            for (var i = 0; i < _issues.Count; i++)
            {
                if (_issues[i].Severity == severity) count++;
            }

            return count;
        }

        /// <summary>Number of blocking errors.</summary>
        public int ErrorCount => CountOf(ApaSeverity.Error);

        /// <summary>Number of warnings.</summary>
        public int WarningCount => CountOf(ApaSeverity.Warning);

        /// <summary>Number of informational messages.</summary>
        public int InfoCount => CountOf(ApaSeverity.Info);

        /// <summary>True when any issue carries the given code.</summary>
        public bool ContainsCode(string code)
        {
            for (var i = 0; i < _issues.Count; i++)
            {
                if (string.Equals(_issues[i].Code, code, System.StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the first issue with the given code, or null when none is present. Useful for tests that
        /// assert on the specific diagnostic a malformed asset produced.
        /// </summary>
        public ValidationIssue FindByCode(string code)
        {
            for (var i = 0; i < _issues.Count; i++)
            {
                if (string.Equals(_issues[i].Code, code, System.StringComparison.Ordinal)) return _issues[i];
            }

            return null;
        }

        /// <summary>
        /// Sorts, deduplicates, and freezes a mutable issue list into a result.
        /// </summary>
        public static ValidationResult Build(List<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return Empty;

            issues.Sort();

            var deduped = new List<ValidationIssue>(issues.Count);
            for (var i = 0; i < issues.Count; i++)
            {
                if (i > 0 && deduped.Count > 0 && deduped[deduped.Count - 1].Equals(issues[i])) continue;
                deduped.Add(issues[i]);
            }

            return new ValidationResult(deduped);
        }

        /// <summary>
        /// Formats every issue on its own line, in order. Used by the validator window and by test failure
        /// messages so that a failing assertion prints the whole diagnostic set.
        /// </summary>
        public string FormatAll()
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < _issues.Count; i++)
            {
                sb.AppendLine(_issues[i].ToString());
            }

            sb.Append(ErrorCount).Append(" ERROR, ").Append(WarningCount).Append(" WARNING, ")
                .Append(InfoCount).Append(" INFO");
            return sb.ToString();
        }

        /// <summary>Creates a result from a single issue.</summary>
        public static ValidationResult Single(ValidationIssue issue)
        {
            return Build(new List<ValidationIssue> { issue });
        }
    }
}

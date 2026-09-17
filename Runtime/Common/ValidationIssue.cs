using System;

namespace AvatarPartAssembler
{
    /// <summary>
    /// Severity of a <see cref="ValidationIssue"/>. Ordering is significant: a higher value is more severe, and
    /// only <see cref="Error"/> blocks preview and build.
    /// </summary>
    public enum ApaSeverity
    {
        /// <summary>Legal information worth surfacing: a new bone, UV layer, or material slot.</summary>
        Info = 0,

        /// <summary>Legal but noteworthy. Preview and build continue.</summary>
        Warning = 1,

        /// <summary>Violates the asset contract. Preview and build are blocked.</summary>
        Error = 2
    }

    /// <summary>
    /// The pipeline stage that produced an issue. Used only as the primary deterministic sort key so that
    /// issues read in the order a user would debug them.
    /// </summary>
    public enum ApaIssuePhase
    {
        /// <summary>Configuration and profile-level checks.</summary>
        Configuration = 0,

        /// <summary>Target renderer and compatibility signature checks.</summary>
        Compatibility = 1,

        /// <summary>Removal triangle set checks.</summary>
        Removal = 2,

        /// <summary>Seam cardinality, position, and matching checks.</summary>
        Seam = 3,

        /// <summary>UV semantic and channel checks.</summary>
        Uv = 4,

        /// <summary>Material semantic and conflict checks.</summary>
        Materials = 5,

        /// <summary>Mesh attribute support checks.</summary>
        Attributes = 6,

        /// <summary>Camera-independent final mesh construction checks.</summary>
        Assembly = 7
    }

    /// <summary>
    /// A single diagnostic produced by validation or planning.
    /// </summary>
    /// <remarks>
    /// Instances are immutable. Ordering is defined by <see cref="CompareTo"/> and is fully deterministic:
    /// phase, then code, then part identity, then source index, then detail. Nothing in the ordering depends
    /// on collection iteration order.
    /// </remarks>
    public sealed class ValidationIssue : IComparable<ValidationIssue>, IEquatable<ValidationIssue>
    {
        /// <summary>Stable machine-readable code, one of <see cref="ApaErrorCode"/>.</summary>
        public string Code { get; }

        /// <summary>Severity. Only <see cref="ApaSeverity.Error"/> blocks.</summary>
        public ApaSeverity Severity { get; }

        /// <summary>Pipeline stage that produced the issue.</summary>
        public ApaIssuePhase Phase { get; }

        /// <summary>Short human-readable summary.</summary>
        public string Message { get; }

        /// <summary>
        /// Stable identity of the part that produced the issue, or an empty string for base-level issues.
        /// This is the serialized part id, never a hierarchy path.
        /// </summary>
        public string PartId { get; }

        /// <summary>Source vertex, triangle, submesh, or channel index the issue refers to, or -1 when not applicable.</summary>
        public int SourceIndex { get; }

        /// <summary>Secondary index for issues that concern a pair of values, or -1 when not applicable.</summary>
        public int SecondaryIndex { get; }

        /// <summary>
        /// Extra machine-checkable detail. Free-form, but must be stable for identical inputs because tests and
        /// the deterministic ordering both consume it.
        /// </summary>
        public string Detail { get; }

        /// <summary>Creates a diagnostic.</summary>
        public ValidationIssue(
            string code,
            ApaSeverity severity,
            ApaIssuePhase phase,
            string message,
            string partId = "",
            int sourceIndex = -1,
            int secondaryIndex = -1,
            string detail = "")
        {
            Code = code ?? string.Empty;
            Severity = severity;
            Phase = phase;
            Message = message ?? string.Empty;
            PartId = partId ?? string.Empty;
            SourceIndex = sourceIndex;
            SecondaryIndex = secondaryIndex;
            Detail = detail ?? string.Empty;
        }

        /// <summary>Creates an error diagnostic.</summary>
        public static ValidationIssue Error(
            string code,
            ApaIssuePhase phase,
            string message,
            string partId = "",
            int sourceIndex = -1,
            int secondaryIndex = -1,
            string detail = "")
        {
            return new ValidationIssue(code, ApaSeverity.Error, phase, message, partId, sourceIndex, secondaryIndex, detail);
        }

        /// <summary>Creates a warning diagnostic.</summary>
        public static ValidationIssue Warning(
            string code,
            ApaIssuePhase phase,
            string message,
            string partId = "",
            int sourceIndex = -1,
            int secondaryIndex = -1,
            string detail = "")
        {
            return new ValidationIssue(code, ApaSeverity.Warning, phase, message, partId, sourceIndex, secondaryIndex, detail);
        }

        /// <summary>Creates an informational diagnostic.</summary>
        public static ValidationIssue Info(
            string code,
            ApaIssuePhase phase,
            string message,
            string partId = "",
            int sourceIndex = -1,
            int secondaryIndex = -1,
            string detail = "")
        {
            return new ValidationIssue(code, ApaSeverity.Info, phase, message, partId, sourceIndex, secondaryIndex, detail);
        }

        /// <summary>True when this issue blocks preview and build.</summary>
        public bool IsBlocking => Severity == ApaSeverity.Error;

        /// <inheritdoc />
        public int CompareTo(ValidationIssue other)
        {
            if (ReferenceEquals(other, null)) return -1;

            var c = Phase.CompareTo(other.Phase);
            if (c != 0) return c;

            c = string.CompareOrdinal(Code, other.Code);
            if (c != 0) return c;

            c = string.CompareOrdinal(PartId, other.PartId);
            if (c != 0) return c;

            c = SourceIndex.CompareTo(other.SourceIndex);
            if (c != 0) return c;

            c = SecondaryIndex.CompareTo(other.SecondaryIndex);
            if (c != 0) return c;

            c = Severity.CompareTo(other.Severity);
            if (c != 0) return c;

            c = string.CompareOrdinal(Detail, other.Detail);
            if (c != 0) return c;

            return string.CompareOrdinal(Message, other.Message);
        }

        /// <inheritdoc />
        public bool Equals(ValidationIssue other)
        {
            if (ReferenceEquals(other, null)) return false;
            if (ReferenceEquals(this, other)) return true;

            return Phase == other.Phase
                   && string.Equals(Code, other.Code, StringComparison.Ordinal)
                   && string.Equals(PartId, other.PartId, StringComparison.Ordinal)
                   && SourceIndex == other.SourceIndex
                   && SecondaryIndex == other.SecondaryIndex
                   && Severity == other.Severity
                   && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
                   && string.Equals(Message, other.Message, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj) => Equals(obj as ValidationIssue);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Phase;
                hash = (hash * 397) ^ (Code != null ? Code.GetHashCode() : 0);
                hash = (hash * 397) ^ (PartId != null ? PartId.GetHashCode() : 0);
                hash = (hash * 397) ^ SourceIndex;
                hash = (hash * 397) ^ SecondaryIndex;
                hash = (hash * 397) ^ (int)Severity;
                hash = (hash * 397) ^ (Detail != null ? Detail.GetHashCode() : 0);
                hash = (hash * 397) ^ (Message != null ? Message.GetHashCode() : 0);
                return hash;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var title = ApaErrorCode.GetTitle(Code);
            var label = string.IsNullOrEmpty(title) ? Code : Code + " " + title;
            var where = SourceIndex >= 0 ? " [" + SourceIndex + "]" : string.Empty;
            var who = string.IsNullOrEmpty(PartId) ? string.Empty : " (" + PartId + ")";
            var extra = string.IsNullOrEmpty(Detail) ? string.Empty : " :: " + Detail;
            return Severity + " " + label + where + who + ": " + Message + extra;
        }
    }
}

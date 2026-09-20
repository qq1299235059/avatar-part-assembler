using System.Collections.Generic;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Everything the validation rules read. Immutable for the duration of a validation run.
    /// </summary>
    /// <remarks>
    /// A single context is passed to every rule so that rules cannot accumulate hidden state and so that a test
    /// can construct an exact scenario by hand.
    /// </remarks>
    public sealed class ValidationContext
    {
        /// <summary>The body being modified.</summary>
        public BaseSnapshot Base { get; }

        /// <summary>
        /// The parts to install, in <see cref="PartOrderingKey"/> order. Callers must sort before constructing
        /// the context; the planner exposes a helper that does so.
        /// </summary>
        public IReadOnlyList<PartSnapshot> Parts { get; }

        /// <summary>Numeric tolerances.</summary>
        public ApaNumericPolicy NumericPolicy { get; }

        /// <summary>
        /// Compatibility signature captured at authoring time, or null when none was recorded.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the single-representative-signature contract the M2 entry points and hand-built test contexts
        /// use: one signature, compared once when <see cref="CompatibilityVerified"/> is false.
        /// </para>
        /// <para>
        /// A context produced by <see cref="ContextBuilder"/> carries <c>null</c> here and
        /// <see cref="CompatibilityVerified"/> true instead, because each installer's own captured signature was
        /// already compared against this base once per installer. There is no "first installer wins"
        /// representative any more: it was arbitrary when a group's installers carried different signatures, and
        /// it made the compatibility rule run a second time over data that had already been checked.
        /// </para>
        /// </remarks>
        public ApaAvatarCompatibilityProfile ExpectedCompatibility { get; }

        /// <summary>
        /// True when every installer that contributed to this context was already checked against this base with
        /// its own captured signature.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Set by <see cref="ContextBuilder"/>, which is the only place that knows which installer owns which
        /// profile. When it is true, <c>CompatibilityRule</c> does not run: the check has already happened, once
        /// per installer, anchored on that installer's part id, and running it again through one representative
        /// signature would report the same condition a second time with a different anchor.
        /// </para>
        /// <para>
        /// The check is not skipped for a context that builds its own signature (<see cref="ExpectedCompatibility"/>
        /// is non-null), so every M2 caller and every hand-built test context keeps its exact behaviour.
        /// </para>
        /// </remarks>
        public bool CompatibilityVerified { get; }

        /// <summary>
        /// True when the NDMF Generating pass already compared every part mesh with its profile before Modular
        /// Avatar rewrote skinning data during armature merge. The later post-merge context must not compare the
        /// intentionally rewritten temporary mesh with the pre-merge authoring fingerprint a second time.
        /// </summary>
        public bool PartMeshFingerprintsVerifiedBeforeMerge { get; }

        /// <summary>
        /// The target renderer group this context belongs to: the resolved target renderer's avatar-root-relative
        /// path, or an empty string for a single-target context built by the legacy entry points.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One avatar may contain several target renderer groups, and each group is planned and validated on its
        /// own context, because parts welded to different bodies do not share a body, a slot namespace, a removal
        /// namespace, a UV layout, a material layout, or a bone table.
        /// </para>
        /// <para>
        /// The key is a hierarchy path, so it is unique by construction. It is echoed into the generated mesh
        /// name so two groups can never produce identically named meshes, and into group diagnostics so a report
        /// that spans several groups stays readable.
        /// </para>
        /// </remarks>
        public string GroupKey { get; }

        /// <summary>True when this context belongs to an explicitly keyed target group.</summary>
        public bool HasGroupKey => !string.IsNullOrEmpty(GroupKey);

        /// <summary>Creates a context.</summary>
        /// <param name="groupKey">
        /// Target group key, or null/empty for a single-target context. Optional so that every schema-2 call site
        /// keeps compiling and keeps its exact behaviour.
        /// </param>
        /// <param name="compatibilityVerified">
        /// True when the caller has already compared every contributing installer's own captured signature
        /// against this base. Optional and defaulting to false, so every schema-2 call site keeps its exact
        /// behaviour.
        /// </param>
        /// <param name="partMeshFingerprintsVerifiedBeforeMerge">
        /// True when an earlier NDMF Generating pass validated source part meshes before armature merging. Optional
        /// and defaulting to false for preview and direct core callers.
        /// </param>
        public ValidationContext(
            BaseSnapshot baseSnapshot,
            IReadOnlyList<PartSnapshot> parts,
            ApaNumericPolicy numericPolicy,
            ApaAvatarCompatibilityProfile expectedCompatibility,
            string groupKey = null,
            bool compatibilityVerified = false,
            bool partMeshFingerprintsVerifiedBeforeMerge = false)
        {
            Base = baseSnapshot;
            var partCopy = new PartSnapshot[parts?.Count ?? 0];
            for (var i = 0; i < partCopy.Length; i++) partCopy[i] = parts[i];
            Parts = System.Array.AsReadOnly(partCopy);
            NumericPolicy = numericPolicy ?? ApaNumericPolicy.Default;
            ExpectedCompatibility = CopySignature(expectedCompatibility);
            GroupKey = groupKey ?? string.Empty;
            CompatibilityVerified = compatibilityVerified;
            PartMeshFingerprintsVerifiedBeforeMerge = partMeshFingerprintsVerifiedBeforeMerge;
        }

        private static ApaAvatarCompatibilityProfile CopySignature(ApaAvatarCompatibilityProfile source)
        {
            if (source == null) return null;
            return new ApaAvatarCompatibilityProfile
            {
                MeshName = source.MeshName,
                RendererPath = source.RendererPath,
                MeshGuid = source.MeshGuid,
                MeshFingerprint = source.MeshFingerprint,
                VertexCount = source.VertexCount,
                SubMeshIndexCounts = Copy(source.SubMeshIndexCounts),
                SubMeshTopologyValues = Copy(source.SubMeshTopologyValues),
                BlendShapeNames = Copy(source.BlendShapeNames),
                BlendShapeFrameCounts = Copy(source.BlendShapeFrameCounts),
                BonePaths = Copy(source.BonePaths),
                IsCaptured = source.IsCaptured
            };
        }

        private static T[] Copy<T>(T[] source)
        {
            if (source == null || source.Length == 0) return System.Array.Empty<T>();
            var copy = new T[source.Length];
            System.Array.Copy(source, copy, source.Length);
            return copy;
        }

        /// <summary>
        /// Creates a copy of this context with a different base snapshot. Used by tests and by callers that
        /// resolve the base lazily.
        /// </summary>
        public ValidationContext WithBase(BaseSnapshot baseSnapshot)
        {
            return new ValidationContext(
                baseSnapshot, Parts, NumericPolicy, ExpectedCompatibility, GroupKey, CompatibilityVerified,
                PartMeshFingerprintsVerifiedBeforeMerge);
        }

        /// <summary>Creates a copy of this context with a different part list.</summary>
        public ValidationContext WithParts(IReadOnlyList<PartSnapshot> parts)
        {
            return new ValidationContext(
                Base, parts, NumericPolicy, ExpectedCompatibility, GroupKey, CompatibilityVerified,
                PartMeshFingerprintsVerifiedBeforeMerge);
        }

        /// <summary>Creates a copy of this context with a different target group key.</summary>
        public ValidationContext WithGroupKey(string groupKey)
        {
            return new ValidationContext(
                Base, Parts, NumericPolicy, ExpectedCompatibility, groupKey, CompatibilityVerified,
                PartMeshFingerprintsVerifiedBeforeMerge);
        }

        /// <summary>
        /// Finds a part by its stable id. Returns null when no part matches, which callers treat as an internal
        /// error rather than as a license to skip the vertex.
        /// </summary>
        public PartSnapshot FindPart(string partId)
        {
            var key = partId ?? string.Empty;
            for (var i = 0; i < Parts.Count; i++)
            {
                if (string.Equals(Parts[i].PartId, key, System.StringComparison.Ordinal)) return Parts[i];
            }

            return null;
        }

        /// <summary>
        /// Sorts parts into the canonical processing order. Callers must use this rather than relying on the
        /// order a hierarchy scan happened to produce.
        /// </summary>
        public static List<PartSnapshot> SortParts(IEnumerable<PartSnapshot> parts)
        {
            var list = new List<PartSnapshot>();
            if (parts != null)
            {
                foreach (var part in parts)
                {
                    if (part != null) list.Add(part);
                }
            }

            list.Sort(CompareParts);
            return list;
        }

        private static int CompareParts(PartSnapshot a, PartSnapshot b)
        {
            return a.OrderingKey.CompareTo(b.OrderingKey);
        }
    }

    /// <summary>
    /// A single, independently maintainable validation rule.
    /// </summary>
    /// <remarks>
    /// Rules append issues and never throw for bad input. An unexpected exception in a rule is caught by the
    /// validator and converted into an internal error diagnostic, because a crash during validation would
    /// otherwise look like a successful no-op.
    /// </remarks>
    public interface IApaValidationRule
    {
        /// <summary>Stable name used in diagnostics and in rule ordering documentation.</summary>
        string Name { get; }

        /// <summary>Appends any issues this rule finds.</summary>
        void Validate(ValidationContext context, List<ValidationIssue> issues);
    }
}

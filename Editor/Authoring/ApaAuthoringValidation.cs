using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The outcome of an authoring validation run.
    /// </summary>
    public sealed class ApaAuthoringValidationResult
    {
        /// <summary>The merged, ordered, deduplicated diagnostics.</summary>
        public ValidationResult Validation { get; }

        /// <summary>
        /// The context the run used, or null when no snapshot could be built. A non-null context lets a caller
        /// reuse the captured snapshots for a dry-run plan without capturing them twice.
        /// </summary>
        public ValidationContext Context { get; }

        /// <summary>True when a context was built.</summary>
        public bool HasContext => Context != null;

        /// <summary>True when nothing blocked.</summary>
        public bool IsValid => Validation != null && Validation.IsValid;

        /// <summary>Creates a result.</summary>
        public ApaAuthoringValidationResult(ValidationResult validation, ValidationContext context)
        {
            Validation = validation ?? ValidationResult.Empty;
            Context = context;
        }
    }

    /// <summary>
    /// The outcome of a dry-run plan: what the assembly would produce, or why it would not.
    /// </summary>
    public sealed class ApaAuthoringPlanResult
    {
        /// <summary>The planning result. <c>Plan</c> is null when planning failed.</summary>
        public PlanningResult Planning { get; }

        /// <summary>The merged diagnostics of capture, validation, and planning.</summary>
        public ValidationResult Validation { get; }

        /// <summary>True when a plan was produced.</summary>
        public bool Succeeded => Planning != null && Planning.Succeeded;

        /// <summary>Creates a result.</summary>
        public ApaAuthoringPlanResult(PlanningResult planning, ValidationResult validation)
        {
            Planning = planning;
            Validation = validation ?? ValidationResult.Empty;
        }

        /// <summary>A one-line description of the planned output, or the reason there is none.</summary>
        public string Describe()
        {
            if (!Succeeded) return Localization.ApaLocalization.Tr("no plan");
            var plan = Planning.Plan;
            return Localization.ApaLocalization.TrFormat(
                "{0} output vertex(es), {1} triangle(s), {2} submesh(es), {3} UV channel(s), " +
                "{4} removed triangle(s)",
                plan.VertexCount,
                plan.TotalTriangleCount(),
                plan.SubMeshes.Count,
                plan.UvLayout.Channels.Count,
                plan.RemovedTriangleCount);
        }
    }

    /// <summary>
    /// Validates an authoring draft before it is written or used to generate a prefab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three sources of diagnostics are merged here, and none of them re-implements a rule:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Draft data checks</b> for conditions the assembly core never sees, because they exist only while a
    /// profile is being edited — a semantic name that is empty, a semantic declared twice by the same source, or
    /// a material reference that is a scene object.
    /// </description></item>
    /// <item><description>
    /// <b>A stored-signature check</b>, run only when no live mesh is available to compare against. When a target
    /// mesh is selected, <see cref="CompatibilityRule"/> is the single authority and is reached through
    /// <c>ApaCore.Validate</c>.
    /// </description></item>
    /// <item><description>
    /// <b>The assembly core itself</b>, through <see cref="ApaAuthoringContextBuilder"/> and <c>ApaCore</c>. Every
    /// geometry, seam, UV, material, bone, and blend shape rule lives there and only there.
    /// </description></item>
    /// </list>
    /// <para>
    /// Messages for conditions the core also reports are written to match it exactly, so
    /// <see cref="ValidationResult.Build"/> collapses the two into one line instead of showing the author the
    /// same defect twice in different words.
    /// </para>
    /// </remarks>
    public static class ApaAuthoringValidation
    {
        /// <summary>
        /// Validates the draft against the selection without producing a mesh.
        /// </summary>
        public static ApaAuthoringValidationResult Validate(ApaAuthoringSelection selection, ApaProfileDraft draft)
        {
            if (draft == null)
            {
                return new ApaAuthoringValidationResult(
                    ValidationResult.Single(ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Configuration,
                        "No profile draft was supplied.",
                        detail: "reason=null-draft")),
                    null);
            }

            draft.EnsureInitialized();

            var issues = new List<ValidationIssue>();
            issues.AddRange(ValidateDraftData(draft, CapturePartMesh(selection)));

            return WithProfile(draft, profile =>
            {
                var built = ApaAuthoringContextBuilder.Build(selection, profile);

                if (!built.Succeeded)
                {
                    issues.AddRange(built.Issues.Issues);
                    // Without a live target there is nothing to compare the stored signature against, so the
                    // signature's own completeness is the only compatibility statement that can be made.
                    issues.AddRange(ApaCompatibilityCapture.ValidateCapturedProfile(
                        draft.Compatibility,
                        selection != null ? selection.TargetMesh : null));

                    return new ApaAuthoringValidationResult(ValidationResult.Build(issues), null);
                }

                var validation = ApaCore.Validate(built.Context);
                issues.AddRange(built.Issues.Issues);
                issues.AddRange(validation.Issues);
                return new ApaAuthoringValidationResult(ValidationResult.Build(issues), built.Context);
            });
        }

        /// <summary>
        /// Plans the assembly without building a mesh. This is the strongest pre-save check available: it proves
        /// the profile can be assembled, not merely that it satisfies the individual rules.
        /// </summary>
        public static ApaAuthoringPlanResult DryRun(ApaAuthoringSelection selection, ApaProfileDraft draft)
        {
            if (draft == null)
            {
                var issue = ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "No profile draft was supplied.",
                    detail: "reason=null-draft");
                return new ApaAuthoringPlanResult(
                    PlanningResult.Failure(ValidationResult.Single(issue)),
                    ValidationResult.Single(issue));
            }

            draft.EnsureInitialized();

            var issues = new List<ValidationIssue>();
            issues.AddRange(ValidateDraftData(draft, CapturePartMesh(selection)));

            return WithProfile(draft, profile =>
            {
                var built = ApaAuthoringContextBuilder.Build(selection, profile);
                issues.AddRange(built.Issues.Issues);

                if (!built.Succeeded)
                {
                    var failure = ValidationResult.Build(issues);
                    return new ApaAuthoringPlanResult(PlanningResult.Failure(failure), failure);
                }

                var planning = ApaCore.Plan(built.Context);
                issues.AddRange(planning.Issues.Issues);
                var merged = ValidationResult.Build(issues);
                return new ApaAuthoringPlanResult(planning, merged);
            });
        }

        /// <summary>
        /// Checks the parts of a draft that no assembly rule can see: semantic names, duplicate declarations, and
        /// references that cannot be stored in a reusable asset.
        /// </summary>
        /// <param name="draft">The draft to check.</param>
        /// <param name="partMesh">
        /// A snapshot of the selected part mesh, or null when there is none. It is used to notice a declared
        /// source submesh the mesh does not have, and a declared UV channel the mesh does not carry.
        /// </param>
        public static List<ValidationIssue> ValidateDraftData(ApaProfileDraft draft, MeshSnapshot partMesh)
        {
            var issues = new List<ValidationIssue>();
            if (draft == null) return issues;

            draft.EnsureInitialized();

            ValidateUvSemantics(draft, partMesh != null ? (Func<int, bool>)partMesh.HasUvChannel : null, issues);
            ValidateMaterialSemantics(draft, partMesh != null ? partMesh.SubMeshCount : -1, partMesh != null, issues);
            issues.AddRange(FindNonPersistentReferences(draft));

            return issues;
        }

        /// <summary>
        /// The same draft-level checks, run against a live part mesh instead of a captured snapshot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This overload exists for the authoring window's section-local issue lists, which are redrawn whenever
        /// something changes: capturing a full snapshot per redraw would copy every vertex, UV channel, bone
        /// weight, and blend shape frame on every keystroke. The rules are not re-implemented — the UV and
        /// material checks share one implementation, and the UV channel probe follows the same "present" rule as
        /// <see cref="MeshSnapshot.HasUvChannel"/>: a channel counts as present only when it has one entry per
        /// vertex of a non-empty mesh.
        /// </para>
        /// <para>
        /// The authoritative pre-write check remains <see cref="Validate"/>, which captures the snapshot and runs
        /// the core as well; this overload never decides whether an asset may be written.
        /// </para>
        /// </remarks>
        public static List<ValidationIssue> ValidateDraftDataAgainstMesh(ApaProfileDraft draft, Mesh partMesh)
        {
            var issues = new List<ValidationIssue>();
            if (draft == null) return issues;

            draft.EnsureInitialized();

            var readable = partMesh != null && partMesh.isReadable;
            ValidateUvSemantics(
                draft,
                readable ? (Func<int, bool>)(channel => MeshHasUvChannel(partMesh, channel)) : null,
                issues);
            ValidateMaterialSemantics(
                draft,
                readable ? Mathf.Max(partMesh.subMeshCount, 0) : -1,
                readable,
                issues);
            issues.AddRange(FindNonPersistentReferences(draft));

            return issues;
        }

        /// <summary>
        /// True when a live mesh carries a UV channel, by the same rule <see cref="MeshSnapshot.HasUvChannel"/>
        /// applies to a captured snapshot.
        /// </summary>
        /// <remarks>
        /// The vertex-attribute check is a cheap pre-filter: a channel the mesh's layout does not declare cannot
        /// be present, and <c>GetUVs</c> is only called for a channel that might be. A channel index outside
        /// <c>0..7</c> throws on some Unity versions and returns empty on others; both are "absent" here, exactly
        /// as <see cref="MeshSnapshotFactory"/> treats them.
        /// </remarks>
        public static bool MeshHasUvChannel(Mesh mesh, int channel)
        {
            if (mesh == null || !mesh.isReadable) return false;
            if (channel < 0 || channel >= ApaMeshLimits.MaxUvChannels) return false;
            if (mesh.vertexCount <= 0) return false;

            var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (!mesh.HasVertexAttribute(attribute)) return false;

            var values = new List<Vector4>();
            try
            {
                mesh.GetUVs(channel, values);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return values.Count == mesh.vertexCount;
        }

        /// <summary>
        /// Reports every material reference in the draft that is not a persisted asset.
        /// </summary>
        /// <remarks>
        /// The profile is a reusable asset: it travels with the part prefab to other projects and other users. A
        /// reference to a material that exists only in the authoring scene cannot be serialized into it, so
        /// writing the profile would either drop the reference or store a broken one. Blocking the write is the
        /// only outcome that keeps the promise that the profile is self-describing.
        /// </remarks>
        public static List<ValidationIssue> FindNonPersistentReferences(ApaProfileDraft draft)
        {
            var issues = new List<ValidationIssue>();
            if (draft == null) return issues;

            var semantics = draft.MaterialSemantics;
            for (var i = 0; i < semantics.Length; i++)
            {
                var entry = semantics[i];
                var material = entry != null ? entry.Material : null;
                if (material == null) continue;
                if (UnityEditor.EditorUtility.IsPersistent(material)) continue;

                var semantic = ApaSemanticName.Normalize(entry.Semantic);
                issues.Add(ValidationIssue.Error(
                    ApaAuthoringErrorCode.NonPersistentReference,
                    ApaIssuePhase.Materials,
                    "The material '" + material.name + "' assigned to semantic '" + (semantic ?? string.Empty) +
                    "' is a scene object, not a project asset. The profile is a reusable asset, so it cannot " +
                    "store a reference that only exists in this scene. Assign a project material or clear the " +
                    "reference before saving.",
                    detail: "semantic=" + (semantic ?? string.Empty) + "; submesh=" + entry.SourceSubMesh +
                            "; material=" + material.name + "; reason=non-persistent-material-reference"));
            }

            return issues;
        }

        private static void ValidateUvSemantics(
            ApaProfileDraft draft,
            Func<int, bool> channelIsPresent,
            List<ValidationIssue> issues)
        {
            var semantics = draft.UvSemantics;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < semantics.Length; i++)
            {
                var entry = semantics[i];
                if (entry == null) continue;

                var normalized = ApaSemanticName.Normalize(entry.Semantic);
                if (!ApaSemanticName.IsValid(normalized))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSemanticName,
                        ApaIssuePhase.Uv,
                        "A UV semantic name is null, empty, or whitespace. " + ApaSemanticName.RuleDescription + ".",
                        sourceIndex: entry.SourceChannel,
                        detail: "semantic='" + (entry.Semantic ?? string.Empty) + "'"));
                    continue;
                }

                if (entry.SourceChannel < 0 || entry.SourceChannel >= ApaMeshLimits.MaxUvChannels)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSemanticName,
                        ApaIssuePhase.Uv,
                        "UV semantic '" + normalized + "' declares source channel " + entry.SourceChannel +
                        ", which is outside 0 through " + (ApaMeshLimits.MaxUvChannels - 1) + ".",
                        sourceIndex: entry.SourceChannel,
                        detail: "semantic=" + normalized + "; channel=" + entry.SourceChannel));
                    continue;
                }

                // The assembler writes a declared channel only when the source mesh carries it; otherwise every
                // vertex silently receives the channel default, which is the "silently ignore author data"
                // outcome the specification forbids. Nothing else in the pipeline reports it: the core only
                // range-checks the declared channel, so the authoring layer is the last guard before the save.
                if (channelIsPresent != null && !channelIsPresent(entry.SourceChannel))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaAuthoringErrorCode.UvSemanticChannelAbsent,
                        ApaIssuePhase.Uv,
                        "UV semantic '" + normalized + "' declares source channel " + entry.SourceChannel +
                        ", but the part mesh has no channel " + entry.SourceChannel + " (present: " +
                        DescribePresentUvChannels(channelIsPresent) + "). Those vertices would receive the " +
                        "channel default.",
                        sourceIndex: entry.SourceChannel,
                        detail: "semantic=" + normalized + "; channel=" + entry.SourceChannel +
                                "; reason=channel-not-present"));
                    continue;
                }

                if (!seen.Add(normalized))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.DuplicateSemantic,
                        ApaIssuePhase.Uv,
                        "UV semantic '" + normalized + "' is declared more than once by the same source.",
                        sourceIndex: entry.SourceChannel,
                        detail: "semantic=" + normalized + "; channel=" + entry.SourceChannel));
                }
            }
        }

        /// <summary>
        /// The channels a mesh does carry, in ascending order, for the message of
        /// <see cref="ApaAuthoringErrorCode.UvSemanticChannelAbsent"/>. Computed only when that message is
        /// actually built.
        /// </summary>
        private static string DescribePresentUvChannels(Func<int, bool> channelIsPresent)
        {
            var present = new List<int>();
            for (var channel = 0; channel < ApaMeshLimits.MaxUvChannels; channel++)
            {
                if (channelIsPresent(channel)) present.Add(channel);
            }

            return present.Count == 0 ? "none" : string.Join(", ", present.ToArray());
        }

        private static void ValidateMaterialSemantics(
            ApaProfileDraft draft,
            int partSubMeshCount,
            bool hasPartMesh,
            List<ValidationIssue> issues)
        {
            var semantics = draft.MaterialSemantics;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < semantics.Length; i++)
            {
                var entry = semantics[i];
                if (entry == null) continue;

                var normalized = ApaSemanticName.Normalize(entry.Semantic);
                if (!ApaSemanticName.IsValid(normalized))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSemanticName,
                        ApaIssuePhase.Materials,
                        "A material semantic name is null, empty, or whitespace. " + ApaSemanticName.RuleDescription + ".",
                        sourceIndex: entry.SourceSubMesh,
                        detail: "semantic='" + (entry.Semantic ?? string.Empty) + "'"));
                    continue;
                }

                if (!seen.Add(normalized))
                {
                    // The assembly core does not currently reject a duplicate material semantic within one source,
                    // so this check is the only thing standing between the author and two slots for one semantic.
                    // The code and wording follow the UV rule, which does reject it.
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.DuplicateSemantic,
                        ApaIssuePhase.Materials,
                        "Material semantic '" + normalized + "' is declared more than once by the same source.",
                        sourceIndex: entry.SourceSubMesh,
                        detail: "semantic=" + normalized + "; submesh=" + entry.SourceSubMesh));
                }

                if (!hasPartMesh) continue;
                if (entry.SourceSubMesh >= 0 && entry.SourceSubMesh < partSubMeshCount) continue;

                // The condition is "this submesh ends up with no final material slot", which is what the core
                // reports as APA038 SUBMESH_WITHOUT_MATERIAL_SLOT. It is the same condition here, deliberately
                // under the same code, so the author connects the pre-check to the build error; reusing APA009
                // (MATERIAL_SEMANTIC_CONFLICT, "two material assets claim one semantic") would give one code two
                // meanings, which the allocation contract forbids. The severity is the only difference: the
                // pre-check is a Warning because the authoring layer has no final layout in hand, while the core
                // blocks the build with this code once it does. The pre-write guard closes the gap by requiring a
                // successful plan before an asset is written (see ApaAuthoringWindow.PassesPreWriteValidation).
                issues.Add(ValidationIssue.Warning(
                    ApaErrorCode.SubMeshWithoutMaterialSlot,
                    ApaIssuePhase.Materials,
                    "Material semantic '" + normalized + "' declares source submesh " + entry.SourceSubMesh +
                    ", but the part mesh has " + partSubMeshCount + " submesh(es). The submesh will have no " +
                    "final material slot, which blocks the build with APA038 once the final layout is resolved.",
                    sourceIndex: entry.SourceSubMesh,
                    detail: "semantic=" + normalized + "; submesh=" + entry.SourceSubMesh +
                            "; subMeshCount=" + partSubMeshCount +
                            "; reason=declared-submesh-out-of-range"));
            }
        }

        /// <summary>
        /// Captures a part mesh snapshot for the draft-level checks, or returns null. A mesh that cannot be read
        /// is reported by the selection check, so this returns null rather than adding a second diagnostic.
        /// </summary>
        private static MeshSnapshot CapturePartMesh(ApaAuthoringSelection selection)
        {
            if (selection == null) return null;

            var mesh = selection.PartMesh;
            if (mesh == null || !mesh.isReadable) return null;

            return MeshSnapshotFactory.Capture(mesh, null, out _);
        }

        private static T WithProfile<T>(ApaProfileDraft draft, Func<ApaPartProfile, T> body)
        {
            var profile = draft.Materialize();
            try
            {
                return body(profile);
            }
            finally
            {
                // The transient profile is the only object this method creates, and it must never outlive the
                // call: a leaked instance would show up in the author's project as a stray asset-like object.
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Runs every validation rule and produces one deterministic result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation is not an optional helper; the specification treats it as the module that keeps invalid
    /// assets out of the build. The validator runs rules in a fixed order, catches exceptions per rule so that
    /// one broken rule cannot suppress the others, and always returns a result rather than throwing.
    /// </para>
    /// <para>
    /// A rule that throws produces an internal error diagnostic. Reporting the failure is important: a
    /// silently skipped rule would look like a clean validation pass.
    /// </para>
    /// </remarks>
    public sealed class AvatarPartValidator
    {
        private readonly List<IApaValidationRule> _rules;

        /// <summary>Creates a validator with the default rule set.</summary>
        public AvatarPartValidator()
        {
            _rules = new List<IApaValidationRule>
            {
                new ConfigurationRule(),
                new CompatibilityRule(),
                new UnsupportedAttributeRule(),
                new RemovalRule(),
                new PartSlotRule(),
                new SeamRule(),
                new UvRule(),
                new MaterialRule(),
                new SkinningRule(),
                new BlendShapeRule(),
                new PartPolicyRule()
            };
        }

        /// <summary>Creates a validator with an explicit rule set, for tests and for future extension.</summary>
        public AvatarPartValidator(IEnumerable<IApaValidationRule> rules)
        {
            _rules = rules != null ? new List<IApaValidationRule>(rules) : new List<IApaValidationRule>();
        }

        /// <summary>The rules this validator runs, in order.</summary>
        public IReadOnlyList<IApaValidationRule> Rules => _rules;

        /// <summary>Runs every rule and returns the merged, deterministically ordered result.</summary>
        public ValidationResult Validate(ValidationContext context)
        {
            if (context == null)
            {
                return ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "Validation was invoked without a context.",
                    detail: "reason=null-context"));
            }

            var issues = new List<ValidationIssue>();

            var policyMessage = string.Empty;
            if (context.NumericPolicy == null || !context.NumericPolicy.TryValidate(out policyMessage))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InvalidEpsilon,
                    ApaIssuePhase.Configuration,
                    string.IsNullOrEmpty(policyMessage)
                        ? "The numeric policy is not configured."
                        : policyMessage,
                    detail: "reason=invalid-numeric-policy"));
            }

            for (var i = 0; i < _rules.Count; i++)
            {
                var rule = _rules[i];
                if (rule == null) continue;

                try
                {
                    rule.Validate(context, issues);
                }
                catch (Exception e)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Configuration,
                        "Validation rule '" + rule.Name + "' threw " + e.GetType().Name + ": " + e.Message,
                        detail: "rule=" + rule.Name + "; exception=" + e.GetType().FullName));
                }
            }

            return ValidationResult.Build(issues);
        }
    }

    /// <summary>
    /// Checks that the context itself is usable: a base mesh exists, and every declared part identity is
    /// present and unique where uniqueness is required.
    /// </summary>
    public sealed class ConfigurationRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "ConfigurationRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context.Base == null || context.Base.Mesh == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target body renderer or its mesh could not be resolved.",
                    detail: "reason=missing-base-mesh"));
            }

            if (context.Parts.Count == 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.PartProfileIncompatible,
                    ApaIssuePhase.Configuration,
                    "No parts were resolved. An installer without a usable profile has nothing to assemble.",
                    detail: "reason=no-parts"));
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.PartProfileIncompatible,
                        ApaIssuePhase.Configuration,
                        "A part slot in the plan is null.",
                        detail: "reason=null-part; index=" + i));
                    continue;
                }

                if (string.IsNullOrEmpty(part.PartId))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.PartProfileIncompatible,
                        ApaIssuePhase.Configuration,
                        "A part has no stable identifier, so its removal triangles and seam cannot be ordered " +
                        "deterministically. Assign a profile with a part id.",
                        detail: "reason=missing-part-id; index=" + i));
                    continue;
                }

                if (!seenIds.Add(part.PartId))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.PartProfileIncompatible,
                        ApaIssuePhase.Configuration,
                        "Part id '" + part.PartId + "' is used by more than one installer. Part ids must be " +
                        "unique so that ordering and diagnostics are stable.",
                        part.PartId,
                        detail: "reason=duplicate-part-id; partId=" + part.PartId));
                }
            }
        }
    }

    /// <summary>
    /// Enforces the slot contract: at most one part may <i>replace</i> a non-Custom body slot, any number of
    /// parts may <i>augment</i> it, and an augmenting part may not declare a removal set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ApaPartSlot.Custom"/> is exempt because a Custom part is by definition outside the standard
    /// body list, and several such parts may legitimately coexist.
    /// </para>
    /// <para>
    /// The <see cref="ApaPartSlotMode.Replace"/> / <see cref="ApaPartSlotMode.Augment"/> split is the explicit,
    /// non-guessing answer for the real cases that used to force Custom (a hat, hair, a tail, an accessory on
    /// the head) while keeping the exclusive claim meaningful: exactly one part may replace a body region.
    /// </para>
    /// <para>
    /// An augmenting part does not own a body region, so it has no standing to remove one. It may still weld,
    /// merge UV and material semantics, contribute bones, and contribute blend shapes.
    /// </para>
    /// </remarks>
    public sealed class PartSlotRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "PartSlotRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            var replaceOwners = new Dictionary<ApaPartSlot, string>();

            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part == null) continue;

                // A serialized profile can carry an integer outside the defined enum range — for example when a
                // profile is written by a newer build and read by an older one, or when the asset is edited by
                // hand. Such a value would otherwise fall through to the Custom exemption below and be accepted
                // silently, which is exactly the kind of unchecked author data this validator exists to catch.
                if (!IsDefinedSlot(part.Slot))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidPartSlot,
                        ApaIssuePhase.Configuration,
                        "Part '" + part.PartId + "' declares part slot value " + (int)part.Slot +
                        ", which is not a defined slot. Re-author the part's profile.",
                        part.PartId,
                        (int)part.Slot,
                        detail: "slot=" + (int)part.Slot + "; reason=undefined-slot"));
                    continue;
                }

                if (!IsDefinedSlotMode(part.SlotMode))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidPartSlot,
                        ApaIssuePhase.Configuration,
                        "Part '" + part.PartId + "' declares slot mode value " + (int)part.SlotMode +
                        ", which is not a defined mode. Re-author the part's profile.",
                        part.PartId,
                        (int)part.SlotMode,
                        detail: "slotMode=" + (int)part.SlotMode + "; reason=undefined-slot-mode"));
                    continue;
                }

                // An augmenting part attaches to a region it does not own, so it may not remove body geometry.
                // The claim is checked first because it holds for Custom as well: Custom means "outside the
                // standard body list", not "may delete any body region it likes".
                if (part.SlotMode == ApaPartSlotMode.Augment
                    && part.RemovedTriangles != null
                    && part.RemovedTriangles.Count > 0)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.DuplicatePartSlot,
                        ApaIssuePhase.Configuration,
                        "Part '" + part.PartId + "' augments slot " + part.Slot + " but also declares " +
                        part.RemovedTriangles.Count + " removal triangle(s). A part that augments a region does " +
                        "not own it, so it cannot remove body geometry; use Replace if this part owns the " +
                        "region, or drop the removal selection.",
                        part.PartId,
                        (int)part.Slot,
                        part.RemovedTriangles.Count,
                        detail: "slot=" + part.Slot + "; slotMode=Augment; removals=" +
                                part.RemovedTriangles.Count + "; reason=augment-declares-removal"));
                    continue;
                }

                if (part.Slot == ApaPartSlot.Custom) continue;
                if (part.SlotMode == ApaPartSlotMode.Augment) continue;

                if (replaceOwners.TryGetValue(part.Slot, out var otherPartId))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.DuplicatePartSlot,
                        ApaIssuePhase.Configuration,
                        "Parts '" + otherPartId + "' and '" + part.PartId + "' both claim slot " + part.Slot +
                        " with mode Replace. Only one part may replace a standard body slot; mark the additional " +
                        "part Augment to attach it to the region, or use Custom for a part outside the body list.",
                        part.PartId,
                        (int)part.Slot,
                        detail: "slot=" + part.Slot + "; otherPart=" + otherPartId +
                                "; reason=duplicate-replace-slot"));
                    continue;
                }

                replaceOwners.Add(part.Slot, part.PartId);
            }
        }

        /// <summary>True when the value is one of the defined <see cref="ApaPartSlot"/> members.</summary>
        private static bool IsDefinedSlot(ApaPartSlot slot)
        {
            return slot >= ApaPartSlot.Head && slot <= ApaPartSlot.Custom;
        }

        /// <summary>True when the value is one of the defined <see cref="ApaPartSlotMode"/> members.</summary>
        private static bool IsDefinedSlotMode(ApaPartSlotMode mode)
        {
            return mode >= ApaPartSlotMode.Replace && mode <= ApaPartSlotMode.Augment;
        }
    }

    /// <summary>
    /// Enforces the per-part policy that narrows what the strict rules accept: currently
    /// <see cref="ApaBlendShapeProfile.AllowPartOnlyShapes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field was serialized before M6 but nothing read it, so declaring <c>false</c> silently did nothing.
    /// An inert policy field is worse than no field: it is a promise the product does not keep, and it becomes
    /// the precedent for the next policy that silently does nothing. This rule wires it.
    /// </para>
    /// <para>
    /// The refusal has its own code, <c>APA040 PART_ONLY_SHAPE_DISALLOWED</c>. It must not reuse
    /// <c>APA029</c>, whose registered meaning is the blend-shape <i>seam delta</i> condition: the two have
    /// different remedies, and a code's meaning must never change (see <see cref="ApaErrorCode"/>).
    /// </para>
    /// <para>
    /// A shape is "part-only" when the base body's mesh does not carry its name. Refusing it is a strictly
    /// stronger block than the strict rules already apply (a part-only shape with zero seam deltas was accepted),
    /// so the default <c>true</c> keeps every existing profile behaving exactly as it did.
    /// </para>
    /// <para>
    /// The rule reads names rather than the merged catalog because the decision is exactly "does the base carry
    /// this name", and reading it here means the verdict is available to <c>Validate</c> as well as to
    /// <c>Plan</c>, not only to the planner that owns the seam correspondence.
    /// </para>
    /// </remarks>
    public sealed class PartPolicyRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "PartPolicyRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context == null || context.Base == null || context.Base.Mesh == null) return;

            var baseShapes = context.Base.Mesh.Shapes;

            for (var p = 0; p < context.Parts.Count; p++)
            {
                var part = context.Parts[p];
                if (part?.Mesh == null) continue;
                if (part.AllowPartOnlyShapes) continue;

                var partShapes = part.Mesh.Shapes;
                for (var s = 0; s < partShapes.Count; s++)
                {
                    var name = partShapes[s];
                    if (HasShape(baseShapes, name)) continue;

                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.PartOnlyShapeDisallowed,
                        ApaIssuePhase.Attributes,
                        "Blend shape '" + name + "' exists only on part '" + part.PartId + "', and the part's " +
                        "profile sets AllowPartOnlyShapes to false. The strict rules would accept the shape when " +
                        "its seam deltas are zero; the policy refuses it outright. Enable the policy, or remove " +
                        "the shape from the part.",
                        part.PartId,
                        s,
                        -1,
                        detail: "reason=part-only-shape-disallowed; shape=" + name +
                                "; policy=allowPartOnlyShapes=false"));
                }
            }
        }

        /// <summary>Ordinal, normalized name lookup against the base body's shape list.</summary>
        private static bool HasShape(IReadOnlyList<string> shapes, string name)
        {
            var normalized = ApaSemanticName.Normalize(name);
            for (var i = 0; i < shapes.Count; i++)
            {
                if (string.Equals(ApaSemanticName.Normalize(shapes[i]), normalized, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The result of planning every target group of one avatar.
    /// </summary>
    /// <remarks>
    /// Planning is the phase that makes no mutation at all, so this result is safe to produce for a preview
    /// refresh. <see cref="Succeeded"/> is false when any group has a blocking issue; a failing group fails the
    /// whole avatar, because assembling the groups that happen to be valid would ship an avatar whose parts were
    /// only half installed.
    /// </remarks>
    public sealed class TargetGroupPlanResult
    {
        /// <summary>The group plans, in ordinal group-key order. Empty on failure or when there is nothing to do.</summary>
        public IReadOnlyList<ApaTargetGroupPlan> Groups { get; }

        /// <summary>The planning result of each group, parallel to <see cref="Groups"/>.</summary>
        public IReadOnlyList<PlanningResult> Plans { get; }

        /// <summary>Every diagnostic, in deterministic order, with the group key in the detail.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when every group produced a plan.</summary>
        public bool Succeeded { get; }

        /// <summary>True when the avatar has no active installer, which is "not applicable", not a failure.</summary>
        public bool NotApplicable { get; }

        /// <summary>
        /// The numeric policy these plans were validated and planned with. Never null.
        /// </summary>
        /// <remarks>
        /// A plan is bound to the policy it was produced with: seam matching, blend-shape epsilon comparisons and
        /// the bone table all read it, so building later under a different policy would silently mix two
        /// tolerance sets. <see cref="TargetGroupAssembly.BuildPlanned"/> reads this to honour a caller-supplied
        /// policy instead of ignoring it.
        /// </remarks>
        public ApaNumericPolicy Policy { get; }

        /// <summary>Number of target groups.</summary>
        public int GroupCount => Groups.Count;

        /// <summary>Creates a plan result.</summary>
        public TargetGroupPlanResult(
            IReadOnlyList<ApaTargetGroupPlan> groups,
            IReadOnlyList<PlanningResult> plans,
            ValidationResult issues,
            bool succeeded,
            bool notApplicable,
            ApaNumericPolicy policy = null)
        {
            Groups = Freeze(groups);
            Plans = Freeze(plans);
            Issues = issues ?? ValidationResult.Empty;
            Succeeded = succeeded;
            NotApplicable = notApplicable;
            Policy = policy ?? ApaNumericPolicy.Default;
        }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            var copy = new T[source != null ? source.Count : 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
        }
    }

    /// <summary>
    /// One group's generated mesh inside a <see cref="TargetGroupAssemblyResult"/>.
    /// </summary>
    public sealed class TargetGroupAssemblyEntry
    {
        /// <summary>The group this mesh belongs to.</summary>
        public ApaTargetGroupPlan Group { get; }

        /// <summary>The group's plan.</summary>
        public PlanningResult Planning { get; }

        /// <summary>The group's build result, or null when the group was never built.</summary>
        public AssemblyResult Assembly { get; }

        /// <summary>The generated mesh, or null when this group produced none.</summary>
        public Mesh Mesh => Assembly != null ? Assembly.Mesh : null;

        /// <summary>The group key, for diagnostics and for name-based lookup.</summary>
        public string GroupKey => Group != null ? Group.GroupKey : string.Empty;

        /// <summary>The renderer this group's mesh replaces.</summary>
        public Renderer TargetRenderer => Group != null ? Group.TargetRenderer : null;

        /// <summary>
        /// True once the caller has taken ownership of <see cref="Mesh"/> by assigning it to the build clone.
        /// An assigned mesh is never released by <see cref="TargetGroupAssemblyResult.ReleaseUnassigned"/>.
        /// </summary>
        public bool Assigned { get; private set; }

        /// <summary>Creates an entry.</summary>
        public TargetGroupAssemblyEntry(ApaTargetGroupPlan group, PlanningResult planning, AssemblyResult assembly)
        {
            Group = group;
            Planning = planning;
            Assembly = assembly;
        }

        /// <summary>
        /// Marks the mesh as assigned, which transfers responsibility for destroying it to the caller.
        /// </summary>
        /// <remarks>
        /// The assignment site is the only place that knows the mesh reached the clone; marking it here is what
        /// lets the orchestrator release everything that did not, which is the leak this API exists to prevent.
        /// </remarks>
        public void MarkAssigned()
        {
            Assigned = true;
        }
    }

    /// <summary>
    /// The outcome of assembling every target group of one avatar, and the owner of every mesh it generated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity does not collect unreferenced meshes, so a generated mesh that nobody destroys is a leak that
    /// survives every preview refresh until the editor restarts. This result makes the ownership explicit: a
    /// caller marks the groups whose meshes it assigned, and then calls
    /// <see cref="ReleaseUnassigned"/> (or disposes the result) to release everything else.
    /// </para>
    /// <para>
    /// Disposal is deliberately limited to unassigned meshes. Releasing an assigned mesh would destroy the mesh
    /// a renderer is displaying, so the caller must either assign it or leave it unassigned and let this type
    /// clean it up.
    /// </para>
    /// </remarks>
    public sealed class TargetGroupAssemblyResult : IDisposable
    {
        private readonly List<TargetGroupAssemblyEntry> _groups;
        private bool _released;

        /// <summary>The groups that were assembled, in ordinal group-key order. Empty on failure.</summary>
        public IReadOnlyList<TargetGroupAssemblyEntry> Groups => _groups.AsReadOnly();

        /// <summary>Every diagnostic, in deterministic order, with the group key in the detail.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when every group produced a mesh.</summary>
        public bool Succeeded { get; }

        /// <summary>True when the avatar has no active installer, which is "not applicable", not a failure.</summary>
        public bool NotApplicable { get; }

        /// <summary>Number of groups.</summary>
        public int GroupCount => _groups.Count;

        /// <summary>Number of generated meshes still owned by this result.</summary>
        public int OwnedMeshCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < _groups.Count; i++)
                {
                    var mesh = _groups[i].Mesh;
                    if (mesh != null && !_groups[i].Assigned) count++;
                }

                return count;
            }
        }

        /// <summary>Creates a result.</summary>
        public TargetGroupAssemblyResult(
            IReadOnlyList<TargetGroupAssemblyEntry> groups,
            ValidationResult issues,
            bool succeeded,
            bool notApplicable)
        {
            _groups = new List<TargetGroupAssemblyEntry>(groups != null ? groups.Count : 0);
            if (groups != null)
            {
                for (var i = 0; i < groups.Count; i++) _groups.Add(groups[i]);
            }

            Issues = issues ?? ValidationResult.Empty;
            Succeeded = succeeded;
            NotApplicable = notApplicable;
        }

        /// <summary>A failed result carrying only diagnostics.</summary>
        public static TargetGroupAssemblyResult Failure(ValidationResult issues, bool notApplicable = false)
        {
            return new TargetGroupAssemblyResult(null, issues, false, notApplicable);
        }

        /// <summary>
        /// Marks a group's mesh as assigned, transferring ownership of it to the caller.
        /// </summary>
        /// <remarks>
        /// Call this at the assignment site, immediately after writing the mesh to the build clone's renderer.
        /// Anything not marked is released by <see cref="ReleaseUnassigned"/>.
        /// </remarks>
        public void MarkAssigned(int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= _groups.Count) return;
            _groups[groupIndex].MarkAssigned();
        }

        /// <summary>Marks the group with the given key as assigned. Returns false when no group has that key.</summary>
        public bool MarkAssigned(string groupKey)
        {
            for (var i = 0; i < _groups.Count; i++)
            {
                if (!string.Equals(_groups[i].GroupKey, groupKey ?? string.Empty, StringComparison.Ordinal)) continue;
                _groups[i].MarkAssigned();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Destroys every generated mesh that was never assigned, and returns how many were released.
        /// </summary>
        /// <remarks>
        /// Idempotent: a second call releases nothing, because destroying a mesh nulls it on the entry.
        /// </remarks>
        public int ReleaseUnassigned()
        {
            if (_released) return 0;
            _released = true;

            var released = 0;
            for (var i = 0; i < _groups.Count; i++)
            {
                var entry = _groups[i];
                if (entry.Assigned) continue;
                if (entry.Assembly == null) continue;
                if (entry.Assembly.Mesh == null) continue;

                entry.Assembly.Destroy();
                released++;
            }

            return released;
        }

        /// <summary>Releases every unassigned mesh. See <see cref="ReleaseUnassigned"/>.</summary>
        public void Dispose()
        {
            ReleaseUnassigned();
        }
    }

    /// <summary>
    /// The target-group orchestrator: the single place that plans every group, validates every group, and only
    /// then builds one mesh per group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transaction shape.</b> Every group is resolved, captured, validated, and planned before any mesh is
    /// allocated. A blocking issue in any group fails the whole avatar and produces no mesh, so a partially
    /// assembled avatar cannot exist: either every group builds, or nothing is built and the report names the
    /// group that blocked.
    /// </para>
    /// <para>
    /// <b>One plan per group, one mesh per group.</b> A group's mesh is built once, from the plan that already
    /// contains every part of that group, and a group never sees another group's plan or output. N groups
    /// produce exactly N meshes on success.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> The returned <see cref="TargetGroupAssemblyResult"/> owns every generated mesh. The
    /// caller assigns the ones it wants and releases the rest, which is what makes a failed or partially
    /// consumed assembly leak-free without the core having to know when a renderer stops using a mesh.
    /// </para>
    /// </remarks>
    public static class TargetGroupAssembly
    {
        /// <summary>
        /// Resolves, captures, validates, and plans every target group without building or mutating anything.
        /// </summary>
        /// <param name="avatarRoot">The avatar root GameObject.</param>
        /// <param name="numericPolicy">Numeric tolerances, or null for the defaults.</param>
        /// <param name="issues">
        /// Receives every diagnostic of this pass: avatar-level discovery issues, each group's capture and
        /// compatibility issues (tagged with that group's key by <see cref="ContextBuilder"/>), and each group's
        /// planning issues (tagged here). The blocking test is
        /// <c>issues.Exists(issue =&gt; issue.IsBlocking)</c>, the same test the rest of the pipeline uses.
        /// </param>
        /// <remarks>
        /// <b>One issue stream.</b> A condition is reported exactly once. Group-scoped conditions already carry
        /// their group tag when this method receives them, and the planning pass appends its own diagnostics with
        /// the same tag, so an untagged copy of a group's problem is never seeded into the report alongside the
        /// tagged one.
        /// </remarks>
        public static TargetGroupPlanResult Plan(
            GameObject avatarRoot,
            ApaNumericPolicy numericPolicy,
            out List<ValidationIssue> issues,
            bool allowPostMergePartArmatureScope = false,
            bool partMeshFingerprintsVerifiedBeforeMerge = false)
        {
            var policy = numericPolicy ?? ApaNumericPolicy.Default;

            var groups = ContextBuilder.BuildGroups(
                avatarRoot,
                policy,
                out var discovery,
                allowPostMergePartArmatureScope,
                partMeshFingerprintsVerifiedBeforeMerge);
            discovery = discovery ?? new List<ValidationIssue>();

            if (groups.Count == 0)
            {
                // "No active installer" is not a failure. Everything else that produced no groups is.
                var notApplicable = !discovery.Exists(issue => issue.IsBlocking);
                issues = discovery;
                return new TargetGroupPlanResult(
                    null, null, ValidationResult.Build(discovery), false, notApplicable, policy);
            }

            var planner = new AssemblyPlanner();
            var plans = new List<PlanningResult>(groups.Count);
            var allIssues = new List<ValidationIssue>(discovery);

            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var planning = planner.Plan(group.Context);
                plans.Add(planning);

                AppendGroupIssues(allIssues, planning.Issues, group.GroupKey);
            }

            // Cross-group closure: a part root of one group must not contain another group's target renderer.
            // Such a configuration would make that group's consumption destroy the other group's target, so it
            // is refused here — before any mesh, proxy, or destruction exists — and by the same code path the
            // build and the preview both call. See ApaPartConsumptionPlanner.
            ApaPartConsumptionPlanner.CollectTargetsInsideForeignPartRoots(avatarRoot, groups, allIssues);

            issues = allIssues;
            var result = ValidationResult.Build(allIssues);
            var succeeded = !result.HasErrors && plans.Count == groups.Count;

            return new TargetGroupPlanResult(
                succeeded ? groups : null,
                succeeded ? plans : null,
                result,
                succeeded,
                false,
                policy);
        }

        /// <summary>
        /// Plans every group and then builds exactly one mesh per group.
        /// </summary>
        /// <remarks>
        /// See <see cref="Plan"/> for the transaction shape. When a build fails after other groups were built,
        /// every mesh already created is destroyed before the failure is returned, so a failed assembly never
        /// hands back a partial avatar.
        /// </remarks>
        public static TargetGroupAssemblyResult Assemble(
            GameObject avatarRoot,
            ApaNumericPolicy numericPolicy,
            out List<ValidationIssue> issues)
        {
            var policy = numericPolicy ?? ApaNumericPolicy.Default;
            var planned = Plan(avatarRoot, policy, out issues);

            if (!planned.Succeeded) return TargetGroupAssemblyResult.Failure(planned.Issues, planned.NotApplicable);

            return BuildPlannedGroups(planned, policy);
        }

        /// <summary>
        /// Builds one mesh per group from an already-planned result, without re-planning.
        /// </summary>
        /// <param name="planned">The plan result to build. Required.</param>
        /// <param name="numericPolicy">
        /// The numeric policy to build with, or null to use the policy the plans were produced with
        /// (<see cref="TargetGroupPlanResult.Policy"/>).
        /// </param>
        /// <remarks>
        /// <para>
        /// This is the entry point for a caller that wants to inspect the plans before deciding to build. The
        /// plans are immutable, so building from them here produces exactly what a direct assemble would.
        /// </para>
        /// <para>
        /// <b>The policy parameter is honoured, not ignored.</b> A plan is bound to the policy it was validated
        /// and planned with — the seam, blend-shape, and bone-table decisions it already contains were made with
        /// those tolerances — so a supplied policy may either agree with it or be refused. Passing a different
        /// policy is a contradiction the caller must resolve (re-plan with the policy it wants, or pass the one
        /// it planned with); silently ignoring it would be the inert-parameter defect, and silently building
        /// under it would mix two tolerance sets in one result.
        /// </para>
        /// </remarks>
        public static TargetGroupAssemblyResult BuildPlanned(
            TargetGroupPlanResult planned,
            ApaNumericPolicy numericPolicy)
        {
            if (planned == null)
            {
                return TargetGroupAssemblyResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "The target-group orchestration was invoked without a plan result.",
                    detail: "reason=null-plan-result")));
            }

            if (!planned.Succeeded) return TargetGroupAssemblyResult.Failure(planned.Issues, planned.NotApplicable);

            var policy = numericPolicy ?? planned.Policy ?? ApaNumericPolicy.Default;
            if (!SamePolicy(policy, planned.Policy))
            {
                return TargetGroupAssemblyResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "The numeric policy supplied to build the planned groups (" + DescribePolicy(policy) +
                    ") is not the policy the groups were planned with (" + DescribePolicy(planned.Policy) +
                    "). A plan is bound to the policy it was validated and planned with, because its seam, blend " +
                    "shape, and bone-table decisions were already made with those tolerances. Plan again with the " +
                    "policy you want, or build with the policy the plan carries.",
                    detail: "reason=numeric-policy-mismatch; supplied=" + DescribePolicy(policy) +
                            "; planned=" + DescribePolicy(planned.Policy))));
            }

            return BuildPlannedGroups(planned, policy);
        }

        /// <summary>
        /// True when two policies define the same tolerances. Null is only equal to null; callers resolve the
        /// effective policy before comparing.
        /// </summary>
        private static bool SamePolicy(ApaNumericPolicy a, ApaNumericPolicy b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            return a.PositionEpsilon == b.PositionEpsilon
                   && a.UvEpsilon == b.UvEpsilon
                   && a.DegenerateAreaEpsilon == b.DegenerateAreaEpsilon
                   && a.WeightEpsilon == b.WeightEpsilon;
        }

        private static string DescribePolicy(ApaNumericPolicy policy)
        {
            if (policy == null) return "(null)";

            return "position=" + policy.PositionEpsilon +
                   ", uv=" + policy.UvEpsilon +
                   ", degenerateArea=" + policy.DegenerateAreaEpsilon +
                   ", weight=" + policy.WeightEpsilon;
        }

        /// <summary>
        /// Builds one group's mesh from an already-validated plan.
        /// </summary>
        /// <param name="group">The group whose context the mesh is built against.</param>
        /// <param name="planning">The group's validated plan, as <see cref="Plan"/> produced it.</param>
        /// <param name="assembler">The assembler to use, or null for a fresh one.</param>
        /// <remarks>
        /// <para>
        /// <b>The one per-group build step.</b> The group transaction uses it for every group, and a caller that
        /// has to build exactly one group — the preview builds one proxy per target group — uses the same call
        /// rather than its own copy of "plan, then assemble". Two implementations of that step would be two
        /// chances for preview and build to disagree.
        /// </para>
        /// <para>
        /// The generated mesh is owned by the caller. A group that cannot be built returns an
        /// <see cref="AssemblyResult"/> with no mesh and a blocking diagnostic rather than throwing.
        /// </para>
        /// </remarks>
        public static AssemblyResult BuildGroup(
            ApaTargetGroupPlan group,
            PlanningResult planning,
            MeshAssembler assembler)
        {
            if (group == null || group.Context == null || planning == null || planning.Plan == null)
            {
                return AssemblyResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "A target group was asked to build without a context or a plan.",
                    detail: "reason=incomplete-group-plan")));
            }

            return (assembler ?? new MeshAssembler()).Build(planning.Plan, group.Context);
        }

        private static TargetGroupAssemblyResult BuildPlannedGroups(
            TargetGroupPlanResult planned,
            ApaNumericPolicy policy)
        {
            var assembler = new MeshAssembler();
            var entries = new List<TargetGroupAssemblyEntry>(planned.GroupCount);
            var allIssues = new List<ValidationIssue>(planned.Issues.Issues);

            for (var g = 0; g < planned.GroupCount; g++)
            {
                var group = planned.Groups[g];

                var assembly = BuildGroup(group, planned.Plans[g], assembler);
                AppendGroupIssues(allIssues, assembly.Issues, group.GroupKey);

                if (assembly.Mesh == null || assembly.Issues.HasErrors)
                {
                    // All-or-nothing: release everything built so far, including this group's mesh when the
                    // assembler produced one and then reported a failure.
                    if (assembly.Mesh != null) assembly.Destroy();
                    for (var i = 0; i < entries.Count; i++)
                    {
                        if (entries[i].Assembly != null) entries[i].Assembly.Destroy();
                    }

                    return TargetGroupAssemblyResult.Failure(ValidationResult.Build(allIssues));
                }

                entries.Add(new TargetGroupAssemblyEntry(group, planned.Plans[g], assembly));
            }

            return new TargetGroupAssemblyResult(
                entries,
                ValidationResult.Build(allIssues),
                entries.Count == planned.GroupCount,
                false);
        }

        /// <summary>
        /// Copies another result's issues into the running report, tagging each with its group key.
        /// </summary>
        /// <remarks>
        /// The group key is appended to the detail rather than replacing the existing reason token, so a test or
        /// a tool that keys on <c>reason=</c> keeps working, and a report that spans several groups is still
        /// readable. The tag is appended once per issue, so the deduplication in
        /// <see cref="ValidationResult.Build"/> cannot merge two different groups' diagnostics into one.
        /// </remarks>
        private static void AppendGroupIssues(
            List<ValidationIssue> destination,
            ValidationResult source,
            string groupKey)
        {
            if (source == null) return;

            for (var i = 0; i < source.Issues.Count; i++)
            {
                destination.Add(WithGroup(source.Issues[i], groupKey));
            }
        }

        private static ValidationIssue WithGroup(ValidationIssue issue, string groupKey)
        {
            if (issue == null) return null;
            if (string.IsNullOrEmpty(groupKey)) return issue;

            var tag = "group=" + groupKey;
            var detail = string.IsNullOrEmpty(issue.Detail) ? tag : issue.Detail + "; " + tag;

            return new ValidationIssue(
                issue.Code,
                issue.Severity,
                issue.Phase,
                issue.Message,
                issue.PartId,
                issue.SourceIndex,
                issue.SecondaryIndex,
                detail);
        }
    }
}

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// One build request: the avatar clone to assemble, and optionally the installers and registry to use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request is a value the caller can construct without NDMF. That is what lets the same processor serve
    /// the build passes and the M4 preview: preview supplies its own clone and its own registry, and gets the
    /// same plan decisions and the same generated mesh.
    /// </para>
    /// <para>
    /// <see cref="Installers"/> defaults to the enabled installers discovered under the avatar root, which is
    /// what a pass wants; supplying an explicit list is for a caller that has already discovered them (and wants
    /// the same list reported against).
    /// </para>
    /// </remarks>
    public sealed class ApaBuildRequest
    {
        /// <summary>The avatar root of the hierarchy to assemble. Must be a transient clone.</summary>
        public GameObject AvatarRoot { get; }

        /// <summary>The installers to process, in canonical order, or null to discover them.</summary>
        public IReadOnlyList<AvatarPartInstaller> Installers { get; }

        /// <summary>Numeric tolerances, or null for <see cref="ApaNumericPolicy.Default"/>.</summary>
        public ApaNumericPolicy NumericPolicy { get; }

        /// <summary>
        /// The registry that records what a generated object replaced, or null. NDMF's build context provides
        /// one; a caller outside a build may pass null and lose only the "this mesh came from that mesh" link in
        /// error reports.
        /// </summary>
        public IObjectRegistry ObjectRegistry { get; }

        /// <summary>
        /// True only when the caller has already verified that Modular Avatar consumed and applied APA's
        /// transient merge-armature plans. In that state a part renderer may legitimately be skinned entirely to
        /// the target armature even though its original part armature no longer exists below the part root.
        /// </summary>
        public bool AllowPostMergePartArmatureScope { get; }

        /// <summary>
        /// True only for the NDMF path whose Generating pass validated each part mesh before Modular Avatar
        /// rewrote its skinning data. The post-merge context then skips a duplicate part-fingerprint comparison.
        /// </summary>
        public bool PartMeshFingerprintsVerifiedBeforeMerge { get; }

        /// <summary>Creates a request.</summary>
        public ApaBuildRequest(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> installers = null,
            ApaNumericPolicy numericPolicy = null,
            IObjectRegistry objectRegistry = null,
            bool allowPostMergePartArmatureScope = false,
            bool partMeshFingerprintsVerifiedBeforeMerge = false)
        {
            AvatarRoot = avatarRoot;
            Installers = installers;
            NumericPolicy = numericPolicy;
            ObjectRegistry = objectRegistry;
            AllowPostMergePartArmatureScope = allowPostMergePartArmatureScope;
            PartMeshFingerprintsVerifiedBeforeMerge = partMeshFingerprintsVerifiedBeforeMerge;
        }
    }

    /// <summary>
    /// One consumed part renderer object and the target renderer object its geometry was assembled into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both members are the live GameObjects of the build clone. <see cref="Source"/> is the object the consumed
    /// renderer component lived on — the object an author's animation (a blend-shape curve on the part renderer,
    /// for instance) addresses — and <see cref="Target"/> is the object of the group's target renderer, which
    /// survives the run. The pair is captured <i>before</i> the run destroys the source renderer, because a
    /// destroyed component can no longer be asked for its GameObject.
    /// </para>
    /// <para>
    /// The pair is what NDMF's <c>AnimatorServicesContext.ObjectPathRemapper.ReplaceObject</c> is called with, so
    /// a recorded animation path that pointed at the consumed part renderer follows the geometry into the target
    /// renderer. It is an identity record: the target object outlives the run, and the source object may be
    /// removed afterwards if it is left empty (see <see cref="ApaSourceObjectCleanup"/>). Only pairs whose two
    /// objects are alive and different are recorded; a null or identical pair is not a mapping and is skipped.
    /// </para>
    /// </remarks>
    public sealed class ApaRendererReplacement
    {
        /// <summary>The consumed part renderer's GameObject. Never null in a recorded pair.</summary>
        public GameObject Source { get; }

        /// <summary>The target renderer's GameObject the part's geometry was assembled into. Never null.</summary>
        public GameObject Target { get; }

        /// <summary>Creates a pair.</summary>
        public ApaRendererReplacement(GameObject source, GameObject target)
        {
            Source = source;
            Target = target;
        }
    }

    /// <summary>
    /// What one target group's assembly produced, and what it replaced.
    /// </summary>
    /// <remarks>
    /// One entry per resolved target renderer. The mesh is owned by the run's result until the group is marked
    /// assigned; <see cref="ConsumedRenderers"/> and <see cref="ConsumedInstallers"/> are the objects the run
    /// destroyed for this group, listed so a caller can report what happened without dereferencing them.
    /// </remarks>
    public sealed class ApaBuildGroupResult
    {
        private static readonly Renderer[] s_noRenderers = new Renderer[0];
        private static readonly AvatarPartInstaller[] s_noInstallers = new AvatarPartInstaller[0];

        /// <summary>The group key: the target renderer's avatar-root-relative path.</summary>
        public string GroupKey { get; }

        /// <summary>The renderer this group's mesh was written to.</summary>
        public Renderer TargetRenderer { get; }

        /// <summary>The generated mesh, or null. The caller owns it once the group is assigned.</summary>
        public Mesh GeneratedMesh { get; }

        /// <summary>The installers whose geometry this group assembled.</summary>
        public IReadOnlyList<AvatarPartInstaller> Installers { get; }

        /// <summary>The part renderers this group replaced, in discovery order.</summary>
        public IReadOnlyList<Renderer> ConsumedRenderers { get; }

        /// <summary>The installers this group consumed (destroyed), in discovery order.</summary>
        public IReadOnlyList<AvatarPartInstaller> ConsumedInstallers { get; }

        /// <summary>Creates a group result.</summary>
        public ApaBuildGroupResult(
            string groupKey,
            Renderer targetRenderer,
            Mesh generatedMesh,
            IReadOnlyList<AvatarPartInstaller> installers,
            IReadOnlyList<Renderer> consumedRenderers,
            IReadOnlyList<AvatarPartInstaller> consumedInstallers)
        {
            GroupKey = groupKey ?? string.Empty;
            TargetRenderer = targetRenderer;
            GeneratedMesh = generatedMesh;
            Installers = installers ?? s_noInstallers;
            ConsumedRenderers = consumedRenderers ?? s_noRenderers;
            ConsumedInstallers = consumedInstallers ?? s_noInstallers;
        }
    }

    /// <summary>
    /// What one processing run did: the diagnostics, one generated mesh per target group, and what it consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Groups"/> carries one entry per target renderer the run assembled. The generated meshes are
    /// owned by the caller on success, exactly as <see cref="AssemblyResult"/> documents, so a caller that
    /// discards a successful result must destroy them. On failure no mesh is returned at all: a partially
    /// assembled or unassigned mesh is never handed out, and any mesh the run did create is destroyed before the
    /// failure is returned.
    /// </para>
    /// <para>
    /// <see cref="GeneratedMesh"/> and <see cref="TargetRenderer"/> are the single-group convenience: they are
    /// the first group's values, and <see cref="GeneratedMesh"/> is null when the run produced no group at all.
    /// A caller that must reason about every target uses <see cref="Groups"/>.
    /// </para>
    /// <para>
    /// <see cref="ConsumedInstallers"/> and <see cref="ConsumedRenderers"/> are the objects this run destroyed:
    /// they are identity records for the caller (what was consumed), and neither the caller nor a diagnostic may
    /// dereference them. <see cref="References"/> carries the clickable references a report needs, resolved
    /// before the destruction.
    /// </para>
    /// </remarks>
    public sealed class ApaBuildResult
    {
        private static readonly ApaBuildGroupResult[] s_noGroups = new ApaBuildGroupResult[0];
        private static readonly AvatarPartInstaller[] s_noInstallers = new AvatarPartInstaller[0];
        private static readonly Renderer[] s_noRenderers = new Renderer[0];
        private static readonly ApaRendererReplacement[] s_noReplacements = new ApaRendererReplacement[0];

        /// <summary>True when there was nothing to do: the avatar has no enabled installer.</summary>
        public bool NothingToDo { get; }

        /// <summary>True when a mesh was generated for every target group and written to its renderer.</summary>
        public bool Succeeded { get; }

        /// <summary>Every diagnostic the run produced, in deterministic order.</summary>
        public ValidationResult Issues { get; }

        /// <summary>One entry per assembled target group, in ordinal group-key order. Empty on failure.</summary>
        public IReadOnlyList<ApaBuildGroupResult> Groups { get; }

        /// <summary>The first group's mesh, or null. See the type remarks.</summary>
        public Mesh GeneratedMesh => Groups.Count > 0 ? Groups[0].GeneratedMesh : null;

        /// <summary>The first group's target renderer, or null. See the type remarks.</summary>
        public Renderer TargetRenderer => Groups.Count > 0 ? Groups[0].TargetRenderer : null;

        /// <summary>The installers whose geometry was assembled and which were then consumed (destroyed).</summary>
        public IReadOnlyList<AvatarPartInstaller> ConsumedInstallers { get; }

        /// <summary>The part renderers whose geometry was assembled and which were then consumed (destroyed).</summary>
        public IReadOnlyList<Renderer> ConsumedRenderers { get; }

        /// <summary>
        /// The consumed part renderer objects and the target renderer objects they were assembled into.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Captured while both objects are still alive, because the consumed renderer components are destroyed by
        /// the time this result exists. One pair per consumed renderer that contributed to a target group, in
        /// group-key order and then discovery order, with null and identical pairs skipped and a repeated source
        /// object recorded once.
        /// </para>
        /// <para>
        /// The build pass records this list in the transient build state; the animator-retarget step hands each
        /// pair to NDMF's object path remapper, and the cleanup step removes a source object that the run left
        /// empty. A caller outside a build (the preview, or a direct processor call) may ignore it: it is a set of
        /// identities, and neither member is dereferenced by this result.
        /// </para>
        /// </remarks>
        public IReadOnlyList<ApaRendererReplacement> RetargetMappings { get; }

        /// <summary>
        /// The object references <see cref="Issues"/> point at, resolved before anything was mutated.
        /// </summary>
        /// <remarks>
        /// Reporting a per-part issue through this value never touches the (consumed) installer component it was
        /// resolved from; see <see cref="ApaNdmfDiagnostics.CaptureReferences"/>.
        /// </remarks>
        public ApaDiagnosticReferences References { get; }

        private ApaBuildResult(
            bool nothingToDo,
            bool succeeded,
            ValidationResult issues,
            IReadOnlyList<ApaBuildGroupResult> groups,
            IReadOnlyList<AvatarPartInstaller> consumedInstallers,
            IReadOnlyList<Renderer> consumedRenderers,
            ApaDiagnosticReferences references,
            IReadOnlyList<ApaRendererReplacement> retargetMappings)
        {
            NothingToDo = nothingToDo;
            Succeeded = succeeded;
            Issues = issues ?? ValidationResult.Empty;
            Groups = groups ?? s_noGroups;
            ConsumedInstallers = consumedInstallers ?? s_noInstallers;
            ConsumedRenderers = consumedRenderers ?? s_noRenderers;
            References = references ?? ApaDiagnosticReferences.Empty;
            RetargetMappings = retargetMappings ?? s_noReplacements;
        }

        /// <summary>No enabled installer was found, so nothing was validated and nothing was touched.</summary>
        public static readonly ApaBuildResult NoWork = new ApaBuildResult(
            true, false, ValidationResult.Empty, null, null, null, null, null);

        /// <summary>The run stopped before producing a mesh. The issues explain why.</summary>
        public static ApaBuildResult Failure(ValidationResult issues, ApaDiagnosticReferences references = null)
        {
            return new ApaBuildResult(false, false, issues, null, null, null, references, null);
        }

        /// <summary>The run produced one mesh per group and wrote each to its target renderer.</summary>
        public static ApaBuildResult Success(
            IReadOnlyList<ApaBuildGroupResult> groups,
            ValidationResult issues,
            IReadOnlyList<AvatarPartInstaller> consumedInstallers,
            IReadOnlyList<Renderer> consumedRenderers,
            ApaDiagnosticReferences references = null,
            IReadOnlyList<ApaRendererReplacement> retargetMappings = null)
        {
            return new ApaBuildResult(
                false, true, issues, groups, consumedInstallers, consumedRenderers, references, retargetMappings);
        }
    }

    /// <summary>
    /// The one entry point that turns an avatar clone plus its installers into assembled target meshes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Build and preview must produce the same result (R14, section 25), and the only reliable way to guarantee
    /// that is one processor both call. The processor owns the whole operation:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Discover and capture.</b> <see cref="ContextBuilder"/> resolves every target group and copies every
    /// input into immutable snapshots. Nothing is mutated by this step, and a parked installer is reported
    /// (<c>APA039</c>) rather than silently missing.
    /// </description></item>
    /// <item><description>
    /// <b>Plan every group, then validate the write.</b> <see cref="ApaCore.PlanGroups"/> validates and plans
    /// every target group before anything is built, and <see cref="ApaBuildTargets"/> resolves the target
    /// renderer and every final bone of every group while the hierarchy is still untouched. A group that cannot
    /// be written fails the whole avatar here, before a single mesh exists.
    /// </description></item>
    /// <item><description>
    /// <b>Build one mesh per group.</b> <see cref="ApaCore.AssembleGroups(TargetGroupPlanResult, ApaNumericPolicy)"/>
    /// builds exactly one mesh per plan from the plans that were just validated. N groups produce N meshes, and
    /// no group sees another group's output.
    /// </description></item>
    /// <item><description>
    /// <b>Write every group, or none.</b> The meshes, materials, and bones of every group are assigned first; a
    /// failure part-way through restores every renderer that was already written, releases every mesh, and
    /// reports the failure, so a partially assembled avatar cannot exist.
    /// </description></item>
    /// <item><description>
    /// <b>Consume last.</b> Only after every group is written are the consumed part renderers and installer
    /// components removed, so a failure can never remove geometry while leaving the assembled mesh unwritten.
    /// The source objects and their group's target objects are captured as identity pairs immediately before that
    /// removal (<see cref="ApaBuildResult.RetargetMappings"/>): the renderer components are gone afterwards, and
    /// the animation-retarget step that runs later in the build needs the objects, not the components.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Mutating only the clone.</b> The request names the hierarchy to mutate; the caller passes the NDMF
    /// build clone. No authoring asset is written, no generated mesh is assigned to an authoring object, and the
    /// mesh's lifetime belongs to the caller that received it.
    /// </para>
    /// </remarks>
    public static class ApaBuildProcessor
    {
        /// <summary>
        /// Validates, plans, assembles, and applies every enabled installer on one avatar clone.
        /// </summary>
        public static ApaBuildResult Process(ApaBuildRequest request)
        {
            if (request == null || request.AvatarRoot == null)
            {
                return ApaBuildResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "The build processor was invoked without an avatar root.",
                    detail: "reason=null-request")));
            }

            var avatarRoot = request.AvatarRoot;
            var policy = request.NumericPolicy ?? ApaNumericPolicy.Default;

            // Discovery reports a parked installer (APA039) rather than dropping it silently. When the caller
            // supplied its own list, that list is authoritative: the caller has already discovered them and owns
            // the diagnostics for the skip.
            var discoveryIssues = new List<ValidationIssue>();
            var installers = request.Installers ?? ContextBuilder.CollectInstallers(avatarRoot, discoveryIssues);
            if (installers.Count == 0) return ApaBuildResult.NoWork;

            // Resolve the diagnostic references now, while every installer is still alive: Apply consumes
            // (destroys) the installer components, and NDMF's registry asks an object for its path when it
            // resolves one, so reporting against a destroyed component would either throw or silently degrade
            // to the avatar root. See ApaNdmfDiagnostics.CaptureReferences.
            var references = ApaNdmfDiagnostics.CaptureReferences(avatarRoot, installers, request.ObjectRegistry);

            // ---- Plan: every group is resolved, validated, and planned before anything is built ------------
            var planned = ApaCore.PlanGroups(
                avatarRoot,
                policy,
                out var planningIssues,
                request.AllowPostMergePartArmatureScope,
                request.PartMeshFingerprintsVerifiedBeforeMerge);
            var groupPlan = ValidationResult.Build(planningIssues);

            if (planned.NotApplicable)
            {
                // No active installer: "not applicable", not a failure. An unrelated avatar in the same build
                // must not be turned red by this plugin.
                return ApaBuildResult.NoWork;
            }

            if (!planned.Succeeded)
            {
                return ApaBuildResult.Failure(BuildIssues(discoveryIssues, groupPlan, (ValidationIssue)null), references);
            }

            // ---- Resolve: every group's live write target and consumption set, still without mutating -------
            var steps = new List<GroupStep>(planned.GroupCount);
            var consumptionIssues = new List<ValidationIssue>();

            // Every target renderer of this run. The consumption planner excludes all of them, not just the
            // group's own: a part root that contains another group's target would otherwise consume (destroy) it
            // after the run has already reported success. The hierarchy conflict that produces that shape is
            // refused up front by ApaCore.PlanGroups, and this list keeps the rule true for the consumption step
            // itself.
            var targetRenderers = new List<Renderer>(planned.GroupCount);
            for (var g = 0; g < planned.GroupCount; g++)
            {
                var targetRenderer = planned.Groups[g].TargetRenderer;
                if (targetRenderer != null) targetRenderers.Add(targetRenderer);
            }

            for (var g = 0; g < planned.GroupCount; g++)
            {
                var group = planned.Groups[g];
                var planning = planned.Plans[g];

                if (!ApaBuildTargets.TryResolve(
                        avatarRoot, group.Context, planning.Plan, out var target, out var resolutionIssue))
                {
                    // Nothing has been built yet, so the failure is clean: no mesh exists and no renderer was
                    // written.
                    return ApaBuildResult.Failure(
                        BuildIssues(discoveryIssues, groupPlan, resolutionIssue), references);
                }

                var consumption = ApaPartConsumptionPlanner.ForGroup(
                    avatarRoot, group.Context, installers, targetRenderers, consumptionIssues);

                steps.Add(new GroupStep(group, planning, target, consumption));
            }

            // ---- Build: one mesh per group, from the plans that were just validated -------------------------
            var assembled = ApaCore.AssembleGroups(planned, policy);
            var assemblyIssues = MergeIssues(null, groupPlan, assembled.Issues.Issues);

            if (!assembled.Succeeded)
            {
                // TargetGroupAssembly already destroyed every mesh it created for this failed run.
                return ApaBuildResult.Failure(
                    BuildIssues(discoveryIssues, assemblyIssues, null), references);
            }

            // ---- Write: every group first; a failure restores every group that was already written ----------
            var applied = new List<AppliedGroup>(assembled.GroupCount);

            for (var g = 0; g < assembled.GroupCount; g++)
            {
                var entry = assembled.Groups[g];
                var step = steps[g];

                // Captured before the write, so a later group's failure can restore this one exactly.
                var previous = CaptureState(step.Target);

                if (!Apply(entry.Assembly, step.Target, request.ObjectRegistry, out var applyIssue))
                {
                    // Roll back in reverse order. Nothing was consumed yet, so a rollback restores the exact
                    // pre-run state of every renderer this loop touched.
                    RestoreApplied(applied);

                    // Every mesh is still unassigned, so this destroys all of them: no leak, and no renderer
                    // points at a destroyed mesh.
                    assembled.ReleaseUnassigned();

                    return ApaBuildResult.Failure(
                        BuildIssues(discoveryIssues, assemblyIssues, applyIssue), references);
                }

                applied.Add(new AppliedGroup(step, entry, previous));
            }

            // The write succeeded for every group, so ownership of every mesh transfers to the caller.
            for (var g = 0; g < assembled.GroupCount; g++) assembled.MarkAssigned(g);

            // ---- Consume: only now, and only the geometry that was assembled --------------------------------
            var consumedRenderers = new List<Renderer>();
            var consumedInstallers = new List<AvatarPartInstaller>();
            var groupResults = new List<ApaBuildGroupResult>(applied.Count);

            for (var i = 0; i < applied.Count; i++)
            {
                var step = applied[i].Step;
                var renderers = step.Consumption.Renderers;
                var groupInstallers = step.Consumption.Installers;

                for (var r = 0; r < renderers.Count; r++)
                {
                    var renderer = renderers[r];
                    if (renderer == null || consumedRenderers.Contains(renderer)) continue;
                    consumedRenderers.Add(renderer);
                }

                for (var s = 0; s < groupInstallers.Count; s++)
                {
                    var installer = groupInstallers[s];
                    if (installer == null || consumedInstallers.Contains(installer)) continue;
                    consumedInstallers.Add(installer);
                }

                groupResults.Add(new ApaBuildGroupResult(
                    step.Group.GroupKey,
                    step.Target.Renderer,
                    applied[i].Entry.Assembly.Mesh,
                    step.Group.Installers,
                    renderers,
                    groupInstallers));
            }

            // The source objects and their group's target objects are captured here, while both are still alive:
            // the renderer components are destroyed immediately below, and a destroyed component can no longer be
            // asked for its GameObject. The pairs are what the animator-retarget step hands to NDMF's object path
            // remapper and what the cleanup step inspects, so they have to be identities, not component
            // references. See ApaRendererReplacement.
            var retargetMappings = CaptureRendererReplacements(groupResults);

            // Consumption happens only after every assignment succeeded, so a failure can never remove the part
            // geometry while leaving the assembled mesh unwritten. Only the component is destroyed: bones,
            // children, and any other object under the part root are left alone, because the generated mesh may
            // be skinned to them. ApaPartConsumptionPlanner decided which renderers are in the set, and why every
            // renderer under a consumed part root belongs to it.
            for (var i = 0; i < consumedRenderers.Count; i++)
            {
                UnityEngine.Object.DestroyImmediate(consumedRenderers[i]);
            }

            for (var i = 0; i < consumedInstallers.Count; i++)
            {
                UnityEngine.Object.DestroyImmediate(consumedInstallers[i]);
            }

            return ApaBuildResult.Success(
                groupResults,
                MergeIssues(new List<ValidationIssue>(discoveryIssues), assemblyIssues, consumptionIssues),
                consumedInstallers,
                consumedRenderers,
                references,
                retargetMappings);
        }

        /// <summary>
        /// The source→target object pairs of a successful run, captured before the source renderers are destroyed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every consumed renderer of a group maps to that group's own target renderer object, which is where the
        /// group's mesh was written. A pair whose source or target object is missing, or whose two objects are
        /// the same object, is not a mapping and is skipped: there is nothing for the remapper to move, and
        /// recording it would only make the cleanup step consider an object that was never replaced. A source
        /// object that appears twice — a renderer reachable from two groups' consumption sets — is recorded once,
        /// against the first group that claimed it, so the remapper is never asked to move the same object twice.
        /// </para>
        /// <para>
        /// The result is a fresh list in group-key order and then discovery order, so a build's mapping set is
        /// deterministic and does not depend on hash or scan order.
        /// </para>
        /// <para>
        /// Public because it is the capture seam: it is a pure function over the run's own result values, so the
        /// mapping rule can be asserted against real objects without standing up an NDMF build, exactly as the
        /// build's other public contracts are.
        /// </para>
        /// </remarks>
        public static List<ApaRendererReplacement> CaptureRendererReplacements(
            IReadOnlyList<ApaBuildGroupResult> groups)
        {
            var result = new List<ApaRendererReplacement>();
            if (groups == null) return result;

            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group == null) continue;

                var target = group.TargetRenderer != null ? group.TargetRenderer.gameObject : null;
                if (target == null) continue;

                var sources = group.ConsumedRenderers;
                for (var r = 0; r < sources.Count; r++)
                {
                    var renderer = sources[r];
                    if (renderer == null) continue;

                    var source = renderer.gameObject;
                    if (source == null || source == target) continue;
                    if (ContainsSource(result, source)) continue;

                    result.Add(new ApaRendererReplacement(source, target));
                }
            }

            return result;
        }

        /// <summary>True when a pair for this source object has already been captured.</summary>
        private static bool ContainsSource(List<ApaRendererReplacement> pairs, GameObject source)
        {
            for (var i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Source == source) return true;
            }

            return false;
        }

        /// <summary>One group's resolved write target and the geometry it replaces.</summary>
        private sealed class GroupStep
        {
            internal ApaTargetGroupPlan Group { get; }
            internal PlanningResult Planning { get; }
            internal ApaBuildTargets.MutationTarget Target { get; }
            internal ApaPartConsumptionPlan Consumption { get; }

            internal GroupStep(
                ApaTargetGroupPlan group,
                PlanningResult planning,
                ApaBuildTargets.MutationTarget target,
                ApaPartConsumptionPlan consumption)
            {
                Group = group;
                Planning = planning;
                Target = target;
                Consumption = consumption ?? ApaPartConsumptionPlan.Empty;
            }
        }

        /// <summary>A group whose mesh was written, with what is needed to undo the write.</summary>
        private sealed class AppliedGroup
        {
            internal GroupStep Step { get; }
            internal TargetGroupAssemblyEntry Entry { get; }
            internal AssignedState Previous { get; }

            internal AppliedGroup(GroupStep step, TargetGroupAssemblyEntry entry, AssignedState previous)
            {
                Step = step;
                Entry = entry;
                Previous = previous;
            }
        }

        /// <summary>The renderer state captured before one group's mesh was written.</summary>
        private sealed class AssignedState
        {
            internal Renderer Renderer;
            internal Mesh Mesh;
            internal Material[] Materials;
            internal Transform[] Bones;
        }

        /// <summary>Reads everything a rollback has to put back, before the write happens.</summary>
        private static AssignedState CaptureState(ApaBuildTargets.MutationTarget target)
        {
            var renderer = target != null ? target.Renderer : null;
            var skinned = target != null ? target.Skinned : null;

            return new AssignedState
            {
                Renderer = renderer,
                Mesh = skinned != null ? skinned.sharedMesh : (target != null && target.MeshFilter != null
                    ? target.MeshFilter.sharedMesh
                    : null),
                Materials = renderer != null ? renderer.sharedMaterials : null,
                Bones = skinned != null ? skinned.bones : null
            };
        }

        private static void RestoreApplied(List<AppliedGroup> applied)
        {
            for (var i = applied.Count - 1; i >= 0; i--)
            {
                var previous = applied[i].Previous;
                var renderer = previous.Renderer;
                if (renderer == null) continue;

                var skinned = renderer as SkinnedMeshRenderer;
                Restore(
                    renderer,
                    skinned,
                    skinned != null ? null : renderer.GetComponent<MeshFilter>(),
                    previous.Mesh,
                    previous.Materials,
                    previous.Bones);
            }

            applied.Clear();
        }

        /// <summary>
        /// Writes the generated mesh, materials, and bones of one group, and captures what it replaced.
        /// </summary>
        /// <remarks>
        /// The previous assignments are captured first so that a later group's failure can be undone: an
        /// exception here must not leave a renderer pointing at a mesh that is about to be destroyed. The restore
        /// is best effort and its own failure is reported in the diagnostic rather than thrown, because by then
        /// the original exception is the interesting one.
        /// </remarks>
        private static bool Apply(
            AssemblyResult assembly,
            ApaBuildTargets.MutationTarget target,
            IObjectRegistry registry,
            out ValidationIssue issue)
        {
            issue = null;

            var renderer = target.Renderer;
            var skinned = target.Skinned;
            var filter = target.MeshFilter;
            var mesh = assembly != null ? assembly.Mesh : null;

            if (renderer == null || mesh == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "The assembly layer was asked to write a group without a renderer or a generated mesh.",
                    detail: "reason=null-apply-input");
                return false;
            }

            var previousMesh = skinned != null ? skinned.sharedMesh : filter.sharedMesh;
            var previousMaterials = renderer.sharedMaterials;
            var previousBones = skinned != null ? skinned.bones : null;

            try
            {
                if (skinned != null)
                {
                    // Bones are written before the mesh so the renderer never points at a mesh whose bone indices
                    // have no array to index. When the plan carries no bone table the renderer keeps the bone
                    // array it already had: an unskinned mesh ignores it, and clearing it would be a change the
                    // plan never asked for.
                    if (target.Bones != null) skinned.bones = target.Bones;
                    skinned.sharedMesh = mesh;
                }
                else
                {
                    filter.sharedMesh = mesh;
                }

                renderer.sharedMaterials = assembly.Materials;

                // The mesh carries no hide flags on purpose. NDMF persists every non-persistent asset the avatar
                // references into its generated-asset container when the build finishes
                // (BuildContext.Serialize -> AssetSaver.SaveAssets), which is how Modular Avatar's own
                // generated meshes reach the uploaded avatar; HideFlags.DontSave would make it unsavable and
                // leave the uploaded avatar without its body mesh. Its lifetime is the build clone's, and the
                // caller that receives it destroys it if it is not used.
                mesh.hideFlags = HideFlags.None;

                if (registry != null && previousMesh != null)
                {
                    // Records "this mesh replaced that one" so an error report can still point at the mesh the
                    // author knows, which is what Modular Avatar's own mesh retargeting does.
                    registry.RegisterReplacedObject(previousMesh, mesh);
                }
            }
            catch (Exception e)
            {
                var restoreFailure = Restore(renderer, skinned, filter, previousMesh, previousMaterials, previousBones);
                issue = ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "Assigning the assembled mesh to '" + renderer.name + "' failed: " + e.Message,
                    detail: "reason=assignment-failed; exception=" + e.GetType().FullName + restoreFailure);
                return false;
            }

            return true;
        }

        private static string Restore(
            Renderer renderer,
            SkinnedMeshRenderer skinned,
            MeshFilter filter,
            Mesh previousMesh,
            Material[] previousMaterials,
            Transform[] previousBones)
        {
            try
            {
                if (skinned != null)
                {
                    skinned.sharedMesh = previousMesh;
                    if (previousBones != null) skinned.bones = previousBones;
                }
                else if (filter != null)
                {
                    filter.sharedMesh = previousMesh;
                }

                if (renderer != null) renderer.sharedMaterials = previousMaterials;
                return string.Empty;
            }
            catch (Exception restore)
            {
                return "; restore-failed=" + restore.GetType().FullName;
            }
        }

        private static ValidationResult BuildIssues(
            IReadOnlyList<ValidationIssue> discoveryIssues,
            ValidationResult assemblyIssues,
            ValidationIssue extra = null)
        {
            return MergeIssues(
                discoveryIssues,
                assemblyIssues,
                extra != null ? new List<ValidationIssue> { extra } : null);
        }

        /// <summary>Every stream of diagnostics this run produced, as one sorted, deduplicated report.</summary>
        private static ValidationResult MergeIssues(
            IReadOnlyList<ValidationIssue> first,
            ValidationResult second,
            IReadOnlyList<ValidationIssue> third)
        {
            var merged = new List<ValidationIssue>();
            if (first != null) merged.AddRange(first);
            if (second != null) merged.AddRange(second.Issues);
            if (third != null) merged.AddRange(third);

            // Sorting and deduplicating here is what makes the reported set identical to the set the validator
            // would produce on its own; a defect both the context builder and the planner see is reported once.
            return ValidationResult.Build(merged);
        }
    }
}

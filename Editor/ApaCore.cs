using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The single core entry point that preview and build both use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Having exactly one facade is a deliberate architectural choice, not convenience. The specification
    /// requires that preview and build produce identical results, and the only reliable way to guarantee that is
    /// for both to call the same code with the same inputs rather than for two similar implementations to be
    /// kept in sync by discipline.
    /// </para>
    /// <para>
    /// Every method here returns an explicit result. Nothing throws for bad asset data, and nothing assigns a
    /// generated mesh to an authoring asset.
    /// </para>
    /// <para>
    /// <b>Single target and target groups.</b> The context-based methods (<see cref="Validate"/>,
    /// <see cref="Plan"/>, <see cref="Assemble(ValidationContext)"/>) keep their M2 contracts and operate on one
    /// already-built context. The group methods (<see cref="PlanGroups"/> and <see cref="AssembleGroups"/>)
    /// resolve an avatar's target renderers, produce one context and one plan per target, and only then build
    /// one mesh per group. Both paths funnel into the same validator, planner, and assembler, which is what keeps
    /// a group's result identical to the result the single-target path would have produced for that group.
    /// </para>
    /// </remarks>
    public static class ApaCore
    {
        /// <summary>
        /// Validates a configuration without producing a plan. Use this to drive a validator UI.
        /// </summary>
        public static ValidationResult Validate(ValidationContext context)
        {
            return new AvatarPartValidator().Validate(context);
        }

        /// <summary>
        /// Plans an assembly without building a mesh.
        /// </summary>
        public static PlanningResult Plan(ValidationContext context)
        {
            return new AssemblyPlanner().Plan(context);
        }

        /// <summary>
        /// Validates, plans, and builds in one call.
        /// </summary>
        /// <remarks>
        /// This is the method an NDMF pass or a preview filter should call. It always produces a fresh transient
        /// mesh on success; the caller owns that mesh and must destroy it when done.
        /// </remarks>
        public static AssemblyResult Assemble(ValidationContext context)
        {
            return Assemble(context, new MeshAssembler());
        }

        /// <summary>
        /// Validates, plans, and builds using explicit skinning and blend shape providers.
        /// </summary>
        public static AssemblyResult Assemble(ValidationContext context, MeshAssembler assembler)
        {
            if (context == null)
            {
                return AssemblyResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "Assembly was invoked without a context.",
                    detail: "reason=null-context")));
            }

            var planning = Plan(context);
            if (!planning.Succeeded)
            {
                return AssemblyResult.Failure(planning.Issues);
            }

            var result = (assembler ?? new MeshAssembler()).Build(planning.Plan, context);

            // The plan carries only non-blocking issues, so the run's full diagnostic set is the validation and
            // planning issues plus whatever the build added. Merging them keeps a single report for the user.
            var merged = new List<ValidationIssue>(planning.Issues.Issues);
            merged.AddRange(result.Issues.Issues);

            return new AssemblyResult(result.Mesh, result.Materials, result.Plan, ValidationResult.Build(merged));
        }

        /// <summary>
        /// Resolves every target group of an avatar and plans all of them, without building anything.
        /// </summary>
        /// <remarks>
        /// Every group is planned before any group is built, so a blocking issue in any group fails the whole
        /// avatar and no mesh is produced. This is the method a preview implementation should call when it needs
        /// to know whether the configuration is buildable before it touches a proxy.
        /// </remarks>
        public static TargetGroupPlanResult PlanGroups(
            GameObject avatarRoot,
            ApaNumericPolicy numericPolicy,
            out List<ValidationIssue> issues,
            bool allowPostMergePartArmatureScope = false)
        {
            return TargetGroupAssembly.Plan(
                avatarRoot,
                numericPolicy,
                out issues,
                allowPostMergePartArmatureScope);
        }

        /// <summary>
        /// Resolves, plans, and builds one mesh per target group of an avatar.
        /// </summary>
        /// <remarks>
        /// The returned result owns every generated mesh: the caller marks the groups whose mesh it assigned to
        /// the build clone's renderer and releases the rest. A blocking issue in any group produces no mesh at
        /// all, so a failed assembly never hands back a partial avatar.
        /// </remarks>
        public static TargetGroupAssemblyResult AssembleGroups(
            GameObject avatarRoot,
            ApaNumericPolicy numericPolicy,
            out List<ValidationIssue> issues)
        {
            return TargetGroupAssembly.Assemble(avatarRoot, numericPolicy, out issues);
        }

        /// <summary>
        /// Builds one mesh per group from an already-planned group result, without re-planning.
        /// </summary>
        /// <param name="planned">The plan result to build.</param>
        /// <param name="numericPolicy">
        /// The numeric policy to build with. Null means "the policy the plans were produced with"; a policy that
        /// disagrees with the plan's own is refused rather than ignored, because the plan's decisions were already
        /// made with those tolerances. See <see cref="TargetGroupAssembly.BuildPlanned"/>.
        /// </param>
        public static TargetGroupAssemblyResult AssembleGroups(
            TargetGroupPlanResult planned,
            ApaNumericPolicy numericPolicy)
        {
            return TargetGroupAssembly.BuildPlanned(planned, numericPolicy);
        }

        /// <summary>
        /// Builds a default UV semantic declaration for a mesh that has never been configured: channel 0 is
        /// named <c>UV0</c> and any further present channel is named <c>UV&lt;index&gt;</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This exists so that a simple part works without the authoring window, while still flowing through the
        /// same semantic pipeline as a fully configured one. Nothing here is a matching rule; it is capture-time
        /// naming, and the authoring window replaces it with explicit semantics.
        /// </para>
        /// <para>
        /// The names are the ones the build's automatic passthrough channel uses
        /// (<see cref="ApaWellKnownSemantics.AutoChannelNameFor"/>), so a source captured with inferred semantics
        /// and a source the build names automatically produce the <i>same</i> semantic for the same physical
        /// channel. That is what keeps two parts carrying an undeclared UV0 on one final channel instead of one
        /// channel each, whichever path named them.
        /// </para>
        /// </remarks>
        public static ApaUvChannelSemantic[] InferUvSemantics(MeshSnapshot mesh)
        {
            if (mesh == null) return Array.Empty<ApaUvChannelSemantic>();

            var channels = mesh.PresentUvChannels();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ApaUvChannelSemantic>(channels.Count);

            for (var i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                var name = ApaWellKnownSemantics.AutoChannelNameFor(channel, names);
                if (name == null) continue;

                names.Add(name);
                result.Add(new ApaUvChannelSemantic(name, channel));
            }

            return result.ToArray();
        }

        /// <summary>
        /// Builds a default material semantic declaration for a renderer's materials: one semantic per submesh,
        /// named after the material asset.
        /// </summary>
        public static ApaMaterialSlotSemantic[] InferMaterialSemantics(int subMeshCount, Material[] materials)
        {
            var count = Mathf.Max(subMeshCount, 0);
            var result = new ApaMaterialSlotSemantic[count];
            for (var i = 0; i < count; i++)
            {
                var material = materials != null && i < materials.Length ? materials[i] : null;
                var name = material != null ? material.name : "SubMesh" + i;
                result[i] = new ApaMaterialSlotSemantic(name, i, material, ApaMaterialPolicyMode.Auto);
            }

            return result;
        }
    }
}

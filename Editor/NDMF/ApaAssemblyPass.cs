using System.Collections.Generic;
using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Runs the assembly core against the post-merge clone and writes one assembled mesh per target group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass is registered in <see cref="BuildPhase.Transforming"/> after Modular Avatar, so by the time it
    /// runs the transient merge configuration created in Generating has been consumed: the part armatures are
    /// one with the avatar's, and the bone table is rebuilt from the hierarchy that will actually be uploaded
    /// (section 19, section 44.8).
    /// </para>
    /// <para>
    /// <b>One transaction, one mesh per target.</b> The processor resolves and plans every target group of the
    /// avatar before it builds anything, builds exactly one mesh per group, writes all of them or none, and only
    /// then removes the geometry they replaced. An avatar whose parts weld to a body renderer and to a separate
    /// clothing renderer therefore produces two meshes in one build, with no partial output on any failure.
    /// </para>
    /// <para>
    /// <b>The merge is verified, not assumed.</b> Before anything is assembled, every part the Generating pass
    /// configured must pass the post-merge check: its transient component is gone <i>and</i> the bone the plan
    /// recorded has left the part root. Modular Avatar destroys a configuration it skipped exactly like one it
    /// merged, so the hierarchy postcondition — not the component's lifetime — is what proves the bones now
    /// follow the avatar's skeleton. The same gate also checks that the Generating pass ran at all: an empty
    /// record of configurations means "nothing needed merging" only when that pass actually executed, and a
    /// build in which it never ran is refused instead of being assembled with unmerged skeletons (see
    /// <see cref="ApaTransientArtifacts.Configured"/>).
    /// </para>
    /// <para>
    /// <b>A build that has already failed is not mutated.</b> If any plugin has reported a blocking error by the
    /// time this pass runs — including this plugin's own Generating pass — <c>BuildContext.Successful</c> is
    /// false and the pass returns immediately. That single gate is what makes "a failed merge configuration
    /// stops the assembly" structural rather than a second, parallel bookkeeping flag: NDMF's report is the
    /// bookkeeping.
    /// </para>
    /// <para>
    /// <b>An avatar with no enabled installer is not touched and not reported against.</b> The discovery gate is
    /// the same <see cref="ContextBuilder.CollectInstallers(GameObject)"/> call the processor makes, so an
    /// unrelated avatar in the same project cannot be blocked by this plugin.
    /// </para>
    /// <para>
    /// All of the work lives in <see cref="ApaBuildProcessor.Process(ApaBuildRequest)"/>, which is the entry
    /// point M4's preview will call; this pass only decides whether to run and how to report.
    /// </para>
    /// </remarks>
    internal sealed class ApaAssemblyPass : Pass<ApaAssemblyPass>
    {
        /// <inheritdoc />
        /// <remarks>
        /// Resolved when NDMF draws the build report, so the pass name follows the selected language.
        /// </remarks>
        public override string DisplayName => ApaLocalization.Tr("Assemble part geometry into the target body mesh");

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var avatarRoot = context.AvatarRootObject;
            if (avatarRoot == null) return;

            // The report is the gate: an error reported earlier (a merge target that could not be derived, a
            // Modular Avatar failure, anything else) has already blocked the upload, and mutating the clone
            // anyway would only produce a second, misleading failure.
            if (!context.Successful) return;

            var installers = ContextBuilder.CollectInstallers(avatarRoot);
            if (installers.Count == 0) return;

            // Modular Avatar consumes (destroys) every merge configuration the Generating pass created --
            // including a configuration it could not merge -- so the component's absence is only half the check.
            // The postcondition is that the part's skeleton actually left the part root; a part whose bone is
            // still there was never merged, and assembling anyway would ship bones that are not part of the
            // avatar's armature. Refusing here keeps "the configuration was applied" a checked property rather
            // than an assumption; see ApaTransientArtifacts.
            var artifacts = ApaTransientArtifacts.For(context);

            // An empty record means two different things, and only one of them is a pass. "This pass's
            // predecessor ran and no part needed a configuration" has nothing left to verify; "the Generating
            // pass never ran" means no part's bones were merged at all, and the empty postcondition list below
            // would otherwise pass silently. The flag records which one happened.
            if (!artifacts.Configured)
            {
                // Nothing has been consumed on this path: the pass returns before it calls the processor, so the
                // references are resolved here and now, from live installers.
                ApaNdmfDiagnostics.Report(
                    new List<ValidationIssue>
                    {
                        ValidationIssue.Error(
                            ApaErrorCode.TargetBoneNotFound,
                            ApaIssuePhase.Compatibility,
                            "The pass that creates the transient merge-armature configurations did not run for " +
                            "this build, so no part's bones were merged into the avatar's armature. Assembling " +
                            "now would ship parts whose skeletons are not part of the avatar, which looks " +
                            "correct at rest and detaches the moment the avatar animates. Check that this " +
                            "plugin's Generating pass is enabled and that its build phase ran for this avatar.",
                            detail: "reason=merge-pass-did-not-run")
                    },
                    ApaNdmfDiagnostics.CaptureReferences(avatarRoot, installers, context.ObjectRegistry));
                return;
            }

            var leftovers = new List<ValidationIssue>();
            artifacts.CollectUnapplied(leftovers, ApaMergeArmaturePass.CollectPartRoots(installers));
            if (leftovers.Count > 0)
            {
                // Nothing has been consumed on this path: the pass returns before it calls the processor, so the
                // references are resolved here and now, from live installers.
                ApaNdmfDiagnostics.Report(
                    leftovers,
                    ApaNdmfDiagnostics.CaptureReferences(avatarRoot, installers, context.ObjectRegistry));
                return;
            }

            // The installer list is deliberately not handed to the processor: the processor's own discovery is
            // the one that reports a parked installer (APA039) and applies the shared activity predicate and the
            // avatar-ownership boundary, and a second, silent copy of that walk here would be a second answer to
            // "which installers are in play". The list collected above is this pass's gate and the input of the
            // post-merge check only.
            var request = new ApaBuildRequest(
                avatarRoot,
                null,
                null,
                context.ObjectRegistry,
                allowPostMergePartArmatureScope: true);
            var result = ApaBuildProcessor.Process(request);

            // NDMF recalculates UV distribution metrics for every temporary mesh the avatar references when the
            // build finishes (BuildContext.Finish -> RecalculateAllMeshes). That metric measures how UVs are
            // distributed so streaming mipmaps can pick a level; a mesh that carries no UV0 has no distribution
            // to measure, so it is excluded through the switch NDMF exposes for exactly this purpose instead of
            // being handed to the recalculation. Every group's mesh is checked: a target group whose parts
            // declared no channel 0 produces a mesh without UV0 exactly like the single-target case does.
            for (var i = 0; i < result.Groups.Count; i++)
            {
                var mesh = result.Groups[i].GeneratedMesh;
                if (mesh != null && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
                {
                    context.SetEnableUVDistributionRecalculation(mesh, false);
                }
            }

            // The references ride on the result rather than being resolved from the installers again: a
            // successful run has consumed (destroyed) them by now.
            ApaNdmfDiagnostics.Report(result.Issues.Issues, result.References);
        }
    }
}

using System.Collections.Generic;
using AvatarPartAssembler.Editor.Integration;
using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Creates the transient Modular Avatar merge-armature configuration each part profile asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pass creates configuration only. It reads meshes, transforms, and profile data, and adds at most one
    /// transient component per installed part; it does not assemble geometry, and it does not touch an authoring
    /// asset. Its decisions live in <see cref="MergeArmatureGenerator"/>, which is also where every Modular
    /// Avatar API call is made.
    /// </para>
    /// <para>
    /// <b>An avatar with no enabled installer is left completely alone.</b> That gate matters: without it, a
    /// build of any unrelated avatar in the project would report "no installers found" and block the upload. The
    /// gate uses the same discovery routine the assembly pass uses
    /// (<see cref="ContextBuilder.CollectInstallers"/>), so the two passes always agree on which installers are
    /// in play and in which order.
    /// </para>
    /// <para>
    /// <b>The discovery walk itself is not owned here.</b> Avatar scoping (a nested avatar's installers must not
    /// be processed by the outer avatar's build) and the shared installer activity predicate belong to
    /// <see cref="ContextBuilder.CollectInstallers"/>, which is the one place both passes call; that keeps this
    /// pass free of a second, divergent notion of "which installers are in play".
    /// </para>
    /// <para>
    /// Diagnostics are reported through NDMF's error report. A blocking issue reported here makes
    /// <c>BuildContext.Successful</c> false for the rest of the build, which is what stops the assembly pass from
    /// mutating the clone when the configuration it depends on could not be produced.
    /// </para>
    /// </remarks>
    internal sealed class ApaMergeArmaturePass : Pass<ApaMergeArmaturePass>
    {
        /// <inheritdoc />
        /// <remarks>
        /// Resolved when NDMF draws the build report, so the pass name follows the selected language.
        /// </remarks>
        public override string DisplayName => ApaLocalization.Tr("Create transient merge-armature configuration");

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var avatarRoot = context.AvatarRootObject;
            if (avatarRoot == null) return;

            var installers = ContextBuilder.CollectInstallers(avatarRoot);
            if (installers.Count == 0) return;

            // The references the report will use are resolved before this pass adds anything to the clone, and
            // while every installer is alive; see ApaNdmfDiagnostics.CaptureReferences.
            var references = ApaNdmfDiagnostics.CaptureReferences(avatarRoot, installers, context.ObjectRegistry);

            var partRoots = CollectPartRoots(installers);
            var issues = new List<ValidationIssue>();
            var artifacts = ApaTransientArtifacts.For(context);

            // Declares that this pass ran, before any part is planned: the assembly pass distinguishes "no part
            // needed a merge configuration" from "this pass never ran", and only the first one is a pass. See
            // ApaTransientArtifacts.Configured.
            artifacts.MarkConfigured();

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                var profile = installer.Profile;

                // A missing or unreadable profile is the assembly pass's diagnostic to report: it has the same
                // installer in its own canonical list, and reporting it twice would only duplicate the message.
                if (profile == null) continue;

                var partRoot = installer.ResolvePartRoot();
                if (partRoot == null) continue;

                // The profile is a shared authoring asset: read the non-mutating accessor, never the materializing
                // Bones property. A null bone profile means "no policy", which the generator already defines as
                // exact-name matching, and it must not be materialized onto the asset from a build.
                var bones = profile.BonesOrNull;

                var request = new MergeArmatureRequest(
                    avatarRoot,
                    partRoot,
                    // The same resolution the context builder used, so a merge diagnostic and an assembly
                    // diagnostic name a legacy part by the same (derived) id.
                    ApaPartIdentityResolver.ResolvePartId(installer),
                    bones,
                    partRoots);

                if (!MergeArmatureGenerator.TryPlanMerge(request, issues, out var plan)) continue;

                var configuration = MergeArmatureGenerator.CreateMergeConfiguration(
                    plan.MergeRoot, plan.TargetArmature, issues);

                // The configuration and the bone it has to move are recorded so the assembly pass can tell
                // whether Modular Avatar actually merged the part rather than only destroying the component;
                // see ApaTransientArtifacts.
                artifacts.Record(configuration, request.PartId, plan.PartTopBone);
            }

            ApaNdmfDiagnostics.Report(issues, references);
        }

        /// <summary>
        /// The part root of every enabled installer, in the canonical installer order.
        /// </summary>
        /// <remarks>
        /// The list is used to separate part bones from avatar bones when the merge target is derived, so it must
        /// contain every part in the build, not only the one being planned: two parts that copy the same skeleton
        /// must not match each other's copies.
        /// </remarks>
        internal static List<GameObject> CollectPartRoots(IReadOnlyList<AvatarPartInstaller> installers)
        {
            var result = new List<GameObject>(installers != null ? installers.Count : 0);
            if (installers == null) return result;

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                if (installer == null) continue;

                var partRoot = installer.ResolvePartRoot();
                if (partRoot == null) continue;
                if (result.Contains(partRoot)) continue;

                result.Add(partRoot);
            }

            return result;
        }
    }
}

using System.Collections.Generic;
using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Removes every <see cref="AvatarPartInstaller"/> a successful build clone still carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the removal has to exist.</b> The assembly consumes an installer only when the part's geometry was
    /// assembled into a target mesh (<see cref="ApaBuildProcessor"/> destroys exactly
    /// <see cref="ApaPartConsumptionPlan.Installers"/>). Every other installer survives into the uploaded avatar:
    /// a parked one (<see cref="AvatarPartInstaller.IsActiveForBuild"/> false) is never processed at all, an
    /// installer whose part contributed no geometry is not in the plan, and an avatar whose only installers are
    /// parked never reaches the processor.
    /// </para>
    /// <para>
    /// <b>It is not the fix for the SDK panel warning — the interface is.</b> <see cref="AvatarPartInstaller"/>
    /// implements <c>nadena.dev.ndmf.INDMFEditorOnly</c> (and therefore <c>VRC.SDKBase.IEditorOnly</c>), so the
    /// VRChat SDK's own <c>ValidationUtils.IsEditorOnly</c> already excludes it from the panel's
    /// <i>"component types … will be removed by the client"</i> scan, which reads the <i>scene</i> avatar and never
    /// sees a clone. This pass exists for the paths the SDK's strip callbacks never run on: Modular Avatar and
    /// VRCFury both replace the SDK's <c>RemoveAvatarEditorOnly</c> with late-stage callbacks, and
    /// <c>AvatarProcessor.ProcessAvatar</c> — the Play Mode prebuild in <see cref="ApaPlayModeScenePrebuild"/> and
    /// NDMF's own manual build — runs every phase in place without invoking a single preprocess callback. On those
    /// paths this pass is what keeps the processed avatar free of authoring-only components.
    /// </para>
    /// <para>
    /// <b>Only the component is removed, never the object.</b> The installer's GameObject can still carry the
    /// author's bones, children, colliders, or prefab-instance data, and the generated mesh may be skinned to
    /// bones that live there. Destroying the component is the whole change; pruning the objects is
    /// <see cref="ApaEmptySourceCleanupPass"/>'s job and it applies its own, stricter predicate.
    /// </para>
    /// <para>
    /// <b>The walk is the build's own walk.</b> <see cref="ContextBuilder.CollectAllInstallers"/> applies the
    /// avatar-ownership boundary (a nested avatar's installers belong to that avatar's build) and includes
    /// inactive components, which is exactly the set the client would otherwise see. Nothing is re-derived from a
    /// hierarchy scan with different rules.
    /// </para>
    /// <para>
    /// <b>It is a public seam on purpose.</b> The removal is the part of this feature a caller can exercise
    /// without standing up an NDMF build, so the pass and a test both call the same method rather than one of
    /// them re-implementing the walk.
    /// </para>
    /// </remarks>
    public static class ApaInstallerCleanup
    {
        /// <summary>
        /// Destroys every installer component under an avatar root and returns the components it removed.
        /// </summary>
        /// <param name="avatarRoot">The build clone's root. May be null, in which case nothing is removed.</param>
        /// <remarks>
        /// The returned list is an identity record of what was destroyed: a caller may count it or report
        /// against it, but it must not dereference the entries. The caller decides whether the build is allowed
        /// to be mutated at all — the pass checks <c>BuildContext.Successful</c> before calling this.
        /// </remarks>
        public static List<AvatarPartInstaller> RemoveAll(GameObject avatarRoot)
        {
            var removed = new List<AvatarPartInstaller>();
            if (avatarRoot == null) return removed;

            var installers = ContextBuilder.CollectAllInstallers(avatarRoot);
            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                if (installer == null) continue;

                // A component a run already consumed compares equal to null here and is skipped, so the removal
                // is idempotent for the ordinary "everything was assembled" build.
                Object.DestroyImmediate(installer);
                removed.Add(installer);
            }

            return removed;
        }
    }

    /// <summary>
    /// The pass that runs <see cref="ApaInstallerCleanup"/> after a successful build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failed build keeps its evidence.</b> <c>BuildContext.Successful</c> is checked first and the pass
    /// returns immediately when it is false, so a build that already reported an error is not mutated — removing
    /// components from a failed clone would only remove evidence from the report. The pass is also ordered after
    /// <see cref="ApaEmptySourceCleanupPass"/>, which needs the consumed source objects to still exist while it
    /// decides whether they are empty.
    /// </para>
    /// <para>
    /// The report is one informational line for the whole avatar: the author does not need one entry per parked
    /// part to understand that the component does not travel to the client. Its detail carries the stable
    /// <c>reason=installer-removed-from-build</c> token and the count, so a report, a test, and a log stay
    /// comparable.
    /// </para>
    /// </remarks>
    internal sealed class ApaInstallerCleanupPass : Pass<ApaInstallerCleanupPass>
    {
        /// <inheritdoc />
        /// <remarks>
        /// Resolved when NDMF draws the build report, so the pass name follows the selected language.
        /// </remarks>
        public override string DisplayName => ApaLocalization.Tr("Remove remaining APA installers from the build");

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var avatarRoot = context.AvatarRootObject;
            if (avatarRoot == null) return;

            // The report is the gate, exactly as it is for the assembly and the empty-source cleanup: a build
            // that already failed is not mutated.
            if (!context.Successful) return;

            var installers = ContextBuilder.CollectAllInstallers(avatarRoot);
            if (installers.Count == 0) return;

            // The references are resolved while every installer is still alive: NDMF asks an object for its path
            // when it resolves a reference, and a destroyed component can no longer be asked. See
            // ApaNdmfDiagnostics.CaptureReferences.
            var references = ApaNdmfDiagnostics.CaptureReferences(avatarRoot, installers, context.ObjectRegistry);

            var removed = ApaInstallerCleanup.RemoveAll(avatarRoot);
            if (removed.Count == 0) return;

            ApaNdmfDiagnostics.Report(
                new List<ValidationIssue>
                {
                    ValidationIssue.Info(
                        ApaErrorCode.InstallerRemovedFromBuild,
                        ApaIssuePhase.Configuration,
                        "Removed " + removed.Count + " AvatarPartInstaller component(s) from the build clone. The " +
                        "component is authoring data: the VRChat client cannot use it and would remove it with a " +
                        "warning. Only the build clone is touched; the scene and every prefab are unchanged.",
                        detail: "reason=installer-removed-from-build; count=" + removed.Count)
                },
                references);
        }
    }
}

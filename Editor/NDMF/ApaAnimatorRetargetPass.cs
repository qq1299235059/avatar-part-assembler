using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Points the animation paths recorded for a consumed part renderer at the target renderer that replaced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem this solves.</b> A part prefab may carry a Modular Avatar <i>Merge Animator</i> — typically
    /// in Relative path mode on the part root — whose controller animates the part renderer, most often its blend
    /// shapes. Modular Avatar virtualizes that controller and prefixes every recorded path with the part root's
    /// avatar-relative path, so the clip addresses the part renderer by the path it had when the animator services
    /// context was opened. APA then assembles that renderer's geometry into the target body renderer and consumes
    /// the part renderer. Without this step the animation would keep addressing an object the assembly replaced,
    /// and the blend shape would stop responding.
    /// </para>
    /// <para>
    /// <b>The mapping is registered, not written into the clip.</b> The pass calls NDMF's
    /// <see cref="ObjectPathRemapper.ReplaceObject(UnityEngine.GameObject, UnityEngine.GameObject)"/> once per
    /// recorded pair. NDMF transfers every virtual path recorded for the source object to the target object, and
    /// when <see cref="AnimatorServicesContext"/> deactivates it commits those mappings through
    /// <c>AnimationIndex.RewritePaths</c>, which is the same mechanism Modular Avatar itself uses when it merges a
    /// bone away (<c>MeshRetargeter</c>). Nothing here edits a serialized animation curve, touches a controller
    /// asset, or changes a Modular Avatar file: the build owns no animation data, and a second, hand-rolled
    /// rewriter would be a second answer to "which paths moved".
    /// </para>
    /// <para>
    /// <b>Timing is the whole contract, and it is why this is a separate pass.</b> The mappings must be registered
    /// after Modular Avatar has discovered and prefixed the virtual animation paths, and before the animator
    /// services context commits them. Modular Avatar's own Transforming sequence opens the context, processes the
    /// Merge Animator and Merge Armature components, and then closes it in the same sequence — its later passes do
    /// not require the context — so by the time APA's Transforming sequence runs (it is ordered after Modular
    /// Avatar's plugin end) the context is already closed and the paths of that activation are committed. This
    /// pass therefore <i>requires</i> the context: NDMF opens it before the pass executes, the pass registers the
    /// pairs against the fresh snapshot of the current hierarchy, and that activation's own deactivation commits
    /// them. Reopening the context is a supported NDMF flow — Modular Avatar itself opens it once in Resolving and
    /// again in Transforming — and the committed controllers are reused rather than re-created.
    /// </para>
    /// <para>
    /// <b>It does not need the part renderer component to be alive.</b> The pairs are GameObject identities
    /// captured before the renderer components were destroyed (<see cref="ApaBuildResult.RetargetMappings"/>), so
    /// the mapping survives consumption. A pair whose two objects are missing, or which names the same object
    /// twice, is skipped: it is not a mapping, and the remapper would have nothing to move.
    /// </para>
    /// <para>
    /// <b>No animator services, no change.</b> An avatar whose build never assembled anything has no recorded
    /// pair, and the pass returns before it reads the extension, so an avatar APA did not touch is not affected.
    /// A build that has already failed is not mutated either: <c>BuildContext.Successful</c> is checked first, and
    /// an error reported by any plugin blocks the registration exactly like it blocks the assembly.
    /// </para>
    /// <para>
    /// <b>Unrelated paths are untouched.</b> Only the objects this run consumed are mapped, and only to their own
    /// group's target renderer; no other object, clip, layer, or target renderer is named.
    /// </para>
    /// </remarks>
    internal sealed class ApaAnimatorRetargetPass : Pass<ApaAnimatorRetargetPass>
    {
        /// <inheritdoc />
        /// <remarks>
        /// Resolved when NDMF draws the build report, so the pass name follows the selected language.
        /// </remarks>
        public override string DisplayName => ApaLocalization.Tr("Retarget consumed part animation onto the target renderer");

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var avatarRoot = context.AvatarRootObject;
            if (avatarRoot == null) return;

            // The report is the gate, exactly as it is for the assembly pass: a build that already failed is not
            // mutated, and registering a path mapping on a failed build would only make the report harder to read.
            if (!context.Successful) return;

            var replacements = ApaTransientArtifacts.For(context).RendererReplacements;
            if (replacements.Count == 0) return;

            // The context is open for this pass: the plugin declares it as a required extension, so NDMF activates
            // it (and its VirtualControllerContext dependency) before this method runs, and deactivates it — which
            // is what commits the mappings — when the phase ends or the next pass that does not require it runs.
            var remapper = context.Extension<AnimatorServicesContext>().ObjectPathRemapper;

            for (var i = 0; i < replacements.Count; i++)
            {
                var source = replacements[i].Source;
                var target = replacements[i].Target;

                // A destroyed source object compares equal to null, and an unresolved target is not a mapping.
                // Registering either would ask the remapper to move paths onto nothing.
                if (source == null || target == null) continue;

                // The source and the target are different objects by construction; the guard is kept because
                // ReplaceObject with the same object on both sides would be a no-op that reads like a mapping.
                if (source == target) continue;

                remapper.ReplaceObject(source, target);
            }
        }
    }
}

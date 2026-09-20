using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Removes the objects the assembly consumed a part renderer from when the run left them genuinely empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it removes, and what it must not.</b> The assembly consumes a part renderer by destroying its
    /// component, and only its component: bones, children, and every other object under the part root survive
    /// because the generated mesh may be skinned to them. That leaves the renderer's own object behind, often
    /// carrying nothing but a Transform — a dead object in the uploaded avatar. This pass removes exactly those
    /// objects, and only those: <see cref="ApaSourceObjectCleanup.IsEmptySourceObject"/> is the whole decision, so
    /// an object that still carries a child, a constraint, a PhysBone, an authoring component, or a broken script
    /// reference is kept.
    /// </para>
    /// <para>
    /// <b>Why it runs last in the phase.</b> Two ordering constraints, both load-bearing. It runs after
    /// <see cref="ApaAnimatorRetargetPass"/>, because the retarget step needs the source objects alive to hand
    /// their recorded paths to NDMF's object path remapper — removing an object first would leave the mapping with
    /// nothing to read. And it runs after Modular Avatar's late transform stages
    /// (<c>nadena.dev.modular-avatar.late-transform-stages</c>), which purge every remaining Modular Avatar
    /// component from the avatar. Without that ordering a leftover Modular Avatar component — a
    /// <c>ModularAvatarMergeAnimator</c> on a part root, for instance — would make the object non-empty and it
    /// would survive even though the plugin that owned the component was about to delete it. The cleanup runs
    /// after the purge, so it sees the object as it will actually be uploaded.
    /// </para>
    /// <para>
    /// <b>The list is this build's own record.</b> The candidates are the source objects the assembly pass
    /// recorded in the transient build state from <see cref="ApaBuildResult.RetargetMappings"/>; nothing is
    /// re-derived from the hierarchy, and no global or static state is involved. An avatar APA did not assemble
    /// has an empty record and is not walked at all.
    /// </para>
    /// <para>
    /// <b>A failed build is not pruned.</b> <c>BuildContext.Successful</c> is checked first: an error reported by
    /// any plugin stops the removal, because deleting objects from a clone whose build has already failed would
    /// only remove evidence from the report.
    /// </para>
    /// </remarks>
    internal sealed class ApaEmptySourceCleanupPass : Pass<ApaEmptySourceCleanupPass>
    {
        /// <inheritdoc />
        /// <remarks>
        /// Resolved when NDMF draws the build report, so the pass name follows the selected language.
        /// </remarks>
        public override string DisplayName => ApaLocalization.Tr("Remove consumed part objects left empty");

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var avatarRoot = context.AvatarRootObject;
            if (avatarRoot == null) return;

            // The report is the gate, exactly as it is for the assembly pass: a build that already failed is not
            // mutated.
            if (!context.Successful) return;

            var replacements = ApaTransientArtifacts.For(context).RendererReplacements;

            for (var i = 0; i < replacements.Count; i++)
            {
                var source = replacements[i].Source;

                // A destroyed (already removed) source compares equal to null and is refused by the predicate, so
                // a repeated entry is harmless.
                if (!ApaSourceObjectCleanup.IsEmptySourceObject(source, avatarRoot)) continue;

                Object.DestroyImmediate(source);
            }
        }
    }
}

using System.Collections.Generic;
using AvatarPartAssembler.Editor.Integration;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// The transient components the Generating pass created for this build, so their consumption and their
    /// post-merge effect can both be verified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Generating pass creates one Modular Avatar merge-armature configuration per part that needs one (section
    /// 24). Modular Avatar destroys every configuration it reads while merging
    /// (<c>MergeArmatureHook.TopoProcessMergeArmatures</c>), including the ones it skipped because the target no
    /// longer resolved, so a configuration that is still alive when the assembly pass runs means the merging pass
    /// did not run at all — Modular Avatar's plugin or its merging pass is disabled, or its pass did not run for
    /// this build.
    /// </para>
    /// <para>
    /// <b>Destruction is necessary but not sufficient.</b> A skipped merge destroys its configuration exactly
    /// like a successful one, so the assembly pass also checks the postcondition the plan recorded: the part's
    /// top bone must have left every part root (see <see cref="MergeArmatureGenerator.IsMergeApplied"/>).
    /// Assembling otherwise would ship a part whose bones are not part of the avatar's armature, which looks
    /// correct at rest and detaches the moment the avatar animates.
    /// </para>
    /// <para>
    /// The state travels in the NDMF build context (<see cref="BuildContext.GetState{T}()"/>), which is the
    /// object every pass of one avatar build shares. A preview run builds its own context, so nothing leaks
    /// between runs.
    /// </para>
    /// <para>
    /// <b>An empty record is not proof that the pass ran.</b> <see cref="Configured"/> records that the
    /// Generating pass executed; the assembly pass blocks a build in which it did not, because the empty
    /// configuration list would otherwise satisfy the postcondition trivially. See <see cref="Configured"/>.
    /// </para>
    /// <para>
    /// <b>The same state carries what the assembly consumed.</b> <see cref="RendererReplacements"/> records the
    /// consumed part renderer objects and their group's target renderer objects, captured before the renderer
    /// components were destroyed. The steps that run after the assembly — retargeting recorded animation paths
    /// onto the target renderer and removing a source object left empty — read that ledger instead of re-deriving
    /// the mapping from a hierarchy the run has already rewritten.
    /// </para>
    /// </remarks>
    internal sealed class ApaTransientArtifacts
    {
        private readonly List<Component> _mergeConfigurations = new List<Component>();
        private readonly List<string> _partIds = new List<string>();
        private readonly List<Transform> _partTopBones = new List<Transform>();
        private readonly List<ApaRendererReplacement> _rendererReplacements = new List<ApaRendererReplacement>();

        /// <summary>
        /// True once the Generating pass has run far enough to declare that it configured this build, whether or
        /// not it recorded a single configuration.
        /// </summary>
        /// <remarks>
        /// <b>An empty record is two different facts.</b> "The Generating pass ran and no part needed a merge
        /// configuration" is a legitimate build with nothing to verify; "the Generating pass never ran" is a
        /// build in which no part's bones were merged, and reading the empty record as success would assemble
        /// parts whose skeletons were never merged — they look correct at rest and detach the moment the avatar
        /// animates. The assembly pass therefore asks this flag, not the list, before it treats the empty record
        /// as a pass. <see cref="ApaMergeArmaturePass"/> sets it as soon as it starts configuring.
        /// </remarks>
        internal bool Configured { get; private set; }

        /// <summary>The artifacts recorded for this build, created on first use.</summary>
        internal static ApaTransientArtifacts For(BuildContext context)
        {
            return context.GetState<ApaTransientArtifacts>();
        }

        /// <summary>
        /// Declares that the Generating pass ran for this build and is about to record what it creates.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="ApaMergeArmaturePass"/> before it plans any part: the flag means "the pass that
        /// creates the merge configurations executed", not "a configuration exists".
        /// </remarks>
        internal void MarkConfigured()
        {
            Configured = true;
        }

        /// <summary>
        /// Records a configuration the Generating pass created for a part, and the bone the merge has to move.
        /// </summary>
        /// <remarks>
        /// The bone is captured as the live transform the plan resolved, not as a path: Modular Avatar reparents
        /// or removes it, so the identity is what proves the merge happened while a path would have to be
        /// re-resolved in a hierarchy the merge has since rewritten.
        /// </remarks>
        internal void Record(Component configuration, string partId, Transform partTopBone)
        {
            if (configuration == null) return;

            _mergeConfigurations.Add(configuration);
            _partIds.Add(partId ?? string.Empty);
            _partTopBones.Add(partTopBone);
        }

        /// <summary>
        /// The consumed part renderer objects and the target renderer objects they were assembled into.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Written by the assembly pass from <see cref="ApaBuildResult.RetargetMappings"/> after a successful run,
        /// and read by the two steps that run later in the same build: the animator-retarget step, which hands
        /// each pair to NDMF's object path remapper while the animator services context is live, and the cleanup
        /// step, which removes a source object the run left empty. The list is empty on every other path, so a
        /// build that assembled nothing maps nothing and removes nothing.
        /// </para>
        /// <para>
        /// The state travels in the NDMF build context like every other artifact here, so it is per build and
        /// cannot leak into another avatar's build or into a preview run.
        /// </para>
        /// </remarks>
        internal IReadOnlyList<ApaRendererReplacement> RendererReplacements => _rendererReplacements;

        /// <summary>
        /// Records the source→target renderer object pairs a successful assembly produced.
        /// </summary>
        /// <remarks>
        /// A null entry, or an entry with no source object, is dropped: it names nothing that could be retargeted
        /// or removed. The list itself is already deduplicated by source object and ordered by the processor, so
        /// this method preserves the order it is given rather than sorting again.
        /// </remarks>
        internal void RecordRendererReplacements(IReadOnlyList<ApaRendererReplacement> replacements)
        {
            if (replacements == null) return;

            for (var i = 0; i < replacements.Count; i++)
            {
                var replacement = replacements[i];
                if (replacement == null || replacement.Source == null) continue;

                _rendererReplacements.Add(replacement);
            }
        }

        /// <summary>
        /// Adds one blocking diagnostic per recorded part whose merge cannot be shown to have actually happened.
        /// </summary>
        /// <param name="issues">Receives the diagnostics.</param>
        /// <param name="partRoots">
        /// Every part root in this build, so a bone that stayed under any of them — not only under its own part
        /// root — is caught.
        /// </param>
        internal void CollectUnapplied(List<ValidationIssue> issues, IReadOnlyList<GameObject> partRoots)
        {
            if (issues == null) return;

            for (var i = 0; i < _mergeConfigurations.Count; i++)
            {
                var partId = _partIds[i];
                var where = string.IsNullOrEmpty(partId) ? "a part" : "part '" + partId + "'";

                // A destroyed Unity object compares equal to null; that is the consumed case. It only proves
                // Modular Avatar read the component, never that it merged anything, which is why the
                // postcondition below is checked as well.
                if (_mergeConfigurations[i] != null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetBoneNotFound,
                        ApaIssuePhase.Compatibility,
                        "The transient merge-armature configuration created for " + where + " was not consumed, so " +
                        "the part's bones were never merged into the avatar's armature. Modular Avatar's merging " +
                        "pass did not run for this build; enable the Modular Avatar plugin and build again.",
                        partId,
                        detail: "reason=merge-not-consumed"));
                    continue;
                }

                if (MergeArmatureGenerator.IsMergeApplied(_partTopBones[i], partRoots)) continue;

                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetBoneNotFound,
                    ApaIssuePhase.Compatibility,
                    "The transient merge-armature configuration created for " + where + " was consumed, but the " +
                    "part's top bone still lives under the part root, so the skeleton was never moved into the " +
                    "avatar's armature. Modular Avatar destroys a configuration it could not merge — for example " +
                    "when another Transforming pass has removed or replaced the derived merge target — and " +
                    "assembling now would ship a part whose bones do not follow the animated skeleton. Check " +
                    "whether the merge target still resolves and whether the part's bone names still match the " +
                    "avatar's.",
                    partId,
                    detail: "reason=merge-not-applied"));
            }
        }
    }
}

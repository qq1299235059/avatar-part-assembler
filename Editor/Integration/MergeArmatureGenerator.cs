using System;
using System.Collections.Generic;
using UnityEngine;
using nadena.dev.modular_avatar.core;

namespace AvatarPartAssembler.Editor.Integration
{
    /// <summary>
    /// Where a part's transient merge-armature configuration belongs: the object the component is created on,
    /// and the object it merges into.
    /// </summary>
    /// <remarks>
    /// A planned merge is not yet a component. Planning is a pure decision over the current hierarchy, so the
    /// caller can decide that the configuration is needed, create it, and report nothing when it is not needed —
    /// rather than creating a component and then discovering it was wrong.
    /// </remarks>
    public readonly struct MergeArmaturePlan
    {
        /// <summary>The object the merge component is added to. Its children are the mergable bone level.</summary>
        public GameObject MergeRoot { get; }

        /// <summary>The object inside the avatar that the part merges into.</summary>
        public GameObject TargetArmature { get; }

        /// <summary>
        /// The part's highest bone at plan time: the bone Modular Avatar reparents when it merges this part.
        /// </summary>
        /// <remarks>
        /// The identity the post-merge check is anchored on. A real merge moves this bone out of the part (into
        /// the avatar's armature, or away with the proxy hierarchy); a merge that Modular Avatar skipped leaves
        /// it exactly where it was, under the part root. See
        /// <see cref="MergeArmatureGenerator.IsMergeApplied"/>.
        /// </remarks>
        public Transform PartTopBone { get; }

        /// <summary>Creates a plan.</summary>
        public MergeArmaturePlan(GameObject mergeRoot, GameObject targetArmature)
            : this(mergeRoot, targetArmature, null)
        {
        }

        /// <summary>Creates a plan that also records the part bone the merge will move.</summary>
        public MergeArmaturePlan(GameObject mergeRoot, GameObject targetArmature, Transform partTopBone)
        {
            MergeRoot = mergeRoot;
            TargetArmature = targetArmature;
            PartTopBone = partTopBone;
        }

        /// <summary>True when both ends of the configuration were resolved.</summary>
        public bool IsValid => MergeRoot != null && TargetArmature != null;
    }

    /// <summary>
    /// Everything the merge decision reads, in one value.
    /// </summary>
    /// <remarks>
    /// A request shape rather than seven positional parameters: every field is read once by the planner, and a
    /// caller that omits one (an empty <see cref="PartId"/>, a null <see cref="Bones"/>) gets the documented
    /// default instead of an argument-order mistake.
    /// </remarks>
    public sealed class MergeArmatureRequest
    {
        /// <summary>The avatar root of the build clone.</summary>
        public GameObject AvatarRoot { get; }

        /// <summary>The part root of the installer being processed.</summary>
        public GameObject PartRoot { get; }

        /// <summary>Stable part identity, used only to attribute diagnostics.</summary>
        public string PartId { get; }

        /// <summary>The profile's bone configuration, or null when the profile has none.</summary>
        public ApaBoneProfile Bones { get; }

        /// <summary>
        /// Every part root in this build. Bones under these objects are part bones, never avatar bones, so they
        /// are excluded when the avatar skeleton is searched.
        /// </summary>
        public IReadOnlyList<GameObject> PartRoots { get; }

        /// <summary>Creates a request.</summary>
        public MergeArmatureRequest(
            GameObject avatarRoot,
            GameObject partRoot,
            string partId,
            ApaBoneProfile bones,
            IReadOnlyList<GameObject> partRoots)
        {
            AvatarRoot = avatarRoot;
            PartRoot = partRoot;
            PartId = partId ?? string.Empty;
            Bones = bones;
            PartRoots = partRoots ?? Array.Empty<GameObject>();
        }
    }

    /// <summary>
    /// Decides and creates the transient Modular Avatar merge-armature configuration for installed parts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The specification requires that an end user never has to configure Modular Avatar by hand (section 5), so
    /// at build time the assembler creates the merge configuration each part profile asks for and Modular Avatar
    /// consumes it (section 19, section 24).
    /// </para>
    /// <para>
    /// <b>This type is the only place in the package that names a Modular Avatar type.</b> Every member it uses
    /// is recorded in <c>Third Party Notices.md</c>, so a dependency upgrade has one file to review; the NDMF
    /// layer talks to Modular Avatar exclusively through this type.
    /// </para>
    /// <para>
    /// <b>Every mutation is transient.</b> The caller passes the NDMF build clone, never the authoring avatar,
    /// and the component is created without recording undo state. Nothing here writes to an authoring asset, and
    /// nothing here assigns a generated object to one.
    /// </para>
    /// <para>
    /// <b>Planning is deterministic and refusals are explicit.</b> The part's skeleton, the merge root, and the
    /// merge target come from the profile's two explicit armature selections compared with ordinal paths; a
    /// configuration that cannot be resolved is reported as a blocking diagnostic instead of being derived from
    /// bone names, hierarchy shape, or the legacy merge path.
    /// </para>
    /// </remarks>
    public static class MergeArmatureGenerator
    {
        /// <summary>
        /// Creates a merge-armature component on the selected part armature, targeting the selected target
        /// armature.
        /// </summary>
        /// <param name="partArmatureRoot">
        /// The part armature the author selected. The component is added here, and Modular Avatar matches its
        /// children against the target armature's children — which is exactly the relation the two selections
        /// describe.
        /// </param>
        /// <param name="targetArmature">The target armature the author selected.</param>
        /// <param name="issues">Receives diagnostics when the configuration cannot be produced.</param>
        /// <remarks>
        /// <para>
        /// <b>No name policy is applied (M10).</b> The component is written with an empty <c>prefix</c> and
        /// <c>suffix</c> and inference is never attempted, so Modular Avatar merges by exact bone name. That is
        /// deliberate rather than a simplification: the pipeline's own identity is the bone's path relative to
        /// its selected armature, and a rewritten name could match two bones whose relative paths differ — that
        /// is, a bone the pipeline considers a different joint. Exact-name matching over two corresponding
        /// armature roots is the one Modular Avatar rule that agrees with the armature-relative identity.
        /// </para>
        /// <para>
        /// The parameters are the two objects the author picked, not a derived or inferred pair: the profile's
        /// legacy <c>MergeTargetPath</c> and name policy are ignored, because a second statement about which bone
        /// is which joint is exactly the guessing M10 removes.
        /// </para>
        /// </remarks>
        /// <returns>The created component, or null when it could not be created. Callers that need the concrete
        /// type cast it to <c>ModularAvatarMergeArmature</c>.</returns>
        public static Component CreateMergeConfiguration(
            GameObject partArmatureRoot,
            GameObject targetArmature,
            List<ValidationIssue> issues)
        {
            if (issues == null) issues = new List<ValidationIssue>();

            if (partArmatureRoot == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Configuration,
                    "Cannot create a merge armature configuration without a part armature root.",
                    detail: "reason=null-part-armature"));
                return null;
            }

            if (targetArmature == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Configuration,
                    "Cannot create a merge armature configuration without a target armature.",
                    detail: "reason=null-target-armature"));
                return null;
            }

            var merge = partArmatureRoot.AddComponent<ModularAvatarMergeArmature>();

            // mergeTarget is a public field initialized to an empty AvatarObjectReference by the component's
            // field initializer, but a component that arrived through deserialization may carry a null one.
            if (merge.mergeTarget == null) merge.mergeTarget = new AvatarObjectReference();
            merge.mergeTarget.Set(targetArmature);

            // Exact-name matching, always. See the method remarks: a prefix, a suffix, or an inferred mapping
            // would rewrite the names Modular Avatar matches on, and the pipeline's identity is the
            // armature-relative path rather than the name.
            merge.prefix = string.Empty;
            merge.suffix = string.Empty;

            // The declared lock mode. The default is Legacy, which Modular Avatar migrates in OnEnable --
            // AddComponent runs OnEnable immediately, before mergeTarget exists -- so the lock controller that
            // OnEnable constructs is fed the migrated default, cannot build a bone mapping (GetBonesMapping
            // returns null while mergeTarget is unset), and therefore registers no lock job. Writing the field
            // afterwards does not re-run that lifecycle: ModularAvatarMergeArmature.SetLockMode() and
            // ResetArmatureLock(), the only members that would push a new value into the lock controller, are
            // internal to Modular Avatar, and ArmatureLockController itself is an internal type, so there is no
            // public path to it at all. NotLocked is therefore the value this component *declares* (for the
            // inspector and for any future Modular Avatar version that re-reads it); the build relies on the
            // lifecycle facts above, and on Modular Avatar destroying this transient component (and thereby
            // disposing its controller in OnDestroy) during the same pass. The value is the one the assembler
            // wants: a transient build-time configuration must never drive the part's proxy hierarchy from the
            // editor-time bone lock.
            merge.LockMode = ArmatureLockMode.NotLocked;

            return merge;
        }

        /// <summary>
        /// True when this build has the Modular Avatar API available.
        /// </summary>
        /// <remarks>The package declares Modular Avatar as a required VPM dependency.</remarks>
        public static bool IsModularAvatarAvailable => true;

        /// <summary>
        /// The lock mode a created configuration declares, as the installed Modular Avatar enum's name, or
        /// <c>null</c> when <paramref name="configuration"/> is not a merge configuration.
        /// </summary>
        /// <remarks>
        /// A seam for callers that have no Modular Avatar reference of their own (the tests, and a diagnostic
        /// that wants to report the declared value without naming the component type). It reports what the
        /// component declares, not what Modular Avatar's internal lock controller holds; see
        /// <see cref="CreateMergeConfiguration"/> for why the two can differ in 1.18.0-beta.0.
        /// </remarks>
        public static string GetDeclaredLockModeName(Component configuration)
        {
            var merge = configuration as ModularAvatarMergeArmature;
            return merge != null ? merge.LockMode.ToString() : null;
        }

        /// <summary>
        /// The merge-name policy a created configuration declares, as <c>prefix='…'; suffix='…'</c>, or
        /// <c>null</c> when <paramref name="configuration"/> is not a merge configuration.
        /// </summary>
        /// <remarks>
        /// A seam for callers with no Modular Avatar reference of their own (the tests, and a diagnostic that
        /// wants to report the applied names without naming the component type). It reads the values the build
        /// will actually use, after <see cref="CreateMergeConfiguration"/> applied the profile's policy, so a
        /// test can assert that <c>InferMergeNames</c> or an explicit prefix/suffix reached the configuration.
        /// </remarks>
        public static string DescribeDeclaredMergeNames(Component configuration)
        {
            var merge = configuration as ModularAvatarMergeArmature;
            if (merge == null) return null;

            return "prefix='" + merge.prefix + "'; suffix='" + merge.suffix + "'";
        }

        /// <summary>
        /// True when this part already carries a merge configuration that merges its own skeleton.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An author-provided configuration always wins: the plugin promises that no manual Modular Avatar setup
        /// is <i>required</i>, not that a manual one is overwritten. The check also makes the generating pass
        /// idempotent, so a preview that re-runs it cannot stack two merges on one part.
        /// </para>
        /// <para>
        /// <b>Coverage is structural, not positional.</b> Modular Avatar merges the <i>children of the
        /// component's own object</i> against the target's children, and skips a subtree that carries its own
        /// configuration (<c>MergeArmatureHook.RecursiveMerge</c>;
        /// <c>ModularAvatarMergeArmature.GetBonesMapping</c>). A configuration therefore covers this part only
        /// while its object is an ancestor of the part's highest bone: the part's merge root, or an object above
        /// that root but still enclosing the skeleton. A configuration on a bone <i>below</i> the highest bone
        /// merges only that bone's children and leaves the part's top bone unmerged, and a configuration on an
        /// unrelated ancestor or descendant (a sibling sub-part's own merge) merges something else entirely.
        /// Neither may suppress the configuration this part needs, which is why the search below is filtered by
        /// <see cref="IsSkeletonCoveredBy"/> instead of accepting any component on the path.
        /// </para>
        /// <para>
        /// A part with no skeleton of its own needs no configuration and reports as not covered; the planner
        /// refuses it for the same reason (<see cref="TryFindPartSkeleton"/>).
        /// </para>
        /// </remarks>
        public static bool IsConfigured(GameObject partRoot)
        {
            if (partRoot == null) return false;
            if (!TryFindPartSkeleton(partRoot, out var highestBone, out _)) return false;

            return IsSkeletonCoveredBy(CollectConfigurationObjects(partRoot), highestBone);
        }

        /// <summary>
        /// True when one of the given configuration objects merges the skeleton whose highest bone is given.
        /// </summary>
        /// <remarks>
        /// The structural rule <see cref="IsConfigured"/> is built on, expressed without a Modular Avatar type so
        /// that a test — or any caller with a configuration object rather than a component — can assert it
        /// directly. A configuration object covers the skeleton exactly when it is a strict ancestor of the
        /// highest bone: Modular Avatar matches the component object's <i>children</i>, so the component object
        /// itself is the merge level and its child is the first bone merged.
        /// </remarks>
        /// <param name="configurationObjects">Objects that carry an existing configuration. May be null.</param>
        /// <param name="partTopBone">The part's highest bone, as <see cref="TryFindPartSkeleton"/> returns it.</param>
        public static bool IsSkeletonCoveredBy(
            IReadOnlyList<GameObject> configurationObjects,
            Transform partTopBone)
        {
            if (configurationObjects == null || partTopBone == null) return false;

            for (var i = 0; i < configurationObjects.Count; i++)
            {
                var configurationObject = configurationObjects[i];
                if (configurationObject == null) continue;

                var configuration = configurationObject.transform;
                if (configuration == partTopBone) continue;
                if (partTopBone.IsChildOf(configuration)) return true;
            }

            return false;
        }

        /// <summary>
        /// The objects carrying an existing merge configuration on, above, or below the part root, deduplicated.
        /// </summary>
        /// <remarks>
        /// The union of the two searches is the only region a configuration that could cover this part can live
        /// in: the part root's own skeleton is underneath it, so an object that encloses the highest bone is
        /// either the part root, one of its ancestors, or one of its descendants.
        /// </remarks>
        private static List<GameObject> CollectConfigurationObjects(GameObject partRoot)
        {
            var result = new List<GameObject>();

            var onPath = partRoot.GetComponentsInParent<ModularAvatarMergeArmature>(true);
            for (var i = 0; i < onPath.Length; i++)
            {
                if (onPath[i] == null) continue;
                if (result.Contains(onPath[i].gameObject)) continue;
                result.Add(onPath[i].gameObject);
            }

            var below = partRoot.GetComponentsInChildren<ModularAvatarMergeArmature>(true);
            for (var i = 0; i < below.Length; i++)
            {
                if (below[i] == null) continue;
                if (result.Contains(below[i].gameObject)) continue;
                result.Add(below[i].gameObject);
            }

            return result;
        }

        /// <summary>
        /// True when the part skeleton recorded at plan time has actually left every part root.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The post-merge postcondition, and the reason a destroyed merge component alone is not evidence that
        /// the merge happened: Modular Avatar destroys <i>every</i> configuration it enumerated, including the
        /// ones it skipped because the target no longer resolved
        /// (<c>MergeArmatureHook.TopoProcessMergeArmatures</c>). What a real merge leaves behind is a moved
        /// skeleton: the bone recorded by <see cref="MergeArmaturePlan.PartTopBone"/> is reparented under the
        /// target, so it no longer lives under any part root — or it is gone with the proxy hierarchy Modular
        /// Avatar prunes.
        /// </para>
        /// <para>
        /// Expressed without a Modular Avatar type and against plain hierarchy state, so it is asserted directly
        /// by the tests.
        /// </para>
        /// </remarks>
        /// <param name="partTopBone">The highest bone recorded when the configuration was planned.</param>
        /// <param name="partRoots">Every part root in this build.</param>
        public static bool IsMergeApplied(Transform partTopBone, IReadOnlyList<GameObject> partRoots)
        {
            // A destroyed bone is the merged case: the merge either reparented it into the avatar's armature or
            // removed it with the rest of the proxy hierarchy. This is a postcondition read on hierarchy state,
            // never an object handed to a diagnostic — see ApaNdmfDiagnostics for why that distinction matters.
            if (partTopBone == null) return true;

            return !IsUnderAny(partTopBone, partRoots);
        }

        /// <summary>
        /// Decides where the transient merge configuration for one part belongs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The decision answers four questions in order, and stops at the first one that says "no":
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// <b>Does the profile want a merge?</b> <see cref="ApaBoneProfile.MergeArmature"/> defaults to true;
        /// false means the author keeps the part's bones separate on purpose.
        /// </description></item>
        /// <item><description>
        /// <b>Does the part have a skeleton of its own?</b> Only bones that live under the part root can be
        /// merged. A part whose renderer is already skinned to the avatar's bones — or one that lives inside the
        /// avatar's armature — has nothing to merge, and no configuration is created.
        /// </description></item>
        /// <item><description>
        /// <b>Does an existing configuration already cover that skeleton?</b> Only structurally:
        /// <see cref="IsConfigured"/> counts a configuration whose own object encloses the part's highest bone,
        /// so a nested merge on a sub-bone does not suppress the one this part needs.
        /// </description></item>
        /// <item><description>
        /// <b>Are both armatures selected and resolvable?</b> The part armature (relative to the part root) and
        /// the target armature (relative to the avatar root) are the author's explicit selections. Neither is
        /// derived: since M10 the two selections are the statement about which part bone is which body joint, so
        /// a missing, unresolvable, or out-of-root selection is a blocking <c>APA043</c> rather than a guess from
        /// bone names or hierarchy shape.
        /// </description></item>
        /// </list>
        /// <para>
        /// A returned <c>false</c> therefore means either "nothing to do" or "reported a blocking diagnostic";
        /// the caller distinguishes the two by whether the issue list grew.
        /// </para>
        /// </remarks>
        /// <param name="request">The part, its profile configuration, and the avatar hierarchy.</param>
        /// <param name="issues">Receives blocking diagnostics when a required configuration cannot be derived.</param>
        /// <param name="plan">
        /// Receives the merge root, target, and the part's highest bone, or a default plan when nothing is planned.
        /// </param>
        /// <returns>True when a configuration should be created for this part.</returns>
        public static bool TryPlanMerge(
            MergeArmatureRequest request,
            List<ValidationIssue> issues,
            out MergeArmaturePlan plan)
        {
            if (issues == null) issues = new List<ValidationIssue>();
            plan = new MergeArmaturePlan(null, null);

            if (request == null || request.AvatarRoot == null || request.PartRoot == null) return false;

            // The profile can decline the merge outright.
            if (request.Bones != null && !request.Bones.MergeArmature) return false;

            // The skeleton decides both remaining questions, so it is resolved once: a part with no skeleton of
            // its own has nothing to merge, and only a configuration whose own object encloses the highest bone
            // covers the part (see IsConfigured for the structural rule). The merge root itself is the selected
            // part armature rather than the top bone's parent, so it is discarded here.
            if (!TryFindPartSkeleton(request.PartRoot, out var highestBone, out _)) return false;
            if (IsSkeletonCoveredBy(CollectConfigurationObjects(request.PartRoot), highestBone)) return false;

            // A part that already lives inside the avatar's armature is skinned to the avatar's own bones, or to
            // extra bones parented under them, so there is nothing to merge — and merging would be destructive:
            // Modular Avatar reparents the merge component's object, which here is an object of the avatar's own
            // skeleton. An explicit target path cannot override this; it asks for something that would restructure
            // the armature rather than merge a part into it.
            var armatureRoot = ResolveAvatarArmatureRoot(request.AvatarRoot, request.PartRoots);
            if (armatureRoot != null && IsSelfOrDescendant(request.PartRoot.transform, armatureRoot.transform))
            {
                return false;
            }

            // Both armatures are the author's selections, resolved against the roots they were recorded
            // against. Nothing is derived from bone names, hierarchy shape, or a legacy path: since M10 the two
            // selections *are* the statement about which bone is which joint, and the pipeline's identity (a
            // bone's path relative to its own selected armature) is exactly what Modular Avatar's exact-name
            // merge over two corresponding armature roots reproduces.
            var partArmaturePath = request.Bones != null ? request.Bones.PartArmaturePath : string.Empty;
            if (!ApaAvatarPath.HasIdentity(partArmaturePath))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "Part '" + request.PartId + "' has a skeleton of its own but its profile selects no part " +
                    "armature, so the merge configuration cannot be placed. Open the Part Authoring window, " +
                    "select the target armature inside the avatar and the part armature inside the part, then " +
                    "save the profile.",
                    request.PartId,
                    detail: "reason=missing-part-armature; partRoot=" + request.PartRoot.name));
                return false;
            }

            var partArmature = ResolveUnder(request.PartRoot, partArmaturePath, "part", request, issues);
            if (partArmature == null) return false;

            var targetArmaturePath = request.Bones != null ? request.Bones.TargetArmaturePath : string.Empty;
            if (!ApaAvatarPath.HasIdentity(targetArmaturePath))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "Part '" + request.PartId + "' selects no target armature, so there is nothing to merge its " +
                    "skeleton into. Open the Part Authoring window, select the target armature inside the avatar " +
                    "and the part armature inside the part, then save the profile.",
                    request.PartId,
                    detail: "reason=missing-target-armature; partRoot=" + request.PartRoot.name));
                return false;
            }

            var targetArmature = ResolveUnder(request.AvatarRoot, targetArmaturePath, "target", request, issues);
            if (targetArmature == null) return false;

            // The part's top bone must live under the selected part armature, or the component would be created
            // on an object whose children are not the bones being merged. This is the plan-time counterpart of
            // the capture-time APA044 check, reported here because this is the code that decides where the
            // component goes.
            if (!IsSelfOrDescendant(highestBone, partArmature.transform))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "The part's highest bone '" + highestBone.name + "' is not inside the selected part armature " +
                    "'" + partArmature.name + "'. Modular Avatar merges the selected armature's children, so the " +
                    "skeleton would not be merged. Select the armature that contains the part's bones.",
                    request.PartId,
                    detail: "reason=part-top-bone-outside-armature; bone=" + highestBone.name +
                            "; armature=" + partArmature.name));
                return false;
            }

            // Modular Avatar reports a merge whose target is the component's own object, or a child of it, as a
            // circular dependency and fails the build (ComponentValidation.CheckInternal). Refuse it here, where
            // the message can say which part and which object are involved, instead of letting Modular Avatar
            // report it without the part identity.
            if (targetArmature == partArmature
                || IsSelfOrDescendant(targetArmature.transform, partArmature.transform))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "The selected target armature '" + targetArmature.name + "' is the part's own armature root " +
                    "or a child of it, which Modular Avatar rejects as a circular dependency. Select the " +
                    "avatar's corresponding bone level as the target armature instead.",
                    request.PartId,
                    detail: "reason=target-armature-inside-part-armature; target=" + targetArmature.name));
                return false;
            }

            // A target anywhere else inside the part cannot merge anything: Modular Avatar matches the component's
            // children against the target's children, and the part's own objects are the ones being merged. The
            // merge would silently do nothing and the part's bones would stay detached from the body, so it is
            // refused rather than attempted. (A target that is an ancestor of the part — the avatar root, when the
            // avatar's skeleton hangs directly off it — is a different and legitimate case.)
            if (targetArmature == request.PartRoot
                || IsSelfOrDescendant(targetArmature.transform, request.PartRoot.transform))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "The selected target armature '" + targetArmature.name + "' is inside the part that is being " +
                    "merged, so merging it would match the part against itself and no bone would merge. Select " +
                    "the avatar's corresponding bone level as the target armature instead.",
                    request.PartId,
                    detail: "reason=merge-target-is-part; target=" + targetArmature.name));
                return false;
            }

            plan = new MergeArmaturePlan(partArmature, targetArmature, highestBone);
            return true;
        }

        /// <summary>
        /// Resolves a selected armature path under the root it was recorded against.
        /// </summary>
        /// <remarks>
        /// The same rule as <see cref="ResolvePath"/>, plus the strict ancestor check the selection requires: a
        /// path that resolves outside its root names an object the part (or the body) does not own, so it is
        /// refused rather than used. The empty path is handled by the caller, which reports the missing
        /// selection with its own wording.
        /// </remarks>
        private static GameObject ResolveUnder(
            GameObject root,
            string path,
            string side,
            MergeArmatureRequest request,
            List<ValidationIssue> issues)
        {
            if (root == null) return null;

            var found = ResolvePath(root, path);
            var transform = found != null ? found.transform : null;
            if (transform != null && (transform == root.transform || transform.IsChildOf(root.transform)))
            {
                return found;
            }

            issues.Add(ValidationIssue.Error(
                ApaErrorCode.ArmatureSelectionInvalid,
                ApaIssuePhase.Compatibility,
                "The " + side + " armature path '" + path + "' selected by part '" + request.PartId +
                "' no longer resolves to an object inside '" + root.name + "'. Re-select the armature in the " +
                "Part Authoring window; the hierarchy it was recorded against has changed.",
                request.PartId,
                detail: "reason=" + side + "-armature-not-found; path=" + path + "; root=" + root.name));
            return null;
        }

        /// <summary>
        /// Resolves an avatar-root-relative path to a live object, treating the root token as the avatar root.
        /// </summary>
        /// <remarks>
        /// The same rule the rest of the pipeline resolves captured paths with
        /// (<see cref="ApaAvatarPath"/>): <c>Transform.Find</c> reserves <c>"."</c>, so the token is handled
        /// explicitly, and the empty string is "missing" rather than a lookup.
        /// </remarks>
        public static GameObject ResolvePath(GameObject avatarRoot, string path)
        {
            if (avatarRoot == null || !ApaAvatarPath.HasIdentity(path)) return null;

            var transform = ApaAvatarPath.IsRoot(path)
                ? avatarRoot.transform
                : avatarRoot.transform.Find(path);

            return transform != null ? transform.gameObject : null;
        }

        /// <summary>
        /// The part's highest skinned bone and the object whose children Modular Avatar matches.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The renderer is the one the core reads geometry from — the first <see cref="Renderer"/> under the
        /// part root in hierarchy order, exactly as <c>ContextBuilder</c> selects it — so the skeleton planned
        /// here belongs to the geometry that is actually assembled.
        /// </para>
        /// <para>
        /// The <b>highest bone</b> is the first bone in the renderer's own bone order that has no ancestor
        /// inside the same bone list. The renderer's bone order is the deterministic order section 44.2 uses for
        /// the final bone table, so a part with several disjoint chains resolves to the first chain in that
        /// order instead of to whichever bone a dictionary happened to yield.
        /// </para>
        /// <para>
        /// The <b>merge root</b> is that bone's parent: Modular Avatar matches the merge component's children
        /// against the target's children, so the root must be the object that <i>contains</i> the part's top bone,
        /// and it must itself be inside the part root. For the prefab structure of section 5
        /// (<c>CyberArm_L/Armature/UpperArm_L</c>) the merge root is <c>Armature</c>; for a part whose bones hang
        /// directly off the part root it is the part root itself.
        /// </para>
        /// <para>
        /// <b>Known structural constraint.</b> Modular Avatar reparents everything under the merge root into the
        /// avatar's hierarchy, so a part renderer that hangs <i>inside</i> the part's own armature subtree moves
        /// with it and is no longer found under the part root when the assembly pass captures geometry. The
        /// prefab structure in section 5 keeps the mesh outside the armature (<c>CyberArm_L/Mesh</c> next to
        /// <c>CyberArm_L/Armature</c>) precisely so this cannot happen; a part that nests its renderer inside the
        /// armature is reported by the assembly pass as a part root with no renderer rather than assembled from
        /// the wrong object. Letting the capture step accept a renderer recorded before the merge would need an
        /// API change in <c>ContextBuilder</c>; the constraint and its rationale are recorded in the M3 run
        /// report.
        /// </para>
        /// </remarks>
        public static bool TryFindPartSkeleton(
            GameObject partRoot,
            out Transform highestBone,
            out GameObject mergeRoot)
        {
            highestBone = null;
            mergeRoot = null;
            if (partRoot == null) return false;

            var renderer = partRoot.GetComponentInChildren<Renderer>(true) as SkinnedMeshRenderer;
            if (renderer == null) return false;

            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) return false;

            for (var i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;

                // Only bones that live under the part root are the part's own skeleton. A bone of the avatar's
                // own armature is not merged: it already is the avatar's bone.
                if (!IsSelfOrDescendant(bone, partRoot.transform)) continue;

                var hasBoneAncestor = false;
                for (var j = 0; j < bones.Length; j++)
                {
                    var candidate = bones[j];
                    if (candidate == null || candidate == bone) continue;
                    if (IsSelfOrDescendant(bone, candidate))
                    {
                        hasBoneAncestor = true;
                        break;
                    }
                }

                if (hasBoneAncestor) continue;

                highestBone = bone;
                break;
            }

            if (highestBone == null) return false;

            var parent = highestBone.parent;
            if (parent == null) return false;
            if (!IsSelfOrDescendant(parent, partRoot.transform)) return false;

            mergeRoot = parent.gameObject;
            return true;
        }

        /// <summary>
        /// The object that owns the avatar's skeleton: the humanoid hips' parent when the avatar is humanoid,
        /// otherwise the highest ancestor of the first body renderer's root bone that is still below the avatar
        /// root.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This mirrors Modular Avatar's own setup rule. <c>SetupOutfit.SetupOutfitUI</c> merges an outfit
        /// armature into <c>avatarHips.transform.parent</c>, because a merge component's children are matched
        /// against the target's children and the hips are the armature root's child.
        /// </para>
        /// <para>
        /// Part renderers are excluded from the search: their root bone belongs to the part, not to the avatar,
        /// and using it would make a part the anchor of its own merge. The result is therefore only used as the
        /// proposal the authoring window offers and to recognize a part that already lives inside the armature;
        /// the merge target itself is the author's selection (M10), never this derivation.
        /// </para>
        /// </remarks>
        public static GameObject ResolveAvatarArmatureRoot(GameObject avatarRoot, IReadOnlyList<GameObject> partRoots)
        {
            if (avatarRoot == null) return null;

            var animator = avatarRoot.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null && hips.parent != null) return hips.parent.gameObject;
            }

            var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                if (IsUnderAny(renderer.transform, partRoots)) continue;

                var bone = renderer.rootBone;
                if (bone == null) continue;
                if (!IsSelfOrDescendant(bone, avatarRoot.transform)) continue;

                var top = bone;
                while (top.parent != null && top.parent != avatarRoot.transform) top = top.parent;
                return top.gameObject;
            }

            return null;
        }

        private static bool IsUnderAny(Transform transform, IReadOnlyList<GameObject> partRoots)
        {
            if (transform == null || partRoots == null) return false;

            for (var i = 0; i < partRoots.Count; i++)
            {
                var partRoot = partRoots[i];
                if (partRoot == null) continue;
                if (IsSelfOrDescendant(transform, partRoot.transform)) return true;
            }

            return false;
        }

        private static bool IsSelfOrDescendant(Transform transform, Transform ancestor)
        {
            if (transform == null || ancestor == null) return false;
            return transform == ancestor || transform.IsChildOf(ancestor);
        }
    }
}

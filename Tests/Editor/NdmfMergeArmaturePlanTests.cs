using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AvatarPartAssembler.Editor.Integration;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the decisions the NDMF Generating pass makes: where a part's transient Modular Avatar
    /// merge-armature configuration belongs, and when there is none to create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass itself is three lines of loop over <see cref="MergeArmatureGenerator"/>; the decisions are in the
    /// generator, which is why the tests target it directly. Every rule asserted here is a rule the build makes
    /// against a real hierarchy, so these tests build hierarchies rather than snapshots.
    /// </para>
    /// <para>
    /// <b>Both ends of a merge are the author's selections (M10).</b> The merge root is the selected part
    /// armature — whose children Modular Avatar matches — and the merge target is the selected target armature.
    /// Nothing is derived from bone names, hierarchy shape, or the legacy merge path any more, so a profile that
    /// selects neither armature blocks (<c>APA043</c>) instead of guessing, and these fixtures always state both.
    /// </para>
    /// <para>
    /// Three later fixes are pinned here as well: existing configuration coverage is structural relative to the
    /// part skeleton, a plan records the bone the merge has to move, and a created configuration declares the
    /// <c>NotLocked</c> mode. The first two are asserted through Modular-Avatar-free seams
    /// (<see cref="MergeArmatureGenerator.IsSkeletonCoveredBy"/>,
    /// <see cref="MergeArmatureGenerator.IsMergeApplied"/>), and the end-to-end coverage cases add the installed
    /// component by its type name, because this assembly cannot reference Modular Avatar.
    /// </para>
    /// </remarks>
    public sealed class NdmfMergeArmaturePlanTests
    {
        /// <summary>Objects created by a test, destroyed in reverse order in <see cref="TearDown"/>.</summary>
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        /// <summary>
        /// A part that carries a copy of the whole armature merges into the selected target armature, and the
        /// component is created on the selected part armature.
        /// </summary>
        [Test]
        public void Plan_WithFullArmatureCopy_MergesIntoTheSelectedArmatures()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberBody", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partHips = NewGameObject("Hips", partArmature.transform);
            var partSpine = NewGameObject("Spine", partHips.transform);
            NewSkinnedRenderer(partHost, "Body", partHips, partSpine);

            var planned = TryPlan(rig, partHost, Armatures("Armature", "Armature"), out var plan, out var issues);

            Assert.IsTrue(planned, Describe(issues));
            Assert.AreSame(partArmature, plan.MergeRoot,
                "The merge component belongs on the selected part armature.");
            Assert.AreSame(rig.Armature, plan.TargetArmature,
                "The selected target armature is the merge target.");
        }

        /// <summary>
        /// An arm fragment's target is the selected bone level that owns the counterpart bone, because the
        /// fragment's children correspond to that level's children.
        /// </summary>
        [Test]
        public void Plan_WithArmFragment_UsesTheSelectedTargetArmature()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            var partLowerArm = NewGameObject("LowerArm_L", partUpperArm.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm, partLowerArm);

            var planned = TryPlan(rig, partHost, Armatures("Armature", "Chest"), out var plan, out var issues);

            Assert.IsTrue(planned, Describe(issues));
            Assert.AreSame(partArmature, plan.MergeRoot);
            Assert.AreSame(rig.Chest, plan.TargetArmature,
                "The selected target armature is the object whose children hold the counterpart of UpperArm_L.");
        }

        /// <summary>
        /// A part that already lives inside the avatar's armature is skinned to the avatar's own bones: there is
        /// nothing to merge and no configuration is created.
        /// </summary>
        [Test]
        public void Plan_WithPartInsideTheAvatarArmature_CreatesNothing()
        {
            var rig = CreateAvatar();

            // The part root is the avatar's own UpperArm_L, and its mesh is skinned to the avatar's LowerArm_L.
            NewSkinnedRenderer(rig.UpperArmL, "ArmMesh", rig.LowerArmL);

            var planned = TryPlan(rig, rig.UpperArmL, null, out var plan, out var issues);

            Assert.IsFalse(planned, "A part inside the armature needs no merge configuration.");
            Assert.IsFalse(plan.IsValid);
            Assert.AreEqual(0, issues.Count, "Nothing to do is not a defect." + Describe(issues));
        }

        /// <summary>
        /// An armature selection does not override the armature rule: merging an object of the avatar's own
        /// skeleton would reparent that skeleton, which is a restructure rather than a merge.
        /// </summary>
        [Test]
        public void Plan_WithArmatureSelectionForAPartInsideTheArmature_CreatesNothing()
        {
            var rig = CreateAvatar();

            NewSkinnedRenderer(rig.UpperArmL, "ArmMesh", rig.LowerArmL);

            var bones = Armatures("Armature", "Armature");
            var planned = TryPlan(rig, rig.UpperArmL, bones, out var plan, out var issues);

            Assert.IsFalse(planned, "A part inside the armature has nothing to merge, whatever the profile says.");
            Assert.IsFalse(plan.IsValid);
            Assert.AreEqual(0, issues.Count, Describe(issues));
        }

        /// <summary>The profile can decline the merge, in which case the part keeps its own bones.</summary>
        [Test]
        public void Plan_WithMergeDeclinedByProfile_CreatesNothing()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm);

            var bones = new ApaBoneProfile { MergeArmature = false };
            var planned = TryPlan(rig, partHost, bones, out var plan, out var issues);

            Assert.IsFalse(planned);
            Assert.IsFalse(plan.IsValid);
            Assert.AreEqual(0, issues.Count, "An author's explicit choice is not a defect." + Describe(issues));
        }

        /// <summary>A rigid part has no skeleton of its own, so no merge configuration is created for it.</summary>
        [Test]
        public void Plan_WithRigidPart_CreatesNothing()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberProp", rig.Avatar.transform);
            var part = NewGameObject("Prop", partHost.transform);
            part.AddComponent<MeshFilter>().sharedMesh = NewRingMesh("PropMesh");
            part.AddComponent<MeshRenderer>();

            var planned = TryPlan(rig, partHost, null, out var plan, out var issues);

            Assert.IsFalse(planned);
            Assert.IsFalse(plan.IsValid);
            Assert.AreEqual(0, issues.Count, Describe(issues));
        }

        /// <summary>
        /// The two selections are used exactly as recorded; neither is derived from the hierarchy, so a target
        /// level that is not the part's own counterpart is still honoured.
        /// </summary>
        [Test]
        public void Plan_UsesTheTwoSelectedArmaturesVerbatim()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm);

            var planned = TryPlan(rig, partHost, Armatures("Armature", "Spine"), out var plan, out var issues);

            Assert.IsTrue(planned, Describe(issues));
            Assert.AreSame(partArmature, plan.MergeRoot);
            Assert.AreSame(rig.Spine, plan.TargetArmature,
                "The recorded selection is resolved as written; nothing re-derives it from the part's bone names.");
        }

        /// <summary>
        /// The avatar root's canonical token resolves to the avatar root itself, which is a legitimate target
        /// armature for an avatar whose skeleton hangs directly off its root.
        /// </summary>
        [Test]
        public void Plan_WithRootTokenTarget_TargetsTheAvatarRoot()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm);

            var bones = Armatures("Armature", ApaAvatarPath.Root);
            var planned = TryPlan(rig, partHost, bones, out var plan, out var issues);

            Assert.IsTrue(planned, Describe(issues));
            Assert.AreSame(rig.Avatar, plan.TargetArmature);
        }

        /// <summary>
        /// A recorded target armature that no longer resolves blocks with one stable diagnostic instead of
        /// falling back to a derivation the author explicitly replaced.
        /// </summary>
        [Test]
        public void Plan_WithUnresolvableTargetArmature_Blocks()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm);

            var planned = TryPlan(rig, partHost, Armatures("Armature", "Armature/Missing"), out var plan, out var issues);

            Assert.IsFalse(planned);
            Assert.IsFalse(plan.IsValid);

            var blocking = issues.FindAll(issue => issue.IsBlocking);
            Assert.AreEqual(1, blocking.Count, "One reason, reported once." + Describe(issues));
            Assert.AreEqual(ApaErrorCode.ArmatureSelectionInvalid, blocking[0].Code);
            StringAssert.Contains("reason=target-armature-not-found", blocking[0].Detail);
            Assert.AreEqual("part-a", blocking[0].PartId, "A part-scoped diagnostic carries the part identity.");
        }

        /// <summary>
        /// Two candidate armatures in the avatar are no longer ambiguous: the merge target is exactly the one
        /// the profile selects, and a different selection produces a different target.
        /// </summary>
        /// <remarks>
        /// The defect this replaces: the planner used to search the avatar's skeleton for a bone whose name
        /// matched the part's top bone and block when two bones shared it. The selection removes the search, so
        /// the same hierarchy that used to be unplannable is now plannable — and the plan follows the selection.
        /// </remarks>
        [Test]
        public void Plan_WithTwoCandidateArmatures_UsesTheSelectedOne()
        {
            var rig = CreateAvatar();

            // A second body renderer whose skeleton also has a Hips bone, outside every part root.
            var altArmature = NewGameObject("AltArmature", rig.Avatar.transform);
            var altHips = NewGameObject("Hips", altArmature.transform);
            var altBody = NewGameObject("AltBody", rig.Avatar.transform);
            NewSkinnedRenderer(altBody, "AltMesh", altHips);

            var partHost = NewGameObject("CyberBody", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partHips = NewGameObject("Hips", partArmature.transform);
            var partSpine = NewGameObject("Spine", partHips.transform);
            NewSkinnedRenderer(partHost, "Body", partHips, partSpine);

            var planned = TryPlan(rig, partHost, Armatures("Armature", "Armature"), out var plan, out var issues);
            Assert.IsTrue(planned, Describe(issues));
            Assert.AreSame(rig.Armature, plan.TargetArmature, "The selected armature is the merge target.");

            planned = TryPlan(rig, partHost, Armatures("Armature", "AltArmature"), out plan, out issues);
            Assert.IsTrue(planned, Describe(issues));
            Assert.AreSame(altArmature, plan.TargetArmature, "A different selection is a different merge target.");
        }

        /// <summary>
        /// A part whose highest bone is not inside the selected part armature blocks: Modular Avatar matches the
        /// selected armature's children, so the skeleton would not be merged at all.
        /// </summary>
        [Test]
        public void Plan_WithTopBoneOutsideTheSelectedPartArmature_BlocksWithTheReasonToken()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberProp", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var otherArmature = NewGameObject("OtherArmature", partHost.transform);
            var partJoint = NewGameObject("MechanicalJoint_01", partArmature.transform);
            NewSkinnedRenderer(partHost, "Prop", partJoint);

            var planned = TryPlan(rig, partHost, Armatures("OtherArmature", "Armature"), out var plan, out var issues);

            Assert.IsFalse(planned);
            Assert.IsFalse(plan.IsValid);
            Assert.AreEqual(1, issues.Count, Describe(issues));
            Assert.AreEqual(ApaErrorCode.ArmatureSelectionInvalid, issues[0].Code);
            StringAssert.Contains("reason=part-top-bone-outside-armature", issues[0].Detail);
            Assert.IsNotNull(otherArmature, "The selected armature is the sibling the fixture deliberately chose.");
        }

        /// <summary>
        /// Every selection state the planner cannot use blocks with <c>APA043</c> and its own reason token, so the
        /// author sees one actionable remedy rather than a merge placed by guesswork.
        /// </summary>
        [Test]
        public void Plan_WithAnUnusableSelection_BlocksWithItsReasonToken()
        {
            var cases = new[]
            {
                new SelectionCase("missing part armature", string.Empty, "Armature", "reason=missing-part-armature"),
                new SelectionCase("missing target armature", "Armature", string.Empty, "reason=missing-target-armature"),
                new SelectionCase(
                    "part armature not found", "Armature/Missing", "Armature", "reason=part-armature-not-found"),
                new SelectionCase(
                    "target inside the part", "Armature", "CyberArm_L/Armature/Target",
                    "reason=target-armature-inside-part-armature"),
                new SelectionCase(
                    "merge target is the part", "Armature", "CyberArm_L/InnerArmature",
                    "reason=merge-target-is-part")
            };

            for (var i = 0; i < cases.Length; i++)
            {
                var rig = CreateAvatar();

                var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
                var partArmature = NewGameObject("Armature", partHost.transform);
                var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
                NewSkinnedRenderer(partHost, "Arm", partUpperArm);

                // The two objects the last two cases select: one inside the selected part armature, one inside
                // the part root but outside it. Both exist only so the path resolves; the defect is the relation.
                NewGameObject("Target", partArmature.transform);
                NewGameObject("InnerArmature", partHost.transform);

                var bones = Armatures(cases[i].PartArmaturePath, cases[i].TargetArmaturePath);
                var planned = TryPlan(rig, partHost, bones, out var plan, out var issues);

                Assert.IsFalse(planned, cases[i].What + " must not be planned.");
                Assert.IsFalse(plan.IsValid, cases[i].What + " must not produce a plan.");

                var blocking = issues.FindAll(issue => issue.IsBlocking);
                Assert.AreEqual(1, blocking.Count, cases[i].What + " reports one reason." + Describe(issues));
                Assert.AreEqual(ApaErrorCode.ArmatureSelectionInvalid, blocking[0].Code, cases[i].What);
                StringAssert.Contains(cases[i].Reason, blocking[0].Detail, cases[i].What);
            }
        }

        /// <summary>One unusable armature selection, as <see cref="Plan_WithAnUnusableSelection_BlocksWithItsReasonToken"/> varies it.</summary>
        private readonly struct SelectionCase
        {
            public readonly string What;
            public readonly string PartArmaturePath;
            public readonly string TargetArmaturePath;
            public readonly string Reason;

            public SelectionCase(string what, string partArmaturePath, string targetArmaturePath, string reason)
            {
                What = what;
                PartArmaturePath = partArmaturePath;
                TargetArmaturePath = targetArmaturePath;
                Reason = reason;
            }
        }

        /// <summary>
        /// Resolution of a merge target uses the same path vocabulary as the rest of the pipeline: the empty
        /// string is "missing" and never resolves, while the root token resolves to the avatar root.
        /// </summary>
        [Test]
        public void ResolvePath_FollowsTheCanonicalPathVocabulary()
        {
            var rig = CreateAvatar();

            Assert.IsNull(MergeArmatureGenerator.ResolvePath(rig.Avatar, string.Empty),
                "The empty path means missing and must not resolve to anything.");
            Assert.AreSame(rig.Avatar, MergeArmatureGenerator.ResolvePath(rig.Avatar, ApaAvatarPath.Root));
            Assert.AreSame(rig.Hips, MergeArmatureGenerator.ResolvePath(rig.Avatar, "Armature/Hips"));
            Assert.IsNull(MergeArmatureGenerator.ResolvePath(rig.Avatar, "Armature/Missing"));
        }

        /// <summary>
        /// The armature root is the highest object of the avatar's skeleton below the avatar root, so a part that
        /// lives anywhere inside it is recognized as already merged.
        /// </summary>
        [Test]
        public void ResolveAvatarArmatureRoot_FindsTheSkeletonRoot()
        {
            var rig = CreateAvatar();

            var armatureRoot = MergeArmatureGenerator.ResolveAvatarArmatureRoot(
                rig.Avatar, new List<GameObject> { rig.UpperArmL });

            Assert.AreSame(rig.Armature, armatureRoot);
        }

        // ---- existing configuration coverage, the merge postcondition, and the declared lock mode -------------

        /// <summary>
        /// Existing configuration coverage is structural: only a configuration whose own object encloses the
        /// part's top bone merges that bone. A configuration on a sub-bone merges only that bone's children, and a
        /// configuration somewhere else merges something else, so neither may suppress the part's own merge.
        /// </summary>
        /// <remarks>
        /// Asserted through the Modular-Avatar-free seam <see cref="MergeArmatureGenerator.IsSkeletonCoveredBy"/>
        /// rather than only through a live component, so the rule itself is pinned independently of the
        /// enumeration that finds configurations.
        /// </remarks>
        [Test]
        public void Coverage_IsStructuralRelativeToThePartTopBone()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            var partLowerArm = NewGameObject("LowerArm_L", partUpperArm.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm, partLowerArm);

            Assert.IsTrue(
                MergeArmatureGenerator.TryFindPartSkeleton(partHost, out var topBone, out var mergeRoot),
                "The rig must have a skeleton for the coverage rule to be about.");
            Assert.AreSame(partUpperArm.transform, topBone);
            Assert.AreSame(partArmature, mergeRoot);

            Assert.IsTrue(
                MergeArmatureGenerator.IsSkeletonCoveredBy(new List<GameObject> { partArmature }, topBone),
                "A configuration on the merge root merges the part's top bone.");
            Assert.IsTrue(
                MergeArmatureGenerator.IsSkeletonCoveredBy(new List<GameObject> { partHost }, topBone),
                "A configuration above the merge root but still enclosing the skeleton covers the part.");
            Assert.IsFalse(
                MergeArmatureGenerator.IsSkeletonCoveredBy(new List<GameObject> { partLowerArm }, topBone),
                "A configuration on a bone below the top bone merges only that bone's children.");
            Assert.IsFalse(
                MergeArmatureGenerator.IsSkeletonCoveredBy(new List<GameObject> { partUpperArm }, topBone),
                "A configuration on the top bone itself merges its children, never the bone.");
            Assert.IsFalse(
                MergeArmatureGenerator.IsSkeletonCoveredBy(new List<GameObject> { rig.Armature }, topBone),
                "A configuration on an unrelated object covers nothing.");
            Assert.IsFalse(
                MergeArmatureGenerator.IsSkeletonCoveredBy(null, topBone),
                "No configuration covers nothing.");
        }

        /// <summary>
        /// An author configuration that already merges the part's skeleton suppresses the transient one, which is
        /// also what keeps the generating pass idempotent when a preview re-runs it.
        /// </summary>
        [Test]
        public void Plan_WithConfigurationOnThePartArmature_CreatesNothing()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm);
            AddMergeConfiguration(partArmature);

            var planned = TryPlan(rig, partHost, null, out var plan, out var issues);

            Assert.IsFalse(planned, "The existing configuration already merges this skeleton.");
            Assert.IsFalse(plan.IsValid);
            Assert.AreEqual(0, issues.Count, "An author's configuration is not a defect." + Describe(issues));
        }

        /// <summary>
        /// The regression the structural rule exists for: a nested configuration on a sub-bone does not merge the
        /// part's top bone, so the part still needs a configuration of its own. Accepting any descendant
        /// component would leave the top bone unmerged and the assembly would proceed with a detached skeleton.
        /// </summary>
        [Test]
        public void Plan_WithConfigurationBelowTheTopBone_StillPlansTheMerge()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            var partLowerArm = NewGameObject("LowerArm_L", partUpperArm.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm, partLowerArm);
            AddMergeConfiguration(partLowerArm);

            var planned = TryPlan(rig, partHost, Armatures("Armature", "Chest"), out var plan, out var issues);

            Assert.IsTrue(
                planned,
                "A configuration below the top bone merges only that bone's children, so the part still needs " +
                "its own merge." + Describe(issues));
            Assert.AreSame(partArmature, plan.MergeRoot);
            Assert.AreSame(rig.Chest, plan.TargetArmature);
        }

        /// <summary>
        /// A planned merge records the bone it has to move, which is the identity the assembly pass checks the
        /// post-merge postcondition against.
        /// </summary>
        [Test]
        public void Plan_RecordsTheTopBoneAsThePostMergeAnchor()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberBody", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partHips = NewGameObject("Hips", partArmature.transform);
            var partSpine = NewGameObject("Spine", partHips.transform);
            NewSkinnedRenderer(partHost, "Body", partHips, partSpine);

            var planned = TryPlan(rig, partHost, Armatures("Armature", "Armature"), out var plan, out var issues);

            Assert.IsTrue(planned, Describe(issues));
            Assert.AreSame(
                partHips.transform,
                plan.PartTopBone,
                "The planned bone is the part's highest bone, the one Modular Avatar reparents.");
        }

        /// <summary>
        /// The merge postcondition is hierarchy state, not a component's lifetime: it fails while the recorded
        /// bone is still under the part root — the case a destroyed-but-never-applied configuration leaves
        /// behind — and holds only once the bone has left it.
        /// </summary>
        [Test]
        public void MergePostcondition_FailsWhileTheBoneStaysUnderThePart()
        {
            var rig = CreateAvatar();

            var partHost = NewGameObject("CyberArm_L", rig.Avatar.transform);
            var partArmature = NewGameObject("Armature", partHost.transform);
            var partUpperArm = NewGameObject("UpperArm_L", partArmature.transform);
            NewSkinnedRenderer(partHost, "Arm", partUpperArm);

            var partRoots = new List<GameObject> { partHost };
            var topBone = partUpperArm.transform;

            Assert.IsFalse(
                MergeArmatureGenerator.IsMergeApplied(topBone, partRoots),
                "A bone still under the part root is a merge that did not happen, however the component ended.");

            // What Modular Avatar's merge leaves behind: the bone is reparented out of the part, into the armature.
            partUpperArm.transform.SetParent(rig.Chest.transform, false);
            Assert.IsTrue(
                MergeArmatureGenerator.IsMergeApplied(topBone, partRoots),
                "A bone reparented into the avatar's armature has been merged.");

            // The other shape a merge leaves: the proxy bone itself is gone.
            Object.DestroyImmediate(partUpperArm);
            Assert.IsTrue(
                MergeArmatureGenerator.IsMergeApplied(topBone, partRoots),
                "A proxy bone removed by the merge counts as merged.");
        }

        // ---- declared lock mode -------------------------------------------------------------------------------

        /// <summary>
        /// A created configuration declares <c>NotLocked</c>, the mode the assembler wants for a transient
        /// build-time merge. See <see cref="MergeArmatureGenerator.CreateMergeConfiguration"/> for why the
        /// declared value and Modular Avatar's internal lock controller can differ in the installed version.
        /// </summary>
        [Test]
        public void CreateMergeConfiguration_DeclaresTheNotLockedMode()
        {
            var rig = CreateAvatar();

            var partRoot = NewGameObject("CyberArm_L", rig.Avatar.transform);

            // The object is inactive while the component is added so that Modular Avatar's ExecuteInEditMode
            // lifecycle (OnEnable -> armature-lock controller) does not run inside a unit test. The production
            // path writes the same declared value either way; the lifecycle itself is documented on
            // CreateMergeConfiguration.
            partRoot.SetActive(false);

            var issues = new List<ValidationIssue>();
            var configuration = MergeArmatureGenerator.CreateMergeConfiguration(partRoot, rig.Armature, issues);

            Assert.IsNotNull(
                configuration,
                "A configuration with both ends resolved must be created." + Describe(issues));
            Assert.AreEqual(0, issues.Count, Describe(issues));
            Assert.AreEqual(
                "NotLocked",
                MergeArmatureGenerator.GetDeclaredLockModeName(configuration),
                "The transient configuration must declare NotLocked.");
        }

        /// <summary>
        /// A created configuration always declares exact-name matching, whatever the profile's legacy name policy
        /// says, so Modular Avatar's merge mirrors the armature-relative bone identity the pipeline records.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Modular Avatar consumes <c>prefix</c>/<c>suffix</c> when it maps bones (<c>MergeArmatureHook</c>). A
        /// rewritten name could match two bones whose paths relative to their own armatures differ — that is, a
        /// bone this pipeline considers a different joint — so the M10 build writes an empty prefix and suffix
        /// and never infers. The legacy fields are still serialized on the profile for deserialization
        /// compatibility, which is exactly what this test proves they no longer influence.
        /// </para>
        /// <para>
        /// The exact-name promise is not a fallback: <see cref="MergeArmatureGenerator.TryPlanMerge"/> resolves
        /// both armatures explicitly, so the two objects the component is created on and pointed at are already
        /// the corresponding bone levels, which is the relation exact matching needs.
        /// </para>
        /// </remarks>
        [Test]
        public void CreateMergeConfiguration_AlwaysWritesExactNameMatching()
        {
            var rig = CreateAvatar();

            // Every legacy policy shape, including the two that used to reach the component.
            var profiles = new[]
            {
                new ApaBoneProfile { MergePrefix = "P_", MergeSuffix = "_L" },
                new ApaBoneProfile { InferMergeNames = true },
                new ApaBoneProfile { MergeTargetPath = "Armature" },
                new ApaBoneProfile()
            };

            for (var i = 0; i < profiles.Length; i++)
            {
                var partRoot = NewGameObject("Part" + i, rig.Avatar.transform);
                partRoot.SetActive(false);

                var issues = new List<ValidationIssue>();
                var configuration = MergeArmatureGenerator.CreateMergeConfiguration(
                    partRoot, rig.Armature, issues);

                Assert.IsNotNull(configuration, Describe(issues));
                Assert.AreEqual(0, issues.Count, Describe(issues));
                Assert.AreEqual(
                    "prefix=''; suffix=''",
                    MergeArmatureGenerator.DescribeDeclaredMergeNames(configuration),
                    "Profile " + i + " must not reach the component with a name policy.");
            }

            Assert.IsNull(
                MergeArmatureGenerator.DescribeDeclaredMergeNames(null),
                "A null configuration is not a merge configuration.");
        }

        // ---- rig --------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Armature;
            public GameObject Hips;
            public GameObject Spine;
            public GameObject Chest;
            public GameObject UpperArmL;
            public GameObject LowerArmL;
            public SkinnedMeshRenderer BodyRenderer;
        }

        /// <summary>
        /// Builds "Avatar" -&gt; "Armature" -&gt; "Hips" -&gt; "Spine" -&gt; "Chest" -&gt; "UpperArm_L" -&gt;
        /// "LowerArm_L", plus a body renderer whose bone list covers the hips, the spine, the chest, and the
        /// upper arm — the deform bones an avatar body renderer really carries. Movement is never involved, so
        /// every object keeps its default transform; only names and the hierarchy matter to a merge decision.
        /// </summary>
        private Rig CreateAvatar()
        {
            var avatar = NewGameObject("Avatar", null);
            var armature = NewGameObject("Armature", avatar.transform);
            var hips = NewGameObject("Hips", armature.transform);
            var spine = NewGameObject("Spine", hips.transform);
            var chest = NewGameObject("Chest", spine.transform);
            var upperArm = NewGameObject("UpperArm_L", chest.transform);
            var lowerArm = NewGameObject("LowerArm_L", upperArm.transform);

            var body = NewGameObject("Body", avatar.transform);
            var bodyRenderer = NewSkinnedRenderer(body, "BodyMesh", hips, spine, chest, upperArm);

            return new Rig
            {
                Avatar = avatar,
                Armature = armature,
                Hips = hips,
                Spine = spine,
                Chest = chest,
                UpperArmL = upperArm,
                LowerArmL = lowerArm,
                BodyRenderer = bodyRenderer
            };
        }

        private bool TryPlan(
            Rig rig,
            GameObject partRoot,
            ApaBoneProfile bones,
            out MergeArmaturePlan plan,
            out List<ValidationIssue> issues)
        {
            issues = new List<ValidationIssue>();
            var request = new MergeArmatureRequest(
                rig.Avatar,
                partRoot,
                "part-a",
                bones,
                new List<GameObject> { partRoot });

            return MergeArmatureGenerator.TryPlanMerge(request, issues, out plan);
        }

        /// <summary>
        /// A bone profile that selects both armatures, which is the only state the M10 planner can place a merge
        /// from. The part path is relative to the part root and the target path to the avatar root.
        /// </summary>
        private static ApaBoneProfile Armatures(string partArmaturePath, string targetArmaturePath)
        {
            return new ApaBoneProfile
            {
                PartArmaturePath = partArmaturePath,
                TargetArmaturePath = targetArmaturePath
            };
        }

        private SkinnedMeshRenderer NewSkinnedRenderer(GameObject host, string meshName, params GameObject[] bones)
        {
            var renderer = host.AddComponent<SkinnedMeshRenderer>();
            var mesh = NewRingMesh(meshName);
            renderer.sharedMesh = mesh;

            var transforms = new Transform[bones.Length];
            for (var i = 0; i < bones.Length; i++) transforms[i] = bones[i].transform;

            renderer.bones = transforms;
            renderer.rootBone = transforms.Length > 0 ? transforms[0] : null;
            mesh.bindposes = NewIdentityPoses(transforms.Length);

            return renderer;
        }

        private GameObject NewGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            _created.Add(gameObject);
            return gameObject;
        }

        /// <summary>
        /// The installed Modular Avatar merge component's full type name.
        /// </summary>
        /// <remarks>
        /// Named as a string because this test assembly does not reference Modular Avatar: the tests asmdef
        /// declares the runtime, editor, preview and NDMF assemblies, not the Modular Avatar assemblies, and the
        /// merge component is resolved from the loaded assemblies at run time instead.
        /// </remarks>
        private const string MergeArmatureTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";

        /// <summary>Adds the installed Modular Avatar merge component to an object, without naming its type.</summary>
        /// <remarks>
        /// The object is deactivated first on purpose: the component's ExecuteInEditMode lifecycle (OnEnable and
        /// the armature-lock controller it constructs) must not run inside a unit test. The coverage search reads
        /// with <c>includeInactive</c>, so an inactive configuration is found exactly like an active one.
        /// </remarks>
        private void AddMergeConfiguration(GameObject host)
        {
            var type = FindMergeArmatureType();
            Assert.IsNotNull(
                type,
                "The installed Modular Avatar component must be loadable: " + MergeArmatureTypeName);

            host.SetActive(false);
            host.AddComponent(type);
        }

        /// <summary>The merge component's <see cref="System.Type"/> from whichever loaded assembly declares it, or null.</summary>
        private static System.Type FindMergeArmatureType()
        {
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var type = assemblies[i].GetType(MergeArmatureTypeName, false);
                if (type != null) return type;
            }

            return null;
        }

        private Mesh NewRingMesh(string name)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var mesh = new Mesh { name = name };
            mesh.vertices = positions.ToArray();
            mesh.SetTriangles(MeshFixtures.CapTriangles(4, 0, 4), 0);
            _created.Add(mesh);
            return mesh;
        }

        private static Matrix4x4[] NewIdentityPoses(int count)
        {
            var poses = new Matrix4x4[count];
            for (var i = 0; i < count; i++) poses[i] = Matrix4x4.identity;
            return poses;
        }

        private static string Describe(List<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return " (no issues)";

            var builder = new StringBuilder();
            for (var i = 0; i < issues.Count; i++) builder.Append('\n').Append(issues[i]);
            return builder.ToString();
        }
    }
}

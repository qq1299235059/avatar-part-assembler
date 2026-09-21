using System;
using System.Collections.Generic;
using System.IO;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests that loading a profile is a replacement: Profile B's removal, seam, policy, and live references are
    /// used after B is loaded, and nothing Profile A contributed survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transition under test is <see cref="ApaProfileLoader"/>, which is the one code path both entry points
    /// of the window use (the <c>Load Existing Profile</c> field and the installer inspector's shortcut), so
    /// these tests exercise production logic rather than re-implementing it. The derived state that is not
    /// profile-owned — resolved candidates, mesh arrays, merge-check classification, status lines — is dropped by
    /// the window around this call and is pinned as a source contract plus the cache's own behaviour.
    /// </para>
    /// <para>
    /// No asset is created or written: the profiles are transient <c>Materialize</c> results, destroyed in
    /// teardown.
    /// </para>
    /// </remarks>
    public sealed class AuthoringProfileIsolationTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        // ---- Loading B after A -------------------------------------------------------------------------

        [Test]
        public void LoadingProfileB_ReplacesEveryPieceOfProfileOwnedStateFromA()
        {
            var scene = NewScene();
            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot
            };

            var first = ApaProfileLoader.Load(ProfileA(scene), selection, "Assets/ProfileA.asset");

            Assert.AreEqual("Profile A", first.Draft.Identity.DisplayName);
            Assert.AreEqual(2, first.Draft.Removal.Count);
            Assert.AreEqual(ApaPartSlot.LeftArm, first.Draft.Identity.Slot);
            Assert.IsFalse(first.Draft.Bones.MergeArmature);
            Assert.IsFalse(first.Draft.BlendShapes.AllowPartOnlyShapes);
            Assert.AreEqual("ArmatureA", first.Draft.Bones.TargetArmaturePath);
            Assert.AreSame(scene.TargetArmatureA, selection.TargetArmature, "A's path resolves into the live field.");

            var second = ApaProfileLoader.Load(ProfileB(scene), selection, "Assets/ProfileB.asset");

            Assert.AreNotSame(first.Draft, second.Draft, "A load builds a fresh draft rather than merging into one.");
            Assert.AreEqual("Assets/ProfileB.asset", second.ProfilePath);

            // B's authored data, not A's.
            Assert.AreEqual("Profile B", second.Draft.Identity.DisplayName);
            Assert.AreEqual(ApaPartSlot.RightLeg, second.Draft.Identity.Slot);
            Assert.AreEqual(1, second.Draft.Removal.Count);
            Assert.AreEqual(new RemovedTriangleAddress(2, 9), second.Draft.Removal.GetAddress(0));
            Assert.IsFalse(
                second.Draft.Removal.Contains(new RemovedTriangleAddress(0, 1)),
                "A's removal addresses must not survive into B.");

            CollectionAssert.AreEqual(new[] { 4 }, second.Draft.Seam.GetBaseIndices());
            CollectionAssert.AreEqual(new[] { 8 }, second.Draft.Seam.GetPartIndices());
            Assert.AreEqual(1, second.Draft.UvSemantics.Length);
            Assert.AreEqual("UVMapB", second.Draft.UvSemantics[0].Semantic);
            Assert.AreEqual(1, second.Draft.MaterialSemantics.Length);
            Assert.AreEqual("MetalB", second.Draft.MaterialSemantics[0].Semantic);
            Assert.AreEqual(ApaMaterialPolicyMode.KeepPart, second.Draft.MaterialSemantics[0].Policy);

            // B's policy, not A's.
            Assert.IsTrue(second.Draft.Bones.MergeArmature);
            Assert.IsTrue(second.Draft.BlendShapes.AllowPartOnlyShapes);

            // B's live references, not A's.
            Assert.AreEqual("ArmatureB", second.Draft.Bones.TargetArmaturePath);
            Assert.AreEqual("SkeletonB", second.Draft.Bones.PartArmaturePath);
            Assert.AreSame(
                scene.TargetArmatureB,
                selection.TargetArmature,
                "The live target armature is B's, never A's.");
            Assert.AreSame(scene.PartArmatureB, selection.PartArmature);
            Assert.AreNotSame(scene.TargetArmatureA, selection.TargetArmature);
        }

        [Test]
        public void LoadingAProfileWhoseArmaturePathDoesNotResolve_ClearsThePreviousLiveReference()
        {
            var scene = NewScene();
            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot
            };

            ApaProfileLoader.Load(ProfileA(scene), selection, "Assets/ProfileA.asset");
            Assert.AreSame(scene.TargetArmatureA, selection.TargetArmature);

            // Profile C records armature paths the hierarchy does not carry.
            var third = ApaProfileLoader.Load(ProfileC(scene), selection, "Assets/ProfileC.asset");

            Assert.IsNull(
                selection.TargetArmature,
                "An unresolved path leaves the field empty; the previous profile's object is never retained.");
            Assert.IsNull(selection.PartArmature);
            Assert.AreEqual(
                "MissingTarget",
                third.Draft.Bones.TargetArmaturePath,
                "The path stays in the draft for validation to report.");
            Assert.AreEqual("MissingPart", third.Draft.Bones.PartArmaturePath);
        }

        [Test]
        public void LoadingWithNoLiveSelection_StillYieldsTheLoadedDraft()
        {
            var scene = NewScene();
            var selection = new ApaAuthoringSelection { AvatarRoot = scene.AvatarRoot, PartRoot = scene.PartRoot };

            var loaded = ApaProfileLoader.Load(ProfileA(scene), selection, "Assets/ProfileA.asset");

            Assert.IsNotNull(loaded.Draft);
            Assert.AreEqual("Profile A", loaded.Draft.Identity.DisplayName);
            Assert.IsTrue(loaded.PrefabPath.EndsWith("Part.prefab", StringComparison.Ordinal), loaded.PrefabPath);
        }

        // ---- The derived state that must not survive a load --------------------------------------------

        [Test]
        public void InvalidatingTheCandidateCache_DropsTheResolvedResultsAndRequestsExactlyOneRefresh()
        {
            var cache = new ApaSeamCandidateCache();
            var target = Result(ApaSeamVertexColorCandidates.TargetSide);
            var part = Result(ApaSeamVertexColorCandidates.PartSide);

            cache.Store(target, part);
            Assert.IsTrue(cache.HasValue);
            Assert.IsFalse(cache.NeedsRefresh(false), "An unchanged repaint reuses the resolved sets.");

            cache.Invalidate();

            Assert.IsFalse(cache.HasValue);
            Assert.IsNull(cache.Target, "A stale result must not be reachable, even by a caller that forgot the flag.");
            Assert.IsNull(cache.Part);
            Assert.IsTrue(cache.NeedsRefresh(false), "The next read resolves once, for the new inputs.");

            cache.Store(Result(ApaSeamVertexColorCandidates.TargetSide), Result(ApaSeamVertexColorCandidates.PartSide));
            Assert.IsTrue(cache.HasValue);
        }

        [Test]
        public void Window_LoadsThroughTheSharedTransitionAndDropsDerivedState()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");
            var loader = ReadEditorSource("Authoring", "ApaProfileLoader.cs");

            // The loader owns the isolation rules.
            StringAssert.Contains("ApaProfileDraft.FromProfile(asset)", loader);
            StringAssert.Contains("draft.EnsureStablePartId();", loader);
            StringAssert.Contains("true)", loader);
            StringAssert.Contains("RestoreArmatures(", loader);

            // The window owns no second copy of the transition.
            var load = window.IndexOf("private void LoadProfile(ApaPartProfile asset)", StringComparison.Ordinal);
            var apply = window.IndexOf("private void ApplyLoadedProfile(", StringComparison.Ordinal);
            var openWith = window.IndexOf("public static ApaAuthoringWindow OpenWith(", StringComparison.Ordinal);

            Assert.GreaterOrEqual(load, 0);
            Assert.Greater(apply, load);
            Assert.GreaterOrEqual(openWith, 0);

            var loadBody = window.Substring(load, apply - load);
            StringAssert.Contains("ApplyLoadedProfile(asset, AssetDatabase.GetAssetPath(asset));", loadBody);
            Assert.IsFalse(
                loadBody.Contains("ApaProfileDraft.FromProfile("),
                "The window must not build a draft of its own: two transitions would drift apart.");

            var openEnd = window.IndexOf("// ---- IApaAuthoringSceneHost", openWith, StringComparison.Ordinal);
            Assert.Greater(openEnd, openWith);
            StringAssert.Contains(
                "window.ApplyLoadedProfile(profile, AssetDatabase.GetAssetPath(profile));",
                window.Substring(openWith, openEnd - openWith),
                "Opening the window on a profile is the same transition as loading one.");

            var applyEnd = window.IndexOf("private static string ToProjectRelative(", apply, StringComparison.Ordinal);
            Assert.Greater(applyEnd, apply);
            var applyBody = window.Substring(apply, applyEnd - apply);
            StringAssert.Contains("InvalidateDerivedState();", applyBody);
            Assert.Less(
                applyBody.IndexOf("_profilePath = loaded.ProfilePath;", StringComparison.Ordinal),
                applyBody.IndexOf("InvalidateDerivedState();", StringComparison.Ordinal));
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private static ApaSeamColorCandidateResult Result(string side)
        {
            return ApaSeamVertexColorCandidates.FromColors(
                new[] { new Color32(0, 255, 0, 255) },
                1,
                new Color32(0, 255, 0, 255),
                side);
        }

        private ApaPartProfile ProfileA(Scene scene)
        {
            var draft = ApaProfileDraft.New("Profile A", ApaPartSlot.LeftArm);
            draft.Removal.AddRange(
                new[] { new RemovedTriangleAddress(0, 1), new RemovedTriangleAddress(0, 2) },
                null);
            draft.SetPairedSeam(new[] { 3, 1 }, new[] { 7, 5 });
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMapA", 0) };
            draft.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic("MetalA", 0, null, ApaMaterialPolicyMode.ForceNew)
            };
            draft.Bones.MergeArmature = false;
            draft.BlendShapes.AllowPartOnlyShapes = false;
            draft.SetArmatures(
                scene.AvatarRoot.transform,
                scene.TargetArmatureA,
                scene.PartRoot.transform,
                scene.PartArmatureA);
            return Track(draft.Materialize());
        }

        private ApaPartProfile ProfileB(Scene scene)
        {
            var draft = ApaProfileDraft.New("Profile B", ApaPartSlot.RightLeg);
            draft.Removal.Add(new RemovedTriangleAddress(2, 9));
            draft.SetPairedSeam(new[] { 4 }, new[] { 8 });
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMapB", 1) };
            draft.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic("MetalB", 1, null, ApaMaterialPolicyMode.KeepPart)
            };
            draft.Bones.MergeArmature = true;
            draft.BlendShapes.AllowPartOnlyShapes = true;
            draft.SetArmatures(
                scene.AvatarRoot.transform,
                scene.TargetArmatureB,
                scene.PartRoot.transform,
                scene.PartArmatureB);
            return Track(draft.Materialize());
        }

        private ApaPartProfile ProfileC(Scene scene)
        {
            var draft = ApaProfileDraft.New("Profile C", ApaPartSlot.Custom);

            // Paths that no object in the scene carries: the load must leave the fields empty rather than keep
            // the previous profile's live references.
            draft.Bones.TargetArmaturePath = "MissingTarget";
            draft.Bones.PartArmaturePath = "MissingPart";
            return Track(draft.Materialize());
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            _created.Add(value);
            return value;
        }

        private GameObject Track(GameObject value)
        {
            _created.Add(value);
            return value;
        }

        /// <summary>
        /// A hierarchy with two distinguishable armature levels on each side, so a test can tell which profile's
        /// recorded path was restored.
        /// </summary>
        private Scene NewScene()
        {
            var avatarRoot = Track(new GameObject("Avatar"));
            var targetArmatureA = Track(new GameObject("ArmatureA"));
            targetArmatureA.transform.SetParent(avatarRoot.transform, false);
            var targetArmatureB = Track(new GameObject("ArmatureB"));
            targetArmatureB.transform.SetParent(avatarRoot.transform, false);

            var partRoot = Track(new GameObject("Part"));
            var partArmatureA = Track(new GameObject("SkeletonA"));
            partArmatureA.transform.SetParent(partRoot.transform, false);
            var partArmatureB = Track(new GameObject("SkeletonB"));
            partArmatureB.transform.SetParent(partRoot.transform, false);

            return new Scene(
                avatarRoot,
                targetArmatureA.transform,
                targetArmatureB.transform,
                partRoot,
                partArmatureA.transform,
                partArmatureB.transform);
        }

        private static string ReadEditorSource(string folder, string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", folder, fileName);

            if (!File.Exists(path)) Assert.Ignore("Editor source not found: " + path);

            return File.ReadAllText(path);
        }

        private sealed class Scene
        {
            public Scene(
                GameObject avatarRoot,
                Transform targetArmatureA,
                Transform targetArmatureB,
                GameObject partRoot,
                Transform partArmatureA,
                Transform partArmatureB)
            {
                AvatarRoot = avatarRoot;
                TargetArmatureA = targetArmatureA;
                TargetArmatureB = targetArmatureB;
                PartRoot = partRoot;
                PartArmatureA = partArmatureA;
                PartArmatureB = partArmatureB;
            }

            public GameObject AvatarRoot { get; }

            public Transform TargetArmatureA { get; }

            public Transform TargetArmatureB { get; }

            public GameObject PartRoot { get; }

            public Transform PartArmatureA { get; }

            public Transform PartArmatureB { get; }
        }
    }
}

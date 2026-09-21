using System;
using System.Collections.Generic;
using System.IO;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for preserving the two armature selections across a window reopen, a domain reload, and a profile
    /// load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live <see cref="Transform"/> references the two object fields show do not survive a reopen, a layout
    /// restore, or a domain reload; the recorded paths do. These tests drive the restore against a real hierarchy
    /// and assert the three properties that make it safe: it fills both sides from the paths, it never overwrites
    /// a selection the author made, and it neither invents nor clears anything when a path does not resolve.
    /// </para>
    /// <para>
    /// The window's own call sites are pinned as a source contract, because reaching <c>OnEnable</c> and a profile
    /// load needs a running Editor window; the restore itself is covered behaviourally here.
    /// </para>
    /// </remarks>
    public sealed class AuthoringArmatureRestoreTests
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

        // ---- The restore itself ------------------------------------------------------------------------

        [Test]
        public void Restore_FillsBothLiveSelectionsFromTheRecordedPaths()
        {
            var scene = NewScene();
            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot
            };

            Assert.IsNull(selection.TargetArmature, "A reopened window starts with the live references gone.");
            Assert.IsNull(selection.PartArmature);

            var restored = selection.RestoreArmatures("Armature", "Skeleton");

            Assert.IsNotEmpty(restored);
            Assert.AreSame(scene.TargetArmature, selection.TargetArmature);
            Assert.AreSame(scene.PartArmature, selection.PartArmature);
            Assert.IsTrue(selection.HasArmatureSelection);
            StringAssert.Contains("target=", restored);
            StringAssert.Contains("part=", restored);
        }

        [Test]
        public void Restore_ResolvesTheRootTokenToTheRootItself()
        {
            // "." is a real identity, not "missing": an author may select the avatar root or the part root as the
            // armature. Transform.Find reserves the token, so it has to be handled explicitly.
            var scene = NewScene();
            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot
            };

            selection.RestoreArmatures(ApaAvatarPath.Root, ApaAvatarPath.Root);

            Assert.AreSame(scene.AvatarRoot.transform, selection.TargetArmature);
            Assert.AreSame(scene.PartRoot.transform, selection.PartArmature);
        }

        [Test]
        public void Restore_DoesNotOverwriteASelectionTheAuthorAlreadyMade()
        {
            var scene = NewScene();
            var other = Track(new GameObject("OtherArmature"));
            other.transform.SetParent(scene.AvatarRoot.transform, false);

            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot,
                TargetArmature = other.transform,
                PartArmature = scene.PartArmature
            };

            var restored = selection.RestoreArmatures("Armature", "Skeleton");

            Assert.IsEmpty(restored, "Nothing was empty, so nothing may be filled.");
            Assert.AreSame(
                other.transform,
                selection.TargetArmature,
                "A restore fills empty slots; it never replaces the author's choice with the recorded path.");
            Assert.AreSame(scene.PartArmature, selection.PartArmature);
        }

        [Test]
        public void Restore_ProfileLoadReplacesSelectionsFromThePreviousDraft()
        {
            var scene = NewScene();
            var oldTarget = Track(new GameObject("OldTargetArmature"));
            oldTarget.transform.SetParent(scene.AvatarRoot.transform, false);
            var oldPart = Track(new GameObject("OldPartArmature"));
            oldPart.transform.SetParent(scene.PartRoot.transform, false);

            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot,
                TargetArmature = oldTarget.transform,
                PartArmature = oldPart.transform
            };

            selection.RestoreArmatures("Armature", "Skeleton", true);

            Assert.AreSame(scene.TargetArmature, selection.TargetArmature);
            Assert.AreSame(scene.PartArmature, selection.PartArmature);
        }

        [Test]
        public void Restore_LeavesAnUnresolvablePathEmptyAndPreservesItInTheDraft()
        {
            var scene = NewScene();
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            draft.SetArmatures(
                scene.AvatarRoot.transform,
                scene.TargetArmature,
                scene.PartRoot.transform,
                scene.PartArmature);

            // The hierarchy the path was recorded against is gone: the recorded value must survive untouched.
            var recordedTargetPath = draft.Bones.TargetArmaturePath;
            UnityEngine.Object.DestroyImmediate(scene.TargetArmature.gameObject);

            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                TargetRenderer = scene.Body,
                PartRoot = scene.PartRoot,
                PartRenderer = scene.PartRenderer,
                PartArmature = scene.PartArmature
            };

            var restored = selection.RestoreArmatures(
                draft.Bones.TargetArmaturePath,
                draft.Bones.PartArmaturePath);

            Assert.IsEmpty(restored);
            Assert.IsNull(selection.TargetArmature, "An unresolvable path leaves the field empty rather than guessing.");
            Assert.AreEqual(
                recordedTargetPath,
                draft.Bones.TargetArmaturePath,
                "The draft keeps the path: the author has to see which selection is missing, not lose it.");

            // And the existing selection check reports it, with the stable token the build uses for the same
            // condition. Nothing here invents a second diagnostic for it.
            var issues = selection.Validate();
            var missing = issues.FindAll(issue => issue.Detail != null
                                                  && issue.Detail.Contains("reason=missing-target-armature"));
            Assert.IsNotEmpty(
                missing,
                "An empty target armature must be reported as the missing selection it is: " +
                string.Join(" | ", issues.ConvertAll(issue => issue.Code + " " + issue.Detail).ToArray()));
        }

        [Test]
        public void Restore_RefusesAPathThatResolvesOutsideItsRoot()
        {
            var scene = NewScene();
            var elsewhere = Track(new GameObject("Elsewhere"));
            var outside = Track(new GameObject("Deep"));
            outside.transform.SetParent(elsewhere.transform, false);

            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot
            };

            var restored = selection.RestoreArmatures("Elsewhere/Deep", string.Empty);

            Assert.IsEmpty(restored);
            Assert.IsNull(
                selection.TargetArmature,
                "An armature outside the root has no identity this pipeline can record, so it is refused rather " +
                "than used.");
            Assert.IsNotNull(outside, "The object exists in the scene; it is simply not inside the avatar root.");
        }

        [Test]
        public void Restore_DoesNothingWithoutTheRootThePathIsRelativeTo()
        {
            var scene = NewScene();
            var selection = new ApaAuthoringSelection { PartRoot = scene.PartRoot };

            var restored = selection.RestoreArmatures("Armature", "Skeleton");

            Assert.IsEmpty(restored, "No avatar root means the target path cannot be resolved at all.");
            Assert.IsNull(selection.TargetArmature);
            Assert.AreSame(
                scene.PartArmature,
                selection.PartArmature,
                "The part side is independent: its own root is selected, so its path still resolves.");
        }

        // ---- The profile round trip --------------------------------------------------------------------

        [Test]
        public void ProfileRoundTrip_RestoresTheSelectionsTheProfileRecorded()
        {
            var scene = NewScene();
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            draft.SetArmatures(
                scene.AvatarRoot.transform,
                scene.TargetArmature,
                scene.PartRoot.transform,
                scene.PartArmature);

            var profile = TrackAsset(draft.Materialize());
            var loaded = ApaProfileDraft.FromProfile(profile);

            Assert.AreEqual("Armature", loaded.Bones.TargetArmaturePath);
            Assert.AreEqual("Skeleton", loaded.Bones.PartArmaturePath);

            // This is what the window does after loading: the paths come from the profile, the roots from the
            // scene, and the two live fields are restored from them.
            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = scene.AvatarRoot,
                PartRoot = scene.PartRoot
            };

            var restored = selection.RestoreArmatures(
                loaded.Bones.TargetArmaturePath,
                loaded.Bones.PartArmaturePath);

            Assert.IsNotEmpty(restored);
            Assert.AreSame(scene.TargetArmature, selection.TargetArmature);
            Assert.AreSame(scene.PartArmature, selection.PartArmature);
            Assert.AreEqual(
                scene.TargetArmature,
                ApaAuthoringSelection.ResolveArmaturePath(
                    scene.AvatarRoot.transform, loaded.Bones.TargetArmaturePath));
        }

        [Test]
        public void ResolveArmaturePath_IsTheOnePlaceAPathBecomesATransform()
        {
            var scene = NewScene();

            Assert.AreSame(
                scene.TargetArmature,
                ApaAuthoringSelection.ResolveArmaturePath(scene.AvatarRoot.transform, "Armature"));
            Assert.AreSame(
                scene.AvatarRoot.transform,
                ApaAuthoringSelection.ResolveArmaturePath(scene.AvatarRoot.transform, ApaAvatarPath.Root));
            Assert.IsNull(
                ApaAuthoringSelection.ResolveArmaturePath(scene.AvatarRoot.transform, string.Empty),
                "The empty path is 'missing', not an identity.");
            Assert.IsNull(ApaAuthoringSelection.ResolveArmaturePath(null, "Armature"));
            Assert.IsNull(ApaAuthoringSelection.ResolveArmaturePath(scene.AvatarRoot.transform, "Nope/Deep"));
        }

        // ---- The window's call sites -------------------------------------------------------------------

        [Test]
        public void Window_RestoresOnEnableAndAfterAProfileLoad()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");
            var loader = ReadEditorSource("Authoring", "ApaProfileLoader.cs");

            StringAssert.Contains(
                "private bool RestoreArmatureSelections(bool replaceExisting = false)",
                window,
                "The window must have one restore entry point.");

            var onEnable = window.IndexOf("private void OnEnable()", StringComparison.Ordinal);
            var onDisable = window.IndexOf("private void OnDisable()", StringComparison.Ordinal);
            Assert.GreaterOrEqual(onEnable, 0);
            Assert.Greater(onDisable, onEnable);

            StringAssert.Contains(
                "RestoreArmatureSelections();",
                window.Substring(onEnable, onDisable - onEnable),
                "A reopened window must re-resolve the recorded paths before the first repaint draws the fields.");

            // Loading a profile restores through the shared transition: the window delegates, and the loader
            // replaces both live references from the newly loaded profile's paths with replacement enabled.
            var load = window.IndexOf("private void LoadProfile(ApaPartProfile asset)", StringComparison.Ordinal);
            var apply = window.IndexOf("private void ApplyLoadedProfile(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(load, 0);
            Assert.Greater(apply, load);

            StringAssert.Contains(
                "ApplyLoadedProfile(asset, AssetDatabase.GetAssetPath(asset));",
                window.Substring(load, apply - load),
                "Loading a profile goes through the one transition.");

            StringAssert.Contains(
                "ApaProfileLoader.Load(asset, _selection, profileAssetPath);",
                window.Substring(apply),
                "The transition is the loader's, so the two entry points cannot diverge.");

            StringAssert.Contains("ApaProfileDraft.FromProfile(asset)", loader);
            StringAssert.Contains("RestoreArmatures(", loader);
            StringAssert.Contains("true)", loader);
            Assert.Less(
                loader.IndexOf("ApaProfileDraft.FromProfile(asset)", StringComparison.Ordinal),
                loader.IndexOf("selection.RestoreArmatures(", StringComparison.Ordinal),
                "The paths only exist from the moment the draft holds them.");

            var openWith = window.IndexOf("public static ApaAuthoringWindow OpenWith(", StringComparison.Ordinal);
            var openEnd = window.IndexOf("// ---- IApaAuthoringSceneHost", openWith, StringComparison.Ordinal);
            Assert.GreaterOrEqual(openWith, 0);
            Assert.Greater(openEnd, openWith);
            StringAssert.Contains(
                "window.ApplyLoadedProfile(profile, AssetDatabase.GetAssetPath(profile));",
                window.Substring(openWith, openEnd - openWith),
                "Opening the window on a profile must restore the same way a load does.");
        }

        [Test]
        public void Window_DoesNotRewriteTheArmaturePathsOnAnUnrelatedSelectionEdit()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            var block = window.IndexOf("if (EditorGUI.EndChangeCheck())", StringComparison.Ordinal);
            var nextButton = window.IndexOf("EditorGUILayout.BeginHorizontal();", block, StringComparison.Ordinal);
            Assert.GreaterOrEqual(block, 0);
            Assert.Greater(nextButton, block);

            var body = window.Substring(block, nextButton - block);

            StringAssert.Contains(
                "if (armatureChanged || rootsChanged)",
                body,
                "Writing the armature paths is gated: an unrelated edit in this block (the output folder, the " +
                "part renderer) must not re-record a selection whose live field could not be restored.");
            Assert.Less(
                body.IndexOf("if (armatureChanged || rootsChanged)", StringComparison.Ordinal),
                body.IndexOf("_draft.SetArmatures(", StringComparison.Ordinal),
                "The gate must come before the write.");
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private static string ReadEditorSource(string folder, string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", folder, fileName);

            if (!File.Exists(path)) Assert.Ignore("Editor source not found: " + path);

            return File.ReadAllText(path);
        }

        private GameObject Track(GameObject gameObject)
        {
            _created.Add(gameObject);
            return gameObject;
        }

        private T TrackAsset<T>(T value) where T : UnityEngine.Object
        {
            _created.Add(value);
            return value;
        }

        /// <summary>
        /// A minimal authoring hierarchy: an avatar root with a skinned body renderer and a target armature, and
        /// a part root with a skinned part renderer and a part armature.
        /// </summary>
        private Scene NewScene()
        {
            var avatarRoot = Track(new GameObject("Avatar"));
            var targetArmature = Track(new GameObject("Armature"));
            targetArmature.transform.SetParent(avatarRoot.transform, false);

            var bodyHost = Track(new GameObject("Body"));
            bodyHost.transform.SetParent(avatarRoot.transform, false);
            var body = bodyHost.AddComponent<SkinnedMeshRenderer>();
            body.bones = new[] { targetArmature.transform };

            var partRoot = Track(new GameObject("Part"));
            var partArmature = Track(new GameObject("Skeleton"));
            partArmature.transform.SetParent(partRoot.transform, false);

            var partHost = Track(new GameObject("PartBody"));
            partHost.transform.SetParent(partRoot.transform, false);
            var partRenderer = partHost.AddComponent<SkinnedMeshRenderer>();
            partRenderer.bones = new[] { partArmature.transform };

            return new Scene(avatarRoot, targetArmature, partRoot, partArmature, body, partRenderer);
        }

        private sealed class Scene
        {
            public Scene(
                GameObject avatarRoot,
                GameObject targetArmature,
                GameObject partRoot,
                GameObject partArmature,
                SkinnedMeshRenderer body,
                SkinnedMeshRenderer partRenderer)
            {
                AvatarRoot = avatarRoot;
                TargetArmature = targetArmature.transform;
                PartRoot = partRoot;
                PartArmature = partArmature.transform;
                Body = body;
                PartRenderer = partRenderer;
            }

            public GameObject AvatarRoot { get; }

            public Transform TargetArmature { get; }

            public GameObject PartRoot { get; }

            public Transform PartArmature { get; }

            public SkinnedMeshRenderer Body { get; }

            public SkinnedMeshRenderer PartRenderer { get; }
        }
    }
}

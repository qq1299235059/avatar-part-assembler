using System;
using System.IO;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the Scene View overlay gating: the three visible toolbar switches, merge-check precedence, and the
    /// callback which reaches the tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The precedence itself is pure (<see cref="ApaOverlayVisibility"/>), so the whole rule is asserted directly
    /// instead of being inferred from the draw path. The connections a pure test cannot see — that the tool draws
    /// through that one function, that no picking path is left, that the window turns the master gate back on, and
    /// that a profile load drops the derived state — are pinned as source contracts, the way this suite pins the
    /// other Scene View behaviour.
    /// </para>
    /// <para>
    /// The registry is exercised behaviourally: a registered tool is dispatched to, a disposed tool is dropped,
    /// and the hook is installed exactly once.
    /// </para>
    /// </remarks>
    public sealed class AuthoringOverlayVisibilityTests
    {
        [TearDown]
        public void TearDown()
        {
            ApaAuthoringSceneToolRegistry.Clear();
        }

        // ---- The gating rule ---------------------------------------------------------------------------

        [Test]
        public void AllOverlaySwitchesOff_DrawsNoLayer()
        {
            var plan = ApaOverlayVisibility.For(false, false, false);

            Assert.IsFalse(plan.DrawsAnyLayer);
            Assert.IsFalse(plan.Removal);
            Assert.IsFalse(plan.Candidates);
            Assert.IsFalse(plan.Seams);
            Assert.IsFalse(plan.StatusLabel);
            Assert.IsFalse(plan.DrawsAnything);
        }

        [Test]
        public void RemovalOverlay_IsDrawnOnlyWhileItsToggleIsOn()
        {
            var on = ApaOverlayVisibility.For(false, true, true);
            var off = ApaOverlayVisibility.For(false, false, true);

            Assert.IsTrue(on.Removal);
            Assert.IsFalse(off.Removal, "The red overlay is off by default and stays off until asked for.");
            Assert.IsTrue(on.Candidates);
            Assert.IsTrue(on.Seams);
            Assert.IsTrue(off.Candidates);
            Assert.IsTrue(off.Seams);

            // The label follows the removal toggle: with the overlay armed the author can read the removal count.
            Assert.IsTrue(on.StatusLabel);
            Assert.IsTrue(off.StatusLabel, "The candidate switch is still active while the removal switch is off.");
        }

        [Test]
        public void MergeCheck_HidesTheOrdinaryLayersAndWins()
        {
            var plan = ApaOverlayVisibility.For(true, true, true);

            Assert.IsTrue(plan.MergeCheck);
            Assert.IsFalse(plan.Removal, "The mode hides the ordinary layers rather than stacking on them.");
            Assert.IsFalse(plan.Candidates);
            Assert.IsFalse(plan.Seams);
            Assert.IsTrue(plan.StatusLabel, "The merge check's result is a count, and the count must be readable.");
            Assert.IsTrue(plan.DrawsAnyLayer);
        }

        [Test]
        public void DefaultToggles_DrawTheCandidateAndSeamLayersOnly()
        {
            // A fresh window: candidate overlay on, removal and merge-check off.
            var plan = ApaOverlayVisibility.For(false, false, true);

            Assert.IsFalse(plan.Removal);
            Assert.IsTrue(plan.Candidates);
            Assert.IsTrue(plan.Seams);
            Assert.IsFalse(plan.MergeCheck);
        }

        [Test]
        public void ThePlanIsAPureFunctionOfTheThreeToggles()
        {
            // Every combination is defined, so no toggle can leave a layer in an undefined state.
            for (var bits = 0; bits < 8; bits++)
            {
                var mergeCheck = (bits & 2) != 0;
                var removal = (bits & 4) != 0;
                var candidates = (bits & 1) != 0;

                var plan = ApaOverlayVisibility.For(mergeCheck, removal, candidates);

                Assert.AreEqual(mergeCheck, plan.MergeCheck);
                Assert.AreEqual(!mergeCheck && removal, plan.Removal);
                Assert.AreEqual(!mergeCheck && candidates, plan.Candidates);
                Assert.AreEqual(!mergeCheck && candidates, plan.Seams);
                Assert.AreEqual(mergeCheck || removal || candidates, plan.StatusLabel);
            }
        }

        // ---- The Scene View tool -----------------------------------------------------------------------

        [Test]
        public void SceneTool_DrawsThroughTheOneGatingFunction()
        {
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            StringAssert.Contains(
                "var plan = ApaOverlayVisibility.For(",
                tool,
                "The draw path must ask the pure rule which layers to draw, not re-derive the precedence inline.");
            StringAssert.Contains("if (plan.MergeCheck)", tool);
            StringAssert.Contains("if (plan.Removal) DrawRemoval(host, selection);", tool);
            StringAssert.Contains("if (plan.Candidates) DrawCandidates(host, selection);", tool);
            StringAssert.Contains("if (plan.Seams) DrawSeams(host, selection);", tool);
            StringAssert.Contains("if (plan.StatusLabel) DrawStatusLabel(host, selection, plan);", tool);
            Assert.IsFalse(
                tool.Contains("if (host.MergeCheckOverlay)"),
                "The precedence must have exactly one implementation: the pure rule.");
        }

        [Test]
        public void SceneTool_HasNoPickingPathLeft()
        {
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            foreach (var gone in new[]
                     {
                         "AddDefaultControl",
                         "TryPickTriangle",
                         "EventType.MouseDown",
                         "ToggleRemovalTriangle",
                         "RemoveRemovalTriangle",
                         "RepaintAuthoringWindow",
                         "ApaSceneToolMode",
                         "_hoveredTriangle"
                     })
            {
                Assert.IsFalse(tool.Contains(gone), "The read-only overlay must not carry the removed path: " + gone);
            }

            StringAssert.Contains(
                "currentEvent.type != EventType.Repaint",
                tool,
                "The tool draws on the repaint event and consumes nothing.");
            Assert.IsFalse(
                tool.Contains("currentEvent.Use()"),
                "Nothing in a read-only overlay may consume a Scene View event.");
        }

        [Test]
        public void SceneTool_DoesNotCarryAHiddenMasterGate()
        {
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            Assert.IsFalse(tool.Contains("ShowHighlights"));
            Assert.IsFalse(tool.Contains("plan.Highlights"));
            Assert.IsFalse(
                tool.Contains("removal overlay: hidden"),
                "The old label described a picking mode that no longer exists.");
        }

        [Test]
        public void SceneTool_DoesNotReadEditorStylesDuringStaticInitialization()
        {
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            Assert.IsFalse(
                Regex.IsMatch(tool, @"static\s+readonly\s+GUIStyle\s+\w+\s*=\s*new\s+GUIStyle\s*\(\s*EditorStyles\."),
                "EditorStyles is not safe during domain reload; the Scene View tool must be constructible from OnEnable.");
            StringAssert.Contains("private static GUIStyle StatusStyle", tool);
            StringAssert.Contains("Handles.zTest = CompareFunction.Always;", tool);
        }

        // ---- The callback ------------------------------------------------------------------------------

        [Test]
        public void Registry_InstallsTheCallbackOnceAndDropsDisposedTools()
        {
            ApaAuthoringSceneToolRegistry.Clear();
            Assert.AreEqual(0, ApaAuthoringSceneToolRegistry.RegisteredCount);

            var first = new ApaAuthoringSceneTool(new FakeHost());
            var second = new ApaAuthoringSceneTool(new FakeHost());

            ApaAuthoringSceneToolRegistry.Register(first);
            ApaAuthoringSceneToolRegistry.Register(second);
            ApaAuthoringSceneToolRegistry.Register(second);

            Assert.IsTrue(ApaAuthoringSceneToolRegistry.IsInstalled, "Registering a tool installs the hook.");
            Assert.AreEqual(2, ApaAuthoringSceneToolRegistry.RegisteredCount, "A tool is never registered twice.");

            // A null view is a no-op inside the tool, which is what makes this dispatch safe to drive in a test.
            ApaAuthoringSceneToolRegistry.Dispatch(null);

            first.Dispose();
            ApaAuthoringSceneToolRegistry.Dispatch(null);

            Assert.AreEqual(
                1,
                ApaAuthoringSceneToolRegistry.RegisteredCount,
                "A disposed tool is dropped rather than called with a destroyed host.");

            ApaAuthoringSceneToolRegistry.Unregister(second);
            Assert.AreEqual(0, ApaAuthoringSceneToolRegistry.RegisteredCount);

            second.Dispose();
        }

        [Test]
        public void Registry_IsTheOnlySceneViewCallbackOwner()
        {
            var registry = ReadEditorSource("Authoring", "ApaAuthoringSceneToolRegistry.cs");
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            StringAssert.Contains("[InitializeOnLoadMethod]", registry);
            StringAssert.Contains("SceneView.duringSceneGui += Dispatch;", registry);
            StringAssert.Contains("SceneView.duringSceneGui -= Dispatch;", registry);

            StringAssert.Contains(
                "ApaAuthoringSceneToolRegistry.Register(_sceneTool);",
                window,
                "The window contributes its tool to the registry instead of owning the callback.");
            StringAssert.Contains("ApaAuthoringSceneToolRegistry.Unregister(_sceneTool);", window);
            Assert.IsFalse(
                window.Contains("SceneView.duringSceneGui"),
                "A callback subscribed by one window instance is the 'missing callback' failure this removes.");
        }

        // ---- The window's toggles ----------------------------------------------------------------------

        [Test]
        public void Window_OverlaySwitchesLiveInTheTopToolbar()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            Assert.IsFalse(window.Contains("_showHighlights"));
            var removalToggle = window.IndexOf("var removalOverlay = GUILayout.Toggle(", StringComparison.Ordinal);
            var removalRepaint = window.IndexOf("SceneView.RepaintAll();", removalToggle, StringComparison.Ordinal);
            Assert.GreaterOrEqual(removalToggle, 0);
            Assert.Greater(removalRepaint, removalToggle);

            StringAssert.Contains("var candidateOverlay = GUILayout.Toggle(", window);
            StringAssert.Contains("var mergeCheck = GUILayout.Toggle(", window);
            StringAssert.Contains("Candidate Overlay", window);
        }

        [Test]
        public void Window_InvalidatesDerivedStateOnEveryInputChange()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            var reset = window.IndexOf("private void InvalidateDerivedState()", StringComparison.Ordinal);
            Assert.GreaterOrEqual(reset, 0, "The window must have one place that drops derived state.");

            var resetEnd = window.IndexOf("// ---- Drawing", reset, StringComparison.Ordinal);
            Assert.Greater(resetEnd, reset);
            var resetBody = window.Substring(reset, resetEnd - reset);

            StringAssert.Contains("SeamCandidates.Invalidate();", resetBody);
            StringAssert.Contains("_sceneTool?.InvalidateMeshCache();", resetBody);
            StringAssert.Contains("_maskResultText = string.Empty;", resetBody);
            StringAssert.Contains("_seamResultText = string.Empty;", resetBody);
            StringAssert.Contains("_seamColorInput = null;", resetBody);
            StringAssert.Contains("_existingProfileState = null;", resetBody);

            // The points at which the inputs are replaced: a selection change, an undo, a new draft, and a
            // profile load.
            StringAssert.Contains(
                "if (targetChanged || rootsChanged || armatureChanged) InvalidateDerivedState();",
                window,
                "A changed selection is a changed candidate set and a changed merge check.");

            var undo = window.IndexOf("private void OnUndoRedo()", StringComparison.Ordinal);
            var undoEnd = window.IndexOf("private void MarkSelectionDirty()", undo, StringComparison.Ordinal);
            Assert.Greater(undo, 0);
            Assert.Greater(undoEnd, undo);
            StringAssert.Contains(
                "InvalidateDerivedState();",
                window.Substring(undo, undoEnd - undo),
                "An undo can change a mesh in place without changing any identity the caches key on.");

            var apply = window.IndexOf("private void ApplyLoadedProfile(", StringComparison.Ordinal);
            Assert.Greater(apply, 0);
            var applyEnd = window.IndexOf("private static string ToProjectRelative(", apply, StringComparison.Ordinal);
            Assert.Greater(applyEnd, apply);
            var applyBody = window.Substring(apply, applyEnd - apply);

            StringAssert.Contains("ApaProfileLoader.Load(asset, _selection, profileAssetPath);", applyBody);
            StringAssert.Contains("_draft = loaded.Draft;", applyBody);
            StringAssert.Contains("InvalidateDerivedState();", applyBody);
            Assert.Less(
                applyBody.IndexOf("_draft = loaded.Draft;", StringComparison.Ordinal),
                applyBody.IndexOf("InvalidateDerivedState();", StringComparison.Ordinal),
                "The derived state is dropped after the new draft replaced the old one.");
            Assert.Less(
                applyBody.IndexOf("InvalidateDerivedState();", StringComparison.Ordinal),
                applyBody.IndexOf("ClearResults(", StringComparison.Ordinal),
                "The status line is written last, so the reset cannot clear it.");
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

        /// <summary>
        /// A host with nothing selected. Enough to build a tool — the draw path returns before touching any of
        /// these values when the view is null.
        /// </summary>
        private sealed class FakeHost : IApaAuthoringSceneHost
        {
            public ApaAuthoringSelection Selection { get; } = new ApaAuthoringSelection();

            public ApaRemovalMask Removal { get; } = new ApaRemovalMask();

            public ApaSeamSelection Seam { get; } = new ApaSeamSelection();

            public bool ShowCandidateOverlay => true;

            public ApaSeamColorCandidateResult TargetSeamCandidates => null;

            public ApaSeamColorCandidateResult PartSeamCandidates => null;

            public Color32 SeamCandidateColor => ApaSeamVertexColorCandidates.DefaultColor;

            public bool ShowRemovalOverlay => false;

            public bool MergeCheckOverlay => false;

            public float SeamTolerance => ApaSeamWorldMatcher.DefaultTolerance;
        }
    }
}

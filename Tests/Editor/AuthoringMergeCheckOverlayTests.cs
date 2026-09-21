using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the merge-check overlay: the prospective pairing of the resolved seam candidates, its matched
    /// and unmatched classification, and the precedence between the removal, candidate, seam, and merge-check
    /// overlays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classification is the whole feature, so it is driven against real renderers and real meshes and
    /// compared with a direct <see cref="ApaSeamWorldMatcher"/> run: "the overlay shows what generating the seam
    /// would write" is only true if both go through the same matcher, the same candidate policies, and the same
    /// tolerance. The toggle precedence is a property of the draw path, which needs a Scene View and a repaint,
    /// so it is pinned as a source contract the way the rest of this suite pins the overlay's pose behaviour.
    /// </para>
    /// <para>
    /// <b>The two sides are classified by different rules (M15).</b> The part side is a color filter, so every
    /// candidate it declares is either matched or unmatched. The target side is spatial, so an unclaimed body
    /// vertex is not a defect and is never reported as unmatched — the target classification tests below pin both
    /// halves of that asymmetry.
    /// </para>
    /// </remarks>
    public sealed class AuthoringMergeCheckOverlayTests
    {
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);
        private static readonly Color32 Red = new Color32(255, 0, 0, 255);

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

        // ---- Matched and unmatched classification ------------------------------------------------------

        [Test]
        public void Evaluate_ClassifiesEveryPartCandidateAsMatchedOrUnmatched()
        {
            var target = NewBody(new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) });
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var result = Evaluate(target, part);

            Assert.IsTrue(result.Succeeded, result.Issue != null ? result.Issue.Detail : string.Empty);
            Assert.IsNull(result.MatchIssue, "A run that paired something must not report a matcher diagnostic.");
            Assert.AreEqual(1, result.PairCount);
            CollectionAssert.AreEqual(new[] { 0 }, result.MatchedTargetIndices);
            CollectionAssert.AreEqual(new[] { 0 }, result.MatchedPartIndices);
            CollectionAssert.AreEqual(
                new[] { 1 },
                result.UnmatchedPartIndices,
                "The far part candidate has no target counterpart within the tolerance and is drawn red.");
            Assert.AreEqual(2, result.PartCandidateCount);
            Assert.IsNotEmpty(result.Describe(), "A classification must describe itself for the Scene View label.");
        }

        [Test]
        public void Evaluate_ClassifiesASpatialTargetWithoutRequiringTargetColors()
        {
            // The regression this contract exists for: a body mesh that carries no vertex colors at all, a part
            // whose selected color resolves, and coincident positions. The merge check must classify the pairing
            // instead of refusing the target side.
            var target = NewBody("Body", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, null);
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var result = Evaluate(target, part);

            Assert.IsTrue(result.Succeeded, result.Issue != null ? result.Issue.Detail : string.Empty);
            Assert.IsTrue(result.TargetIsSpatial, "The target side is spatial, not color-filtered.");
            Assert.AreEqual(1, result.PairCount);
            CollectionAssert.AreEqual(new[] { 0 }, result.MatchedTargetIndices);
            CollectionAssert.AreEqual(new[] { 0 }, result.MatchedPartIndices);
            Assert.AreEqual(
                2,
                result.TargetCandidateCount,
                "The spatial search space is every target vertex, which is what the label reports.");
            Assert.IsNotEmpty(result.Describe());
            StringAssert.Contains(
                "spatial",
                result.Describe(),
                "The summary must describe the target side as spatial rather than as a candidate list.");
        }

        [Test]
        public void Evaluate_SpatialTargetReportsNoUnmatchedTargetVertices()
        {
            // Two of the three body vertices are nowhere near the part. They are not defects — an unwelded body
            // vertex is true of every part — so the classification reports none of them, and the overlay draws no
            // red discs on the body.
            var target = NewBody("Body", new[] { Vector3.zero, new Vector3(9f, 9f, 9f), new Vector3(8f, 8f, 8f) }, null);
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var result = Evaluate(target, part);

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.TargetIsSpatial);
            Assert.AreEqual(3, result.TargetCandidateCount);
            Assert.IsEmpty(
                result.UnmatchedTargetIndices,
                "A spatial target has no nominated candidates, so none of its vertices can be 'unmatched'.");
            CollectionAssert.AreEqual(
                new[] { 1 },
                result.UnmatchedPartIndices,
                "The part side is still classified candidate by candidate.");
        }

        [Test]
        public void Evaluate_ClassifiesANonCandidatePartVertexAsNeitherMatchedNorUnmatched()
        {
            // Vertex 1 of the part is painted red, so it is not a candidate at all: it must not appear in either
            // list, or the overlay would draw a vertex the generator can never pair.
            var target = NewBody("Body", new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) }, null);
            var part = NewPart(new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, 1);

            var result = Evaluate(target, part);

            Assert.IsTrue(result.Succeeded);
            CollectionAssert.AreEqual(new[] { 0 }, result.MatchedPartIndices);
            CollectionAssert.AreEqual(new[] { 1 }, result.UnmatchedPartIndices);
            Assert.IsFalse(
                result.MatchedPartIndices.Contains(1) || result.UnmatchedPartIndices.Contains(1),
                "A non-candidate part vertex is never classified.");
            Assert.IsFalse(
                result.UnmatchedTargetIndices.Contains(1),
                "The target side is spatial, so its vertices are never classified as unmatched either.");
        }

        [Test]
        public void Evaluate_ReportsNoPairAsAMatchIssueAndMarksEveryPartCandidateUnmatched()
        {
            var target = NewBody(new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) });
            var part = NewPart(new[] { new Vector3(50f, 50f, 50f), new Vector3(60f, 60f, 60f) });

            var result = Evaluate(target, part);

            Assert.IsTrue(result.Succeeded, "The classification still exists when the matcher refuses the run.");
            Assert.IsNotNull(result.MatchIssue);
            Assert.AreEqual(ApaErrorCode.SeamPositionMismatch, result.MatchIssue.Code);
            StringAssert.Contains("reason=no-world-coincident-vertices", result.MatchIssue.Detail);
            Assert.AreEqual(0, result.PairCount);
            Assert.IsEmpty(result.UnmatchedTargetIndices);
            CollectionAssert.AreEqual(new[] { 0, 1 }, result.UnmatchedPartIndices);
        }

        [Test]
        public void Evaluate_IsUnavailableWhenThePartCandidateColorCouldNotBeResolved()
        {
            var target = NewBody(new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) });
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var targetCandidates = ResolveTarget(target);
            var partFailure = ApaSeamVertexColorCandidates.FromColors(
                new Color32[0], 2, Green, ApaSeamVertexColorCandidates.PartSide);

            var result = ApaSeamMergeCheck.Evaluate(
                target.Renderer,
                target.Mesh,
                part.Renderer,
                part.Mesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                targetCandidates,
                partFailure);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ApaErrorCode.SeamCandidateColorInvalid, result.Issue.Code);
            StringAssert.Contains("reason=vertex-color-missing", result.Issue.Detail);
            Assert.IsEmpty(result.MatchedTargetIndices);
            Assert.IsEmpty(result.UnmatchedTargetIndices);
            Assert.IsEmpty(result.MatchedPartIndices);
            Assert.IsEmpty(result.UnmatchedPartIndices);
        }

        [Test]
        public void Evaluate_ReportsAnUnresolvedSideAsABlockingDiagnosticInsteadOfThrowing()
        {
            var target = NewBody(new[] { Vector3.zero });
            var part = NewPart(new[] { Vector3.zero });

            var result = ApaSeamMergeCheck.Evaluate(
                target.Renderer,
                target.Mesh,
                part.Renderer,
                part.Mesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                null,
                null);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ApaErrorCode.SeamCandidateColorInvalid, result.Issue.Code);
            StringAssert.Contains("reason=vertex-color-candidates-unresolved", result.Issue.Detail);
        }

        // ---- The same rules as seam generation ---------------------------------------------------------

        [Test]
        public void Evaluate_UsesTheSameMatcherCandidatePoliciesAndToleranceAsSeamGeneration()
        {
            var target = NewBody(new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) });
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var targetCandidates = ResolveTarget(target);
            var partCandidates = ResolvePart(part);
            const float tolerance = 1e-5f;

            var mergeCheck = ApaSeamMergeCheck.Evaluate(
                target.Renderer,
                target.Mesh,
                part.Renderer,
                part.Mesh,
                tolerance,
                targetCandidates,
                partCandidates);

            var direct = ApaSeamWorldMatcher.Match(
                target.Renderer,
                target.Mesh,
                part.Renderer,
                part.Mesh,
                tolerance,
                targetCandidates.MatcherCandidateIndices,
                partCandidates.Indices);

            Assert.IsTrue(direct.Succeeded);
            CollectionAssert.AreEqual(direct.BaseIndices, mergeCheck.MatchedTargetIndices);
            CollectionAssert.AreEqual(direct.PartIndices, mergeCheck.MatchedPartIndices);
            Assert.AreEqual(tolerance, mergeCheck.Tolerance);
        }

        [Test]
        public void Evaluate_IsDeterministicForIdenticalInputs()
        {
            var target = NewBody(new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) });
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var first = Evaluate(target, part);
            var second = Evaluate(target, part);

            CollectionAssert.AreEqual(first.MatchedTargetIndices, second.MatchedTargetIndices);
            CollectionAssert.AreEqual(first.MatchedPartIndices, second.MatchedPartIndices);
            CollectionAssert.AreEqual(first.UnmatchedTargetIndices, second.UnmatchedTargetIndices);
            CollectionAssert.AreEqual(first.UnmatchedPartIndices, second.UnmatchedPartIndices);
        }

        /// <summary>
        /// The reported regression, in one test: the classification is bind-pose data, while the discs that show
        /// it must follow the pose. Moving a bone therefore changes <i>where</i> the overlay draws and not
        /// <i>what</i> it says — a pairing derived from a posed body would rewrite the seam the moment the author
        /// dragged a bone.
        /// </summary>
        [Test]
        public void Evaluate_KeepsTheBindPoseClassificationWhileTheDisplayedPositionsFollowThePose()
        {
            var target = NewBody(new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) });
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var bone = new GameObject("Hips");
            _created.Add(bone);
            bone.transform.position = new Vector3(0f, 1f, 0f);

            part.Renderer.bones = new[] { bone.transform };
            part.Mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f }
            };
            part.Mesh.bindposes = new[] { Matrix4x4.identity };

            var before = Evaluate(target, part);

            Assert.IsTrue(
                ApaPreviewSkinning.TrySolveLocalPositions(part.Renderer, part.Mesh, out var rest),
                "The overlay's positions come from the renderer's current pose.");
            Assert.AreEqual(new Vector3(0f, 1f, 0f), rest[0]);

            // A pose edit: the bone moves, and with it every vertex the overlay draws.
            bone.transform.position = new Vector3(0f, 4f, 0f);
            Assert.IsTrue(ApaPreviewSkinning.TrySolveLocalPositions(part.Renderer, part.Mesh, out var posed));
            Assert.AreEqual(new Vector3(0f, 4f, 0f), posed[0]);
            Assert.AreNotEqual(rest[0], posed[0], "A moved bone must move the disc.");

            var after = Evaluate(target, part);

            CollectionAssert.AreEqual(
                before.MatchedTargetIndices,
                after.MatchedTargetIndices,
                "The pairing is decided on the bind pose and must not follow the bone.");
            CollectionAssert.AreEqual(before.MatchedPartIndices, after.MatchedPartIndices);
            CollectionAssert.AreEqual(before.UnmatchedPartIndices, after.UnmatchedPartIndices);
            Assert.AreEqual(before.PairCount, after.PairCount);
            Assert.IsNull(after.MatchIssue);
        }

        // ---- Nothing is written ------------------------------------------------------------------------

        [Test]
        public void Evaluate_DoesNotTouchTheStoredSeamOrTakeOneAsInput()
        {
            // The API has no seam or draft parameter at all: the overlay cannot write profile data by
            // construction, not merely by discipline.
            var parameters = typeof(ApaSeamMergeCheck)
                .GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Static)
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray();

            Assert.IsFalse(parameters.Contains(typeof(ApaSeamSelection)));
            Assert.IsFalse(parameters.Contains(typeof(ApaProfileDraft)));
            Assert.IsFalse(parameters.Contains(typeof(ApaRemovalMask)));

            var seam = new ApaSeamSelection();
            seam.SetPaired(new[] { 0 }, new[] { 1 });
            var beforeBase = seam.GetBaseIndices();
            var beforePart = seam.GetPartIndices();

            var target = NewBody(new[] { Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(9f, 9f, 9f) });
            var part = NewPart(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });
            var result = Evaluate(target, part);

            Assert.AreEqual(1, seam.PairCount, "The stored seam must be exactly as it was.");
            CollectionAssert.AreEqual(beforeBase, seam.GetBaseIndices());
            CollectionAssert.AreEqual(beforePart, seam.GetPartIndices());
            Assert.AreNotSame(beforeBase, result.MatchedTargetIndices, "The result carries fresh arrays.");
        }

        // ---- Toggle precedence -------------------------------------------------------------------------

        [Test]
        public void SceneTool_MergeCheckHidesTheRemovalCandidateAndSeamOverlays()
        {
            // The precedence is the pure rule's, so it is asserted there; this pins that the draw path is the
            // rule's only consumer and that the merge check cannot write anything.
            var source = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            var mergeCheck = source.IndexOf("if (plan.MergeCheck)", StringComparison.Ordinal);
            var removalDraw = source.IndexOf("if (plan.Removal) DrawRemoval(host, selection);", StringComparison.Ordinal);
            var candidateDraw = source.IndexOf("if (plan.Candidates) DrawCandidates(host, selection);", StringComparison.Ordinal);
            var seamDraw = source.IndexOf("if (plan.Seams) DrawSeams(host, selection);", StringComparison.Ordinal);

            Assert.GreaterOrEqual(mergeCheck, 0, "The merge-check mode must gate the draw path.");
            Assert.Greater(removalDraw, mergeCheck, "The removal overlay is drawn after the merge-check test.");
            Assert.Greater(candidateDraw, removalDraw, "The candidate overlay is drawn after the removal overlay.");
            Assert.Greater(seamDraw, candidateDraw, "The seam overlay is drawn after the candidate overlay.");

            var branch = source.Substring(mergeCheck, candidateDraw - mergeCheck);
            StringAssert.Contains("DrawMergeCheck(host, selection);", branch);
            StringAssert.Contains(
                "else",
                branch,
                "The ordinary overlays must sit in the else branch, so the mode hides rather than overlays.");

            var draw = source.Substring(mergeCheck, seamDraw - mergeCheck);
            Assert.IsFalse(
                draw.Contains("mask.Remove(") || draw.Contains("SetPaired"),
                "Drawing the merge check must not mutate the removal set or write the stored seam.");
        }

        [Test]
        public void SceneTool_IsReadOnlyAndHonorsTheRemovalOverlayToggle()
        {
            var source = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            // The removal layer is drawn only while its toggle is on, and the plan is what says so.
            StringAssert.Contains("if (plan.Removal) DrawRemoval(host, selection);", source);
            StringAssert.Contains(
                "ApaOverlayVisibility.For(\n                host.MergeCheckOverlay,\n                host.ShowRemovalOverlay,\n                host.ShowCandidateOverlay);",
                source,
                "The tool reads the window's three toggles through the one gating function.");

            // And nothing in the tool edits anything: the pick mode, the hover state, and the click handler are
            // gone with mask-only removal authoring.
            foreach (var gone in new[]
                     {
                         "ToggleRemovalTriangle",
                         "RemoveRemovalTriangle",
                         "TryPickTriangle",
                         "AddDefaultControl",
                         "_hoveredTriangle"
                     })
            {
                Assert.IsFalse(source.Contains(gone), "No manual removal path may remain: " + gone);
            }

            Assert.IsFalse(
                ReadEditorSource("Authoring", "ApaAuthoringWindow.cs").Contains("ToggleRemovalTriangle"),
                "The window must not implement the removed host methods either.");
        }

        [Test]
        public void Window_DefaultsBothOverlayTogglesOffAndKeepsTheMergeCheckFromResettingThem()
        {
            var source = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            Assert.IsTrue(
                source.Contains("[SerializeField] private bool _showRemovalOverlay;"),
                "The removal overlay must default off: a bool field with no initializer is false.");
            Assert.IsTrue(
                source.Contains("[SerializeField] private bool _mergeCheckOverlay;"),
                "The merge-check mode must default off.");
            Assert.IsFalse(
                source.Contains("[SerializeField] private bool _showRemovalOverlay = true;"),
                "The removal overlay must not default on.");
            Assert.IsFalse(
                source.Contains("[SerializeField] private bool _mergeCheckOverlay = true;"),
                "The merge-check mode must not default on.");

            Assert.IsFalse(
                source.Contains("_showRemovalOverlay = false;"),
                "Nothing may reset the removal toggle when the merge-check mode is left: the mode hides the " +
                "overlays, so the author's prior choices must survive it.");
            Assert.IsFalse(
                source.Contains("_mergeCheckOverlay = false;"),
                "The mode is only ever set by its own control.");

            StringAssert.Contains(
                "_mergeCheckOverlay = mergeCheck;",
                source,
                "The merge-check toggle is the only writer of its field.");
        }

        [Test]
        public void Window_OverlaySwitchesAreOnlyInTheToolbar()
        {
            var source = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            Assert.IsFalse(source.Contains("_showHighlights"));
            Assert.GreaterOrEqual(
                source.IndexOf("var removalOverlay = GUILayout.Toggle(", StringComparison.Ordinal),
                0);
            Assert.GreaterOrEqual(
                source.IndexOf("var candidateOverlay = GUILayout.Toggle(", StringComparison.Ordinal),
                0);
            Assert.GreaterOrEqual(
                source.IndexOf("var mergeCheck = GUILayout.Toggle(", StringComparison.Ordinal),
                0);
            Assert.IsFalse(
                source.Contains("EditorGUILayout.Toggle(\n                Content(\n                    \"Removal Overlay\""),
                "The removal switch belongs in the shared top toolbar, not in the mask section.");
        }

        [Test]
        public void Window_ExposesTheCandidateColorAndBothTogglesToTheSceneTool()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            StringAssert.Contains("public Color32 SeamCandidateColor => _seamCandidateColor;", window);
            StringAssert.Contains("public bool ShowRemovalOverlay => _showRemovalOverlay;", window);
            StringAssert.Contains("public bool ShowCandidateOverlay => _showCandidateOverlay;", window);
            StringAssert.Contains("public bool MergeCheckOverlay => _mergeCheckOverlay;", window);
            StringAssert.Contains("public float SeamTolerance => _seamTolerance;", window);

            // The tool reads the window's cached resolution rather than resolving the colors itself, so one
            // repaint never reads Mesh.colors32 twice.
            StringAssert.Contains("ApaSeamColorCandidateResult TargetSeamCandidates { get; }", tool);
            StringAssert.Contains("ApaSeamColorCandidateResult PartSeamCandidates { get; }", tool);
            StringAssert.Contains("Color32 SeamCandidateColor { get; }", tool);
        }

        [Test]
        public void Window_ResolvesTheTargetSpatiallyAndFiltersOnlyThePartByColor()
        {
            var source = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            StringAssert.Contains(
                "ApaSeamSpatialTargetCandidates.Resolve(_selection.TargetRenderer, _selection.TargetMesh)",
                source,
                "The target side must be resolved spatially: a body mesh without vertex colors is a normal body.");
            StringAssert.Contains(
                "ApaSeamVertexColorCandidates.ResolvePartCandidates(",
                source,
                "The part side is the color filter.");
            StringAssert.Contains(
                "targetCandidates.MatcherCandidateIndices,",
                source,
                "The spatial target reaches the matcher as its all-vertices argument, not as an empty index list.");
            Assert.IsFalse(
                source.Contains("ApaSeamVertexColorCandidates.Resolve(\n                _selection.TargetRenderer"),
                "The window must never resolve the target side from vertex colors again.");
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private ApaSeamMergeCheckResult Evaluate(Body target, Body part)
        {
            return ApaSeamMergeCheck.Evaluate(
                target.Renderer,
                target.Mesh,
                part.Renderer,
                part.Mesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                ResolveTarget(target),
                ResolvePart(part));
        }

        /// <summary>The target side is spatial: every body vertex is a candidate, colors or not.</summary>
        private static ApaSeamColorCandidateResult ResolveTarget(Body body)
        {
            return ApaSeamSpatialTargetCandidates.Resolve(body.Renderer, body.Mesh);
        }

        /// <summary>The part side is the color filter, which is the only side the selected color restricts.</summary>
        private static ApaSeamColorCandidateResult ResolvePart(Body body)
        {
            return ApaSeamVertexColorCandidates.ResolvePartCandidates(body.Renderer, body.Mesh, Green);
        }

        private Body NewBody(Vector3[] vertices)
        {
            // The body is a spatial search space, so its own colors are irrelevant: it is built without a color
            // channel on purpose, which is the regression this contract exists for.
            return NewBody("Body", vertices, null);
        }

        /// <summary>
        /// A part whose vertices all carry the candidate color except <paramref name="nonCandidateIndex"/>, which
        /// is painted red so the classification tests have a vertex the color excludes.
        /// </summary>
        private Body NewPart(Vector3[] vertices, int nonCandidateIndex = -1)
        {
            var colors = new Color32[vertices.Length];
            for (var i = 0; i < colors.Length; i++) colors[i] = i == nonCandidateIndex ? Red : Green;
            return NewBody("Part", vertices, colors);
        }

        private Body NewBody(string name, Vector3[] vertices, Color32[] colors)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            if (colors != null) mesh.colors32 = colors;
            _created.Add(mesh);

            var host = new GameObject(name);
            _created.Add(host);
            var renderer = host.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;

            return new Body(mesh, renderer);
        }

        private static string ReadEditorSource(string folder, string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", folder, fileName);

            if (!File.Exists(path)) Assert.Ignore("Editor source not found: " + path);

            return File.ReadAllText(path);
        }

        private sealed class Body
        {
            public Body(Mesh mesh, SkinnedMeshRenderer renderer)
            {
                Mesh = mesh;
                Renderer = renderer;
            }

            public Mesh Mesh { get; }

            public SkinnedMeshRenderer Renderer { get; }
        }
    }
}

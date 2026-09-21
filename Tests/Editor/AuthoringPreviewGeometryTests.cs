using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the Scene View preview's evaluated-geometry decision and cache: when the renderer's drawn
    /// geometry differs from the mesh's rest pose, how the bake is keyed and invalidated, and that every failure
    /// degrades to the rest pose instead of throwing out of a repaint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bake itself is a <see cref="SkinnedMeshRenderer.BakeMesh"/> call, which needs a graphics device and a
    /// posed rig; it is isolated behind <see cref="IApaEvaluatedMeshBaker"/> so everything that decides
    /// <i>whether</i> and <i>how often</i> to bake is testable with a deterministic stand-in. The production
    /// baker is exercised only on its early-return paths, which are pure.
    /// </para>
    /// <para>
    /// Nothing here mutates a source mesh or a renderer: the evaluated positions are a separate array, and the
    /// rest-pose half is still the mesh's own <c>vertices</c>.
    /// </para>
    /// </remarks>
    public sealed class AuthoringPreviewGeometryTests
    {
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

        // ---- When the evaluated geometry is needed -----------------------------------------------------

        [Test]
        public void NeedsEvaluatedGeometry_IsFalseWithoutActiveBlendShapeWeights()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);

            Assert.IsFalse(
                ApaPreviewGeometry.NeedsEvaluatedGeometry(renderer, mesh),
                "With every weight at zero the drawn geometry is the rest pose, so no bake is needed.");

            renderer.SetBlendShapeWeight(0, 40f);

            Assert.IsTrue(ApaPreviewGeometry.NeedsEvaluatedGeometry(renderer, mesh));
        }

        [Test]
        public void NeedsEvaluatedGeometry_CountsANegativeWeightAsActive()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            renderer.SetBlendShapeWeight(0, -25f);

            Assert.IsTrue(
                ApaPreviewGeometry.HasActiveBlendShapeWeights(renderer, mesh),
                "A negative weight deforms the mesh exactly as a positive one does.");
        }

        [Test]
        public void NeedsEvaluatedGeometry_IsFalseForNonSkinnedOrMismatchedInputs()
        {
            var mesh = NewBlendedMesh("Body", 2);

            Assert.IsFalse(ApaPreviewGeometry.NeedsEvaluatedGeometry(null, mesh));
            Assert.IsFalse(ApaPreviewGeometry.NeedsEvaluatedGeometry(NewMeshRenderer("Static"), mesh));

            var renderer = NewSkinnedRenderer("Body", mesh);
            renderer.SetBlendShapeWeight(0, 100f);

            var other = NewBlendedMesh("Other", 2);
            Assert.IsFalse(
                ApaPreviewGeometry.NeedsEvaluatedGeometry(renderer, other),
                "A mesh the renderer does not hold cannot be baked against its blend shape list.");

            Assert.IsFalse(ApaPreviewGeometry.HasActiveBlendShapeWeights(null, mesh));
            Assert.IsFalse(ApaPreviewGeometry.HasActiveBlendShapeWeights(renderer, null));
        }

        [Test]
        public void Fingerprint_TracksTheWeightsAndTheRootBoneButNotTheRendererTransform()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            var root = NewGameObject("Root").transform;
            renderer.rootBone = root;

            var initial = ApaPreviewGeometry.Fingerprint(renderer, mesh);

            // Baked vertices are in the renderer's local space, so moving the renderer itself must not invalidate
            // the bake: the transform is applied when the vertices are drawn.
            renderer.transform.position = new Vector3(3f, 4f, 5f);
            renderer.transform.localScale = new Vector3(2f, 2f, 2f);
            Assert.AreEqual(initial, ApaPreviewGeometry.Fingerprint(renderer, mesh));

            renderer.SetBlendShapeWeight(0, 30f);
            var posed = ApaPreviewGeometry.Fingerprint(renderer, mesh);
            Assert.AreNotEqual(initial, posed);

            root.position = new Vector3(0f, 1f, 0f);
            Assert.AreNotEqual(posed, ApaPreviewGeometry.Fingerprint(renderer, mesh));
        }

        [Test]
        public void Fingerprint_IsStableForIdenticalInputs()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            renderer.SetBlendShapeWeight(0, 12.5f);

            Assert.AreEqual(
                ApaPreviewGeometry.Fingerprint(renderer, mesh),
                ApaPreviewGeometry.Fingerprint(renderer, mesh));
            Assert.AreEqual(
                ApaPreviewGeometry.OffsetBasis,
                ApaPreviewGeometry.Fingerprint(null, null));
        }

        // ---- The cache ---------------------------------------------------------------------------------

        [Test]
        public void TryRead_UsesTheEvaluatedPositionsWhileABlendShapeIsActive()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            renderer.SetBlendShapeWeight(0, 60f);

            var baker = new FakeBaker(new[] { new Vector3(1f, 1f, 1f), new Vector3(2f, 2f, 2f) });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var positions, out var source));

                Assert.AreEqual(ApaPreviewPositionSource.Evaluated, source);
                CollectionAssert.AreEqual(baker.Positions, positions);
                Assert.AreEqual(1, baker.Calls);
            }
        }

        [Test]
        public void TryRead_ReusesTheBakeUntilTheWeightChangesAndRebakesAfterInvalidation()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            renderer.SetBlendShapeWeight(0, 60f);

            var baker = new FakeBaker(new[] { new Vector3(1f, 1f, 1f), new Vector3(2f, 2f, 2f) });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var first, out _));
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var second, out _));

                Assert.AreEqual(1, baker.Calls, "An unchanged renderer must not re-bake on every repaint.");
                Assert.AreSame(first, second);

                // A weight change is what an animation or a gesture preview does, and it must be picked up.
                renderer.SetBlendShapeWeight(0, 75f);
                Assert.IsTrue(cache.TryRead(renderer, mesh, out _, out _));
                Assert.AreEqual(2, baker.Calls);

                // An undo can change the mesh or a weight without changing any key, which is what the explicit
                // invalidation is for.
                cache.Invalidate();
                Assert.IsTrue(cache.TryRead(renderer, mesh, out _, out _));
                Assert.AreEqual(3, baker.Calls);
            }
        }

        [Test]
        public void TryRead_DoesNotBakeAtAllWithoutAnActiveBlendShape()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);

            var baker = new FakeBaker(new[] { Vector3.one, Vector3.one });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var positions, out var source));

                Assert.AreEqual(ApaPreviewPositionSource.RestPose, source);
                Assert.AreEqual(0, baker.Calls, "With no active weight the rest pose is already the drawn pose.");
                CollectionAssert.AreEqual(mesh.vertices, positions);
            }
        }

        [Test]
        public void TryRead_FallsBackToTheRestPoseWhenTheBakeFailsOrIsTheWrongSize()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            renderer.SetBlendShapeWeight(0, 60f);

            var failing = new FakeBaker(null);
            using (var cache = new ApaPreviewPositionCache(failing))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var positions, out var source));

                Assert.AreEqual(ApaPreviewPositionSource.RestPose, source);
                CollectionAssert.AreEqual(mesh.vertices, positions);
                Assert.AreEqual(1, failing.Calls);
            }

            // A bake that returns a vertex count other than the mesh's is as unusable as a failed one, because
            // the overlay indexes it with the mesh's own triangle indices.
            var truncated = new FakeBaker(new[] { Vector3.one });
            using (var cache = new ApaPreviewPositionCache(truncated))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out _, out var source));
                Assert.AreEqual(ApaPreviewPositionSource.RestPose, source);
            }
        }

        [Test]
        public void TryRead_IgnoresTheEvaluatedPathWhenTheRendererHoldsADifferentMesh()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var other = NewBlendedMesh("Other", 2);
            var renderer = NewSkinnedRenderer("Body", other);
            renderer.SetBlendShapeWeight(0, 60f);

            var baker = new FakeBaker(new[] { Vector3.one, Vector3.one });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var positions, out var source));

                Assert.AreEqual(ApaPreviewPositionSource.RestPose, source);
                Assert.AreEqual(0, baker.Calls);
                CollectionAssert.AreEqual(mesh.vertices, positions);
            }
        }

        [Test]
        public void TryRead_ReturnsFalseForAMissingMeshAndCachesNothing()
        {
            var baker = new FakeBaker(new[] { Vector3.one });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsFalse(cache.TryRead(null, null, out var positions, out var source));
                Assert.IsNull(positions);
                Assert.AreEqual(ApaPreviewPositionSource.RestPose, source);
                Assert.AreEqual(0, baker.Calls);
            }
        }

        [Test]
        public void Dispose_DoesNotDisposeABakerTheCallerOwns()
        {
            var baker = new FakeBaker(new[] { Vector3.one });

            // A caller-supplied baker is the caller's to release; only the cache's own default baker is owned.
            var cache = new ApaPreviewPositionCache(baker);
            cache.Dispose();

            Assert.IsFalse(baker.Disposed);
            cache.Dispose();
        }

        // ---- The production baker's pure paths ---------------------------------------------------------

        [Test]
        public void SkinnedMeshBaker_RefusesInputsItCannotBakeWithoutTouchingTheRenderer()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var other = NewBlendedMesh("Other", 2);
            var renderer = NewSkinnedRenderer("Body", other);

            var baker = new ApaSkinnedMeshBaker();
            try
            {
                Assert.IsFalse(baker.TryBakeLocalPositions(null, mesh, out var none));
                Assert.IsNull(none);

                Assert.IsFalse(baker.TryBakeLocalPositions(renderer, null, out none));
                Assert.IsNull(none);

                Assert.IsFalse(
                    baker.TryBakeLocalPositions(renderer, mesh, out none),
                    "The renderer must hold the mesh whose blend shapes are evaluated.");
                Assert.IsNull(none);
            }
            finally
            {
                baker.Dispose();
            }

            // Disposal is idempotent: a window can be disabled more than once.
            baker.Dispose();
            Assert.AreEqual("ApaPreviewBakedMesh", ApaSkinnedMeshBaker.BakedMeshName);
        }

        // ---- Source contracts: which half reads which pose ---------------------------------------------

        /// <summary>
        /// The Scene View tool draws the removal overlay and runs both picks through the evaluated-geometry
        /// cache, draws the candidate and merge-check overlays from the rest-pose arrays seam generation reads,
        /// and releases the baker when the window closes.
        /// </summary>
        /// <remarks>
        /// A structural check, because the alternative is standing up a Scene View and a repaint. The two halves
        /// are deliberately different since M14: the red removal overlay and its picks describe the geometry the
        /// author sees, so they follow a blend-shape pose, while the green candidate overlay and the merge check
        /// describe the <i>input</i> of seam generation, which is rest-pose data. What matters is that neither
        /// half reads the wrong source.
        /// </remarks>
        [Test]
        public void SceneTool_DrawsTheRemovalOverlayAndPicksFromEvaluatedGeometryAndTheCandidatesFromRestPose()
        {
            var source = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            Assert.GreaterOrEqual(
                Regex.Matches(source, @"_targetPreview\.TryRead\(").Count,
                3,
                "The removal overlay, the hover pick, and the click pick must all read the preview positions.");

            Assert.IsFalse(
                Regex.IsMatch(source, @"_targetArrays\.TryRead\([^)]*out var vertices"),
                "The removal overlay must not take its positions from the rest-pose array cache.");

            // The candidate overlay and the merge check read mesh.vertices, exactly as ApaSeamWorldMatcher does.
            Assert.GreaterOrEqual(
                Regex.Matches(source, @"arrays\.TryRead\(mesh, out var vertices, out _\)").Count,
                2,
                "The candidate overlay and the merge check must read the rest-pose vertex array.");

            StringAssert.Contains("_baker.Dispose()", source, "The transient bake mesh must be released.");
            StringAssert.Contains("public void InvalidateMeshCache()", source);
        }

        [Test]
        public void AuthoringWindow_DisposesTheSceneToolAndWiresTheCandidatesIntoSeamGeneration()
        {
            var source = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            StringAssert.Contains(
                "_sceneTool?.Dispose()",
                source,
                "Closing the window must release the Scene View tool's transient mesh.");

            StringAssert.Contains("ApaSeamVertexColorCandidates.Resolve(", source);
            StringAssert.Contains("targetCandidates.Indices", source);
            StringAssert.Contains("partCandidates.Indices", source);
            Assert.IsFalse(
                Regex.IsMatch(
                    source,
                    @"ApaSeamWorldMatcher\.Match\(\s*_selection\.TargetRenderer,\s*_selection\.TargetMesh," +
                    @"\s*_selection\.PartRenderer,\s*_selection\.PartMesh,\s*_seamTolerance\)"),
                "Seam generation must pass both candidate lists, never fall back to the all-vertices overload.");
        }

        [Test]
        public void SeamGeneration_StaysOnTheRestPose()
        {
            var matcher = ReadEditorSource("Authoring", "ApaSeamWorldMatcher.cs");

            StringAssert.Contains("targetMesh.vertices", matcher);
            StringAssert.Contains("partMesh.vertices", matcher);
            Assert.IsFalse(
                matcher.Contains("BakeMesh"),
                "Seam generation and build data read the bind geometry, never a posed bake.");
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

        private sealed class FakeBaker : IApaEvaluatedMeshBaker, System.IDisposable
        {
            public FakeBaker(Vector3[] positions)
            {
                Positions = positions;
            }

            public Vector3[] Positions { get; }

            public int Calls { get; private set; }

            public bool Disposed { get; private set; }

            public bool TryBakeLocalPositions(
                SkinnedMeshRenderer renderer,
                Mesh mesh,
                out Vector3[] localPositions)
            {
                Calls++;
                localPositions = Positions;
                return Positions != null;
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private GameObject NewGameObject(string name)
        {
            var host = new GameObject(name);
            _created.Add(host);
            return host;
        }

        private Mesh NewMesh(string name, int vertexCount)
        {
            var vertices = new Vector3[vertexCount];
            for (var i = 0; i < vertexCount; i++) vertices[i] = new Vector3(i, 0f, 0f);

            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            _created.Add(mesh);
            return mesh;
        }

        private Mesh NewBlendedMesh(string name, int vertexCount)
        {
            var mesh = NewMesh(name, vertexCount);

            var deltas = new Vector3[vertexCount];
            for (var i = 0; i < vertexCount; i++) deltas[i] = new Vector3(0f, 1f, 0f);
            mesh.AddBlendShapeFrame("Open", 100f, deltas, null, null);

            return mesh;
        }

        private SkinnedMeshRenderer NewSkinnedRenderer(string name, Mesh mesh)
        {
            var renderer = NewGameObject(name).AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }

        private MeshRenderer NewMeshRenderer(string name)
        {
            return NewGameObject(name).AddComponent<MeshRenderer>();
        }
    }
}

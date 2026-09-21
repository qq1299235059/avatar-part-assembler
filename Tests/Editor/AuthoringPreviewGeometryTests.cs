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
    /// geometry differs from the mesh's rest pose, how the solve is keyed and invalidated, and that every failure
    /// degrades to the rest pose instead of throwing out of a repaint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A live mesh's bake is a <see cref="SkinnedMeshRenderer.BakeMesh"/> call, which needs a graphics device and a
    /// posed rig; it is isolated behind <see cref="IApaEvaluatedMeshBaker"/> so everything that decides
    /// <i>whether</i> and <i>how often</i> to evaluate is testable with a deterministic stand-in. A protected
    /// part's renderer holds no mesh, so its pose is solved in memory by <see cref="ApaPreviewSkinning"/>, and
    /// that half is exercised for real here: it needs no graphics device and no Scene View.
    /// </para>
    /// <para>
    /// Nothing here mutates a source mesh or a renderer: the evaluated positions are a separate array, the
    /// rest-pose half is still the mesh's own <c>vertices</c>, and a protected renderer is left with no mesh.
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
        public void NeedsEvaluatedGeometry_IsTrueForASkinnedRendererWithEveryWeightAtZero()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);

            // The bones are evaluated on every frame, so the drawn vertices are the current pose even when no
            // blend shape contributes: a moved bone is enough to separate them from mesh.vertices.
            Assert.IsTrue(
                ApaPreviewGeometry.NeedsEvaluatedGeometry(renderer, mesh),
                "A skinned renderer draws its current pose, not the mesh's bind pose.");

            renderer.SetBlendShapeWeight(0, 40f);

            Assert.IsTrue(ApaPreviewGeometry.NeedsEvaluatedGeometry(renderer, mesh));
        }

        [Test]
        public void NeedsEvaluatedGeometry_IsTrueForAProtectedRendererThatHoldsNoMesh()
        {
            var decoded = NewBlendedMesh("Part (APA protected)", 2);
            var renderer = NewSkinnedRenderer("Part", null);

            Assert.IsTrue(
                ApaPreviewGeometry.NeedsEvaluatedGeometry(renderer, decoded),
                "A protected part's geometry is the transient decode, which the renderer would draw if it held it.");
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
            Assert.IsFalse(ApaPreviewGeometry.NeedsEvaluatedGeometry(NewSkinnedRenderer("Body", mesh), null));

            var renderer = NewSkinnedRenderer("Body", mesh);
            renderer.SetBlendShapeWeight(0, 100f);

            var other = NewBlendedMesh("Other", 2);
            Assert.IsFalse(
                ApaPreviewGeometry.NeedsEvaluatedGeometry(renderer, other),
                "A mesh the renderer does not hold cannot be evaluated against its bones or blend shapes.");

            Assert.IsFalse(ApaPreviewGeometry.HasActiveBlendShapeWeights(null, mesh));
            Assert.IsFalse(ApaPreviewGeometry.HasActiveBlendShapeWeights(renderer, null));
        }

        /// <summary>
        /// Every input of the evaluated result is in the key: the weights, the bones, and the renderer's own space.
        /// </summary>
        /// <remarks>
        /// The space is not an optimisation detail. The positions are local to the renderer — the same space a
        /// <c>BakeMesh(…, useScale: true)</c> result is in — so the same world point has different coordinates
        /// under a different transform. A renderer scaled without its bones moving, served the array from the old
        /// space, would be drawn at the new matrix and land away from the mesh; at three times the scale, three
        /// times too far. Moving the whole rig invalidates through the bones either way.
        /// </remarks>
        [Test]
        public void Fingerprint_TracksTheWeightsTheBonesAndTheRenderersOwnSpace()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            var root = NewGameObject("Root").transform;
            renderer.rootBone = root;

            var initial = ApaPreviewGeometry.Fingerprint(renderer, mesh);

            // The renderer's own transform defines the space the cached array lives in, so moving or scaling the
            // renderer alone has to invalidate it: the drawn world point does not move, but its local coordinates
            // do, and the overlay maps whatever it is given through the new matrix.
            renderer.transform.position = new Vector3(3f, 4f, 5f);
            var moved = ApaPreviewGeometry.Fingerprint(renderer, mesh);
            Assert.AreNotEqual(initial, moved, "A moved renderer expresses the same pose in another space.");

            renderer.transform.localScale = new Vector3(2f, 2f, 2f);
            var scaled = ApaPreviewGeometry.Fingerprint(renderer, mesh);
            Assert.AreNotEqual(moved, scaled, "A scaled renderer expresses the same pose in another space.");

            renderer.SetBlendShapeWeight(0, 30f);
            var posed = ApaPreviewGeometry.Fingerprint(renderer, mesh);
            Assert.AreNotEqual(scaled, posed);

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

        /// <summary>
        /// The reported risk: a protected part's renderer holds no mesh while the caller hands in a transient
        /// decode that <i>does</i> declare blend shapes. <c>GetBlendShapeWeight</c>'s index is defined against the
        /// mesh attached to the renderer, so the fingerprint must answer this state without making that call —
        /// neither throwing nor logging an error out of a Scene View repaint.
        /// </summary>
        [Test]
        public void Fingerprint_AnswersForAProtectedMeshWithoutQueryingBlendShapeWeights()
        {
            var transient = NewBlendedMesh("Part (APA protected)", 2);
            var renderer = NewSkinnedRenderer("Part", null);

            Assert.IsFalse(
                ApaPreviewGeometry.CanReadBlendShapeWeights(renderer, transient),
                "A renderer holding no mesh may not be asked for the transient decode's weights.");
            Assert.IsFalse(
                ApaPreviewGeometry.HasActiveBlendShapeWeights(renderer, transient),
                "The weight query must be refused by the same rule.");

            // Unity answers an index outside that contract with a native error rather than a managed exception in
            // some builds, which the test runner turns into a failure; the value itself is not the point, its
            // stability is.
            var first = ApaPreviewGeometry.Fingerprint(renderer, transient);
            Assert.AreEqual(
                first,
                ApaPreviewGeometry.Fingerprint(renderer, transient),
                "The protected fingerprint must be stable and must not depend on a weight it cannot read.");
        }

        /// <summary>
        /// The guard itself: only the renderer that holds the mesh may be asked, so the live path keeps its
        /// weights while the protected path and a mismatched mesh are both refused.
        /// </summary>
        [Test]
        public void CanReadBlendShapeWeights_IsTrueOnlyForTheRenderersOwnMesh()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var other = NewBlendedMesh("Other", 2);
            var live = NewSkinnedRenderer("Body", mesh);
            var protectedRenderer = NewSkinnedRenderer("Part", null);

            Assert.IsTrue(ApaPreviewGeometry.CanReadBlendShapeWeights(live, mesh));
            Assert.IsFalse(
                ApaPreviewGeometry.CanReadBlendShapeWeights(live, other),
                "A mesh the renderer does not hold has no index the renderer could answer for.");
            Assert.IsFalse(ApaPreviewGeometry.CanReadBlendShapeWeights(protectedRenderer, mesh));
            Assert.IsFalse(ApaPreviewGeometry.CanReadBlendShapeWeights(null, mesh));
            Assert.IsFalse(ApaPreviewGeometry.CanReadBlendShapeWeights(live, null));
        }

        /// <summary>
        /// A live bake and a protected solve are different evaluations of the same mesh, so a renderer that loses
        /// its mesh must not be served the entry computed while it held one.
        /// </summary>
        [Test]
        public void Fingerprint_DistinguishesAProtectedSolveFromALiveBakeOfTheSameMesh()
        {
            var mesh = NewBlendedMesh("Part", 2);
            var renderer = NewSkinnedRenderer("Part", mesh);

            var live = ApaPreviewGeometry.Fingerprint(renderer, mesh);

            // The very same mesh instance, now handed in as the transient decode of a renderer that holds none.
            renderer.sharedMesh = null;
            var protectedSolve = ApaPreviewGeometry.Fingerprint(renderer, mesh);

            Assert.AreNotEqual(
                live,
                protectedSolve,
                "The key records which evaluation the entry describes: a bake is not a solve.");

            // The marker is what makes the two sections unreachable from each other: a live section always begins
            // with a blend-shape count, which is a uint and therefore can never reach the marker.
            Assert.IsTrue(
                ApaPreviewGeometry.NoBlendShapeWeightsRead > (ulong)uint.MaxValue,
                "No live blend-shape count can produce the protected marker.");
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

        /// <summary>
        /// The regression the user reported: a posed skeleton with every blend-shape weight at zero must move the
        /// overlay, and an unchanged pose must not re-evaluate on every repaint.
        /// </summary>
        [Test]
        public void TryRead_FollowsABonePoseChangeWithEveryBlendShapeWeightAtZero()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);
            var bone = NewGameObject("Hips").transform;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;

            var baker = new FakeBaker(new[] { new Vector3(1f, 1f, 1f), new Vector3(2f, 2f, 2f) });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var first, out var source));
                Assert.AreEqual(ApaPreviewPositionSource.Evaluated, source);
                Assert.AreEqual(1, baker.Calls);

                // A repaint that changed nothing reuses the solved positions.
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var second, out _));
                Assert.AreEqual(1, baker.Calls);
                Assert.AreSame(first, second);

                // Moving a bone is what a pose edit does; the weights stay at zero.
                bone.position = new Vector3(0f, 1f, 0f);
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var third, out source));

                Assert.AreEqual(ApaPreviewPositionSource.Evaluated, source);
                Assert.AreEqual(2, baker.Calls, "A moved bone must re-evaluate the drawn positions.");
                Assert.AreNotSame(first, third);
            }
        }

        /// <summary>
        /// The protected path the reported risk is about: the renderer holds no mesh while the mesh handed to the
        /// cache carries blend shapes. The read must succeed, be reusable, and follow the pose — without the
        /// fingerprint ever asking the renderer for a weight it cannot answer for.
        /// </summary>
        [Test]
        public void TryRead_ReusesTheSolvedPoseOfAProtectedMeshThatCarriesBlendShapes()
        {
            var transient = NewBlendedMesh("Part (APA protected)", 2);
            var renderer = NewSkinnedRenderer("Part", null);
            var bone = NewGameObject("Hips").transform;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;

            var baker = new FakeBaker(new[] { Vector3.one, Vector3.one });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, transient, out var first, out var source));
                Assert.AreEqual(ApaPreviewPositionSource.Evaluated, source);
                Assert.AreEqual(1, baker.Calls);

                // A repaint that changed nothing reuses the solve: one call, the same array, and no weight query
                // that a mesh-less renderer could not answer.
                Assert.IsTrue(cache.TryRead(renderer, transient, out var second, out _));
                Assert.AreEqual(1, baker.Calls);
                Assert.AreSame(first, second);

                bone.position = new Vector3(0f, 1f, 0f);
                Assert.IsTrue(cache.TryRead(renderer, transient, out _, out source));
                Assert.AreEqual(ApaPreviewPositionSource.Evaluated, source);
                Assert.AreEqual(2, baker.Calls, "A moved bone must invalidate a protected part's solved pose.");
            }
        }

        /// <summary>
        /// The space the cached array lives in is part of the key, proven end to end: a renderer scaled without its
        /// bones moving must be re-solved, or the disc is drawn through the new matrix from the old space and lands
        /// away from the mesh it names.
        /// </summary>
        /// <remarks>
        /// The production baker on the protected path is the real CPU solve — no graphics device, no Scene View —
        /// so this is the whole evaluated path, not a stand-in: the vertex is bound at (2,0,0) to a bone at
        /// (0,3,0) through a bind pose that moves it to the origin, and every scale draws that same world point.
        /// </remarks>
        [Test]
        public void TryRead_ResolvesAgainWhenOnlyTheRenderersOwnSpaceChanges()
        {
            var bone = NewGameObject("Hips").transform;
            bone.position = new Vector3(0f, 3f, 0f);

            var mesh = NewSkinnedMesh(
                "Part (APA protected)", new Vector3(2f, 0f, 0f), 0, Matrix4x4.Translate(new Vector3(-2f, 0f, 0f)));
            var renderer = NewSkinnedRenderer("Part", null);
            renderer.bones = new[] { bone };

            using (var cache = new ApaPreviewPositionCache())
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var first, out var source));
                Assert.AreEqual(ApaPreviewPositionSource.Evaluated, source);
                Assert.AreEqual(
                    new Vector3(0f, 3f, 0f),
                    renderer.transform.localToWorldMatrix.MultiplyPoint3x4(first[0]));

                renderer.transform.localScale = new Vector3(3f, 3f, 3f);
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var second, out _));

                Assert.AreNotSame(
                    first,
                    second,
                    "The renderer's own space changed, so the array from the old space must not be reused.");
                Assert.AreEqual(
                    new Vector3(0f, 3f, 0f),
                    renderer.transform.localToWorldMatrix.MultiplyPoint3x4(second[0]),
                    "A scaled renderer draws the same world point, and the disc must stay on it.");
            }
        }

        [Test]
        public void TryRead_ReadsTheEvaluatedPositionsEvenWithEveryWeightAtZero()
        {
            var mesh = NewBlendedMesh("Body", 2);
            var renderer = NewSkinnedRenderer("Body", mesh);

            var baker = new FakeBaker(new[] { Vector3.one, Vector3.one });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var positions, out var source));

                Assert.AreEqual(
                    ApaPreviewPositionSource.Evaluated,
                    source,
                    "A skinned renderer's bind pose is only what is drawn while the skeleton sits in it.");
                Assert.AreEqual(1, baker.Calls);
                CollectionAssert.AreEqual(baker.Positions, positions);
            }
        }

        [Test]
        public void TryRead_KeepsTheRestPoseForAMeshRenderer()
        {
            var mesh = NewBlendedMesh("Static", 2);
            var renderer = NewMeshRenderer("Static");

            var baker = new FakeBaker(new[] { Vector3.one, Vector3.one });
            using (var cache = new ApaPreviewPositionCache(baker))
            {
                Assert.IsTrue(cache.TryRead(renderer, mesh, out var positions, out var source));

                Assert.AreEqual(ApaPreviewPositionSource.RestPose, source);
                Assert.AreEqual(0, baker.Calls, "A MeshRenderer has no bones and no weights to evaluate.");
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

        // ---- The production baker ----------------------------------------------------------------------

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

        /// <summary>
        /// A protected part: the renderer is saved with no mesh, the geometry is the transient decode, and the
        /// overlay must still follow the pose — without the decoded mesh ever reaching the scene renderer, without
        /// a scene object being created to bake through, and without a mesh of any kind being created on this path.
        /// </summary>
        [Test]
        public void SkinnedMeshBaker_SolvesThePoseOfAProtectedRendererWithoutTouchingItOrTheScene()
        {
            var bone = NewGameObject("Hips").transform;
            bone.position = new Vector3(0f, 1f, 0f);

            var mesh = NewSkinnedMesh("Part (APA protected)", new Vector3(1f, 0f, 0f), 0, Matrix4x4.identity);
            var renderer = NewSkinnedRenderer("Part", null);
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;

            var sceneObjects = Object.FindObjectsOfType<GameObject>().Length;

            // The bake destination mesh belongs to the live path only; a protected solve must not create one, so
            // nothing transient is left for a window close to leak either.
            var bakedMeshes = CountObjectsNamed<Mesh>(ApaSkinnedMeshBaker.BakedMeshName);

            var baker = new ApaSkinnedMeshBaker();
            try
            {
                Assert.IsTrue(baker.TryBakeLocalPositions(renderer, mesh, out var positions));
                Assert.IsNotNull(positions);
                Assert.AreEqual(1, positions.Length);

                // The renderer is at the origin with no rotation, so the skinned position is the vertex moved by
                // the bone's world translation.
                Assert.AreEqual(new Vector3(1f, 1f, 0f), positions[0]);

                // The mesh was solved, not attached: the renderer keeps the empty slot the protected prefab
                // saved, and no transient object was left in the scene to bake through.
                Assert.IsNull(renderer.sharedMesh, "The decoded mesh must never be assigned to the scene renderer.");
                Assert.AreEqual(
                    sceneObjects,
                    Object.FindObjectsOfType<GameObject>().Length,
                    "Solving a protected part's pose must not leave a scene object behind.");
                Assert.AreEqual(
                    bakedMeshes,
                    CountObjectsNamed<Mesh>(ApaSkinnedMeshBaker.BakedMeshName),
                    "Solving a protected part's pose must not create the transient bake destination mesh.");

                bone.position = new Vector3(0f, 2f, 0f);
                Assert.IsTrue(baker.TryBakeLocalPositions(renderer, mesh, out var posed));
                Assert.AreEqual(new Vector3(1f, 2f, 0f), posed[0]);
            }
            finally
            {
                baker.Dispose();
            }
        }

        // ---- The protected-part pose solver ------------------------------------------------------------

        [Test]
        public void PreviewSkinning_BlendsEveryInfluenceAndKeepsUninfluencedVerticesInPlace()
        {
            var boneA = NewGameObject("BoneA").transform;
            var boneB = NewGameObject("BoneB").transform;
            boneA.position = new Vector3(0f, 0f, 0f);
            boneB.position = new Vector3(2f, 0f, 0f);

            // Two vertices: the first is shared evenly by the two bones, the second is weighted to a bone the
            // renderer does not have, which is the degenerate case a broken rig produces.
            var mesh = new Mesh { name = "Part" };
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f) };
            mesh.SetTriangles(new[] { 0, 1, 1 }, 0);
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f },
                new BoneWeight { boneIndex0 = 7, weight0 = 1f }
            };
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            _created.Add(mesh);

            var renderer = NewSkinnedRenderer("Part", null);
            renderer.bones = new[] { boneA, boneB };

            Assert.IsTrue(ApaPreviewSkinning.TrySolveLocalPositions(renderer, mesh, out var solved));

            Assert.AreEqual(new Vector3(1f, 0f, 0f), solved[0], "An even blend must land halfway between the bones.");
            Assert.AreEqual(
                new Vector3(1f, 0f, 0f),
                solved[1],
                "A vertex whose only bone is missing keeps its own position instead of being moved by a guess.");
        }

        [Test]
        public void PreviewSkinning_ReadsTheBindPoseAndTheBonesAndNothingElse()
        {
            var bone = NewGameObject("Hips").transform;
            bone.position = new Vector3(0f, 3f, 0f);

            // The bind pose moves the vertex from (2,0,0) to (0,0,0), so a solver that ignored it would answer
            // (2,3,0) instead of (0,3,0).
            var mesh = NewSkinnedMesh("Part", new Vector3(2f, 0f, 0f), 0, Matrix4x4.Translate(new Vector3(-2f, 0f, 0f)));
            var renderer = NewSkinnedRenderer("Part", null);
            renderer.bones = new[] { bone };

            Assert.IsTrue(ApaPreviewSkinning.TrySolveLocalPositions(renderer, mesh, out var solved));
            Assert.AreEqual(new Vector3(0f, 3f, 0f), solved[0]);

            // A scaled renderer draws the same world point. The solved vertices are in the renderer's own local
            // space — the space a BakeMesh(useScale: true) result and the mesh's own vertices share — so mapping
            // them with the renderer's localToWorldMatrix puts the disc exactly where the vertex is drawn.
            renderer.transform.localScale = new Vector3(3f, 3f, 3f);
            Assert.IsTrue(ApaPreviewSkinning.TrySolveLocalPositions(renderer, mesh, out var scaled));
            Assert.AreEqual(
                new Vector3(0f, 3f, 0f),
                renderer.transform.localToWorldMatrix.MultiplyPoint3x4(scaled[0]),
                "A scaled renderer must draw its vertices at the same world position, and the disc with them.");
        }

        [Test]
        public void PreviewSkinning_AnswersMeshesWithoutSkinningAndRefusesUnreadableInputs()
        {
            var mesh = NewBlendedMesh("Static", 2);
            var renderer = NewSkinnedRenderer("Part", null);

            Assert.IsTrue(
                ApaPreviewSkinning.TrySolveLocalPositions(renderer, mesh, out var positions),
                "A mesh with no weights and no bind poses is simply not deformed.");
            CollectionAssert.AreEqual(mesh.vertices, positions);

            Assert.IsFalse(ApaPreviewSkinning.TrySolveLocalPositions(null, mesh, out var none));
            Assert.IsNull(none);
            Assert.IsFalse(ApaPreviewSkinning.TrySolveLocalPositions(renderer, null, out none));
            Assert.IsNull(none);
        }

        /// <summary>
        /// Both halves of the evaluated path produce the renderer's <i>own</i> local space, which is the one
        /// <c>localToWorldMatrix</c> maps correctly: the live bake by including the transform's scale, the
        /// protected solver by inverting the renderer's full world matrix. A scale-free result would be mapped by
        /// that matrix as if it had already been scaled, and the discs would sit beside a scaled mesh.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The convention was re-checked against Unity's own documentation for this project's version
        /// (2022.3):</b> <c>SkinnedMeshRenderer.BakeMesh</c> states that "the vertices are relative to the
        /// SkinnedMeshRenderer Transform component", and its <c>useScale</c> parameter states that with true the
        /// bake uses the transform's position, rotation, <i>and scale</i>, and with false the position and rotation
        /// without the scale. <c>BakeMesh(_baked, true)</c> followed by <c>localToWorldMatrix</c> therefore applies
        /// the scale exactly once; the assertion below also pins that this basis is recorded in the source.
        /// </para>
        /// <para>
        /// A source contract for the live half, because <see cref="SkinnedMeshRenderer.BakeMesh"/> needs a
        /// graphics device and a posed rig; the protected half is pinned behaviourally above.
        /// </para>
        /// </remarks>
        [Test]
        public void ProductionBaker_BakesInTheRenderersOwnLocalSpace()
        {
            var source = ReadEditorSource("Authoring", "ApaPreviewGeometry.cs");

            StringAssert.Contains("renderer.BakeMesh(_baked, true)", source);
            Assert.IsFalse(
                source.Contains("BakeMesh(_baked, false)"),
                "A scale-free bake would be misplaced by the localToWorldMatrix the overlays map with.");
            StringAssert.Contains(
                "transform.worldToLocalMatrix",
                source,
                "The protected solver must invert the renderer's full world matrix, scale included.");

            // The documented basis for that choice, so a future reader does not have to re-derive the space rule
            // from the API docs to know why the pair is not a double scale.
            StringAssert.Contains(
                "vertices are relative to the SkinnedMeshRenderer Transform component",
                source,
                "The bake's coordinate convention must stay recorded in the source it justifies.");
        }

        // ---- Source contracts: the protected path's reads and writes -----------------------------------

        /// <summary>
        /// The source contract behind the reported risk: the fingerprint's weight read lives behind the shared
        /// "the renderer holds this mesh" guard, and <c>Fingerprint</c> itself contains no weight read at all — so
        /// a protected renderer (<c>sharedMesh == null</c>) can never reach <c>GetBlendShapeWeight</c>.
        /// </summary>
        /// <remarks>
        /// A structural check in addition to the behavioural ones above, because Unity answers an index outside
        /// that contract with a native error rather than a managed exception in some builds: the call has to be
        /// provably absent, not merely observed not to throw.
        /// </remarks>
        [Test]
        public void Fingerprint_ReadsBlendShapeWeightsOnlyBehindTheRendererHoldsTheMeshGuard()
        {
            var source = ReadEditorSource("Authoring", "ApaPreviewGeometry.cs");

            var fingerprint = CodeOnly(MethodBody(
                source,
                "public static ulong Fingerprint(SkinnedMeshRenderer renderer, Mesh mesh)",
                "private static ulong FingerprintBlendShapeWeights("));
            Assert.IsFalse(
                fingerprint.Contains("GetBlendShapeWeight"),
                "Fingerprint must delegate the weight read to the guarded helper, never call it directly.");

            var weights = CodeOnly(MethodBody(
                source,
                "private static ulong FingerprintBlendShapeWeights(ulong hash, SkinnedMeshRenderer renderer, Mesh mesh)",
                "private static ulong Mix("));

            var guard = weights.IndexOf("CanReadBlendShapeWeights(renderer, mesh)", System.StringComparison.Ordinal);
            var read = weights.IndexOf("GetBlendShapeWeight", System.StringComparison.Ordinal);

            Assert.GreaterOrEqual(guard, 0, "The weight helper must consult the shared guard.");
            Assert.Greater(read, guard, "The guard must come first: the read may only happen behind it.");
            StringAssert.Contains(
                "if (!CanReadBlendShapeWeights(renderer, mesh)) return Mix(hash, NoBlendShapeWeightsRead);",
                weights,
                "The state a renderer cannot answer for must fold the marker instead of weights.");

            // The same guard is the one the weight query uses, so the two cannot drift apart.
            StringAssert.Contains(
                "if (!CanReadBlendShapeWeights(renderer, mesh)) return false;",
                CodeOnly(MethodBody(
                    source,
                    "public static bool HasActiveBlendShapeWeights(SkinnedMeshRenderer renderer, Mesh mesh)",
                    "public static ulong Fingerprint(SkinnedMeshRenderer renderer, Mesh mesh)")));
        }

        /// <summary>
        /// The protected solve path is read-only by construction: no mesh assignment (not even a temporary one), no
        /// bake, no scene object, no project write, and no weight write. That is what keeps a protected prefab
        /// instance clean and leaves no mesh — transient or persistent — behind.
        /// </summary>
        [Test]
        public void ProtectedSolvePath_NeverWritesTheRendererOrTheProject()
        {
            var source = ReadEditorSource("Authoring", "ApaPreviewGeometry.cs");

            // The solver, from its declaration to the cache that follows it, with its remarks removed so every
            // check below is about the code and not about the prose that forbids the same thing.
            var solver = CodeOnly(MethodBody(
                source,
                "public static class ApaPreviewSkinning",
                "public sealed class ApaPreviewPositionCache"));

            // What it may do: read the mesh's arrays and the bones' matrices.
            StringAssert.Contains("TrySolveLocalPositions", solver);
            StringAssert.Contains("mesh.vertices", solver);
            StringAssert.Contains("mesh.boneWeights", solver);
            StringAssert.Contains("mesh.bindposes", solver);
            StringAssert.Contains("transform.worldToLocalMatrix", solver);

            // What it may never do. The patterns match code, not the remarks that forbid it.
            Assert.IsFalse(
                Regex.IsMatch(solver, @"sharedMesh\s*="),
                "The solver must never assign a mesh to the renderer, temporarily or otherwise.");
            Assert.IsFalse(
                solver.Contains("BakeMesh("),
                "The solver must not bake: it exists because a mesh-less renderer cannot be baked.");
            Assert.IsFalse(
                Regex.IsMatch(solver, @"AssetDatabase\."),
                "The solver must not touch the project.");
            Assert.IsFalse(
                Regex.IsMatch(solver, @"new\s+GameObject"),
                "The solver must not create a scene object to solve through.");
            Assert.IsFalse(
                solver.Contains("AddComponent"),
                "The solver must not create a scene object to solve through.");
            Assert.IsFalse(
                solver.Contains("SetBlendShapeWeight"),
                "The solver must not write a weight to the renderer.");
            Assert.IsFalse(
                solver.Contains("hideFlags"),
                "The solver must not create a mesh, transient or persistent.");

            // The production baker reaches the solver only for the protected state, and never for a mesh the
            // renderer actually holds.
            StringAssert.Contains(
                "ApaPreviewSkinning.TrySolveLocalPositions(renderer, mesh, out localPositions)", source);
            StringAssert.Contains(
                "if (renderer.sharedMesh == mesh) return TryBakeHeldMesh(renderer, mesh, out localPositions);", source);
        }

        // ---- Source contracts: which half reads which pose ---------------------------------------------

        /// <summary>
        /// Every overlay draws from the evaluated-position cache, and only the removal overlay's triangle
        /// <i>addresses</i> come from the rest-pose array cache; the baker is released when the window closes.
        /// </summary>
        /// <remarks>
        /// A structural check, because the alternative is standing up a Scene View and a repaint. The two halves
        /// are deliberately different: what an overlay <i>names</i> — a candidate index, a matched pair, a removal
        /// address — is bind-pose data, while the coordinate it is drawn at is the renderer's current pose. The
        /// regression this pins is the reported one: the candidate and merge-check overlays used to take their
        /// positions from the rest-pose arrays, so their discs sat beside a posed mesh.
        /// </remarks>
        [Test]
        public void SceneTool_DrawsEveryOverlayFromTheEvaluatedPositions()
        {
            var source = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            // One preview read per drawn layer: the removal overlay, the candidate overlay (both sides), the
            // merge check (both sides), and the stored seam (both sides).
            Assert.GreaterOrEqual(
                Regex.Matches(source, @"(?:preview|_targetPreview)\.TryRead\(").Count,
                4,
                "Every overlay must take its positions from the evaluated-position cache.");

            Assert.IsFalse(
                Regex.IsMatch(source, @"arrays\.TryRead\(mesh, out var vertices"),
                "No overlay may draw from the rest-pose vertex array; only the removal addresses still read it.");

            // The removal overlay still needs the triangle addresses, which are topology and never move with the
            // pose, so its index read is the one array-cache use that remains.
            StringAssert.Contains("_targetArrays.TryRead(mesh, out _, out var triangleIndices)", source);

            // Drawing is read-only. A protected part's decoded geometry is handed to the cache and never to a
            // renderer, so the candidate and merge-check overlays cannot write the mesh back onto the prefab
            // instance they are drawing.
            Assert.IsFalse(
                Regex.IsMatch(source, @"sharedMesh\s*="),
                "The Scene View overlay must never assign a mesh to a renderer.");
            Assert.IsFalse(
                Regex.IsMatch(source, @"\.mesh\s*=\s*mesh"),
                "The Scene View overlay must never assign the decoded geometry to a renderer.");

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

        /// <summary>
        /// The source text of one member, from its declaration to the declaration that follows it.
        /// </summary>
        /// <remarks>
        /// The source contracts need to say where a call is allowed to appear, which a whole-file search cannot
        /// express: "the fingerprint contains no weight read" is a statement about one method's body.
        /// </remarks>
        private static string MethodBody(string source, string signature, string nextSignature)
        {
            var start = source.IndexOf(signature, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, "Missing declaration: " + signature);

            var end = source.IndexOf(nextSignature, start, System.StringComparison.Ordinal);
            Assert.Greater(end, start, "Missing the declaration after: " + signature);

            return source.Substring(start, end - start);
        }

        /// <summary>
        /// The source with its XML documentation lines removed, so a "must never" contract matches code and not
        /// the prose that forbids the very same thing.
        /// </summary>
        private static string CodeOnly(string source)
        {
            var kept = new List<string>();
            var lines = source.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("///", System.StringComparison.Ordinal)) continue;
                kept.Add(lines[i]);
            }

            return string.Join("\n", kept.ToArray());
        }

        /// <summary>How many loaded objects of a type carry a name, used to prove a transient mesh was not made.</summary>
        private static int CountObjectsNamed<T>(string name) where T : Object
        {
            var found = Resources.FindObjectsOfTypeAll<T>();
            var count = 0;
            for (var i = 0; i < found.Length; i++)
            {
                if (found[i] != null && found[i].name == name) count++;
            }

            return count;
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

        /// <summary>
        /// A one-vertex mesh whose only vertex is rigidly weighted to one bone through one bind pose, which is
        /// the smallest input <see cref="ApaPreviewSkinning"/> can be driven with.
        /// </summary>
        private Mesh NewSkinnedMesh(string name, Vector3 vertex, int boneIndex, Matrix4x4 bindPose)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { vertex };
            mesh.SetTriangles(new[] { 0, 0, 0 }, 0);
            mesh.boneWeights = new[] { new BoneWeight { boneIndex0 = boneIndex, weight0 = 1f } };
            mesh.bindposes = new[] { bindPose };
            _created.Add(mesh);
            return mesh;
        }

        private MeshRenderer NewMeshRenderer(string name)
        {
            return NewGameObject(name).AddComponent<MeshRenderer>();
        }
    }
}

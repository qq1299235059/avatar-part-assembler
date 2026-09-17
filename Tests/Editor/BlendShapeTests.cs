using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using AvatarPartAssembler.Editor;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for M2 blend shapes: frame and delta remapping, deterministic shape order, and the merge rules of
    /// section 43.8 (R11).
    /// </summary>
    /// <remarks>
    /// The fixtures are unskinned on purpose. Skinning and blend shapes are independent features, and a fixture
    /// that exercises both at once makes a failure ambiguous: it would not say whether the wrong value came
    /// from the weight remap or from the delta remap.
    /// </remarks>
    public sealed class BlendShapeTests
    {
        /// <summary>Five-vertex body: ring vertices 0..3 plus apex 4. The ring is the seam.</summary>
        private static MeshSnapshot Body(
            string[] shapeNames = null,
            BlendShapeFrameSnapshot[][] frames = null,
            int ringCount = 4)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(ringCount, 1f)) { new Vector3(0f, 0f, 1f) };
            return MeshFixtures.BlendShapedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(ringCount, 0, ringCount),
                shapeNames ?? new string[0],
                frames ?? new BlendShapeFrameSnapshot[0][]);
        }

        /// <summary>Part whose ring coincides with the body's ring, plus its own apex at a different Z.</summary>
        private static MeshSnapshot Part(
            string[] shapeNames,
            BlendShapeFrameSnapshot[][] frames,
            float apexOffset = -1f,
            int ringCount = 4)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(ringCount, 1f)) { new Vector3(0f, 0f, apexOffset) };
            return MeshFixtures.BlendShapedSnapshot(
                "Part",
                positions.ToArray(),
                MeshFixtures.CapTriangles(ringCount, 0, ringCount),
                shapeNames,
                frames);
        }

        /// <summary>The body's "Blink" shape: two frames, moving only the apex.</summary>
        private static BlendShapeFrameSnapshot[] BlinkFrames(float apexDelta)
        {
            return new[]
            {
                MeshFixtures.Frame(0f, 5, -1, Vector3.zero),
                MeshFixtures.Frame(100f, 5, 4, new Vector3(0f, 0f, apexDelta))
            };
        }

        private static ValidationContext Context(MeshSnapshot body, MeshSnapshot part)
        {
            return MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4))
            });
        }

        private static ValidationContext SinglePartContext(
            string[] partShapes,
            BlendShapeFrameSnapshot[][] partFrames)
        {
            return Context(Body(new[] { "Blink" }, new[] { BlinkFrames(0.5f) }),
                Part(partShapes, partFrames));
        }

        /// <summary>
        /// Every frame of every final shape is remapped over every retained vertex: base vertices read the
        /// base mesh, part vertices read the part mesh, and a vertex whose source has no such shape is zero.
        /// </summary>
        [Test]
        public void EveryFrameAndDelta_IsRemappedOverRetainedVertices()
        {
            var result = ApaCore.Assemble(SinglePartContext(
                new[] { "Blink", "PartOnly" },
                new[]
                {
                    BlinkFrames(-0.25f),
                    new[] { MeshFixtures.Frame(100f, 5, 4, new Vector3(0f, 0f, -0.5f)) }
                }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var mesh = result.Mesh;
                Assert.AreEqual(6, mesh.vertexCount, "Four retained base vertices plus apex plus part apex.");

                var blink = MeshFixtures.OutputShapeIndex(mesh, "Blink");
                Assert.GreaterOrEqual(blink, 0, "The merged shape must exist on the generated mesh.");
                Assert.AreEqual(2, mesh.GetBlendShapeFrameCount(blink));
                Assert.AreEqual(0f, mesh.GetBlendShapeFrameWeight(blink, 0), 1e-6f);
                Assert.AreEqual(100f, mesh.GetBlendShapeFrameWeight(blink, 1), 1e-6f);

                var blinkFrame = MeshFixtures.ReadFramePositionDeltas(mesh, blink, 1);
                for (var i = 0; i < 4; i++)
                {
                    Assert.AreEqual(Vector3.zero, blinkFrame[i], "Retained base ring vertex " + i + " does not move.");
                }

                Assert.AreEqual(0.5f, blinkFrame[4].z, 1e-6f, "The base apex keeps the base delta.");
                Assert.AreEqual(-0.25f, blinkFrame[5].z, 1e-6f, "The part apex keeps the part delta.");

                var partOnly = MeshFixtures.OutputShapeIndex(mesh, "PartOnly");
                Assert.GreaterOrEqual(partOnly, 0);
                var partOnlyFrame = MeshFixtures.ReadFramePositionDeltas(mesh, partOnly, 0);
                for (var i = 0; i < 5; i++)
                {
                    Assert.AreEqual(Vector3.zero, partOnlyFrame[i],
                        "A body vertex has no delta for a part-only shape (vertex " + i + ").");
                }

                Assert.AreEqual(-0.5f, partOnlyFrame[5].z, 1e-6f);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// Shape order is base shapes first, then new part shapes by stable part and source order.
        /// </summary>
        [Test]
        public void ShapeOrder_IsBaseFirstThenPartOrder()
        {
            var body = Body(new[] { "Blink", "BaseOnly" }, new[]
            {
                BlinkFrames(0.5f),
                new[] { MeshFixtures.Frame(100f, 5, 3, new Vector3(0f, 0.5f, 0f)) }
            });

            var part = Part(new[] { "Zeta", "Alpha" }, new[]
            {
                new[] { MeshFixtures.Frame(100f, 5, 4, new Vector3(0f, 0f, -0.5f)) },
                new[] { MeshFixtures.Frame(100f, 5, 4, new Vector3(0.5f, 0f, 0f)) }
            });

            var result = ApaCore.Assemble(Context(body, part));
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var mesh = result.Mesh;
                Assert.AreEqual(4, mesh.blendShapeCount);
                Assert.AreEqual("Blink", mesh.GetBlendShapeName(0), "Body shapes keep their order.");
                Assert.AreEqual("BaseOnly", mesh.GetBlendShapeName(1));
                Assert.AreEqual("Zeta", mesh.GetBlendShapeName(2), "New part shapes follow source order.");
                Assert.AreEqual("Alpha", mesh.GetBlendShapeName(3));
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>Two parts introducing different shapes append in stable part identity order.</summary>
        [Test]
        public void ShapeOrder_FollowsPartIdentityNotSupplyOrder()
        {
            var body = Body();
            var partA = Part(new[] { "Alpha" }, new[]
            {
                new[] { MeshFixtures.Frame(100f, 5, 4, new Vector3(0.1f, 0f, 0f)) }
            });
            var partB = Part(new[] { "Beta" }, new[]
            {
                new[] { MeshFixtures.Frame(100f, 5, 4, new Vector3(0.2f, 0f, 0f)) }
            });

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(4), slot: ApaPartSlot.RightArm),
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(4), slot: ApaPartSlot.LeftArm)
            });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                Assert.AreEqual(2, result.Mesh.blendShapeCount);
                Assert.AreEqual("Alpha", result.Mesh.GetBlendShapeName(0));
                Assert.AreEqual("Beta", result.Mesh.GetBlendShapeName(1));
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// A shape that exists on both sides merges into one final shape, and the welded seam vertex uses the
        /// base delta.
        /// </summary>
        [Test]
        public void SameNameShape_MergesAndKeepsTheBaseSeamDelta()
        {
            var baseDeltas = new Vector3[5];
            baseDeltas[0] = new Vector3(0f, 0.25f, 0f);
            var body = Body(new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, baseDeltas) }
            });

            // The part agrees at the seam within epsilon and moves its own apex.
            var partDeltas = new Vector3[5];
            partDeltas[0] = new Vector3(0.000001f, 0.25f, 0f);
            partDeltas[4] = new Vector3(0f, 0f, -0.25f);
            var part = Part(new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, partDeltas) }
            });

            var result = ApaCore.Assemble(Context(body, part));
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var shape = MeshFixtures.OutputShapeIndex(result.Mesh, "Seam");
                Assert.GreaterOrEqual(shape, 0);
                Assert.AreEqual(1, result.Mesh.blendShapeCount, "A same-named shape merges into one.");

                var deltas = MeshFixtures.ReadFramePositionDeltas(result.Mesh, shape, 0);
                Assert.AreEqual(0f, deltas[0].x, 1e-6f, "The welded vertex keeps the base value.");
                Assert.AreEqual(0.25f, deltas[0].y, 1e-6f);
                Assert.AreEqual(-0.25f, deltas[5].z, 1e-6f, "The part's own vertex keeps the part delta.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>A same-named shape whose frame count differs blocks with <c>APA028</c>.</summary>
        [Test]
        public void SameNameShape_WithDifferentFrameCounts_ReportsApa028()
        {
            var body = Body(new[] { "Blink" }, new[] { BlinkFrames(0.5f) });
            var part = Part(new[] { "Blink" }, new[]
            {
                new[]
                {
                    MeshFixtures.Frame(0f, 5, -1, Vector3.zero),
                    MeshFixtures.Frame(100f, 5, 4, new Vector3(0f, 0f, -0.25f)),
                    MeshFixtures.Frame(50f, 5, 4, new Vector3(0f, 0f, -0.1f))
                }
            });

            var planning = ApaCore.Plan(Context(body, part));
            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.BlendShapeFrameMismatch), planning.Issues.FormatAll());
        }

        /// <summary>A same-named shape whose frame weights differ blocks with <c>APA028</c>.</summary>
        [Test]
        public void SameNameShape_WithDifferentFrameWeights_ReportsApa028()
        {
            var body = Body(new[] { "Blink" }, new[] { BlinkFrames(0.5f) });
            var part = Part(new[] { "Blink" }, new[]
            {
                new[]
                {
                    MeshFixtures.Frame(0f, 5, -1, Vector3.zero),
                    MeshFixtures.Frame(50f, 5, 4, new Vector3(0f, 0f, -0.25f))
                }
            });

            var planning = ApaCore.Plan(Context(body, part));
            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.BlendShapeFrameMismatch), planning.Issues.FormatAll());
        }

        /// <summary>
        /// A same-named shape whose seam deltas disagree beyond epsilon blocks with <c>APA029</c>.
        /// </summary>
        [Test]
        public void SameNameShape_WithIncompatibleSeamDeltas_ReportsApa029()
        {
            var baseDeltas = new Vector3[5];
            baseDeltas[4] = new Vector3(0f, 0f, 0.5f);
            var body = Body(new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, baseDeltas) }
            });

            var partDeltas = new Vector3[5];
            partDeltas[0] = new Vector3(0.01f, 0f, 0f);
            partDeltas[4] = new Vector3(0f, 0f, -0.5f);
            var part = Part(new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, partDeltas) }
            });

            var planning = ApaCore.Plan(Context(body, part));
            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(
                planning.Issues.ContainsCode(ApaErrorCode.BlendShapeSeamDeltaMismatch), planning.Issues.FormatAll());
        }

        /// <summary>
        /// A shape that exists only on a part must not move a welded seam vertex: <c>APA029</c>.
        /// </summary>
        [Test]
        public void PartOnlyShape_WithNonZeroSeamDelta_ReportsApa029()
        {
            var body = Body(new[] { "Blink" }, new[] { BlinkFrames(0.5f) });

            var partDeltas = new Vector3[5];
            partDeltas[0] = new Vector3(0f, 0.001f, 0f);
            partDeltas[4] = new Vector3(0f, 0f, -0.5f);
            var part = Part(new[] { "Blink", "PartOnly" }, new[]
            {
                BlinkFrames(-0.25f),
                new[] { MeshFixtures.FrameFromDeltas(100f, partDeltas) }
            });

            var planning = ApaCore.Plan(Context(body, part));

            Assert.IsFalse(planning.Succeeded, "A part-only delta at a welded vertex must block.");
            Assert.IsTrue(
                planning.Issues.ContainsCode(ApaErrorCode.BlendShapeSeamDeltaMismatch),
                planning.Issues.FormatAll());
        }

        /// <summary>A shape that exists only on the body survives at its retained vertices.</summary>
        [Test]
        public void BaseOnlyShape_SurvivesAtRetainedVertices()
        {
            var body = Body(new[] { "BaseOnly" }, new[]
            {
                new[] { MeshFixtures.Frame(100f, 5, 4, new Vector3(0f, 0f, 0.75f)) }
            });
            var part = Part(new string[0], new BlendShapeFrameSnapshot[0][]);

            var result = ApaCore.Assemble(Context(body, part));
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var shape = MeshFixtures.OutputShapeIndex(result.Mesh, "BaseOnly");
                Assert.GreaterOrEqual(shape, 0);
                var deltas = MeshFixtures.ReadFramePositionDeltas(result.Mesh, shape, 0);
                Assert.AreEqual(0.75f, deltas[4].z, 1e-6f);
                Assert.AreEqual(Vector3.zero, deltas[5], "A part vertex has no delta for a base-only shape.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>One mesh declaring the same shape name twice blocks with <c>APA027</c>.</summary>
        [Test]
        public void DuplicateShapeNameInOneMesh_ReportsApa027()
        {
            var body = Body(new[] { "Blink", "Blink" }, new[]
            {
                BlinkFrames(0.5f),
                BlinkFrames(0.25f)
            });

            var planning = ApaCore.Plan(Context(body, Part(new string[0], new BlendShapeFrameSnapshot[0][])));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(
                planning.Issues.ContainsCode(ApaErrorCode.BlendShapeDuplicateName), planning.Issues.FormatAll());
        }

        /// <summary>A frame whose delta arrays do not match the vertex count blocks with <c>APA030</c>.</summary>
        [Test]
        public void DeltaLengthMismatch_ReportsApa030()
        {
            var body = Body(new[] { "Blink" }, new[]
            {
                new[]
                {
                    new BlendShapeFrameSnapshot(100f, new Vector3[3], new Vector3[3], new Vector3[3])
                }
            });

            var planning = ApaCore.Plan(Context(body, Part(new string[0], new BlendShapeFrameSnapshot[0][])));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(
                planning.Issues.ContainsCode(ApaErrorCode.InvalidBlendShapeDelta), planning.Issues.FormatAll());
        }

        /// <summary>A non-finite delta is <c>APA016</c>.</summary>
        [Test]
        public void NonFiniteDelta_ReportsApa016()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var deltas = new Vector3[5];
            deltas[2] = new Vector3(float.NaN, 0f, 0f);

            var body = MeshFixtures.BlendShapedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                new[] { "Blink" },
                new[] { new[] { MeshFixtures.FrameFromDeltas(100f, deltas) } });

            var planning = ApaCore.Plan(Context(body, Part(new string[0], new BlendShapeFrameSnapshot[0][])));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.NonFiniteValue), planning.Issues.FormatAll());
        }

        /// <summary>
        /// Blend shape output is deterministic: assembling the same context twice produces identical frames.
        /// </summary>
        [Test]
        public void BlendShapeOutput_IsDeterministic()
        {
            var context = SinglePartContext(
                new[] { "Blink", "PartOnly" },
                new[]
                {
                    BlinkFrames(-0.25f),
                    new[] { MeshFixtures.Frame(100f, 5, 4, new Vector3(0f, 0f, -0.5f)) }
                });

            var first = ApaCore.Assemble(context);
            var second = ApaCore.Assemble(context);
            Assert.IsTrue(first.Succeeded, first.Issues.FormatAll());
            Assert.IsTrue(second.Succeeded, second.Issues.FormatAll());

            try
            {
                Assert.AreEqual(first.Mesh.blendShapeCount, second.Mesh.blendShapeCount);
                for (var shape = 0; shape < first.Mesh.blendShapeCount; shape++)
                {
                    Assert.AreEqual(first.Mesh.GetBlendShapeName(shape), second.Mesh.GetBlendShapeName(shape));
                    for (var frame = 0; frame < first.Mesh.GetBlendShapeFrameCount(shape); frame++)
                    {
                        CollectionAssert.AreEqual(
                            MeshFixtures.ReadFramePositionDeltas(first.Mesh, shape, frame),
                            MeshFixtures.ReadFramePositionDeltas(second.Mesh, shape, frame));
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(first.Mesh);
                Object.DestroyImmediate(second.Mesh);
            }
        }
    }
}

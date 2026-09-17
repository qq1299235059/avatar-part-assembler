using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests that space transforms are captured immutably and that every delta kind is compared and written
    /// with the operator its attribute requires.
    /// </summary>
    /// <remarks>
    /// The shared fixture is a part whose local frame is rotated and non-uniformly scaled relative to the
    /// target renderer. A uniform transform would make the linear matrix and the inverse transpose agree for
    /// every delta, which is exactly why a wrong operator survives ordinary testing and only surfaces on a
    /// scaled part.
    /// </remarks>
    public sealed class TransformSpaceTests
    {
        /// <summary>
        /// Part-local to target-local: a quarter turn about Z composed with a non-uniform scale.
        /// </summary>
        /// <remarks>
        /// The determinant is 1, so the transform is invertible and preserves handedness; the non-uniform
        /// scale is what makes the linear and inverse-transpose operators differ.
        /// </remarks>
        private static readonly Matrix4x4 PartToTarget =
            Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, 90f)) * Matrix4x4.Scale(new Vector3(2f, 0.5f, 1f));

        /// <summary>Every operator is defined for a delta, and the normal operator is not the tangent one.</summary>
        [Test]
        public void DeltaMatrix_UsesTheOperatorEachAttributeRequires()
        {
            var transforms = MeshFixtures.Spaces(PartToTarget);

            AssertMatrix(PartToTarget, transforms.SourceToTargetLocal(), "position/source-to-target");
            AssertMatrix(PartToTarget, transforms.SourceToTargetDeltaMatrix(BlendShapeDeltaKind.Position),
                "a position delta uses the source-to-target linear part");
            AssertMatrix(PartToTarget, transforms.SourceToTargetDeltaMatrix(BlendShapeDeltaKind.Tangent),
                "a tangent delta is a direction and uses the linear part");
            AssertMatrix(PartToTarget.inverse.transpose,
                transforms.SourceToTargetDeltaMatrix(BlendShapeDeltaKind.Normal),
                "a normal delta uses the inverse transpose");

            // The fixture is only meaningful if the two operators actually differ.
            Assert.IsFalse(
                MatricesEqual(PartToTarget, PartToTarget.inverse.transpose),
                "The fixture transform must distinguish the linear operator from the inverse transpose.");
        }

        /// <summary>
        /// A same-named shape whose part deltas describe the same motion in a rotated, non-uniformly scaled
        /// frame passes: the comparison happens in the target renderer's local space.
        /// </summary>
        /// <remarks>
        /// The deltas are mapped with the operator each attribute requires, so the raw values differ from the
        /// base values while the transformed values agree. A raw part-local comparison rejects this pair, which
        /// is the defect this test pins.
        /// </remarks>
        [Test]
        public void SameNameShape_EquivalentDeltasInATransformedPart_Pass()
        {
            var baseDeltas = new Vector3[5];
            baseDeltas[0] = new Vector3(0.02f, 0.01f, 0.005f);
            baseDeltas[4] = new Vector3(0f, 0f, 0.5f);

            var baseNormals = new Vector3[5];
            baseNormals[0] = new Vector3(0.3f, 0.4f, 0.5f);

            var baseTangents = new Vector3[5];
            baseTangents[0] = new Vector3(0.1f, -0.2f, 0.3f);

            var body = Body(new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, baseDeltas, baseNormals, baseTangents) }
            });

            // A position or tangent delta is an offset and is carried by the inverse linear map; a normal delta
            // is carried by the inverse of the inverse transpose, which is the transpose itself.
            var part = Part(PartToTarget, new[] { "Seam" }, new[]
            {
                new[]
                {
                    MeshFixtures.FrameFromDeltas(
                        100f,
                        MapLinear(PartToTarget.inverse, baseDeltas),
                        MapLinear(PartToTarget.transpose, baseNormals),
                        MapLinear(PartToTarget.inverse, baseTangents))
                }
            });

            var planning = ApaCore.Plan(Context(body, part));

            Assert.IsTrue(
                planning.Succeeded,
                "Physically equivalent deltas in a rotated, scaled part frame must agree in target space." +
                planning.Issues.FormatAll());
        }

        /// <summary>
        /// A part that authors the identical raw delta triple while its transform moves the surface elsewhere
        /// blocks: raw equality is not agreement.
        /// </summary>
        /// <remarks>
        /// This is the false-acceptance direction of the same defect. The part's deltas are byte-identical to
        /// the body's, but the part's transform rotates and scales them, so the emitted motion differs from the
        /// body's at the weld.
        /// </remarks>
        [Test]
        public void SameNameShape_IdenticalRawDeltasOnATransformedPart_Block()
        {
            var baseDeltas = new Vector3[5];
            baseDeltas[0] = new Vector3(0.02f, 0.01f, 0.005f);

            var body = Body(new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, baseDeltas) }
            });

            var part = Part(PartToTarget, new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, baseDeltas) }
            });

            var planning = ApaCore.Plan(Context(body, part));

            Assert.IsFalse(planning.Succeeded, "Raw-equal deltas are not equal in target space.");
            Assert.IsTrue(
                planning.Issues.ContainsCode(ApaErrorCode.BlendShapeSeamDeltaMismatch),
                planning.Issues.FormatAll());
        }

        /// <summary>A delta that disagrees in the target space blocks even when it is well formed locally.</summary>
        [Test]
        public void SameNameShape_TargetSpaceMismatch_Blocks()
        {
            var baseDeltas = new Vector3[5];
            baseDeltas[0] = new Vector3(0.02f, 0.01f, 0.005f);

            var body = Body(new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, baseDeltas) }
            });

            // Equivalent at the seam, then moved by a tenth of a millimetre in target space.
            var partDeltas = MapLinear(PartToTarget.inverse, baseDeltas);
            partDeltas[0] += PartToTarget.inverse.MultiplyVector(new Vector3(0.01f, 0f, 0f));

            var part = Part(PartToTarget, new[] { "Seam" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, partDeltas) }
            });

            var planning = ApaCore.Plan(Context(body, part));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(
                planning.Issues.ContainsCode(ApaErrorCode.BlendShapeSeamDeltaMismatch),
                planning.Issues.FormatAll());
        }

        /// <summary>
        /// A part-only shape's tangent delta is materialized with the linear operator, not with the normal
        /// matrix, on a non-uniformly scaled part.
        /// </summary>
        /// <remarks>
        /// The two operators disagree here, so the assertion distinguishes them: the expected tangent delta is
        /// <c>S * t</c>, while using the normal matrix would produce <c>inverseTranspose(S) * t</c>. Position
        /// and normal deltas are asserted at the same time so that a future change cannot fix one by breaking
        /// another.
        /// </remarks>
        [Test]
        public void PartOnlyShape_MaterializesEachDeltaWithItsOwnOperator()
        {
            var body = Body(new string[0], new BlendShapeFrameSnapshot[0][]);

            var position = new Vector3[5];
            position[4] = new Vector3(0.1f, 0.2f, 0.3f);
            var normal = new Vector3[5];
            normal[4] = new Vector3(0.5f, -0.25f, 0.75f);
            var tangent = new Vector3[5];
            tangent[4] = new Vector3(0.3f, 0.6f, 0.1f);

            var part = Part(PartToTarget, new[] { "Tilt" }, new[]
            {
                new[] { MeshFixtures.FrameFromDeltas(100f, position, normal, tangent) }
            });

            var result = ApaCore.Assemble(Context(body, part));
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var shape = MeshFixtures.OutputShapeIndex(result.Mesh, "Tilt");
                Assert.GreaterOrEqual(shape, 0, "The part-only shape must exist on the generated mesh.");

                var apex = result.Plan.FinalIndexOf("part-a", 4);
                Assert.GreaterOrEqual(apex, 0);

                var expectedPosition = PartToTarget.MultiplyVector(position[4]);
                var expectedNormal = PartToTarget.inverse.transpose.MultiplyVector(normal[4]);
                var expectedTangent = PartToTarget.MultiplyVector(tangent[4]);

                AssertVector(expectedPosition, MeshFixtures.ReadFramePositionDeltas(result.Mesh, shape, 0)[apex],
                    "position delta");
                AssertVector(expectedNormal, MeshFixtures.ReadFrameNormalDeltas(result.Mesh, shape, 0)[apex],
                    "normal delta");
                AssertVector(expectedTangent, MeshFixtures.ReadFrameTangentDeltas(result.Mesh, shape, 0)[apex],
                    "tangent delta");

                // State the tangent rule explicitly: the inverse transpose is the wrong operator for a tangent,
                // and on this fixture it produces a different vector.
                Assert.IsFalse(
                    VectorsEqual(
                        expectedTangent,
                        PartToTarget.inverse.transpose.MultiplyVector(tangent[4])),
                    "The fixture must distinguish the tangent's linear operator from the normal matrix.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// A singular or non-finite transform is rejected with a stable reason instead of being inverted into a
        /// matrix full of NaN.
        /// </summary>
        [Test]
        public void TryCreate_RejectsUnusableMatrices()
        {
            var zeroScale = Matrix4x4.Scale(new Vector3(1f, 0f, 1f));
            Assert.IsFalse(
                SpaceTransforms.TryCreate(Matrix4x4.identity, zeroScale, out _, out var singularReason));
            Assert.AreEqual("singular-source-to-target", singularReason);

            var nonFinite = Matrix4x4.identity;
            nonFinite.m00 = float.NaN;
            Assert.IsFalse(
                SpaceTransforms.TryCreate(nonFinite, Matrix4x4.identity, out _, out var nonFiniteReason));
            Assert.AreEqual("non-finite-source-to-avatar", nonFiniteReason);

            Assert.IsTrue(
                SpaceTransforms.TryCreate(PartToTarget, PartToTarget, out var captured, out var okReason));
            Assert.IsTrue(captured.IsValid);
            Assert.AreEqual(string.Empty, okReason);
        }

        /// <summary>A default value is the identity mapping, which is what hand-built fixtures rely on.</summary>
        [Test]
        public void DefaultValue_IsTheIdentityMapping()
        {
            var transforms = default(SpaceTransforms);

            Assert.IsFalse(transforms.IsValid);
            AssertMatrix(Matrix4x4.identity, transforms.SourceToAvatarLocal(), "source-to-avatar");
            AssertMatrix(Matrix4x4.identity, transforms.SourceToTargetLocal(), "source-to-target");
            AssertMatrix(Matrix4x4.identity, transforms.SourceToTargetNormalMatrix(), "normal");
            AssertMatrix(Matrix4x4.identity, transforms.SourceToTargetDeltaMatrix(BlendShapeDeltaKind.Tangent),
                "tangent delta");
        }

        // ---- fixtures ---------------------------------------------------------------------------------

        /// <summary>
        /// A body whose ring sits in the target's local space: four ring vertices plus an apex.
        /// </summary>
        private static MeshSnapshot Body(string[] shapeNames, BlendShapeFrameSnapshot[][] frames)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            return MeshFixtures.BlendShapedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                shapeNames,
                frames);
        }

        /// <summary>
        /// The same geometry authored in the part's own local frame, so that
        /// <paramref name="partToTarget"/> maps it exactly onto the body.
        /// </summary>
        private static MeshSnapshot Part(
            Matrix4x4 partToTarget,
            string[] shapeNames,
            BlendShapeFrameSnapshot[][] frames)
        {
            var source = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var inverse = partToTarget.inverse;
            var positions = new Vector3[source.Count];
            for (var i = 0; i < positions.Length; i++) positions[i] = inverse.MultiplyPoint3x4(source[i]);

            return MeshFixtures.BlendShapedSnapshot(
                "Part",
                positions,
                MeshFixtures.CapTriangles(4, 0, 4),
                shapeNames,
                frames);
        }

        /// <summary>A context whose single part carries the transformed space matrices.</summary>
        private static ValidationContext Context(MeshSnapshot body, MeshSnapshot part)
        {
            return MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot(
                    "part-a",
                    part,
                    MeshFixtures.Seam(4),
                    transforms: MeshFixtures.Spaces(PartToTarget))
            });
        }

        private static Vector3[] MapLinear(Matrix4x4 matrix, Vector3[] values)
        {
            var result = new Vector3[values.Length];
            for (var i = 0; i < result.Length; i++) result[i] = matrix.MultiplyVector(values[i]);
            return result;
        }

        private static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual, string message)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    Assert.AreEqual(
                        expected[row, column],
                        actual[row, column],
                        1e-5f,
                        message + " (element " + row + "," + column + ")");
                }
            }
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string message)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f, message + " x");
            Assert.AreEqual(expected.y, actual.y, 1e-4f, message + " y");
            Assert.AreEqual(expected.z, actual.z, 1e-4f, message + " z");
        }

        private static bool MatricesEqual(Matrix4x4 a, Matrix4x4 b)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    if (Mathf.Abs(a[row, column] - b[row, column]) > 1e-6f) return false;
                }
            }

            return true;
        }

        private static bool VectorsEqual(Vector3 a, Vector3 b)
        {
            return Mathf.Abs(a.x - b.x) <= 1e-6f
                   && Mathf.Abs(a.y - b.y) <= 1e-6f
                   && Mathf.Abs(a.z - b.z) <= 1e-6f;
        }
    }
}

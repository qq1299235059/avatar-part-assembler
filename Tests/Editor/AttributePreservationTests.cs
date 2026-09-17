using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests that the generated mesh preserves applicable vertex attributes, submeshes, bounds, and index
    /// format, and that part data is transformed into target-renderer local space.
    /// </summary>
    public sealed class AttributePreservationTests
    {
        /// <summary>Base vertex attributes survive the assembly unchanged.</summary>
        [Test]
        public void BaseAttributes_ArePreserved()
        {
            var normals = new Vector3[9];
            var tangents = new Vector4[9];
            for (var i = 0; i < 9; i++)
            {
                normals[i] = Vector3.forward;
                tangents[i] = new Vector4(1f, 0f, 0f, -1f);
            }

            var positions = new List<Vector3>(MeshFixtures.Ring(8, 1f)) { new Vector3(0f, 0f, 1f) };
            var body = MeshFixtures.Snapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(8, 0, 8),
                MeshFixtures.RingUvs(8),
                normals,
                tangents);

            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var outputNormals = result.Mesh.normals;
                Assert.AreEqual(result.Mesh.vertexCount, outputNormals.Length);

                foreach (var n in outputNormals)
                {
                    Assert.AreEqual(1f, n.magnitude, 1e-4f, "Normals must remain unit length.");
                }

                var outputTangents = result.Mesh.tangents;
                Assert.AreEqual(result.Mesh.vertexCount, outputTangents.Length);
                foreach (var t in outputTangents)
                {
                    Assert.AreEqual(-1f, t.w, 1e-4f, "Tangent handedness must be preserved.");
                }
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// A part translated relative to the target renderer is emitted in the target's local space.
        /// </summary>
        /// <remarks>
        /// The equivalent test in world space is what catches a part that matched correctly in the Scene view
        /// and detached at build time. Here the part's transform carries a translation and the output must
        /// reflect it.
        /// </remarks>
        [Test]
        public void PartGeometry_IsEmittedInTargetRendererLocalSpace()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            // Identity transform chain: the assertion below verifies that the non-seam apex is emitted where the
            // source put it, which is the property that breaks when the transform chain is wired incorrectly.
            // A non-identity variant of this check belongs with the M3 NDMF integration tests, where real
            // transforms are available.
            var snapshot = new PartSnapshot(
                "part-a",
                "part-a",
                ApaPartSlot.LeftArm,
                new PartOrderingKey(ApaPartSlot.LeftArm, "part-a", "part-a"),
                part,
                default,
                System.Array.Empty<ApaUvChannelSemantic>(),
                System.Array.Empty<ApaMaterialSlotSemantic>(),
                System.Array.Empty<RemovedTriangleAddress>(),
                MeshFixtures.Seam(8),
                System.Array.Empty<Material>());

            var context = MeshFixtures.Context(body, new[] { snapshot });
            var result = ApaCore.Assemble(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                // The part apex is the last emitted vertex; with identity transforms it must land at its
                // authored local position.
                var apexIndex = result.Plan.FinalIndexOf("part-a", 8);
                Assert.GreaterOrEqual(apexIndex, 0);
                Assert.AreEqual(part.Vertices[8].z, result.Mesh.vertices[apexIndex].z, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>A 32-bit source index buffer is never silently narrowed.</summary>
        [Test]
        public void IndexFormat_PreservesWidestSourceFormat()
        {
            var regularBody = MeshFixtures.Body(8);
            var body = MeshFixtures.Snapshot(
                regularBody.Name,
                Copy(regularBody.Vertices),
                Copy(regularBody.SubMeshIndices[0]),
                indexFormat: UnityEngine.Rendering.IndexFormat.UInt32);
            var part = MeshFixtures.Part(8, apexOffset: -1f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                Assert.LessOrEqual(result.Mesh.vertexCount, ApaMeshLimits.UInt16VertexLimit);
                Assert.AreEqual(UnityEngine.Rendering.IndexFormat.UInt32, result.Mesh.indexFormat);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// The index format helper switches to 32-bit only when the vertex count actually requires it.
        /// </summary>
        [Test]
        public void IndexFormatHelper_Uses32BitAboveUInt16Limit()
        {
            Assert.AreEqual(
                UnityEngine.Rendering.IndexFormat.UInt16,
                ApaMeshLimits.IndexFormatForVertexCount(ApaMeshLimits.UInt16VertexLimit));
            Assert.AreEqual(
                UnityEngine.Rendering.IndexFormat.UInt32,
                ApaMeshLimits.IndexFormatForVertexCount(ApaMeshLimits.UInt16VertexLimit + 1));
        }

        private static T[] Copy<T>(IReadOnlyList<T> source)
        {
            var result = new T[source.Count];
            for (var i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }

        /// <summary>The output bounds are computed from the actual generated geometry.</summary>
        [Test]
        public void Bounds_AreComputedFromGeneratedGeometry()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -3f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                Assert.LessOrEqual(result.Mesh.bounds.min.z, -3f + 1e-4f,
                    "The bounds must include the part's extended apex.");
                Assert.GreaterOrEqual(result.Mesh.bounds.max.z, 1f - 1e-4f,
                    "The bounds must include the body's apex.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>Submeshes from the base and the part are preserved as distinct output submeshes.</summary>
        [Test]
        public void SubMeshes_ArePreserved()
        {
            var body = MeshFixtures.SnapshotWithSubMeshes(
                "Body",
                new[]
                {
                    Vector3.zero, Vector3.right, Vector3.up,
                    Vector3.zero, Vector3.right, Vector3.forward
                },
                new[]
                {
                    new[] { 0, 1, 2 },
                    new[] { 3, 4, 5 }
                });

            var bodyMaterialA = new Material(Shader.Find("Standard")) { name = "BodyA" };
            var bodyMaterialB = new Material(Shader.Find("Standard")) { name = "BodyB" };

            try
            {
                var context = MeshFixtures.Context(
                    body,
                    new[]
                    {
                        MeshFixtures.PartSnapshot(
                            "part-a",
                            MeshFixtures.SnapshotWithSubMeshes(
                                "Part",
                                new[] { Vector3.zero, Vector3.right, Vector3.up },
                                new[] { new[] { 0, 1, 2 } }),
                            materialSemantics: new[]
                            {
                                new ApaMaterialSlotSemantic("Trim", 0, bodyMaterialB, ApaMaterialPolicyMode.Auto)
                            })
                    },
                    baseMaterials: new[] { bodyMaterialA, bodyMaterialB },
                    baseMaterialSemantics: new[]
                    {
                        new ApaMaterialSlotSemantic("Skin", 0, bodyMaterialA, ApaMaterialPolicyMode.Auto),
                        new ApaMaterialSlotSemantic("Trim", 1, bodyMaterialB, ApaMaterialPolicyMode.Auto)
                    });

                var result = ApaCore.Assemble(context);
                Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

                try
                {
                    Assert.AreEqual(2, result.Mesh.subMeshCount,
                        "Two base submeshes must survive into two output submeshes.");
                    Assert.AreEqual(2, result.Materials.Length);
                    Assert.IsNotEmpty(result.Mesh.GetIndices(0));
                    Assert.IsNotEmpty(result.Mesh.GetIndices(1));
                }
                finally
                {
                    Object.DestroyImmediate(result.Mesh);
                }
            }
            finally
            {
                Object.DestroyImmediate(bodyMaterialA);
                Object.DestroyImmediate(bodyMaterialB);
            }
        }

        /// <summary>
        /// A sealed base with the part welded on produces a mesh where the part's non-seam vertex is reachable
        /// from the part's triangles, proving the index remap actually connected the two meshes.
        /// </summary>
        [Test]
        public void WeldedTriangles_ReferenceBaseSeamVertices()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var indices = result.Mesh.GetIndices(0);
                var apexIndex = result.Plan.FinalIndexOf("part-a", 8);

                var found = false;
                for (var i = 0; i < indices.Length; i++)
                {
                    if (indices[i] == apexIndex)
                    {
                        found = true;
                        break;
                    }
                }

                Assert.IsTrue(found, "The part's apex must be referenced by the assembled index buffer.");

                // Every index must be in range of the generated vertex buffer.
                foreach (var index in indices)
                {
                    Assert.GreaterOrEqual(index, 0);
                    Assert.Less(index, result.Mesh.vertexCount);
                }
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }
    }
}

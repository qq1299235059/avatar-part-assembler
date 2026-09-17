using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests determinism and the non-mutation guarantee.
    /// </summary>
    /// <remarks>
    /// Determinism is a product requirement, not a nicety: identical inputs must produce identical plans,
    /// orderings, and output data. The tests below compare full output arrays rather than a summary, because a
    /// summary can hide a reordered buffer.
    /// </remarks>
    public sealed class DeterminismAndImmutabilityTests
    {
        /// <summary>Planning the same context twice produces identical vertex and index data.</summary>
        [Test]
        public void PlanningTwice_ProducesIdenticalPlanData()
        {
            var context = BuildContext();

            var first = ApaCore.Plan(context);
            var second = ApaCore.Plan(context);

            Assert.IsTrue(first.Succeeded, first.Issues.FormatAll());
            Assert.IsTrue(second.Succeeded, second.Issues.FormatAll());

            Assert.AreEqual(first.Plan.VertexCount, second.Plan.VertexCount);
            Assert.AreEqual(first.Plan.SubMeshes.Count, second.Plan.SubMeshes.Count);

            for (var i = 0; i < first.Plan.Vertices.Count; i++)
            {
                Assert.AreEqual(first.Plan.Vertices[i].Origin, second.Plan.Vertices[i].Origin);
                Assert.AreEqual(first.Plan.Vertices[i].SourceVertex, second.Plan.Vertices[i].SourceVertex);
                Assert.AreEqual(first.Plan.Vertices[i].PartId, second.Plan.Vertices[i].PartId);
            }

            for (var s = 0; s < first.Plan.SubMeshes.Count; s++)
            {
                CollectionAssert.AreEqual(
                    first.Plan.SubMeshes[s].Indices,
                    second.Plan.SubMeshes[s].Indices,
                    "Submesh " + s + " index buffer must be identical between runs.");
            }
        }

        /// <summary>Assembling the same context twice produces byte-identical vertex positions.</summary>
        [Test]
        public void AssemblingTwice_ProducesIdenticalMeshData()
        {
            var context = BuildContext();

            var first = ApaCore.Assemble(context);
            var second = ApaCore.Assemble(context);

            Assert.IsTrue(first.Succeeded, first.Issues.FormatAll());
            Assert.IsTrue(second.Succeeded, second.Issues.FormatAll());

            try
            {
                CollectionAssert.AreEqual(first.Mesh.vertices, second.Mesh.vertices);
                Assert.AreEqual(first.Mesh.indexFormat, second.Mesh.indexFormat);
                Assert.AreEqual(first.Mesh.subMeshCount, second.Mesh.subMeshCount);

                for (var s = 0; s < first.Mesh.subMeshCount; s++)
                {
                    CollectionAssert.AreEqual(first.Mesh.GetIndices(s), second.Mesh.GetIndices(s));
                }
            }
            finally
            {
                Object.DestroyImmediate(first.Mesh);
                Object.DestroyImmediate(second.Mesh);
            }
        }

        /// <summary>
        /// Issue ordering is deterministic and follows phase, code, part identity, then source index.
        /// </summary>
        [Test]
        public void IssueOrdering_IsDeterministic()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(7, apexOffset: -1f);

            var seam = new ApaSeamProfile();
            seam.SetPaired(Range(8), Range(7));

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, seam, MeshFixtures.Removed(0, 999))
            });

            var first = ApaCore.Validate(context);
            var second = ApaCore.Validate(context);

            Assert.IsTrue(first.HasErrors);
            CollectionAssert.AreEqual(first.Issues, second.Issues,
                "Validation results must be identical and identically ordered across runs.");

            for (var i = 1; i < first.Issues.Count; i++)
            {
                Assert.LessOrEqual(first.Issues[i - 1].CompareTo(first.Issues[i]), 0,
                    "Issues must be returned in ascending deterministic order.");
            }
        }

        /// <summary>Parts are ordered by profile identity, not by the order they were supplied.</summary>
        /// <remarks>
        /// This is the ordering rule that keeps a build stable when an object is reparented in the hierarchy. The
        /// test supplies parts in reverse identity order and asserts that the plan still processes them in the
        /// canonical order.
        /// </remarks>
        [Test]
        public void Parts_AreOrderedByIdentityNotSupplyOrder()
        {
            var body = MeshFixtures.Body(8);
            var partA = MeshFixtures.Part(8, apexOffset: -1f, name: "PartA");
            var partB = MeshFixtures.Part(8, apexOffset: -1f, name: "PartB");

            var forward = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(8), null, ApaPartSlot.LeftArm),
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(8), null, ApaPartSlot.RightArm)
            });

            var reversed = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(8), null, ApaPartSlot.RightArm),
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(8), null, ApaPartSlot.LeftArm)
            });

            var firstPlan = ApaCore.Plan(forward);
            var secondPlan = ApaCore.Plan(reversed);

            Assert.IsTrue(firstPlan.Succeeded, firstPlan.Issues.FormatAll());
            Assert.IsTrue(secondPlan.Succeeded, secondPlan.Issues.FormatAll());

            // LeftArm sorts before RightArm, so the vertex provenance must match regardless of supply order.
            for (var i = 0; i < firstPlan.Plan.Vertices.Count; i++)
            {
                Assert.AreEqual(
                    firstPlan.Plan.Vertices[i].PartId,
                    secondPlan.Plan.Vertices[i].PartId,
                    "Vertex " + i + " must come from the same part in both runs.");
            }
        }

        /// <summary>
        /// A failed plan leaves the caller's snapshots — and therefore the meshes they were copied from —
        /// untouched.
        /// </summary>
        /// <remarks>
        /// Snapshots are constructed from arrays that the test retains a reference to. If the pipeline mutated
        /// them, the retained arrays would change. This is a direct test of the non-mutation guarantee.
        /// </remarks>
        [Test]
        public void FailedPlan_DoesNotMutateInputs()
        {
            var bodyPositions = MeshFixtures.Ring(8, 1f);
            var bodyTriangles = MeshFixtures.CapTriangles(8, 0, 8);
            var body = MeshFixtures.Snapshot("Body", bodyPositions, bodyTriangles, MeshFixtures.RingUvs(8));

            var partPositions = MeshFixtures.Ring(8, 1f);
            var partTriangles = MeshFixtures.CapTriangles(8, 0, 8);
            var part = MeshFixtures.Snapshot("Part", partPositions, partTriangles, MeshFixtures.RingUvs(8));

            var positionsCopy = (Vector3[])bodyPositions.Clone();
            var trianglesCopy = (int[])bodyTriangles.Clone();
            var partPositionsCopy = (Vector3[])partPositions.Clone();

            // A cardinality mismatch guarantees failure after both meshes have been read.
            var seam = new ApaSeamProfile();
            seam.SetPaired(Range(8), Range(4));

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, seam)
            });

            var result = ApaCore.Assemble(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsNull(result.Mesh);

            CollectionAssert.AreEqual(positionsCopy, bodyPositions, "Base positions must be unmodified.");
            CollectionAssert.AreEqual(trianglesCopy, bodyTriangles, "Base triangles must be unmodified.");
            CollectionAssert.AreEqual(partPositionsCopy, partPositions, "Part positions must be unmodified.");
        }

        /// <summary>
        /// A successful assembly does not modify the caller's input arrays either.
        /// </summary>
        [Test]
        public void SuccessfulAssembly_DoesNotMutateInputs()
        {
            var bodyPositions = MeshFixtures.Ring(8, 1f);
            var body = MeshFixtures.Snapshot("Body", bodyPositions, MeshFixtures.CapTriangles(8, 0, 8), MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var positionsCopy = (Vector3[])bodyPositions.Clone();

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                CollectionAssert.AreEqual(positionsCopy, bodyPositions,
                    "Assembly must read the base mesh, never write it.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        private static ValidationContext BuildContext()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            return MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });
        }

        private static int[] Range(int count)
        {
            var result = new int[count];
            for (var i = 0; i < count; i++) result[i] = i;
            return result;
        }
    }
}

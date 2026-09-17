using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for removal validation: declared triangle sets remove only what they declare, and malformed sets
    /// block.
    /// </summary>
    public sealed class RemovalValidationTests
    {
        /// <summary>A declared triangle set removes exactly those triangles and nothing else.</summary>
        [Test]
        public void DeclaredTriangles_AreRemoved()
        {
            var body = MeshFixtures.Body(6);
            var part = MeshFixtures.Part(6, apexOffset: -1f);

            // The body cap has six triangles; remove exactly one of them.
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(6), MeshFixtures.Removed(0, 0))
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(1, result.Plan.RemovedTriangleCount);
            Assert.AreEqual(new RemovedTriangleAddress(0, 0), result.Plan.RemovedTriangles[0]);
        }

        /// <summary>A removal address affects only its named submesh.</summary>
        [Test]
        public void RemovalInSecondSubMesh_DoesNotAliasFirstSubMesh()
        {
            var regularBody = MeshFixtures.Body(6);
            var body = MeshFixtures.SnapshotWithSubMeshes(
                "Body",
                Copy(regularBody.Vertices),
                new[]
                {
                    new[] { 0, 1, 6 },
                    new[] { 1, 2, 6, 2, 3, 6, 3, 4, 6, 4, 5, 6, 5, 0, 6 }
                });
            var part = MeshFixtures.Part(6, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(6), MeshFixtures.Removed(1, 0))
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(new RemovedTriangleAddress(1, 0), result.Plan.RemovedTriangles[0]);
            Assert.AreEqual(7, result.Plan.SubMeshes[0].TriangleCount,
                "Submesh 0 keeps its base triangle and also receives the part's six triangles.");
            Assert.AreEqual(4, result.Plan.SubMeshes[1].TriangleCount,
                "Only triangle 0 of submesh 1 is removed.");
        }

        /// <summary>An out-of-range removal index blocks with <c>APA017</c>.</summary>
        [Test]
        public void OutOfRangeRemovalIndex_ReportsApa017()
        {
            var body = MeshFixtures.Body(6);
            var part = MeshFixtures.Part(6, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(6), MeshFixtures.Removed(0, 99))
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "An out-of-range removal index must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.RemovalIndexOutOfRange));
        }

        /// <summary>A negative removal address component is <c>APA026</c>, not a silent no-op.</summary>
        [Test]
        public void NegativeRemovalIndex_ReportsApa026()
        {
            var body = MeshFixtures.Body(6);
            var part = MeshFixtures.Part(6, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(6), MeshFixtures.Removed(0, -1))
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.InvalidTriangleAddress));
        }

        /// <summary>A repeated address is canonicalized because removal data is a mathematical set.</summary>
        [Test]
        public void DuplicateAddressWithinPart_IsCanonicalized()
        {
            var body = MeshFixtures.Body(6);
            var part = MeshFixtures.Part(6, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(6), MeshFixtures.Removed(0, 0, 0))
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(1, result.Plan.RemovedTriangleCount,
                "A removal set must canonicalize duplicate addresses.");
        }

        /// <summary>Two parts claiming the same triangle is <c>APA010</c>.</summary>
        [Test]
        public void OverlappingRemovalAcrossParts_ReportsApa010()
        {
            var body = MeshFixtures.Body(6);
            var partA = MeshFixtures.Part(6, apexOffset: -1f, name: "PartA");
            var partB = MeshFixtures.Part(6, apexOffset: -2f, name: "PartB");

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(6), MeshFixtures.Removed(0, 0, 1),
                    ApaPartSlot.LeftArm),
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(6), MeshFixtures.Removed(0, 1, 2),
                    ApaPartSlot.RightArm)
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "Overlapping removal regions must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.RemovalRegionOverlap));

            var issue = result.Issues.FindByCode(ApaErrorCode.RemovalRegionOverlap);
            Assert.AreEqual(1, issue.SourceIndex, "The overlap must be reported against triangle 1.");
        }

        /// <summary>
        /// Removing triangles never removes a seam vertex the part welds onto, because the planner retains seam
        /// vertices explicitly.
        /// </summary>
        /// <remarks>
        /// This is the case that produces an apparently successful build followed by a detached limb. A seam
        /// vertex must survive removal even when every triangle that originally referenced it was removed.
        /// </remarks>
        [Test]
        public void SeamVertices_SurviveRemovalOfAllTheirTriangles()
        {
            var body = MeshFixtures.Body(6);
            var part = MeshFixtures.Part(6, apexOffset: -1f);

            // Remove every triangle of the body cap.
            var allTriangles = new int[6];
            for (var i = 0; i < allTriangles.Length; i++) allTriangles[i] = i;

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(6), MeshFixtures.Removed(0, allTriangles))
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            for (var i = 0; i < 6; i++)
            {
                Assert.GreaterOrEqual(
                    result.Plan.FinalIndexOf(string.Empty, i),
                    0,
                    "Base seam vertex " + i + " must survive removal because a part welds onto it.");
            }

            // The body apex is only referenced by the removed triangles, so it must not be emitted.
            Assert.AreEqual(-1, result.Plan.FinalIndexOf(string.Empty, 6),
                "An unreferenced non-seam base vertex must not be emitted.");
        }

        private static T[] Copy<T>(IReadOnlyList<T> source)
        {
            var result = new T[source.Count];
            for (var i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }
    }
}

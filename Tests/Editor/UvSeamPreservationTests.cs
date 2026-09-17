using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for M11's attribute-aware seam merge: a pair whose same-name UVs disagree keeps the part's own
    /// vertex instead of blocking, and the preserved vertex is positioned on the base vertex so the surface stays
    /// closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scenarios are the ones a real part with a different UV atlas produces: the body's <c>UV0</c> and the
    /// part's <c>UV0</c> disagree at some or all seam vertices, and the two are not the same texture mapping.
    /// </para>
    /// <para>
    /// Every test declares the part's UV semantic explicitly. A part with no declaration contributes the implicit
    /// <c>UV0</c>; a body that declares <c>UVMap</c> then shares no semantic with it, there is nothing to compare,
    /// and the pair welds. That is the documented behaviour, not the case under test.
    /// </para>
    /// </remarks>
    public sealed class UvSeamPreservationTests
    {
        private const int RingCount = 8;

        /// <summary>
        /// A disagreeing pair keeps both sides' UV values, and the preserved vertex sits exactly on the base
        /// vertex.
        /// </summary>
        [Test]
        public void MismatchedUvs_KeepBothSidesUvsAndOnePosition()
        {
            var baseUvs = MeshFixtures.RingUvs(RingCount);
            var body = MeshFixtures.Body(RingCount, baseUvs);

            var partUvs = MeshFixtures.RingUvs(RingCount);
            partUvs[3] = new Vector4(partUvs[3].x + 0.05f, partUvs[3].y, 0f, 0f);
            var part = MeshFixtures.Part(RingCount, partUvs, apexOffset: -1f);

            var context = SeamContext(body, part);

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var splitFinal = result.Plan.FinalIndexOf("part-a", 3);
                var baseFinal = result.Plan.FinalIndexOf(string.Empty, 3);
                Assert.GreaterOrEqual(splitFinal, 0);
                Assert.GreaterOrEqual(baseFinal, 0);
                Assert.AreNotEqual(baseFinal, splitFinal, "A preserved pair must keep its own vertex.");
                Assert.IsTrue(result.Plan.Vertices[splitFinal].IsSplit);
                Assert.AreEqual(3, result.Plan.Vertices[splitFinal].WeldedBaseVertex,
                    "The preserved vertex records the base vertex it is paired with.");

                // The two sides coincide exactly, so no crack is visible at rest.
                Assert.AreEqual(result.Mesh.vertices[baseFinal], result.Mesh.vertices[splitFinal],
                    "A preserved vertex must sit exactly on the base vertex it is paired with.");

                var channel = result.Plan.UvLayout.FindOutputChannel("UVMap");
                Assert.GreaterOrEqual(channel, 0);
                var uvs = new List<Vector4>();
                result.Mesh.GetUVs(channel, uvs);

                Assert.AreEqual(baseUvs[3], uvs[baseFinal], "The body keeps its own UV.");
                Assert.AreEqual(partUvs[3], uvs[splitFinal], "The part keeps its own UV.");
            }
            finally
            {
                result.Destroy();
            }
        }

        /// <summary>
        /// A pair the author wrote stays a pair even when the two vertices are not coincident: the preserved
        /// vertex is snapped onto the base vertex rather than left where the part put it.
        /// </summary>
        [Test]
        public void DisplacedPreservedPair_IsSnappedToTheBasePosition()
        {
            var baseUvs = MeshFixtures.RingUvs(RingCount);
            var body = MeshFixtures.Body(RingCount, baseUvs);

            var partUvs = MeshFixtures.RingUvs(RingCount);
            partUvs[3] = new Vector4(partUvs[3].x + 0.05f, partUvs[3].y, 0f, 0f);

            var positions = new List<Vector3>(MeshFixtures.Ring(RingCount, 1f));
            positions[3] = positions[3] + new Vector3(0.05f, 0f, 0f);
            positions.Add(new Vector3(0f, 0f, -1f));
            var part = MeshFixtures.Snapshot(
                "Part", positions.ToArray(), MeshFixtures.CapTriangles(RingCount, 0, RingCount), partUvs);

            var context = SeamContext(body, part);
            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var splitFinal = result.Plan.FinalIndexOf("part-a", 3);
                var baseFinal = result.Plan.FinalIndexOf(string.Empty, 3);

                Assert.AreEqual(result.Mesh.vertices[baseFinal], result.Mesh.vertices[splitFinal],
                    "A displaced preserved pair must be snapped to the base vertex so the seam closes.");
            }
            finally
            {
                result.Destroy();
            }
        }

        /// <summary>
        /// A semantic the body does not have is kept on the preserved vertex, and the body's own vertex receives
        /// the channel default rather than the part's value.
        /// </summary>
        [Test]
        public void PreservedPair_KeepsThePartOnlySemanticOnThePartVertex()
        {
            var body = MeshFixtures.Body(RingCount, MeshFixtures.RingUvs(RingCount));

            var partUvs = MeshFixtures.RingUvs(RingCount);
            partUvs[3] = new Vector4(partUvs[3].x + 0.05f, partUvs[3].y, 0f, 0f);
            var decal = new Vector4[RingCount + 1];
            for (var i = 0; i < decal.Length; i++) decal[i] = new Vector4(0.1f + i * 0.01f, 0.9f, 0f, 0f);

            var part = MeshFixtures.Snapshot(
                "Part",
                PartPositions(),
                MeshFixtures.CapTriangles(RingCount, 0, RingCount),
                partUvs,
                uv1: decal);

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(RingCount),
                        uvSemantics: new[]
                        {
                            new ApaUvChannelSemantic("UVMap", 0),
                            new ApaUvChannelSemantic("ArmDecal", 1)
                        })
                },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var splitFinal = result.Plan.FinalIndexOf("part-a", 3);
                var baseFinal = result.Plan.FinalIndexOf(string.Empty, 3);

                var channel = result.Plan.UvLayout.FindOutputChannel("ArmDecal");
                Assert.GreaterOrEqual(channel, 0);
                var uvs = new List<Vector4>();
                result.Mesh.GetUVs(channel, uvs);

                Assert.AreEqual(decal[3], uvs[splitFinal],
                    "The preserved vertex carries the part's own value for a part-only semantic.");
                Assert.AreEqual(Vector4.zero, uvs[baseFinal],
                    "The body's own vertex must not receive the part's value.");
            }
            finally
            {
                result.Destroy();
            }
        }

        /// <summary>
        /// One part that welds and one that is preserved do not conflict: the preserved part has a vertex of its
        /// own, so the shared base vertex only has to represent the welding part's value.
        /// </summary>
        [Test]
        public void OneWeldedPartAndOnePreservedPart_DoNotConflict()
        {
            var body = MeshFixtures.Body(RingCount, MeshFixtures.RingUvs(RingCount));

            var matching = MeshFixtures.Part(RingCount, MeshFixtures.RingUvs(RingCount), apexOffset: -1f);

            var mismatchedUvs = MeshFixtures.RingUvs(RingCount);
            mismatchedUvs[0] = new Vector4(mismatchedUvs[0].x + 0.25f, mismatchedUvs[0].y, 0f, 0f);
            var mismatched = MeshFixtures.Part(RingCount, mismatchedUvs, apexOffset: -2f, name: "PartB");

            var semantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };
            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a", matching, MeshFixtures.Seam(RingCount),
                        slot: ApaPartSlot.LeftArm, uvSemantics: semantics),
                    MeshFixtures.PartSnapshot(
                        "part-b", mismatched, MeshFixtures.Seam(RingCount),
                        slot: ApaPartSlot.RightArm, uvSemantics: semantics)
                },
                baseUvSemantics: semantics);

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.WeldUvConflict));
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.SeamUvMismatch));

            Assert.AreEqual(0, result.Plan.SeamWeldsFor("part-a").SplitCount);
            Assert.AreEqual(1, result.Plan.SeamWeldsFor("part-b").SplitCount);
        }

        /// <summary>
        /// A blend shape that exists only on the part is not blocked at a preserved pair, and the preserved
        /// vertex follows the base body so the seam cannot open under deformation.
        /// </summary>
        [Test]
        public void PreservedPair_FollowsTheBaseUnderBlendShapeDeformation()
        {
            var bodyPositions = PartPositions();
            var body = MeshFixtures.BlendShapedSnapshot(
                "Body",
                bodyPositions,
                MeshFixtures.CapTriangles(RingCount, 0, RingCount),
                new[] { "Blink" },
                new[] { new[] { MeshFixtures.Frame(100f, bodyPositions.Length, 3, new Vector3(0f, 0f, 0.1f)) } },
                FullUvs(MeshFixtures.RingUvs(RingCount)));

            var partUvs = MeshFixtures.RingUvs(RingCount);
            partUvs[3] = new Vector4(partUvs[3].x + 0.05f, partUvs[3].y, 0f, 0f);
            var partPositions = PartPositions();
            partPositions[RingCount] = new Vector3(0f, 0f, -1f);
            var part = MeshFixtures.BlendShapedSnapshot(
                "Part",
                partPositions,
                MeshFixtures.CapTriangles(RingCount, 0, RingCount),
                new[] { "Blink" },
                new[] { new[] { MeshFixtures.Frame(100f, partPositions.Length, 3, new Vector3(0f, 0f, 0.9f)) } },
                FullUvs(partUvs));

            var context = SeamContext(body, part);
            var result = ApaCore.Assemble(context);

            Assert.IsTrue(result.Succeeded,
                "A preserved pair represents the shape, so it must not be refused." + result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.BlendShapeSeamDeltaMismatch));

            try
            {
                var shape = MeshFixtures.OutputShapeIndex(result.Mesh, "Blink");
                Assert.GreaterOrEqual(shape, 0);

                var deltas = MeshFixtures.ReadFramePositionDeltas(result.Mesh, shape, 0);
                var splitFinal = result.Plan.FinalIndexOf("part-a", 3);
                var baseFinal = result.Plan.FinalIndexOf(string.Empty, 3);

                Assert.AreEqual(new Vector3(0f, 0f, 0.1f), deltas[baseFinal]);
                Assert.AreEqual(deltas[baseFinal], deltas[splitFinal],
                    "The preserved vertex must move exactly with the base vertex, or the seam opens.");
            }
            finally
            {
                result.Destroy();
            }
        }

        /// <summary>
        /// The whole decision is reproducible: two plans over the same input agree on the vertex list and on the
        /// weld/split counts.
        /// </summary>
        [Test]
        public void PreservedPlan_IsDeterministic()
        {
            var body = MeshFixtures.Body(RingCount, MeshFixtures.RingUvs(RingCount));

            var partUvs = MeshFixtures.RingUvs(RingCount);
            partUvs[2] = new Vector4(partUvs[2].x + 0.1f, partUvs[2].y, 0f, 0f);
            partUvs[5] = new Vector4(partUvs[5].x + 0.2f, partUvs[5].y, 0f, 0f);
            var part = MeshFixtures.Part(RingCount, partUvs, apexOffset: -1f);

            var first = ApaCore.Plan(SeamContext(body, part));
            var second = ApaCore.Plan(SeamContext(body, part));

            Assert.IsTrue(first.Succeeded, first.Issues.FormatAll());
            Assert.IsTrue(second.Succeeded, second.Issues.FormatAll());

            Assert.AreEqual(first.Plan.VertexCount, second.Plan.VertexCount);
            Assert.AreEqual(2, first.Plan.SeamWeldsFor("part-a").SplitCount);
            Assert.AreEqual(2, second.Plan.SeamWeldsFor("part-a").SplitCount);

            for (var i = 0; i < first.Plan.VertexCount; i++)
            {
                Assert.AreEqual(first.Plan.Vertices[i].Origin, second.Plan.Vertices[i].Origin);
                Assert.AreEqual(first.Plan.Vertices[i].SourceVertex, second.Plan.Vertices[i].SourceVertex);
                Assert.AreEqual(first.Plan.Vertices[i].PartId, second.Plan.Vertices[i].PartId);
                Assert.AreEqual(first.Plan.Vertices[i].SeamRole, second.Plan.Vertices[i].SeamRole);
            }
        }

        /// <summary>
        /// An automatically preserved channel takes part in the weld/split decision exactly like a declared one:
        /// when its values differ at a pair, the part keeps its own vertex instead of the value being overwritten.
        /// </summary>
        /// <remarks>
        /// This is the seam between the two halves of M11. An undeclared channel is contributed under a generated
        /// passthrough semantic, so the body's channel 0 and the part's channel 0 are the <i>same</i> semantic and
        /// the resolver is comparing real values rather than nothing. Without that, the automatic channel would
        /// reach the output and still be silently welded away at every seam vertex.
        /// </remarks>
        [Test]
        public void AutoPreservedChannel_ParticipatesInTheWeldSplitDecision()
        {
            // Both meshes carry channel 0 with one value per vertex — the ring plus the apex. The two channels use
            // different offsets, so every seam pair disagrees on the automatically preserved semantic.
            var body = MultiPartFixtures.RingMeshWithUv0("Body", RingCount, 1f);

            var part = MultiPartFixtures.WithChannels(
                MultiPartFixtures.RingMeshWithUv0("Part", RingCount, -1f),
                MultiPartFixtures.RampUv(RingCount + 1, 0.5f),
                MultiPartFixtures.RampUv(RingCount + 1, 0.25f));

            // Neither side declares channel 0, so both contribute it as the passthrough semantic UV0; the part
            // declares only channel 1.
            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(RingCount),
                        uvSemantics: new[] { new ApaUvChannelSemantic("ArmDecal", 1) })
                });

            var result = ApaCore.Plan(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var welds = result.Plan.SeamWeldsFor("part-a");
            Assert.IsNotNull(welds);
            Assert.AreEqual(0, welds.WeldCount, "Every pair disagrees on the automatically preserved channel.");
            Assert.AreEqual(RingCount, welds.SplitCount, "The part's own values must survive the seam.");
            Assert.AreEqual(RingCount, result.Plan.CountEmittedSplitSeamVertices());

            var summary = result.Issues.FindByCode(ApaErrorCode.SeamUvPreserved);
            Assert.IsNotNull(summary, result.Issues.FormatAll());
            StringAssert.Contains("semantic=" + ApaWellKnownSemantics.Uv0, summary.Detail);

            var auto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            Assert.AreEqual(1, auto.Count, "The undeclared channel is recorded for the part.");
            Assert.AreEqual("part-a", auto[0].PartId);
            Assert.AreEqual(ApaWellKnownSemantics.Uv0, auto[0].Semantic);
        }

        private static ValidationContext SeamContext(MeshSnapshot body, MeshSnapshot part)
        {
            var semantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };
            return MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a", part, MeshFixtures.Seam(RingCount), uvSemantics: semantics)
                },
                baseUvSemantics: semantics);
        }

        /// <summary>The ring plus an apex at +Z, which is the body's own shape.</summary>
        private static Vector3[] PartPositions()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(RingCount, 1f))
            {
                new Vector3(0f, 0f, 1f)
            };

            return positions.ToArray();
        }

        /// <summary>
        /// A ring UV set padded to the mesh's full vertex count. A channel whose length disagrees with the vertex
        /// count is not a channel at all, so a shorter array would make the fixture report "no UVs" and the test
        /// would compare nothing.
        /// </summary>
        private static Vector4[] FullUvs(Vector4[] ringUvs)
        {
            var uvs = new Vector4[RingCount + 1];
            for (var i = 0; i < RingCount; i++) uvs[i] = ringUvs[i];
            uvs[RingCount] = new Vector4(0.5f, 0.5f, 0f, 0f);
            return uvs;
        }
    }
}

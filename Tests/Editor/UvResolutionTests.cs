using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for UV semantic resolution: merging by name, strict seam agreement, defaults, and the channel
    /// limit.
    /// </summary>
    public sealed class UvResolutionTests
    {
        /// <summary>Same semantic name in different source channels merges into one final channel.</summary>
        [Test]
        public void SameSemanticInDifferentChannels_MergesToOneFinalChannel()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            // The body declares its UV set in channel 0; the part declares the same semantic in channel 1.
            var partUvs = MeshSnapshot.NewUvArray();
            partUvs[1] = Copy(part.GetUvChannel(0));
            var partMesh = MeshFixtures.Snapshot(
                "Part", Copy(part.Vertices), Copy(part.SubMeshIndices[0]), null, null, null, partUvs[1]);
            partUvs[0] = System.Array.Empty<Vector4>();

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        partMesh,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 1) })
                },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(1, result.Plan.UvLayout.ChannelCount,
                "A shared semantic must occupy exactly one final channel.");
            Assert.AreEqual(0, result.Plan.UvLayout.FindOutputChannel("UVMap"));
        }

        /// <summary>Same-name UVs that agree at the seam weld without error.</summary>
        /// <remarks>
        /// The part declares the semantic explicitly so that the two sides really do share a semantic; without a
        /// declaration the part would contribute the implicit <c>UV0</c> and the test would compare nothing.
        /// </remarks>
        [Test]
        public void SameSemanticMatchingSeamUvs_Welds()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) })
                },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.SeamUvMismatch));
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.SeamUvPreserved),
                "Agreeing seam UVs are welded, so nothing was preserved.");

            var welds = result.Plan.SeamWeldsFor("part-a");
            Assert.IsNotNull(welds);
            Assert.AreEqual(8, welds.WeldCount);
            Assert.AreEqual(0, welds.SplitCount);
        }

        /// <summary>
        /// Same-name UVs that disagree at the seam are preserved as split vertices instead of blocking (M11).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A part and a body routinely use different UV atlases, so a seam that coincides in space and disagrees
        /// in UV is normal input. The part seam vertex is kept as a part-owned vertex: the body keeps its own UV,
        /// the part keeps its own, and the two positions are made identical. The condition is reported once per
        /// part and semantic as <c>APA045</c>, never as a blocking <c>APA004</c>.
        /// </para>
        /// <para>
        /// The part's semantic is declared explicitly, because a part with no declaration contributes the implicit
        /// <c>UV0</c> and would then share no semantic with a body that declares <c>UVMap</c> — a configuration in
        /// which there is nothing to compare and the pair welds.
        /// </para>
        /// </remarks>
        [Test]
        public void SameSemanticMismatchedSeamUvs_PreservesThePartVertex()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));

            var partUvs = MeshFixtures.RingUvs(8);
            partUvs[3] = new Vector4(partUvs[3].x + 0.05f, partUvs[3].y, 0f, 0f);
            var part = MeshFixtures.Part(8, partUvs, apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) })
                },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded,
                "A UV atlas difference must not block the install." + result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.SeamUvMismatch),
                "A representable difference is preserved, not reported as an error.");

            var summary = result.Issues.FindByCode(ApaErrorCode.SeamUvPreserved);
            Assert.IsNotNull(summary, "The preserved pair must be reported as one informational summary.");
            StringAssert.Contains("reason=uv-seam-preserved", summary.Detail);
            StringAssert.Contains("semantic=UVMap", summary.Detail);
            StringAssert.Contains("preservedPairs=1", summary.Detail);

            var welds = result.Plan.SeamWeldsFor("part-a");
            Assert.IsNotNull(welds);
            Assert.AreEqual(7, welds.WeldCount);
            Assert.AreEqual(1, welds.SplitCount);
            Assert.AreEqual(1, result.Plan.CountEmittedSplitSeamVertices());
            Assert.AreEqual(0, result.Plan.CountEmittedPartSeamVertices(),
                "The preserved vertex is not a weld, and no welded part vertex is emitted.");
        }

        /// <summary>
        /// A semantic present only on the base is preserved, and part vertices receive the channel default.
        /// </summary>
        [Test]
        public void BaseOnlySemantic_IsPreservedWithZeroFillOnPart()
        {
            var bodyUv1 = MeshFixtures.RingUvs(8, 0.25f);
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8), "Body");
            var bodyWithTwo = MeshFixtures.Snapshot(
                "Body", Copy(body.Vertices), Copy(body.SubMeshIndices[0]), Copy(body.GetUvChannel(0)), null, null, bodyUv1);

            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            var context = MeshFixtures.Context(
                bodyWithTwo,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                baseUvSemantics: new[]
                {
                    new ApaUvChannelSemantic("UVMap", 0),
                    new ApaUvChannelSemantic("DetailUV", 1)
                },
                baseMaterialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, null, ApaMaterialPolicyMode.Auto) });

            var result = ApaCore.Plan(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var detailChannel = result.Plan.UvLayout.FindOutputChannel("DetailUV");
            Assert.GreaterOrEqual(detailChannel, 0, "A base-only semantic must survive into the output layout.");
        }

        /// <summary>A semantic present only on the part creates a new final channel.</summary>
        [Test]
        public void PartOnlySemantic_CreatesNewChannel()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[]
                        {
                            new ApaUvChannelSemantic("UVMap", 0),
                            new ApaUvChannelSemantic("ArmDecal", 1)
                        })
                },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.UvLayout.ChannelCount);
            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel("ArmDecal"), 0);
        }

        /// <summary>A part-only semantic supplies the retained base vertex at a true weld.</summary>
        [Test]
        public void PartOnlySemantic_WritesPartValueAtWeldedBaseVertex()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var originalPart = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);
            var armDecal = new Vector4[originalPart.VertexCount];
            for (var i = 0; i < armDecal.Length; i++)
            {
                armDecal[i] = new Vector4(0.25f + i * 0.01f, 0.75f, 0f, 0f);
            }

            var part = WithUv1(originalPart, armDecal);
            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[] { new ApaUvChannelSemantic("ArmDecal", 1) })
                });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var outputChannel = result.Plan.UvLayout.FindOutputChannel("ArmDecal");
                var output = new List<Vector4>();
                result.Mesh.GetUVs(outputChannel, output);
                var weldedBaseVertex = result.Plan.FinalIndexOf(string.Empty, 0);

                Assert.AreEqual(armDecal[0], output[weldedBaseVertex]);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>Two parts cannot assign different values to one welded part-only semantic.</summary>
        [Test]
        public void ConflictingPartOnlyValuesAtSharedWeld_ReportApa025()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var sourceA = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f, name: "PartA");
            var sourceB = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -2f, name: "PartB");
            var valuesA = new Vector4[sourceA.VertexCount];
            var valuesB = new Vector4[sourceB.VertexCount];
            for (var i = 0; i < valuesA.Length; i++)
            {
                valuesA[i] = new Vector4(0.25f, 0.5f, 0f, 0f);
                valuesB[i] = valuesA[i];
            }
            valuesB[0] = new Vector4(0.75f, 0.5f, 0f, 0f);

            var semantic = new[] { new ApaUvChannelSemantic("ArmDecal", 1) };
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot(
                    "part-a", WithUv1(sourceA, valuesA), MeshFixtures.Seam(8),
                    slot: ApaPartSlot.LeftArm, uvSemantics: semantic),
                MeshFixtures.PartSnapshot(
                    "part-b", WithUv1(sourceB, valuesB), MeshFixtures.Seam(8),
                    slot: ApaPartSlot.RightArm,
                    uvSemantics: new[] { new ApaUvChannelSemantic("ArmDecal", 1) })
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.WeldUvConflict));
        }

        /// <summary>More than eight final semantics is <c>APA005</c> and blocks.</summary>
        [Test]
        public void MoreThanEightSemantics_ReportsApa005()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            var many = new List<ApaUvChannelSemantic>();
            for (var i = 0; i < 9; i++) many.Add(new ApaUvChannelSemantic("Layer" + i, 0));

            var context = MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8), uvSemantics: many) });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "Nine UV channels must block, because Unity supports eight.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.UvChannelOverflow));
        }

        /// <summary>A duplicated semantic within one source is reported rather than silently resolved.</summary>
        [Test]
        public void DuplicateSemanticWithinSource_IsReported()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[]
                        {
                            new ApaUvChannelSemantic("UVMap", 0),
                            new ApaUvChannelSemantic("UVMap", 1)
                        })
                });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.DuplicateSemantic));
        }

        /// <summary>An empty semantic name is rejected rather than treated as "no opinion".</summary>
        [Test]
        public void EmptySemanticName_IsRejected()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[] { new ApaUvChannelSemantic("   ", 0) })
                });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.InvalidSemanticName));
        }

        /// <summary>
        /// Semantic names are compared ordinally, so case differences are genuinely different semantics.
        /// </summary>
        [Test]
        public void SemanticNames_AreCaseSensitive()
        {
            var body = MeshFixtures.Body(8, MeshFixtures.RingUvs(8));
            var part = MeshFixtures.Part(8, MeshFixtures.RingUvs(8), apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        MeshFixtures.Seam(8),
                        uvSemantics: new[] { new ApaUvChannelSemantic("uvmap", 0) })
                },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.UvLayout.ChannelCount,
                "'UVMap' and 'uvmap' are different semantics under ordinal comparison.");
        }
        private static T[] Copy<T>(IReadOnlyList<T> source)
        {
            var result = new T[source.Count];
            for (var i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }

        private static MeshSnapshot WithUv1(MeshSnapshot source, Vector4[] uv1)
        {
            return MeshFixtures.Snapshot(
                source.Name,
                Copy(source.Vertices),
                Copy(source.SubMeshIndices[0]),
                source.HasUvChannel(0) ? Copy(source.GetUvChannel(0)) : null,
                source.HasNormals ? Copy(source.Normals) : null,
                source.HasTangents ? Copy(source.Tangents) : null,
                uv1,
                source.IndexFormat);
        }
    }
}

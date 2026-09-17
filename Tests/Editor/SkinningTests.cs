using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using AvatarPartAssembler.Editor;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for M2 skinning: the final bone table, bone weight remapping, and bind pose reconstruction (R10).
    /// </summary>
    /// <remarks>
    /// The scenarios are deliberately small and translation-only. A skinning bug is almost always either a
    /// wrong bone index, a weight taken from the wrong side of a weld, or a matrix multiplied the wrong way
    /// round, and each of those is directly visible in a fixture with two bones and one known translation.
    /// </remarks>
    public sealed class SkinningTests
    {
        private static readonly Vector3 HipsPosition = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 SpinePosition = new Vector3(0f, 2f, 0f);
        private static readonly Vector3 PropPosition = new Vector3(0f, 3f, 0f);

        private static readonly string[] BodyBones = { "Armature/Hips", "Armature/Spine" };
        private static readonly string[] PartBones = { "Armature/Hips", "Armature/Prop" };

        /// <summary>A five-vertex skinned body: four seam-ring vertices on the hips, one apex blended with the spine.</summary>
        private static MeshSnapshot Body(Matrix4x4[] bindPoses = null)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var weights = MeshFixtures.UniformWeights(positions.Count, 0);
            weights[4] = MeshFixtures.BlendWeight(0, 0.5f, 1, 0.5f);

            return MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                BodyBones,
                weights,
                MeshFixtures.BonesAt(HipsPosition, SpinePosition),
                bindPoses);
        }

        /// <summary>
        /// A five-vertex skinned part whose seam ring coincides with the body's, with its own weights.
        /// </summary>
        /// <param name="seamBone">Part bone index the seam ring is weighted to.</param>
        /// <param name="apexBone">Part bone index the apex is weighted to.</param>
        private static MeshSnapshot Part(
            int seamBone = 0,
            int apexBone = 1,
            string[] bonePaths = null,
            Matrix4x4[] boneWorldToLocal = null,
            float apexOffset = -1f)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, apexOffset) };
            var weights = MeshFixtures.UniformWeights(positions.Count, seamBone);
            weights[4] = new BoneWeight { boneIndex0 = apexBone, weight0 = 1f };

            return MeshFixtures.SkinnedSnapshot(
                "Part",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                bonePaths ?? PartBones,
                weights,
                boneWorldToLocal ?? MeshFixtures.BonesAt(HipsPosition, PropPosition));
        }

        private static ValidationContext Context(MeshSnapshot body, MeshSnapshot part, Matrix4x4 renderer = default)
        {
            return MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                rendererLocalToWorld: renderer);
        }

        /// <summary>
        /// The final table keeps the body's own bone order first and appends new part bones in part and source
        /// order, merging a part bone whose identity the body already has.
        /// </summary>
        [Test]
        public void FinalBoneTable_PutsBaseBonesFirstAndMergesSharedIdentities()
        {
            var planning = ApaCore.Plan(Context(Body(), Part()));

            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());
            var table = planning.Plan.BoneTable;
            Assert.IsNotNull(table, "A skinned input must produce a final bone table.");

            Assert.AreEqual(3, table.Count);
            Assert.AreEqual("Armature/Hips", table.Bones[0].Path);
            Assert.AreEqual("Armature/Spine", table.Bones[1].Path);
            Assert.AreEqual("Armature/Prop", table.Bones[2].Path);

            Assert.AreEqual(string.Empty, table.Bones[0].OwnerPartId, "The body owns its own bones.");
            Assert.AreEqual("part-a", table.Bones[2].OwnerPartId, "The appended bone belongs to the part.");

            Assert.AreEqual(2, table.BaseBoneCount);
            Assert.AreEqual(1, table.AppendedBoneCount);

            Assert.AreEqual(0, table.RemapBone("part-a", 0), "The part's hips merge onto the body's hips.");
            Assert.AreEqual(2, table.RemapBone("part-a", 1), "The part's own bone is appended.");
            Assert.AreEqual(1, table.RemapBone(string.Empty, 1), "The body's bone order is preserved.");
        }

        /// <summary>
        /// Bone order follows stable part identity, not the order the caller happened to supply parts in.
        /// </summary>
        [Test]
        public void FinalBoneTable_OrderFollowsPartIdentityNotSupplyOrder()
        {
            var body = Body();
            var partA = Part(0, 1, new[] { "Armature/PropA" }, MeshFixtures.BonesAt(PropPosition));
            var partB = Part(0, 1, new[] { "Armature/PropB" }, MeshFixtures.BonesAt(PropPosition));

            // Supplied in reverse identity order on purpose.
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(4), slot: ApaPartSlot.RightArm),
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(4), slot: ApaPartSlot.LeftArm)
            });

            var planning = ApaCore.Plan(context);
            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            Assert.AreEqual(4, table.Count);
            Assert.AreEqual("Armature/Hips", table.Bones[0].Path);
            Assert.AreEqual("Armature/Spine", table.Bones[1].Path);
            Assert.AreEqual("Armature/PropA", table.Bones[2].Path, "Parts are ordered by identity, not supply order.");
            Assert.AreEqual("Armature/PropB", table.Bones[3].Path);
        }

        /// <summary>Planning twice produces an identical bone table.</summary>
        [Test]
        public void FinalBoneTable_IsDeterministic()
        {
            var context = Context(Body(), Part());

            var first = ApaCore.Plan(context);
            var second = ApaCore.Plan(context);

            Assert.IsTrue(first.Succeeded, first.Issues.FormatAll());
            Assert.IsTrue(second.Succeeded, second.Issues.FormatAll());

            Assert.AreEqual(first.Plan.BoneTable.Count, second.Plan.BoneTable.Count);
            for (var i = 0; i < first.Plan.BoneTable.Count; i++)
            {
                Assert.AreEqual(first.Plan.BoneTable.Bones[i].Path, second.Plan.BoneTable.Bones[i].Path);
                Assert.AreEqual(first.Plan.BoneTable.Bones[i].OwnerPartId, second.Plan.BoneTable.Bones[i].OwnerPartId);
                Assert.AreEqual(first.Plan.BoneTable.Bones[i].BindPose, second.Plan.BoneTable.Bones[i].BindPose);
            }
        }

        /// <summary>
        /// A welded vertex keeps the base body's skin weight, even when the part's seam vertex carried a
        /// different one (section 20).
        /// </summary>
        [Test]
        public void WeldedVertex_KeepsBaseSkinWeight()
        {
            // The part's seam ring is deliberately weighted to its own prop bone, not to the body's hips.
            var result = ApaCore.Assemble(Context(Body(), Part(seamBone: 1, apexBone: 1)));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var weights = result.Mesh.boneWeights;
                Assert.AreEqual(result.Mesh.vertexCount, weights.Length);

                // Final vertices 0..4 are the retained body vertices, in ascending original index.
                for (var i = 0; i < 4; i++)
                {
                    Assert.AreEqual(0, weights[i].boneIndex0, "Welded vertex " + i + " must use the body's bone.");
                    Assert.AreEqual(1f, weights[i].weight0, 1e-6f, "Welded vertex " + i + " keeps the body's weight.");
                }

                // Final vertex 5 is the part's apex, which keeps the part's own weight on the appended bone.
                Assert.AreEqual(2, weights[5].boneIndex0, "A non-seam part vertex keeps its own bone.");
                Assert.AreEqual(1f, weights[5].weight0, 1e-6f);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>A body vertex with two influences keeps both, remapped onto the final table.</summary>
        [Test]
        public void BaseVertex_KeepsBothInfluencesRemapped()
        {
            var result = ApaCore.Assemble(Context(Body(), Part()));
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var weight = result.Mesh.boneWeights[4];
                Assert.AreEqual(0, weight.boneIndex0);
                Assert.AreEqual(0.5f, weight.weight0, 1e-6f);
                Assert.AreEqual(1, weight.boneIndex1);
                Assert.AreEqual(0.5f, weight.weight1, 1e-6f);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// Every bind pose is <c>bone.worldToLocalMatrix * renderer.localToWorldMatrix</c>, and a source bind
        /// pose is never reused.
        /// </summary>
        [Test]
        public void BindPose_IsBoneWorldToLocalTimesRendererLocalToWorld()
        {
            var renderer = Matrix4x4.Translate(new Vector3(10f, 0f, 0f));

            // The body's own bind poses are deliberately nonsense: reusing them would be visible immediately.
            var wrongBindPoses = MeshFixtures.BonesAt(
                new Vector3(500f, 500f, 500f), new Vector3(500f, 500f, 500f));
            var body = Body(wrongBindPoses);

            var planning = ApaCore.Plan(Context(body, Part(), renderer));
            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            var binding = ApaCore.Assemble(Context(body, Part(), renderer));
            Assert.IsTrue(binding.Succeeded, binding.Issues.FormatAll());

            try
            {
                Assert.AreEqual(table.Count, binding.Mesh.bindposes.Length);

                AssertVector(new Vector3(10f, -1f, 0f), binding.Mesh.bindposes[0].GetColumn(3));
                AssertVector(new Vector3(10f, -2f, 0f), binding.Mesh.bindposes[1].GetColumn(3));
                AssertVector(new Vector3(10f, -3f, 0f), binding.Mesh.bindposes[2].GetColumn(3));
            }
            finally
            {
                Object.DestroyImmediate(binding.Mesh);
            }
        }

        /// <summary>
        /// The source's own bind poses must not be copied through when the transforms differ from the final
        /// scene relation.
        /// </summary>
        [Test]
        public void BindPose_DoesNotReuseSourceBindPoses()
        {
            var wrongBindPoses = MeshFixtures.BonesAt(new Vector3(500f, 500f, 500f), new Vector3(500f, 500f, 500f));
            var result = ApaCore.Assemble(Context(Body(wrongBindPoses), Part()));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                Assert.AreNotEqual(
                    wrongBindPoses[0], result.Mesh.bindposes[0],
                    "A source bind pose must not be reused as a final bind pose.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>Skin weights without a bone identity block with <c>APA007</c> and produce no mesh.</summary>
        [Test]
        public void SkinWeightsWithoutBoneIdentity_ReportsApa007()
        {
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                positions,
                new[] { 0, 1, 2 },
                new string[0],
                MeshFixtures.UniformWeights(3, 0),
                new Matrix4x4[0]);

            var result = ApaCore.Assemble(MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", MeshFixtures.Part(3, apexOffset: -1f))
            }));

            Assert.IsFalse(result.Succeeded, "Weights that cannot be mapped must block.");
            Assert.IsNull(result.Mesh);
            Assert.IsNull(result.Plan);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.TargetBoneNotFound), result.Issues.FormatAll());
        }

        /// <summary>
        /// A part bone that shares a path with a body bone but describes a different transform is <b>redirected
        /// onto the body's bone</b>, not reported as ambiguous (M11).
        /// </summary>
        /// <remarks>
        /// The path is relative to the armature the author selected for each side, so an identical path already
        /// means "the same joint". The part's own bind transform is discarded: the body's bind pose, world
        /// transform, and final Transform are authoritative, and the part's weights follow the body's bone. Before
        /// M11 this configuration blocked with <c>APA008 reason=ambiguous-bone-identity</c>, which refused an
        /// ordinary part that merely sat somewhere else at authoring time.
        /// </remarks>
        [Test]
        public void SameBonePathDifferentTransform_RedirectsOntoTheTargetBone()
        {
            // The part's "Armature/Hips" sits somewhere else entirely.
            var part = Part(
                0,
                1,
                PartBones,
                MeshFixtures.BonesAt(new Vector3(0f, 9f, 0f), PropPosition));

            var planning = ApaCore.Plan(Context(Body(), part));

            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());
            Assert.IsFalse(
                planning.Issues.ContainsCode(ApaErrorCode.BoneHierarchyConflict),
                "An identical armature-relative path is not an ambiguity." + planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            Assert.AreEqual(3, table.Count, "The part adds no duplicate bone.");
            Assert.AreEqual("Armature/Hips", table.Bones[0].Path);
            Assert.AreEqual(string.Empty, table.Bones[0].OwnerPartId, "The body keeps ownership of its own bone.");

            // The body's bind pose is the one that ships: the part's "Hips" was at y = 9.
            AssertVector(new Vector3(0f, -1f, 0f), table.Bones[0].BindPose.GetColumn(3));
            Assert.AreEqual(0, table.RemapBone("part-a", 0), "The part's bone maps onto the body's bone.");

            // The part's own prop bone is still appended, and only it.
            Assert.AreEqual("Armature/Prop", table.Bones[2].Path);
            Assert.AreEqual("part-a", table.Bones[2].OwnerPartId);
        }

        /// <summary>
        /// The redirect is reported once per part as an informational summary, never once per bone.
        /// </summary>
        [Test]
        public void SameBonePathDifferentTransform_ReportsOneInfoSummary()
        {
            var part = Part(
                0,
                1,
                PartBones,
                MeshFixtures.BonesAt(new Vector3(0f, 9f, 0f), PropPosition));

            var planning = ApaCore.Plan(Context(Body(), part));
            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());

            var summaries = 0;
            ValidationIssue summary = null;
            for (var i = 0; i < planning.Issues.Issues.Count; i++)
            {
                var issue = planning.Issues.Issues[i];
                if (issue.Detail == null || !issue.Detail.Contains("reason=part-bone-remapped-to-target")) continue;
                summaries++;
                summary = issue;
            }

            Assert.AreEqual(1, summaries, "One summary per part, not one line per bone." + planning.Issues.FormatAll());
            Assert.IsNotNull(summary);
            Assert.AreEqual(ApaSeverity.Info, summary.Severity, "Using the body's bone is not a defect.");
            StringAssert.Contains("redirectedBones=1", summary.Detail);
            StringAssert.Contains("differingTransforms=1", summary.Detail);
            StringAssert.Contains("part='part-a'", summary.Detail);
        }

        /// <summary>
        /// The body's bind pose survives when a second part names the same path: no part can overwrite the
        /// target's entry, whichever order the parts are supplied in.
        /// </summary>
        [Test]
        public void SeveralPartsSharingOneTargetPath_KeepTheTargetBindPose()
        {
            var partA = Part(
                0, 1, PartBones,
                MeshFixtures.BonesAt(new Vector3(0f, 9f, 0f), new Vector3(0f, 3f, 0f)));
            var partB = Part(
                0, 1, PartBones,
                MeshFixtures.BonesAt(new Vector3(0f, -9f, 0f), new Vector3(0f, -3f, 0f)));

            var context = MeshFixtures.Context(Body(), new[]
            {
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(4), slot: ApaPartSlot.RightArm),
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(4), slot: ApaPartSlot.LeftArm)
            });

            var planning = ApaCore.Plan(context);
            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            Assert.AreEqual(4, table.Count, "One entry per identity, owned by the body.");
            AssertVector(new Vector3(0f, -1f, 0f), table.Bones[0].BindPose.GetColumn(3));
            Assert.AreEqual(0, table.RemapBone("part-a", 0));
            Assert.AreEqual(0, table.RemapBone("part-b", 0));
            Assert.AreEqual(string.Empty, table.Bones[0].OwnerPartId);
        }

        /// <summary>
        /// A body that declares <c>Hips</c> and <c>Spine</c> while every one of its vertices follows <c>Hips</c>.
        /// </summary>
        /// <remarks>
        /// This is the M11 shape: the body's <i>signature</i> lists a joint no body weight reaches. The bone is
        /// declared by the target body, so a part that weights it must still follow the body's transform rather
        /// than append a bone of its own. The unused slot has a usable transform, because a declared slot whose
        /// transform is unusable is a different defect.
        /// </remarks>
        private static MeshSnapshot BodyDeclaringAnUnweightedSpine()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };

            return MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                BodyBones,
                MeshFixtures.UniformWeights(positions.Count, 0),
                MeshFixtures.BonesAt(HipsPosition, SpinePosition));
        }

        /// <summary>
        /// A part that weights a body-declared but body-unweighted path still follows the body's bone (M11).
        /// </summary>
        /// <remarks>
        /// The body's signature lists <c>Armature/Spine</c> while no body vertex weights it. Before this rule the
        /// spine was appended as a <i>part-owned</i> bone carrying the part's transform, so the same identity had
        /// one Transform in preview and another in the NDMF build. The body owns the entry now, its bind pose is
        /// the one that ships, and the part's different transform is discarded rather than reported.
        /// </remarks>
        [Test]
        public void PartWeightsABodyDeclaredButUnweightedBone_FollowsTheBodyBone()
        {
            // The part's "Spine" sits at y = 9, so a table entry carrying the part's transform is unmistakable.
            var part = Part(
                0,
                1,
                PartBones,
                MeshFixtures.BonesAt(HipsPosition, new Vector3(0f, 9f, 0f)));

            var planning = ApaCore.Plan(Context(BodyDeclaringAnUnweightedSpine(), part));

            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());
            Assert.IsFalse(
                planning.Issues.ContainsCode(ApaErrorCode.BoneHierarchyConflict),
                "A unique body path is not an ambiguity, whether or not a body weight reaches it." +
                planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            Assert.AreEqual(2, table.Count, "The body owns both entries; the part appends no duplicate.");

            Assert.AreEqual("Armature/Hips", table.Bones[0].Path);
            Assert.AreEqual("Armature/Spine", table.Bones[1].Path);

            Assert.AreEqual(string.Empty, table.Bones[1].OwnerPartId, "The body owns the declared spine.");
            Assert.AreEqual(2, table.BaseBoneCount, "Both entries are body bones, not appended part bones.");
            Assert.AreEqual(0, table.AppendedBoneCount);

            // The body's bind pose is the one that ships: the part's spine was at y = 9.
            AssertVector(new Vector3(0f, -2f, 0f), table.Bones[1].BindPose.GetColumn(3));

            Assert.AreEqual(0, table.RemapBone("part-a", 0), "The part's hips redirect onto the body's hips.");
            Assert.AreEqual(1, table.RemapBone("part-a", 1), "The part's spine redirects onto the body's spine.");
            Assert.AreEqual(1, table.RemapBone(string.Empty, 1), "The body's own order is preserved.");
            Assert.AreEqual(1, table.IndexOfPath("Armature/Spine"));
        }

        /// <summary>
        /// The redirect of a body-declared but body-unweighted path is still reported as the one per-part summary.
        /// </summary>
        [Test]
        public void PartWeightsABodyDeclaredButUnweightedBone_ReportsOneInfoSummary()
        {
            var part = Part(
                0,
                1,
                PartBones,
                MeshFixtures.BonesAt(HipsPosition, new Vector3(0f, 9f, 0f)));

            var planning = ApaCore.Plan(Context(BodyDeclaringAnUnweightedSpine(), part));
            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());

            var summaries = 0;
            ValidationIssue summary = null;
            for (var i = 0; i < planning.Issues.Issues.Count; i++)
            {
                var issue = planning.Issues.Issues[i];
                if (issue.Detail == null || !issue.Detail.Contains("reason=part-bone-remapped-to-target")) continue;
                summaries++;
                summary = issue;
            }

            Assert.AreEqual(1, summaries, "One summary per part, not one line per bone." + planning.Issues.FormatAll());
            Assert.IsNotNull(summary);
            Assert.AreEqual(ApaSeverity.Info, summary.Severity, "Using the body's bone is not a defect.");
            StringAssert.Contains("redirectedBones=2", summary.Detail);
            StringAssert.Contains("differingTransforms=1", summary.Detail);
            StringAssert.Contains("part='part-a'", summary.Detail);
        }

        /// <summary>
        /// No part can overwrite the body's entry for a declared but unweighted path, whichever order the parts
        /// are supplied in.
        /// </summary>
        [Test]
        public void SeveralPartsRequestingOneUnweightedBodyPath_KeepTheBodyBindPose()
        {
            var partA = Part(
                0, 1, PartBones,
                MeshFixtures.BonesAt(HipsPosition, new Vector3(0f, 9f, 0f)));
            var partB = Part(
                0, 1, PartBones,
                MeshFixtures.BonesAt(HipsPosition, new Vector3(0f, -9f, 0f)));

            var context = MeshFixtures.Context(BodyDeclaringAnUnweightedSpine(), new[]
            {
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(4), slot: ApaPartSlot.RightArm),
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(4), slot: ApaPartSlot.LeftArm)
            });

            var planning = ApaCore.Plan(context);
            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            Assert.AreEqual(2, table.Count, "One entry per identity, owned by the body.");
            Assert.AreEqual(string.Empty, table.Bones[1].OwnerPartId);
            AssertVector(new Vector3(0f, -2f, 0f), table.Bones[1].BindPose.GetColumn(3));
            Assert.AreEqual(1, table.RemapBone("part-a", 1));
            Assert.AreEqual(1, table.RemapBone("part-b", 1));
        }

        /// <summary>
        /// A duplicated path inside the body still blocks when a part weights it, even though no body weight
        /// reaches either copy.
        /// </summary>
        /// <remarks>
        /// Two body bones with one identity cannot be told apart, so there is no single body Transform to be
        /// authoritative and no defensible choice between them. A duplicated path <i>no</i> weight reaches stays
        /// ignored, which is the M10 rule this builds on.
        /// </remarks>
        [Test]
        public void DuplicateUnweightedBodyPath_RequestedByAPart_StillBlocks()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };

            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                new[] { "Armature/Hips", "Armature/Spine", "Armature/Spine" },
                MeshFixtures.UniformWeights(positions.Count, 0),
                MeshFixtures.BonesAt(HipsPosition, SpinePosition, new Vector3(0f, 4f, 0f)));

            var planning = ApaCore.Plan(Context(body, Part()));

            Assert.IsFalse(planning.Succeeded, "A duplicated body identity a part weights must block.");
            var issue = planning.Issues.FindByCode(ApaErrorCode.BoneHierarchyConflict);
            Assert.IsNotNull(issue, planning.Issues.FormatAll());
            StringAssert.Contains("reason=duplicate-bone-identity", issue.Detail);
            StringAssert.Contains("path=Armature/Spine", issue.Detail);
        }

        /// <summary>
        /// A duplicated body path that no weight reaches is still ignored: the new authority rule does not turn
        /// the M10 "unreferenced slots are not defects" guarantee back into a blocking check.
        /// </summary>
        [Test]
        public void DuplicateUnweightedBodyPath_NoWeightReachesIt_IsStillIgnored()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };

            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                new[] { "Armature/Hips", "Armature/Spine", "Armature/Spine" },
                MeshFixtures.UniformWeights(positions.Count, 0),
                MeshFixtures.BonesAt(HipsPosition, SpinePosition, new Vector3(0f, 4f, 0f)));

            // The part weights only "Armature/Prop", which the body does not declare, so the body's duplicated
            // spine is never requested and stays out of the table.
            var part = Part(
                0,
                0,
                new[] { "Armature/Hips", "Armature/Prop" },
                MeshFixtures.BonesAt(HipsPosition, PropPosition));

            var planning = ApaCore.Plan(Context(body, part));

            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());
            Assert.IsFalse(
                planning.Issues.ContainsCode(ApaErrorCode.BoneHierarchyConflict),
                "An unreferenced duplicated slot is still not a defect." + planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            Assert.AreEqual(2, table.Count, "The body's hips and the part's prop bone, nothing else.");
            Assert.AreEqual("Armature/Hips", table.Bones[0].Path);
            Assert.AreEqual("Armature/Prop", table.Bones[1].Path);
            Assert.AreEqual("part-a", table.Bones[1].OwnerPartId);
        }

        /// <summary>
        /// A weighted bone with no identity still blocks: the redirect loosens only the "different transform"
        /// half of the old rule.
        /// </summary>
        [Test]
        public void WeightedBoneWithoutIdentity_StillBlocksAfterTheRedirectChange()
        {
            var part = Part(0, 1, new[] { "Armature/Hips", string.Empty }, MeshFixtures.BonesAt(
                HipsPosition, PropPosition));

            var planning = ApaCore.Plan(Context(Body(), part));

            Assert.IsFalse(planning.Succeeded, "A weighted bone with no identity must still block.");
            var issue = planning.Issues.FindByCode(ApaErrorCode.BoneHierarchyConflict);
            Assert.IsNotNull(issue, planning.Issues.FormatAll());
            StringAssert.Contains("reason=bone-without-identity", issue.Detail);
        }

        /// <summary>Two weighted bones of one source sharing a path still block.</summary>
        [Test]
        public void DuplicateIdentityWithinOneSource_StillBlocks()
        {
            var part = Part(0, 1, new[] { "Armature/Hips", "Armature/Hips" }, MeshFixtures.BonesAt(
                HipsPosition, PropPosition));

            var planning = ApaCore.Plan(Context(Body(), part));

            Assert.IsFalse(planning.Succeeded, "Two bones with one identity cannot be told apart.");
            var issue = planning.Issues.FindByCode(ApaErrorCode.BoneHierarchyConflict);
            Assert.IsNotNull(issue, planning.Issues.FormatAll());
            StringAssert.Contains("reason=duplicate-bone-identity", issue.Detail);
        }

        /// <summary>
        /// A part bone whose transform is unusable still blocks even though the body's transform is the one that
        /// would ship: a broken rig is a broken asset, not something the authority rule hides.
        /// </summary>
        [Test]
        public void RedirectedBoneWithUnusableTransform_StillBlocks()
        {
            var part = Part(0, 1, PartBones, new[] { Matrix4x4.zero, MeshFixtures.BoneAt(PropPosition) });

            var planning = ApaCore.Plan(Context(Body(), part));

            Assert.IsFalse(planning.Succeeded, "An unusable bone transform must still block.");
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.InvalidBindPose), planning.Issues.FormatAll());
        }

        /// <summary>A weight that names a bone outside its source's bone list is <c>APA007</c>.</summary>
        [Test]
        public void BoneIndexOutOfRange_ReportsApa007()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var weights = MeshFixtures.UniformWeights(positions.Count, 7);
            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                BodyBones,
                weights,
                MeshFixtures.BonesAt(HipsPosition, SpinePosition));

            var planning = ApaCore.Plan(Context(body, Part()));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.TargetBoneNotFound), planning.Issues.FormatAll());
        }

        /// <summary>A vertex whose weights sum to zero would not follow any bone: <c>APA031</c>.</summary>
        [Test]
        public void ZeroWeightSum_ReportsApa031()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var weights = MeshFixtures.UniformWeights(positions.Count, 0);
            weights[2] = default;

            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                BodyBones,
                weights,
                MeshFixtures.BonesAt(HipsPosition, SpinePosition));

            var planning = ApaCore.Plan(Context(body, Part()));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.InvalidBoneWeight), planning.Issues.FormatAll());
        }

        /// <summary>A non-finite weight is <c>APA016</c>, not a confusing "no bone" message.</summary>
        [Test]
        public void NonFiniteWeight_ReportsApa016()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var weights = MeshFixtures.UniformWeights(positions.Count, 0);
            weights[1] = new BoneWeight { boneIndex0 = 0, weight0 = float.NaN };

            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                BodyBones,
                weights,
                MeshFixtures.BonesAt(HipsPosition, SpinePosition));

            var planning = ApaCore.Plan(Context(body, Part()));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.NonFiniteValue), planning.Issues.FormatAll());
        }

        /// <summary>A bone weight count that does not match the vertex count is <c>APA031</c>.</summary>
        [Test]
        public void WeightCountMismatch_ReportsApa031()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                BodyBones,
                MeshFixtures.UniformWeights(2, 0),
                MeshFixtures.BonesAt(HipsPosition, SpinePosition));

            var planning = ApaCore.Plan(Context(body, Part()));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.InvalidBoneWeight), planning.Issues.FormatAll());
        }

        /// <summary>
        /// A source with no weights at all cannot take part in a skinned assembly: every vertex of a skinned
        /// mesh needs a weight.
        /// </summary>
        [Test]
        public void SourceWithoutWeights_ReportsApa031()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var part = MeshFixtures.SkinnedSnapshot(
                "Part",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                PartBones,
                new BoneWeight[0],
                MeshFixtures.BonesAt(HipsPosition, PropPosition));

            var planning = ApaCore.Plan(Context(Body(), part));

            Assert.IsFalse(planning.Succeeded);
            Assert.IsTrue(planning.Issues.ContainsCode(ApaErrorCode.InvalidBoneWeight), planning.Issues.FormatAll());
        }

        /// <summary>
        /// A mesh with no bone data at all is still assembled as before: M2 must not make unskinned avatars
        /// require a bone table.
        /// </summary>
        [Test]
        public void UnskinnedInput_StillAssemblesWithoutABoneTable()
        {
            var body = MeshFixtures.Body(4);
            var part = MeshFixtures.Part(4, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4))
            });

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                Assert.IsNull(result.Plan.BoneTable, "No skinning input means no bone table.");
                Assert.AreEqual(0, result.Mesh.bindposes.Length);
                Assert.AreEqual(0, result.Mesh.boneWeights.Length);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// The refusing provider still blocks a skinned plan, so the "never lose data silently" guarantee does
        /// not depend on which provider happens to be installed.
        /// </summary>
        [Test]
        public void BlockingProvider_RefusesSkinnedPlan()
        {
            var context = Context(Body(), Part());
            var result = ApaCore.Assemble(context, new MeshAssembler(new BlockingSkinningProvider(), null));

            Assert.IsFalse(result.Succeeded, "The refusing provider must block a skinned plan.");
            Assert.IsNull(result.Mesh, "A blocked build must not produce a mesh.");
            Assert.IsTrue(
                result.Issues.ContainsCode(ApaErrorCode.UnsupportedMeshAttribute), result.Issues.FormatAll());
        }

        /// <summary>
        /// The default provider supplies skinning, which is what makes the M2 boundary real rather than
        /// documented.
        /// </summary>
        [Test]
        public void DefaultProviders_SupplySkinning()
        {
            var plan = ApaCore.Plan(Context(Body(), Part()));
            Assert.IsTrue(plan.Succeeded, plan.Issues.FormatAll());

            var provider = new FinalBoneTableSkinningProvider();
            Assert.IsTrue(provider.CanProvide(plan.Plan, Context(Body(), Part())));
            Assert.AreEqual(plan.Plan.VertexCount, provider.BuildBoneWeights(plan.Plan, Context(Body(), Part())).Length);
        }

        private static void AssertVector(Vector3 expected, Vector4 actual, float tolerance = 1e-5f)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance);
            Assert.AreEqual(expected.y, actual.y, tolerance);
            Assert.AreEqual(expected.z, actual.z, tolerance);
            Assert.AreEqual(1f, actual.w, tolerance);
        }
    }
}

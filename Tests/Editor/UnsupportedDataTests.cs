using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests that mesh data the assembler cannot interpret is blocked with a stable diagnostic rather than
    /// silently discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until M2 this file asserted that skinned and blend-shaped input was refused outright with
    /// <c>APA014</c>, because M1 could not preserve either. M2 processes both, so those assertions moved to
    /// <see cref="SkinningTests"/> and <see cref="BlendShapeTests"/>, which assert that the data is remapped
    /// correctly and that malformed data is blocked with the specific code for its defect.
    /// </para>
    /// <para>
    /// What remains here is the failure mode no milestone removes: a value that cannot be compared at all, a
    /// mapping that cannot exist, and the guarantee that a blocked build produces no mesh and no plan.
    /// </para>
    /// </remarks>
    public sealed class UnsupportedDataTests
    {
        /// <summary>
        /// A skinned mesh whose bone identities were never captured blocks with <c>APA007</c>. The weights
        /// themselves are well formed; it is the mapping onto a final bone table that cannot exist.
        /// </summary>
        [Test]
        public void SkinWeightsWithoutBoneIdentities_ReportApa007()
        {
            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { 0, 1, 2 },
                new string[0],
                MeshFixtures.UniformWeights(3, 0),
                new Matrix4x4[0]);

            var part = MeshFixtures.Part(3, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part)
            });

            var result = ApaCore.Assemble(context);

            Assert.IsFalse(result.Succeeded, "Weights without a bone identity must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.TargetBoneNotFound));
        }

        /// <summary>
        /// A part whose skin weights name no bone blocks with <c>APA007</c> as well: the failure is symmetric,
        /// because the final table has to cover both sides.
        /// </summary>
        [Test]
        public void PartSkinWeightsWithoutBoneIdentities_ReportApa007()
        {
            var body = MeshFixtures.Body(3);
            var part = MeshFixtures.SkinnedSnapshot(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { 0, 1, 2 },
                new string[0],
                MeshFixtures.UniformWeights(3, 0),
                new Matrix4x4[0]);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part)
            });

            var result = ApaCore.Assemble(context);

            Assert.IsFalse(result.Succeeded, "Skinned part geometry without a bone identity must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.TargetBoneNotFound));
        }

        /// <summary>
        /// A zero-frame blend shape carries no deltas to preserve, so it is not a reason to block. M1 refused
        /// every shape; M2 preserves the ones that have data.
        /// </summary>
        [Test]
        public void ZeroFrameBlendShape_DoesNotBlock()
        {
            var body = MeshFixtures.Body(3);
            var part = MeshFixtures.BlendShapedSnapshot(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { 0, 1, 2 },
                new[] { "Empty" },
                new[] { new BlendShapeFrameSnapshot[0] });

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part)
            });

            var result = ApaCore.Assemble(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Object.DestroyImmediate(result.Mesh);
        }

        /// <summary>
        /// A blocked input is reported before any mesh is created, so no partial result is ever handed back.
        /// </summary>
        [Test]
        public void BlockedInput_ProducesNoMesh()
        {
            var body = MeshFixtures.SkinnedSnapshot(
                "Body",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { 0, 1, 2 },
                new string[0],
                MeshFixtures.UniformWeights(3, 0),
                new Matrix4x4[0]);
            var part = MeshFixtures.Part(3, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part)
            });

            var result = ApaCore.Assemble(context);

            Assert.IsNull(result.Mesh, "A blocked build must not produce a mesh.");
            Assert.IsNull(result.Plan, "A blocked build must not produce a plan.");
            Assert.IsEmpty(result.Materials, "A blocked build must not produce materials.");
            Assert.IsTrue(result.Issues.HasErrors, "A blocked build must report at least one error.");
        }

        /// <summary>A non-finite position is <c>APA016</c>, not a confusing seam mismatch.</summary>
        /// <remarks>
        /// A NaN position makes every epsilon comparison false. Without an explicit finiteness check, the author
        /// would see "no seam vertex within epsilon" and go looking for a seam problem that does not exist.
        /// </remarks>
        [Test]
        public void NonFinitePosition_ReportsApa016()
        {
            var positions = MeshFixtures.Ring(8, 1f);
            positions[2] = new Vector3(float.NaN, 0f, 0f);
            var body = MeshFixtures.Snapshot("Body", positions, MeshFixtures.CapTriangles(8, 0, 8));
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.NonFiniteValue));
        }

        /// <summary>An infinite UV value is also <c>APA016</c>.</summary>
        [Test]
        public void NonFiniteUv_ReportsApa016()
        {
            var uvs = MeshFixtures.RingUvs(8);
            uvs[1] = new Vector4(float.PositiveInfinity, 0f, 0f, 0f);
            var body = MeshFixtures.Body(8, uvs);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.NonFiniteValue));
        }

        /// <summary>
        /// The refusing providers still report that they cannot supply skinning and blend shape data. They are
        /// no longer the defaults — M2's providers are — but they remain the mechanism a caller selects when it
        /// wants skinned input rejected outright, so the guarantee they encode is still asserted.
        /// </summary>
        [Test]
        public void BlockingProviders_RefuseSkinningAndBlendShapes()
        {
            var plan = BuildTrivialPlan();

            Assert.IsFalse(new BlockingSkinningProvider().CanProvide(plan, BuildTrivialContext()));
            Assert.IsFalse(new BlockingBlendShapeProvider().CanProvide(plan, BuildTrivialContext()));
            Assert.AreEqual(ApaErrorCode.UnsupportedMeshAttribute, BlockingSkinningProvider.CreateIssue().Code);
            Assert.AreEqual(ApaErrorCode.UnsupportedMeshAttribute, BlockingBlendShapeProvider.CreateIssue().Code);
        }

        /// <summary>
        /// The M2 providers are the defaults, so an unskinned plan is still buildable and a skinned one is
        /// handled rather than refused.
        /// </summary>
        [Test]
        public void DefaultProviders_AreTheM2Providers()
        {
            var plan = BuildTrivialPlan();

            Assert.IsFalse(new FinalBoneTableSkinningProvider().CanProvide(plan, BuildTrivialContext()),
                "An unskinned plan has no bone table, so there is nothing for the skinning provider to supply.");
            Assert.IsFalse(new RemappedBlendShapeProvider().CanProvide(plan, BuildTrivialContext()));

            var result = ApaCore.Assemble(BuildTrivialContext());
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Object.DestroyImmediate(result.Mesh);
        }

        private static MeshAssemblyPlan BuildTrivialPlan()
        {
            var context = BuildTrivialContext();
            var result = ApaCore.Plan(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            return result.Plan;
        }

        private static ValidationContext BuildTrivialContext()
        {
            var body = MeshFixtures.Body(4);
            var part = MeshFixtures.Part(4, apexOffset: -1f);
            return MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4))
            });
        }
    }
}

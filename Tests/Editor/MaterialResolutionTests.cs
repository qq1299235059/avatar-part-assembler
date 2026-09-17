using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for material semantic resolution and the four conflict policies.
    /// </summary>
    /// <remarks>
    /// The material tests need real <see cref="Material"/> assets to exercise reference equality. They are
    /// created in memory and destroyed in teardown so that the tests leave no project state behind.
    /// </remarks>
    public sealed class MaterialResolutionTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        private Material NewMaterial(string name)
        {
            var material = new Material(Shader.Find("Standard")) { name = name };
            _created.Add(material);
            return material;
        }

        /// <summary>Same semantic and the same asset merge into one slot.</summary>
        [Test]
        public void SameSemanticSameAsset_Merges()
        {
            var shared = NewMaterial("M_Skin");

            var body = MeshFixtures.SnapshotWithSubMeshes(
                "Body",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            var part = MeshFixtures.SnapshotWithSubMeshes(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        materialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, shared, ApaMaterialPolicyMode.Auto) })
                },
                baseMaterials: new[] { shared },
                baseMaterialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, shared, ApaMaterialPolicyMode.Auto) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(1, result.Plan.MaterialLayout.SlotCount,
                "Identical semantic and asset must merge into a single slot.");
        }

        /// <summary>Same semantic but different assets under <c>Auto</c> is <c>APA009</c>.</summary>
        [Test]
        public void SameSemanticDifferentAsset_AutoReportsApa009()
        {
            var bodyMaterial = NewMaterial("BodySkin");
            var partMaterial = NewMaterial("PartSkin");

            var body = MeshFixtures.SnapshotWithSubMeshes(
                "Body",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            var part = MeshFixtures.SnapshotWithSubMeshes(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        materialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, partMaterial, ApaMaterialPolicyMode.Auto) })
                },
                baseMaterials: new[] { bodyMaterial },
                baseMaterialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, bodyMaterial, ApaMaterialPolicyMode.Auto) });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "A same-semantic material conflict under Auto must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.MaterialSemanticConflict));
        }

        /// <summary><c>UseTarget</c> redirects the part geometry into the base slot.</summary>
        [Test]
        public void SameSemanticDifferentAsset_UseTargetMergesIntoBaseSlot()
        {
            var bodyMaterial = NewMaterial("BodySkin");
            var partMaterial = NewMaterial("PartSkin");

            var body = MeshFixtures.SnapshotWithSubMeshes(
                "Body",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            var part = MeshFixtures.SnapshotWithSubMeshes(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Skin", 0, partMaterial, ApaMaterialPolicyMode.UseTarget)
                        })
                },
                baseMaterials: new[] { bodyMaterial },
                baseMaterialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, bodyMaterial, ApaMaterialPolicyMode.Auto) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(1, result.Plan.MaterialLayout.SlotCount,
                "UseTarget must resolve into the base slot rather than creating a new one.");
            Assert.AreSame(bodyMaterial, result.Plan.MaterialLayout.Slots[0].Material,
                "UseTarget must keep the base material.");
        }

        /// <summary><c>KeepPart</c> keeps the part material in an additional slot.</summary>
        [Test]
        public void SameSemanticDifferentAsset_KeepPartCreatesSlot()
        {
            var bodyMaterial = NewMaterial("BodySkin");
            var partMaterial = NewMaterial("PartSkin");

            var context = BuildSingleSlotContext(partMaterial, ApaMaterialPolicyMode.KeepPart, bodyMaterial);
            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.MaterialLayout.SlotCount,
                "KeepPart must create an additional slot.");
            Assert.AreSame(partMaterial, result.Plan.MaterialLayout.Slots[1].Material);
        }

        /// <summary><c>ForceNew</c> always creates a new slot, even when the assets are identical.</summary>
        [Test]
        public void SameSemanticSameAsset_ForceNewStillCreatesSlot()
        {
            var shared = NewMaterial("M_Skin");

            var context = BuildSingleSlotContext(shared, ApaMaterialPolicyMode.ForceNew, shared);
            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.MaterialLayout.SlotCount,
                "ForceNew must create a slot even when the semantic and asset already match.");
        }

        /// <summary>The base material is never overwritten by a part policy.</summary>
        [Test]
        public void BaseMaterial_IsNeverReplaced()
        {
            var bodyMaterial = NewMaterial("BodySkin");
            var partMaterial = NewMaterial("PartSkin");

            var context = BuildSingleSlotContext(partMaterial, ApaMaterialPolicyMode.KeepPart, bodyMaterial);
            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreSame(bodyMaterial, result.Plan.MaterialLayout.Slots[0].Material,
                "The base slot must retain the base material.");
        }

        private static ValidationContext BuildSingleSlotContext(
            Material partMaterial,
            ApaMaterialPolicyMode policy,
            Material bodyMaterial)
        {
            var body = MeshFixtures.SnapshotWithSubMeshes(
                "Body",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            var part = MeshFixtures.SnapshotWithSubMeshes(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });

            return MeshFixtures.Context(
                body,
                new[]
                {
                    MeshFixtures.PartSnapshot(
                        "part-a",
                        part,
                        materialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, partMaterial, policy) })
                },
                baseMaterials: new[] { bodyMaterial },
                baseMaterialSemantics: new[] { new ApaMaterialSlotSemantic("Skin", 0, bodyMaterial, ApaMaterialPolicyMode.Auto) });
        }
    }
}

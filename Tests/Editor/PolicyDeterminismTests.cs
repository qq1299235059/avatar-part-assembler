using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;
using AuthoringCodes = AvatarPartAssembler.Editor.Authoring.ApaAuthoringErrorCode;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests that the M6 policy work preserves the determinism guarantee and the schema-2 behaviour.
    /// </summary>
    /// <remarks>
    /// The invariant under test is: identical inputs produce an identical plan, ordering, issue list, and mesh;
    /// and a profile that declares no policy behaves exactly as it did before M6.
    /// </remarks>
    public sealed class PolicyDeterminismTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        /// <summary>Planning the same context twice produces identical vertices, indices, and issues.</summary>
        [Test]
        public void SameInputTwice_ProducesAnIdenticalPlanAndReport()
        {
            var context = PolicyContext();

            var first = ApaCore.Plan(context);
            var second = ApaCore.Plan(context);

            Assert.IsTrue(first.Succeeded, first.Issues.FormatAll());
            Assert.IsTrue(second.Succeeded, second.Issues.FormatAll());
            Assert.AreEqual(first.Plan.VertexCount, second.Plan.VertexCount);
            Assert.AreEqual(first.Plan.SubMeshes.Count, second.Plan.SubMeshes.Count);

            for (var subMesh = 0; subMesh < first.Plan.SubMeshes.Count; subMesh++)
            {
                CollectionAssert.AreEqual(
                    first.Plan.SubMeshes[subMesh].Indices,
                    second.Plan.SubMeshes[subMesh].Indices,
                    "Submesh " + subMesh + " must be byte-identical across two plans.");
                Assert.AreEqual(first.Plan.SubMeshes[subMesh].Semantic, second.Plan.SubMeshes[subMesh].Semantic);
            }

            Assert.AreEqual(first.Issues.FormatAll(), second.Issues.FormatAll());
            Assert.AreEqual(
                first.Plan.RemovedTriangles.Count,
                second.Plan.RemovedTriangles.Count,
                "The resolved removal set must be identical.");
        }

        /// <summary>Assembling the same context twice produces identical mesh data.</summary>
        [Test]
        public void SameInputTwice_ProducesIdenticalMeshData()
        {
            var context = PolicyContext();

            var first = ApaCore.Assemble(context);
            var second = ApaCore.Assemble(context);

            Assert.IsTrue(first.Succeeded, first.Issues.FormatAll());
            Assert.IsTrue(second.Succeeded, second.Issues.FormatAll());

            try
            {
                Assert.AreEqual(first.Mesh.vertexCount, second.Mesh.vertexCount);
                Assert.AreEqual(first.Mesh.subMeshCount, second.Mesh.subMeshCount);
                CollectionAssert.AreEqual(first.Mesh.vertices, second.Mesh.vertices);
                CollectionAssert.AreEqual(first.Mesh.triangles, second.Mesh.triangles);
                Assert.AreEqual(first.Mesh.name, second.Mesh.name, "The generated name must be deterministic.");
            }
            finally
            {
                Object.DestroyImmediate(first.Mesh);
                Object.DestroyImmediate(second.Mesh);
            }
        }

        /// <summary>Reversing the declared priorities mirrors the emitted vertex order.</summary>
        [Test]
        public void PriorityReversal_MirrorsThePlanOrder()
        {
            var body = MeshFixtures.Body(6);
            var forward = ApaCore.Plan(MeshFixtures.Context(body, new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 2),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1)
            }));

            var reversed = ApaCore.Plan(MeshFixtures.Context(body, new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 1),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 2)
            }));

            Assert.IsTrue(forward.Succeeded, forward.Issues.FormatAll());
            Assert.IsTrue(reversed.Succeeded, reversed.Issues.FormatAll());
            Assert.Less(
                forward.Plan.FinalIndexOf("part-a", 6),
                forward.Plan.FinalIndexOf("part-b", 6));
            Assert.Less(
                reversed.Plan.FinalIndexOf("part-b", 6),
                reversed.Plan.FinalIndexOf("part-a", 6));
        }

        /// <summary>With no declared priority, the order is the schema-2 (slot, part id) order.</summary>
        [Test]
        public void NoDeclaredPriority_ProducesTheLegacyOrder()
        {
            var parts = new[]
            {
                MultiPartFixtures.Part("part-c", MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 0),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(4, apexOffset: -2f), MeshFixtures.Seam(4),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 0),
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(4, apexOffset: -3f), MeshFixtures.Seam(4),
                    ApaPartSlot.Head, ApaPartSlotMode.Replace, 0)
            };

            var sorted = ValidationContext.SortParts(parts);

            Assert.AreEqual("part-a", sorted[0].PartId, "Head is the lowest slot value.");
            Assert.AreEqual("part-b", sorted[1].PartId, "Then LeftArm by part id.");
            Assert.AreEqual("part-c", sorted[2].PartId);

            for (var i = 0; i < sorted.Count; i++)
            {
                Assert.AreEqual(0, sorted[i].OrderingKey.EffectivePriority, "Nothing may re-order by default.");
            }
        }

        /// <summary>A single-target plan has no group key, so its mesh name is unchanged from M2.</summary>
        [Test]
        public void SingleTargetPlan_HasNoGroupKey()
        {
            var result = ApaCore.Assemble(PolicyContext());
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                Assert.IsFalse(result.Plan.HasGroupKey);
                Assert.AreEqual(string.Empty, result.Plan.GroupKey);
                Assert.AreEqual(string.Empty, result.GroupKey);
                StringAssert.EndsWith("_Assembled", result.Mesh.name);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        // ---- schema and policy defaults ----------------------------------------------------------------

        /// <summary>A schema-2 profile is accepted and is not rewritten by the migration check.</summary>
        /// <remarks>
        /// The pipeline reads the profile asset, and the asset is shared with the authoring scene. Rewriting the
        /// version number on read would be a hidden, non-undoable mutation of the author's asset, so the
        /// migration is a no-op accept and the version stays exactly what it was.
        /// </remarks>
        [Test]
        public void SchemaTwoProfile_IsAcceptedWithoutBeingRewritten()
        {
            var profile = NewProfile();
            try
            {
                profile.SchemaVersion = 2;

                var message = string.Empty;
                Assert.IsTrue(profile.TryMigrate(out message), message);
                Assert.AreEqual(string.Empty, message);
                Assert.AreEqual(2, profile.SchemaVersion, "Migration must not write to the asset.");
                Assert.IsTrue(profile.IsSchemaSupported);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>A schema-2 profile means the strict policy: nothing is resolved, nothing is re-ordered, and no
        /// armature is selected.</summary>
        [Test]
        public void SchemaTwoProfile_DefaultsToTheStrictPolicy()
        {
            var profile = NewProfile();
            try
            {
                profile.SchemaVersion = 2;
                var policy = PartPolicySnapshot.FromProfile(profile);

                Assert.AreEqual(ApaPartSlotMode.Replace, policy.SlotMode);
                Assert.AreEqual(0, policy.ConflictPriority);
                Assert.IsFalse(policy.HasConflictPriority);
                Assert.IsTrue(policy.AllowPartOnlyShapes, "The strict default keeps accepting zero-delta part shapes.");
                Assert.AreEqual(string.Empty, policy.TargetArmaturePath);
                Assert.AreEqual(string.Empty, policy.PartArmaturePath);
                Assert.IsFalse(
                    policy.HasArmatureSelection,
                    "A schema-2 profile must not silently start merging bones it never selected armatures for.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>The serialized policy axes are read back, so none of them is an inert field.</summary>
        [Test]
        public void PolicyAxes_AreReadFromTheProfile()
        {
            var profile = NewProfile();
            try
            {
                profile.Identity.SlotMode = ApaPartSlotMode.Augment;
                profile.Identity.ConflictPriority = 4;
                profile.BlendShapes.AllowPartOnlyShapes = false;
                profile.Bones.TargetArmaturePath = "Armature";
                profile.Bones.PartArmaturePath = "PartArmature";

                var policy = PartPolicySnapshot.FromProfile(profile);

                Assert.AreEqual(ApaPartSlotMode.Augment, policy.SlotMode);
                Assert.AreEqual(4, policy.ConflictPriority);
                Assert.IsFalse(policy.AllowPartOnlyShapes);
                Assert.AreEqual("Armature", policy.TargetArmaturePath);
                Assert.AreEqual("PartArmature", policy.PartArmaturePath);
                Assert.IsTrue(policy.HasArmatureSelection);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The legacy merge-name policy never stands in for an armature selection: a profile that declares a
        /// prefix, a suffix, and inference still reports no armature selection, which is the state the build
        /// refuses.
        /// </summary>
        /// <remarks>
        /// This is the M10 boundary stated as a test. The legacy fields are still serialized — so an old asset
        /// loads and round-trips — but the build derives nothing from them, and a selection is the one thing that
        /// makes a bone identity possible.
        /// </remarks>
        [Test]
        public void LegacyMergeNamePolicy_ImpartsNoArmatureSelection()
        {
            var profile = NewProfile();
            try
            {
                profile.Bones.MergeTargetPath = "Armature";
                profile.Bones.MergePrefix = "P_";
                profile.Bones.MergeSuffix = "_S";
                profile.Bones.InferMergeNames = true;

                var policy = PartPolicySnapshot.FromProfile(profile);

                Assert.AreEqual(string.Empty, policy.TargetArmaturePath);
                Assert.AreEqual(string.Empty, policy.PartArmaturePath);
                Assert.IsFalse(
                    policy.HasArmatureSelection,
                    "The removed name policy must not be read as a selection.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>The current schema version is four, and two and three are still readable as no-ops.</summary>
        [Test]
        public void SchemaVersion_IsFour_AndOlderVersionsAreStillReadable()
        {
            Assert.AreEqual(4, ApaPartProfile.CurrentSchemaVersion);
            Assert.AreEqual(2, ApaPartProfile.MinimumMigratableSchemaVersion);

            var profile = NewProfile();
            try
            {
                var versions = new[] { 2, 3, ApaPartProfile.CurrentSchemaVersion };
                for (var i = 0; i < versions.Length; i++)
                {
                    profile.SchemaVersion = versions[i];

                    var message = string.Empty;
                    Assert.IsTrue(profile.TryMigrate(out message), "Schema " + versions[i] + ": " + message);
                    Assert.AreEqual(string.Empty, message);
                    Assert.AreEqual(
                        versions[i],
                        profile.SchemaVersion,
                        "Migration must not rewrite the asset's version.");
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>The M6 codes are allocated above the authoring layer's codes and are all titled.</summary>
        [Test]
        public void MilestoneSixCodes_AreAllocatedAndTitled()
        {
            Assert.AreEqual(6, ApaReservedCodes.Milestone6.Length);
            for (var i = 0; i < ApaReservedCodes.Milestone6.Length; i++)
            {
                var code = ApaReservedCodes.Milestone6[i];
                Assert.IsTrue(ApaReservedCodes.IsMilestone6Code(code));
                Assert.IsNotEmpty(ApaErrorCode.GetTitle(code), "Every allocated code needs a title: " + code);
                Assert.Greater(
                    string.CompareOrdinal(code, "APA034"),
                    0,
                    "M6 codes must not collide with the authoring layer's APA033/APA034.");
            }

            // Every allocated code is distinct, and the review fix's APA040 sits inside the M6 range.
            for (var i = 0; i < ApaReservedCodes.Milestone6.Length; i++)
            {
                for (var j = i + 1; j < ApaReservedCodes.Milestone6.Length; j++)
                {
                    Assert.AreNotEqual(
                        ApaReservedCodes.Milestone6[i],
                        ApaReservedCodes.Milestone6[j],
                        "Two M6 codes must never share a number.");
                }
            }

            Assert.AreEqual("APA040", ApaErrorCode.PartOnlyShapeDisallowed);
            Assert.AreEqual("PART_ONLY_SHAPE_DISALLOWED", ApaErrorCode.GetTitle(ApaErrorCode.PartOnlyShapeDisallowed));
            Assert.AreNotEqual(
                ApaErrorCode.BlendShapeSeamDeltaMismatch,
                ApaErrorCode.PartOnlyShapeDisallowed,
                "The part-only-shape refusal must not reuse APA029.");
        }

        /// <summary>
        /// M9's authoring-layer code is allocated once, titled, and outside every earlier range.
        /// </summary>
        /// <remarks>
        /// The mask conversion's refusal is the only code M9 allocates, and it must not collide with the M6 range
        /// it sits above or with the authoring layer's M5 codes below it. The per-code properties are covered by
        /// <see cref="AllocatedCodes_HaveOneMeaningAndOneTitleSource"/> now that the M9 record is part of the
        /// allocation list; this test pins the number and the ordering claim.
        /// </remarks>
        [Test]
        public void MilestoneNineCode_IsAllocatedAndTitled()
        {
            Assert.AreEqual(1, ApaReservedCodes.Milestone9Authoring.Length);
            Assert.AreEqual(ApaErrorCode.RemovalMaskTextureFailed, ApaReservedCodes.Milestone9Authoring[0]);
            Assert.IsTrue(ApaReservedCodes.IsMilestone9AuthoringCode(ApaErrorCode.RemovalMaskTextureFailed));
            Assert.IsFalse(ApaReservedCodes.IsMilestone9AuthoringCode(ApaErrorCode.PartOnlyShapeDisallowed));

            Assert.AreEqual("APA041", ApaErrorCode.RemovalMaskTextureFailed);
            Assert.AreEqual("REMOVAL_MASK_TEXTURE_FAILED", ApaErrorCode.GetTitle(ApaErrorCode.RemovalMaskTextureFailed));
            Assert.Greater(
                string.CompareOrdinal(ApaErrorCode.RemovalMaskTextureFailed, "APA040"),
                0,
                "M9's code must sit above the M6 range it was allocated after.");

            // The mask conversion is an authoring-layer refusal, so it is not one of the core's assembly codes.
            Assert.IsTrue(AuthoringCodes.IsAuthoringCode(ApaErrorCode.RemovalMaskTextureFailed));
            Assert.IsFalse(ApaReservedCodes.IsMilestone6Code(ApaErrorCode.RemovalMaskTextureFailed));
        }

        /// <summary>Every allocated code has exactly one meaning and exactly one title source.</summary>
        /// <remarks>
        /// The package's code contract is "a code's meaning never changes and a number is never reused", which
        /// only holds while the allocation record, the title table, and the authoring aliases agree. This test
        /// pins the properties that can silently drift: a code allocated twice, a code without a title, two codes
        /// rendered under one title, and an authoring alias that stops being the core constant or stops delegating
        /// its title lookup to the core table.
        /// </remarks>
        [Test]
        public void AllocatedCodes_HaveOneMeaningAndOneTitleSource()
        {
            var allocation = new List<string>();
            allocation.AddRange(ApaReservedCodes.Milestone2);
            allocation.AddRange(ApaReservedCodes.Milestone5Authoring);
            allocation.AddRange(ApaReservedCodes.Milestone6);
            allocation.AddRange(ApaReservedCodes.Milestone9Authoring);
            allocation.AddRange(ApaReservedCodes.Milestone10);

            Assert.IsNotEmpty(allocation, "The allocation record must not be empty.");

            var titles = new List<string>();
            for (var i = 0; i < allocation.Count; i++)
            {
                var code = allocation[i];

                // No code is allocated twice: a second allocation is how one number acquires two meanings.
                for (var j = i + 1; j < allocation.Count; j++)
                {
                    Assert.AreNotEqual(code, allocation[j], "A code must be allocated exactly once: " + code);
                }

                var title = ApaErrorCode.GetTitle(code);
                Assert.IsNotEmpty(title, "An allocated code must have a title: " + code);
                CollectionAssert.DoesNotContain(titles, title, "Two codes must not render under one title.");
                titles.Add(title);

                // The authoring layer is an alias and a delegate, never a second table.
                Assert.AreEqual(
                    title,
                    AuthoringCodes.GetTitle(code),
                    "The authoring title lookup must be the core title lookup for " + code + ".");
            }
        }

        /// <summary>
        /// M10's three codes are allocated once, titled, and above every earlier range, so the refusal the
        /// milestone introduced cannot silently reuse a number an older build already gives another meaning.
        /// </summary>
        [Test]
        public void MilestoneTenCodes_AreAllocatedAndTitled()
        {
            Assert.AreEqual(3, ApaReservedCodes.Milestone10.Length);
            Assert.AreEqual(ApaErrorCode.SeamPairingRequired, ApaReservedCodes.Milestone10[0]);
            Assert.AreEqual(ApaErrorCode.ArmatureSelectionInvalid, ApaReservedCodes.Milestone10[1]);
            Assert.AreEqual(ApaErrorCode.BoneOutsideSelectedArmature, ApaReservedCodes.Milestone10[2]);

            for (var i = 0; i < ApaReservedCodes.Milestone10.Length; i++)
            {
                var code = ApaReservedCodes.Milestone10[i];

                Assert.IsTrue(ApaReservedCodes.IsMilestone10Code(code));
                Assert.IsNotEmpty(ApaErrorCode.GetTitle(code), "Every allocated code needs a title: " + code);
                Assert.Greater(
                    string.CompareOrdinal(code, ApaErrorCode.RemovalMaskTextureFailed),
                    0,
                    "M10's codes must sit above M9's APA041.");

                Assert.IsFalse(
                    ApaReservedCodes.IsMilestone6Code(code) || ApaReservedCodes.IsMilestone9AuthoringCode(code),
                    "An M10 code must not also belong to an earlier milestone's range: " + code);
            }

            Assert.IsFalse(ApaReservedCodes.IsMilestone10Code(ApaErrorCode.RemovalMaskTextureFailed));
            Assert.IsNotEmpty(ApaErrorCode.GetTitle(ApaErrorCode.ArmatureSelectionInvalid));
            Assert.IsNotEmpty(ApaErrorCode.GetTitle(ApaErrorCode.BoneOutsideSelectedArmature));
        }

        /// <summary>
        /// The authoring aliases are the core constants, so a code cannot be renumbered in one place only.
        /// </summary>
        [Test]
        public void AuthoringAliases_AreTheCoreConstants()
        {
            Assert.AreEqual("APA033", AuthoringCodes.InvalidAuthoringPath);
            Assert.AreEqual(ApaErrorCode.InvalidAuthoringPath, AuthoringCodes.InvalidAuthoringPath);

            Assert.AreEqual("APA034", AuthoringCodes.NonPersistentReference);
            Assert.AreEqual(ApaErrorCode.NonPersistentReference, AuthoringCodes.NonPersistentReference);

            Assert.AreEqual("APA050", AuthoringCodes.UvSemanticChannelAbsent);
            Assert.AreEqual(ApaErrorCode.UvSemanticChannelAbsent, AuthoringCodes.UvSemanticChannelAbsent);

            // M9's mask-conversion refusal is the fourth authoring-layer code. It is not a second allocation
            // table: the alias is the core constant, and the code is enumerated in its own milestone record.
            Assert.AreEqual("APA041", AuthoringCodes.RemovalMaskTextureFailed);
            Assert.AreEqual(ApaErrorCode.RemovalMaskTextureFailed, AuthoringCodes.RemovalMaskTextureFailed);
            Assert.IsTrue(ApaReservedCodes.IsMilestone9AuthoringCode(AuthoringCodes.RemovalMaskTextureFailed));
            Assert.IsTrue(AuthoringCodes.IsAuthoringCode(ApaErrorCode.RemovalMaskTextureFailed));

            Assert.IsTrue(ApaReservedCodes.IsMilestone5AuthoringCode(AuthoringCodes.InvalidAuthoringPath));
            Assert.IsTrue(AuthoringCodes.IsAuthoringCode(ApaErrorCode.NonPersistentReference));
            Assert.IsTrue(AuthoringCodes.IsAuthoringCode(ApaErrorCode.UvSemanticChannelAbsent));

            // A code the authoring layer does not own is not an authoring code.
            Assert.IsFalse(AuthoringCodes.IsAuthoringCode(ApaErrorCode.UndeclaredUvChannel));
            Assert.IsFalse(AuthoringCodes.IsAuthoringCode(ApaErrorCode.PartOnlyShapeDisallowed));
        }

        // ---- helpers ---------------------------------------------------------------------------------

        /// <summary>
        /// A context that declares the M6 policy surface: two parts whose removal overlap is resolved by an
        /// explicit, unequal priority.
        /// </summary>
        private ValidationContext PolicyContext()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 5, removed: MeshFixtures.Removed(0, 0));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1, removed: MeshFixtures.Removed(0, 0));

            return MeshFixtures.Context(body, new[] { partA, partB });
        }

        private ApaPartProfile NewProfile()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);
            return profile;
        }
    }
}

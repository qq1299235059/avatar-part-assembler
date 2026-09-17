using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the draft: materialization into a transient profile, round-tripping an existing profile, and
    /// the semantic-editing helpers.
    /// </summary>
    /// <remarks>
    /// No asset is created or written. The transient profiles these tests build are destroyed in teardown, which
    /// is exactly what the authoring window does after every validation run.
    /// </remarks>
    public sealed class AuthoringProfileDraftTests
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

        [Test]
        public void New_AssignsAStablePartIdAndTheRequestedSlot()
        {
            var draft = ApaProfileDraft.New("Cyber Arm L", ApaPartSlot.LeftArm);

            Assert.AreEqual("Cyber Arm L", draft.Identity.DisplayName);
            Assert.AreEqual(ApaPartSlot.LeftArm, draft.Identity.Slot);
            Assert.IsTrue(draft.HasStablePartId);
            Assert.IsFalse(draft.EnsureStablePartId(), "A stable id is assigned once and then kept.");
        }

        [Test]
        public void Reset_ClearsEverythingIncludingTheIdentity()
        {
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            draft.Removal.Add(new RemovedTriangleAddress(0, 3));

            draft.Reset();

            Assert.IsFalse(draft.HasStablePartId);
            Assert.IsTrue(draft.Removal.IsEmpty);
            Assert.AreEqual(ApaPartSlot.Custom, draft.Identity.Slot);
        }

        [Test]
        public void Materialize_WritesTheCurrentSchemaVersionAndTheWholeDraft()
        {
            var draft = ApaProfileDraft.New("Cyber Arm L", ApaPartSlot.LeftArm);
            draft.Removal.AddRange(new[]
            {
                new RemovedTriangleAddress(1, 4),
                new RemovedTriangleAddress(0, 2)
            }, null);
            draft.SetPairedSeam(new[] { 3, 1 }, new[] { 7, 5 });
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };
            draft.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic("Metal", 0, null, ApaMaterialPolicyMode.ForceNew)
            };
            draft.Bones.MergeArmature = false;

            // The two armature selections are recorded as paths relative to their own roots, which is the form
            // the build resolves; the draft itself keeps no scene reference.
            var avatarRoot = Track(new GameObject("Avatar"));
            var targetArmature = Track(new GameObject("Armature"));
            targetArmature.transform.SetParent(avatarRoot.transform, false);
            var partRoot = Track(new GameObject("Part"));
            var partArmature = Track(new GameObject("PartArmature"));
            partArmature.transform.SetParent(partRoot.transform, false);
            draft.SetArmatures(
                avatarRoot.transform,
                targetArmature.transform,
                partRoot.transform,
                partArmature.transform);

            draft.BlendShapes.AllowPartOnlyShapes = false;
            draft.Compatibility = new ApaAvatarCompatibilityProfile
            {
                IsCaptured = true,
                RendererPath = "Body",
                VertexCount = 12,
                SubMeshIndexCounts = new[] { 36 },
                SubMeshTopologyValues = new[] { (int)MeshTopology.Triangles },
                BlendShapeNames = new string[0],
                BlendShapeFrameCounts = new int[0]
            };

            var profile = Track(draft.Materialize());

            Assert.AreEqual(ApaPartProfile.CurrentSchemaVersion, profile.SchemaVersion);
            Assert.AreEqual(draft.Identity.PartId, profile.Identity.PartId);
            Assert.AreEqual(ApaPartSlot.LeftArm, profile.Identity.Slot);
            Assert.AreEqual(2, profile.Removal.Count);
            Assert.AreEqual(new RemovedTriangleAddress(0, 2), profile.Removal.RemovedTriangles[0]);

            // The pair order is the correspondence, so it must survive materialization unsorted.
            CollectionAssert.AreEqual(new[] { 3, 1 }, profile.Seam.Base.VertexIndices);
            CollectionAssert.AreEqual(new[] { 7, 5 }, profile.Seam.Part.VertexIndices);
            Assert.AreEqual(ApaSeamProfile.ExplicitPairingVersion, profile.Seam.PairingVersion);

            Assert.AreEqual("UVMap", profile.UvSemantics[0].Semantic);
            Assert.AreEqual(ApaMaterialPolicyMode.ForceNew, profile.MaterialSemantics[0].Policy);
            Assert.IsFalse(profile.Bones.MergeArmature);
            Assert.AreEqual("Armature", profile.Bones.TargetArmaturePath);
            Assert.AreEqual("PartArmature", profile.Bones.PartArmaturePath);
            Assert.IsTrue(profile.Bones.HasArmatureSelection);
            Assert.IsFalse(profile.BlendShapes.AllowPartOnlyShapes);
            Assert.IsTrue(profile.Compatibility.IsCaptured);
            Assert.AreEqual("Body", profile.Compatibility.RendererPath);
            Assert.IsTrue(profile.IsSchemaSupported);
        }

        /// <summary>
        /// Clearing an armature selection writes the documented "not selected" state rather than leaving a stale
        /// path behind, which is what makes a re-selection necessary rather than silently reused.
        /// </summary>
        [Test]
        public void SetArmatures_RecordsPathsAndClearArmaturesRemovesThem()
        {
            var avatarRoot = Track(new GameObject("Avatar"));
            var targetArmature = Track(new GameObject("Armature"));
            targetArmature.transform.SetParent(avatarRoot.transform, false);
            var partRoot = Track(new GameObject("Part"));
            var partArmature = Track(new GameObject("PartArmature"));
            partArmature.transform.SetParent(partRoot.transform, false);

            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            draft.SetArmatures(
                avatarRoot.transform,
                targetArmature.transform,
                partRoot.transform,
                partArmature.transform);

            Assert.AreEqual("Armature", draft.Bones.TargetArmaturePath);
            Assert.AreEqual("PartArmature", draft.Bones.PartArmaturePath);
            Assert.IsTrue(draft.Bones.HasArmatureSelection);

            draft.ClearArmatures();

            Assert.AreEqual(string.Empty, draft.Bones.TargetArmaturePath);
            Assert.AreEqual(string.Empty, draft.Bones.PartArmaturePath);
            Assert.IsFalse(draft.Bones.HasArmatureSelection);
        }

        [Test]
        public void Materialize_DoesNotShareStateWithTheDraft()
        {
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };

            var profile = Track(draft.Materialize());

            draft.UvSemantics[0].Semantic = "Changed";
            draft.Removal.Add(new RemovedTriangleAddress(0, 1));

            Assert.AreEqual("UVMap", profile.UvSemantics[0].Semantic);
            Assert.AreEqual(0, profile.Removal.Count);
        }

        [Test]
        public void FromProfile_RoundTripsEveryFieldItOwns()
        {
            var source = Track(ScriptableObject.CreateInstance<ApaPartProfile>());
            source.SchemaVersion = ApaPartProfile.CurrentSchemaVersion;
            source.Identity.PartId = "stable-id";
            source.Identity.DisplayName = "Cyber Arm L";
            source.Identity.Slot = ApaPartSlot.LeftArm;
            source.Removal = ApaRemovalProfile.ForSubMesh(1, new[] { 5, 2 });

            // A paired seam: the two lists are a correspondence, and the round trip must not sort or re-pair it.
            source.Seam = new ApaSeamProfile();
            source.Seam.SetPaired(new[] { 2, 0 }, new[] { 9, 4 });

            source.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };
            source.MaterialSemantics = new[] { new ApaMaterialSlotSemantic("Metal", 1, null, ApaMaterialPolicyMode.KeepPart) };
            source.Bones.MergeArmature = false;
            source.Bones.TargetArmaturePath = "Armature";
            source.Bones.PartArmaturePath = "PartArmature";

            // The legacy name policy is still serialized on the asset, so the round trip has to carry it even
            // though nothing consults it; ContentEquals serializes every field and fails if one is lost.
            source.Bones.MergePrefix = "P_";
            source.Bones.MergeSuffix = "_L";
            source.Bones.InferMergeNames = true;
            source.Identity.SlotMode = ApaPartSlotMode.Augment;
            source.Identity.ConflictPriority = 7;
            source.BlendShapes.AllowPartOnlyShapes = false;
            source.Compatibility = new ApaAvatarCompatibilityProfile
            {
                IsCaptured = true,
                RendererPath = "Body",
                VertexCount = 4,
                SubMeshIndexCounts = new[] { 6 },
                SubMeshTopologyValues = new[] { (int)MeshTopology.Triangles }
            };

            var draft = ApaProfileDraft.FromProfile(source);
            var rebuilt = Track(draft.Materialize());

            Assert.AreEqual("stable-id", draft.Identity.PartId);
            Assert.AreEqual(2, draft.Removal.Count);
            Assert.AreEqual(2, draft.Seam.BaseCount);
            Assert.IsTrue(draft.Seam.IsPaired);
            CollectionAssert.AreEqual(new[] { 2, 0 }, draft.Seam.GetBaseIndices());
            CollectionAssert.AreEqual(new[] { 9, 4 }, draft.Seam.GetPartIndices());
            Assert.IsFalse(draft.Bones.MergeArmature);
            Assert.AreEqual("Armature", draft.Bones.TargetArmaturePath);
            Assert.AreEqual("PartArmature", draft.Bones.PartArmaturePath);
            Assert.IsFalse(draft.BlendShapes.AllowPartOnlyShapes);
            Assert.IsTrue(ApaProfileWriter.ContentEquals(source, rebuilt));
        }

        /// <summary>
        /// The M10 armature selections survive a load/save round trip through the authoring draft.
        /// </summary>
        /// <remarks>
        /// The build resolves the two selected armatures to scope every bone identity, so a draft that dropped
        /// them would silently reset the author's decision to "not selected" — the state the build refuses with
        /// <c>APA043</c> — on the next save. The comparison is the writer's own content equality, which serializes
        /// every field: it fails if any one of them is lost.
        /// </remarks>
        [Test]
        public void FromProfile_KeepsTheArmatureSelectionsAndMultiPartPolicyFields()
        {
            var source = Track(ScriptableObject.CreateInstance<ApaPartProfile>());
            source.SchemaVersion = ApaPartProfile.CurrentSchemaVersion;
            source.Identity.PartId = "policy-id";
            source.Identity.DisplayName = "Hat";
            source.Identity.Slot = ApaPartSlot.Head;
            source.Identity.SlotMode = ApaPartSlotMode.Augment;
            source.Identity.ConflictPriority = 4;
            source.Bones.MergeArmature = true;
            source.Bones.TargetArmaturePath = "Armature";
            source.Bones.PartArmaturePath = "Armature";

            var draft = ApaProfileDraft.FromProfile(source);

            Assert.AreEqual(ApaPartSlotMode.Augment, draft.Identity.SlotMode);
            Assert.AreEqual(4, draft.Identity.ConflictPriority);
            Assert.AreEqual("Armature", draft.Bones.TargetArmaturePath);
            Assert.AreEqual("Armature", draft.Bones.PartArmaturePath);
            Assert.IsTrue(draft.Bones.HasArmatureSelection);

            var rebuilt = Track(draft.Materialize());
            Assert.AreEqual(ApaPartSlotMode.Augment, rebuilt.Identity.SlotMode);
            Assert.AreEqual(4, rebuilt.Identity.ConflictPriority);
            Assert.AreEqual("Armature", rebuilt.Bones.TargetArmaturePath);
            Assert.AreEqual("Armature", rebuilt.Bones.PartArmaturePath);
            Assert.IsTrue(ApaProfileWriter.ContentEquals(source, rebuilt));
        }

        /// <summary>A draft that never touches the policy fields writes the strict defaults.</summary>
        [Test]
        public void NewDraft_DefaultsToTheStrictMultiPartPolicy()
        {
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            var profile = Track(draft.Materialize());

            Assert.AreEqual(ApaPartSlotMode.Replace, profile.Identity.SlotMode);
            Assert.AreEqual(0, profile.Identity.ConflictPriority);
            Assert.IsFalse(profile.Identity.HasConflictPriority);

            // "Not selected" is the default and is never guessed at; the build reports it as APA043.
            Assert.AreEqual(string.Empty, profile.Bones.TargetArmaturePath);
            Assert.AreEqual(string.Empty, profile.Bones.PartArmaturePath);
            Assert.IsFalse(profile.Bones.HasArmatureSelection);
            Assert.IsTrue(profile.Seam.IsEmpty, "A new draft declares no seam.");
        }

        [Test]
        public void SetFromProfile_ReplacesTheDraftInPlace()
        {
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            var source = Track(ScriptableObject.CreateInstance<ApaPartProfile>());
            source.Identity.PartId = "other";
            source.Identity.DisplayName = "Leg";
            source.Identity.Slot = ApaPartSlot.LeftLeg;

            draft.SetFromProfile(source);

            Assert.AreEqual("other", draft.Identity.PartId);
            Assert.AreEqual("Leg", draft.Identity.DisplayName);
            Assert.AreEqual(ApaPartSlot.LeftLeg, draft.Identity.Slot);
        }

        [Test]
        public void ContentEquals_DetectsContentDifferencesOnly()
        {
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            var left = Track(draft.Materialize());
            var right = Track(draft.Materialize());

            Assert.IsTrue(ApaProfileWriter.ContentEquals(left, right));

            right.Identity.DisplayName = "Different";
            Assert.IsFalse(ApaProfileWriter.ContentEquals(left, right));
        }

        [Test]
        public void HasUnsavedChanges_ComparesTheDraftWithTheAsset()
        {
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            var asset = Track(draft.Materialize());

            var loaded = ApaProfileDraft.FromProfile(asset);
            Assert.IsFalse(ApaProfileWriter.HasUnsavedChanges(asset, loaded));

            loaded.Removal.Add(new RemovedTriangleAddress(0, 1));
            Assert.IsTrue(ApaProfileWriter.HasUnsavedChanges(asset, loaded));
        }

        [Test]
        public void AddUvSemantic_PicksTheNextFreeChannel()
        {
            var draft = new ApaProfileDraft();
            draft.EnsureInitialized();

            Assert.AreEqual(0, draft.AddUvSemantic());
            draft.UvSemantics[0].Semantic = "UVMap";
            Assert.AreEqual(1, draft.AddUvSemantic());
            draft.UvSemantics[1].Semantic = "DetailUV";
            Assert.AreEqual(2, draft.AddUvSemantic());

            Assert.AreEqual(3, draft.UvSemantics.Length);
            Assert.AreEqual(2, draft.UvSemantics[2].SourceChannel);
        }

        [Test]
        public void RemoveAndMoveUvSemantic_KeepTheAuthorOrderMeaningful()
        {
            var draft = new ApaProfileDraft();
            draft.EnsureInitialized();
            draft.UvSemantics = new[]
            {
                new ApaUvChannelSemantic("A", 0),
                new ApaUvChannelSemantic("B", 1),
                new ApaUvChannelSemantic("C", 2)
            };

            draft.MoveUvSemantic(2, 0);
            Assert.AreEqual("C", draft.UvSemantics[0].Semantic);
            Assert.AreEqual("A", draft.UvSemantics[1].Semantic);

            draft.RemoveUvSemanticAt(1);
            Assert.AreEqual(2, draft.UvSemantics.Length);
            Assert.AreEqual("C", draft.UvSemantics[0].Semantic);
            Assert.AreEqual("B", draft.UvSemantics[1].Semantic);
        }

        [Test]
        public void AddMaterialSemantic_PicksTheNextFreeSubMeshAndDefaultsToAuto()
        {
            var draft = new ApaProfileDraft();
            draft.EnsureInitialized();

            draft.AddMaterialSemantic();
            draft.MaterialSemantics[0].Semantic = "Skin";
            Assert.AreEqual(1, draft.AddMaterialSemantic());

            Assert.AreEqual(ApaMaterialPolicyMode.Auto, draft.MaterialSemantics[1].Policy);
            Assert.AreEqual(1, draft.MaterialSemantics[1].SourceSubMesh);

            draft.SetMaterialPolicy(1, ApaMaterialPolicyMode.UseTarget);
            Assert.AreEqual(ApaMaterialPolicyMode.UseTarget, draft.MaterialSemantics[1].Policy);
        }

        [Test]
        public void RemoveAndMoveMaterialSemantic_KeepTheResolverOrderMeaningful()
        {
            // The window exposes these two operations on a row, and the material resolver numbers new slots in
            // declaration order, so the order a row is moved to is author-visible behaviour.
            var draft = new ApaProfileDraft();
            draft.EnsureInitialized();
            draft.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic("Skin", 0, null, ApaMaterialPolicyMode.Auto),
                new ApaMaterialSlotSemantic("Cloth", 1, null, ApaMaterialPolicyMode.KeepPart),
                new ApaMaterialSlotSemantic("Hair", 2, null, ApaMaterialPolicyMode.ForceNew)
            };

            draft.MoveMaterialSemantic(2, 0);
            Assert.AreEqual("Hair", draft.MaterialSemantics[0].Semantic);
            Assert.AreEqual("Skin", draft.MaterialSemantics[1].Semantic);
            Assert.AreEqual(ApaMaterialPolicyMode.ForceNew, draft.MaterialSemantics[0].Policy);

            draft.RemoveMaterialSemanticAt(1);
            Assert.AreEqual(2, draft.MaterialSemantics.Length);
            Assert.AreEqual("Hair", draft.MaterialSemantics[0].Semantic);
            Assert.AreEqual("Cloth", draft.MaterialSemantics[1].Semantic);
        }

        [Test]
        public void MoveSemantics_RefuseAnIndexOutsideTheRowsTheyOwn()
        {
            var draft = new ApaProfileDraft();
            draft.EnsureInitialized();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("A", 0) };
            draft.MaterialSemantics = new[] { new ApaMaterialSlotSemantic("Skin", 0, null, ApaMaterialPolicyMode.Auto) };

            draft.MoveUvSemantic(0, 1);
            draft.MoveUvSemantic(-1, 0);
            draft.MoveMaterialSemantic(0, 1);
            draft.MoveMaterialSemantic(0, -1);

            Assert.AreEqual("A", draft.UvSemantics[0].Semantic);
            Assert.AreEqual("Skin", draft.MaterialSemantics[0].Semantic);
        }

        [Test]
        public void Materialize_HandlesANullSubObjectArrayEntry()
        {
            // Unity deserializes a null array entry as a null reference, so every read path has to survive one.
            var draft = ApaProfileDraft.New("Arm", ApaPartSlot.LeftArm);
            draft.UvSemantics = new ApaUvChannelSemantic[] { null };
            draft.MaterialSemantics = new ApaMaterialSlotSemantic[] { null };

            draft.EnsureInitialized();
            var profile = Track(draft.Materialize());

            Assert.AreEqual(1, profile.UvSemantics.Length);
            Assert.IsNotNull(profile.UvSemantics[0]);
            Assert.IsNotNull(profile.MaterialSemantics[0]);
        }

        private T Track<T>(T value) where T : Object
        {
            _created.Add(value);
            return value;
        }
    }
}

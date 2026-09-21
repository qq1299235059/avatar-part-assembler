using System;
using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Protected
{
    /// <summary>
    /// Tests for the protected-mesh asset, its writer's transaction, and the installer field that carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the tests that need an asset database, because the properties under test are about files: a
    /// replaced payload keeps the asset's GUID, a refused overwrite writes nothing, a rolled-back write restores
    /// the previous contents, and an unrelated asset at the target path is never deleted.
    /// </para>
    /// <para>
    /// Everything runs inside one temporary project folder that is removed afterwards, so the suite leaves the
    /// project exactly as it found it.
    /// </para>
    /// </remarks>
    public sealed class ProtectedMeshAssetTests
    {
        private const string TempFolder = "Assets/APAProtectedMeshTests";

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "APAProtectedMeshTests");
            }
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();

            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.Refresh();
            ApaProtectedMeshCache.Clear();
        }

        // ---- The asset ---------------------------------------------------------------------------------

        [Test]
        public void Asset_AssignCopiesEveryArrayAndDescribesTheEnvelope()
        {
            var payload = PayloadFor(Snapshot(4), "part-a");
            var asset = NewAsset(payload);

            Assert.AreEqual(ApaProtectedMeshAsset.CurrentFormatVersion, asset.FormatVersion);
            Assert.AreEqual(ApaProtectedMeshAsset.CurrentCodec, asset.Codec);
            Assert.AreEqual("part-a", asset.PartId);
            Assert.AreEqual(payload.SourceFingerprint, asset.SourceFingerprint);
            Assert.AreEqual(payload.PlaintextLength, asset.PlaintextLength);
            Assert.AreEqual(payload.Ciphertext.Length, asset.CiphertextLength);
            Assert.IsTrue(asset.HasPayload);

            // Every accessor hands out a copy: a caller that mutates what it received must not be able to change
            // the payload the asset will decode.
            var salt = asset.CopySalt();
            salt[0] ^= 0xFF;
            Assert.AreNotEqual(salt[0], asset.CopySalt()[0]);

            var ciphertext = asset.CopyCiphertext();
            ciphertext[0] ^= 0xFF;
            Assert.AreNotEqual(ciphertext[0], asset.CopyCiphertext()[0]);

            StringAssert.Contains("part-a", asset.Describe());
        }

        [Test]
        public void Asset_HasPayloadRequiresACompleteEnvelope()
        {
            var asset = ScriptableObject.CreateInstance<ApaProtectedMeshAsset>();
            _created.Add(asset);

            Assert.IsFalse(asset.HasPayload, "A fresh asset carries no payload.");

            asset.Assign(
                ApaProtectedMeshAsset.CurrentFormatVersion,
                ApaProtectedMeshAsset.CurrentCodec,
                "part-a",
                string.Empty,
                4,
                new byte[ApaProtectedMeshAsset.SaltLength - 1],
                new byte[ApaProtectedMeshAsset.IvLength],
                new byte[16],
                new byte[ApaProtectedMeshAsset.TagLength]);

            Assert.IsFalse(asset.HasPayload, "A short salt makes the envelope incomplete.");
        }

        [Test]
        public void Asset_ContentRevisionChangesWhenThePayloadChanges()
        {
            var first = NewAsset(PayloadFor(Snapshot(4), "part-a"));
            var revision = first.ContentRevision;

            Assert.AreEqual(revision, first.ContentRevision, "An unchanged payload must report a stable revision.");

            first.Assign(
                ApaProtectedMeshAsset.CurrentFormatVersion,
                ApaProtectedMeshAsset.CurrentCodec,
                "part-a",
                "fingerprint",
                8,
                new byte[ApaProtectedMeshAsset.SaltLength],
                new byte[ApaProtectedMeshAsset.IvLength],
                new byte[32],
                new byte[ApaProtectedMeshAsset.TagLength]);

            Assert.AreNotEqual(revision, first.ContentRevision, "A replaced payload must change the revision.");
        }

        // ---- The writer --------------------------------------------------------------------------------

        [Test]
        public void Writer_CreatesAnAssetThatLoadsBackAndDecodes()
        {
            var path = TempFolder + "/Part_ProtectedMesh.asset";
            var payload = PayloadFor(Snapshot(5), "part-a");

            var result = ApaProtectedMeshAssetWriter.Create(path, payload, payload.SourceFingerprint);

            Assert.AreEqual(ApaProtectedMeshWriteStatus.Created, result.Status, result.Message);
            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(path, result.Path);

            var loaded = AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(path);
            Assert.IsNotNull(loaded, "The written asset must be loadable at its path.");
            Assert.IsTrue(loaded.HasPayload);
            Assert.IsTrue(ApaProtectedMeshCodec.TryDecode(loaded, out var data, out var issue, "part-a"), issue?.Message);
            Assert.AreEqual(5, data.VertexCount);
        }

        [Test]
        public void Writer_RefusesAnOverwriteUntilTheCallerAllowsIt()
        {
            var path = TempFolder + "/Part_ProtectedMesh.asset";
            var first = PayloadFor(Snapshot(4), "part-a");
            var second = PayloadFor(Snapshot(7), "part-a");

            Assert.AreEqual(
                ApaProtectedMeshWriteStatus.Created,
                ApaProtectedMeshAssetWriter.Create(path, first, first.SourceFingerprint).Status);

            var refused = ApaProtectedMeshAssetWriter.Create(path, second, second.SourceFingerprint);
            Assert.AreEqual(ApaProtectedMeshWriteStatus.RefusedOverwrite, refused.Status);
            Assert.IsTrue(refused.RequiresOverwriteConfirmation);
            Assert.AreEqual(
                4,
                StoredVertexCount(path),
                "The refused write must have left the original payload in place.");
        }

        [Test]
        public void Writer_UpdateKeepsTheGuidAndRollbackRestoresThePreviousPayload()
        {
            var path = TempFolder + "/Part_ProtectedMesh.asset";
            var first = PayloadFor(Snapshot(4), "part-a");
            var second = PayloadFor(Snapshot(7), "part-a");

            var created = ApaProtectedMeshAssetWriter.Create(path, first, first.SourceFingerprint);
            Assert.AreEqual(ApaProtectedMeshWriteStatus.Created, created.Status, created.Message);
            created.Commit();

            var guidBefore = AssetDatabase.AssetPathToGUID(path);
            Assert.IsNotEmpty(guidBefore);

            var updated = ApaProtectedMeshAssetWriter.Write(path, second, second.SourceFingerprint, true);
            Assert.AreEqual(ApaProtectedMeshWriteStatus.Updated, updated.Status, updated.Message);
            Assert.AreEqual(
                guidBefore,
                AssetDatabase.AssetPathToGUID(path),
                "Replacing a payload must keep the asset's GUID, or every prefab that references it breaks.");

            Assert.AreEqual(7, StoredVertexCount(path));

            updated.Rollback();

            Assert.AreEqual(
                4,
                StoredVertexCount(path),
                "A rolled-back replacement must restore the previous payload exactly.");
        }

        [Test]
        public void Writer_CommitMakesARollbackANoOp()
        {
            var path = TempFolder + "/Part_ProtectedMesh.asset";
            var first = PayloadFor(Snapshot(4), "part-a");
            var second = PayloadFor(Snapshot(7), "part-a");

            ApaProtectedMeshAssetWriter.Create(path, first, first.SourceFingerprint).Commit();
            var updated = ApaProtectedMeshAssetWriter.Write(path, second, second.SourceFingerprint, true);
            Assert.AreEqual(ApaProtectedMeshWriteStatus.Updated, updated.Status, updated.Message);

            updated.Commit();
            updated.Rollback();

            Assert.AreEqual(
                7,
                StoredVertexCount(path),
                "A committed write must not be undone by a later rollback call.");
        }

        [Test]
        public void Writer_RefusesAnOccupiedPathOfAnotherTypeWithoutDeletingIt()
        {
            var path = TempFolder + "/Occupied.asset";
            var occupant = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(occupant);
            AssetDatabase.CreateAsset(occupant, path);
            AssetDatabase.SaveAssets();

            var payload = PayloadFor(Snapshot(4), "part-a");
            var result = ApaProtectedMeshAssetWriter.Write(path, payload, payload.SourceFingerprint, true);

            Assert.AreEqual(ApaProtectedMeshWriteStatus.RefusedOccupiedPath, result.Status, result.Message);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<ApaPartProfile>(path),
                "Permission to replace a payload is not permission to delete an unrelated asset.");
        }

        [Test]
        public void Writer_RefusesAnInvalidPath()
        {
            var payload = PayloadFor(Snapshot(4), "part-a");

            var outside = ApaProtectedMeshAssetWriter.Write(
                "Assets/../Escaped.asset", payload, payload.SourceFingerprint, true);
            Assert.AreEqual(ApaProtectedMeshWriteStatus.RefusedInvalidPath, outside.Status);

            var wrongExtension = ApaProtectedMeshAssetWriter.Write(
                TempFolder + "/Payload.prefab", payload, payload.SourceFingerprint, true);
            Assert.AreEqual(ApaProtectedMeshWriteStatus.RefusedInvalidPath, wrongExtension.Status);

            var noPayload = ApaProtectedMeshAssetWriter.Write(
                TempFolder + "/NoPayload.asset", null, string.Empty, true);
            Assert.AreEqual(ApaProtectedMeshWriteStatus.Failed, noPayload.Status);
        }

        [Test]
        public void DefaultPathFor_PutsThePayloadBesideThePrefab()
        {
            Assert.AreEqual(
                "Assets/Parts/Arm_ProtectedMesh.asset",
                ApaProtectedMeshAssetWriter.DefaultPathFor("Assets/Parts/Arm.prefab"));
            Assert.AreEqual(
                "Assets/Parts/Arm_ProtectedMesh.asset",
                ApaProtectedMeshAssetWriter.DefaultPathFor("Assets\\Parts\\Arm.PREFAB"));
            Assert.AreEqual(string.Empty, ApaProtectedMeshAssetWriter.DefaultPathFor(null));
        }

        // ---- The installer field -----------------------------------------------------------------------

        [Test]
        public void Installer_ProtectedMeshDefaultsToNullAndIsSerializedOnAPrefab()
        {
            var go = new GameObject("Protected Part");
            _created.Add(go);

            var installer = go.AddComponent<AvatarPartInstaller>();
            Assert.IsFalse(installer.HasProtectedMesh, "Protection is opt-in: a fresh installer has no payload.");
            Assert.IsNull(installer.ProtectedMesh);

            var payload = PayloadFor(Snapshot(4), "part-a");
            var asset = ApaProtectedMeshAssetWriter.Create(
                TempFolder + "/Part_ProtectedMesh.asset", payload, payload.SourceFingerprint).Asset;
            Assert.IsNotNull(asset);

            installer.ProtectedMesh = asset;

            var prefabPath = TempFolder + "/ProtectedPart.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);

            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(loaded);

            var loadedInstaller = loaded.GetComponent<AvatarPartInstaller>();
            Assert.IsNotNull(loadedInstaller);
            Assert.IsTrue(loadedInstaller.HasProtectedMesh, "The payload reference must survive the prefab save.");
            Assert.AreEqual(asset, loadedInstaller.ProtectedMesh);

            // The field name is a contract: the prefab generator and the installer inspector address the
            // serialized field by name, so a rename would silently break the protection wiring.
            var serialized = new SerializedObject(loadedInstaller);
            Assert.IsNotNull(
                serialized.FindProperty("_protectedMesh"),
                "AvatarPartInstaller must keep the serialized field name '_protectedMesh'.");
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private ApaProtectedMeshAsset NewAsset(ApaProtectedMeshPayload payload)
        {
            var asset = ScriptableObject.CreateInstance<ApaProtectedMeshAsset>();
            asset.Assign(
                payload.FormatVersion,
                payload.Codec,
                payload.PartId,
                payload.SourceFingerprint,
                payload.PlaintextLength,
                payload.Salt,
                payload.Iv,
                payload.Ciphertext,
                payload.Tag);
            _created.Add(asset);
            return asset;
        }

        private static ApaProtectedMeshPayload PayloadFor(MeshSnapshot snapshot, string partId)
        {
            Assert.IsTrue(
                ApaProtectedMeshCodec.TryCreatePayload(snapshot, partId, out var payload, out var issue),
                issue != null ? issue.Message : "the payload was not created");
            return payload;
        }

        /// <summary>
        /// Decodes the asset at a path and reports its vertex count, which is the cheapest way for a test to tell
        /// two payloads apart without reading the ciphertext.
        /// </summary>
        private static int StoredVertexCount(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(path);
            Assert.IsNotNull(asset, "No protected mesh asset at " + path);

            ApaProtectedMeshCache.Clear();
            Assert.IsTrue(
                ApaProtectedMeshCache.TryDecode(asset, asset.PartId, out var data, out var issue),
                issue != null ? issue.Message : "the stored payload did not decode");

            return data.VertexCount;
        }

        private static MeshSnapshot Snapshot(int vertexCount)
        {
            var vertices = new Vector3[vertexCount];
            for (var i = 0; i < vertexCount; i++) vertices[i] = new Vector3(i, 0f, 0f);

            var indices = new int[(vertexCount / 3) * 3];
            for (var i = 0; i < indices.Length; i++) indices[i] = i % vertexCount;

            return MeshSnapshot.Create(
                "Protected" + vertexCount,
                vertices,
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { indices },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16);
        }
    }
}

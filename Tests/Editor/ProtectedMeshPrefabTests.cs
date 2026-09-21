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
    /// Tests for protected prefab creation: what is written, what is cleared, what is refused, and what is left
    /// behind when a step fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the tests that prove the feature's central promise on real files: a protected prefab has no
    /// dependency on the source mesh, an unprotected prefab is untouched by the feature, and a creation that
    /// cannot prove the prefab is leak-free writes neither the prefab nor the payload.
    /// </para>
    /// <para>
    /// Every test runs inside one temporary project folder that is removed afterwards, and the scene objects it
    /// creates are destroyed in teardown.
    /// </para>
    /// </remarks>
    public sealed class ProtectedMeshPrefabTests
    {
        private const string TempFolder = "Assets/APAProtectedPrefabTests";

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "APAProtectedPrefabTests");
            }

            ApaProtectedMeshCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ApaProtectedMeshCache.Clear();

            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();

            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// The unprotected path is exactly what it always was: the prefab keeps the source mesh, the installer
        /// carries no payload, and no protected asset is written.
        /// </summary>
        [Test]
        public void CreateFromScene_UnprotectedPathKeepsTheMeshAndWritesNoPayload()
        {
            var rig = NewRig("Unprotected");
            var prefabPath = TempFolder + "/Unprotected.prefab";

            var result = ApaPrefabGenerator.CreateFromScene(rig.Selection, rig.Profile, prefabPath, false);

            Assert.AreEqual(ApaPrefabStatus.Created, result.Status, result.Message);

            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(loaded);

            var installer = loaded.GetComponent<AvatarPartInstaller>();
            Assert.IsNotNull(installer);
            Assert.IsFalse(installer.HasProtectedMesh, "The unprotected path must not assign a payload.");

            var renderer = installer.ResolvePartRoot().GetComponentInChildren<Renderer>(true);
            Assert.AreEqual(
                rig.SourceMesh,
                SharedMeshOf(renderer),
                "The unprotected prefab must keep referencing the source mesh.");

            Assert.IsNull(
                AssetDatabase.LoadMainAssetAtPath(ApaProtectedMeshAssetWriter.DefaultPathFor(prefabPath)),
                "No protected asset may be written on the unprotected path.");
        }

        /// <summary>
        /// The protected path writes the payload, clears the renderer's serialized mesh, and leaves the prefab
        /// with no dependency on the source mesh asset.
        /// </summary>
        [Test]
        public void CreateFromScene_ProtectedPathPublishesThePayloadAndLeavesNoMeshDependency()
        {
            var rig = NewRig("Protected");
            var prefabPath = TempFolder + "/Protected.prefab";
            var payloadPath = ApaProtectedMeshAssetWriter.DefaultPathFor(prefabPath);

            var result = ApaPrefabGenerator.CreateFromScene(
                rig.Selection, rig.Profile, prefabPath, false, true, payloadPath);

            Assert.AreEqual(ApaPrefabStatus.Created, result.Status, result.Message);

            var payload = AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(payloadPath);
            Assert.IsNotNull(payload, "The protected asset must be written beside the prefab.");
            Assert.IsTrue(payload.HasPayload);

            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var installer = loaded.GetComponent<AvatarPartInstaller>();
            Assert.IsTrue(installer.HasProtectedMesh, "The installer must reference the payload.");
            Assert.AreEqual(payload, installer.ProtectedMesh);

            var renderer = installer.ResolvePartRoot().GetComponentInChildren<Renderer>(true);
            Assert.IsTrue(
                SharedMeshOf(renderer) == null,
                "The saved prefab's part renderer must carry no mesh.");

            var sourcePath = AssetDatabase.GetAssetPath(rig.SourceMesh);
            Assert.IsNotEmpty(sourcePath);
            foreach (var dependency in AssetDatabase.GetDependencies(prefabPath, true))
            {
                Assert.IsFalse(
                    ApaAuthoringAssetPaths.PathsEqual(dependency, sourcePath),
                    "The protected prefab must not depend on the source mesh '" + sourcePath + "'.");
            }

            // The authoring scene is only read: the scene object keeps its mesh and its components.
            Assert.AreEqual(rig.SourceMesh, SharedMeshOf(rig.PartRenderer));
        }

        /// <summary>
        /// A second reference to the source mesh — a collider, or another renderer — is a leak, and the creation
        /// refuses rather than publishing a prefab that still ships the mesh it protects.
        /// </summary>
        [Test]
        public void CreateFromScene_RefusesWhenAnotherComponentStillReferencesTheSourceMesh()
        {
            var rig = NewRig("Leaky");
            var collider = rig.PartRoot.AddComponent<MeshCollider>();
            collider.sharedMesh = rig.SourceMesh;

            var prefabPath = TempFolder + "/Leaky.prefab";
            var payloadPath = ApaProtectedMeshAssetWriter.DefaultPathFor(prefabPath);

            var result = ApaPrefabGenerator.CreateFromScene(
                rig.Selection, rig.Profile, prefabPath, false, true, payloadPath);

            Assert.AreEqual(ApaPrefabStatus.RefusedProtectedPayload, result.Status, result.Message);
            Assert.IsNull(
                AssetDatabase.LoadMainAssetAtPath(prefabPath),
                "A refused creation must not leave the prefab it just wrote behind.");
            Assert.IsNull(
                AssetDatabase.LoadMainAssetAtPath(payloadPath),
                "A refused creation must not leave an orphan payload behind.");
            Assert.AreEqual(rig.SourceMesh, SharedMeshOf(rig.PartRenderer), "The scene part must be untouched.");
        }

        /// <summary>
        /// An existing payload needs its own confirmation: a create that would replace one is refused with the
        /// status that names the payload, not the prefab.
        /// </summary>
        [Test]
        public void CreateFromScene_RefusesAProtectedOverwriteUntilTheCallerAllowsIt()
        {
            var rig = NewRig("Overwrite");
            var prefabPath = TempFolder + "/Overwrite.prefab";
            var payloadPath = ApaProtectedMeshAssetWriter.DefaultPathFor(prefabPath);

            // A payload exists while the prefab does not: the only file in the way is the payload.
            var existing = ApaProtectedMeshCodec.TryCreatePayload(
                rig.Snapshot, "part-a", out var payload, out var issue);
            Assert.IsTrue(existing, issue?.Message);
            ApaProtectedMeshAssetWriter.Create(payloadPath, payload, payload.SourceFingerprint);

            var refused = ApaPrefabGenerator.CreateFromScene(
                rig.Selection, rig.Profile, prefabPath, false, true, payloadPath);

            Assert.AreEqual(ApaPrefabStatus.RefusedProtectedOverwrite, refused.Status, refused.Message);
            Assert.IsTrue(refused.RequiresOverwriteConfirmation);

            var created = ApaPrefabGenerator.CreateFromScene(
                rig.Selection, rig.Profile, prefabPath, true, true, payloadPath);

            Assert.AreEqual(ApaPrefabStatus.Created, created.Status, created.Message);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(payloadPath));
        }

        /// <summary>
        /// A path occupied by an unrelated asset is refused before anything is planned, so the payload is never
        /// written and the occupant is never deleted.
        /// </summary>
        [Test]
        public void CreateFromScene_RefusesAPayloadPathOccupiedByAnotherAsset()
        {
            var rig = NewRig("Occupied");
            var prefabPath = TempFolder + "/Occupied.prefab";
            var payloadPath = ApaProtectedMeshAssetWriter.DefaultPathFor(prefabPath);

            var occupant = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(occupant);
            AssetDatabase.CreateAsset(occupant, payloadPath);
            AssetDatabase.SaveAssets();

            var result = ApaPrefabGenerator.CreateFromScene(
                rig.Selection, rig.Profile, prefabPath, false, true, payloadPath);

            Assert.AreEqual(ApaPrefabStatus.RefusedProtectedPayload, result.Status, result.Message);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<ApaPartProfile>(payloadPath),
                "The unrelated asset at the payload path must be untouched.");
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(prefabPath));
        }

        /// <summary>
        /// A protected creation on a part whose mesh cannot be read refuses with the readable-mesh diagnostic and
        /// writes nothing: a payload that could not carry the mesh must not be published as if it had.
        /// </summary>
        [Test]
        public void CreateFromScene_RefusesWhenThePartMeshCannotBeProtected()
        {
            var rig = NewRig("NoMesh");
            SharedMeshSetter(rig.PartRenderer, null);

            var prefabPath = TempFolder + "/NoMesh.prefab";
            var payloadPath = ApaProtectedMeshAssetWriter.DefaultPathFor(prefabPath);

            var result = ApaPrefabGenerator.CreateFromScene(
                rig.Selection, rig.Profile, prefabPath, false, true, payloadPath);

            Assert.AreEqual(ApaPrefabStatus.RefusedProtectedPayload, result.Status, result.Message);
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(prefabPath));
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(payloadPath));
        }

        /// <summary>
        /// The payload reference a protected creation writes is still the same Unity asset after the asset
        /// database has been saved and reloaded.
        /// </summary>
        /// <remarks>
        /// This is the reload half of the reference contract. Reading the prefab back resolves the payload
        /// reference through the asset database, which is exactly the operation that can hand out a second managed
        /// wrapper for the payload the creation wrote. The check must accept that state — it is what every later
        /// consumer sees — while still refusing a reference to a different payload.
        /// </remarks>
        [Test]
        public void CreateFromScene_ProtectedReferenceSurvivesASaveAndAssetDatabaseReload()
        {
            var rig = NewRig("Reload");
            var prefabPath = TempFolder + "/Reload.prefab";
            var payloadPath = ApaProtectedMeshAssetWriter.DefaultPathFor(prefabPath);

            var result = ApaPrefabGenerator.CreateFromScene(
                rig.Selection, rig.Profile, prefabPath, false, true, payloadPath);
            Assert.AreEqual(ApaPrefabStatus.Created, result.Status, result.Message);

            // Everything is re-read from disk afterwards: the payload the prefab stores is resolved by the asset
            // database rather than being the instance the creation held.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var payload = AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(payloadPath);
            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(payload, "The payload must still be on disk after the reload.");
            Assert.IsNotNull(loaded);

            var installer = loaded.GetComponent<AvatarPartInstaller>();
            Assert.IsTrue(installer.HasProtectedMesh, "The installer must still reference the payload after a reload.");
            Assert.IsTrue(
                ApaAssetDatabaseUtility.SameAsset(installer.ProtectedMesh, payload),
                "The reloaded reference and a freshly loaded payload must be the same Unity asset.");
            Assert.IsTrue(
                ApaAssetDatabaseUtility.IsAssetAtPath(installer.ProtectedMesh, payloadPath),
                "The reloaded reference must resolve to the payload path.");
            Assert.IsTrue(
                ApaAssetDatabaseUtility.IsSameAssetAtPath(installer.ProtectedMesh, payloadPath, payload),
                "The reference check must accept the payload after an asset database reload.");
        }

        /// <summary>
        /// The identity rule behind the reference check: one asset loaded twice is the same asset, and a
        /// different payload is not — whichever instance the write returned.
        /// </summary>
        [Test]
        public void PayloadIdentity_AcceptsTheSameAssetAndRejectsADifferentPayload()
        {
            var rig = NewRig("Identity");
            var firstPath = TempFolder + "/IdentityFirst.asset";
            var secondPath = TempFolder + "/IdentitySecond.asset";
            WritePayload(rig, firstPath);
            WritePayload(rig, secondPath);

            var first = AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(firstPath);
            var second = AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(secondPath);
            var firstAgain = AssetDatabase.LoadMainAssetAtPath(firstPath) as ApaProtectedMeshAsset;
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.IsNotNull(firstAgain);

            Assert.IsTrue(
                ApaAssetDatabaseUtility.SameAsset(first, firstAgain),
                "Two loads of one asset are one asset, whatever wrappers the asset database hands out.");
            Assert.IsTrue(ApaAssetDatabaseUtility.IsAssetAtPath(first, firstPath));
            Assert.IsTrue(ApaAssetDatabaseUtility.IsSameAssetAtPath(first, firstPath, first));
            Assert.IsTrue(
                ApaAssetDatabaseUtility.IsSameAssetAtPath(first, firstPath, null),
                "A write instance a reload detached is not evidence against a reference that resolves to the path.");

            Assert.IsFalse(
                ApaAssetDatabaseUtility.SameAsset(first, second),
                "Two payload assets are never the same asset.");
            Assert.IsFalse(ApaAssetDatabaseUtility.IsAssetAtPath(second, firstPath));
            Assert.IsFalse(
                ApaAssetDatabaseUtility.IsSameAssetAtPath(second, firstPath, first),
                "A reference to a different payload must fail the check even though the write instance is alive.");
            Assert.IsFalse(
                ApaAssetDatabaseUtility.IsSameAssetAtPath(first, firstPath, second),
                "A written instance that disagrees with the reference must fail the check.");
        }

        /// <summary>
        /// A transient instance is not an asset, so it can never satisfy the reference check — not even when it
        /// is the object the creation just built.
        /// </summary>
        [Test]
        public void PayloadIdentity_RejectsATransientInstance()
        {
            var rig = NewRig("Transient");
            var payloadPath = TempFolder + "/Transient.asset";
            WritePayload(rig, payloadPath);

            var payload = AssetDatabase.LoadAssetAtPath<ApaProtectedMeshAsset>(payloadPath);
            var transient = ScriptableObject.CreateInstance<ApaProtectedMeshAsset>();
            _created.Add(transient);
            Assert.IsNotNull(payload);

            Assert.IsFalse(
                ApaAssetDatabaseUtility.IsAssetAtPath(transient, payloadPath),
                "A transient instance is not the asset at the path.");
            Assert.IsFalse(ApaAssetDatabaseUtility.SameAsset(transient, payload));
            Assert.IsFalse(ApaAssetDatabaseUtility.IsSameAssetAtPath(transient, payloadPath, payload));
            Assert.IsFalse(ApaAssetDatabaseUtility.SameAsset(payload, null));
            Assert.IsFalse(ApaAssetDatabaseUtility.IsAssetAtPath(payload, null));
        }

        // ---- Fixtures ----------------------------------------------------------------------------------

        private sealed class Rig
        {
            public ApaAuthoringSelection Selection;
            public ApaPartProfile Profile;
            public GameObject Avatar;
            public GameObject PartRoot;
            public Renderer PartRenderer;
            public Mesh SourceMesh;
            public MeshSnapshot Snapshot;
        }

        /// <summary>Writes a real payload asset for the rig's part mesh at a path.</summary>
        private void WritePayload(Rig rig, string assetPath)
        {
            Assert.IsTrue(
                ApaProtectedMeshCodec.TryCreatePayload(rig.Snapshot, "part-a", out var payload, out var issue),
                issue != null ? issue.Message : "payload creation failed");

            var write = ApaProtectedMeshAssetWriter.Create(assetPath, payload, payload.SourceFingerprint);
            Assert.IsTrue(write.Succeeded, write.Message);
        }

        /// <summary>
        /// A minimal authoring rig: an avatar root, a body renderer with a readable runtime mesh, a part root
        /// whose renderer reads a mesh asset on disk, and a persistent profile asset.
        /// </summary>
        private Rig NewRig(string name)
        {
            var avatar = NewGameObject(name + " Avatar", null);

            var bodyMesh = NewRuntimeMesh(name + " BodyMesh");
            var body = NewGameObject(name + " Body", avatar.transform);
            body.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
            body.AddComponent<MeshRenderer>();

            var partRoot = NewGameObject(name + " Part", avatar.transform);
            var sourceMesh = NewMeshAsset(name + " SourceMesh");
            partRoot.AddComponent<MeshFilter>().sharedMesh = sourceMesh;
            var partRenderer = partRoot.AddComponent<MeshRenderer>();

            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);
            profile.Identity.PartId = "part-a";
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(bodyMesh, name + " Body", string.Empty);
            AssetDatabase.CreateAsset(profile, TempFolder + "/" + name + "Profile.asset");
            AssetDatabase.SaveAssets();

            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = avatar,
                PartRoot = partRoot,
                PartRenderer = partRenderer,
                OutputFolder = TempFolder
            };

            return new Rig
            {
                Selection = selection,
                Profile = profile,
                Avatar = avatar,
                PartRoot = partRoot,
                PartRenderer = partRenderer,
                SourceMesh = sourceMesh,
                Snapshot = SnapshotOf(sourceMesh)
            };
        }

        private GameObject NewGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            _created.Add(gameObject);
            return gameObject;
        }

        private Mesh NewRuntimeMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
            mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            _created.Add(mesh);
            return mesh;
        }

        private Mesh NewMeshAsset(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
            mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };

            var path = TempFolder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        private static MeshSnapshot SnapshotOf(Mesh mesh)
        {
            return MeshSnapshotFactory.Capture(mesh, null, out _);
        }

        private static Mesh SharedMeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null ? filter.sharedMesh : null;
        }

        private static void SharedMeshSetter(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                skinned.sharedMesh = mesh;
                return;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = mesh;
        }
    }
}

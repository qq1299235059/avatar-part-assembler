using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the package-version stamp a profile write records: what the asset gets, what the draft the write
    /// was given gets, and what a write that did not happen leaves alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are the tests behind the reported regression.</b> An author loads a profile written by an older
    /// release, saves it, and immediately presses "Create Part Prefab". The prefab step refuses a draft that
    /// differs from the saved profile, so a write that stamps the asset but leaves the draft's own record alone
    /// blocks the author on a profile that was just saved. The divergence is produced by the write itself, so it
    /// is reproduced here through the real writer on a real asset rather than by asserting the shape of the code
    /// that fixes it.
    /// </para>
    /// <para>
    /// The comparison the prefab step makes is <see cref="ApaProfileWriter.HasUnsavedChanges"/>, which is called
    /// with exactly the asset and the draft these tests use; the last assertion of each scenario is that call.
    /// </para>
    /// <para>
    /// Every test runs inside one temporary project folder that is removed afterwards, and the transient objects
    /// it creates are destroyed in teardown.
    /// </para>
    /// </remarks>
    public sealed class AuthoringProfileVersionStampTests
    {
        private const string TempFolder = "Assets/APAVersionStampTests";

        /// <summary>A stamp no build of this package can be running, standing in for an older release.</summary>
        private const string OlderStamp = "0.0.1-legacy";

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "APAVersionStampTests");
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
        }

        /// <summary>
        /// The reported sequence: load a profile written by an older release, save it, then let the prefab step
        /// look at the two. The draft and the asset must agree once the save returns.
        /// </summary>
        /// <remarks>
        /// This is the test that fails without the fix, and it fails on the exact call the window makes: the
        /// writer stamps the installed version onto the asset, so a draft that kept the older stamp no longer
        /// describes the profile on disk.
        /// </remarks>
        [Test]
        public void Save_AfterLoadingAnOlderProfile_LeavesTheDraftMatchingTheAsset()
        {
            // Without a readable manifest the writer stamps nothing, so the divergence this test reproduces
            // cannot arise; the sync rule itself is covered by the two tests below.
            Assume.That(
                ApaPackageVersion.IsKnown,
                "The installed package version is unreadable here, so a write records no stamp at all.");

            var path = TempFolder + "/LegacyProfile.asset";
            var created = ApaProfileWriter.Save(NewWritableDraft("Legacy"), path, false);
            Assert.AreEqual(ApaProfileWriteStatus.Created, created.Status, created.Message);

            // Make the asset look like one written by an older release: it records a stamp this build disagrees
            // with, and the draft loaded from it carries that same stamp.
            var asset = AssetDatabase.LoadAssetAtPath<ApaPartProfile>(path);
            Assert.IsNotNull(asset, "The profile the writer created must exist at " + path + ".");
            asset.ApaPackageVersion = OlderStamp;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            var loaded = ApaProfileDraft.FromProfile(asset);
            Assert.AreEqual(OlderStamp, loaded.ApaPackageVersion, "The draft carries the loaded profile's stamp.");
            Assert.IsFalse(
                ApaProfileWriter.HasUnsavedChanges(asset, loaded),
                "A draft that was loaded and not edited describes the asset it came from.");

            var saved = ApaProfileWriter.Save(loaded, path, true);
            Assert.AreEqual(ApaProfileWriteStatus.Updated, saved.Status, saved.Message);
            Assert.AreEqual(
                ApaPackageVersion.Current,
                saved.WrittenApaPackageVersion,
                "The result must report the stamp the written asset carries.");

            // Read the file back rather than trusting the instance the writer updated: the claim is about what a
            // later Editor session loads.
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<ApaPartProfile>(path);
            Assert.IsNotNull(reloaded);
            Assert.AreEqual(
                ApaPackageVersion.Current,
                reloaded.ApaPackageVersion,
                "A successful write records the installed package version on the asset.");

            Assert.AreEqual(
                reloaded.ApaPackageVersion,
                loaded.ApaPackageVersion,
                "The draft must describe the profile that was written from it.");

            // The regression, on the call the prefab step makes.
            Assert.IsFalse(
                ApaProfileWriter.HasUnsavedChanges(reloaded, loaded),
                "A profile that was just saved must not look unsaved to 'Create Part Prefab'.");

            // The sync removes a difference the author did not make; it must not remove one they did.
            loaded.Removal.Add(new RemovedTriangleAddress(0, 1));
            Assert.IsTrue(
                ApaProfileWriter.HasUnsavedChanges(reloaded, loaded),
                "An edit made after the save is still an unsaved change.");
        }

        /// <summary>The same divergence without a project folder: the rule that removes it, on real objects.</summary>
        /// <remarks>
        /// This half needs no AssetDatabase, so it states the sync rule even in an environment where the
        /// manifest cannot be read: the asset carries the stamp of the write, the draft carries the one it was
        /// loaded with, the comparison reports the difference, and adopting the written stamp removes exactly
        /// that difference.
        /// </remarks>
        [Test]
        public void HasUnsavedChanges_StopsReportingAProfileThatWasJustStamped()
        {
            var asset = Track(NewWritableDraft("Stamped").Materialize());
            asset.ApaPackageVersion = "9.9.9";

            var loaded = ApaProfileDraft.FromProfile(asset);
            loaded.ApaPackageVersion = OlderStamp;
            Assert.IsTrue(
                ApaProfileWriter.HasUnsavedChanges(asset, loaded),
                "The stamp difference alone is what the prefab step reports.");

            Assert.IsTrue(
                ApaProfileWriter.AdoptWrittenPackageVersion(loaded, asset),
                "Adopting the written asset's stamp changes the draft.");
            Assert.AreEqual("9.9.9", loaded.ApaPackageVersion);
            Assert.IsFalse(
                ApaProfileWriter.HasUnsavedChanges(asset, loaded),
                "Once the draft describes the written profile, the two are the same profile again.");

            // Only content the author can edit may make the two differ; the comparison is not weakened.
            loaded.Identity.DisplayName = "Edited After Saving";
            Assert.IsTrue(ApaProfileWriter.HasUnsavedChanges(asset, loaded));
        }

        /// <summary>Adopting a stamp is a copy of what the asset records, and a no-op when there is nothing to do.</summary>
        [Test]
        public void AdoptWrittenPackageVersion_CopiesTheWrittenStampAndIgnoresMissingInputs()
        {
            var written = Track(ScriptableObject.CreateInstance<ApaPartProfile>());
            written.ApaPackageVersion = "1.2.3";

            var draft = new ApaProfileDraft();
            Assert.AreEqual(string.Empty, draft.ApaPackageVersion);

            Assert.IsTrue(ApaProfileWriter.AdoptWrittenPackageVersion(draft, written));
            Assert.AreEqual("1.2.3", draft.ApaPackageVersion);

            Assert.IsFalse(
                ApaProfileWriter.AdoptWrittenPackageVersion(draft, written),
                "A draft that already records the written stamp is not changed again.");
            Assert.AreEqual("1.2.3", draft.ApaPackageVersion);

            Assert.IsFalse(ApaProfileWriter.AdoptWrittenPackageVersion(null, written));
            Assert.IsFalse(ApaProfileWriter.AdoptWrittenPackageVersion(draft, null));
            Assert.AreEqual("1.2.3", draft.ApaPackageVersion, "Missing inputs change nothing.");
        }

        /// <summary>Creating a profile stamps the new asset and the draft it was written from.</summary>
        [Test]
        public void Create_StampsTheNewAssetAndTheDraftItWasWrittenFrom()
        {
            var path = TempFolder + "/CreatedProfile.asset";
            var draft = NewWritableDraft("Created");
            Assert.AreEqual(string.Empty, draft.ApaPackageVersion, "A new draft records no version.");

            var result = ApaProfileWriter.Save(draft, path, false);
            Assert.AreEqual(ApaProfileWriteStatus.Created, result.Status, result.Message);

            var asset = AssetDatabase.LoadAssetAtPath<ApaPartProfile>(path);
            Assert.IsNotNull(asset);
            Assert.AreEqual(result.WrittenApaPackageVersion, asset.ApaPackageVersion);
            Assert.AreEqual(
                asset.ApaPackageVersion,
                draft.ApaPackageVersion,
                "The draft describes the asset the write produced.");
            Assert.IsFalse(
                ApaProfileWriter.HasUnsavedChanges(asset, draft),
                "A profile that was just created must not look unsaved to 'Create Part Prefab'.");
        }

        /// <summary>
        /// A write the author cancels at the overwrite prompt writes nothing, so it may not update the draft
        /// either — including the case where the draft's stamp differs from the asset's.
        /// </summary>
        [Test]
        public void RefusedOverwrite_LeavesTheDraftAndTheAssetAlone()
        {
            var path = TempFolder + "/RefusedProfile.asset";
            var created = ApaProfileWriter.Save(NewWritableDraft("Refused"), path, false);
            Assert.AreEqual(ApaProfileWriteStatus.Created, created.Status, created.Message);

            var asset = AssetDatabase.LoadAssetAtPath<ApaPartProfile>(path);
            Assert.IsNotNull(asset);
            var assetStamp = asset.ApaPackageVersion;

            // The author's state when the prompt appears: a draft that carries a different stamp from the asset
            // (because it was loaded from an older profile) and an edit that made it differ.
            var loaded = ApaProfileDraft.FromProfile(asset);
            loaded.ApaPackageVersion = OlderStamp;
            loaded.Removal.Add(new RemovedTriangleAddress(0, 1));

            var refused = ApaProfileWriter.Save(loaded, path, false);
            Assert.AreEqual(ApaProfileWriteStatus.RefusedOverwrite, refused.Status, refused.Message);
            Assert.IsTrue(refused.RequiresOverwriteConfirmation, "The author must be asked before the asset is replaced.");
            Assert.AreEqual(asset, refused.Asset, "A refused overwrite reports the asset it found.");
            Assert.AreEqual(
                string.Empty,
                refused.WrittenApaPackageVersion,
                "Nothing was written, so the result may not report a stamp the asset was never given.");

            Assert.AreEqual(OlderStamp, loaded.ApaPackageVersion, "A refused write must not touch the draft.");
            Assert.AreEqual(assetStamp, asset.ApaPackageVersion, "A refused write must not touch the asset.");
            Assert.IsTrue(
                ApaProfileWriter.HasUnsavedChanges(asset, loaded),
                "The difference the author still has is still reported.");
        }

        /// <summary>A draft the writer refuses as invalid never reaches the asset, and never reaches the draft.</summary>
        [Test]
        public void RefusedInvalidDraft_LeavesTheDraftAlone()
        {
            var path = TempFolder + "/InvalidProfile.asset";
            var draft = new ApaProfileDraft();
            draft.ApaPackageVersion = OlderStamp;

            var refused = ApaProfileWriter.Save(draft, path, true);
            Assert.AreEqual(ApaProfileWriteStatus.RefusedInvalidDraft, refused.Status, refused.Message);
            Assert.AreEqual(string.Empty, refused.WrittenApaPackageVersion);
            Assert.AreEqual(OlderStamp, draft.ApaPackageVersion, "A refused write must not touch the draft.");
            Assert.IsNull(
                AssetDatabase.LoadMainAssetAtPath(path),
                "A refused draft must not leave an asset behind.");
        }

        /// <summary>A draft the writer accepts: a stable part id and a complete captured target signature.</summary>
        private ApaProfileDraft NewWritableDraft(string name)
        {
            var mesh = Track(new Mesh { name = name + " BodyMesh" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
            mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };

            var draft = ApaProfileDraft.New(name, ApaPartSlot.Custom);
            draft.Compatibility = MeshSnapshotFactory.CaptureSignature(mesh, name + " Body", string.Empty);
            draft.PartMeshFingerprint = ApaMeshFingerprint.OfMesh(mesh);
            return draft;
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            _created.Add(value);
            return value;
        }
    }
}

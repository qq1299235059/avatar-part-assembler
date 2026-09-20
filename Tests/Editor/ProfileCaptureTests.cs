using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the authoring-time compatibility capture: the path convention, what a captured signature
    /// records, and the completeness decision the writer enforces.
    /// </summary>
    /// <remarks>
    /// The capture of mesh data itself belongs to <see cref="MeshSnapshotFactory"/> and is covered by the core
    /// compatibility tests. What is asserted here is the authoring half: the canonical renderer path, and the
    /// refusal to treat an uncaptured or incomplete signature as usable.
    /// </remarks>
    public sealed class ProfileCaptureTests
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
        public void ResolveRendererPath_UsesTheCanonicalAvatarRootConvention()
        {
            var avatarRoot = Track(new GameObject("Avatar"));
            var body = Track(new GameObject("Body"));
            var armature = Track(new GameObject("Armature"));
            var hips = Track(new GameObject("Hips"));

            armature.transform.SetParent(avatarRoot.transform, false);
            hips.transform.SetParent(armature.transform, false);
            body.transform.SetParent(avatarRoot.transform, false);

            var bodyRenderer = body.AddComponent<MeshRenderer>();
            var hipsRenderer = hips.AddComponent<MeshRenderer>();
            var rootRenderer = avatarRoot.AddComponent<MeshRenderer>();

            // The avatar root is a valid identity and records the canonical token, never the empty string.
            Assert.AreEqual(ApaAvatarPath.Root, ApaCompatibilityCapture.ResolveRendererPath(avatarRoot, rootRenderer));
            Assert.AreEqual("Body", ApaCompatibilityCapture.ResolveRendererPath(avatarRoot, bodyRenderer));
            Assert.AreEqual("Armature/Hips", ApaCompatibilityCapture.ResolveRendererPath(avatarRoot, hipsRenderer));

            // A missing renderer records the empty string, which means "missing" and is never resolved.
            Assert.AreEqual(string.Empty, ApaCompatibilityCapture.ResolveRendererPath(avatarRoot, null));
        }

        [Test]
        public void ResolveRendererPath_WithoutAnAvatarRootRecordsAScenePath()
        {
            var sceneRoot = Track(new GameObject("SceneRoot"));
            var body = Track(new GameObject("Body"));
            body.transform.SetParent(sceneRoot.transform, false);
            var renderer = body.AddComponent<MeshRenderer>();

            // No root means the path is built from the scene root. The authoring selection reports this as a
            // blocking defect, because such a path stops resolving once the part is installed elsewhere.
            Assert.AreEqual("SceneRoot/Body", ApaCompatibilityCapture.ResolveRendererPath(null, renderer));
        }

        [Test]
        public void ResolveMeshGuid_ForARuntimeMeshIsEmpty()
        {
            var mesh = Track(CreateTriangleMesh("Runtime Mesh", 1));

            Assert.AreEqual(string.Empty, ApaCompatibilityCapture.ResolveMeshGuid(mesh));
        }

        [Test]
        public void CaptureFrom_RecordsEverySafetyFieldOfTheSchemaV2Signature()
        {
            var mesh = Track(CreateTriangleMesh("Body", 2));

            var signature = ApaCompatibilityCapture.CaptureFrom(
                mesh, "Body", "0123456789abcdef", new BoneSignature(new[] { "Armature/Hips" }));

            Assert.IsTrue(signature.IsCaptured);
            Assert.AreEqual("Body", signature.RendererPath);
            Assert.AreEqual("0123456789abcdef", signature.MeshGuid);
            Assert.AreEqual(mesh.vertexCount, signature.VertexCount);
            Assert.AreEqual(2, signature.SubMeshIndexCounts.Length);
            Assert.AreEqual(2, signature.SubMeshTopologyValues.Length);
            Assert.AreEqual((int)MeshTopology.Triangles, signature.SubMeshTopologyValues[0]);
            Assert.IsTrue(signature.HasSubMeshTopologies);
            Assert.IsTrue(signature.HasBlendShapeFrameCounts);
            Assert.IsTrue(signature.HasBonePaths);
            Assert.IsTrue(signature.HasCompleteSafetyData);
            Assert.IsEmpty(ApaCompatibilityCapture.ValidateCapturedProfile(signature, mesh));
        }

        [Test]
        public void Capture_WithoutATargetReportsTheReasonAndReturnsAnUncapturedSignature()
        {
            var avatarRoot = Track(new GameObject("Avatar"));

            var signature = ApaCompatibilityCapture.Capture(avatarRoot, null, out var issues);

            Assert.IsFalse(signature.IsCaptured);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, issues[0].Code);
            Assert.AreEqual("reason=missing-target-renderer", issues[0].Detail);
        }

        [Test]
        public void Capture_ReportsATargetOutsideTheAvatarRootAsNonPortable()
        {
            var avatarRoot = Track(new GameObject("Avatar"));
            var elsewhere = Track(new GameObject("Elsewhere"));
            var body = Track(new GameObject("Body"));
            body.transform.SetParent(elsewhere.transform, false);

            var renderer = body.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = Track(CreateTriangleMesh("Body", 1));

            var signature = ApaCompatibilityCapture.Capture(avatarRoot, renderer, out var issues);

            Assert.IsTrue(signature.IsCaptured, "The capture still reports what it recorded.");
            Assert.AreEqual("Elsewhere/Body", signature.RendererPath);

            var portableIssue = FindDetail(issues, ApaErrorCode.TargetRendererNotFound,
                "reason=target-renderer-outside-avatar-root");
            Assert.IsNotNull(portableIssue, "A scene-absolute renderer path must be reported as non-portable.");
        }

        [Test]
        public void Capture_OnTheAvatarRootRecordsTheRootTokenAndResolves()
        {
            var avatarRoot = Track(new GameObject("Avatar"));
            var renderer = avatarRoot.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = Track(CreateTriangleMesh("Body", 1));

            var signature = ApaCompatibilityCapture.Capture(avatarRoot, renderer, out var issues);

            Assert.AreEqual(ApaAvatarPath.Root, signature.RendererPath);
            Assert.IsEmpty(issues);
        }

        [Test]
        public void ValidateCapturedProfile_RefusesAnUncapturedSignatureWithTheCoreCode()
        {
            var issues = ApaCompatibilityCapture.ValidateCapturedProfile(
                new ApaAvatarCompatibilityProfile(), Track(CreateTriangleMesh("Body", 1)));

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.PartProfileIncompatible, issues[0].Code);
            Assert.AreEqual("reason=signature-not-captured", issues[0].Detail);
        }

        [Test]
        public void ValidateCapturedProfile_RefusesAnIncompleteSignature()
        {
            // An empty mesh produces a signature with no submesh index counts, which is exactly the shape of a
            // capture that cannot prove anything about stored topology indices.
            var mesh = Track(CreateTriangleMesh("Empty", 0));
            var signature = ApaCompatibilityCapture.CaptureFrom(mesh, "Body", string.Empty, null);

            Assert.IsFalse(signature.HasCompleteSafetyData);

            var issues = ApaCompatibilityCapture.ValidateCapturedProfile(signature, mesh);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.IncompleteCompatibilitySignature, issues[0].Code);
            Assert.AreEqual("APA024", issues[0].Code);
            StringAssert.Contains("missing=", issues[0].Detail);
        }

        [Test]
        public void DescribeMissing_ListsTheAbsentSafetyFields()
        {
            Assert.AreEqual("capture flag, vertex count, submesh index counts, submesh topologies, mesh fingerprint",
                ApaCompatibilityCapture.DescribeMissing(new ApaAvatarCompatibilityProfile()));
        }

        [Test]
        public void NeedsRecapture_DetectsAChangedOrDifferentTarget()
        {
            var mesh = Track(CreateTriangleMesh("Body", 1));
            var signature = ApaCompatibilityCapture.CaptureFrom(mesh, "Body", string.Empty, null);

            Assert.IsFalse(ApaCompatibilityCapture.NeedsRecapture(signature, mesh, "Body", string.Empty));
            Assert.IsTrue(ApaCompatibilityCapture.NeedsRecapture(signature, mesh, "Renamed", string.Empty),
                "A different renderer path requires a recapture.");
            Assert.IsTrue(ApaCompatibilityCapture.NeedsRecapture(signature, mesh, "Body", "guid"),
                "A newly available mesh GUID requires a recapture.");
            Assert.IsTrue(ApaCompatibilityCapture.NeedsRecapture(new ApaAvatarCompatibilityProfile(), mesh, "Body", string.Empty),
                "An uncaptured signature always requires a capture.");
        }

        [Test]
        public void SafetyFieldsMatch_ComparesVertexCountTopologyAndBlendShapes()
        {
            var mesh = Track(CreateTriangleMesh("Body", 2));
            var signature = ApaCompatibilityCapture.CaptureFrom(mesh, "Body", string.Empty, null);

            Assert.IsTrue(ApaCompatibilityCapture.SafetyFieldsMatch(signature, mesh));

            signature.VertexCount = mesh.vertexCount + 1;
            Assert.IsFalse(ApaCompatibilityCapture.SafetyFieldsMatch(signature, mesh));
        }

        [Test]
        public void SafetyFieldsDiffer_SeparatesWhatBlocksFromWhatIsAdvisory()
        {
            var mesh = Track(CreateTriangleMesh("Body", 2));
            var signature = ApaCompatibilityCapture.CaptureFrom(mesh, "Body", string.Empty, null);

            Assert.IsFalse(ApaCompatibilityCapture.SafetyFieldsDiffer(signature, mesh),
                "An unchanged target does not differ.");

            // A renamed path and a newly available mesh GUID are the two differences CompatibilityRule never
            // compares: they are advisory, not a build block. This is exactly the split the window's staleness
            // message needs, because the old single predicate promised a build failure for a renderer rename.
            Assert.IsTrue(ApaCompatibilityCapture.IdentityFieldsDiffer(signature, mesh, "Renamed", string.Empty));
            Assert.IsTrue(ApaCompatibilityCapture.IdentityFieldsDiffer(signature, mesh, "Body", "guid"));
            Assert.IsFalse(ApaCompatibilityCapture.SafetyFieldsDiffer(signature, mesh),
                "An identity difference is not a safety difference.");
            Assert.IsTrue(ApaCompatibilityCapture.NeedsRecapture(signature, mesh, "Renamed", string.Empty),
                "An advisory difference still offers a recapture.");

            signature.VertexCount = mesh.vertexCount + 1;
            Assert.IsTrue(ApaCompatibilityCapture.SafetyFieldsDiffer(signature, mesh),
                "A changed vertex count is a safety difference and does block.");
        }

        [Test]
        public void StalenessPredicates_ReportNothingWithoutACapturedSignatureOrAMesh()
        {
            var mesh = Track(CreateTriangleMesh("Body", 1));
            var uncaptured = new ApaAvatarCompatibilityProfile();

            Assert.IsFalse(ApaCompatibilityCapture.SafetyFieldsDiffer(uncaptured, mesh));
            Assert.IsFalse(ApaCompatibilityCapture.IdentityFieldsDiffer(uncaptured, mesh, "Body", string.Empty));
            Assert.IsFalse(ApaCompatibilityCapture.SafetyFieldsDiffer(
                ApaCompatibilityCapture.CaptureFrom(mesh, "Body", string.Empty, null), null));
            Assert.IsFalse(ApaCompatibilityCapture.IdentityFieldsDiffer(
                ApaCompatibilityCapture.CaptureFrom(mesh, "Body", string.Empty, null), null, "Body", string.Empty));
        }

        [Test]
        public void Capture_ReportsAnUnreadableTargetMeshInsteadOfReadingIt()
        {
            var avatarRoot = Track(new GameObject("Avatar"));
            var body = Track(new GameObject("Body"));
            body.transform.SetParent(avatarRoot.transform, false);

            var renderer = body.AddComponent<SkinnedMeshRenderer>();
            var mesh = Track(CreateTriangleMesh("Body", 1));
            mesh.UploadMeshData(true);
            Assume.That(mesh.isReadable, Is.False,
                "UploadMeshData(true) must leave the mesh unreadable for this test to mean anything.");
            renderer.sharedMesh = mesh;

            var signature = ApaCompatibilityCapture.Capture(avatarRoot, renderer, out var issues);

            // The readability check runs before the signature read, so the author gets the actionable
            // diagnostic (and no Unity error output from reading an unreadable mesh) instead of a read that
            // fails first and is explained afterwards.
            Assert.IsFalse(signature.IsCaptured);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.UnsupportedMeshAttribute, issues[0].Code);
            Assert.AreEqual("mesh=Body; reason=not-readable", issues[0].Detail);
        }

        [Test]
        public void MeshHasUvChannel_FollowsTheSnapshotRuleOnALiveMesh()
        {
            var mesh = Track(CreateTriangleMesh("Part", 1));

            Assert.IsFalse(ApaAuthoringValidation.MeshHasUvChannel(mesh, 0), "A mesh without UV0 reports it absent.");
            Assert.IsFalse(ApaAuthoringValidation.MeshHasUvChannel(mesh, 1), "A channel outside 0..7 is absent.");
            Assert.IsFalse(ApaAuthoringValidation.MeshHasUvChannel(mesh, -1), "A negative channel is absent.");
            Assert.IsFalse(ApaAuthoringValidation.MeshHasUvChannel(null, 0), "A null mesh has no channels.");

            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f)
            });

            Assert.IsTrue(ApaAuthoringValidation.MeshHasUvChannel(mesh, 0), "A full UV0 channel is present.");
            Assert.IsFalse(ApaAuthoringValidation.MeshHasUvChannel(mesh, 1), "UV1 is still absent.");
        }

        [Test]
        public void ValidateDraftDataAgainstMesh_RefusesADeclaredChannelTheLiveMeshDoesNotCarry()
        {
            var mesh = Track(CreateTriangleMesh("Part", 1));
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };

            var issues = ApaAuthoringValidation.ValidateDraftDataAgainstMesh(draft, mesh);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaAuthoringErrorCode.UvSemanticChannelAbsent, issues[0].Code);
            StringAssert.Contains("reason=channel-not-present", issues[0].Detail);

            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f)
            });

            Assert.IsEmpty(ApaAuthoringValidation.ValidateDraftDataAgainstMesh(draft, mesh));
        }

        /// <summary>
        /// Builds a readable triangle-list mesh with the requested number of submeshes. A runtime mesh is
        /// readable, which is the same requirement the assembly core enforces on an authored part.
        /// </summary>
        private static Mesh CreateTriangleMesh(string name, int subMeshCount)
        {
            var mesh = new Mesh { name = name };

            if (subMeshCount <= 0) return mesh;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (var subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                var baseIndex = vertices.Count;
                vertices.Add(new Vector3(subMesh, 0f, 0f));
                vertices.Add(new Vector3(subMesh, 1f, 0f));
                vertices.Add(new Vector3(subMesh, 0f, 1f));
                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);
            }

            mesh.SetVertices(vertices);
            mesh.subMeshCount = subMeshCount;
            for (var subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                mesh.SetTriangles(new[] { subMesh * 3, subMesh * 3 + 1, subMesh * 3 + 2 }, subMesh);
            }

            return mesh;
        }

        private static ValidationIssue FindDetail(List<ValidationIssue> issues, string code, string detailFragment)
        {
            for (var i = 0; i < issues.Count; i++)
            {
                if (!string.Equals(issues[i].Code, code, System.StringComparison.Ordinal)) continue;
                if (issues[i].Detail.IndexOf(detailFragment, System.StringComparison.Ordinal) >= 0) return issues[i];
            }

            return null;
        }

        private T Track<T>(T value) where T : Object
        {
            _created.Add(value);
            return value;
        }
    }
}

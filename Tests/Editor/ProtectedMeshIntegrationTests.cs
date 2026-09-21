using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using AvatarPartAssembler.Editor.Preview;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Protected
{
    /// <summary>
    /// Tests that a protected part is a normal part to everything that reads geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The protected feature changes what a <i>prefab</i> stores, not what the pipeline assembles: the payload is
    /// decoded in memory and the resulting snapshot is the same snapshot an unprotected capture would have
    /// produced. These tests are the ones that hold that promise at the boundaries where it is easiest to break —
    /// the context builder, the preview discovery pass, and the authoring selection with its Scene View overlays.
    /// </para>
    /// <para>
    /// The regression they exist for is precise: a protected prefab's renderer has no mesh, so any code path that
    /// answers "is this a usable part?" from <c>sharedMesh</c> alone reports <c>APA006</c> against a part that is
    /// perfectly good. Each test therefore asserts both directions — the protected part is accepted, and a part
    /// that really has no geometry is still refused with the actionable diagnostic.
    /// </para>
    /// </remarks>
    public sealed class ProtectedMeshIntegrationTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            ApaProtectedMeshCache.Clear();
            ApaProtectedMeshCache.ResetStatistics();
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
        }

        // ---- context builder -------------------------------------------------------------------------

        /// <summary>
        /// The exact reported scenario: a protected prefab's renderer has no mesh, and the payload is the
        /// geometry. The context must build, carry the payload's geometry, and report no APA006.
        /// </summary>
        [Test]
        public void Build_ProtectedPartWithNoLiveMesh_ProducesAPartSnapshotAndNoApa006()
        {
            var rig = CreateRig();
            Protect(rig);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNotNull(context, Describe(issues));
            Assert.AreEqual(1, context.Parts.Count, Describe(issues));

            var part = context.FindPart("part-a");
            Assert.IsNotNull(part, Describe(issues));
            Assert.AreEqual(
                rig.SourceMesh.vertexCount,
                part.Mesh.VertexCount,
                "The part snapshot must carry the payload's geometry.");
            Assert.AreEqual(rig.SourceMesh.subMeshCount, part.Mesh.SubMeshCount);

            Assert.IsFalse(
                HasCode(issues, ApaErrorCode.TargetRendererNotFound),
                "A valid protected part must not be reported as a missing target renderer." + Describe(issues));
            Assert.IsFalse(HasCode(issues, ApaErrorCode.ProtectedMeshInvalid), Describe(issues));
        }

        /// <summary>
        /// The decode is shared: a second capture of the same unchanged payload reuses the cached data rather
        /// than paying for the key derivation and the decryption again (R8).
        /// </summary>
        [Test]
        public void Build_ProtectedPart_DecodesOncePerPayloadIdentity()
        {
            var rig = CreateRig();
            Protect(rig);

            ContextBuilder.Build(rig.Avatar, null, out _);
            var afterFirst = ApaProtectedMeshCache.DecodeCount;

            ContextBuilder.Build(rig.Avatar, null, out _);

            Assert.AreEqual(1, afterFirst, "The first capture must decode the payload exactly once.");
            Assert.AreEqual(
                1,
                ApaProtectedMeshCache.DecodeCount,
                "A second capture of an unchanged payload must be served from the cache.");
            Assert.GreaterOrEqual(ApaProtectedMeshCache.CacheHitCount, 1);
        }

        /// <summary>
        /// The other direction: an ordinary part with no mesh and no payload keeps the actionable APA006.
        /// </summary>
        [Test]
        public void Build_MeshLessUnprotectedPart_StillReportsApa006()
        {
            var rig = CreateRig();
            ClearPartMesh(rig);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNull(context, Describe(issues));

            var blocking = issues.FindAll(issue => issue.Code == ApaErrorCode.TargetRendererNotFound);
            Assert.AreEqual(1, blocking.Count, Describe(issues));
            StringAssert.Contains("has no mesh assigned", blocking[0].Message);
            StringAssert.Contains("protected mode", blocking[0].Message);
            StringAssert.Contains("reason=missing-part-mesh", blocking[0].Detail);
            Assert.IsFalse(
                HasCode(issues, ApaErrorCode.ProtectedMeshInvalid),
                "A part with no payload must not be reported as a protected-payload failure." + Describe(issues));
        }

        /// <summary>
        /// A tampered payload fails closed with the protected diagnostic, and produces no context at all — there
        /// is no fallback to the null renderer, and no partial geometry.
        /// </summary>
        [Test]
        public void Build_TamperedPayload_FailsClosedWithApa053()
        {
            var rig = CreateRig();
            Protect(rig, tamperCiphertext: true);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNull(context, Describe(issues));

            var blocking = issues.FindAll(issue => issue.Code == ApaErrorCode.ProtectedMeshInvalid);
            Assert.AreEqual(1, blocking.Count, Describe(issues));
            StringAssert.Contains("reason=" + ApaProtectedMeshReasons.AuthenticationFailed, blocking[0].Detail);
            Assert.IsFalse(
                HasCode(issues, ApaErrorCode.TargetRendererNotFound),
                "A protected part with a bad payload must not also be reported as an ordinary mesh-less part." +
                Describe(issues));
        }

        /// <summary>
        /// A payload that authenticates but was written for another part is refused, so a copied asset cannot be
        /// assembled into the wrong part.
        /// </summary>
        [Test]
        public void Build_PayloadForAnotherPart_FailsClosedWithApa053()
        {
            var rig = CreateRig();
            Protect(rig, payloadPartId: "part-somewhere-else");

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNull(context, Describe(issues));

            var blocking = issues.FindAll(issue => issue.Code == ApaErrorCode.ProtectedMeshInvalid);
            Assert.AreEqual(1, blocking.Count, Describe(issues));
            StringAssert.Contains("reason=" + ApaProtectedMeshReasons.PartIdMismatch, blocking[0].Detail);
        }

        // ---- preview discovery -----------------------------------------------------------------------

        /// <summary>
        /// The preview must produce a renderable request for a protected part. A request that is not renderable
        /// is exactly what "the preview draws nothing and reports APA006" looks like from the outside.
        /// </summary>
        [Test]
        public void DiscoverGroups_ProtectedPart_ProducesARenderableRequest()
        {
            var rig = CreateRig();
            Protect(rig);

            var groups = ApaPreviewDiscovery.DiscoverGroups(rig.Avatar, null);

            Assert.AreEqual(1, groups.Count);
            Assert.IsTrue(groups[0].IsRenderable, groups[0].ToString());
            Assert.IsNotNull(groups[0].Context, "The protected part must contribute a captured context.");
            Assert.IsNotNull(groups[0].Context.FindPart("part-a"));
            Assert.IsFalse(
                HasCode(groups[0].Issues.Issues, ApaErrorCode.TargetRendererNotFound),
                groups[0].Issues.FormatAll());
        }

        /// <summary>
        /// A payload edit changes the request's geometry identity, which is what makes the preview rebuild the
        /// node instead of drawing the geometry the asset no longer describes.
        /// </summary>
        [Test]
        public void DiscoverGroups_RepublishedPayload_ChangesTheGeometryIdentity()
        {
            var rig = CreateRig();
            Protect(rig);

            var before = ApaPreviewDiscovery.DiscoverGroups(rig.Avatar, null)[0];

            // A different mesh behind the same installer: the payload is replaced, the renderer stays mesh-less.
            rig.SourceMesh = NewRingMesh("PartMesh2", new Vector3(0.75f, 0f, 0f));
            Reprotect(rig);

            var after = ApaPreviewDiscovery.DiscoverGroups(rig.Avatar, null)[0];

            Assert.AreNotEqual(
                before.GeometryFingerprint,
                after.GeometryFingerprint,
                "Replacing the payload must change the geometry the preview would assemble.");
        }

        // ---- authoring selection and its overlays ----------------------------------------------------

        /// <summary>
        /// The authoring selection is what the Scene View overlays read. A protected part must pass its check and
        /// expose the decoded geometry, without anything being assigned to the scene renderer.
        /// </summary>
        [Test]
        public void Selection_ProtectedPart_PassesValidationAndExposesDecodedGeometry()
        {
            var rig = CreateRig();
            Protect(rig);

            var selection = NewSelection(rig);

            var issues = selection.Validate();
            Assert.IsFalse(
                HasCode(issues, ApaErrorCode.TargetRendererNotFound),
                "The selection check must accept a protected part." + Describe(issues));

            var geometry = selection.PartGeometryMesh;
            Assert.IsNotNull(geometry, "The overlays must be able to read the payload's geometry.");
            Assert.AreEqual(rig.SourceMesh.vertexCount, geometry.vertexCount);
            Assert.AreEqual(rig.SourceMesh.subMeshCount, geometry.subMeshCount);

            Assert.IsNull(
                SharedMeshOf(rig.PartRenderer),
                "The scene renderer must be left with no mesh: the decoded geometry is never assigned to it.");
            Assert.IsTrue(selection.HasProtectedMesh);
        }

        /// <summary>An ordinary mesh-less part keeps its APA006 in the selection check too.</summary>
        [Test]
        public void Selection_MeshLessUnprotectedPart_StillReportsApa006()
        {
            var rig = CreateRig();
            ClearPartMesh(rig);

            var selection = NewSelection(rig);
            var issues = selection.Validate();

            Assert.IsTrue(HasCode(issues, ApaErrorCode.TargetRendererNotFound), Describe(issues));
            Assert.IsNull(selection.PartGeometryMesh);
        }

        /// <summary>A corrupt payload is reported with the protected diagnostic, not as a missing mesh.</summary>
        [Test]
        public void Selection_TamperedPayload_ReportsApa053AndExposesNoGeometry()
        {
            var rig = CreateRig();
            Protect(rig, tamperCiphertext: true);

            var selection = NewSelection(rig);
            var issues = selection.Validate();

            Assert.IsTrue(HasCode(issues, ApaErrorCode.ProtectedMeshInvalid), Describe(issues));
            Assert.IsFalse(
                HasCode(issues, ApaErrorCode.TargetRendererNotFound),
                "The selection must name the payload, not the renderer, when the payload is the defect." +
                Describe(issues));
            Assert.IsNull(selection.PartGeometryMesh);
        }

        /// <summary>
        /// The transient decoded mesh is released on invalidation: the session contract is that nothing decoded
        /// outlives the state that asked for it.
        /// </summary>
        [Test]
        public void Selection_Invalidate_ReleasesTheTransientGeometry()
        {
            var rig = CreateRig();
            Protect(rig);

            var selection = NewSelection(rig);
            var geometry = selection.PartGeometryMesh;
            Assert.IsNotNull(geometry);

            selection.InvalidateProtectedGeometry();

            Assert.IsTrue(geometry == null, "Invalidating must destroy the transient decoded mesh.");
            Assert.IsNull(selection.PartGeometryMesh, "The geometry is rebuilt only when it is asked for again.");
            Assert.IsNotNull(selection.PartGeometryMesh, "A later read must rebuild the geometry.");
        }

        /// <summary>
        /// A protected part whose renderer is skinned: the overlays must read the payload's geometry <i>and</i>
        /// draw it where the renderer would draw it. The decoded mesh is never assigned to the renderer — the
        /// pose is solved in memory — so a protected part's candidate and merge-check discs land on the part
        /// instead of beside it, and nothing is written to the prefab.
        /// </summary>
        /// <remarks>
        /// The payload carries a blend shape as well as skinning, so the decoded transient mesh declares
        /// <c>blendShapeCount &gt; 0</c> while the renderer holds no mesh. That is the state in which asking the
        /// renderer for a weight would be a call outside <c>GetBlendShapeWeight</c>'s index contract, which is the
        /// risk this test pins: the evaluated-position cache must still read, cache, and reuse the solved pose.
        /// </remarks>
        [Test]
        public void Selection_ProtectedSkinnedPart_DrawsAtTheSolvedPoseWithoutAssigningTheMesh()
        {
            var avatar = NewGameObject("Avatar", null);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));

            var targetObject = NewGameObject("BodySkinned", avatar.transform);
            var target = targetObject.AddComponent<SkinnedMeshRenderer>();
            target.sharedMesh = bodyMesh;
            target.rootBone = avatar.transform;

            var bone = NewGameObject("Hips", avatar.transform).transform;
            bone.position = new Vector3(0f, 1f, 0f);

            // The part is skinned, and its renderer is saved without a mesh: the geometry is the payload.
            var partObject = NewGameObject("Part", avatar.transform);
            var sourceMesh = NewSkinnedPartMesh("PartMesh", new Vector3(1f, 0f, 0f), Matrix4x4.identity);
            sourceMesh.AddBlendShapeFrame("Open", 100f, new[] { new Vector3(0f, 1f, 0f) }, null, null);
            var partRenderer = partObject.AddComponent<SkinnedMeshRenderer>();
            partRenderer.sharedMesh = sourceMesh;
            partRenderer.bones = new[] { bone };
            partRenderer.rootBone = bone;

            var installer = partObject.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(bodyMesh, "part-a");
            installer.PartRoot = partObject;
            installer.TargetRendererObject = targetObject;
            installer.ProtectedMesh = NewPayload(sourceMesh, "part-a", false);
            partRenderer.sharedMesh = null;

            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = avatar,
                TargetRenderer = target,
                PartRoot = partObject,
                PartRenderer = partRenderer
            };

            var geometry = selection.PartGeometryMesh;
            Assert.IsNotNull(geometry, "The overlay must be able to read the payload's geometry.");
            Assert.AreEqual(sourceMesh.vertexCount, geometry.vertexCount);
            Assert.AreEqual(
                1,
                geometry.blendShapeCount,
                "The decoded geometry must carry the payload's blend shape: that is the state the fingerprint " +
                "has to survive without querying the mesh-less renderer.");

            using (var cache = new ApaPreviewPositionCache())
            {
                Assert.IsFalse(
                    ApaPreviewGeometry.CanReadBlendShapeWeights(partRenderer, geometry),
                    "A renderer holding no mesh may not be asked for the transient decode's weights.");

                Assert.IsTrue(cache.TryRead(partRenderer, geometry, out var positions, out var source));

                Assert.AreEqual(
                    ApaPreviewPositionSource.Evaluated,
                    source,
                    "A protected part's discs must follow the bone pose, not the payload's rest pose.");
                Assert.AreEqual(1, positions.Length);
                Assert.AreEqual(
                    new Vector3(1f, 1f, 0f),
                    positions[0],
                    "The vertex is skinned by the bone at (0, 1, 0), so the disc moves with the pose.");

                // The same read twice must be served from the cache: a repaint may not re-solve, and it certainly
                // may not re-decrypt.
                Assert.IsTrue(cache.TryRead(partRenderer, geometry, out var again, out _));
                Assert.AreSame(positions, again);
            }

            Assert.IsNull(
                SharedMeshOf(partRenderer),
                "Solving the pose must not attach the decoded mesh to the scene renderer.");
        }

        /// <summary>
        /// The authoring context builder — the overlay path — must build a part snapshot from the payload when
        /// the renderer has no mesh.
        /// </summary>
        [Test]
        public void AuthoringContextBuilder_ProtectedPart_BuildsThePartSnapshotFromThePayload()
        {
            var rig = CreateRig();
            Protect(rig);

            var selection = NewSelection(rig);
            var profile = rig.Installer.Profile;

            var result = ApaAuthoringContextBuilder.Build(selection, profile);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(1, result.Context.Parts.Count);
            Assert.AreEqual(rig.SourceMesh.vertexCount, result.Context.Parts[0].Mesh.VertexCount);
            Assert.IsFalse(
                HasCode(result.Issues.Issues, ApaErrorCode.TargetRendererNotFound),
                result.Issues.FormatAll());
        }

        /// <summary>
        /// The draft-level checks validate a declared source submesh against the part geometry. For a protected
        /// part they must read the payload's submesh count: a declaration inside it is accepted, and one beyond
        /// it is still refused — which is what proves the geometry, not an empty mesh, is being read.
        /// </summary>
        [Test]
        public void AuthoringValidation_ProtectedPart_SeesThePayloadsSubMeshes()
        {
            var rig = CreateRig();
            Protect(rig);

            var selection = NewSelection(rig);
            var draft = new ApaProfileDraft();
            draft.EnsureInitialized();
            draft.Identity.PartId = "part-a";
            draft.AddMaterialSemantic();
            draft.MaterialSemantics[0].Semantic = "Body";
            draft.MaterialSemantics[0].SourceSubMesh = 0;

            var accepted = ApaAuthoringValidation.ValidateDraftDataAgainstMesh(draft, selection.PartGeometryMesh);
            Assert.IsFalse(
                HasCode(accepted, ApaErrorCode.SubMeshWithoutMaterialSlot),
                "A declaration inside the payload's submeshes must be accepted." + Describe(accepted));

            draft.MaterialSemantics[0].SourceSubMesh = 9;
            var refused = ApaAuthoringValidation.ValidateDraftDataAgainstMesh(draft, selection.PartGeometryMesh);
            Assert.IsTrue(
                HasCode(refused, ApaErrorCode.SubMeshWithoutMaterialSlot),
                "A declaration beyond the payload's submeshes must be refused, which proves the geometry is read." +
                Describe(refused));
        }

        // ---- rig -------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Body;
            public GameObject PartHost;
            public Renderer PartRenderer;
            public Mesh BodyMesh;
            public Mesh SourceMesh;
            public AvatarPartInstaller Installer;
            public ApaProtectedMeshAsset Payload;
        }

        private Rig CreateRig()
        {
            var avatar = NewGameObject("Avatar", null);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, bodyMesh);

            var host = NewGameObject("Part Host", avatar.transform);
            var sourceMesh = NewRingMesh("PartMesh", new Vector3(0.5f, 0f, 0f));
            var partRendererObject = NewRenderer("Part", host.transform, sourceMesh);

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(bodyMesh, "part-a");
            installer.PartRoot = partRendererObject;
            installer.TargetRendererObject = null;

            return new Rig
            {
                Avatar = avatar,
                Body = body,
                PartHost = host,
                PartRenderer = partRendererObject.GetComponent<Renderer>(),
                BodyMesh = bodyMesh,
                SourceMesh = sourceMesh,
                Installer = installer
            };
        }

        private ApaAuthoringSelection NewSelection(Rig rig)
        {
            return new ApaAuthoringSelection
            {
                AvatarRoot = rig.Avatar,
                TargetRenderer = rig.Body.GetComponent<SkinnedMeshRenderer>() ?? NewSkinnedTarget(rig),
                PartRoot = rig.PartHost,
                PartRenderer = rig.PartRenderer
            };
        }

        /// <summary>
        /// The target side of the selection must be a skinned renderer (the schema-v2 signature records a bone
        /// signature). The rig's body is a MeshRenderer, so a skinned stand-in with the same mesh is created.
        /// </summary>
        private SkinnedMeshRenderer NewSkinnedTarget(Rig rig)
        {
            var target = NewGameObject("BodySkinned", rig.Avatar.transform);
            var skinned = target.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = rig.BodyMesh;
            skinned.rootBone = rig.Avatar.transform;
            return skinned;
        }

        /// <summary>Turns the rig's part into a protected part: payload assigned, renderer mesh cleared.</summary>
        private void Protect(Rig rig, bool tamperCiphertext = false, string payloadPartId = null)
        {
            rig.Payload = NewPayload(rig.SourceMesh, payloadPartId ?? "part-a", tamperCiphertext);
            rig.Installer.ProtectedMesh = rig.Payload;
            ClearPartMesh(rig);
        }

        /// <summary>Replaces the payload after the source mesh changed, leaving the renderer mesh-less.</summary>
        private void Reprotect(Rig rig)
        {
            rig.Payload = NewPayload(rig.SourceMesh, "part-a", false);
            rig.Installer.ProtectedMesh = rig.Payload;
            ClearPartMesh(rig);
        }

        private ApaProtectedMeshAsset NewPayload(Mesh mesh, string partId, bool tamperCiphertext)
        {
            var snapshot = MeshSnapshotFactory.Capture(mesh, null, out var captureIssues);
            Assert.IsNotNull(snapshot, Describe(captureIssues));
            Assert.IsTrue(
                ApaProtectedMeshCodec.TryCreatePayload(snapshot, partId, out var payload, out var issue),
                issue != null ? issue.ToString() : "payload creation failed");

            var ciphertext = payload.Ciphertext;
            if (tamperCiphertext)
            {
                ciphertext = (byte[])payload.Ciphertext.Clone();
                ciphertext[0] ^= 0xFF;
            }

            var asset = ScriptableObject.CreateInstance<ApaProtectedMeshAsset>();
            _created.Add(asset);
            asset.Assign(
                payload.FormatVersion,
                payload.Codec,
                payload.PartId,
                payload.SourceFingerprint,
                payload.PlaintextLength,
                payload.Salt,
                payload.Iv,
                ciphertext,
                payload.Tag);
            return asset;
        }

        private static void ClearPartMesh(Rig rig)
        {
            var filter = rig.PartRenderer.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = null;

            var skinned = rig.PartRenderer as SkinnedMeshRenderer;
            if (skinned != null) skinned.sharedMesh = null;
        }

        private static Mesh SharedMeshOf(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        private ApaPartProfile NewProfile(Mesh bodyMesh, string partId)
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);

            profile.Identity.PartId = partId;
            profile.Identity.Slot = ApaPartSlot.LeftArm;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(bodyMesh, "Body", string.Empty);
            profile.Seam = new ApaSeamProfile();
            profile.Seam.SetPaired(Range(4), Range(4));
            return profile;
        }

        private GameObject NewGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            _created.Add(gameObject);
            return gameObject;
        }

        private GameObject NewRenderer(string name, Transform parent, Mesh mesh)
        {
            var gameObject = NewGameObject(name, parent);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            gameObject.AddComponent<MeshRenderer>();
            return gameObject;
        }

        private Mesh NewRingMesh(string name, Vector3 apex)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { apex };
            var mesh = new Mesh { name = name };
            mesh.vertices = positions.ToArray();
            mesh.SetTriangles(MeshFixtures.CapTriangles(4, 0, 4), 0);
            _created.Add(mesh);
            return mesh;
        }

        /// <summary>
        /// A one-vertex skinned mesh rigidly weighted to one bone, which is the smallest payload that can carry
        /// skinning through the codec and back.
        /// </summary>
        private Mesh NewSkinnedPartMesh(string name, Vector3 vertex, Matrix4x4 bindPose)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { vertex };
            mesh.SetTriangles(new[] { 0, 0, 0 }, 0);
            mesh.boneWeights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 1f } };
            mesh.bindposes = new[] { bindPose };
            _created.Add(mesh);
            return mesh;
        }

        private static int[] Range(int count)
        {
            var result = new int[count];
            for (var i = 0; i < count; i++) result[i] = i;
            return result;
        }

        private static bool HasCode(IReadOnlyList<ValidationIssue> issues, string code)
        {
            if (issues == null) return false;
            for (var i = 0; i < issues.Count; i++)
            {
                if (issues[i] != null && issues[i].Code == code) return true;
            }

            return false;
        }

        private static string Describe(IReadOnlyList<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return " (no issues)";

            var builder = new StringBuilder();
            for (var i = 0; i < issues.Count; i++) builder.Append('\n').Append(issues[i]);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Source-level contract tests for the protected feature's NDMF integration.
    /// </summary>
    /// <remarks>
    /// The ordering of a plugin's passes exists only inside <c>Configure</c>, and the release of a transient
    /// decrypted mesh exists only as control flow inside a pass. Standing up an NDMF build session to observe
    /// either would test the pipeline rather than this package, so both are pinned structurally — the same
    /// approach the preview layer's contract tests use.
    /// </remarks>
    public sealed class ProtectedMeshNdmfContractTests
    {
        [Test]
        public void HydrationPass_RunsBeforeTheMergePassAndIsReleasedByTheAssemblyPass()
        {
            var plugin = ProtectedMeshSource.Read("Editor", "NDMF", "ApaNdmfPlugin.cs");
            var hydration = ProtectedMeshSource.Read("Editor", "NDMF", "ApaProtectedMeshPass.cs");
            var assembly = ProtectedMeshSource.Read("Editor", "NDMF", "ApaAssemblyPass.cs");

            // Ordering: the hydration pass is declared before the merge-armature pass in the same phase, so a
            // protected part's geometry is back on the clone before Modular Avatar reads its weights.
            var hydrationIndex = plugin.IndexOf("Run(ApaProtectedMeshPass.Instance)", StringComparison.Ordinal);
            var mergeIndex = plugin.IndexOf("Then.Run(ApaMergeArmaturePass.Instance)", StringComparison.Ordinal);
            Assert.GreaterOrEqual(hydrationIndex, 0, "The protected-mesh hydration pass must be registered.");
            Assert.GreaterOrEqual(mergeIndex, 0, "The merge-armature pass must still be registered.");
            Assert.Less(
                hydrationIndex,
                mergeIndex,
                "Protected hydration must run before the merge-armature pass.");

            // Cleanup: the assembly pass releases the leases in a finally, and a later phase sweeps anything an
            // aborted build left behind, so a transient decrypted mesh cannot reach NDMF's asset serializer.
            StringAssert.Contains("ReleaseProtectedMeshLeases()", assembly);
            StringAssert.Contains("finally", assembly);
            StringAssert.Contains("BuildPhase.Optimizing", plugin);
            StringAssert.Contains("Run(ApaProtectedMeshLeaseCleanupPass.Instance)", plugin);

            // Nothing on the protected path may write a decoded mesh to the project.
            Assert.IsFalse(
                hydration.Contains("AssetDatabase"),
                "The protected-mesh pass must not touch the asset database.");
            StringAssert.Contains("HideAndDontSave", ProtectedMeshSource.Read("Editor", "Protected", "ApaProtectedMeshHydration.cs"));
        }

        [Test]
        public void ProtectedGeometryResolution_IsSharedAndNeverAssignsToTheScene()
        {
            var geometry = ProtectedMeshSource.Read("Editor", "Protected", "ApaProtectedPartGeometry.cs");
            var selection = ProtectedMeshSource.Read("Editor", "Authoring", "ApaAuthoringSelection.cs");

            // The overlay path decodes through the shared cache, so an overlay repaint and a build cannot
            // disagree about the payload's bytes, and no second decryption path exists.
            StringAssert.Contains("ApaProtectedMeshCache.TryDecode", geometry);
            StringAssert.Contains("ApaProtectedMeshHydration.TryBuildTransientMesh", geometry);
            Assert.IsFalse(
                geometry.Contains("sharedMesh ="),
                "Resolving overlay geometry must not assign a mesh to a scene renderer.");
            StringAssert.Contains("InvalidateProtectedGeometry", selection);
        }
    }

    /// <summary>
    /// Source-level contract for the identity rule behind the protected prefab reference check.
    /// </summary>
    /// <remarks>
    /// The check only runs inside a real prefab creation, and the state it defends against — Unity handing out a
    /// second managed wrapper for one native asset after a save or a reload — cannot be produced from managed code
    /// on demand. The rule is therefore pinned structurally as well as behaviourally: the generator must delegate
    /// to the Unity-identity predicate, and the predicate must compare native identity, GUID, and path rather than
    /// managed wrapper identity.
    /// </remarks>
    public sealed class ProtectedMeshReferenceContractTests
    {
        [Test]
        public void VerifyProtectedReference_ComparesUnityAssetIdentityRatherThanManagedWrappers()
        {
            var generator = ProtectedMeshSource.Read("Editor", "Authoring", "ApaPrefabGenerator.cs");
            var utility = ProtectedMeshSource.Read("Editor", "Authoring", "ApaAssetDatabaseUtility.cs");

            // The verification delegates to the shared predicate: the payload path is what a recipient resolves,
            // and the written instance is compared as a Unity object while it is still alive.
            StringAssert.Contains("ApaAssetDatabaseUtility.IsSameAssetAtPath", generator);
            Assert.IsFalse(
                generator.Contains("ReferenceEquals(installer.ProtectedMesh"),
                "The payload reference must never be compared by managed wrapper identity.");

            // The predicate itself: Unity's native comparison first, then the persisted-asset identity.
            StringAssert.Contains("if (first == second) return true;", utility);
            StringAssert.Contains("AssetDatabase.GetAssetPath", utility);
            StringAssert.Contains("AssetDatabase.TryGetGUIDAndLocalFileIdentifier", utility);
            StringAssert.Contains("EditorUtility.IsPersistent", utility);
        }
    }

    /// <summary>Reads a file from the package under test, for source-level contract tests.</summary>
    internal static class ProtectedMeshSource
    {
        internal static string Read(params string[] parts)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var full = Path.Combine(
                Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler"),
                string.Join(Path.DirectorySeparatorChar.ToString(), parts));

            if (!File.Exists(full)) Assert.Ignore("Source file not found: " + full);
            return File.ReadAllText(full);
        }
    }
}

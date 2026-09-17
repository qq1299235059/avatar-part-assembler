using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for the canonical avatar-root path token (CR-M2-7): the avatar root is <c>"."</c>, and the empty
    /// string means "missing" alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect these cover: <c>MeshSnapshotFactory.RelativePath</c> returned an empty string both for a null
    /// transform and for the avatar root, so a renderer on the root could not be resolved by the captured
    /// fallback, an avatar-root bone was recorded as an identity-less bone and blocked with <c>APA008</c>, and
    /// the empty identity was refused by <c>FinalBoneTable.IndexOfPath</c>.
    /// </para>
    /// <para>
    /// Since M10 the token names whichever root a path is recorded against, and for a bone that root is the
    /// <i>selected armature</i> rather than the avatar root, so the bone cases below use a selected armature that
    /// is an ordinary child of the avatar.
    /// </para>
    /// <para>
    /// Every case here needs a real hierarchy, because the defect is about which transform a path is recorded
    /// from. The pure-core half — the token is a valid bone identity and an empty identity still blocks — is also
    /// executed outside Unity by the run harness.
    /// </para>
    /// </remarks>
    public sealed class AvatarRootPathTests
    {
        /// <summary>Objects created by a test, destroyed in reverse order in <see cref="TearDown"/>.</summary>
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

        /// <summary>
        /// The producer separates the three meanings: the root token, ordinary nested paths, and the empty path
        /// for a null transform. A nested avatar root must still stop at the avatar so that the recorded path
        /// stays resolvable.
        /// </summary>
        [Test]
        public void RelativePath_SeparatesTheRootFromMissingAndKeepsNestedPaths()
        {
            var sceneRoot = NewGameObject("Scene Root", null);
            sceneRoot.transform.position = new Vector3(4f, -1f, 2f);
            sceneRoot.transform.rotation = Quaternion.Euler(15f, 30f, 45f);

            var avatar = NewGameObject("Avatar", sceneRoot.transform);
            var body = NewGameObject("Body", avatar.transform);
            var armature = NewGameObject("Armature", avatar.transform);
            var hips = NewGameObject("Hips", armature.transform);

            Assert.AreEqual(".", ApaAvatarPath.Root, "The documented canonical token is '.'.");
            Assert.AreEqual(
                ApaAvatarPath.Root,
                MeshSnapshotFactory.RelativePath(avatar.transform, avatar.transform),
                "The avatar root must record the root token, not an empty path.");
            Assert.AreEqual(
                "Body",
                MeshSnapshotFactory.RelativePath(avatar.transform, body.transform),
                "An ordinary child path must not change.");
            Assert.AreEqual(
                "Armature/Hips",
                MeshSnapshotFactory.RelativePath(avatar.transform, hips.transform),
                "A nested ordinary path must not change.");
            Assert.AreEqual(
                string.Empty,
                MeshSnapshotFactory.RelativePath(avatar.transform, null),
                "A null transform must keep recording the empty path.");

            Assert.IsTrue(ApaAvatarPath.IsRoot(ApaAvatarPath.Root));
            Assert.IsFalse(ApaAvatarPath.IsRoot(string.Empty), "Empty is missing, never the avatar root.");
            Assert.IsFalse(ApaAvatarPath.IsRoot("Body"));
            Assert.IsTrue(ApaAvatarPath.HasIdentity(ApaAvatarPath.Root), "The root token is a real identity.");
            Assert.IsFalse(ApaAvatarPath.HasIdentity(string.Empty), "Empty is never an identity.");
        }

        /// <summary>
        /// A target Renderer that lives on the avatar-root GameObject is resolved through the profile's captured
        /// compatibility path, which records the root as the canonical token.
        /// </summary>
        /// <remarks>
        /// The avatar is nested under a moved and rotated scene root on purpose: the recorded path is
        /// avatar-root-relative, and the fallback must resolve it against the avatar root rather than by scene
        /// path. Before CR-M2-7 the capture wrote an empty path here, the fallback skipped it as missing, and the
        /// build failed with "no target body renderer".
        /// </remarks>
        [Test]
        public void Build_WithBodyRendererOnTheAvatarRoot_ResolvesItThroughTheCapturedFallback()
        {
            var rig = CreateRootBodyRig();
            rig.Installer.Profile.Compatibility =
                MeshSnapshotFactory.CaptureSignature(rig.BodyMesh, ApaAvatarPath.Root, string.Empty);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNotNull(context, Describe(issues));
            Assert.AreEqual(
                ApaAvatarPath.Root,
                context.Base.RendererPath,
                "The body renderer sits on the avatar root, so its recorded path is the root token.");

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                Assert.IsNotNull(result.Mesh);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// A legacy profile that recorded the avatar root as an empty renderer path stays unsupported: empty
        /// means "missing", and it cannot be told apart from "no path was recorded" without guessing.
        /// </summary>
        [Test]
        public void Build_WithLegacyEmptyRendererPath_DoesNotGuessTheAvatarRoot()
        {
            var rig = CreateRootBodyRig();
            rig.Installer.Profile.Compatibility =
                MeshSnapshotFactory.CaptureSignature(rig.BodyMesh, string.Empty, string.Empty);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNull(context, "An empty renderer path is missing data, not the avatar root.");
            Assert.IsTrue(
                issues.Exists(issue =>
                    issue.Detail != null && issue.Detail.Contains("reason=missing-target-renderer")),
                Describe(issues));
        }

        /// <summary>
        /// The selected armature's own root is a valid skinned bone identity: it is recorded as the root token,
        /// it is remapped onto the final bone table, and the build reports neither <c>APA007</c> nor
        /// <c>APA008</c>.
        /// </summary>
        /// <remarks>
        /// Since M10 the token names the <i>selected armature</i> rather than the avatar root, which is why the
        /// fixture's armature is an ordinary child of the avatar: the bone that records <c>"."</c> is the
        /// armature root, and it merges onto the body's own armature-root bone because both sides record the
        /// same armature-relative path.
        /// </remarks>
        [Test]
        public void Build_WithArmatureRootAsABone_RemapsItWithoutApa007OrApa008()
        {
            var rig = CreateRootBoneRig(hipsDestroyed: false);

            var bodySignature = MeshSnapshotFactory.CaptureBoneSignature(rig.Armature.transform, rig.BodyRenderer);
            Assert.AreEqual(
                ApaAvatarPath.Root,
                bodySignature.PathAt(0),
                "A bone that is the selected armature's root must record the root token, not an empty path.");
            Assert.IsTrue(bodySignature.HasIdentityAt(0), "The root token is a bone identity.");

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);
            Assert.IsNotNull(context, Describe(issues));

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.TargetBoneNotFound),
                "A root-weighted mesh must not report APA007." + result.Issues.FormatAll());
            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.BoneHierarchyConflict),
                "The armature root is a real identity, so it must not report APA008." + result.Issues.FormatAll());

            try
            {
                var table = result.Plan.BoneTable;
                Assert.IsNotNull(table, "A root-weighted skinned input must produce a final bone table.");
                Assert.AreEqual(3, table.Count);
                Assert.AreEqual(ApaAvatarPath.Root, table.Bones[0].Path, "Body bone order comes first.");
                Assert.AreEqual(string.Empty, table.Bones[0].OwnerPartId);

                // Bone identities are armature-relative since M10: the body contributes its armature's own root
                // and the hips; the part contributes the prop bone, which the body does not have.
                Assert.AreEqual("Hips", table.Bones[1].Path);
                Assert.AreEqual("Prop", table.Bones[2].Path);

                Assert.AreEqual(0, table.IndexOfPath(ApaAvatarPath.Root), "The root must resolve by identity.");
                Assert.AreEqual(0, table.RemapBone(string.Empty, 0), "The body's root bone remaps onto itself.");
                Assert.AreEqual(0, table.RemapBone("part-a", 0), "The part's root bone merges onto the body's.");

                // Final vertex 5 is the part's apex, the only part vertex the weld does not absorb; it keeps the
                // part's own weight on the appended prop bone.
                Assert.AreEqual(2, result.Mesh.boneWeights[5].boneIndex0);
                Assert.AreEqual(1f, result.Mesh.boneWeights[5].weight0, 1e-6f);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// A null bone entry still records the empty path — not the root token — and still blocks. The rejection
        /// of an identity-less bone is not weakened by introducing the token.
        /// </summary>
        [Test]
        public void Build_WithNullBone_RecordsEmptyAndBlocks()
        {
            var rig = CreateRootBoneRig(hipsDestroyed: true);

            var bodySignature = MeshSnapshotFactory.CaptureBoneSignature(rig.Armature.transform, rig.BodyRenderer);
            Assert.AreEqual(
                string.Empty,
                bodySignature.PathAt(1),
                "A null bone entry must keep recording the empty path.");
            Assert.IsFalse(bodySignature.HasIdentityAt(1), "A null bone has no identity.");
            Assert.IsTrue(bodySignature.HasIdentityAt(0), "The armature root next to it still has one.");

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);
            Assert.IsNotNull(context, Describe(issues));

            var result = ApaCore.Assemble(context);

            Assert.IsFalse(result.Succeeded, "A null bone must block the build.");
            Assert.IsNull(result.Mesh, "A blocked build must not produce a mesh.");

            var issue = result.Issues.FindByCode(ApaErrorCode.BoneHierarchyConflict);
            Assert.IsNotNull(
                issue,
                "A null bone must block as a bone without identity." + result.Issues.FormatAll());
            StringAssert.Contains("reason=bone-without-identity", issue.Detail);
        }

        // ---- rigs -------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Armature;
            public GameObject BodyRendererObject;
            public GameObject PartHost;
            public GameObject PartRendererObject;
            public Mesh BodyMesh;
            public SkinnedMeshRenderer BodyRenderer;
            public AvatarPartInstaller Installer;
        }

        /// <summary>
        /// "Scene Root" (moved and rotated) -&gt; "Avatar" with the body renderer on the avatar root itself, plus
        /// a "Part Host" whose installer names no explicit target. The profile's recorded path is therefore the
        /// only way the body renderer can be found.
        /// </summary>
        private Rig CreateRootBodyRig()
        {
            var sceneRoot = NewGameObject("Scene Root", null);
            sceneRoot.transform.position = new Vector3(2f, 3f, -4f);
            sceneRoot.transform.rotation = Quaternion.Euler(10f, 20f, 30f);

            var avatar = NewGameObject("Avatar", sceneRoot.transform);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));

            // The body renderer is ON the avatar root: this is the case the empty path used to alias.
            avatar.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
            avatar.AddComponent<MeshRenderer>();

            var host = NewGameObject("Part Host", avatar.transform);
            var partRenderer = NewRenderer(
                "Part", host.transform, NewRingMesh("PartMesh", new Vector3(0f, 0f, -1f)));

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(bodyMesh, "part-a", ApaPartSlot.LeftArm, "Body", null);
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = null;

            return new Rig
            {
                Avatar = avatar,
                BodyRendererObject = avatar,
                PartHost = host,
                PartRendererObject = partRenderer,
                BodyMesh = bodyMesh,
                Installer = installer
            };
        }

        /// <summary>
        /// "Scene Root" -&gt; "Avatar" -&gt; "Armature" -&gt; "Hips", with a skinned body on "Body" (bones: the
        /// selected target armature and "Hips") and a skinned part under "Part Host/Part" whose own armature
        /// "Part/Armature" carries the "Prop" bone. Both sides record the armature root as <c>"."</c>, so the
        /// part's root bone must merge onto the body's, and each side's other bone is armature-relative.
        /// </summary>
        /// <param name="hipsDestroyed">
        /// When true, the "Hips" GameObject is destroyed after the renderer is wired, which leaves a null bone
        /// entry in the renderer's bone list — the missing-bone state this pipeline must keep blocking.
        /// </param>
        private Rig CreateRootBoneRig(bool hipsDestroyed)
        {
            var sceneRoot = NewGameObject("Scene Root", null);
            sceneRoot.transform.position = new Vector3(-3f, 2f, 5f);
            sceneRoot.transform.rotation = Quaternion.Euler(0f, 40f, 0f);

            var avatar = NewGameObject("Avatar", sceneRoot.transform);
            var armature = NewGameObject("Armature", avatar.transform);
            var hips = NewGameObject("Hips", armature.transform);

            var bodyMesh = NewSkinnedRingMesh("BodyMesh", 2, new Vector3(0f, 0f, 1f));
            var bodyRendererObject = NewGameObject("Body", avatar.transform);
            var bodyRenderer = bodyRendererObject.AddComponent<SkinnedMeshRenderer>();
            bodyRenderer.sharedMesh = bodyMesh;
            bodyRenderer.bones = new[] { armature.transform, hips.transform };

            // The part root is the renderer object, and the part armature is a child of it, which is the shape
            // the part-root-relative selection is defined over: "Armature" resolves under the part root and the
            // part's bones are inside it.
            var partMesh = NewSkinnedRingMesh("PartMesh", 2, new Vector3(0f, 0f, -1f));
            var host = NewGameObject("Part Host", avatar.transform);
            var partRendererObject = NewGameObject("Part", host.transform);
            var partArmature = NewGameObject("Armature", partRendererObject.transform);
            var prop = NewGameObject("Prop", partArmature.transform);
            var partRenderer = partRendererObject.AddComponent<SkinnedMeshRenderer>();
            partRenderer.sharedMesh = partMesh;
            partRenderer.bones = new[] { partArmature.transform, prop.transform };

            // The body apex is weighted to the second bone so that a broken identity there is observable in the
            // weight remap as well as in the signature.
            var bodyWeights = UniformWeights(bodyMesh.vertexCount, 0);
            bodyWeights[4] = new BoneWeight { boneIndex0 = 1, weight0 = 1f };
            bodyMesh.boneWeights = bodyWeights;

            var partWeights = UniformWeights(partMesh.vertexCount, 0);
            partWeights[4] = new BoneWeight { boneIndex0 = 1, weight0 = 1f };
            partMesh.boneWeights = partWeights;

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(
                bodyMesh,
                "part-a",
                ApaPartSlot.LeftArm,
                MeshSnapshotFactory.RelativePath(avatar.transform, bodyRendererObject.transform),
                MeshSnapshotFactory.CaptureBoneSignature(armature.transform, bodyRenderer),
                "Armature",
                "Armature");
            installer.PartRoot = partRendererObject;
            installer.TargetRendererObject = null;

            if (hipsDestroyed)
            {
                // Destroying the bone leaves the renderer's bone list entry null, which is the missing-bone
                // state a real avatar can ship.
                Object.DestroyImmediate(hips);
                Assert.IsTrue(
                    bodyRenderer.bones[1] == null,
                    "The destroyed bone must read as a null bone entry for this test to mean anything.");
            }

            return new Rig
            {
                Avatar = avatar,
                Armature = armature,
                BodyRendererObject = bodyRendererObject,
                PartHost = host,
                PartRendererObject = partRendererObject,
                BodyMesh = bodyMesh,
                BodyRenderer = bodyRenderer,
                Installer = installer
            };
        }

        /// <summary>
        /// A profile whose signature is captured the way authoring captures it: the mesh, the recorded renderer
        /// path, the live bone signature, and the two armature selections the M10 build resolves bone identities
        /// against.
        /// </summary>
        private ApaPartProfile NewProfile(
            Mesh bodyMesh,
            string partId,
            ApaPartSlot slot,
            string rendererPath,
            BoneSignature boneSignature,
            string targetArmaturePath = "",
            string partArmaturePath = "")
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);

            profile.Identity.PartId = partId;
            profile.Identity.Slot = slot;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(
                bodyMesh, rendererPath, string.Empty, boneSignature);

            // The seam is explicitly paired: the build consumes the pairs it is given and refuses an unpaired
            // legacy seam (APA042).
            profile.Seam = new ApaSeamProfile();
            profile.Seam.SetPaired(Range(4), Range(4));

            profile.Bones.TargetArmaturePath = targetArmaturePath;
            profile.Bones.PartArmaturePath = partArmaturePath;

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

        /// <summary>
        /// A readable ring plus apex, the same seam topology the pure-core fixtures use, at the given apex.
        /// </summary>
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
        /// A readable ring plus apex whose bind pose array is already sized for the given bone count, so a
        /// renderer can be wired to it before the weights are assigned.
        /// </summary>
        private Mesh NewSkinnedRingMesh(string name, int boneCount, Vector3 apex)
        {
            var mesh = NewRingMesh(name, apex);
            mesh.bindposes = IdentityPoses(boneCount);
            return mesh;
        }

        private static Matrix4x4[] IdentityPoses(int count)
        {
            var result = new Matrix4x4[count];
            for (var i = 0; i < count; i++) result[i] = Matrix4x4.identity;
            return result;
        }

        /// <summary>Every vertex rigidly weighted to one bone, replaced per vertex where a test needs it.</summary>
        private static BoneWeight[] UniformWeights(int vertexCount, int boneIndex)
        {
            var result = new BoneWeight[vertexCount];
            for (var i = 0; i < vertexCount; i++) result[i] = BoneWeightToOne(boneIndex);
            return result;
        }

        private static BoneWeight BoneWeightToOne(int boneIndex)
        {
            return new BoneWeight { boneIndex0 = boneIndex, weight0 = 1f };
        }

        private static int[] Range(int count)
        {
            var result = new int[count];
            for (var i = 0; i < count; i++) result[i] = i;
            return result;
        }

        private static string Describe(List<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return " (no issues)";

            var builder = new StringBuilder();
            for (var i = 0; i < issues.Count; i++) builder.Append('\n').Append(issues[i]);
            return builder.ToString();
        }
    }
}

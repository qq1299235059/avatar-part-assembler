using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the two properties the NDMF Transforming pass depends on: one pass assembles every installer's
    /// geometry into one mesh, and a configuration that cannot be assembled produces no mesh and leaves the
    /// hierarchy untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass itself is a gate, a discovery call, and a call to <c>ApaBuildProcessor.Process</c>; the processor
    /// then calls <see cref="ContextBuilder"/> and <see cref="ApaCore.Assemble"/>. Those two calls are what these
    /// tests exercise, because they are the parts of the chain that decide whether the clone is mutated at all.
    /// The tests that need a real hierarchy build one, for the same reason <c>ContextBuilderTests</c> does: the
    /// defects they cover are about which Unity object is read and which array is written.
    /// </para>
    /// <para>
    /// <b>The skinned case models two armatures with one bone layout.</b> Since M10 a bone identity is its path
    /// relative to its own selected armature, so a part that shares the avatar's skeleton carries its own mirror
    /// of that layout and declares both selections: "Hips"/"Spine" on the part side match "Hips"/"Spine" on the
    /// body side, and the part's weights follow the body's bones rather than being appended as duplicates.
    /// </para>
    /// <para>
    /// The processor-level guarantees these seams feed — capturing every diagnostic reference before an installer
    /// is consumed, planning every target group before any renderer is written, and consuming nothing on a failed
    /// run — live in the NDMF assembly (<c>ApaBuildProcessor</c>, <c>ApaNdmfDiagnostics</c>). The tests asmdef
    /// references <c>dev.avatar-part-assembler.editor.ndmf</c>, so those guarantees are driven directly rather
    /// than through a static proxy: see <c>BuildProcessorGroupTransactionTests</c>. What stays here is the core
    /// assembly seam, which is reachable without the NDMF assembly.
    /// </para>
    /// </remarks>
    public sealed class BuildPipelineAssemblySeamTests
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
        /// Two installers that name the same target renderer are one assembly, not two: a single capture and a
        /// single mesh build emit the base geometry once and each part's geometry once.
        /// </summary>
        /// <remarks>
        /// The vertex count is the assertion that matters. The welded part seam vertices are deleted rather than
        /// emitted (section 43.2), so the expected mesh is the five base vertices plus one apex per part. Two
        /// separate assemblies would instead duplicate the whole base body.
        /// </remarks>
        [Test]
        public void Assemble_WithTwoInstallersOnOneTarget_EmitsOneMeshWithBothParts()
        {
            var rig = CreateRig();
            rig.Installer.TargetRendererObject = rig.Body;
            AddInstaller(rig, "part-b", ApaPartSlot.RightArm, "Part Host B", rig.Body);

            var installers = ContextBuilder.CollectInstallers(rig.Avatar);
            Assert.AreEqual(2, installers.Count);

            var context = ContextBuilder.BuildFromInstallers(rig.Avatar, installers, null, out var issues);
            Assert.IsNotNull(context, Describe(issues));
            Assert.AreEqual(2, context.Parts.Count, Describe(issues));

            var result = ApaCore.Assemble(context);
            try
            {
                Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
                Assert.IsNotNull(result.Mesh);
                Assert.GreaterOrEqual(result.Plan.FinalIndexOf("part-a", 4), 0, "part-a's apex must be emitted.");
                Assert.GreaterOrEqual(result.Plan.FinalIndexOf("part-b", 4), 0, "part-b's apex must be emitted.");
                Assert.AreEqual(
                    0,
                    result.Plan.CountEmittedPartSeamVertices(),
                    "A welded seam vertex must not be emitted a second time.");
                Assert.AreEqual(
                    7,
                    result.Mesh.vertexCount,
                    "Five base vertices plus one apex per part, with both welds applied, is one assembly.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// Installers that name different target renderers produce no context at all, and the failure path
        /// leaves every renderer's mesh exactly as it was.
        /// </summary>
        /// <remarks>
        /// This is the seam the build processor relies on to guarantee "no partial output": with no context there
        /// is nothing to plan, <see cref="ApaCore.Assemble"/> cannot produce a mesh from it, and the hierarchy is
        /// untouched because nothing was ever written to it.
        /// </remarks>
        [Test]
        public void Assemble_WithConflictingTargets_ProducesNoMeshAndLeavesTheHierarchyUntouched()
        {
            var rig = CreateRig();
            rig.Installer.TargetRendererObject = rig.Body;

            var otherBody = NewRenderer(
                "BodyAlt", rig.Avatar.transform, NewRingMesh("BodyAltMesh", new Vector3(0f, 0f, 1f)));
            AddInstaller(rig, "part-b", ApaPartSlot.RightArm, "Part Host B", otherBody);

            var bodyMeshBefore = rig.BodyFilter.sharedMesh;
            var otherMeshBefore = otherBody.GetComponent<MeshFilter>().sharedMesh;

            var installers = ContextBuilder.CollectInstallers(rig.Avatar);
            var context = ContextBuilder.BuildFromInstallers(rig.Avatar, installers, null, out var issues);

            Assert.IsNull(context, "A conflicting target must produce no context.");
            Assert.IsTrue(
                issues.Exists(issue => issue.IsBlocking),
                "The conflict must be reported as a blocking diagnostic." + Describe(issues));

            var result = ApaCore.Assemble(context);
            Assert.IsFalse(result.Succeeded, "Assembly without a context must produce nothing.");
            Assert.IsNull(result.Mesh, "No partial mesh may be produced.");

            Assert.AreSame(bodyMeshBefore, rig.BodyFilter.sharedMesh, "The target renderer must not be modified.");
            Assert.AreSame(
                otherMeshBefore,
                otherBody.GetComponent<MeshFilter>().sharedMesh,
                "The conflicting renderer must not be modified either.");
        }

        /// <summary>
        /// A skinned assembly produces a bone table whose every path resolves on the hierarchy, and arrays whose
        /// lengths line up with it — the two preconditions the assembly pass checks before it writes
        /// <c>bones</c>, <c>sharedMesh</c>, and the bind poses into the target renderer.
        /// </summary>
        /// <remarks>
        /// The bone path is resolved exactly the way the assembly layer resolves it: the root token
        /// (<see cref="ApaAvatarPath.Root"/>) is the avatar root, and any other path is looked up with
        /// <c>Transform.Find</c>. A path that resolved during validation but not at assignment time would leave
        /// one joint unbound, which is invisible at rest and wrong the moment the avatar animates.
        /// </remarks>
        [Test]
        public void Assemble_WithSkinnedSources_EmitsBonesThatResolveOnTheHierarchy()
        {
            var rig = CreateSkinnedRig();

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);
            Assert.IsNotNull(context, Describe(issues));

            var result = ApaCore.Assemble(context);
            try
            {
                Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
                Assert.IsNotNull(result.Plan.BoneTable, "A skinned assembly must carry a bone table.");
                Assert.AreEqual(2, result.Plan.BoneTable.Count,
                    "The part reuses the avatar's two bones, so the table has two merged entries.");

                // The two armatures record the same relative layout, which is the whole reason the part's bones
                // are the body's bones rather than two appended duplicates.
                var bodySignature = MeshSnapshotFactory.CaptureBoneSignature(rig.Armature.transform, rig.BodyRenderer);
                var partSignature = MeshSnapshotFactory.CaptureBoneSignature(
                    rig.PartArmature.transform,
                    rig.PartHost.GetComponentInChildren<SkinnedMeshRenderer>(true));
                CollectionAssert.AreEqual(
                    bodySignature.Paths,
                    partSignature.Paths,
                    "Both sides must record the same armature-relative bone identities.");

                for (var i = 0; i < result.Plan.BoneTable.Count; i++)
                {
                    var bone = result.Plan.BoneTable.Bones[i];

                    // A merged bone is owned by the body, so its identity is armature-relative to the selected
                    // target armature — which is the scope the assembly layer resolves it in (M10).
                    Assert.AreEqual(
                        string.Empty,
                        bone.OwnerPartId,
                        "Bone '" + bone.Path + "' must be the body's own bone, not appended from the part.");

                    var live = ApaAvatarPath.IsRoot(bone.Path)
                        ? rig.Armature.transform
                        : rig.Armature.transform.Find(bone.Path);

                    Assert.IsNotNull(
                        live,
                        "The final bone '" + bone.Path + "' must resolve in the armature it is relative to.");
                }

                Assert.AreEqual(
                    result.Plan.BoneTable.Count,
                    result.Mesh.bindposes.Length,
                    "One bind pose per final bone.");
                Assert.AreEqual(
                    result.Mesh.vertexCount,
                    result.Mesh.boneWeights.Length,
                    "One weight per emitted vertex.");
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        // ---- rigs -------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Body;
            public MeshFilter BodyFilter;
            public GameObject PartHost;
            public GameObject PartRenderer;
            public Mesh BodyMesh;
            public AvatarPartInstaller Installer;
        }

        private sealed class SkinnedRig
        {
            public GameObject Avatar;
            public GameObject Armature;
            public GameObject Hips;
            public GameObject Spine;
            public GameObject PartHost;
            public GameObject PartArmature;
            public SkinnedMeshRenderer BodyRenderer;
        }

        /// <summary>
        /// "Avatar" -&gt; "Body" (an unskinned renderer) plus a "Part Host" whose part renderer is a ring that
        /// coincides with the body's, so the declared four-vertex seam welds.
        /// </summary>
        private Rig CreateRig()
        {
            var avatar = NewGameObject("Avatar", null);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, bodyMesh);

            var host = NewGameObject("Part Host", avatar.transform);
            var partRenderer = NewRenderer(
                "Part", host.transform, NewRingMesh("PartMesh", new Vector3(0.5f, 0f, 0f)));

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(bodyMesh, "part-a", ApaPartSlot.LeftArm);
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = null;

            return new Rig
            {
                Avatar = avatar,
                Body = body,
                BodyFilter = body.GetComponent<MeshFilter>(),
                PartHost = host,
                PartRenderer = partRenderer,
                BodyMesh = bodyMesh,
                Installer = installer
            };
        }

        /// <summary>
        /// "Avatar" -&gt; "Armature" -&gt; "Hips" -&gt; "Spine", plus a body and a part that carry the same bone
        /// layout in two separate armatures. This is the shape a part with its own skeleton has before the merge:
        /// the armature-relative paths line up, which is what makes the part's weights follow the body's bones.
        /// </summary>
        private SkinnedRig CreateSkinnedRig()
        {
            var avatar = NewGameObject("Avatar", null);
            var armature = NewGameObject("Armature", avatar.transform);
            var hips = NewGameObject("Hips", armature.transform);
            var spine = NewGameObject("Spine", hips.transform);

            var body = NewGameObject("Body", avatar.transform);
            var bodyRenderer = NewSkinnedRenderer(body, "BodyMesh", hips, spine);

            // The part's own armature mirrors the avatar's layout, and the renderer sits on the part root so the
            // core's GetComponentInChildren finds exactly it.
            var host = NewGameObject("Part Host", avatar.transform);
            var partArmature = NewGameObject("Armature", host.transform);
            var partHips = NewGameObject("Hips", partArmature.transform);
            var partSpine = NewGameObject("Spine", partHips.transform);
            NewSkinnedRenderer(host, "PartMesh", partHips, partSpine);

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(
                bodyRenderer.sharedMesh,
                "part-a",
                ApaPartSlot.LeftArm,
                MeshSnapshotFactory.CaptureBoneSignature(armature.transform, bodyRenderer),
                "Armature",
                "Armature");
            installer.PartRoot = host;
            installer.TargetRendererObject = body;

            return new SkinnedRig
            {
                Avatar = avatar,
                Armature = armature,
                Hips = hips,
                Spine = spine,
                PartHost = host,
                PartArmature = partArmature,
                BodyRenderer = bodyRenderer
            };
        }

        /// <summary>Adds a second installer under its own host object, with an explicit target.</summary>
        private void AddInstaller(Rig rig, string partId, ApaPartSlot slot, string hostName, GameObject explicitTarget)
        {
            var host = NewGameObject(hostName, rig.Avatar.transform);
            var partRenderer = NewRenderer(
                "Part", host.transform, NewRingMesh("PartMesh", new Vector3(0.5f, 0f, 0f)));

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(rig.BodyMesh, partId, slot);
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = explicitTarget;
        }

        /// <summary>
        /// A profile whose signature is captured from the body mesh exactly the way authoring captures it:
        /// the recorded renderer path, the live bone signature scoped to the selected target armature, and the
        /// two armature selections the M10 build resolves bone identities against. The seam declares the four
        /// ring vertices on both sides, as an explicit pairing.
        /// </summary>
        private ApaPartProfile NewProfile(
            Mesh bodyMesh,
            string partId,
            ApaPartSlot slot,
            BoneSignature boneSignature = null,
            string targetArmaturePath = "",
            string partArmaturePath = "")
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);

            profile.Identity.PartId = partId;
            profile.Identity.Slot = slot;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(
                bodyMesh, "Body", string.Empty, boneSignature);

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

        private SkinnedMeshRenderer NewSkinnedRenderer(GameObject host, string meshName, params GameObject[] bones)
        {
            var renderer = host.AddComponent<SkinnedMeshRenderer>();
            var mesh = NewSkinnedRingMesh(meshName);
            renderer.sharedMesh = mesh;

            var transforms = new Transform[bones.Length];
            for (var i = 0; i < bones.Length; i++) transforms[i] = bones[i].transform;

            renderer.bones = transforms;
            renderer.rootBone = transforms.Length > 0 ? transforms[0] : null;
            mesh.bindposes = NewIdentityPoses(transforms.Length);

            return renderer;
        }

        /// <summary>
        /// A runtime mesh with the same four-vertex ring the pure-core fixtures use, plus an apex. Runtime
        /// meshes are readable, which is what the capture path requires.
        /// </summary>
        private Mesh NewRingMesh(string name, Vector3 apex)
        {
            var mesh = NewMesh(name);
            mesh.vertices = RingPositions(apex);
            mesh.SetTriangles(MeshFixtures.CapTriangles(4, 0, 4), 0);
            return mesh;
        }

        /// <summary>
        /// The same ring, skinned: the four ring vertices follow the first bone and the apex is blended between
        /// both, so the assembly has a real weight array to remap and real bind poses to rebuild.
        /// </summary>
        private Mesh NewSkinnedRingMesh(string name)
        {
            var mesh = NewMesh(name);
            mesh.vertices = RingPositions(new Vector3(0f, 0f, 1f));
            mesh.SetTriangles(MeshFixtures.CapTriangles(4, 0, 4), 0);

            var weights = new BoneWeight[5];
            for (var i = 0; i < 4; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            weights[4] = new BoneWeight
            {
                boneIndex0 = 0,
                weight0 = 0.5f,
                boneIndex1 = 1,
                weight1 = 0.5f
            };

            mesh.boneWeights = weights;
            return mesh;
        }

        private static Vector3[] RingPositions(Vector3 apex)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { apex };
            return positions.ToArray();
        }

        private Mesh NewMesh(string name)
        {
            var mesh = new Mesh { name = name };
            _created.Add(mesh);
            return mesh;
        }

        private static Matrix4x4[] NewIdentityPoses(int count)
        {
            var poses = new Matrix4x4[count];
            for (var i = 0; i < count; i++) poses[i] = Matrix4x4.identity;
            return poses;
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

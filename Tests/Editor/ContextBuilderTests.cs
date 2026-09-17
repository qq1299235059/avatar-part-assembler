using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the Unity-object boundary: resolving the one target body renderer, capturing every part against
    /// it, and recording avatar-root-relative paths.
    /// </summary>
    /// <remarks>
    /// These are the tests that need a real hierarchy, because the defects they cover are about which Unity
    /// transform is read and which path is recorded. Everything downstream of the snapshot is covered by the
    /// pure-core tests.
    /// </remarks>
    public sealed class ContextBuilderTests
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
        /// The renderer path recorded for the body is relative to the avatar root, so it resolves with
        /// <c>avatarRoot.transform.Find</c> even when the avatar is nested below another scene object.
        /// </summary>
        /// <remarks>
        /// The scene root carries a translation and a rotation on purpose: a full scene path would be
        /// "Scene Root/Avatar/Body", which cannot be resolved against the avatar root, while the recorded path
        /// is what the compatibility fallback later hands to <c>Find</c>.
        /// </remarks>
        [Test]
        public void Build_WithNestedAvatarRoot_RecordsAvatarRootRelativeRendererPath()
        {
            var rig = CreateRig();
            rig.Avatar.transform.localPosition = new Vector3(0.5f, 0f, 0f);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNotNull(context, Describe(issues));
            Assert.AreEqual("Body", context.Base.RendererPath);
            Assert.IsNotNull(
                rig.Avatar.transform.Find(context.Base.RendererPath),
                "The recorded renderer path must be exactly what the fallback resolves with.");

            // The part ordering key uses the same routine, so the installer path is avatar-root-relative too.
            Assert.AreEqual("Part Host", context.Parts[0].OrderingKey.InstallerPath);
        }

        /// <summary>
        /// A part is mapped into the resolved target renderer, not into its own transform, when the installer
        /// leaves the target assignment empty and the profile's recorded path resolves.
        /// </summary>
        /// <remarks>
        /// The part is rotated ninety degrees about the avatar's Z axis. Using the part's own transform as the
        /// target would make the mapping the identity, so both the captured matrix and the emitted geometry
        /// distinguish the two behaviours: the part's apex is authored at <c>(0.5, 0, 0)</c> locally and must
        /// land at <c>(0, 0.5, 0)</c> in the target's local space.
        /// </remarks>
        [Test]
        public void Build_WithoutExplicitTarget_MapsPartsIntoTheResolvedTargetRenderer()
        {
            var rig = CreateRig();

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNotNull(context, Describe(issues));
            Assert.AreEqual(1, context.Parts.Count, Describe(issues));

            var expected = Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, 90f));
            var part = context.FindPart("part-a");
            Assert.IsNotNull(part);
            AssertMatrix(expected, part.Transforms.SourceToTargetLocal(),
                "The part must be mapped into the resolved target renderer's local space.");

            var result = ApaCore.Assemble(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            try
            {
                var apexFinal = result.Plan.FinalIndexOf("part-a", 4);
                Assert.GreaterOrEqual(apexFinal, 0, "The part's apex must be emitted.");

                var emitted = result.Mesh.vertices[apexFinal];
                Assert.AreEqual(0f, emitted.x, 1e-4f, "The part apex must be rotated into target space.");
                Assert.AreEqual(0.5f, emitted.y, 1e-4f, "The part apex must be rotated into target space.");
                Assert.AreEqual(0f, emitted.z, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        /// <summary>
        /// Two installers that name the same target renderer describe one assembly, not a conflict.
        /// </summary>
        [Test]
        public void Build_WithTwoInstallersNamingOneTarget_Succeeds()
        {
            var rig = CreateRig();
            rig.Installer.TargetRendererObject = rig.Body;
            AddInstaller(rig, "part-b", ApaPartSlot.RightArm, "Part Host B", rig.Body);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNotNull(context, Describe(issues));
            Assert.AreEqual(2, context.Parts.Count, Describe(issues));
        }

        /// <summary>
        /// Two installers that name different target renderers block with one stable diagnostic and produce no
        /// context, because the single-mesh contract cannot plan them against one body.
        /// </summary>
        [Test]
        public void Build_WithTwoInstallersNamingDifferentTargets_BlocksWithOneDiagnostic()
        {
            var rig = CreateRig();
            rig.Installer.TargetRendererObject = rig.Body;

            var otherBody = NewRenderer(
                "BodyAlt", rig.Avatar.transform, NewRingMesh("BodyAltMesh", new Vector3(0f, 0f, 1f)));
            AddInstaller(rig, "part-b", ApaPartSlot.RightArm, "Part Host B", otherBody);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNull(context, "A conflicting target must produce no context.");

            var blocking = issues.FindAll(issue => issue.IsBlocking);
            Assert.AreEqual(
                1,
                blocking.Count,
                "The conflict must be reported once, not restated as a generic missing-target error." +
                Describe(issues));
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, blocking[0].Code);
            StringAssert.Contains("reason=conflicting-target-renderers", blocking[0].Detail);
        }

        /// <summary>
        /// The captured matrices are a snapshot: moving, rotating, or scaling the hierarchy afterwards cannot
        /// change what planning and the mesh build read.
        /// </summary>
        [Test]
        public void Build_CapturedMatrices_DoNotFollowLaterHierarchyChanges()
        {
            var rig = CreateRig();

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);
            Assert.IsNotNull(context, Describe(issues));

            var captured = context.FindPart("part-a").Transforms.SourceToTargetLocal();

            rig.PartHost.transform.localPosition = new Vector3(3f, -2f, 1f);
            rig.PartHost.transform.localRotation = Quaternion.Euler(15f, 40f, 70f);
            rig.PartHost.transform.localScale = new Vector3(3f, 3f, 3f);

            AssertMatrix(captured, context.FindPart("part-a").Transforms.SourceToTargetLocal(),
                "A captured transform must not change when the hierarchy does.");
        }

        /// <summary>
        /// A part with a zero scale has no invertible source-to-target transform, so it blocks before any
        /// normal or blend shape delta reads a matrix that has no inverse.
        /// </summary>
        [Test]
        public void Build_WithZeroScaledPart_ReportsInvalidSpaceTransform()
        {
            var rig = CreateRig();
            rig.PartHost.transform.localScale = Vector3.zero;

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);

            Assert.IsNull(context, Describe(issues));
            Assert.IsTrue(
                issues.Exists(issue => issue.Code == ApaErrorCode.InvalidSpaceTransform),
                Describe(issues));
            Assert.IsTrue(
                issues.Exists(issue => issue.Detail.Contains("reason=singular-source-to-target")),
                Describe(issues));
        }

        // ---- rig --------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Body;
            public GameObject PartHost;
            public GameObject PartRenderer;
            public Mesh BodyMesh;
            public AvatarPartInstaller Installer;
        }

        /// <summary>
        /// Builds "Scene Root" (moved and rotated) -&gt; "Avatar" -&gt; "Body", plus a "Part Host" whose part
        /// renderer is rotated ninety degrees about the avatar's Z axis. The installer names no explicit
        /// target, so the profile's recorded path is what resolves the body.
        /// </summary>
        private Rig CreateRig()
        {
            var sceneRoot = NewGameObject("Scene Root", null);
            sceneRoot.transform.position = new Vector3(5f, 1f, -2f);
            sceneRoot.transform.rotation = Quaternion.Euler(10f, 20f, 30f);

            var avatar = NewGameObject("Avatar", sceneRoot.transform);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, bodyMesh);

            var host = NewGameObject("Part Host", avatar.transform);
            host.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
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
                PartHost = host,
                PartRenderer = partRenderer,
                BodyMesh = bodyMesh,
                Installer = installer
            };
        }

        /// <summary>Adds a second installer under its own host object, with an explicit target.</summary>
        private void AddInstaller(
            Rig rig,
            string partId,
            ApaPartSlot slot,
            string hostName,
            GameObject explicitTarget)
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
        /// A profile whose signature is captured from the body mesh exactly the way authoring captures it, with
        /// the seam declaring the four ring vertices on both sides.
        /// </summary>
        private ApaPartProfile NewProfile(Mesh bodyMesh, string partId, ApaPartSlot slot)
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);

            profile.Identity.PartId = partId;
            profile.Identity.Slot = slot;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(bodyMesh, "Body", string.Empty);

            // The seam is explicitly paired, which is the only form the build consumes (M10).
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

        /// <summary>
        /// A runtime mesh with the same four-vertex ring the pure-core fixtures use, plus an apex. Runtime
        /// meshes are readable, which is what the capture path requires.
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

        private static int[] Range(int count)
        {
            var result = new int[count];
            for (var i = 0; i < count; i++) result[i] = i;
            return result;
        }

        private static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual, string message)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    Assert.AreEqual(
                        expected[row, column],
                        actual[row, column],
                        1e-5f,
                        message + " (element " + row + "," + column + ")");
                }
            }
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

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// The Marshmallow PB detection point and the narrow, directional compatibility exception it enables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vendor exception is a single boolean on <see cref="ValidationContext"/> that three consumers read —
    /// the compatibility rule, the seam bone-safety check, and the final bone table. This suite pins the two
    /// halves of that contract: that the boolean is set by the real detection routine and by nothing else, and
    /// that a hierarchy which does <i>not</i> carry the plugin keeps the strict, symmetric verdict.
    /// </para>
    /// <para>
    /// <b>The detection has two shapes and both are covered.</b> The plugin's setup component is what an
    /// authoring scene carries; the structure the plugin generates is what a build or preview clone carries,
    /// because the plugin destroys its own component and reparents its dummy-bone object while it wraps the
    /// breast bones. A detector that only knew the component would answer "no Marshmallow PB" in exactly the
    /// hierarchy whose wrapped bone paths need the exception, so the generated shape is tested in both its
    /// pre-run and its post-run form.
    /// </para>
    /// <para>
    /// The end-to-end tests drive a real hierarchy through <see cref="ContextBuilder.Build"/>, because the
    /// detection-to-context wiring is what the unit-level predicate tests cannot prove: a context built by hand
    /// can set the boolean directly, which is precisely the call site under test.
    /// </para>
    /// </remarks>
    public sealed class MarshmallowPbCompatibilityTests
    {
        /// <summary>The plugin's runtime component, matched by full name exactly as the detector matches it.</summary>
        private const string PluginComponentFullName = "wataameya.marshmallow_PB.ndmf.marshmallow_PB_MA";

        private const string GeneratedRootName = "marshmallow_PB";
        private const string DummyBoneName = "marshmallow_PB(DummyBone)";

        /// <summary>Objects created by a test, destroyed in reverse order in <see cref="TearDown"/>.</summary>
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        // ---- detection: the structure the plugin generates ---------------------------------------------

        /// <summary>The nested shape, as the plugin's own prefab starts out.</summary>
        [Test]
        public void GeneratedStructure_IsDetectedWhileTheDummyBoneIsStillNested()
        {
            var avatar = NewGameObject("Avatar", null);
            var generated = NewGameObject(GeneratedRootName, avatar.transform);
            NewGameObject(DummyBoneName, generated.transform);

            Assert.IsTrue(
                ContextBuilder.HasMarshmallowPb(avatar),
                "The plugin's generated root with its dummy bone is the structure it instantiates.");
        }

        /// <summary>
        /// The post-run shape: the component is gone, and the dummy-bone object has been reparented to the chest
        /// while the wrapped bone sits under a same-named wrapper.
        /// </summary>
        /// <remarks>
        /// This is the shape a build-time clone has when the assembly pass runs, so it is the shape the
        /// compatibility exception actually has to be enabled in.
        /// </remarks>
        [Test]
        public void GeneratedStructure_IsDetectedAfterThePluginReparentedItsDummyBone()
        {
            var avatar = NewGameObject("Avatar", null);
            var armature = NewGameObject("Armature", avatar.transform);
            var chest = NewGameObject("Chest", armature.transform);

            var generated = NewGameObject(GeneratedRootName, avatar.transform);
            AddGeneratedSubsystems(generated);

            // The plugin's own ordering: the dummy bone is moved to the chest, the wrapper is renamed to the bone
            // it wraps, and the original bone is parented under it.
            NewGameObject(DummyBoneName, chest.transform);
            var wrapper = NewGameObject("Breast_L", chest.transform);
            NewGameObject("Breast_L", wrapper.transform);

            Assert.IsTrue(
                ContextBuilder.HasMarshmallowPb(avatar),
                "The plugin removes its own component and reparents its dummy bone; the generated structure is " +
                "what remains and what the detector must accept.");
        }

        /// <summary>The generated root's own prefab subsystems are a marker on their own.</summary>
        [Test]
        public void GeneratedStructure_IsDetectedByItsPrefabSubsystemsAlone()
        {
            var avatar = NewGameObject("Avatar", null);
            AddGeneratedSubsystems(NewGameObject(GeneratedRootName, avatar.transform));

            Assert.IsTrue(
                ContextBuilder.HasMarshmallowPb(avatar),
                "A generated root carrying the plugin's own prefab children is the plugin's structure.");
        }

        // ---- detection: what must NOT enable the exception ---------------------------------------------

        /// <summary>
        /// An object that merely happens to be named <c>marshmallow_PB</c> does not enable the vendor exception,
        /// even when the hierarchy carries the exact duplicate-segment shape the exception exists for.
        /// </summary>
        /// <remarks>
        /// This is R1's boundary: a hand-authored <c>Spine/Spine</c> is a real hierarchy, not a plugin artifact,
        /// and it must keep the strict verdict.
        /// </remarks>
        [Test]
        public void GeneratedRootWithoutItsOwnStructure_DoesNotEnableTheException()
        {
            var avatar = NewGameObject("Avatar", null);
            NewGameObject(GeneratedRootName, avatar.transform);

            var armature = NewGameObject("Armature", avatar.transform);
            var spine = NewGameObject("Spine", armature.transform);
            NewGameObject("Spine", spine.transform);

            Assert.IsFalse(
                ContextBuilder.HasMarshmallowPb(avatar),
                "A bare object named 'marshmallow_PB' is not the plugin's generated structure.");
        }

        /// <summary>A hierarchy with no plugin at all is not detected.</summary>
        [Test]
        public void HierarchyWithoutMarshmallowPb_IsNotDetected()
        {
            var avatar = NewGameObject("Avatar", null);
            var armature = NewGameObject("Armature", avatar.transform);
            var spine = NewGameObject("Spine", armature.transform);
            NewGameObject("Spine", spine.transform);

            Assert.IsFalse(ContextBuilder.HasMarshmallowPb(avatar));
            Assert.IsFalse(ContextBuilder.HasMarshmallowPb(null), "A null root is never a detection.");
        }

        // ---- detection: the plugin's setup component ---------------------------------------------------

        /// <summary>
        /// The plugin's setup component is matched by full name, on active objects, regardless of its
        /// <c>enabled</c> flag.
        /// </summary>
        /// <remarks>
        /// The plugin finds its own component with <c>GetComponentInChildren</c>, which ignores <c>enabled</c> but
        /// skips inactive objects. Matching that exactly is what keeps "the plugin will run" and "the detector
        /// says it is present" the same statement: a disabled component still generates the wrapper, and an
        /// inactive object is one the plugin cannot see.
        /// </remarks>
        [Test]
        public void PluginComponent_IsDetectedByFullNameAndFollowsThePluginsOwnActivationRule()
        {
            var type = FindPluginComponentType();
            if (type == null)
            {
                Assert.Ignore("Marshmallow PB is not installed in this project: " + PluginComponentFullName);
            }

            var avatar = NewGameObject("Avatar", null);
            var host = NewGameObject("Marshmallow PB Setup", avatar.transform);

            Component component;
            try
            {
                component = host.AddComponent(type);
            }
            catch (Exception e)
            {
                Assert.Ignore("Marshmallow PB's component could not be created here: " + e.Message);
                return;
            }

            Assert.IsTrue(
                ContextBuilder.HasMarshmallowPb(avatar),
                "The plugin's setup component is the authoring-scene shape of the detection.");

            var behaviour = component as Behaviour;
            if (behaviour != null)
            {
                behaviour.enabled = false;
                Assert.IsTrue(
                    ContextBuilder.HasMarshmallowPb(avatar),
                    "A disabled component still generates the wrapper, so it must still enable the exception.");
                behaviour.enabled = true;
            }

            host.SetActive(false);
            Assert.IsFalse(
                ContextBuilder.HasMarshmallowPb(avatar),
                "The plugin cannot find its component on an inactive object, so it will not wrap anything.");
        }

        // ---- the exception, end to end ------------------------------------------------------------------

        /// <summary>
        /// The wrapped path blocks while no Marshmallow PB is present, and is accepted once the plugin's
        /// generated structure is there.
        /// </summary>
        /// <remarks>
        /// Both halves run against the same rig, so the only difference between the two verdicts is the detected
        /// structure. The live path is also asserted to survive unchanged: the exception redirects a lookup, it
        /// never rewrites the captured hierarchy.
        /// </remarks>
        [Test]
        public void WrappedBonePath_BlocksWithoutThePlugin_AndIsAcceptedWithItsGeneratedStructure()
        {
            var rig = CreateWrapperRig();

            var blocked = ContextBuilder.Build(rig.Avatar, null, out var blockedIssues);
            Assert.IsNull(
                blocked,
                "A repeated final segment is a real mismatch while no Marshmallow PB is present.");
            Assert.IsTrue(
                blockedIssues.Exists(issue => issue.IsBlocking &&
                                              issue.Detail != null &&
                                              issue.Detail.Contains("reason=bone-signature-mismatch")),
                Describe(blockedIssues));

            AddGeneratedStructure(rig.Avatar, rig.Chest.transform);

            var accepted = ContextBuilder.Build(rig.Avatar, null, out var acceptedIssues);
            Assert.IsNotNull(accepted, Describe(acceptedIssues));
            Assert.IsFalse(
                acceptedIssues.Exists(issue => issue.IsBlocking),
                "The detected vendor mode must clear the wrapped-path mismatch: " + Describe(acceptedIssues));
            Assert.IsTrue(
                accepted.MarshmallowPbCompatibilityEnabled,
                "The detection must reach the context every rule reads.");
            Assert.AreEqual(
                "Hips/Spine/Spine",
                accepted.Base.Mesh.BoneSignature.PathAt(1),
                "Compatibility must not rewrite the captured live hierarchy path.");
        }

        /// <summary>
        /// With the vendor mode on, the part's authored bone redirects onto the wrapped live bone instead of
        /// appending a duplicate.
        /// </summary>
        /// <remarks>
        /// This is the whole point of the exception: the part was authored against <c>Hips/Spine</c>, the live
        /// body carries <c>Hips/Spine/Spine</c>, and the two are one joint. The final table must therefore hold
        /// the body's two bones and nothing else, and the part's weight must land on the body's entry.
        /// </remarks>
        [Test]
        public void WrappedBonePath_WithDetection_RedirectsThePartBoneWithoutAppendingOne()
        {
            var rig = CreateWrapperRig();
            AddGeneratedStructure(rig.Avatar, rig.Chest.transform);

            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);
            Assert.IsNotNull(context, Describe(issues));

            var planning = ApaCore.Plan(context);
            Assert.IsTrue(planning.Succeeded, planning.Issues.FormatAll());

            var table = planning.Plan.BoneTable;
            Assert.IsNotNull(table, "A skinned body and a skinned part must produce a bone table.");
            Assert.AreEqual(2, table.Count, "The alias must not append a duplicate part bone: " + DescribeTable(table));
            Assert.AreEqual("Hips", table.Bones[0].Path);
            Assert.AreEqual("Hips/Spine/Spine", table.Bones[1].Path, "The live path stays the bone's identity.");
            Assert.AreEqual(string.Empty, table.Bones[1].OwnerPartId, "The body owns the wrapped bone.");
            Assert.AreEqual(
                1,
                table.RemapBone("part-a", 1),
                "The part's authored bone must resolve to the body's wrapped bone.");
        }

        // ---- the exception is narrow, even when it is on -------------------------------------------------

        /// <summary>A different inserted segment still blocks, even with the vendor mode enabled.</summary>
        [Test]
        public void EnabledException_StillBlocksADifferentInsertedSegment()
        {
            var result = PlanAgainstRecordedPaths(
                new[] { "Hips", "Hips/Spine/Other" },
                marshmallowPbCompatibilityEnabled: true);

            Assert.IsFalse(result.Succeeded, "Only a repeated final segment is compatible.");
            var issue = result.Issues.FindByCode(ApaErrorCode.PartProfileIncompatible);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            StringAssert.Contains("reason=bone-signature-mismatch", issue.Detail);
        }

        /// <summary>A different parent still blocks, even with the vendor mode enabled.</summary>
        [Test]
        public void EnabledException_StillBlocksADifferentParent()
        {
            var result = PlanAgainstRecordedPaths(
                new[] { "Hips", "Other/Spine" },
                marshmallowPbCompatibilityEnabled: true);

            Assert.IsFalse(result.Succeeded, "The recorded path must remain a prefix of the live path.");
            Assert.IsTrue(
                result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible),
                result.Issues.FormatAll());
        }

        /// <summary>One repeated copy of the final segment is compatible; a deeper repeat is too.</summary>
        [Test]
        public void EnabledException_AcceptsOneOrMoreRepeatedFinalSegments()
        {
            var single = PlanAgainstRecordedPaths(
                new[] { "Hips", "Hips/Spine/Spine" },
                marshmallowPbCompatibilityEnabled: true);
            Assert.IsFalse(
                single.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible),
                "One wrapper is the plugin's documented shape: " + single.Issues.FormatAll());

            var nested = PlanAgainstRecordedPaths(
                new[] { "Hips", "Hips/Spine/Spine/Spine" },
                marshmallowPbCompatibilityEnabled: true);
            Assert.IsFalse(
                nested.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible),
                "The plugin can wrap more than once: " + nested.Issues.FormatAll());
        }

        // ---- fixtures ------------------------------------------------------------------------------------

        /// <summary>Runs the planner over a skinned body whose live bone paths are supplied.</summary>
        /// <remarks>
        /// The recorded signature is always <c>Hips</c> + <c>Hips/Spine</c> — the identity an author selected
        /// before the plugin ran — so a caller varies only the live hierarchy it is compared against.
        /// </remarks>
        private static PlanningResult PlanAgainstRecordedPaths(
            string[] liveBonePaths,
            bool marshmallowPbCompatibilityEnabled)
        {
            var body = MeshFixtures.SkinnedBody(4, liveBonePaths);
            var part = MeshFixtures.Part(4, apexOffset: -1f);

            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = new[] { "Hips", "Hips/Spine" };

            return ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                signature: signature,
                marshmallowPbCompatibilityEnabled: marshmallowPbCompatibilityEnabled));
        }

        /// <summary>One wrapper rig, so a test can compare the same hierarchy with and without detection.</summary>
        private sealed class WrapperRig
        {
            public GameObject Avatar;
            public GameObject Chest;
        }

        /// <summary>
        /// An avatar whose body bone has already been wrapped: the authored identity <c>Hips/Spine</c> is the
        /// live <c>Hips/Spine/Spine</c>, which is exactly what the plugin produces when it renames its wrapper
        /// to the bone it wraps.
        /// </summary>
        private WrapperRig CreateWrapperRig()
        {
            var avatar = NewGameObject("Avatar", null);

            var armature = NewGameObject("Armature", avatar.transform);
            var hips = NewGameObject("Hips", armature.transform);
            var wrapper = NewGameObject("Spine", hips.transform);
            var spine = NewGameObject("Spine", wrapper.transform);

            var bodyMesh = NewSkinnedRingMesh("BodyMesh", 1f);
            var body = NewGameObject("Body", avatar.transform);
            var bodyRenderer = body.AddComponent<SkinnedMeshRenderer>();
            bodyRenderer.sharedMesh = bodyMesh;
            bodyRenderer.bones = new[] { hips.transform, spine.transform };

            var partRoot = NewGameObject("Part Host", avatar.transform);
            var partHips = NewGameObject("Hips", partRoot.transform);
            var partSpine = NewGameObject("Spine", partHips.transform);

            var partMesh = NewSkinnedRingMesh("PartMesh", -1f);
            var part = NewGameObject("Part", partRoot.transform);
            var partRenderer = part.AddComponent<SkinnedMeshRenderer>();
            partRenderer.sharedMesh = partMesh;
            partRenderer.bones = new[] { partHips.transform, partSpine.transform };

            var installer = partRoot.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewWrapperProfile(bodyMesh, partMesh, "part-a");
            installer.PartRoot = partRoot;
            installer.TargetRendererObject = body;

            return new WrapperRig
            {
                Avatar = avatar,
                Chest = hips
            };
        }

        /// <summary>
        /// The profile an author would have saved before the plugin ran: the body signature records
        /// <c>Hips/Spine</c>, and both armature selections are made.
        /// </summary>
        private ApaPartProfile NewWrapperProfile(Mesh bodyMesh, Mesh partMesh, string partId)
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);

            profile.Identity.PartId = partId;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(bodyMesh, "Body", string.Empty);
            profile.Compatibility.BonePaths = new[] { "Hips", "Hips/Spine" };

            // The target armature is the body's skeleton; the part's own bones hang directly off the part root.
            profile.Bones = new ApaBoneProfile
            {
                TargetArmaturePath = "Armature",
                PartArmaturePath = ApaAvatarPath.Root
            };

            profile.Seam = MeshFixtures.Seam(4);
            profile.PartMeshFingerprint = ApaMeshFingerprint.OfMesh(partMesh);

            return profile;
        }

        /// <summary>Adds the plugin's generated root and the dummy bone it reparents to the chest.</summary>
        private void AddGeneratedStructure(GameObject avatar, Transform chest)
        {
            AddGeneratedSubsystems(NewGameObject(GeneratedRootName, avatar.transform));
            NewGameObject(DummyBoneName, chest);
        }

        /// <summary>Adds the prefab children the plugin's generated root keeps as its own.</summary>
        private void AddGeneratedSubsystems(GameObject generated)
        {
            var names = new[] { "PhysBone_L", "PhysBone_R", "Collider", "Constraint", "System" };
            for (var i = 0; i < names.Length; i++)
            {
                NewGameObject(names[i], generated.transform);
            }
        }

        /// <summary>A readable ring-plus-apex mesh whose ring uses bone 0 and whose apex uses bone 1.</summary>
        private Mesh NewSkinnedRingMesh(string name, float apexZ)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, apexZ) };

            var mesh = new Mesh { name = name };
            mesh.vertices = positions.ToArray();
            mesh.SetTriangles(MeshFixtures.CapTriangles(4, 0, 4), 0);

            var weights = MeshFixtures.UniformWeights(positions.Count, 0);
            weights[positions.Count - 1] = new BoneWeight { boneIndex0 = 1, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            mesh.RecalculateBounds();

            _created.Add(mesh);
            return mesh;
        }

        private GameObject NewGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            _created.Add(gameObject);
            return gameObject;
        }

        /// <summary>The plugin's runtime component type, or null when the plugin is not installed.</summary>
        private static Type FindPluginComponentType()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var type = assemblies[i].GetType(PluginComponentFullName, false);
                if (type != null) return type;
            }

            return null;
        }

        private static string DescribeTable(FinalBoneTable table)
        {
            if (table == null) return " (no table)";

            var builder = new StringBuilder();
            for (var i = 0; i < table.Count; i++) builder.Append('\n').Append(table.Bones[i]);
            return builder.ToString();
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

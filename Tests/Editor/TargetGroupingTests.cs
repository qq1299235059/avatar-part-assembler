using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the M6 target-group model on a real hierarchy: target resolution, grouping, per-group planning,
    /// avatar ownership boundaries, the shared activity predicate, and mesh ownership.
    /// </summary>
    /// <remarks>
    /// These are the tests that need a real hierarchy, because the defects they cover are about which renderer is
    /// resolved and which subtree belongs to which avatar. Everything downstream of the snapshot is covered by
    /// the pure-core tests.
    /// </remarks>
    public sealed class TargetGroupingTests
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

        /// <summary>A component used to prove that a registered boundary type stops installer discovery.</summary>
        /// <remarks>
        /// The production rule matches the VRChat descriptor by full type name through reflection, which is why
        /// this test can register its own stand-in instead of depending on the SDK being installed.
        /// </remarks>
        private sealed class TestAvatarBoundary : MonoBehaviour
        {
        }

        // ---- grouping --------------------------------------------------------------------------------

        /// <summary>Two installers naming two renderers form two groups, while the single-target API blocks.</summary>
        [Test]
        public void BuildGroups_WithTwoExplicitTargets_ProducesTwoGroups()
        {
            var rig = CreateTwoTargetRig();

            var context = ContextBuilder.Build(rig.Avatar, null, out var singleIssues);
            Assert.IsNull(context, "The single-target contract must keep blocking on two targets.");
            Assert.IsTrue(
                singleIssues.Exists(issue => issue.IsBlocking &&
                                             issue.Detail.Contains("reason=conflicting-target-renderers")),
                Describe(singleIssues));

            var groups = ContextBuilder.BuildGroups(rig.Avatar, null, out var issues);

            Assert.AreEqual(2, groups.Count, Describe(issues));
            Assert.AreEqual("Body", groups[0].GroupKey, "Groups must be ordered ordinally by key.");
            Assert.AreEqual("BodyAlt", groups[1].GroupKey);
            Assert.AreSame(rig.Body.GetComponent<Renderer>(), groups[0].TargetRenderer);
            Assert.AreSame(rig.BodyAlt.GetComponent<Renderer>(), groups[1].TargetRenderer);
            Assert.AreEqual(1, groups[0].Context.Parts.Count);
            Assert.AreEqual(1, groups[1].Context.Parts.Count);
            Assert.AreEqual("part-a", groups[0].Context.Parts[0].PartId);
            Assert.AreEqual("part-b", groups[1].Context.Parts[0].PartId);
            Assert.AreEqual("Body", groups[0].Context.GroupKey);
            Assert.AreEqual("BodyAlt", groups[1].Context.GroupKey);
        }

        /// <summary>Two recorded targets form two groups; the single-target API still blocks on them.</summary>
        [Test]
        public void BuildGroups_WithTwoRecordedTargets_ProducesTwoGroups()
        {
            var rig = CreateTwoTargetRig();
            rig.InstallerA.TargetRendererObject = null;
            rig.InstallerB.TargetRendererObject = null;

            var groups = ContextBuilder.BuildGroups(rig.Avatar, null, out var issues);

            Assert.AreEqual(2, groups.Count, Describe(issues));

            var context = ContextBuilder.Build(rig.Avatar, null, out var singleIssues);
            Assert.IsNull(context, "Two recorded targets cannot be expressed by the single-target contract.");
            Assert.IsTrue(
                singleIssues.Exists(issue => issue.IsBlocking &&
                                             issue.Detail.Contains("reason=conflicting-target-renderers")),
                Describe(singleIssues));
        }

        /// <summary>An explicit target that disagrees with the recorded path blocks for that one installer.</summary>
        [Test]
        public void BuildGroups_ExplicitTargetDisagreeingWithRecordedPath_Blocks()
        {
            var avatar = NewGameObject("Avatar", null);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, bodyMesh);
            var bodyAltMesh = NewRingMesh("BodyAltMesh", new Vector3(0f, 0f, 2f));
            var bodyAlt = NewRenderer("BodyAlt", avatar.transform, bodyAltMesh);

            // The profile was authored against "Body" but the installer names "BodyAlt": one part cannot be
            // authored against one body and installed into another.
            var host = NewGameObject("Part Host", avatar.transform);
            var partRenderer = NewRenderer("Part", host.transform, NewRingMesh("PartMesh", new Vector3(0f, 0f, -1f)));
            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(bodyMesh, "part-a", ApaPartSlot.LeftArm, "Body");
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = bodyAlt;

            var groups = ContextBuilder.BuildGroups(avatar, null, out var issues);

            Assert.AreEqual(0, groups.Count, "A conflicting per-installer target must produce no group.");
            Assert.IsTrue(
                issues.Exists(issue => issue.IsBlocking &&
                                       issue.Detail.Contains("reason=conflicting-target-renderers")),
                Describe(issues));
            Assert.IsTrue(
                issues.Exists(issue => issue.Detail != null && issue.Detail.Contains("recorded=Body")),
                Describe(issues));
        }

        /// <summary>A target renderer outside the avatar root is refused: it has no stable identity.</summary>
        [Test]
        public void BuildGroups_TargetOutsideTheAvatarRoot_Blocks()
        {
            var sceneRoot = NewGameObject("Scene Root", null);
            var avatar = NewGameObject("Avatar", sceneRoot.transform);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            NewRenderer("Body", avatar.transform, bodyMesh);

            var outsideMesh = NewRingMesh("OutsideMesh", new Vector3(0f, 0f, 1f));
            var outside = NewRenderer("Outside Body", sceneRoot.transform, outsideMesh);

            var host = NewGameObject("Part Host", avatar.transform);
            var partRenderer = NewRenderer("Part", host.transform, NewRingMesh("PartMesh", new Vector3(0f, 0f, -1f)));
            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(outsideMesh, "part-a", ApaPartSlot.LeftArm, "Outside Body");
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = outside;

            var groups = ContextBuilder.BuildGroups(avatar, null, out var issues);

            Assert.AreEqual(0, groups.Count);
            Assert.IsTrue(
                issues.Exists(issue => issue.IsBlocking &&
                                       issue.Detail.Contains("reason=target-outside-avatar-root")),
                Describe(issues));
        }

        // ---- assembly and mesh ownership --------------------------------------------------------------

        /// <summary>N groups produce N meshes, and the mesh names differ by group key.</summary>
        [Test]
        public void AssembleGroups_ProducesOneMeshPerGroup_WithDistinctNames()
        {
            var rig = CreateTwoTargetRig();

            var result = ApaCore.AssembleGroups(rig.Avatar, null, out var discovery);
            Assert.IsTrue(result.Succeeded, Describe(discovery) + "\n" + result.Issues.FormatAll());
            Assert.AreEqual(2, result.GroupCount);
            Assert.IsNotNull(result.Groups[0].Mesh);
            Assert.IsNotNull(result.Groups[1].Mesh);

            var firstName = result.Groups[0].Mesh.name;
            var secondName = result.Groups[1].Mesh.name;

            try
            {
                Assert.AreNotEqual(firstName, secondName, "Two groups must never share a mesh name.");
                StringAssert.Contains("Body", firstName);
                StringAssert.Contains("BodyAlt", secondName);
                Assert.IsTrue(result.Groups[0].Mesh != result.Groups[1].Mesh);

                // Both meshes are assigned by the caller, so nothing is left for the orchestrator to release.
                result.MarkAssigned(0);
                result.MarkAssigned(1);
                Assert.AreEqual(0, result.OwnedMeshCount);
                Assert.AreEqual(0, result.ReleaseUnassigned());
            }
            finally
            {
                for (var i = 0; i < result.GroupCount; i++)
                {
                    if (result.Groups[i].Mesh != null) Object.DestroyImmediate(result.Groups[i].Mesh);
                }
            }
        }

        /// <summary>The orchestration result releases exactly the meshes the caller did not assign.</summary>
        [Test]
        public void AssembleGroups_ReleaseUnassigned_DestroysOnlyUnassignedMeshes()
        {
            var rig = CreateTwoTargetRig();

            var result = ApaCore.AssembleGroups(rig.Avatar, null, out var discovery);
            Assert.IsTrue(result.Succeeded, Describe(discovery) + "\n" + result.Issues.FormatAll());
            Assert.AreEqual(2, result.OwnedMeshCount);

            var assignedMesh = result.Groups[0].Mesh;
            result.MarkAssigned(0);

            Assert.AreEqual(1, result.OwnedMeshCount);
            Assert.AreEqual(1, result.ReleaseUnassigned(), "The unassigned mesh must be released.");
            Assert.IsNull(result.Groups[1].Mesh, "A released mesh must not be readable through the entry.");
            Assert.IsNotNull(result.Groups[0].Mesh, "An assigned mesh must survive.");
            Assert.AreSame(assignedMesh, result.Groups[0].Mesh);

            Assert.AreEqual(0, result.ReleaseUnassigned(), "Release must be idempotent.");

            Object.DestroyImmediate(result.Groups[0].Mesh);
        }

        /// <summary>A blocking issue in one group fails the whole avatar and produces no mesh at all.</summary>
        [Test]
        public void AssembleGroups_OneGroupInvalid_FailsWholeAvatar_AndNamesTheGroup()
        {
            var rig = CreateTwoTargetRig();

            // Make the second group's signature disagree with its base, without touching the first group.
            rig.InstallerB.Profile.Compatibility.VertexCount = 999;

            var result = ApaCore.AssembleGroups(rig.Avatar, null, out var discovery);

            Assert.IsFalse(result.Succeeded, "A blocking issue in any group must fail the whole avatar.");
            Assert.AreEqual(0, result.GroupCount, "No partial avatar may be produced.");
            Assert.IsTrue(
                result.Issues.HasErrors || discovery.Exists(issue => issue.IsBlocking),
                Describe(discovery) + "\n" + result.Issues.FormatAll());
            Assert.IsTrue(
                AllIssues(result, discovery).Exists(issue =>
                    issue.IsBlocking && issue.Detail.Contains("group=BodyAlt")),
                Describe(discovery) + "\n" + result.Issues.FormatAll());
        }

        /// <summary>
        /// A group's blocking condition is reported exactly once, with its group tag.
        /// </summary>
        /// <remarks>
        /// The same condition used to reach the report twice: once as an untagged discovery diagnostic and once
        /// tagged, after the validator re-ran the compatibility rule through the group's representative
        /// signature. A user-facing report that states one group's problem twice is a defect even when the
        /// blocking verdict is right, so this test counts the occurrences rather than only looking for one.
        /// </remarks>
        [Test]
        public void AssembleGroups_OneGroupInvalid_ReportsTheConditionOnce()
        {
            var rig = CreateTwoTargetRig();
            rig.InstallerB.Profile.Compatibility.VertexCount = 999;

            var result = ApaCore.AssembleGroups(rig.Avatar, null, out var discovery);
            var all = AllIssues(result, discovery);

            var mismatches = 0;
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].Detail != null && all[i].Detail.Contains("vertex count expected 999")) mismatches++;
            }

            Assert.AreEqual(
                1,
                mismatches,
                "One failing group must report its mismatch once, not once per validator pass:" + Describe(all));
            Assert.IsTrue(
                all.Exists(issue => issue.Detail != null &&
                                    issue.Detail.Contains("vertex count expected 999") &&
                                    issue.Detail.Contains("group=BodyAlt")),
                "The single report must carry the group tag:" + Describe(all));
        }

        /// <summary>
        /// A non-blocking per-installer compatibility verdict is reported once and anchored on its installer.
        /// </summary>
        /// <remarks>
        /// This is the case in which the duplicate used to appear: the advisory did not block, so the group was
        /// planned and the validator's second compatibility pass emitted the same warning again — tagged — while
        /// the installer's own copy stayed untagged.
        /// </remarks>
        [Test]
        public void PerInstallerCompatibilityAdvisory_IsReportedOncePerGroup()
        {
            var rig = CreateTwoTargetRig();

            // A recorded bone path the live body does not have. Nothing in this rig is skinned, so the difference
            // is a staleness warning rather than a block.
            rig.InstallerA.Profile.Compatibility.BonePaths = new[] { "Armature/Nope" };

            var planned = ApaCore.PlanGroups(rig.Avatar, null, out var issues);

            Assert.IsTrue(planned.Succeeded, Describe(issues) + "\n" + planned.Issues.FormatAll());

            var advisories = FindAll(planned.Issues.Issues, "reason=bone-signature-advisory");
            Assert.AreEqual(
                1,
                advisories.Count,
                "The advisory must be reported once, not once per compatibility pass:" +
                Describe(planned.Issues.Issues));
            Assert.AreEqual("part-a", advisories[0].PartId, "The verdict belongs to the installer that produced it.");
            StringAssert.Contains("group=Body", advisories[0].Detail);
            StringAssert.Contains("bone count expected 1 but found 0", advisories[0].Detail);
        }

        /// <summary>
        /// Each installer in one group is compared with its own captured signature: no installer's verdict stands
        /// in for the others.
        /// </summary>
        /// <remarks>
        /// The previous implementation picked the first captured signature as the group's "expected" one and let
        /// the context-level rule run through it, so a group whose installers disagreed carried one arbitrary
        /// verdict anchored on the first part. Here the second installer is the one that disagrees, and the report
        /// must name it.
        /// </remarks>
        [Test]
        public void GroupCompatibility_IsCheckedPerInstaller_NotThroughTheFirstSignature()
        {
            var avatar = NewGameObject("Avatar", null);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, bodyMesh);

            NewPartInstaller(avatar, body, bodyMesh, "part-a", ApaPartSlot.LeftArm, "Body");
            var failing = NewPartInstaller(avatar, body, bodyMesh, "part-b", ApaPartSlot.RightArm, "Body");
            failing.Profile.Compatibility.VertexCount = 999;

            var groups = ContextBuilder.BuildGroups(avatar, null, out var issues);

            Assert.AreEqual(0, groups.Count, "A blocking per-installer mismatch must produce no group.");

            var blocking = new List<ValidationIssue>();
            for (var i = 0; i < issues.Count; i++)
            {
                if (issues[i].IsBlocking) blocking.Add(issues[i]);
            }

            Assert.AreEqual(
                1,
                blocking.Count,
                "Exactly one installer disagrees, so exactly one blocking issue is expected:" + Describe(issues));
            Assert.AreEqual("part-b", blocking[0].PartId, "The verdict must name the installer that failed.");
            StringAssert.Contains("group=Body", blocking[0].Detail);
            StringAssert.Contains("vertex count expected 999", blocking[0].Detail);
        }

        // ---- activity predicate and ownership boundaries ----------------------------------------------

        /// <summary>An avatar with no active installer is "not applicable", not an error.</summary>
        [Test]
        public void TryBuild_WithNoInstallers_IsNotApplicable()
        {
            var avatar = NewGameObject("Avatar", null);
            NewRenderer("Body", avatar.transform, NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f)));

            var built = ContextBuilder.TryBuild(avatar, null, out var context, out var issues);

            Assert.IsFalse(built);
            Assert.IsNull(context);
            Assert.IsFalse(
                issues.Exists(issue => issue.IsBlocking),
                "No installer is not a failure." + Describe(issues));
            Assert.IsTrue(
                issues.Exists(issue => issue.Code == ApaErrorCode.InactiveInstallerSkipped &&
                                       issue.Detail.Contains("reason=no-active-installers")),
                Describe(issues));

            // The legacy entry point keeps its M2 contract, which does treat it as an error.
            var legacy = ContextBuilder.Build(avatar, null, out var legacyIssues);
            Assert.IsNull(legacy);
            Assert.IsTrue(
                legacyIssues.Exists(issue => issue.IsBlocking && issue.Detail.Contains("reason=no-installers")),
                Describe(legacyIssues));
        }

        /// <summary>Every inactive condition parks the part, and parking is reported rather than silent.</summary>
        [Test]
        public void CollectInstallers_SkipsInactiveInstallers_AndReportsThem()
        {
            var rig = CreateTwoTargetRig();

            // 1. The author's own switch.
            rig.InstallerB.EnabledForBuild = false;
            var issues = new List<ValidationIssue>();
            var active = ContextBuilder.CollectInstallers(rig.Avatar, issues);

            Assert.AreEqual(1, active.Count);
            Assert.AreSame(rig.InstallerA, active[0]);
            Assert.IsTrue(
                issues.Exists(issue => issue.Code == ApaErrorCode.InactiveInstallerSkipped &&
                                       issue.Detail.Contains("enabledForBuild=false")),
                Describe(issues));

            // 2. A disabled component.
            rig.InstallerB.EnabledForBuild = true;
            rig.InstallerB.enabled = false;
            issues = new List<ValidationIssue>();
            active = ContextBuilder.CollectInstallers(rig.Avatar, issues);

            Assert.AreEqual(1, active.Count);
            Assert.IsTrue(
                issues.Exists(issue => issue.Detail.Contains("componentDisabled=true")),
                Describe(issues));

            // 3. An inactive GameObject.
            rig.InstallerB.enabled = true;
            rig.InstallerB.gameObject.SetActive(false);
            issues = new List<ValidationIssue>();
            active = ContextBuilder.CollectInstallers(rig.Avatar, issues);

            Assert.AreEqual(1, active.Count);
            Assert.IsTrue(
                issues.Exists(issue => issue.Detail.Contains("gameObjectInactive=true")),
                Describe(issues));

            // The shared predicate is the single source of truth for all three.
            Assert.IsTrue(rig.InstallerA.IsActiveForBuild);
            Assert.IsFalse(rig.InstallerB.IsActiveForBuild);
        }

        /// <summary>A nested avatar's installers are not this avatar's to install.</summary>
        [Test]
        public void CollectInstallers_StopsAtANestedAvatarBoundary()
        {
            ContextBuilder.RegisterAvatarBoundaryType(typeof(TestAvatarBoundary));

            var rig = CreateTwoTargetRig();

            var nested = NewGameObject("Nested Avatar", rig.Avatar.transform);
            nested.AddComponent<TestAvatarBoundary>();

            var nestedHost = NewGameObject("Nested Part Host", nested.transform);
            var nestedRenderer = NewRenderer(
                "Part", nestedHost.transform, NewRingMesh("NestedPartMesh", new Vector3(0f, 0f, -1f)));
            var nestedInstaller = nestedHost.AddComponent<AvatarPartInstaller>();
            nestedInstaller.Profile = NewProfile(rig.BodyMesh, "part-nested", ApaPartSlot.Head, "Body");
            nestedInstaller.PartRoot = nestedRenderer;
            nestedInstaller.TargetRendererObject = rig.Body;

            Assert.IsTrue(ContextBuilder.IsAvatarBoundary(nested), "The registered type must mark a boundary.");

            var active = ContextBuilder.CollectInstallers(rig.Avatar);
            Assert.AreEqual(2, active.Count, "The nested avatar's installer must not be collected.");
            for (var i = 0; i < active.Count; i++)
            {
                Assert.AreNotEqual("part-nested", active[i].ResolvePartId());
            }
        }

        // ---- rig -------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Body;
            public GameObject BodyAlt;
            public Mesh BodyMesh;
            public AvatarPartInstaller InstallerA;
            public AvatarPartInstaller InstallerB;
        }

        /// <summary>
        /// "Avatar" with two body renderers ("Body" and "BodyAlt") and one installer per body, each naming its
        /// target explicitly and recording the matching path.
        /// </summary>
        private Rig CreateTwoTargetRig()
        {
            var avatar = NewGameObject("Avatar", null);

            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, bodyMesh);

            var bodyAltMesh = NewRingMesh("BodyAltMesh", new Vector3(0f, 0f, 2f));
            var bodyAlt = NewRenderer("BodyAlt", avatar.transform, bodyAltMesh);

            var hostA = NewGameObject("Part Host A", avatar.transform);
            var partA = NewRenderer("Part", hostA.transform, NewRingMesh("PartMeshA", new Vector3(0f, 0f, -1f)));
            var installerA = hostA.AddComponent<AvatarPartInstaller>();
            installerA.Profile = NewProfile(bodyMesh, "part-a", ApaPartSlot.LeftArm, "Body");
            installerA.PartRoot = partA;
            installerA.TargetRendererObject = body;

            var hostB = NewGameObject("Part Host B", avatar.transform);
            var partB = NewRenderer("Part", hostB.transform, NewRingMesh("PartMeshB", new Vector3(0f, 0f, -2f)));
            var installerB = hostB.AddComponent<AvatarPartInstaller>();
            installerB.Profile = NewProfile(bodyAltMesh, "part-b", ApaPartSlot.RightArm, "BodyAlt");
            installerB.PartRoot = partB;
            installerB.TargetRendererObject = bodyAlt;

            return new Rig
            {
                Avatar = avatar,
                Body = body,
                BodyAlt = bodyAlt,
                BodyMesh = bodyMesh,
                InstallerA = installerA,
                InstallerB = installerB
            };
        }

        /// <summary>
        /// A profile whose signature is captured from the body mesh exactly the way authoring captures it, with
        /// the seam declaring the four ring vertices on both sides.
        /// </summary>
        private ApaPartProfile NewProfile(Mesh bodyMesh, string partId, ApaPartSlot slot, string rendererPath)
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);

            profile.Identity.PartId = partId;
            profile.Identity.Slot = slot;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(bodyMesh, rendererPath, string.Empty);

            // The seam is explicitly paired, which is the only form the build consumes (M10).
            profile.Seam = new ApaSeamProfile();
            profile.Seam.SetPaired(Range(4), Range(4));

            return profile;
        }

        /// <summary>
        /// A part host under the avatar: one installer naming the given target explicitly, with a profile that
        /// records the matching renderer path.
        /// </summary>
        private AvatarPartInstaller NewPartInstaller(
            GameObject avatar,
            GameObject target,
            Mesh bodyMesh,
            string partId,
            ApaPartSlot slot,
            string rendererPath)
        {
            var host = NewGameObject("Part Host " + partId, avatar.transform);
            var partRenderer = NewRenderer(
                "Part " + partId, host.transform, NewRingMesh("PartMesh-" + partId, new Vector3(0f, 0f, -1f)));

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(bodyMesh, partId, slot, rendererPath);
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = target;
            return installer;
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

        /// <summary>A readable ring plus apex, the same seam topology the pure-core fixtures use.</summary>
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

        private static List<ValidationIssue> AllIssues(
            TargetGroupAssemblyResult result,
            List<ValidationIssue> discovery)
        {
            var all = new List<ValidationIssue>();
            if (discovery != null) all.AddRange(discovery);
            if (result != null && result.Issues != null) all.AddRange(result.Issues.Issues);
            return all;
        }

        /// <summary>Every issue whose detail carries a token, so a report can be counted instead of inspected.</summary>
        private static List<ValidationIssue> FindAll(IReadOnlyList<ValidationIssue> issues, string detailToken)
        {
            var found = new List<ValidationIssue>();
            if (issues == null) return found;

            for (var i = 0; i < issues.Count; i++)
            {
                if (issues[i].Detail != null && issues[i].Detail.Contains(detailToken)) found.Add(issues[i]);
            }

            return found;
        }

        private static string Describe(List<ValidationIssue> issues)
        {
            return Describe((IReadOnlyList<ValidationIssue>)issues);
        }

        private static string Describe(IReadOnlyList<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return " (no issues)";

            var builder = new StringBuilder();
            for (var i = 0; i < issues.Count; i++) builder.Append('\n').Append(issues[i]);
            return builder.ToString();
        }
    }
}

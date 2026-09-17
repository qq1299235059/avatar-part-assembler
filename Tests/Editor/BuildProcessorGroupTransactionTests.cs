using System.Collections.Generic;
using System.Text;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Ndmf;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the NDMF build processor's target-group transaction on a real hierarchy: every group is planned
    /// before anything is built, one mesh is produced per target group, and a failure leaves the clone untouched
    /// with no mesh handed out.
    /// </summary>
    /// <remarks>
    /// The test assembly references <c>dev.avatar-part-assembler.editor.ndmf</c>, so these tests drive the real
    /// processor rather than a copy of its logic. What they cover is exactly what cannot be seen from the core
    /// tests: which renderers are written, which source renderers are consumed afterwards, and that consumption
    /// only happens once every group succeeded.
    /// </remarks>
    public sealed class BuildProcessorGroupTransactionTests
    {
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
        /// Two installers naming two targets produce one mesh per target group, each written to its own renderer,
        /// and both part renderers are consumed.
        /// </summary>
        [Test]
        public void Process_WithTwoTargets_WritesOneMeshPerGroupAndConsumesBothParts()
        {
            var rig = CreateTwoTargetRig();

            var result = ApaBuildProcessor.Process(new ApaBuildRequest(rig.Avatar));

            try
            {
                Assert.IsTrue(result.Succeeded, Describe(result));
                Assert.IsFalse(result.NothingToDo);

                Assert.AreEqual(2, result.Groups.Count, "One mesh per target group is the contract.");
                Assert.AreEqual("Body", result.Groups[0].GroupKey, "Groups are reported in ordinal key order.");
                Assert.AreEqual("BodyAlt", result.Groups[1].GroupKey);

                Assert.AreSame(rig.Body.GetComponent<Renderer>(), result.Groups[0].TargetRenderer);
                Assert.AreSame(rig.BodyAlt.GetComponent<Renderer>(), result.Groups[1].TargetRenderer);

                for (var i = 0; i < result.Groups.Count; i++)
                {
                    var group = result.Groups[i];
                    Assert.IsNotNull(group.GeneratedMesh, "Every group must carry a generated mesh.");
                    Assert.AreSame(
                        group.GeneratedMesh,
                        group.TargetRenderer.GetComponent<MeshFilter>().sharedMesh,
                        "Each group's mesh must be written to its own target renderer.");
                }

                Assert.AreNotSame(
                    result.Groups[0].GeneratedMesh,
                    result.Groups[1].GeneratedMesh,
                    "Groups must not share a mesh.");

                Assert.AreEqual(2, result.ConsumedRenderers.Count, "Both part renderers must be consumed.");
                Assert.AreEqual(2, result.ConsumedInstallers.Count, "Both installers must be consumed.");

                for (var i = 0; i < result.ConsumedRenderers.Count; i++)
                {
                    Assert.IsTrue(
                        result.ConsumedRenderers[i] == null,
                        "A consumed renderer must have been destroyed.");
                }

                for (var i = 0; i < result.ConsumedInstallers.Count; i++)
                {
                    Assert.IsTrue(
                        result.ConsumedInstallers[i] == null,
                        "A consumed installer must have been destroyed.");
                }

                // The legacy single-group convenience stays meaningful: it is the first group, not "the" group.
                Assert.AreSame(result.Groups[0].GeneratedMesh, result.GeneratedMesh);
                Assert.AreSame(result.Groups[0].TargetRenderer, result.TargetRenderer);
            }
            finally
            {
                for (var i = 0; i < result.Groups.Count; i++)
                {
                    if (result.Groups[i].GeneratedMesh != null)
                    {
                        Object.DestroyImmediate(result.Groups[i].GeneratedMesh);
                    }
                }
            }
        }

        /// <summary>
        /// A blocking conflict in one installer produces no group, no mesh and no consumption: the failure is
        /// reported without partial output.
        /// </summary>
        [Test]
        public void Process_WithABlockingConflict_ProducesNoMeshAndConsumesNothing()
        {
            var rig = CreateTwoTargetRig();

            // part-b was authored against "BodyAlt" and is now pointed at "Body": one assembly naming two bodies.
            rig.InstallerB.TargetRendererObject = rig.Body;

            var bodyMeshBefore = rig.Body.GetComponent<MeshFilter>().sharedMesh;
            var altMeshBefore = rig.BodyAlt.GetComponent<MeshFilter>().sharedMesh;

            var result = ApaBuildProcessor.Process(new ApaBuildRequest(rig.Avatar));

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(0, result.Groups.Count, "A failed run must not hand out a group.");
            Assert.IsNull(result.GeneratedMesh);
            Assert.AreEqual(0, result.ConsumedRenderers.Count, "Nothing may be consumed on a failed run.");
            Assert.AreEqual(0, result.ConsumedInstallers.Count);

            Assert.IsTrue(
                HasBlockingIssueWith(result.Issues.Issues, "reason=conflicting-target-renderers"),
                Describe(result));

            Assert.AreSame(bodyMeshBefore, rig.Body.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(altMeshBefore, rig.BodyAlt.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(rig.InstallerB != null, "The installers must survive a failed run.");
            Assert.IsTrue(rig.PartB != null, "The part renderers must survive a failed run.");
        }

        /// <summary>An avatar with no active installer is "not applicable", not a failure and not a mutation.</summary>
        [Test]
        public void Process_WithNoActiveInstaller_DoesNothing()
        {
            var rig = CreateTwoTargetRig();
            rig.InstallerA.EnabledForBuild = false;
            rig.InstallerB.gameObject.SetActive(false);

            var result = ApaBuildProcessor.Process(new ApaBuildRequest(rig.Avatar));

            Assert.IsTrue(result.NothingToDo);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(0, result.Groups.Count);
            Assert.AreEqual(0, result.Issues.ErrorCount, Describe(result));
        }

        /// <summary>
        /// The consumption planner reports the part renderers under one group's part roots and never the target.
        /// </summary>
        [Test]
        public void ConsumptionPlanner_ForOneGroup_ExcludesTheTargetAndOtherGroupsParts()
        {
            var rig = CreateTwoTargetRig();

            var groups = ContextBuilder.BuildGroups(rig.Avatar, null, out var groupIssues);
            Assert.AreEqual(2, groups.Count, Describe(groupIssues));

            var installers = ContextBuilder.CollectInstallers(rig.Avatar);
            Assert.AreEqual(2, installers.Count);

            var targets = new[] { groups[0].TargetRenderer, groups[1].TargetRenderer };

            var issues = new List<ValidationIssue>();
            var consumption = ApaPartConsumptionPlanner.ForGroup(
                rig.Avatar, groups[0].Context, installers, targets, issues);

            Assert.AreEqual(1, consumption.Renderers.Count, Describe(issues));
            Assert.AreSame(rig.PartA.GetComponent<Renderer>(), consumption.Renderers[0]);
            Assert.AreEqual(1, consumption.Installers.Count);
            Assert.AreSame(rig.InstallerA, consumption.Installers[0]);
            Assert.AreNotSame(
                rig.PartB.GetComponent<Renderer>(),
                consumption.Renderers[0],
                "Another group's part must not be consumed by this group.");

            var second = ApaPartConsumptionPlanner.ForGroup(
                rig.Avatar, groups[1].Context, installers, targets, issues);
            Assert.AreEqual(1, second.Renderers.Count, Describe(issues));
            Assert.AreSame(rig.PartB.GetComponent<Renderer>(), second.Renderers[0]);
        }

        /// <summary>
        /// A target renderer of another group is never consumed, even when it lives under the consumed part root:
        /// the build must not be able to destroy the body another group assembles into.
        /// </summary>
        [Test]
        public void ConsumptionPlanner_NeverConsumesAnotherGroupsTargetRenderer()
        {
            var rig = CreateNestedTargetRig();

            var groups = ContextBuilder.BuildGroups(rig.Avatar, null, out var groupIssues);
            Assert.AreEqual(2, groups.Count, Describe(groupIssues));

            var installers = ContextBuilder.CollectInstallers(rig.Avatar);
            var targets = new[] { groups[0].TargetRenderer, groups[1].TargetRenderer };

            var issues = new List<ValidationIssue>();
            var consumption = ApaPartConsumptionPlanner.ForGroup(
                rig.Avatar, groups[0].Context, installers, targets, issues);

            Assert.AreSame(rig.PartA.GetComponent<Renderer>(), consumption.Renderers[0]);
            CollectionAssert.DoesNotContain(
                consumption.Renderers,
                rig.BodyAlt.GetComponent<Renderer>(),
                "Another group's target renderer must never enter a consumption set, even from inside the part " +
                "root.");
        }

        /// <summary>
        /// A target that lives inside another group's part root is a blocking conflict: the run produces no group
        /// and no mesh, and both targets survive untouched.
        /// </summary>
        /// <remarks>
        /// This is the configuration the old consumption rule destroyed: group A's part root contained group B's
        /// target, so consuming A's part also destroyed B's target renderer after the run had already reported
        /// success — B's geometry silently disappeared from an otherwise successful build.
        /// </remarks>
        [Test]
        public void Process_WithATargetInsideAnotherGroupsPartRoot_BlocksAndKeepsBothTargetsAlive()
        {
            var rig = CreateNestedTargetRig();

            var bodyMeshBefore = rig.Body.GetComponent<MeshFilter>().sharedMesh;
            var altMeshBefore = rig.BodyAlt.GetComponent<MeshFilter>().sharedMesh;

            var result = ApaBuildProcessor.Process(new ApaBuildRequest(rig.Avatar));

            Assert.IsFalse(result.Succeeded, "A cross-group nesting conflict must fail the whole avatar.");
            Assert.AreEqual(0, result.Groups.Count, "A failed run must not hand out a group.");
            Assert.IsNull(result.GeneratedMesh);
            Assert.AreEqual(0, result.ConsumedRenderers.Count, "Nothing may be consumed on a failed run.");
            Assert.AreEqual(0, result.ConsumedInstallers.Count);

            Assert.IsTrue(
                HasBlockingIssueWith(result.Issues.Issues, "reason=target-inside-another-groups-part-root"),
                Describe(result));

            Assert.IsTrue(rig.Body != null, "The first group's target must survive the refused run.");
            Assert.IsTrue(rig.BodyAlt != null, "The nested group's target must survive the refused run.");
            Assert.AreSame(bodyMeshBefore, rig.Body.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(altMeshBefore, rig.BodyAlt.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(rig.InstallerA != null, "The installers must survive a failed run.");
            Assert.IsTrue(rig.InstallerB != null, "The installers must survive a failed run.");
        }

        /// <summary>
        /// A second renderer under a part root is consumed with the part and reported, and the capture records
        /// which renderer the part's geometry actually came from.
        /// </summary>
        [Test]
        public void ConsumptionPlanner_WithAnExtraRendererUnderAPartRoot_ConsumesItAndReportsIt()
        {
            var rig = CreateTwoTargetRig();

            // A second renderer below the part's own renderer: the part root now carries geometry the profile
            // does not describe, which the part's geometry has to replace as a whole.
            var extra = NewRenderer(
                "Extra", rig.PartA.transform, NewRingMesh("ExtraMesh", new Vector3(0f, 0f, -3f)));
            var partRenderer = rig.PartA.GetComponent<Renderer>();

            var groups = ContextBuilder.BuildGroups(rig.Avatar, null, out var groupIssues);
            Assert.AreEqual(2, groups.Count, Describe(groupIssues));
            Assert.AreSame(
                partRenderer,
                groups[0].Context.Parts[0].SourceRenderer,
                "The snapshot must record the renderer the capture read from, not hierarchy index zero.");

            var installers = ContextBuilder.CollectInstallers(rig.Avatar);
            var targets = new[] { groups[0].TargetRenderer, groups[1].TargetRenderer };

            var issues = new List<ValidationIssue>();
            var consumption = ApaPartConsumptionPlanner.ForGroup(
                rig.Avatar, groups[0].Context, installers, targets, issues);

            CollectionAssert.Contains(consumption.Renderers, partRenderer);
            CollectionAssert.Contains(consumption.Renderers, extra.GetComponent<Renderer>());

            var reported = 0;
            for (var i = 0; i < issues.Count; i++)
            {
                if (issues[i].Detail != null &&
                    issues[i].Detail.Contains("reason=additional-part-renderer"))
                {
                    reported++;
                }
            }

            Assert.AreEqual(
                1,
                reported,
                "Exactly the renderer that is not the captured one is reported as an additional part renderer." +
                Describe(issues));
        }

        // ---- rig -------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Body;
            public GameObject BodyAlt;
            public GameObject PartA;
            public GameObject PartB;
            public AvatarPartInstaller InstallerA;
            public AvatarPartInstaller InstallerB;
        }

        /// <summary>
        /// "Avatar" with two body renderers and one installer per body, each naming its target explicitly and
        /// recording the matching path — the two-group shape the grouped build exists for.
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
                PartA = partA,
                PartB = partB,
                InstallerA = installerA,
                InstallerB = installerB
            };
        }

        /// <summary>
        /// The two-target rig with the second group's target moved <i>under the first group's part root</i> — the
        /// cross-group nesting the consumption rule has to refuse. The second profile is re-captured against the
        /// renderer's new path, so the only defect in the rig is the nesting itself.
        /// </summary>
        private Rig CreateNestedTargetRig()
        {
            var rig = CreateTwoTargetRig();

            // "Part Host A/Part" is group A's part root and carries group A's part renderer; BodyAlt becomes a
            // child of it, so group A's part root now contains group B's target.
            rig.BodyAlt.transform.SetParent(rig.PartA.transform, false);

            rig.InstallerB.Profile = NewProfile(
                rig.BodyAlt.GetComponent<MeshFilter>().sharedMesh,
                "part-b",
                ApaPartSlot.RightArm,
                MeshSnapshotFactory.RelativePath(rig.Avatar.transform, rig.BodyAlt.transform));

            return rig;
        }

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

        private static string Describe(ApaBuildResult result)
        {
            if (result == null) return " (no result)";
            return Describe(result.Issues != null ? result.Issues.Issues : null);
        }

        private static bool HasBlockingIssueWith(IReadOnlyList<ValidationIssue> issues, string detailToken)
        {
            if (issues == null) return false;

            for (var i = 0; i < issues.Count; i++)
            {
                var issue = issues[i];
                if (issue == null || !issue.IsBlocking) continue;
                if (issue.Detail != null && issue.Detail.Contains(detailToken)) return true;
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
}

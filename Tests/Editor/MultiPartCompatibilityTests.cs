using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// The two compatibility-signature fields whose behaviour M6 changed: the bone signature, which now blocks
    /// whenever a source carries skinning data, and the recorded renderer path, which is now compared against the
    /// resolved body instead of being diagnostic-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shared-mesh case at the end is the failure the renderer-path comparison exists to close: two renderers
    /// that share one mesh asset report identical vertex counts, index counts, topologies and blend shape names,
    /// so without the path every signature field matches for either body and a part authored against one can be
    /// welded into the other with no diagnostic.
    /// </para>
    /// <para>
    /// The fixtures are local because the shared <c>MeshFixtures</c> help is used by the M2 suite and this file
    /// must not change what those tests assert.
    /// </para>
    /// </remarks>
    public sealed class MultiPartCompatibilityTests
    {
        private static readonly string[] BodyBones = { "Armature/Hips", "Armature/Spine" };
        private static readonly string[] PartBones = { "Armature/Hips", "Armature/Prop" };

        private static readonly Vector3 HipsPosition = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 SpinePosition = new Vector3(0f, 2f, 0f);
        private static readonly Vector3 PropPosition = new Vector3(0f, 3f, 0f);

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

        // ---- the bone signature: blocking exactly when skinning data exists ---------------------------

        /// <summary>
        /// A bone-path mismatch blocks once a source in the configuration carries skinning data.
        /// </summary>
        /// <remarks>
        /// This is the M6 promotion recorded in spec section 43.4: after the armature merge the final bone table
        /// is rebuilt from the live hierarchy, so a part authored against a different hierarchy is merged against
        /// bones it was never authored for.
        /// </remarks>
        [Test]
        public void SkinnedBoneSignatureMismatch_Blocks()
        {
            var body = SkinnedBody();
            var part = SkinnedPart();
            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = new[] { "Armature/Hips", "Armature/Other" };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                signature: signature));

            Assert.IsFalse(result.Succeeded, "A bone mismatch with skinned sources must block.");
            var issue = result.Issues.FindByCode(ApaErrorCode.PartProfileIncompatible);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Error, issue.Severity);
            StringAssert.Contains("reason=bone-signature-mismatch", issue.Detail);
        }

        /// <summary>
        /// A repeated final path segment is accepted for the same-named wrapper transforms inserted by
        /// marshmallow_PB, while retaining the live path on the snapshot for later bone resolution.
        /// </summary>
        [Test]
        public void SkinnedBoneSignature_AllowsRepeatedFinalSegmentWrapper()
        {
            const string expectedPath = "Armature/Spine";
            const string livePath = "Armature/Spine/Spine/Spine";
            var body = SkinnedBody(new[] { "Armature/Hips", livePath });
            var part = SkinnedPart();
            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = new[] { "Armature/Hips", expectedPath };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                signature: signature,
                marshmallowPbCompatibilityEnabled: true));

            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible),
                "Only repeated copies of the recorded final segment should be tolerated. " +
                result.Issues.FormatAll());
            Assert.AreEqual(livePath, body.BoneSignature.PathAt(1),
                "Compatibility must not rewrite the captured live hierarchy path.");
        }

        /// <summary>
        /// The same path shape remains a mismatch when the caller did not detect Marshmallow PB. This keeps a
        /// hand-authored duplicate hierarchy from receiving the vendor-specific exception by accident.
        /// </summary>
        [Test]
        public void RepeatedFinalSegmentWrapper_RequiresMarshmallowPbDetection()
        {
            const string expectedPath = "Armature/Spine";
            const string livePath = "Armature/Spine/Spine";
            var body = SkinnedBody(new[] { "Armature/Hips", livePath });
            var part = SkinnedPart();
            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = new[] { "Armature/Hips", expectedPath };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                signature: signature));

            Assert.IsFalse(result.Succeeded, "The wrapper exception is only valid after Marshmallow PB detection.");
            Assert.IsTrue(
                result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible),
                result.Issues.FormatAll());
        }

        /// <summary>A real added hierarchy segment still blocks even when the last segment happens to match.</summary>
        [Test]
        public void SkinnedBoneSignature_StillBlocksDifferentHierarchyChange()
        {
            var body = SkinnedBody(new[] { "Armature/Hips", "Armature/Spine/Other" });
            var part = SkinnedPart();
            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = new[] { "Armature/Hips", "Armature/Spine" };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                signature: signature));

            Assert.IsFalse(result.Succeeded, "A different added bone path must remain blocking.");
            var issue = result.Issues.FindByCode(ApaErrorCode.PartProfileIncompatible);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Error, issue.Severity);
            StringAssert.Contains("reason=bone-signature-mismatch", issue.Detail);
        }

        /// <summary>
        /// The identical mismatch stays a warning when nothing in the configuration is skinned: there are no
        /// weights to remap and no bone table to build, so the stored paths are a staleness signal only.
        /// </summary>
        /// <remarks>
        /// The body still records bone paths, and those paths must come with usable world-to-local transforms.
        /// <c>FinalBoneTable.HasSkinningInputs</c> counts a non-empty bone signature as a skinning input, so a
        /// body with paths but no matrices builds a bone table, fails <c>APA011 reason=missing-bone-transform</c>,
        /// and never reaches a verdict about the signature at all — the fixture would be testing a broken scene
        /// rather than the advisory boundary.
        /// </remarks>
        [Test]
        public void UnskinnedBoneSignatureMismatch_StaysAdvisory()
        {
            var body = UnskinnedBodyWithBones(BodyBones);
            var part = MeshFixtures.Part(4, apexOffset: -1f);
            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = new[] { "Armature/Hips", "Armature/Other" };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                signature: signature));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            var issue = result.Issues.FindByCode(ApaErrorCode.PartProfileIncompatible);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Warning, issue.Severity);
            StringAssert.Contains("reason=bone-signature-advisory", issue.Detail);
            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.InvalidBindPose),
                "The advisory case must not be blocked by a bone transform problem: " +
                result.Issues.FormatAll());
        }

        /// <summary>
        /// The advisory branch is also reachable with no bone data at all, which is the only configuration in
        /// which no bone table exists.
        /// </summary>
        /// <remarks>
        /// A recorded signature that lists bone paths while the live body has none is a real staleness signal: the
        /// profile was captured against a body that had an armature. Nothing is skinned, so there is nothing to
        /// remap and the difference cannot change the output; it stays a warning, and the plan carries no bone
        /// table at all.
        /// </remarks>
        [Test]
        public void UnskinnedBodyWithoutBoneData_ReportsTheAdvisoryAndBuildsNoBoneTable()
        {
            var body = UnskinnedBodyWithoutBones();
            var part = MeshFixtures.Part(4, apexOffset: -1f);
            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = BodyBones;

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(4)) },
                signature: signature));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(result.Plan.RequiresSkinning, "An unskinned plan must not carry a bone table.");
            Assert.IsNull(result.Plan.BoneTable);

            var issue = result.Issues.FindByCode(ApaErrorCode.PartProfileIncompatible);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Warning, issue.Severity);
            StringAssert.Contains("reason=bone-signature-advisory", issue.Detail);
            StringAssert.Contains("bone count expected 2 but found 0", issue.Detail);
        }

        // ---- the recorded renderer path ---------------------------------------------------------------

        /// <summary>
        /// A recorded renderer path that differs from the resolved body is a blocking signature mismatch, even
        /// when every mesh field matches.
        /// </summary>
        [Test]
        public void RendererPathMismatch_BlocksAsSignatureMismatch()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            // The profile was authored against a renderer at "Body"; the pipeline resolved "BodyAlt". The two
            // renderers share the mesh asset, so only the recorded path can tell them apart.
            var signature = MeshFixtures.SignatureFor(body, rendererPath: "Body");
            var baseSnapshot = new BaseSnapshot(
                body,
                default,
                System.Array.Empty<Material>(),
                "BodyAlt");

            var context = new ValidationContext(
                baseSnapshot,
                ValidationContext.SortParts(
                    new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) }),
                ApaNumericPolicy.Default,
                signature);

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "A recorded path that names another renderer must block.");
            var issue = result.Issues.FindByCode(ApaErrorCode.PartProfileIncompatible);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Error, issue.Severity);
            StringAssert.Contains("reason=renderer-path-mismatch", issue.Detail);
        }

        /// <summary>
        /// Two renderers sharing one mesh asset do not collapse into the first one: the single-target entry point
        /// blocks, and the group entry point resolves both recorded targets as two groups.
        /// </summary>
        /// <remarks>
        /// This is the audit's G-2 failure mode. The old resolver returned the first renderer whose recorded path
        /// resolved and every other installer's signature then passed, because the shared mesh makes vertex
        /// count, index counts, topologies and blend shape names identical. Resolving every installer's own path
        /// is what removes the silent wrong target.
        /// </remarks>
        [Test]
        public void TwoRenderersSharingOneMesh_DoNotCollapseIntoOneTarget()
        {
            var avatar = NewGameObject("Avatar", null);
            var sharedMesh = NewRingMesh("SharedBodyMesh", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, sharedMesh);
            var bodyAlt = NewRenderer("BodyAlt", avatar.transform, sharedMesh);
            Assert.AreSame(body.GetComponent<MeshFilter>().sharedMesh, bodyAlt.GetComponent<MeshFilter>().sharedMesh);

            var hostA = NewGameObject("Part Host A", avatar.transform);
            var partA = NewRenderer("Part A", hostA.transform, NewRingMesh("PartMeshA", new Vector3(0f, 0f, -1f)));
            var installerA = hostA.AddComponent<AvatarPartInstaller>();
            installerA.Profile = NewProfile(sharedMesh, "part-a", ApaPartSlot.LeftArm, "Body");
            installerA.PartRoot = partA;

            var hostB = NewGameObject("Part Host B", avatar.transform);
            var partB = NewRenderer("Part B", hostB.transform, NewRingMesh("PartMeshB", new Vector3(0f, 0f, -2f)));
            var installerB = hostB.AddComponent<AvatarPartInstaller>();
            installerB.Profile = NewProfile(sharedMesh, "part-b", ApaPartSlot.RightArm, "BodyAlt");
            installerB.PartRoot = partB;

            var context = ContextBuilder.Build(avatar, null, out var issues);

            Assert.IsNull(context, "Two recorded targets must not silently collapse into the first one.");
            Assert.IsTrue(
                issues.Exists(issue => issue.IsBlocking &&
                                       issue.Detail != null &&
                                       issue.Detail.Contains("reason=conflicting-target-renderers")),
                Describe(issues));

            // The same avatar is legitimate under the group model: two recorded targets are two groups.
            var groups = ContextBuilder.BuildGroups(avatar, null, out var groupIssues);

            Assert.AreEqual(2, groups.Count, Describe(groupIssues));
            Assert.AreEqual("Body", groups[0].GroupKey);
            Assert.AreEqual("BodyAlt", groups[1].GroupKey);
            Assert.AreSame(body.GetComponent<Renderer>(), groups[0].TargetRenderer);
            Assert.AreSame(bodyAlt.GetComponent<Renderer>(), groups[1].TargetRenderer);
            Assert.AreEqual("part-a", groups[0].Context.Parts[0].PartId);
            Assert.AreEqual("part-b", groups[1].Context.Parts[0].PartId);
        }

        // ---- fixtures ---------------------------------------------------------------------------------

        /// <summary>A skinned four-vertex ring plus apex, the same shape the skinning suite uses.</summary>
        private static MeshSnapshot SkinnedBody(string[] bonePaths = null)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };
            var weights = MeshFixtures.UniformWeights(positions.Count, 0);
            weights[4] = MeshFixtures.BlendWeight(0, 0.5f, 1, 0.5f);

            return MeshFixtures.SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                bonePaths ?? BodyBones,
                weights,
                MeshFixtures.BonesAt(HipsPosition, SpinePosition));
        }

        /// <summary>A skinned part whose seam ring coincides with the body's.</summary>
        private static MeshSnapshot SkinnedPart()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, -1f) };
            var weights = MeshFixtures.UniformWeights(positions.Count, 0);
            weights[4] = new BoneWeight { boneIndex0 = 1, weight0 = 1f };

            return MeshFixtures.SkinnedSnapshot(
                "Part",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                PartBones,
                weights,
                MeshFixtures.BonesAt(HipsPosition, PropPosition));
        }

        /// <summary>
        /// An unskinned body that still records bone paths, so the only difference from the skinned case is the
        /// absence of weights.
        /// </summary>
        /// <remarks>
        /// The paths carry usable world-to-local transforms (<see cref="MeshFixtures.BonesAt"/>), because a
        /// non-empty bone signature is a skinning input for <c>FinalBoneTable.HasSkinningInputs</c>: without the
        /// matrices the plan would fail with <c>APA011 reason=missing-bone-transform</c> before the bone-signature
        /// verdict was ever reached.
        /// </remarks>
        private static MeshSnapshot UnskinnedBodyWithBones(string[] bonePaths)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };

            return MeshSnapshot.Create(
                "Body",
                positions.ToArray(),
                System.Array.Empty<Vector3>(),
                System.Array.Empty<Vector4>(),
                System.Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { MeshFixtures.CapTriangles(4, 0, 4) },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16,
                bonePaths: bonePaths,
                boneWorldToLocalMatrices: MeshFixtures.BonesAt(HipsPosition, SpinePosition));
        }

        /// <summary>
        /// A body with no bone data whatsoever: no weights, no bind poses, no bone paths, no world-to-local
        /// matrices. This is the configuration in which no bone table exists at all.
        /// </summary>
        private static MeshSnapshot UnskinnedBodyWithoutBones()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, 1f) };

            return MeshSnapshot.Create(
                "Body",
                positions.ToArray(),
                System.Array.Empty<Vector3>(),
                System.Array.Empty<Vector4>(),
                System.Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { MeshFixtures.CapTriangles(4, 0, 4) },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16);
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

        private static string Describe(List<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return " (no issues)";

            var builder = new StringBuilder();
            for (var i = 0; i < issues.Count; i++) builder.Append('\n').Append(issues[i]);
            return builder.ToString();
        }
    }
}

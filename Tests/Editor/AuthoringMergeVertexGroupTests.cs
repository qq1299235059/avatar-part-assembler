using System.Collections.Generic;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the named <c>merge vertex</c> group contract: how a renderer's candidate vertices are resolved
    /// from an <see cref="ApaMergeVertexGroup"/> component or from a skinned bone of that name, which malformed
    /// groups are refused with <c>APA051</c>, and that only resolved candidates can ever be paired.
    /// </summary>
    /// <remarks>
    /// The distinction this file is built around is the one the contract exists for: "no group" is a blocking
    /// defect, never an instruction to fall back to every vertex. Every failure test therefore asserts both the
    /// code and the stable <c>reason=</c> token, and asserts that the returned index list is empty — a failure
    /// that carried candidates would be the fallback in disguise.
    /// </remarks>
    public sealed class AuthoringMergeVertexGroupTests
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

        // ---- The metadata component --------------------------------------------------------------------

        [Test]
        public void Resolve_ComponentGroupIsSortedAndUsedVerbatim()
        {
            var mesh = NewMesh("Body", FourVertices);
            var renderer = NewSkinnedRenderer("Body", mesh, null);
            var group = renderer.gameObject.AddComponent<ApaMergeVertexGroup>();
            group.SetVertexIndices(new[] { 3, 1 }, mesh.vertexCount);

            var result = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);

            Assert.IsTrue(result.Succeeded, result.Issue != null ? result.Issue.Detail : string.Empty);
            Assert.AreEqual(ApaMergeVertexGroupSource.MetadataComponent, result.Source);
            Assert.AreEqual(4, result.VertexCount);
            CollectionAssert.AreEqual(new[] { 1, 3 }, result.Indices, "The group is returned in ascending order.");
            Assert.IsNotEmpty(result.Describe(), "A resolved group must describe itself for the window.");
        }

        [Test]
        public void Resolve_ComponentIsAuthoritativeEvenWhenAGroupBoneExists()
        {
            var mesh = NewMesh("Body", FourVertices);
            mesh.boneWeights = new[]
            {
                Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f)
            };

            var bone = NewBone(ApaMergeVertexGroup.GroupName);
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { bone });

            var group = renderer.gameObject.AddComponent<ApaMergeVertexGroup>();
            group.SetVertexIndices(new[] { 2 }, mesh.vertexCount);

            var result = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(
                ApaMergeVertexGroupSource.MetadataComponent,
                result.Source,
                "An attached component is the group; presence is authoritative, not one source among several.");
            CollectionAssert.AreEqual(new[] { 2 }, result.Indices);
        }

        [Test]
        public void Resolve_EmptyComponentFailsInsteadOfFallingBackToTheBone()
        {
            var mesh = NewMesh("Body", FourVertices);
            mesh.boneWeights = new[] { Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f) };
            var bone = NewBone(ApaMergeVertexGroup.GroupName);
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { bone });

            var group = renderer.gameObject.AddComponent<ApaMergeVertexGroup>();
            group.SetVertexIndices(null, mesh.vertexCount);

            Assert.IsTrue(group.IsEmpty);

            var result = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);

            AssertFailure(result, "reason=merge-vertex-group-empty");
        }

        [Test]
        public void Resolve_OutOfRangeIndexFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var renderer = NewSkinnedRenderer("Body", mesh, null);
            renderer.gameObject.AddComponent<ApaMergeVertexGroup>().SetVertexIndices(new[] { 0, 9 }, 4);

            var result = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);

            AssertFailure(result, "reason=merge-vertex-group-index-out-of-range");
            Assert.AreEqual(9, result.Issue.SourceIndex, "The offending index must stay visible in the report.");
        }

        [Test]
        public void Resolve_NegativeIndexFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var renderer = NewSkinnedRenderer("Body", mesh, null);
            renderer.gameObject.AddComponent<ApaMergeVertexGroup>().SetVertexIndices(new[] { -1 }, 4);

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-index-out-of-range");
        }

        [Test]
        public void Resolve_DuplicateIndexFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var renderer = NewSkinnedRenderer("Body", mesh, null);
            renderer.gameObject.AddComponent<ApaMergeVertexGroup>().SetVertexIndices(new[] { 2, 2 }, 4);

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-duplicate-index");
        }

        [Test]
        public void Resolve_GroupRecordedAgainstADifferentMeshSizeFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var renderer = NewSkinnedRenderer("Body", mesh, null);
            renderer.gameObject.AddComponent<ApaMergeVertexGroup>().SetVertexIndices(new[] { 1 }, 12);

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-vertex-count-mismatch");
        }

        [Test]
        public void Resolve_ComponentWithoutARecordedMeshSizeStillValidatesTheIndices()
        {
            var mesh = NewMesh("Body", FourVertices);
            var renderer = NewSkinnedRenderer("Body", mesh, null);
            renderer.gameObject.AddComponent<ApaMergeVertexGroup>().SetVertexIndices(new[] { 2 }, -1);

            var result = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);

            Assert.IsTrue(result.Succeeded, "An unrecorded size is 'unknown', not a mismatch.");
            CollectionAssert.AreEqual(new[] { 2 }, result.Indices);
        }

        [Test]
        public void Component_CopyVertexIndicesDoesNotExposeTheSerializedArray()
        {
            var host = NewGameObject("Body");
            var group = host.AddComponent<ApaMergeVertexGroup>();
            group.SetVertexIndices(new[] { 1, 2 }, 4);

            var copy = group.CopyVertexIndices();
            copy[0] = 99;

            CollectionAssert.AreEqual(
                new[] { 1, 2 },
                group.CopyVertexIndices(),
                "A read must not be able to rewrite the authored group.");
            Assert.AreEqual(4, group.RecordedVertexCount);
        }

        // ---- The skinned bone --------------------------------------------------------------------------

        [Test]
        public void Resolve_BoneWeightsDefineTheGroupByStrictlyPositiveWeight()
        {
            var mesh = NewMesh("Body", FourVertices);
            var groupBone = NewBone(ApaMergeVertexGroup.GroupName);
            var otherBone = NewBone("Hips");

            mesh.boneWeights = new[]
            {
                // Vertex 0: fully in the group.
                Weighted(0, 1f),
                // Vertex 1: shared with another bone, still in the group.
                new BoneWeight { boneIndex0 = 0, weight0 = 0.25f, boneIndex1 = 1, weight1 = 0.75f },
                // Vertex 2: assigned to the group bone but with no influence: importers write this, and it is
                // not membership.
                new BoneWeight { boneIndex0 = 0, weight0 = 0f, boneIndex1 = 1, weight1 = 1f },
                // Vertex 3: not in the group at all.
                Weighted(1, 1f)
            };

            var renderer = NewSkinnedRenderer("Body", mesh, new[] { groupBone, otherBone });

            var result = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.PartSide);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(ApaMergeVertexGroupSource.SkinnedBone, result.Source);
            CollectionAssert.AreEqual(
                new[] { 0, 1 },
                result.Indices,
                "Only vertices with a strictly positive weight to the group bone are members.");
        }

        [Test]
        public void Resolve_BonePathIsDeterministicAcrossRepeatedResolutions()
        {
            var mesh = NewMesh("Body", FourVertices);
            var groupBone = NewBone(ApaMergeVertexGroup.GroupName);
            mesh.boneWeights = new[] { Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f) };
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { groupBone });

            var first = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);
            var second = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);

            CollectionAssert.AreEqual(first.Indices, second.Indices);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, first.Indices);
        }

        [Test]
        public void Resolve_OneTransformListedInTwoBoneSlotsIsStillOneBone()
        {
            var mesh = NewMesh("Body", FourVertices);
            var groupBone = NewBone(ApaMergeVertexGroup.GroupName);
            mesh.boneWeights = new[] { Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f) };

            // The same transform in two slots is one bone, not an ambiguous pair.
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { groupBone, groupBone });

            var result = ApaMergeVertexGroupResolver.Resolve(
                renderer, mesh, ApaMergeVertexGroupResolver.TargetSide);

            Assert.IsTrue(result.Succeeded, result.Issue != null ? result.Issue.Detail : string.Empty);
            Assert.AreEqual(4, result.Indices.Length);
        }

        [Test]
        public void Resolve_TwoDistinctBonesWithTheGroupNameAreRefused()
        {
            var mesh = NewMesh("Body", FourVertices);
            var first = NewBone(ApaMergeVertexGroup.GroupName);
            var second = NewBone(ApaMergeVertexGroup.GroupName);
            mesh.boneWeights = new[] { Weighted(0, 1f), Weighted(1, 1f), Weighted(0, 1f), Weighted(1, 1f) };

            var renderer = NewSkinnedRenderer("Body", mesh, new[] { first, second });

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-ambiguous-bone");
        }

        [Test]
        public void Resolve_AnAlmostRightBoneNameIsNotTheGroup()
        {
            var mesh = NewMesh("Body", FourVertices);
            var bone = NewBone("Merge Vertex");
            mesh.boneWeights = new[] { Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f) };
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { bone });

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-missing",
                "The comparison is ordinal: 'Merge Vertex' is a different name.");
        }

        [Test]
        public void Resolve_GroupBoneThatWeightsNothingFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var groupBone = NewBone(ApaMergeVertexGroup.GroupName);
            var otherBone = NewBone("Hips");
            mesh.boneWeights = new[] { Weighted(1, 1f), Weighted(1, 1f), Weighted(1, 1f), Weighted(1, 1f) };
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { groupBone, otherBone });

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-no-weighted-vertices");
        }

        [Test]
        public void Resolve_SkinnedRendererWithoutAGroupBoneFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var bone = NewBone("Hips");
            mesh.boneWeights = new[] { Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f), Weighted(0, 1f) };
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { bone });

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-missing");
        }

        [Test]
        public void Resolve_NonSkinnedRendererWithoutAComponentFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var host = NewGameObject("Body");
            var renderer = host.AddComponent<MeshRenderer>();

            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.PartSide),
                "reason=merge-vertex-group-not-skinned");
        }

        [Test]
        public void Resolve_GroupBoneOnAMeshWithoutBoneWeightsFails()
        {
            var mesh = NewMesh("Body", FourVertices);
            var groupBone = NewBone(ApaMergeVertexGroup.GroupName);
            var renderer = NewSkinnedRenderer("Body", mesh, new[] { groupBone });

            // No skinning data at all: the bone exists but weights nothing, so the group is empty.
            AssertFailure(
                ApaMergeVertexGroupResolver.Resolve(renderer, mesh, ApaMergeVertexGroupResolver.TargetSide),
                "reason=merge-vertex-group-no-weighted-vertices");
        }

        // ---- Missing inputs ----------------------------------------------------------------------------

        [Test]
        public void Resolve_MissingRendererOrMeshReportsTheMatchersOwnCodes()
        {
            var mesh = NewMesh("Body", FourVertices);

            var missingTarget = ApaMergeVertexGroupResolver.Resolve(
                null, mesh, ApaMergeVertexGroupResolver.TargetSide);
            Assert.IsFalse(missingTarget.Succeeded);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, missingTarget.Issue.Code);
            StringAssert.Contains("reason=missing-target-mesh", missingTarget.Issue.Detail);

            var host = NewGameObject("Part");
            var renderer = host.AddComponent<MeshRenderer>();
            var missingPart = ApaMergeVertexGroupResolver.Resolve(
                renderer, null, ApaMergeVertexGroupResolver.PartSide);
            Assert.IsFalse(missingPart.Succeeded);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, missingPart.Issue.Code);
            StringAssert.Contains("reason=missing-part-mesh", missingPart.Issue.Detail);
        }

        // ---- Candidate filtering: the acceptance the contract exists for --------------------------------

        /// <summary>
        /// A world-coincident vertex outside the group is never paired, and a part vertex that coincides only
        /// with such a vertex matches nothing at all.
        /// </summary>
        /// <remarks>
        /// This is the property the whole change is about, so it is asserted from both directions: the positive
        /// case proves the group vertex is the one used when both coincide, and the negative case proves the
        /// generator refuses rather than silently widening its search to the vertex the author did not nominate.
        /// </remarks>
        [Test]
        public void WorldMatcher_PairsOnlyGroupVerticesAndNeverCoincidentOutsiders()
        {
            // The body has two vertices at the origin (0 in the group, 1 not) and one far away (2, in the group).
            var targetMesh = NewMesh(
                "Body",
                new[] { Vector3.zero, Vector3.zero, new Vector3(5f, 5f, 5f) });
            var targetRenderer = NewSkinnedRenderer("Body", targetMesh, null);
            targetRenderer.gameObject.AddComponent<ApaMergeVertexGroup>()
                .SetVertexIndices(new[] { 0, 2 }, targetMesh.vertexCount);

            var partMesh = NewMesh("Part", new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });
            var partRenderer = NewSkinnedRenderer("Part", partMesh, null);
            partRenderer.gameObject.AddComponent<ApaMergeVertexGroup>()
                .SetVertexIndices(new[] { 0, 1 }, partMesh.vertexCount);

            var targetGroup = ApaMergeVertexGroupResolver.Resolve(
                targetRenderer, targetMesh, ApaMergeVertexGroupResolver.TargetSide);
            var partGroup = ApaMergeVertexGroupResolver.Resolve(
                partRenderer, partMesh, ApaMergeVertexGroupResolver.PartSide);

            Assert.IsTrue(targetGroup.Succeeded);
            Assert.IsTrue(partGroup.Succeeded);

            var matched = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                targetGroup.Indices,
                partGroup.Indices);

            Assert.IsTrue(matched.Succeeded, matched.Issue != null ? matched.Issue.Detail : string.Empty);
            CollectionAssert.AreEqual(
                new[] { 0, 2 },
                matched.BaseIndices,
                "The group vertex at the origin is the one claimed, not the coincident outsider.");
            CollectionAssert.AreEqual(new[] { 0, 1 }, matched.PartIndices);

            // Now move the group vertex away and leave the outsider exactly where the part vertex is: the
            // generator must refuse rather than pair the vertex the group excludes.
            targetMesh.vertices = new[] { new Vector3(5f, 5f, 5f), Vector3.zero, new Vector3(5f, 5f, 5f) };
            var outsiderOnly = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                targetGroup.Indices,
                new[] { 0 });

            Assert.IsFalse(
                outsiderOnly.Succeeded,
                "A coincident vertex outside the group must never be paired.");
            Assert.AreEqual(ApaErrorCode.SeamPositionMismatch, outsiderOnly.Issue.Code);
            StringAssert.Contains("reason=no-world-coincident-vertices", outsiderOnly.Issue.Detail);
            Assert.IsEmpty(outsiderOnly.BaseIndices);
        }

        [Test]
        public void WorldMatcher_ResolvedGroupsFromBonesProduceThePairedSeamTheWindowWrites()
        {
            var groupBone = NewBone(ApaMergeVertexGroup.GroupName);

            var targetMesh = NewMesh("Body", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) });
            targetMesh.boneWeights = new[] { Weighted(0, 1f), Weighted(0, 1f) };
            var targetRenderer = NewSkinnedRenderer("Body", targetMesh, new[] { groupBone });

            var partMesh = NewMesh("Part", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) });
            partMesh.boneWeights = new[] { Weighted(0, 1f), Weighted(0, 1f) };
            var partRenderer = NewSkinnedRenderer("Part", partMesh, new[] { groupBone });

            var targetGroup = ApaMergeVertexGroupResolver.Resolve(
                targetRenderer, targetMesh, ApaMergeVertexGroupResolver.TargetSide);
            var partGroup = ApaMergeVertexGroupResolver.Resolve(
                partRenderer, partMesh, ApaMergeVertexGroupResolver.PartSide);

            var matched = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                targetGroup.Indices,
                partGroup.Indices);

            Assert.IsTrue(matched.Succeeded);
            CollectionAssert.AreEqual(new[] { 0, 1 }, matched.BaseIndices);
            CollectionAssert.AreEqual(new[] { 0, 1 }, matched.PartIndices);

            var seam = new ApaSeamSelection();
            seam.SetPaired(matched.BaseIndices, matched.PartIndices);

            Assert.IsTrue(seam.IsPaired);
            Assert.IsTrue(seam.IsConsumable);
            Assert.AreEqual(2, seam.PairCount);
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private static readonly Vector3[] FourVertices =
        {
            Vector3.zero, Vector3.right, Vector3.up, Vector3.forward
        };

        private static BoneWeight Weighted(int boneIndex, float weight)
        {
            return new BoneWeight { boneIndex0 = boneIndex, weight0 = weight };
        }

        private static void AssertFailure(
            ApaMergeVertexGroupResult result,
            string reasonToken,
            string message = null)
        {
            Assert.IsFalse(result.Succeeded, "The group must be refused, not resolved.");
            Assert.AreEqual(ApaErrorCode.MergeVertexGroupInvalid, result.Issue.Code);
            StringAssert.Contains(reasonToken, result.Issue.Detail, message);
            Assert.IsEmpty(result.Indices, "A failure must never carry a fallback index list.");
            Assert.AreEqual(ApaMergeVertexGroupSource.None, result.Source);
        }

        private GameObject NewGameObject(string name)
        {
            var host = new GameObject(name);
            _created.Add(host);
            return host;
        }

        private Transform NewBone(string name)
        {
            return NewGameObject(name).transform;
        }

        private Mesh NewMesh(string name, Vector3[] vertices)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            _created.Add(mesh);
            return mesh;
        }

        private SkinnedMeshRenderer NewSkinnedRenderer(string name, Mesh mesh, Transform[] bones)
        {
            var renderer = NewGameObject(name).AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            if (bones != null) renderer.bones = bones;
            return renderer;
        }
    }
}

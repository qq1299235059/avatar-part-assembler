using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Pins the distinction between the strict authoring/preview armature scope and the verified post-Modular-
    /// Avatar build scope. The latter exists because MA retargets the live renderer's bones before APA captures
    /// the part for final assembly.
    /// </summary>
    public sealed class PostMergeArmatureScopeTests
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

        [Test]
        public void StrictScope_DoesNotAcceptTargetArmatureAsPartArmature()
        {
            var avatar = NewGameObject("Avatar");
            var targetArmature = NewGameObject("Armature", avatar.transform).transform;
            var targetBone = NewGameObject("Hips", targetArmature).transform;
            var partRoot = NewGameObject("Part", avatar.transform);
            var renderer = NewSkinnedRenderer(partRoot, targetBone);
            var issues = new List<ValidationIssue>();

            var resolved = ApaArmatureScope.TryResolvePartCaptureScope(
                partRoot.transform,
                "Armature",
                targetArmature,
                renderer,
                ApaNumericPolicy.Default,
                false,
                "part-a",
                issues,
                out var scope);

            Assert.IsFalse(resolved);
            Assert.IsNull(scope);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.ArmatureSelectionInvalid, issues[0].Code);
            StringAssert.Contains("reason=part-armature-outside-root", issues[0].Detail);
        }

        [Test]
        public void PostMergeScope_WhenWeightedBonesWereRetargeted_UsesTargetArmature()
        {
            var avatar = NewGameObject("Avatar");
            var targetArmature = NewGameObject("Armature", avatar.transform).transform;
            var targetBone = NewGameObject("Hips", targetArmature).transform;
            var partRoot = NewGameObject("Part", avatar.transform);
            var renderer = NewSkinnedRenderer(partRoot, targetBone);
            var issues = new List<ValidationIssue>();

            // This is the hierarchy APA sees after Modular Avatar: the profile still records the original
            // Part/Armature selection, but that object has been merged away and the live SMR now references the
            // avatar-side Hips transform.
            var resolved = ApaArmatureScope.TryResolvePartCaptureScope(
                partRoot.transform,
                "Armature",
                targetArmature,
                renderer,
                ApaNumericPolicy.Default,
                true,
                "part-a",
                issues,
                out var scope);

            Assert.IsTrue(resolved, Describe(issues));
            Assert.AreSame(targetArmature, scope);
            Assert.AreEqual(0, issues.Count, Describe(issues));
        }

        [Test]
        public void PostMergeScope_MissingSerializedSelectionStillBlocks()
        {
            var avatar = NewGameObject("Avatar");
            var targetArmature = NewGameObject("Armature", avatar.transform).transform;
            var targetBone = NewGameObject("Hips", targetArmature).transform;
            var partRoot = NewGameObject("Part", avatar.transform);
            var renderer = NewSkinnedRenderer(partRoot, targetBone);
            var issues = new List<ValidationIssue>();

            var resolved = ApaArmatureScope.TryResolvePartCaptureScope(
                partRoot.transform,
                string.Empty,
                targetArmature,
                renderer,
                ApaNumericPolicy.Default,
                true,
                "part-a",
                issues,
                out var scope);

            Assert.IsFalse(resolved);
            Assert.IsNull(scope);
            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("reason=missing-part-armature", issues[0].Detail);
        }

        [Test]
        public void PostMergeScope_RetainedDotArmatureButRetargetedBones_PrefersTargetScope()
        {
            var avatar = NewGameObject("Avatar");
            var targetArmature = NewGameObject("Armature", avatar.transform).transform;
            var targetBone = NewGameObject("Hips", targetArmature).transform;
            var partRoot = NewGameObject("Part", avatar.transform);
            var renderer = NewSkinnedRenderer(partRoot, targetBone);
            var issues = new List<ValidationIssue>();

            // MA may retain a merge root that carries extra components. If the selected part armature was the
            // part root itself ("."), the path still resolves even though every effective weight now targets the
            // avatar armature. This case must not fall back to the stale retained object.
            var resolved = ApaArmatureScope.TryResolvePartCaptureScope(
                partRoot.transform,
                ApaAvatarPath.Root,
                targetArmature,
                renderer,
                ApaNumericPolicy.Default,
                true,
                "part-a",
                issues,
                out var scope);

            Assert.IsTrue(resolved, Describe(issues));
            Assert.AreSame(targetArmature, scope);
            Assert.AreEqual(0, issues.Count, Describe(issues));
        }

        private GameObject NewGameObject(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            _created.Add(go);
            return go;
        }

        private SkinnedMeshRenderer NewSkinnedRenderer(GameObject host, Transform bone)
        {
            var renderer = host.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = host.name + "_Mesh" };
            mesh.vertices = new[] { Vector3.zero };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f }
            };
            mesh.bindposes = new[] { Matrix4x4.identity };
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;
            _created.Add(mesh);
            return renderer;
        }

        private static string Describe(IReadOnlyList<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return string.Empty;
            var text = string.Empty;
            for (var i = 0; i < issues.Count; i++)
            {
                text += "\n" + issues[i];
            }

            return text;
        }
    }
}

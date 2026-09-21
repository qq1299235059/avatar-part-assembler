using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the removal set: canonical ordering, duplicate reporting, whole-set replacement, and the
    /// structural checks that give the mask block live feedback.
    /// </summary>
    /// <remarks>
    /// The structural checks are asserted against the same codes and detail tokens the assembly core's
    /// <c>RemovalRule</c> emits, because the authoring layer is only allowed to say the same thing earlier — not
    /// to say something different. The per-triangle toggle, the submesh bulk delete, and the address-list parser
    /// are gone with mask-only authoring, so nothing here drives them any more.
    /// </remarks>
    public sealed class AuthoringRemovalMaskTests
    {
        [Test]
        public void Add_KeepsTheSetCanonicalAndFreeOfDuplicates()
        {
            var mask = new ApaRemovalMask();

            Assert.IsTrue(mask.Add(new RemovedTriangleAddress(1, 5)));
            Assert.IsTrue(mask.Add(new RemovedTriangleAddress(0, 9)));
            Assert.IsTrue(mask.Add(new RemovedTriangleAddress(0, 2)));
            Assert.IsFalse(mask.Add(new RemovedTriangleAddress(0, 9)), "A repeated address is not added twice.");

            Assert.AreEqual(3, mask.Count);
            Assert.AreEqual(new RemovedTriangleAddress(0, 2), mask.GetAddress(0));
            Assert.AreEqual(new RemovedTriangleAddress(0, 9), mask.GetAddress(1));
            Assert.AreEqual(new RemovedTriangleAddress(1, 5), mask.GetAddress(2));
            Assert.IsTrue(mask.Contains(new RemovedTriangleAddress(1, 5)));
            Assert.IsFalse(mask.Contains(new RemovedTriangleAddress(1, 6)));
        }

        [Test]
        public void AddRange_ReportsDuplicatesInsteadOfSilentlyDroppingThem()
        {
            var mask = new ApaRemovalMask();
            mask.Add(new RemovedTriangleAddress(0, 1));

            var duplicates = new List<RemovedTriangleAddress>();
            var added = mask.AddRange(new[]
            {
                new RemovedTriangleAddress(0, 1),
                new RemovedTriangleAddress(0, 2),
                new RemovedTriangleAddress(0, 2)
            }, duplicates);

            Assert.AreEqual(1, added);
            Assert.AreEqual(2, duplicates.Count);
            Assert.AreEqual(2, mask.Count);
        }

        [Test]
        public void RemoveAndClearBehaveOnTheCanonicalSet()
        {
            var mask = new ApaRemovalMask();
            mask.AddRange(new[]
            {
                new RemovedTriangleAddress(0, 1),
                new RemovedTriangleAddress(0, 2),
                new RemovedTriangleAddress(1, 1)
            }, null);

            Assert.IsTrue(mask.Remove(new RemovedTriangleAddress(0, 1)));
            Assert.IsFalse(mask.Remove(new RemovedTriangleAddress(0, 1)));
            Assert.AreEqual(2, mask.Count);

            mask.Clear();
            Assert.IsTrue(mask.IsEmpty);
        }

        [Test]
        public void SetFromAddresses_ReplacesTheWholeSetCanonically()
        {
            // The mask's Apply action writes through this: a Replace is one whole-set edit, and the canonical
            // invariant (sorted, duplicate-free, parallel arrays) has to hold for whatever the sampler produced.
            var mask = new ApaRemovalMask();
            mask.AddRange(new[]
            {
                new RemovedTriangleAddress(0, 1),
                new RemovedTriangleAddress(1, 4)
            }, null);

            mask.SetFromAddresses(new[]
            {
                new RemovedTriangleAddress(2, 7),
                new RemovedTriangleAddress(0, 3),
                new RemovedTriangleAddress(2, 7)
            });

            Assert.AreEqual(2, mask.Count);
            Assert.AreEqual(new RemovedTriangleAddress(0, 3), mask.GetAddress(0));
            Assert.AreEqual(new RemovedTriangleAddress(2, 7), mask.GetAddress(1));
            Assert.IsFalse(
                mask.Contains(new RemovedTriangleAddress(1, 4)),
                "A replace is a replacement, not a merge.");
            Assert.IsFalse(mask.HasCorruptStorage);
        }

        [Test]
        public void ToRemovalProfile_RoundTripsThroughTheSerializedForm()
        {
            var mask = new ApaRemovalMask();
            mask.AddRange(new[]
            {
                new RemovedTriangleAddress(2, 7),
                new RemovedTriangleAddress(0, 3)
            }, null);

            var profile = mask.ToRemovalProfile();
            var restored = ApaRemovalMask.FromRemovalProfile(profile);

            Assert.AreEqual(2, profile.Count);
            Assert.IsFalse(profile.HasCorruptStorage);
            Assert.AreEqual(mask.Count, restored.Count);
            CollectionAssert.AreEqual(mask.ToAddresses(), restored.ToAddresses());
        }

        [Test]
        public void Validate_ReportsSubmeshRangeTopologyAndTriangleRangeWithCoreCodes()
        {
            var mask = new ApaRemovalMask();
            mask.AddRange(new[]
            {
                new RemovedTriangleAddress(0, 1),   // valid
                new RemovedTriangleAddress(5, 0),   // submesh out of range
                new RemovedTriangleAddress(1, 0),   // submesh 1 is a line list
                new RemovedTriangleAddress(2, 9),   // triangle out of range in a 1-triangle submesh
                new RemovedTriangleAddress(-1, 0)   // negative component
            }, null);

            var counts = new List<int> { 4, 0, 1 };
            var topologies = new List<MeshTopology> { MeshTopology.Triangles, MeshTopology.Lines, MeshTopology.Triangles };

            var issues = mask.Validate(counts, topologies, "part-1");

            // Five addresses, four defects: the valid one reports nothing, and each of the other four reports the
            // specific reason rather than a generic "out of range".
            Assert.AreEqual(4, issues.Count);
            Assert.AreEqual(3, CountCode(issues, ApaErrorCode.InvalidTriangleAddress));
            Assert.AreEqual(1, CountCode(issues, ApaErrorCode.RemovalIndexOutOfRange));

            var outOfRange = FindDetail(issues, ApaErrorCode.RemovalIndexOutOfRange);
            StringAssert.Contains("triangleCount=1", outOfRange.Detail);

            var topology = FindDetail(issues, ApaErrorCode.InvalidTriangleAddress, "reason=not-a-triangle-list");
            Assert.IsNotNull(topology, "A triangle address in a line submesh must be reported as a category error.");

            var subMeshRange = FindDetail(issues, ApaErrorCode.InvalidTriangleAddress, "reason=submesh-out-of-range");
            Assert.IsNotNull(subMeshRange);
            StringAssert.Contains("subMeshCount=3", subMeshRange.Detail);

            var negative = FindDetail(issues, ApaErrorCode.InvalidTriangleAddress, "reason=negative-component");
            Assert.IsNotNull(negative);
        }

        [Test]
        public void Validate_WithoutAMeshReportsNothingButCorruptStorage()
        {
            var mask = new ApaRemovalMask();
            mask.Add(new RemovedTriangleAddress(3, 3));

            Assert.IsEmpty(mask.Validate(null, null, "part-1"));
        }

        [Test]
        public void Validate_ReportsCorruptStorageRatherThanGuessing()
        {
            // A serialized form with mismatched arrays is what a hand-edited or partially written asset looks
            // like. The missing component cannot be reconstructed, so it must be reported, never repaired.
            var mask = JsonUtility.FromJson<ApaRemovalMask>(
                "{\"_subMeshIndices\":[0,1],\"_triangleIndices\":[4]}");

            Assert.IsTrue(mask.HasCorruptStorage);

            var issues = mask.Validate(new List<int> { 10, 10 }, null, "part-1");

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.InvalidTriangleAddress, issues[0].Code);
            StringAssert.Contains("reason=corrupt-removal-storage", issues[0].Detail);
        }

        [Test]
        public void Describe_CountsTrianglesAndSubmeshes()
        {
            var mask = new ApaRemovalMask();
            Assert.AreEqual("no triangles removed", mask.Describe());

            mask.AddRange(new[]
            {
                new RemovedTriangleAddress(0, 0),
                new RemovedTriangleAddress(0, 1),
                new RemovedTriangleAddress(2, 0)
            }, null);

            Assert.AreEqual("3 triangle(s) in 2 submesh(es)", mask.Describe());
        }

        // ---- Mask-only authoring -----------------------------------------------------------------------

        [Test]
        public void RemovalRegion_KeepsOnlyTheMaskInputsAndTheGeneratedSelectionReview()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            var region = Between(
                window,
                "private void DrawRemovalSection()",
                "private void DrawTextureMaskSection(");

            StringAssert.Contains("DrawTextureMaskSection(mask);", region, "The mask inputs are the authoring surface.");
            StringAssert.Contains("DrawAddressList(mask);", region, "The generated selection is still reviewable.");
            StringAssert.Contains("mask.Describe()", region, "The summary line stays.");
            StringAssert.Contains("Clear", region, "The whole-set clear stays: it is not a per-triangle edit.");

            foreach (var gone in new[]
                     {
                         "DrawModeButton",
                         "Pick Triangles",
                         "Add Address",
                         "Add List",
                         "Remove All In Submesh",
                         "IntField",
                         "TextField"
                     })
            {
                Assert.IsFalse(
                    region.Contains(gone),
                    "The Removal Region must carry no manual triangle or address entry UI: " + gone);
            }
        }

        [Test]
        public void RemovalAddressList_IsAReadOnlyReview()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");
            var list = Between(window, "private void DrawAddressList(", "// ---- Actions");

            StringAssert.Contains("mask.GetAddress(i)", list);
            Assert.IsFalse(list.Contains("mask.Remove("), "No per-triangle remove control may remain.");
            Assert.IsFalse(list.Contains("Add Address"), "The list is a review, not an editing surface.");
            Assert.IsFalse(list.Contains("Add List"), "The list is a review, not an editing surface.");
            Assert.IsFalse(list.Contains("Remove All In Submesh"), "The list is a review, not an editing surface.");
        }

        [Test]
        public void TheManualEditingBackendIsGone()
        {
            var mask = ReadEditorSource("Authoring", "ApaRemovalMask.cs");

            Assert.IsFalse(mask.Contains("TryParseAddressList"), "The address text field's parser is gone with it.");
            Assert.IsFalse(mask.Contains("RemoveSubMesh"), "The submesh bulk delete is gone with its button.");
            Assert.IsFalse(mask.Contains("public bool Toggle("), "The per-triangle toggle served the pick mode only.");

            // The picking helper and its budgets were the click path's; the array cache the overlays read stayed.
            var authoring = PackageEditorPath("Authoring");
            Assert.IsFalse(
                System.IO.File.Exists(System.IO.Path.Combine(authoring, "ApaScenePicking.cs")),
                "The Scene View picking helper is removed, not left as dead code.");
            Assert.IsTrue(System.IO.File.Exists(System.IO.Path.Combine(authoring, "ApaMeshArrayCache.cs")));
        }

        private static string Between(string source, string startToken, string endToken)
        {
            var start = source.IndexOf(startToken, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, "Missing member: " + startToken);

            var end = source.IndexOf(endToken, start, System.StringComparison.Ordinal);
            Assert.Greater(end, start, "Missing end marker: " + endToken);

            return source.Substring(start, end - start);
        }

        private static string ReadEditorSource(string folder, string fileName)
        {
            var path = System.IO.Path.Combine(PackageEditorPath(folder), fileName);
            if (!System.IO.File.Exists(path)) Assert.Ignore("Editor source not found: " + path);

            return System.IO.File.ReadAllText(path);
        }

        private static string PackageEditorPath(string folder)
        {
            var projectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, ".."));
            return System.IO.Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", folder);
        }

        private static int CountCode(List<ValidationIssue> issues, string code)
        {
            var count = 0;
            for (var i = 0; i < issues.Count; i++)
            {
                if (string.Equals(issues[i].Code, code, System.StringComparison.Ordinal)) count++;
            }

            return count;
        }

        private static ValidationIssue FindDetail(List<ValidationIssue> issues, string code, string detailFragment = null)
        {
            for (var i = 0; i < issues.Count; i++)
            {
                if (!string.Equals(issues[i].Code, code, System.StringComparison.Ordinal)) continue;
                if (detailFragment != null && issues[i].Detail.IndexOf(detailFragment, System.StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                return issues[i];
            }

            return null;
        }
    }
}

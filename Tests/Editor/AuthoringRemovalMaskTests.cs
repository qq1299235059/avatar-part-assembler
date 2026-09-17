using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the editable removal set: canonical ordering, duplicate reporting, and the structural checks
    /// that give the picker live feedback.
    /// </summary>
    /// <remarks>
    /// The structural checks are asserted against the same codes and detail tokens the assembly core's
    /// <c>RemovalRule</c> emits, because the authoring layer is only allowed to say the same thing earlier — not
    /// to say something different.
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
        public void RemoveToggleAndRemoveSubMeshBehaveOnTheCanonicalSet()
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

            Assert.IsTrue(mask.Toggle(new RemovedTriangleAddress(1, 4)), "Toggling an absent address selects it.");
            Assert.IsFalse(mask.Toggle(new RemovedTriangleAddress(1, 4)), "Toggling a present address removes it.");

            Assert.AreEqual(1, mask.RemoveSubMesh(0));
            Assert.AreEqual(1, mask.Count);
            Assert.AreEqual(new RemovedTriangleAddress(1, 1), mask.GetAddress(0));

            mask.Clear();
            Assert.IsTrue(mask.IsEmpty);
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

        [Test]
        public void TryParseAddressList_ParsesSingleAddressesListsAndRanges()
        {
            Assert.IsTrue(ApaRemovalMask.TryParseAddressList("0:12", out var single, out var error), error);
            Assert.AreEqual(1, single.Length);
            Assert.AreEqual(new RemovedTriangleAddress(0, 12), single[0]);

            Assert.IsTrue(ApaRemovalMask.TryParseAddressList("0:12, 1:3; 2:0", out var list, out error), error);
            Assert.AreEqual(3, list.Length);

            Assert.IsTrue(ApaRemovalMask.TryParseAddressList("1:4-7", out var range, out error), error);
            Assert.AreEqual(4, range.Length);
            Assert.AreEqual(new RemovedTriangleAddress(1, 4), range[0]);
            Assert.AreEqual(new RemovedTriangleAddress(1, 7), range[3]);
        }

        [Test]
        public void TryParseAddressList_PreservesDuplicatesForValidationToReport()
        {
            Assert.IsTrue(ApaRemovalMask.TryParseAddressList("0:1, 0:1", out var parsed, out var error), error);
            Assert.AreEqual(2, parsed.Length, "Author input is preserved; duplicates are reported, not collapsed.");
        }

        [Test]
        public void TryParseAddressList_ReportsStableErrors()
        {
            Assert.IsFalse(ApaRemovalMask.TryParseAddressList("012", out _, out var malformed));
            Assert.AreEqual("malformed:012", malformed);

            Assert.IsFalse(ApaRemovalMask.TryParseAddressList("0:x", out _, out var notANumber));
            Assert.AreEqual("malformed:0:x", notANumber);

            Assert.IsFalse(ApaRemovalMask.TryParseAddressList("0:7-3", out _, out var reversed));
            Assert.AreEqual("invalid-range:0:7-3", reversed);

            Assert.IsFalse(ApaRemovalMask.TryParseAddressList("0:0-200000", out _, out var huge));
            StringAssert.StartsWith("too-large:", huge);
        }

        [Test]
        public void TryParseAddressList_TreatsEmptyInputAsAnEmptySelection()
        {
            Assert.IsTrue(ApaRemovalMask.TryParseAddressList(null, out var none, out var error), error);
            Assert.IsEmpty(none);

            Assert.IsTrue(ApaRemovalMask.TryParseAddressList("   ", out var blank, out error), error);
            Assert.IsEmpty(blank);
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

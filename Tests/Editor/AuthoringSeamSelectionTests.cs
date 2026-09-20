using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the paired seam loops: the paired writer, the per-side editors that downgrade to the legacy
    /// state, the world-position generator that produces the pairs, the numeric fallback, and the checks that
    /// mirror the strict seam contract's codes.
    /// </summary>
    /// <remarks>
    /// The distinction this file is built around is the M10 one: <see cref="ApaSeamSelection.SetPaired"/> writes
    /// the only state the build can consume, while every per-side editor (<c>SetBase</c>, <c>TryAddPart</c>, …)
    /// deliberately leaves the legacy, unpaired state behind, because editing one side of a pair destroys the
    /// correspondence the editor no longer knows.
    /// </remarks>
    public sealed class AuthoringSeamSelectionTests
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
        public void SetBase_SortsAscendingKeepsDuplicatesAndDowngradesToLegacy()
        {
            var seam = new ApaSeamSelection();
            seam.SetBase(new[] { 5, 1, 3, 1 });

            CollectionAssert.AreEqual(new[] { 1, 1, 3, 5 }, seam.GetBaseIndices());
            Assert.AreEqual(4, seam.BaseCount);

            // One side of a pairing has been rewritten, so the correspondence is no longer known: the selection
            // falls back to the legacy state and the seam rule asks for a regeneration rather than guessing.
            Assert.IsFalse(seam.IsPaired);
            Assert.AreEqual(ApaSeamProfile.LegacyUnpairedVersion, seam.PairingVersion);
        }

        [Test]
        public void SetPaired_PreservesTheOrderItIsGivenAndRoundTripsTheVersion()
        {
            var seam = new ApaSeamSelection();

            // Deliberately out of ascending order on both sides: a pair position is author data, and sorting
            // either list would weld each vertex to an unrelated one.
            seam.SetPaired(new[] { 5, 1 }, new[] { 9, 2 });

            Assert.IsTrue(seam.IsPaired);
            Assert.AreEqual(ApaSeamProfile.ExplicitPairingVersion, seam.PairingVersion);
            Assert.AreEqual(2, seam.PairCount);
            Assert.IsTrue(seam.IsConsumable);
            CollectionAssert.AreEqual(new[] { 5, 1 }, seam.GetBaseIndices());
            CollectionAssert.AreEqual(new[] { 9, 2 }, seam.GetPartIndices());

            var profile = seam.ToSeamProfile();
            Assert.AreEqual(ApaSeamProfile.ExplicitPairingVersion, profile.PairingVersion);
            CollectionAssert.AreEqual(new[] { 5, 1 }, profile.Base.VertexIndices);
            CollectionAssert.AreEqual(new[] { 9, 2 }, profile.Part.VertexIndices);

            var restored = ApaSeamSelection.FromSeamProfile(profile);
            Assert.IsTrue(restored.IsPaired);
            CollectionAssert.AreEqual(seam.GetBaseIndices(), restored.GetBaseIndices());
            CollectionAssert.AreEqual(seam.GetPartIndices(), restored.GetPartIndices());
        }

        [Test]
        public void SetPaired_KeepsALengthMismatchInsteadOfPadding()
        {
            var seam = new ApaSeamSelection();
            seam.SetPaired(new[] { 0, 1, 2 }, new[] { 0, 1 });

            Assert.AreEqual(3, seam.BaseCount);
            Assert.AreEqual(2, seam.PartCount);
            Assert.AreEqual(2, seam.PairCount, "The pair count is the smaller cardinality.");
            Assert.IsFalse(seam.IsConsumable, "A mismatched pairing is not consumable.");
        }

        [Test]
        public void Validate_ReportsALegacySelectionAsAPairingRequirement()
        {
            var seam = new ApaSeamSelection();
            seam.SetBase(new[] { 0, 1 });
            seam.SetPart(new[] { 0, 1 });

            var issues = seam.Validate(10, 10, "part-1");

            Assert.AreEqual(1, issues.Count, "An unpaired legacy seam is reported alone; nothing else is checkable.");
            Assert.AreEqual(ApaErrorCode.SeamPairingRequired, issues[0].Code);
            StringAssert.Contains("reason=seam-pairing-required", issues[0].Detail);
            StringAssert.Contains("pairingVersion=0", issues[0].Detail);
        }

        [Test]
        public void Validate_ReportsDuplicatesWithTheCoreDetailTokens()
        {
            var seam = new ApaSeamSelection();
            seam.SetPaired(new[] { 5, 1, 3, 1 }, new[] { 0, 1, 2, 3 });

            var issues = seam.Validate(6, 6, "part-1");

            Assert.AreEqual(1, issues.Count, "A duplicate index is author data the report must keep showing.");
            Assert.AreEqual(ApaErrorCode.InvalidSeamSelection, issues[0].Code);
            StringAssert.Contains("reason=duplicate", issues[0].Detail);
        }

        [Test]
        public void TryAdd_InsertsSortedAndRefusesDuplicates()
        {
            var seam = new ApaSeamSelection();

            Assert.IsTrue(seam.TryAddBase(4));
            Assert.IsTrue(seam.TryAddBase(2));
            Assert.IsTrue(seam.TryAddBase(9));
            Assert.IsFalse(seam.TryAddBase(4), "An index that is already selected is not added twice.");

            CollectionAssert.AreEqual(new[] { 2, 4, 9 }, seam.GetBaseIndices());
        }

        [Test]
        public void Remove_RemovesEveryOccurrence()
        {
            var seam = new ApaSeamSelection();
            seam.SetBase(new[] { 1, 2, 2, 3 });

            Assert.IsTrue(seam.RemoveBase(2));
            CollectionAssert.AreEqual(new[] { 1, 3 }, seam.GetBaseIndices());
            Assert.IsFalse(seam.RemoveBase(2));
        }

        [Test]
        public void Toggle_AddsAndRemoves()
        {
            var seam = new ApaSeamSelection();

            Assert.IsTrue(seam.TogglePart(7));
            Assert.IsTrue(seam.TogglePart(3));
            Assert.IsFalse(seam.TogglePart(7));
            CollectionAssert.AreEqual(new[] { 3 }, seam.GetPartIndices());
        }

        [Test]
        public void Validate_ReportsACardinalityMismatchAlone()
        {
            var seam = new ApaSeamSelection();
            seam.SetPaired(new[] { 0, 1, 2 }, new[] { 0, 1 });

            var issues = seam.Validate(10, 10, "part-1");

            Assert.AreEqual(1, issues.Count, "A cardinality mismatch is reported alone; matching is meaningless.");
            Assert.AreEqual(ApaErrorCode.SeamVertexCountMismatch, issues[0].Code);
            Assert.AreEqual("baseCount=3; partCount=2", issues[0].Detail);
        }

        [Test]
        public void Validate_ReportsOutOfRangeAndDuplicateWithTheCoreDetailTokens()
        {
            var seam = new ApaSeamSelection();
            seam.SetPaired(new[] { 0, 12 }, new[] { 0, 1 });

            var issues = seam.Validate(5, 5, "part-1");

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.InvalidSeamSelection, issues[0].Code);
            Assert.AreEqual("side=base; vertex=12; vertexCount=5", issues[0].Detail);

            seam.SetPaired(new[] { 3, -1 }, new[] { 0, 1 });
            issues = seam.Validate(5, 5, "part-1");

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.InvalidSeamSelection, issues[0].Code);
            StringAssert.Contains("vertex=-1", issues[0].Detail);
        }

        [Test]
        public void Validate_ReportsThePartSideAgainstThePartMesh()
        {
            var seam = new ApaSeamSelection();
            seam.SetPaired(new[] { 0, 1 }, new[] { 0, 9 });

            var issues = seam.Validate(4, 2, "part-1");

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual("side=part; vertex=9; vertexCount=2", issues[0].Detail);
        }

        [Test]
        public void Validate_OfAnEmptyOrValidSelectionReportsNothing()
        {
            var seam = new ApaSeamSelection();
            Assert.IsEmpty(seam.Validate(10, 10, "part-1"));

            seam.SetPaired(new[] { 0, 1, 2 }, new[] { 2, 3, 4 });
            Assert.IsEmpty(seam.Validate(10, 10, "part-1"));
        }

        [Test]
        public void Describe_DistinguishesEmptyLegacyAndPairedSelections()
        {
            var seam = new ApaSeamSelection();
            Assert.AreEqual("no seam pairs", seam.Describe());

            seam.SetBase(new[] { 0, 1 });
            seam.SetPart(new[] { 0, 1 });
            Assert.AreEqual("unpaired legacy seam", seam.Describe());

            seam.SetPaired(new[] { 0, 1 }, new[] { 4, 5 });
            Assert.AreEqual("2 seam pair(s)", seam.Describe());
            StringAssert.StartsWith("2 seam pair(s); first: 0, 1 -> 4, 5", seam.DescribePairs(2));
        }

        [Test]
        public void TryParseIndexList_ParsesListsAndRanges()
        {
            Assert.IsTrue(ApaSeamSelection.TryParseIndexList("0, 1, 2", out var list, out var error), error);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, list);

            Assert.IsTrue(ApaSeamSelection.TryParseIndexList("0-3", out var hyphen, out error), error);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, hyphen);

            Assert.IsTrue(ApaSeamSelection.TryParseIndexList("2..4", out var dots, out error), error);
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, dots);

            Assert.IsTrue(ApaSeamSelection.TryParseIndexList("0 1\n2\t3", out var whitespace, out error), error);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, whitespace);
        }

        [Test]
        public void TryParseIndexList_PreservesDuplicatesAndNegativeIndices()
        {
            Assert.IsTrue(ApaSeamSelection.TryParseIndexList("1, 1, -4", out var parsed, out var error), error);
            CollectionAssert.AreEqual(new[] { 1, 1, -4 }, parsed);

            var seam = new ApaSeamSelection();
            seam.SetPaired(new[] { 0, 1, 2 }, parsed);

            var issues = seam.Validate(8, 8, "part-1");
            Assert.AreEqual(2, issues.Count, "The duplicate and the negative index are both reported.");
        }

        [Test]
        public void TryParseIndexList_ReportsStableErrors()
        {
            Assert.IsFalse(ApaSeamSelection.TryParseIndexList("abc", out _, out var notANumber));
            Assert.AreEqual("not-a-number:abc", notANumber);

            Assert.IsFalse(ApaSeamSelection.TryParseIndexList("5-2", out _, out var reversed));
            Assert.AreEqual("invalid-range:5-2", reversed);

            Assert.IsFalse(ApaSeamSelection.TryParseIndexList("0-500000", out _, out var huge));
            StringAssert.StartsWith("too-large:", huge);
        }

        [Test]
        public void TryParseIndexList_TreatsEmptyInputAsAnEmptyLoop()
        {
            Assert.IsTrue(ApaSeamSelection.TryParseIndexList(string.Empty, out var none, out var error), error);
            Assert.IsEmpty(none);

            Assert.IsTrue(ApaSeamSelection.TryParseIndexList("   ", out var blank, out error), error);
            Assert.IsEmpty(blank);
        }

        [Test]
        public void FormatIndexList_RoundTripsThroughTheParser()
        {
            var original = new List<int> { 3, 5, 8 };
            var text = ApaSeamSelection.FormatIndexList(original);

            Assert.AreEqual("3, 5, 8", text);
            Assert.IsTrue(ApaSeamSelection.TryParseIndexList(text, out var parsed, out var error), error);
            CollectionAssert.AreEqual(original, parsed);
        }

        [Test]
        public void Clear_CanClearOneSideOrBoth()
        {
            var seam = new ApaSeamSelection();
            seam.SetBase(new[] { 0, 1 });
            seam.SetPart(new[] { 2, 3 });

            seam.Clear(ApaSeamSideKind.Base);
            Assert.AreEqual(0, seam.BaseCount);
            Assert.AreEqual(2, seam.PartCount);

            seam.Clear();
            Assert.IsTrue(seam.IsEmpty);
        }

        // ---- world-position generation (M10) ---------------------------------------------------------

        /// <summary>
        /// The generator pairs vertices by world position, one-to-one and deterministically, and reports the part
        /// vertices that found no counterpart.
        /// </summary>
        /// <remarks>
        /// The part sits under a parent scaled by two, so its local coordinates differ from the target's by that
        /// factor: the comparison happens in world space, which is the quantity an author can see in the Scene
        /// View and the one the stated tolerance is expressed in. The generated arrays are written through
        /// <see cref="ApaSeamSelection.SetPaired"/>, which is the state the build consumes.
        /// </remarks>
        [Test]
        public void WorldMatcher_PairsByWorldPositionOneToOne()
        {
            var targetHost = Track(new GameObject("Target"));
            var targetMesh = Track(NewMeshWithVertices(
                "TargetMesh",
                new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) }));
            targetHost.AddComponent<MeshFilter>().sharedMesh = targetMesh;
            var targetRenderer = targetHost.AddComponent<MeshRenderer>();

            var partParent = Track(new GameObject("Part Parent"));
            partParent.transform.localScale = new Vector3(2f, 2f, 2f);
            var partHost = Track(new GameObject("Part"));
            partHost.transform.SetParent(partParent.transform, false);
            var partMesh = Track(NewMeshWithVertices(
                "PartMesh",
                new[]
                {
                    // Half of the target's coordinates: twice these world positions coincide with the target's.
                    new Vector3(0f, 0f, 0f),
                    new Vector3(0.5f, 0f, 0f),
                    new Vector3(0f, 0.5f, 0f),
                    // Deliberately nowhere near the target.
                    new Vector3(9f, 9f, 9f)
                }));
            partHost.AddComponent<MeshFilter>().sharedMesh = partMesh;
            var partRenderer = partHost.AddComponent<MeshRenderer>();

            var first = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance);

            Assert.IsTrue(first.Succeeded, first.Describe());
            Assert.AreEqual(3, first.BaseVertexCount);
            Assert.AreEqual(4, first.PartVertexCount);
            Assert.AreEqual(3, first.PairCount);
            Assert.AreEqual(1, first.UnmatchedPartVertexCount, "The far-away part vertex has no counterpart.");
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, first.BaseIndices);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, first.PartIndices);

            var second = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance);

            CollectionAssert.AreEqual(first.BaseIndices, second.BaseIndices, "The match must be deterministic.");
            CollectionAssert.AreEqual(first.PartIndices, second.PartIndices);

            // The generated correspondence is the paired state the profile persists, not a set to be re-matched.
            var selection = new ApaSeamSelection();
            selection.SetPaired(first.BaseIndices, first.PartIndices);

            Assert.IsTrue(selection.IsPaired);
            Assert.IsTrue(selection.IsConsumable);
            Assert.AreEqual(3, selection.PairCount);
        }

        /// <summary>
        /// The generator's refusals carry the stable codes and reason tokens the window shows, so "nothing
        /// matched" and "the tolerance is unusable" are never confused with each other.
        /// </summary>
        [Test]
        public void WorldMatcher_ReportsTheStableFailureReasons()
        {
            var targetHost = Track(new GameObject("Target"));
            var targetMesh = Track(NewMeshWithVertices("TargetMesh", new[] { Vector3.zero, Vector3.right }));
            targetHost.AddComponent<MeshFilter>().sharedMesh = targetMesh;
            var targetRenderer = targetHost.AddComponent<MeshRenderer>();

            var partHost = Track(new GameObject("Part"));
            var partMesh = Track(NewMeshWithVertices("PartMesh", new[] { new Vector3(9f, 9f, 9f) }));
            partHost.AddComponent<MeshFilter>().sharedMesh = partMesh;
            var partRenderer = partHost.AddComponent<MeshRenderer>();

            var noMatch = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance);

            Assert.IsFalse(noMatch.Succeeded);
            Assert.AreEqual(ApaErrorCode.SeamPositionMismatch, noMatch.Issue.Code);
            StringAssert.Contains("reason=no-world-coincident-vertices", noMatch.Issue.Detail);

            var badTolerance = ApaSeamWorldMatcher.Match(targetRenderer, targetMesh, partRenderer, partMesh, 0f);

            Assert.IsFalse(badTolerance.Succeeded);
            Assert.AreEqual(ApaErrorCode.InvalidEpsilon, badTolerance.Issue.Code);
            StringAssert.Contains("reason=invalid-seam-tolerance", badTolerance.Issue.Detail);

            var tooLoose = ApaSeamWorldMatcher.Match(
                targetRenderer, targetMesh, partRenderer, partMesh,
                ApaSeamWorldMatcher.MaximumTolerance * 2f);
            Assert.IsFalse(tooLoose.Succeeded);
            Assert.AreEqual(ApaErrorCode.InvalidEpsilon, tooLoose.Issue.Code);
            StringAssert.Contains("reason=seam-tolerance-too-large", tooLoose.Issue.Detail);

            var candidateFailure = ApaSeamWorldMatcher.Match(
                targetRenderer, targetMesh, partRenderer, partMesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                new[] { 0, 0 }, new[] { 0 });
            Assert.IsFalse(candidateFailure.Succeeded);
            Assert.AreEqual(ApaErrorCode.InvalidSeamSelection, candidateFailure.Issue.Code);
            StringAssert.Contains("reason=duplicate-candidate-index", candidateFailure.Issue.Detail);

            var missingPart = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                null,
                null,
                ApaSeamWorldMatcher.DefaultTolerance);

            Assert.IsFalse(missingPart.Succeeded);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, missingPart.Issue.Code);
            StringAssert.Contains("reason=missing-part-mesh", missingPart.Issue.Detail);
        }

        /// <summary>A readable runtime mesh carrying only positions, which is all the matcher reads.</summary>
        private Mesh NewMeshWithVertices(string name, Vector3[] vertices)
        {
            var mesh = Track(new Mesh { name = name });
            mesh.vertices = vertices;
            return mesh;
        }

        private T Track<T>(T value) where T : Object
        {
            _created.Add(value);
            return value;
        }
    }
}

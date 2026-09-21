using System.Collections.Generic;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the vertex-color seam candidate contract: how a renderer's candidate vertices are resolved from
    /// <c>Mesh.colors32</c>, which malformed color data is refused with <c>APA052</c>, and that only resolved
    /// candidates can ever be paired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction this file is built around is the one the contract exists for: "no candidate" is a blocking
    /// defect, never an instruction to fall back to every vertex. Every failure test therefore asserts both the
    /// code and the stable <c>reason=</c> token, and asserts that the returned index list is empty — a failure
    /// that carried candidates would be the fallback in disguise.
    /// </para>
    /// <para>
    /// <b>The filter is the part's, and only the part's (M15).</b> The color restricts the part mesh. The target
    /// body is resolved spatially — every vertex is a candidate and position decides the pairing — so the
    /// target-side tests in this file assert the spatial policy rather than a color refusal, and the matcher tests
    /// assert that a non-candidate <i>part</i> vertex is never paired.
    /// </para>
    /// </remarks>
    public sealed class AuthoringVertexColorCandidateTests
    {
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);
        private static readonly Color32 Red = new Color32(255, 0, 0, 255);
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

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

        // ---- The comparison rule -----------------------------------------------------------------------

        [Test]
        public void Matches_RequiresEveryChannelToBeEqual()
        {
            Assert.IsTrue(ApaSeamVertexColorCandidates.Matches(Green, Green));
            Assert.IsTrue(
                ApaSeamVertexColorCandidates.Matches(new Color32(7, 8, 9, 10), new Color32(7, 8, 9, 10)),
                "The rule is exact equality on all four channels, not a tolerance.");

            Assert.IsFalse(ApaSeamVertexColorCandidates.Matches(new Color32(1, 255, 0, 255), Green), "red differs");
            Assert.IsFalse(ApaSeamVertexColorCandidates.Matches(new Color32(0, 254, 0, 255), Green), "green differs");
            Assert.IsFalse(ApaSeamVertexColorCandidates.Matches(new Color32(0, 255, 1, 255), Green), "blue differs");
            Assert.IsFalse(ApaSeamVertexColorCandidates.Matches(new Color32(0, 255, 0, 254), Green), "alpha differs");

            Assert.IsFalse(
                ApaSeamVertexColorCandidates.Matches(new Color32(0, 255, 0, 0), Green),
                "A one-channel difference is never 'close enough': a tolerance would make the candidate set " +
                "depend on how far a paint tool drifted.");
        }

        [Test]
        public void Matches_IgnoresNothingSoADifferentHueIsNeverACandidate()
        {
            Assert.IsFalse(ApaSeamVertexColorCandidates.Matches(Red, Green));
            Assert.IsFalse(ApaSeamVertexColorCandidates.Matches(White, Green));
            Assert.IsFalse(ApaSeamVertexColorCandidates.Matches(default(Color32), Green));
        }

        [Test]
        public void Matches_ComparesTheValueTheCodeFieldCommitted()
        {
            // The color is edited as a #RRGGBB / #RRGGBBAA code now, so the value the comparison sees is exactly
            // the value the author typed. This pins the two halves together: what the code parses to is what the
            // matcher compares.
            Assert.IsTrue(ApaSeamColorCode.TryParse("#00FF00", out var parsed));
            Assert.IsTrue(
                ApaSeamVertexColorCandidates.Matches(Green, parsed),
                "A mesh painted #00FF00 is a candidate for the code #00FF00.");

            Assert.IsTrue(ApaSeamColorCode.TryParse("#00FF0080", out var translucent));
            Assert.IsFalse(
                ApaSeamVertexColorCandidates.Matches(Green, translucent),
                "Six digits mean opaque alpha, and alpha participates in the comparison.");
        }

        [Test]
        public void Format_IsStableInvariantAndCarriesEveryChannel()
        {
            Assert.AreEqual("RGBA(0, 255, 0, 255)", ApaSeamVertexColorCandidates.Format(Green));
            Assert.AreEqual("RGBA(255, 0, 0, 255)", ApaSeamVertexColorCandidates.Format(Red));
        }

        [Test]
        public void DefaultColor_IsOpaqueBlack()
        {
            Assert.AreEqual(
                new Color32(0, 0, 0, 255),
                ApaSeamVertexColorCandidates.DefaultColor,
                "The default candidate color is opaque black #000000: an imported mesh that carries a color " +
                "channel but was never painted stores black, and alpha is opaque because the comparison " +
                "includes it.");

            Assert.AreEqual(
                "RGBA(0, 0, 0, 255)",
                ApaSeamVertexColorCandidates.Format(ApaSeamVertexColorCandidates.DefaultColor));
        }

        // ---- The spatial target policy (M15) -----------------------------------------------------------

        [Test]
        public void ResolveTarget_AcceptsAMeshThatCarriesNoVertexColorsAtAll()
        {
            var mesh = NewMesh("Body", FourVertices, null);
            var renderer = NewSkinnedRenderer("Body", mesh);

            var result = ApaSeamSpatialTargetCandidates.Resolve(renderer, mesh);

            Assert.IsTrue(result.Succeeded, result.Issue != null ? result.Issue.Detail : string.Empty);
            Assert.IsTrue(result.IsSpatial, "The target side is spatial: every body vertex is a candidate.");
            Assert.AreEqual(4, result.VertexCount);
            Assert.IsEmpty(result.Indices, "A spatial candidate set has no index list of its own.");
            Assert.IsNull(
                result.MatcherCandidateIndices,
                "A spatial result must reach the matcher as its documented all-vertices argument; an empty list " +
                "would mean the opposite.");
            Assert.AreEqual(0, result.ColorCount, "The spatial resolver never reads the color channel.");
            Assert.IsNotEmpty(result.Describe(), "A resolved candidate policy must describe itself for the window.");
            StringAssert.Contains("4", result.Describe(), "The description must state the spatial vertex count.");
        }

        [Test]
        public void ResolveTarget_IgnoresAStoredColorChannelInsteadOfFilteringByIt()
        {
            // A body painted entirely red is still a spatial candidate set: the selected color is the part's
            // filter, so no target vertex is excluded by what the body happens to store.
            var mesh = NewMesh("Body", FourVertices, new[] { Red, Red, Red, Red });
            var renderer = NewSkinnedRenderer("Body", mesh);

            var result = ApaSeamSpatialTargetCandidates.Resolve(renderer, mesh);

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.IsSpatial);
            Assert.AreEqual(4, result.VertexCount);
            Assert.IsNull(result.MatcherCandidateIndices);
        }

        [Test]
        public void ResolveTarget_MissingRendererOrMeshAndEmptyMeshAreBlocked()
        {
            var mesh = NewMesh("Body", FourVertices, null);

            var missing = ApaSeamSpatialTargetCandidates.Resolve(null, mesh);
            Assert.IsFalse(missing.Succeeded);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, missing.Issue.Code);
            StringAssert.Contains("reason=missing-target-mesh", missing.Issue.Detail);
            Assert.IsFalse(missing.IsSpatial);

            var emptyMesh = NewMesh("Empty", new Vector3[0], null);
            var empty = ApaSeamSpatialTargetCandidates.Resolve(NewSkinnedRenderer("Empty", emptyMesh), emptyMesh);
            Assert.IsFalse(empty.Succeeded);
            Assert.AreEqual(ApaErrorCode.SeamCandidateColorInvalid, empty.Issue.Code);
            StringAssert.Contains("reason=empty-mesh", empty.Issue.Detail);
            StringAssert.Contains("side=target", empty.Issue.Detail);
        }

        [Test]
        public void ResolveTarget_UnreadableMeshIsReportedWithoutReadingIt()
        {
            var mesh = NewMesh("Body", FourVertices, null);
            var renderer = NewSkinnedRenderer("Body", mesh);
            mesh.UploadMeshData(true);

            Assume.That(mesh.isReadable, Is.False, "UploadMeshData(true) must leave the mesh unreadable.");

            var result = ApaSeamSpatialTargetCandidates.Resolve(renderer, mesh);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ApaErrorCode.UnsupportedMeshAttribute, result.Issue.Code);
        }

        [Test]
        public void WorldMatcher_SpatialTargetPairsAColorlessBodyMesh()
        {
            // The regression this contract exists for: a body with no vertex colors, a part whose selected color
            // resolves, and coincident positions. Nothing about the body's color channel may block it.
            var targetMesh = NewMesh("Body", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, null);
            var targetRenderer = NewSkinnedRenderer("Body", targetMesh);

            var partMesh = NewMesh("Part", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, new[] { Green, Green });
            var partRenderer = NewSkinnedRenderer("Part", partMesh);

            var targetCandidates = ApaSeamSpatialTargetCandidates.Resolve(targetRenderer, targetMesh);
            var partCandidates = ApaSeamVertexColorCandidates.ResolvePartCandidates(partRenderer, partMesh, Green);

            Assert.IsTrue(targetCandidates.Succeeded);
            Assert.IsTrue(partCandidates.Succeeded);

            var matched = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                targetCandidates.MatcherCandidateIndices,
                partCandidates.Indices);

            Assert.IsTrue(matched.Succeeded, matched.Issue != null ? matched.Issue.Detail : string.Empty);
            CollectionAssert.AreEqual(new[] { 0, 1 }, matched.BaseIndices);
            CollectionAssert.AreEqual(new[] { 0, 1 }, matched.PartIndices);
        }

        [Test]
        public void WorldMatcher_NeverPairsAPartVertexOutsideTheSelectedColor()
        {
            // The part's vertex 1 is painted red, so it is not a candidate: it must not be paired even though it
            // sits exactly on a target vertex, and the candidate that does coincide must take that target.
            var targetMesh = NewMesh("Body", new[] { Vector3.zero, Vector3.zero }, null);
            var targetRenderer = NewSkinnedRenderer("Body", targetMesh);

            var partMesh = NewMesh("Part", new[] { Vector3.zero, Vector3.zero }, new[] { Green, Red });
            var partRenderer = NewSkinnedRenderer("Part", partMesh);

            var targetCandidates = ApaSeamSpatialTargetCandidates.Resolve(targetRenderer, targetMesh);
            var partCandidates = ApaSeamVertexColorCandidates.ResolvePartCandidates(partRenderer, partMesh, Green);

            Assert.IsTrue(partCandidates.Succeeded);
            CollectionAssert.AreEqual(new[] { 0 }, partCandidates.Indices);

            var matched = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                targetCandidates.MatcherCandidateIndices,
                partCandidates.Indices);

            Assert.IsTrue(matched.Succeeded, matched.Issue != null ? matched.Issue.Detail : string.Empty);
            CollectionAssert.AreEqual(
                new[] { 0 },
                matched.PartIndices,
                "The non-candidate part vertex is never paired, no matter how well it coincides.");
            CollectionAssert.AreEqual(new[] { 0 }, matched.BaseIndices);

            // And with only the non-candidate left coincident, the run refuses rather than widening the part side.
            partMesh.vertices = new[] { new Vector3(9f, 9f, 9f), Vector3.zero };
            var outsiderOnly = ApaSeamWorldMatcher.Match(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                ApaSeamWorldMatcher.DefaultTolerance,
                targetCandidates.MatcherCandidateIndices,
                partCandidates.Indices);

            Assert.IsFalse(outsiderOnly.Succeeded, "A part vertex outside the color must never be paired.");
            Assert.AreEqual(ApaErrorCode.SeamPositionMismatch, outsiderOnly.Issue.Code);
            StringAssert.Contains("reason=no-world-coincident-vertices", outsiderOnly.Issue.Detail);
            Assert.IsEmpty(outsiderOnly.BaseIndices);
        }

        // ---- The candidate contract on a real mesh -----------------------------------------------------

        [Test]
        public void Resolve_ReturnsOnlyTheVerticesCarryingTheSelectedColorInAscendingOrder()
        {
            var mesh = NewMesh("Part", FourVertices, new[] { Red, Green, White, Green });
            var renderer = NewSkinnedRenderer("Part", mesh);

            var result = ApaSeamVertexColorCandidates.ResolvePartCandidates(renderer, mesh, Green);

            Assert.IsTrue(result.Succeeded, result.Issue != null ? result.Issue.Detail : string.Empty);
            Assert.IsFalse(result.IsSpatial, "The part side is always a resolved color filter.");
            Assert.AreEqual(4, result.VertexCount);
            Assert.AreEqual(4, result.ColorCount);
            Assert.AreEqual(Green, result.Color);
            CollectionAssert.AreEqual(
                new[] { 1, 3 },
                result.Indices,
                "The candidates are the vertices that carry the color, ascending and duplicate-free.");
            CollectionAssert.AreEqual(
                result.Indices,
                result.MatcherCandidateIndices,
                "A color-filtered result reaches the matcher as its own index list.");
            Assert.IsNotEmpty(result.Describe(), "A resolved candidate set must describe itself for the window.");
        }

        [Test]
        public void Resolve_MissingVertexColorsIsBlockingAndNeverFallsBackToEveryVertex()
        {
            var mesh = NewMesh("Part", FourVertices, null);
            var renderer = NewSkinnedRenderer("Part", mesh);

            var result = ApaSeamVertexColorCandidates.ResolvePartCandidates(renderer, mesh, Green);

            AssertFailure(result, "reason=vertex-color-missing", ApaSeamVertexColorCandidates.PartSide);
            Assert.AreEqual(0, result.ColorCount);
        }

        [Test]
        public void Resolve_NoVertexCarriesTheColorIsBlockingAndNamesTheColor()
        {
            var mesh = NewMesh("Part", FourVertices, new[] { Red, Red, White, Red });
            var renderer = NewSkinnedRenderer("Part", mesh);

            var result = ApaSeamVertexColorCandidates.ResolvePartCandidates(renderer, mesh, Green);

            AssertFailure(result, "reason=no-vertex-color-candidates", ApaSeamVertexColorCandidates.PartSide);
            StringAssert.Contains(
                "RGBA(0, 255, 0, 255)",
                result.Issue.Detail,
                "The diagnostic must name the color that matched nothing, or the author cannot fix it.");
        }

        [Test]
        public void Resolve_AlphaDifferenceAloneMakesNoVertexACandidate()
        {
            var mesh = NewMesh("Part", FourVertices, new[]
            {
                new Color32(0, 255, 0, 0), new Color32(0, 255, 0, 0), Green, new Color32(0, 255, 0, 128)
            });
            var renderer = NewSkinnedRenderer("Part", mesh);

            var result = ApaSeamVertexColorCandidates.ResolvePartCandidates(renderer, mesh, Green);

            Assert.IsTrue(result.Succeeded);
            CollectionAssert.AreEqual(
                new[] { 2 },
                result.Indices,
                "The alpha channel participates in the comparison, so only the opaque green vertex matches.");
        }

        [Test]
        public void FromColors_MalformedColorCountIsBlocking()
        {
            // A mesh whose color array disagrees with its vertex count cannot be produced through Mesh.colors32 —
            // Unity rejects the assignment — which is exactly why the pure half is public and tested directly.
            var result = ApaSeamVertexColorCandidates.FromColors(
                new[] { Green, Green, Green },
                4,
                Green,
                ApaSeamVertexColorCandidates.PartSide);

            AssertFailure(result, "reason=vertex-color-count-mismatch", ApaSeamVertexColorCandidates.PartSide);
            Assert.AreEqual(3, result.ColorCount);
            Assert.AreEqual(4, result.VertexCount);
        }

        [Test]
        public void FromColors_AnEmptyMeshIsBlocking()
        {
            AssertFailure(
                ApaSeamVertexColorCandidates.FromColors(new Color32[0], 0, Green,
                    ApaSeamVertexColorCandidates.PartSide),
                "reason=empty-mesh",
                ApaSeamVertexColorCandidates.PartSide);
        }

        [Test]
        public void Resolve_MissingRendererOrMeshReportsTheSelectionDiagnosticInsteadOfFallingBack()
        {
            var mesh = NewMesh("Part", FourVertices, new[] { Green, Green, Green, Green });
            var renderer = NewSkinnedRenderer("Part", mesh);

            var missingTarget = ApaSeamVertexColorCandidates.Resolve(
                null, mesh, Green, ApaSeamVertexColorCandidates.TargetSide);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, missingTarget.Issue.Code);
            StringAssert.Contains("reason=missing-target-mesh", missingTarget.Issue.Detail);
            Assert.IsEmpty(missingTarget.Indices);

            var missingPart = ApaSeamVertexColorCandidates.ResolvePartCandidates(renderer, null, Green);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, missingPart.Issue.Code);
            StringAssert.Contains("reason=missing-part-mesh", missingPart.Issue.Detail);
            Assert.IsEmpty(missingPart.Indices);

            var missingSpatialTarget = ApaSeamSpatialTargetCandidates.Resolve(null, null);
            Assert.AreEqual(ApaErrorCode.TargetRendererNotFound, missingSpatialTarget.Issue.Code);
            StringAssert.Contains("reason=missing-target-mesh", missingSpatialTarget.Issue.Detail);
        }

        [Test]
        public void Resolve_UnreadableMeshReportsTheCompatibilityDiagnosticWithoutReadingIt()
        {
            var mesh = NewMesh("Part", FourVertices, new[] { Green, Green, Green, Green });
            var renderer = NewSkinnedRenderer("Part", mesh);
            mesh.UploadMeshData(true);

            Assume.That(
                mesh.isReadable,
                Is.False,
                "UploadMeshData(true) must leave the mesh unreadable for this test to mean anything.");

            var result = ApaSeamVertexColorCandidates.ResolvePartCandidates(renderer, mesh, Green);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ApaErrorCode.UnsupportedMeshAttribute, result.Issue.Code);
            Assert.IsEmpty(result.Indices);
        }

        // ---- Candidate filtering is the matcher's input -------------------------------------------------

        [Test]
        public void WorldMatcher_StillAcceptsTheAllVerticesOverloadForExplicitSeamConsumers()
        {
            // The all-vertices overload is public API the build and the existing tests use, so it must keep
            // working: the candidate contract restricts the authoring action, not the matcher's surface. It is
            // also exactly the form the spatial target side is handed to the matcher.
            var targetMesh = NewMesh("Body", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, null);
            var targetRenderer = NewSkinnedRenderer("Body", targetMesh);
            var partMesh = NewMesh("Part", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, null);
            var partRenderer = NewSkinnedRenderer("Part", partMesh);

            var matched = ApaSeamWorldMatcher.Match(
                targetRenderer, targetMesh, partRenderer, partMesh, ApaSeamWorldMatcher.DefaultTolerance);

            Assert.IsTrue(matched.Succeeded, matched.Issue != null ? matched.Issue.Detail : string.Empty);
            CollectionAssert.AreEqual(new[] { 0, 1 }, matched.BaseIndices);
            CollectionAssert.AreEqual(new[] { 0, 1 }, matched.PartIndices);
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private static readonly Vector3[] FourVertices =
        {
            Vector3.zero, Vector3.right, Vector3.up, Vector3.forward
        };

        private static void AssertFailure(
            ApaSeamColorCandidateResult result,
            string reasonToken,
            string side = ApaSeamVertexColorCandidates.PartSide)
        {
            Assert.IsFalse(result.Succeeded, "The candidate set must be refused, not resolved.");
            Assert.AreEqual(ApaErrorCode.SeamCandidateColorInvalid, result.Issue.Code);
            StringAssert.Contains(reasonToken, result.Issue.Detail);
            StringAssert.Contains("side=" + side, result.Issue.Detail);
            Assert.IsEmpty(result.Indices, "A failure must never carry a fallback index list.");
        }

        private GameObject NewGameObject(string name)
        {
            var host = new GameObject(name);
            _created.Add(host);
            return host;
        }

        private Mesh NewMesh(string name, Vector3[] vertices, Color32[] colors)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            if (colors != null) mesh.colors32 = colors;
            _created.Add(mesh);
            return mesh;
        }

        private SkinnedMeshRenderer NewSkinnedRenderer(string name, Mesh mesh)
        {
            var renderer = NewGameObject(name).AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }
    }
}

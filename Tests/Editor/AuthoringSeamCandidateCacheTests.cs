using System;
using System.IO;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the seam candidate cache: the rule that decides when the expensive candidate resolution runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The behavior half drives the cache directly and counts how many times a "repaint" would have been allowed
    /// to resolve the candidates. A hundred simulated repaints must request zero resolutions, and an invalidation
    /// must request exactly one — which is the property the authoring window depends on: neither a repaint nor an
    /// incomplete color code may rebuild the candidate arrays or re-run the merge check.
    /// </para>
    /// <para>
    /// The source half pins the connections a pure unit test cannot see: that the window goes through the cache's
    /// rule rather than resolving inline, that the generate action re-resolves unconditionally, that the color
    /// code commits through one place, and that the Scene View merge-check cache is keyed on the resolved
    /// candidate results rather than on the raw color.
    /// </para>
    /// </remarks>
    public sealed class AuthoringSeamCandidateCacheTests
    {
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);

        // ---- The reuse rule ----------------------------------------------------------------------------

        [Test]
        public void NeedsRefresh_ResolvesOncePerChangeRatherThanOncePerRepaint()
        {
            var cache = new ApaSeamCandidateCache();

            Assert.IsTrue(cache.NeedsRefresh(false), "A cache that was never filled must resolve.");
            Assert.IsFalse(cache.HasValue);
            Assert.IsNull(cache.Target);
            Assert.IsNull(cache.Part);

            cache.Store(Result(), Result());

            // The repaints of one unchanged window state.
            for (var frame = 0; frame < 120; frame++)
            {
                Assert.IsFalse(cache.NeedsRefresh(false), "repaint " + frame + " must reuse the cached sets.");
            }

            Assert.IsTrue(cache.NeedsRefresh(true), "A changed input must request exactly one resolution.");
        }

        [Test]
        public void Store_KeepsBothSides()
        {
            var target = Result();
            var part = Result();
            var cache = new ApaSeamCandidateCache();

            cache.Store(target, part);

            Assert.IsTrue(cache.HasValue);
            Assert.AreSame(target, cache.Target);
            Assert.AreSame(part, cache.Part);
            Assert.IsFalse(cache.NeedsRefresh(false));
        }

        [Test]
        public void Store_WithAMissingSideLeavesTheCacheEmpty()
        {
            var cache = new ApaSeamCandidateCache();
            cache.Store(Result(), null);

            Assert.IsFalse(cache.HasValue, "A half-resolved pair must not be served as a value.");
            Assert.IsTrue(cache.NeedsRefresh(false));
        }

        [Test]
        public void Invalidate_DropsTheResultsSoAStaleSetCannotBeServed()
        {
            var cache = new ApaSeamCandidateCache();
            cache.Store(Result(), Result());

            cache.Invalidate();

            Assert.IsFalse(cache.HasValue);
            Assert.IsNull(
                cache.Target,
                "A caller that forgot to consult NeedsRefresh must not be able to serve the previous mesh's or " +
                "color's candidates.");
            Assert.IsNull(cache.Part);
        }

        [Test]
        public void Invalidate_RequestsExactlyOneResolution()
        {
            var cache = new ApaSeamCandidateCache();
            cache.Store(Result(), Result());

            cache.Invalidate();

            var resolutions = 0;
            for (var frame = 0; frame < 60; frame++)
            {
                if (cache.NeedsRefresh(true)) resolutions++;
            }

            Assert.AreEqual(1, resolutions, "One invalidation is one resolution, not one per repaint.");

            cache.Store(Result(), Result());
            for (var frame = 0; frame < 60; frame++) Assert.IsFalse(cache.NeedsRefresh(false));
        }

        [Test]
        public void Invalidate_IsIdempotentSoEveryChangeCanCallIt()
        {
            var cache = new ApaSeamCandidateCache();
            cache.Store(Result(), Result());

            for (var frame = 0; frame < 30; frame++) cache.Invalidate();

            Assert.IsFalse(cache.HasValue);
            Assert.IsTrue(cache.NeedsRefresh(false));
        }

        // ---- The window's connections ------------------------------------------------------------------

        [Test]
        public void Window_ResolvesOnlyWhenTheCacheAsksForIt()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            StringAssert.Contains(
                "[NonSerialized] private ApaSeamCandidateCache _seamCandidates",
                window,
                "The window must own the cache rather than resolve the candidates inline.");
            StringAssert.Contains(
                "if (!_seamCandidates.NeedsRefresh(_liveChecksDirty)) return;",
                window,
                "The display path must go through the cache's rule rather than resolving on every repaint.");

            var ensure = window.IndexOf("private void EnsureSeamCandidates()", StringComparison.Ordinal);
            var refresh = window.IndexOf("private void RefreshSeamCandidates()", StringComparison.Ordinal);
            Assert.GreaterOrEqual(ensure, 0);
            Assert.GreaterOrEqual(refresh, 0);

            // The display path ends at the next documented member, which is DescribeCandidates in either order
            // the two methods are written in.
            var afterEnsure = ensure + "private void EnsureSeamCandidates()".Length;
            var nextMember = window.IndexOf("/// <summary>", afterEnsure, StringComparison.Ordinal);
            Assert.Greater(nextMember, afterEnsure);

            var ensureBody = window.Substring(ensure, nextMember - ensure);
            Assert.IsFalse(
                ensureBody.Contains("Mesh.colors32") || ensureBody.Contains("ResolvePartCandidates"),
                "The display path may only decide whether to resolve; the resolution itself lives in one place.");

            // The generate action is the documented exception: it re-resolves unconditionally, because the color
            // and the meshes are that action's authoritative input.
            var generate = window.IndexOf("private void GenerateWorldSeam(", StringComparison.Ordinal);
            var generateBody = window.Substring(generate, refresh - generate);
            StringAssert.Contains(
                "RefreshSeamCandidates();",
                generateBody,
                "The generate action re-resolves from the live color instead of trusting the display cache.");
        }

        [Test]
        public void Window_InvalidatesOncePerCommittedColorCodeAndNeverPerKeystroke()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            // The deferral existed for the color wheel's per-frame value stream; a text field has none.
            foreach (var gone in new[] { "Defer();", "IsDeferred", "CommitDeferredSeamColor" })
            {
                Assert.IsFalse(window.Contains(gone), "The color-wheel deferral is dead code now: " + gone);
            }

            var commit = window.IndexOf("private void CommitSeamColorCode(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(commit, 0, "A color code commits in exactly one place.");

            var end = window.IndexOf("/// <summary>", commit, StringComparison.Ordinal);
            Assert.Greater(end, commit);
            var body = window.Substring(commit, end - commit);

            Assert.AreEqual(
                1,
                CountOccurrences(body, "SeamCandidates.Invalidate();"),
                "One committed edit is one invalidation.");
            Assert.IsFalse(
                body.Contains("Mesh.colors32") || body.Contains("ResolvePartCandidates"),
                "The commit path must not read the mesh itself; the cache's next read does that once.");

            // Both early returns — malformed input and a no-op code — happen before the invalidation.
            var invalidate = body.IndexOf("SeamCandidates.Invalidate();", StringComparison.Ordinal);
            var malformed = body.IndexOf("_seamColorError = TrFormat(", StringComparison.Ordinal);
            var noChange = body.IndexOf("if (!changed)", StringComparison.Ordinal);
            Assert.Greater(malformed, 0);
            Assert.Greater(noChange, 0);
            Assert.Less(malformed, invalidate);
            Assert.Less(noChange, invalidate);
        }

        [Test]
        public void SceneTool_KeysTheMergeCheckOnTheCandidatesRatherThanOnTheRawColor()
        {
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            Assert.IsFalse(
                tool.Contains("_mergeCheckColor"),
                "The merge-check cache must not be keyed on the selected color: the color is not an input of the " +
                "matcher, and keying on it would recompute the matching on every committed code.");
            StringAssert.Contains(
                "ReferenceEquals(_mergeCheckTargetCandidates, targetCandidates)",
                tool,
                "The candidate results are the key, and they change exactly when the window commits a new color.");
        }

        [Test]
        public void SceneTool_DoesNotPaintTheSpatialTargetSideAsCandidates()
        {
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            var drawCandidates = tool.IndexOf("private void DrawCandidates(", StringComparison.Ordinal);
            var nextMember = tool.IndexOf("private void DrawCandidateSide(", StringComparison.Ordinal);

            Assert.GreaterOrEqual(drawCandidates, 0);
            Assert.Greater(nextMember, drawCandidates);

            var body = tool.Substring(drawCandidates, nextMember - drawCandidates);
            StringAssert.Contains(
                "!targetCandidates.IsSpatial",
                body,
                "The green candidate overlay draws the part's color filter; 'every body vertex' is not a set " +
                "worth painting.");
            StringAssert.Contains("host.PartSeamCandidates", body);
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private static ApaSeamColorCandidateResult Result()
        {
            // A real result object, because the window and the Scene View tool compare candidate results by
            // reference: what matters here is identity, not the contents.
            return ApaSeamVertexColorCandidates.FromColors(
                new[] { Green }, 1, Green, ApaSeamVertexColorCandidates.PartSide);
        }

        private static int CountOccurrences(string text, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string ReadEditorSource(string folder, string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", folder, fileName);

            if (!File.Exists(path)) Assert.Ignore("Editor source not found: " + path);

            return File.ReadAllText(path);
        }
    }
}

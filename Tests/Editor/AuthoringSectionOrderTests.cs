using System;
using System.IO;
using AvatarPartAssembler.Editor.Authoring;
using AvatarPartAssembler.Editor.Localization;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the order of the Part Authoring window's sections and for the placement of the profile load
    /// control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is deliberate rather than incidental: loading a profile is the first decision an author makes, so
    /// its control belongs directly under the toolbar; Output belongs between the bone/blend-shape policy it
    /// follows and the Actions that write it. A repaint needs a real Editor window, so the order is pinned as a
    /// source contract — the same way this suite pins the overlay's draw precedence — and the labels the order
    /// depends on are checked against the localization table.
    /// </para>
    /// </remarks>
    public sealed class AuthoringSectionOrderTests
    {
        [Test]
        public void OnGui_DrawsTheSectionsInTheDocumentedOrder()
        {
            var source = ReadEditorSource("ApaAuthoringWindow.cs");

            var onGui = source.IndexOf("private void OnGUI()", StringComparison.Ordinal);
            var endScroll = source.IndexOf("EditorGUILayout.EndScrollView();", onGui, StringComparison.Ordinal);
            Assert.GreaterOrEqual(onGui, 0);
            Assert.Greater(endScroll, onGui);

            var body = source.Substring(onGui, endScroll - onGui);

            // The exact call sequence, in order. Every section the window draws must appear here, so a new
            // section cannot be added without deciding where it belongs.
            var expected = new[]
            {
                "DrawToolbar();",
                "DrawProfileLoadSection();",
                "DrawSelectionSection();",
                "DrawSignatureSection();",
                "DrawIdentitySection();",
                "DrawRemovalSection();",
                "DrawSeamSection();",
                "DrawUvSection();",
                "DrawMaterialSection();",
                "DrawPolicySection();",
                "DrawOutputSection();",
                "DrawActionsSection();",
                "DrawDiagnosticsSection();"
            };

            var previous = -1;
            foreach (var call in expected)
            {
                var index = body.IndexOf(call, StringComparison.Ordinal);
                Assert.GreaterOrEqual(index, 0, "OnGUI must draw " + call);
                Assert.Greater(index, previous, call + " is drawn out of order.");
                previous = index;
            }
        }

        [Test]
        public void OutputSection_SitsDirectlyBelowTheBonePolicyAndDirectlyAboveTheActions()
        {
            var source = ReadEditorSource("ApaAuthoringWindow.cs");

            var policy = source.IndexOf("DrawPolicySection();", StringComparison.Ordinal);
            var output = source.IndexOf("DrawOutputSection();", StringComparison.Ordinal);
            var actions = source.IndexOf("DrawActionsSection();", StringComparison.Ordinal);

            Assert.GreaterOrEqual(policy, 0);
            Assert.Greater(output, policy, "Output comes after Bones And Blend Shapes.");
            Assert.Greater(actions, output, "Output comes before Actions.");

            // "Directly" means nothing but whitespace between the two calls: no section may be slipped between
            // them without this failing.
            Assert.IsEmpty(
                Between(source, policy, "DrawPolicySection();", output).Trim(),
                "Output must be the very next section after Bones And Blend Shapes.");
            Assert.IsEmpty(
                Between(source, output, "DrawOutputSection();", actions).Trim(),
                "Actions must be the very next section after Output.");
        }

        [Test]
        public void LoadProfileControl_IsDrawnOnceDirectlyUnderTheToolbar()
        {
            var source = ReadEditorSource("ApaAuthoringWindow.cs");

            Assert.AreEqual(
                1,
                CountOccurrences(source, "\"Load Existing Profile\""),
                "The load control must exist exactly once: a second copy in the Output section would be a second " +
                "place to look and a second place to keep in step.");

            var toolbar = source.IndexOf("DrawToolbar();", StringComparison.Ordinal);
            var load = source.IndexOf("DrawProfileLoadSection();", StringComparison.Ordinal);
            var selection = source.IndexOf("DrawSelectionSection();", StringComparison.Ordinal);

            Assert.Greater(load, toolbar, "The load control is drawn after the toolbar.");
            Assert.Less(load, selection, "The load control is drawn before every other section.");

            // The Output section must not carry it any more.
            var outputStart = source.IndexOf("private void DrawOutputSection()", StringComparison.Ordinal);
            var outputEnd = source.IndexOf("private void DrawRemovalSection()", StringComparison.Ordinal);
            Assert.GreaterOrEqual(outputStart, 0);
            Assert.Greater(outputEnd, outputStart);

            Assert.IsFalse(
                source.Substring(outputStart, outputEnd - outputStart).Contains("LoadProfile("),
                "The Output section must not duplicate the load control.");

            // And the control that remains is the drag target it always was: it reads a profile, never writes one.
            var loadSection = source.IndexOf("private void DrawProfileLoadSection()", StringComparison.Ordinal);
            var loadEnd = source.IndexOf("private void DrawSelectionSection()", StringComparison.Ordinal);
            var loadBody = source.Substring(loadSection, loadEnd - loadSection);
            StringAssert.Contains("typeof(ApaPartProfile)", loadBody);
            StringAssert.Contains("if (loadTarget != null) LoadProfile(loadTarget);", loadBody);
        }

        [Test]
        public void NewLabelsAndTooltipsAreLocalized()
        {
            Assert.IsTrue(
                ApaLocalization.HasTranslation("Load Existing Profile"),
                "The load control's label must have a Simplified Chinese entry.");

            Assert.IsTrue(
                ApaLocalization.HasTranslation(
                    "Read a saved profile asset into the draft. The asset itself is never written until you save " +
                    "it, and loading replaces the whole draft, so the fields below show the loaded profile."),
                "The load control's tooltip must have a Simplified Chinese entry.");

            Assert.IsTrue(
                ApaLocalization.HasTranslation(
                    "{0} spatial target vertex(es) (paired by world position, not by color)"),
                "The target side's spatial status line must have a Simplified Chinese entry.");

            Assert.IsTrue(
                ApaLocalization.HasTranslation(
                    "{0} matched pair(s); the target side is spatial ({1} target vertex(es)); {2} of {3} part " +
                    "candidate(s) unmatched at a world tolerance of {4}"),
                "The spatial merge-check summary must have a Simplified Chinese entry.");
        }

        [Test]
        public void SectionTitles_ComeFromTheLocalizationLayer()
        {
            var source = ReadEditorSource("ApaAuthoringWindow.cs");

            foreach (var title in new[] { "Selection", "Output", "Bones And Blend Shapes", "Actions" })
            {
                Assert.IsTrue(
                    source.Contains("Tr(\"" + title + "\")"),
                    "The section title '" + title + "' must be drawn through the localization layer.");
                Assert.IsTrue(
                    ApaLocalization.HasTranslation(title),
                    "The section title '" + title + "' must have a Simplified Chinese entry.");
            }
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        /// <summary>The text between a call and the next one, exclusive of both.</summary>
        private static string Between(string text, int start, string startToken, int end)
        {
            var after = start + startToken.Length;
            Assert.Greater(end, after, "The end index must follow the start token.");
            return text.Substring(after, end - after);
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

        private static string ReadEditorSource(string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", "Authoring", fileName);

            if (!File.Exists(path)) Assert.Ignore("Editor source not found: " + path);

            return File.ReadAllText(path);
        }
    }
}

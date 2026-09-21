using System;
using System.IO;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the candidate color code: the strict parsing and formatting rules, the retention rule for
    /// malformed input, and the absence of the Unity color-wheel control from the authoring window.
    /// </summary>
    /// <remarks>
    /// The parsing and formatting rules are pure, so they are asserted directly. The two properties that decide
    /// the authoring behaviour — malformed input keeps the previous color, and a valid edit invalidates the
    /// candidate cache exactly once — are asserted through <see cref="ApaSeamColorCode.TryApply"/> and the
    /// window's own commit method.
    /// </remarks>
    public sealed class AuthoringColorCodeTests
    {
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);

        // ---- Formatting --------------------------------------------------------------------------------

        [Test]
        public void DefaultCode_IsOpaqueBlack()
        {
            Assert.AreEqual("#000000", ApaSeamColorCode.DefaultCode);
            Assert.AreEqual(
                ApaSeamVertexColorCandidates.DefaultColor,
                Parse(ApaSeamColorCode.DefaultCode),
                "The default displayed code must parse to the default candidate color.");
        }

        [Test]
        public void Format_WritesTheCanonicalRgbForm()
        {
            Assert.AreEqual("#000000", ApaSeamColorCode.Format(new Color32(0, 0, 0, 255)));
            Assert.AreEqual("#00FF00", ApaSeamColorCode.Format(Green));
            Assert.AreEqual("#0A1B2C", ApaSeamColorCode.Format(new Color32(10, 27, 44, 255)));
            Assert.AreEqual(
                "#FFFFFF",
                ApaSeamColorCode.Format(new Color32(255, 255, 255, 255)),
                "Upper-case digits, invariant culture, exactly six of them.");
        }

        [Test]
        public void Format_KeepsANonOpaqueAlphaAsRgba()
        {
            Assert.AreEqual("#00FF0080", ApaSeamColorCode.Format(new Color32(0, 255, 0, 128)));
            Assert.AreEqual("#00000000", ApaSeamColorCode.Format(new Color32(0, 0, 0, 0)));

            // And the written form parses back to the same value, so a non-opaque color round-trips.
            var color = new Color32(17, 34, 51, 68);
            Assert.AreEqual(color, Parse(ApaSeamColorCode.Format(color)));
        }

        // ---- Parsing -----------------------------------------------------------------------------------

        [Test]
        public void TryParse_AcceptsBothDocumentedForms()
        {
            Assert.AreEqual(new Color32(0, 255, 0, 255), Parse("#00FF00"), "Six digits mean opaque alpha.");
            Assert.AreEqual(new Color32(0, 255, 0, 0), Parse("#00FF0000"));
            Assert.AreEqual(new Color32(255, 255, 255, 255), Parse("#ffffff"), "Hex digits are case-insensitive.");
            Assert.AreEqual(new Color32(171, 205, 239, 255), Parse("  #ABCDEF  "), "Surrounding whitespace is ignored.");
        }

        [Test]
        public void TryParse_RejectsEveryOtherSpelling()
        {
            var malformed = new[]
            {
                null,
                string.Empty,
                "   ",
                "00FF00",      // no prefix
                "FF00",        // too short
                "#FFF",        // shorthand
                "#00FF0",      // five digits
                "#00FF000",    // seven digits
                "#00FF00000",  // nine digits
                "#GGGGGG",     // not hexadecimal
                "#00FF0Z",
                "##00FF00",
                "#00 FF00",    // interior whitespace
                "green"
            };

            foreach (var text in malformed)
            {
                Assert.IsFalse(
                    ApaSeamColorCode.TryParse(text, out _),
                    "This text is not a color code and must be refused rather than guessed at: '" + text + "'");
            }
        }

        [Test]
        public void TryParse_NeverReturnsAPartialParse()
        {
            Assert.IsFalse(ApaSeamColorCode.TryParse("#00FF0Z", out var color));
            Assert.AreEqual(
                ApaSeamVertexColorCandidates.DefaultColor,
                color,
                "A refused parse reports the default rather than a half-read value.");
        }

        // ---- The retention rule ------------------------------------------------------------------------

        [Test]
        public void TryApply_KeepsThePreviousColorOnMalformedInput()
        {
            var applied = ApaSeamColorCode.TryApply("#00FF", Green, out var color, out var changed);

            Assert.IsFalse(applied);
            Assert.AreEqual(Green, color, "A typo must not change which vertices are candidates.");
            Assert.IsFalse(changed, "A typo must not invalidate anything either.");
        }

        [Test]
        public void TryApply_ReportsAChangeOnlyForADifferentValidColor()
        {
            Assert.IsTrue(ApaSeamColorCode.TryApply("#FF0000", Green, out var red, out var changed));
            Assert.AreEqual(new Color32(255, 0, 0, 255), red);
            Assert.IsTrue(changed, "A different color is exactly when the candidate cache must be invalidated.");

            Assert.IsTrue(
                ApaSeamColorCode.TryApply("#00FF00", Green, out var same, out var sameChanged),
                "Re-stating the current color is a valid edit.");
            Assert.AreEqual(Green, same);
            Assert.IsFalse(sameChanged, "A no-op edit must not cost a re-resolution.");
        }

        // ---- The window --------------------------------------------------------------------------------

        [Test]
        public void Window_DrawsACodeFieldAndNoColorWheel()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");
            var tool = ReadEditorSource("Authoring", "ApaAuthoringSceneTool.cs");

            StringAssert.Contains(
                "\"Candidate Color Code\"",
                window,
                "The seam block must expose the candidate color as a code.");
            StringAssert.Contains("ApaSeamColorCode.Format(_seamCandidateColor)", window);
            StringAssert.Contains("CommitSeamColorCode(text);", window);

            Assert.IsFalse(
                window.Contains("EditorGUI.ColorField") || window.Contains("EditorGUILayout.ColorField"),
                "The Unity color wheel is gone from the authoring window; a read-only one would keep it one click " +
                "away.");
            Assert.IsFalse(
                tool.Contains("EditorGUI.ColorField") || tool.Contains("EditorGUILayout.ColorField"));
            Assert.IsFalse(
                window.Contains("ToColor32"),
                "The picker-to-Color32 conversion is dead with the picker.");
        }

        [Test]
        public void Window_CommitsOncePerValidEditAndKeepsTheColorOnMalformedInput()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            var commit = window.IndexOf("private void CommitSeamColorCode(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(commit, 0);

            var commitEnd = window.IndexOf("/// <summary>", commit, StringComparison.Ordinal);
            Assert.Greater(commitEnd, commit);
            var body = window.Substring(commit, commitEnd - commit);

            StringAssert.Contains("ApaSeamColorCode.TryApply(", body, "The rule lives in the pure helper.");
            StringAssert.Contains("_seamColorError = TrFormat(", body, "Malformed input is reported in the UI.");
            StringAssert.Contains("SeamCandidates.Invalidate();", body, "A committed edit invalidates once.");
            StringAssert.Contains("SceneView.RepaintAll();", body, "And the Scene View catches up immediately.");

            // The malformed branch must return before the invalidation, or a typo would cost a re-resolution.
            var malformed = body.IndexOf("_seamColorError = TrFormat(", StringComparison.Ordinal);
            var invalidate = body.IndexOf("SeamCandidates.Invalidate();", StringComparison.Ordinal);
            Assert.Less(malformed, invalidate);
            StringAssert.Contains("return;", body.Substring(malformed, invalidate - malformed));

            // The no-change branch must return before the invalidation too.
            var noChange = body.IndexOf("if (!changed)", StringComparison.Ordinal);
            Assert.Greater(noChange, 0);
            Assert.Less(noChange, invalidate);
        }

        [Test]
        public void Window_DerivesTheTextFieldFromTheCommittedColor()
        {
            var window = ReadEditorSource("Authoring", "ApaAuthoringWindow.cs");

            StringAssert.Contains(
                "if (_seamColorInput == null) _seamColorInput = ApaSeamColorCode.Format(_seamCandidateColor);",
                window,
                "The buffer is derived from the color, so the field can never show a code that is not the one " +
                "being matched.");
            StringAssert.Contains("EditorGUILayout.TextField(", window);
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        private static Color32 Parse(string text)
        {
            Assert.IsTrue(ApaSeamColorCode.TryParse(text, out var color), "Expected a valid code: " + text);
            return color;
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

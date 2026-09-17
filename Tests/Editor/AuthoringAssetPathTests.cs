using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the pure path and overwrite policy of the authoring writers.
    /// </summary>
    /// <remarks>
    /// These are decisions, not asset operations: no asset is created, read, or deleted. The point of the suite
    /// is that "is this path usable" and "may this file be replaced" have exactly one answer each, so that the
    /// profile writer and the prefab generator cannot behave differently.
    /// </remarks>
    public sealed class AuthoringAssetPathTests
    {
        [Test]
        public void Normalize_UnifiesSeparatorsAndCollapsesEmptySegments()
        {
            Assert.AreEqual("Assets/Parts/Arm.asset", ApaAuthoringAssetPaths.Normalize("Assets\\Parts//Arm.asset"));
            Assert.AreEqual("Assets/Parts/Arm.asset", ApaAuthoringAssetPaths.Normalize("  Assets/Parts/Arm.asset/  "));
            Assert.AreEqual("Assets/Parts", ApaAuthoringAssetPaths.Normalize("/Assets/Parts"));
        }

        [Test]
        public void Normalize_RestoresCanonicalAssetsCapitalization()
        {
            Assert.AreEqual("Assets/Parts/Arm.asset", ApaAuthoringAssetPaths.Normalize("assets/Parts/Arm.asset"));
        }

        [Test]
        public void Normalize_ReturnsEmptyForEmptyInput()
        {
            Assert.AreEqual(string.Empty, ApaAuthoringAssetPaths.Normalize(null));
            Assert.AreEqual(string.Empty, ApaAuthoringAssetPaths.Normalize("   "));
            Assert.AreEqual(string.Empty, ApaAuthoringAssetPaths.Normalize("///"));
        }

        [Test]
        public void TryValidateAssetPath_AcceptsAProjectRelativePathWithTheRightExtension()
        {
            var ok = ApaAuthoringAssetPaths.TryValidateAssetPath(
                "Assets/Parts/Arm.asset", ApaAuthoringAssetPaths.ProfileExtension, out var normalized, out var reason);

            Assert.IsTrue(ok, reason);
            Assert.AreEqual("Assets/Parts/Arm.asset", normalized);
            Assert.AreEqual(string.Empty, reason);
        }

        [Test]
        public void TryValidateAssetPath_RejectsTheStableReasons()
        {
            AssertRejected(string.Empty, ApaAuthoringAssetPaths.ProfileExtension, "empty-path");
            AssertRejected("   ", ApaAuthoringAssetPaths.ProfileExtension, "empty-path");
            AssertRejected("C:/Projects/Arm.asset", ApaAuthoringAssetPaths.ProfileExtension, "absolute-path");
            AssertRejected("//server/share/Arm.asset", ApaAuthoringAssetPaths.ProfileExtension, "absolute-path");
            AssertRejected("Assets/../Arm.asset", ApaAuthoringAssetPaths.ProfileExtension, "parent-segment");
            AssertRejected("AssetsX/Arm.asset", ApaAuthoringAssetPaths.ProfileExtension, "outside-assets-folder");
            AssertRejected("Arm.asset", ApaAuthoringAssetPaths.ProfileExtension, "outside-assets-folder");
            AssertRejected("Assets", ApaAuthoringAssetPaths.ProfileExtension, "missing-file-name");
            // A folder path and a file name without an extension report the same reason: the remedy is to end the
            // path with the extension the asset type requires.
            AssertRejected("Assets/Parts", ApaAuthoringAssetPaths.ProfileExtension, "wrong-extension");
            AssertRejected("Assets/Arm:1.asset", ApaAuthoringAssetPaths.ProfileExtension, "invalid-character");
            AssertRejected("Assets/Arm.prefab", ApaAuthoringAssetPaths.ProfileExtension, "wrong-extension");
        }

        [Test]
        public void TryValidateAssetPath_ExtensionComparisonIsCaseInsensitive()
        {
            var ok = ApaAuthoringAssetPaths.TryValidateAssetPath(
                "Assets/Parts/Arm.ASSET", ApaAuthoringAssetPaths.ProfileExtension, out _, out var reason);

            Assert.IsTrue(ok, reason);
        }

        [Test]
        public void DecideWriteAction_NeverOverwritesWithoutExplicitPermission()
        {
            Assert.AreEqual(ApaAssetWriteAction.Create, ApaAuthoringAssetPaths.DecideWriteAction(false, false));
            Assert.AreEqual(ApaAssetWriteAction.Create, ApaAuthoringAssetPaths.DecideWriteAction(false, true));
            Assert.AreEqual(ApaAssetWriteAction.RefuseOverwrite, ApaAuthoringAssetPaths.DecideWriteAction(true, false));
            Assert.AreEqual(ApaAssetWriteAction.Update, ApaAuthoringAssetPaths.DecideWriteAction(true, true));
        }

        [Test]
        public void SanitizeFileName_ProducesAUsableNameAndNeverAnEmptyOne()
        {
            Assert.AreEqual("Cyber_Arm_L", ApaAuthoringAssetPaths.SanitizeFileName("Cyber Arm L"));
            Assert.AreEqual("Part1", ApaAuthoringAssetPaths.SanitizeFileName("Part:1"));
            Assert.AreEqual("AvatarPart", ApaAuthoringAssetPaths.SanitizeFileName("   "));
            Assert.AreEqual("AvatarPart", ApaAuthoringAssetPaths.SanitizeFileName("///"));
            Assert.AreEqual("Arm.L", ApaAuthoringAssetPaths.SanitizeFileName("Arm.L"));
        }

        [Test]
        public void DefaultPaths_UseTheSanitizedNameAndTheRequiredExtension()
        {
            Assert.AreEqual("Assets/Parts/Cyber_Arm_LProfile.asset",
                ApaAuthoringAssetPaths.DefaultProfilePath("Assets/Parts", "Cyber Arm L"));
            Assert.AreEqual("Assets/Parts/Cyber_Arm_L.prefab",
                ApaAuthoringAssetPaths.DefaultPrefabPath("Assets/Parts", "Cyber Arm L"));
        }

        [Test]
        public void Combine_NormalizesBothHalves()
        {
            Assert.AreEqual("Assets/Parts/Arm.asset",
                ApaAuthoringAssetPaths.Combine("Assets\\Parts\\", "/Arm.asset"));
            Assert.AreEqual("Arm.asset", ApaAuthoringAssetPaths.Combine(string.Empty, "Arm.asset"));
        }

        [Test]
        public void PathsEqual_IsCaseInsensitiveOverNormalizedPaths()
        {
            Assert.IsTrue(ApaAuthoringAssetPaths.PathsEqual("Assets/Arm.asset", "assets\\ARM.asset"));
            Assert.IsFalse(ApaAuthoringAssetPaths.PathsEqual("Assets/Arm.asset", "Assets/Leg.asset"));
        }

        [Test]
        public void IsUnderFolder_IncludesTheFolderItselfAndExcludesSiblings()
        {
            Assert.IsTrue(ApaAuthoringAssetPaths.IsUnderFolder("Assets/Parts/Arm.asset", "Assets/Parts"));
            Assert.IsTrue(ApaAuthoringAssetPaths.IsUnderFolder("Assets/Parts", "Assets/Parts"));
            Assert.IsFalse(ApaAuthoringAssetPaths.IsUnderFolder("Assets/PartsOther/Arm.asset", "Assets/Parts"));
            Assert.IsFalse(ApaAuthoringAssetPaths.IsUnderFolder("Assets/Arm.asset", "Assets/Parts"));
        }

        [Test]
        public void InvalidPathIssue_CarriesTheAuthoringPathCodeAndTheReasonToken()
        {
            var issue = ApaAuthoringAssetPaths.InvalidPathIssue(
                "Arm.asset", ApaAuthoringAssetPaths.ProfileExtension, "outside-assets-folder", "profile");

            Assert.AreEqual(ApaAuthoringErrorCode.InvalidAuthoringPath, issue.Code);
            Assert.AreEqual("APA033", issue.Code);
            Assert.IsTrue(issue.IsBlocking);
            StringAssert.Contains("reason=outside-assets-folder", issue.Detail);
            StringAssert.Contains("APA033 INVALID_AUTHORING_PATH", ApaDiagnosticText.Format(issue));
        }

        [Test]
        public void FolderSegments_ReturnsEveryFolderOfAnAssetPath()
        {
            CollectionAssert.AreEqual(
                new[] { "Assets", "Parts", "Arms" },
                ApaAuthoringAssetPaths.FolderSegments("Assets/Parts/Arms/Arm.asset"));
        }

        private static void AssertRejected(string path, string extension, string expectedReason)
        {
            var ok = ApaAuthoringAssetPaths.TryValidateAssetPath(path, extension, out var normalized, out var reason);

            Assert.IsFalse(ok, "Expected '" + path + "' to be rejected.");
            Assert.AreEqual(expectedReason, reason);
            Assert.AreEqual(string.Empty, normalized);
        }
    }
}

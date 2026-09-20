using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using AvatarPartAssembler.Editor.Localization;
using AvatarPartAssembler.Editor.Preview;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Contract tests for the localization layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layer's promises are all checkable without a human looking at a window: English is the fallback by
    /// construction, every translated entry must keep its English key's format placeholders, the language
    /// preference must be per-user editor state, the enum display map must cover every defined member, and the
    /// stable diagnostic tokens (<c>APAxxx</c>, <c>reason=…</c>, severity-bearing English text) must survive
    /// translation unchanged.
    /// </para>
    /// <para>
    /// Two tests read the package sources as text. They pin the property that cannot be checked from the compiled
    /// types: that no user-visible English literal in the authoring UI bypasses the localization layer, and that
    /// every literal passed <i>through</i> the layer has an entry. A source scan is the only way to see a control
    /// whose label was written inline, because nothing about the compiled call distinguishes it from a localized
    /// one.
    /// </para>
    /// </remarks>
    public class LocalizationContractTests
    {
        /// <summary>One C# string literal, including its quotes.</summary>
        private const string StringLiteral = "\"(?:[^\"\\\\]|\\\\.)*\"";

        /// <summary>One literal, or a run of literals joined with <c>+</c>, which is how a long key is wrapped.</summary>
        private static readonly string LiteralRun = StringLiteral + @"(?:\s*\+\s*" + StringLiteral + @")*";

        private static readonly string TrLiteralPattern = @"\bTr(?:Format)?\(\s*(?<key>" + LiteralRun + ")";

        private static readonly string ContentLiteralPattern =
            @"\bContent\(\s*(?<key>" + LiteralRun + @")\s*(?:,\s*(?<tip>" + LiteralRun + @"))?\s*\)";

        /// <summary>A key passed through a named constant rather than written inline.</summary>
        private static readonly string ConstKeyPattern = @"\bTr(?:Format)?\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*[,)]";

        private static readonly string ConstDeclarationPattern =
            @"const\s+string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>" + LiteralRun + ")";

        /// <summary>A user-visible control whose first argument is an inline English sentence.</summary>
        private static readonly string InlineUiLiteralPattern =
            @"(?:Button|LabelField|HelpBox|Toggle|ToggleLeft|TextField|ObjectField|IntField|Popup|SetStatus|" +
            @"DisplayDialog|SaveFilePanelInProject|OpenFilePanelWithFilters|new GUIContent)" +
            @"\s*\(\s*""(?<literal>[A-Za-z][^""]{1,})""";

        /// <summary>Matches a composite-format placeholder such as <c>{0}</c>.</summary>
        private static readonly Regex PlaceholderPattern = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);

        private static string PackageRoot
        {
            get
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler");
            }
        }

        // ---- English fallback --------------------------------------------------------------------------

        [Test]
        public void Translate_EnglishReturnsTheKeyItself()
        {
            Assert.AreEqual("Save Profile Asset", ApaLocalization.Translate(ApaLanguage.English, "Save Profile Asset"));
            Assert.AreEqual("… and {0} more", ApaLocalization.Translate(ApaLanguage.English, "… and {0} more"));
        }

        [Test]
        public void Translate_UnknownKeyFallsBackToEnglishInBothLanguages()
        {
            const string unknown = "A sentence no table carries (yet).";

            Assert.AreEqual(unknown, ApaLocalization.Translate(ApaLanguage.English, unknown));
            Assert.AreEqual(
                unknown,
                ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, unknown),
                "A key with no entry must render the English text rather than an empty label.");
        }

        [Test]
        public void Translate_EmptyInputIsSafe()
        {
            Assert.AreEqual(string.Empty, ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, string.Empty));
            Assert.AreEqual(string.Empty, ApaLocalization.Translate(ApaLanguage.English, null));
            Assert.AreEqual(string.Empty, ApaLocalization.TrFormat("", 1));
        }

        [Test]
        public void Resolve_AutoFollowsTheSystemLanguageAndExplicitChoicesWin()
        {
            Assert.AreEqual(
                ApaLanguage.SimplifiedChinese,
                ApaLocalization.Resolve(ApaLanguage.Auto, SystemLanguage.ChineseSimplified));
            Assert.AreEqual(
                ApaLanguage.SimplifiedChinese,
                ApaLocalization.Resolve(ApaLanguage.Auto, SystemLanguage.Chinese));

            Assert.AreEqual(ApaLanguage.English, ApaLocalization.Resolve(ApaLanguage.Auto, SystemLanguage.English));
            Assert.AreEqual(ApaLanguage.English, ApaLocalization.Resolve(ApaLanguage.Auto, SystemLanguage.Japanese));
            Assert.AreEqual(
                ApaLanguage.English,
                ApaLocalization.Resolve(ApaLanguage.Auto, SystemLanguage.ChineseTraditional),
                "Traditional Chinese is not served by the Simplified table.");

            Assert.AreEqual(
                ApaLanguage.English,
                ApaLocalization.Resolve(ApaLanguage.English, SystemLanguage.ChineseSimplified));
            Assert.AreEqual(
                ApaLanguage.SimplifiedChinese,
                ApaLocalization.Resolve(ApaLanguage.SimplifiedChinese, SystemLanguage.English));
        }

        // ---- The table ---------------------------------------------------------------------------------

        [Test]
        public void Table_IsSubstantialAndOnEveryEntryTheValueDiffersFromTheKey()
        {
            Assert.GreaterOrEqual(
                ApaLocalization.TranslationCount,
                300,
                "The Simplified Chinese table must cover the authoring UI, the inspector, the Scene View tool, " +
                "the preview surfaces, and the operation statuses.");

            var untranslated = new List<string>();
            for (var i = 0; i < ApaLocalization.TranslationKeys.Count; i++)
            {
                var key = ApaLocalization.TranslationKeys[i];
                var value = ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, key);

                if (string.IsNullOrEmpty(value) || string.Equals(value, key, StringComparison.Ordinal))
                {
                    untranslated.Add(key);
                    continue;
                }

                if (!ContainsChinese(value)) untranslated.Add(key + " -> " + value);
            }

            Assert.IsEmpty(untranslated, "Entries that are missing, identical to their key, or not Chinese: " +
                                         string.Join(" | ", untranslated.ToArray()));
        }

        [Test]
        public void Table_EveryCompositeEntryKeepsItsPlaceholders()
        {
            var mismatches = new List<string>();
            for (var i = 0; i < ApaLocalization.TranslationKeys.Count; i++)
            {
                var key = ApaLocalization.TranslationKeys[i];
                var value = ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, key);

                var keySlots = Placeholders(key);
                var valueSlots = Placeholders(value);
                if (!string.Equals(keySlots, valueSlots, StringComparison.Ordinal))
                {
                    mismatches.Add("{" + keySlots + "} vs {" + valueSlots + "}: " + key);
                }
            }

            Assert.IsEmpty(
                mismatches,
                "A translated entry must carry exactly the placeholders of its English key, or string.Format " +
                "throws at repaint time: " + string.Join(" | ", mismatches.ToArray()));
        }

        [Test]
        public void TrFormat_FormatsInBothLanguages()
        {
            const string format = "{0} error(s), {1} warning(s), {2} info";

            Assert.AreEqual("2 error(s), 1 warning(s), 0 info", ApaLocalization.TranslateFormat(ApaLanguage.English, format, 2, 1, 0));

            var chinese = ApaLocalization.TranslateFormat(ApaLanguage.SimplifiedChinese, format, 2, 1, 0);
            StringAssert.Contains("2", chinese);
            StringAssert.Contains("1", chinese);
            StringAssert.Contains("0", chinese);
            Assert.IsFalse(chinese.Contains("{0}"), "Every placeholder must have been substituted: " + chinese);
        }

        // ---- The preference ----------------------------------------------------------------------------

        [Test]
        public void LanguagePreference_UsesTheStableEditorPrefsKeyAndNeverAProjectAsset()
        {
            Assert.AreEqual(
                "dev.avatar-part-assembler/localization/language",
                ApaLocalization.LanguagePrefsKey,
                "The key is a persisted contract: renaming it silently resets every user's language choice.");

            var source = ReadEditorSource("Localization", "ApaLocalization.cs");

            StringAssert.Contains("EditorPrefs.SetInt", source);
            StringAssert.Contains("EditorPrefs.GetInt", source);
            Assert.IsFalse(
                source.Contains("AssetDatabase.") || source.Contains("EditorUtility.") || source.Contains("SetDirty"),
                "A language choice is per-user editor state; the localization layer must not write a project asset.");
        }

        [Test]
        public void LanguagePreference_RoundTripsThroughEditorPrefs()
        {
            var hadValue = EditorPrefs.HasKey(ApaLocalization.LanguagePrefsKey);
            var previous = EditorPrefs.GetInt(ApaLocalization.LanguagePrefsKey, (int)ApaLanguage.English);

            try
            {
                ApaLocalization.Language = ApaLanguage.SimplifiedChinese;
                Assert.AreEqual(
                    (int)ApaLanguage.SimplifiedChinese,
                    EditorPrefs.GetInt(ApaLocalization.LanguagePrefsKey, -1),
                    "Selecting a language must persist it.");

                // A domain reload drops the cached value; reloading the preference must reproduce the choice.
                ApaLocalization.ReloadFromPreferences();
                Assert.AreEqual(ApaLanguage.SimplifiedChinese, ApaLocalization.Language);
                Assert.AreEqual(ApaLanguage.SimplifiedChinese, ApaLocalization.ResolvedLanguage);
                Assert.IsTrue(ApaLocalization.IsSimplifiedChinese);

                ApaLocalization.Language = ApaLanguage.Auto;
                ApaLocalization.ReloadFromPreferences();
                Assert.AreEqual(ApaLanguage.Auto, ApaLocalization.Language);
                Assert.AreEqual(
                    ApaLocalization.Resolve(ApaLanguage.Auto, Application.systemLanguage),
                    ApaLocalization.ResolvedLanguage);

                ApaLocalization.Language = ApaLanguage.English;
                ApaLocalization.ReloadFromPreferences();
                Assert.AreEqual(ApaLanguage.English, ApaLocalization.ResolvedLanguage);
                Assert.AreEqual("Save Profile Asset", ApaLocalization.Tr("Save Profile Asset"));
            }
            finally
            {
                if (hadValue) EditorPrefs.SetInt(ApaLocalization.LanguagePrefsKey, previous);
                else EditorPrefs.DeleteKey(ApaLocalization.LanguagePrefsKey);

                ApaLocalization.ReloadFromPreferences();
            }
        }

        // ---- Enum display mapping ----------------------------------------------------------------------

        [Test]
        public void EnumDisplayNames_AreTranslatedWithoutRenamingTheMembers()
        {
            var missing = new List<string>();

            using (new LanguageScope(ApaLanguage.SimplifiedChinese))
            {
                foreach (ApaPartSlot slot in Enum.GetValues(typeof(ApaPartSlot)))
                {
                    Check("ApaPartSlot." + slot, ApaLocalization.DisplayName(slot), slot.ToString(), missing);
                }

                foreach (ApaPartSlotMode mode in Enum.GetValues(typeof(ApaPartSlotMode)))
                {
                    Check("ApaPartSlotMode." + mode, ApaLocalization.DisplayName(mode), mode.ToString(), missing);
                }

                foreach (ApaMaterialPolicyMode policy in Enum.GetValues(typeof(ApaMaterialPolicyMode)))
                {
                    Check(
                        "ApaMaterialPolicyMode." + policy,
                        ApaLocalization.DisplayName(policy),
                        policy.ToString(),
                        missing);
                }
            }

            Assert.IsEmpty(missing, "Enum display names that are not translated: " + string.Join(" | ", missing.ToArray()));

            // The serialized contract: members and their values are untouched by the display mapping.
            Assert.AreEqual("Custom", ApaPartSlot.Custom.ToString());
            Assert.AreEqual(10, (int)ApaPartSlot.Custom);
            Assert.AreEqual(0, (int)ApaPartSlot.Head);
            Assert.AreEqual("Replace", ApaPartSlotMode.Replace.ToString());
            Assert.AreEqual(0, (int)ApaPartSlotMode.Replace);
            Assert.AreEqual(1, (int)ApaPartSlotMode.Augment);
            Assert.AreEqual("Auto", ApaMaterialPolicyMode.Auto.ToString());
            Assert.AreEqual(0, (int)ApaMaterialPolicyMode.Auto);
            Assert.AreEqual(3, (int)ApaMaterialPolicyMode.ForceNew);
        }

        // ---- Diagnostics -------------------------------------------------------------------------------

        [Test]
        public void DiagnosticText_KeepsTheStableTokensAndAddsAChineseDescription()
        {
            var issue = ValidationIssue.Error(
                ApaErrorCode.UvSemanticChannelAbsent,
                ApaIssuePhase.Uv,
                "Declared channel 3 is not present on the part mesh.",
                partId: "part-a",
                sourceIndex: 3,
                detail: "reason=channel-absent; channel=3");

            using (new LanguageScope(ApaLanguage.English))
            {
                var english = ApaDiagnosticText.Format(issue);

                // The English rendering is the package's long-standing diagnostic shape, byte for byte.
                Assert.AreEqual(
                    "ERROR APA050 UV_SEMANTIC_CHANNEL_ABSENT [3]: Declared channel 3 is not present on the " +
                    "part mesh. :: reason=channel-absent; channel=3",
                    english);
            }

            using (new LanguageScope(ApaLanguage.SimplifiedChinese))
            {
                var chinese = ApaDiagnosticText.Format(issue);

                StringAssert.Contains("APA050", chinese);
                StringAssert.Contains("UV_SEMANTIC_CHANNEL_ABSENT", chinese);
                StringAssert.Contains("reason=channel-absent; channel=3", chinese);
                StringAssert.Contains("[3]", chinese);
                StringAssert.Contains("错误", chinese);
                StringAssert.Contains(ApaLocalization.ErrorCodeDescription(ApaErrorCode.UvSemanticChannelAbsent), chinese);

                // An unknown code must stay visible rather than being dropped or emptied.
                var unknown = ValidationIssue.Error("APA777", ApaIssuePhase.Configuration, "from a newer build");
                StringAssert.Contains("APA777", ApaDiagnosticText.Format(unknown));
            }
        }

        [Test]
        public void DiagnosticSeveritiesAndSummaries_AreLocalized()
        {
            Assert.AreEqual("ERROR", ApaLocalization.SeverityLabel(ApaLanguage.English, ApaSeverity.Error));
            Assert.AreEqual("WARNING", ApaLocalization.SeverityLabel(ApaLanguage.English, ApaSeverity.Warning));
            Assert.AreEqual("INFO", ApaLocalization.SeverityLabel(ApaLanguage.English, ApaSeverity.Info));
            Assert.AreEqual("错误", ApaLocalization.SeverityLabel(ApaLanguage.SimplifiedChinese, ApaSeverity.Error));
            Assert.AreEqual("警告", ApaLocalization.SeverityLabel(ApaLanguage.SimplifiedChinese, ApaSeverity.Warning));
            Assert.AreEqual("信息", ApaLocalization.SeverityLabel(ApaLanguage.SimplifiedChinese, ApaSeverity.Info));

            var issues = new List<ValidationIssue>
            {
                ValidationIssue.Error("APA006", ApaIssuePhase.Compatibility, "blocked"),
                ValidationIssue.Warning("APA035", ApaIssuePhase.Removal, "resolved"),
                ValidationIssue.Info("APA039", ApaIssuePhase.Configuration, "parked")
            };

            var result = ValidationResult.Build(issues);

            Assert.AreEqual(
                "1 error(s), 1 warning(s), 1 info",
                ApaLocalization.TranslateFormat(ApaLanguage.English, "{0} error(s), {1} warning(s), {2} info", 1, 1, 1));

            using (new LanguageScope(ApaLanguage.SimplifiedChinese))
            {
                var summarized = ApaDiagnosticText.Summarize(result);
                StringAssert.Contains("1", summarized);
                StringAssert.DoesNotContain("error(s)", summarized);

                // The authoring renderer and the preview renderer describe a code the same way.
                StringAssert.Contains(
                    ApaLocalization.ErrorCodeDescription("APA035"),
                    ApaPreviewDiagnostics.FormatIssue(issues[1]));
            }
        }

        [Test]
        public void PreviewReport_HeadlineAndDisplayString_AreLocalized()
        {
            var report = ApaPreviewDiagnostics.Build(
                ApaPreviewDiagnosticKey.None,
                null,
                null,
                "Body",
                ValidationResult.Build(new List<ValidationIssue>
                {
                    ValidationIssue.Error("APA006", ApaIssuePhase.Compatibility, "no target")
                }),
                new List<string> { "note from the preview layer" },
                true);

            Assert.IsNotNull(report);

            using (new LanguageScope(ApaLanguage.English))
            {
                Assert.AreEqual("Preview blocked — 1 ERROR", report.Summary);
                StringAssert.StartsWith("APA preview: ", report.ToDisplayString(8));
            }

            using (new LanguageScope(ApaLanguage.SimplifiedChinese))
            {
                StringAssert.Contains("预览已阻止", report.Summary);
                StringAssert.Contains("错误", report.Summary);

                var display = report.ToDisplayString(8);
                StringAssert.Contains("APA 预览：", display);
                StringAssert.Contains("APA006", display);
                StringAssert.Contains("注意：", display);
            }
        }

        // ---- Source contracts --------------------------------------------------------------------------

        [Test]
        public void AuthoringMenuAliases_KeepUnitysCanonicalToolsRoot()
        {
            StringAssert.StartsWith("Tools/", ApaAuthoringWindow.MenuPath);
            Assert.IsFalse(
                ApaAuthoringWindow.MenuPath.StartsWith("工具/", StringComparison.Ordinal),
                "MenuItem paths must not localize Unity's top-level Tools root. A separate top-level '工具' menu " +
                "can collide with Unity's localized menu and hide its normal entries.");
        }

        [Test]
        public void AuthoringWindow_RegistersExactlyOneMenuEntry()
        {
            // The plugin used to register an English path and a permanent Chinese alias side by side, which made
            // the Tools menu list the same window twice. The localization now swaps the two spellings of ONE
            // entry instead, so a second live [MenuItem] on the authoring window must never come back.
            var sources = EditorSources();
            Assert.IsNotEmpty(sources, "No editor sources were found; the source scan would be vacuous.");

            var windowSource = sources.FirstOrDefault(
                file => Path.GetFileName(file) == "ApaAuthoringWindow.cs");
            Assert.IsNotNull(windowSource, "ApaAuthoringWindow.cs was not found in the editor sources.");

            var text = File.ReadAllText(windowSource);

            // Sanity-check the scan itself before trusting its counts.
            Assert.AreEqual(
                2,
                Regex.Matches(text, @"\[MenuItem\(").Count,
                "ApaAuthoringWindow must declare exactly two [MenuItem] attributes: the English one and the " +
                "Chinese one. Any third is a duplicate entry.");
            Assert.AreEqual(
                1,
                Regex.Matches(text, @"#if !?APA_CHINESE_MENU").Count,
                "The two attributes must be guarded by complementary APA_CHINESE_MENU conditions.");
        }

        [Test]
        public void LocalizedMenuGroupLabels_AreDistinctAndUntranslated()
        {
            Assert.AreEqual("Avatar Part Assembler", ApaAuthoringWindow.MenuGroupLabel);
            Assert.AreEqual("部件装配器", ApaAuthoringWindow.ChineseMenuGroupLabel);
            Assert.AreNotEqual(
                ApaAuthoringWindow.MenuGroupLabel,
                ApaAuthoringWindow.ChineseMenuGroupLabel,
                "The two menu labels must be different spellings, otherwise the localization would be a no-op.");

            // The item label comes from a named constant rather than the key table, so a missing translation can
            // never silently fall back to English part-way through a menu path.
            Assert.AreEqual("Part Authoring", ApaLocalization.MenuPartAuthoring);
            Assert.AreEqual("部件编辑", ApaLocalization.MenuPartAuthoringChinese);

            // Both spellings must address a real Tools submenu, and neither may localize the canonical root:
            // a top-level '工具' menu can collide with Unity's localized Tools menu and hide its normal entries.
            var english = "Tools/" + ApaAuthoringWindow.MenuGroupLabel + "/" + ApaLocalization.MenuPartAuthoring;
            var chinese =
                "Tools/" + ApaAuthoringWindow.ChineseMenuGroupLabel + "/" + ApaLocalization.MenuPartAuthoringChinese;

            Assert.AreEqual(ApaAuthoringWindow.MenuPath, english);
            Assert.IsTrue(chinese.StartsWith("Tools/", StringComparison.Ordinal));
            Assert.IsFalse(chinese.StartsWith("工具/", StringComparison.Ordinal));
            StringAssert.Contains(ApaAuthoringWindow.ChineseMenuGroupLabel, chinese);
            StringAssert.Contains(ApaLocalization.MenuPartAuthoringChinese, chinese);
        }

        [Test]
        public void EditorAssemblyDefinesTheMenuLanguageSymbol()
        {
            // The menu label is baked into an attribute argument, so the language switch has to be a compile-time
            // symbol. It is declared in the assembly definition; if that entry disappears the Chinese menu silently
            // stops being registered and the English one comes back, which is the bug this pins down.
            var asmdef = Path.Combine(PackageRoot, "Editor", "dev.avatar-part-assembler.editor.asmdef");
            if (!File.Exists(asmdef)) Assert.Ignore("Assembly definition not found: " + asmdef);

            var text = File.ReadAllText(asmdef);
            StringAssert.Contains(
                "\"" + ApaLocalization.MenuLanguageDefineSymbol + "\"",
                text,
                "The editor assembly must define " + ApaLocalization.MenuLanguageDefineSymbol +
                " so the Chinese menu path is the one compiled in. Remove it to compile the English menu.");
        }

        [Test]
        public void EveryLocalizedLiteralInTheEditorSourcesHasAnEntry()
        {
            var sources = EditorSources();
            Assert.IsNotEmpty(sources, "No editor sources were found; the source scan would be vacuous.");

            var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var scan = new Regex(TrLiteralPattern, RegexOptions.Multiline);
            var contentScan = new Regex(ContentLiteralPattern, RegexOptions.Multiline);
            var constScan = new Regex(ConstKeyPattern, RegexOptions.Multiline);
            var constDeclaration = new Regex(ConstDeclarationPattern, RegexOptions.Multiline);

            foreach (var file in sources)
            {
                var text = File.ReadAllText(file);
                var name = Path.GetFileName(file);

                var constants = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Match declaration in constDeclaration.Matches(text))
                {
                    constants[declaration.Groups["name"].Value] = Unescape(declaration.Groups["value"].Value);
                }

                foreach (Match match in scan.Matches(text))
                {
                    Remember(used, name, Unescape(match.Groups["key"].Value));
                }

                foreach (Match match in contentScan.Matches(text))
                {
                    Remember(used, name, Unescape(match.Groups["key"].Value));

                    var tip = match.Groups["tip"];
                    if (tip.Success) Remember(used, name, Unescape(tip.Value));
                }

                foreach (Match match in constScan.Matches(text))
                {
                    string value;
                    if (constants.TryGetValue(match.Groups["name"].Value, out value)) Remember(used, name, value);
                }
            }

            Assert.GreaterOrEqual(
                used.Count, 300, "The source scan found suspiciously few localized literals; check the pattern.");

            var missing = new List<string>();
            foreach (var pair in used)
            {
                if (!ApaLocalization.HasTranslation(pair.Key)) missing.Add("[" + pair.Value + "] " + pair.Key);
            }

            Assert.IsEmpty(
                missing,
                "These literals are passed through the localization layer but have no Simplified Chinese entry: " +
                string.Join(" | ", missing.ToArray()));
        }

        [Test]
        public void AuthoringUiSourcesDoNotBypassTheLocalizationLayer()
        {
            var sources = new[]
            {
                Path.Combine(PackageRoot, "Editor", "Authoring", "ApaAuthoringWindow.cs"),
                Path.Combine(PackageRoot, "Editor", "Authoring", "ApaInstallerInspector.cs"),
                Path.Combine(PackageRoot, "Editor", "Authoring", "ApaAuthoringSceneTool.cs")
            };

            var inline = new Regex(InlineUiLiteralPattern, RegexOptions.Multiline);
            var offenders = new List<string>();

            foreach (var file in sources)
            {
                if (!File.Exists(file))
                {
                    Assert.Ignore("Authoring source not found: " + file);
                }

                var text = File.ReadAllText(file);
                foreach (Match match in inline.Matches(text))
                {
                    offenders.Add(Path.GetFileName(file) + ": " + match.Groups["literal"].Value);
                }
            }

            Assert.IsEmpty(
                offenders,
                "These user-visible labels are inline English literals instead of localized lookups: " +
                string.Join(" | ", offenders.ToArray()));
        }

        [Test]
        public void LocalizationLayerDelegatesToTheSharedAllocationTables()
        {
            // The description table is keyed by the codes the package allocates, so a code with no description is
            // a translation gap rather than a silently different code.
            var codes = new List<string>
            {
                ApaErrorCode.SeamVertexCountMismatch, ApaErrorCode.SeamPositionMismatch,
                ApaErrorCode.SeamDuplicatePositionMatch, ApaErrorCode.SeamUvMismatch,
                ApaErrorCode.UvChannelOverflow, ApaErrorCode.TargetRendererNotFound,
                ApaErrorCode.TargetBoneNotFound, ApaErrorCode.BoneHierarchyConflict,
                ApaErrorCode.MaterialSemanticConflict, ApaErrorCode.RemovalRegionOverlap,
                ApaErrorCode.InvalidBindPose, ApaErrorCode.PartProfileIncompatible,
                ApaErrorCode.DuplicatePartSlot, ApaErrorCode.UnsupportedMeshAttribute,
                ApaErrorCode.UnknownProfileSchema, ApaErrorCode.NonFiniteValue,
                ApaErrorCode.RemovalIndexOutOfRange, ApaErrorCode.InvalidSeamSelection,
                ApaErrorCode.DuplicateSemantic, ApaErrorCode.InvalidSemanticName,
                ApaErrorCode.DegenerateOutputTriangle, ApaErrorCode.InvalidEpsilon,
                ApaErrorCode.InvalidPartSlot, ApaErrorCode.IncompleteCompatibilitySignature,
                ApaErrorCode.WeldUvConflict, ApaErrorCode.InvalidTriangleAddress,
                ApaErrorCode.BlendShapeDuplicateName, ApaErrorCode.BlendShapeFrameMismatch,
                ApaErrorCode.BlendShapeSeamDeltaMismatch, ApaErrorCode.InvalidBlendShapeDelta,
                ApaErrorCode.InvalidBoneWeight, ApaErrorCode.InvalidSpaceTransform,
                ApaErrorCode.InvalidAuthoringPath, ApaErrorCode.NonPersistentReference,
                ApaErrorCode.RemovalMaskTextureFailed,
                ApaErrorCode.SeamPairingRequired, ApaErrorCode.ArmatureSelectionInvalid,
                ApaErrorCode.BoneOutsideSelectedArmature,
                ApaErrorCode.SeamUvPreserved, ApaErrorCode.PartIdDerived,
                ApaErrorCode.RemovalOverlapResolvedByPriority, ApaErrorCode.UndeclaredUvChannel,
                ApaErrorCode.InvalidConflictPriority, ApaErrorCode.SubMeshWithoutMaterialSlot,
                ApaErrorCode.InactiveInstallerSkipped, ApaErrorCode.PartOnlyShapeDisallowed,
                ApaErrorCode.UvSemanticChannelAbsent, ApaErrorCode.MergeVertexGroupInvalid, ApaErrorCode.InternalError
            };

            var missing = new List<string>();
            foreach (var code in codes)
            {
                Assert.IsNotEmpty(ApaErrorCode.GetTitle(code), "The code registry has no title for " + code);
                if (string.IsNullOrEmpty(ApaLocalization.ErrorCodeDescription(code))) missing.Add(code);
            }

            Assert.IsEmpty(missing, "Codes with no Simplified Chinese description: " + string.Join(", ", missing.ToArray()));

            // No description leaks into the English rendering, which is what keeps the English log identical.
            Assert.AreEqual(string.Empty, ApaLocalization.ErrorCodeDescription(ApaLanguage.English, "APA050"));
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        /// <summary>
        /// Selects a language for the duration of a test and restores the previous <see cref="EditorPrefs"/>
        /// state exactly, including "no preference was ever stored".
        /// </summary>
        /// <remarks>
        /// The tests that exercise a translated surface have to change the selected language, because the UI
        /// helpers read the global preference. Restoring the raw key rather than the cached value keeps the test
        /// from leaving a preference behind for the machine's next editor session.
        /// </remarks>
        private sealed class LanguageScope : IDisposable
        {
            private readonly bool _hadValue;
            private readonly int _value;

            internal LanguageScope(ApaLanguage language)
            {
                _hadValue = EditorPrefs.HasKey(ApaLocalization.LanguagePrefsKey);
                _value = EditorPrefs.GetInt(ApaLocalization.LanguagePrefsKey, (int)ApaLanguage.English);
                ApaLocalization.Language = language;
            }

            public void Dispose()
            {
                if (_hadValue) EditorPrefs.SetInt(ApaLocalization.LanguagePrefsKey, _value);
                else EditorPrefs.DeleteKey(ApaLocalization.LanguagePrefsKey);

                ApaLocalization.ReloadFromPreferences();
            }
        }

        private static void Check(string what, string chinese, string memberName, List<string> missing)
        {
            if (string.IsNullOrEmpty(chinese) || string.Equals(chinese, memberName, StringComparison.Ordinal)
                || !ContainsChinese(chinese))
            {
                missing.Add(what + " -> " + chinese);
            }
        }

        private static bool ContainsChinese(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] >= '\u4e00' && text[i] <= '\u9fff') return true;
            }

            return false;
        }

        private static string Placeholders(string text)
        {
            var values = new List<string>();
            foreach (Match match in PlaceholderPattern.Matches(text ?? string.Empty))
            {
                if (!values.Contains(match.Groups[1].Value)) values.Add(match.Groups[1].Value);
            }

            values.Sort(StringComparer.Ordinal);
            return string.Join(",", values.ToArray());
        }

        private static void Remember(IDictionary<string, string> used, string file, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!used.ContainsKey(key)) used[key] = file;
        }

        private static List<string> EditorSources()
        {
            var editor = Path.Combine(PackageRoot, "Editor");
            if (!Directory.Exists(editor))
            {
                Assert.Ignore("Editor source directory not found: " + editor);
            }

            var files = new List<string>(Directory.GetFiles(editor, "*.cs", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        private static string ReadEditorSource(string folder, string fileName)
        {
            var path = Path.Combine(PackageRoot, "Editor", folder, fileName);
            if (!File.Exists(path))
            {
                Assert.Ignore("Editor source not found: " + path);
            }

            return File.ReadAllText(path);
        }

        /// <summary>Evaluates one C# string literal, including its surrounding quotes.</summary>
        private static string Unescape(string literal)
        {
            if (string.IsNullOrEmpty(literal) || literal.Length < 2) return string.Empty;

            var text = new StringBuilder(literal.Length);
            for (var i = 1; i < literal.Length - 1; i++)
            {
                var c = literal[i];
                if (c != '\\')
                {
                    text.Append(c);
                    continue;
                }

                i++;
                if (i >= literal.Length - 1) break;

                switch (literal[i])
                {
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case '\\': text.Append('\\'); break;
                    case '"': text.Append('"'); break;
                    case '\'': text.Append('\''); break;
                    default: text.Append(literal[i]); break;
                }
            }

            return text.ToString();
        }
    }
}

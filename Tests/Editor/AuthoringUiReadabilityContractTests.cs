using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor.Authoring;
using AvatarPartAssembler.Editor.Localization;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Source contracts for the UI readability iteration: the reading order of the Installer Inspector, which
    /// authoring sections are open on the first paint, that the actions that write a part are still reachable, and
    /// that every line the new layout adds is localized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are contracts about a shape of code rather than about a value a test can call. Whether the status
    /// verdict is drawn before the fields, whether a foldout starts closed, and whether a summary is passed into
    /// its title are all decided by the order and the arguments of draw calls that only run inside a real Editor
    /// window — there is no return value to assert on without opening one. The sources are therefore read as
    /// text, exactly as <c>AuthoringSectionOrderTests</c>, <c>InstallerUxContractTests</c>, and
    /// <c>LocalizationContractTests</c> already do for the same reason.
    /// </para>
    /// <para>
    /// <b>UI contracts only.</b> Nothing here re-tests validation, saving, the prefab generator, or the Scene
    /// View tool: those have their own suites, and this iteration changed no behaviour of theirs. What is pinned
    /// here is what a reader of the window sees and in which order, plus the fact that the business calls behind
    /// every button are still the ones the buttons had before.
    /// </para>
    /// </remarks>
    public sealed class AuthoringUiReadabilityContractTests
    {
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

        private static string InspectorSource =>
            ReadSource(Path.Combine("Editor", "Authoring", "ApaInstallerInspector.cs"));

        private static string WindowSource =>
            ReadSource(Path.Combine("Editor", "Authoring", "ApaAuthoringWindow.cs"));

        private static string WriterSource =>
            ReadSource(Path.Combine("Editor", "Authoring", "ApaProfileWriter.cs"));

        // ---- Installer Inspector -----------------------------------------------------------------------

        /// <summary>
        /// The Installer Inspector leads with the verdict, then the actions, then the fields, and keeps the
        /// creator-only block and the diagnostics at the end.
        /// </summary>
        /// <remarks>
        /// This is the whole point of the iteration: a user opens the Inspector to learn whether the part works
        /// and what to do next, so "Status" and the action buttons come before the serialized fields, and the
        /// advanced bone-fit block and the diagnostics list come last, behind foldouts.
        /// </remarks>
        [Test]
        public void InstallerInspector_LeadsWithStatusThenActionsThenFieldsThenAdvancedThenDiagnostics()
        {
            var body = MemberBody(
                InspectorSource, "public override void OnInspectorGUI()", "private void DrawSettings()");

            var expected = new[]
            {
                "DrawHealth(installer);",
                "DrawActions(installer);",
                "DrawSettings();",
                "DrawAdvancedBoneFit(installer);",
                "DrawDiagnostics();"
            };

            var previous = -1;
            foreach (var call in expected)
            {
                var index = body.IndexOf(call, StringComparison.Ordinal);
                Assert.GreaterOrEqual(index, 0, "OnInspectorGUI must draw " + call);
                Assert.Greater(index, previous, call + " is drawn out of order.");
                previous = index;
            }

            // The fields are applied before the foldouts below them are drawn, so a field edit is committed in
            // the pass that drew it.
            var fields = body.IndexOf("DrawSettings();", StringComparison.Ordinal);
            var apply = body.IndexOf("serializedObject.ApplyModifiedProperties();", StringComparison.Ordinal);
            var advanced = body.IndexOf("DrawAdvancedBoneFit(installer);", StringComparison.Ordinal);
            Assert.Greater(apply, fields, "The fields must be applied right after they are drawn.");
            Assert.Greater(advanced, apply, "The advanced block must be drawn after the fields were applied.");
        }

        /// <summary>The advanced bone-fit block and the diagnostics list start collapsed.</summary>
        /// <remarks>
        /// The diagnostics default is conditional rather than fixed: the list opens itself while the last
        /// validation reported an error, because the red verdict says "See the details below" and a collapsed
        /// list would make that sentence false. Both defaults are pinned here, including the rule that a click of
        /// the author's own wins over the automatic one.
        /// </remarks>
        [Test]
        public void InstallerInspector_KeepsTheAdvancedBlockAndTheDiagnosticsCollapsedByDefault()
        {
            var source = InspectorSource;

            Assert.IsTrue(
                Regex.IsMatch(source, @"private bool _showAdvancedBoneFit\s*;"),
                "The advanced block's foldout state must be declared without an initializer, so it starts closed.");
            Assert.IsTrue(
                Regex.IsMatch(source, @"private bool _showDiagnostics\s*;"),
                "The diagnostics foldout state must be declared without an initializer.");

            var flat = Normalize(source);
            StringAssert.Contains(
                "_showAdvancedBoneFit = EditorGUILayout.Foldout(_showAdvancedBoneFit, FoldoutLabel(Tr(\"Advanced: Bone Fit\"), DescribeBoneFitSummary(installer)), true);",
                flat,
                "The advanced block must be drawn as a foldout whose title carries its state.");
            StringAssert.Contains(
                "var next = EditorGUILayout.Foldout(expanded, FoldoutLabel(Tr(\"Validation\"), DescribeDiagnosticsSummary()), true);",
                flat,
                "The diagnostics must be drawn as a foldout whose title carries the last verdict.");

            // A collapsed block draws no body: that is what keeps the first screen about the status.
            StringAssert.Contains("if (!_showAdvancedBoneFit) return;", source);
            StringAssert.Contains("if (!_showDiagnostics) return;", source);

            // The one conditional default, and the author's override of it.
            StringAssert.Contains(
                "_showDiagnostics = _validation != null && _validation.HasErrors;",
                source,
                "The diagnostics list must open itself only while the verdict is failing.");
            StringAssert.Contains("if (!_diagnosticsChoiceMade)", source);
            StringAssert.Contains("_diagnosticsChoiceMade = true;", source);

            // Nothing about the foldout state may become component data.
            StringAssert.Contains("[NonSerialized] private bool _showAdvancedBoneFit;", source);
            StringAssert.Contains("[NonSerialized] private bool _showDiagnostics;", source);
        }

        /// <summary>Every routine action, and the business call behind it, is still in the Inspector.</summary>
        /// <remarks>
        /// The layout moved; the actions did not. Each label is pinned with the call it triggers, so a later
        /// re-layout cannot quietly drop the authoring shortcut or the validation action. The rebuild is
        /// deliberately not in this list: it is the fix for a version problem, not a routine action, and its own
        /// contract below pins that it is drawn only under the version warning.
        /// </remarks>
        [Test]
        public void InstallerInspector_KeepsEveryPrimaryActionAndItsBusinessCall()
        {
            var source = InspectorSource;

            foreach (var label in new[]
                     {
                         "Create Profile Asset…", "Open Profile", "Edit In Part Authoring", "Validate",
                         "Clear Results"
                     })
            {
                StringAssert.Contains("Tr(\"" + label + "\")", source, "The action '" + label + "' is missing.");
            }

            foreach (var call in new[]
                     {
                         "CreateProfileAsset(installer);",
                         "ApaAuthoringWindow.OpenWith(",
                         "RunValidation(installer, true);",
                         "ClearResults(installer);"
                     })
            {
                StringAssert.Contains(call, source, "The action behind it no longer calls " + call);
            }

            // The profile's readability check survives as the gate in front of the version block under the actions.
            StringAssert.Contains("profile.TryMigrate(out migrationMessage)", source);
            StringAssert.Contains("DrawProfileVersionStatus(installer, profile, readable, migrationMessage);", source);

            // The serialized fields are still drawn, and still as the component's own properties.
            foreach (var property in new[]
                     {
                         "_profileProperty", "_partRootProperty", "_targetRendererProperty",
                         "_enabledForBuildProperty", "_followAvatarBonesProperty", "_includeScaleProperty"
                     })
            {
                StringAssert.Contains(property, source, property + " must still be drawn.");
            }

            StringAssert.Contains("EditorGUILayout.PropertyField(", Normalize(source));
        }

        // ---- Installer Inspector: the profile's package version (U6) ------------------------------------

        /// <summary>
        /// The version block compares the profile's recorded package version with the installed one, and the
        /// Inspector never presents the internal schema number as that version.
        /// </summary>
        /// <remarks>
        /// U6: a profile is versioned for the user by the Avatar Part Assembler release that wrote it. The schema
        /// stays an internal readability contract, checked through <c>TryMigrate</c> exactly as before, so the
        /// source must contain the migration check and must not contain a schema-as-version line.
        /// </remarks>
        [Test]
        public void InstallerInspector_StatesThePackageVersionInsteadOfTheSchema()
        {
            var source = InspectorSource;

            StringAssert.Contains("ApaPackageVersion.Compare(recorded, installed)", source);
            StringAssert.Contains("profile.ApaPackageVersion", source);
            StringAssert.Contains("ApaPackageVersion.Current", source);
            StringAssert.Contains(
                "DrawProfileVersionStatus(installer, profile, readable, migrationMessage);", source);

            // The schema check stays, as the readability gate behind the same block.
            StringAssert.Contains("profile.TryMigrate(out migrationMessage)", source);

            Assert.IsFalse(
                source.Contains("Profile schema v"),
                "The schema number must never be printed as the profile's version.");
            Assert.IsFalse(
                source.Contains("current v{1}"),
                "The 'current schema' line must not come back as a user-facing version.");
            Assert.IsFalse(
                source.Contains("profile.SchemaVersion < ApaPartProfile.CurrentSchemaVersion"),
                "A legacy profile is reported by its missing package-version stamp, not by a schema warning.");
        }

        /// <summary>The matching verdict returns before anything is drawn; the two actionable ones state a fix.</summary>
        /// <remarks>
        /// "Version consistent, so nothing is drawn" is a property of control flow rather than of a value, which is
        /// why the source is read here: the matching case must leave the method before the first warning, and each
        /// of the two actionable cases must carry the rebuild that rewrites the stamp inside its own branch.
        /// </remarks>
        [Test]
        public void InstallerInspector_DrawsNothingWhileTheRecordedVersionMatches()
        {
            var body = VersionStatusBody();

            var matches = body.IndexOf("case ApaPackageVersionVerdict.Matches:", StringComparison.Ordinal);
            Assert.GreaterOrEqual(matches, 0, "The matching verdict must be decided explicitly.");

            var notRecorded = body.IndexOf(
                "case ApaPackageVersionVerdict.NotRecorded:", matches + 1, StringComparison.Ordinal);
            Assert.Greater(notRecorded, matches, "The matching verdict must be one case of the version switch.");

            var matchCase = body.Substring(matches, notRecorded - matches);
            StringAssert.Contains("return;", matchCase, "A matching version must leave the block immediately.");
            Assert.IsFalse(matchCase.Contains("HelpBox"), "A matching version must draw no warning.");
            Assert.IsFalse(matchCase.Contains("LabelField"), "A matching version must draw no line at all.");
            Assert.IsFalse(matchCase.Contains("Button"), "A matching version must offer no action.");

            // The two states that need a decision offer the rebuild that resolves them, inside their own branch and
            // directly under the warning, so the two can never be separated by a later re-layout.
            StringAssert.Contains("ApaPackageVersionVerdict.NotRecorded", body);
            StringAssert.Contains("ApaPackageVersionVerdict.Mismatch", body);

            var notRecordedCase = VerdictCase(body, "NotRecorded");
            var mismatchCase = VerdictCase(body, "Mismatch");

            foreach (var pair in new[]
                     {
                         new KeyValuePair<string, string>("NotRecorded", notRecordedCase),
                         new KeyValuePair<string, string>("Mismatch", mismatchCase)
                     })
            {
                StringAssert.Contains("HelpBox", pair.Value, pair.Key + " must state its reason.");
                StringAssert.Contains(
                    "DrawRebuildProfileButton(installer);",
                    pair.Value,
                    pair.Key + " must offer the rebuild that resolves it.");
                StringAssert.Contains(
                    "MessageType.Warning",
                    pair.Value,
                    pair.Key + " must warn rather than state a fact.");
            }

            // The rebuild entry itself is the one guarded call, and it is the only place the button is drawn.
            var button = MemberBody(InspectorSource, "private void DrawRebuildProfileButton(", "private void ClearValidationCache()");
            StringAssert.Contains("if (GUILayout.Button(Tr(\"Rebuild Profile Configuration\")))", button);
            StringAssert.Contains("RebuildProfileConfiguration(installer);", button);
            StringAssert.Contains("EditorGUI.BeginDisabledGroup(ApaAssetDatabaseUtility.IsPersistent(installer));", button);
        }

        /// <summary>
        /// The rebuild is not a permanent entry in the action row: it exists only as the fix for a version problem.
        /// </summary>
        /// <remarks>
        /// This is the U6 correction. The version block was already silent while the recorded version matched, but
        /// the action row still drew a rebuild button for every installer that had a profile — so a consistent
        /// profile kept an entry the user never asked for. The row is pinned here as free of the rebuild, and the
        /// button is pinned as reachable only from the version block, so "no rebuild while the version is
        /// consistent" is a property of the layout and not only of the warning's control flow.
        /// </remarks>
        [Test]
        public void InstallerInspector_KeepsTheRebuildOutOfTheActionRow()
        {
            var source = InspectorSource;

            var actions = MemberBody(
                source, "private void DrawActions(", "private void DrawProfileVersionStatus(");

            Assert.IsFalse(
                actions.Contains("Rebuild Profile Configuration"),
                "The permanent action row must not offer the rebuild; it belongs under the version warning.");
            Assert.IsFalse(
                actions.Contains("RebuildProfileConfiguration(installer);"),
                "The action row must not call the rebuild.");
            Assert.IsFalse(
                actions.Contains("DrawRebuildProfileButton("),
                "The action row must not draw the rebuild entry.");

            // What the row keeps: open or create the profile, edit it in Part Authoring, validate, clear.
            foreach (var label in new[]
                     {
                         "Create Profile Asset…", "Open Profile", "Edit In Part Authoring", "Validate",
                         "Clear Results"
                     })
            {
                StringAssert.Contains(
                    "Tr(\"" + label + "\")", actions, "The action row lost the routine action '" + label + "'.");
            }

            // The version block is the only caller, and it is drawn after the row, so the button follows the
            // warning it belongs to.
            var versionStatus = VersionStatusBody();
            Assert.AreEqual(
                2,
                Regex.Matches(versionStatus, "DrawRebuildProfileButton\\(").Count,
                "The rebuild must be offered by the two actionable version verdicts and nothing else.");
            StringAssert.Contains("DrawRebuildProfileButton(installer);", versionStatus);

            Assert.Greater(
                source.IndexOf("DrawProfileVersionStatus(installer, profile, readable, migrationMessage);", StringComparison.Ordinal),
                source.IndexOf("private void DrawActions(", StringComparison.Ordinal),
                "The version block must still be drawn from the action area, after the row.");
        }

        /// <summary>The comparison rule itself: exact equality, and no guess when the installed version is unknown.</summary>
        [Test]
        public void PackageVersionVerdict_ComparesExactVersionsAndNeverGuesses()
        {
            Assert.AreEqual(
                ApaPackageVersionVerdict.Matches,
                ApaPackageVersion.Compare("0.5.1", "0.5.1"),
                "Identical versions are the silent state.");
            Assert.AreEqual(
                ApaPackageVersionVerdict.NotRecorded,
                ApaPackageVersion.Compare(string.Empty, "0.5.1"),
                "A profile with no stamp is reported as missing one, not as a mismatch.");
            Assert.AreEqual(
                ApaPackageVersionVerdict.Mismatch,
                ApaPackageVersion.Compare("0.4.0", "0.5.1"),
                "A different version is a mismatch.");
            Assert.AreEqual(
                ApaPackageVersionVerdict.Mismatch,
                ApaPackageVersion.Compare("0.5.1", "0.5.1.1"),
                "The comparison is exact text, not a version range.");

            // An installed version that cannot be read is never reported as agreement or disagreement.
            Assert.AreEqual(
                ApaPackageVersionVerdict.Unverifiable,
                ApaPackageVersion.Compare("0.5.1", string.Empty));
            Assert.AreEqual(
                ApaPackageVersionVerdict.Unverifiable,
                ApaPackageVersion.Compare(string.Empty, string.Empty));
        }

        /// <summary>The stamp is optional profile data, carried by the draft and written by the one writer.</summary>
        /// <remarks>
        /// A profile written before the stamp existed carries no version and must stay readable: the field is
        /// additive, and the migration that decides readability is unchanged. The write itself is pinned as a
        /// source contract because the stamp has to be applied where the profile reaches disk, and only from a
        /// readable installed version.
        /// </remarks>
        [Test]
        public void ProfilePackageVersion_IsOptionalDataStampedByTheWriter()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            ApaPartProfile rebuilt = null;
            try
            {
                Assert.AreEqual(string.Empty, profile.ApaPackageVersion);
                Assert.IsFalse(
                    profile.HasApaPackageVersion, "A profile that records no version is the legacy default.");
                Assert.IsTrue(
                    profile.TryMigrate(out var message),
                    "A missing version stamp must not affect schema readability: " + message);

                profile.ApaPackageVersion = "0.5.1";
                var draft = ApaProfileDraft.FromProfile(profile);
                Assert.AreEqual("0.5.1", draft.ApaPackageVersion, "The draft must carry the loaded stamp.");

                rebuilt = draft.Materialize();
                Assert.AreEqual("0.5.1", rebuilt.ApaPackageVersion, "Materialize must carry the stamp through.");
            }
            finally
            {
                if (rebuilt != null) UnityEngine.Object.DestroyImmediate(rebuilt);
                UnityEngine.Object.DestroyImmediate(profile);
            }

            var writer = WriterSource;
            StringAssert.Contains("ApaPackageVersion.Current", writer);
            StringAssert.Contains(
                "if (!string.IsNullOrEmpty(installedVersion)) workingCopy.ApaPackageVersion = installedVersion;",
                writer,
                "The writer stamps the installed version, and never overwrites a stamp with an unknown one.");
        }

        /// <summary>
        /// The draft follows the stamp of the profile that was written from it, and only where a write happened.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The stamp the writer records on the asset has to reach the draft as well: otherwise a draft loaded
        /// from a profile written by an older release still carries the older stamp after the save, the draft and
        /// the asset differ by that one field, and the prefab step refuses a profile that was just saved.
        /// </para>
        /// <para>
        /// The reverse is just as important. A refused overwrite reports the profile it <i>found</i> — an asset
        /// this write never touched — and a failed write guarantees nothing at all, so neither may bring the
        /// draft in line: doing so would mark a difference the author still has as saved. The method is therefore
        /// read as control flow rather than as one literal: every result it returns is classified by the status
        /// it reports, and the adoption is required in the code that runs before the two write results, after the
        /// asset has been saved, and in none of the code that runs before a refusal or the failure.
        /// </para>
        /// <para>
        /// The behaviour itself is tested on real files by <c>AuthoringProfileVersionStampTests</c>; this pins
        /// the shape that makes the behaviour true for every caller of the writer, including a caller that never
        /// compares a draft with an asset itself.
        /// </para>
        /// </remarks>
        [Test]
        public void ProfileWriter_AdoptsTheWrittenStampOnlyOnThePathsThatWrote()
        {
            var save = MemberBody(
                WriterSource,
                "public static ApaProfileWriteResult Save(ApaProfileDraft draft, string assetPath, " +
                "bool allowOverwrite)",
                "public static bool HasUnsavedChanges(");

            var results = WriteResultSegments(save);
            Assert.AreEqual(8, results.Count, "Every path out of Save reports its own result.");

            foreach (var result in results)
            {
                var adopted = result.Value.Contains("AdoptWrittenPackageVersion(draft,");
                var wrote = result.Key == "Created" || result.Key == "Updated";

                Assert.AreEqual(
                    wrote,
                    adopted,
                    wrote
                        ? "The " + result.Key + " path must bring the draft in line with the asset it wrote."
                        : "The " + result.Key + " path wrote nothing, so it must not touch the draft's stamp.");

                if (!wrote) continue;

                var savedAt = result.Value.IndexOf("AssetDatabase.SaveAssets()", StringComparison.Ordinal);
                var adoptedAt = result.Value.IndexOf("AdoptWrittenPackageVersion(draft,", StringComparison.Ordinal);
                Assert.Greater(savedAt, -1, "The " + result.Key + " path must save the asset.");
                Assert.Greater(
                    adoptedAt, savedAt, "The draft follows the stamp only after the asset has been saved.");
            }

            Assert.AreEqual(
                2,
                Regex.Matches(save, "AdoptWrittenPackageVersion\\(").Count,
                "The draft is brought in line once per write path and nowhere else.");
        }

        // ---- Part Authoring window ---------------------------------------------------------------------

        /// <summary>Selection and Signature start open; every later block starts closed.</summary>
        [Test]
        public void AuthoringWindow_OpensSelectionAndSignatureAndCollapsesEveryLaterBlock()
        {
            var source = WindowSource;

            Assert.IsTrue(
                Regex.IsMatch(source, @"\[SerializeField\] private bool _showSelectionSection = true;"),
                "Selection is the first decision of the workflow, so it must start open.");
            Assert.IsTrue(
                Regex.IsMatch(source, @"\[SerializeField\] private bool _showSignatureSection = true;"),
                "The target signature must start open, next to the selection it belongs to.");

            var collapsed = new[]
            {
                "_showIdentitySection", "_showRemovalSection", "_showSeamSection", "_showUvSection",
                "_showMaterialSection", "_showPolicySection", "_showOutputSection", "_showDiagnosticsSection"
            };

            foreach (var field in collapsed)
            {
                Assert.IsTrue(
                    Regex.IsMatch(source, @"\[SerializeField\] private bool " + field + @"\s*;"),
                    field + " must be declared without an initializer, so it starts collapsed.");
                Assert.IsFalse(
                    source.Contains(field + " = true"),
                    field + " must not default to open.");
            }

            // The state is window UI state, never profile data: it is serialized on the window and must not be
            // reachable from the draft the build consumes.
            foreach (var field in new List<string>(collapsed)
                     {
                         "_showSelectionSection", "_showSignatureSection"
                     })
            {
                StringAssert.Contains("ref " + field, source, field + " must be the foldout's state.");
            }
        }

        /// <summary>Each collapsible section passes a summary into its foldout title.</summary>
        /// <remarks>
        /// A collapsed block without a summary is a blind click. The expected calls are pinned whole — the state
        /// field, the title, and the summary expression — so a section cannot be collapsed without saying what it
        /// contains, and the summary cannot be replaced by a constant that says nothing.
        /// </remarks>
        [Test]
        public void AuthoringWindow_EveryCollapsibleSectionCarriesASummaryInItsTitle()
        {
            var flat = Normalize(WindowSource);

            var expected = new[]
            {
                "DrawSectionFoldout(ref _showSelectionSection, Tr(\"Selection\"), DescribeSelectionReadiness())",
                "DrawSectionFoldout(ref _showSignatureSection, TrFormat(\"Target Signature (schema v{0})\", " +
                "ApaPartProfile.CurrentSchemaVersion), DescribeSignatureReadiness())",
                "DrawSectionFoldout(ref _showIdentitySection, Tr(\"Part Identity\"), DescribeIdentitySummary())",
                "DrawSectionFoldout(ref _showRemovalSection, Tr(\"Removal Region\"), " +
                "WithProblemCount(mask.Describe(), ValidateRemoval()))",
                "DrawSectionFoldout(ref _showSeamSection, Tr(\"Seam (paired loops)\"), " +
                "WithProblemCount(seam.Describe(), ValidateSeam()))",
                "DrawSectionFoldout(ref _showUvSection, Tr(\"UV Semantics\"), " +
                "DescribeSemanticSummary(_draft.UvSemantics.Length, ApaIssuePhase.Uv))",
                "DrawSectionFoldout(ref _showMaterialSection, Tr(\"Material Semantics\"), " +
                "DescribeSemanticSummary(_draft.MaterialSemantics.Length, ApaIssuePhase.Materials))",
                "DrawSectionFoldout(ref _showPolicySection, Tr(\"Bones And Blend Shapes\"), " +
                "DescribePolicySummary())",
                "DrawSectionFoldout(ref _showOutputSection, Tr(\"Output\"), DescribeOutputReadiness())",
                "DrawSectionFoldout(ref _showDiagnosticsSection, Tr(\"Diagnostics\"), " +
                "DescribeDiagnosticsSummary())"
            };

            foreach (var call in expected)
            {
                // StringAssert.Contains(expected, actual): the window source is the text searched, and the call
                // is the substring it must contain.
                StringAssert.Contains(call, flat, "A collapsible section lost its summary: " + call);
            }

            // The title and the summary are joined into the one line the foldout shows. The separator is a single
            // space on each side of the em dash, so the label reads as one sentence rather than two columns.
            StringAssert.Contains(
                "return string.IsNullOrEmpty(summary) ? title : title + \" — \" + summary;",
                WindowSource,
                "The foldout label must carry the summary beside the title.");

            // A collapsed section's own problems are part of that line, so nothing blocking can hide in a
            // foldout: the removal, seam, and semantic summaries all carry their issue count.
            StringAssert.Contains(
                "return summary + \", \" + TrFormat(\"{0} problem(s)\", issues.Issues.Count);",
                WindowSource);
            StringAssert.Contains("DescribeSemanticSummary(int rows, ApaIssuePhase phase)", WindowSource);
        }

        /// <summary>The Actions block is never folded, and its four writes are still the same calls.</summary>
        [Test]
        public void AuthoringWindow_KeepsTheActionsOutsideEveryFoldout()
        {
            var body = MemberBody(
                WindowSource, "private void DrawActionsSection()", "private void DrawDiagnosticsSection()");

            Assert.IsFalse(
                body.Contains("DrawSectionFoldout"),
                "The actions that write the part must never be hidden behind a foldout.");

            foreach (var label in new[] { "Validate", "Dry-Run Assembly", "Save Profile Asset", "Create Part Prefab" })
            {
                StringAssert.Contains("Tr(\"" + label + "\")", body, "The action '" + label + "' is missing.");
            }

            foreach (var call in new[] { "RunValidation();", "RunDryRun();", "WriteProfile();", "CreatePrefab();" })
            {
                StringAssert.Contains(call, body, "The button no longer calls " + call);
            }
        }

        /// <summary>
        /// The prefab step still refuses a draft that differs from the saved profile: the check the reported
        /// regression tripped on.
        /// </summary>
        /// <remarks>
        /// The fix for the stamp belongs in the writer, not here. This guard is what makes an unsaved draft
        /// unbuildable, and it has to keep comparing the draft with the profile rather than being relaxed to let
        /// a just-saved profile through: the reason the comparison reported a difference was that the draft was
        /// never brought in line with the write, which is fixed where the write happens.
        /// <c>AuthoringProfileVersionStampTests</c> asserts the comparison this guard makes, on the real asset a
        /// save produces.
        /// </remarks>
        [Test]
        public void AuthoringWindow_RefusesThePrefabStepWhileTheDraftDiffersFromTheSavedProfile()
        {
            var resolve = MemberBody(
                WindowSource,
                "private ApaPartProfile ResolveSavedProfile()",
                "private bool PassesPreWriteValidation(");

            StringAssert.Contains(
                "ApaProfileWriter.HasUnsavedChanges(asset, _draft)",
                resolve,
                "The prefab step must compare the draft with the profile that would be referenced.");
            StringAssert.Contains("return null;", resolve, "A draft that differs must stop the prefab step.");
            StringAssert.Contains("SetStatus(", resolve, "The refusal must say what to do about it.");

            var create = MemberBody(
                WindowSource, "private void CreatePrefab()", "private void UpdatePrefabInstaller()");
            StringAssert.Contains(
                "ResolveSavedProfile();", create, "Creating a prefab must build from the saved profile.");

            var update = MemberBody(
                WindowSource, "private void UpdatePrefabInstaller()", "private void ReportPrefabResult(");
            StringAssert.Contains(
                "ResolveSavedProfile();", update, "Updating a prefab must build from the saved profile.");
        }

        /// <summary>The workflow summary is one line directly under the toolbar.</summary>
        [Test]
        public void AuthoringWindow_DrawsTheWorkflowSummaryDirectlyUnderTheToolbar()
        {
            var source = WindowSource;

            var toolbar = source.IndexOf("DrawToolbar();", StringComparison.Ordinal);
            var summary = source.IndexOf("DrawWorkflowSummary();", StringComparison.Ordinal);
            var load = source.IndexOf("DrawProfileLoadSection();", StringComparison.Ordinal);

            Assert.GreaterOrEqual(toolbar, 0);
            Assert.Greater(summary, toolbar, "The workflow summary belongs directly under the toolbar.");
            Assert.Greater(load, summary, "The summary is read before the first section is drawn.");

            var body = MemberBody(
                WindowSource, "private void DrawWorkflowSummary()", "private string DescribeSelectionReadiness()");

            var flat = Normalize(body);
            StringAssert.Contains(
                "TrFormat(\"Workflow: selection {0}; signature {1}; seam {2}; output {3}\", " +
                "DescribeSelectionReadiness(), DescribeSignatureReadiness(), DescribeSeamReadiness(), " +
                "DescribeOutputReadiness())",
                flat,
                "The summary must state the selection, the signature, the seam, and the output.");

            // It reads state that is already cached; it must not capture a mesh or run a validation of its own.
            Assert.IsFalse(flat.Contains("MeshSnapshotFactory."), "The summary must not capture a mesh.");
            Assert.IsFalse(flat.Contains("ApaAuthoringValidation."), "The summary must not run a validation.");
            Assert.IsFalse(flat.Contains("RunValidation"), "The summary must not run a validation.");
        }

        // ---- Localization ------------------------------------------------------------------------------

        /// <summary>Every line the new layout adds has a Chinese entry that keeps its placeholders.</summary>
        /// <remarks>
        /// The English text is the key, so a missing entry would silently fall back to English in the middle of a
        /// Chinese window. The placeholder half is checked as well because a translated entry with a different
        /// <c>{n}</c> set throws at repaint time, which is the one localization failure a user sees as a broken
        /// window rather than as English text.
        /// </remarks>
        [Test]
        public void NewUiCopy_HasChineseEntriesWithMatchingPlaceholders()
        {
            var keys = new[]
            {
                // Installer Inspector
                "Settings",
                "Advanced: Bone Fit",
                "scene installer only",
                "not available in play mode",
                "armature lock active",
                "armature selection problem",
                "no matching bones",
                "{0} bone pair(s)",
                "This installer is disabled for build, so it contributes nothing and validation is skipped. " +
                "Enable 'Enabled For Build' in Settings to install it.",
                "This profile cannot be used by the current build: {0}. Re-author it in Part Authoring " +
                "instead of rebuilding it.",

                // Installer Inspector: the profile's recorded package version (U6)
                "This profile does not record which Avatar Part Assembler version created it. " +
                "Rebuild its configuration with the installed version ({0}) to stamp it.",
                "This profile was created by Avatar Part Assembler {0}, and the installed version is " +
                "{1}. Rebuild its configuration to recapture it with the installed version.",
                "The installed Avatar Part Assembler version could not be read from the package " +
                "manifest, and this profile records none either, so its version cannot be " +
                "checked.",
                "The installed Avatar Part Assembler version could not be read from the package " +
                "manifest, so the {0} this profile records cannot be checked against it.",

                // Part Authoring window
                "Workflow: selection {0}; signature {1}; seam {2}; output {3}",
                "{0}/4 selection fields set",
                "{0} problem(s)",
                "{0} row(s)",
                "captured",
                "not captured",
                "captured, safety data incomplete",
                "merge armature on",
                "merge armature off",
                "(not set)"
            };

            var missing = new List<string>();
            var mismatched = new List<string>();

            foreach (var key in keys)
            {
                if (!ApaLocalization.HasTranslation(key))
                {
                    missing.Add(key);
                    continue;
                }

                var value = ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, key);
                if (!string.Equals(Placeholders(key), Placeholders(value), StringComparison.Ordinal))
                {
                    mismatched.Add("{" + Placeholders(key) + "} vs {" + Placeholders(value) + "}: " + key);
                }
            }

            Assert.IsEmpty(
                missing,
                "These new user-visible strings have no Simplified Chinese entry: " +
                string.Join(" | ", missing.ToArray()));
            Assert.IsEmpty(
                mismatched,
                "These translations do not carry the placeholders of their English key: " +
                string.Join(" | ", mismatched.ToArray()));

            // The two section-level additions, spelled out: a wrong entry here would rename a whole block.
            Assert.AreEqual("设置", ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, "Settings"));
            Assert.AreEqual(
                "高级：骨骼适配",
                ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, "Advanced: Bone Fit"));
            Assert.AreEqual(
                "流程：选择 {0}；签名 {1}；接缝 {2}；输出 {3}",
                ApaLocalization.Translate(
                    ApaLanguage.SimplifiedChinese,
                    "Workflow: selection {0}; signature {1}; seam {2}; output {3}"));

            // The version block's copy, spelled out: it is the one place the user learns which release wrote the
            // profile, so a wrong or placeholder-dropping entry would misreport the part's provenance.
            Assert.AreEqual(
                "此配置文件未记录创建它的部件装配器版本。请使用已安装版本（{0}）重构其配置，以写入该版本。",
                ApaLocalization.Translate(
                    ApaLanguage.SimplifiedChinese,
                    "This profile does not record which Avatar Part Assembler version created it. " +
                    "Rebuild its configuration with the installed version ({0}) to stamp it."));
            Assert.AreEqual(
                "此配置文件由部件装配器 {0} 创建，当前安装的版本为 {1}。请重构其配置，以使用已安装版本重新捕获。",
                ApaLocalization.Translate(
                    ApaLanguage.SimplifiedChinese,
                    "This profile was created by Avatar Part Assembler {0}, and the installed version is " +
                    "{1}. Rebuild its configuration to recapture it with the installed version."));

            // And the new labels are drawn through the localization layer rather than written inline.
            StringAssert.Contains("Tr(\"Settings\")", InspectorSource);
            StringAssert.Contains("Tr(\"Advanced: Bone Fit\")", InspectorSource);
        }

        // ---- Helpers -----------------------------------------------------------------------------------

        /// <summary>The text of a member, from its declaration up to the next member's declaration.</summary>
        private static string MemberBody(string source, string declaration, string nextDeclaration)
        {
            var start = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, declaration + " was not found.");

            var end = source.IndexOf(nextDeclaration, start, StringComparison.Ordinal);
            Assert.Greater(end, start, nextDeclaration + " was not found after " + declaration + ".");

            return source.Substring(start, end - start);
        }

        /// <summary>
        /// One entry per write result a method returns: the status it reports, and the code that runs before it
        /// since the previous result.
        /// </summary>
        /// <remarks>
        /// A call that only runs on one path out of the method is inside the segment that ends at that path's
        /// result, so "this refusal must not do X" and "this write must do X after saving" are properties of the
        /// segments rather than of the method as a whole.
        /// </remarks>
        private static List<KeyValuePair<string, string>> WriteResultSegments(string body)
        {
            const string marker = "return new ApaProfileWriteResult(";
            var segments = new List<KeyValuePair<string, string>>();

            var previousEnd = 0;
            for (var start = body.IndexOf(marker, StringComparison.Ordinal);
                 start >= 0;
                 start = body.IndexOf(marker, previousEnd, StringComparison.Ordinal))
            {
                var end = body.IndexOf(");", start, StringComparison.Ordinal);
                Assert.Greater(end, start, "Every write result must be a single statement.");

                var statement = body.Substring(start, end + 2 - start);
                var status = Regex.Match(statement, @"ApaProfileWriteStatus\.([A-Za-z]+)");
                Assert.IsTrue(status.Success, "Every write result must name its status: " + statement);

                segments.Add(new KeyValuePair<string, string>(
                    status.Groups[1].Value,
                    body.Substring(previousEnd, start - previousEnd)));

                previousEnd = end + 2;
            }

            return segments;
        }

        /// <summary>The body of the installer Inspector's version block, up to the rebuild entry it may draw.</summary>
        /// <remarks>
        /// The version block is the only member allowed to offer the rebuild, so its body is bounded by the
        /// rebuild helper that follows it rather than by the next unrelated member.
        /// </remarks>
        private static string VersionStatusBody()
        {
            return MemberBody(
                InspectorSource,
                "private void DrawProfileVersionStatus(",
                "private void DrawRebuildProfileButton(");
        }

        /// <summary>One <c>case</c> of the version block's verdict switch, from its label to the next case.</summary>
        /// <remarks>
        /// Reading a single branch is what makes "the rebuild sits inside this verdict, under its warning" a
        /// checkable property: a call moved back out of the switch — the layout this correction removed — would no
        /// longer be part of either branch.
        /// </remarks>
        private static string VerdictCase(string body, string verdict)
        {
            var start = body.IndexOf(
                "case ApaPackageVersionVerdict." + verdict + ":", StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, "The " + verdict + " verdict must be decided explicitly.");

            var next = body.IndexOf("case ApaPackageVersionVerdict.", start + 1, StringComparison.Ordinal);
            if (next < 0) next = body.IndexOf("default:", start + 1, StringComparison.Ordinal);
            Assert.Greater(next, start, "The " + verdict + " verdict must be a case of the version switch.");

            return body.Substring(start, next - start);
        }

        /// <summary>
        /// Collapses every run of whitespace to one space and drops the space a wrapped argument list leaves
        /// after an opening parenthesis, so a call split over several lines matches the single line it means.
        /// </summary>
        /// <remarks>
        /// Only whitespace outside the literals is affected in practice: the keys themselves are single-spaced
        /// sentences, so collapsing a run never changes one.
        /// </remarks>
        private static string Normalize(string text)
        {
            return Regex.Replace(Regex.Replace(text, @"\s+", " "), @"\(\s+", "(");
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

        private static string ReadSource(string relativePath)
        {
            var path = Path.Combine(PackageRoot, relativePath);
            if (!File.Exists(path)) Assert.Ignore("Package source not found: " + path);

            return File.ReadAllText(path);
        }
    }
}

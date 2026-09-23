using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The Avatar Part Installer inspector: whether this part is ready, and the shortcuts that open the authoring
    /// workflow, including the profile's recorded package-version verdict and the guarded profile refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The end-user experience the product promises is "drag the prefab in and it works", so the inspector leads
    /// with one concise health line — green when the part validates, red when it does not — and keeps the full
    /// diagnostic list underneath for the user who has to fix something. The developer-facing detail (part id,
    /// armature selections, schema and signature, recorded target path, removal and seam counts, UV and material
    /// semantics) belongs to the authoring tooling and is deliberately not repeated here. Every diagnostic it
    /// prints begins with its stable <c>APA</c> code, because the code is what a user searches for and what a bug
    /// report has to carry.
    /// </para>
    /// <para>
    /// Nothing here mutates an asset or a mesh: the validation builds a context and runs the same core the build
    /// uses, and reports what it returned. It is not run on every repaint — the inspector validates once per
    /// relevant change (see <see cref="RefreshValidation"/>) or when the user presses Validate — because a full
    /// validation walks the whole avatar. Reads go through the profile's non-mutating <c>*OrNull</c> accessors:
    /// a repaint must never materialize a nested object onto the shared profile asset.
    /// </para>
    /// </remarks>
    [CustomEditor(typeof(AvatarPartInstaller))]
    [CanEditMultipleObjects]
    public sealed class ApaInstallerInspector : UnityEditor.Editor
    {
        /// <summary>The green of the "this part is ready" indicator.</summary>
        private static readonly Color s_readyBackground = new Color(0.13f, 0.42f, 0.18f);

        /// <summary>Text color that stays readable on the green indicator in both editor skins.</summary>
        private static readonly Color s_readyText = new Color(0.86f, 1f, 0.88f);

        /// <summary>The red of the "this part has a problem" indicator.</summary>
        private static readonly Color s_problemBackground = new Color(0.50f, 0.13f, 0.13f);

        /// <summary>Text color that stays readable on the red indicator in both editor skins.</summary>
        private static readonly Color s_problemText = new Color(1f, 0.88f, 0.88f);

        /// <summary>
        /// The concise health of the selected installer, derived from the cached validation result.
        /// </summary>
        private enum ApaInstallerHealth
        {
            /// <summary>No verdict is available: nothing was validated yet, or validation cannot run here.</summary>
            NotValidated = 0,

            /// <summary>The last full validation reported no errors.</summary>
            Ready = 1,

            /// <summary>The last full validation reported errors, or could not build a context.</summary>
            Problem = 2
        }

        private SerializedProperty _profileProperty;
        private SerializedProperty _partRootProperty;
        private SerializedProperty _targetRendererProperty;
        private SerializedProperty _enabledForBuildProperty;
        private SerializedProperty _followAvatarBonesProperty;
        private SerializedProperty _includeScaleProperty;

        [NonSerialized] private ValidationResult _validation;
        [NonSerialized] private string _status = string.Empty;
        [NonSerialized] private string _boneFitStatus = string.Empty;
        [NonSerialized] private ApaInstallerHealth _health = ApaInstallerHealth.NotValidated;
        [NonSerialized] private string _healthDetail = string.Empty;

        /// <summary>
        /// Whether the advanced bone-fit block is expanded. Collapsed by default, because bone fit, follow, and
        /// scale are creator actions: the first screen belongs to "is this part usable" and "what do I do next".
        /// </summary>
        /// <remarks>
        /// Inspector state, never component data: the two preferences the block edits are serialized on the
        /// installer, while how the block is displayed is a property of this Inspector session. Nothing here is
        /// written into a scene or a profile.
        /// </remarks>
        [NonSerialized] private bool _showAdvancedBoneFit;

        /// <summary>Whether the diagnostics list is expanded. See <see cref="DrawDiagnostics"/> for the default.</summary>
        [NonSerialized] private bool _showDiagnostics;

        /// <summary>
        /// True once the user opened or closed the diagnostics foldout themselves, which is what stops the
        /// "expanded while there are errors" default from overriding that choice on the next repaint.
        /// </summary>
        [NonSerialized] private bool _diagnosticsChoiceMade;

        /// <summary>
        /// The input signature the cached validation belongs to. A repaint revalidates only when this changes,
        /// which is what keeps a full avatar validation out of the repaint loop.
        /// </summary>
        [NonSerialized] private string _validatedSignature = string.Empty;

        private void OnEnable()
        {
            _profileProperty = serializedObject.FindProperty("_profile");
            _partRootProperty = serializedObject.FindProperty("_partRoot");
            _targetRendererProperty = serializedObject.FindProperty("_targetRendererObject");
            _enabledForBuildProperty = serializedObject.FindProperty("_enabledForBuild");
            _followAvatarBonesProperty = serializedObject.FindProperty("_followAvatarBones");
            _includeScaleProperty = serializedObject.FindProperty("_includeScale");
            _validation = null;
            _status = string.Empty;
            _boneFitStatus = string.Empty;
            _health = ApaInstallerHealth.NotValidated;
            _healthDetail = string.Empty;
            _showDiagnostics = false;
            _diagnosticsChoiceMade = false;

            // An empty signature can never match a real one, so the first paint validates once. The verdict is
            // therefore real from the moment the Inspector opens, without a validation per repaint.
            _validatedSignature = string.Empty;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// <b>The reading order is the end-user order.</b> The status verdict comes first, because "can I use
        /// this part" is the question a user opens the Inspector with; the main actions follow, so the next step
        /// is one click away and not buried under fields; the serialized fields sit below them, still editable and
        /// still visible; the creator-only bone-fit block and the diagnostics list are foldouts that do not take
        /// over the first screen. The order is asserted by a source contract.
        /// </para>
        /// <para>
        /// The serialized object is updated once at the top and applied right after the fields, so a field edit is
        /// committed in the same pass that drew it while the foldouts below still see a valid
        /// <see cref="SerializedObject"/>.
        /// </para>
        /// </remarks>
        public override void OnInspectorGUI()
        {
            var installer = target as AvatarPartInstaller;
            if (installer == null) return;

            serializedObject.Update();

            if (targets.Length > 1)
            {
                EditorGUILayout.HelpBox(
                    TrFormat(
                        "{0} installers are selected. Status and shortcuts apply to '{1}'.",
                        targets.Length, installer.gameObject.name), MessageType.Info);
            }

            DrawHealth(installer);
            DrawActions(installer);
            DrawSettings();
            serializedObject.ApplyModifiedProperties();
            DrawAdvancedBoneFit(installer);
            DrawDiagnostics();
        }

        /// <summary>
        /// The installer's serialized fields, drawn below the status and the actions.
        /// </summary>
        /// <remarks>
        /// They stay expanded rather than becoming a foldout: the profile and the part root are what a user
        /// assigns when something is not configured yet, and hiding them behind a click would trade one kind of
        /// noise for a dead end. They are simply no longer the first thing on screen.
        /// </remarks>
        private void DrawSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Settings"), EditorStyles.boldLabel);

            if (_profileProperty == null || _partRootProperty == null
                || _targetRendererProperty == null || _enabledForBuildProperty == null
                || _followAvatarBonesProperty == null || _includeScaleProperty == null)
            {
                // A renamed serialized field would silently hide the component's data, so the failure is stated
                // rather than drawn as an empty inspector.
                EditorGUILayout.HelpBox(
                    Tr("The inspector could not find the installer's serialized fields. This is an internal " +
                       "consistency error in the package."),
                    MessageType.Error);
                return;
            }

            EditorGUILayout.PropertyField(
                _profileProperty, Content("Profile", "The authored part profile."));
            EditorGUILayout.PropertyField(
                _partRootProperty,
                Content("Part Root", "Root of the part geometry. Empty means this GameObject."));
            EditorGUILayout.PropertyField(
                _targetRendererProperty,
                Content("Target Renderer Object", "Optional explicit target body. Empty resolves the target from " +
                                                  "the profile's captured renderer path."));
            EditorGUILayout.PropertyField(_enabledForBuildProperty, Content("Enabled For Build"));
        }

        /// <summary>
        /// The one concise end-user verdict: green when the part validates, red when it does not, with the
        /// detailed diagnostics kept below in <see cref="DrawDiagnostics"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The verdict is derived from a real full validation, never from a guess about the profile's fields: the
        /// same context and the same core the build uses. It is drawn as a colored box because a green/red
        /// answer is what a user reads at a glance; Unity's own help boxes have no green state.
        /// </para>
        /// <para>
        /// Validation runs through <see cref="RefreshValidation"/>, which caches the result against the inputs it
        /// was computed from, so a repaint does not walk the avatar. The states that cannot be validated at all
        /// (no profile, disabled for build, a prefab asset) are stated as such rather than painted green, because
        /// a green light for an unvalidated part would be a false promise.
        /// </para>
        /// </remarks>
        private void DrawHealth(AvatarPartInstaller installer)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Tr("Status"), EditorStyles.boldLabel);

            // The language selector shares the status row: it stays on the first screen — which matters most to
            // the user who cannot read the current language — without pushing the verdict down a row.
            GUILayout.FlexibleSpace();
            LanguagePopup(null, GUILayout.Width(110));
            EditorGUILayout.EndHorizontal();

            if (installer.Profile == null)
            {
                ClearValidationCache();
                DrawHealthBox(
                    Tr("No profile is assigned, so this part cannot be installed. Create a profile or assign an " +
                       "existing one."),
                    false);
                return;
            }

            if (!installer.EnabledForBuild)
            {
                ClearValidationCache();
                EditorGUILayout.HelpBox(
                    Tr("This installer is disabled for build, so it contributes nothing and validation is " +
                       "skipped. Enable 'Enabled For Build' in Settings to install it."),
                    MessageType.Info);
                return;
            }

            if (ApaAssetDatabaseUtility.IsPersistent(installer))
            {
                ClearValidationCache();
                // A prefab asset has no scene hierarchy to validate against, and validating it in place would
                // report defects that do not exist. This one box is the status and the next step; the action
                // block below deliberately does not repeat it.
                EditorGUILayout.HelpBox(
                    Tr("This installer belongs to a prefab asset, so it can only be validated after it is placed " +
                       "under an avatar in a scene."),
                    MessageType.Info);
                return;
            }

            RefreshValidation(installer);

            switch (_health)
            {
                case ApaInstallerHealth.Ready:
                    DrawHealthBox(
                        TrFormat("This part is ready: validation reported {0}.",
                            ApaDiagnosticText.Summarize(_validation)),
                        true);
                    return;
                case ApaInstallerHealth.Problem:
                    DrawHealthBox(
                        TrFormat("This part has a problem: validation reported {0}. See the details below.",
                            ApaDiagnosticText.Summarize(_validation)),
                        false);
                    return;
                default:
                    EditorGUILayout.HelpBox(
                        string.IsNullOrEmpty(_healthDetail)
                            ? Tr("This part has not been validated yet.")
                            : _healthDetail,
                        MessageType.Warning);
                    return;
            }
        }

        /// <summary>Draws the green or red verdict box.</summary>
        /// <remarks>
        /// The label carries the message and the box carries the color, with a text color chosen to stay readable
        /// on the filled background in both editor skins. The background is painted only on the repaint event;
        /// <see cref="EditorGUI.DrawRect"/> is a no-op for every other event. It is kept directly below
        /// <see cref="DrawHealth"/> because the health verdict and its box are one unit.
        /// </remarks>
        private static void DrawHealthBox(string message, bool ready)
        {
            var style = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                normal = { textColor = ready ? s_readyText : s_problemText }
            };
            var width = Mathf.Max(120f, EditorGUIUtility.currentViewWidth - 28f);
            var height = style.CalcHeight(new GUIContent(message), width);
            var rect = EditorGUILayout.GetControlRect(false, height + 10f);
            EditorGUI.DrawRect(rect, ready ? s_readyBackground : s_problemBackground);
            GUI.Label(
                new Rect(rect.x + 6f, rect.y + 5f, rect.width - 12f, rect.height - 10f),
                message,
                style);
        }

        /// <summary>
        /// The main action area: open or create the profile, edit it in Part Authoring, validate, clear.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One place for the routine actions.</b> The shortcuts block and the rebuild button used to be two
        /// sections with two explanations of the same prefab-asset condition; they are one block now, ordered by
        /// what a user does next rather than by which subsystem the action belongs to. Nothing about what a button
        /// does changed: every call, guard, and confirmation is the one that was here before.
        /// </para>
        /// <para>
        /// <b>The rebuild is not one of the routine actions.</b> It is the fix for one condition — a profile whose
        /// recorded package version is missing or different — so it is drawn by
        /// <see cref="DrawProfileVersionStatus"/> next to the warning that asks for it, and a profile that is up
        /// to date has no rebuild entry anywhere in the Inspector.
        /// </para>
        /// <para>
        /// <b>The prefab-asset note is stated once.</b> The status box above already says that a prefab asset can
        /// only be validated once it is placed under an avatar, so the block states nothing about it and lets the
        /// disabled buttons and that verdict speak.
        /// </para>
        /// </remarks>
        private void DrawActions(AvatarPartInstaller installer)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Actions"), EditorStyles.boldLabel);

            var profile = installer.Profile;
            var isPrefabAsset = ApaAssetDatabaseUtility.IsPersistent(installer);

            EditorGUILayout.BeginHorizontal();

            if (profile == null)
            {
                EditorGUI.BeginDisabledGroup(isPrefabAsset);
                if (GUILayout.Button(Tr("Create Profile Asset…")))
                {
                    CreateProfileAsset(installer);
                }

                EditorGUI.EndDisabledGroup();
            }
            else
            {
                if (GUILayout.Button(Tr("Open Profile")))
                {
                    Selection.activeObject = profile;
                    EditorGUIUtility.PingObject(profile);
                }
            }

            EditorGUI.BeginDisabledGroup(isPrefabAsset);
            if (GUILayout.Button(Tr("Edit In Part Authoring")))
            {
                var root = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
                ApaAuthoringWindow.OpenWith(
                    profile,
                    installer.ResolvePartRoot(),
                    root != null ? root.gameObject : null,
                    ResolveTargetRenderer(installer));
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            // The profile's readability is read once here and handed to the version block below, which is the only
            // place that needs it: an unreadable profile is reported there as the error it is.
            var migrationMessage = string.Empty;
            var readable = profile == null || profile.TryMigrate(out migrationMessage);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(!installer.EnabledForBuild || isPrefabAsset);
            if (GUILayout.Button(Tr("Validate")))
            {
                RunValidation(installer, true);
            }

            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button(Tr("Clear Results")))
            {
                ClearResults(installer);
            }

            EditorGUILayout.EndHorizontal();

            // The rebuild is deliberately not part of this row. Rebuilding is not a routine action: it is the fix
            // for one condition — a profile whose recorded package version is missing or different — so it is
            // drawn only by DrawProfileVersionStatus, next to the warning that asks for it, and an up-to-date
            // profile has no rebuild entry at all.
            DrawProfileVersionStatus(installer, profile, readable, migrationMessage);

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, MessageType.None);
            }
        }

        /// <summary>
        /// The profile's version line: silent while the profile records the installed package version, and a
        /// compact warning with the one action that resolves it when it does not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The version a user sees is the package version, never the schema.</b> A schema number describes the
        /// shape of the serialized data — an internal compatibility contract — and says nothing about which
        /// release produced the profile, so the normal Inspector never prints one. The schema verdict survives
        /// where it is load-bearing: <see cref="ApaPartProfile.TryMigrate"/> still decides whether the profile can
        /// be read at all, and an unreadable profile is reported as the error it is.
        /// </para>
        /// <para>
        /// <b>The normal case draws nothing.</b> A profile that records the installed version is the expected
        /// state, and a permanent "everything is fine" line is exactly the noise this layout removes. Only a
        /// missing or different version is stated, together with the rebuild that rewrites the stamp — a warning
        /// without its fix would be a dead end.
        /// </para>
        /// </remarks>
        private void DrawProfileVersionStatus(
            AvatarPartInstaller installer, ApaPartProfile profile, bool readable, string migrationMessage)
        {
            if (profile == null) return;

            if (!readable)
            {
                EditorGUILayout.HelpBox(
                    TrFormat(
                        "This profile cannot be used by the current build: {0}. Re-author it in Part Authoring " +
                        "instead of rebuilding it.",
                        migrationMessage),
                    MessageType.Error);
                return;
            }

            var recorded = profile.ApaPackageVersion;
            var installed = ApaPackageVersion.Current;

            switch (ApaPackageVersion.Compare(recorded, installed))
            {
                case ApaPackageVersionVerdict.Matches:
                    // Nothing is drawn: the profile and the installed package agree, which is the normal state.
                    return;

                case ApaPackageVersionVerdict.NotRecorded:
                    EditorGUILayout.HelpBox(
                        TrFormat(
                            "This profile does not record which Avatar Part Assembler version created it. " +
                            "Rebuild its configuration with the installed version ({0}) to stamp it.",
                            installed),
                        MessageType.Warning);
                    DrawRebuildProfileButton(installer);
                    return;

                case ApaPackageVersionVerdict.Mismatch:
                    EditorGUILayout.HelpBox(
                        TrFormat(
                            "This profile was created by Avatar Part Assembler {0}, and the installed version is " +
                            "{1}. Rebuild its configuration to recapture it with the installed version.",
                            recorded, installed),
                        MessageType.Warning);
                    DrawRebuildProfileButton(installer);
                    return;

                default:
                    // Unverifiable: the installed version could not be read, so there is nothing to compare and
                    // no action to offer. The reason is stated — the profile's own record is named when it has
                    // one — rather than a schema number being shown in the version's place.
                    EditorGUILayout.LabelField(
                        string.IsNullOrEmpty(recorded)
                            ? Tr("The installed Avatar Part Assembler version could not be read from the package " +
                                 "manifest, and this profile records none either, so its version cannot be " +
                                 "checked.")
                            : TrFormat(
                                "The installed Avatar Part Assembler version could not be read from the package " +
                                "manifest, so the {0} this profile records cannot be checked against it.",
                                recorded),
                        EditorStyles.wordWrappedMiniLabel);
                    return;
            }
        }

        /// <summary>
        /// The one rebuild entry: drawn directly under the version warning that asks for it, and nowhere else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rebuild is not a routine action.</b> It exists to rewrite a profile that no longer records the
        /// installed package version, so it belongs beside the warning that says so; a profile that is up to date
        /// never draws this button, which is what keeps the normal Inspector free of a permanent rebuild entry.
        /// Its only two callers are the two actionable verdicts — <c>NotRecorded</c> and <c>Mismatch</c> — so the
        /// unreadable-version state offers nothing (nothing there proves a rebuild is needed) and an unreadable
        /// profile offers re-authoring instead.
        /// </para>
        /// <para>
        /// The disabled condition is the one the action row used to apply — a prefab asset has no live hierarchy
        /// to recapture from — and the call behind the button is the same guarded
        /// <see cref="RebuildProfileConfiguration"/> it always was, so what the button does is unchanged.
        /// </para>
        /// </remarks>
        private void DrawRebuildProfileButton(AvatarPartInstaller installer)
        {
            EditorGUI.BeginDisabledGroup(ApaAssetDatabaseUtility.IsPersistent(installer));
            if (GUILayout.Button(Tr("Rebuild Profile Configuration")))
            {
                RebuildProfileConfiguration(installer);
            }

            EditorGUI.EndDisabledGroup();
        }

        /// <summary>Discards a verdict that cannot describe the installer's current non-scene state.</summary>
        /// <remarks>Used by every state in which a full validation cannot describe this installer.</remarks>
        private void ClearValidationCache()
        {
            _validation = null;
            _health = ApaInstallerHealth.NotValidated;
            _healthDetail = string.Empty;
            _validatedSignature = string.Empty;

            // Nothing is left to keep open or closed, so the diagnostics foldout returns to its automatic
            // default: expanded again as soon as a later validation reports an error.
            _diagnosticsChoiceMade = false;
        }

        /// <summary>
        /// Validates the part once per relevant change and caches the verdict for every repaint in between.
        /// </summary>
        /// <remarks>
        /// The signature covers the inputs this verdict is computed from: the assigned profile (identity and
        /// dirty count, so an edit made in Part Authoring refreshes it), the part root, the explicit target
        /// renderer, and the build-enabled switch. A repaint whose signature is unchanged reuses the previous
        /// result — that is the difference between a health line and a validation loop.
        /// </remarks>
        private void RefreshValidation(AvatarPartInstaller installer)
        {
            var signature = ComputeValidationSignature(installer);
            if (string.Equals(signature, _validatedSignature, StringComparison.Ordinal)) return;

            RunValidation(installer, false);
        }

        /// <summary>The input signature a cached validation result belongs to.</summary>
        private static string ComputeValidationSignature(AvatarPartInstaller installer)
        {
            var profile = installer.Profile;
            var partRoot = installer.ResolvePartRoot();
            var target = installer.TargetRendererObject;

            return string.Concat(
                (profile != null ? profile.GetInstanceID() : 0).ToString(CultureInfo.InvariantCulture),
                ":",
                (profile != null ? EditorUtility.GetDirtyCount(profile) : 0).ToString(CultureInfo.InvariantCulture),
                ":",
                (profile != null ? profile.SchemaVersion : 0).ToString(CultureInfo.InvariantCulture),
                ":",
                (partRoot != null ? partRoot.GetInstanceID() : 0).ToString(CultureInfo.InvariantCulture),
                ":",
                (target != null ? target.GetInstanceID() : 0).ToString(CultureInfo.InvariantCulture),
                ":",
                installer.EnabledForBuild ? "1" : "0");
        }

        /// <summary>
        /// The collapsed advanced block: bone fit, follow, and scale, behind one foldout that summarizes itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These are creator actions — they align a part authored against a rest-pose body onto the avatar's
        /// current pose, and they write scene transforms while following — so they are not what a user opens the
        /// Inspector to do, and they no longer take over the first screen. The block is not lost: the foldout is
        /// always drawn, and its title carries the current state so the reason to open it is visible while it is
        /// closed.
        /// </para>
        /// <para>
        /// The summary is built from the same checks the block itself runs, and it never mutates anything: the
        /// armature resolution and the bone walk are read-only, and the merge-lock scan is a component query.
        /// </para>
        /// </remarks>
        private void DrawAdvancedBoneFit(AvatarPartInstaller installer)
        {
            EditorGUILayout.Space();
            _showAdvancedBoneFit = EditorGUILayout.Foldout(
                _showAdvancedBoneFit,
                FoldoutLabel(Tr("Advanced: Bone Fit"), DescribeBoneFitSummary(installer)),
                true);

            if (!_showAdvancedBoneFit) return;

            DrawBoneFit(installer);
        }

        /// <summary>
        /// The one-line state the collapsed advanced block shows: why it is unavailable, what needs fixing, or
        /// how many bones the fit would align.
        /// </summary>
        /// <remarks>
        /// The order is the order of the checks inside the block, so the summary cannot describe a state the block
        /// would not reach: unavailable placement first, then the armature lock that gates the controls, then the
        /// resolution failure, then the pair count. The reason tokens are the stable identifiers
        /// <see cref="ApaBoneFitter.TryResolveArmatures"/> reports and are translated by
        /// <see cref="DescribeBoneFitReason"/>, exactly as the block's own message is.
        /// </remarks>
        private static string DescribeBoneFitSummary(AvatarPartInstaller installer)
        {
            if (ApaAssetDatabaseUtility.IsPersistent(installer)) return Tr("scene installer only");
            if (Application.isPlaying) return Tr("not available in play mode");

            if (CollectActiveMergeLocks(installer.transform, null).Count > 0) return Tr("armature lock active");

            if (!ApaBoneFitter.TryResolveArmatures(
                    installer, out var partArmature, out var targetArmature, out var reason))
            {
                return reason == "no-selection" || reason == "resolve-part" || reason == "resolve-target"
                    ? Tr("armature selection problem")
                    : DescribeBoneFitReason(reason);
            }

            var pairs = new List<ApaBoneFitter.BonePair>();
            var unmatched = new List<string>();
            ApaBoneFitter.CollectBonePairs(partArmature, targetArmature, pairs, unmatched);

            return pairs.Count == 0 ? Tr("no matching bones") : TrFormat("{0} bone pair(s)", pairs.Count);
        }

        /// <summary>Joins a section title and its summary into the one line a collapsed foldout shows.</summary>
        private static string FoldoutLabel(string title, string summary)
        {
            return string.IsNullOrEmpty(summary) ? title : title + " — " + summary;
        }

        /// <summary>
        /// The bone-fit block: align the part's skeleton onto the avatar's current pose, once or live.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Modular Avatar's merge preserves each part bone's world pose and only changes its parent, so a part
        /// authored against a rest-pose body keeps that pose even after the avatar's bones have since been moved
        /// or scaled. Writing the avatar's current pose onto the part's bones makes the merge a pose no-op, and
        /// the pairing behind it is the same exact-name walk the merge performs, so what this aligns is what the
        /// merge will merge.
        /// </para>
        /// <para>
        /// The block is offered only for a scene installer: a prefab asset has no avatar to align against, and
        /// the live-follow mode writes scene transforms continuously, which only means something where the
        /// avatar actually is. It is also withdrawn in play mode, where the avatar's pose belongs to animation.
        /// </para>
        /// </remarks>
        private void DrawBoneFit(AvatarPartInstaller installer)
        {
            // The block is drawn inside its own foldout, so it does not repeat the title: the foldout row above
            // already names the block and carries its state.
            if (ApaAssetDatabaseUtility.IsPersistent(installer))
            {
                EditorGUILayout.HelpBox(
                    Tr("Bone fit needs the part placed in a scene under an avatar, so it is offered only for a " +
                       "scene installer."),
                    MessageType.Info);
                return;
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox(Tr("Bone fit is unavailable in play mode."), MessageType.Info);
                return;
            }

            // The snap-back check runs before the fit controls and gates them: while a merge configuration
            // with bone locking sits on the part, Modular Avatar's lock fights every write below, bone for
            // bone — the author would see sync and follow lose to the pull-back.
            var partLocks = CollectActiveMergeLocks(installer.transform, null);
            if (partLocks.Count > 0)
            {
                DrawMergeLockFix(
                    TrFormat(
                        "Modular Avatar's armature lock is active on {0} merge configuration(s) bundled with " +
                        "this part ({1}). It runs in the edit scene and snaps bones back while you pose them — " +
                        "that is the pull-back you are seeing, not the merge preview. APA generates its own " +
                        "merge configuration at build time, so these are not needed here.",
                        partLocks.Count, DescribeMergeLocks(partLocks)),
                    partLocks);
                return;
            }

            var boneFitAvatarRoot = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
            if (boneFitAvatarRoot != null)
            {
                var otherLocks = CollectActiveMergeLocks(boneFitAvatarRoot, installer.transform);
                if (otherLocks.Count > 0)
                {
                    // Locks elsewhere in the avatar (another outfit's configuration) snap the same shared
                    // bones; this part's own fit is not blocked, but the author should know they are there.
                    DrawMergeLockFix(
                        TrFormat(
                            "Modular Avatar's armature lock is active on {0} merge configuration(s) elsewhere " +
                            "in the avatar hierarchy ({1}). Its lock also snaps avatar bones while you pose; " +
                            "if bones still pull back after this part's own locks are disabled, these are the " +
                            "cause.",
                            otherLocks.Count, DescribeMergeLocks(otherLocks)),
                        otherLocks);
                }
            }

            if (!ApaBoneFitter.TryResolveArmatures(
                    installer, out var partArmature, out var targetArmature, out var reason))
            {
                EditorGUILayout.HelpBox(DescribeBoneFitReason(reason), MessageType.Warning);
                return;
            }

            var pairs = new List<ApaBoneFitter.BonePair>();
            var unmatched = new List<string>();
            ApaBoneFitter.CollectBonePairs(partArmature, targetArmature, pairs, unmatched);

            EditorGUI.BeginDisabledGroup(pairs.Count == 0);
            if (GUILayout.Button(Tr("Sync Part Bones To Avatar")))
            {
                SyncBonePose(pairs, unmatched, installer.IncludeScale);
            }

            EditorGUI.EndDisabledGroup();

            // Both options are persisted on the installer, so the author's choice survives closing the Inspector
            // and a domain reload. The write goes through the SerializedObject, which keeps undo and multi-object
            // editing working exactly like the other fields.
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                _followAvatarBonesProperty,
                Content(
                    "Follow Avatar Bones",
                    "Keep this part's bones on the avatar's current pose while the part is placed. Stored on this " +
                    "installer, so the choice survives closing the Inspector and a domain reload."));
            EditorGUILayout.PropertyField(
                _includeScaleProperty,
                Content(
                    "Include Scale",
                    "Also copy the avatar bones' scale onto the matching part bones. Needed when the avatar's " +
                    "bones have been rescaled."));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
            }

            // The running follow session is synchronized to the preferences on every repaint, so the loop always
            // applies exactly what the toggles say — including right after a domain reload, when the session
            // table is empty but the preference is not.
            SynchronizeFollowSession(installer, pairs.Count);
            var following = ApaBoneFollowRuntime.IsFollowing(installer);

            if (pairs.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    Tr("No part bone matched a same-named avatar bone, so there is nothing to align. Check the " +
                       "two Armature selections in Part Authoring."),
                    MessageType.Warning);
            }

            if (following)
            {
                EditorGUILayout.HelpBox(
                    Tr("While following, a moved or scaled avatar bone moves the matching part bones with it " +
                       "every editor update. The writes are not undoable — turn the toggle off to keep the pose " +
                       "as it is. The bone pairs are captured when following turns on, so toggle it off and on " +
                       "again after changing the Armature selections or the part."),
                    MessageType.None);
            }

            if (!string.IsNullOrEmpty(_boneFitStatus))
            {
                EditorGUILayout.HelpBox(_boneFitStatus, MessageType.None);
            }
        }

        /// <summary>
        /// Makes the live follow session match the installer's persisted preferences.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A session is started when the follow preference is on and the two armature selections resolved to at
        /// least one bone pair, refreshed when the scale option changed, and stopped when the preference is off —
        /// so turning either option off is honored immediately, and a session can never outlive the toggle that
        /// asked for it. Starting on the Inspector's first paint is also what makes the preference survive a
        /// domain reload: the session table is session-local and empty after a reload, the preference is not, and
        /// this synchronization rebuilds the session from it.
        /// </para>
        /// <para>
        /// The session is left alone while it already matches, because starting one rebuilds the whole bone pair
        /// list and a repaint must stay cheap.
        /// </para>
        /// </remarks>
        private static void SynchronizeFollowSession(AvatarPartInstaller installer, int pairCount)
        {
            if (!installer.FollowAvatarBones || pairCount == 0)
            {
                if (ApaBoneFollowRuntime.IsFollowing(installer))
                {
                    ApaBoneFollowRuntime.SetFollowing(installer, false, false);
                }

                return;
            }

            if (!ApaBoneFollowRuntime.IsFollowing(installer)
                || ApaBoneFollowRuntime.IsIncludingScale(installer) != installer.IncludeScale)
            {
                ApaBoneFollowRuntime.SetFollowing(installer, true, installer.IncludeScale);
            }
        }

        /// <summary>Writes the avatar pose onto the matched part bones once, under undo, and reports the outcome.</summary>
        private void SyncBonePose(
            List<ApaBoneFitter.BonePair> pairs, List<string> unmatched, bool includeScale)
        {
            var undoObjects = new UnityEngine.Object[pairs.Count];
            for (var i = 0; i < pairs.Count; i++) undoObjects[i] = pairs[i].Part;

            // The write below is plain transform assignment, so undo is recorded around it: one step restores
            // the whole pose. (Bare `Object` would be ambiguous between System and UnityEngine here.)
            Undo.RecordObjects(undoObjects, Tr("Sync Part Bones To Avatar"));
            ApaBoneFitter.ApplyPose(pairs, includeScale);

            if (unmatched.Count == 0)
            {
                _boneFitStatus = TrFormat("Aligned {0} part bone(s) to the avatar's bones.", pairs.Count);
                return;
            }

            const int maxShown = 8;
            var shown = unmatched.Count <= maxShown
                ? string.Join(", ", unmatched)
                : string.Join(", ", unmatched.GetRange(0, maxShown)) + " " +
                  TrFormat("… and {0} more unmatched bone(s)", unmatched.Count - maxShown);
            _boneFitStatus = TrFormat(
                "Aligned {0} part bone(s); {1} had no same-named avatar bone and stayed in place: {2}",
                pairs.Count, unmatched.Count, shown);
        }

        /// <summary>Translates a bone-fit resolution failure into the message the section shows.</summary>
        /// <remarks>
        /// The reason tokens are the stable identifiers <see cref="ApaBoneFitter.TryResolveArmatures"/> reports;
        /// they are translated here rather than inside the fitter so the fitter stays UI-free.
        /// </remarks>
        private static string DescribeBoneFitReason(string reason)
        {
            switch (reason)
            {
                case "no-part-root":
                    return Tr("The part root could not be resolved, so bone fit is unavailable.");
                case "no-avatar-root":
                    return Tr("This installer is not under an avatar, so there is no avatar pose to fit the part " +
                              "to. Place the part under an avatar in a scene.");
                case "no-selection":
                    return Tr("The profile has no Armature selections yet, so the bones cannot be paired. Select " +
                              "both Armatures in Part Authoring (APA043).");
                case "resolve-part":
                    return Tr("The part armature recorded in the profile could not be resolved under the part " +
                              "root. Re-select the Part Armature in Part Authoring.");
                case "resolve-target":
                    return Tr("The target armature recorded in the profile could not be resolved under the avatar " +
                              "root. Re-select the Target Armature in Part Authoring.");
                default:
                    return TrFormat("Bone fit is unavailable ({0}).", reason);
            }
        }

        /// <summary>Collects merge configurations under <paramref name="root"/> whose bone lock is active.</summary>
        /// <remarks>
        /// Modular Avatar's lock system is what actually pulls bones back in the edit scene: the merge
        /// component is <c>[ExecuteInEditMode]</c>, its lock job runs on <c>EditorApplication.update</c> while
        /// the global "Sync Bones in Edit Mode" switch is on (the default), and a Legacy configuration
        /// migrates to BidirectionalExact — which snaps the avatar's bones back toward the part's bones the
        /// moment they are dragged. The NDMF merge preview never writes transforms; this lock does.
        /// </remarks>
        private static List<ModularAvatarMergeArmature> CollectActiveMergeLocks(
            Transform root, Transform excludeSubtree)
        {
            var result = new List<ModularAvatarMergeArmature>();
            var all = root.GetComponentsInChildren<ModularAvatarMergeArmature>(true);
            for (var i = 0; i < all.Length; i++)
            {
                var merge = all[i];
                if (merge == null || merge.LockMode == ArmatureLockMode.NotLocked) continue;
                if (excludeSubtree != null && merge.transform.IsChildOf(excludeSubtree)) continue;
                result.Add(merge);
            }

            return result;
        }

        /// <summary>Renders the names and lock modes of a collected set, truncated for readability.</summary>
        private static string DescribeMergeLocks(List<ModularAvatarMergeArmature> merges)
        {
            const int maxShown = 4;
            var shown = new List<string>();
            for (var i = 0; i < merges.Count && i < maxShown; i++)
            {
                shown.Add(merges[i].name + " (" + merges[i].LockMode + ")");
            }

            var text = string.Join(", ", shown);
            if (merges.Count > maxShown)
            {
                text += " " + TrFormat("… and {0} more merge configuration(s)", merges.Count - maxShown);
            }

            return text;
        }

        /// <summary>Draws one armature-lock warning with its one-click fix.</summary>
        private void DrawMergeLockFix(string message, List<ModularAvatarMergeArmature> merges)
        {
            EditorGUILayout.HelpBox(message, MessageType.Warning);
            if (GUILayout.Button(Tr("Set Lock Mode To Not Locked")))
            {
                DisableMergeLocks(merges);
            }
        }

        /// <summary>Sets every collected configuration to Not Locked, under one undo step.</summary>
        /// <remarks>
        /// The lock controller only rebuilds its job from <c>SetLockMode</c>, which runs on enable, so the
        /// component is cycled off and on after the field write — otherwise the snapping would continue until
        /// the next domain reload. A component the author had disabled is left disabled.
        /// </remarks>
        private void DisableMergeLocks(List<ModularAvatarMergeArmature> merges)
        {
            var undoObjects = new UnityEngine.Object[merges.Count];
            for (var i = 0; i < merges.Count; i++) undoObjects[i] = merges[i];

            Undo.RecordObjects(undoObjects, Tr("Set Lock Mode To Not Locked"));

            for (var i = 0; i < merges.Count; i++)
            {
                var merge = merges[i];
                var wasEnabled = merge.enabled;
                merge.LockMode = ArmatureLockMode.NotLocked;
                merge.enabled = false;
                merge.enabled = true;
                if (!wasEnabled) merge.enabled = false;
                EditorUtility.SetDirty(merge);
            }

            _boneFitStatus = TrFormat("Set {0} merge configuration(s) to Not Locked.", merges.Count);
        }

        /// <summary>
        /// The diagnostics list, behind a foldout that opens itself while the last validation reported errors.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The full list is what a user who has to fix something needs, and it is exactly what a user who does
        /// not should not have to scroll past: the foldout is collapsed for a clean verdict and expanded while
        /// there are errors, so "See the details below" in the red box stays true without the list taking over
        /// every Inspector that has a warning in it.
        /// </para>
        /// <para>
        /// <b>The user's own choice wins.</b> Once the foldout is clicked, the automatic default stops applying,
        /// so a user who collapsed a failing report is not overruled on the next repaint. Clearing the results
        /// resets that, because there is then nothing to keep open or closed.
        /// </para>
        /// </remarks>
        private void DrawDiagnostics()
        {
            if (!_diagnosticsChoiceMade)
            {
                _showDiagnostics = _validation != null && _validation.HasErrors;
            }

            EditorGUILayout.Space();

            var expanded = _showDiagnostics;
            var next = EditorGUILayout.Foldout(
                expanded,
                FoldoutLabel(Tr("Validation"), DescribeDiagnosticsSummary()),
                true);

            if (next != expanded)
            {
                _showDiagnostics = next;
                _diagnosticsChoiceMade = true;
            }

            if (!_showDiagnostics) return;

            if (_validation == null)
            {
                EditorGUILayout.LabelField(Tr("Not validated yet."), EditorStyles.miniLabel);
                return;
            }

            for (var i = 0; i < _validation.Issues.Count; i++)
            {
                var issue = _validation.Issues[i];
                var previous = GUI.color;
                GUI.color = ApaDiagnosticText.ColorOf(issue.Severity);
                EditorGUILayout.LabelField(ApaDiagnosticText.Format(issue), EditorStyles.wordWrappedMiniLabel);
                GUI.color = previous;
            }
        }

        /// <summary>The one-line state the collapsed diagnostics foldout shows.</summary>
        private string DescribeDiagnosticsSummary()
        {
            return _validation == null
                ? Tr("Not validated yet.")
                : ApaDiagnosticText.Summarize(_validation);
        }

        /// <summary>
        /// Validates the whole avatar the installer sits under, using the same core entry point a build uses, and
        /// records the verdict the health indicator reads.
        /// </summary>
        /// <param name="installer">The installer whose avatar is validated.</param>
        /// <param name="reportStatus">
        /// True for the Validate action, which also writes the one-line result into the shortcut status; false for
        /// the automatic refresh behind the health indicator, which must not overwrite what the user last did.
        /// </param>
        /// <remarks>
        /// The signature is stored before the work starts, so a validation that throws or is interrupted cannot
        /// leave the cache claiming an older verdict, and a repaint never repeats it.
        /// </remarks>
        private void RunValidation(AvatarPartInstaller installer, bool reportStatus)
        {
            _validatedSignature = ComputeValidationSignature(installer);

            var anchor = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
            if (anchor == null)
            {
                _validation = null;
                _health = ApaInstallerHealth.NotValidated;
                _healthDetail = Tr("No avatar root could be resolved from this installer.");
                if (reportStatus) _status = _healthDetail;
                return;
            }

            var context = ContextBuilder.Build(anchor.gameObject, null, out var discoveryIssues);
            if (context == null)
            {
                _validation = ValidationResult.Build(discoveryIssues);
                _health = ApaInstallerHealth.Problem;
                _healthDetail = string.Empty;
                if (reportStatus)
                {
                    _status = TrFormat(
                        "Validation could not build a context: {0}.", ApaDiagnosticText.Summarize(_validation));
                }

                return;
            }

            var result = ApaCore.Validate(context);
            var merged = new List<ValidationIssue>(discoveryIssues);
            merged.AddRange(result.Issues);
            _validation = ValidationResult.Build(merged);
            _health = _validation.HasErrors ? ApaInstallerHealth.Problem : ApaInstallerHealth.Ready;
            _healthDetail = string.Empty;

            if (!reportStatus) return;

            _status = _validation.IsValid
                ? TrFormat("Validation passed: {0}.", ApaDiagnosticText.Summarize(_validation))
                : TrFormat("Validation failed: {0}.", ApaDiagnosticText.Summarize(_validation));
        }

        /// <summary>
        /// Drops the displayed diagnostics and the cached verdict without immediately validating again.
        /// </summary>
        /// <remarks>
        /// The current signature is recorded as "already answered", so the next repaint does not re-run the
        /// validation the user just cleared; the next relevant change, or the Validate button, brings it back.
        /// </remarks>
        private void ClearResults(AvatarPartInstaller installer)
        {
            _validation = null;
            _status = string.Empty;
            _boneFitStatus = string.Empty;
            _health = ApaInstallerHealth.NotValidated;
            _healthDetail = Tr("Validation results were cleared. Press Validate to check this part again.");
            _validatedSignature = ComputeValidationSignature(installer);
            _diagnosticsChoiceMade = false;
        }

        /// <summary>
        /// Recaptures the installer configuration from the current avatar and updates the existing profile asset.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The order is the safety property.</b> Everything that can refuse the rebuild runs before the author
        /// is asked to confirm: the profile is read, the live target and part are captured, the draft is checked
        /// for blocking findings, and the assembly is planned through the same dry run the authoring window uses.
        /// Only a draft that passed all of that reaches the confirmation, so the dialog asks about a write that
        /// will actually happen, and a rebuild that is going to be refused never asks at all.
        /// </para>
        /// <para>
        /// <b>What is kept and what is refreshed.</b> The draft starts from the profile, so the stable part id
        /// and every authored removal, seam, material, UV, bone-merge, and blend-shape policy survive. Only the
        /// live capture data is replaced: the target compatibility signature, the part mesh fingerprint, the two
        /// armature selections, and a semantic declaration the profile never made. The write goes through
        /// <see cref="ApaProfileWriter.Save"/> with overwrite allowed, which updates the existing asset in place
        /// through <c>CopySerialized</c>, so the GUID every prefab references is kept — and that is verified
        /// afterwards rather than assumed.
        /// </para>
        /// <para>
        /// A profile the build cannot read is refused here as well as by the button's disabled state: schema 1
        /// and a future schema cannot be migrated without guessing, so neither is ever rewritten into the current
        /// schema.
        /// </para>
        /// </remarks>
        private void RebuildProfileConfiguration(AvatarPartInstaller installer)
        {
            if (installer == null || installer.Profile == null) return;

            var profile = installer.Profile;
            if (!profile.TryMigrate(out var migrationMessage))
            {
                _status = TrFormat(
                    "This profile cannot be rebuilt automatically: {0}. Open Part Authoring and re-author it " +
                    "from scratch.", migrationMessage);
                return;
            }

            // A removal profile whose two address arrays disagree reads as "no triangles removed", because the
            // missing entry cannot be reconstructed without guessing which one it was. A rebuild would therefore
            // write that empty set over the author's removal selection, so the condition is refused here — with
            // the code, message, and reason token the build reports for it — instead of being normalized away by
            // the draft. The dry run cannot catch this: by the time it runs, the draft has already converted the
            // corrupt storage into a well-formed empty mask.
            var removal = profile.RemovalOrNull;
            if (removal != null && removal.HasCorruptStorage)
            {
                _validation = ValidationResult.Build(new List<ValidationIssue>
                {
                    ValidationIssue.Error(
                        ApaErrorCode.InvalidTriangleAddress,
                        ApaIssuePhase.Removal,
                        "The removal profile has mismatched submesh and triangle address arrays. " +
                        "The missing address component cannot be reconstructed without guessing; re-author " +
                        "the removal selection.",
                        ApaPartIdentityResolver.ResolvePartId(installer),
                        detail: "reason=corrupt-removal-storage")
                });
                _health = ApaInstallerHealth.Problem;
                _status = TrFormat(
                    "Profile rebuild was blocked: {0}. Nothing was written.",
                    ApaDiagnosticText.Summarize(_validation));
                return;
            }

            var avatarRoot = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
            var partRoot = installer.ResolvePartRoot();
            var target = ResolveTargetRenderer(installer);
            if (avatarRoot == null || partRoot == null || target == null)
            {
                _status = Tr("Rebuilding needs a scene avatar root, a part root, and a target body renderer. " +
                             "Place the prefab under its avatar and assign or resolve the target renderer first.");
                return;
            }

            var profilePath = AssetDatabase.GetAssetPath(profile);
            if (string.IsNullOrEmpty(profilePath))
            {
                _status = Tr("The assigned profile is not a project asset, so it cannot be rebuilt in place.");
                return;
            }

            // ---- Capture: the draft starts from the profile, so every authored policy is kept -----------------
            var draft = ApaProfileDraft.FromProfile(profile);
            ApaAuthoringSelection.ProposeArmatures(
                avatarRoot.transform,
                target,
                partRoot,
                out var targetArmature,
                out var partArmature);
            draft.SetArmatures(avatarRoot.transform, targetArmature, partRoot.transform, partArmature);

            var rebuilt = new List<ValidationIssue>();
            draft.Compatibility = ApaCompatibilityCapture.Capture(
                avatarRoot.gameObject,
                target,
                out var captureIssues,
                targetArmature);
            rebuilt.AddRange(captureIssues);

            var partRenderer = partRoot.GetComponentInChildren<Renderer>(true);
            var partMesh = ApaCompatibilityCapture.ResolveMesh(partRenderer);
            ValidationIssue protectedIssue = null;
            if (partMesh != null)
            {
                if (!partMesh.isReadable)
                {
                    rebuilt.Add(ApaCompatibilityCapture.NotReadableIssue(partMesh));
                }
                else
                {
                    var snapshot = MeshSnapshotFactory.Capture(partMesh, null, out var snapshotIssues);
                    rebuilt.AddRange(snapshotIssues);

                    // A snapshot that could not be taken leaves the profile's own fingerprint in place rather
                    // than clearing it: an absent fingerprint is not checked at all, so dropping it would make
                    // the rebuilt profile weaker than the one it replaced.
                    if (snapshot != null) draft.PartMeshFingerprint = ApaMeshFingerprint.OfSnapshot(snapshot);

                    // Empty declarations are the legacy default. Existing explicit declarations and material
                    // policies are author choices, so they are preserved rather than silently replaced.
                    if (snapshot != null && draft.UvSemantics.Length == 0) draft.InferUvSemanticsFrom(snapshot);
                    if (snapshot != null && draft.MaterialSemantics.Length == 0)
                    {
                        draft.InferMaterialSemanticsFrom(
                            partMesh.subMeshCount,
                            partRenderer != null ? partRenderer.sharedMaterials : null);
                    }
                }
            }
            else if (partRenderer != null
                     && ApaProtectedPartGeometry.TryDecode(
                         partRenderer,
                         partRoot,
                         out var protectedData,
                         out _,
                         out protectedIssue))
            {
                var snapshot = protectedData.CreateSnapshot();
                draft.PartMeshFingerprint = ApaMeshFingerprint.OfSnapshot(snapshot);
                if (draft.UvSemantics.Length == 0) draft.InferUvSemanticsFrom(snapshot);
                if (draft.MaterialSemantics.Length == 0)
                {
                    draft.InferMaterialSemanticsFrom(
                        protectedData.SubMeshCount,
                        partRenderer.sharedMaterials);
                }
            }
            else if (protectedIssue != null)
            {
                rebuilt.Add(protectedIssue);
            }

            if (rebuilt.Exists(issue => issue != null && issue.IsBlocking))
            {
                _validation = ValidationResult.Build(rebuilt);
                _health = ApaInstallerHealth.Problem;
                _status = TrFormat(
                    "Profile rebuild was blocked: {0}. Nothing was written.",
                    ApaDiagnosticText.Summarize(_validation));
                return;
            }

            // ---- Prove: the rebuilt draft must be assemblable before the author is asked about a write --------
            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = avatarRoot.gameObject,
                TargetRenderer = target,
                PartRoot = partRoot,
                PartRenderer = partRenderer,
                TargetArmature = targetArmature,
                PartArmature = partArmature
            };
            var dryRun = ApaAuthoringValidation.DryRun(selection, draft);
            if (!dryRun.Succeeded)
            {
                _validation = dryRun.Validation;
                _health = ApaInstallerHealth.Problem;
                _status = TrFormat(
                    "Profile rebuild was blocked: {0}. Nothing was written.",
                    ApaDiagnosticText.Summarize(dryRun.Validation));
                return;
            }

            // ---- Confirm, then write --------------------------------------------------------------------------
            if (!EditorUtility.DisplayDialog(
                    Tr("Rebuild Profile Configuration?"),
                    TrFormat(
                        "This refreshes the existing profile at\n\n{0}\n\nusing the current target body and part " +
                        "hierarchy. Its GUID and authored removal, seam, and material policies are kept. Continue?",
                        profilePath),
                    Tr("Rebuild"),
                    Tr("Cancel")))
            {
                _status = Tr("Profile rebuild cancelled. Nothing was written.");
                return;
            }

            var guidBefore = AssetDatabase.AssetPathToGUID(profilePath);
            var result = ApaProfileWriter.Save(draft, profilePath, true);
            _validation = result.Issues;
            _health = result.Succeeded && !result.Issues.HasErrors
                ? ApaInstallerHealth.Ready
                : ApaInstallerHealth.Problem;
            _healthDetail = string.Empty;
            _validatedSignature = string.Empty;

            if (!result.Succeeded)
            {
                _status = result.Message;
                return;
            }

            AssetDatabase.ImportAsset(profilePath);
            serializedObject.Update();
            Repaint();

            // The in-place update is the whole reason a rebuild is safe for the prefabs that reference the
            // profile, so it is verified instead of assumed: a GUID that changed means the writer replaced the
            // asset rather than updating it, and every reference to the old asset is now dangling.
            var guidAfter = AssetDatabase.AssetPathToGUID(profilePath);
            if (!string.Equals(guidBefore, guidAfter, StringComparison.Ordinal))
            {
                _status = TrFormat(
                    "The profile at '{0}' was rewritten, but its asset GUID changed ({1} to {2}), so prefabs " +
                    "that referenced it no longer do. Undo this change and re-author the profile instead of " +
                    "rebuilding it.",
                    profilePath, guidBefore, guidAfter);
                return;
            }

            _status = TrFormat(
                "Rebuilt '{0}': target signature, part mesh fingerprint, and armature selections recaptured; " +
                "stable part id and authored removal, seam, and material policies kept.",
                profilePath);
        }

        /// <summary>
        /// Creates a profile asset from the installer's own part root and explicit target, at a path the author
        /// chooses.
        /// </summary>
        /// <remarks>
        /// This is a shortcut, not a second authoring path: it captures the signature and infers the semantics
        /// exactly the way the window does, writes through the same guarded writer, and leaves removal and seam
        /// to the full workflow. It requires an explicit target assignment because the profile's signature can
        /// only be captured from a renderer the author has named.
        /// </remarks>
        private void CreateProfileAsset(AvatarPartInstaller installer)
        {
            var avatarRoot = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
            var partRoot = installer.ResolvePartRoot();
            var target = ResolveTargetRenderer(installer);

            if (avatarRoot == null || partRoot == null || target == null)
            {
                _status = Tr("Creating a profile needs an avatar root, a part root, and an explicit target body " +
                             "renderer on this installer. Assign the target renderer object, or use Part Authoring " +
                             "to pick the target body.");
                return;
            }

            var path = EditorUtility.SaveFilePanelInProject(
                Tr("Create Avatar Part Profile"),
                ApaAuthoringAssetPaths.SanitizeFileName(partRoot.name) + "Profile",
                "asset",
                Tr("Choose where the profile is written"));

            if (string.IsNullOrEmpty(path)) return;

            var draft = ApaProfileDraft.New(partRoot.name, ApaPartSlot.Custom);

            // The two armature selections are proposed from the live hierarchy and written into the draft, because
            // this shortcut exists to produce a starting point and a profile with no selection cannot build
            // (APA043). The proposal is the same one the window's button offers, it is stored as data, and the
            // author reviews it in the window before the geometry is authored.
            ApaAuthoringSelection.ProposeArmatures(
                avatarRoot.transform,
                target,
                partRoot,
                out var targetArmature,
                out var partArmature);
            draft.SetArmatures(avatarRoot.transform, targetArmature, partRoot.transform, partArmature);
            draft.Compatibility = ApaCompatibilityCapture.Capture(
                avatarRoot.gameObject, target, out var captureIssues, targetArmature);

            var partRenderer = partRoot.GetComponentInChildren<Renderer>(true);
            var partMesh = ApaCompatibilityCapture.ResolveMesh(partRenderer);
            if (partMesh != null && partMesh.isReadable)
            {
                var snapshot = MeshSnapshotFactory.Capture(partMesh, null, out _);
                draft.InferUvSemanticsFrom(snapshot);
                draft.InferMaterialSemanticsFrom(partMesh.subMeshCount, partRenderer != null ? partRenderer.sharedMaterials : null);
            }
            else if (partRenderer != null
                     && ApaProtectedPartGeometry.TryDecode(
                         partRenderer, partRoot.gameObject, out var protectedData, out _, out _))
            {
                // A protected part's renderer carries no mesh; its payload is the geometry, and it is the same
                // geometry the window's `Infer From Part Mesh` actions read. Decoding through the shared helper
                // keeps this shortcut's proposal identical to what the author would get in the window.
                draft.InferUvSemanticsFrom(protectedData.CreateSnapshot());
                draft.InferMaterialSemanticsFrom(protectedData.SubMeshCount, partRenderer.sharedMaterials);
            }

            // This shortcut writes a profile, so it owes the same proof the window owes: a draft that validates
            // but cannot be planned would produce a profile that can never build (the material-coverage verdict
            // is the planner's, not the rule set's). The plan runs against the same selection the window would
            // have been given, armature selections included.
            var selection = new ApaAuthoringSelection
            {
                AvatarRoot = avatarRoot.gameObject,
                TargetRenderer = target,
                PartRoot = partRoot,
                PartRenderer = partRoot.GetComponentInChildren<Renderer>(true),
                TargetArmature = targetArmature,
                PartArmature = partArmature
            };

            var plan = ApaAuthoringValidation.DryRun(selection, draft);
            _validation = plan.Validation;
            if (!plan.Succeeded)
            {
                _status = TrFormat(
                    "Creating the profile was blocked: the assembly cannot be planned. {0}. Nothing was written.",
                    ApaDiagnosticText.Summarize(plan.Validation));
                return;
            }

            var result = ApaProfileWriter.Save(draft, path, false);
            _validation = result.Issues;
            _status = result.Message;

            if (captureIssues.Count > 0)
            {
                var merged = new List<ValidationIssue>(result.Issues.Issues);
                merged.AddRange(captureIssues);
                _validation = ValidationResult.Build(merged);
            }

            if (!result.Succeeded) return;

            Undo.RecordObject(installer, Tr("Assign Avatar Part Profile"));
            installer.Profile = result.Asset;
            EditorUtility.SetDirty(installer);
            serializedObject.Update();
        }

        private static SkinnedMeshRenderer ResolveTargetRenderer(AvatarPartInstaller installer)
        {
            if (installer == null) return null;

            if (installer.TargetRendererObject != null)
            {
                var explicitTarget = installer.TargetRendererObject.GetComponent<SkinnedMeshRenderer>();
                if (explicitTarget != null) return explicitTarget;
            }

            var profile = installer.Profile;
            var recorded = profile != null ? profile.CompatibilityOrNull : null;
            var path = recorded != null ? recorded.RendererPath : null;
            if (!ApaAvatarPath.HasIdentity(path)) return null;

            var root = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
            if (root == null) return null;

            var found = ApaAvatarPath.IsRoot(path) ? root : root.Find(path);
            return found != null ? found.GetComponent<SkinnedMeshRenderer>() : null;
        }
    }
}

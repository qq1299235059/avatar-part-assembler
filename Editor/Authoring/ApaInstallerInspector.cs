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
    /// workflow.
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

            // An empty signature can never match a real one, so the first paint validates once. The verdict is
            // therefore real from the moment the Inspector opens, without a validation per repaint.
            _validatedSignature = string.Empty;
        }

        /// <inheritdoc />
        public override void OnInspectorGUI()
        {
            var installer = target as AvatarPartInstaller;
            if (installer == null) return;

            serializedObject.Update();
            DrawFields();
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            LanguagePopup(Content(
                "Language",
                "The language of the Avatar Part Assembler user interface. Stored per user, not in the project."));

            if (targets.Length > 1)
            {
                EditorGUILayout.HelpBox(
                    TrFormat(
                        "{0} installers are selected. Status and shortcuts apply to '{1}'.",
                        targets.Length, installer.gameObject.name), MessageType.Info);
            }

            DrawHealth(installer);
            DrawShortcuts(installer);
            DrawBoneFit(installer);
            DrawDiagnostics();
        }

        private void DrawFields()
        {
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
            EditorGUILayout.LabelField(Tr("Status"), EditorStyles.boldLabel);

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
                    Tr("This installer is disabled for build, so it contributes nothing and validation is skipped."),
                    MessageType.Info);
                return;
            }

            if (ApaAssetDatabaseUtility.IsPersistent(installer))
            {
                ClearValidationCache();
                // A prefab asset has no scene hierarchy to validate against, and validating it in place would
                // report defects that do not exist. The shortcut section repeats this in more detail.
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
        /// <see cref="EditorGUI.DrawRect"/> is a no-op for every other event.
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

        /// <summary>Discards a verdict that cannot describe the installer's current non-scene state.</summary>
        private void ClearValidationCache()
        {
            _validation = null;
            _health = ApaInstallerHealth.NotValidated;
            _healthDetail = string.Empty;
            _validatedSignature = string.Empty;
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
                (partRoot != null ? partRoot.GetInstanceID() : 0).ToString(CultureInfo.InvariantCulture),
                ":",
                (target != null ? target.GetInstanceID() : 0).ToString(CultureInfo.InvariantCulture),
                ":",
                installer.EnabledForBuild ? "1" : "0");
        }

        private void DrawShortcuts(AvatarPartInstaller installer)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Shortcuts"), EditorStyles.boldLabel);

            // A prefab asset has no scene hierarchy: its part root resolves to an object inside the prefab and
            // "the avatar root" resolves to the prefab root, which is not an avatar. Opening the authoring window
            // in that state, or capturing a signature from it, would present a hierarchy that does not exist in
            // any scene — so the two shortcuts that do that are offered only for a scene installer, with the same
            // test that already guards Validate.
            var isPrefabAsset = ApaAssetDatabaseUtility.IsPersistent(installer);

            EditorGUILayout.BeginHorizontal();

            if (installer.Profile == null)
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
                    Selection.activeObject = installer.Profile;
                    EditorGUIUtility.PingObject(installer.Profile);
                }
            }

            EditorGUI.BeginDisabledGroup(isPrefabAsset);
            if (GUILayout.Button(Tr("Edit In Part Authoring")))
            {
                var root = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
                ApaAuthoringWindow.OpenWith(
                    installer.Profile,
                    installer.ResolvePartRoot(),
                    root != null ? root.gameObject : null,
                    ResolveTargetRenderer(installer));
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

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

            if (isPrefabAsset)
            {
                // A prefab asset has no scene transform, so its renderer matrices live in a different space than
                // the avatar's. Validating geometry in that state would report seam defects that do not exist,
                // and authoring from it would capture a signature for a hierarchy that is not in a scene, so
                // both are offered only where they are meaningful.
                EditorGUILayout.HelpBox(
                    Tr("This installer belongs to a prefab asset. Place the prefab under an avatar in a scene and " +
                       "select it there to validate geometry or to edit it in Part Authoring, so the part and the " +
                       "body share a space."),
                    MessageType.Info);
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, MessageType.None);
            }
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
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Bone Fit"), EditorStyles.boldLabel);

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

        private void DrawDiagnostics()
        {
            if (_validation == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Validation"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(ApaDiagnosticText.Summarize(_validation));

            for (var i = 0; i < _validation.Issues.Count; i++)
            {
                var issue = _validation.Issues[i];
                var previous = GUI.color;
                GUI.color = ApaDiagnosticText.ColorOf(issue.Severity);
                EditorGUILayout.LabelField(ApaDiagnosticText.Format(issue), EditorStyles.wordWrappedMiniLabel);
                GUI.color = previous;
            }
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

            var partMesh = ApaCompatibilityCapture.ResolveMesh(partRoot.GetComponentInChildren<Renderer>(true));
            if (partMesh != null && partMesh.isReadable)
            {
                var snapshot = MeshSnapshotFactory.Capture(partMesh, null, out _);
                draft.InferUvSemanticsFrom(snapshot);
                var renderer = partRoot.GetComponentInChildren<Renderer>(true);
                draft.InferMaterialSemanticsFrom(partMesh.subMeshCount, renderer != null ? renderer.sharedMaterials : null);
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

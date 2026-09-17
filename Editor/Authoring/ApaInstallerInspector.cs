using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The Avatar Part Installer inspector: what this part is, whether it currently validates, and the shortcuts
    /// that open the authoring workflow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The end-user experience the product promises is "drag the prefab in and it works", so the inspector leads
    /// with status rather than with fields: the profile, the resolved target body, the captured signature, and a
    /// one-click validation. Every diagnostic it prints begins with its stable <c>APA</c> code, because the code
    /// is what a user searches for and what a bug report has to carry.
    /// </para>
    /// <para>
    /// Nothing here mutates an asset or a mesh: the validate action builds a context and runs the same core the
    /// build uses, and reports what it returned. The status reads go through the profile's non-mutating
    /// <c>*OrNull</c> accessors for the same reason — a repaint must never materialize a nested object onto the
    /// shared profile asset, so a profile that genuinely lacks one shows "none" instead of being written to.
    /// </para>
    /// </remarks>
    [CustomEditor(typeof(AvatarPartInstaller))]
    [CanEditMultipleObjects]
    public sealed class ApaInstallerInspector : UnityEditor.Editor
    {
        private SerializedProperty _profileProperty;
        private SerializedProperty _partRootProperty;
        private SerializedProperty _targetRendererProperty;
        private SerializedProperty _enabledForBuildProperty;

        [NonSerialized] private ValidationResult _validation;
        [NonSerialized] private string _status = string.Empty;

        private void OnEnable()
        {
            _profileProperty = serializedObject.FindProperty("_profile");
            _partRootProperty = serializedObject.FindProperty("_partRoot");
            _targetRendererProperty = serializedObject.FindProperty("_targetRendererObject");
            _enabledForBuildProperty = serializedObject.FindProperty("_enabledForBuild");
            _validation = null;
            _status = string.Empty;
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

            DrawStatus(installer);
            DrawShortcuts(installer);
            DrawDiagnostics();
        }

        private void DrawFields()
        {
            if (_profileProperty == null || _partRootProperty == null
                || _targetRendererProperty == null || _enabledForBuildProperty == null)
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

        private void DrawStatus(AvatarPartInstaller installer)
        {
            EditorGUILayout.LabelField(Tr("Status"), EditorStyles.boldLabel);

            var profile = installer.Profile;
            if (profile == null)
            {
                EditorGUILayout.HelpBox(
                    Tr("No profile is assigned, so this part cannot be installed. Create a profile or assign an " +
                       "existing one."), MessageType.Warning);
                return;
            }

            // Status is a read: the profile is a shared authoring asset, so a repaint must not materialize its
            // missing nested objects (an invisible, non-undoable write that an unrelated SaveAssets could
            // persist). The *OrNull accessors return null for an object the asset never carried, and every read
            // below has a "not configured yet" answer for that case. Nothing in this inspector writes an asset.
            var identity = profile.IdentityOrNull;
            var bones = profile.BonesOrNull;
            var captured = profile.CompatibilityOrNull;
            var removal = profile.RemovalOrNull;
            var seam = profile.SeamOrNull;

            EditorGUILayout.LabelField(Tr("Part"), identity == null || string.IsNullOrEmpty(identity.DisplayName)
                ? Tr("(unnamed)")
                : identity.DisplayName);
            EditorGUILayout.LabelField(
                Tr("Slot"), identity != null ? DisplayName(identity.Slot) : Tr("(none)"));
            DrawPartIdStatus(profile);

            // The M10 bone policy the build actually reads. The inspector shows it rather than editing it: the
            // authoring window is the editing surface for these fields, and "Edit In Part Authoring" below opens
            // it on this part. Showing them here is what keeps a part's behaviour explainable from the component
            // that carries it — a shared slot, a priority that decided a removal overlap, and the two armature
            // selections the bone identity is scoped to are all decisions the author made in the window.
            EditorGUILayout.LabelField(
                Tr("Slot Mode"),
                identity != null ? DisplayName(identity.SlotMode) : DisplayName(ApaPartSlotMode.Replace));
            EditorGUILayout.LabelField(Tr("Conflict Priority"), identity != null && identity.HasConflictPriority
                ? TrFormat("{0} (resolves removal overlaps)", identity.ConflictPriority)
                : Tr("none (0)"));
            EditorGUILayout.LabelField(
                Tr("Armatures"), bones != null ? bones.DescribeArmatures() : Tr("not selected"));

            // The version prefix is a technical token, not prose: it is built here rather than looked up so the
            // table cannot acquire an entry whose translation is itself.
            EditorGUILayout.LabelField(Tr("Schema"), "v" + profile.SchemaVersion +
                                                      (profile.IsSchemaSupported
                                                          ? string.Empty
                                                          : Tr(" (newer than this build)")));

            EditorGUILayout.LabelField(Tr("Signature"), captured != null && captured.IsCaptured
                ? (captured.HasCompleteSafetyData
                    ? Tr("captured and complete")
                    : Tr("captured but incomplete (APA024)"))
                : Tr("not captured (APA012)"));
            EditorGUILayout.LabelField(Tr("Target Path"),
                captured != null && !string.IsNullOrEmpty(captured.RendererPath)
                    ? captured.RendererPath
                    : Tr("(none)"));

            EditorGUILayout.LabelField(Tr("Removal"), removal != null
                ? TrFormat("{0} triangle(s)", removal.Count)
                : Tr("none"));
            EditorGUILayout.LabelField(Tr("Seam"), DescribeSeam(seam));
            EditorGUILayout.LabelField(Tr("UV Semantics"), profile.UvSemantics.Length.ToString());
            EditorGUILayout.LabelField(Tr("Material Semantics"), profile.MaterialSemantics.Length.ToString());

            EditorGUILayout.LabelField(Tr("Resolved Target"), DescribeResolvedTarget(installer));

            if (!installer.EnabledForBuild)
            {
                EditorGUILayout.HelpBox(
                    Tr("This installer is disabled for build, so it contributes nothing and validation is skipped."),
                    MessageType.Info);
            }
        }

        /// <summary>
        /// The part-id line: the stored id, the derived fallback with a one-click repair, or the reason neither
        /// exists.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A profile written before the identity field existed carries no id. The build no longer refuses it: a
        /// stable id is derived from the profile asset's GUID, so the part installs and the identity is the same
        /// in preview, validation, sorting, and the build. What the author still needs is a way to make that
        /// identity explicit on the asset, and this is it.
        /// </para>
        /// <para>
        /// The repair writes the <i>derived</i> id, not a new random one, so nothing about the part changes
        /// except that the id is now stored. The write goes through
        /// <see cref="ApaPartIdentityResolver.TryRepair"/>, which records undo and saves the asset; a repaint
        /// never writes anything.
        /// </para>
        /// </remarks>
        private void DrawPartIdStatus(ApaPartProfile profile)
        {
            var partId = ApaPartIdentityResolver.ResolvePartId(profile);

            EditorGUILayout.LabelField(Tr("Part Id"), string.IsNullOrEmpty(partId) ? Tr("(none)") : partId);

            if (ApaPartIdentityResolver.HasStoredPartId(profile)) return;

            if (string.IsNullOrEmpty(partId))
            {
                EditorGUILayout.HelpBox(
                    Tr("This profile carries no part id and is not a project asset, so no stable identity can be " +
                       "derived for it. Save it as an asset inside the project, then repair it."),
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.HelpBox(
                TrFormat(
                    "This profile carries no stored part id, so a stable one is derived from the profile asset's " +
                    "GUID: {0}. The part installs normally and the identity does not change between preview, " +
                    "validation, sorting, and the build. Press Repair Part Id to store it on the asset.",
                    partId),
                MessageType.Info);

            if (GUILayout.Button(Tr("Repair Part Id")))
            {
                RepairPartId(profile);
            }
        }

        /// <summary>Stores the derived part id on the profile asset, under undo.</summary>
        private void RepairPartId(ApaPartProfile profile)
        {
            if (!ApaPartIdentityResolver.TryRepair(profile, out var partId, out var reason))
            {
                _status = TrFormat("The part id could not be repaired ({0}). Nothing was written.", reason);
                Repaint();
                return;
            }

            _status = TrFormat("Stored part id '{0}' on the profile.", partId);
            Repaint();
        }

        /// <summary>
        /// The seam line of the status block: the pair count, the legacy state, or "none".
        /// </summary>
        /// <remarks>
        /// A seam is read as <i>pairs</i> since M10, so two list lengths are no longer the meaningful summary:
        /// the profile's pairing version says whether the two lists are pairs at all, and a legacy seam is
        /// reported as the refusal it will produce rather than as a count that looks valid.
        /// </remarks>
        private static string DescribeSeam(ApaSeamProfile seam)
        {
            if (seam == null || seam.IsEmpty) return Tr("none");
            if (!seam.HasExplicitPairing) return Tr("unpaired legacy seam (APA042)");

            var pairs = Mathf.Min(seam.Base.Count, seam.Part.Count);
            return TrFormat("{0} seam pair(s)", pairs);
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
                RunValidation(installer);
            }

            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button(Tr("Clear Results")))
            {
                _validation = null;
                _status = string.Empty;
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
        /// Validates the whole avatar the installer sits under, using the same core entry point a build uses.
        /// </summary>
        private void RunValidation(AvatarPartInstaller installer)
        {
            var anchor = ApaAuthoringSelection.FindAvatarRoot(installer.transform);
            if (anchor == null)
            {
                _status = Tr("No avatar root could be resolved from this installer.");
                _validation = null;
                return;
            }

            var context = ContextBuilder.Build(anchor.gameObject, null, out var discoveryIssues);
            if (context == null)
            {
                _validation = ValidationResult.Build(discoveryIssues);
                _status = TrFormat(
                    "Validation could not build a context: {0}.", ApaDiagnosticText.Summarize(_validation));
                return;
            }

            var result = ApaCore.Validate(context);
            var merged = new List<ValidationIssue>(discoveryIssues);
            merged.AddRange(result.Issues);
            _validation = ValidationResult.Build(merged);

            _status = _validation.IsValid
                ? TrFormat("Validation passed: {0}.", ApaDiagnosticText.Summarize(_validation))
                : TrFormat("Validation failed: {0}.", ApaDiagnosticText.Summarize(_validation));
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

        private static string DescribeResolvedTarget(AvatarPartInstaller installer)
        {
            var target = ResolveTargetRenderer(installer);
            if (target != null) return target.name;

            var profile = installer.Profile;
            var recorded = profile != null ? profile.CompatibilityOrNull : null;
            var path = recorded != null ? recorded.RendererPath : null;
            if (!ApaAvatarPath.HasIdentity(path)) return Tr("(no recorded path)");

            return TrFormat("(not found: {0})", path);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>What a prefab generation did, or why it did nothing.</summary>
    public enum ApaPrefabStatus
    {
        /// <summary>A new prefab asset was created from the scene part root.</summary>
        Created = 0,

        /// <summary>An existing prefab's installer was updated in place.</summary>
        Updated = 1,

        /// <summary>A prefab already exists and overwriting it was not allowed. Nothing was written.</summary>
        RefusedOverwrite = 2,

        /// <summary>The output path cannot name a Unity asset. Nothing was written.</summary>
        RefusedInvalidPath = 3,

        /// <summary>The selection is not usable as a prefab source. Nothing was written.</summary>
        RefusedInvalidSelection = 4,

        /// <summary>The profile is not a project asset, so the prefab could not reference it. Nothing was written.</summary>
        RefusedProfileNotPersistent = 5,

        /// <summary>
        /// The prefab could not be proven portable: it contains, or may contain, a reference that cannot survive
        /// outside the authoring scene.
        /// </summary>
        RefusedNonPortableReference = 6,

        /// <summary>An unexpected failure occurred.</summary>
        Failed = 7,

        /// <summary>
        /// The path is already occupied by an asset of a different type, so writing there would replace
        /// somebody else's work. Nothing was written.
        /// </summary>
        RefusedOccupiedPath = 8
    }

    /// <summary>The outcome of a prefab generation.</summary>
    public sealed class ApaPrefabResult
    {
        /// <summary>What happened.</summary>
        public ApaPrefabStatus Status { get; }

        /// <summary>The normalized prefab path.</summary>
        public string Path { get; }

        /// <summary>The prefab asset, when the operation succeeded.</summary>
        public GameObject Prefab { get; }

        /// <summary>Code-carrying diagnostics for a refusal or a note, in deterministic order.</summary>
        public ValidationResult Issues { get; }

        /// <summary>A human-readable explanation.</summary>
        public string Message { get; }

        /// <summary>True when a prefab was written.</summary>
        public bool Succeeded => Status == ApaPrefabStatus.Created || Status == ApaPrefabStatus.Updated;

        /// <summary>True when the operation stopped because the file exists.</summary>
        public bool RequiresOverwriteConfirmation => Status == ApaPrefabStatus.RefusedOverwrite;

        /// <summary>Creates a result.</summary>
        public ApaPrefabResult(
            ApaPrefabStatus status,
            string path,
            GameObject prefab,
            ValidationResult issues,
            string message)
        {
            Status = status;
            Path = path ?? string.Empty;
            Prefab = prefab;
            Issues = issues ?? ValidationResult.Empty;
            Message = message ?? string.Empty;
        }
    }

    /// <summary>The result of scanning a hierarchy for references that cannot be stored in a prefab.</summary>
    public sealed class ApaReferenceScanResult
    {
        /// <summary>One blocking issue per non-portable reference.</summary>
        public List<ValidationIssue> Issues { get; }

        /// <summary>Number of serialized properties inspected.</summary>
        public int ScannedProperties { get; }

        /// <summary>True when the scan stopped at its property budget and therefore proves nothing.</summary>
        public bool Truncated { get; }

        /// <summary>Creates a scan result.</summary>
        public ApaReferenceScanResult(List<ValidationIssue> issues, int scannedProperties, bool truncated)
        {
            Issues = issues ?? new List<ValidationIssue>();
            ScannedProperties = scannedProperties;
            Truncated = truncated;
        }
    }

    /// <summary>
    /// Creates or updates the part prefab that carries an <see cref="AvatarPartInstaller"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Portability is the whole point of this type.</b> A part prefab is downloaded, dragged under an avatar
    /// in a different scene, and expected to work. Anything it stores must therefore survive outside the
    /// authoring scene:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The installer's <c>Profile</c> must be a project asset, and the prefab is refused when it is not.
    /// </description></item>
    /// <item><description>
    /// The installer's <c>PartRoot</c> is a reference <i>inside</i> the prefab, which is portable by
    /// construction.
    /// </description></item>
    /// <item><description>
    /// The installer's <c>TargetRendererObject</c> is <b>never</b> assigned to the authoring scene's body
    /// renderer. The target travels as the profile's captured renderer path instead, which is exactly what the
    /// compatibility signature is for. An assignment that pointed outside the prefab is cleared, with a note.
    /// </description></item>
    /// <item><description>
    /// Every reference in the saved prefab is scanned, and the operation is refused when one of them cannot be
    /// stored in an asset — or when the scan runs out of its property budget before it can prove that there are
    /// none. A path occupied by an asset that is not a prefab is refused as well, so a write can never replace
    /// an unrelated asset.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Nothing is overwritten silently.</b> <see cref="CreateFromScene"/> refuses an existing path unless the
    /// caller allows it, and <see cref="UpdateInstallerOnPrefab"/> only rewrites the installer's own fields,
    /// never the prefab's geometry, so prefab-side edits are not lost.
    /// </para>
    /// <para>
    /// <b>An existing prefab is proven portable before it is touched.</b> When the caller has allowed the
    /// replacement, the portability scan runs twice: once on the scene hierarchy before the first save, so that
    /// a refusal leaves the existing file exactly as it was, and once on the loaded prefab afterwards, which is
    /// the scan that proves what is actually stored. A failure that only the second scan can see — the prefab
    /// failing to load — is reported as "the file was replaced but the installer could not be configured"
    /// rather than as "nothing was written", because the author has to know which of the two happened.
    /// </para>
    /// </remarks>
    public static class ApaPrefabGenerator
    {
        /// <summary>Serialized field of <see cref="AvatarPartInstaller"/> holding the profile.</summary>
        private const string ProfileFieldPath = "_profile";

        /// <summary>Serialized field of <see cref="AvatarPartInstaller"/> holding the part root.</summary>
        private const string PartRootFieldPath = "_partRoot";

        /// <summary>Serialized field of <see cref="AvatarPartInstaller"/> holding the explicit target body.</summary>
        private const string TargetRendererFieldPath = "_targetRendererObject";

        /// <summary>
        /// Builds a brand-new prefab from the scene part root and installs the part component on it.
        /// </summary>
        /// <remarks>
        /// The saved prefab contains the part root's current scene state. The scene object itself is only read:
        /// adding the installer happens on a loaded copy of the prefab, so the author's scene hierarchy is not
        /// modified by generating a prefab.
        /// </remarks>
        public static ApaPrefabResult CreateFromScene(
            ApaAuthoringSelection selection,
            ApaPartProfile profileAsset,
            string prefabPath,
            bool allowOverwrite)
        {
            if (selection == null || selection.PartRoot == null)
            {
                var issue = ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Configuration,
                    "No part root is selected.",
                    detail: "reason=missing-part-root");
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedInvalidSelection,
                    prefabPath,
                    null,
                    ValidationResult.Single(issue),
                    ApaDiagnosticText.Format(issue));
            }

            if (!ApaAuthoringAssetPaths.TryValidateAssetPath(
                    prefabPath,
                    ApaAuthoringAssetPaths.PrefabExtension,
                    out var normalized,
                    out var reason))
            {
                var issue = ApaAuthoringAssetPaths.InvalidPathIssue(
                    prefabPath, ApaAuthoringAssetPaths.PrefabExtension, reason, "prefab");
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedInvalidPath,
                    normalized,
                    null,
                    ValidationResult.Single(issue),
                    ApaDiagnosticText.Format(issue));
            }

            if (!ApaAssetDatabaseUtility.IsPersistent(profileAsset))
            {
                var issue = ValidationIssue.Error(
                    ApaAuthoringErrorCode.NonPersistentReference,
                    ApaIssuePhase.Configuration,
                    "The profile is not a project asset, so the prefab cannot reference it. Save the profile " +
                    "first; a prefab that referenced an unsaved profile would install nothing on another machine.",
                    detail: "reason=profile-not-an-asset");
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedProfileNotPersistent,
                    normalized,
                    null,
                    ValidationResult.Single(issue),
                    ApaDiagnosticText.Format(issue));
            }

            var existing = ApaAssetDatabaseUtility.Load<GameObject>(normalized);

            // The typed load answers "is there a prefab here", not "is the path in use". SaveAsPrefabAsset
            // replaces whatever occupies the path, so a create decided from a null typed load would silently
            // destroy an unrelated asset. Any occupant that is not a GameObject (a prefab) is a refusal, whatever
            // allowOverwrite says: permission to replace a prefab is not permission to delete a material.
            var occupant = ApaAssetDatabaseUtility.LoadMainAsset(normalized);
            if (occupant != null && !(occupant is GameObject))
            {
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedOccupiedPath,
                    normalized,
                    null,
                    ValidationResult.Empty,
                    TrFormat(
                        "The path '{0}' is occupied by '{1}' ({2}). Nothing was written. Choose a path that is not " +
                        "already in use.",
                        normalized, occupant.name, occupant.GetType().Name));
            }

            var action = ApaAuthoringAssetPaths.DecideWriteAction(existing != null, allowOverwrite);
            if (action == ApaAssetWriteAction.RefuseOverwrite)
            {
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedOverwrite,
                    normalized,
                    existing,
                    ValidationResult.Empty,
                    TrFormat(
                        "A prefab already exists at '{0}'. Nothing was written. Confirm the replacement " +
                        "explicitly to overwrite it, or use 'Update installer on prefab' to keep the existing " +
                        "content.", normalized));
            }

            var createdFile = action == ApaAssetWriteAction.Create;
            var notes = new List<string>();

            if (!createdFile)
            {
                // Overwriting an existing prefab starts by saving the scene part over that file, so a portability
                // refusal discovered after the save would already have destroyed the prefab the author had. The
                // pre-flight scan therefore runs before that save, on the scene hierarchy. It skips the installer
                // fields ConfigureInstaller is about to rewrite, because those are the only references whose
                // portability the save changes; the scan that runs afterwards, on the loaded prefab, is still the
                // authority and still runs without that exclusion.
                var preflight = FindUnserializableReferences(selection.PartRoot, skipReconciledInstallerFields: true);
                if (preflight.Issues.Count > 0 || preflight.Truncated)
                {
                    var refusal = preflight.Truncated
                        ? TrFormat(
                              "Nothing was written and the existing prefab at '{0}' is untouched: the reference " +
                              "scan stopped at its property budget after {1} properties, so the scene part cannot " +
                              "be proven portable.", normalized, preflight.ScannedProperties) +
                          (preflight.Issues.Count > 0
                              ? TrFormat(
                                  " It had already found {0} non-portable reference(s).", preflight.Issues.Count)
                              : string.Empty)
                        : TrFormat(
                            "Nothing was written and the existing prefab at '{0}' is untouched: the scene part " +
                            "contains {1} reference(s) that cannot be stored in an asset.",
                            normalized, preflight.Issues.Count);

                    return new ApaPrefabResult(
                        ApaPrefabStatus.RefusedNonPortableReference,
                        normalized,
                        existing,
                        ValidationResult.Build(preflight.Issues),
                        AppendNotes(refusal, notes));
                }
            }

            try
            {
                ApaAssetDatabaseUtility.EnsureFolder(normalized);

                if (PrefabUtility.IsPartOfPrefabInstance(selection.PartRoot))
                {
                    notes.Add(Tr(
                        "The scene part root is a prefab instance, so the new prefab was built from its " +
                        "current scene state rather than from the source prefab."));
                }

                // Step 1: copy the scene part root into a prefab asset. The scene object is not modified by this
                // call, and the copy is what everything below operates on.
                var saved = PrefabUtility.SaveAsPrefabAsset(selection.PartRoot, normalized, out var savedOk);
                if (!savedOk || saved == null)
                {
                    if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);
                    return new ApaPrefabResult(
                        ApaPrefabStatus.Failed,
                        normalized,
                        null,
                        ValidationResult.Empty,
                        TrFormat(
                            "Unity could not save '{0}' as a prefab at '{1}'.",
                            selection.PartRoot.name, normalized));
                }

                // Step 2: configure the installer on a loaded copy, then verify the copy is portable before it is
                // written back. Doing the check here rather than on the scene object means the check sees exactly
                // the content that will be stored.
                var contents = PrefabUtility.LoadPrefabContents(normalized);
                if (contents == null)
                {
                    if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);

                    // On the overwrite path the save above already replaced the file, so saying "nothing was
                    // written" would be false and would hide a prefab that is now present but unconfigured.
                    return new ApaPrefabResult(
                        ApaPrefabStatus.Failed,
                        normalized,
                        null,
                        ValidationResult.Empty,
                        createdFile
                            ? TrFormat("The prefab at '{0}' could not be loaded for configuration.", normalized)
                            : TrFormat(
                                "The existing prefab at '{0}' was replaced with the scene part, but the written " +
                                "prefab could not be loaded to configure its installer. The prefab is present but " +
                                "unconfigured; restore it from version control if that content mattered.",
                                normalized));
                }

                try
                {
                    ConfigureInstaller(contents, profileAsset, contents, notes);

                    var scan = FindUnserializableReferences(contents);
                    if (scan.Issues.Count > 0 || scan.Truncated)
                    {
                        // Roll back a prefab this call created: leaving behind a prefab without a configured
                        // installer would look like a successful generation to the author. An existing prefab
                        // cannot be rolled back, so that case is reported as it is rather than as "not written".
                        if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);

                        var portabilityReason = scan.Truncated
                            ? TrFormat(
                                "the reference scan stopped at its property budget after {0} properties, so the " +
                                "prefab cannot be proven portable", scan.ScannedProperties)
                            : TrFormat(
                                "it contains {0} reference(s) that cannot be stored in an asset", scan.Issues.Count);

                        var message = createdFile
                            ? TrFormat("The prefab was not written: {0}.", portabilityReason)
                            : TrFormat(
                                "The existing prefab at '{0}' was replaced with the scene part, but its installer " +
                                "could not be configured because {1}. The prefab is present but unconfigured; " +
                                "restore it from version control if that content mattered.",
                                normalized, portabilityReason);

                        return new ApaPrefabResult(
                            ApaPrefabStatus.RefusedNonPortableReference,
                            normalized,
                            null,
                            ValidationResult.Build(scan.Issues),
                            AppendNotes(message, notes));
                    }

                    PrefabUtility.SaveAsPrefabAsset(contents, normalized);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }

                var asset = ApaAssetDatabaseUtility.Load<GameObject>(normalized);
                return new ApaPrefabResult(
                    createdFile ? ApaPrefabStatus.Created : ApaPrefabStatus.Updated,
                    normalized,
                    asset,
                    ValidationResult.Empty,
                    AppendNotes(
                        (createdFile
                            ? TrFormat("Created prefab '{0}'.", normalized)
                            : TrFormat("Replaced prefab '{0}'.", normalized)),
                        notes));
            }
            catch (Exception e)
            {
                if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);
                return new ApaPrefabResult(
                    ApaPrefabStatus.Failed,
                    normalized,
                    null,
                    ValidationResult.Single(ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Configuration,
                        "Generating the prefab threw " + e.GetType().Name + ": " + e.Message,
                        detail: "exception=" + e.GetType().FullName)),
                    TrFormat("Generating the prefab failed: {0}", e.Message));
            }
        }

        /// <summary>
        /// Points an existing prefab's installer at a profile without touching the prefab's content.
        /// </summary>
        /// <remarks>
        /// This is the update path an author uses after re-authoring a profile: the geometry inside the prefab is
        /// left exactly as it is, and only the installer's profile, part root, and target assignment are
        /// reconciled. No scene object is read or written, so this cannot disturb the open scene.
        /// </remarks>
        public static ApaPrefabResult UpdateInstallerOnPrefab(string prefabPath, ApaPartProfile profileAsset)
        {
            if (!ApaAuthoringAssetPaths.TryValidateAssetPath(
                    prefabPath,
                    ApaAuthoringAssetPaths.PrefabExtension,
                    out var normalized,
                    out var reason))
            {
                var issue = ApaAuthoringAssetPaths.InvalidPathIssue(
                    prefabPath, ApaAuthoringAssetPaths.PrefabExtension, reason, "prefab");
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedInvalidPath,
                    normalized,
                    null,
                    ValidationResult.Single(issue),
                    ApaDiagnosticText.Format(issue));
            }

            if (!ApaAssetDatabaseUtility.IsPersistent(profileAsset))
            {
                var issue = ValidationIssue.Error(
                    ApaAuthoringErrorCode.NonPersistentReference,
                    ApaIssuePhase.Configuration,
                    "The profile is not a project asset, so the prefab cannot reference it. Save the profile first.",
                    detail: "reason=profile-not-an-asset");
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedProfileNotPersistent,
                    normalized,
                    null,
                    ValidationResult.Single(issue),
                    ApaDiagnosticText.Format(issue));
            }

            var existing = ApaAssetDatabaseUtility.Load<GameObject>(normalized);

            // The same "is the path in use" check the create path performs: a typed load returns null for a
            // material or a texture, and reporting "no prefab exists" for a path that holds somebody's texture
            // would misdescribe the situation (and would invite the author to overwrite it with a create).
            var occupant = ApaAssetDatabaseUtility.LoadMainAsset(normalized);
            if (occupant != null && !(occupant is GameObject))
            {
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedOccupiedPath,
                    normalized,
                    null,
                    ValidationResult.Empty,
                    TrFormat(
                        "The path '{0}' is occupied by '{1}' ({2}), not a prefab. Nothing was written. Choose a " +
                        "path that is not already in use.",
                        normalized, occupant.name, occupant.GetType().Name));
            }

            if (existing == null)
            {
                return new ApaPrefabResult(
                    ApaPrefabStatus.RefusedInvalidSelection,
                    normalized,
                    null,
                    ValidationResult.Empty,
                    TrFormat("No prefab exists at '{0}'. Create it first.", normalized));
            }

            var notes = new List<string>();

            try
            {
                var contents = PrefabUtility.LoadPrefabContents(normalized);
                if (contents == null)
                {
                    return new ApaPrefabResult(
                        ApaPrefabStatus.Failed,
                        normalized,
                        null,
                        ValidationResult.Empty,
                        TrFormat("The prefab at '{0}' could not be loaded for configuration.", normalized));
                }

                try
                {
                    ConfigureInstaller(contents, profileAsset, contents, notes);

                    var scan = FindUnserializableReferences(contents);
                    if (scan.Issues.Count > 0 || scan.Truncated)
                    {
                        // Nothing was saved, so the prefab on disk is untouched. The loaded copy is discarded.
                        // A budget truncation is reported in its own words: it is not "N bad references", it is
                        // "the scan could not establish that there are none".
                        var portabilityReason = scan.Truncated
                            ? TrFormat(
                                  "the reference scan stopped at its property budget after {0} properties, so the " +
                                  "loaded prefab cannot be proven portable", scan.ScannedProperties) +
                              (scan.Issues.Count > 0
                                  ? TrFormat(
                                      " (it had already found {0} non-portable reference(s))", scan.Issues.Count)
                                  : string.Empty)
                            : TrFormat(
                                "it contains {0} reference(s) that cannot be stored in an asset", scan.Issues.Count);

                        return new ApaPrefabResult(
                            ApaPrefabStatus.RefusedNonPortableReference,
                            normalized,
                            null,
                            ValidationResult.Build(scan.Issues),
                            TrFormat("The prefab was not updated: {0}.", portabilityReason));
                    }

                    PrefabUtility.SaveAsPrefabAsset(contents, normalized);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }

                return new ApaPrefabResult(
                    ApaPrefabStatus.Updated,
                    normalized,
                    ApaAssetDatabaseUtility.Load<GameObject>(normalized),
                    ValidationResult.Empty,
                    AppendNotes(TrFormat("Updated the installer on '{0}'.", normalized), notes));
            }
            catch (Exception e)
            {
                return new ApaPrefabResult(
                    ApaPrefabStatus.Failed,
                    normalized,
                    null,
                    ValidationResult.Single(ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Configuration,
                        "Updating the prefab threw " + e.GetType().Name + ": " + e.Message,
                        detail: "exception=" + e.GetType().FullName)),
                    TrFormat("Updating the prefab failed: {0}", e.Message));
            }
        }

        /// <summary>
        /// Finds every reference in a hierarchy that could not be stored in a prefab asset.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A reference is portable when it is a persisted project asset, or when it points at an object inside
        /// the subtree being saved — that object becomes part of the prefab, so the reference stays resolvable.
        /// Everything else is a scene object reference that a prefab cannot carry.
        /// </para>
        /// <para>
        /// <b>This is a guard, not a proof.</b> It walks every visible serialized property of every component —
        /// the whole property tree, with no depth limit, because a reference nested deeper than an arbitrary
        /// bound would be a hole the caller could not see — and stops at a property budget;
        /// <see cref="ApaReferenceScanResult.Truncated"/> says whether that budget was reached, and the generator
        /// refuses the write rather than claiming a clean result it did not establish. The budget is the only
        /// truncation this scan can produce.
        /// </para>
        /// </remarks>
        public static ApaReferenceScanResult FindUnserializableReferences(GameObject root)
        {
            return FindUnserializableReferences(root, false);
        }

        /// <summary>
        /// The scan behind <see cref="FindUnserializableReferences(GameObject)"/>, with an option to ignore the
        /// installer fields the generator rewrites.
        /// </summary>
        /// <param name="root">The hierarchy to scan.</param>
        /// <param name="skipReconciledInstallerFields">
        /// When true, the profile, part root, and explicit target fields of an <see cref="AvatarPartInstaller"/>
        /// are not reported. <see cref="ConfigureInstaller"/> replaces those three with portable values, so a
        /// reference they hold cannot reach the written asset. The option exists for the pre-flight scan that
        /// protects an existing prefab from being overwritten; the scan that verifies the stored asset never
        /// uses it.
        /// </param>
        private static ApaReferenceScanResult FindUnserializableReferences(
            GameObject root,
            bool skipReconciledInstallerFields)
        {
            var issues = new List<ValidationIssue>();
            if (root == null) return new ApaReferenceScanResult(issues, 0, false);

            const int propertyBudget = 200000;

            var scanned = 0;
            var components = root.GetComponentsInChildren<Component>(true);

            for (var c = 0; c < components.Length; c++)
            {
                var component = components[c];
                if (component == null) continue;

                SerializedObject serialized;
                try
                {
                    serialized = new SerializedObject(component);
                }
                catch (Exception)
                {
                    // A component the serializer cannot open cannot be inspected; it is skipped rather than
                    // reported, because "the serializer refused" is not evidence of a scene reference.
                    continue;
                }

                var iterator = serialized.GetIterator();
                var enterChildren = true;

                while (iterator.NextVisible(enterChildren))
                {
                    scanned++;
                    if (scanned > propertyBudget)
                    {
                        return new ApaReferenceScanResult(issues, scanned, true);
                    }

                    // The whole tree is walked, with no depth limit: a reference under a nested generic would
                    // otherwise be skipped silently, and a guard that reports success it has not established is
                    // worse than no guard. The property budget above is what bounds the work.
                    enterChildren = true;

                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (skipReconciledInstallerFields && IsReconciledInstallerField(component, iterator.propertyPath))
                    {
                        continue;
                    }

                    var value = iterator.objectReferenceValue;
                    if (value == null) continue;
                    if (IsPortableReference(value, root)) continue;

                    issues.Add(ValidationIssue.Error(
                        ApaAuthoringErrorCode.NonPersistentReference,
                        ApaIssuePhase.Configuration,
                        "Component '" + component.GetType().Name + "' on '" + component.gameObject.name +
                        "' references '" + value.name + "', which is a scene object outside the prefab. A prefab " +
                        "cannot store that reference, so the part would install with a missing link. Move the " +
                        "referenced object into the part hierarchy, or convert it to a project asset.",
                        detail: "component=" + component.GetType().Name +
                                "; gameObject=" + component.gameObject.name +
                                "; property=" + iterator.propertyPath +
                                "; reference=" + value.name +
                                "; reason=non-persistent-prefab-reference"));
                }
            }

            return new ApaReferenceScanResult(issues, scanned, false);
        }

        /// <summary>
        /// Adds or reconciles the installer on the prefab root.
        /// </summary>
        /// <param name="prefabRoot">The loaded prefab contents root.</param>
        /// <param name="profileAsset">The persistent profile the prefab must reference.</param>
        /// <param name="partRoot">The object the part geometry is read from, inside the prefab.</param>
        /// <param name="notes">
        /// Receives human-readable notes about what was reconciled. Notes are prose rather than
        /// <see cref="ValidationIssue"/> values on purpose: they describe a normalization this generator
        /// performed, and dressing them up as diagnostics with a code would give a code a meaning it does not
        /// have.
        /// </param>
        private static void ConfigureInstaller(
            GameObject prefabRoot,
            ApaPartProfile profileAsset,
            GameObject partRoot,
            List<string> notes)
        {
            var installer = prefabRoot.GetComponent<AvatarPartInstaller>();
            if (installer == null)
            {
                installer = prefabRoot.AddComponent<AvatarPartInstaller>();
                notes.Add(TrFormat("Added an AvatarPartInstaller to the prefab root '{0}'.", prefabRoot.name));
            }

            installer.Profile = profileAsset;

            var existingPartRoot = installer.PartRoot;
            if (existingPartRoot == null
                || !ApaAssetDatabaseUtility.IsInsideSubtree(existingPartRoot.transform, partRoot.transform))
            {
                installer.PartRoot = partRoot;
            }

            var target = installer.TargetRendererObject;
            if (target != null && !ApaAssetDatabaseUtility.IsInsideSubtree(target.transform, partRoot.transform))
            {
                // The target body belongs to the avatar, not to the part prefab. Recording it here would create
                // exactly the scene reference this workflow exists to avoid, and it would not resolve on another
                // machine anyway. The profile's captured renderer path is the portable form of the same fact.
                installer.TargetRendererObject = null;
                notes.Add(TrFormat(
                    "Cleared the installer's explicit target renderer assignment ('{0}'), which pointed outside " +
                    "the prefab. The target is resolved through the profile's captured renderer path instead, " +
                    "which is what makes the prefab portable.", target.name));
            }

            EditorUtility.SetDirty(installer);
        }

        /// <summary>
        /// True when a serialized field is one <see cref="ConfigureInstaller"/> overwrites on every generation.
        /// </summary>
        /// <remarks>
        /// The paths are the serialized field names of <see cref="AvatarPartInstaller"/>, which is a Runtime type
        /// this editor assembly may read. A rename there breaks this lookup, so the names are additionally
        /// covered by the installer inspector, which resolves the same three properties and reports an internal
        /// consistency error when one is missing.
        /// </remarks>
        private static bool IsReconciledInstallerField(Component component, string propertyPath)
        {
            if (!(component is AvatarPartInstaller)) return false;

            return string.Equals(propertyPath, ProfileFieldPath, StringComparison.Ordinal)
                   || string.Equals(propertyPath, PartRootFieldPath, StringComparison.Ordinal)
                   || string.Equals(propertyPath, TargetRendererFieldPath, StringComparison.Ordinal);
        }

        private static bool IsPortableReference(UnityEngine.Object value, GameObject root)
        {
            if (ApaAssetDatabaseUtility.IsPersistent(value)) return true;

            if (value is GameObject gameObject)
            {
                return ApaAssetDatabaseUtility.IsInsideSubtree(gameObject.transform, root.transform);
            }

            if (value is Component component)
            {
                return ApaAssetDatabaseUtility.IsInsideSubtree(component.transform, root.transform);
            }

            // A scene material, mesh, or scriptable object instance cannot be stored in a prefab.
            return false;
        }

        /// <summary>Appends the reconciliation notes to a result message.</summary>
        private static string AppendNotes(string message, List<string> notes)
        {
            if (notes == null || notes.Count == 0) return message;

            var text = new System.Text.StringBuilder(message);
            for (var i = 0; i < notes.Count; i++)
            {
                text.Append(' ').Append(notes[i]);
            }

            return text.ToString();
        }
    }
}

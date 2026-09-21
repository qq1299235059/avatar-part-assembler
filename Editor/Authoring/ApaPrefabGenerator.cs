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
        RefusedOccupiedPath = 8,

        /// <summary>
        /// The protected payload could not be produced, published, or proven not to leak the source mesh, so the
        /// protected prefab was not written.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="RefusedNonPortableReference"/> because the remedy is different: a non-portable
        /// reference means "move the referenced object into the part or make it an asset", while this status means
        /// "the payload itself could not be created, or the published prefab would still ship the source mesh".
        /// The diagnostic that accompanies it names which of the two happened.
        /// </remarks>
        RefusedProtectedPayload = 9,

        /// <summary>
        /// A protected-mesh asset already exists and replacing it was not allowed. Nothing was written.
        /// </summary>
        /// <remarks>
        /// Reported separately from <see cref="RefusedOverwrite"/> so the confirmation names the asset that is
        /// actually in the way: the prefab and its payload are two files, and "a prefab already exists" would send
        /// the author looking at the wrong one.
        /// </remarks>
        RefusedProtectedOverwrite = 10
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

        /// <summary>True when the operation stopped because a file it would write already exists.</summary>
        public bool RequiresOverwriteConfirmation =>
            Status == ApaPrefabStatus.RefusedOverwrite || Status == ApaPrefabStatus.RefusedProtectedOverwrite;

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
    /// <item><description>
    /// A part root that is a scene Prefab Instance is saved from a temporary, unpacked copy of that instance, so
    /// the asset is an independent regular prefab carrying the instance's current scene state rather than a
    /// variant of the source prefab. The author's instance is never unpacked or modified.
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
        /// modified by generating a prefab. When the part root is a scene Prefab Instance, the save is made from a
        /// temporary unpacked copy of it rather than from the instance itself, which is what makes the result an
        /// independent regular prefab instead of a Prefab Variant; the original keeps its prefab connection and its
        /// overrides, and the copy is destroyed before this method returns.
        /// </remarks>
        public static ApaPrefabResult CreateFromScene(
            ApaAuthoringSelection selection,
            ApaPartProfile profileAsset,
            string prefabPath,
            bool allowOverwrite)
        {
            return CreateFromScene(selection, profileAsset, prefabPath, allowOverwrite, false, null);
        }

        /// <summary>
        /// Builds a prefab from the scene part root, optionally publishing the part mesh as a protected payload.
        /// </summary>
        /// <param name="selection">The authoring selection; only its part root is read.</param>
        /// <param name="profileAsset">The persistent profile the prefab must reference.</param>
        /// <param name="prefabPath">Where the prefab is written.</param>
        /// <param name="allowOverwrite">True when the caller has confirmed replacing an existing prefab.</param>
        /// <param name="protectPartMesh">
        /// True to write an APA protected-mesh payload beside the prefab and save the prefab's part renderer with
        /// no mesh reference. False is the ordinary path and is byte-for-byte what this generator always did.
        /// </param>
        /// <param name="protectedMeshPath">
        /// Where the payload is written. Required when <paramref name="protectPartMesh"/> is true; the Part
        /// Authoring window derives it from the prefab path
        /// (<see cref="ApaProtectedMeshAssetWriter.DefaultPathFor"/>).
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>The protected path changes exactly two things.</b> The part mesh is serialized into an
        /// <see cref="ApaProtectedMeshAsset"/> and the saved prefab's part renderer is left with no mesh; every
        /// other reference — materials, bones, animator assets, and the profile — is saved exactly as before. The
        /// authoring scene and the source assets are never modified.
        /// </para>
        /// <para>
        /// <b>The result is verified, not assumed.</b> Before the prefab is written, every serialized reference of
        /// the loaded copy is scanned for one that still points at the source mesh or its model file, and after the
        /// write the saved asset's dependency list is checked for the same path. Either check failing refuses the
        /// operation and rolls back what this call wrote, because a protected prefab that still depends on the
        /// source mesh is precisely the leak the feature exists to prevent.
        /// </para>
        /// </remarks>
        public static ApaPrefabResult CreateFromScene(
            ApaAuthoringSelection selection,
            ApaPartProfile profileAsset,
            string prefabPath,
            bool allowOverwrite,
            bool protectPartMesh,
            string protectedMeshPath)
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

            // The protected payload is planned before anything is written: the source mesh is read, serialized, and
            // encrypted here, so a mesh that cannot be protected refuses the operation while the existing prefab
            // and every existing asset are still untouched.
            ApaProtectedCreation protection = null;
            if (protectPartMesh)
            {
                if (!TryPlanProtectedCreation(
                        selection,
                        profileAsset,
                        protectedMeshPath,
                        allowOverwrite,
                        out protection,
                        out var protectionIssue,
                        out var protectedOverwriteRequired))
                {
                    return new ApaPrefabResult(
                        protectedOverwriteRequired
                            ? ApaPrefabStatus.RefusedProtectedOverwrite
                            : ApaPrefabStatus.RefusedProtectedPayload,
                        normalized,
                        existing,
                        ValidationResult.Single(protectionIssue),
                        ApaDiagnosticText.Format(protectionIssue));
                }
            }

            // The temporary clone the prefab-instance path saves instead of the author's scene object. It is
            // created inside the try below and destroyed in the finally that closes it, so no path — including an
            // early return or an exception — can leave it behind in the scene.
            var clone = default(GameObject);

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
                    // Saving the instance directly would connect the new asset to the instance's source prefab,
                    // which is what produces a Prefab Variant. The instance is copied to a temporary parentless
                    // clone and that clone is unpacked, so the asset written below is an independent regular
                    // prefab built from the state the author sees. The original is only read: it is never
                    // unpacked, reparented, or otherwise modified, and the clone is destroyed in the finally.
                    clone = CloneUnpackedInstanceRoot(selection.PartRoot);

                    notes.Add(Tr(
                        "The scene part root is a prefab instance, so the new prefab was built from the instance's " +
                        "current scene state as an independent prefab, not as a variant of the source prefab."));
                }

                // Step 1: copy the scene part root into a prefab asset. The scene object is not modified by this
                // call, and the copy is what everything below operates on. A prefab instance is saved from the
                // unpacked clone instead of from the instance itself.
                var saved = PrefabUtility.SaveAsPrefabAsset(
                    clone != null ? clone : selection.PartRoot, normalized, out var savedOk);
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

                    // The protected payload is published on the loaded copy, after the installer exists (so the
                    // reference can be assigned) and before the portability scan (so the scan sees the cleared mesh
                    // and the new payload reference exactly as they will be stored).
                    if (protection != null
                        && !TryApplyProtectedCreation(contents, protection, allowOverwrite, notes, out var protectionFailure))
                    {
                        // Nothing has been saved back yet: the loaded copy is discarded and a prefab this call
                        // created is removed, so a refusal leaves no unconfigured prefab and no orphan payload.
                        protection.Rollback();
                        if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);

                        if (protectionFailure.RequiresOverwrite)
                        {
                            return new ApaPrefabResult(
                                ApaPrefabStatus.RefusedProtectedOverwrite,
                                normalized,
                                existing,
                                protectionFailure.Issues,
                                protectionFailure.Message);
                        }

                        var protectedMessage = createdFile
                            ? TrFormat("The prefab was not written: {0}.", protectionFailure.Message)
                            : TrFormat(
                                "The existing prefab at '{0}' was replaced with the scene part, but the protected " +
                                "payload could not be published because {1}. The prefab is present but " +
                                "unconfigured; restore it from version control if that content mattered.",
                                normalized, protectionFailure.Message);

                        return new ApaPrefabResult(
                            ApaPrefabStatus.RefusedProtectedPayload,
                            normalized,
                            null,
                            protectionFailure.Issues,
                            AppendNotes(protectedMessage, notes));
                    }

                    var scan = FindUnserializableReferences(contents);
                    if (scan.Issues.Count > 0 || scan.Truncated)
                    {
                        // Roll back a prefab this call created: leaving behind a prefab without a configured
                        // installer would look like a successful generation to the author. An existing prefab
                        // cannot be rolled back, so that case is reported as it is rather than as "not written".
                        if (protection != null) protection.Rollback();
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

                // Step 3 (protected path only): prove on the written asset that the source mesh or its model file
                // is no longer anywhere in the prefab's dependency graph. The scan above inspected the loaded copy's
                // serialized references; this check is the authority for what the file actually depends on, which is
                // what a recipient receives. A failure rolls back both assets this call wrote.
                if (protection != null
                    && !VerifyNoSourceDependency(normalized, protection, out var dependencyIssue))
                {
                    protection.Rollback();
                    if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);

                    var leakMessage = createdFile
                        ? TrFormat("The prefab was not written: {0}.", dependencyIssue.Message)
                        : TrFormat(
                            "The existing prefab at '{0}' was replaced with the scene part, but the written prefab " +
                            "still depends on the source mesh or its model file, so it was removed again. Restore " +
                            "the prefab from version control if its previous content mattered.",
                            normalized);

                    return new ApaPrefabResult(
                        ApaPrefabStatus.RefusedProtectedPayload,
                        normalized,
                        null,
                        ValidationResult.Single(dependencyIssue),
                        AppendNotes(leakMessage, notes));
                }

                // Step 4 (protected path only): prove on the written asset that the installer really carries the
                // payload reference and that the part renderer really has no mesh. A prefab whose installer lost
                // the reference is indistinguishable from an ordinary mesh-less part to every later consumer, so
                // without this check a creation defect would surface much later as a misleading APA006 against a
                // perfectly good protected part.
                if (protection != null
                    && !VerifyProtectedReference(normalized, protection, out var referenceIssue))
                {
                    protection.Rollback();
                    if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);

                    var missingMessage = createdFile
                        ? TrFormat("The prefab was not written: {0}.", referenceIssue.Message)
                        : TrFormat(
                            "The existing prefab at '{0}' was replaced with the scene part, but the written prefab " +
                            "does not carry the protected mesh reference, so it was removed again. Restore the " +
                            "prefab from version control if its previous content mattered.",
                            normalized);

                    return new ApaPrefabResult(
                        ApaPrefabStatus.RefusedProtectedPayload,
                        normalized,
                        null,
                        ValidationResult.Single(referenceIssue),
                        AppendNotes(missingMessage, notes));
                }

                var asset = ApaAssetDatabaseUtility.Load<GameObject>(normalized);

                // Every step of the transaction succeeded, so the payload write is final and the temporary copy
                // of a replaced payload can be released. This is the only commit point: any earlier return has
                // already rolled the payload back.
                if (protection != null) protection.Commit();

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
                // Both assets this call may have written are rolled back before the failure is reported: an
                // exception must not leave an orphan payload or a half-configured prefab behind.
                if (protection != null) protection.Rollback();
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
            finally
            {
                // The clone exists only to be saved: once the save, the installer configuration, the portability
                // scan, and the unload are done — or one of them has failed — it must not be left in the scene.
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
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
        /// One planned protected creation: the encrypted payload, the asset path it goes to, and the source asset
        /// the written prefab must no longer depend on.
        /// </summary>
        /// <remarks>
        /// The plan is built before the first write so that everything that can fail for a content reason — an
        /// unreadable mesh, a mesh that cannot be serialized, an occupied payload path — fails while the project is
        /// still untouched. <see cref="Rollback"/> then undoes the payload write if a later step of the same
        /// transaction fails.
        /// </remarks>
        private sealed class ApaProtectedCreation
        {
            internal string AssetPath { get; }
            internal ApaProtectedMeshPayload Payload { get; }
            internal string SourceAssetPath { get; }
            internal string Fingerprint { get; }
            internal ApaProtectedMeshWriteResult Write { get; private set; }

            internal ApaProtectedCreation(
                string assetPath,
                ApaProtectedMeshPayload payload,
                string sourceAssetPath,
                string fingerprint)
            {
                AssetPath = assetPath;
                Payload = payload;
                SourceAssetPath = sourceAssetPath;
                Fingerprint = fingerprint;
            }

            internal void RecordWrite(ApaProtectedMeshWriteResult write)
            {
                Write = write;
            }

            /// <summary>Undoes the payload write, if one happened. Safe to call repeatedly.</summary>
            internal void Rollback()
            {
                var write = Write;
                Write = null;
                if (write != null) write.Rollback();
            }

            /// <summary>
            /// Accepts the payload write as final once the whole prefab transaction succeeded.
            /// </summary>
            /// <remarks>
            /// Releases the temporary copy of the previous payload the writer keeps for a rollback. Without this
            /// the copy would survive as an unreachable <see cref="ScriptableObject"/> after every successful
            /// replacement; calling it is therefore the last step of a successful creation, not an optional
            /// tidy-up.
            /// </remarks>
            internal void Commit()
            {
                var write = Write;
                Write = null;
                if (write != null) write.Commit();
            }
        }

        /// <summary>Why a protected creation could not be applied, and whether a confirmation would unblock it.</summary>
        private sealed class ApaProtectedFailure
        {
            internal string Message { get; }
            internal ValidationResult Issues { get; }
            internal bool RequiresOverwrite { get; }

            internal ApaProtectedFailure(string message, ValidationResult issues, bool requiresOverwrite)
            {
                Message = message ?? string.Empty;
                Issues = issues ?? ValidationResult.Empty;
                RequiresOverwrite = requiresOverwrite;
            }
        }

        /// <summary>
        /// Reads the scene part mesh and turns it into an authenticated payload, before anything is written.
        /// </summary>
        /// <remarks>
        /// The mesh is captured through <see cref="MeshSnapshotFactory"/>, which is the same reader the
        /// unprotected path uses, so the protected payload can only ever carry what an ordinary capture would have
        /// carried. The bone signature is resolved against the profile's selected part armature when it is
        /// declared and resolvable; a profile that declares none records no paths, exactly as an unprotected
        /// capture of a renderer without a resolvable scope would.
        /// </remarks>
        private static bool TryPlanProtectedCreation(
            ApaAuthoringSelection selection,
            ApaPartProfile profileAsset,
            string protectedMeshPath,
            bool allowOverwrite,
            out ApaProtectedCreation creation,
            out ValidationIssue issue,
            out bool requiresOverwrite)
        {
            creation = null;
            issue = null;
            requiresOverwrite = false;

            if (!ApaAuthoringAssetPaths.TryValidateAssetPath(
                    protectedMeshPath,
                    ApaAuthoringAssetPaths.ProtectedMeshExtension,
                    out var normalizedProtectedPath,
                    out var pathReason))
            {
                issue = ApaAuthoringAssetPaths.InvalidPathIssue(
                    protectedMeshPath, ApaAuthoringAssetPaths.ProtectedMeshExtension, pathReason, "protected mesh");
                return false;
            }

            var occupant = ApaAssetDatabaseUtility.LoadMainAsset(normalizedProtectedPath);
            if (occupant != null && !(occupant is ApaProtectedMeshAsset))
            {
                issue = ValidationIssue.Error(
                    ApaAuthoringErrorCode.InvalidAuthoringPath,
                    ApaIssuePhase.Configuration,
                    "The protected mesh path '" + normalizedProtectedPath + "' is occupied by '" + occupant.name +
                    "' (" + occupant.GetType().Name + "), which is not a protected mesh asset. Nothing was " +
                    "written. Choose a different part prefab path, or move the asset that occupies it.",
                    detail: "reason=protected-path-occupied; path=" + normalizedProtectedPath);
                return false;
            }

            if (occupant != null && !allowOverwrite)
            {
                requiresOverwrite = true;
                issue = ValidationIssue.Error(
                    ApaAuthoringErrorCode.InvalidAuthoringPath,
                    ApaIssuePhase.Configuration,
                    "A protected mesh asset already exists at '" + normalizedProtectedPath + "'. Nothing was " +
                    "written. Confirm the replacement explicitly to overwrite it.",
                    detail: "reason=protected-overwrite-not-allowed; path=" + normalizedProtectedPath);
                return false;
            }

            var partRoot = selection.PartRoot;
            var renderer = partRoot.GetComponentInChildren<Renderer>(true);
            if (renderer == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "Part root '" + partRoot.name + "' has no Renderer, so there is no mesh to protect.",
                    detail: "reason=missing-part-renderer; partRoot=" + partRoot.name);
                return false;
            }

            var mesh = MeshOf(renderer);
            if (mesh == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "Part renderer '" + renderer.name + "' has no mesh assigned, so there is nothing to protect. " +
                    "Assign the source mesh before creating a protected prefab.",
                    detail: "reason=missing-part-mesh; renderer=" + renderer.name);
                return false;
            }

            if (!mesh.isReadable)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.UnsupportedMeshAttribute,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + mesh.name + "' is not readable, so it cannot be serialized into a protected " +
                    "payload. Enable Read/Write in its import settings.",
                    detail: "reason=not-readable; mesh=" + mesh.name);
                return false;
            }

            var partId = ApaPartIdentityResolver.ResolvePartId(profileAsset);
            var bonePaths = ResolvePartBonePaths(profileAsset, partRoot, renderer);

            var snapshot = MeshSnapshotFactory.Capture(
                mesh,
                bonePaths,
                out var captureIssues,
                MeshSnapshotFactory.CaptureBoneWorldToLocalMatrices(renderer));
            if (snapshot == null)
            {
                var first = captureIssues.Count > 0
                    ? captureIssues[0]
                    : ValidationIssue.Error(
                        ApaErrorCode.UnsupportedMeshAttribute,
                        ApaIssuePhase.Attributes,
                        "Mesh '" + mesh.name + "' could not be captured for a protected payload.",
                        detail: "reason=capture-failed; mesh=" + mesh.name);
                issue = first;
                return false;
            }

            if (!ApaProtectedMeshCodec.TryCreatePayload(snapshot, partId, out var payload, out issue))
            {
                return false;
            }

            creation = new ApaProtectedCreation(
                normalizedProtectedPath,
                payload,
                AssetDatabase.GetAssetPath(mesh),
                snapshot.ContentFingerprint);
            return true;
        }

        /// <summary>
        /// The bone paths recorded into the payload, resolved exactly as the build would resolve them.
        /// </summary>
        /// <remarks>
        /// Best effort by design: a profile that declares no part armature, or one that no longer resolves, records
        /// no paths rather than blocking creation. The strict armature rule belongs to validation and the build,
        /// where it is reported once with the one action that fixes it; refusing here would report the same defect
        /// a second time in the middle of an unrelated operation.
        /// </remarks>
        private static string[] ResolvePartBonePaths(ApaPartProfile profileAsset, GameObject partRoot, Renderer renderer)
        {
            var bones = profileAsset != null ? profileAsset.BonesOrNull : null;
            var path = bones != null ? bones.PartArmaturePath : string.Empty;
            if (!ApaAvatarPath.HasIdentity(path)) return Array.Empty<string>();

            var root = partRoot != null ? partRoot.transform : null;
            if (root == null) return Array.Empty<string>();

            var armature = ApaAvatarPath.IsRoot(path) ? root : root.Find(path);
            if (armature == null || (armature != root && !armature.IsChildOf(root))) return Array.Empty<string>();

            var signature = MeshSnapshotFactory.CaptureBoneSignature(armature, renderer);
            return signature.ToArray();
        }

        /// <summary>
        /// Publishes the payload on the loaded prefab copy, clears the part renderer's mesh, and assigns the
        /// reference to the installer.
        /// </summary>
        /// <remarks>
        /// The order is the contract. The mesh is cleared first, so the source-leak scan that follows sees the copy
        /// exactly as it would be stored; the payload is written only after that scan passes, so a refusal leaves
        /// no orphan asset; and the installer's reference is assigned last, so a payload write that fails cannot
        /// leave the installer pointing at nothing.
        /// </remarks>
        private static bool TryApplyProtectedCreation(
            GameObject contents,
            ApaProtectedCreation creation,
            bool allowOverwrite,
            List<string> notes,
            out ApaProtectedFailure failure)
        {
            failure = null;

            var installer = contents.GetComponent<AvatarPartInstaller>();
            var partRoot = installer != null ? installer.ResolvePartRoot() : null;
            var renderer = partRoot != null ? partRoot.GetComponentInChildren<Renderer>(true) : null;
            if (renderer == null)
            {
                failure = new ApaProtectedFailure(
                    "the prefab has no part renderer to clear the mesh reference on",
                    ValidationResult.Single(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Configuration,
                        "The prefab's part root has no Renderer, so the source mesh reference could not be cleared.",
                        detail: "reason=missing-part-renderer")),
                    false);
                return false;
            }

            ClearMeshReference(renderer);
            notes.Add(TrFormat(
                "Cleared the part renderer's mesh reference in the saved prefab ('{0}'); the geometry now lives " +
                "only in the protected payload.", renderer.name));

            if (!string.IsNullOrEmpty(creation.SourceAssetPath))
            {
                var leak = FindReferencesToAsset(contents, creation.SourceAssetPath);
                if (leak.Issues.Count > 0 || leak.Truncated)
                {
                    var reason = leak.Truncated
                        ? TrFormat(
                            "the reference scan stopped at its property budget after {0} properties, so the " +
                            "prefab cannot be proven free of the source mesh", leak.ScannedProperties)
                        : TrFormat(
                            "the part still references the source mesh or model file '{0}' from {1} other " +
                            "component reference(s)", creation.SourceAssetPath, leak.Issues.Count);

                    failure = new ApaProtectedFailure(
                        reason,
                        ValidationResult.Build(leak.Issues),
                        false);
                    return false;
                }
            }

            var write = ApaProtectedMeshAssetWriter.Write(
                creation.AssetPath, creation.Payload, creation.Fingerprint, allowOverwrite);
            if (!write.Succeeded)
            {
                failure = new ApaProtectedFailure(
                    write.Message,
                    write.Issue != null ? ValidationResult.Single(write.Issue) : ValidationResult.Empty,
                    write.RequiresOverwriteConfirmation);
                return false;
            }

            creation.RecordWrite(write);

            installer.ProtectedMesh = write.Asset;
            EditorUtility.SetDirty(installer);
            notes.Add(TrFormat("Wrote the protected mesh payload to '{0}'.", write.Path));
            return true;
        }

        /// <summary>Removes the mesh reference a part renderer would otherwise serialize.</summary>
        /// <remarks>
        /// Both renderer shapes are handled because both are supported part sources. Only the mesh is cleared:
        /// materials, bones, the root bone, and every other component are left exactly as they are, which is what
        /// keeps a protected prefab's non-geometry references normal Unity references.
        /// </remarks>
        private static void ClearMeshReference(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                skinned.sharedMesh = null;
                return;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = null;
        }

        /// <summary>The mesh a part renderer reads, whichever renderer shape it is.</summary>
        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>
        /// Proves on the written prefab that its installer carries the payload reference and its part renderer has
        /// no mesh left.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why the write is verified rather than assumed.</b> The reference is assigned on the loaded copy and
        /// then saved; a save that dropped it, an installer the configuration step replaced, or a component the
        /// scene hierarchy put in the way would all produce a prefab that looks, to every later consumer, exactly
        /// like an ordinary part whose mesh is missing. That misreport is the expensive kind: the author is told
        /// their part has no mesh when the real defect is that the payload reference never made it into the asset.
        /// </para>
        /// <para>
        /// The check reads the file back through the asset database, so it sees what a recipient receives rather
        /// than what this call intended to write.
        /// </para>
        /// <para>
        /// <b>The reference is compared as a Unity asset, not as a managed wrapper.</b> Reading the file back is
        /// exactly the operation that can hand out a second wrapper for the payload this creation wrote, so a
        /// wrapper-identity test would reject a perfectly good prefab after a save or an asset database reload.
        /// The reference must resolve to the payload path this creation wrote, and while the written instance is
        /// still alive it must also be that same Unity object; a payload that was replaced, a reference that
        /// points elsewhere, and a transient instance that was never persisted all fail closed.
        /// </para>
        /// </remarks>
        private static bool VerifyProtectedReference(
            string prefabPath,
            ApaProtectedCreation creation,
            out ValidationIssue issue)
        {
            issue = null;
            if (creation == null) return true;

            var prefab = ApaAssetDatabaseUtility.Load<GameObject>(prefabPath);
            if (prefab == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshInvalid,
                    ApaIssuePhase.Configuration,
                    "The protected prefab at '" + prefabPath + "' could not be loaded back to verify that its " +
                    "installer references the payload, so it cannot be published. The prefab and its payload were " +
                    "rolled back.",
                    detail: "reason=protected-reference-unverifiable; prefab=" + prefabPath);
                return false;
            }

            var installer = prefab.GetComponentInChildren<AvatarPartInstaller>(true);
            if (installer == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshInvalid,
                    ApaIssuePhase.Configuration,
                    "The written prefab at '" + prefabPath + "' has no Avatar Part Installer, so it cannot carry " +
                    "the protected mesh reference and would install nothing. The prefab and its payload were " +
                    "rolled back.",
                    detail: "reason=protected-reference-missing; prefab=" + prefabPath + "; installer=(none)");
                return false;
            }

            if (installer.ProtectedMesh == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshInvalid,
                    ApaIssuePhase.Configuration,
                    "The written prefab at '" + prefabPath + "' has an installer that does not reference the " +
                    "protected mesh payload '" + creation.AssetPath + "'. Without that reference the part has no " +
                    "mesh and no payload, and every later consumer reports it as a mesh-less part instead of as " +
                    "the protected part it is. The prefab and its payload were rolled back; create the protected " +
                    "prefab again.",
                    detail: "reason=protected-reference-missing; prefab=" + prefabPath +
                            "; asset=" + creation.AssetPath);
                return false;
            }

            // The reference is proven by two authorities, because they fail in different ways. The payload path is
            // what a recipient resolves the reference to, and it survives the save, import, or reload that can
            // replace or detach the wrapper this creation wrote with; the write result's own instance is the
            // stronger statement that the reference is the very asset that was written, and it is only available
            // while that wrapper is still alive. The comparison is Unity identity, never managed wrapper identity:
            // two wrappers for one asset are the same asset, while a reference to another payload — or to a
            // transient instance that only looks like one — fails the proof and rolls the prefab back.
            var written = creation.Write != null ? creation.Write.Asset : null;
            if (!ApaAssetDatabaseUtility.IsSameAssetAtPath(installer.ProtectedMesh, creation.AssetPath, written))
            {
                var referencedPath = AssetDatabase.GetAssetPath(installer.ProtectedMesh);
                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshInvalid,
                    ApaIssuePhase.Configuration,
                    "The written prefab at '" + prefabPath + "' references a different protected mesh asset than " +
                    "the payload this creation wrote ('" + creation.AssetPath + "'). The prefab and its payload " +
                    "were rolled back; create the protected prefab again.",
                    detail: "reason=protected-reference-mismatch; prefab=" + prefabPath +
                            "; asset=" + creation.AssetPath +
                            "; referenced=" + (string.IsNullOrEmpty(referencedPath)
                                ? "(not-a-persisted-asset)"
                                : referencedPath));
                return false;
            }

            var partRoot = installer.ResolvePartRoot();
            var renderer = partRoot != null ? partRoot.GetComponentInChildren<Renderer>(true) : null;
            if (renderer != null && MeshOf(renderer) != null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshInvalid,
                    ApaIssuePhase.Configuration,
                    "The written prefab at '" + prefabPath + "' still has a mesh assigned to its part renderer '" +
                    renderer.name + "'. A protected prefab must not reference the source mesh, because publishing " +
                    "it would ship the mesh the protection withholds. The prefab and its payload were rolled " +
                    "back.",
                    detail: "reason=protected-mesh-not-cleared; prefab=" + prefabPath +
                            "; renderer=" + renderer.name);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Proves on the written prefab that the source mesh or model file is no longer a dependency.
        /// </summary>
        /// <remarks>
        /// This is the authority for what the published file depends on: the earlier scan inspected the loaded
        /// copy's serialized references, while <see cref="AssetDatabase.GetDependencies(string, bool)"/> reports
        /// what the asset database resolves for the file on disk, including nested prefabs and any reference this
        /// generator's own walk could not see.
        /// </remarks>
        private static bool VerifyNoSourceDependency(
            string prefabPath,
            ApaProtectedCreation creation,
            out ValidationIssue issue)
        {
            issue = null;
            if (creation == null || string.IsNullOrEmpty(creation.SourceAssetPath)) return true;

            string[] dependencies;
            try
            {
                dependencies = AssetDatabase.GetDependencies(prefabPath, true);
            }
            catch (Exception e)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshSourceLeak,
                    ApaIssuePhase.Configuration,
                    "The written prefab's dependency list could not be read (" + e.GetType().Name + "), so it " +
                    "cannot be proven free of the source mesh '" + creation.SourceAssetPath + "'. The prefab and " +
                    "its payload were rolled back.",
                    detail: "reason=source-mesh-dependency-unverifiable; exception=" + e.GetType().FullName);
                return false;
            }

            for (var i = 0; i < dependencies.Length; i++)
            {
                if (!ApaAuthoringAssetPaths.PathsEqual(dependencies[i], creation.SourceAssetPath)) continue;

                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshSourceLeak,
                    ApaIssuePhase.Configuration,
                    "The written prefab still depends on '" + creation.SourceAssetPath +
                    "', the source mesh or model file it was supposed to stop depending on. Publishing it would " +
                    "ship the mesh the protection withholds, so the prefab and its payload were rolled back. " +
                    "Remove the remaining reference (a collider, a second renderer, or an avatar or animation " +
                    "asset imported from the same file) and create the protected prefab again.",
                    detail: "reason=source-mesh-dependency; prefab=" + prefabPath +
                            "; asset=" + creation.SourceAssetPath);
                return false;
            }

            return true;
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
            return ScanReferences(root, null, false);
        }

        /// <summary>
        /// Finds every reference in a hierarchy that still points at one specific project asset.
        /// </summary>
        /// <param name="root">The hierarchy to scan.</param>
        /// <param name="assetPath">
        /// The asset path to look for, as <c>AssetDatabase.GetAssetPath</c> reports it. For a mesh imported from a
        /// model file this is the model file's path, which is exactly what has to disappear from a protected
        /// prefab: the mesh and the model it came from are one dependency.
        /// </param>
        /// <remarks>
        /// <para>
        /// Used by protected prefab creation after the part renderer's mesh reference has been cleared. Clearing
        /// that one field is not proof of anything on its own — a collider, a second renderer, or an avatar asset
        /// imported from the same model file keeps the source in the prefab's dependency graph — so every
        /// serialized reference of the saved copy is checked, and the reference that would leak the source is named
        /// in the diagnostic.
        /// </para>
        /// <para>
        /// An empty <paramref name="assetPath"/> scans nothing and reports nothing: a mesh that is not a project
        /// asset cannot be referenced by a prefab at all, so there is no path to forbid.
        /// </para>
        /// </remarks>
        public static ApaReferenceScanResult FindReferencesToAsset(GameObject root, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return new ApaReferenceScanResult(new List<ValidationIssue>(), 0, false);
            return ScanReferences(root, assetPath, false);
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
            return ScanReferences(root, null, skipReconciledInstallerFields);
        }

        /// <summary>
        /// Walks every visible serialized property of every component and applies one reference rule.
        /// </summary>
        /// <param name="root">The hierarchy to scan.</param>
        /// <param name="forbiddenAssetPath">
        /// When non-null, the scan reports references to that asset path instead of non-portable references.
        /// </param>
        /// <param name="skipReconciledInstallerFields">When true, the three installer fields the generator rewrites are skipped.</param>
        /// <remarks>
        /// <para>
        /// One walk, two rules. The portability rule and the source-leak rule must see exactly the same set of
        /// properties — a reference the portability rule inspects but the leak rule misses would be a hole in the
        /// protection guarantee — so the traversal exists once and the rule is a parameter.
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
        private static ApaReferenceScanResult ScanReferences(
            GameObject root,
            string forbiddenAssetPath,
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

                    if (forbiddenAssetPath != null)
                    {
                        if (!ReferencesAsset(value, forbiddenAssetPath)) continue;

                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.ProtectedMeshSourceLeak,
                            ApaIssuePhase.Configuration,
                            "Component '" + component.GetType().Name + "' on '" + component.gameObject.name +
                            "' still references the source mesh or model file '" + forbiddenAssetPath +
                            "'. A protected prefab must not depend on the mesh it protects, because publishing it " +
                            "would ship the very asset the protection withholds. Remove or replace that reference " +
                            "(clear the mesh on the extra renderer or collider, or make the referenced object an " +
                            "asset of its own) and create the protected prefab again.",
                            detail: "reason=source-mesh-reference" +
                                    "; component=" + component.GetType().Name +
                                    "; gameObject=" + component.gameObject.name +
                                    "; property=" + iterator.propertyPath +
                                    "; asset=" + forbiddenAssetPath));
                        continue;
                    }

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
        /// True when a reference resolves to the given asset path.
        /// </summary>
        /// <remarks>
        /// The comparison is on the asset path rather than on object identity because a model file's mesh, its
        /// avatar, and its animation clips are distinct objects that share one path: any of them keeps the model
        /// file in the dependency graph, so any of them is the leak.
        /// </remarks>
        private static bool ReferencesAsset(UnityEngine.Object value, string assetPath)
        {
            var path = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrEmpty(path)) return false;
            return ApaAuthoringAssetPaths.PathsEqual(path, assetPath);
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

        /// <summary>
        /// Copies a scene prefab instance to a parentless scene clone whose outermost prefab connection is unpacked.
        /// </summary>
        /// <param name="source">The scene object the author selected. It is only read, never modified.</param>
        /// <remarks>
        /// <para>
        /// <see cref="PrefabUtility.SaveAsPrefabAsset(GameObject, string)"/> keeps a saved prefab instance connected
        /// to the instance's source prefab, and that connection is what makes the written asset a Prefab Variant.
        /// The part prefab this generator writes has to be an independent, regular prefab instead, so the instance
        /// is copied first and the copy is disconnected from its source before it is saved.
        /// </para>
        /// <para>
        /// The copy is deliberately parentless. A clone left under the instance's parent would itself become part
        /// of that parent's prefab instance, and saving it would reintroduce the connection this method exists to
        /// remove. The selected hierarchy's current serialized state travels with the copy, and its local position,
        /// rotation, and scale are written explicitly afterwards, so the asset stores the values the author sees
        /// whatever context the copy was created in.
        /// </para>
        /// <para>
        /// <see cref="PrefabUnpackMode.OutermostRoot"/> removes only the copy's own outermost connection: a nested
        /// prefab instance inside the part stays an instance and keeps its link to its own prefab asset. The caller
        /// owns the returned object and must destroy it.
        /// </para>
        /// </remarks>
        private static GameObject CloneUnpackedInstanceRoot(GameObject source)
        {
            var clone = UnityEngine.Object.Instantiate(source);
            try
            {
                clone.name = source.name;

                // Instantiate copies the serialized local transform, but the copy is made explicit here so the
                // asset stores exactly the values the author sees rather than depending on that default.
                clone.transform.localPosition = source.transform.localPosition;
                clone.transform.localRotation = source.transform.localRotation;
                clone.transform.localScale = source.transform.localScale;

                if (!PrefabUtility.IsPartOfPrefabInstance(clone))
                {
                    // The copy did not carry a prefab connection, so there is nothing to unpack: it is already an
                    // ordinary hierarchy and saving it produces a regular prefab.
                    return clone;
                }

                var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(clone);
                PrefabUtility.UnpackPrefabInstance(
                    instanceRoot != null ? instanceRoot : clone,
                    PrefabUnpackMode.OutermostRoot,
                    InteractionMode.AutomatedAction);

                return clone;
            }
            catch
            {
                // A copy that could not be prepared must not be left behind in the scene: the caller only receives
                // the object on success, so the failure path destroys it here and lets the caller report the error.
                UnityEngine.Object.DestroyImmediate(clone);
                throw;
            }
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

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>What a profile write did, or why it did nothing.</summary>
    public enum ApaProfileWriteStatus
    {
        /// <summary>A new profile asset was created.</summary>
        Created = 0,

        /// <summary>An existing profile asset was updated in place, keeping its GUID.</summary>
        Updated = 1,

        /// <summary>An asset already exists and overwriting it was not allowed. Nothing was written.</summary>
        RefusedOverwrite = 2,

        /// <summary>The output path cannot name a Unity asset. Nothing was written.</summary>
        RefusedInvalidPath = 3,

        /// <summary>The draft is not valid authoring data. Nothing was written.</summary>
        RefusedInvalidDraft = 4,

        /// <summary>An unexpected failure occurred. Nothing is guaranteed about the asset.</summary>
        Failed = 5,

        /// <summary>
        /// The path is already occupied by an asset of a different type, so writing there would replace
        /// somebody else's work. Nothing was written.
        /// </summary>
        RefusedOccupiedPath = 6
    }

    /// <summary>The outcome of a profile write.</summary>
    public sealed class ApaProfileWriteResult
    {
        /// <summary>What happened.</summary>
        public ApaProfileWriteStatus Status { get; }

        /// <summary>The normalized path that was written or refused.</summary>
        public string Path { get; }

        /// <summary>The written asset, when the write succeeded.</summary>
        public ApaPartProfile Asset { get; }

        /// <summary>Code-carrying diagnostics for a refusal, in deterministic order.</summary>
        public ValidationResult Issues { get; }

        /// <summary>A human-readable explanation, especially for a refusal.</summary>
        public string Message { get; }

        /// <summary>True when the asset was written.</summary>
        public bool Succeeded => Status == ApaProfileWriteStatus.Created || Status == ApaProfileWriteStatus.Updated;

        /// <summary>
        /// True when the write stopped because the file exists. The caller must ask the author explicitly before
        /// retrying with overwrite enabled.
        /// </summary>
        public bool RequiresOverwriteConfirmation => Status == ApaProfileWriteStatus.RefusedOverwrite;

        /// <summary>Creates a result.</summary>
        public ApaProfileWriteResult(
            ApaProfileWriteStatus status,
            string path,
            ApaPartProfile asset,
            ValidationResult issues,
            string message)
        {
            Status = status;
            Path = path ?? string.Empty;
            Asset = asset;
            Issues = issues ?? ValidationResult.Empty;
            Message = message ?? string.Empty;
        }
    }

    /// <summary>
    /// Writes an authoring draft into a serialized profile asset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place a profile is written. It is separated from the window so that the promise "the
    /// authoring window never overwrites a profile silently" is a property of one reviewable method rather than
    /// of UI code, and it is separated from validation so that writing cannot be smuggled into a check.
    /// </para>
    /// <para>
    /// <b>Safety properties, in order of importance.</b>
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// An existing asset is never replaced unless the caller passes <c>allowOverwrite</c>. The default entry
    /// point does not, so a caller has to make the decision explicitly. A path occupied by an asset that is
    /// <i>not</i> a profile is refused outright, whatever <c>allowOverwrite</c> says: permission to replace a
    /// profile is not permission to delete a material.
    /// </description></item>
    /// <item><description>
    /// An invalid draft is never written: semantic names, duplicate declarations, non-persistent material
    /// references, and the schema-v2 compatibility signature are all checked first, and any blocking finding
    /// stops the write before the asset is touched.
    /// </description></item>
    /// <item><description>
    /// An update goes through <see cref="EditorUtility.CopySerialized"/> under
    /// <see cref="Undo.RecordObject"/>, so the asset keeps its GUID and every prefab that references the profile
    /// keeps working, and the author can undo the change.
    /// </description></item>
    /// <item><description>
    /// No scene reference is written: the compatibility signature stores paths and topology, and a material
    /// reference that is not a project asset is refused rather than serialized.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class ApaProfileWriter
    {
        private const string UndoLabel = "Update Avatar Part Profile";

        /// <summary>
        /// Writes the draft to an asset path, creating the asset when it does not exist.
        /// </summary>
        /// <remarks>
        /// This is the safe default: an existing profile is reported and left untouched.
        /// </remarks>
        public static ApaProfileWriteResult Create(ApaProfileDraft draft, string assetPath)
        {
            return Save(draft, assetPath, false);
        }

        /// <summary>
        /// Writes the draft to an asset path, updating an existing asset only when the caller allows it.
        /// </summary>
        public static ApaProfileWriteResult Save(ApaProfileDraft draft, string assetPath, bool allowOverwrite)
        {
            if (draft == null)
            {
                return new ApaProfileWriteResult(
                    ApaProfileWriteStatus.RefusedInvalidDraft,
                    assetPath,
                    null,
                    ValidationResult.Single(ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Configuration,
                        "No profile draft was supplied.",
                        detail: "reason=null-draft")),
                    Tr("No profile draft was supplied."));
            }

            draft.EnsureInitialized();
            draft.EnsureStablePartId();

            if (!ApaAuthoringAssetPaths.TryValidateAssetPath(
                    assetPath,
                    ApaAuthoringAssetPaths.ProfileExtension,
                    out var normalized,
                    out var reason))
            {
                var issue = ApaAuthoringAssetPaths.InvalidPathIssue(
                    assetPath, ApaAuthoringAssetPaths.ProfileExtension, reason, "profile");
                return new ApaProfileWriteResult(
                    ApaProfileWriteStatus.RefusedInvalidPath,
                    normalized,
                    null,
                    ValidationResult.Single(issue),
                    ApaDiagnosticText.Format(issue));
            }

            var issues = new List<ValidationIssue>();

            // Draft-level checks. The mesh-dependent checks are skipped here because the writer has no mesh; the
            // window runs the full validation, this guard exists so the writer cannot be misused on its own.
            issues.AddRange(ApaAuthoringValidation.ValidateDraftData(draft, null));

            // The schema-v2 promise is that a written profile is self-describing. A signature that was never
            // captured, or that lacks a safety field, would produce a profile that can never build, so it is
            // refused here as well as reported by validation.
            issues.AddRange(ApaCompatibilityCapture.ValidateCapturedProfile(draft.Compatibility, null));

            if (HasBlocking(issues))
            {
                var built = ValidationResult.Build(issues);
                return new ApaProfileWriteResult(
                    ApaProfileWriteStatus.RefusedInvalidDraft,
                    normalized,
                    null,
                    built,
                    TrFormat(
                        "The profile was not written because the draft is not valid: {0}.",
                        ApaDiagnosticText.Summarize(built)));
            }

            var existing = ApaAssetDatabaseUtility.Load<ApaPartProfile>(normalized);

            // The typed load above answers "is there a profile here", not "is the path in use": it returns null
            // for a material, a texture, or a prefab. AssetDatabase.CreateAsset deletes whatever occupies the
            // path, so a create decided from that null would destroy an unrelated asset without a dialog. The
            // main-asset check is what makes the "an existing asset is never replaced unless the caller passes
            // allowOverwrite" promise true for a path that happens to be occupied by another asset type.
            var occupant = ApaAssetDatabaseUtility.LoadMainAsset(normalized);
            if (occupant != null && !(occupant is ApaPartProfile))
            {
                return new ApaProfileWriteResult(
                    ApaProfileWriteStatus.RefusedOccupiedPath,
                    normalized,
                    null,
                    ValidationResult.Build(issues),
                    TrFormat(
                        "The path '{0}' is occupied by '{1}' ({2}). Nothing was written. Choose a path that is not " +
                        "already in use.",
                        normalized, occupant.name, occupant.GetType().Name));
            }

            var action = ApaAuthoringAssetPaths.DecideWriteAction(existing != null, allowOverwrite);

            if (action == ApaAssetWriteAction.RefuseOverwrite)
            {
                return new ApaProfileWriteResult(
                    ApaProfileWriteStatus.RefusedOverwrite,
                    normalized,
                    existing,
                    ValidationResult.Build(issues),
                    TrFormat(
                        "A profile already exists at '{0}'. Nothing was written. Confirm the replacement " +
                        "explicitly to update it.", normalized));
            }

            ApaPartProfile workingCopy = null;
            try
            {
                workingCopy = draft.Materialize();
                workingCopy.hideFlags = HideFlags.HideAndDontSave;

                ApaAssetDatabaseUtility.EnsureFolder(normalized);

                if (action == ApaAssetWriteAction.Create)
                {
                    var created = ScriptableObject.CreateInstance<ApaPartProfile>();
                    EditorUtility.CopySerialized(workingCopy, created);
                    created.hideFlags = HideFlags.None;
                    created.name = System.IO.Path.GetFileNameWithoutExtension(normalized);

                    AssetDatabase.CreateAsset(created, normalized);
                    Undo.RegisterCreatedObjectUndo(created, Tr("Create Avatar Part Profile"));
                    EditorUtility.SetDirty(created);
                    AssetDatabase.SaveAssets();

                    return new ApaProfileWriteResult(
                        ApaProfileWriteStatus.Created,
                        normalized,
                        created,
                        ValidationResult.Build(issues),
                        TrFormat("Created profile '{0}'.", normalized));
                }

                Undo.RecordObject(existing, Tr(UndoLabel));
                var assetName = existing.name;
                EditorUtility.CopySerialized(workingCopy, existing);

                // CopySerialized copies the object header as well as the fields, so the name and the hide flags
                // are restored after the copy. Without the hide-flags repair the update path would stamp the
                // working copy's HideAndDontSave onto the asset, which hides it from the Project window and can
                // exclude it from the build — the exact silent failure this writer exists to prevent.
                existing.name = assetName;
                existing.hideFlags = HideFlags.None;
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();

                return new ApaProfileWriteResult(
                    ApaProfileWriteStatus.Updated,
                    normalized,
                    existing,
                    ValidationResult.Build(issues),
                    TrFormat("Updated profile '{0}'.", normalized));
            }
            catch (Exception e)
            {
                return new ApaProfileWriteResult(
                    ApaProfileWriteStatus.Failed,
                    normalized,
                    null,
                    ValidationResult.Single(ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Configuration,
                        "Writing the profile threw " + e.GetType().Name + ": " + e.Message,
                        detail: "exception=" + e.GetType().FullName)),
                    TrFormat("Writing the profile failed: {0}", e.Message));
            }
            finally
            {
                if (workingCopy != null) UnityEngine.Object.DestroyImmediate(workingCopy);
            }
        }

        /// <summary>
        /// True when the draft would change the asset. Used by the window to say "up to date" instead of always
        /// offering an update.
        /// </summary>
        /// <remarks>
        /// The comparison is made between two transient copies, never between a draft and an asset directly, so
        /// that object-level fields such as the asset name or hide flags cannot make an identical profile look
        /// different.
        /// </remarks>
        public static bool HasUnsavedChanges(ApaPartProfile asset, ApaProfileDraft draft)
        {
            if (asset == null) return true;
            if (draft == null) return false;

            ApaPartProfile workingCopy = null;
            try
            {
                workingCopy = draft.Materialize();
                return !ContentEquals(asset, workingCopy);
            }
            finally
            {
                if (workingCopy != null) UnityEngine.Object.DestroyImmediate(workingCopy);
            }
        }

        /// <summary>
        /// True when two profiles serialize to identical content.
        /// </summary>
        /// <remarks>
        /// Two transient hosts are used so that the comparison is over serialized fields only. Comparing a
        /// transient instance with an asset directly would include the object header — the asset name, the
        /// instance's hide flags — and would report a difference for two profiles that are the same part.
        /// </remarks>
        public static bool ContentEquals(ApaPartProfile a, ApaPartProfile b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            ApaPartProfile left = null;
            ApaPartProfile right = null;
            try
            {
                left = ScriptableObject.CreateInstance<ApaPartProfile>();
                right = ScriptableObject.CreateInstance<ApaPartProfile>();

                EditorUtility.CopySerialized(a, left);
                EditorUtility.CopySerialized(b, right);

                // CopySerialized copies the object header as well as the fields, so the hide flags and the name
                // are normalized after the copy. Otherwise an asset and an identical transient draft would
                // compare as different purely because one is a saved asset and the other is not.
                left.hideFlags = HideFlags.None;
                right.hideFlags = HideFlags.None;
                left.name = "compare";
                right.name = "compare";

                return string.Equals(
                    EditorJsonUtility.ToJson(left),
                    EditorJsonUtility.ToJson(right),
                    StringComparison.Ordinal);
            }
            finally
            {
                if (left != null) UnityEngine.Object.DestroyImmediate(left);
                if (right != null) UnityEngine.Object.DestroyImmediate(right);
            }
        }

        private static bool HasBlocking(List<ValidationIssue> issues)
        {
            for (var i = 0; i < issues.Count; i++)
            {
                if (issues[i].IsBlocking) return true;
            }

            return false;
        }
    }
}

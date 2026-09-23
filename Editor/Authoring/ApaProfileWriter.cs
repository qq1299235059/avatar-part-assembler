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
        /// The Avatar Part Assembler package version the written asset records, or an empty string when nothing
        /// was written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read back from the object that was written rather than recomputed, because "what the asset records" is
        /// the fact the draft has to agree with. <see cref="Asset"/> alone cannot answer it: a refused overwrite
        /// reports the profile that was <i>found</i>, not one this write produced, so a caller that read the stamp
        /// off <see cref="Asset"/> would treat an untouched asset as if it had just been stamped.
        /// </para>
        /// <para>
        /// <see cref="ApaProfileWriter.Save"/> brings the draft in line with this value on every successful write,
        /// so a caller normally has nothing to do; the value is exposed for a caller that reports what a write
        /// recorded, and for tests.
        /// </para>
        /// </remarks>
        public string WrittenApaPackageVersion => Succeeded && Asset != null ? Asset.ApaPackageVersion : string.Empty;

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
    /// <item><description>
    /// A successful write leaves the draft describing the asset it produced. The profile records the package
    /// version that wrote it, so the writer stamps the installed version onto the asset and then brings the
    /// draft's own record in line with it (<see cref="AdoptWrittenPackageVersion"/>). Without that step the
    /// draft would keep the stamp of the older release it was loaded from, and the next "the draft differs from
    /// the asset" check would refuse a profile the author had just saved. A refused, cancelled, or failed write
    /// changes neither the asset nor the draft.
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

                // The profile records the package version that wrote it, so the installer can compare it with the
                // installed one and stay quiet while the two agree. The stamp is written here, at the one place a
                // profile reaches disk, and only when the installed version can actually be read: recording an
                // unknown version as an empty string would erase a provenance the asset already carries and turn
                // a readable profile into one that reports a missing stamp.
                var installedVersion = ApaPackageVersion.Current;
                if (!string.IsNullOrEmpty(installedVersion)) workingCopy.ApaPackageVersion = installedVersion;

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

                    // The draft now describes what was written, provenance included. This is the create path, so
                    // the asset carries the installed version this run stamped onto the working copy; the draft
                    // adopts it so the two agree from the moment the write returns.
                    AdoptWrittenPackageVersion(draft, created);

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

                // The draft now describes what was written, provenance included: the update copied the stamped
                // working copy over the asset, so the draft's record of the package version follows the asset's.
                AdoptWrittenPackageVersion(draft, existing);

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
        /// Brings a draft's recorded package version in line with a profile that was written from it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is needed.</b> A profile records the Avatar Part Assembler release that produced it, and
        /// the writer stamps the installed version onto the asset as it writes. A draft loaded from a profile
        /// written by an older release — or by no release at all — keeps the older stamp, because nothing the
        /// author did changed it. That is not an author edit, but <see cref="HasUnsavedChanges"/> compares
        /// content, so the very next prefab step would report "the draft differs from the asset" and refuse to
        /// build from a profile that had just been saved.
        /// </para>
        /// <para>
        /// <b>Only a write that happened may call this, and only with the object that was written.</b> The draft
        /// copies the asset's stamp verbatim, so after a successful write the two agree by construction. A
        /// refused, cancelled, or failed write must leave the draft alone: nothing on disk changed, and a draft
        /// that claimed otherwise would hide a real difference between the two.
        /// </para>
        /// <para>
        /// <b>The comparison is not weakened.</b> <see cref="HasUnsavedChanges"/> still serializes every field of
        /// both profiles and reports any difference; this method removes the difference at its source instead of
        /// teaching the comparison to ignore one.
        /// </para>
        /// </remarks>
        /// <param name="draft">The draft the profile was written from. Null is ignored.</param>
        /// <param name="writtenAsset">The profile object the write produced. Null is ignored.</param>
        /// <returns>True when the draft's recorded version changed.</returns>
        public static bool AdoptWrittenPackageVersion(ApaProfileDraft draft, ApaPartProfile writtenAsset)
        {
            if (draft == null || writtenAsset == null) return false;

            var written = writtenAsset.ApaPackageVersion;
            if (string.Equals(draft.ApaPackageVersion, written, StringComparison.Ordinal)) return false;

            draft.ApaPackageVersion = written;
            return true;
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

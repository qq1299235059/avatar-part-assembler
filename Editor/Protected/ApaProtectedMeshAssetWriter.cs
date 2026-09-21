using System;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>What writing a protected-mesh asset did, or why it did nothing.</summary>
    public enum ApaProtectedMeshWriteStatus
    {
        /// <summary>A new protected asset was created.</summary>
        Created = 0,

        /// <summary>An existing protected asset's payload was replaced in place.</summary>
        Updated = 1,

        /// <summary>An asset already exists and replacing it was not allowed. Nothing was written.</summary>
        RefusedOverwrite = 2,

        /// <summary>The output path cannot name a Unity asset. Nothing was written.</summary>
        RefusedInvalidPath = 3,

        /// <summary>The path is occupied by an asset of a different type. Nothing was written.</summary>
        RefusedOccupiedPath = 4,

        /// <summary>The write failed.</summary>
        Failed = 5
    }

    /// <summary>The outcome of writing a protected-mesh asset, including how to undo it.</summary>
    /// <remarks>
    /// <para>
    /// The rollback is the point of this type. Protected prefab creation writes two assets — the payload and the
    /// prefab that references it — and a failure after the payload was written must not leave an orphan payload
    /// behind, nor must it leave a replaced payload whose original content is gone. <see cref="Rollback"/>
    /// therefore restores exactly what this write changed: it deletes a newly created asset, and it copies the
    /// captured previous contents back into an updated one, keeping the asset's GUID either way.
    /// </para>
    /// <para>
    /// It never deletes an asset this call did not create. An unrelated asset at the target path is refused before
    /// anything is written (<see cref="ApaProtectedMeshWriteStatus.RefusedOccupiedPath"/>), which is the same rule
    /// the profile writer and the prefab generator follow: permission to replace a payload is not permission to
    /// delete somebody's material.
    /// </para>
    /// </remarks>
    public sealed class ApaProtectedMeshWriteResult
    {
        private ApaProtectedMeshAsset _backup;
        private readonly bool _createdFile;

        /// <summary>What happened.</summary>
        public ApaProtectedMeshWriteStatus Status { get; }

        /// <summary>The normalized asset path.</summary>
        public string Path { get; }

        /// <summary>The asset, when the write succeeded.</summary>
        public ApaProtectedMeshAsset Asset { get; }

        /// <summary>A blocking diagnostic, when the write failed for a coded reason.</summary>
        public ValidationIssue Issue { get; }

        /// <summary>A human-readable explanation.</summary>
        public string Message { get; }

        /// <summary>True when the payload was written.</summary>
        public bool Succeeded =>
            Status == ApaProtectedMeshWriteStatus.Created || Status == ApaProtectedMeshWriteStatus.Updated;

        /// <summary>True when the caller must confirm replacing an existing asset before retrying.</summary>
        public bool RequiresOverwriteConfirmation => Status == ApaProtectedMeshWriteStatus.RefusedOverwrite;

        internal ApaProtectedMeshWriteResult(
            ApaProtectedMeshWriteStatus status,
            string path,
            ApaProtectedMeshAsset asset,
            ValidationIssue issue,
            string message,
            bool createdFile,
            ApaProtectedMeshAsset backup)
        {
            Status = status;
            Path = path ?? string.Empty;
            Asset = asset;
            Issue = issue;
            Message = message ?? string.Empty;
            _createdFile = createdFile;
            _backup = backup;
        }

        /// <summary>
        /// Undoes what this write changed, leaving the project as it was before the call.
        /// </summary>
        /// <remarks>
        /// Safe to call after a successful write, and safe to call more than once: the backup instance is consumed
        /// on the first call and destroyed, so a second call is a no-op and a rollback cannot leak the temporary
        /// copy either.
        /// </remarks>
        public void Rollback()
        {
            var backup = _backup;
            _backup = null;

            try
            {
                if (_createdFile && !string.IsNullOrEmpty(Path))
                {
                    ApaAssetDatabaseUtility.RollBackCreatedAsset(Path);
                }
                else if (backup != null && Asset != null)
                {
                    EditorUtility.CopySerialized(backup, Asset);
                    EditorUtility.SetDirty(Asset);
                    AssetDatabase.SaveAssets();
                }
            }
            finally
            {
                if (backup != null) UnityEngine.Object.DestroyImmediate(backup, true);
            }
        }

        /// <summary>
        /// Accepts the write as final and releases the temporary copy of the previous contents.
        /// </summary>
        /// <remarks>
        /// The counterpart to <see cref="Rollback"/>, and the reason a successful replacement does not leave a
        /// hidden <see cref="ScriptableObject"/> behind. The caller that owns the wider transaction — prefab
        /// creation writes the payload and the prefab — calls this only once every step has succeeded, because
        /// until then the previous contents must stay restorable. Calling it after a rollback, or more than once,
        /// is a no-op.
        /// </remarks>
        public void Commit()
        {
            var backup = _backup;
            _backup = null;
            if (backup != null) UnityEngine.Object.DestroyImmediate(backup, true);
        }
    }

    /// <summary>
    /// Writes a protected-mesh payload into an APA-owned asset beside the prefab it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place a protected asset is written. It is separated from the prefab generator so that "an
    /// existing protected asset is never replaced silently" and "a failed write is recoverable" are properties of
    /// one reviewable method rather than of a larger transaction.
    /// </para>
    /// <para>
    /// <b>An update keeps the GUID.</b> Replacing an existing payload goes through
    /// <see cref="EditorUtility.CopySerialized"/> under <see cref="Undo.RecordObject"/>, so a prefab that already
    /// references the asset keeps working and the author can undo the change. Creating a new asset at the same
    /// path instead would silently break that reference.
    /// </para>
    /// </remarks>
    public static class ApaProtectedMeshAssetWriter
    {
        private const string UndoLabel = "Write Protected Part Mesh";

        /// <summary>
        /// Writes a payload to an asset path, creating the asset when it does not exist.
        /// </summary>
        /// <remarks>This is the safe default: an existing asset is reported and left untouched.</remarks>
        public static ApaProtectedMeshWriteResult Create(
            string assetPath,
            ApaProtectedMeshPayload payload,
            string sourceFingerprint)
        {
            return Write(assetPath, payload, sourceFingerprint, false);
        }

        /// <summary>
        /// Writes a payload to an asset path, replacing an existing payload only when the caller allows it.
        /// </summary>
        /// <param name="assetPath">Target asset path. Must end in <c>.asset</c> and live under <c>Assets/</c>.</param>
        /// <param name="payload">The authenticated payload to store.</param>
        /// <param name="sourceFingerprint">
        /// Content fingerprint of the source mesh. Recorded on the asset for diagnostics; the authoritative
        /// comparison is the decoded snapshot against the profile.
        /// </param>
        /// <param name="allowOverwrite">True when the caller has explicitly confirmed replacing the asset.</param>
        public static ApaProtectedMeshWriteResult Write(
            string assetPath,
            ApaProtectedMeshPayload payload,
            string sourceFingerprint,
            bool allowOverwrite)
        {
            if (payload == null)
            {
                var nullIssue = ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "No protected mesh payload was supplied to the writer.",
                    detail: "reason=null-payload");
                return new ApaProtectedMeshWriteResult(
                    ApaProtectedMeshWriteStatus.Failed,
                    assetPath,
                    null,
                    nullIssue,
                    ApaDiagnosticText.Format(nullIssue),
                    false,
                    null);
            }

            if (!ApaAuthoringAssetPaths.TryValidateAssetPath(
                    assetPath,
                    ApaAuthoringAssetPaths.ProtectedMeshExtension,
                    out var normalized,
                    out var reason))
            {
                var issue = ApaAuthoringAssetPaths.InvalidPathIssue(
                    assetPath, ApaAuthoringAssetPaths.ProtectedMeshExtension, reason, "protected mesh");
                return new ApaProtectedMeshWriteResult(
                    ApaProtectedMeshWriteStatus.RefusedInvalidPath,
                    normalized,
                    null,
                    issue,
                    ApaDiagnosticText.Format(issue),
                    false,
                    null);
            }

            var existing = ApaAssetDatabaseUtility.Load<ApaProtectedMeshAsset>(normalized);

            // The typed load answers "is there a payload here", not "is the path in use". A path occupied by a
            // different asset type is refused whatever allowOverwrite says: permission to replace a payload is not
            // permission to delete a texture.
            var occupant = ApaAssetDatabaseUtility.LoadMainAsset(normalized);
            if (occupant != null && !(occupant is ApaProtectedMeshAsset))
            {
                return new ApaProtectedMeshWriteResult(
                    ApaProtectedMeshWriteStatus.RefusedOccupiedPath,
                    normalized,
                    null,
                    null,
                    "The path '" + normalized + "' is occupied by '" + occupant.name + "' (" +
                    occupant.GetType().Name + "), not a protected mesh asset. Nothing was written. Choose a path " +
                    "that is not already in use.",
                    false,
                    null);
            }

            if (existing != null && !allowOverwrite)
            {
                return new ApaProtectedMeshWriteResult(
                    ApaProtectedMeshWriteStatus.RefusedOverwrite,
                    normalized,
                    existing,
                    null,
                    "A protected mesh asset already exists at '" + normalized + "'. Nothing was written. Confirm " +
                    "the replacement explicitly to overwrite it.",
                    false,
                    null);
            }

            // Tracked outside the try so the catch below can remove a file this call created even when the
            // failure happened after CreateAsset: a half-written payload left at the target path is exactly the
            // orphan asset the transaction contract forbids.
            var createdFile = false;

            try
            {
                ApaAssetDatabaseUtility.EnsureFolder(normalized);

                if (existing == null)
                {
                    var created = ScriptableObject.CreateInstance<ApaProtectedMeshAsset>();
                    created.Assign(
                        payload.FormatVersion,
                        payload.Codec,
                        payload.PartId,
                        string.IsNullOrEmpty(payload.SourceFingerprint) ? sourceFingerprint : payload.SourceFingerprint,
                        payload.PlaintextLength,
                        payload.Salt,
                        payload.Iv,
                        payload.Ciphertext,
                        payload.Tag);

                    AssetDatabase.CreateAsset(created, normalized);
                    createdFile = true;
                    AssetDatabase.SaveAssets();

                    var loaded = ApaAssetDatabaseUtility.Load<ApaProtectedMeshAsset>(normalized);
                    if (loaded == null)
                    {
                        ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);
                        return new ApaProtectedMeshWriteResult(
                            ApaProtectedMeshWriteStatus.Failed,
                            normalized,
                            null,
                            null,
                            "The protected mesh asset was written to '" + normalized +
                            "' but could not be loaded back. Nothing was kept.",
                            false,
                            null);
                    }

                    return new ApaProtectedMeshWriteResult(
                        ApaProtectedMeshWriteStatus.Created,
                        normalized,
                        loaded,
                        null,
                        "Created the protected mesh asset '" + normalized + "'.",
                        true,
                        null);
                }

                // The previous contents are copied into a temporary instance before the write, so a failure later
                // in the same transaction can restore them exactly, GUID included.
                var backup = ScriptableObject.CreateInstance<ApaProtectedMeshAsset>();
                EditorUtility.CopySerialized(existing, backup);

                Undo.RecordObject(existing, UndoLabel);
                existing.Assign(
                    payload.FormatVersion,
                    payload.Codec,
                    payload.PartId,
                    string.IsNullOrEmpty(payload.SourceFingerprint) ? sourceFingerprint : payload.SourceFingerprint,
                    payload.PlaintextLength,
                    payload.Salt,
                    payload.Iv,
                    payload.Ciphertext,
                    payload.Tag);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();

                return new ApaProtectedMeshWriteResult(
                    ApaProtectedMeshWriteStatus.Updated,
                    normalized,
                    existing,
                    null,
                    "Replaced the protected mesh payload in '" + normalized + "'.",
                    false,
                    backup);
            }
            catch (Exception e)
            {
                // A create that threw after the file appeared is rolled back here. The update path needs no
                // rollback at this point: Assign only mutates the in-memory asset, and the file on disk is
                // rewritten by the SaveAssets call that follows it, so a throw before that leaves the stored
                // contents untouched.
                if (createdFile) ApaAssetDatabaseUtility.RollBackCreatedAsset(normalized);

                return new ApaProtectedMeshWriteResult(
                    ApaProtectedMeshWriteStatus.Failed,
                    normalized,
                    null,
                    ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Configuration,
                        "Writing the protected mesh asset threw " + e.GetType().Name + ": " + e.Message,
                        detail: "exception=" + e.GetType().FullName),
                    "Writing the protected mesh asset failed: " + e.Message,
                    false,
                    null);
            }
        }

        /// <summary>
        /// The path a protected asset takes for a prefab path, so the two travel together.
        /// </summary>
        /// <remarks>
        /// Derived rather than typed by the author: the payload belongs beside the prefab it protects, and a second
        /// path field would be one more thing to keep consistent. The suffix makes the pairing visible in the
        /// Project window, which is what a creator needs when assembling a delivery folder.
        /// </remarks>
        public static string DefaultPathFor(string prefabPath)
        {
            var normalized = ApaAuthoringAssetPaths.Normalize(prefabPath);
            if (string.IsNullOrEmpty(normalized)) return string.Empty;

            var withoutExtension = normalized.EndsWith(ApaAuthoringAssetPaths.PrefabExtension, StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(0, normalized.Length - ApaAuthoringAssetPaths.PrefabExtension.Length)
                : normalized;

            return withoutExtension + ApaAuthoringAssetPaths.ProtectedMeshSuffix +
                   ApaAuthoringAssetPaths.ProtectedMeshExtension;
        }
    }
}

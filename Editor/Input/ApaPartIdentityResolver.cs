using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Resolves the stable part identity of a profile, including the deterministic fallback M11 introduced for
    /// profiles that carry none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem.</b> <see cref="ApaPartIdentity.PartId"/> is assigned when a profile is authored, but a
    /// profile written before the field existed — or written by a tool that never called
    /// <see cref="ApaPartIdentity.EnsureStablePartId"/> — carries an empty id. The build refuses an empty part id
    /// (<c>APA012 reason=missing-part-id</c>), which leaves the user of an already-installed part with nothing to
    /// act on: the profile is valid in every other respect, and re-authoring it is a large answer to a small
    /// problem.
    /// </para>
    /// <para>
    /// <b>The answer.</b> A profile without a stored id resolves to an id derived from the profile asset's GUID.
    /// The GUID is stable across domain reloads, previews, validations, sorts, and builds, and it is unique per
    /// asset, so the derived id is as stable and as unique as a stored one for every purpose the pipeline uses
    /// it for (ordering, diagnostics, and per-part dictionary keys). It is deliberately prefixed
    /// (<c>apa-asset-…</c>) so that a derived id is recognizable in a report rather than masquerading as an
    /// authored one.
    /// </para>
    /// <para>
    /// <b>Nothing is written while reading.</b> Derivation is a pure read: the build, the preview, and a repaint
    /// must never modify a shared authoring asset, because such a write is invisible, non-undoable, and can be
    /// persisted by an unrelated <c>AssetDatabase.SaveAssets()</c>. Persisting the id is an explicit author
    /// action — saving the profile in the Part Authoring window, or <see cref="TryRepair"/> from the installer
    /// inspector — and it writes exactly the derived id, so repairing a profile never changes the identity the
    /// pipeline was already using.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> <see cref="DeriveFromAssetIdentity"/> is a pure function of the asset GUID and the
    /// local file id, so two runs over the same asset produce the same id, and a test can assert the derivation
    /// without an asset database.
    /// </para>
    /// </remarks>
    public static class ApaPartIdentityResolver
    {
        /// <summary>Prefix of an id derived from a profile asset, so a derived id is recognizable in a report.</summary>
        public const string DerivedPrefix = "apa-asset-";

        /// <summary>Stable reason token reported when a derived id is used.</summary>
        public const string DerivedReason = "part-id-derived-from-asset-guid";

        /// <summary>Stable reason token reported when a profile could not be repaired.</summary>
        public const string RepairFailedReason = "part-id-repair-failed";

        /// <summary>
        /// True when the profile carries a stored (authored) part id. Never assigns, so a read cannot mutate the
        /// shared asset.
        /// </summary>
        public static bool HasStoredPartId(ApaPartProfile profile)
        {
            var identity = profile != null ? profile.IdentityOrNull : null;
            return identity != null && !string.IsNullOrEmpty(identity.PartId);
        }

        /// <summary>
        /// The part id of a profile: the stored one when it exists, otherwise the id derived from the profile
        /// asset's GUID, otherwise an empty string for a profile that can be identified by neither.
        /// </summary>
        /// <remarks>
        /// An empty result means "genuinely unidentifiable" — a transient profile that is not an asset and
        /// carries no id — which the configuration rule still refuses rather than guessing at.
        /// </remarks>
        public static string ResolvePartId(ApaPartProfile profile)
        {
            if (profile == null) return string.Empty;
            if (HasStoredPartId(profile)) return profile.IdentityOrNull.PartId;

            return TryDeriveFromAsset(profile, out var derived) ? derived : string.Empty;
        }

        /// <summary>The resolved part id of an installer's profile, or an empty string when it has none.</summary>
        public static string ResolvePartId(AvatarPartInstaller installer)
        {
            return installer != null ? ResolvePartId(installer.Profile) : string.Empty;
        }

        /// <summary>
        /// True when the profile is a project asset but carries no stored part id, so the identity is currently
        /// derived and the author may want to persist it.
        /// </summary>
        public static bool NeedsRepair(ApaPartProfile profile)
        {
            if (profile == null || HasStoredPartId(profile)) return false;
            return TryDeriveFromAsset(profile, out _);
        }

        /// <summary>
        /// Derives the fallback id from the profile's asset identity, or returns false when the profile is not a
        /// project asset.
        /// </summary>
        public static bool TryDeriveFromAsset(ApaPartProfile profile, out string partId)
        {
            partId = string.Empty;
            if (profile == null) return false;

            // A profile that is not a project asset has no GUID to derive from. Checking persistence first also
            // keeps the asset-database call off an object it does not apply to.
            if (!EditorUtility.IsPersistent(profile)) return false;

            // The typed overload reports both the asset GUID and the local file id, which is what makes a profile
            // stored as a sub-asset distinguishable from the main asset of the same file.
            string guid;
            long localFileId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(profile, out guid, out localFileId)) return false;
            if (string.IsNullOrEmpty(guid)) return false;

            partId = DeriveFromAssetIdentity(guid, localFileId);
            return true;
        }

        /// <summary>
        /// The pure derivation: a deterministic identifier for an asset identity.
        /// </summary>
        /// <remarks>
        /// The local file id is appended only when it is non-zero, so the common case (a profile stored as the
        /// main asset of its own file) produces the short, readable form and a sub-asset cannot collide with it.
        /// </remarks>
        public static string DeriveFromAssetIdentity(string guid, long localFileId)
        {
            if (string.IsNullOrEmpty(guid)) return string.Empty;

            var trimmed = guid.Trim();
            if (trimmed.Length == 0) return string.Empty;

            return localFileId != 0
                ? DerivedPrefix + trimmed + "-" + localFileId.ToString(CultureInfo.InvariantCulture)
                : DerivedPrefix + trimmed;
        }

        /// <summary>
        /// Persists a part id onto a profile that has none, under undo, and returns the id it wrote.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one write path for a missing id, and it is only reachable from an explicit author action
        /// (the installer inspector's <i>Repair Part Id</i> button). It writes the <i>derived</i> id rather than a
        /// fresh random one, so the identity the pipeline already used for the part does not change when the
        /// author repairs the asset.
        /// </para>
        /// <para>
        /// A profile that already carries an id is left untouched and reported as success: the caller asked for
        /// "this profile has an id", and it does.
        /// </para>
        /// </remarks>
        /// <param name="profile">The profile asset to repair. Must be a project asset.</param>
        /// <param name="partId">Receives the id the profile carries after the call.</param>
        /// <param name="failureReason">
        /// On failure, a stable token: <c>no-profile</c>, <c>no-asset-guid</c>, or <c>not-persistent</c>.
        /// </param>
        /// <returns>True when the profile carries a stored id after the call.</returns>
        public static bool TryRepair(ApaPartProfile profile, out string partId, out string failureReason)
        {
            partId = string.Empty;
            failureReason = string.Empty;

            if (profile == null)
            {
                failureReason = "no-profile";
                return false;
            }

            if (HasStoredPartId(profile))
            {
                partId = profile.IdentityOrNull.PartId;
                return true;
            }

            // A profile that is not a project asset cannot be persisted; writing to it would produce an id that
            // disappears with the object.
            if (!EditorUtility.IsPersistent(profile))
            {
                failureReason = "not-persistent";
                return false;
            }

            if (!TryDeriveFromAsset(profile, out partId))
            {
                failureReason = "no-asset-guid";
                return false;
            }

            Undo.RecordObject(profile, "Repair Avatar Part Id");

            // EnsureInitialized materializes a missing identity object, which is exactly the state this repair
            // exists for. It is called here — an explicit author action under undo — and never from a read path.
            profile.EnsureInitialized();
            profile.Identity.PartId = partId;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return true;
        }
    }
}

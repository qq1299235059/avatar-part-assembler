using System;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// What reading a profile asset into an authoring window produced.
    /// </summary>
    /// <remarks>
    /// The result carries the whole profile-owned state and nothing else: the draft the asset defines, the two
    /// paths the window writes to, whether the asset needed its stable id derived, and what the armature restore
    /// did. A caller that assigns exactly these values has replaced everything the profile owns, which is what
    /// makes loading deterministic.
    /// </remarks>
    public sealed class ApaProfileLoadResult
    {
        internal ApaProfileLoadResult(
            ApaProfileDraft draft,
            string profilePath,
            string prefabPath,
            bool needsStablePartId,
            string restoredArmatures)
        {
            Draft = draft;
            ProfilePath = profilePath ?? string.Empty;
            PrefabPath = prefabPath ?? string.Empty;
            NeedsStablePartId = needsStablePartId;
            RestoredArmatures = restoredArmatures ?? string.Empty;
        }

        /// <summary>The draft the asset defines. Always a fresh object, never the previous draft.</summary>
        public ApaProfileDraft Draft { get; }

        /// <summary>Project-relative path of the loaded asset.</summary>
        public string ProfilePath { get; }

        /// <summary>The prefab path the loaded profile's part root suggests.</summary>
        public string PrefabPath { get; }

        /// <summary>True when the asset carried no stored part id, so the draft derived one.</summary>
        public bool NeedsStablePartId { get; }

        /// <summary>A description of the armatures the live selection was restored from, or empty.</summary>
        public string RestoredArmatures { get; }
    }

    /// <summary>
    /// Reads a profile asset into the state an authoring window shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One transition, not two.</b> The window loads a profile from two places — the
    /// <c>Load Existing Profile</c> field and the installer inspector's <c>Open Part Authoring</c> shortcut — and
    /// both must produce the same state. The transition therefore lives here rather than being spelled twice:
    /// the draft is replaced from the asset, the stable id is ensured, the live armature references are restored
    /// from <i>the loaded profile's</i> recorded paths with replacement, and the prefab path is derived from the
    /// part root the selection currently holds.
    /// </para>
    /// <para>
    /// <b>Isolation.</b> Everything returned is built from the asset. Nothing is carried over from a previous
    /// draft: the draft is a fresh object, the paths come from the asset and the live selection, and the armature
    /// restore replaces both live references — including clearing one whose recorded path no longer resolves,
    /// which is left in the draft for validation to report rather than being answered with the previous
    /// profile's object. The window's own derived state (resolved candidates, mesh arrays, merge-check
    /// classification, status lines) is not touched here because it is not profile-owned; the window drops it in
    /// one place when it applies this result.
    /// </para>
    /// <para>
    /// <b>The asset is only read.</b> Nothing here writes, dirties, or replaces the profile.
    /// </para>
    /// </remarks>
    public static class ApaProfileLoader
    {
        /// <summary>
        /// Builds the state a window shows after loading a profile.
        /// </summary>
        /// <param name="asset">The profile to read. Never null.</param>
        /// <param name="selection">
        /// The live scene selection the armature paths are resolved against, or null when there is none. A null
        /// selection still yields the loaded draft; only the live references cannot be restored.
        /// </param>
        /// <param name="profileAssetPath">Project-relative path of the asset, as the caller resolved it.</param>
        public static ApaProfileLoadResult Load(
            ApaPartProfile asset,
            ApaAuthoringSelection selection,
            string profileAssetPath)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            // Read before the draft is built: the repair verdict describes the asset, not the draft.
            var needsStablePartId = ApaPartIdentityResolver.NeedsRepair(asset);

            var draft = ApaProfileDraft.FromProfile(asset);
            draft.EnsureStablePartId();

            // The paths only exist from this moment on, so the restore runs after the draft was built and before
            // anything can re-record an empty selection over them. Replacement mode is what keeps the previous
            // profile's live armatures out of the newly loaded one.
            var restored = selection != null
                ? selection.RestoreArmatures(
                    draft.Bones.TargetArmaturePath,
                    draft.Bones.PartArmaturePath,
                    true)
                : string.Empty;

            var partName = selection != null && selection.PartRoot != null
                ? selection.PartRoot.name
                : "AvatarPart";

            var prefabPath = ApaAuthoringAssetPaths.DefaultPrefabPath(
                selection != null ? selection.OutputFolder : ApaAuthoringAssetPaths.DefaultOutputFolder,
                partName);

            return new ApaProfileLoadResult(
                draft,
                profileAssetPath,
                prefabPath,
                needsStablePartId,
                restored);
        }
    }
}

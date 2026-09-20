using System;
using UnityEngine;

namespace AvatarPartAssembler
{
    /// <summary>
    /// The authored description of one modular avatar part: what it replaces, how its seam matches, and how
    /// its UV and material semantics merge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is the authoring contract and lives in the Runtime assembly. It must remain usable without
    /// any Editor dependency so that a built part prefab is self-describing.
    /// </para>
    /// <para>
    /// Authoring data is versioned by <see cref="SchemaVersion"/>. Two version numbers exist and are never
    /// conflated: the schema version describes the shape of the serialized data, while the package version
    /// describes the shipped build. A profile whose schema version is newer than this build's
    /// <see cref="CurrentSchemaVersion"/> is rejected with <see cref="ApaErrorCode.UnknownProfileSchema"/>.
    /// </para>
    /// </remarks>
    [CreateAssetMenu(
        fileName = "AvatarPartProfile",
        menuName = "Avatar Part Assembler/Part Profile",
        order = 0)]
    public sealed class ApaPartProfile : ScriptableObject
    {
        /// <summary>
        /// The schema version this build writes and understands. Increment when the serialized shape changes
        /// in a way that older readers cannot interpret.
        /// </summary>
        /// <remarks>
        /// <para><b>Version history.</b></para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>1</b> — initial shape. Removal was stored as a flat <c>int[]</c> of triangle indices with no
        /// submesh component, and the compatibility signature carried only index counts and blend shape names.
        /// </description></item>
        /// <item><description>
        /// <b>2</b> — removal is an explicit <see cref="RemovedTriangleAddress"/> set of (submesh, triangle)
        /// pairs, and the compatibility signature additionally carries per-submesh topology, blend shape frame
        /// counts, and a deterministic bone signature.
        /// </description></item>
        /// <item><description>
        /// <b>3</b> — multi-part policy: <see cref="ApaPartIdentity.ConflictPriority"/>,
        /// <see cref="ApaPartIdentity.SlotMode"/>, and the serialized bone merge name policy
        /// (<see cref="ApaBoneProfile.MergePrefix"/>, <see cref="ApaBoneProfile.MergeSuffix"/>,
        /// <see cref="ApaBoneProfile.InferMergeNames"/>). Every added field defaults to the version-2 behaviour,
        /// so the 2 → 3 migration is a no-op accept rather than a rewrite.
        /// </description></item>
        /// <item><description>
        /// <b>4</b> — explicit Armature pairing and explicit seam pairing. <see cref="ApaBoneProfile"/> gains
        /// <see cref="ApaBoneProfile.TargetArmaturePath"/> and <see cref="ApaBoneProfile.PartArmaturePath"/>, and
        /// a bone's identity becomes its path relative to its own selected armature root instead of its path
        /// relative to the avatar root; <see cref="ApaSeamProfile.PairingVersion"/> records whether the two seam
        /// lists are pairs by position (<see cref="ApaSeamProfile.ExplicitPairingVersion"/>) or the pre-M10
        /// unordered sets (<see cref="ApaSeamProfile.LegacyUnpairedVersion"/>). Both added fields default to
        /// "not declared", so the 3 → 4 migration is a no-op accept; what changes is that the build now
        /// <i>refuses</i> the states it used to guess at (<c>APA042</c>, <c>APA043</c>) instead of resolving them
        /// from a heuristic. This is why the version is bumped rather than the fields being added quietly: an
        /// older build reading a version-4 profile must block with
        /// <see cref="ApaErrorCode.UnknownProfileSchema"/> rather than fall back to the removed position
        /// matching, which would pair a version-4 seam by a rule its author never used.
        /// </description></item>
        /// <item><description>
        /// <b>5</b> — the target compatibility profile records a deterministic mesh-content fingerprint, and the
        /// part records the same fingerprint for its own source mesh. The fingerprints make a reimport that keeps
        /// an asset GUID but changes vertex attributes, skin weights, UVs, or blend-shape deltas fail closed. The
        /// fields default to empty when a version-4 asset is read; that asset remains readable but is refused by
        /// compatibility validation until the author captures the current meshes again.
        /// </description></item>
        /// </list>
        /// </remarks>
        public const int CurrentSchemaVersion = 5;

        /// <summary>
        /// The oldest schema version this build can read. Older profiles are rejected rather than migrated,
        /// because the data they are missing cannot be recovered without guessing. See <see cref="TryMigrate"/>.
        /// </summary>
        public const int MinimumMigratableSchemaVersion = 2;

        [SerializeField] private int _schemaVersion = CurrentSchemaVersion;
        [SerializeField] private ApaPartIdentity _identity = new ApaPartIdentity();
        [SerializeField] private ApaAvatarCompatibilityProfile _compatibility = new ApaAvatarCompatibilityProfile();
        [SerializeField] private string _partMeshFingerprint = string.Empty;
        [SerializeField] private ApaRemovalProfile _removal = new ApaRemovalProfile();
        [SerializeField] private ApaSeamProfile _seam = new ApaSeamProfile();
        [SerializeField] private ApaUvChannelSemantic[] _uvSemantics = Array.Empty<ApaUvChannelSemantic>();
        [SerializeField] private ApaMaterialSlotSemantic[] _materialSemantics = Array.Empty<ApaMaterialSlotSemantic>();
        [SerializeField] private ApaBoneProfile _bones = new ApaBoneProfile();
        [SerializeField] private ApaBlendShapeProfile _blendShapes = new ApaBlendShapeProfile();

        /// <summary>
        /// Shape version of the serialized data. Set to <see cref="CurrentSchemaVersion"/> for newly created
        /// profiles.
        /// </summary>
        public int SchemaVersion
        {
            get => _schemaVersion;
            set => _schemaVersion = value;
        }

        /// <summary>Stable part identity. Legacy display/slot policy fields are ignored.</summary>
        public ApaPartIdentity Identity
        {
            get => _identity ?? (_identity = new ApaPartIdentity());
            set => _identity = value ?? new ApaPartIdentity();
        }

        /// <summary>The target body mesh signature this part was authored against.</summary>
        public ApaAvatarCompatibilityProfile Compatibility
        {
            get => _compatibility ?? (_compatibility = new ApaAvatarCompatibilityProfile());
            set => _compatibility = value ?? new ApaAvatarCompatibilityProfile();
        }

        /// <summary>
        /// Deterministic content fingerprint of the part mesh used when this profile was authored. Empty means
        /// the profile predates schema 5 or the author has not captured the part mesh yet.
        /// </summary>
        /// <remarks>
        /// The target body's fingerprint lives on <see cref="ApaAvatarCompatibilityProfile.MeshFingerprint"/>;
        /// this companion value covers the other mesh whose vertex, UV, skinning, and blend-shape indices are
        /// consumed by the profile. A mismatch is never repaired by replacing the stored value during a build.
        /// </remarks>
        public string PartMeshFingerprint
        {
            get => _partMeshFingerprint ?? string.Empty;
            set => _partMeshFingerprint = value ?? string.Empty;
        }

        /// <summary>True when a part-mesh fingerprint was captured.</summary>
        public bool HasPartMeshFingerprint => !string.IsNullOrEmpty(_partMeshFingerprint);

        /// <summary>The base body triangles this part removes.</summary>
        public ApaRemovalProfile Removal
        {
            get => _removal ?? (_removal = new ApaRemovalProfile());
            set => _removal = value ?? new ApaRemovalProfile();
        }

        /// <summary>The base and part seam loops.</summary>
        public ApaSeamProfile Seam
        {
            get => _seam ?? (_seam = new ApaSeamProfile());
            set => _seam = value ?? new ApaSeamProfile();
        }

        /// <summary>UV channel semantics declared by the part's own mesh.</summary>
        public ApaUvChannelSemantic[] UvSemantics
        {
            get => _uvSemantics ?? Array.Empty<ApaUvChannelSemantic>();
            set => _uvSemantics = value ?? Array.Empty<ApaUvChannelSemantic>();
        }

        /// <summary>Material slot semantics declared by the part's own mesh.</summary>
        public ApaMaterialSlotSemantic[] MaterialSemantics
        {
            get => _materialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>();
            set => _materialSemantics = value ?? Array.Empty<ApaMaterialSlotSemantic>();
        }

        /// <summary>Bone merging configuration. Consumed by M3; M2 uses only the captured bone data.</summary>
        public ApaBoneProfile Bones
        {
            get => _bones ?? (_bones = new ApaBoneProfile());
            set => _bones = value ?? new ApaBoneProfile();
        }

        /// <summary>Blend shape policy. The strict rules are always enforced; this only widens what is allowed.</summary>
        public ApaBlendShapeProfile BlendShapes
        {
            get => _blendShapes ?? (_blendShapes = new ApaBlendShapeProfile());
            set => _blendShapes = value ?? new ApaBlendShapeProfile();
        }

        /// <summary>
        /// True when this profile's schema version is at or below what this build understands.
        /// </summary>
        public bool IsSchemaSupported => _schemaVersion <= CurrentSchemaVersion;

        /// <summary>
        /// Applies forward migrations so that a profile authored by an older build can be read by this one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Migration must never invent authoring data. Where a field genuinely cannot be recovered, the correct
        /// outcome is a refusal, not a guess.
        /// </para>
        /// <para>
        /// <b>The 1 → 2 migration is intentionally absent.</b> Schema version 1 stored removal as a flat
        /// <c>int[]</c> of triangle indices, and the same integer can mean different triangles in different
        /// submeshes. Resolving one to an address would require choosing a submesh the author never recorded, so
        /// every choice would be a guess about which body region to delete. A wrong guess removes geometry
        /// silently, which is the exact failure the product principle forbids, so version-1 profiles are
        /// rejected and must be re-authored. Version-1 profiles also lack the topology, frame-count, and bone
        /// data needed to verify anything they reference.
        /// </para>
        /// <para>
        /// <b>The 2 → 3 migration is an explicit no-op accept.</b> Schema version 3 adds conflict priority, the
        /// slot mode, and the serialized merge name policy; every one of those fields defaults to the version-2
        /// behaviour. A version-2 profile therefore means exactly the same thing under this build, and this
        /// method deliberately writes nothing: the pipeline may be reading a <c>ScriptableObject</c> asset that
        /// is shared with the authoring scene, and rewriting a version number on read is a hidden, non-undoable
        /// mutation of the author's asset. A version-3 profile read by an older build blocks with
        /// <see cref="ApaErrorCode.UnknownProfileSchema"/>, which is the fail-closed direction: an older build
        /// cannot honour a declared policy it does not understand.
        /// </para>
        /// <para>
        /// <b>The 3 → 4 migration is the same no-op accept, with one deliberate consequence.</b> The armature
        /// paths and the seam pairing version are absent from a version-3 asset and read as their defaults, which
        /// mean "not declared". Nothing is written, and the profile still loads, opens, and round-trips. What it
        /// cannot do is build: a profile with no armature selection is refused with <c>APA043</c> and one with an
        /// unpaired legacy seam is refused with <c>APA042</c>, each naming the single authoring action that fixes
        /// it. Accepting the version while refusing the two states the removed heuristics used to resolve is the
        /// intended behaviour: the alternative — quietly re-deriving the pairing and the bone identity from
        /// avatar-root paths — is exactly the guessing M10 exists to remove.
        /// </para>
        /// <para>
        /// <b>The 4 → 5 migration is also a no-op accept.</b> Version-4 assets have no mesh-content fingerprints,
        /// and a read must not invent one or rewrite the shared asset. They remain loadable for inspection, but
        /// compatibility validation refuses them with <c>APA047</c> until the author captures a schema-5 profile.
        /// A schema-5 profile records the target fingerprint and, when authored through the current window, the
        /// part fingerprint as well; a present fingerprint that no longer matches is reported as <c>APA048</c>.
        /// </para>
        /// </remarks>
        /// <returns>True when the profile is readable after migration; false when it cannot be migrated.</returns>
        public bool TryMigrate(out string message)
        {
            if (_schemaVersion > CurrentSchemaVersion)
            {
                message = "Profile schema version " + _schemaVersion +
                          " is newer than the supported version " + CurrentSchemaVersion +
                          ". Update the Avatar Part Assembler package, or re-author this profile.";
                return false;
            }

            if (_schemaVersion < MinimumMigratableSchemaVersion)
            {
                message = "Profile schema version " + _schemaVersion + " predates version " +
                          MinimumMigratableSchemaVersion + ", which is the oldest this build can read. Version 1 " +
                          "stored removal as flat triangle indices with no submesh, and the compatibility " +
                          "signature did not carry submesh topology, blend shape frame counts, or a bone " +
                          "signature. Neither can be reconstructed without guessing, so this profile must be " +
                          "re-authored against the target body.";
                return false;
            }

            // Versions 2 through 5 are all readable as-is. The loop that used to step the version number here
            // wrote to the asset; there is nothing to write, because every later schema field defaults to the
            // earlier behaviour. Keeping the profile's own version intact is what makes the read lossless.
            message = string.Empty;
            return true;
        }

        /// <summary>
        /// Ensures every nullable collection and nested object is materialized. Called after deserialization
        /// because Unity does not run field initializers on data loaded from disk.
        /// </summary>
        /// <remarks>
        /// <b>Authoring-only.</b> This method assigns to the asset and must not be called from the build or
        /// preview pipeline: the profile is a <c>ScriptableObject</c> asset shared with the authoring scene, and a
        /// write during a build is invisible, non-undoable and can be persisted by an unrelated
        /// <c>AssetDatabase.SaveAssets()</c>. Pipeline code reads the non-mutating <c>*OrNull</c> accessors
        /// instead.
        /// </remarks>
        public void EnsureInitialized()
        {
            if (_identity == null) _identity = new ApaPartIdentity();
            if (_compatibility == null) _compatibility = new ApaAvatarCompatibilityProfile();
            if (_removal == null) _removal = new ApaRemovalProfile();
            if (_seam == null) _seam = new ApaSeamProfile();
            if (_bones == null) _bones = new ApaBoneProfile();
            if (_blendShapes == null) _blendShapes = new ApaBlendShapeProfile();
            if (_uvSemantics == null) _uvSemantics = Array.Empty<ApaUvChannelSemantic>();
            if (_materialSemantics == null) _materialSemantics = Array.Empty<ApaMaterialSlotSemantic>();
        }

        /// <summary>
        /// The identity as serialized, or null when the asset carries none. Never assigns, so a build-time read
        /// cannot mutate the shared asset.
        /// </summary>
        /// <remarks>
        /// A null identity means the profile was never authored (or was authored by a tool that wrote no
        /// identity object). Callers must treat it as "no identity" and report it, never materialize it.
        /// </remarks>
        public ApaPartIdentity IdentityOrNull => _identity;

        /// <summary>The compatibility signature as serialized, or null when the asset carries none.</summary>
        public ApaAvatarCompatibilityProfile CompatibilityOrNull => _compatibility;

        /// <summary>The removal profile as serialized, or null when the asset carries none.</summary>
        public ApaRemovalProfile RemovalOrNull => _removal;

        /// <summary>The seam profile as serialized, or null when the asset carries none.</summary>
        public ApaSeamProfile SeamOrNull => _seam;

        /// <summary>The bone profile as serialized, or null when the asset carries none.</summary>
        public ApaBoneProfile BonesOrNull => _bones;

        /// <summary>The blend shape policy as serialized, or null when the asset carries none.</summary>
        public ApaBlendShapeProfile BlendShapesOrNull => _blendShapes;

        /// <summary>Assigns a new stable part id when the profile does not have one yet.</summary>
        public void EnsureStablePartId()
        {
            Identity.EnsureStablePartId();
        }
    }
}

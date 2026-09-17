using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The authoring state of one part: everything the authoring window edits and everything the profile writer
    /// serializes, held as plain serializable data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The draft is <b>not</b> a <see cref="ApaPartProfile"/> instance. It deliberately holds no asset, so that:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Editing can never mutate a saved profile. The only way author data reaches disk is an explicit write
    /// through <see cref="ApaProfileWriter"/>.
    /// </description></item>
    /// <item><description>
    /// The draft survives Unity's undo and assembly-reload serialization as a field of the editor window,
    /// because every member is either a serializable value, a serializable Runtime type, or a
    /// <see cref="UnityEngine.Object"/> reference — a non-persistent <c>ScriptableObject</c> working copy would
    /// not.
    /// </description></item>
    /// <item><description>
    /// Validation, writing, and prefab generation receive the same immutable-by-convention description of the
    /// part and cannot accidentally depend on an in-memory asset's identity.
    /// </description></item>
    /// </list>
    /// <para>
    /// <see cref="Materialize"/> builds a transient profile for the core to validate and for the writer to copy
    /// into an asset. The caller owns the returned object and must destroy it.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaProfileDraft
    {
        [SerializeField] private ApaPartIdentity _identity = new ApaPartIdentity();
        [SerializeField] private ApaAvatarCompatibilityProfile _compatibility = new ApaAvatarCompatibilityProfile();
        [SerializeField] private ApaRemovalMask _removal = new ApaRemovalMask();
        [SerializeField] private ApaSeamSelection _seam = new ApaSeamSelection();
        [SerializeField] private ApaUvChannelSemantic[] _uvSemantics = Array.Empty<ApaUvChannelSemantic>();
        [SerializeField] private ApaMaterialSlotSemantic[] _materialSemantics = Array.Empty<ApaMaterialSlotSemantic>();
        [SerializeField] private ApaBoneProfile _bones = new ApaBoneProfile();
        [SerializeField] private ApaBlendShapeProfile _blendShapes = new ApaBlendShapeProfile();

        /// <summary>Stable identity, display name, and declared slot.</summary>
        public ApaPartIdentity Identity
        {
            get => _identity ?? (_identity = new ApaPartIdentity());
            set => _identity = value ?? new ApaPartIdentity();
        }

        /// <summary>Captured schema-v2 compatibility signature of the selected target body mesh.</summary>
        public ApaAvatarCompatibilityProfile Compatibility
        {
            get => _compatibility ?? (_compatibility = new ApaAvatarCompatibilityProfile());
            set => _compatibility = value ?? new ApaAvatarCompatibilityProfile();
        }

        /// <summary>The editable removal triangle set.</summary>
        public ApaRemovalMask Removal
        {
            get => _removal ?? (_removal = new ApaRemovalMask());
            set => _removal = value ?? new ApaRemovalMask();
        }

        /// <summary>The editable paired seam loops.</summary>
        public ApaSeamSelection Seam
        {
            get => _seam ?? (_seam = new ApaSeamSelection());
            set => _seam = value ?? new ApaSeamSelection();
        }

        /// <summary>UV semantics declared by the part's own mesh, in author order.</summary>
        public ApaUvChannelSemantic[] UvSemantics
        {
            get => _uvSemantics ?? Array.Empty<ApaUvChannelSemantic>();
            set => _uvSemantics = value ?? Array.Empty<ApaUvChannelSemantic>();
        }

        /// <summary>Material slot semantics declared by the part's own mesh, in author order.</summary>
        public ApaMaterialSlotSemantic[] MaterialSemantics
        {
            get => _materialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>();
            set => _materialSemantics = value ?? Array.Empty<ApaMaterialSlotSemantic>();
        }

        /// <summary>Bone merge policy: whether to merge, into what, and the merge-name policy the build applies.</summary>
        public ApaBoneProfile Bones
        {
            get => _bones ?? (_bones = new ApaBoneProfile());
            set => _bones = value ?? new ApaBoneProfile();
        }

        /// <summary>Blend shape policy.</summary>
        public ApaBlendShapeProfile BlendShapes
        {
            get => _blendShapes ?? (_blendShapes = new ApaBlendShapeProfile());
            set => _blendShapes = value ?? new ApaBlendShapeProfile();
        }

        /// <summary>True when a stable part id has been assigned to this draft.</summary>
        public bool HasStablePartId => !string.IsNullOrEmpty(Identity.PartId);

        /// <summary>Creates an empty draft with a fresh stable part id.</summary>
        public static ApaProfileDraft New(string displayName, ApaPartSlot slot)
        {
            var draft = new ApaProfileDraft();
            draft.Identity.DisplayName = displayName ?? string.Empty;
            draft.Identity.Slot = slot;
            draft.Identity.EnsureStablePartId();
            return draft;
        }

        /// <summary>
        /// Reads a saved profile into a new draft. The saved asset is only read, never written, and the draft
        /// receives an independent copy of every array.
        /// </summary>
        public static ApaProfileDraft FromProfile(ApaPartProfile profile)
        {
            var draft = new ApaProfileDraft();
            draft.SetFromProfile(profile);
            return draft;
        }

        /// <summary>
        /// Replaces this draft's contents with a saved profile's contents, in place, so an existing window
        /// selection keeps working.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The schema version is not copied: the draft always writes the schema version this build understands.
        /// A profile with a newer schema is rejected before it reaches this method.
        /// </para>
        /// <para>
        /// <b>Loading a profile does not write it.</b> The profile is a shared authoring asset, so every read
        /// goes through the non-mutating <c>*OrNull</c> accessors and a missing nested object is treated as "the
        /// author never set it", not as something to materialize onto the asset. <c>EnsureInitialized</c> assigns
        /// to the asset and is reserved for the draft's own transient profile
        /// (<see cref="Materialize"/>); calling it here would make merely opening a profile a hidden,
        /// non-undoable write that an unrelated <c>AssetDatabase.SaveAssets()</c> could persist.
        /// </para>
        /// <para>
        /// <b>A profile with no part id gets one in the draft (M11).</b> The id proposed is the one the build
        /// already derives from the profile asset's GUID (<see cref="ApaPartIdentityResolver"/>), so saving the
        /// profile stores the identity the pipeline has been using rather than replacing it with a new one.
        /// A profile that is not an asset at all (a transient instance) falls back to a fresh id, which is what
        /// the writer would have assigned anyway.
        /// </para>
        /// </remarks>
        public void SetFromProfile(ApaPartProfile profile)
        {
            if (profile == null)
            {
                Reset();
                return;
            }

            var identity = profile.IdentityOrNull;
            var partId = identity != null ? identity.PartId : string.Empty;
            if (string.IsNullOrEmpty(partId))
            {
                partId = ApaPartIdentityResolver.ResolvePartId(profile);
                if (string.IsNullOrEmpty(partId)) partId = Guid.NewGuid().ToString("N");
            }

            Identity = new ApaPartIdentity
            {
                PartId = partId,
                DisplayName = identity != null ? identity.DisplayName : string.Empty,
                Slot = identity != null ? identity.Slot : ApaPartSlot.Custom,
                SlotMode = identity != null ? identity.SlotMode : ApaPartSlotMode.Replace,
                ConflictPriority = identity != null ? identity.ConflictPriority : 0
            };

            Compatibility = CopyCompatibility(profile.CompatibilityOrNull);
            Removal = ApaRemovalMask.FromRemovalProfile(profile.RemovalOrNull);
            Seam = ApaSeamSelection.FromSeamProfile(profile.SeamOrNull);
            UvSemantics = CopyUv(profile.UvSemantics);
            MaterialSemantics = CopyMaterials(profile.MaterialSemantics);

            // A profile without a bone object means "no policy", which is the schema-2 default: merge the
            // armature, match bone names exactly, infer nothing. The M10 armature selections default to empty,
            // which means "not selected": the build refuses that state rather than deriving one.
            var bones = profile.BonesOrNull;
            Bones = new ApaBoneProfile
            {
                MergeArmature = bones == null || bones.MergeArmature,
                MergeTargetPath = bones != null ? bones.MergeTargetPath : string.Empty,
                MergePrefix = bones != null ? bones.MergePrefix : string.Empty,
                MergeSuffix = bones != null ? bones.MergeSuffix : string.Empty,
                InferMergeNames = bones != null && bones.InferMergeNames,
                TargetArmaturePath = bones != null ? bones.TargetArmaturePath : string.Empty,
                PartArmaturePath = bones != null ? bones.PartArmaturePath : string.Empty
            };

            var blendShapes = profile.BlendShapesOrNull;
            BlendShapes = new ApaBlendShapeProfile
            {
                AllowPartOnlyShapes = blendShapes == null || blendShapes.AllowPartOnlyShapes
            };
        }

        /// <summary>Clears every field back to an empty draft, keeping no identity.</summary>
        public void Reset()
        {
            _identity = new ApaPartIdentity();
            _compatibility = new ApaAvatarCompatibilityProfile();
            _removal = new ApaRemovalMask();
            _seam = new ApaSeamSelection();
            _uvSemantics = Array.Empty<ApaUvChannelSemantic>();
            _materialSemantics = Array.Empty<ApaMaterialSlotSemantic>();
            _bones = new ApaBoneProfile();
            _blendShapes = new ApaBlendShapeProfile();
        }

        /// <summary>Materializes every nullable member. Called after deserialization and before any read.</summary>
        public void EnsureInitialized()
        {
            if (_identity == null) _identity = new ApaPartIdentity();
            if (_compatibility == null) _compatibility = new ApaAvatarCompatibilityProfile();
            if (_removal == null) _removal = new ApaRemovalMask();
            if (_seam == null) _seam = new ApaSeamSelection();
            if (_uvSemantics == null) _uvSemantics = Array.Empty<ApaUvChannelSemantic>();
            if (_materialSemantics == null) _materialSemantics = Array.Empty<ApaMaterialSlotSemantic>();
            if (_bones == null) _bones = new ApaBoneProfile();
            if (_blendShapes == null) _blendShapes = new ApaBlendShapeProfile();

            for (var i = 0; i < _uvSemantics.Length; i++)
            {
                if (_uvSemantics[i] == null) _uvSemantics[i] = new ApaUvChannelSemantic();
            }

            for (var i = 0; i < _materialSemantics.Length; i++)
            {
                if (_materialSemantics[i] == null) _materialSemantics[i] = new ApaMaterialSlotSemantic();
            }
        }

        /// <summary>Assigns a stable part id when the draft does not have one yet.</summary>
        public bool EnsureStablePartId()
        {
            return Identity.EnsureStablePartId();
        }

        /// <summary>
        /// Builds a transient profile carrying this draft. The caller owns the returned object and must destroy
        /// it with <c>UnityEngine.Object.DestroyImmediate</c>.
        /// </summary>
        public ApaPartProfile Materialize()
        {
            EnsureInitialized();

            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;
            profile.SchemaVersion = ApaPartProfile.CurrentSchemaVersion;
            profile.Identity = new ApaPartIdentity
            {
                PartId = Identity.PartId,
                DisplayName = Identity.DisplayName,
                Slot = Identity.Slot,
                SlotMode = Identity.SlotMode,
                ConflictPriority = Identity.ConflictPriority
            };
            profile.Compatibility = CopyCompatibility(Compatibility);
            profile.Removal = Removal.ToRemovalProfile();
            profile.Seam = Seam.ToSeamProfile();
            profile.UvSemantics = CopyUv(UvSemantics);
            profile.MaterialSemantics = CopyMaterials(MaterialSemantics);
            profile.Bones = new ApaBoneProfile
            {
                MergeArmature = Bones.MergeArmature,
                MergeTargetPath = Bones.MergeTargetPath,
                MergePrefix = Bones.MergePrefix,
                MergeSuffix = Bones.MergeSuffix,
                InferMergeNames = Bones.InferMergeNames,
                TargetArmaturePath = Bones.TargetArmaturePath,
                PartArmaturePath = Bones.PartArmaturePath
            };
            profile.BlendShapes = new ApaBlendShapeProfile
            {
                AllowPartOnlyShapes = BlendShapes.AllowPartOnlyShapes
            };

            return profile;
        }

        /// <summary>
        /// Writes the two armature selections into the bone policy (M10).
        /// </summary>
        /// <param name="avatarRoot">The avatar root the target path is recorded against.</param>
        /// <param name="targetArmature">
        /// Armature inside the avatar, or null. Recorded as its path relative to the avatar root, with the avatar
        /// root itself recorded as <see cref="ApaAvatarPath.Root"/>.
        /// </param>
        /// <param name="partRoot">The part root the part path is recorded against.</param>
        /// <param name="partArmature">
        /// Armature inside the part, or null. Recorded as its path relative to the part root, with the part root
        /// itself recorded as <see cref="ApaAvatarPath.Root"/>.
        /// </param>
        /// <remarks>
        /// <para>
        /// A null armature clears the corresponding path, which is the documented "not selected" state; a null
        /// root likewise cannot record a path and clears it rather than inventing one. The two are written
        /// together because they are one decision: a bone identity only exists as a path relative to the
        /// armature on its own side, so a half-selection cannot be consumed and the build reports it as
        /// <c>APA043</c>.
        /// </para>
        /// <para>
        /// The roots are passed rather than stored: the draft deliberately holds no scene reference, so only the
        /// two strings become part of it, which is what keeps the draft serializable and the profile portable.
        /// </para>
        /// </remarks>
        public void SetArmatures(
            Transform avatarRoot,
            Transform targetArmature,
            Transform partRoot,
            Transform partArmature)
        {
            EnsureInitialized();

            Bones.TargetArmaturePath = targetArmature != null && avatarRoot != null
                ? MeshSnapshotFactory.RelativePath(avatarRoot, targetArmature)
                : string.Empty;

            Bones.PartArmaturePath = partArmature != null && partRoot != null
                ? MeshSnapshotFactory.RelativePath(partRoot, partArmature)
                : string.Empty;
        }

        /// <summary>Clears both armature selections.</summary>
        public void ClearArmatures()
        {
            EnsureInitialized();
            Bones.TargetArmaturePath = string.Empty;
            Bones.PartArmaturePath = string.Empty;
        }

        // ---- Seam editing ----------------------------------------------------------------------------

        /// <summary>
        /// Replaces the seam with an explicit pairing generated from world positions (M10).
        /// </summary>
        /// <remarks>
        /// The two arrays are written in pair order through <see cref="ApaSeamSelection.SetPaired"/>, which also
        /// stamps the pairing version, so a profile can never hold paired lists that claim to be the legacy
        /// unordered form.
        /// </remarks>
        public void SetPairedSeam(int[] baseIndices, int[] partIndices)
        {
            EnsureInitialized();
            Seam.SetPaired(baseIndices, partIndices);
        }

        /// <summary>Clears the seam entirely, which is the documented "this part declares no seam" state.</summary>
        public void ClearSeam()
        {
            EnsureInitialized();
            Seam.Clear();
        }

        // ---- UV semantic editing ---------------------------------------------------------------------

        /// <summary>Adds an empty UV semantic row for the author to fill in.</summary>
        public int AddUvSemantic()
        {
            EnsureInitialized();
            var next = new ApaUvChannelSemantic(string.Empty, NextFreeUvChannel());
            var list = new List<ApaUvChannelSemantic>(UvSemantics) { next };
            UvSemantics = list.ToArray();
            return list.Count - 1;
        }

        /// <summary>Removes one UV semantic row.</summary>
        public void RemoveUvSemanticAt(int index)
        {
            EnsureInitialized();
            if (index < 0 || index >= UvSemantics.Length) return;

            var list = new List<ApaUvChannelSemantic>(UvSemantics);
            list.RemoveAt(index);
            UvSemantics = list.ToArray();
        }

        /// <summary>
        /// Moves a UV semantic row. The declaration order is what the resolver preserves for new part
        /// semantics, so the row order is author-visible behaviour rather than cosmetics.
        /// </summary>
        public void MoveUvSemantic(int from, int to)
        {
            EnsureInitialized();
            if (from < 0 || from >= UvSemantics.Length) return;
            if (to < 0 || to >= UvSemantics.Length || to == from) return;

            var list = new List<ApaUvChannelSemantic>(UvSemantics);
            var entry = list[from];
            list.RemoveAt(from);
            list.Insert(to, entry);
            UvSemantics = list.ToArray();
        }

        /// <summary>Replaces every UV semantic from a mesh's present channels.</summary>
        public void InferUvSemanticsFrom(MeshSnapshot partMesh)
        {
            UvSemantics = ApaCore.InferUvSemantics(partMesh);
        }

        // ---- Material semantic editing ---------------------------------------------------------------

        /// <summary>Adds an empty material semantic row for the author to fill in.</summary>
        public int AddMaterialSemantic()
        {
            EnsureInitialized();
            var next = new ApaMaterialSlotSemantic(string.Empty, NextFreeSubMesh(), null, ApaMaterialPolicyMode.Auto);
            var list = new List<ApaMaterialSlotSemantic>(MaterialSemantics) { next };
            MaterialSemantics = list.ToArray();
            return list.Count - 1;
        }

        /// <summary>Removes one material semantic row.</summary>
        public void RemoveMaterialSemanticAt(int index)
        {
            EnsureInitialized();
            if (index < 0 || index >= MaterialSemantics.Length) return;

            var list = new List<ApaMaterialSlotSemantic>(MaterialSemantics);
            list.RemoveAt(index);
            MaterialSemantics = list.ToArray();
        }

        /// <summary>Moves a material semantic row, preserving the declaration order.</summary>
        public void MoveMaterialSemantic(int from, int to)
        {
            EnsureInitialized();
            if (from < 0 || from >= MaterialSemantics.Length) return;
            if (to < 0 || to >= MaterialSemantics.Length || to == from) return;

            var list = new List<ApaMaterialSlotSemantic>(MaterialSemantics);
            var entry = list[from];
            list.RemoveAt(from);
            list.Insert(to, entry);
            MaterialSemantics = list.ToArray();
        }

        /// <summary>Replaces every material semantic from a renderer's material list.</summary>
        public void InferMaterialSemanticsFrom(int subMeshCount, Material[] materials)
        {
            MaterialSemantics = ApaCore.InferMaterialSemantics(subMeshCount, materials);
        }

        /// <summary>Sets the conflict policy of one material semantic row.</summary>
        public void SetMaterialPolicy(int index, ApaMaterialPolicyMode policy)
        {
            EnsureInitialized();
            if (index < 0 || index >= MaterialSemantics.Length) return;
            MaterialSemantics[index].Policy = policy;
        }

        private int NextFreeUvChannel()
        {
            var used = new bool[ApaMeshLimits.MaxUvChannels];
            for (var i = 0; i < UvSemantics.Length; i++)
            {
                var channel = UvSemantics[i].SourceChannel;
                if (channel >= 0 && channel < used.Length) used[channel] = true;
            }

            for (var i = 0; i < used.Length; i++)
            {
                if (!used[i]) return i;
            }

            return 0;
        }

        private int NextFreeSubMesh()
        {
            var used = new bool[MaterialSemantics.Length + 1];
            for (var i = 0; i < MaterialSemantics.Length; i++)
            {
                var subMesh = MaterialSemantics[i].SourceSubMesh;
                if (subMesh >= 0 && subMesh < used.Length) used[subMesh] = true;
            }

            for (var i = 0; i < used.Length; i++)
            {
                if (!used[i]) return i;
            }

            return MaterialSemantics.Length;
        }

        private static ApaAvatarCompatibilityProfile CopyCompatibility(ApaAvatarCompatibilityProfile source)
        {
            if (source == null) return new ApaAvatarCompatibilityProfile();

            return new ApaAvatarCompatibilityProfile
            {
                MeshName = source.MeshName,
                RendererPath = source.RendererPath,
                MeshGuid = source.MeshGuid,
                VertexCount = source.VertexCount,
                SubMeshIndexCounts = Copy(source.SubMeshIndexCounts),
                SubMeshTopologyValues = Copy(source.SubMeshTopologyValues),
                BlendShapeNames = Copy(source.BlendShapeNames),
                BlendShapeFrameCounts = Copy(source.BlendShapeFrameCounts),
                BonePaths = Copy(source.BonePaths),
                IsCaptured = source.IsCaptured
            };
        }

        private static ApaUvChannelSemantic[] CopyUv(ApaUvChannelSemantic[] source)
        {
            if (source == null || source.Length == 0) return Array.Empty<ApaUvChannelSemantic>();

            var result = new ApaUvChannelSemantic[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                result[i] = source[i] == null
                    ? new ApaUvChannelSemantic()
                    : new ApaUvChannelSemantic(source[i].Semantic, source[i].SourceChannel);
            }

            return result;
        }

        private static ApaMaterialSlotSemantic[] CopyMaterials(ApaMaterialSlotSemantic[] source)
        {
            if (source == null || source.Length == 0) return Array.Empty<ApaMaterialSlotSemantic>();

            var result = new ApaMaterialSlotSemantic[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                result[i] = source[i] == null
                    ? new ApaMaterialSlotSemantic()
                    : new ApaMaterialSlotSemantic(
                        source[i].Semantic,
                        source[i].SourceSubMesh,
                        source[i].Material,
                        source[i].Policy);
            }

            return result;
        }

        private static T[] Copy<T>(T[] source)
        {
            if (source == null || source.Length == 0) return Array.Empty<T>();
            var copy = new T[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }
    }
}

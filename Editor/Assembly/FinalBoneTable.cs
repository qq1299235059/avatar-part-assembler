using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// One bone of the final table: its stable identity, where it came from, and its final bind pose.
    /// </summary>
    public sealed class FinalBone
    {
        /// <summary>Index in the final table. This is the value written into a remapped <see cref="BoneWeight"/>.</summary>
        public int Index { get; }

        /// <summary>
        /// Armature-relative hierarchy path, the stable identity of the bone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A path rather than an object reference or an instance id: the table has to be reproducible on another
        /// machine and comparable against a serialized compatibility signature, and neither a reference nor an
        /// id survives either.
        /// </para>
        /// <para>
        /// <b>The path is relative to the armature selected for the bone's own side (M10).</b> A body bone's path
        /// is relative to the selected target armature and a part-only bone's path to the selected part armature,
        /// which is why <see cref="OwnerPartId"/> decides the scope a reader resolves the path under. A body bone
        /// — including one only a part weights — is therefore always resolved under the target armature.
        /// </para>
        /// <para>
        /// The armature root itself is a bone like any other and is identified by
        /// <see cref="ApaAvatarPath.Root"/> (<c>"."</c>). Only the empty string means "no identity", and no bone
        /// with an empty path is ever added to the table.
        /// </para>
        /// </remarks>
        public string Path { get; }

        /// <summary>Owning part id, or an empty string for a bone of the target body.</summary>
        public string OwnerPartId { get; }

        /// <summary>Index of this bone inside its owner's own bone list.</summary>
        public int SourceBoneIndex { get; }

        /// <summary>
        /// The final bind pose, computed as
        /// <c>sourceBindPose * sourceToTargetLocal.inverse</c> when the source mesh carries bind poses. This
        /// keeps an edited live pose from becoming a new bind pose. Legacy snapshots without source bind poses
        /// use <c>bone.worldToLocalMatrix * renderer.localToWorldMatrix</c> as a compatibility fallback.
        /// </summary>
        public Matrix4x4 BindPose { get; }

        /// <summary>
        /// True when this bone was merged onto an existing bone rather than appended.
        /// </summary>
        /// <remarks>
        /// Always false for a table this build produces. Since the target body became authoritative for a shared
        /// identity, a part bone never rewrites the entry it redirects onto, so no entry is ever re-created as
        /// "merged"; the property is kept because it is part of the published <see cref="FinalBone"/> contract
        /// and a reader that asks the question must get an answer rather than an exception.
        /// </remarks>
        public bool IsMerged { get; }

        /// <summary>Creates a final bone.</summary>
        public FinalBone(
            int index,
            string path,
            string ownerPartId,
            int sourceBoneIndex,
            Matrix4x4 bindPose,
            bool isMerged)
        {
            Index = index;
            Path = path ?? string.Empty;
            OwnerPartId = ownerPartId ?? string.Empty;
            SourceBoneIndex = sourceBoneIndex;
            BindPose = bindPose;
            IsMerged = isMerged;
        }

        /// <summary>Describes the bone for diagnostics.</summary>
        public override string ToString()
        {
            return "#" + Index + " '" + Path + "'" +
                   (string.IsNullOrEmpty(OwnerPartId) ? " (base)" : " (" + OwnerPartId + ")");
        }
    }

    /// <summary>
    /// The one bone table every retained vertex is skinned against, plus the per-source remap into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is built once, after the final hierarchy is known, and is immutable afterwards. Ordering is
    /// fixed by the product specification: every bone of the existing target body first, in the target's own
    /// bone order, then the bones new parts contribute, in stable part order and then source order. A part bone
    /// whose identity already exists in the table is <i>redirected</i> onto it rather than appended, which is
    /// what makes a part that reuses the avatar's armature deform continuously with it (section 19).
    /// </para>
    /// <para>
    /// <b>The target body is authoritative for a shared identity.</b> The existing entry keeps its index, its
    /// owner, and its bind pose; the part's own bind transform is discarded, never compared and never merged
    /// into the table. A part that was authored somewhere else in the scene therefore still deforms with the
    /// body's joint rather than with the transform it happened to have. The only entry a path can ever have is
    /// the target's, so a second part that names the same path cannot overwrite the bind pose either. Since M11
    /// this holds for every path the body <i>declares</i>, not only for one a body vertex weights: a part that
    /// reaches a declared but unweighted body bone gets the body's entry for it rather than one of its own.
    /// </para>
    /// <para>
    /// Remap tables are exposed as index-to-index arrays. A value of <c>-1</c> means "no final bone", which is
    /// only reachable for a source the build refused; a plan is never produced with an unresolved remap.
    /// </para>
    /// </remarks>
    public sealed class FinalBoneTable
    {
        private readonly ReadOnlyCollection<FinalBone> _bones;
        private readonly ReadOnlyCollection<int> _baseBoneRemap;
        private readonly ReadOnlyCollection<Matrix4x4> _bindPoses;
        private readonly ReadOnlyDictionary<string, IReadOnlyList<int>> _partBoneRemap;
        private readonly Dictionary<string, int> _indexByPath;

        /// <summary>The final bones, in final table order.</summary>
        public IReadOnlyList<FinalBone> Bones => _bones;

        /// <summary>Number of final bones.</summary>
        public int Count => _bones.Count;

        /// <summary>
        /// Maps each bone index of the target body's own bone list to a final index, or -1 when unresolved.
        /// </summary>
        public IReadOnlyList<int> BaseBoneRemap => _baseBoneRemap;

        /// <summary>
        /// Maps each part's own bone indices to final indices, keyed by part id.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<int>> PartBoneRemap => _partBoneRemap;

        /// <summary>
        /// The bind poses in final table order, ready to assign to the generated mesh.
        /// </summary>
        /// <remarks>
        /// A copy is handed out rather than the internal array because Unity's <c>Mesh.bindposes</c> setter
        /// stores the array it is given; a shared array would let the caller's mesh alias the immutable table.
        /// </remarks>
        public Matrix4x4[] BindPoseArray
        {
            get
            {
                var copy = new Matrix4x4[_bindPoses.Count];
                for (var i = 0; i < copy.Length; i++) copy[i] = _bindPoses[i];
                return copy;
            }
        }

        /// <summary>Number of bones contributed by the target body.</summary>
        public int BaseBoneCount { get; }

        /// <summary>Number of bones appended from parts, excluding merged bones.</summary>
        public int AppendedBoneCount => Count - BaseBoneCount;

        /// <summary>Creates a final bone table.</summary>
        public FinalBoneTable(
            IReadOnlyList<FinalBone> bones,
            IReadOnlyList<int> baseBoneRemap,
            IReadOnlyDictionary<string, IReadOnlyList<int>> partBoneRemap)
        {
            var boneCopy = new FinalBone[bones?.Count ?? 0];
            for (var i = 0; i < boneCopy.Length; i++) boneCopy[i] = bones[i];
            _bones = Array.AsReadOnly(boneCopy);

            var baseCopy = new int[baseBoneRemap?.Count ?? 0];
            for (var i = 0; i < baseCopy.Length; i++) baseCopy[i] = baseBoneRemap[i];
            _baseBoneRemap = Array.AsReadOnly(baseCopy);

            var poses = new Matrix4x4[boneCopy.Length];
            var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < boneCopy.Length; i++)
            {
                poses[i] = boneCopy[i] != null ? boneCopy[i].BindPose : Matrix4x4.identity;

                // An empty path is "missing", not an identity, and is deliberately not indexed. The avatar
                // root's token is non-empty and is indexed like any other path, which is what lets a weight on
                // the root bone resolve.
                var path = boneCopy[i] != null ? boneCopy[i].Path : string.Empty;
                if (ApaAvatarPath.HasIdentity(path) && !byPath.ContainsKey(path)) byPath.Add(path, i);
            }

            _bindPoses = Array.AsReadOnly(poses);
            _indexByPath = byPath;

            var partCopy = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
            if (partBoneRemap != null)
            {
                foreach (var pair in partBoneRemap)
                {
                    var values = pair.Value;
                    var copy = new int[values?.Count ?? 0];
                    for (var i = 0; i < copy.Length; i++) copy[i] = values[i];
                    partCopy[pair.Key ?? string.Empty] = Array.AsReadOnly(copy);
                }
            }

            _partBoneRemap = new ReadOnlyDictionary<string, IReadOnlyList<int>>(partCopy);

            var baseBones = 0;
            for (var i = 0; i < boneCopy.Length; i++)
            {
                if (boneCopy[i] != null && string.IsNullOrEmpty(boneCopy[i].OwnerPartId)) baseBones++;
            }

            BaseBoneCount = baseBones;
        }

        /// <summary>
        /// The final index of a bone identity, or -1 when the identity is not in the table.
        /// </summary>
        /// <remarks>
        /// The empty string is "missing" rather than an identity and never resolves, so a caller can pass an
        /// unrecorded path without it accidentally matching something. The avatar root's
        /// <see cref="ApaAvatarPath.Root"/> token is an identity like any other and resolves normally.
        /// </remarks>
        public int IndexOfPath(string path)
        {
            if (!ApaAvatarPath.HasIdentity(path)) return -1;
            return _indexByPath.TryGetValue(path, out var index) ? index : -1;
        }

        /// <summary>
        /// Remaps a bone index of one source onto the final table.
        /// </summary>
        /// <param name="partId">The owning part id, or an empty string for the target body.</param>
        /// <param name="sourceBoneIndex">Index inside that source's own bone list.</param>
        /// <returns>The final index, or -1 when the source or index is not mapped.</returns>
        public int RemapBone(string partId, int sourceBoneIndex)
        {
            var key = partId ?? string.Empty;
            if (string.IsNullOrEmpty(key))
            {
                if (sourceBoneIndex < 0 || sourceBoneIndex >= _baseBoneRemap.Count) return -1;
                return _baseBoneRemap[sourceBoneIndex];
            }

            if (!_partBoneRemap.TryGetValue(key, out var map)) return -1;
            if (sourceBoneIndex < 0 || sourceBoneIndex >= map.Count) return -1;
            return map[sourceBoneIndex];
        }

        /// <summary>The bind pose of a final bone, or the identity matrix when the index is out of range.</summary>
        public Matrix4x4 BindPoseAt(int finalIndex)
        {
            if (finalIndex < 0 || finalIndex >= _bindPoses.Count) return Matrix4x4.identity;
            return _bindPoses[finalIndex];
        }
    }

    /// <summary>
    /// Builds the final bone table from the captured snapshots, or reports why it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs during validation and again during planning. Both callers get byte-identical diagnostics
    /// because the messages are constructed in exactly one place, which is what lets the planner's report
    /// deduplicate against the validator's instead of doubling every bone error.
    /// </para>
    /// <para>
    /// Nothing here mutates a Unity object, and nothing is emitted when a check fails: a table is either
    /// complete and coherent, or the caller receives <c>null</c> plus blocking diagnostics.
    /// </para>
    /// </remarks>
    public static class FinalBoneTableBuilder
    {
        /// <summary>
        /// True when any source carries skinning data of any kind.
        /// </summary>
        /// <remarks>
        /// Bone weights, bind poses, and a bone signature are checked separately because a source can carry one
        /// without the others, and each combination needs its own diagnostic rather than a silent skip.
        /// </remarks>
        public static bool HasSkinningInputs(ValidationContext context)
        {
            if (HasSkinningInputs(context?.Base?.Mesh)) return true;
            if (context?.Parts == null) return false;
            for (var i = 0; i < context.Parts.Count; i++)
            {
                if (HasSkinningInputs(context.Parts[i]?.Mesh)) return true;
            }

            return false;
        }

        private static bool HasSkinningInputs(MeshSnapshot mesh)
        {
            if (mesh == null) return false;
            return mesh.SkinWeights.Count > 0
                   || mesh.SkinBindPoses.Count > 0
                   || mesh.BoneSignature.Count > 0
                   || mesh.BoneWorldToLocalMatrices.Count > 0;
        }

        /// <summary>
        /// Builds the final bone table, or returns null when there is no skinning or when a check failed.
        /// </summary>
        public static FinalBoneTable Build(ValidationContext context, List<ValidationIssue> issues)
        {
            if (issues == null) throw new ArgumentNullException(nameof(issues));
            if (context?.Base?.Mesh == null) return null;
            if (!HasSkinningInputs(context)) return null;

            var rendererLocalToWorld = context.Base.RendererLocalToWorld;
            var baseSourceToTarget = context.Base.Transforms.SourceToTargetLocal();
            var baseMesh = context.Base.Mesh;

            var bones = new List<FinalBone>();
            var indexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            var failed = false;

            failed |= !AppendSourceBones(
                bones,
                indexByPath,
                baseMesh,
                string.Empty,
                rendererLocalToWorld,
                baseSourceToTarget,
                context.NumericPolicy,
                issues,
                marshmallowPbCompatibilityEnabled: false);

            // A part may weight a bone the target body's signature declares but no body vertex uses. The body is
            // still the authority for that path, so its entry is created here — before any part is processed —
            // from the body's own transform, owner, and source bone index. Creating it first is what makes "the
            // target is authoritative for a shared path" hold for a path the body merely declares (M11).
            failed |= !AppendRequestedBodyBones(
                bones,
                indexByPath,
                baseMesh,
                context.Parts,
                rendererLocalToWorld,
                baseSourceToTarget,
                context.NumericPolicy,
                issues,
                marshmallowPbCompatibilityEnabled: context.MarshmallowPbCompatibilityEnabled);

            var baseRemap = new int[baseMesh.BoneSignature.Count];
            for (var i = 0; i < baseRemap.Length; i++) baseRemap[i] = -1;

            // The base remap is read back out of the identity map rather than recorded during the append: a
            // merged bone is not appended, so recording during the append would lose exactly the merged case.
            for (var i = 0; i < baseRemap.Length; i++)
            {
                var path = baseMesh.BoneSignature.PathAt(i);
                if (indexByPath.TryGetValue(path, out var index)) baseRemap[i] = index;
            }

            var partRemaps = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

            for (var p = 0; p < context.Parts.Count; p++)
            {
                var part = context.Parts[p];

                // A part with no mesh contributes no vertices, so it has no bone map to record. Recording an
                // entry under an empty key instead would make "no part" and "part with no bones" look the same.
                if (part?.Mesh == null) continue;

                var remap = new int[part.Mesh.BoneSignature.Count];
                for (var i = 0; i < remap.Length; i++) remap[i] = -1;

                // One summary per part rather than one line per bone: a part that reuses the body's armature can
                // redirect dozens of bones, and a per-bone log would bury every other diagnostic. The record is
                // produced inside the append because that is where "this path already existed" is known.
                var redirected = new List<BoneRedirect>();

                var partFailed = !AppendSourceBones(
                    bones,
                    indexByPath,
                    part.Mesh,
                    part.PartId,
                    rendererLocalToWorld,
                    part.Transforms.SourceToTargetLocal(),
                    context.NumericPolicy,
                    issues,
                    redirected,
                    context.MarshmallowPbCompatibilityEnabled);

                failed |= partFailed;

                if (redirected.Count > 0) issues.Add(PartBonesRedirected(part, redirected, context.NumericPolicy));

                for (var i = 0; i < remap.Length; i++)
                {
                    var path = part.Mesh.BoneSignature.PathAt(i);
                    if (TryFindCompatibleBoneIndex(
                            bones,
                            indexByPath,
                            path,
                            context.MarshmallowPbCompatibilityEnabled,
                            out var index)) remap[i] = index;
                }

                partRemaps[part.PartId] = remap;
            }

            if (failed) return null;

            // A source that carries skin data but no bone identity cannot be remapped at all: there is nothing
            // to map its indices onto. Reported per source so the diagnostic names the offender.
            if (HasSkinDataWithoutIdentity(baseMesh))
            {
                issues.Add(MissingBoneIdentity(string.Empty, "the target body", baseMesh));
                failed = true;
            }

            for (var p = 0; p < context.Parts.Count; p++)
            {
                var part = context.Parts[p];
                if (part?.Mesh == null) continue;
                if (!HasSkinDataWithoutIdentity(part.Mesh)) continue;

                issues.Add(MissingBoneIdentity(part.PartId, DescribePart(part), part.Mesh));
                failed = true;
            }

            if (failed) return null;

            if (bones.Count == 0)
            {
                // Reachable only when a source declared skinning inputs that carry no bone information at all.
                // Returning null silently would look like "this avatar has no skinning", which is the one
                // reading that must never happen.
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetBoneNotFound,
                    ApaIssuePhase.Attributes,
                    "Skinning data was found but no bone table could be resolved from it, so bone weights " +
                    "cannot be remapped.",
                    detail: "reason=empty-bone-table"));
                return null;
            }

            return new FinalBoneTable(bones, baseRemap, partRemaps);
        }

        private static bool HasSkinDataWithoutIdentity(MeshSnapshot mesh)
        {
            if (mesh == null) return false;
            if (mesh.BoneSignature.Count > 0) return false;
            return mesh.SkinWeights.Count > 0
                   || mesh.SkinBindPoses.Count > 0
                   || mesh.BoneWorldToLocalMatrices.Count > 0;
        }

        /// <summary>
        /// Appends the bones one source actually uses to the final table, merging identities that already exist.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Only bones an effective weight reaches are considered (APA008 fix).</b> A renderer's bone list
        /// routinely carries slots no vertex references — an unused hand bone, a slot left behind by a merge, a
        /// <c>null</c> entry — and iterating all of them made every unreferenced slot a blocking
        /// <c>bone-without-identity</c> defect. Nothing about such a slot can change the output: no weight is
        /// remapped through it, so it needs no identity, contributes no bone, and leaves its remap entry at
        /// <c>-1</c>. The set of required identities is therefore derived from
        /// <see cref="MeshSnapshot.SkinWeights"/> first, through
        /// <see cref="CollectUsedBoneIndices"/>, and only those indices are validated and appended.
        /// </para>
        /// <para>
        /// The threshold is <see cref="ApaNumericPolicy.WeightEpsilon"/>, the same one the remap uses to decide
        /// that an influence is absent. The two must agree: a stricter "used" test would require an identity for
        /// an influence the remap then cannot resolve, and a looser one would let a weight survive into the
        /// output pointing at a bone that was never appended.
        /// </para>
        /// <para>
        /// The scan is deterministic: the used indices are collected into a sorted set, so identical inputs
        /// produce an identical table regardless of weight-array layout.
        /// </para>
        /// <para>
        /// <b>A part bone whose path already exists always redirects onto the existing target bone.</b> The
        /// target body's transform is authoritative: a part bone that reuses an identity gets the body's
        /// <see cref="FinalBone"/> — its index, its owner, and above all its bind pose — and the part's own bind
        /// transform is discarded rather than compared. The earlier rule refused such a pair with
        /// <c>APA008 reason=ambiguous-bone-identity</c>, which made a part that merely sat somewhere else in the
        /// authoring scene unusable even though the joint it names is unambiguous: the path is relative to the
        /// armature the author selected for each side, so an identical path already means "the same joint".
        /// Because the part bone never enters the table, a later part cannot overwrite the target's bind pose
        /// either — the first (and only) entry for a path is always the target's.
        /// </para>
        /// <para>
        /// <b>The body's authority covers a path it declares but does not weight.</b> Before any part is
        /// processed, <see cref="AppendRequestedBodyBones"/> creates the body's entry for every path some part
        /// weights, so a part that reaches a body bone no body vertex reaches redirects onto the body's bone
        /// exactly like any other shared path rather than appending one of its own.
        /// </para>
        /// <para>
        /// Everything that is a genuine ambiguity still blocks: a weighted bone with no identity, two weighted
        /// bones of one source sharing a path, and a bone whose captured transform or computed bind pose is not
        /// a usable matrix. The last one is checked before the redirect so that a broken part rig is still
        /// reported even though its transform is not the one that ships.
        /// </para>
        /// </remarks>
        /// <param name="redirectedToTarget">
        /// Receives the path of every part bone that redirected onto an existing target bone, in source order, or
        /// may be null when the caller does not report a summary.
        /// </param>
        /// <returns>True when the source was appended without a blocking defect.</returns>
        private static bool AppendSourceBones(
            List<FinalBone> bones,
            Dictionary<string, int> indexByPath,
            MeshSnapshot mesh,
            string partId,
            Matrix4x4 rendererLocalToWorld,
            Matrix4x4 sourceToTargetLocal,
            ApaNumericPolicy policy,
            List<ValidationIssue> issues,
            List<BoneRedirect> redirectedToTarget = null,
            bool marshmallowPbCompatibilityEnabled = false)
        {
            if (mesh == null || mesh.BoneSignature.Count == 0) return true;

            var used = CollectUsedBoneIndices(mesh, policy);
            if (used.Count == 0) return true;

            var ok = true;
            var isPart = !string.IsNullOrEmpty(partId);
            var seenInSource = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var u = 0; u < used.Count; u++)
            {
                var i = used[u];
                var path = mesh.BoneSignature.PathAt(i);

                // Empty means the source's bone entry was null, or the bone is not inside the armature the
                // caller scoped the signature to: either way there is no transform and no identity, and a
                // weighted vertex names a bone that cannot be remapped. The armature root's token is NOT this
                // case — it is a real joint, reaches this loop as ".", and is appended or merged like any other
                // path.
                if (!ApaAvatarPath.HasIdentity(path))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.BoneHierarchyConflict,
                        ApaIssuePhase.Attributes,
                        "Bone " + i + " of " + DescribeSource(partId) +
                        " carries weight but has no armature-relative path, so it has no stable identity to " +
                        "remap bone weights onto. A null bone entry in the renderer's bone list, or a bone " +
                        "outside the armature selected for this source, is the usual cause.",
                        partId,
                        i,
                        detail: "reason=bone-without-identity; bone=" + i));
                    ok = false;
                    continue;
                }

                if (seenInSource.TryGetValue(path, out var earlier))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.BoneHierarchyConflict,
                        ApaIssuePhase.Attributes,
                        "Bone " + i + " of " + DescribeSource(partId) + " repeats the identity '" +
                        path + "' already used by bone " + earlier + " of the same source. Two bones with one " +
                        "identity cannot be told apart when a weight is remapped.",
                        partId,
                        i,
                        earlier,
                        detail: "reason=duplicate-bone-identity; path=" + path + "; first=" + earlier));
                    ok = false;
                    continue;
                }

                seenInSource.Add(path, i);

                var worldToLocal = i < mesh.BoneWorldToLocalMatrices.Count
                    ? mesh.BoneWorldToLocalMatrices[i]
                    : Matrix4x4.zero;

                if (!IsUsableTransform(worldToLocal))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBindPose,
                        ApaIssuePhase.Attributes,
                        "Bone " + i + " ('" + path + "') of " + DescribeSource(partId) +
                        " has no usable world-to-local transform, so its bind pose cannot be computed. A destroy" +
                        "ed or missing bone transform is the usual cause.",
                        partId,
                        i,
                        detail: "reason=missing-bone-transform; bone=" + i + "; path=" + path));
                    ok = false;
                    continue;
                }

                var bindPose = ComputeBindPose(
                    mesh,
                    i,
                    sourceToTargetLocal,
                    worldToLocal,
                    rendererLocalToWorld);
                if (!IsUsableTransform(bindPose))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBindPose,
                        ApaIssuePhase.Attributes,
                        "The bind pose computed for bone " + i + " ('" + path + "') of " +
                        DescribeSource(partId) +
                        " is not a usable matrix. The bone transform and the renderer transform do not combine " +
                        "into a valid bind pose.",
                        partId,
                        i,
                        detail: "reason=unusable-bind-pose; bone=" + i + "; path=" + path));
                    ok = false;
                    continue;
                }

                var hasExisting = isPart
                    ? TryFindCompatibleBoneIndex(
                        bones,
                        indexByPath,
                        path,
                        marshmallowPbCompatibilityEnabled,
                        out var existing)
                    : indexByPath.TryGetValue(path, out existing);
                if (hasExisting)
                {
                    // Merged: the part reuses a bone the body already has (section 19). The existing entry is
                    // authoritative and is deliberately NOT rewritten — its index, owner, and bind pose are the
                    // target body's, and the part's own bind transform is discarded. The part's remap entry is
                    // filled in below from the identity map, which is what makes the part's weights follow the
                    // body's bone.
                    if (isPart && redirectedToTarget != null)
                    {
                        redirectedToTarget.Add(new BoneRedirect(
                            path,
                            !TransformsAgree(bones[existing].BindPose, bindPose, policy.PositionEpsilon)));
                    }

                    continue;
                }

                var index = bones.Count;
                bones.Add(new FinalBone(index, path, partId, i, bindPose, false));
                indexByPath.Add(path, index);
            }

            return ok;
        }

        /// <summary>
        /// Appends the target body's own entry for every path a part actually weights, even when no body vertex
        /// reaches it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The body is authoritative for a path it declares, not only for a path it weights (M11).</b> The
        /// append above deliberately considers only the bones a body weight reaches, so a body signature that
        /// lists <c>Hips</c> and <c>Spine</c> while every body vertex follows <c>Hips</c> would leave
        /// <c>Spine</c> out of the table. A part that weights that <c>Spine</c> then appended a bone of its own
        /// for a path the body already declares, which contradicts "an identical armature-relative path is the
        /// same joint" and let preview and the NDMF build end up with different Transforms for one identity.
        /// </para>
        /// <para>
        /// The entry is created exactly the way <see cref="AppendSourceBones"/> creates a body bone — the body's
        /// captured world-to-local matrix, its renderer's local-to-world matrix, its own bone index, and an empty
        /// <see cref="FinalBone.OwnerPartId"/> — and it is created <i>before</i> any part is processed. Every
        /// part that weights the path therefore redirects onto it in the ordinary way: the part's own transform
        /// is discarded, its weights are remapped to the body's final index, and no later part can overwrite the
        /// entry.
        /// </para>
        /// <para>
        /// <b>Only requested paths are considered, so nothing else about an unreferenced slot changes.</b> The
        /// candidate set is the paths some part's effective weights reach (with the same
        /// <see cref="ApaNumericPolicy.WeightEpsilon"/> threshold the append and the remap use), so a body
        /// signature full of unused slots still produces no bone, no diagnostic, and no <c>APA008</c> — which is
        /// the M10 rule this builds on rather than replaces.
        /// </para>
        /// <para>
        /// <b>A requested path the body declares more than once blocks.</b> Two bones of the body with one path
        /// cannot be told apart, so there is no single Transform to be authoritative and no defensible choice
        /// between them; the part's weights are not remapped onto a guess. A path the body weights is already
        /// reported by the append above (a source that weights two bones with one path is
        /// <c>reason=duplicate-bone-identity</c>), so the check here fires only for a duplicated path no body
        /// weight reaches — one condition, one diagnostic, whichever side asked for it.
        /// </para>
        /// <para>
        /// A requested path whose body transform is unusable also blocks, for the same reason the append blocks
        /// on a broken part rig: the body's transform is the one that ships, so an asset that cannot produce a
        /// bind pose for it is broken rather than merely ignored.
        /// </para>
        /// </remarks>
        /// <returns>True when every requested body path produced a usable entry or already had one.</returns>
        private static bool AppendRequestedBodyBones(
            List<FinalBone> bones,
            Dictionary<string, int> indexByPath,
            MeshSnapshot baseMesh,
            IReadOnlyList<PartSnapshot> parts,
            Matrix4x4 rendererLocalToWorld,
            Matrix4x4 sourceToTargetLocal,
            ApaNumericPolicy policy,
            List<ValidationIssue> issues,
            bool marshmallowPbCompatibilityEnabled = false)
        {
            if (baseMesh == null || baseMesh.BoneSignature.Count == 0) return true;

            var requested = CollectRequestedPaths(parts, policy);
            if (requested.Count == 0) return true;

            var ok = true;
            var seenInBody = new Dictionary<string, int>(StringComparer.Ordinal);

            // The body's own bone order, so the entries a part requested land where the body's own bones land:
            // before every part-contributed bone and in the order the body lists them.
            for (var i = 0; i < baseMesh.BoneSignature.Count; i++)
            {
                var path = baseMesh.BoneSignature.PathAt(i);

                // An unreferenced slot, or a null bone entry, is not this rule's business: it needs no identity,
                // contributes nothing, and is reported by nothing.
                if (!ApaAvatarPath.HasIdentity(path)) continue;
                if (!ContainsCompatiblePath(requested, path, marshmallowPbCompatibilityEnabled)) continue;

                if (seenInBody.TryGetValue(path, out var earlier))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.BoneHierarchyConflict,
                        ApaIssuePhase.Attributes,
                        "The target body declares the identity '" + path + "' at both bone " + earlier + " and " +
                        "bone " + i + ", and a part weights that path. Two body bones with one identity cannot be " +
                        "told apart, so there is no single body bone the part's weights could follow.",
                        string.Empty,
                        i,
                        earlier,
                        detail: "reason=duplicate-bone-identity; path=" + path + "; first=" + earlier));
                    ok = false;
                    continue;
                }

                seenInBody.Add(path, i);

                // Already present: the body weights this path itself, so the append above already created the
                // entry and this rule has nothing left to do for it.
                if (indexByPath.ContainsKey(path)) continue;

                var worldToLocal = i < baseMesh.BoneWorldToLocalMatrices.Count
                    ? baseMesh.BoneWorldToLocalMatrices[i]
                    : Matrix4x4.zero;

                if (!IsUsableTransform(worldToLocal))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBindPose,
                        ApaIssuePhase.Attributes,
                        "Bone " + i + " ('" + path + "') of the target body is declared by a part's bone " +
                        "weights but has no usable world-to-local transform, so its bind pose cannot be computed. " +
                        "A destroyed or missing bone transform is the usual cause.",
                        string.Empty,
                        i,
                        detail: "reason=missing-bone-transform; bone=" + i + "; path=" + path));
                    ok = false;
                    continue;
                }

                var bindPose = ComputeBindPose(
                    baseMesh,
                    i,
                    sourceToTargetLocal,
                    worldToLocal,
                    rendererLocalToWorld);
                if (!IsUsableTransform(bindPose))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBindPose,
                        ApaIssuePhase.Attributes,
                        "The bind pose computed for bone " + i + " ('" + path + "') of the target body is not a " +
                        "usable matrix. The bone transform and the renderer transform do not combine into a " +
                        "valid bind pose.",
                        string.Empty,
                        i,
                        detail: "reason=unusable-bind-pose; bone=" + i + "; path=" + path));
                    ok = false;
                    continue;
                }

                // The body owns it: the owner is the empty string and the source index is the body's own bone
                // index, which is what lets the preview and the NDMF build resolve this entry to the same
                // Transform the body itself would use.
                var index = bones.Count;
                bones.Add(new FinalBone(index, path, string.Empty, i, bindPose, false));
                indexByPath.Add(path, index);
            }

            return ok;
        }

        /// <summary>
        /// Finds an exact bone identity first, then the narrow same-named-wrapper alias accepted by the
        /// compatibility rule. The list order is the target body's deterministic authority order.
        /// </summary>
        private static bool TryFindCompatibleBoneIndex(
            List<FinalBone> bones,
            Dictionary<string, int> indexByPath,
            string path,
            bool marshmallowPbCompatibilityEnabled,
            out int index)
        {
            if (indexByPath.TryGetValue(path, out index)) return true;
            if (!marshmallowPbCompatibilityEnabled || !ApaAvatarPath.HasIdentity(path))
            {
                index = -1;
                return false;
            }

            for (var i = 0; i < bones.Count; i++)
            {
                if (!ApaBonePathCompatibility.MatchesLiveTarget(path, bones[i].Path)) continue;
                index = i;
                return true;
            }

            index = -1;
            return false;
        }

        private static bool ContainsCompatiblePath(
            HashSet<string> requested,
            string path,
            bool marshmallowPbCompatibilityEnabled)
        {
            if (requested.Contains(path)) return true;
            if (!marshmallowPbCompatibilityEnabled) return false;

            foreach (var candidate in requested)
            {
                if (ApaBonePathCompatibility.MatchesLiveTarget(candidate, path)) return true;
            }

            return false;
        }

        /// <summary>
        /// Computes a bind pose that is stable across live pose edits. A source mesh's bind pose describes the
        /// source renderer and its authored rest pose; when the source vertices are written into the target
        /// renderer's local space, the inverse source-to-target matrix converts that bind pose into the final
        /// vertex basis. The live bone matrix is only a compatibility fallback for hand-built/legacy snapshots
        /// that predate captured bind poses.
        /// </summary>
        private static Matrix4x4 ComputeBindPose(
            MeshSnapshot mesh,
            int boneIndex,
            Matrix4x4 sourceToTargetLocal,
            Matrix4x4 worldToLocal,
            Matrix4x4 rendererLocalToWorld)
        {
            if (mesh != null && boneIndex >= 0 && boneIndex < mesh.SkinBindPoses.Count)
            {
                var sourceBindPose = mesh.SkinBindPoses[boneIndex];
                if (IsUsableTransform(sourceBindPose) && IsUsableTransform(sourceToTargetLocal))
                {
                    var targetToSource = sourceToTargetLocal.inverse;
                    if (IsUsableTransform(targetToSource)) return sourceBindPose * targetToSource;
                }
            }

            return worldToLocal * rendererLocalToWorld;
        }

        /// <summary>
        /// The set of bone paths some part's effective weights reach, or an empty set when no part weights
        /// anything.
        /// </summary>
        /// <remarks>
        /// A set rather than a list because the answer is only ever asked as a membership question, and the same
        /// threshold the append and the remap use is applied here, so "a part requests this path" cannot mean
        /// something different from "a part's weight survives on this path". Parts with no mesh, and paths that
        /// are not identities, contribute nothing.
        /// </remarks>
        private static HashSet<string> CollectRequestedPaths(
            IReadOnlyList<PartSnapshot> parts,
            ApaNumericPolicy policy)
        {
            var requested = new HashSet<string>(StringComparer.Ordinal);
            if (parts == null) return requested;

            for (var p = 0; p < parts.Count; p++)
            {
                var mesh = parts[p]?.Mesh;
                if (mesh == null) continue;

                var used = CollectUsedBoneIndices(mesh, policy);
                for (var u = 0; u < used.Count; u++)
                {
                    var path = mesh.BoneSignature.PathAt(used[u]);
                    if (ApaAvatarPath.HasIdentity(path)) requested.Add(path);
                }
            }

            return requested;
        }

        /// <summary>
        /// The ascending, duplicate-free list of bone indices some vertex references with an effective weight.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one definition of "this source uses this bone". Four influences are tested per vertex with no
        /// temporary allocation, the result is a sorted set so the append order cannot depend on the weight
        /// array's layout, and an index outside the source's bone list is dropped here because
        /// <see cref="BoneWeightValidator"/> already reports it as <c>APA007 reason=bone-index-out-of-range</c>
        /// and the build blocks before a table is needed.
        /// </para>
        /// <para>
        /// A source with no weights at all yields nothing, which is correct: with nothing to remap, no bone of
        /// that source has to exist in the table.
        /// </para>
        /// </remarks>
        private static List<int> CollectUsedBoneIndices(MeshSnapshot mesh, ApaNumericPolicy policy)
        {
            var used = new List<int>();
            var effective = policy ?? ApaNumericPolicy.Default;
            var weights = mesh.SkinWeights;
            var boneCount = mesh.BoneSignature.Count;
            if (boneCount == 0) return used;

            // A membership bitmap rather than a List.Contains probe per influence: the scan is per vertex of
            // every source, and a linear probe would make it quadratic in the bone count.
            var seen = new bool[boneCount];

            for (var v = 0; v < weights.Count; v++)
            {
                var weight = weights[v];
                AddUsedBoneIndex(used, seen, weight.boneIndex0, weight.weight0, effective);
                AddUsedBoneIndex(used, seen, weight.boneIndex1, weight.weight1, effective);
                AddUsedBoneIndex(used, seen, weight.boneIndex2, weight.weight2, effective);
                AddUsedBoneIndex(used, seen, weight.boneIndex3, weight.weight3, effective);
            }

            used.Sort();
            return used;
        }

        private static void AddUsedBoneIndex(
            List<int> used,
            bool[] seen,
            int index,
            float weight,
            ApaNumericPolicy policy)
        {
            if (index < 0 || index >= seen.Length) return;
            if (policy.IsNegligibleWeight(weight)) return;
            if (seen[index]) return;

            seen[index] = true;
            used.Add(index);
        }

        private static ValidationIssue MissingBoneIdentity(string partId, string who, MeshSnapshot mesh)
        {
            return ValidationIssue.Error(
                ApaErrorCode.TargetBoneNotFound,
                ApaIssuePhase.Attributes,
                "Mesh '" + MeshName(mesh) + "' on " + who + " has skin weights but no bone signature, so there " +
                "is no stable identity to remap them onto. Capture the renderer's bones before assembling.",
                partId,
                detail: "reason=missing-bone-signature; weights=" + mesh.SkinWeights.Count +
                        "; bindPoses=" + mesh.SkinBindPoses.Count);
        }

        /// <summary>
        /// True when every element is finite and the matrix is not the zero matrix.
        /// </summary>
        /// <remarks>
        /// A zero matrix is what <see cref="MeshSnapshotFactory"/> records for a null bone, and it is also what
        /// a destroyed transform produces. It is not a transform: skinning by it collapses every affected
        /// vertex to the renderer origin, which is the "mesh flies away" failure the bind pose exists to
        /// prevent.
        /// </remarks>
        private static bool IsUsableTransform(Matrix4x4 matrix)
        {
            var zero = true;
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var value = matrix[r, c];
                    if (!ApaNumericPolicy.IsFinite(value)) return false;
                    if (value != 0f) zero = false;
                }
            }

            return !zero;
        }

        /// <summary>
        /// True when two bind poses describe the same transform within the configured tolerance.
        /// </summary>
        /// <remarks>
        /// Compared element-wise with the spatial epsilon. The linear part of a matrix is unitless while the
        /// translation column is in avatar-local units, so one tolerance is a compromise; it is the same
        /// tolerance the seam match uses, and a bone whose transform disagrees by more than a hundredth of a
        /// millimetre is a different joint for any practical avatar.
        /// </remarks>
        private static bool TransformsAgree(Matrix4x4 a, Matrix4x4 b, float epsilon)
        {
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    if (Mathf.Abs(a[r, c] - b[r, c]) > epsilon) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// One part bone that redirected onto a bone the target body already has.
        /// </summary>
        /// <remarks>
        /// <see cref="TransformDiffers"/> is recorded rather than acted on: the target body is authoritative
        /// either way, but a report that says "and the part's transform was ignored at N of them" tells an author
        /// whether the part was placed somewhere else at authoring time, which is the fact the old blocking
        /// diagnostic existed to surface.
        /// </remarks>
        private readonly struct BoneRedirect
        {
            public readonly string Path;
            public readonly bool TransformDiffers;

            public BoneRedirect(string path, bool transformDiffers)
            {
                Path = path ?? string.Empty;
                TransformDiffers = transformDiffers;
            }
        }

        /// <summary>
        /// The one informational summary per part whose bones redirected onto existing target bones.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reported against <c>APA007</c>, whose registered meaning is "a required final bone could not be
        /// resolved". Every redirected bone <i>is</i> resolved — onto the body's own bone — so the informational
        /// reading is the resolution the code names, and no new code is allocated for a condition that is not a
        /// defect. <c>APA008</c> keeps its registered meaning (a genuine identity ambiguity) and is no longer
        /// produced by a same-path/different-transform pair.
        /// </para>
        /// <para>
        /// The message names a bounded sample of the paths rather than all of them: a part that reuses a whole
        /// armature redirects dozens of bones, and the actionable fact is the count and the first few identities.
        /// </para>
        /// </remarks>
        private static ValidationIssue PartBonesRedirected(
            PartSnapshot part,
            List<BoneRedirect> redirects,
            ApaNumericPolicy policy)
        {
            const int SampleLimit = 8;

            var epsilon = (policy ?? ApaNumericPolicy.Default).PositionEpsilon;
            var differing = 0;
            var sample = new StringBuilder();
            var shown = redirects.Count < SampleLimit ? redirects.Count : SampleLimit;

            for (var i = 0; i < shown; i++)
            {
                if (i > 0) sample.Append(',');
                sample.Append(redirects[i].Path);
            }

            if (redirects.Count > shown) sample.Append(",…");

            for (var i = 0; i < redirects.Count; i++)
            {
                if (redirects[i].TransformDiffers) differing++;
            }

            return ValidationIssue.Info(
                ApaErrorCode.TargetBoneNotFound,
                ApaIssuePhase.Attributes,
                redirects.Count + " bone(s) of part '" + part.PartId + "' have the same armature-relative path " +
                "as a bone of the target body, so the body's bone is used: its bind pose, world transform, and " +
                "final Transform are authoritative and the part's own bind transform is ignored. The body owns " +
                "the entry whether or not a body vertex weights it, because a path the body declares is the same " +
                "joint. The part's skin weights are remapped onto the body's bone and no duplicate bone is added " +
                "to the final table." +
                (differing > 0
                    ? " " + differing + " of them described a different transform, which is expected when the " +
                      "part sat somewhere else at authoring time."
                    : string.Empty),
                part.PartId,
                detail: "reason=part-bone-remapped-to-target" +
                        "; part='" + part.PartId + "'" +
                        "; redirectedBones=" + redirects.Count +
                        "; differingTransforms=" + differing +
                        "; positionEpsilon=" + epsilon +
                        "; paths=" + sample);
        }

        private static string DescribeSource(string partId)
        {
            if (string.IsNullOrEmpty(partId)) return "the target body";
            return "'" + partId + "'";
        }

        private static string DescribePart(PartSnapshot part)
        {
            return "'" + (part != null ? part.PartId : string.Empty) + "'";
        }

        private static string MeshName(MeshSnapshot mesh)
        {
            return string.IsNullOrEmpty(mesh?.Name) ? "(unnamed mesh)" : mesh.Name;
        }
    }

    /// <summary>
    /// Validates bone weights and remaps them onto the final bone table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The validator and the mesh assembler share <see cref="TryRemapWeight"/> so that "what was validated" and
    /// "what was written" cannot drift apart. A weight that the validator accepted is exactly a weight the
    /// assembler can resolve, which is what makes "never emit a partial mesh" more than a promise.
    /// </para>
    /// <para>
    /// Sources are validated as a whole rather than per retained vertex, matching how M1 validates positions:
    /// a NaN or a zero-sum weight anywhere in a source means the asset itself is not trustworthy, and the
    /// retained subset is derived from authoring data that is under suspicion anyway.
    /// </para>
    /// </remarks>
    public static class BoneWeightValidator
    {
        /// <summary>
        /// Validates the weight arrays of every source. Structural and per-vertex checks both run here.
        /// </summary>
        /// <param name="anyWeights">
        /// True when at least one source carries weights. When no source does, the generated mesh carries no
        /// bone weights at all (only bind poses), so there is nothing to validate.
        /// </param>
        public static void ValidateSourceData(ValidationContext context, bool anyWeights, List<ValidationIssue> issues)
        {
            if (issues == null) throw new ArgumentNullException(nameof(issues));
            if (context?.Base?.Mesh == null) return;

            var policy = context.NumericPolicy ?? ApaNumericPolicy.Default;

            ValidateSource(context.Base.Mesh, string.Empty, "the target body", anyWeights, policy, issues);

            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null) continue;
                ValidateSource(part.Mesh, part.PartId, DescribePart(part), anyWeights, policy, issues);
            }
        }

        /// <summary>True when any source carries skin weights.</summary>
        public static bool AnyWeights(ValidationContext context)
        {
            if (context?.Base?.Mesh != null && context.Base.Mesh.SkinWeights.Count > 0) return true;
            if (context?.Parts == null) return false;
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var mesh = context.Parts[i]?.Mesh;
                if (mesh != null && mesh.SkinWeights.Count > 0) return true;
            }

            return false;
        }

        private static void ValidateSource(
            MeshSnapshot mesh,
            string partId,
            string who,
            bool anyWeights,
            ApaNumericPolicy policy,
            List<ValidationIssue> issues)
        {
            var weights = mesh.SkinWeights;
            if (weights.Count == 0)
            {
                // A skinned output needs a weight for every vertex, so a source that carries none while another
                // source does would have to be given invented weights. The builder does not guess.
                if (anyWeights && mesh.VertexCount > 0)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBoneWeight,
                        ApaIssuePhase.Attributes,
                        "Mesh '" + MeshName(mesh) + "' on " + who + " has no bone weights, but the assembly is " +
                        "skinned because another source supplies them. Every vertex of a skinned mesh needs a " +
                        "weight; without one it would collapse to the renderer origin when animated.",
                        partId,
                        detail: "reason=source-has-no-weights; vertices=" + mesh.VertexCount));
                }

                return;
            }

            if (weights.Count != mesh.VertexCount)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InvalidBoneWeight,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + MeshName(mesh) + "' on " + who + " has " + weights.Count +
                    " bone weight entr" + (weights.Count == 1 ? "y" : "ies") + " for " + mesh.VertexCount +
                    " vertices. A weight array must have exactly one entry per vertex.",
                    partId,
                    detail: "reason=weight-count-mismatch; weights=" + weights.Count +
                            "; vertices=" + mesh.VertexCount));
                return;
            }

            var boneCount = mesh.BoneSignature.Count;
            var reportedMissingBone = false;
            var reportedNonFinite = false;
            var reportedInvalidWeight = false;

            for (var v = 0; v < weights.Count; v++)
            {
                var weight = weights[v];

                if (!reportedNonFinite
                    && (!ApaNumericPolicy.IsFinite(weight.weight0) || !ApaNumericPolicy.IsFinite(weight.weight1)
                        || !ApaNumericPolicy.IsFinite(weight.weight2) || !ApaNumericPolicy.IsFinite(weight.weight3)))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.NonFiniteValue,
                        ApaIssuePhase.Attributes,
                        "Mesh '" + MeshName(mesh) + "' on " + who + " has a non-finite bone weight at vertex " +
                        v + ".",
                        partId,
                        v,
                        detail: "attribute=boneweight; vertex=" + v));
                    reportedNonFinite = true;
                }

                if (!reportedInvalidWeight
                    && (weight.weight0 < 0f || weight.weight1 < 0f || weight.weight2 < 0f || weight.weight3 < 0f))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBoneWeight,
                        ApaIssuePhase.Attributes,
                        "Mesh '" + MeshName(mesh) + "' on " + who + " has a negative bone weight at vertex " + v +
                        ". Negative influences would move the vertex away from every bone it names.",
                        partId,
                        v,
                        detail: "reason=negative-weight; vertex=" + v));
                    reportedInvalidWeight = true;
                }

                // A negligible total means the vertex names no bone that actually influences it: every influence
                // is at or below the policy's influence threshold, so the remap clears all four to index 0 /
                // weight 0 and the vertex would stay pinned to the renderer origin, which is the "mesh flies
                // away" failure in its purest form. The threshold is the remap's own, so "the source is valid"
                // and "the remap can resolve every influence that survives" cannot disagree.
                if (!reportedInvalidWeight && !policy.HasEffectiveInfluence(weight))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBoneWeight,
                        ApaIssuePhase.Attributes,
                        "Mesh '" + MeshName(mesh) + "' on " + who + " has a vertex whose bone weights sum to at " +
                        "most the influence threshold (" + policy.WeightEpsilon + ") at vertex " + v + ", so it " +
                        "would not follow any bone.",
                        partId,
                        v,
                        detail: "reason=zero-weight-sum; vertex=" + v +
                                "; weightEpsilon=" + policy.WeightEpsilon));
                    reportedInvalidWeight = true;
                }

                if (reportedMissingBone) continue;

                if ((weight.weight0 > policy.WeightEpsilon && !IsBoneIndexValid(weight.boneIndex0, boneCount))
                    || (weight.weight1 > policy.WeightEpsilon && !IsBoneIndexValid(weight.boneIndex1, boneCount))
                    || (weight.weight2 > policy.WeightEpsilon && !IsBoneIndexValid(weight.boneIndex2, boneCount))
                    || (weight.weight3 > policy.WeightEpsilon && !IsBoneIndexValid(weight.boneIndex3, boneCount)))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetBoneNotFound,
                        ApaIssuePhase.Attributes,
                        "Mesh '" + MeshName(mesh) + "' on " + who + " has a bone weight at vertex " + v +
                        " that references a bone outside its own bone list (" + boneCount + " bone(s)). The " +
                        "profile and the target do not describe the same armature.",
                        partId,
                        v,
                        detail: "reason=bone-index-out-of-range; vertex=" + v +
                                "; indices=" + weight.boneIndex0 + "," + weight.boneIndex1 + "," +
                                weight.boneIndex2 + "," + weight.boneIndex3 + "; bones=" + boneCount));
                    reportedMissingBone = true;
                }
            }
        }

        private static bool IsBoneIndexValid(int index, int boneCount)
        {
            return index >= 0 && index < boneCount;
        }

        private static string DescribePart(PartSnapshot part)
        {
            return "'" + (part != null ? part.PartId : string.Empty) + "'";
        }

        private static string MeshName(MeshSnapshot mesh)
        {
            return string.IsNullOrEmpty(mesh?.Name) ? "(unnamed mesh)" : mesh.Name;
        }

        /// <summary>
        /// Verifies that every retained vertex's weights resolve onto the final table.
        /// </summary>
        /// <remarks>
        /// Runs in the planner, after the final vertex list and the remaps exist. <see cref="ValidateSourceData"/>
        /// already proved every index is inside its source's own bone list; this proves the last link, that the
        /// source bone has a final index, which is what the assembler will actually write.
        /// </remarks>
        public static void ValidateFinalRemap(
            FinalBoneTable table,
            IReadOnlyList<FinalVertexSource> finalVertices,
            ValidationContext context,
            List<ValidationIssue> issues)
        {
            if (table == null || finalVertices == null || context == null) return;

            var reported = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < finalVertices.Count; i++)
            {
                var source = finalVertices[i];
                var mesh = source.Origin == VertexOrigin.Base
                    ? context.Base.Mesh
                    : context.FindPart(source.PartId)?.Mesh;

                if (mesh == null) continue;
                if (source.SourceVertex < 0 || source.SourceVertex >= mesh.SkinWeights.Count) continue;

                if (TryRemapWeight(
                        mesh.SkinWeights[source.SourceVertex],
                        table,
                        source.PartId,
                        out _,
                        context.NumericPolicy))
                {
                    continue;
                }

                var key = source.PartId ?? string.Empty;
                if (!reported.Add(key)) continue;

                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetBoneNotFound,
                    ApaIssuePhase.Attributes,
                    "A bone weight on " + (string.IsNullOrEmpty(key) ? "the target body" : "'" + key + "'") +
                    " (vertex " + source.SourceVertex + ") has no final bone to map onto, so the mesh cannot " +
                    "be skinned coherently.",
                    source.PartId,
                    source.SourceVertex,
                    detail: "reason=unmapped-final-bone; vertex=" + source.SourceVertex));
            }
        }

        /// <summary>
        /// Remaps one source weight onto the final bone table.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An influence whose weight is at or below <see cref="ApaNumericPolicy.WeightEpsilon"/> has its index
        /// cleared to zero and its weight zeroed instead of being remapped. Unity treats such an influence as
        /// absent, so clearing it keeps the output canonical and, more importantly, means a negligible reference
        /// to a bone that was never appended cannot make a valid weight look invalid. The threshold is the same
        /// one <see cref="FinalBoneTableBuilder"/> uses to decide which bones need an identity, so the two
        /// cannot disagree about whether an influence exists.
        /// </para>
        /// <para>
        /// The policy is optional so that the existing call sites keep compiling; a caller that omits it gets the
        /// documented default policy.
        /// </para>
        /// </remarks>
        /// <returns>True when every influence above the threshold resolved.</returns>
        public static bool TryRemapWeight(
            BoneWeight source,
            FinalBoneTable table,
            string partId,
            out BoneWeight result,
            ApaNumericPolicy policy = null)
        {
            result = default;
            if (table == null) return false;

            var effective = policy ?? ApaNumericPolicy.Default;

            if (!TryRemapInfluence(source.boneIndex0, source.weight0, table, partId, effective, out var index0, out var weight0)
                || !TryRemapInfluence(source.boneIndex1, source.weight1, table, partId, effective, out var index1, out var weight1)
                || !TryRemapInfluence(source.boneIndex2, source.weight2, table, partId, effective, out var index2, out var weight2)
                || !TryRemapInfluence(source.boneIndex3, source.weight3, table, partId, effective, out var index3, out var weight3))
            {
                return false;
            }

            result = new BoneWeight
            {
                boneIndex0 = index0,
                weight0 = weight0,
                boneIndex1 = index1,
                weight1 = weight1,
                boneIndex2 = index2,
                weight2 = weight2,
                boneIndex3 = index3,
                weight3 = weight3
            };

            return true;
        }

        private static bool TryRemapInfluence(
            int sourceIndex,
            float weight,
            FinalBoneTable table,
            string partId,
            ApaNumericPolicy policy,
            out int finalIndex,
            out float finalWeight)
        {
            if (policy.IsNegligibleWeight(weight))
            {
                // Cleared, not remapped: the index is canonical zero and the weight becomes exactly zero, so a
                // negligible influence cannot leave a dangling reference to a bone the table never received.
                finalIndex = 0;
                finalWeight = 0f;
                return true;
            }

            finalWeight = weight;
            finalIndex = table.RemapBone(partId, sourceIndex);
            return finalIndex >= 0;
        }
    }
}

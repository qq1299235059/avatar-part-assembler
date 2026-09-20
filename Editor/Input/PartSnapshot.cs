using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The captured matrices needed to move a mesh's vertex data into avatar-root local space and, separately,
    /// into the target renderer's local space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seam matching is defined in avatar-root local space, while emitted vertices are written in the target
    /// renderer's local space. Those are two different spaces, and conflating them is the classic way to get a
    /// part that matches in the Scene view and detaches at build time. Both are therefore carried explicitly.
    /// </para>
    /// <para>
    /// <b>This is a snapshot, not a view.</b> Every matrix is captured when the value is created and no live
    /// <see cref="Transform"/> is retained, so reparenting, moving, or scaling the avatar after the context was
    /// built cannot change what validation, planning, or the mesh build computes. The normal matrix is derived
    /// and validated at capture time rather than inverted on every call, because a singular source-to-target
    /// transform has no inverse and must be rejected before anything reads it.
    /// </para>
    /// <para>
    /// A default-initialized value is the identity mapping. Hand-built contexts and fixtures that work purely in
    /// one space (part-local space equals target-local space) rely on that, and it is the same behaviour the
    /// previous live-transform type had when its transforms were null.
    /// </para>
    /// </remarks>
    public readonly struct SpaceTransforms
    {
        private readonly Matrix4x4 _sourceToAvatarLocal;
        private readonly Matrix4x4 _sourceToTargetLocal;
        private readonly Matrix4x4 _sourceToTargetNormal;
        private readonly bool _isCaptured;

        /// <summary>True when this value carries validated captured matrices.</summary>
        public bool IsValid => _isCaptured;

        /// <summary>
        /// The matrix that maps a source-local position into avatar-root local space.
        /// </summary>
        public Matrix4x4 SourceToAvatarLocal()
        {
            return _isCaptured ? _sourceToAvatarLocal : Matrix4x4.identity;
        }

        /// <summary>
        /// The matrix that maps a source-local position into target-renderer local space.
        /// </summary>
        public Matrix4x4 SourceToTargetLocal()
        {
            return _isCaptured ? _sourceToTargetLocal : Matrix4x4.identity;
        }

        /// <summary>
        /// The normal matrix (the inverse transpose of the linear part) that maps a source-local direction into
        /// target-renderer local space.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Normals are covectors. Transforming them with the ordinary matrix shears them under non-uniform
        /// scale, which is exactly the case a scaled part prefab produces. The inverse transpose is the correct
        /// operator and is required for correctness rather than for polish.
        /// </para>
        /// <para>
        /// Only the 3x3 linear part is meaningful for a direction; the translation row and column are the
        /// identity. The matrix is captured and validated at construction, so this never inverts a degenerate
        /// transform.
        /// </para>
        /// </remarks>
        public Matrix4x4 SourceToTargetNormalMatrix()
        {
            return _isCaptured ? _sourceToTargetNormal : Matrix4x4.identity;
        }

        /// <summary>
        /// The matrix that maps one kind of blend shape delta from source-local space into target-renderer
        /// local space.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The operator is defined once, here, so that output materialization and the seam-delta validation
        /// cannot drift apart: a delta that the validator accepts is transformed exactly the way the emitted
        /// frame transforms it. A delta is an offset, so callers apply the returned matrix with
        /// <see cref="Matrix4x4.MultiplyVector"/>, which uses the linear part only and never the translation
        /// column.
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>Position</b> — the linear part of the source-to-target matrix. A delta is an offset, so the
        /// translation column must not be added to it.
        /// </description></item>
        /// <item><description>
        /// <b>Normal</b> — the inverse transpose. Normals are covectors and would be sheared by the ordinary
        /// matrix under non-uniform scale.
        /// </description></item>
        /// <item><description>
        /// <b>Tangent</b> — the linear part, because a tangent's xyz is a direction (section 43.2). It is
        /// <i>not</i> the inverse transpose; the two agree only for a uniform scale, which is why using the
        /// wrong one looks correct until a part is scaled non-uniformly.
        /// </description></item>
        /// </list>
        /// </remarks>
        public Matrix4x4 SourceToTargetDeltaMatrix(BlendShapeDeltaKind kind)
        {
            if (!_isCaptured) return Matrix4x4.identity;
            return kind == BlendShapeDeltaKind.Normal ? _sourceToTargetNormal : _sourceToTargetLocal;
        }

        /// <summary>
        /// Creates a transform value from matrices that were read from live transforms.
        /// </summary>
        /// <remarks>
        /// Prefer <see cref="MeshSnapshotFactory.TryCaptureTransforms"/>, which reads the transforms and reports
        /// a blocking diagnostic when a matrix is unusable. This entry point exists for callers that already hold
        /// matrices (tests) and assumes they were validated.
        /// </remarks>
        public static SpaceTransforms Capture(
            Matrix4x4 sourceToAvatarLocal,
            Matrix4x4 sourceToTargetLocal,
            Matrix4x4 sourceToTargetNormal)
        {
            return new SpaceTransforms(sourceToAvatarLocal, sourceToTargetLocal, sourceToTargetNormal, true);
        }

        /// <summary>
        /// Builds a transform value, or reports why the matrices cannot be used.
        /// </summary>
        /// <remarks>
        /// This is the single validation point for the space matrices, and it is deliberately pure: the reader
        /// that touches Unity transforms calls it, and the offline harness can call it directly. The reason
        /// token is stable so that a diagnostic can be asserted on without matching prose.
        /// </remarks>
        /// <param name="sourceToAvatarLocal">Maps source-local positions into avatar-root local space.</param>
        /// <param name="sourceToTargetLocal">Maps source-local positions into target-local space.</param>
        /// <param name="transforms">Receives the captured value on success.</param>
        /// <param name="failureReason">
        /// On failure, a stable token: <c>non-finite-source-to-avatar</c>, <c>non-finite-source-to-target</c>,
        /// <c>singular-source-to-target</c>, or <c>non-finite-normal-matrix</c>.
        /// </param>
        /// <returns>True when every required matrix is finite and the normal matrix could be derived.</returns>
        public static bool TryCreate(
            Matrix4x4 sourceToAvatarLocal,
            Matrix4x4 sourceToTargetLocal,
            out SpaceTransforms transforms,
            out string failureReason)
        {
            transforms = default;

            if (!IsFinite(sourceToAvatarLocal))
            {
                failureReason = "non-finite-source-to-avatar";
                return false;
            }

            if (!IsFinite(sourceToTargetLocal))
            {
                failureReason = "non-finite-source-to-target";
                return false;
            }

            // A singular source-to-target transform cannot be inverted, so it has no normal matrix. Testing the
            // determinant up front means the inverse transpose is never computed on a degenerate matrix, which
            // is what would otherwise write NaN into a normal or a blend shape delta.
            if (!TryBuildNormalMatrix(sourceToTargetLocal, out var normal, out failureReason))
            {
                return false;
            }

            transforms = new SpaceTransforms(sourceToAvatarLocal, sourceToTargetLocal, normal, true);
            failureReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Builds the inverse transpose of a transform's linear part, or reports why it does not exist.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The 3x3 linear part is inverted directly rather than through <c>Matrix4x4.inverse</c> and
        /// <c>Matrix4x4.determinant</c> for two reasons. Only the linear part is ever used — a delta is applied
        /// with <see cref="Matrix4x4.MultiplyVector"/> — and it makes the operator usable without the Unity
        /// Editor, which is what allows the pure core to be executed outside it. The result is the same 3x3
        /// operator the full 4x4 inverse transpose would provide.
        /// </para>
        /// <para>
        /// A matrix whose linear part is singular is refused before an inverse is produced from it, so a
        /// degenerate transform cannot reach a normal or a blend shape delta as NaN.
        /// </para>
        /// </remarks>
        private static bool TryBuildNormalMatrix(
            Matrix4x4 matrix,
            out Matrix4x4 normal,
            out string failureReason)
        {
            normal = Matrix4x4.identity;

            var a = matrix.m00;
            var b = matrix.m01;
            var c = matrix.m02;
            var d = matrix.m10;
            var e = matrix.m11;
            var f = matrix.m12;
            var g = matrix.m20;
            var h = matrix.m21;
            var i = matrix.m22;

            var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (!ApaNumericPolicy.IsFinite(determinant) || Mathf.Abs(determinant) <= SingularDeterminantEpsilon)
            {
                failureReason = "singular-source-to-target";
                return false;
            }

            var inverseDeterminant = 1f / determinant;

            // The inverse is the transposed cofactor matrix divided by the determinant; transposing it again
            // for the normal matrix means the cofactors are written in their already-transposed order.
            var n00 = (e * i - f * h) * inverseDeterminant;
            var n01 = (f * g - d * i) * inverseDeterminant;
            var n02 = (d * h - e * g) * inverseDeterminant;
            var n10 = (c * h - b * i) * inverseDeterminant;
            var n11 = (a * i - c * g) * inverseDeterminant;
            var n12 = (b * g - a * h) * inverseDeterminant;
            var n20 = (b * f - c * e) * inverseDeterminant;
            var n21 = (c * d - a * f) * inverseDeterminant;
            var n22 = (a * e - b * d) * inverseDeterminant;

            normal.m00 = n00;
            normal.m01 = n01;
            normal.m02 = n02;
            normal.m10 = n10;
            normal.m11 = n11;
            normal.m12 = n12;
            normal.m20 = n20;
            normal.m21 = n21;
            normal.m22 = n22;

            if (!IsFinite(normal))
            {
                failureReason = "non-finite-normal-matrix";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        private SpaceTransforms(
            Matrix4x4 sourceToAvatarLocal,
            Matrix4x4 sourceToTargetLocal,
            Matrix4x4 sourceToTargetNormal,
            bool isCaptured)
        {
            _sourceToAvatarLocal = sourceToAvatarLocal;
            _sourceToTargetLocal = sourceToTargetLocal;
            _sourceToTargetNormal = sourceToTargetNormal;
            _isCaptured = isCaptured;
        }

        /// <summary>
        /// Determinant magnitude below which a source-to-target transform counts as singular.
        /// </summary>
        /// <remarks>
        /// The determinant of a pure scale is the product of its three scale factors, so this threshold rejects
        /// a collapsed axis (scale 0) and a transform whose combined scale is far below anything a real part
        /// prefab uses, while still accepting a uniformly tiny but invertible part.
        /// </remarks>
        private const float SingularDeterminantEpsilon = 1e-12f;

        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    if (!ApaNumericPolicy.IsFinite(matrix[row, column])) return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The immutable non-identity policy axes a part profile contributes to the merge generator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value here is serialized author data copied out of the profile when the snapshot is built. Nothing
    /// in this type is a heuristic or an inferred default: the defaults are the strict schema-2 behaviour, so a
    /// snapshot built from a version-2 profile policy-resolves nothing and orders exactly as it did before M6.
    /// </para>
    /// <para>
    /// The type is separate from <see cref="PartSnapshot"/> so that adding a policy axis does not grow the
    /// snapshot constructor, and so a consumer that only cares about policy (the merge generator, an authoring
    /// overlay) can take exactly that.
    /// </para>
    /// </remarks>
    public sealed class PartPolicySnapshot
    {
        /// <summary>The strict default every schema-2 profile produces.</summary>
        public static readonly PartPolicySnapshot Default = new PartPolicySnapshot(
            ApaPartSlotMode.Replace,
            0,
            true,
            string.Empty,
            string.Empty,
            false,
            string.Empty,
            string.Empty);

        /// <summary>Legacy slot mode; always the strict default and ignored by current validation.</summary>
        public ApaPartSlotMode SlotMode { get; }

        /// <summary>Legacy conflict priority; always zero for current snapshots.</summary>
        public int ConflictPriority { get; }

        /// <summary>Whether a shape that exists only on this part is accepted (with zero seam deltas).</summary>
        public bool AllowPartOnlyShapes { get; }

        /// <summary>
        /// <b>Legacy.</b> Serialized M6 bone-merge prefix, or empty for none. Recorded so a report can still show
        /// what an old asset declared; the M10 build never consults it.
        /// </summary>
        public string MergePrefix { get; }

        /// <summary><b>Legacy.</b> Serialized M6 bone-merge suffix. See <see cref="MergePrefix"/>.</summary>
        public string MergeSuffix { get; }

        /// <summary><b>Legacy.</b> Whether the M6 build might derive the merge names. Never consulted.</summary>
        public bool InferMergeNames { get; }

        /// <summary>
        /// Avatar-root-relative path of the armature selected as the target for this part's bone identities, or
        /// empty when none is selected.
        /// </summary>
        public string TargetArmaturePath { get; }

        /// <summary>
        /// Part-root-relative path of the armature selected as the scope of this part's own bone identities, or
        /// empty when none is selected.
        /// </summary>
        public string PartArmaturePath { get; }

        /// <summary>Creates a policy snapshot.</summary>
        /// <param name="targetArmaturePath">
        /// The M10 target armature selection. Optional so that every pre-M10 call site keeps compiling and keeps
        /// meaning "not selected".
        /// </param>
        /// <param name="partArmaturePath">The M10 part armature selection. Optional; see above.</param>
        public PartPolicySnapshot(
            ApaPartSlotMode slotMode,
            int conflictPriority,
            bool allowPartOnlyShapes,
            string mergePrefix,
            string mergeSuffix,
            bool inferMergeNames,
            string targetArmaturePath = null,
            string partArmaturePath = null)
        {
            SlotMode = slotMode;
            ConflictPriority = conflictPriority;
            AllowPartOnlyShapes = allowPartOnlyShapes;
            MergePrefix = mergePrefix ?? string.Empty;
            MergeSuffix = mergeSuffix ?? string.Empty;
            InferMergeNames = inferMergeNames;
            TargetArmaturePath = targetArmaturePath ?? string.Empty;
            PartArmaturePath = partArmaturePath ?? string.Empty;
        }

        /// <summary>True when a conflict priority was declared.</summary>
        public bool HasConflictPriority => ConflictPriority != 0;

        /// <summary>True when both M10 armature selections are present.</summary>
        public bool HasArmatureSelection =>
            ApaAvatarPath.HasIdentity(TargetArmaturePath) && ApaAvatarPath.HasIdentity(PartArmaturePath);

        /// <summary>
        /// <b>Legacy.</b> True when an explicit, serialized M6 merge name mapping exists. Never consulted by the
        /// M10 build; see <see cref="MergePrefix"/>.
        /// </summary>
        public bool HasExplicitMergeNames =>
            !string.IsNullOrEmpty(MergePrefix) || !string.IsNullOrEmpty(MergeSuffix);

        /// <summary>
        /// <b>Legacy.</b> True when the M6 build had a deterministic name mapping to apply. Never consulted by
        /// the M10 build, which merges by the armature-relative bone identity instead.
        /// </summary>
        public bool HasDeterministicMergeNames => HasExplicitMergeNames || InferMergeNames;

        /// <summary>The strict default policy.</summary>
        public static PartPolicySnapshot FromProfile(ApaPartProfile profile)
        {
            if (profile == null) return Default;

            var bones = profile.BonesOrNull;
            var blendShapes = profile.BlendShapesOrNull;

            return new PartPolicySnapshot(
                ApaPartSlotMode.Replace,
                0,
                blendShapes == null || blendShapes.AllowPartOnlyShapes,
                bones != null ? bones.MergePrefix : string.Empty,
                bones != null ? bones.MergeSuffix : string.Empty,
                bones != null && bones.InferMergeNames,
                bones != null ? bones.TargetArmaturePath : string.Empty,
                bones != null ? bones.PartArmaturePath : string.Empty);
        }
    }

    /// <summary>
    /// An immutable snapshot of one part: its declared profile data plus its mesh, transforms, and material
    /// assignments, all copied out of Unity objects before any processing begins.
    /// </summary>
    public sealed class PartSnapshot
    {
        /// <summary>Stable part id used for ordering and diagnostics.</summary>
        public string PartId { get; }

        /// <summary>Human-readable part name used in diagnostics.</summary>
        public string DisplayName { get; }

        /// <summary>Declared body slot.</summary>
        public ApaPartSlot Slot { get; }

        /// <summary>Stable ordering key derived from the profile; see <see cref="OrderingKey"/>.</summary>
        public PartOrderingKey OrderingKey { get; }

        /// <summary>The part's mesh data.</summary>
        public MeshSnapshot Mesh { get; }

        /// <summary>
        /// Content fingerprint captured in the owning profile for this part mesh, or empty when the profile
        /// predates schema 5 and has not been recaptured yet.
        /// </summary>
        public string ProfileMeshFingerprint { get; }

        /// <summary>Transforms needed to place the part's data into shared spaces.</summary>
        public SpaceTransforms Transforms { get; }

        /// <summary>The part's declared UV semantics, in declared order.</summary>
        public IReadOnlyList<ApaUvChannelSemantic> UvSemantics { get; }

        /// <summary>The part's declared material slot semantics, in declared order.</summary>
        public IReadOnlyList<ApaMaterialSlotSemantic> MaterialSemantics { get; }

        /// <summary>The base body triangles this part removes, as explicit addresses.</summary>
        public IReadOnlyList<RemovedTriangleAddress> RemovedTriangles { get; }

        /// <summary>The part's declared seam, or null when the part declares no seam.</summary>
        public ApaSeamProfile Seam { get; }

        /// <summary>The material assets assigned to the part's renderer, in submesh order.</summary>
        public IReadOnlyList<Material> RendererMaterials { get; }

        /// <summary>
        /// The serialized policy axes of this part: slot mode, conflict priority, blend shape allowance, and the
        /// bone merge name mapping. Never null; defaults to the strict schema-2 policy.
        /// </summary>
        public PartPolicySnapshot Policy { get; }

        /// <summary>
        /// The live renderer the context builder took this part's geometry from, or null when the snapshot was
        /// built without a live part renderer (a pure-core fixture, or a snapshot taken before the renderer was
        /// resolved).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the identity of "the renderer whose mesh is inside the assembled mesh", captured where that
        /// decision is actually made (<see cref="ContextBuilder.CaptureParts"/>) rather than re-derived later.
        /// Re-deriving it is not equivalent: the capture expression is
        /// <c>GetComponentInChildren&lt;Renderer&gt;(true)</c>, while an enumeration is
        /// <c>GetComponentsInChildren&lt;Renderer&gt;(true)</c>, and the two do not agree on which element comes
        /// first when a part root carries more than one renderer. Treating index zero as "the captured renderer"
        /// therefore mislabels which renderer is the extra one.
        /// </para>
        /// <para>
        /// The reference is read by <see cref="ApaPartConsumptionPlanner"/>, which is the only consumer. It is a
        /// Unity object reference, so it is only valid while the part renderer is alive; the consumption planner
        /// runs before anything is destroyed.
        /// </para>
        /// </remarks>
        public Renderer SourceRenderer { get; }

        /// <summary>Legacy slot mode projection; always Replace.</summary>
        public ApaPartSlotMode SlotMode => ApaPartSlotMode.Replace;

        /// <summary>Legacy conflict-priority projection; always zero.</summary>
        public int ConflictPriority => 0;

        /// <summary>Whether a shape that exists only on this part is accepted.</summary>
        public bool AllowPartOnlyShapes => Policy.AllowPartOnlyShapes;

        /// <summary>Creates a part snapshot.</summary>
        /// <param name="policy">
        /// The serialized policy axes, or null for the strict default. Optional so that every schema-2 call site
        /// keeps compiling and keeps its exact behaviour.
        /// </param>
        /// <param name="sourceRenderer">
        /// The live renderer this part's geometry was captured from, or null when the snapshot was built without
        /// one. See <see cref="SourceRenderer"/> for why the caller must pass the reference the capture used.
        /// </param>
        /// <param name="profileMeshFingerprint">
        /// The serialized fingerprint of the part mesh, or null/empty when no profile value is available. The
        /// optional parameter preserves the pure-core constructor contract used by existing fixtures; build and
        /// authoring capture paths pass the profile value explicitly.
        /// </param>
        public PartSnapshot(
            string partId,
            string displayName,
            ApaPartSlot slot,
            PartOrderingKey orderingKey,
            MeshSnapshot mesh,
            SpaceTransforms transforms,
            IReadOnlyList<ApaUvChannelSemantic> uvSemantics,
            IReadOnlyList<ApaMaterialSlotSemantic> materialSemantics,
            IReadOnlyList<RemovedTriangleAddress> removedTriangles,
            ApaSeamProfile seam,
            IReadOnlyList<Material> rendererMaterials,
            PartPolicySnapshot policy = null,
            Renderer sourceRenderer = null,
            string profileMeshFingerprint = null)
        {
            PartId = partId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Slot = slot;
            OrderingKey = orderingKey;
            Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
            Transforms = transforms;
            UvSemantics = uvSemantics ?? Array.Empty<ApaUvChannelSemantic>();
            MaterialSemantics = materialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>();
            RemovedTriangles = new RemovedTriangleAddressSet(removedTriangles).Addresses;
            Seam = seam;
            RendererMaterials = rendererMaterials ?? Array.Empty<Material>();
            Policy = policy ?? PartPolicySnapshot.Default;
            SourceRenderer = sourceRenderer;
            ProfileMeshFingerprint = profileMeshFingerprint ?? string.Empty;
        }
    }

    /// <summary>
    /// The immutable key that fixes the order in which parts are processed.
    /// Only the stable part id and installer path participate in ordering; legacy policy fields are ignored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key is built from serialized profile identity, never from hierarchy scan order. A part's identity
    /// must survive prefab renaming and being dragged between avatars, so it can be the ordering key; a
    /// hierarchy path cannot, because it changes for reasons that have nothing to do with the asset.
    /// </para>
    /// <para>
    /// <b>The components, in order:</b> declared conflict priority (descending), slot (ascending), stable part
    /// id, display name, and finally the installer's avatar-root-relative path. The display name is a tiebreaker
    /// for degenerate authoring data (two installers that resolve to the same slot <i>and</i> the same stable part
    /// id), which is the same component <c>ApaPartIdentity.Compare</c> uses at the profile level; the installer
    /// path can only be compared once an installer exists, so it is the last component.
    /// </para>
    /// <para>
    /// <b>The target group key is deliberately not a component.</b> A key is only ever compared inside one
    /// group's context (<see cref="ValidationContext.SortParts"/> sorts that group's parts), so every key in a
    /// comparison shares one group key: adding it as a leading component could not change any relative order,
    /// and it would make two identical parts from different groups compare unequal for no benefit. Groups are
    /// ordered by key where they are enumerated (<c>ContextBuilder.BuildGroups</c>), not by this type.
    /// </para>
    /// <para>
    /// <b>The conflict priority is the first component and defaults to zero.</b> With every part at zero — which
    /// is what every schema-2 profile produces — the key degenerates to (slot, part id, display name, installer
    /// path), whose first three components are exactly the pre-M6 key. Declaring a priority on some parts
    /// re-orders the whole group by priority first; that is a determinism change for that group and is
    /// documented as one.
    /// </para>
    /// </remarks>
    public struct PartOrderingKey : IComparable<PartOrderingKey>, IEquatable<PartOrderingKey>
    {
        /// <summary>Legacy conflict-priority field retained for source compatibility; ignored by comparison.</summary>
        public int EffectivePriority;

        /// <summary>Legacy slot field retained for source compatibility; ignored by comparison.</summary>
        public ApaPartSlot Slot;

        /// <summary>Stable part id.</summary>
        public string PartId;

        /// <summary>Legacy display-name field retained for source compatibility; ignored by comparison.</summary>
        public string DisplayName;

        /// <summary>
        /// Avatar-root-relative path of the installer, used only as a final tiebreaker. An installer on the
        /// avatar root records <see cref="ApaAvatarPath.Root"/>.
        /// </summary>
        public string InstallerPath;

        /// <summary>Builds the active ordering key from the stable part id and installer path.</summary>
        public PartOrderingKey(string partId, string installerPath)
            : this(0, ApaPartSlot.Custom, partId, string.Empty, installerPath)
        {
        }

        /// <summary>Builds a key with no declared priority, which is the schema-2 key.</summary>
        public PartOrderingKey(ApaPartSlot slot, string partId, string installerPath)
            : this(0, slot, partId, string.Empty, installerPath)
        {
        }

        /// <summary>Builds a key with an explicit conflict priority and no display name.</summary>
        public PartOrderingKey(int effectivePriority, ApaPartSlot slot, string partId, string installerPath)
            : this(effectivePriority, slot, partId, string.Empty, installerPath)
        {
        }

        /// <summary>Builds a key with an explicit conflict priority and a display name.</summary>
        public PartOrderingKey(
            int effectivePriority,
            ApaPartSlot slot,
            string partId,
            string displayName,
            string installerPath)
        {
            EffectivePriority = effectivePriority;
            Slot = slot;
            PartId = partId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            InstallerPath = installerPath ?? string.Empty;
        }

        /// <inheritdoc />
        public int CompareTo(PartOrderingKey other)
        {
            var c = string.CompareOrdinal(PartId ?? string.Empty, other.PartId ?? string.Empty);
            if (c != 0) return c;
            return string.CompareOrdinal(InstallerPath ?? string.Empty, other.InstallerPath ?? string.Empty);
        }

        /// <inheritdoc />
        public bool Equals(PartOrderingKey other)
        {
            return string.Equals(PartId ?? string.Empty, other.PartId ?? string.Empty, StringComparison.Ordinal)
                   && string.Equals(InstallerPath ?? string.Empty, other.InstallerPath ?? string.Empty, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is PartOrderingKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = PartId != null ? PartId.GetHashCode() : 0;
                hash = (hash * 397) ^ (InstallerPath != null ? InstallerPath.GetHashCode() : 0);
                return hash;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return PartId + " @" + InstallerPath;
        }
    }

    /// <summary>
    /// An immutable snapshot of the body being modified: the target mesh plus its renderer's materials and
    /// transforms.
    /// </summary>
    public sealed class BaseSnapshot
    {
        /// <summary>The target body mesh data.</summary>
        public MeshSnapshot Mesh { get; }

        /// <summary>Transforms needed to place the base data into shared spaces.</summary>
        public SpaceTransforms Transforms { get; }

        /// <summary>The target renderer's material assets, in submesh order.</summary>
        public IReadOnlyList<Material> RendererMaterials { get; }

        /// <summary>
        /// Avatar-root-relative path of the target renderer, for diagnostics, or
        /// <see cref="ApaAvatarPath.Root"/> when the renderer is on the avatar root itself.
        /// </summary>
        /// <remarks>
        /// The empty string means the path was never recorded and must not be read as the avatar root; see
        /// <see cref="ApaAvatarPath"/>.
        /// </remarks>
        public string RendererPath { get; }

        /// <summary>
        /// UV semantics declared for the base body. Empty means "infer the conventional UV0 from channel 0",
        /// which is what a body that has never been configured through the authoring window will report.
        /// </summary>
        public IReadOnlyList<ApaUvChannelSemantic> ExpectedUvSemantics { get; }

        /// <summary>
        /// Material slot semantics declared for the base body. Empty means "infer one semantic per submesh
        /// from the renderer's material order".
        /// </summary>
        public IReadOnlyList<ApaMaterialSlotSemantic> ExpectedMaterialSemantics { get; }

        /// <summary>
        /// The final renderer's <c>localToWorldMatrix</c>, captured with the mesh.
        /// </summary>
        /// <remarks>
        /// This is the captured renderer relation retained for legacy snapshots that do not carry source bind
        /// poses. Normal Unity captures use the mesh's authored bind pose and convert it through
        /// <see cref="Transforms"/> so that a current live bone pose is never treated as the new bind pose.
        /// </remarks>
        public Matrix4x4 RendererLocalToWorld { get; }

        /// <summary>
        /// Avatar-root-relative path of the armature the body's bone paths are recorded against, or empty when
        /// the capture had no scope of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Since M10 a bone identity is its path relative to the armature the author selected, so a consumer that
        /// turns a final bone path back into a live transform has to resolve it under that same object. The build
        /// layer is the one such consumer (<c>ApaBuildTargets.TryResolveBones</c>): on the build clone Modular
        /// Avatar has already merged the part's armature into this one, so every final bone — body-owned or
        /// merged — resolves here.
        /// </para>
        /// <para>
        /// Empty means "no scope was recorded", which is the pre-M10 avatar-root-relative form and the form every
        /// hand-built context has. The consumer resolves such a path against the avatar root, which is exactly
        /// what those callers mean.
        /// </para>
        /// </remarks>
        public string BoneScopePath { get; }

        /// <summary>Creates a base snapshot.</summary>
        /// <param name="boneScopePath">
        /// The armature the body's bone paths are relative to, or null/empty for the avatar-root-relative form.
        /// Optional so that every pre-M10 call site keeps compiling and keeps its exact behaviour.
        /// </param>
        public BaseSnapshot(
            MeshSnapshot mesh,
            SpaceTransforms transforms,
            IReadOnlyList<Material> rendererMaterials,
            string rendererPath,
            IReadOnlyList<ApaUvChannelSemantic> expectedUvSemantics = null,
            IReadOnlyList<ApaMaterialSlotSemantic> expectedMaterialSemantics = null,
            Matrix4x4 rendererLocalToWorld = default,
            string boneScopePath = null)
        {
            Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
            Transforms = transforms;
            RendererMaterials = rendererMaterials ?? Array.Empty<Material>();
            RendererPath = rendererPath ?? string.Empty;
            ExpectedUvSemantics = expectedUvSemantics ?? Array.Empty<ApaUvChannelSemantic>();
            ExpectedMaterialSemantics = expectedMaterialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>();

            // A default-constructed Matrix4x4 is all zeros, which is not a transform. Treating "not supplied"
            // as the identity keeps a hand-built context (a test, or a caller that works purely in one space)
            // meaningful instead of producing a bind pose of zero.
            RendererLocalToWorld = rendererLocalToWorld == default ? Matrix4x4.identity : rendererLocalToWorld;
            BoneScopePath = boneScopePath ?? string.Empty;
        }
    }
}

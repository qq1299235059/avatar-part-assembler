using System;

namespace AvatarPartAssembler
{
    /// <summary>
    /// Stable diagnostic codes. These are a user-facing and test-facing contract: a code's meaning must never
    /// change, and a retired code must never be reused for a different condition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Codes APA001 through APA014 are allocated by the product specification (section 34). Codes APA007,
    /// APA008, and APA011 were reserved for M2 (bones and bind poses) and are emitted by M2 once bone weights,
    /// the final bone table, and bind poses are actually processed. Codes above APA014 were introduced after
    /// section 34; the M2 allocations (APA027 through APA032) are documented in the specification's M2
    /// clarifications. A code's meaning never changes, so a code that was allocated for a later milestone
    /// keeps the meaning it was allocated with.
    /// </para>
    /// <para>
    /// <b>Allocation record.</b> This type is the single allocation table for the whole package, including the
    /// authoring layer: APA033 (<c>INVALID_AUTHORING_PATH</c>), APA034 (<c>NON_PERSISTENT_REFERENCE</c>) and
    /// APA050 (<c>UV_SEMANTIC_CHANNEL_ABSENT</c>) were allocated by M5 and are enumerated here in
    /// <see cref="ApaReservedCodes.Milestone5Authoring"/>; <c>Editor/Authoring/ApaAuthoringErrorCode.cs</c>
    /// carries aliases rather than a second table, so a code can never be allocated twice. M6 allocated APA035
    /// through APA040 (see <see cref="ApaReservedCodes.Milestone6"/>), and M9 allocated APA041
    /// (<c>REMOVAL_MASK_TEXTURE_FAILED</c>, see <see cref="ApaReservedCodes.Milestone9Authoring"/>). M10 allocated
    /// APA042 through APA044 (see <see cref="ApaReservedCodes.Milestone10"/>), and M11 allocated APA045
    /// (<c>SEAM_UV_PRESERVED</c>) and APA046 (<c>PART_ID_DERIVED</c>), see
    /// <see cref="ApaReservedCodes.Milestone11"/>.
    /// M12 allocates APA047 through APA049 for mesh-content fingerprints and seam skinning safety.
    /// M13 allocated APA051 (<c>MERGE_VERTEX_GROUP_INVALID</c>) for the named <c>merge vertex</c> group the
    /// automatic seam generator read; see <see cref="ApaReservedCodes.Milestone13"/>. That representation was
    /// retired by M14 — automatic seam generation now reads mesh vertex colors — so APA051 is kept as a retired
    /// allocation and is no longer emitted by this build, while APA052
    /// (<c>SEAM_CANDIDATE_COLOR_INVALID</c>) is the M14 code the vertex-color candidate contract emits; see
    /// <see cref="ApaReservedCodes.Milestone14"/>.
    /// </para>
    /// </remarks>
    public static class ApaErrorCode
    {
        /// <summary>Base and part seam vertex counts differ.</summary>
        public const string SeamVertexCountMismatch = "APA001";

        /// <summary>A part seam vertex has no matching base seam vertex within the position epsilon.</summary>
        public const string SeamPositionMismatch = "APA002";

        /// <summary>A part seam vertex matches more than one base seam vertex within the position epsilon.</summary>
        public const string SeamDuplicatePositionMatch = "APA003";

        /// <summary>A same-name UV semantic disagrees at a welded seam vertex beyond the UV epsilon.</summary>
        public const string SeamUvMismatch = "APA004";

        /// <summary>Semantic merge requires more than eight final UV channels.</summary>
        public const string UvChannelOverflow = "APA005";

        /// <summary>The target renderer or its mesh could not be resolved.</summary>
        public const string TargetRendererNotFound = "APA006";

        /// <summary>
        /// A required final bone could not be resolved: a bone weight references a bone outside the source's
        /// bone list, or a mesh carries skin data without a usable bone identity.
        /// </summary>
        /// <remarks>
        /// Also the code of the M11 <b>Info</b> summary that reports the opposite outcome — a part bone whose
        /// path already exists in the target table was resolved onto the target's own bone
        /// (<c>reason=part-bone-remapped-to-target</c>). The code names "the resolution of a final bone", and the
        /// informational reading is that resolution succeeding against the body's authoritative entry, so no new
        /// code is allocated for a condition that is not a defect.
        /// </remarks>
        public const string TargetBoneNotFound = "APA007";

        /// <summary>
        /// The resolved bone hierarchy is not usable: a bone has no identity (an empty path, which is what a null
        /// bone entry records — the avatar root's "." token is a real identity, not this case), or two bones of
        /// one source claim the same identity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An identical path is not an ambiguity, and no longer reports this code.</b> Two bones whose paths
        /// relative to the armature selected for their own side are byte-identical name the same joint, so a part
        /// bone that shares a path with a body bone is redirected onto the body's bone: the body's bind pose,
        /// world transform, and final Transform are authoritative and the part's bind transform is discarded.
        /// Before M11 that case blocked as <c>reason=ambiguous-bone-identity</c>, which refused an ordinary part
        /// that merely sat somewhere else at authoring time. The redirect is reported once per part as
        /// <c>APA007 reason=part-bone-remapped-to-target</c>.
        /// </para>
        /// <para>
        /// This code therefore keeps its registered meaning for the conditions that really are ambiguous: a
        /// weighted bone with no identity (<c>reason=bone-without-identity</c>), two weighted bones of one source
        /// sharing a path (<c>reason=duplicate-bone-identity</c>), and a duplicated path inside the target body
        /// itself — including a duplicated body path that no body weight reaches but a part weights, because
        /// there is then no single body bone the part's weights could follow. A duplicated body path that no
        /// weight reaches is still ignored rather than reported.
        /// </para>
        /// </remarks>
        public const string BoneHierarchyConflict = "APA008";

        /// <summary>Two different material assets claim the same semantic under a policy that cannot resolve it.</summary>
        public const string MaterialSemanticConflict = "APA009";

        /// <summary>Two parts declare removal of the same base triangle.</summary>
        public const string RemovalRegionOverlap = "APA010";

        /// <summary>
        /// A bind pose could not be produced: a captured bone transform is missing, zero, or non-finite, or the
        /// product that defines the final bind pose is not a usable matrix.
        /// </summary>
        public const string InvalidBindPose = "APA011";

        /// <summary>The profile is not compatible with the resolved target renderer/mesh.</summary>
        public const string PartProfileIncompatible = "APA012";

        /// <summary>Two non-Custom parts claim the same slot.</summary>
        public const string DuplicatePartSlot = "APA013";

        /// <summary>
        /// The input uses mesh data this build cannot preserve, or a provider was configured that refuses it.
        /// </summary>
        /// <remarks>
        /// Since M2 this no longer covers skinning or blend shapes, which are preserved: it covers a non-triangle
        /// submesh topology, and it is the code the refusing providers emit when a caller deliberately installs
        /// one.
        /// </remarks>
        public const string UnsupportedMeshAttribute = "APA014";

        /// <summary>A serialized profile uses a schema version newer than this build understands.</summary>
        public const string UnknownProfileSchema = "APA015";

        /// <summary>An input value is NaN or infinite.</summary>
        public const string NonFiniteValue = "APA016";

        /// <summary>A non-negative removal triangle index is outside the valid range of its target submesh.</summary>
        public const string RemovalIndexOutOfRange = "APA017";

        /// <summary>Seam indices are duplicated, out of range, or otherwise not a valid vertex set.</summary>
        public const string InvalidSeamSelection = "APA018";

        /// <summary>Two sources declare the same UV or material semantic.</summary>
        public const string DuplicateSemantic = "APA019";

        /// <summary>A semantic name is null, empty, or whitespace.</summary>
        public const string InvalidSemanticName = "APA020";

        /// <summary>A triangle produced by remapping has degenerate geometry.</summary>
        public const string DegenerateOutputTriangle = "APA021";

        /// <summary>A configured epsilon is not a positive finite number.</summary>
        public const string InvalidEpsilon = "APA022";

        /// <summary>The part prefab declares no part slot, or a slot that is inconsistent with its profile.</summary>
        public const string InvalidPartSlot = "APA023";

        /// <summary>
        /// The captured target signature is missing data this build requires to verify topology-indexed
        /// authoring data.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="PartProfileIncompatible"/> on purpose: <c>APA012</c> means "the target is a
        /// different mesh", while this code means "the target may well be the same mesh, but this profile was
        /// captured before the signature recorded enough to prove it". Collapsing the two would tell an author
        /// to re-author against the wrong body. Introduced with schema version 2.
        /// </remarks>
        public const string IncompleteCompatibilitySignature = "APA024";

        /// <summary>Two or more parts contribute different UV values to the same welded vertex.</summary>
        /// <remarks>
        /// A welded vertex is a single output vertex, so it can hold only one value per semantic. Two parts that
        /// disagree are not resolvable by priority: the specification gives the assembler no basis to prefer one
        /// author's seam UV over another's, and picking one silently would ship the wrong texture mapping on the
        /// other part.
        /// </remarks>
        public const string WeldUvConflict = "APA025";

        /// <summary>A removal address is malformed, out of range, or names a submesh that is not a triangle list.</summary>
        /// <remarks>
        /// Distinct from <see cref="RemovalIndexOutOfRange"/>: <c>APA017</c> means "this triangle does not exist
        /// in the submesh you named", while this code means "the address itself cannot be interpreted" — a
        /// negative submesh, a negative triangle, or a submesh whose topology has no triangles to address.
        /// </remarks>
        public const string InvalidTriangleAddress = "APA026";

        /// <summary>One mesh declares the same blend shape name more than once.</summary>
        /// <remarks>
        /// Unity addresses a blend shape by name at animation time, so two shapes with one name are not two
        /// shapes: an animation curve written against that name drives whichever one the runtime resolves
        /// first. Merging them would silently pick a winner, which is what the product principle forbids, so
        /// the duplicate is a blocking error. Introduced with M2.
        /// </remarks>
        public const string BlendShapeDuplicateName = "APA027";

        /// <summary>
        /// A blend shape present on both the base and a part disagrees on frame count or frame weights.
        /// </summary>
        /// <remarks>
        /// A frame index is a position in an array, not an identity (section 43.8). Two same-named shapes whose
        /// frames do not line up cannot be merged into one shape without either inventing a frame or dropping
        /// animation intent, so the conflict blocks. Introduced with M2.
        /// </remarks>
        public const string BlendShapeFrameMismatch = "APA028";

        /// <summary>
        /// A blend shape delta is unusable at a welded seam vertex: a part-only shape has a non-zero delta
        /// there, or a same-name base/part shape disagrees beyond the configured epsilon.
        /// </summary>
        /// <remarks>
        /// The welded output vertex <i>is</i> the base vertex (section 43.2), so a part delta at that vertex has
        /// no representation in the final mesh. A part-only shape with a non-zero seam delta would therefore be
        /// silently ignored, and a same-name shape would silently pick one author's value. Both block.
        /// Introduced with M2.
        /// </remarks>
        public const string BlendShapeSeamDeltaMismatch = "APA029";

        /// <summary>
        /// A blend shape frame's delta arrays are structurally unusable: a delta array does not have exactly one
        /// entry per vertex.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="NonFiniteValue"/>, which covers a NaN or Infinity <i>value</i> inside an
        /// otherwise well-formed array. This code means the array itself cannot be indexed by vertex, which
        /// makes every later read undefined. Introduced with M2.
        /// </remarks>
        public const string InvalidBlendShapeDelta = "APA030";

        /// <summary>
        /// A bone weight is unusable: the weight array does not have one entry per vertex, a weight is
        /// negative, or a vertex's weights sum to zero so the vertex would not deform at all.
        /// </summary>
        /// <remarks>
        /// Out-of-range bone <i>indices</i> are <see cref="TargetBoneNotFound"/> instead, because the defect is
        /// a missing bone rather than a malformed weight. Introduced with M2.
        /// </remarks>
        public const string InvalidBoneWeight = "APA031";

        /// <summary>
        /// A source-to-avatar or source-to-target space transform is missing, non-finite, or singular, so the
        /// source's geometry, normals, and blend shape deltas cannot be materialized.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A singular transform has no inverse transpose, so there is no correct normal matrix: the only
        /// alternatives are a NaN normal or a silently unscaled one. Both would ship a mesh that looks right at
        /// rest and is wrong wherever it is shaded or animated, so the transform blocks before anything reads
        /// it. A missing transform on a live renderer is the same class of defect.
        /// </para>
        /// <para>
        /// Introduced with the M2 review fixes. The detail carries a stable reason token
        /// (<c>missing-transform</c>, <c>non-finite-source-to-avatar</c>, <c>non-finite-source-to-target</c>,
        /// <c>singular-source-to-target</c>, or <c>non-finite-normal-matrix</c>).
        /// </para>
        /// </remarks>
        public const string InvalidSpaceTransform = "APA032";

        /// <summary>
        /// Two parts claimed the same base triangle and an explicit, unequal conflict priority resolved the
        /// overlap in favour of one of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Warning rather than error because the resolved geometry is unambiguous: the removed set is the union
        /// either way (removal is idempotent), and the priority only decides <i>ownership</i> of the region.
        /// It is reported so that a reviewer can see that a declared policy changed the owner of a body region,
        /// which is what the diagnostics, the weld-ownership narrative and any future per-region feature must
        /// agree on.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=removal-overlap-resolved-by-priority</c> plus the owner, the owner
        /// priority, the losing parts with their priorities, and a bounded sample of the resolved addresses.
        /// Introduced with M6.
        /// </para>
        /// </remarks>
        public const string RemovalOverlapResolvedByPriority = "APA035";

        /// <summary>
        /// A source mesh carries a UV channel the profile does not declare, and the channel could not be given a
        /// stable passthrough identity of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An undeclared channel is no longer a defect (M11).</b> The resolver contributes every present
        /// channel: the declared ones under their declared semantic, and the undeclared ones under a generated
        /// passthrough semantic (<c>UV0</c>, <c>UVn</c>, or <c>PassthroughUVn</c> when the conventional name is
        /// already declared). The values therefore reach the assembled mesh, the layer takes part in the
        /// weld/split decision, and two parts that both carry an undeclared channel 0 land on the same final
        /// channel because they land on the same semantic. That case is reported as an <b>Info</b> under this same
        /// code with <c>reason=uv-channel-auto-preserved</c>, because the code's registered subject — "a present
        /// UV channel the profile does not declare" — is exactly what happened, while its severity follows a
        /// condition that is no longer a defect.
        /// </para>
        /// <para>
        /// The blocking form is reserved for the one configuration the resolver must not guess at: every
        /// candidate passthrough name for the channel is already an explicit semantic of the same source, so
        /// naming the channel automatically would merge it into a layer the author declared. That carries
        /// <c>reason=auto-channel-name-taken</c>, and its remedy is to declare the channel explicitly or remove
        /// the unused layer. A layout that the automatic channels push past Unity's eight channels is
        /// <see cref="UvChannelOverflow"/> instead, because the defect there is the channel count.
        /// </para>
        /// <para>
        /// Section 15 of the specification forbids silently discarding UV data. Automatic preservation is what
        /// makes that guarantee hold without refusing the asset: nothing is discarded, so nothing has to block.
        /// </para>
        /// </remarks>
        public const string UndeclaredUvChannel = "APA036";

        /// <summary>
        /// A declared conflict priority is outside its defined domain (it is negative).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Zero means "no priority declared" and is inert. A negative value is not a weaker priority, it is a
        /// value the domain does not define, and silently reinterpreting it (as zero, or as "lowest") would let a
        /// malformed authoring asset change conflict resolution. It blocks instead.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=negative-conflict-priority</c> and the offending value. Introduced with
        /// M6.
        /// </para>
        /// </remarks>
        public const string InvalidConflictPriority = "APA037";

        /// <summary>
        /// A source submesh produced no final material slot, so its triangles cannot be emitted.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="MaterialSemanticConflict"/> on purpose: <c>APA009</c> means "two different
        /// material assets claim one semantic under a policy that cannot resolve it", while this code means "the
        /// material layout has no slot for this submesh at all". Reusing <c>APA009</c> for both would make one
        /// code carry two meanings, which the code contract forbids. Introduced with M6.
        /// </remarks>
        public const string SubMeshWithoutMaterialSlot = "APA038";

        /// <summary>
        /// An installer was skipped because it is not active for build: the component is disabled, its
        /// GameObject is inactive, or <see cref="AvatarPartInstaller.EnabledForBuild"/> is false.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Informational: parking a part is a legitimate authoring action, not a defect. The part is reported
        /// rather than silently ignored so that a disabled part cannot look like a part that was installed.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=inactive-installer</c> and which of the three conditions held. Introduced
        /// with M6.
        /// </para>
        /// </remarks>
        public const string InactiveInstallerSkipped = "APA039";

        /// <summary>
        /// A part declares a blend shape that exists only on that part while its profile sets
        /// <c>AllowPartOnlyShapes</c> to false.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Distinct from <see cref="BlendShapeSeamDeltaMismatch"/> on purpose: <c>APA029</c> means "a blend shape
        /// delta cannot survive the weld" (a non-zero part-only seam delta, or a same-name shape whose values
        /// disagree), while this code means "the part's declared policy refuses the shape outright, whatever its
        /// deltas are". The two conditions have different remedies — re-author the shape versus enable the policy
        /// or move the shape to the base — so they must not share a code, and <c>APA029</c>'s title describes
        /// only the first.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=part-only-shape-disallowed</c>, the shape name and the policy value.
        /// Introduced with M6.
        /// </para>
        /// </remarks>
        public const string PartOnlyShapeDisallowed = "APA040";

        /// <summary>
        /// An authoring output path cannot name a Unity asset: it is empty, absolute, outside <c>Assets/</c>,
        /// contains a parent segment, or does not end with the extension the asset type requires.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Emitted by the authoring layer only; the core never sees an output path. Allocated by M5 and
        /// consolidated here by M7 (<see cref="ApaReservedCodes.Milestone5Authoring"/>); the authoring alias is
        /// <c>ApaAuthoringErrorCode.InvalidAuthoringPath</c>.
        /// </para>
        /// <para>The detail carries <c>reason=</c> tokens such as <c>empty-path</c> and <c>not-asset-relative</c>.</para>
        /// </remarks>
        public const string InvalidAuthoringPath = "APA033";

        /// <summary>
        /// A value that must be serialized into a reusable asset (a material reference in a profile, or an object
        /// reference in the part hierarchy that is about to become a prefab) resolves to a scene object.
        /// </summary>
        /// <remarks>
        /// Emitted by the authoring layer only. The value cannot survive outside the scene, so writing it would
        /// silently drop or corrupt the reference. Allocated by M5; the authoring alias is
        /// <c>ApaAuthoringErrorCode.NonPersistentReference</c>.
        /// </remarks>
        public const string NonPersistentReference = "APA034";

        /// <summary>
        /// A UV semantic declares a source channel the part mesh does not carry.
        /// </summary>
        /// <remarks>
        /// Emitted by the authoring layer only: the assembler writes a channel only when the source mesh has it
        /// (otherwise every vertex silently receives the channel default), so the declaration is refused rather
        /// than saved as data that does nothing. Distinct from <see cref="UndeclaredUvChannel"/>, which is the
        /// build's "a present channel has no declaration" rule; this code is the authoring "a declaration has no
        /// channel" rule. Allocated by M5 above the M6 range; the authoring alias is
        /// <c>ApaAuthoringErrorCode.UvSemanticChannelAbsent</c>.
        /// </remarks>
        public const string UvSemanticChannelAbsent = "APA050";

        /// <summary>
        /// A black/white texture mask could not be converted into a removal triangle selection.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Emitted by the authoring layer only, by the mask-to-triangle conversion the authoring window drives.
        /// The conversion reads a UV channel of the target mesh and a grayscale of a mask texture; this code
        /// reports every way that read can legitimately fail before a single triangle is selected: a null mesh
        /// or mask, an unreadable target mesh, an out-of-range or absent UV channel, a UV channel whose entry
        /// count disagrees with the vertex count, a non-finite or out-of-range threshold, a triangle index
        /// outside its submesh, a non-finite UV, a mask format or texture type the Editor readback cannot
        /// sample, and a readback the graphics device refused.
        /// </para>
        /// <para>
        /// The distinction from <see cref="InvalidTriangleAddress"/> and <see cref="RemovalIndexOutOfRange"/>
        /// is deliberate: those describe a removal <i>address</i> that is already stored in a mask, while this
        /// code describes a mask <i>source</i> that produced no address at all. One code carrying two meanings is
        /// what the allocation contract forbids, so the conversion has its own. The failing condition is carried
        /// by a stable <c>reason=…</c> token in the detail, so a report, a test, and a log stay comparable.
        /// </para>
        /// <para>
        /// Allocated by M9 above the M6 range, in the authoring-layer block that APA050 also occupies; the
        /// authoring alias is <c>ApaAuthoringErrorCode.RemovalMaskTextureFailed</c>.
        /// </para>
        /// </remarks>
        public const string RemovalMaskTextureFailed = "APA041";

        /// <summary>
        /// A seam loop pair was authored before explicit world-position pairing, so the two index lists are two
        /// unordered sets rather than pairs and cannot be paired without guessing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Before M10 the correspondence between the base loop and the part loop was re-derived at build time by
        /// matching positions in avatar-root local space. That derivation is not reproducible once the pairing is
        /// supposed to be the author's explicit decision: the same avatar-local epsilon means a different world
        /// distance on every scaled hierarchy level, and two unordered sets can be paired several ways. Reading
        /// an old profile's two sets as if position <c>i</c> of one named position <c>i</c> of the other would
        /// silently weld every seam vertex to an unrelated one, which is the class of corruption the strict seam
        /// contract exists to prevent.
        /// </para>
        /// <para>
        /// The remedy is one explicit action in the Part Authoring window (generate the seam from world
        /// positions), so the diagnostic names that action rather than offering a migration.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=seam-pairing-required</c> and the two cardinalities. Allocated by M10.
        /// </para>
        /// </remarks>
        public const string SeamPairingRequired = "APA042";

        /// <summary>
        /// The merge-armature selection is unusable: an armature was not selected, its recorded path does not
        /// resolve, or the selected object is outside the root it must belong to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A bone identity is now the bone's path relative to the armature the author selected for its side, so
        /// both selections are load-bearing: without them there is no scope to record a bone path in, and no
        /// defensible way to decide which part bone is the same joint as which body bone. The conditions share
        /// this code because they share one remedy — select the correct armature in the Part Authoring window —
        /// and each carries its own stable <c>reason=…</c> token:
        /// <c>missing-target-armature</c>, <c>missing-part-armature</c>, <c>target-armature-not-found</c>,
        /// <c>part-armature-not-found</c>, <c>target-armature-outside-root</c>,
        /// <c>part-armature-outside-root</c>, <c>conflicting-target-armature-paths</c>,
        /// <c>null-target-armature</c>, <c>null-part-armature</c>, <c>part-top-bone-outside-armature</c>,
        /// <c>target-armature-inside-part-armature</c>, and <c>merge-target-is-part</c>. The last three come from
        /// the merge planner, which places the transient Modular Avatar component and reports a placement that
        /// would merge nothing or would be circular.
        /// </para>
        /// <para>
        /// Distinct from <see cref="TargetBoneNotFound"/> (a required bone cannot be resolved at all) and from
        /// <see cref="BoneOutsideSelectedArmature"/>, which is about a bone rather than about the selection.
        /// Allocated by M10. The window's selection check and the build's context builder resolve through one
        /// routine (<c>ApaArmatureScope.TryResolve</c>), so both surfaces report the same token for the same
        /// condition.
        /// </para>
        /// </remarks>
        public const string ArmatureSelectionInvalid = "APA043";

        /// <summary>
        /// A skinned source carries weight on a bone that is not inside the armature selected for that source,
        /// so the bone has no identity in the armature-relative scheme the merge matches on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only bones an effective influence actually reaches are checked, so an unreferenced bone slot that
        /// happens to sit outside the armature cannot block. A weighted bone outside the selected armature is a
        /// real defect: its path cannot be recorded relative to the armature, so a part bone could never merge
        /// with the body bone it belongs to and the weights would follow an appended duplicate instead.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=bone-outside-armature</c> plus the bone index and name. Allocated by M10.
        /// </para>
        /// </remarks>
        public const string BoneOutsideSelectedArmature = "APA044";

        /// <summary>
        /// A seam pair kept its own UV values because a same-name UV semantic disagreed at the pair, so the part
        /// seam vertex is emitted as a preserved split vertex instead of being welded onto the base vertex.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Informational, and the reason <c>APA004</c> is no longer emitted for a same-name UV disagreement: a
        /// part and a body routinely use different UV atlases, so a seam that matches in space and disagrees in
        /// UV is a normal input rather than a modelling defect. The condition is reported once per part and
        /// semantic with the number of pairs it affected, so a report stays readable instead of carrying one line
        /// per seam vertex.
        /// </para>
        /// <para>
        /// <c>APA004</c> keeps its registered meaning (a same-name UV semantic disagrees at a <i>welded</i> seam
        /// vertex) and is reserved for a mismatch that cannot be represented by preserving a vertex; the automatic
        /// preservation path reports this code instead. One code carrying two meanings is what the allocation
        /// contract forbids, so the new behaviour received its own code rather than reusing <c>APA004</c> with a
        /// different severity.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=uv-seam-preserved</c>, the semantic, the number of preserved pair(s), and
        /// the largest observed difference. Allocated by M11.
        /// </para>
        /// </remarks>
        public const string SeamUvPreserved = "APA045";

        /// <summary>
        /// A profile carries no stored part identifier, so a stable identifier was derived from the profile
        /// asset's GUID.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Informational: the derived identifier is deterministic, so preview, validation, ordering, the build,
        /// and a repeated domain reload all resolve the same part identity and a legacy profile installs without
        /// the user having to understand what a GUID is. It is reported so that the one action which makes the
        /// identifier explicit — saving the profile once in the Part Authoring window, or pressing
        /// <i>Repair Part Id</i> in the installer inspector — is discoverable rather than invisible.
        /// </para>
        /// <para>
        /// The detail carries <c>reason=part-id-derived-from-asset-guid</c> and the derived identifier. Allocated
        /// by M11.
        /// </para>
        /// </remarks>
        public const string PartIdDerived = "APA046";

        /// <summary>A profile has no deterministic content fingerprint for the mesh it describes.</summary>
        /// <remarks>
        /// The profile may have been authored by a pre-fingerprint build, or the capture could not read the
        /// mesh. A GUID and topology summary are not enough to prove that a reimport preserved the attributes the
        /// assembler consumes, so the author must capture the profile again.
        /// </remarks>
        public const string ProfileMeshFingerprintMissing = "APA047";

        /// <summary>The live mesh content differs from the fingerprint stored in the profile.</summary>
        /// <remarks>
        /// This is deliberately separate from <see cref="PartProfileIncompatible"/>: the remedy is to recapture
        /// the profile after an intentional mesh change, not to search for a different renderer.
        /// </remarks>
        public const string ProfileMeshFingerprintMismatch = "APA048";

        /// <summary>A seam vertex carries an effective weight to a bone the target avatar does not declare.</summary>
        /// <remarks>
        /// A UV-preserved seam vertex is still animated independently. Allowing it to reference a part-only or
        /// unbound bone makes the two sides separate when that bone moves, so the part author must use only bones
        /// present in the target armature at a seam.
        /// </remarks>
        public const string SeamWeightBoneNotInTarget = "APA049";

        /// <summary>
        /// The named <c>merge vertex</c> group of a renderer cannot be resolved into candidate vertices.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Retired by M14; never emitted by this build.</b> The named <c>merge vertex</c> representation — an
        /// <c>ApaMergeVertexGroup</c> component, or a skinned bone of that name with positive weights — was
        /// replaced by the vertex-color candidate contract, because Unity import pipelines routinely drop
        /// non-bone vertex groups while preserving mesh vertex colors. The constant is kept, with its registered
        /// meaning and title unchanged, so the code is never reused for a different condition; the condition that
        /// replaced it is <see cref="SeamCandidateColorInvalid"/> (<c>APA052</c>), which has its own meaning: a
        /// mesh's vertex colors cannot be resolved into candidates. A reader who finds <c>APA051</c> in an old log
        /// or an old profile therefore still reads the right thing, and nothing in the current workflow can
        /// produce it.
        /// </para>
        /// <para>
        /// The condition it named was the whole contract of "which vertices may pair": the renderer declared no
        /// group at all, declared it in a representation that carried no data (an empty component list, or a bone
        /// that weighted no vertex positively), declared it ambiguously (two bones shared the name), or declared
        /// indices the mesh could not address (out of range, repeated, or recorded against a mesh of a different
        /// size). Every one of them had the same remedy — fix the group on the source or in the component — and
        /// the same alternative the contract forbade, which is silently pairing every vertex instead.
        /// </para>
        /// <para>
        /// The failing condition was carried by a stable <c>reason=…</c> token in the detail
        /// (<c>merge-vertex-group-missing</c>, <c>merge-vertex-group-not-skinned</c>,
        /// <c>merge-vertex-group-empty</c>, <c>merge-vertex-group-no-weighted-vertices</c>,
        /// <c>merge-vertex-group-ambiguous-bone</c>, <c>merge-vertex-group-index-out-of-range</c>,
        /// <c>merge-vertex-group-duplicate-index</c>, <c>merge-vertex-group-vertex-count-mismatch</c>, and
        /// <c>merge-vertex-group-weight-count-mismatch</c>). Allocated by M13 above the M12 range.
        /// </para>
        /// </remarks>
        public const string MergeVertexGroupInvalid = "APA051";

        /// <summary>
        /// The vertex-color seam candidate contract of a renderer cannot be resolved into candidate vertices.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Emitted by the authoring layer only, by the automatic world-position seam generator and by the Scene
        /// View candidate overlay, because the candidate color is an authoring <i>input</i>: the build consumes
        /// the explicit seam pairs the generator writes, never the mesh's vertex colors. The condition is the
        /// whole contract of "which vertices may pair" since M14: the mesh carries no vertex color at all, its
        /// color array does not have exactly one entry per vertex, or no vertex carries the selected candidate
        /// color. Every one of them has the same remedy — paint the seam vertices with the candidate color (or
        /// select the color the mesh stores) — and the same alternative the contract forbids, which is silently
        /// pairing every vertex instead.
        /// </para>
        /// <para>
        /// Distinct from the retired <see cref="MergeVertexGroupInvalid"/> (<c>APA051</c>) on purpose: that code
        /// names a <i>named group</i> — a component list or a bone — while this one names a mesh's stored
        /// <i>colors</i>. The two conditions have different inputs and different remedies, so one code must not
        /// carry both meanings; the M14 replacement of the representation therefore allocated a new code rather
        /// than reusing the retired one.
        /// </para>
        /// <para>
        /// The failing condition is carried by a stable <c>reason=…</c> token in the detail
        /// (<c>vertex-color-missing</c>, <c>vertex-color-count-mismatch</c>, <c>no-vertex-color-candidates</c>,
        /// <c>empty-mesh</c>, and <c>vertex-color-candidates-unresolved</c>), so a report, a test, and a log stay
        /// comparable without a code per condition. Allocated by M14 above the M13 range.
        /// </para>
        /// </remarks>
        public const string SeamCandidateColorInvalid = "APA052";

        /// <summary>
        /// A protected part-mesh payload could not be authenticated, decoded, or verified, so no geometry was used.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One code, one remedy, one stable reason token.</b> The protected payload is a binary envelope with a
        /// version, a codec identifier, a salt, an IV, a ciphertext, and an authentication tag, and every way it can
        /// fail has the same remedy: recreate the protected part prefab from the source mesh, or restore the asset
        /// from version control. The failing condition is therefore carried by a stable <c>reason=…</c> token in the
        /// detail (<c>protected-mesh-missing</c>, <c>unsupported-format-version</c>, <c>unsupported-codec</c>,
        /// <c>incomplete-envelope</c>, <c>invalid-length</c>, <c>authentication-failed</c>, <c>invalid-padding</c>,
        /// <c>plaintext-length-mismatch</c>, <c>bad-magic</c>, <c>unsupported-payload-version</c>,
        /// <c>length-limit-exceeded</c>, <c>count-mismatch</c>, <c>truncated-payload</c>, <c>trailing-garbage</c>,
        /// <c>index-out-of-range</c>, <c>invalid-topology</c>, <c>invalid-index-format</c>, <c>malformed-string</c>,
        /// <c>empty-payload</c>, and <c>part-id-mismatch</c>) rather than by a code per condition, exactly like the
        /// vertex-color candidate contract's <c>APA052</c>.
        /// </para>
        /// <para>
        /// <b>It is always blocking and never falls back.</b> A payload that fails this check produces no snapshot:
        /// the part is not assembled from a stale mesh, from the null renderer's geometry, or from every vertex.
        /// A payload whose content is intact but whose mesh no longer matches the profile is <i>not</i> this code —
        /// that is <see cref="ProfileMeshFingerprintMismatch"/> (<c>APA048</c>), which names the one action that
        /// fixes it (recapture the profile after an intentional mesh change).
        /// </para>
        /// <para>Allocated by M15 above the M14 range.</para>
        /// </remarks>
        public const string ProtectedMeshInvalid = "APA053";

        /// <summary>
        /// A protected part prefab would still depend on the source mesh or its model file, so it was not written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Emitted by the authoring layer only, by the protected prefab creation path. Clearing the part
        /// renderer's mesh reference is not enough on its own: another component — a collider, a second renderer, an
        /// avatar asset imported from the same <c>.fbx</c> — can keep the source file in the prefab's dependency
        /// graph, and publishing that prefab would ship the very asset the protection exists to withhold. The
        /// condition is carried by a stable <c>reason=…</c> token (<c>source-mesh-reference</c> for a component
        /// reference found before the save, <c>source-mesh-dependency</c> for a dependency found in the saved
        /// asset), and the diagnostic names the component, the property, and the asset path that must be removed.
        /// </para>
        /// <para>Allocated by M15 above the M14 range.</para>
        /// </remarks>
        public const string ProtectedMeshSourceLeak = "APA054";

        /// <summary>An unexpected exception escaped the assembler. Always accompanied by the exception detail.</summary>
        public const string InternalError = "APA999";
        /// <summary>
        /// Returns a short, human-readable title for a code. Used by diagnostics rendering; returns an empty
        /// string for unrecognized codes rather than throwing.
        /// </summary>
        public static string GetTitle(string code)
        {
            switch (code)
            {
                case SeamVertexCountMismatch: return "SEAM_VERTEX_COUNT_MISMATCH";
                case SeamPositionMismatch: return "SEAM_POSITION_MISMATCH";
                case SeamDuplicatePositionMatch: return "SEAM_DUPLICATE_POSITION_MATCH";
                case SeamUvMismatch: return "SEAM_UV_MISMATCH";
                case UvChannelOverflow: return "UV_CHANNEL_OVERFLOW";
                case TargetRendererNotFound: return "TARGET_RENDERER_NOT_FOUND";
                case TargetBoneNotFound: return "TARGET_BONE_NOT_FOUND";
                case BoneHierarchyConflict: return "BONE_HIERARCHY_CONFLICT";
                case MaterialSemanticConflict: return "MATERIAL_SEMANTIC_CONFLICT";
                case RemovalRegionOverlap: return "REMOVAL_REGION_OVERLAP";
                case InvalidBindPose: return "INVALID_BINDPOSE";
                case PartProfileIncompatible: return "PART_PROFILE_INCOMPATIBLE";
                case DuplicatePartSlot: return "DUPLICATE_PART_SLOT";
                case UnsupportedMeshAttribute: return "UNSUPPORTED_MESH_ATTRIBUTE";
                case UnknownProfileSchema: return "UNKNOWN_PROFILE_SCHEMA";
                case NonFiniteValue: return "NON_FINITE_VALUE";
                case RemovalIndexOutOfRange: return "REMOVAL_INDEX_OUT_OF_RANGE";
                case InvalidSeamSelection: return "INVALID_SEAM_SELECTION";
                case DuplicateSemantic: return "DUPLICATE_SEMANTIC";
                case InvalidSemanticName: return "INVALID_SEMANTIC_NAME";
                case DegenerateOutputTriangle: return "DEGENERATE_OUTPUT_TRIANGLE";
                case InvalidEpsilon: return "INVALID_EPSILON";
                case InvalidPartSlot: return "INVALID_PART_SLOT";
                case IncompleteCompatibilitySignature: return "INCOMPLETE_COMPATIBILITY_SIGNATURE";
                case WeldUvConflict: return "WELD_UV_CONFLICT";
                case InvalidTriangleAddress: return "INVALID_TRIANGLE_ADDRESS";
                case BlendShapeDuplicateName: return "BLENDSHAPE_DUPLICATE_NAME";
                case BlendShapeFrameMismatch: return "BLENDSHAPE_FRAME_MISMATCH";
                case BlendShapeSeamDeltaMismatch: return "BLENDSHAPE_SEAM_DELTA_MISMATCH";
                case InvalidBlendShapeDelta: return "INVALID_BLENDSHAPE_DELTA";
                case InvalidBoneWeight: return "INVALID_BONE_WEIGHT";
                case InvalidSpaceTransform: return "INVALID_SPACE_TRANSFORM";
                case RemovalOverlapResolvedByPriority: return "REMOVAL_OVERLAP_RESOLVED_BY_PRIORITY";
                case UndeclaredUvChannel: return "UNDECLARED_UV_CHANNEL";
                case InvalidConflictPriority: return "INVALID_CONFLICT_PRIORITY";
                case SubMeshWithoutMaterialSlot: return "SUBMESH_WITHOUT_MATERIAL_SLOT";
                case InactiveInstallerSkipped: return "INACTIVE_INSTALLER_SKIPPED";
                case PartOnlyShapeDisallowed: return "PART_ONLY_SHAPE_DISALLOWED";
                case InvalidAuthoringPath: return "INVALID_AUTHORING_PATH";
                case NonPersistentReference: return "NON_PERSISTENT_REFERENCE";
                case UvSemanticChannelAbsent: return "UV_SEMANTIC_CHANNEL_ABSENT";
                case RemovalMaskTextureFailed: return "REMOVAL_MASK_TEXTURE_FAILED";
                case SeamPairingRequired: return "SEAM_PAIRING_REQUIRED";
                case ArmatureSelectionInvalid: return "ARMATURE_SELECTION_INVALID";
                case BoneOutsideSelectedArmature: return "BONE_OUTSIDE_SELECTED_ARMATURE";
                case SeamUvPreserved: return "SEAM_UV_PRESERVED";
                case PartIdDerived: return "PART_ID_DERIVED";
                case ProfileMeshFingerprintMissing: return "PROFILE_MESH_FINGERPRINT_MISSING";
                case ProfileMeshFingerprintMismatch: return "PROFILE_MESH_FINGERPRINT_MISMATCH";
                case SeamWeightBoneNotInTarget: return "SEAM_WEIGHT_BONE_NOT_IN_TARGET";
                case MergeVertexGroupInvalid: return "MERGE_VERTEX_GROUP_INVALID";
                case SeamCandidateColorInvalid: return "SEAM_CANDIDATE_COLOR_INVALID";
                case ProtectedMeshInvalid: return "PROTECTED_MESH_INVALID";
                case ProtectedMeshSourceLeak: return "PROTECTED_MESH_SOURCE_LEAK";
                case InternalError: return "INTERNAL_ERROR";
                default: return string.Empty;
            }
        }
    }

    /// <summary>
    /// The allocation record for the package's diagnostic codes, grouped by the milestone that introduced them.
    /// Used by documentation and tests so that a reviewer can tell "not implemented yet" apart from
    /// "deliberately never implemented".
    /// </summary>
    public static class ApaReservedCodes
    {
        /// <summary>
        /// Codes section 34 allocated for M2 (skinning, bind poses). M2 implements and emits them; the array is
        /// kept as the allocation record so a reader can still see which milestone introduced a code and a later
        /// milestone cannot silently renumber one.
        /// </summary>
        public static readonly string[] Milestone2 =
        {
            ApaErrorCode.TargetBoneNotFound,
            ApaErrorCode.BoneHierarchyConflict,
            ApaErrorCode.InvalidBindPose
        };

        /// <summary>
        /// Codes allocated but deliberately not produced by this build. Empty at the release candidate: every
        /// code allocated so far is either emitted by the code that owns it (M2's bone/bind-pose codes, M5's
        /// authoring codes, M6's multi-part policy codes, M9's texture-mask authoring code, M10's
        /// armature-selection, out-of-scope-bone, and seam-pairing codes, M11's seam-preservation and
        /// derived-part-id codes, M12's fingerprint and seam-skinning codes, and M14's vertex-color candidate
        /// code) or <b>retired</b> — <c>APA051</c> names the named <c>merge vertex</c> group M14 replaced, so it
        /// stays allocated with its meaning and title unchanged and is simply no longer emitted. A retired code is
        /// not "reserved for a later milestone": this array stays empty, because a later milestone must never
        /// produce a retired code's condition under a new meaning. A later
        /// milestone adds its allocation array beside <see cref="Milestone6"/> rather than repopulating this one,
        /// which keeps each code's introducing milestone readable.
        /// </summary>
        public static readonly string[] LaterMilestones = new string[0];

        /// <summary>
        /// Codes allocated by M6 (multi-part policy, target grouping, and conflict priority). Kept as the
        /// allocation record so the next milestone cannot silently renumber one, and so the authoring table that
        /// owns APA033/APA034 cannot collide with them.
        /// </summary>
        /// <remarks>
        /// <c>APA040</c> was allocated by the M6 review fixes: <c>AllowPartOnlyShapes</c> was initially wired to
        /// <c>APA029</c>, whose registered meaning and title describe only the blend-shape seam-delta condition.
        /// One code carrying two meanings is exactly what the allocation contract forbids, so the part-only-shape
        /// refusal received its own code.
        /// <para>
        /// <c>APA036</c> keeps its M6 allocation while its severity became conditional in M11: an undeclared
        /// present channel is now preserved automatically and reported as an <b>Info</b>, and the blocking form is
        /// reserved for a channel no passthrough name can represent. The code is not renumbered and its subject is
        /// unchanged, which is what the allocation contract requires.
        /// </para>
        /// </remarks>
        public static readonly string[] Milestone6 =
        {
            ApaErrorCode.RemovalOverlapResolvedByPriority,
            ApaErrorCode.UndeclaredUvChannel,
            ApaErrorCode.InvalidConflictPriority,
            ApaErrorCode.SubMeshWithoutMaterialSlot,
            ApaErrorCode.InactiveInstallerSkipped,
            ApaErrorCode.PartOnlyShapeDisallowed
        };

        /// <summary>
        /// Codes allocated by M5 for the authoring layer, consolidated into <see cref="ApaErrorCode"/> by M7.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These are the only codes this package emits outside the build pipeline. They describe a defect in
        /// authoring <i>input</i> rather than in an assembly input, which is why they are not aliases of an
        /// existing code: <c>APA033</c> is about an output path (which the core never sees), <c>APA034</c> about a
        /// reference that cannot survive serialization, and <c>APA050</c> about a declaration that names a channel
        /// the mesh does not carry.
        /// </para>
        /// <para>
        /// The authoring table aliases these constants; it must never allocate a code of its own, because a second
        /// table is exactly how two conditions end up sharing one code with two meanings.
        /// </para>
        /// </remarks>
        public static readonly string[] Milestone5Authoring =
        {
            ApaErrorCode.InvalidAuthoringPath,
            ApaErrorCode.NonPersistentReference,
            ApaErrorCode.UvSemanticChannelAbsent
        };

        /// <summary>
        /// Codes allocated by M9 for the authoring layer: the black/white texture-mask removal selection.
        /// </summary>
        /// <remarks>
        /// Kept as its own allocation record for the same reason as <see cref="Milestone6"/>: a later milestone
        /// reads which milestone introduced a code, and a milestone that adds codes never renumbers an existing
        /// one. The mask conversion emits exactly one code, with the failing condition carried by a stable
        /// <c>reason=…</c> token rather than by a second code, because every one of those conditions has the same
        /// remedy — fix the mask source or the target selection — and one code with a reason token is easier to
        /// search than eight near-synonyms.
        /// </remarks>
        public static readonly string[] Milestone9Authoring =
        {
            ApaErrorCode.RemovalMaskTextureFailed
        };

        /// <summary>
        /// Codes allocated by M10: the two explicit Armature selections the bone identity is scoped to
        /// (<c>APA043</c>, <c>APA044</c>) and the seam-pairing refusal that replaced the build-time position
        /// match (<c>APA042</c>).
        /// </summary>
        /// <remarks>
        /// Kept as its own allocation record for the same reason as <see cref="Milestone9Authoring"/>: a later
        /// milestone reads which milestone introduced a code, and a milestone that adds codes never renumbers an
        /// existing one. <c>APA045</c> and above are free; the next free code is <c>APA045</c>.
        /// </remarks>
        public static readonly string[] Milestone10 =
        {
            ApaErrorCode.SeamPairingRequired,
            ApaErrorCode.ArmatureSelectionInvalid,
            ApaErrorCode.BoneOutsideSelectedArmature
        };

        /// <summary>
        /// Codes allocated by M11: attribute-aware seam merging and the derived part identifier.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>APA045</c> is the informational counterpart of <c>APA004</c>: a same-name UV disagreement at a seam
        /// pair is now represented by preserving the part's seam vertex, so it is reported once per part and
        /// semantic instead of once per vertex, and it no longer blocks. <c>APA046</c> reports that a profile
        /// carried no stored part identifier and a deterministic one was derived from the profile asset's GUID.
        /// </para>
        /// <para>
        /// Kept as its own allocation record for the same reason as <see cref="Milestone10"/>: a later milestone
        /// reads which milestone introduced a code, and a milestone that adds codes never renumbers an existing
        /// one. M12 owns the next three codes; no later milestone may reuse them.
        /// </para>
        /// </remarks>
        public static readonly string[] Milestone11 =
        {
            ApaErrorCode.SeamUvPreserved,
            ApaErrorCode.PartIdDerived
        };

        /// <summary>Codes allocated by M12 for mesh content identity and seam skinning safety.</summary>
        public static readonly string[] Milestone12 =
        {
            ApaErrorCode.ProfileMeshFingerprintMissing,
            ApaErrorCode.ProfileMeshFingerprintMismatch,
            ApaErrorCode.SeamWeightBoneNotInTarget
        };

        /// <summary>
        /// Codes allocated by M13: the named <c>merge vertex</c> group the automatic seam generator read.
        /// </summary>
        /// <remarks>
        /// Kept as its own allocation record for the same reason as the earlier arrays: a later milestone reads
        /// which milestone introduced a code, and a milestone that adds codes never renumbers an existing one.
        /// <c>APA051</c> was an authoring-layer code — the build never saw a vertex group — emitted only by the
        /// retired <c>ApaMergeVertexGroupResolver</c>, with the failing condition carried by a <c>reason=…</c>
        /// token rather than by a code per condition.
        /// <para>
        /// <b>Retired by M14.</b> The named-group representation was replaced by the vertex-color candidate
        /// contract, so nothing in this build emits <c>APA051</c>. The code stays allocated, and its registered
        /// meaning and title are unchanged, because a retired code must never be reused for a different
        /// condition; the M14 contract has its own code in <see cref="Milestone14"/>.
        /// </para>
        /// </remarks>
        public static readonly string[] Milestone13 =
        {
            ApaErrorCode.MergeVertexGroupInvalid
        };

        /// <summary>
        /// Codes allocated by M14: the vertex-color seam candidate contract that replaced the named
        /// <c>merge vertex</c> group.
        /// </summary>
        /// <remarks>
        /// Kept as its own allocation record for the same reason as the earlier arrays: a later milestone reads
        /// which milestone introduced a code, and a milestone that adds codes never renumbers an existing one.
        /// <c>APA052</c> is an authoring-layer code — the build never sees a vertex color as a candidate set — so
        /// it is emitted only by <c>ApaSeamVertexColorCandidates</c> and <c>ApaSeamMergeCheck</c>, and the failing
        /// condition is carried by a <c>reason=…</c> token rather than by a code per condition.
        /// </remarks>
        public static readonly string[] Milestone14 =
        {
            ApaErrorCode.SeamCandidateColorInvalid
        };

        /// <summary>
        /// Codes allocated by M15: the protected part-mesh payload and its distribution-leak refusal.
        /// </summary>
        /// <remarks>
        /// Kept as its own allocation record for the same reason as the earlier arrays: a later milestone reads
        /// which milestone introduced a code, and a milestone that adds codes never renumbers an existing one.
        /// <c>APA053</c> carries every payload failure behind a <c>reason=…</c> token, because all of them share
        /// one remedy; <c>APA054</c> is the authoring-layer refusal that stops a protected prefab from being
        /// published while it still depends on the source mesh or its model file.
        /// </remarks>
        public static readonly string[] Milestone15 =
        {
            ApaErrorCode.ProtectedMeshInvalid,
            ApaErrorCode.ProtectedMeshSourceLeak
        };

        /// <summary>Returns true when the code was allocated for the M12 safety work.</summary>
        public static bool IsMilestone12Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone12.Length; i++)
            {
                if (string.Equals(Milestone12[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M13 named merge-vertex group work.</summary>
        public static bool IsMilestone13Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone13.Length; i++)
            {
                if (string.Equals(Milestone13[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M14 vertex-color candidate work.</summary>
        public static bool IsMilestone14Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone14.Length; i++)
            {
                if (string.Equals(Milestone14[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M11 seam-preservation/part-id work.</summary>
        public static bool IsMilestone11Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone11.Length; i++)
            {
                if (string.Equals(Milestone11[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M10 armature/seam authoring workflow.</summary>
        public static bool IsMilestone10Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone10.Length; i++)
            {
                if (string.Equals(Milestone10[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M9 texture-mask authoring workflow.</summary>
        public static bool IsMilestone9AuthoringCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone9Authoring.Length; i++)
            {
                if (string.Equals(Milestone9Authoring[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M5 authoring layer.</summary>
        public static bool IsMilestone5AuthoringCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone5Authoring.Length; i++)
            {
                if (string.Equals(Milestone5Authoring[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code is allocated but deliberately not produced by this build.</summary>
        public static bool IsReservedForLaterMilestone(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < LaterMilestones.Length; i++)
            {
                if (string.Equals(LaterMilestones[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M2 milestone.</summary>
        public static bool IsMilestone2Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone2.Length; i++)
            {
                if (string.Equals(Milestone2[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Returns true when the code was allocated for the M6 milestone.</summary>
        public static bool IsMilestone6Code(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            for (var i = 0; i < Milestone6.Length; i++)
            {
                if (string.Equals(Milestone6[i], code, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }
}

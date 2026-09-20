using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace AvatarPartAssembler
{
    /// <summary>
    /// Legacy serialized slot values. Current authoring and build logic do not consume part slots.
    /// </summary>
    public enum ApaPartSlot
    {
        Head = 0,
        Torso = 1,
        LeftArm = 2,
        RightArm = 3,
        LeftHand = 4,
        RightHand = 5,
        LeftLeg = 6,
        RightLeg = 7,
        LeftFoot = 8,
        RightFoot = 9,

        /// <summary>
        /// A slot outside the standard body list. <see cref="ApaPartSlot.Custom"/> is the only slot that may be
        /// claimed by more than one part; two non-Custom parts claiming the same slot is <c>APA013</c> unless
        /// their <see cref="ApaPartSlotMode"/> makes the sharing explicit.
        /// </summary>
        Custom = 10
    }

    /// <summary>Legacy serialized slot-mode values retained for old profile compatibility.</summary>
    /// <remarks>
    /// <para>
    /// The default <see cref="Replace"/> keeps the M2 rule: at most one part may replace a standard body region,
    /// and two parts that both replace the same slot are <c>APA013</c>. <see cref="Augment"/> is the explicit,
    /// deterministic way to attach a part (a hat, hair, a tail, an accessory) to a body region another part
    /// already owns, without falling back to <see cref="ApaPartSlot.Custom"/> and losing the region identity.
    /// </para>
    /// <para>
    /// An augmenting part does not own a body region, so it may not declare a removal set
    /// (<c>APA013 reason=augment-declares-removal</c>). It may still weld, merge UV and material semantics,
    /// contribute bones, and contribute blend shapes.
    /// </para>
    /// <para>Introduced with M6 (schema version 3).</para>
    /// </remarks>
    public enum ApaPartSlotMode
    {
        /// <summary>This part replaces the body region. At most one part per non-Custom slot may do this.</summary>
        Replace = 0,

        /// <summary>This part attaches to the body region without claiming exclusive ownership of it.</summary>
        Augment = 1
    }

    /// <summary>
    /// How a part's material for a given semantic is reconciled with the base avatar's material.
    /// See the specification section 18 and the clarifications section 43.5 for exact behaviour.
    /// </summary>
    public enum ApaMaterialPolicyMode
    {
        /// <summary>Same semantic and same material asset merge; same semantic and different assets conflict (<c>APA009</c>).</summary>
        Auto = 0,

        /// <summary>Part triangles for this semantic use the base avatar's material.</summary>
        UseTarget = 1,

        /// <summary>The part's material is kept in an additional slot, even when the semantic matches the base.</summary>
        KeepPart = 2,

        /// <summary>An additional slot is always created for the part's material.</summary>
        ForceNew = 3
    }

    /// <summary>
    /// One UV layer declared by a source mesh, mapped to the plugin's own semantic name.
    /// </summary>
    /// <remarks>
    /// The semantic name, not the Unity channel index, is the identity. Two sources may store <c>UVMap</c> in
    /// different channels and still merge into one final channel.
    /// </remarks>
    [Serializable]
    public sealed class ApaUvChannelSemantic
    {
        [SerializeField] private string _semantic = string.Empty;
        [SerializeField] private int _sourceChannel;

        /// <summary>Author-facing semantic name. Normalized by <see cref="ApaSemanticName"/> before use.</summary>
        public string Semantic
        {
            get => _semantic;
            set => _semantic = value ?? string.Empty;
        }

        /// <summary>Zero-based channel index in the source mesh (0 through 7).</summary>
        public int SourceChannel
        {
            get => _sourceChannel;
            set => _sourceChannel = value;
        }

        /// <summary>Creates an empty entry. Required by Unity serialization.</summary>
        public ApaUvChannelSemantic()
        {
        }

        /// <summary>Creates an entry.</summary>
        public ApaUvChannelSemantic(string semantic, int sourceChannel)
        {
            _semantic = semantic ?? string.Empty;
            _sourceChannel = sourceChannel;
        }
    }

    /// <summary>
    /// One material slot declared by a source mesh, mapped to the plugin's own semantic name.
    /// </summary>
    /// <remarks>
    /// The material asset name is deliberately not used as the semantic. A material may be renamed or upgraded
    /// without breaking the semantic identity that parts match on.
    /// </remarks>
    [Serializable]
    public sealed class ApaMaterialSlotSemantic
    {
        [SerializeField] private string _semantic = string.Empty;
        [SerializeField] private int _sourceSubMesh;
        [SerializeField] private Material _material;
        [SerializeField] private ApaMaterialPolicyMode _policy = ApaMaterialPolicyMode.Auto;

        /// <summary>Author-facing semantic name. Normalized by <see cref="ApaSemanticName"/> before use.</summary>
        public string Semantic
        {
            get => _semantic;
            set => _semantic = value ?? string.Empty;
        }

        /// <summary>Zero-based submesh index in the source mesh.</summary>
        public int SourceSubMesh
        {
            get => _sourceSubMesh;
            set => _sourceSubMesh = value;
        }

        /// <summary>The material asset referenced by this slot. Never mutated by the assembler.</summary>
        public Material Material
        {
            get => _material;
            set => _material = value;
        }

        /// <summary>Conflict policy applied when this semantic also exists on the base.</summary>
        public ApaMaterialPolicyMode Policy
        {
            get => _policy;
            set => _policy = value;
        }

        /// <summary>Creates an empty entry. Required by Unity serialization.</summary>
        public ApaMaterialSlotSemantic()
        {
        }

        /// <summary>Creates an entry.</summary>
        public ApaMaterialSlotSemantic(string semantic, int sourceSubMesh, Material material, ApaMaterialPolicyMode policy)
        {
            _semantic = semantic ?? string.Empty;
            _sourceSubMesh = sourceSubMesh;
            _material = material;
            _policy = policy;
        }
    }

    /// <summary>
    /// One base-body triangle, addressed explicitly by submesh and by triangle index within that submesh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The address is deliberately two indices rather than one flat integer. A flat "triangle index" is only
    /// unambiguous if every consumer agrees on the same enumeration, and a body mesh routinely has several
    /// triangle submeshes. A single flat index silently meant "triangle 0 of every submesh" in the first draft
    /// of this package, which could remove geometry from submeshes the author never selected.
    /// </para>
    /// <para>
    /// A triangle index is only meaningful against the exact mesh it was authored against, so every address is
    /// guarded by the compatibility signature (<see cref="ApaAvatarCompatibilityProfile"/>).
    /// </para>
    /// <para>
    /// The type is a struct with value equality because addresses are used as set members and as issue detail
    /// keys, and because a value type makes the immutability of <c>RemovedTriangleAddressSet</c> structural.
    /// Zero-initializing it therefore yields the valid address (0, 0); use
    /// <see cref="RemovedTriangleAddress.None"/> when "no address" is meant.
    /// </para>
    /// </remarks>
    [Serializable]
    public struct RemovedTriangleAddress : IEquatable<RemovedTriangleAddress>, IComparable<RemovedTriangleAddress>
    {
        [SerializeField] private int _subMeshIndex;
        [SerializeField] private int _triangleIndexWithinSubMesh;

        /// <summary>
        /// The address that no triangle can occupy, because a submesh index is never negative. Used as the
        /// "unset" value in editor tooling; it never passes validation.
        /// </summary>
        public static readonly RemovedTriangleAddress None = new RemovedTriangleAddress(-1, -1);

        /// <summary>Zero-based index of the target mesh's submesh.</summary>
        public int SubMeshIndex
        {
            get => _subMeshIndex;
            set => _subMeshIndex = value;
        }

        /// <summary>
        /// Zero-based index of the triangle within that submesh, counting triangle lists in index order. Each
        /// triangle occupies three consecutive entries, so the index buffer offset is this value times three.
        /// </summary>
        public int TriangleIndexWithinSubMesh
        {
            get => _triangleIndexWithinSubMesh;
            set => _triangleIndexWithinSubMesh = value;
        }

        /// <summary>Creates an address.</summary>
        public RemovedTriangleAddress(int subMeshIndex, int triangleIndexWithinSubMesh)
        {
            _subMeshIndex = subMeshIndex;
            _triangleIndexWithinSubMesh = triangleIndexWithinSubMesh;
        }

        /// <inheritdoc />
        public bool Equals(RemovedTriangleAddress other)
        {
            return _subMeshIndex == other._subMeshIndex
                   && _triangleIndexWithinSubMesh == other._triangleIndexWithinSubMesh;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is RemovedTriangleAddress other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return (_subMeshIndex * 397) ^ _triangleIndexWithinSubMesh;
            }
        }

        /// <summary>Value equality. Provided so that <c>==</c> on addresses cannot mean reference comparison.</summary>
        public static bool operator ==(RemovedTriangleAddress a, RemovedTriangleAddress b) => a.Equals(b);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(RemovedTriangleAddress a, RemovedTriangleAddress b) => !a.Equals(b);

        /// <summary>
        /// Deterministic ordering: submesh first, then triangle within the submesh.
        /// </summary>
        /// <remarks>
        /// Applying removal in this order visits the target mesh in its own memory order, which keeps the
        /// removal pass linear and makes the resulting vertex retention independent of authoring order.
        /// </remarks>
        public int CompareTo(RemovedTriangleAddress other)
        {
            var c = _subMeshIndex.CompareTo(other._subMeshIndex);
            if (c != 0) return c;
            return _triangleIndexWithinSubMesh.CompareTo(other._triangleIndexWithinSubMesh);
        }

        /// <summary>The stable diagnostic detail form of this address.</summary>
        public override string ToString()
        {
            return "submesh=" + _subMeshIndex + "; triangle=" + _triangleIndexWithinSubMesh;
        }
    }

    /// <summary>
    /// An immutable, canonically ordered set of <see cref="RemovedTriangleAddress"/> values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set is sorted ascending and free of duplicates at construction, so two profiles that declare the same
    /// triangles in different orders serialize and compare identically. The exposed array is never handed out
    /// directly: <see cref="ToArray"/> returns a copy, which is what makes the "immutable" claim true at the API
    /// boundary instead of being a comment.
    /// </para>
    /// <para>
    /// This type is Runtime and has no Editor dependency, because it is part of the authoring contract that a
    /// built part prefab carries.
    /// </para>
    /// </remarks>
    public sealed class RemovedTriangleAddressSet
    {
        private readonly RemovedTriangleAddress[] _addresses;
        private readonly ReadOnlyCollection<RemovedTriangleAddress> _view;

        /// <summary>An empty set.</summary>
        public static readonly RemovedTriangleAddressSet Empty =
            new RemovedTriangleAddressSet(Array.Empty<RemovedTriangleAddress>());

        /// <summary>Creates a canonical set from arbitrary addresses. Null entries are dropped.</summary>
        public RemovedTriangleAddressSet(IEnumerable<RemovedTriangleAddress> addresses)
        {
            if (addresses == null)
            {
                _addresses = Array.Empty<RemovedTriangleAddress>();
                _view = Array.AsReadOnly(_addresses);
                return;
            }

            var list = new List<RemovedTriangleAddress>();
            foreach (var address in addresses) list.Add(address);

            list.Sort();

            var count = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0 && list[i].Equals(list[i - 1])) continue;
                list[count++] = list[i];
            }

            if (count != list.Count) list.RemoveRange(count, list.Count - count);
            _addresses = list.ToArray();
            _view = Array.AsReadOnly(_addresses);
        }

        /// <summary>Number of addresses in the set.</summary>
        public int Count => _addresses.Length;

        /// <summary>True when no triangles are removed.</summary>
        public bool IsEmpty => _addresses.Length == 0;

        /// <summary>
        /// The addresses, in ascending canonical order. Reads are direct because the array is private and never
        /// exposed for mutation.
        /// </summary>
        public IReadOnlyList<RemovedTriangleAddress> Addresses => _view;

        /// <summary>Indexer over the canonical order.</summary>
        public RemovedTriangleAddress this[int index] => _addresses[index];

        /// <summary>True when the set contains the address. Binary search over the canonical order.</summary>
        public bool Contains(RemovedTriangleAddress address)
        {
            return Array.BinarySearch(_addresses, address) >= 0;
        }

        /// <summary>Returns a copy of the canonical addresses. Safe to hand to callers.</summary>
        public RemovedTriangleAddress[] ToArray()
        {
            var copy = new RemovedTriangleAddress[_addresses.Length];
            Array.Copy(_addresses, copy, _addresses.Length);
            return copy;
        }

        /// <summary>True when the two sets contain exactly the same addresses.</summary>
        public bool SetEquals(RemovedTriangleAddressSet other)
        {
            if (other == null) return _addresses.Length == 0;
            if (_addresses.Length != other._addresses.Length) return false;

            for (var i = 0; i < _addresses.Length; i++)
            {
                if (!_addresses[i].Equals(other._addresses[i])) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The base body triangles a part removes, stored as an explicit, canonically ordered address set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stored as ascending de-duplicated addresses rather than a bitmask array so that the serialized form is
    /// identical on every platform and has no padding-dependent content. The set is only meaningful against the
    /// exact mesh recorded in the compatibility signature.
    /// </para>
    /// <para>
    /// <b>Schema history.</b> Schema version 1 stored <c>int[]</c> triangle indices with no submesh component,
    /// which made them ambiguous on any mesh with more than one triangle submesh. Schema version 2 replaced
    /// them with this address set. <c>ApaPartProfile.TryMigrate</c> must fail rather than reinterpret old data,
    /// because a version-1 index cannot be resolved to a submesh without guessing — see section 43.4.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaRemovalProfile
    {
        [SerializeField] private int[] _subMeshIndices = Array.Empty<int>();
        [SerializeField] private int[] _triangleIndices = Array.Empty<int>();

        /// <summary>
        /// The removed triangles, in ascending canonical order.
        /// </summary>
        /// <remarks>
        /// The two parallel arrays are the serialized form; this property is the only way the rest of the
        /// package reads them, so that the canonical ordering and the parallelism of the arrays are established
        /// in one place rather than trusted at each call site.
        /// </remarks>
        public RemovedTriangleAddressSet RemovedTriangles
        {
            get
            {
                var subMeshes = _subMeshIndices ?? Array.Empty<int>();
                var triangles = _triangleIndices ?? Array.Empty<int>();

                // A mismatched pair cannot be repaired without guessing which entry is missing, so it is
                // reported as empty and caught by the removal rule's consistency check instead of being
                // silently truncated to the shorter array.
                if (subMeshes.Length != triangles.Length) return RemovedTriangleAddressSet.Empty;

                var addresses = new RemovedTriangleAddress[subMeshes.Length];
                for (var i = 0; i < subMeshes.Length; i++)
                {
                    addresses[i] = new RemovedTriangleAddress(subMeshes[i], triangles[i]);
                }

                return new RemovedTriangleAddressSet(addresses);
            }
            set
            {
                var canonical = value ?? RemovedTriangleAddressSet.Empty;
                var addresses = canonical.ToArray();

                var subMeshes = new int[addresses.Length];
                var triangles = new int[addresses.Length];
                for (var i = 0; i < addresses.Length; i++)
                {
                    subMeshes[i] = addresses[i].SubMeshIndex;
                    triangles[i] = addresses[i].TriangleIndexWithinSubMesh;
                }

                _subMeshIndices = subMeshes;
                _triangleIndices = triangles;
            }
        }

        /// <summary>
        /// True when the two serialized arrays have different lengths, which means the asset was edited or
        /// written by something other than this package. Always a hard failure.
        /// </summary>
        public bool HasCorruptStorage
        {
            get
            {
                var subMeshes = _subMeshIndices ?? Array.Empty<int>();
                var triangles = _triangleIndices ?? Array.Empty<int>();
                return subMeshes.Length != triangles.Length;
            }
        }

        /// <summary>Number of triangles the profile removes.</summary>
        public int Count => RemovedTriangles.Count;

        /// <summary>Creates an empty removal profile.</summary>
        public ApaRemovalProfile()
        {
        }

        /// <summary>Creates a removal profile from addresses.</summary>
        public ApaRemovalProfile(IEnumerable<RemovedTriangleAddress> addresses)
        {
            RemovedTriangles = new RemovedTriangleAddressSet(addresses);
        }

        /// <summary>
        /// Creates a removal profile that targets one submesh. Used by authoring tooling and by tests, where the
        /// submesh is known and only the triangle indices vary.
        /// </summary>
        public static ApaRemovalProfile ForSubMesh(int subMeshIndex, IEnumerable<int> triangleIndices)
        {
            var addresses = new List<RemovedTriangleAddress>();
            if (triangleIndices != null)
            {
                foreach (var triangle in triangleIndices)
                {
                    addresses.Add(new RemovedTriangleAddress(subMeshIndex, triangle));
                }
            }

            return new ApaRemovalProfile(addresses);
        }
    }

    /// <summary>
    /// One side of the seam contract: an explicit set of vertex indices forming the seam loop.
    /// </summary>
    [Serializable]
    public sealed class ApaSeamSide
    {
        [SerializeField] private int[] _vertexIndices = Array.Empty<int>();

        /// <summary>
        /// Vertex indices forming the seam. Order is not significant; correspondence is established by position.
        /// Indices must be unique and in range.
        /// </summary>
        public int[] VertexIndices
        {
            get => _vertexIndices ?? Array.Empty<int>();
            set => _vertexIndices = value ?? Array.Empty<int>();
        }

        /// <summary>Number of vertices in the seam loop.</summary>
        public int Count => VertexIndices.Length;

        /// <summary>Creates an empty side.</summary>
        public ApaSeamSide()
        {
        }

        /// <summary>Creates a side from explicit indices.</summary>
        public ApaSeamSide(int[] vertexIndices)
        {
            _vertexIndices = vertexIndices ?? Array.Empty<int>();
        }
    }

    /// <summary>
    /// The strict seam contract for one part: a base-side loop and a part-side loop of equal cardinality.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two lists are paired by position, and that is a versioned property of the data.</b> Since M10 the
    /// authoring window writes the correspondence it computed from world positions, so <c>Base.VertexIndices[i]</c>
    /// and <c>Part.VertexIndices[i]</c> are one welded pair; the build consumes exactly that and never re-derives
    /// it. <see cref="PairingVersion"/> records that fact.
    /// </para>
    /// <para>
    /// A profile written before M10 carries two <i>unordered sets</i>: its loops were matched by position in
    /// avatar-root local space at build time. Those two sets cannot be read as pairs, and re-deriving the
    /// pairing is not reproducible (the same avatar-local epsilon is a different world distance on every scaled
    /// hierarchy level), so <see cref="PairingVersion"/> is left at
    /// <see cref="LegacyUnpairedVersion"/> and the seam rule refuses the profile with <c>APA042</c> until the
    /// author regenerates the seam once.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaSeamProfile
    {
        /// <summary>
        /// <see cref="PairingVersion"/> value of a profile whose two lists are unordered sets rather than pairs.
        /// </summary>
        /// <remarks>
        /// It is also the serialized default, which is what makes an existing asset read as legacy without being
        /// rewritten: Unity does not run field initializers on data loaded from disk, and a missing field reads
        /// as zero.
        /// </remarks>
        public const int LegacyUnpairedVersion = 0;

        /// <summary>
        /// <see cref="PairingVersion"/> value written by this build: position <c>i</c> of each list is one pair.
        /// </summary>
        public const int ExplicitPairingVersion = 1;

        [SerializeField] private ApaSeamSide _base = new ApaSeamSide();
        [SerializeField] private ApaSeamSide _part = new ApaSeamSide();
        [SerializeField] private int _pairingVersion;

        /// <summary>The retained base body seam loop.</summary>
        public ApaSeamSide Base
        {
            get => _base ?? (_base = new ApaSeamSide());
            set => _base = value ?? new ApaSeamSide();
        }

        /// <summary>The part seam loop that will be welded onto the base loop.</summary>
        public ApaSeamSide Part
        {
            get => _part ?? (_part = new ApaSeamSide());
            set => _part = value ?? new ApaSeamSide();
        }

        /// <summary>
        /// How the two lists relate: <see cref="ExplicitPairingVersion"/> means they are pairs by position,
        /// <see cref="LegacyUnpairedVersion"/> means they are two unordered sets this build refuses to pair.
        /// </summary>
        /// <remarks>
        /// Stored as an <c>int</c> rather than an enum so that a value written by a newer build is preserved and
        /// reported verbatim instead of being reinterpreted as the nearest defined member. A future version is
        /// refused by the seam rule rather than read as the version this build understands.
        /// </remarks>
        public int PairingVersion
        {
            get => _pairingVersion;
            set => _pairingVersion = value;
        }

        /// <summary>True when the two lists are explicit, position-by-position pairs.</summary>
        public bool HasExplicitPairing => _pairingVersion == ExplicitPairingVersion;

        /// <summary>True when both loops are empty, which means "this part declares no seam".</summary>
        public bool IsEmpty => Base.Count == 0 && Part.Count == 0;

        /// <summary>
        /// Writes the paired form: both lists in pair order, tagged with <see cref="ExplicitPairingVersion"/>.
        /// </summary>
        /// <remarks>
        /// The only writer of the paired form. A caller cannot produce paired lists while leaving the version at
        /// the legacy value, which is what makes the version a statement about the data rather than a hint.
        /// </remarks>
        public void SetPaired(int[] baseIndices, int[] partIndices)
        {
            Base = new ApaSeamSide(baseIndices);
            Part = new ApaSeamSide(partIndices);
            _pairingVersion = ExplicitPairingVersion;
        }
    }

    /// <summary>
    /// Identifies the target body renderer by mesh signature rather than by GameObject name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Populated at authoring time and re-verified before any topology-indexed data is used. The assembler
    /// validates against this signature; it never rewrites it to match whatever mesh it happens to find.
    /// </para>
    /// <para>
    /// <b>The GUID is diagnostic context, never a bypass.</b> The topology, blend shape, and bone data below
    /// are always compared, because a mesh asset keeps its GUID across a reimport that changes its contents.
    /// Trusting the GUID alone would let a reimported body silently invalidate every triangle, seam, and
    /// channel index authored against it, which is exactly the corruption this signature exists to prevent
    /// (section 43.4).
    /// </para>
    /// <para>
    /// The data below is split into <i>safety</i> fields and <i>advisory</i> fields. A mismatch in a safety
    /// field blocks with <c>APA012</c>; a mismatch in an advisory field is reported separately and does not
    /// block, because it cannot change which triangle a stored index refers to.
    /// </para>
    /// <para>
    /// <b>The content fingerprint is a safety field.</b> It covers the mesh attributes the assembler reads, so
    /// a reimport that preserves the asset GUID but changes UVs, weights, or blend-shape deltas is still detected.
    /// A profile without one is readable for migration diagnostics but cannot be applied until it is recaptured.
    /// </para>
    /// <para>
    /// <b>The bone path list is a safety field whenever a source carries skinning data.</b> It is the one field
    /// whose severity depends on the configuration: with weights anywhere in the group the final bone table is
    /// rebuilt from the live hierarchy, so a part authored against a different hierarchy is merged against bones
    /// it was never authored for and the mismatch blocks (<c>reason=bone-signature-mismatch</c>). With nothing
    /// skinned there are no weights to remap and no bone table to build, so the same difference stays an
    /// advisory warning (<c>reason=bone-signature-advisory</c>) — a staleness signal rather than a remap input.
    /// The live <c>RendererPath</c> comparison is a safety field for the same reason and blocks on its own.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaAvatarCompatibilityProfile
    {
        /// <summary>Mesh GUID comparison value, exposed so callers and tests never re-derive the rule.</summary>
        public const string GuidFastPath = "guid";

        [SerializeField] private string _meshGuid = string.Empty;
        [SerializeField] private string _meshFingerprint = string.Empty;
        [SerializeField] private string _meshName = string.Empty;
        [SerializeField] private string _rendererPath = string.Empty;
        [SerializeField] private int _vertexCount = -1;
        [SerializeField] private int[] _subMeshIndexCounts = Array.Empty<int>();

        /// <summary>
        /// Serialized as <c>int</c> rather than as <see cref="UnityEngine.MeshTopology"/>. A serialized enum
        /// silently becomes an arbitrary integer when it is written by a newer build or edited by hand, and an
        /// out-of-range topology would otherwise be compared as "different but plausible".
        /// </summary>
        [SerializeField] private int[] _subMeshTopologies = Array.Empty<int>();

        [SerializeField] private string[] _blendShapeNames = Array.Empty<string>();
        [SerializeField] private int[] _blendShapeFrameCounts = Array.Empty<int>();

        /// <summary>
        /// Avatar-root-relative bone paths in bone order. Serialized as a path list, not as references, so that
        /// the signature is reproducible from text and cannot depend on instance ids or visit order.
        /// </summary>
        [SerializeField] private string[] _bonePaths = Array.Empty<string>();

        [SerializeField] private bool _captured;

        /// <summary>Asset GUID of the target mesh when it is an asset; empty otherwise.</summary>
        public string MeshGuid
        {
            get => _meshGuid;
            set => _meshGuid = value ?? string.Empty;
        }

        /// <summary>
        /// Deterministic content fingerprint of the target mesh. Unlike <see cref="MeshGuid"/>, this changes when
        /// a reimport changes mesh data while retaining the asset identity.
        /// </summary>
        public string MeshFingerprint
        {
            get => _meshFingerprint ?? string.Empty;
            set => _meshFingerprint = value ?? string.Empty;
        }

        /// <summary>Name of the target mesh. Diagnostic fallback only.</summary>
        public string MeshName
        {
            get => _meshName;
            set => _meshName = value ?? string.Empty;
        }

        /// <summary>
        /// Hierarchy path of the target renderer relative to the avatar root.
        /// </summary>
        /// <remarks>
        /// The avatar root itself is recorded as <see cref="ApaAvatarPath.Root"/> (<c>"."</c>), not as an empty
        /// string. Empty means "no path was recorded" and is never resolved, so a legacy profile that wrote the
        /// root as empty stays unsupported rather than being guessed at. A caller that resolves this path back to
        /// a transform must special-case the root token, because <c>Transform.Find</c> reserves <c>"."</c>.
        /// </remarks>
        public string RendererPath
        {
            get => _rendererPath;
            set => _rendererPath = value ?? string.Empty;
        }

        /// <summary>Expected vertex count of the target mesh. -1 means "not captured".</summary>
        public int VertexCount
        {
            get => _vertexCount;
            set => _vertexCount = value;
        }

        /// <summary>Per-submesh index counts, in submesh order. Safety field.</summary>
        public int[] SubMeshIndexCounts
        {
            get => _subMeshIndexCounts ?? Array.Empty<int>();
            set => _subMeshIndexCounts = value ?? Array.Empty<int>();
        }

        /// <summary>
        /// Per-submesh topology as <see cref="MeshTopology"/> values, in submesh order. Safety field.
        /// </summary>
        /// <remarks>
        /// Topology is a safety field rather than a diagnostic one because a triangle count alone does not
        /// identify a submesh: two submeshes can hold the same number of indices with one of them a triangle
        /// list and the other a line list. Treating them as interchangeable would let a removal address resolve
        /// to a different triangle.
        /// </remarks>
        public MeshTopology[] SubMeshTopologies
        {
            get
            {
                var stored = _subMeshTopologies ?? Array.Empty<int>();
                var result = new MeshTopology[stored.Length];
                for (var i = 0; i < stored.Length; i++) result[i] = (MeshTopology)stored[i];
                return result;
            }
            set
            {
                var topologies = value;
                if (topologies == null)
                {
                    _subMeshTopologies = Array.Empty<int>();
                    return;
                }

                var stored = new int[topologies.Length];
                for (var i = 0; i < topologies.Length; i++) stored[i] = (int)topologies[i];
                _subMeshTopologies = stored;
            }
        }

        /// <summary>
        /// The raw topology values as serialized, so a malformed value can be reported verbatim instead of
        /// being shown as a meaningless enum name.
        /// </summary>
        public int[] SubMeshTopologyValues
        {
            get => _subMeshTopologies ?? Array.Empty<int>();
            set => _subMeshTopologies = value ?? Array.Empty<int>();
        }

        /// <summary>
        /// Blend shape names present on the target mesh, in mesh order. Safety field, because M2 indexes
        /// blend shape frames by position.
        /// </summary>
        public string[] BlendShapeNames
        {
            get => _blendShapeNames ?? Array.Empty<string>();
            set => _blendShapeNames = value ?? Array.Empty<string>();
        }

        /// <summary>
        /// Frame count per blend shape, in mesh order. Safety field: the same shape name with a different frame
        /// count is a different animation curve, and a stored frame weight would address the wrong one.
        /// </summary>
        public int[] BlendShapeFrameCounts
        {
            get => _blendShapeFrameCounts ?? Array.Empty<int>();
            set => _blendShapeFrameCounts = value ?? Array.Empty<int>();
        }

        /// <summary>
        /// Armature-root-relative bone paths, in bone order. Safety field whenever a source is skinned.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The paths are recorded against the target armature the profile selects
        /// (<see cref="ApaBoneProfile.TargetArmaturePath"/>), not against the avatar root, because that is the
        /// scope the final bone table merges in since M10. An empty entry means the bone was null, or that it
        /// lies outside the selected armature and therefore has no identity in this scope. A bone that is the
        /// armature root itself is recorded as <see cref="ApaAvatarPath.Root"/> (<c>"."</c>), which is a valid
        /// identity; the two stay distinct, so a profile written by this version compares equal to the live
        /// signature for an armature-root bone.
        /// </para>
        /// <para>
        /// Advisory rather than safety before M6 in the sense that M2 builds the final bone table from the live
        /// captured hierarchy and the profile stores no bone index, so a renamed bone cannot mis-resolve stored
        /// data. Since M6 the comparison blocks whenever a source in the configuration carries skinning data,
        /// because the armature merge then rebuilds the table from bones that must line up with these paths.
        /// </para>
        /// </remarks>
        public string[] BonePaths
        {
            get => _bonePaths ?? Array.Empty<string>();
            set => _bonePaths = value ?? Array.Empty<string>();
        }

        /// <summary>
        /// The bone signature as a value object, for callers that do not want to touch the raw array.
        /// </summary>
        public BoneSignature Bones => new BoneSignature(BonePaths);

        /// <summary>
        /// True once a signature has actually been captured. An uncaptured signature must not be treated as
        /// "matches everything"; it is reported as a compatibility failure so that stale authoring data cannot
        /// be applied by accident.
        /// </summary>
        public bool IsCaptured
        {
            get => _captured;
            set => _captured = value;
        }

        /// <summary>True when a mesh GUID was recorded for diagnostics and authoring provenance.</summary>
        public bool HasMeshGuid => !string.IsNullOrEmpty(_meshGuid);

        /// <summary>True when the schema-5 content fingerprint is present.</summary>
        public bool HasMeshFingerprint => !string.IsNullOrEmpty(_meshFingerprint);

        /// <summary>True when per-submesh topology was recorded by this capture.</summary>
        public bool HasSubMeshTopologies => (_subMeshTopologies?.Length ?? 0) > 0;

        /// <summary>True when blend shape frame counts were recorded by this capture.</summary>
        public bool HasBlendShapeFrameCounts => (_blendShapeFrameCounts?.Length ?? 0) > 0;

        /// <summary>True when bone paths were recorded by this capture.</summary>
        public bool HasBonePaths => (_bonePaths?.Length ?? 0) > 0;

        /// <summary>
        /// True when this signature carries every safety-relevant field the compatibility rule compares.
        /// </summary>
        /// <remarks>
        /// A signature written by an older build lacks the newer fields. There is no correct way to compare a
        /// field that was never captured, so the incompleteness is reported as a compatibility failure rather
        /// than passed over: passing it over would restore exactly the ambiguity the fields were added to
        /// remove. The remedy is to re-author, which is what the diagnostic says.
        /// </remarks>
        public bool HasCompleteSafetyData
        {
            get
            {
                if (!_captured) return false;
                if (_vertexCount < 0) return false;
                if (_subMeshIndexCounts == null || _subMeshIndexCounts.Length == 0) return false;

                // Submesh metadata is three parallel arrays. A capture that recorded some of them is as
                // unusable as one that recorded none, because the missing third cannot be inferred.
                if (_subMeshTopologies == null || _subMeshTopologies.Length != _subMeshIndexCounts.Length)
                {
                    return false;
                }

                if (_blendShapeNames == null) return false;
                if (_blendShapeFrameCounts == null) return false;
                if (_blendShapeNames.Length != _blendShapeFrameCounts.Length) return false;

                return true;
            }
        }

        /// <summary>
        /// A short, stable description of what this signature recorded. Used in diagnostics so an author can
        /// tell "the body changed" apart from "the profile is too old to be checked".
        /// </summary>
        public string DescribeCapture()
        {
            return "captured=" + (_captured ? "true" : "false")
                   + "; vertices=" + _vertexCount
                   + "; submeshes=" + (_subMeshIndexCounts?.Length ?? 0)
                   + "; topologies=" + (_subMeshTopologies?.Length ?? 0)
                   + "; blendShapes=" + (_blendShapeNames?.Length ?? 0)
                   + "; frames=" + (_blendShapeFrameCounts?.Length ?? 0)
                   + "; bones=" + (_bonePaths?.Length ?? 0)
                   + "; fingerprint=" + (HasMeshFingerprint ? "present" : "absent")
                   + "; guid=" + (HasMeshGuid ? "present" : "absent");
        }
    }

    /// <summary>
    /// Stable identity of a part. The part id is the only identity value consumed by the assembler.
    /// </summary>
    /// <remarks>
    /// Older profiles also contain display-name, slot, slot-mode, and conflict-priority fields. They remain in the
    /// serialized shape solely so those assets can still be read; current authoring and build decisions use only
    /// <see cref="PartId"/>.
    /// </remarks>
    [Serializable]
    public sealed class ApaPartIdentity
    {
        [SerializeField] private string _partId = string.Empty;
        // Kept solely so profiles written by older package versions deserialize without data loss. These
        // values are no longer exposed by authoring UI or consumed by the build pipeline.
        [SerializeField] private string _displayName = string.Empty;
        [SerializeField] private ApaPartSlot _slot = ApaPartSlot.Custom;
        [SerializeField] private ApaPartSlotMode _slotMode = ApaPartSlotMode.Replace;
        [SerializeField] private int _conflictPriority;

        /// <summary>
        /// Stable identifier assigned once at authoring time. Survives renaming and hierarchy moves, which is
        /// why it — and not a path — is the ordering key.
        /// </summary>
        public string PartId
        {
            get => _partId;
            set => _partId = value ?? string.Empty;
        }

        /// <summary>Legacy metadata retained for backwards-compatible deserialization; ignored by APA.</summary>
        public string DisplayName
        {
            get => _displayName;
            set => _displayName = value ?? string.Empty;
        }

        /// <summary>Legacy metadata retained for backwards-compatible deserialization; ignored by APA.</summary>
        public ApaPartSlot Slot
        {
            get => _slot;
            set => _slot = value;
        }

        /// <summary>Legacy metadata retained for backwards-compatible deserialization; ignored by APA.</summary>
        public ApaPartSlotMode SlotMode
        {
            get => _slotMode;
            set => _slotMode = value;
        }

        /// <summary>Legacy metadata retained for backwards-compatible deserialization; ignored by APA.</summary>
        public int ConflictPriority
        {
            get => _conflictPriority;
            set => _conflictPriority = value;
        }

        /// <summary>Legacy compatibility flag; always false for new authoring data.</summary>
        public bool HasConflictPriority => _conflictPriority != 0;

        /// <summary>
        /// Assigns a fresh stable identifier when one is missing. Called from authoring code when a profile is
        /// first created. The identifier is deliberately not derived from the asset name or hierarchy, because
        /// it must survive both being changed.
        /// </summary>
        /// <returns>True when a new identifier was assigned.</returns>
        public bool EnsureStablePartId()
        {
            if (!string.IsNullOrEmpty(_partId)) return false;
            _partId = Guid.NewGuid().ToString("N");
            return true;
        }

        /// <summary>Compares identities by stable part id only.</summary>
        /// <remarks>Legacy callers may use this helper; it compares only stable IDs.</remarks>
        public static int Compare(ApaPartIdentity a, ApaPartIdentity b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (ReferenceEquals(a, null)) return -1;
            if (ReferenceEquals(b, null)) return 1;

            return string.CompareOrdinal(a.PartId ?? string.Empty, b.PartId ?? string.Empty);
        }
    }

    /// <summary>
    /// How the part's bones are merged into the avatar armature, and which two armatures define bone identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two explicit Armature selections define bone identity (M10).</b> The author picks the target armature
    /// (inside the avatar, recorded as a path relative to the avatar root) and the part armature (inside the
    /// part, recorded as a path relative to the part root). At build time both roots are resolved, each
    /// renderer's bones are recorded as paths <i>relative to its own selected armature root</i>, and two bones
    /// whose relative paths are identical are the same joint and merge. A bone that <i>is</i> the armature root
    /// records <see cref="ApaAvatarPath.Root"/> (<c>"."</c>), exactly as a root-relative path does everywhere
    /// else in the package.
    /// </para>
    /// <para>
    /// <b>The full outer path from the avatar root to a part bone is no longer an identity.</b> That path
    /// depends on where the part happens to sit under the avatar, which is a placement detail rather than a
    /// property of the joint, and it made two parts that copy the same skeleton merge against each other only by
    /// accident. The relative form is placement-independent, which is what the merge decision needs.
    /// </para>
    /// <para>
    /// <b>The name policy is legacy.</b> <see cref="MergeTargetPath"/>, <see cref="MergePrefix"/>,
    /// <see cref="MergeSuffix"/>, and <see cref="InferMergeNames"/> are kept so that an existing asset
    /// deserializes and round-trips unchanged, but the build no longer consults any of them: the two selections
    /// above decide both the merge target and the correspondence, and Modular Avatar's transient configuration
    /// is written with no prefix and no suffix so that its exact-name matching mirrors the armature-relative
    /// identity. Their defaults are the schema-2 behaviour, so a profile that never touched them is unaffected.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaBoneProfile
    {
        [SerializeField] private bool _mergeArmature = true;
        [SerializeField] private string _mergeTargetPath = string.Empty;
        [SerializeField] private string _mergePrefix = string.Empty;
        [SerializeField] private string _mergeSuffix = string.Empty;
        [SerializeField] private bool _inferMergeNames;

        /// <summary>
        /// Avatar-root-relative path of the armature the part's bones are merged into. Empty means "the author
        /// has not selected one", which is never guessed at.
        /// </summary>
        [SerializeField] private string _targetArmaturePath = string.Empty;

        /// <summary>
        /// Part-root-relative path of the armature the part's own bones belong to. Empty means "the author has
        /// not selected one".
        /// </summary>
        [SerializeField] private string _partArmaturePath = string.Empty;

        /// <summary>When true, a transient Merge Armature configuration is generated at build time (M3).</summary>
        public bool MergeArmature
        {
            get => _mergeArmature;
            set => _mergeArmature = value;
        }

        /// <summary>
        /// <b>Legacy.</b> Hierarchy path of the armature the part merges into, superseded by
        /// <see cref="TargetArmaturePath"/>.
        /// </summary>
        /// <remarks>
        /// Retained for deserialization compatibility only. The build no longer reads it: a merge target that is
        /// not one of the two selected armatures would be a second, conflicting statement about which bone is
        /// which joint.
        /// </remarks>
        public string MergeTargetPath
        {
            get => _mergeTargetPath;
            set => _mergeTargetPath = value ?? string.Empty;
        }

        /// <summary>
        /// Avatar-root-relative path of the selected target armature, or empty when none is selected.
        /// </summary>
        /// <remarks>
        /// The avatar root itself records <see cref="ApaAvatarPath.Root"/>, so "the armature is the avatar root"
        /// is expressible without the empty string, which keeps meaning "not selected".
        /// </remarks>
        public string TargetArmaturePath
        {
            get => _targetArmaturePath;
            set => _targetArmaturePath = value ?? string.Empty;
        }

        /// <summary>
        /// Part-root-relative path of the selected part armature, or empty when none is selected.
        /// </summary>
        /// <remarks>
        /// The part root itself records <see cref="ApaAvatarPath.Root"/>, which is the ordinary case for a part
        /// whose bones hang directly off its root.
        /// </remarks>
        public string PartArmaturePath
        {
            get => _partArmaturePath;
            set => _partArmaturePath = value ?? string.Empty;
        }

        /// <summary>True when both armature selections were made.</summary>
        public bool HasArmatureSelection =>
            ApaAvatarPath.HasIdentity(_targetArmaturePath) && ApaAvatarPath.HasIdentity(_partArmaturePath);

        /// <summary>True when the author selected a target armature but no part armature, or the reverse.</summary>
        public bool HasPartialArmatureSelection =>
            ApaAvatarPath.HasIdentity(_targetArmaturePath) != ApaAvatarPath.HasIdentity(_partArmaturePath);

        /// <summary>A stable description of the two selections, for diagnostics and the authoring UI.</summary>
        public string DescribeArmatures()
        {
            if (!HasArmatureSelection)
            {
                if (HasPartialArmatureSelection)
                {
                    return "target='" + _targetArmaturePath + "'; part='" + _partArmaturePath + "' (incomplete)";
                }

                return "target=''; part='' (not selected)";
            }

            return "target='" + _targetArmaturePath + "'; part='" + _partArmaturePath + "'";
        }

        /// <summary>
        /// <b>Legacy.</b> Serialized prefix the M6 build applied to target bone names when generating the merge
        /// mapping. Superseded by the armature-relative identity, and never read by this build.
        /// </summary>
        /// <remarks>
        /// Kept so that an existing asset round-trips unchanged. The M10 build writes an empty prefix and suffix
        /// onto the transient Modular Avatar configuration, so Modular Avatar's exact-name matching mirrors the
        /// armature-relative identity instead of a name rewrite the profile's identity no longer depends on.
        /// </remarks>
        public string MergePrefix
        {
            get => _mergePrefix;
            set => _mergePrefix = value ?? string.Empty;
        }

        /// <summary>
        /// <b>Legacy.</b> Serialized suffix the M6 build applied to target bone names. Superseded by the
        /// armature-relative identity, and never read by this build.
        /// </summary>
        public string MergeSuffix
        {
            get => _mergeSuffix;
            set => _mergeSuffix = value ?? string.Empty;
        }

        /// <summary>
        /// <b>Legacy.</b> Whether the M6 build might derive the merge prefix and suffix from the hierarchy.
        /// Superseded by the armature-relative identity, and never read by this build.
        /// </summary>
        /// <remarks>
        /// Inference is not merely unused now: it is incompatible with the new rule, because a rewritten bone
        /// name would make Modular Avatar match two bones whose armature-relative paths differ.
        /// </remarks>
        public bool InferMergeNames
        {
            get => _inferMergeNames;
            set => _inferMergeNames = value;
        }

        /// <summary>True when the profile carries a legacy, serialized name mapping.</summary>
        /// <remarks>
        /// Kept for diagnostics only. Nothing in the M10 build consults it; see <see cref="MergePrefix"/>.
        /// </remarks>
        public bool HasExplicitMergeNames =>
            !string.IsNullOrEmpty(_mergePrefix) || !string.IsNullOrEmpty(_mergeSuffix);

        /// <summary>
        /// <b>Legacy.</b> True when the M6 build had a deterministic name mapping to apply. Never read by the M10
        /// build, which merges by the armature-relative bone identity instead.
        /// </summary>
        public bool HasDeterministicMergeNames => HasExplicitMergeNames || _inferMergeNames;

        /// <summary>
        /// A stable description of the legacy merge-name policy, for diagnostics and for reading old profiles.
        /// </summary>
        /// <remarks>
        /// Retained so that a report or a test can still show what an old asset declared. It is not an input to
        /// any decision: the armature selections decide the merge, and
        /// <see cref="DescribeArmatures"/> describes them.
        /// </remarks>
        public string DescribeMergeNames()
        {
            if (HasExplicitMergeNames)
            {
                return "prefix='" + _mergePrefix + "'; suffix='" + _mergeSuffix + "'";
            }

            return _inferMergeNames ? "infer=true" : "infer=false; prefix=''; suffix=''";
        }
    }

    /// <summary>
    /// Declares blend shape handling policy beyond the default strict rules.
    /// </summary>
    /// <remarks>
    /// The strict rules themselves (frame-weight agreement, zero seam deltas for part-only shapes, duplicate
    /// name rejection) are not configurable and are always enforced. <see cref="AllowPartOnlyShapes"/> is a
    /// narrowing switch only: it can refuse a part-only shape that the strict rules would otherwise accept
    /// because its seam deltas are zero. It can never widen what is accepted.
    /// </remarks>
    [Serializable]
    public sealed class ApaBlendShapeProfile
    {
        [SerializeField] private bool _allowPartOnlyShapes = true;

        /// <summary>
        /// When true, a shape that exists only on the part is accepted provided its seam deltas are zero.
        /// When false, a shape that exists only on the part is refused regardless of its deltas
        /// (<c>APA029 reason=part-only-shape-disallowed</c>). A non-zero seam delta is always an error.
        /// </summary>
        /// <remarks>
        /// This field was inert before M6: nothing read it and the strict rules decided unconditionally. M6
        /// wires it into the blend shape seam validator so that declaring false is honoured, which is a strictly
        /// stronger block and therefore safe for existing profiles (the default stays true).
        /// </remarks>
        public bool AllowPartOnlyShapes
        {
            get => _allowPartOnlyShapes;
            set => _allowPartOnlyShapes = value;
        }
    }
}

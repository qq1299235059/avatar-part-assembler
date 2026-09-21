using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Hard bounds the protected-payload decoder enforces before it allocates anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every count in the binary payload is attacker-controlled in the threat model this format is written for:
    /// a recipient can edit the bytes of the asset, and a corrupted file can declare any count at all. A decoder
    /// that trusts a declared count allocates whatever the file asks for, which turns a corrupt asset into an
    /// out-of-memory crash instead of a diagnostic. Each maximum below is therefore checked <i>before</i> the
    /// corresponding allocation, and each array read additionally proves that the declared count fits inside the
    /// bytes that are actually left in the stream.
    /// </para>
    /// <para>
    /// The values are deliberately far above any real avatar part — a 4-million-vertex part is already far past
    /// what a VRChat avatar can upload — because the limit exists to bound damage, not to be a quality bar. A
    /// legitimate part is never refused by them.
    /// </para>
    /// </remarks>
    public static class ApaProtectedMeshLimits
    {
        /// <summary>Largest vertex count a payload may declare.</summary>
        public const int MaxVertexCount = 4_000_000;

        /// <summary>Largest submesh count a payload may declare.</summary>
        public const int MaxSubMeshCount = 256;

        /// <summary>Largest index count one submesh may declare.</summary>
        public const int MaxIndicesPerSubMesh = 96_000_000;

        /// <summary>Largest bone count (bind poses, bone matrices, or bone paths) a payload may declare.</summary>
        public const int MaxBoneCount = 8_192;

        /// <summary>Largest blend shape count a payload may declare.</summary>
        public const int MaxBlendShapeCount = 4_096;

        /// <summary>Largest frame count one blend shape may declare.</summary>
        public const int MaxFramesPerShape = 4_096;

        /// <summary>Largest total frame count across every blend shape of one payload.</summary>
        public const int MaxTotalBlendShapeFrames = 65_536;

        /// <summary>Largest byte length of a stored name or bone path.</summary>
        public const int MaxStringBytes = 4_096;

        /// <summary>AES block size in bytes. PKCS#7 always pads to a whole number of these.</summary>
        public const int AesBlockBytes = 16;

        /// <summary>Largest serialized plaintext a payload may declare or produce.</summary>
        public const long MaxPlaintextBytes = 512L * 1024L * 1024L;

        /// <summary>
        /// Largest encrypted payload the reader will accept.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Derived from <see cref="MaxPlaintextBytes"/>, never smaller than it.</b> PKCS#7 appends between one
        /// and <see cref="AesBlockBytes"/> bytes, so a plaintext that is already a multiple of the block size
        /// gains a whole extra block. The bound is therefore the largest plaintext rounded up to the next block
        /// boundary, which is exactly the ciphertext a legitimate payload of that size produces. A bound below
        /// that would reject a payload this build itself had just written, which is a data-loss bug rather than a
        /// hardening measure; the check exists to stop a hostile *length field* from driving an allocation, not to
        /// be smaller than the writer's own output.
        /// </para>
        /// <para>
        /// The multiplication is done in <see cref="long"/> arithmetic so a future increase of
        /// <see cref="MaxPlaintextBytes"/> cannot overflow the bound into a small number.
        /// </para>
        /// </remarks>
        public const long MaxCiphertextBytes = (MaxPlaintextBytes / AesBlockBytes + 1L) * AesBlockBytes;
    }

    /// <summary>
    /// The stable <c>reason=…</c> tokens the protected-payload diagnostics carry.
    /// </summary>
    /// <remarks>
    /// One diagnostic code (<c>APA053</c>) carries every payload failure and this token names the condition, which
    /// is the pattern the rest of the package uses for a family of conditions that share one remedy. A test, a
    /// report, and a support conversation can therefore name the exact failure without a code per condition.
    /// </remarks>
    public static class ApaProtectedMeshReasons
    {
        /// <summary>The installer references no protected asset at all.</summary>
        public const string MissingAsset = "protected-mesh-missing";

        /// <summary>The asset carries a payload envelope version this build does not understand.</summary>
        public const string UnsupportedFormatVersion = "unsupported-format-version";

        /// <summary>The asset names an authenticated-encryption construction this build does not implement.</summary>
        public const string UnsupportedCodec = "unsupported-codec";

        /// <summary>The asset is missing a salt, IV, tag, or ciphertext.</summary>
        public const string IncompleteEnvelope = "incomplete-envelope";

        /// <summary>The declared ciphertext or plaintext length is not usable.</summary>
        public const string InvalidLength = "invalid-length";

        /// <summary>The authentication tag does not match the header and ciphertext.</summary>
        public const string AuthenticationFailed = "authentication-failed";

        /// <summary>The decrypted bytes are not valid PKCS#7 padded data.</summary>
        public const string InvalidPadding = "invalid-padding";

        /// <summary>The decrypted length differs from the length the header declares.</summary>
        public const string PlaintextLengthMismatch = "plaintext-length-mismatch";

        /// <summary>The plaintext is not a payload this build produced.</summary>
        public const string BadMagic = "bad-magic";

        /// <summary>The plaintext declares a payload version this build does not understand.</summary>
        public const string UnsupportedPayloadVersion = "unsupported-payload-version";

        /// <summary>A declared count is negative, or exceeds <see cref="ApaProtectedMeshLimits"/>.</summary>
        public const string LengthLimitExceeded = "length-limit-exceeded";

        /// <summary>A declared count is negative, which no field of this format can legitimately be.</summary>
        public const string CountOutOfRange = "count-out-of-range";

        /// <summary>A declared count does not match the vertex count it must agree with.</summary>
        public const string CountMismatch = "count-mismatch";

        /// <summary>The stream ended before a declared array did.</summary>
        public const string TruncatedPayload = "truncated-payload";

        /// <summary>Bytes remain after the payload's last field.</summary>
        public const string TrailingGarbage = "trailing-garbage";

        /// <summary>An index buffer addresses a vertex the payload does not carry.</summary>
        public const string IndexOutOfRange = "index-out-of-range";

        /// <summary>A submesh declares a topology this build does not accept.</summary>
        public const string InvalidTopology = "invalid-topology";

        /// <summary>The payload declares an index format this build does not accept.</summary>
        public const string InvalidIndexFormat = "invalid-index-format";

        /// <summary>A stored name or bone path is not valid UTF-8, or is longer than the limit.</summary>
        public const string MalformedString = "malformed-string";

        /// <summary>The payload decodes but carries no geometry at all.</summary>
        public const string EmptyPayload = "empty-payload";

        /// <summary>The payload was produced for a different part id than the installer's.</summary>
        public const string PartIdMismatch = "part-id-mismatch";
    }

    /// <summary>
    /// One authenticated, encrypted payload exactly as it is stored in an <see cref="ApaProtectedMeshAsset"/>.
    /// </summary>
    /// <remarks>
    /// A plain data value with no Unity dependency, so the codec can be tested without an asset database and so
    /// the asset writer never has to reach into the asset's fields.
    /// </remarks>
    public sealed class ApaProtectedMeshPayload
    {
        /// <summary>Envelope version written into the asset.</summary>
        public int FormatVersion { get; }

        /// <summary>Authenticated-encryption construction identifier.</summary>
        public string Codec { get; }

        /// <summary>Stable part id the payload was created for.</summary>
        public string PartId { get; }

        /// <summary>Content fingerprint of the source mesh at creation time.</summary>
        public string SourceFingerprint { get; }

        /// <summary>Length of the serialized plaintext.</summary>
        public int PlaintextLength { get; }

        /// <summary>Per-asset random salt.</summary>
        public byte[] Salt { get; }

        /// <summary>Per-asset random initialization vector.</summary>
        public byte[] Iv { get; }

        /// <summary>Encrypted plaintext.</summary>
        public byte[] Ciphertext { get; }

        /// <summary>HMAC-SHA256 authentication tag over the header and ciphertext.</summary>
        public byte[] Tag { get; }

        /// <summary>Creates a payload value.</summary>
        public ApaProtectedMeshPayload(
            int formatVersion,
            string codec,
            string partId,
            string sourceFingerprint,
            int plaintextLength,
            byte[] salt,
            byte[] iv,
            byte[] ciphertext,
            byte[] tag)
        {
            FormatVersion = formatVersion;
            Codec = codec ?? string.Empty;
            PartId = partId ?? string.Empty;
            SourceFingerprint = sourceFingerprint ?? string.Empty;
            PlaintextLength = plaintextLength;
            Salt = salt ?? Array.Empty<byte>();
            Iv = iv ?? Array.Empty<byte>();
            Ciphertext = ciphertext ?? Array.Empty<byte>();
            Tag = tag ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// The decoded contents of one protected payload: the complete mesh data, still plain managed arrays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split between this type and <see cref="MeshSnapshot"/> exists for one reason: a snapshot's bone
    /// signature is built from the <i>live</i> armature scope, which the capture site resolves from the bone
    /// weights — and those weights are inside the payload. Decoding to plain data first therefore lets the
    /// context builder resolve the same armature scope the unprotected path would resolve, and only then build the
    /// snapshot with the same live bone paths. Decoding straight to a snapshot would have forced the payload's
    /// stored (authoring-time) bone paths to be authoritative.
    /// </para>
    /// <para>
    /// Every property is read-only and the arrays are never mutated after construction, so
    /// <see cref="CreateSnapshot"/> may be called more than once; the resulting snapshots share the buffers, which
    /// is safe precisely because nothing writes them.
    /// </para>
    /// </remarks>
    public sealed class ApaProtectedMeshData
    {
        private readonly Vector3[] _vertices;
        private readonly Vector3[] _normals;
        private readonly Vector4[] _tangents;
        private readonly Color[] _colors;
        private readonly Vector4[][] _uvs;
        private readonly int[][] _subMeshes;
        private readonly MeshTopology[] _topologies;
        private readonly BoneWeight[] _boneWeights;
        private readonly Matrix4x4[] _bindPoses;
        private readonly string[] _shapeNames;
        private readonly int[] _shapeFrameCounts;
        private readonly BlendShapeFrameSnapshot[][] _shapeFrames;
        private readonly Matrix4x4[] _boneWorldToLocal;
        private readonly string[] _bonePaths;
        private readonly ReadOnlyCollectionView<Vector3> _verticesView;
        private readonly ReadOnlyCollectionView<Vector4> _tangentsView;
        private readonly ReadOnlyCollectionView<Color> _colorsView;
        private readonly ReadOnlyCollectionView<Matrix4x4> _bindPoseView;
        private readonly ReadOnlyCollectionView<Matrix4x4> _boneWorldToLocalView;
        private readonly ReadOnlyCollectionView<string> _shapeNameView;
        private readonly ReadOnlyCollectionView<int> _shapeFrameCountView;
        private readonly ReadOnlyCollectionView<string> _bonePathView;
        private readonly IReadOnlyList<IReadOnlyList<Vector4>> _uvViews;
        private readonly IReadOnlyList<IReadOnlyList<int>> _subMeshViews;
        private readonly IReadOnlyList<MeshTopology> _topologyView;
        private readonly IReadOnlyList<BoneWeight> _boneWeightView;
        private readonly IReadOnlyList<IReadOnlyList<BlendShapeFrameSnapshot>> _shapeFrameView;

        /// <summary>Name of the source mesh, kept so the decoded snapshot is named exactly like the source.</summary>
        public string Name { get; }

        /// <summary>Local bounds of the source mesh.</summary>
        public Bounds Bounds { get; }

        /// <summary>Index format of the source mesh.</summary>
        public IndexFormat IndexFormat { get; }

        /// <summary>Vertex positions.</summary>
        public IReadOnlyList<Vector3> Vertices => _verticesView;

        /// <summary>Vertex normals, or an empty list.</summary>
        public IReadOnlyList<Vector3> Normals { get; }

        /// <summary>Vertex tangents, or an empty list.</summary>
        public IReadOnlyList<Vector4> Tangents => _tangentsView;

        /// <summary>Vertex colors, or an empty list.</summary>
        public IReadOnlyList<Color> Colors => _colorsView;

        /// <summary>UV channels 0 through 7 by index.</summary>
        public IReadOnlyList<IReadOnlyList<Vector4>> UvChannels => _uvViews;

        /// <summary>Submesh index buffers, in submesh order.</summary>
        public IReadOnlyList<IReadOnlyList<int>> SubMeshIndices => _subMeshViews;

        /// <summary>Submesh topologies, in submesh order.</summary>
        public IReadOnlyList<MeshTopology> TopologyList => _topologyView;

        /// <summary>Bone weights, one per vertex, or an empty list.</summary>
        public IReadOnlyList<BoneWeight> SkinWeights => _boneWeightView;

        /// <summary>The source mesh's own bind poses, or an empty list.</summary>
        public IReadOnlyList<Matrix4x4> SkinBindPoses => _bindPoseView;

        /// <summary>Blend shape names, in mesh order.</summary>
        public IReadOnlyList<string> Shapes => _shapeNameView;

        /// <summary>Frame counts per blend shape, in mesh order.</summary>
        public IReadOnlyList<int> ShapeFrameCounts => _shapeFrameCountView;

        /// <summary>Blend shape frames, indexed by shape order and then frame order.</summary>
        public IReadOnlyList<IReadOnlyList<BlendShapeFrameSnapshot>> BlendShapeFrames => _shapeFrameView;

        /// <summary>Each bone's <c>worldToLocalMatrix</c> at creation time, or an empty list.</summary>
        public IReadOnlyList<Matrix4x4> BoneWorldToLocalMatrices => _boneWorldToLocalView;

        /// <summary>Bone paths recorded at creation time, or an empty list.</summary>
        public IReadOnlyList<string> BonePaths => _bonePathView;

        /// <summary>Number of vertices.</summary>
        public int VertexCount => _vertices.Length;

        /// <summary>Number of submeshes.</summary>
        public int SubMeshCount => _subMeshes.Length;

        internal ApaProtectedMeshData(
            string name,
            Bounds bounds,
            IndexFormat indexFormat,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Color[] colors,
            Vector4[][] uvs,
            int[][] subMeshes,
            MeshTopology[] topologies,
            BoneWeight[] boneWeights,
            Matrix4x4[] bindPoses,
            string[] shapeNames,
            int[] shapeFrameCounts,
            BlendShapeFrameSnapshot[][] shapeFrames,
            Matrix4x4[] boneWorldToLocal,
            string[] bonePaths)
        {
            Name = name ?? string.Empty;
            Bounds = bounds;
            IndexFormat = indexFormat;
            _vertices = vertices ?? Array.Empty<Vector3>();
            _normals = normals ?? Array.Empty<Vector3>();
            _tangents = tangents ?? Array.Empty<Vector4>();
            _colors = colors ?? Array.Empty<Color>();
            _uvs = uvs ?? MeshSnapshot.NewUvArray();
            _subMeshes = subMeshes ?? Array.Empty<int[]>();
            _topologies = topologies ?? Array.Empty<MeshTopology>();
            _boneWeights = boneWeights ?? Array.Empty<BoneWeight>();
            _bindPoses = bindPoses ?? Array.Empty<Matrix4x4>();
            _shapeNames = shapeNames ?? Array.Empty<string>();
            _shapeFrameCounts = shapeFrameCounts ?? Array.Empty<int>();
            _shapeFrames = shapeFrames ?? Array.Empty<BlendShapeFrameSnapshot[]>();
            _boneWorldToLocal = boneWorldToLocal ?? Array.Empty<Matrix4x4>();
            _bonePaths = bonePaths ?? Array.Empty<string>();

            _verticesView = new ReadOnlyCollectionView<Vector3>(_vertices);
            Normals = new ReadOnlyCollectionView<Vector3>(_normals);
            _tangentsView = new ReadOnlyCollectionView<Vector4>(_tangents);
            _colorsView = new ReadOnlyCollectionView<Color>(_colors);
            _bindPoseView = new ReadOnlyCollectionView<Matrix4x4>(_bindPoses);
            _boneWorldToLocalView = new ReadOnlyCollectionView<Matrix4x4>(_boneWorldToLocal);
            _shapeNameView = new ReadOnlyCollectionView<string>(_shapeNames);
            _shapeFrameCountView = new ReadOnlyCollectionView<int>(_shapeFrameCounts);
            _bonePathView = new ReadOnlyCollectionView<string>(_bonePaths);
            _boneWeightView = new ReadOnlyCollectionView<BoneWeight>(_boneWeights);

            var uvViews = new IReadOnlyList<Vector4>[_uvs.Length];
            for (var i = 0; i < uvViews.Length; i++)
                uvViews[i] = new ReadOnlyCollectionView<Vector4>(_uvs[i] ?? Array.Empty<Vector4>());
            _uvViews = uvViews;

            var subMeshViews = new IReadOnlyList<int>[_subMeshes.Length];
            for (var i = 0; i < subMeshViews.Length; i++)
                subMeshViews[i] = new ReadOnlyCollectionView<int>(_subMeshes[i] ?? Array.Empty<int>());
            _subMeshViews = subMeshViews;

            var topologyView = new MeshTopology[_topologies.Length];
            Array.Copy(_topologies, topologyView, _topologies.Length);
            _topologyView = topologyView;

            var shapeViews = new IReadOnlyList<BlendShapeFrameSnapshot>[_shapeFrames.Length];
            for (var i = 0; i < shapeViews.Length; i++)
                shapeViews[i] = _shapeFrames[i] ?? Array.Empty<BlendShapeFrameSnapshot>();
            _shapeFrameView = shapeViews;
        }

        /// <summary>
        /// Builds the immutable snapshot the assembler consumes, using the caller's bone identity.
        /// </summary>
        /// <param name="bonePaths">
        /// Armature-relative bone paths in the live renderer's bone order, or null to use the paths recorded in the
        /// payload. The live paths are what the unprotected path records, so a caller that can resolve the
        /// armature scope should pass them.
        /// </param>
        /// <param name="boneWorldToLocal">
        /// Each bone's live <c>worldToLocalMatrix</c>, or null to use the matrices recorded in the payload.
        /// </param>
        /// <remarks>
        /// The snapshot takes ownership of the decoded arrays. Nothing in this type writes them afterwards, so
        /// calling this method twice is safe; both snapshots read the same immutable buffers.
        /// </remarks>
        public MeshSnapshot CreateSnapshot(
            IReadOnlyList<string> bonePaths = null,
            IReadOnlyList<Matrix4x4> boneWorldToLocal = null)
        {
            var paths = bonePaths != null && bonePaths.Count > 0 ? ToArray(bonePaths) : _bonePaths;
            var bones = boneWorldToLocal != null && boneWorldToLocal.Count > 0
                ? ToArray(boneWorldToLocal)
                : _boneWorldToLocal;

            return new MeshSnapshot(
                Name,
                _vertices,
                _normals,
                _tangents,
                _colors,
                _uvs,
                _subMeshes,
                _topologies,
                Bounds,
                IndexFormat,
                _boneWeights,
                _bindPoses,
                _shapeNames,
                _shapeFrameCounts,
                new BoneSignature(paths),
                _shapeFrames,
                bones);
        }

        private static T[] ToArray<T>(IReadOnlyList<T> values)
        {
            var result = new T[values.Count];
            for (var i = 0; i < result.Length; i++) result[i] = values[i];
            return result;
        }

        /// <summary>
        /// A minimal read-only wrapper over an array.
        /// </summary>
        /// <remarks>
        /// <see cref="Array.AsReadOnly{T}"/> would be the obvious choice, but it is a class from
        /// <c>System.Collections.ObjectModel</c> that allocates one wrapper per call; this view is created once
        /// per channel and never copied, and it keeps a caller from reaching the mutable array through the
        /// interface.
        /// </remarks>
        private sealed class ReadOnlyCollectionView<T> : IReadOnlyList<T>
        {
            private readonly T[] _items;

            internal ReadOnlyCollectionView(T[] items)
            {
                _items = items ?? Array.Empty<T>();
            }

            public int Count => _items.Length;

            public T this[int index] => _items[index];

            public IEnumerator<T> GetEnumerator()
            {
                for (var i = 0; i < _items.Length; i++) yield return _items[i];
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    /// <summary>
    /// Serializes, encrypts, authenticates, and decodes a part mesh payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Format.</b> The plaintext is a versioned, length-prefixed binary record carrying everything
    /// <see cref="MeshSnapshotFactory"/> reads: name, bounds, index format, vertices, normals, tangents, colors,
    /// UV0 through UV7, per-submesh indices and topology, bone weights, bind poses, every blend shape with every
    /// frame's weight and position/normal/tangent deltas, the recorded bone world-to-local matrices, and the bone
    /// paths. It is deliberately uncompressed: a length-prefixed record whose every count is bounded is easier to
    /// audit than a compressed one, and the payload is encrypted anyway, so the compression ratio would be the
    /// only thing gained.
    /// </para>
    /// <para>
    /// <b>Confidentiality and integrity.</b> AES-256-CBC with PKCS#7 padding, encrypt-then-MAC with HMAC-SHA256,
    /// and a key derived with PBKDF2-SHA256 from a package-local secret, the stable part id, and a per-asset
    /// random salt. The authentication tag covers the envelope header as well as the ciphertext, so the part id,
    /// the codec identifier, and the declared lengths are tamper-evident too, not just the geometry. The tag is
    /// compared in constant time.
    /// </para>
    /// <para>
    /// <b>What this is not.</b> The derivation secret ships inside the package, and the decoded mesh exists in
    /// memory during a build, so this is a distribution format that stops a recipient from receiving the raw mesh
    /// asset — not an unextractable DRM boundary. Nothing here claims otherwise, and the creator guide says so in
    /// the same words.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> Every failure returns a blocking <see cref="ValidationIssue"/> with a stable
    /// <c>reason=…</c> token and no snapshot. There is no partial decode, no "best effort" fallback, and no path
    /// that returns the null renderer's geometry instead.
    /// </para>
    /// </remarks>
    public static class ApaProtectedMeshCodec
    {
        /// <summary>Magic of the serialized plaintext, so a wrong-format payload is refused by name.</summary>
        public const string PayloadMagic = "APAPMESH";

        /// <summary>Version of the serialized plaintext layout.</summary>
        public const int PayloadVersion = 1;

        /// <summary>PBKDF2 iteration count. Stored implicitly: it is part of the codec identifier's meaning.</summary>
        public const int KeyDerivationIterations = 20_000;

        /// <summary>Number of derived bytes: a 32-byte AES key followed by a 32-byte HMAC key.</summary>
        private const int DerivedKeyBytes = 64;

        /// <summary>
        /// The package-local derivation secret.
        /// </summary>
        /// <remarks>
        /// <b>This is obfuscation, not a secret.</b> It is compiled into the shipped package and anyone who
        /// inspects the assembly can read it. It exists so that the key is not the payload itself and so that a
        /// payload copied between two parts does not decode under the other part's context. The authenticated
        /// header and the profile fingerprint are what actually make tampering fail closed.
        /// </remarks>
        private const string DerivationSecret = "apa-protected-part-mesh/v1:8f4b0c2e-6a1d-4f2b-9c3e-5d7a1b2c3d4e";

        /// <summary>Serializes a snapshot and encrypts it into a payload envelope.</summary>
        /// <param name="snapshot">The captured source mesh. Must not be null.</param>
        /// <param name="partId">
        /// Stable part id of the owning profile. It is part of the key-derivation context and of the authenticated
        /// header. An empty id is accepted (a legacy profile can carry none) but is recorded as empty.
        /// </param>
        /// <param name="payload">Receives the envelope on success.</param>
        /// <param name="issue">Receives a blocking diagnostic on failure.</param>
        /// <returns>True when the payload was produced.</returns>
        public static bool TryCreatePayload(
            MeshSnapshot snapshot,
            string partId,
            out ApaProtectedMeshPayload payload,
            out ValidationIssue issue)
        {
            payload = null;
            issue = null;

            if (snapshot == null)
            {
                issue = Error(
                    ApaProtectedMeshReasons.EmptyPayload,
                    "No mesh snapshot was supplied to the protected-mesh codec.",
                    detail: "reason=" + ApaProtectedMeshReasons.EmptyPayload);
                return false;
            }

            byte[] plaintext;
            try
            {
                plaintext = Serialize(snapshot);
            }
            catch (ApaProtectedMeshFormatException e)
            {
                issue = Error(
                    e.Reason,
                    "The part mesh could not be serialized into a protected payload: " + e.Message,
                    detail: "reason=" + e.Reason);
                return false;
            }

            if (plaintext.LongLength > ApaProtectedMeshLimits.MaxPlaintextBytes)
            {
                issue = Error(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    "The part mesh serializes to " + plaintext.LongLength +
                    " bytes, which is beyond the protected payload limit of " +
                    ApaProtectedMeshLimits.MaxPlaintextBytes + " bytes.",
                    detail: "reason=" + ApaProtectedMeshReasons.LengthLimitExceeded +
                            "; plainBytes=" + plaintext.LongLength);
                return false;
            }

            var salt = RandomBytes(ApaProtectedMeshAsset.SaltLength);
            var iv = RandomBytes(ApaProtectedMeshAsset.IvLength);
            var id = partId ?? string.Empty;
            var fingerprint = snapshot.ContentFingerprint ?? string.Empty;

            byte[] ciphertext;
            byte[] tag;
            try
            {
                ciphertext = Encrypt(plaintext, id, salt, iv);
                tag = ComputeTag(ApaProtectedMeshAsset.CurrentFormatVersion, ApaProtectedMeshAsset.CurrentCodec,
                    id, fingerprint, plaintext.Length, salt, iv, ciphertext);
            }
            catch (CryptographicException e)
            {
                issue = Error(
                    ApaProtectedMeshReasons.UnsupportedCodec,
                    "The protected payload could not be encrypted: " + e.Message,
                    detail: "reason=" + ApaProtectedMeshReasons.UnsupportedCodec);
                return false;
            }

            // The encoder proves its own output is inside the envelope bounds the decoder enforces, so a payload
            // this build wrote can never be one this build refuses to read.
            if (ciphertext.LongLength > ApaProtectedMeshLimits.MaxCiphertextBytes
                || ciphertext.Length % ApaProtectedMeshLimits.AesBlockBytes != 0)
            {
                issue = Error(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    "The part mesh serializes to " + plaintext.LongLength + " bytes, which encrypts to " +
                    ciphertext.LongLength + " bytes, beyond the protected payload limit of " +
                    ApaProtectedMeshLimits.MaxCiphertextBytes + " bytes.",
                    detail: "reason=" + ApaProtectedMeshReasons.LengthLimitExceeded +
                            "; plainBytes=" + plaintext.LongLength +
                            "; cipherBytes=" + ciphertext.LongLength);
                return false;
            }

            payload = new ApaProtectedMeshPayload(
                ApaProtectedMeshAsset.CurrentFormatVersion,
                ApaProtectedMeshAsset.CurrentCodec,
                id,
                fingerprint,
                plaintext.Length,
                salt,
                iv,
                ciphertext,
                tag);
            return true;
        }

        /// <summary>
        /// Serializes a snapshot into the payload's plaintext form, without encrypting it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Exposed for the same reason <see cref="TryReadPlaintext"/> is: the parser is the part of the codec
        /// with the interesting failure modes — declared counts, truncation, trailing bytes — and a test that has
        /// to prove those are refused needs to build the exact bytes the parser reads. Reaching them through an
        /// encrypted envelope is impossible, and a second, hand-written serializer in a test would be a copy of
        /// the format that could drift from this one.
        /// </para>
        /// <para>
        /// The bytes this produces are plaintext: they carry the part's geometry with no confidentiality. A
        /// caller must not write them to a project asset, a log, or a report; the production path only ever feeds
        /// them to <see cref="Encrypt"/>.
        /// </para>
        /// </remarks>
        public static bool TrySerialize(
            MeshSnapshot snapshot,
            out byte[] plaintext,
            out ValidationIssue issue)
        {
            plaintext = null;
            issue = null;

            if (snapshot == null)
            {
                issue = Error(
                    ApaProtectedMeshReasons.EmptyPayload,
                    "No mesh snapshot was supplied to the protected-mesh codec.",
                    detail: "reason=" + ApaProtectedMeshReasons.EmptyPayload);
                return false;
            }

            try
            {
                plaintext = Serialize(snapshot);
                return true;
            }
            catch (ApaProtectedMeshFormatException e)
            {
                issue = Error(
                    e.Reason,
                    "The part mesh could not be serialized into a protected payload: " + e.Message,
                    detail: "reason=" + e.Reason + (string.IsNullOrEmpty(e.Detail) ? string.Empty : "; " + e.Detail));
                return false;
            }
        }

        /// <summary>
        /// Authenticates and decrypts a payload envelope, then parses the plaintext.
        /// </summary>
        /// <param name="payload">The envelope to decode.</param>
        /// <param name="data">Receives the decoded mesh data on success.</param>
        /// <param name="issue">Receives a blocking diagnostic on failure.</param>
        /// <returns>True when the payload authenticated and parsed completely.</returns>
        public static bool TryDecode(
            ApaProtectedMeshPayload payload,
            out ApaProtectedMeshData data,
            out ValidationIssue issue)
        {
            data = null;
            issue = null;

            if (payload == null)
            {
                issue = Error(
                    ApaProtectedMeshReasons.MissingAsset,
                    "No protected mesh payload was supplied.",
                    detail: "reason=" + ApaProtectedMeshReasons.MissingAsset);
                return false;
            }

            if (payload.FormatVersion != ApaProtectedMeshAsset.CurrentFormatVersion)
            {
                issue = Error(
                    ApaProtectedMeshReasons.UnsupportedFormatVersion,
                    "The protected mesh payload was written in format version " + payload.FormatVersion +
                    ", which this build does not understand (it reads version " +
                    ApaProtectedMeshAsset.CurrentFormatVersion +
                    "). Recreate the protected part prefab with this package version.",
                    detail: "reason=" + ApaProtectedMeshReasons.UnsupportedFormatVersion +
                            "; found=" + payload.FormatVersion +
                            "; supported=" + ApaProtectedMeshAsset.CurrentFormatVersion);
                return false;
            }

            if (!string.Equals(payload.Codec, ApaProtectedMeshAsset.CurrentCodec, StringComparison.Ordinal))
            {
                issue = Error(
                    ApaProtectedMeshReasons.UnsupportedCodec,
                    "The protected mesh payload names the codec '" + payload.Codec +
                    "', which this build does not implement (it writes and reads '" +
                    ApaProtectedMeshAsset.CurrentCodec + "'). Recreate the protected part prefab.",
                    detail: "reason=" + ApaProtectedMeshReasons.UnsupportedCodec + "; codec=" + payload.Codec);
                return false;
            }

            if (payload.Salt.Length != ApaProtectedMeshAsset.SaltLength
                || payload.Iv.Length != ApaProtectedMeshAsset.IvLength
                || payload.Tag.Length != ApaProtectedMeshAsset.TagLength
                || payload.Ciphertext.Length == 0)
            {
                issue = Error(
                    ApaProtectedMeshReasons.IncompleteEnvelope,
                    "The protected mesh payload is incomplete: it must carry a " +
                    ApaProtectedMeshAsset.SaltLength + "-byte salt, a " + ApaProtectedMeshAsset.IvLength +
                    "-byte IV, a " + ApaProtectedMeshAsset.TagLength + "-byte authentication tag, and a " +
                    "non-empty ciphertext. Recreate the protected part prefab.",
                    detail: "reason=" + ApaProtectedMeshReasons.IncompleteEnvelope +
                            "; salt=" + payload.Salt.Length +
                            "; iv=" + payload.Iv.Length +
                            "; tag=" + payload.Tag.Length +
                            "; cipher=" + payload.Ciphertext.Length);
                return false;
            }

            if (payload.PlaintextLength < 0
                || payload.PlaintextLength > ApaProtectedMeshLimits.MaxPlaintextBytes
                || payload.Ciphertext.Length > ApaProtectedMeshLimits.MaxCiphertextBytes
                || payload.Ciphertext.Length % ApaProtectedMeshLimits.AesBlockBytes != 0)
            {
                issue = Error(
                    ApaProtectedMeshReasons.InvalidLength,
                    "The protected mesh payload declares an unusable length (plaintext " + payload.PlaintextLength +
                    " bytes, ciphertext " + payload.Ciphertext.Length + " bytes). A CBC ciphertext is always a " +
                    "whole number of " + ApaProtectedMeshLimits.AesBlockBytes + "-byte blocks and never exceeds " +
                    ApaProtectedMeshLimits.MaxCiphertextBytes + " bytes. Recreate the protected part prefab.",
                    detail: "reason=" + ApaProtectedMeshReasons.InvalidLength +
                            "; plainBytes=" + payload.PlaintextLength +
                            "; cipherBytes=" + payload.Ciphertext.Length +
                            "; block=" + ApaProtectedMeshLimits.AesBlockBytes);
                return false;
            }

            byte[] expected;
            try
            {
                expected = ComputeTag(payload.FormatVersion, payload.Codec, payload.PartId,
                    payload.SourceFingerprint, payload.PlaintextLength, payload.Salt, payload.Iv,
                    payload.Ciphertext);
            }
            catch (CryptographicException e)
            {
                issue = Error(
                    ApaProtectedMeshReasons.UnsupportedCodec,
                    "The protected payload could not be authenticated: " + e.Message,
                    detail: "reason=" + ApaProtectedMeshReasons.UnsupportedCodec);
                return false;
            }

            if (!FixedTimeEquals(expected, payload.Tag))
            {
                issue = Error(
                    ApaProtectedMeshReasons.AuthenticationFailed,
                    "The protected mesh payload failed authentication. The asset was modified, truncated, or " +
                    "replaced after it was written, so its geometry cannot be trusted. Recreate the protected " +
                    "part prefab from the source mesh, or restore the asset from version control.",
                    detail: "reason=" + ApaProtectedMeshReasons.AuthenticationFailed +
                            "; part=" + (payload.PartId ?? string.Empty));
                return false;
            }

            byte[] plaintext;
            try
            {
                plaintext = Decrypt(payload, out var paddingFailure);
                if (paddingFailure != null)
                {
                    issue = Error(
                        ApaProtectedMeshReasons.InvalidPadding,
                        "The protected mesh payload decrypted to data with invalid padding, so it is not the " +
                        "payload that was written. Recreate the protected part prefab.",
                        detail: "reason=" + ApaProtectedMeshReasons.InvalidPadding);
                    return false;
                }
            }
            catch (CryptographicException e)
            {
                issue = Error(
                    ApaProtectedMeshReasons.InvalidPadding,
                    "The protected mesh payload could not be decrypted: " + e.Message,
                    detail: "reason=" + ApaProtectedMeshReasons.InvalidPadding);
                return false;
            }

            if (plaintext.Length != payload.PlaintextLength)
            {
                issue = Error(
                    ApaProtectedMeshReasons.PlaintextLengthMismatch,
                    "The protected mesh payload declares " + payload.PlaintextLength +
                    " plaintext bytes but produced " + plaintext.Length + ". Recreate the protected part prefab.",
                    detail: "reason=" + ApaProtectedMeshReasons.PlaintextLengthMismatch +
                            "; declared=" + payload.PlaintextLength + "; actual=" + plaintext.Length);
                return false;
            }

            return TryReadPlaintext(plaintext, out data, out issue);
        }

        /// <summary>
        /// Parses an already-authenticated plaintext payload.
        /// </summary>
        /// <remarks>
        /// Exposed because the parser is the part of the codec with the interesting failure modes — declared
        /// counts, truncation, trailing bytes — and because a caller that authenticated the bytes itself (a test
        /// crafting a hostile payload, or a future container format) must be able to reach exactly the same
        /// parser the production path uses rather than a second copy of it.
        /// </remarks>
        public static bool TryReadPlaintext(
            byte[] plaintext,
            out ApaProtectedMeshData data,
            out ValidationIssue issue)
        {
            data = null;
            issue = null;

            if (plaintext == null || plaintext.Length == 0)
            {
                issue = Error(
                    ApaProtectedMeshReasons.TruncatedPayload,
                    "The protected mesh payload carries no plaintext at all. Recreate the protected part prefab.",
                    detail: "reason=" + ApaProtectedMeshReasons.TruncatedPayload);
                return false;
            }

            if (plaintext.LongLength > ApaProtectedMeshLimits.MaxPlaintextBytes)
            {
                issue = Error(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    "The protected mesh payload declares " + plaintext.LongLength +
                    " plaintext bytes, beyond the limit of " + ApaProtectedMeshLimits.MaxPlaintextBytes + ".",
                    detail: "reason=" + ApaProtectedMeshReasons.LengthLimitExceeded +
                            "; plainBytes=" + plaintext.LongLength);
                return false;
            }

            try
            {
                data = Deserialize(plaintext);
                return true;
            }
            catch (ApaProtectedMeshFormatException e)
            {
                issue = Error(
                    e.Reason,
                    "The protected mesh payload is not usable: " + e.Message +
                    " Recreate the protected part prefab from the source mesh.",
                    detail: "reason=" + e.Reason + (string.IsNullOrEmpty(e.Detail) ? string.Empty : "; " + e.Detail));
                return false;
            }
            catch (EndOfStreamException)
            {
                issue = Error(
                    ApaProtectedMeshReasons.TruncatedPayload,
                    "The protected mesh payload ended before its last declared field, so it is truncated. " +
                    "Recreate the protected part prefab from the source mesh.",
                    detail: "reason=" + ApaProtectedMeshReasons.TruncatedPayload);
                return false;
            }
            catch (Exception e)
            {
                issue = Error(
                    ApaProtectedMeshReasons.TruncatedPayload,
                    "The protected mesh payload could not be read: " + e.GetType().Name + ": " + e.Message,
                    detail: "reason=" + ApaProtectedMeshReasons.TruncatedPayload +
                            "; exception=" + e.GetType().FullName);
                return false;
            }
        }

        /// <summary>Reads an asset's envelope into a payload value without decrypting it.</summary>
        public static bool TryReadPayload(
            ApaProtectedMeshAsset asset,
            out ApaProtectedMeshPayload payload,
            out ValidationIssue issue)
        {
            payload = null;
            issue = null;

            if (asset == null)
            {
                issue = Error(
                    ApaProtectedMeshReasons.MissingAsset,
                    "The installer expects a protected mesh payload but no protected mesh asset is assigned. " +
                    "The asset is part of the published package: ship it beside the prefab and assign it to the " +
                    "installer.",
                    detail: "reason=" + ApaProtectedMeshReasons.MissingAsset);
                return false;
            }

            if (!asset.HasPayload)
            {
                issue = Error(
                    ApaProtectedMeshReasons.IncompleteEnvelope,
                    "The protected mesh asset '" + asset.name + "' carries no complete payload. Recreate the " +
                    "protected part prefab, or restore the asset from version control.",
                    detail: "reason=" + ApaProtectedMeshReasons.IncompleteEnvelope + "; " + asset.Describe());
                return false;
            }

            payload = new ApaProtectedMeshPayload(
                asset.FormatVersion,
                asset.Codec,
                asset.PartId,
                asset.SourceFingerprint,
                asset.PlaintextLength,
                asset.CopySalt(),
                asset.CopyIv(),
                asset.CopyCiphertext(),
                asset.CopyTag());
            return true;
        }

        /// <summary>
        /// Decodes a protected asset into mesh data, authenticating it first.
        /// </summary>
        /// <param name="asset">The asset the installer references. May be null, which is a reported failure.</param>
        /// <param name="data">Receives the decoded data on success.</param>
        /// <param name="issue">Receives a blocking diagnostic on failure.</param>
        /// <param name="expectedPartId">
        /// The part id the caller expects the payload to belong to, or null to accept any. A mismatch is reported
        /// as <c>reason=part-id-mismatch</c> and fails closed: a payload that authenticates but belongs to another
        /// part must not be assembled into this one.
        /// </param>
        public static bool TryDecode(
            ApaProtectedMeshAsset asset,
            out ApaProtectedMeshData data,
            out ValidationIssue issue,
            string expectedPartId = null)
        {
            data = null;
            if (!TryReadPayload(asset, out var payload, out issue)) return false;

            if (!string.IsNullOrEmpty(expectedPartId)
                && !string.Equals(payload.PartId, expectedPartId, StringComparison.Ordinal))
            {
                issue = Error(
                    ApaProtectedMeshReasons.PartIdMismatch,
                    "The protected mesh payload belongs to part '" + payload.PartId + "', not to '" +
                    expectedPartId + "'. A payload is only assembled into the part it was created for; recreate " +
                    "the protected part prefab for this part.",
                    expectedPartId,
                    detail: "reason=" + ApaProtectedMeshReasons.PartIdMismatch +
                            "; payloadPart=" + payload.PartId +
                            "; expectedPart=" + expectedPartId);
                return false;
            }

            return TryDecode(payload, out data, out issue);
        }

        /// <summary>
        /// Decodes a protected asset straight into the immutable snapshot the assembler consumes.
        /// </summary>
        /// <param name="asset">The asset to decode.</param>
        /// <param name="snapshot">Receives the snapshot on success.</param>
        /// <param name="issue">Receives a blocking diagnostic on failure.</param>
        /// <param name="expectedPartId">The part id the payload must belong to, or null.</param>
        /// <param name="bonePaths">Live bone paths to record, or null to use the payload's recorded paths.</param>
        /// <param name="boneWorldToLocal">Live bone matrices to record, or null to use the payload's.</param>
        public static bool TryDecodeSnapshot(
            ApaProtectedMeshAsset asset,
            out MeshSnapshot snapshot,
            out ValidationIssue issue,
            string expectedPartId = null,
            IReadOnlyList<string> bonePaths = null,
            IReadOnlyList<Matrix4x4> boneWorldToLocal = null)
        {
            snapshot = null;
            if (!TryDecode(asset, out var data, out issue, expectedPartId)) return false;

            snapshot = data.CreateSnapshot(bonePaths, boneWorldToLocal);
            return true;
        }

        // ---- Serialization ----------------------------------------------------------------------------

        /// <summary>
        /// Proves a snapshot is representable in the payload before a single byte is written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Encode and decode must agree on what is representable.</b> Every bound below is one the decoder
        /// already enforces, so a snapshot that fails one of them would otherwise be encrypted into a payload
        /// this same build refuses to read — the author would get a successful creation followed by a build that
        /// cannot decode the part it just published. Checking here turns that into one precise refusal at the
        /// moment the payload is produced, and it names the same <c>reason=…</c> token the decoder would have
        /// reported.
        /// </para>
        /// <para>
        /// The checks are read-only and allocation-free: they inspect counts and ranges, never copy a buffer. A
        /// legitimate snapshot — anything <see cref="MeshSnapshotFactory"/> produces from a readable Unity mesh —
        /// passes all of them by construction.
        /// </para>
        /// </remarks>
        private static void ValidateSnapshotForSerialization(MeshSnapshot snapshot)
        {
            var vertexCount = snapshot.Vertices.Count;

            if (vertexCount == 0)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.EmptyPayload,
                    "the part mesh carries no vertices.",
                    "vertices=0");
            }

            if (vertexCount > ApaProtectedMeshLimits.MaxVertexCount)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    "the part mesh declares " + vertexCount + " vertices, beyond the limit of " +
                    ApaProtectedMeshLimits.MaxVertexCount + ".",
                    "vertices=" + vertexCount + "; limit=" + ApaProtectedMeshLimits.MaxVertexCount);
            }

            RequireEncodableString(snapshot.Name, "meshName");

            if (snapshot.IndexFormat != IndexFormat.UInt16 && snapshot.IndexFormat != IndexFormat.UInt32)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.InvalidIndexFormat,
                    "the part mesh declares index format " + (int)snapshot.IndexFormat + ".",
                    "indexFormat=" + (int)snapshot.IndexFormat);
            }

            RequireOptionalCount(snapshot.Normals.Count, vertexCount, "normals", ApaProtectedMeshLimits.MaxVertexCount);
            RequireOptionalCount(snapshot.Tangents.Count, vertexCount, "tangents", ApaProtectedMeshLimits.MaxVertexCount);
            RequireOptionalCount(snapshot.Colors.Count, vertexCount, "colors", ApaProtectedMeshLimits.MaxVertexCount);

            for (var channel = 0; channel < ApaMeshLimits.MaxUvChannels; channel++)
            {
                var present = snapshot.HasUvChannel(channel);
                var count = present ? snapshot.GetUvChannel(channel).Count : 0;
                RequireOptionalCount(count, vertexCount, "uv" + channel, ApaProtectedMeshLimits.MaxVertexCount);
            }

            if (snapshot.SubMeshCount > ApaProtectedMeshLimits.MaxSubMeshCount)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    "the part mesh declares " + snapshot.SubMeshCount + " submeshes, beyond the limit of " +
                    ApaProtectedMeshLimits.MaxSubMeshCount + ".",
                    "subMeshes=" + snapshot.SubMeshCount);
            }

            for (var subMesh = 0; subMesh < snapshot.SubMeshCount; subMesh++)
            {
                var topology = subMesh < snapshot.TopologyList.Count
                    ? snapshot.TopologyList[subMesh]
                    : MeshTopology.Triangles;

                if (!IsSupportedTopology((int)topology))
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.InvalidTopology,
                        "submesh " + subMesh + " declares topology " + (int)topology + ".",
                        "submesh=" + subMesh + "; topology=" + (int)topology);
                }

                var indices = snapshot.SubMeshIndices[subMesh];
                if (indices.Count > ApaProtectedMeshLimits.MaxIndicesPerSubMesh)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.LengthLimitExceeded,
                        "submesh " + subMesh + " declares " + indices.Count + " indices, beyond the limit of " +
                        ApaProtectedMeshLimits.MaxIndicesPerSubMesh + ".",
                        "submesh=" + subMesh + "; indices=" + indices.Count);
                }

                RequireTopologyStride(topology, indices.Count, subMesh);

                for (var i = 0; i < indices.Count; i++)
                {
                    var value = indices[i];
                    if (value < 0 || value >= vertexCount)
                    {
                        throw new ApaProtectedMeshFormatException(
                            ApaProtectedMeshReasons.IndexOutOfRange,
                            "submesh " + subMesh + " index " + i + " addresses vertex " + value +
                            " of a " + vertexCount + "-vertex mesh.",
                            "submesh=" + subMesh + "; index=" + i + "; value=" + value);
                    }
                }
            }

            RequireOptionalCount(
                snapshot.SkinWeights.Count, vertexCount, "boneWeights", ApaProtectedMeshLimits.MaxVertexCount);
            RequireCount(snapshot.SkinBindPoses.Count, ApaProtectedMeshLimits.MaxBoneCount, "bindPoses");
            RequireCount(snapshot.BoneWorldToLocalMatrices.Count, ApaProtectedMeshLimits.MaxBoneCount, "boneMatrices");
            RequireCount(snapshot.BoneSignature.Count, ApaProtectedMeshLimits.MaxBoneCount, "bonePaths");

            for (var i = 0; i < snapshot.BoneSignature.Count; i++)
            {
                RequireEncodableString(snapshot.BoneSignature.PathAt(i), "bonePaths[" + i + "]");
            }

            if (snapshot.BlendShapeCount > ApaProtectedMeshLimits.MaxBlendShapeCount)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    "the part mesh declares " + snapshot.BlendShapeCount + " blend shapes, beyond the limit of " +
                    ApaProtectedMeshLimits.MaxBlendShapeCount + ".",
                    "blendShapes=" + snapshot.BlendShapeCount);
            }

            var totalFrames = 0;
            for (var shape = 0; shape < snapshot.BlendShapeCount; shape++)
            {
                RequireEncodableString(snapshot.Shapes[shape], "blendShapes[" + shape + "]");

                var frames = snapshot.BlendShapeFrames[shape];
                var frameCount = frames != null ? frames.Count : 0;
                if (frameCount > ApaProtectedMeshLimits.MaxFramesPerShape)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.LengthLimitExceeded,
                        "blend shape " + shape + " declares " + frameCount + " frames, beyond the limit of " +
                        ApaProtectedMeshLimits.MaxFramesPerShape + ".",
                        "shape=" + shape + "; frames=" + frameCount);
                }

                totalFrames += frameCount;
                if (totalFrames > ApaProtectedMeshLimits.MaxTotalBlendShapeFrames)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.LengthLimitExceeded,
                        "the part mesh declares more than " + ApaProtectedMeshLimits.MaxTotalBlendShapeFrames +
                        " blend shape frames in total.",
                        "totalFrames=" + totalFrames);
                }

                for (var frame = 0; frame < frameCount; frame++)
                {
                    var value = frames[frame];
                    if (value == null)
                    {
                        throw new ApaProtectedMeshFormatException(
                            ApaProtectedMeshReasons.CountMismatch,
                            "blend shape '" + (snapshot.Shapes[shape] ?? string.Empty) + "' frame " + frame +
                            " is missing.",
                            "shape=" + shape + "; frame=" + frame);
                    }

                    RequireDeltaCount(value.DeltaVertices.Count, vertexCount, shape, frame, "position");
                    RequireDeltaCount(value.DeltaNormals.Count, vertexCount, shape, frame, "normal");
                    RequireDeltaCount(value.DeltaTangents.Count, vertexCount, shape, frame, "tangent");
                }
            }
        }

        private static void RequireEncodableString(string value, string what)
        {
            if (string.IsNullOrEmpty(value)) return;

            var bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > ApaProtectedMeshLimits.MaxStringBytes)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.MalformedString,
                    what + " is " + bytes + " UTF-8 bytes, beyond the limit of " +
                    ApaProtectedMeshLimits.MaxStringBytes + ".",
                    what + "=" + bytes);
            }
        }

        private static void RequireOptionalCount(int count, int vertexCount, string what, int limit)
        {
            if (count > limit)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    what + " declares " + count + ", beyond the limit of " + limit + ".",
                    what + "=" + count + "; limit=" + limit);
            }

            if (count != 0 && count != vertexCount)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.CountMismatch,
                    what + " carries " + count + " entries for " + vertexCount +
                    " vertices; it must carry either none or exactly one per vertex.",
                    what + "=" + count + "; vertices=" + vertexCount);
            }
        }

        private static void RequireCount(int count, int limit, string what)
        {
            if (count > limit)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    what + " declares " + count + ", beyond the limit of " + limit + ".",
                    what + "=" + count + "; limit=" + limit);
            }
        }

        private static void RequireDeltaCount(int count, int vertexCount, int shape, int frame, string channel)
        {
            if (count == vertexCount) return;

            throw new ApaProtectedMeshFormatException(
                ApaProtectedMeshReasons.CountMismatch,
                "blend shape " + shape + " frame " + frame + " carries " + count + " " + channel +
                " deltas for " + vertexCount + " vertices.",
                "shape=" + shape + "; frame=" + frame + "; " + channel + "=" + count + "; vertices=" + vertexCount);
        }

        private static byte[] Serialize(MeshSnapshot snapshot)
        {
            ValidateSnapshotForSerialization(snapshot);

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadMagic);
                writer.Write(PayloadVersion);

                writer.Write(snapshot.Name ?? string.Empty);
                writer.Write(snapshot.Bounds.center.x);
                writer.Write(snapshot.Bounds.center.y);
                writer.Write(snapshot.Bounds.center.z);
                writer.Write(snapshot.Bounds.extents.x);
                writer.Write(snapshot.Bounds.extents.y);
                writer.Write(snapshot.Bounds.extents.z);
                writer.Write((int)snapshot.IndexFormat);

                var vertices = snapshot.Vertices;
                writer.Write(vertices.Count);
                for (var i = 0; i < vertices.Count; i++) WriteVector3(writer, vertices[i]);

                WriteOptionalVector3(writer, snapshot.Normals);
                WriteOptionalVector4(writer, snapshot.Tangents);
                WriteOptionalColors(writer, snapshot.Colors);

                for (var channel = 0; channel < ApaMeshLimits.MaxUvChannels; channel++)
                {
                    var present = snapshot.HasUvChannel(channel);
                    WriteOptionalVector4(writer, present ? snapshot.GetUvChannel(channel) : null);
                }

                writer.Write(snapshot.SubMeshCount);
                for (var subMesh = 0; subMesh < snapshot.SubMeshCount; subMesh++)
                {
                    writer.Write((int)snapshot.TopologyList[subMesh]);
                    var indices = snapshot.SubMeshIndices[subMesh];
                    writer.Write(indices.Count);
                    for (var i = 0; i < indices.Count; i++) writer.Write(indices[i]);
                }

                writer.Write(snapshot.SkinWeights.Count);
                for (var i = 0; i < snapshot.SkinWeights.Count; i++)
                {
                    var weight = snapshot.SkinWeights[i];
                    writer.Write(weight.boneIndex0);
                    writer.Write(weight.boneIndex1);
                    writer.Write(weight.boneIndex2);
                    writer.Write(weight.boneIndex3);
                    writer.Write(weight.weight0);
                    writer.Write(weight.weight1);
                    writer.Write(weight.weight2);
                    writer.Write(weight.weight3);
                }

                writer.Write(snapshot.SkinBindPoses.Count);
                for (var i = 0; i < snapshot.SkinBindPoses.Count; i++) WriteMatrix(writer, snapshot.SkinBindPoses[i]);

                writer.Write(snapshot.BlendShapeCount);
                for (var shape = 0; shape < snapshot.BlendShapeCount; shape++)
                {
                    writer.Write(snapshot.Shapes[shape] ?? string.Empty);
                    var frames = snapshot.BlendShapeFrames[shape];
                    writer.Write(frames.Count);
                    for (var frame = 0; frame < frames.Count; frame++)
                    {
                        var value = frames[frame];
                        if (value == null)
                        {
                            // A null frame cannot be represented in the payload. The factory never produces one;
                            // refusing here keeps the decoder from having to invent a meaning for an absent frame.
                            throw new ApaProtectedMeshFormatException(
                                ApaProtectedMeshReasons.CountMismatch,
                                "blend shape '" + snapshot.Shapes[shape] + "' frame " + frame + " is missing.",
                                "shape=" + shape + "; frame=" + frame);
                        }

                        writer.Write(value.Weight);
                        WriteVector3Array(writer, value.DeltaVertices, vertices.Count);
                        WriteVector3Array(writer, value.DeltaNormals, vertices.Count);
                        WriteVector3Array(writer, value.DeltaTangents, vertices.Count);
                    }
                }

                writer.Write(snapshot.BoneWorldToLocalMatrices.Count);
                for (var i = 0; i < snapshot.BoneWorldToLocalMatrices.Count; i++)
                    WriteMatrix(writer, snapshot.BoneWorldToLocalMatrices[i]);

                writer.Write(snapshot.BoneSignature.Count);
                for (var i = 0; i < snapshot.BoneSignature.Count; i++)
                    writer.Write(snapshot.BoneSignature.PathAt(i) ?? string.Empty);

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static ApaProtectedMeshData Deserialize(byte[] plaintext)
        {
            using (var stream = new MemoryStream(plaintext, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                var magic = ReadString(reader);
                if (!string.Equals(magic, PayloadMagic, StringComparison.Ordinal))
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.BadMagic,
                        "the payload does not start with the protected-mesh magic.",
                        "magic=" + magic);
                }

                var version = reader.ReadInt32();
                if (version != PayloadVersion)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.UnsupportedPayloadVersion,
                        "the payload declares version " + version + ", which this build does not read.",
                        "found=" + version + "; supported=" + PayloadVersion);
                }

                var name = ReadString(reader);
                var bounds = new Bounds(
                    new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Vector3.zero);
                bounds.extents = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var indexFormatValue = reader.ReadInt32();
                if (indexFormatValue != (int)IndexFormat.UInt16 && indexFormatValue != (int)IndexFormat.UInt32)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.InvalidIndexFormat,
                        "the payload declares index format " + indexFormatValue + ".",
                        "indexFormat=" + indexFormatValue);
                }

                var vertexCount = ReadCount(reader, ApaProtectedMeshLimits.MaxVertexCount, 4, "vertexCount");
                var vertices = ReadVector3Array(reader, vertexCount, "vertices");

                var normals = ReadOptionalVector3Array(reader, vertexCount, "normals");
                var tangents = ReadOptionalVector4Array(reader, vertexCount, "tangents");
                var colors = ReadOptionalColorArray(reader, vertexCount, "colors");

                var uvs = MeshSnapshot.NewUvArray();
                for (var channel = 0; channel < ApaMeshLimits.MaxUvChannels; channel++)
                {
                    uvs[channel] = ReadOptionalVector4Array(reader, vertexCount, "uv" + channel)
                                   ?? Array.Empty<Vector4>();
                }

                var subMeshCount = ReadCount(reader, ApaProtectedMeshLimits.MaxSubMeshCount, 4, "subMeshCount");
                var subMeshes = new int[subMeshCount][];
                var topologies = new MeshTopology[subMeshCount];
                for (var subMesh = 0; subMesh < subMeshCount; subMesh++)
                {
                    var topologyValue = reader.ReadInt32();
                    if (!IsSupportedTopology(topologyValue))
                    {
                        throw new ApaProtectedMeshFormatException(
                            ApaProtectedMeshReasons.InvalidTopology,
                            "submesh " + subMesh + " declares topology " + topologyValue + ".",
                            "submesh=" + subMesh + "; topology=" + topologyValue);
                    }

                    topologies[subMesh] = (MeshTopology)topologyValue;

                    var indexCount = ReadCount(
                        reader, ApaProtectedMeshLimits.MaxIndicesPerSubMesh, 4, "submesh " + subMesh + " indices");
                    var indices = ReadIndices(reader, indexCount, vertexCount, subMesh);
                    RequireTopologyStride(topologies[subMesh], indices.Length, subMesh);
                    subMeshes[subMesh] = indices;
                }

                var boneWeightCount = ReadCount(reader, ApaProtectedMeshLimits.MaxVertexCount, 16, "boneWeights");
                if (boneWeightCount != 0 && boneWeightCount != vertexCount)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.CountMismatch,
                        "the payload declares " + boneWeightCount + " bone weights for " + vertexCount +
                        " vertices; it must declare either none or exactly one per vertex.",
                        "boneWeights=" + boneWeightCount + "; vertices=" + vertexCount);
                }

                var boneWeights = new BoneWeight[boneWeightCount];
                for (var i = 0; i < boneWeightCount; i++)
                {
                    var index0 = reader.ReadInt32();
                    var index1 = reader.ReadInt32();
                    var index2 = reader.ReadInt32();
                    var index3 = reader.ReadInt32();
                    RequireBoneIndex(index0, i, "boneWeights");
                    RequireBoneIndex(index1, i, "boneWeights");
                    RequireBoneIndex(index2, i, "boneWeights");
                    RequireBoneIndex(index3, i, "boneWeights");

                    boneWeights[i] = new BoneWeight
                    {
                        boneIndex0 = index0,
                        boneIndex1 = index1,
                        boneIndex2 = index2,
                        boneIndex3 = index3,
                        weight0 = reader.ReadSingle(),
                        weight1 = reader.ReadSingle(),
                        weight2 = reader.ReadSingle(),
                        weight3 = reader.ReadSingle()
                    };
                }

                var bindPoseCount = ReadCount(reader, ApaProtectedMeshLimits.MaxBoneCount, 64, "bindPoses");
                var bindPoses = new Matrix4x4[bindPoseCount];
                for (var i = 0; i < bindPoseCount; i++) bindPoses[i] = ReadMatrix(reader);

                var shapeCount = ReadCount(reader, ApaProtectedMeshLimits.MaxBlendShapeCount, 4, "blendShapes");
                var shapeNames = new string[shapeCount];
                var shapeFrameCounts = new int[shapeCount];
                var shapeFrames = new BlendShapeFrameSnapshot[shapeCount][];
                var totalFrames = 0;
                for (var shape = 0; shape < shapeCount; shape++)
                {
                    shapeNames[shape] = ReadString(reader);
                    var frameCount = ReadCount(
                        reader, ApaProtectedMeshLimits.MaxFramesPerShape, 4, "blend shape " + shape + " frames");
                    totalFrames += frameCount;
                    if (totalFrames > ApaProtectedMeshLimits.MaxTotalBlendShapeFrames)
                    {
                        throw new ApaProtectedMeshFormatException(
                            ApaProtectedMeshReasons.LengthLimitExceeded,
                            "the payload declares more than " + ApaProtectedMeshLimits.MaxTotalBlendShapeFrames +
                            " blend shape frames in total.",
                            "totalFrames=" + totalFrames);
                    }

                    shapeFrameCounts[shape] = frameCount;
                    shapeFrames[shape] = new BlendShapeFrameSnapshot[frameCount];
                    for (var frame = 0; frame < frameCount; frame++)
                    {
                        var weight = reader.ReadSingle();
                        var deltaVertices = ReadRequiredVector3Array(
                            reader, vertexCount, "blend shape position deltas");
                        var deltaNormals = ReadRequiredVector3Array(
                            reader, vertexCount, "blend shape normal deltas");
                        var deltaTangents = ReadRequiredVector3Array(
                            reader, vertexCount, "blend shape tangent deltas");
                        shapeFrames[shape][frame] = new BlendShapeFrameSnapshot(
                            weight, deltaVertices, deltaNormals, deltaTangents);
                    }
                }

                var boneMatrixCount = ReadCount(reader, ApaProtectedMeshLimits.MaxBoneCount, 64, "boneMatrices");
                var boneMatrices = new Matrix4x4[boneMatrixCount];
                for (var i = 0; i < boneMatrixCount; i++) boneMatrices[i] = ReadMatrix(reader);

                var bonePathCount = ReadCount(reader, ApaProtectedMeshLimits.MaxBoneCount, 1, "bonePaths");
                var bonePaths = new string[bonePathCount];
                for (var i = 0; i < bonePathCount; i++) bonePaths[i] = ReadString(reader);

                if (stream.Position != stream.Length)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.TrailingGarbage,
                        "the payload carries " + (stream.Length - stream.Position) +
                        " byte(s) after its last field.",
                        "trailing=" + (stream.Length - stream.Position));
                }

                if (vertexCount == 0)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.EmptyPayload,
                        "the payload carries no vertices.",
                        "vertices=0");
                }

                return new ApaProtectedMeshData(
                    name,
                    bounds,
                    (IndexFormat)indexFormatValue,
                    vertices,
                    normals ?? Array.Empty<Vector3>(),
                    tangents ?? Array.Empty<Vector4>(),
                    colors ?? Array.Empty<Color>(),
                    uvs,
                    subMeshes,
                    topologies,
                    boneWeights,
                    bindPoses,
                    shapeNames,
                    shapeFrameCounts,
                    shapeFrames,
                    boneMatrices,
                    bonePaths);
            }
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static void WriteOptionalVector3(BinaryWriter writer, IReadOnlyList<Vector3> values)
        {
            var count = values != null ? values.Count : 0;
            writer.Write(count);
            for (var i = 0; i < count; i++) WriteVector3(writer, values[i]);
        }

        private static void WriteOptionalVector4(BinaryWriter writer, IReadOnlyList<Vector4> values)
        {
            var count = values != null ? values.Count : 0;
            writer.Write(count);
            for (var i = 0; i < count; i++)
            {
                var value = values[i];
                writer.Write(value.x);
                writer.Write(value.y);
                writer.Write(value.z);
                writer.Write(value.w);
            }
        }

        private static void WriteOptionalColors(BinaryWriter writer, IReadOnlyList<Color> values)
        {
            var count = values != null ? values.Count : 0;
            writer.Write(count);
            for (var i = 0; i < count; i++)
            {
                var value = values[i];
                writer.Write(value.r);
                writer.Write(value.g);
                writer.Write(value.b);
                writer.Write(value.a);
            }
        }

        private static void WriteVector3Array(BinaryWriter writer, IReadOnlyList<Vector3> values, int vertexCount)
        {
            var count = values != null ? values.Count : 0;

            // A frame whose delta arrays do not address every vertex cannot be represented: the decoder requires
            // exactly one delta per vertex, so encoding one anyway would produce a payload this build refuses to
            // read. Refusing at encode time is the fail-closed direction and names the source of the problem.
            if (count != 0 && count != vertexCount)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.CountMismatch,
                    "a blend shape frame carries " + count + " deltas for " + vertexCount + " vertices.",
                    "deltas=" + count + "; vertices=" + vertexCount);
            }

            writer.Write(count);
            for (var i = 0; i < count; i++) WriteVector3(writer, values[i]);
        }

        private static void WriteMatrix(BinaryWriter writer, Matrix4x4 value)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++) writer.Write(value[row, column]);
            }
        }

        // ---- Reading helpers --------------------------------------------------------------------------

        private static int ReadCount(BinaryReader reader, int limit, int elementSize, string what)
        {
            var count = reader.ReadInt32();
            if (count < 0)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.CountOutOfRange,
                    what + " is negative (" + count + ").",
                    what + "=" + count);
            }

            if (count > limit)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.LengthLimitExceeded,
                    what + " declares " + count + ", beyond the limit of " + limit + ".",
                    what + "=" + count + "; limit=" + limit);
            }

            // A count that cannot fit in the bytes that remain is a truncation, not a big array: refusing it here
            // is what keeps a hostile count from turning into a multi-gigabyte allocation.
            var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (elementSize > 0 && (long)count * elementSize > remaining)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.TruncatedPayload,
                    what + " declares " + count + " entries but only " + remaining + " byte(s) remain.",
                    what + "=" + count + "; remaining=" + remaining);
            }

            return count;
        }

        private static Vector3[] ReadVector3Array(BinaryReader reader, int count, string what)
        {
            if (count == 0) return Array.Empty<Vector3>();
            if ((long)count * 12 > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.TruncatedPayload,
                    what + " declares " + count + " entries but the payload ends first.",
                    what + "=" + count);
            }

            var result = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            return result;
        }

        private static Vector3[] ReadOptionalVector3Array(BinaryReader reader, int vertexCount, string what)
        {
            var count = ReadCount(reader, ApaProtectedMeshLimits.MaxVertexCount, 12, what);
            RequireOptionalCount(count, vertexCount, what);
            return count == 0 ? Array.Empty<Vector3>() : ReadVector3Array(reader, count, what);
        }

        private static Vector4[] ReadOptionalVector4Array(BinaryReader reader, int vertexCount, string what)
        {
            var count = ReadCount(reader, ApaProtectedMeshLimits.MaxVertexCount, 16, what);
            RequireOptionalCount(count, vertexCount, what);
            if (count == 0) return Array.Empty<Vector4>();

            var result = new Vector4[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = new Vector4(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            return result;
        }

        private static Color[] ReadOptionalColorArray(BinaryReader reader, int vertexCount, string what)
        {
            var count = ReadCount(reader, ApaProtectedMeshLimits.MaxVertexCount, 16, what);
            RequireOptionalCount(count, vertexCount, what);
            if (count == 0) return Array.Empty<Color>();

            var result = new Color[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = new Color(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            return result;
        }

        private static void RequireOptionalCount(int count, int vertexCount, string what)
        {
            if (count != 0 && count != vertexCount)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.CountMismatch,
                    what + " declares " + count + " entries for " + vertexCount +
                    " vertices; it must declare either none or exactly one per vertex.",
                    what + "=" + count + "; vertices=" + vertexCount);
            }
        }

        private static Vector3[] ReadRequiredVector3Array(BinaryReader reader, int expected, string what)
        {
            var count = ReadCount(reader, ApaProtectedMeshLimits.MaxVertexCount, 12, what);
            if (count != expected)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.CountMismatch,
                    what + " declares " + count + " entries for " + expected + " vertices.",
                    what + "=" + count + "; vertices=" + expected);
            }

            return count == 0 ? Array.Empty<Vector3>() : ReadVector3Array(reader, count, what);
        }

        private static int[] ReadIndices(BinaryReader reader, int count, int vertexCount, int subMesh)
        {
            if (count == 0) return Array.Empty<int>();
            if ((long)count * 4 > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.TruncatedPayload,
                    "submesh " + subMesh + " declares " + count + " indices but the payload ends first.",
                    "submesh=" + subMesh + "; indices=" + count);
            }

            var result = new int[count];
            for (var i = 0; i < count; i++)
            {
                var value = reader.ReadInt32();
                if (value < 0 || value >= vertexCount)
                {
                    throw new ApaProtectedMeshFormatException(
                        ApaProtectedMeshReasons.IndexOutOfRange,
                        "submesh " + subMesh + " index " + i + " addresses vertex " + value +
                        " of a " + vertexCount + "-vertex mesh.",
                        "submesh=" + subMesh + "; index=" + i + "; value=" + value + "; vertices=" + vertexCount);
                }

                result[i] = value;
            }

            return result;
        }

        private static Matrix4x4 ReadMatrix(BinaryReader reader)
        {
            var matrix = new Matrix4x4();
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++) matrix[row, column] = reader.ReadSingle();
            }

            return matrix;
        }

        private static string ReadString(BinaryReader reader)
        {
            var byteCount = Read7BitEncodedInt(reader);
            if (byteCount < 0 || byteCount > ApaProtectedMeshLimits.MaxStringBytes)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.MalformedString,
                    "a stored string declares " + byteCount + " bytes, beyond the limit of " +
                    ApaProtectedMeshLimits.MaxStringBytes + ".",
                    "bytes=" + byteCount);
            }

            var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (byteCount > remaining)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.TruncatedPayload,
                    "a stored string declares " + byteCount + " bytes but only " + remaining + " remain.",
                    "bytes=" + byteCount + "; remaining=" + remaining);
            }

            if (byteCount == 0) return string.Empty;

            var bytes = reader.ReadBytes(byteCount);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.MalformedString,
                    "a stored string is not valid UTF-8.",
                    "bytes=" + byteCount);
            }
        }

        /// <summary>
        /// Reads the 7-bit encoded length prefix <see cref="BinaryWriter.Write(string)"/> writes.
        /// </summary>
        /// <remarks>
        /// Reimplemented rather than delegating to <see cref="BinaryReader.ReadString"/>, which allocates the
        /// declared length before the caller can check it against a limit.
        /// </remarks>
        private static int Read7BitEncodedInt(BinaryReader reader)
        {
            var result = 0;
            var shift = 0;
            while (shift < 35)
            {
                var value = reader.ReadByte();
                result |= (value & 0x7F) << shift;
                if ((value & 0x80) == 0) return result;
                shift += 7;
            }

            throw new ApaProtectedMeshFormatException(
                ApaProtectedMeshReasons.MalformedString,
                "a stored string carries a malformed length prefix.",
                "reason=malformed-length-prefix");
        }

        private static bool IsSupportedTopology(int value)
        {
            return value == (int)MeshTopology.Triangles
                   || value == (int)MeshTopology.Lines
                   || value == (int)MeshTopology.LineStrip
                   || value == (int)MeshTopology.Points
                   || value == (int)MeshTopology.Quads;
        }

        private static void RequireTopologyStride(MeshTopology topology, int indexCount, int subMesh)
        {
            var stride = topology == MeshTopology.Triangles ? 3
                : topology == MeshTopology.Quads ? 4
                : topology == MeshTopology.Lines ? 2
                : 0;

            if (stride > 0 && indexCount % stride != 0)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.InvalidTopology,
                    "submesh " + subMesh + " is a " + topology + " list with " + indexCount +
                    " indices, which is not a multiple of " + stride + ".",
                    "submesh=" + subMesh + "; topology=" + topology + "; indices=" + indexCount);
            }
        }

        private static void RequireBoneIndex(int value, int vertex, string what)
        {
            if (value < 0 || value >= ApaProtectedMeshLimits.MaxBoneCount)
            {
                throw new ApaProtectedMeshFormatException(
                    ApaProtectedMeshReasons.IndexOutOfRange,
                    what + " entry " + vertex + " references bone " + value +
                    ", beyond the limit of " + ApaProtectedMeshLimits.MaxBoneCount + ".",
                    what + "=" + vertex + "; bone=" + value);
            }
        }

        // ---- Cryptography -----------------------------------------------------------------------------

        private static byte[] Encrypt(byte[] plaintext, string partId, byte[] salt, byte[] iv)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = DeriveKey(partId, salt, 0, 32);
                aes.IV = iv;

                using (var encryptor = aes.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                }
            }
        }

        private static byte[] Decrypt(ApaProtectedMeshPayload payload, out string paddingFailure)
        {
            paddingFailure = null;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = DeriveKey(payload.PartId, payload.Salt, 0, 32);
                aes.IV = payload.Iv;

                using (var decryptor = aes.CreateDecryptor())
                {
                    try
                    {
                        return decryptor.TransformFinalBlock(
                            payload.Ciphertext, 0, payload.Ciphertext.Length);
                    }
                    catch (CryptographicException)
                    {
                        // The tag already proved the ciphertext is the one that was written, so a padding failure
                        // here means the payload was produced by a different construction (a different iteration
                        // count or secret) rather than that it was tampered with. Reported by name either way.
                        paddingFailure = ApaProtectedMeshReasons.InvalidPadding;
                        return Array.Empty<byte>();
                    }
                }
            }
        }

        /// <summary>Derives the AES key followed by the HMAC key from the secret, the part id, and the salt.</summary>
        private static byte[] DeriveKey(string partId, byte[] salt, int offset, int count)
        {
            var full = DeriveKeyMaterial(partId, salt);
            var result = new byte[count];
            Array.Copy(full, offset, result, 0, count);
            return result;
        }

        private static byte[] DeriveKeyMaterial(string partId, byte[] salt)
        {
            var context = DerivationSecret + "|" + (partId ?? string.Empty);
            using (var derive = new Rfc2898DeriveBytes(
                       context, salt, KeyDerivationIterations, HashAlgorithmName.SHA256))
            {
                return derive.GetBytes(DerivedKeyBytes);
            }
        }

        private static byte[] ComputeTag(
            int formatVersion,
            string codec,
            string partId,
            string sourceFingerprint,
            int plaintextLength,
            byte[] salt,
            byte[] iv,
            byte[] ciphertext)
        {
            byte[] header;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(formatVersion);
                writer.Write(codec ?? string.Empty);
                writer.Write(partId ?? string.Empty);
                writer.Write(sourceFingerprint ?? string.Empty);
                writer.Write(plaintextLength);
                writer.Flush();
                header = stream.ToArray();
            }

            var hmacKey = DeriveKey(partId, salt, 32, 32);
            using (var hmac = new HMACSHA256(hmacKey))
            {
                var buffer = new byte[header.Length + salt.Length + iv.Length + ciphertext.Length];
                var offset = 0;
                Buffer.BlockCopy(header, 0, buffer, offset, header.Length);
                offset += header.Length;
                Buffer.BlockCopy(salt, 0, buffer, offset, salt.Length);
                offset += salt.Length;
                Buffer.BlockCopy(iv, 0, buffer, offset, iv.Length);
                offset += iv.Length;
                Buffer.BlockCopy(ciphertext, 0, buffer, offset, ciphertext.Length);
                return hmac.ComputeHash(buffer);
            }
        }

        /// <summary>
        /// Compares two tags without leaking where they first differ through timing.
        /// </summary>
        /// <remarks>
        /// The length check is allowed to be non-constant: both lengths are public (the tag length is a format
        /// constant), and only the contents are secret.
        /// </remarks>
        private static bool FixedTimeEquals(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null) return false;
            if (expected.Length != actual.Length) return false;

            var difference = 0;
            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ actual[i];
            return difference == 0;
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            return bytes;
        }

        private static ValidationIssue Error(string reason, string message, string partId = null, string detail = null)
        {
            return ValidationIssue.Error(
                ApaErrorCode.ProtectedMeshInvalid,
                ApaIssuePhase.Attributes,
                message,
                partId,
                detail: detail ?? ("reason=" + reason));
        }

        /// <summary>
        /// A payload-format failure carrying the stable reason token the diagnostic reports.
        /// </summary>
        /// <remarks>
        /// A private exception type rather than a bool-and-out-parameter chain: the parser is a nested sequence of
        /// reads, and threading a reason through every one of them would make the happy path unreadable. Nothing
        /// escapes this assembly: <see cref="TryReadPlaintext"/> converts every instance into a diagnostic.
        /// </remarks>
        private sealed class ApaProtectedMeshFormatException : Exception
        {
            internal string Reason { get; }
            internal string Detail { get; }

            internal ApaProtectedMeshFormatException(string reason, string message, string detail)
                : base(message)
            {
                Reason = reason;
                Detail = detail;
            }
        }
    }
}

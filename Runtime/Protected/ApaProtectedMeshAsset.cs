using System;
using UnityEngine;

namespace AvatarPartAssembler
{
    /// <summary>
    /// The APA-owned container for one part's protected mesh payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this asset is.</b> A creator who publishes a licensed part can ask the Part Authoring window to
    /// create the part prefab in protected mode. In that mode the part mesh is serialized into this asset as an
    /// authenticated, encrypted binary payload instead of being referenced as a Unity <see cref="Mesh"/>, and the
    /// prefab's part renderer is saved with no mesh at all. The prefab therefore carries no dependency on the
    /// source <c>.fbx</c> or mesh asset, which is the whole point of the feature: the creator can hand a recipient
    /// the prefab plus this asset without handing them the mesh asset.
    /// </para>
    /// <para>
    /// <b>What it deliberately does not contain.</b> No key material, no Unity object reference, no mesh asset
    /// reference, and no file path. The asset holds only the format/codec identifiers, the stable part id and the
    /// source fingerprint that were used when the payload was created, the payload lengths, and the raw
    /// salt/IV/ciphertext/authentication-tag bytes. The key is derived at decode time from a package-local secret
    /// and the values stored here (see <c>ApaProtectedMeshCodec</c>), so a leaked asset does not leak a key.
    /// </para>
    /// <para>
    /// <b>This is a distribution format, not an unextractable DRM boundary.</b> The package ships the derivation
    /// secret inside its own assemblies, and a build that can run in the Editor can be observed while it runs, so
    /// a determined recipient can recover the mesh. What the payload does provide is that the distributed part no
    /// longer ships a raw mesh asset, that tampering with the payload is detected before any geometry is used, and
    /// that an accidental edit fails closed with a precise diagnostic rather than assembling wrong geometry.
    /// </para>
    /// <para>
    /// <b>This type is Runtime data only.</b> It has no <c>UnityEditor</c> dependency and no cryptographic
    /// implementation: encoding, decoding, and asset writing live in the Editor assembly. That keeps a shipped
    /// prefab's data contract readable without the Editor, exactly like <see cref="ApaPartProfile"/>.
    /// </para>
    /// <para>
    /// <b>Immutable through the API.</b> Every accessor returns a copy of the stored bytes; the only way to fill
    /// the asset is <see cref="Assign"/>, which the Editor-side writer calls exactly once per write.
    /// </para>
    /// </remarks>
    public sealed class ApaProtectedMeshAsset : ScriptableObject
    {
        /// <summary>
        /// The payload envelope version this build writes and understands.
        /// </summary>
        /// <remarks>
        /// Incremented when the meaning of a stored field changes in a way an older reader cannot interpret. A
        /// reader refuses a newer version outright (<c>APA053 reason=unsupported-format-version</c>) rather than
        /// guessing, because a payload whose layout is unknown cannot be authenticated against the right header.
        /// </remarks>
        public const int CurrentFormatVersion = 1;

        /// <summary>
        /// Identifier of the authenticated-encryption construction this build writes.
        /// </summary>
        /// <remarks>
        /// AES-256-CBC with PKCS#7 padding, encrypt-then-MAC with HMAC-SHA256, and a PBKDF2-SHA256 derived key.
        /// The identifier is stored rather than implied so that a payload produced by a future construction is
        /// refused by name (<c>APA053 reason=unsupported-codec</c>) instead of being decoded with the wrong
        /// algorithm.
        /// </remarks>
        public const string CurrentCodec = "aes-256-cbc-pkcs7+hmac-sha256+pbkdf2-sha256";

        /// <summary>Length of the per-asset random salt, in bytes.</summary>
        public const int SaltLength = 16;

        /// <summary>Length of the per-asset random AES initialization vector, in bytes.</summary>
        public const int IvLength = 16;

        /// <summary>Length of the HMAC-SHA256 authentication tag, in bytes.</summary>
        public const int TagLength = 32;

        [SerializeField] private int _formatVersion = CurrentFormatVersion;
        [SerializeField] private string _codec = CurrentCodec;
        [SerializeField] private string _partId = string.Empty;
        [SerializeField] private string _sourceFingerprint = string.Empty;
        [SerializeField] private int _plaintextLength;
        [SerializeField] private byte[] _salt = Array.Empty<byte>();
        [SerializeField] private byte[] _iv = Array.Empty<byte>();
        [SerializeField] private byte[] _ciphertext = Array.Empty<byte>();
        [SerializeField] private byte[] _tag = Array.Empty<byte>();

        // The preview poll needs a cheap "did the payload change" token. Hashing a multi-megabyte ciphertext on
        // every editor frame is not acceptable, so the full hash is cached and invalidated by a cheap sample key
        // (array identity, length, and the first and last bytes). An edit that changes neither the length nor the
        // sampled bytes is still caught by the change stream observation and, if it invalidates the payload, by
        // the authentication failure at decode time.
        [NonSerialized] private int _contentRevision = -1;
        [NonSerialized] private bool _contentRevisionValid;
        [NonSerialized] private byte[] _revisionArray;
        [NonSerialized] private int _revisionLength;
        [NonSerialized] private long _revisionSampleKey;

        /// <summary>The payload envelope version stored in the asset.</summary>
        public int FormatVersion => _formatVersion;

        /// <summary>The authenticated-encryption construction identifier stored in the asset.</summary>
        public string Codec => _codec ?? string.Empty;

        /// <summary>
        /// The stable part id the payload was created for. It is part of the key-derivation context and of the
        /// authenticated header, so it cannot be changed without invalidating the payload.
        /// </summary>
        public string PartId => _partId ?? string.Empty;

        /// <summary>
        /// The deterministic content fingerprint of the source mesh at the moment the payload was created.
        /// </summary>
        /// <remarks>
        /// This is the same vocabulary <c>ApaMeshFingerprint</c> produces, recorded so that a report can state
        /// what the payload was made from. The authoritative comparison is still the decoded snapshot against
        /// <see cref="ApaPartProfile.PartMeshFingerprint"/>, which is what fails closed when the source mesh
        /// changed after the payload was written.
        /// </remarks>
        public string SourceFingerprint => _sourceFingerprint ?? string.Empty;

        /// <summary>Length in bytes of the serialized payload before encryption.</summary>
        public int PlaintextLength => _plaintextLength;

        /// <summary>Length in bytes of the encrypted payload.</summary>
        public int CiphertextLength => _ciphertext != null ? _ciphertext.Length : 0;

        /// <summary>
        /// True when every field the decoder needs is present and has the length the codec requires.
        /// </summary>
        /// <remarks>
        /// This is a shape check only: it says nothing about whether the payload authenticates. A caller that
        /// needs that answer must decode.
        /// </remarks>
        public bool HasPayload =>
            _ciphertext != null
            && _ciphertext.Length > 0
            && _salt != null && _salt.Length == SaltLength
            && _iv != null && _iv.Length == IvLength
            && _tag != null && _tag.Length == TagLength;

        /// <summary>
        /// A content revision of the encrypted payload, for preview invalidation.
        /// </summary>
        /// <remarks>
        /// The value is stable for identical bytes and changes when the payload changes. It is a cache token, not
        /// an identity: two different payloads may in principle collide, which is why it is only ever compared for
        /// equality and never used to prove authenticity.
        /// </remarks>
        public int ContentRevision
        {
            get
            {
                EnsureContentRevision();
                return _contentRevision;
            }
        }

        /// <summary>A copy of the per-asset salt.</summary>
        public byte[] CopySalt() => Copy(_salt);

        /// <summary>A copy of the per-asset initialization vector.</summary>
        public byte[] CopyIv() => Copy(_iv);

        /// <summary>A copy of the encrypted payload.</summary>
        public byte[] CopyCiphertext() => Copy(_ciphertext);

        /// <summary>A copy of the authentication tag.</summary>
        public byte[] CopyTag() => Copy(_tag);

        /// <summary>
        /// Fills the asset with one payload. Called by the Editor-side writer; nothing else may write it.
        /// </summary>
        /// <remarks>
        /// The method copies every array it is given, so the caller keeps ownership of its buffers and the asset
        /// cannot be altered through a reference the caller still holds.
        /// </remarks>
        public void Assign(
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
            _formatVersion = formatVersion;
            _codec = codec ?? string.Empty;
            _partId = partId ?? string.Empty;
            _sourceFingerprint = sourceFingerprint ?? string.Empty;
            _plaintextLength = plaintextLength;
            _salt = Copy(salt);
            _iv = Copy(iv);
            _ciphertext = Copy(ciphertext);
            _tag = Copy(tag);

            // A rewrite must be visible to the preview poll even when the sampled bytes happen to match.
            _contentRevisionValid = false;
            _contentRevision = -1;
            _revisionArray = null;
            _revisionLength = 0;
            _revisionSampleKey = 0;
        }

        /// <summary>A short human-readable description of the stored envelope, for diagnostics.</summary>
        public string Describe()
        {
            return "version=" + _formatVersion
                   + "; codec=" + Codec
                   + "; part=" + (_partId ?? string.Empty)
                   + "; plainBytes=" + _plaintextLength
                   + "; cipherBytes=" + CiphertextLength
                   + "; fingerprint=" + SourceFingerprint;
        }

        private static byte[] Copy(byte[] source)
        {
            if (source == null || source.Length == 0) return Array.Empty<byte>();
            var copy = new byte[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private void EnsureContentRevision()
        {
            var array = _ciphertext;
            var length = array != null ? array.Length : 0;
            var sample = SampleKey(array, length);

            if (_contentRevisionValid
                && ReferenceEquals(_revisionArray, array)
                && _revisionLength == length
                && _revisionSampleKey == sample)
            {
                return;
            }

            _revisionArray = array;
            _revisionLength = length;
            _revisionSampleKey = sample;
            _contentRevisionValid = true;
            _contentRevision = ComputeRevision(array, length);
        }

        /// <summary>Packs the first and last eight bytes and the length into one cheap invalidation key.</summary>
        private static long SampleKey(byte[] array, int length)
        {
            unchecked
            {
                long key = length;
                if (array == null || length == 0) return key;

                key = key * 31 + Pack(array, 0, length);
                key = key * 31 + Pack(array, length - 8, length);
                return key;
            }
        }

        private static long Pack(byte[] array, int start, int length)
        {
            unchecked
            {
                long value = 0;
                for (var i = 0; i < 8; i++)
                {
                    var index = start + i;
                    var b = index >= 0 && index < length ? array[index] : (byte)0;
                    value = (value << 8) | b;
                }

                return value;
            }
        }

        private static int ComputeRevision(byte[] array, int length)
        {
            unchecked
            {
                var hash = (uint)2166136261;
                for (var i = 0; i < length; i++)
                {
                    hash ^= array[i];
                    hash *= 16777619;
                }

                return (int)hash;
            }
        }
    }
}

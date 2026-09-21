using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Decodes protected payloads once per content identity and hands the same decoded data to every consumer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a cache and not just a decode call.</b> Decoding is deliberately expensive: a PBKDF2-SHA256
    /// derivation at <see cref="ApaProtectedMeshCodec.KeyDerivationIterations"/> iterations, an AES-CBC decrypt,
    /// and a full deserialization into fresh arrays. The preview rebuilds its node graph whenever anything in the
    /// scene changes — including changes that do not touch the part — and the assembly pass, the pre-merge
    /// fingerprint gate, and the preview capture all need the same decoded geometry. Without a cache a protected
    /// part would pay that cost several times per rebuild, and the R8 contract ("protected work is lazy and cached
    /// by protected-asset content identity") would be a comment rather than a property.
    /// </para>
    /// <para>
    /// <b>The key is the payload's content, not the asset's identity.</b> Editing the asset in place keeps its
    /// instance id and its GUID, so an identity key would serve stale geometry after a payload was replaced. The
    /// key therefore carries the asset's instance id <i>and</i> a revision of every byte the decode reads — the
    /// format version, codec, part id, source fingerprint, declared plaintext length, and the salt, IV, tag, and
    /// ciphertext. The ciphertext revision is the cheap cached one the asset already maintains
    /// (<see cref="ApaProtectedMeshAsset.ContentRevision"/>); the three small arrays are hashed directly, which
    /// costs 64 byte reads.
    /// </para>
    /// <para>
    /// <b>Only the unprotected path is free, not the protected one.</b> The cache is only ever reached from a
    /// call site that has already established that the part has no live mesh and does carry a payload, so an
    /// ordinary part never enters the codec and never touches this class. <see cref="DecodeCount"/> and
    /// <see cref="CacheHitCount"/> exist so that property is checkable from a test rather than only asserted in
    /// prose.
    /// </para>
    /// <para>
    /// <b>Failures are not cached.</b> A payload that fails authentication, is truncated, or belongs to another
    /// part is reported every time it is asked for. There is nothing reusable to keep, and caching the failure
    /// would keep a repaired asset broken until the next domain reload.
    /// </para>
    /// <para>
    /// <b>Nothing to destroy.</b> The cached value is plain managed arrays with no Unity object inside, so a
    /// domain reload simply drops the static table. No mesh, asset, or scene object is created here, and no
    /// <c>AssetDatabase</c> call is made.
    /// </para>
    /// </remarks>
    public static class ApaProtectedMeshCache
    {
        /// <summary>Number of decoded payloads retained. Four covers the parts of one avatar plus one stale generation.</summary>
        public const int Capacity = 4;

        private static readonly Dictionary<string, Entry> s_entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private static long s_sequence;

        /// <summary>Number of payloads actually decoded since the counters were last reset.</summary>
        public static int DecodeCount { get; private set; }

        /// <summary>Number of requests served from the cache since the counters were last reset.</summary>
        public static int CacheHitCount { get; private set; }

        /// <summary>Number of payloads retained right now.</summary>
        public static int Count => s_entries.Count;

        /// <summary>
        /// Returns the decoded data for a protected asset, decoding it only when the content identity is new.
        /// </summary>
        /// <param name="asset">The asset the installer references. May be null, which is a reported failure.</param>
        /// <param name="expectedPartId">
        /// The part id the caller expects the payload to belong to, or null to accept any. It is part of the cache
        /// key: the same asset asked for by two different parts is two different questions, and a payload that
        /// fails the check for one part must not be served to it from another part's cache entry.
        /// </param>
        /// <param name="data">Receives the decoded data on success.</param>
        /// <param name="issue">Receives a blocking diagnostic on failure.</param>
        /// <returns>True when the payload authenticated and parsed.</returns>
        public static bool TryDecode(
            ApaProtectedMeshAsset asset,
            string expectedPartId,
            out ApaProtectedMeshData data,
            out ValidationIssue issue)
        {
            data = null;
            issue = null;

            if (asset == null)
            {
                // Routed through the codec so the "no asset" diagnostic is the codec's one wording rather than a
                // second copy of it. This is the only call the cache makes without a key.
                return ApaProtectedMeshCodec.TryDecode(asset, out data, out issue, expectedPartId);
            }

            var key = KeyOf(asset, expectedPartId);

            if (s_entries.TryGetValue(key, out var entry))
            {
                entry.LastUseSequence = ++s_sequence;
                CacheHitCount++;
                data = entry.Data;
                return true;
            }

            DecodeCount++;
            if (!ApaProtectedMeshCodec.TryDecode(asset, out data, out issue, expectedPartId))
            {
                data = null;
                return false;
            }

            Store(key, data);
            return true;
        }

        /// <summary>
        /// The content identity of one protected asset, for preview invalidation.
        /// </summary>
        /// <remarks>
        /// Exposed because the preview needs the same "did the payload change" answer the cache keys on, and
        /// deriving it twice from different vocabularies would let a payload edit invalidate the cache while
        /// leaving the preview node considered up to date.
        /// </remarks>
        public static string ContentIdentityOf(ApaProtectedMeshAsset asset)
        {
            return asset == null ? string.Empty : KeyOf(asset, null);
        }

        /// <summary>Drops every cached payload. Used by tests and by an explicit invalidation.</summary>
        public static void Clear()
        {
            s_entries.Clear();
            s_sequence = 0;
        }

        /// <summary>Resets the decode and hit counters without dropping the cached payloads.</summary>
        public static void ResetStatistics()
        {
            DecodeCount = 0;
            CacheHitCount = 0;
        }

        private static void Store(string key, ApaProtectedMeshData data)
        {
            s_entries[key] = new Entry(data, ++s_sequence);
            TrimToCapacity();
        }

        private static void TrimToCapacity()
        {
            while (s_entries.Count > Capacity)
            {
                string victim = null;
                var oldest = long.MaxValue;

                foreach (var pair in s_entries)
                {
                    if (pair.Value.LastUseSequence >= oldest) continue;
                    oldest = pair.Value.LastUseSequence;
                    victim = pair.Key;
                }

                if (victim == null) return;
                s_entries.Remove(victim);
            }
        }

        /// <summary>
        /// Builds the cache key: the asset's identity plus a revision of every byte the decode reads.
        /// </summary>
        /// <remarks>
        /// The parts are length-prefixed before they are hashed so that two different field splits cannot produce
        /// one key. The result is a hex digest rather than the concatenation, so the dictionary never holds a
        /// multi-megabyte string.
        /// </remarks>
        private static string KeyOf(ApaProtectedMeshAsset asset, string expectedPartId)
        {
            var hash = new KeyBuilder();
            hash.Add("apa-protected-mesh-cache-v1");
            hash.Add(asset.GetInstanceID());
            hash.Add(asset.FormatVersion);
            hash.Add(asset.Codec);
            hash.Add(asset.PartId);
            hash.Add(asset.SourceFingerprint);
            hash.Add(asset.PlaintextLength);
            hash.Add(expectedPartId);
            hash.Add(asset.ContentRevision);
            hash.AddBytes(asset.CopySalt());
            hash.AddBytes(asset.CopyIv());
            hash.AddBytes(asset.CopyTag());
            return hash.Value;
        }

        private sealed class Entry
        {
            internal ApaProtectedMeshData Data { get; }
            internal long LastUseSequence { get; set; }

            internal Entry(ApaProtectedMeshData data, long sequence)
            {
                Data = data;
                LastUseSequence = sequence;
            }
        }

        /// <summary>A small FNV-1a accumulator over strings, integers, and byte arrays.</summary>
        private sealed class KeyBuilder
        {
            private const ulong OffsetBasis = 14695981039346656037UL;
            private const ulong Prime = 1099511628211UL;

            private ulong _hash = OffsetBasis;

            internal string Value => _hash.ToString("x16", CultureInfo.InvariantCulture);

            internal void Add(string value)
            {
                if (value == null)
                {
                    Add(-1);
                    return;
                }

                Add(value.Length);
                for (var i = 0; i < value.Length; i++) Mix((uint)value[i]);
            }

            internal void Add(int value)
            {
                Mix((uint)value);
            }

            internal void AddBytes(byte[] bytes)
            {
                if (bytes == null)
                {
                    Add(-1);
                    return;
                }

                Add(bytes.Length);
                for (var i = 0; i < bytes.Length; i++) Mix(bytes[i]);
            }

            private void Mix(uint value)
            {
                unchecked
                {
                    _hash ^= value & 0xFF;
                    _hash *= Prime;
                    _hash ^= (value >> 8) & 0xFF;
                    _hash *= Prime;
                    _hash ^= (value >> 16) & 0xFF;
                    _hash *= Prime;
                    _hash ^= (value >> 24) & 0xFF;
                    _hash *= Prime;
                }
            }
        }
    }
}

using System;
using AvatarPartAssembler.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Tests.Protected
{
    /// <summary>
    /// Tests for the protected-payload codec: the round trip, the authentication, the format bounds, and the
    /// parser's refusal of every malformed shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The codec is the one place where a protected part's geometry crosses a trust boundary, so the suite is
    /// split along the two questions that matter: "does a legitimate payload reproduce the source exactly" and
    /// "is every illegitimate payload refused by name". Both are answered without an asset database, because the
    /// codec deliberately has no Unity asset dependency.
    /// </para>
    /// <para>
    /// The hostile-payload cases are built through <see cref="ApaProtectedMeshCodec.TrySerialize"/>, which is the
    /// production serializer. A test that hand-wrote the format would prove only that two implementations agree,
    /// and would stop testing the real parser the moment the format changed.
    /// </para>
    /// </remarks>
    public sealed class ProtectedMeshCodecTests
    {
        private const string PartId = "part-protected";

        /// <summary>Transient assets a test created, destroyed in reverse order after it.</summary>
        private readonly System.Collections.Generic.List<UnityEngine.Object> _created =
            new System.Collections.Generic.List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
            ApaProtectedMeshCache.Clear();
        }

        // ---- Round trip ---------------------------------------------------------------------------------

        /// <summary>
        /// Every attribute <c>MeshSnapshotFactory</c> reads survives the payload: vertices, normals, tangents,
        /// colors, UV0 through UV7, two submeshes with different topologies, the index format, bounds, bone
        /// weights, bind poses, blend shapes with every frame's deltas, and the recorded bone identity.
        /// </summary>
        [Test]
        public void RoundTrip_PreservesEveryAttributeTheAssemblerReads()
        {
            var source = FullSnapshot();

            Assert.IsTrue(
                ApaProtectedMeshCodec.TryCreatePayload(source, PartId, out var payload, out var createIssue),
                createIssue != null ? createIssue.Message : "the payload was not created");
            Assert.IsTrue(
                ApaProtectedMeshCodec.TryDecode(payload, out var data, out var decodeIssue),
                decodeIssue != null ? decodeIssue.Message : "the payload was not decoded");

            // The content fingerprint covers vertices, normals, tangents, colors, every present UV channel, the
            // submesh index lists and topologies, the index format, the bounds, the skin weights, the bind poses,
            // and every blend shape frame. One comparison therefore proves all of them at once.
            var restored = data.CreateSnapshot();
            Assert.AreEqual(
                source.ContentFingerprint,
                restored.ContentFingerprint,
                "The decoded snapshot must fingerprint exactly like the source mesh.");

            Assert.AreEqual(source.Name, restored.Name);
            Assert.AreEqual(source.Bounds, restored.Bounds);
            Assert.AreEqual(IndexFormat.UInt32, restored.IndexFormat);
            Assert.AreEqual(8, restored.UvChannelCapacity);

            for (var channel = 0; channel < 8; channel++)
            {
                Assert.AreEqual(
                    source.HasUvChannel(channel),
                    restored.HasUvChannel(channel),
                    "UV channel " + channel + " presence must survive the payload.");
            }

            Assert.AreEqual(source.SubMeshCount, restored.SubMeshCount);
            Assert.AreEqual(MeshTopology.Triangles, restored.TopologyList[0]);
            Assert.AreEqual(MeshTopology.Lines, restored.TopologyList[1]);

            // The bone identity and the live bone matrices are deliberately not part of the content fingerprint
            // (paths are checked by the bone signature and matrices are pose), so they are asserted directly.
            Assert.AreEqual(source.BoneSignature.Count, data.BonePaths.Count);
            for (var i = 0; i < source.BoneSignature.Count; i++)
            {
                Assert.AreEqual(source.BoneSignature.PathAt(i), data.BonePaths[i]);
            }

            Assert.AreEqual(source.BoneWorldToLocalMatrices.Count, data.BoneWorldToLocalMatrices.Count);
            for (var i = 0; i < source.BoneWorldToLocalMatrices.Count; i++)
            {
                Assert.AreEqual(source.BoneWorldToLocalMatrices[i], data.BoneWorldToLocalMatrices[i]);
            }
        }

        /// <summary>
        /// A payload built without a part id still round-trips, and the empty id is recorded rather than
        /// substituted: a legacy profile can legitimately carry none.
        /// </summary>
        [Test]
        public void RoundTrip_EmptyPartIdIsRepresentedAndAuthenticated()
        {
            var source = MinimalSnapshot();

            Assert.IsTrue(ApaProtectedMeshCodec.TryCreatePayload(source, null, out var payload, out _));
            Assert.AreEqual(string.Empty, payload.PartId);

            Assert.IsTrue(ApaProtectedMeshCodec.TryDecode(payload, out var data, out var issue), issue?.Message);
            Assert.AreEqual(source.ContentFingerprint, data.CreateSnapshot().ContentFingerprint);
        }

        /// <summary>The asset-level entry point reports a part-id mismatch instead of assembling another part.</summary>
        [Test]
        public void Decode_RejectsAPayloadCreatedForAnotherPart()
        {
            var asset = AssetFor(MinimalSnapshot(), "part-a");

            Assert.IsFalse(ApaProtectedMeshCodec.TryDecode(asset, out _, out var issue, "part-b"));
            StringAssert.Contains(ApaProtectedMeshReasons.PartIdMismatch, issue.Detail);

            Assert.IsTrue(ApaProtectedMeshCodec.TryDecode(asset, out _, out _, "part-a"));
        }

        // ---- Authentication and envelope ---------------------------------------------------------------

        [Test]
        public void Decode_RejectsATamperedCiphertext()
        {
            var payload = PayloadFor(MinimalSnapshot());
            var tampered = payload.Ciphertext;
            tampered[tampered.Length / 2] ^= 0x40;

            var broken = Copy(payload, ciphertext: tampered);
            Assert.IsFalse(ApaProtectedMeshCodec.TryDecode(broken, out _, out var issue));
            StringAssert.Contains(ApaProtectedMeshReasons.AuthenticationFailed, issue.Detail);
        }

        /// <summary>
        /// The header is authenticated too: the part id, the codec identifier, the source fingerprint, and the
        /// declared plaintext length are covered by the tag, so editing any of them is a tamper, not a rename.
        /// </summary>
        [Test]
        public void Decode_RejectsATamperedHeader()
        {
            var payload = PayloadFor(MinimalSnapshot());

            AssertRefused(
                Copy(payload, partId: "part-other"),
                ApaProtectedMeshReasons.AuthenticationFailed,
                "a changed part id");
            AssertRefused(
                Copy(payload, sourceFingerprint: "deadbeef"),
                ApaProtectedMeshReasons.AuthenticationFailed,
                "a changed source fingerprint");
            AssertRefused(
                Copy(payload, plaintextLength: payload.PlaintextLength + 1),
                ApaProtectedMeshReasons.AuthenticationFailed,
                "a changed plaintext length");
        }

        [Test]
        public void Decode_RejectsATamperedTagAndSalt()
        {
            var payload = PayloadFor(MinimalSnapshot());

            var tag = payload.Tag;
            tag[0] ^= 0x01;
            AssertRefused(Copy(payload, tag: tag), ApaProtectedMeshReasons.AuthenticationFailed, "a changed tag");

            var salt = payload.Salt;
            salt[0] ^= 0x01;
            AssertRefused(Copy(payload, salt: salt), ApaProtectedMeshReasons.AuthenticationFailed, "a changed salt");
        }

        [Test]
        public void Decode_RejectsUnsupportedFormatVersionAndCodec()
        {
            var payload = PayloadFor(MinimalSnapshot());

            AssertRefused(
                Copy(payload, formatVersion: ApaProtectedMeshAsset.CurrentFormatVersion + 1),
                ApaProtectedMeshReasons.UnsupportedFormatVersion,
                "a newer format version");
            AssertRefused(
                Copy(payload, codec: "rot13"),
                ApaProtectedMeshReasons.UnsupportedCodec,
                "an unknown codec");
        }

        [Test]
        public void Decode_RejectsAnIncompleteEnvelope()
        {
            var payload = PayloadFor(MinimalSnapshot());

            AssertRefused(
                Copy(payload, salt: Array.Empty<byte>()),
                ApaProtectedMeshReasons.IncompleteEnvelope,
                "a missing salt");
            AssertRefused(
                Copy(payload, iv: Array.Empty<byte>()),
                ApaProtectedMeshReasons.IncompleteEnvelope,
                "a missing IV");
            AssertRefused(
                Copy(payload, tag: Array.Empty<byte>()),
                ApaProtectedMeshReasons.IncompleteEnvelope,
                "a missing tag");
            AssertRefused(
                Copy(payload, ciphertext: Array.Empty<byte>()),
                ApaProtectedMeshReasons.IncompleteEnvelope,
                "a missing ciphertext");
        }

        /// <summary>
        /// A ciphertext that is not a whole number of AES blocks cannot be produced by this writer, so it is
        /// refused as an unusable length before the decryptor ever sees it.
        /// </summary>
        [Test]
        public void Decode_RejectsACiphertextThatIsNotBlockAligned()
        {
            var payload = PayloadFor(MinimalSnapshot());
            var ciphertext = new byte[payload.Ciphertext.Length + 1];
            Array.Copy(payload.Ciphertext, ciphertext, payload.Ciphertext.Length);

            AssertRefused(
                Copy(payload, ciphertext: ciphertext),
                ApaProtectedMeshReasons.InvalidLength,
                "a ciphertext that is not a whole number of blocks");
        }

        [Test]
        public void Decode_RejectsANullOrEmptyAsset()
        {
            Assert.IsFalse(ApaProtectedMeshCodec.TryDecode((ApaProtectedMeshAsset)null, out _, out var missing));
            StringAssert.Contains(ApaProtectedMeshReasons.MissingAsset, missing.Detail);
        }

        // ---- Bounds ------------------------------------------------------------------------------------

        /// <summary>
        /// The ciphertext bound must cover the largest plaintext the writer accepts, including the padding block.
        /// A bound below the writer's own output would make this build reject a payload it had just written.
        /// </summary>
        [Test]
        public void Limits_CiphertextBoundCoversTheLargestPlaintext()
        {
            Assert.GreaterOrEqual(
                ApaProtectedMeshLimits.MaxCiphertextBytes,
                ApaProtectedMeshLimits.MaxPlaintextBytes + ApaProtectedMeshLimits.AesBlockBytes,
                "The ciphertext bound must be at least the largest plaintext plus one padding block.");

            Assert.AreEqual(
                0L,
                ApaProtectedMeshLimits.MaxCiphertextBytes % ApaProtectedMeshLimits.AesBlockBytes,
                "The ciphertext bound must be a whole number of AES blocks.");

            Assert.Greater(
                ApaProtectedMeshLimits.MaxCiphertextBytes,
                ApaProtectedMeshLimits.MaxPlaintextBytes,
                "A bound equal to the plaintext limit would refuse every payload that needed a padding block.");
        }

        /// <summary>
        /// The declared plaintext length is part of the authenticated header, so an inflated one is reported as
        /// tampering rather than as a large payload: an attacker cannot turn a length check into an allocation.
        /// </summary>
        [Test]
        public void Decode_TreatsAnInflatedDeclaredLengthAsTampering()
        {
            var payload = PayloadFor(MinimalSnapshot());

            AssertRefused(
                Copy(payload, plaintextLength: int.MaxValue),
                ApaProtectedMeshReasons.AuthenticationFailed,
                "an inflated declared plaintext length");
        }

        [Test]
        public void Encode_RefusesASnapshotTheDecoderWouldReject()
        {
            var source = MinimalSnapshot();

            // Normals that do not address every vertex cannot be represented: the decoder requires none or one
            // per vertex, so encoding one anyway would produce a payload this build refuses to read.
            var mismatched = MeshSnapshot.Create(
                "Bad",
                Vertices(4),
                new[] { Vector3.up, Vector3.up },
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { new[] { 0, 1, 2 } },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                IndexFormat.UInt16);

            Assert.IsFalse(ApaProtectedMeshCodec.TryCreatePayload(mismatched, PartId, out _, out var issue));
            StringAssert.Contains(ApaProtectedMeshReasons.CountMismatch, issue.Detail);

            // An index that addresses a vertex the mesh does not carry is refused at encode time as well.
            var badIndex = MeshSnapshot.Create(
                "BadIndex",
                Vertices(4),
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { new[] { 0, 1, 99 } },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                IndexFormat.UInt16);

            Assert.IsFalse(ApaProtectedMeshCodec.TryCreatePayload(badIndex, PartId, out _, out var indexIssue));
            StringAssert.Contains(ApaProtectedMeshReasons.IndexOutOfRange, indexIssue.Detail);

            // A triangle list whose index count is not a multiple of three cannot be read back either.
            var badStride = MeshSnapshot.Create(
                "BadStride",
                Vertices(4),
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { new[] { 0, 1 } },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                IndexFormat.UInt16);

            Assert.IsFalse(ApaProtectedMeshCodec.TryCreatePayload(badStride, PartId, out _, out var strideIssue));
            StringAssert.Contains(ApaProtectedMeshReasons.InvalidTopology, strideIssue.Detail);

            Assert.IsTrue(
                ApaProtectedMeshCodec.TryCreatePayload(source, PartId, out _, out _),
                "The valid snapshot must still be accepted; the refusals above are not a broken codec.");
        }

        // ---- Plaintext parser --------------------------------------------------------------------------

        [Test]
        public void Parser_RejectsTruncatedAndTrailingBytes()
        {
            Assert.IsTrue(ApaProtectedMeshCodec.TrySerialize(MinimalSnapshot(), out var plaintext, out _));

            var truncated = new byte[plaintext.Length / 2];
            Array.Copy(plaintext, truncated, truncated.Length);
            AssertRefusedPlaintext(truncated, ApaProtectedMeshReasons.TruncatedPayload, "a truncated payload");

            var trailing = new byte[plaintext.Length + 3];
            Array.Copy(plaintext, trailing, plaintext.Length);
            AssertRefusedPlaintext(trailing, ApaProtectedMeshReasons.TrailingGarbage, "trailing garbage");
        }

        [Test]
        public void Parser_RejectsBadMagicAndUnsupportedPayloadVersion()
        {
            Assert.IsTrue(ApaProtectedMeshCodec.TrySerialize(MinimalSnapshot(), out var plaintext, out _));

            var badMagic = (byte[])plaintext.Clone();

            // The first byte is the 7-bit length prefix of the magic, so the magic itself starts after it; a
            // changed prefix would be reported as a truncation instead, which is a different (also correct)
            // refusal and would make this test prove the wrong thing.
            badMagic[1 + 2] = (byte)'X';
            AssertRefusedPlaintext(badMagic, ApaProtectedMeshReasons.BadMagic, "a wrong magic");

            // The version is the 32-bit integer that follows the 7-bit length prefix of the magic.
            var versionOffset = 1 + ApaProtectedMeshCodec.PayloadMagic.Length;
            var badVersion = (byte[])plaintext.Clone();
            badVersion[versionOffset] = 99;
            AssertRefusedPlaintext(badVersion, ApaProtectedMeshReasons.UnsupportedPayloadVersion, "a newer version");
        }

        [Test]
        public void Parser_RejectsAnEmptyPayload()
        {
            AssertRefusedPlaintext(Array.Empty<byte>(), ApaProtectedMeshReasons.TruncatedPayload, "no bytes");
            AssertRefusedPlaintext(null, ApaProtectedMeshReasons.TruncatedPayload, "a null payload");
        }

        // ---- Cache -------------------------------------------------------------------------------------

        /// <summary>
        /// The cache decodes a payload once per content identity and serves every later request from memory.
        /// This is the R8 property that keeps a Scene View repaint from re-running PBKDF2 and AES.
        /// </summary>
        [Test]
        public void Cache_DecodesOncePerContentIdentity()
        {
            var asset = AssetFor(MinimalSnapshot(), PartId);

            ApaProtectedMeshCache.Clear();
            ApaProtectedMeshCache.ResetStatistics();

            Assert.IsTrue(ApaProtectedMeshCache.TryDecode(asset, PartId, out var first, out var issue), issue?.Message);
            Assert.AreEqual(1, ApaProtectedMeshCache.DecodeCount);
            Assert.AreEqual(0, ApaProtectedMeshCache.CacheHitCount);

            Assert.IsTrue(ApaProtectedMeshCache.TryDecode(asset, PartId, out var second, out _));
            Assert.AreEqual(1, ApaProtectedMeshCache.DecodeCount, "An unchanged payload must not be decoded twice.");
            Assert.AreEqual(1, ApaProtectedMeshCache.CacheHitCount);
            Assert.AreSame(first, second, "The cached decode must hand back the same decoded data.");

            // A different part id is a different question and must not be served the first part's entry.
            Assert.IsFalse(ApaProtectedMeshCache.TryDecode(asset, "part-other", out _, out _));
            Assert.AreEqual(2, ApaProtectedMeshCache.DecodeCount);

            // Replacing the payload changes its content identity, so the next request decodes the new bytes.
            asset.Assign(
                ApaProtectedMeshAsset.CurrentFormatVersion,
                ApaProtectedMeshAsset.CurrentCodec,
                PartId,
                "fingerprint",
                8,
                new byte[ApaProtectedMeshAsset.SaltLength],
                new byte[ApaProtectedMeshAsset.IvLength],
                new byte[16],
                new byte[ApaProtectedMeshAsset.TagLength]);

            Assert.IsFalse(ApaProtectedMeshCache.TryDecode(asset, PartId, out _, out _));
            Assert.AreEqual(3, ApaProtectedMeshCache.DecodeCount, "A rewritten payload must be decoded again.");

            ApaProtectedMeshCache.Clear();
        }

        // ---- Fixtures ----------------------------------------------------------------------------------

        /// <summary>
        /// A snapshot that carries every attribute the factory can read, including two submeshes with different
        /// topologies and all eight UV channels.
        /// </summary>
        private static MeshSnapshot FullSnapshot()
        {
            var vertices = Vertices(6);

            var normals = new Vector3[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            var colors = new Color[vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                normals[i] = new Vector3(0f, 1f, i * 0.1f).normalized;
                tangents[i] = new Vector4(1f, 0f, 0f, i % 2 == 0 ? 1f : -1f);
                colors[i] = new Color(i / 8f, 0.25f, 0.5f, 1f);
            }

            var uvs = MeshSnapshot.NewUvArray();
            for (var channel = 0; channel < 8; channel++)
            {
                var values = new Vector4[vertices.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = new Vector4(i * 0.1f + channel, channel * 0.5f, channel, i);
                }

                uvs[channel] = values;
            }

            var weights = new BoneWeight[vertices.Length];
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = new BoneWeight
                {
                    boneIndex0 = 0,
                    boneIndex1 = 1,
                    weight0 = 0.75f,
                    weight1 = 0.25f
                };
            }

            var bindPoses = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(0f, 1f, 0f)) };

            var frames = new[]
            {
                new[]
                {
                    new BlendShapeFrameSnapshot(
                        0f, Delta(vertices.Length, 0), Delta(vertices.Length, 1), Delta(vertices.Length, 2)),
                    new BlendShapeFrameSnapshot(
                        100f, Delta(vertices.Length, 3), Delta(vertices.Length, 4), Delta(vertices.Length, 5))
                }
            };

            var boneWorldToLocal = new[]
            {
                Matrix4x4.Translate(new Vector3(0f, -1f, 0f)),
                Matrix4x4.Translate(new Vector3(0f, -2f, 0f))
            };

            return MeshSnapshot.Create(
                "ProtectedSource",
                vertices,
                normals,
                tangents,
                colors,
                uvs,
                new[] { new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0, 1, 2, 3 } },
                new[] { MeshTopology.Triangles, MeshTopology.Lines },
                new Bounds(new Vector3(0.25f, 0.5f, 0.75f), new Vector3(2f, 2f, 2f)),
                IndexFormat.UInt32,
                weights,
                bindPoses,
                new[] { "Blink" },
                new[] { 2 },
                new[] { "Armature/Hips", "Armature/Spine" },
                frames,
                boneWorldToLocal);
        }

        private static MeshSnapshot MinimalSnapshot()
        {
            return MeshSnapshot.Create(
                "Minimal",
                Vertices(4),
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { new[] { 0, 1, 2 } },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                IndexFormat.UInt16);
        }

        private static Vector3[] Vertices(int count)
        {
            var result = new Vector3[count];
            for (var i = 0; i < count; i++) result[i] = new Vector3(i * 0.5f, i * 0.25f, 0f);
            return result;
        }

        private static Vector3[] Delta(int count, int seed)
        {
            var result = new Vector3[count];
            for (var i = 0; i < count; i++) result[i] = new Vector3(seed, i, seed + i);
            return result;
        }

        private static ApaProtectedMeshPayload PayloadFor(MeshSnapshot snapshot)
        {
            Assert.IsTrue(
                ApaProtectedMeshCodec.TryCreatePayload(snapshot, PartId, out var payload, out var issue),
                issue != null ? issue.Message : "the payload was not created");
            return payload;
        }

        private ApaProtectedMeshAsset AssetFor(MeshSnapshot snapshot, string partId)
        {
            Assert.IsTrue(
                ApaProtectedMeshCodec.TryCreatePayload(snapshot, partId, out var payload, out var issue),
                issue != null ? issue.Message : "the payload was not created");

            var asset = ScriptableObject.CreateInstance<ApaProtectedMeshAsset>();
            asset.Assign(
                payload.FormatVersion,
                payload.Codec,
                payload.PartId,
                payload.SourceFingerprint,
                payload.PlaintextLength,
                payload.Salt,
                payload.Iv,
                payload.Ciphertext,
                payload.Tag);
            _created.Add(asset);
            return asset;
        }
        private static ApaProtectedMeshPayload Copy(
            ApaProtectedMeshPayload source,
            int? formatVersion = null,
            string codec = null,
            string partId = null,
            string sourceFingerprint = null,
            int? plaintextLength = null,
            byte[] salt = null,
            byte[] iv = null,
            byte[] ciphertext = null,
            byte[] tag = null)
        {
            return new ApaProtectedMeshPayload(
                formatVersion ?? source.FormatVersion,
                codec ?? source.Codec,
                partId ?? source.PartId,
                sourceFingerprint ?? source.SourceFingerprint,
                plaintextLength ?? source.PlaintextLength,
                salt ?? source.Salt,
                iv ?? source.Iv,
                ciphertext ?? source.Ciphertext,
                tag ?? source.Tag);
        }

        private static void AssertRefused(ApaProtectedMeshPayload payload, string reason, string what)
        {
            Assert.IsFalse(
                ApaProtectedMeshCodec.TryDecode(payload, out var data, out var issue),
                "The codec must refuse " + what + ".");
            Assert.IsNull(data, "A refused payload must not produce decoded data.");
            Assert.IsNotNull(issue, "A refused payload must carry a diagnostic.");
            StringAssert.Contains(reason, issue.Detail, "The diagnostic must name " + reason + " for " + what + ".");
        }

        private static void AssertRefusedPlaintext(byte[] plaintext, string reason, string what)
        {
            Assert.IsFalse(
                ApaProtectedMeshCodec.TryReadPlaintext(plaintext, out var data, out var issue),
                "The parser must refuse " + what + ".");
            Assert.IsNull(data);
            Assert.IsNotNull(issue);
            StringAssert.Contains(reason, issue.Detail, "The diagnostic must name " + reason + " for " + what + ".");
        }
    }
}

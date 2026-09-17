using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the compatibility guard, which is what prevents stale authoring data from being applied to the
    /// wrong mesh.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is severe and silent: applying triangle and seam indices authored against one
    /// body to a different body removes triangles from unrelated regions and welds seams to arbitrary vertices.
    /// </remarks>
    public sealed class CompatibilityTests
    {
        /// <summary>A matching signature passes.</summary>
        [Test]
        public void MatchingSignature_Passes()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: MeshFixtures.SignatureFor(body));

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible));
        }

        /// <summary>A vertex-count mismatch is <c>APA012</c> and blocks.</summary>
        [Test]
        public void VertexCountMismatch_ReportsApa012()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var signature = MeshFixtures.SignatureFor(body);
            signature.VertexCount = body.VertexCount + 1;

            var context = MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: signature);

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "A signature mismatch must block planning.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible));
        }

        /// <summary>A submesh index-count mismatch is <c>APA012</c>.</summary>
        [Test]
        public void SubMeshIndexCountMismatch_ReportsApa012()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var signature = MeshFixtures.SignatureFor(body);
            signature.SubMeshIndexCounts = new[] { 999 };

            var context = MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: signature);

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible));
        }

        /// <summary>A topology mismatch blocks even when every submesh index count still matches.</summary>
        [Test]
        public void SubMeshTopologyMismatch_ReportsApa012()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);
            var signature = MeshFixtures.SignatureFor(body);
            signature.SubMeshTopologyValues = new[] { (int)MeshTopology.Lines };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: signature));

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible));
        }

        /// <summary>A signature missing the new safety fields reports <c>APA024</c>.</summary>
        [Test]
        public void IncompleteSignature_ReportsApa024()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);
            var signature = MeshFixtures.SignatureFor(body);
            signature.SubMeshTopologyValues = System.Array.Empty<int>();

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: signature));

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.IncompleteCompatibilitySignature));
        }

        /// <summary>A blend shape name mismatch is <c>APA012</c>, because shape indices would shift.</summary>
        [Test]
        public void BlendShapeNameMismatch_ReportsApa012()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var signature = MeshFixtures.SignatureFor(body);
            signature.BlendShapeNames = new[] { "Blink" };
            signature.BlendShapeFrameCounts = new[] { 1 };

            var context = MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: signature);

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible));
        }

        /// <summary>Blend shape frame counts are part of the guarded signature.</summary>
        [Test]
        public void BlendShapeFrameCountMismatch_ReportsApa012()
        {
            var body = WithMetadata(
                MeshFixtures.Body(8),
                new[] { "Blink" },
                new[] { 1 },
                System.Array.Empty<string>());
            var part = MeshFixtures.Part(8, apexOffset: -1f);
            var signature = MeshFixtures.SignatureFor(body);
            signature.BlendShapeFrameCounts = new[] { 2 };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: signature));

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible));
        }

        /// <summary>
        /// A bone path mismatch on a body whose meshes carry no skinning data is a warning, not a block: nothing
        /// in the configuration consumes bone identities, so a stale path list is a staleness signal.
        /// </summary>
        /// <remarks>
        /// The body still has to be a valid fixture: a mesh that records bone paths but no world-to-local matrices
        /// is treated as a skinning input by the final bone table, which then blocks with <c>APA011</c> for every
        /// recorded bone. Supplying the matrices (as a real captured body does) leaves the bone-signature warning
        /// as the only diagnostic, which is the case this test is about.
        /// </remarks>
        [Test]
        public void BoneSignatureMismatch_IsWarning()
        {
            var body = WithMetadata(
                MeshFixtures.Body(8),
                System.Array.Empty<string>(),
                System.Array.Empty<int>(),
                new[] { "Hips", "Hips/Spine" },
                MeshFixtures.BonesAt(new Vector3(0f, 1f, 0f), new Vector3(0f, 1.2f, 0f)));
            var part = MeshFixtures.Part(8, apexOffset: -1f);
            var signature = MeshFixtures.SignatureFor(body);
            signature.BonePaths = new[] { "Hips", "Hips/Other" };

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: signature));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            var issue = result.Issues.FindByCode(ApaErrorCode.PartProfileIncompatible);
            Assert.IsNotNull(issue);
            Assert.AreEqual(ApaSeverity.Warning, issue.Severity);
            StringAssert.Contains("bone-signature-advisory", issue.Detail);
            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.InvalidBindPose),
                "The fixture must be a valid skinned body, so nothing but the signature can complain.");
        }

        /// <summary>
        /// A profile with no captured signature blocks rather than being assumed compatible.
        /// </summary>
        /// <remarks>
        /// Assuming compatibility is precisely how corruption ships. An unverified profile is treated as
        /// unverified, not as valid.
        /// </remarks>
        [Test]
        public void UncapteredSignature_Blocks()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var context = MeshFixtures.Context(
                body,
                new[] { MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8)) },
                signature: new ApaAvatarCompatibilityProfile { IsCaptured = false });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "An uncaptured signature must not be treated as a match.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.PartProfileIncompatible));
        }

        /// <summary>Two parts claiming the same non-Custom slot is <c>APA013</c>.</summary>
        [Test]
        public void DuplicateStandardSlot_ReportsApa013()
        {
            var body = MeshFixtures.Body(8);
            var partA = MeshFixtures.Part(8, apexOffset: -1f, name: "PartA");
            var partB = MeshFixtures.Part(8, apexOffset: -2f, name: "PartB");

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(8), null, ApaPartSlot.LeftArm),
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(8), null, ApaPartSlot.LeftArm)
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "Two parts claiming one standard slot must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.DuplicatePartSlot));
        }

        /// <summary>Two Custom parts coexist without conflict.</summary>
        [Test]
        public void DuplicateCustomSlots_AreAllowed()
        {
            var body = MeshFixtures.Body(8);
            var partA = MeshFixtures.Part(8, apexOffset: -1f, name: "PartA");
            var partB = MeshFixtures.Part(8, apexOffset: -2f, name: "PartB");

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", partA, MeshFixtures.Seam(8), null, ApaPartSlot.Custom),
                MeshFixtures.PartSnapshot("part-b", partB, MeshFixtures.Seam(8), null, ApaPartSlot.Custom)
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.DuplicatePartSlot));
        }

        /// <summary>
        /// A slot value outside the defined enum range is <c>APA023</c> and blocks.
        /// </summary>
        /// <remarks>
        /// A serialized profile can carry an arbitrary integer. Without this check the value would fall through
        /// to the Custom exemption and be accepted silently, which is exactly the unchecked author data the
        /// validator exists to catch.
        /// </remarks>
        [Test]
        public void UndefinedSlotValue_ReportsApa023()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var snapshot = MeshFixtures.PartSnapshot(
                "part-a",
                part,
                MeshFixtures.Seam(8),
                slot: (ApaPartSlot)99);

            var context = MeshFixtures.Context(body, new[] { snapshot });
            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "An undefined slot value must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.InvalidPartSlot));
        }

        /// <summary>A profile with an unknown newer schema is <c>APA015</c>.</summary>
        [Test]
        public void NewerProfileSchema_IsRejected()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            try
            {
                profile.SchemaVersion = ApaPartProfile.CurrentSchemaVersion + 1;

                var message = string.Empty;
                Assert.IsFalse(profile.TryMigrate(out message),
                    "A newer schema version must not be accepted.");
                StringAssert.Contains("newer", message);
                Assert.IsFalse(profile.IsSchemaSupported);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>The current schema version is accepted and reports no migration needed.</summary>
        [Test]
        public void CurrentProfileSchema_IsAccepted()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            try
            {
                profile.SchemaVersion = ApaPartProfile.CurrentSchemaVersion;

                var message = string.Empty;
                Assert.IsTrue(profile.TryMigrate(out message));
                Assert.IsTrue(profile.IsSchemaSupported);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>A profile assigns a stable id exactly once.</summary>
        [Test]
        public void StablePartId_IsAssignedOnceAndPreserved()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            try
            {
                Assert.IsTrue(profile.Identity.EnsureStablePartId());
                var id = profile.Identity.PartId;
                Assert.IsNotEmpty(id);

                Assert.IsFalse(profile.Identity.EnsureStablePartId(),
                    "An existing stable id must never be replaced.");
                Assert.AreEqual(id, profile.Identity.PartId);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static MeshSnapshot WithMetadata(
            MeshSnapshot source,
            string[] blendShapeNames,
            int[] blendShapeFrameCounts,
            IReadOnlyList<string> bonePaths,
            Matrix4x4[] boneWorldToLocalMatrices = null)
        {
            var uvs = MeshSnapshot.NewUvArray();
            for (var channel = 0; channel < uvs.Length; channel++)
            {
                if (source.HasUvChannel(channel)) uvs[channel] = Copy(source.GetUvChannel(channel));
            }

            var subMeshes = new int[source.SubMeshCount][];
            var topologies = new MeshTopology[source.SubMeshCount];
            for (var i = 0; i < source.SubMeshCount; i++)
            {
                subMeshes[i] = Copy(source.SubMeshIndices[i]);
                topologies[i] = source.TopologyList[i];
            }

            return MeshSnapshot.Create(
                source.Name,
                Copy(source.Vertices),
                source.HasNormals ? Copy(source.Normals) : null,
                source.HasTangents ? Copy(source.Tangents) : null,
                source.HasColors ? Copy(source.Colors) : null,
                uvs,
                subMeshes,
                topologies,
                source.Bounds,
                source.IndexFormat,
                boneWorldToLocalMatrices: boneWorldToLocalMatrices,
                blendShapeNames: blendShapeNames,
                blendShapeFrameCounts: blendShapeFrameCounts,
                bonePaths: bonePaths);
        }

        private static T[] Copy<T>(IReadOnlyList<T> source)
        {
            var result = new T[source.Count];
            for (var i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }
    }
}

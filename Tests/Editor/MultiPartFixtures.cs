using System;
using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Shared builders for the M6 multi-part, grouping, priority, and UV-declaration tests.
    /// </summary>
    /// <remarks>
    /// These exist because every M6 test needs to build a part snapshot that carries a serialized policy
    /// (slot mode, conflict priority, blend shape allowance). <see cref="MeshFixtures.PartSnapshot"/> predates
    /// the policy axes and is deliberately left untouched, so the M6 helpers add the policy argument instead of
    /// changing a shared fixture that older tests depend on.
    /// <para>
    /// The ordering key built here carries every component <see cref="PartOrderingKey"/> defines — priority,
    /// slot, part id, display name, installer path — so a test that sorts parts exercises the real total order
    /// rather than a projection of it.
    /// </para>
    /// </remarks>
    public static class MultiPartFixtures
    {
        /// <summary>Builds a part snapshot carrying an explicit policy.</summary>
        public static PartSnapshot Part(
            string partId,
            MeshSnapshot mesh,
            ApaSeamProfile seam = null,
            ApaPartSlot slot = ApaPartSlot.LeftArm,
            ApaPartSlotMode slotMode = ApaPartSlotMode.Replace,
            int priority = 0,
            bool allowPartOnlyShapes = true,
            IReadOnlyList<RemovedTriangleAddress> removed = null,
            IReadOnlyList<ApaUvChannelSemantic> uvSemantics = null,
            IReadOnlyList<ApaMaterialSlotSemantic> materialSemantics = null,
            IReadOnlyList<Material> materials = null,
            string displayName = null)
        {
            var name = displayName ?? partId;

            return new PartSnapshot(
                partId,
                name,
                slot,
                new PartOrderingKey(priority, slot, partId, name, partId),
                mesh,
                default,
                uvSemantics ?? Array.Empty<ApaUvChannelSemantic>(),
                materialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>(),
                removed ?? Array.Empty<RemovedTriangleAddress>(),
                seam,
                materials ?? Array.Empty<Material>(),
                new PartPolicySnapshot(slotMode, priority, allowPartOnlyShapes, string.Empty, string.Empty, false));
        }

        /// <summary>Builds a part policy value that is not the default, for policy-axis tests.</summary>
        /// <remarks>
        /// The armature selections are the M10 axis: the two paths a profile records for the target and part
        /// armatures, which are what the build reads to scope a bone identity. The legacy M6 merge-name fields are
        /// deliberately not exposed here — nothing consumes them any more, so a test that set them would be
        /// configuring a policy that has no effect.
        /// </remarks>
        public static PartPolicySnapshot Policy(
            ApaPartSlotMode slotMode = ApaPartSlotMode.Replace,
            int priority = 0,
            bool allowPartOnlyShapes = true,
            string targetArmaturePath = "",
            string partArmaturePath = "")
        {
            return new PartPolicySnapshot(
                slotMode,
                priority,
                allowPartOnlyShapes,
                string.Empty,
                string.Empty,
                false,
                targetArmaturePath,
                partArmaturePath);
        }

        /// <summary>
        /// Copies a snapshot and adds a present UV channel 1 with the supplied per-vertex values.
        /// </summary>
        /// <remarks>
        /// The values array must have exactly one entry per vertex: a channel whose length does not match the
        /// vertex buffer is treated as absent by <c>MeshSnapshot.HasUvChannel</c>, and a test that accidentally
        /// built an absent channel would assert nothing.
        /// </remarks>
        public static MeshSnapshot WithUv1(MeshSnapshot source, Vector4[] uv1)
        {
            if (uv1 == null || uv1.Length != source.VertexCount)
            {
                throw new ArgumentException("A present UV channel must have exactly one value per vertex.");
            }

            var uvs = MeshSnapshot.NewUvArray();
            for (var channel = 0; channel < uvs.Length; channel++)
            {
                if (source.HasUvChannel(channel)) uvs[channel] = Copy(source.GetUvChannel(channel));
            }

            uvs[1] = Copy(uv1);
            return CopyTo(source, uvs);
        }

        /// <summary>Builds a snapshot with an arbitrary set of present UV channels from one mesh.</summary>
        public static MeshSnapshot WithChannels(MeshSnapshot source, params Vector4[][] channels)
        {
            var uvs = MeshSnapshot.NewUvArray();
            for (var i = 0; i < channels.Length && i < uvs.Length; i++)
            {
                if (channels[i] == null) continue;
                if (channels[i].Length != source.VertexCount)
                {
                    throw new ArgumentException(
                        "UV channel " + i + " must have exactly one value per vertex to be present.");
                }

                uvs[i] = Copy(channels[i]);
            }

            return CopyTo(source, uvs);
        }

        /// <summary>Builds a flat UV channel with one identical value per vertex.</summary>
        public static Vector4[] FlatUv(int vertexCount, float x, float y)
        {
            var result = new Vector4[vertexCount];
            for (var i = 0; i < vertexCount; i++) result[i] = new Vector4(x, y, 0f, 0f);
            return result;
        }

        /// <summary>Builds a channel that varies with the vertex index, so two channels are distinguishable.</summary>
        public static Vector4[] RampUv(int vertexCount, float offset = 0f)
        {
            var result = new Vector4[vertexCount];
            for (var i = 0; i < vertexCount; i++)
            {
                var t = vertexCount <= 1 ? 0f : (float)i / (vertexCount - 1);
                result[i] = new Vector4(t + offset, t + offset, 0f, 0f);
            }

            return result;
        }

        /// <summary>
        /// Builds a ring-plus-apex mesh whose UV0 is present (one value per vertex) rather than absent.
        /// </summary>
        /// <remarks>
        /// The shared <c>MeshFixtures.Body</c>/<c>Part</c> builders pass a ring-sized UV array for a mesh with
        /// one extra apex vertex, so their channel 0 is <i>absent</i>. That is fine for layout tests but useless
        /// for the undeclared-channel rule, which is defined on present channels.
        /// </remarks>
        public static MeshSnapshot RingMeshWithUv0(string name, int ringCount, float apexZ, Vector4[] channel0 = null)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(ringCount, 1f)) { new Vector3(0f, 0f, apexZ) };
            var uvs = channel0 ?? RampUv(positions.Count);

            return MeshFixtures.Snapshot(
                name,
                positions.ToArray(),
                MeshFixtures.CapTriangles(ringCount, 0, ringCount),
                uvs);
        }

        /// <summary>Copies a snapshot while replacing its UV channel table.</summary>
        private static MeshSnapshot CopyTo(MeshSnapshot source, Vector4[][] uvs)
        {
            var subMeshes = new int[source.SubMeshCount][];
            var topologies = new MeshTopology[source.SubMeshCount];
            for (var i = 0; i < subMeshes.Length; i++)
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
                source.SkinWeights.Count > 0 ? Copy(source.SkinWeights) : null,
                source.SkinBindPoses.Count > 0 ? Copy(source.SkinBindPoses) : null,
                Copy(source.Shapes),
                Copy(source.ShapeFrameCounts),
                Copy(source.BoneSignature.Paths),
                null,
                source.BoneWorldToLocalMatrices.Count > 0 ? Copy(source.BoneWorldToLocalMatrices) : null);
        }

        /// <summary>Creates a material for reference-equality tests, to be destroyed by the caller's teardown.</summary>
        public static Material NewMaterial(string name)
        {
            return new Material(Shader.Find("Standard")) { name = name };
        }

        private static T[] Copy<T>(IReadOnlyList<T> source)
        {
            var result = new T[source.Count];
            for (var i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }
    }
}

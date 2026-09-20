using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Produces a deterministic content identity for a captured mesh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mesh asset GUID is an identity of the asset, not of the bytes imported into it: reimporting an FBX can
    /// keep the GUID while changing vertex order, UVs, skin weights, or blend-shape deltas. Profiles therefore
    /// store this content fingerprint in addition to the human-readable GUID and the smaller compatibility
    /// signature.
    /// </para>
    /// <para>
    /// The vocabulary deliberately follows the data the assembler reads. Bone <i>paths</i> and live bone
    /// transforms are not included: paths are checked by the separate bone signature, while live transforms are
    /// scene state and must not make a mesh profile stale whenever an avatar is posed or reparented.
    /// </para>
    /// </remarks>
    public static class ApaMeshFingerprint
    {
        /// <summary>Stable identifier of the serialized fingerprint vocabulary.</summary>
        public const string Algorithm = "apa-profile-mesh-fnv1a64-v1";

        /// <summary>
        /// Captures a readable Unity mesh and fingerprints the resulting snapshot. An unreadable or null mesh
        /// has no trustworthy content identity and returns an empty string.
        /// </summary>
        public static string OfMesh(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return string.Empty;

            var snapshot = MeshSnapshotFactory.Capture(mesh, null, out _);
            return OfSnapshot(snapshot);
        }

        /// <summary>Fingerprints an immutable mesh snapshot.</summary>
        public static string OfSnapshot(MeshSnapshot mesh)
        {
            if (mesh == null) return string.Empty;

            var builder = new Builder();
            builder.Add(Algorithm);
            builder.Add((int)mesh.IndexFormat);
            builder.Add(mesh.Bounds);
            builder.Add(mesh.VertexCount);

            builder.Add("vertices");
            AddVectors(builder, mesh.Vertices);
            builder.Add("normals");
            AddVectors(builder, mesh.Normals);
            builder.Add("tangents");
            AddVectors(builder, mesh.Tangents);
            builder.Add("colors");
            AddColors(builder, mesh.Colors);

            builder.Add("submeshes");
            builder.Add(mesh.SubMeshCount);
            for (var i = 0; i < mesh.SubMeshCount; i++)
            {
                builder.AddInts(mesh.SubMeshIndices[i]);
                builder.Add(i < mesh.TopologyList.Count ? (int)mesh.TopologyList[i] : -1);
            }

            builder.Add("uvs");
            builder.Add(mesh.UvChannelCapacity);
            for (var channel = 0; channel < mesh.UvChannelCapacity; channel++)
            {
                if (!mesh.HasUvChannel(channel))
                {
                    builder.Add("absent");
                    continue;
                }

                builder.Add("present");
                AddVectors(builder, mesh.GetUvChannel(channel));
            }

            builder.Add("skin-weights");
            builder.Add(mesh.SkinWeights.Count);
            for (var i = 0; i < mesh.SkinWeights.Count; i++)
            {
                var weight = mesh.SkinWeights[i];
                builder.Add(weight.boneIndex0);
                builder.Add(weight.weight0);
                builder.Add(weight.boneIndex1);
                builder.Add(weight.weight1);
                builder.Add(weight.boneIndex2);
                builder.Add(weight.weight2);
                builder.Add(weight.boneIndex3);
                builder.Add(weight.weight3);
            }

            builder.Add("skin-bind-poses");
            builder.Add(mesh.SkinBindPoses.Count);
            for (var i = 0; i < mesh.SkinBindPoses.Count; i++) builder.Add(mesh.SkinBindPoses[i]);

            builder.Add("blend-shapes");
            builder.Add(mesh.BlendShapeCount);
            for (var shape = 0; shape < mesh.BlendShapeCount; shape++)
            {
                builder.Add(mesh.Shapes[shape]);
                var frames = shape < mesh.BlendShapeFrames.Count ? mesh.BlendShapeFrames[shape] : null;
                builder.Add(frames == null ? -1 : frames.Count);
                if (frames == null) continue;

                for (var frame = 0; frame < frames.Count; frame++)
                {
                    var value = frames[frame];
                    if (value == null)
                    {
                        builder.Add("null-frame");
                        continue;
                    }

                    builder.Add(value.Weight);
                    AddVectors(builder, value.DeltaVertices);
                    AddVectors(builder, value.DeltaNormals);
                    AddVectors(builder, value.DeltaTangents);
                }
            }

            return Algorithm + ":" + builder.Value;
        }

        private static void AddVectors(Builder builder, IReadOnlyList<Vector3> values)
        {
            if (values == null)
            {
                builder.Add(-1);
                return;
            }

            builder.Add(values.Count);
            for (var i = 0; i < values.Count; i++) builder.Add(values[i]);
        }

        private static void AddVectors(Builder builder, IReadOnlyList<Vector4> values)
        {
            if (values == null)
            {
                builder.Add(-1);
                return;
            }

            builder.Add(values.Count);
            for (var i = 0; i < values.Count; i++) builder.Add(values[i]);
        }

        private static void AddColors(Builder builder, IReadOnlyList<Color> values)
        {
            if (values == null)
            {
                builder.Add(-1);
                return;
            }

            builder.Add(values.Count);
            for (var i = 0; i < values.Count; i++) builder.Add(values[i]);
        }

        /// <summary>A small, process-independent FNV-1a accumulator.</summary>
        private sealed class Builder
        {
            private const ulong OffsetBasis = 14695981039346656037UL;
            private const ulong Prime = 1099511628211UL;
            private ulong _hash = OffsetBasis;

            public string Value => _hash.ToString("x16", CultureInfo.InvariantCulture);

            public void Add(string value)
            {
                if (value == null)
                {
                    Add(-1);
                    return;
                }

                Add(value.Length);
                for (var i = 0; i < value.Length; i++) Mix(value[i]);
            }

            public void Add(int value) => Mix((uint)value);

            public void Add(float value) => Mix((ulong)BitConverter.DoubleToInt64Bits(value));

            public void Add(Vector3 value)
            {
                Add(value.x);
                Add(value.y);
                Add(value.z);
            }

            public void Add(Vector4 value)
            {
                Add(value.x);
                Add(value.y);
                Add(value.z);
                Add(value.w);
            }

            public void Add(Color value)
            {
                Add(value.r);
                Add(value.g);
                Add(value.b);
                Add(value.a);
            }

            public void Add(Bounds value)
            {
                Add(value.center);
                Add(value.size);
            }

            public void Add(Matrix4x4 value)
            {
                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 4; column++) Add(value[row, column]);
                }
            }

            public void AddInts(IReadOnlyList<int> values)
            {
                if (values == null)
                {
                    Add(-1);
                    return;
                }

                Add(values.Count);
                for (var i = 0; i < values.Count; i++) Add(values[i]);
            }

            private void Mix(uint value)
            {
                unchecked
                {
                    MixByte((byte)(value & 0xFF));
                    MixByte((byte)((value >> 8) & 0xFF));
                    MixByte((byte)((value >> 16) & 0xFF));
                    MixByte((byte)((value >> 24) & 0xFF));
                }
            }

            private void Mix(ulong value)
            {
                Mix((uint)(value & 0xFFFFFFFFUL));
                Mix((uint)(value >> 32));
            }

            private void Mix(char value) => Mix((uint)value);

            private void MixByte(byte value)
            {
                unchecked
                {
                    _hash ^= value;
                    _hash *= Prime;
                }
            }
        }
    }
}

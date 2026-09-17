using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// Deterministic 64-bit FNV-1a accumulator used to fingerprint preview inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preview mesh cache is keyed by the <i>content</i> of its inputs, so the hash has to be reproducible:
    /// the same input must produce the same string in this session, in another editor session, and after a
    /// domain reload. Three commonly tempting primitives are deliberately not used:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>System.HashCode</c> — its seed is not part of any contract, so equal inputs can hash differently in
    /// two processes.
    /// </description></item>
    /// <item><description>
    /// The default string hash — randomized per process on modern runtimes.
    /// </description></item>
    /// <item><description>
    /// The default object hash of a Unity object — identifies an instance, not its content; a reimported mesh
    /// keeps its instance id and a duplicated mesh gets a new one.
    /// </description></item>
    /// </list>
    /// <para>
    /// Floating point values are hashed through <see cref="BitConverter.DoubleToInt64Bits"/> rather than through
    /// <c>float.GetHashCode</c> so that the value-to-bits mapping is fixed by the framework and allocates
    /// nothing. Every <c>float</c> is exactly representable as a <c>double</c>, so no information is lost; a NaN
    /// payload collapses to one bit pattern, which is irrelevant because non-finite input is rejected by the
    /// core (<c>APA016</c>) before it can reach the output.
    /// </para>
    /// <para>
    /// Strings are length-prefixed so that <c>"ab" + "c"</c> and <c>"a" + "bc"</c> cannot collide. The builder is
    /// used only at capture time, never from a per-frame observation callback, so its cost is paid when the
    /// preview is rebuilt rather than every editor frame.
    /// </para>
    /// </remarks>
    public sealed class ApaFingerprintBuilder
    {
        /// <summary>FNV-1a 64-bit offset basis.</summary>
        public const ulong OffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64-bit prime.</summary>
        public const ulong Prime = 1099511628211UL;

        private ulong _hash;

        /// <summary>Creates an empty accumulator.</summary>
        public ApaFingerprintBuilder()
        {
            _hash = OffsetBasis;
        }

        /// <summary>The accumulated fingerprint, as a fixed-width lower-case hex string.</summary>
        public string Value => _hash.ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>Hashes a string, distinguishing null from empty and prefixing its length.</summary>
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

        /// <summary>Hashes a 32-bit integer.</summary>
        public void Add(int value)
        {
            Mix((uint)value);
        }

        /// <summary>Hashes a 64-bit integer.</summary>
        public void Add(long value)
        {
            Mix((ulong)value);
        }

        /// <summary>Hashes a boolean as a single bit.</summary>
        public void Add(bool value)
        {
            Mix(value ? 1U : 0U);
        }

        /// <summary>Hashes a float through its exact double representation.</summary>
        public void Add(float value)
        {
            Mix((ulong)BitConverter.DoubleToInt64Bits(value));
        }

        /// <summary>Hashes a matrix by its sixteen scalars, in row-major order.</summary>
        public void Add(Matrix4x4 value)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++) Add(value[row, column]);
            }
        }

        /// <summary>Hashes a vector.</summary>
        public void Add(Vector2 value)
        {
            Add(value.x);
            Add(value.y);
        }

        /// <summary>Hashes a vector.</summary>
        public void Add(Vector3 value)
        {
            Add(value.x);
            Add(value.y);
            Add(value.z);
        }

        /// <summary>Hashes a vector.</summary>
        public void Add(Vector4 value)
        {
            Add(value.x);
            Add(value.y);
            Add(value.z);
            Add(value.w);
        }

        /// <summary>Hashes a quaternion.</summary>
        public void Add(Quaternion value)
        {
            Add(value.x);
            Add(value.y);
            Add(value.z);
            Add(value.w);
        }

        /// <summary>Hashes a color.</summary>
        public void Add(Color value)
        {
            Add(value.r);
            Add(value.g);
            Add(value.b);
            Add(value.a);
        }

        /// <summary>Hashes bounds.</summary>
        public void Add(Bounds value)
        {
            Add(value.center);
            Add(value.size);
        }

        /// <summary>
        /// Hashes a sequence of floats, prefixing its length so that a shorter list cannot equal a longer one.
        /// </summary>
        public void AddFloats(IReadOnlyList<float> values)
        {
            if (values == null)
            {
                Add(-1);
                return;
            }

            Add(values.Count);
            for (var i = 0; i < values.Count; i++) Add(values[i]);
        }

        /// <summary>Hashes a sequence of integers, prefixing its length.</summary>
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

        /// <summary>Hashes a sequence of strings, prefixing its length.</summary>
        public void AddStrings(IReadOnlyList<string> values)
        {
            if (values == null)
            {
                Add(-1);
                return;
            }

            Add(values.Count);
            for (var i = 0; i < values.Count; i++) Add(values[i]);
        }

        /// <summary>
        /// Hashes the identity of a Unity object.
        /// </summary>
        /// <remarks>
        /// This is an instance identity, not a content identity, and is therefore used only for values that
        /// cannot be hashed by content (a material asset, whose pixels and shader properties do not change the
        /// assembled mesh) and only inside a single session. Content-bearing inputs are hashed by value instead.
        /// </remarks>
        public void AddObjectIdentity(UnityEngine.Object value)
        {
            Add(value == null ? 0 : value.GetInstanceID());
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

        private void Mix(ulong value)
        {
            Mix((uint)(value & 0xFFFFFFFFUL));
            Mix((uint)(value >> 32));
        }

        private void Mix(char value)
        {
            Mix((uint)value);
        }
    }

    /// <summary>
    /// Turns captured preview inputs into stable fingerprints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two fingerprints are produced from the same vocabulary:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="OfContext"/> — the content fingerprint of everything <c>ApaCore</c> reads. It is the cache key
    /// of <c>ApaPreviewMeshCache</c> and the identity of a <c>RenderGroup</c>, so two preview rebuilds over
    /// equivalent inputs reuse one generated mesh and one node instead of rebuilding.
    /// </description></item>
    /// <item><description>
    /// <see cref="OfNumericPolicy"/> — the tolerances, which change validation and seam matching without
    /// changing any mesh, so they must participate in the key but cannot be derived from it.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>What is deliberately not in the fingerprint.</b> Live blend shape <i>weights</i> are renderer state that
    /// the node reads and applies on every frame, so they change the picture without changing the mesh; including
    /// them would rebuild the assembly on every weight change. Skinning <i>weights and bind poses</i> are part of
    /// the captured mesh snapshot (<see cref="MeshSnapshot.SkinWeights"/>, <see cref="MeshSnapshot.SkinBindPoses"/>)
    /// and therefore are included.
    /// </para>
    /// <para>
    /// <b>Algorithm id.</b> <see cref="Algorithm"/> is hashed first, so a future change to this vocabulary cannot
    /// silently reuse a cache entry produced by the previous one.
    /// </para>
    /// </remarks>
    public static class ApaPreviewFingerprint
    {
        /// <summary>Identifier of the fingerprint vocabulary; hashed into every value.</summary>
        public const string Algorithm = "apa-preview-fnv1a64-v1";

        /// <summary>
        /// Fingerprint used for an input set that could not be captured, and therefore must never be cached or
        /// compared against a captured one.
        /// </summary>
        public const string UncachedMarker = "uncached";

        /// <summary>Fingerprint of the full captured input set a preview assembly would read.</summary>
        public static string OfContext(ValidationContext context)
        {
            var builder = new ApaFingerprintBuilder();
            builder.Add(Algorithm);

            if (context == null)
            {
                builder.Add(UncachedMarker);
                return builder.Value;
            }

            builder.Add("policy");
            AddPolicy(builder, context.NumericPolicy);

            builder.Add("expected-compatibility");
            AddCompatibility(builder, context.ExpectedCompatibility);

            builder.Add("base");
            if (context.Base == null)
            {
                builder.Add("no-base");
            }
            else
            {
                AddMesh(builder, context.Base.Mesh);
                builder.Add(context.Base.RendererPath);
                builder.Add(context.Base.RendererLocalToWorld);
                AddSpaceTransforms(builder, context.Base.Transforms);
                AddUvSemantics(builder, context.Base.ExpectedUvSemantics);
                AddMaterialSemantics(builder, context.Base.ExpectedMaterialSemantics);
                AddMaterials(builder, context.Base.RendererMaterials);
            }

            builder.Add("parts");
            builder.Add(context.Parts == null ? -1 : context.Parts.Count);
            if (context.Parts != null)
            {
                for (var i = 0; i < context.Parts.Count; i++) AddPart(builder, context.Parts[i]);
            }

            return builder.Value;
        }

        /// <summary>Fingerprint of a captured mesh, attribute by attribute.</summary>
        public static string OfMesh(MeshSnapshot mesh)
        {
            var builder = new ApaFingerprintBuilder();
            builder.Add(Algorithm);
            AddMesh(builder, mesh);
            return builder.Value;
        }

        /// <summary>Fingerprint of the numeric tolerances.</summary>
        public static string OfNumericPolicy(ApaNumericPolicy policy)
        {
            var builder = new ApaFingerprintBuilder();
            builder.Add(Algorithm);
            AddPolicy(builder, policy);
            return builder.Value;
        }

        private static void AddPolicy(ApaFingerprintBuilder builder, ApaNumericPolicy policy)
        {
            if (policy == null)
            {
                builder.Add("null-policy");
                return;
            }

            builder.Add(policy.PositionEpsilon);
            builder.Add(policy.UvEpsilon);
            builder.Add(policy.DegenerateAreaEpsilon);

            // The influence threshold is part of the policy since M10: it decides which bones enter the final
            // table and which weights survive the remap, so a change to it changes the assembled mesh and the
            // preview must be invalidated by it.
            builder.Add(policy.WeightEpsilon);
        }

        private static void AddCompatibility(ApaFingerprintBuilder builder, ApaAvatarCompatibilityProfile profile)
        {
            if (profile == null)
            {
                builder.Add("null-compatibility");
                return;
            }

            builder.Add(profile.IsCaptured);
            builder.Add(profile.MeshGuid);
            builder.Add(profile.MeshName);
            builder.Add(profile.RendererPath);
            builder.Add(profile.VertexCount);
            builder.AddInts(profile.SubMeshIndexCounts);
            builder.AddInts(profile.SubMeshTopologyValues);
            builder.AddStrings(profile.BlendShapeNames);
            builder.AddInts(profile.BlendShapeFrameCounts);
            builder.AddStrings(profile.BonePaths);
        }

        private static void AddPart(ApaFingerprintBuilder builder, PartSnapshot part)
        {
            if (part == null)
            {
                builder.Add("null-part");
                return;
            }

            builder.Add("part");
            builder.Add(part.PartId);
            builder.Add(part.DisplayName);
            builder.Add((int)part.Slot);

            builder.Add("ordering");
            builder.Add((int)part.OrderingKey.Slot);
            builder.Add(part.OrderingKey.PartId);
            builder.Add(part.OrderingKey.InstallerPath);

            AddMesh(builder, part.Mesh);
            AddSpaceTransforms(builder, part.Transforms);
            AddUvSemantics(builder, part.UvSemantics);
            AddMaterialSemantics(builder, part.MaterialSemantics);

            builder.Add("removal");
            if (part.RemovedTriangles == null)
            {
                builder.Add(-1);
            }
            else
            {
                builder.Add(part.RemovedTriangles.Count);
                for (var i = 0; i < part.RemovedTriangles.Count; i++)
                {
                    var address = part.RemovedTriangles[i];
                    builder.Add(address.SubMeshIndex);
                    builder.Add(address.TriangleIndexWithinSubMesh);
                }
            }

            builder.Add("seam");
            if (part.Seam == null)
            {
                builder.Add("no-seam");
            }
            else
            {
                // The pairing version decides whether the build reads the two lists as pairs or refuses them
                // (APA042), so two profiles with identical indices but different versions do not preview the same
                // result and must not share a node.
                builder.Add(part.Seam.PairingVersion);
                AddSeamSide(builder, part.Seam.Base);
                AddSeamSide(builder, part.Seam.Part);
            }

            AddMaterials(builder, part.RendererMaterials);
        }

        private static void AddSeamSide(ApaFingerprintBuilder builder, ApaSeamSide side)
        {
            if (side == null)
            {
                builder.Add("null-seam-side");
                return;
            }

            var indices = side.VertexIndices;
            builder.Add(indices == null ? -1 : indices.Length);
            if (indices == null) return;
            for (var i = 0; i < indices.Length; i++) builder.Add(indices[i]);
        }

        private static void AddMesh(ApaFingerprintBuilder builder, MeshSnapshot mesh)
        {
            if (mesh == null)
            {
                builder.Add("null-mesh");
                return;
            }

            builder.Add("mesh");
            builder.Add(mesh.Name);
            builder.Add(mesh.Bounds);
            builder.Add((int)mesh.IndexFormat);
            builder.Add(mesh.VertexCount);
            builder.Add(mesh.SubMeshCount);

            builder.Add("vertices");
            for (var i = 0; i < mesh.Vertices.Count; i++) builder.Add(mesh.Vertices[i]);

            builder.Add("normals");
            for (var i = 0; i < mesh.Normals.Count; i++) builder.Add(mesh.Normals[i]);

            builder.Add("tangents");
            for (var i = 0; i < mesh.Tangents.Count; i++) builder.Add(mesh.Tangents[i]);

            builder.Add("colors");
            for (var i = 0; i < mesh.Colors.Count; i++) builder.Add(mesh.Colors[i]);

            builder.Add("submeshes");
            for (var i = 0; i < mesh.SubMeshIndices.Count; i++)
            {
                builder.AddInts(mesh.SubMeshIndices[i]);

                // The topology list is produced alongside the index lists, but a defensive bounds check keeps a
                // malformed snapshot from turning a fingerprint computation into an exception.
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
                var values = mesh.GetUvChannel(channel);
                builder.Add(values.Count);
                for (var i = 0; i < values.Count; i++) builder.Add(values[i]);
            }

            builder.Add("skin-weights");
            builder.Add(mesh.SkinWeights.Count);
            for (var i = 0; i < mesh.SkinWeights.Count; i++)
            {
                var weight = mesh.SkinWeights[i];
                builder.Add(weight.boneIndex0);
                builder.Add(weight.boneIndex1);
                builder.Add(weight.boneIndex2);
                builder.Add(weight.boneIndex3);
                builder.Add(weight.weight0);
                builder.Add(weight.weight1);
                builder.Add(weight.weight2);
                builder.Add(weight.weight3);
            }

            builder.Add("skin-bind-poses");
            for (var i = 0; i < mesh.SkinBindPoses.Count; i++) builder.Add(mesh.SkinBindPoses[i]);

            builder.Add("bone-world-to-local");
            for (var i = 0; i < mesh.BoneWorldToLocalMatrices.Count; i++)
            {
                builder.Add(mesh.BoneWorldToLocalMatrices[i]);
            }

            builder.Add("bone-signature");
            AddBoneSignature(builder, mesh.BoneSignature);

            builder.Add("blend-shapes");
            builder.Add(mesh.BlendShapeCount);
            for (var shape = 0; shape < mesh.BlendShapeCount; shape++)
            {
                builder.Add(mesh.Shapes[shape]);
                var frames = mesh.BlendShapeFrames[shape];
                builder.Add(frames == null ? -1 : frames.Count);
                if (frames == null) continue;

                for (var frame = 0; frame < frames.Count; frame++)
                {
                    var frameSnapshot = frames[frame];
                    if (frameSnapshot == null)
                    {
                        builder.Add("null-frame");
                        continue;
                    }

                    builder.Add(frameSnapshot.Weight);
                    for (var i = 0; i < frameSnapshot.DeltaVertices.Count; i++)
                    {
                        builder.Add(frameSnapshot.DeltaVertices[i]);
                    }

                    for (var i = 0; i < frameSnapshot.DeltaNormals.Count; i++)
                    {
                        builder.Add(frameSnapshot.DeltaNormals[i]);
                    }

                    for (var i = 0; i < frameSnapshot.DeltaTangents.Count; i++)
                    {
                        builder.Add(frameSnapshot.DeltaTangents[i]);
                    }
                }
            }
        }

        private static void AddBoneSignature(ApaFingerprintBuilder builder, BoneSignature signature)
        {
            if (signature == null)
            {
                builder.Add("null-bone-signature");
                return;
            }

            builder.Add(signature.Count);
            for (var i = 0; i < signature.Count; i++) builder.Add(signature.PathAt(i));
        }

        private static void AddSpaceTransforms(ApaFingerprintBuilder builder, SpaceTransforms transforms)
        {
            builder.Add("space-transforms");
            builder.Add(transforms.IsValid);
            builder.Add(transforms.SourceToAvatarLocal());
            builder.Add(transforms.SourceToTargetLocal());
            builder.Add(transforms.SourceToTargetNormalMatrix());
        }

        private static void AddUvSemantics(ApaFingerprintBuilder builder, IReadOnlyList<ApaUvChannelSemantic> semantics)
        {
            builder.Add("uv-semantics");
            if (semantics == null)
            {
                builder.Add(-1);
                return;
            }

            builder.Add(semantics.Count);
            for (var i = 0; i < semantics.Count; i++)
            {
                var semantic = semantics[i];
                if (semantic == null)
                {
                    builder.Add("null-semantic");
                    continue;
                }

                builder.Add(semantic.Semantic);
                builder.Add(semantic.SourceChannel);
            }
        }

        private static void AddMaterialSemantics(
            ApaFingerprintBuilder builder,
            IReadOnlyList<ApaMaterialSlotSemantic> semantics)
        {
            builder.Add("material-semantics");
            if (semantics == null)
            {
                builder.Add(-1);
                return;
            }

            builder.Add(semantics.Count);
            for (var i = 0; i < semantics.Count; i++)
            {
                var semantic = semantics[i];
                if (semantic == null)
                {
                    builder.Add("null-semantic");
                    continue;
                }

                builder.Add(semantic.Semantic);
                builder.Add(semantic.SourceSubMesh);
                builder.Add((int)semantic.Policy);
                builder.AddObjectIdentity(semantic.Material);
            }
        }

        private static void AddMaterials(ApaFingerprintBuilder builder, IReadOnlyList<Material> materials)
        {
            builder.Add("materials");
            if (materials == null)
            {
                builder.Add(-1);
                return;
            }

            builder.Add(materials.Count);
            for (var i = 0; i < materials.Count; i++) builder.AddObjectIdentity(materials[i]);
        }
    }
}

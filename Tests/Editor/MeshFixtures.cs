using System;
using System.Collections.Generic;
using UnityEngine;
using AvatarPartAssembler.Editor;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Builds readable mesh and snapshot fixtures for the core tests, including the M2 bone and blend shape
    /// scenarios.
    /// </summary>
    /// <remarks>
    /// The fixtures exist so that a test reads as the scenario it describes — "a 32-vertex seam with one
    /// mismatched UV" — rather than as a wall of array literals. Every builder is deterministic: the same call
    /// produces the same data, which is what allows tests to assert on exact output ordering.
    /// </remarks>
    public static class MeshFixtures
    {
        /// <summary>
        /// Builds a mesh snapshot from positions with an optional triangle list and UV channel.
        /// </summary>
        public static MeshSnapshot Snapshot(
            string name,
            Vector3[] positions,
            int[] triangles = null,
            Vector4[] uv0 = null,
            Vector3[] normals = null,
            Vector4[] tangents = null,
            Vector4[] uv1 = null,
            UnityEngine.Rendering.IndexFormat indexFormat = UnityEngine.Rendering.IndexFormat.UInt16)
        {
            var uvs = MeshSnapshot.NewUvArray();
            if (uv0 != null) uvs[0] = uv0;
            if (uv1 != null) uvs[1] = uv1;

            var subMeshes = new[] { triangles ?? Array.Empty<int>() };
            var topologies = new[] { MeshTopology.Triangles };

            return MeshSnapshot.Create(
                name,
                positions,
                normals ?? Array.Empty<Vector3>(),
                tangents ?? Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                uvs,
                subMeshes,
                topologies,
                new Bounds(Vector3.zero, Vector3.one),
                indexFormat);
        }

        /// <summary>
        /// Builds a snapshot with explicit submeshes, used by material-layout tests.
        /// </summary>
        public static MeshSnapshot SnapshotWithSubMeshes(
            string name,
            Vector3[] positions,
            IReadOnlyList<int[]> subMeshes,
            Vector4[] uv0 = null)
        {
            var uvs = MeshSnapshot.NewUvArray();
            if (uv0 != null) uvs[0] = uv0;

            var topologyArray = new MeshTopology[subMeshes.Count];
            for (var i = 0; i < topologyArray.Length; i++) topologyArray[i] = MeshTopology.Triangles;

            var array = new int[subMeshes.Count][];
            for (var i = 0; i < subMeshes.Count; i++) array[i] = subMeshes[i];

            return MeshSnapshot.Create(
                name,
                positions,
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                uvs,
                array,
                topologyArray,
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16);
        }

        /// <summary>
        /// Builds a snapshot that carries skin weights, used by the unsupported-attribute tests.
        /// </summary>
        public static MeshSnapshot SkinnedSnapshot(string name, Vector3[] positions, int[] triangles)
        {
            var weights = new BoneWeight[positions.Length];
            var bindPoses = new[] { Matrix4x4.identity };

            var uvs = MeshSnapshot.NewUvArray();

            return MeshSnapshot.Create(
                name,
                positions,
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                uvs,
                new[] { triangles },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16,
                weights,
                bindPoses);
        }

        /// <summary>
        /// Builds a snapshot that carries a blend shape, used by the unsupported-attribute tests.
        /// </summary>
        public static MeshSnapshot BlendShapeSnapshot(string name, Vector3[] positions, int[] triangles)
        {
            var uvs = MeshSnapshot.NewUvArray();

            return MeshSnapshot.Create(
                name,
                positions,
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                uvs,
                new[] { triangles },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16,
                null,
                null,
                new[] { "Blink" },
                new[] { 1 });
        }

        /// <summary>
        /// Builds a square ring of <paramref name="count"/> vertices on the XY plane, centred at the origin,
        /// with the given radius.
        /// </summary>
        /// <remarks>
        /// A ring is the natural seam topology, and generating it makes seam cardinality obvious in a test:
        /// the count passed to the builder is the seam vertex count under test.
        /// </remarks>
        public static Vector3[] Ring(int count, float radius, float z = 0f)
        {
            var result = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var angle = (float)(2.0 * Math.PI * i / count);
                result[i] = new Vector3(
                    (float)Math.Cos(angle) * radius,
                    (float)Math.Sin(angle) * radius,
                    z);
            }

            return result;
        }

        /// <summary>
        /// Shades a ring in ascending UV order, so that UV values are a predictable function of the index.
        /// </summary>
        public static Vector4[] RingUvs(int count, float offset = 0f)
        {
            var result = new Vector4[count];
            for (var i = 0; i < count; i++)
            {
                var t = count <= 1 ? 0f : (float)i / (count - 1);
                result[i] = new Vector4(t + offset, t + offset, 0f, 0f);
            }

            return result;
        }

        /// <summary>
        /// Builds a fan of triangles that connects every ring vertex to a single apex, producing a closed cap.
        /// </summary>
        /// <param name="ringCount">Number of vertices in the ring.</param>
        /// <param name="ringOffset">Index of the ring's first vertex.</param>
        /// <param name="apexIndex">Index of the apex vertex.</param>
        public static int[] CapTriangles(int ringCount, int ringOffset, int apexIndex)
        {
            var triangles = new int[ringCount * 3];
            for (var i = 0; i < ringCount; i++)
            {
                var next = (i + 1) % ringCount;
                triangles[i * 3] = ringOffset + i;
                triangles[i * 3 + 1] = ringOffset + next;
                triangles[i * 3 + 2] = apexIndex;
            }

            return triangles;
        }

        /// <summary>
        /// Builds a body fixture: a ring of <paramref name="ringCount"/> vertices plus an apex, fully capped.
        /// </summary>
        /// <remarks>
        /// The UV array is padded to the mesh's full vertex count, with the apex receiving the same conventional
        /// value the part fixture uses. A channel whose length disagrees with the vertex count is not a channel as
        /// far as <see cref="MeshSnapshot.HasUvChannel"/> is concerned, so a shorter array would silently make the
        /// body report "no UVs" and every UV comparison in a test using this fixture would compare nothing.
        /// </remarks>
        public static MeshSnapshot Body(int ringCount, Vector4[] uv0 = null, string name = "Body")
        {
            var positions = new List<Vector3>();
            positions.AddRange(Ring(ringCount, 1f));
            positions.Add(new Vector3(0f, 0f, 1f));

            var apex = ringCount;
            var triangles = CapTriangles(ringCount, 0, apex);

            var ringUvs = uv0 ?? RingUvs(ringCount);
            var uvs = new Vector4[positions.Count];
            for (var i = 0; i < ringCount && i < ringUvs.Length; i++) uvs[i] = ringUvs[i];
            uvs[apex] = new Vector4(0.5f, 0.5f, 0f, 0f);

            return Snapshot(name, positions.ToArray(), triangles, uvs);
        }

        /// <summary>
        /// Builds a part fixture whose seam ring coincides with the body ring, plus an apex that extends the
        /// geometry.
        /// </summary>
        /// <param name="ringCount">Seam vertex count; must match the body's ring.</param>
        /// <param name="uv0">UVs for the ring vertices.</param>
        /// <param name="apexOffset">How far the part's apex extends along Z.</param>
        /// <param name="apexUv">UV for the apex vertex.</param>
        public static MeshSnapshot Part(
            int ringCount,
            Vector4[] uv0 = null,
            float apexOffset = -1f,
            Vector4? apexUv = null,
            string name = "Part")
        {
            var positions = new List<Vector3>();
            positions.AddRange(Ring(ringCount, 1f));
            positions.Add(new Vector3(0f, 0f, apexOffset));

            var apex = ringCount;
            var triangles = CapTriangles(ringCount, 0, apex);

            var uvs = new Vector4[positions.Count];
            var ringUvs = uv0 ?? RingUvs(ringCount);
            for (var i = 0; i < ringCount && i < ringUvs.Length; i++) uvs[i] = ringUvs[i];
            uvs[apex] = apexUv ?? new Vector4(0.5f, 0.5f, 0f, 0f);

            return Snapshot(name, positions.ToArray(), triangles, uvs);
        }

        /// <summary>
        /// Creates a seam profile that pairs the first <paramref name="count"/> vertices of both meshes with each
        /// other, in order.
        /// </summary>
        /// <remarks>
        /// The paired form is written through <see cref="ApaSeamProfile.SetPaired"/>, which is the only writer of
        /// the pairing version. A fixture that set the two lists directly would produce a legacy, unpaired seam
        /// that the build refuses (<c>APA042</c>) rather than the seam the test means to declare.
        /// </remarks>
        public static ApaSeamProfile Seam(int count)
        {
            var indices = new int[count];
            for (var i = 0; i < count; i++) indices[i] = i;

            var seam = new ApaSeamProfile();
            seam.SetPaired(indices, (int[])indices.Clone());
            return seam;
        }

        /// <summary>
        /// Creates an explicitly paired seam whose part-side list is deliberately not in ascending index order, so
        /// a test can prove the two lists are consumed as pairs rather than sorted or re-matched.
        /// </summary>
        public static ApaSeamProfile ScrambledSeam(int count)
        {
            var baseIndices = new int[count];
            var partIndices = new int[count];
            for (var i = 0; i < count; i++)
            {
                baseIndices[i] = i;
                partIndices[i] = count - 1 - i;
            }

            var seam = new ApaSeamProfile();
            seam.SetPaired(baseIndices, partIndices);
            return seam;
        }

        /// <summary>
        /// Creates a part snapshot with the given identity, mesh, and optional seam.
        /// </summary>
        /// <param name="removedTriangles">
        /// Removal addresses. Defaults to none. <see cref="Removed"/> covers the common test case where only
        /// the triangle indices within one submesh vary.
        /// </param>
        /// <param name="transforms">
        /// Captured space matrices. Defaults to the identity mapping, which is what a fixture whose part sits at
        /// the target's origin in the same basis needs. Tests that exercise a rotated or scaled part supply
        /// <see cref="Spaces"/> instead.
        /// </param>
        public static PartSnapshot PartSnapshot(
            string partId,
            MeshSnapshot mesh,
            ApaSeamProfile seam = null,
            IReadOnlyList<RemovedTriangleAddress> removedTriangles = null,
            ApaPartSlot slot = ApaPartSlot.LeftArm,
            IReadOnlyList<ApaUvChannelSemantic> uvSemantics = null,
            IReadOnlyList<ApaMaterialSlotSemantic> materialSemantics = null,
            IReadOnlyList<Material> materials = null,
            SpaceTransforms transforms = default)
        {
            return new PartSnapshot(
                partId,
                partId,
                slot,
                new PartOrderingKey(slot, partId, partId),
                mesh,
                transforms,
                uvSemantics ?? Array.Empty<ApaUvChannelSemantic>(),
                materialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>(),
                removedTriangles ?? Array.Empty<RemovedTriangleAddress>(),
                seam,
                materials ?? Array.Empty<Material>());
        }

        /// <summary>
        /// Builds captured space matrices from the two matrices a test wants to pin.
        /// </summary>
        /// <remarks>
        /// The normal matrix is derived exactly the way the capture path derives it, so a test cannot
        /// accidentally assert against a hand-built normal matrix that the pipeline would never produce. A
        /// degenerate matrix throws rather than returning a value full of NaN: a fixture that supplies one is a
        /// mistake in the test, not a case under test.
        /// </remarks>
        public static SpaceTransforms Spaces(Matrix4x4 sourceToAvatarLocal, Matrix4x4 sourceToTargetLocal)
        {
            if (!SpaceTransforms.TryCreate(sourceToAvatarLocal, sourceToTargetLocal, out var transforms, out var reason))
            {
                throw new ArgumentException("The fixture space transform is not usable: " + reason);
            }

            return transforms;
        }

        /// <summary>
        /// Builds captured space matrices for a part that sits at the avatar origin, so avatar-space and
        /// target-space are the same basis.
        /// </summary>
        public static SpaceTransforms Spaces(Matrix4x4 sourceToTargetLocal)
        {
            return Spaces(sourceToTargetLocal, sourceToTargetLocal);
        }

        /// <summary>
        /// Builds removal addresses for one submesh. The submesh is explicit because it is part of the address,
        /// and a test that omitted it would be testing the ambiguity the address type exists to remove.
        /// </summary>
        public static RemovedTriangleAddress[] Removed(int subMeshIndex, params int[] triangleIndices)
        {
            var result = new RemovedTriangleAddress[triangleIndices.Length];
            for (var i = 0; i < triangleIndices.Length; i++)
            {
                result[i] = new RemovedTriangleAddress(subMeshIndex, triangleIndices[i]);
            }

            return result;
        }

        /// <summary>
        /// Builds removal addresses spanning several submeshes, from (submesh, triangle) pairs.
        /// </summary>
        public static RemovedTriangleAddress[] RemovedAcross(params int[] subMeshAndTrianglePairs)
        {
            var result = new RemovedTriangleAddress[subMeshAndTrianglePairs.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new RemovedTriangleAddress(
                    subMeshAndTrianglePairs[i * 2],
                    subMeshAndTrianglePairs[i * 2 + 1]);
            }

            return result;
        }

        /// <summary>
        /// Creates a base snapshot around a mesh.
        /// </summary>
        public static BaseSnapshot BaseSnapshot(
            MeshSnapshot mesh,
            IReadOnlyList<Material> materials = null,
            IReadOnlyList<ApaUvChannelSemantic> uvSemantics = null,
            IReadOnlyList<ApaMaterialSlotSemantic> materialSemantics = null,
            Matrix4x4 rendererLocalToWorld = default)
        {
            return new BaseSnapshot(
                mesh,
                default,
                materials ?? Array.Empty<Material>(),
                "Body",
                uvSemantics ?? Array.Empty<ApaUvChannelSemantic>(),
                materialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>(),
                rendererLocalToWorld);
        }

        /// <summary>
        /// Builds a compatibility signature matching a mesh, so that compatibility tests start from a valid
        /// baseline and only the field under test is varied.
        /// </summary>
        /// <remarks>
        /// Every safety field the compatibility rule compares is populated, because a helper that left one out
        /// would make every test using it fail the completeness check instead of testing what it names.
        /// </remarks>
        public static ApaAvatarCompatibilityProfile SignatureFor(
            MeshSnapshot mesh,
            string rendererPath = "Body",
            string meshGuid = "")
        {
            var indexCounts = new int[mesh.SubMeshCount];
            var topologies = new int[mesh.SubMeshCount];
            for (var i = 0; i < mesh.SubMeshCount; i++)
            {
                indexCounts[i] = mesh.SubMeshIndices[i].Count;
                topologies[i] = (int)(i < mesh.TopologyList.Count ? mesh.TopologyList[i] : MeshTopology.Triangles);
            }

            var names = new string[mesh.Shapes.Count];
            var frames = new int[mesh.ShapeFrameCounts.Count];
            for (var i = 0; i < names.Length; i++) names[i] = mesh.Shapes[i];
            for (var i = 0; i < frames.Length; i++) frames[i] = mesh.ShapeFrameCounts[i];

            return new ApaAvatarCompatibilityProfile
            {
                IsCaptured = true,
                MeshName = mesh.Name,
                RendererPath = rendererPath,
                MeshGuid = meshGuid,
                VertexCount = mesh.VertexCount,
                SubMeshIndexCounts = indexCounts,
                SubMeshTopologyValues = topologies,
                BlendShapeNames = names,
                BlendShapeFrameCounts = frames,
                BonePaths = ToArray(mesh.BoneSignature.Paths)
            };
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            var result = new string[values.Count];
            for (var i = 0; i < values.Count; i++) result[i] = values[i];
            return result;
        }

        /// <summary>
        /// Builds a validation context from a base mesh and parts, with a matching signature by default.
        /// </summary>
        public static ValidationContext Context(
            MeshSnapshot baseMesh,
            IReadOnlyList<PartSnapshot> parts,
            ApaNumericPolicy policy = null,
            ApaAvatarCompatibilityProfile signature = null,
            IReadOnlyList<Material> baseMaterials = null,
            IReadOnlyList<ApaUvChannelSemantic> baseUvSemantics = null,
            IReadOnlyList<ApaMaterialSlotSemantic> baseMaterialSemantics = null,
            Matrix4x4 rendererLocalToWorld = default)
        {
            return new ValidationContext(
                BaseSnapshot(baseMesh, baseMaterials, baseUvSemantics, baseMaterialSemantics, rendererLocalToWorld),
                ValidationContext.SortParts(parts),
                policy ?? ApaNumericPolicy.Default,
                signature ?? SignatureFor(baseMesh));
        }

        // ---- M2 fixtures: bones ----------------------------------------------------------------------

        /// <summary>
        /// The <c>worldToLocalMatrix</c> of a bone sitting at a world position with no rotation or scale.
        /// </summary>
        /// <remarks>
        /// Rotation and scale are deliberately out of scope here: a bind-pose bug is almost always a wrong
        /// translation or a matrix multiplied the wrong way round, and a translation-only fixture makes both
        /// readable in a failure message.
        /// </remarks>
        public static Matrix4x4 BoneAt(Vector3 worldPosition)
        {
            return Matrix4x4.Translate(-worldPosition);
        }

        /// <summary>Builds the world-to-local matrices of bones at the given world positions.</summary>
        public static Matrix4x4[] BonesAt(params Vector3[] worldPositions)
        {
            var result = new Matrix4x4[worldPositions.Length];
            for (var i = 0; i < result.Length; i++) result[i] = BoneAt(worldPositions[i]);
            return result;
        }

        /// <summary>Every vertex rigidly weighted to one bone.</summary>
        public static BoneWeight[] UniformWeights(int vertexCount, int boneIndex)
        {
            var result = new BoneWeight[vertexCount];
            for (var i = 0; i < vertexCount; i++)
            {
                result[i] = new BoneWeight { boneIndex0 = boneIndex, weight0 = 1f };
            }

            return result;
        }

        /// <summary>
        /// Every vertex rigidly weighted to <paramref name="defaultBone"/>, except one vertex weighted to a
        /// different bone. Used by the weld-ownership tests, where the whole point is that two vertices that end
        /// up as one output vertex carry different weights.
        /// </summary>
        public static BoneWeight[] WeightsWithOverride(
            int vertexCount,
            int defaultBone,
            int overrideVertex,
            int overrideBone)
        {
            var result = UniformWeights(vertexCount, defaultBone);
            if (overrideVertex >= 0 && overrideVertex < result.Length)
            {
                result[overrideVertex] = new BoneWeight { boneIndex0 = overrideBone, weight0 = 1f };
            }

            return result;
        }

        /// <summary>Two influences on one vertex, for a blend-weight case.</summary>
        public static BoneWeight BlendWeight(int boneA, float weightA, int boneB, float weightB)
        {
            return new BoneWeight
            {
                boneIndex0 = boneA,
                weight0 = weightA,
                boneIndex1 = boneB,
                weight1 = weightB
            };
        }

        /// <summary>
        /// Builds a snapshot that carries skin weights, bone identities, and each bone's world-to-local matrix.
        /// </summary>
        public static MeshSnapshot SkinnedSnapshot(
            string name,
            Vector3[] positions,
            int[] triangles,
            string[] bonePaths,
            BoneWeight[] weights,
            Matrix4x4[] boneWorldToLocal,
            Matrix4x4[] bindPoses = null,
            string[] blendShapeNames = null,
            int[] blendShapeFrameCounts = null,
            BlendShapeFrameSnapshot[][] blendShapeFrames = null)
        {
            return MeshSnapshot.Create(
                name,
                positions,
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { triangles },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16,
                weights,
                bindPoses,
                blendShapeNames,
                blendShapeFrameCounts,
                bonePaths,
                blendShapeFrames,
                boneWorldToLocal);
        }

        /// <summary>Builds a skinned body whose <paramref name="ringCount"/> ring vertices use the first bone and
        /// whose apex uses the second.</summary>
        public static MeshSnapshot SkinnedBody(int ringCount = 4, string[] bonePaths = null, int[] triangles = null)
        {
            var positions = new List<Vector3>();
            positions.AddRange(Ring(ringCount, 1f));
            positions.Add(new Vector3(0f, 0f, 1f));

            var apex = ringCount;
            var indices = triangles ?? CapTriangles(ringCount, 0, apex);

            var weights = UniformWeights(positions.Count, 0);
            weights[apex] = new BoneWeight { boneIndex0 = 1, weight0 = 0.5f, boneIndex1 = 0, weight1 = 0.5f };

            return SkinnedSnapshot(
                "Body",
                positions.ToArray(),
                indices,
                bonePaths ?? new[] { "Armature/Hips", "Armature/Spine" },
                weights,
                BonesAt(new Vector3(0f, 1f, 0f), new Vector3(0f, 2f, 0f)));
        }

        // ---- M2 fixtures: blend shapes ---------------------------------------------------------------

        /// <summary>A blend shape frame that moves one vertex, leaving every other vertex untouched.</summary>
        public static BlendShapeFrameSnapshot Frame(
            float weight,
            int vertexCount,
            int movedVertex,
            Vector3 positionDelta,
            Vector3 normalDelta = default,
            Vector3 tangentDelta = default)
        {
            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector3[vertexCount];
            if (movedVertex >= 0 && movedVertex < vertexCount)
            {
                positions[movedVertex] = positionDelta;
                normals[movedVertex] = normalDelta;
                tangents[movedVertex] = tangentDelta;
            }

            return new BlendShapeFrameSnapshot(weight, positions, normals, tangents);
        }

        /// <summary>
        /// A frame whose deltas are given per vertex, so a whole seam can be shaped at once.
        /// </summary>
        /// <remarks>
        /// Normal and tangent deltas default to zero. A test that exercises a transform which treats the two
        /// differently (a non-uniform scale) supplies them, because a zero vector is transformed identically by
        /// every operator and would make the assertion vacuous.
        /// </remarks>
        public static BlendShapeFrameSnapshot FrameFromDeltas(
            float weight,
            Vector3[] positionDeltas,
            Vector3[] normalDeltas = null,
            Vector3[] tangentDeltas = null)
        {
            var count = positionDeltas?.Length ?? 0;
            return new BlendShapeFrameSnapshot(
                weight,
                positionDeltas ?? Array.Empty<Vector3>(),
                normalDeltas ?? new Vector3[count],
                tangentDeltas ?? new Vector3[count]);
        }

        /// <summary>Builds a snapshot with blend shapes and no skinning.</summary>
        public static MeshSnapshot BlendShapedSnapshot(
            string name,
            Vector3[] positions,
            int[] triangles,
            string[] shapeNames,
            BlendShapeFrameSnapshot[][] frames,
            Vector4[] uv0 = null)
        {
            var uvs = MeshSnapshot.NewUvArray();
            if (uv0 != null) uvs[0] = uv0;

            var counts = new int[shapeNames.Length];
            for (var i = 0; i < counts.Length; i++)
            {
                counts[i] = frames != null && i < frames.Length && frames[i] != null ? frames[i].Length : 0;
            }

            return MeshSnapshot.Create(
                name,
                positions,
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                uvs,
                new[] { triangles },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                UnityEngine.Rendering.IndexFormat.UInt16,
                null,
                null,
                shapeNames,
                counts,
                null,
                frames);
        }

        /// <summary>
        /// The position delta stored in one frame of one shape, or <see cref="Vector3.zero"/> when absent.
        /// Reads through the same accessor the pipeline uses, so a test asserts what the assembler sees.
        /// </summary>
        public static Vector3 ShapeDelta(
            MeshSnapshot mesh,
            string shapeName,
            int frame,
            int vertex,
            BlendShapeDeltaKind kind = BlendShapeDeltaKind.Position)
        {
            for (var shape = 0; shape < mesh.Shapes.Count; shape++)
            {
                if (!string.Equals(mesh.Shapes[shape], shapeName, StringComparison.Ordinal)) continue;
                return BlendShapeDeltas.Delta(mesh, shape, frame, vertex, kind);
            }

            return Vector3.zero;
        }

        /// <summary>Finds the output shape index by name, or -1 when the generated mesh has no such shape.</summary>
        public static int OutputShapeIndex(Mesh mesh, string shapeName)
        {
            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                if (string.Equals(mesh.GetBlendShapeName(i), shapeName, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        /// <summary>
        /// Reads one frame's position deltas out of a generated mesh, so a test can assert the full remapped
        /// array rather than a single vertex.
        /// </summary>
        public static Vector3[] ReadFramePositionDeltas(Mesh mesh, int shapeIndex, int frameIndex)
        {
            var vertices = new Vector3[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, vertices, normals, tangents);
            return vertices;
        }

        /// <summary>Reads one frame's normal deltas out of a generated mesh.</summary>
        public static Vector3[] ReadFrameNormalDeltas(Mesh mesh, int shapeIndex, int frameIndex)
        {
            var vertices = new Vector3[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, vertices, normals, tangents);
            return normals;
        }

        /// <summary>Reads one frame's tangent deltas out of a generated mesh.</summary>
        public static Vector3[] ReadFrameTangentDeltas(Mesh mesh, int shapeIndex, int frameIndex)
        {
            var vertices = new Vector3[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, vertices, normals, tangents);
            return tangents;
        }
    }
}

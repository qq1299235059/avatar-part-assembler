using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// One blend shape frame: its weight and the per-vertex position, normal, and tangent deltas.
    /// </summary>
    /// <remarks>
    /// A frame is addressed by (shape index, frame index). The frame index is a position in an array rather
    /// than an identity, which is why M2 requires same-named base and part shapes to agree on frame count and
    /// frame weights before it will merge them (section 43.8).
    /// </remarks>
    public sealed class BlendShapeFrameSnapshot
    {
        /// <summary>Frame weight, as authored.</summary>
        public float Weight { get; }

        /// <summary>Per-vertex position deltas.</summary>
        public IReadOnlyList<Vector3> DeltaVertices { get; }

        /// <summary>Per-vertex normal deltas.</summary>
        public IReadOnlyList<Vector3> DeltaNormals { get; }

        /// <summary>Per-vertex tangent deltas.</summary>
        public IReadOnlyList<Vector3> DeltaTangents { get; }

        /// <summary>
        /// Number of vertices the frame's delta arrays address, or -1 when the three arrays disagree.
        /// </summary>
        /// <remarks>
        /// A frame whose arrays disagree cannot be indexed by vertex at all, so callers must treat -1 as
        /// "unusable" rather than clamping to the shortest array.
        /// </remarks>
        public int VertexCount
        {
            get
            {
                var count = DeltaVertices.Count;
                if (DeltaNormals.Count != count) return -1;
                if (DeltaTangents.Count != count) return -1;
                return count;
            }
        }

        /// <summary>Creates a frame snapshot.</summary>
        public BlendShapeFrameSnapshot(float weight, Vector3[] vertices, Vector3[] normals, Vector3[] tangents)
        {
            Weight = weight;
            DeltaVertices = Array.AsReadOnly(vertices ?? Array.Empty<Vector3>());
            DeltaNormals = Array.AsReadOnly(normals ?? Array.Empty<Vector3>());
            DeltaTangents = Array.AsReadOnly(tangents ?? Array.Empty<Vector3>());
        }
    }

    /// <summary>
    /// An immutable, plain-managed copy of everything the assembler needs from a mesh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Snapshots exist for two reasons. First, they are the non-mutation guarantee: the assembler reads the
    /// authoring mesh once and then works entirely on its own copy, so no code path can accidentally write
    /// back to an asset. Second, they make the core testable without the Editor, because a snapshot can be
    /// built from plain arrays.
    /// </para>
    /// <para>
    /// <b>Immutability is enforced, not asserted.</b> Callers outside this assembly can only read: the arrays
    /// are private and every accessor is either an <see cref="IReadOnlyList{T}"/> or a copy. Index-buffer and
    /// per-vertex access inside the pipeline therefore does not pay for a defensive copy, while a caller that
    /// wants to keep a validated context from being altered between validation and build simply cannot.
    /// </para>
    /// <para>
    /// The one exception is the constructor, which takes ownership of the arrays the factory passes in. That is
    /// deliberate and is why construction is <c>internal</c>: a public constructor that adopted caller-owned
    /// arrays would let the caller keep writing to them, which is exactly the hole this design closes. Tests
    /// and external callers use <see cref="Create"/>, which copies.
    /// </para>
    /// </remarks>
    public sealed class MeshSnapshot
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
        private readonly string[] _blendShapeNames;
        private readonly int[] _blendShapeFrameCounts;
        private readonly BlendShapeFrameSnapshot[][] _blendShapeFrames;
        private readonly Matrix4x4[] _boneWorldToLocalMatrices;
        private readonly ReadOnlyCollection<Vector3> _verticesView;
        private readonly ReadOnlyCollection<Vector3> _normalsView;
        private readonly ReadOnlyCollection<Vector4> _tangentsView;
        private readonly ReadOnlyCollection<Color> _colorsView;
        private readonly ReadOnlyCollection<IReadOnlyList<Vector4>> _uvViews;
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _subMeshViews;
        private readonly ReadOnlyCollection<MeshTopology> _topologyView;
        private readonly ReadOnlyCollection<BoneWeight> _boneWeightView;
        private readonly ReadOnlyCollection<Matrix4x4> _bindPoseView;
        private readonly ReadOnlyCollection<string> _blendShapeNameView;
        private readonly ReadOnlyCollection<int> _blendShapeFrameCountView;
        private readonly ReadOnlyCollection<IReadOnlyList<BlendShapeFrameSnapshot>> _blendShapeFrameView;
        private readonly ReadOnlyCollection<Matrix4x4> _boneWorldToLocalView;
        private string _contentFingerprint;

        /// <summary>Object name, for diagnostics only. Never used for matching.</summary>
        public string Name { get; }

        /// <summary>Local bounds of the source mesh.</summary>
        public Bounds Bounds { get; }

        /// <summary>Index format of the source mesh, preserved into the output mesh.</summary>
        public IndexFormat IndexFormat { get; }

        /// <summary>Vertex positions in the mesh's own local space.</summary>
        public IReadOnlyList<Vector3> Vertices => _verticesView;

        /// <summary>Vertex normals, or an empty list when the source mesh has none.</summary>
        public IReadOnlyList<Vector3> Normals => _normalsView;

        /// <summary>Vertex tangents, or an empty list when the source mesh has none.</summary>
        public IReadOnlyList<Vector4> Tangents => _tangentsView;

        /// <summary>Vertex colors, or an empty list when the source mesh has none.</summary>
        public IReadOnlyList<Color> Colors => _colorsView;

        /// <summary>Submesh index buffers, in submesh order. Each has a length divisible by three.</summary>
        public IReadOnlyList<IReadOnlyList<int>> SubMeshIndices => _subMeshViews;

        /// <summary>Topology of each submesh, in submesh order.</summary>
        public IReadOnlyList<MeshTopology> TopologyList => _topologyView;

        /// <summary>
        /// Bone weights, one per vertex, or an empty list when the mesh is not skinned.
        /// </summary>
        /// <remarks>
        /// Unity's <c>Mesh.boneWeights</c> exposes at most four influences per vertex. A source authored with
        /// more is read through Unity's own accessor, so the snapshot carries exactly what Unity reports rather
        /// than a re-derived approximation.
        /// </remarks>
        public IReadOnlyList<BoneWeight> SkinWeights => _boneWeightView;

        /// <summary>
        /// The source mesh's own bind poses, one per bone, or an empty list when the source has none.
        /// </summary>
        /// <remarks>
        /// These are the authored rest-pose relation for the source renderer. The final table converts them into
        /// the target renderer's local basis, so a live edit to a bone cannot silently become a new bind pose.
        /// A legacy hand-built snapshot may omit them; the assembly core then retains its compatibility fallback
        /// based on the captured live transform.
        /// </remarks>
        public IReadOnlyList<Matrix4x4> SkinBindPoses => _bindPoseView;

        /// <summary>Blend shape names, in mesh order.</summary>
        public IReadOnlyList<string> Shapes => _blendShapeNameView;

        /// <summary>Frame counts per blend shape, in mesh order.</summary>
        public IReadOnlyList<int> ShapeFrameCounts => _blendShapeFrameCountView;

        /// <summary>
        /// Blend shape frames, indexed by shape order and then frame order. Each frame carries its weight and
        /// its position, normal, and tangent delta arrays.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<BlendShapeFrameSnapshot>> BlendShapeFrames => _blendShapeFrameView;

        /// <summary>
        /// Each bone's <c>worldToLocalMatrix</c> in bone order, or an empty list when the source renderer is not
        /// skinned. Index-aligned with <see cref="BoneSignature"/>.
        /// </summary>
        /// <remarks>
        /// This is the bone's actual live transform, not a bind pose. It is kept separate from the authored
        /// <see cref="SkinBindPoses"/> so a current pose cannot overwrite the source rest-pose relation; it is
        /// also used by the legacy fallback and transform validation.
        /// </remarks>
        public IReadOnlyList<Matrix4x4> BoneWorldToLocalMatrices => _boneWorldToLocalView;

        /// <summary>Number of UV channels the snapshot can hold.</summary>
        public int UvChannelCapacity => _uvs.Length;

        /// <summary>
        /// UV channels 0 through 7 by index. An entry is either empty (channel absent) or has one value per
        /// vertex.
        /// </summary>
        /// <remarks>
        /// Indexed rather than enumerated because a channel is addressed by its Unity channel number, which is
        /// what the UV layout maps semantics onto. The cached wrappers prevent callers from mutating either the
        /// channel table or any channel buffer while keeping reads allocation-free.
        /// </remarks>
        public IReadOnlyList<IReadOnlyList<Vector4>> UvChannels => _uvViews;

        /// <summary>
        /// Index buffers in submesh order, for the pipeline's indexed reads.
        /// </summary>
        /// <remarks>Internal only; the public view is recursively read-only.</remarks>
        internal int[][] SubMeshes => _subMeshes;

        /// <summary>
        /// Submesh topologies by index, for the pipeline's indexed reads.
        /// </summary>
        internal MeshTopology[] Topologies => _topologies;

        /// <summary>Raw UV buffers for allocation-free processing inside this assembly.</summary>
        internal Vector4[][] Uvs => _uvs;

        /// <summary>
        /// Bone paths in bone order, relative to the armature selected for the owning renderer, or an empty
        /// signature for a mesh without bones.
        /// </summary>
        /// <remarks>
        /// Recorded because the compatibility signature needs it (section 43.4). It is captured here rather
        /// than read later from the renderer so that everything the signature compares was read from the same
        /// object graph at the same moment, which is what makes the signature reproducible.
        /// </remarks>
        public BoneSignature BoneSignature { get; }

        /// <summary>
        /// Deterministic content identity of the captured mesh. The value is cached after the first request so a
        /// compatibility pass per installer does not hash the same blend-shape buffers repeatedly.
        /// </summary>
        /// <remarks>
        /// This is a derived cache, not author data: it is intentionally excluded from the snapshot's
        /// constructor and cannot affect any assembly decision other than the explicit profile compatibility
        /// check.
        /// </remarks>
        public string ContentFingerprint =>
            _contentFingerprint ?? (_contentFingerprint = ApaMeshFingerprint.OfSnapshot(this));

        /// <summary>Number of vertices.</summary>
        public int VertexCount => _vertices.Length;

        /// <summary>Number of submeshes.</summary>
        public int SubMeshCount => _subMeshes.Length;

        /// <summary>True when the mesh carries normals.</summary>
        public bool HasNormals => _normals.Length == _vertices.Length && _vertices.Length > 0;

        /// <summary>True when the mesh carries tangents.</summary>
        public bool HasTangents => _tangents.Length == _vertices.Length && _vertices.Length > 0;

        /// <summary>True when the mesh carries colors.</summary>
        public bool HasColors => _colors.Length == _vertices.Length && _vertices.Length > 0;

        /// <summary>Number of blend shapes.</summary>
        public int BlendShapeCount => _blendShapeNames.Length;

        /// <summary>
        /// Constructs a snapshot that takes ownership of the supplied arrays.
        /// </summary>
        /// <remarks>
        /// Internal because ownership transfer is only safe from the factory, which creates the arrays itself.
        /// A public overload that adopted caller arrays would leave the caller holding a writable reference to
        /// data the snapshot calls immutable.
        /// </remarks>
        internal MeshSnapshot(
            string name,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Color[] colors,
            Vector4[][] uvs,
            int[][] subMeshes,
            MeshTopology[] topologies,
            Bounds bounds,
            IndexFormat indexFormat,
            BoneWeight[] boneWeights,
            Matrix4x4[] bindPoses,
            string[] blendShapeNames,
            int[] blendShapeFrameCounts,
            BoneSignature boneSignature = null,
            BlendShapeFrameSnapshot[][] blendShapeFrames = null,
            Matrix4x4[] boneWorldToLocalMatrices = null)
        {
            Name = name ?? string.Empty;
            _vertices = vertices ?? Array.Empty<Vector3>();
            _normals = normals ?? Array.Empty<Vector3>();
            _tangents = tangents ?? Array.Empty<Vector4>();
            _colors = colors ?? Array.Empty<Color>();
            _uvs = uvs ?? NewUvArray();
            _subMeshes = subMeshes ?? Array.Empty<int[]>();
            _topologies = topologies ?? Array.Empty<MeshTopology>();
            Bounds = bounds;
            IndexFormat = indexFormat;
            _boneWeights = boneWeights ?? Array.Empty<BoneWeight>();
            _bindPoses = bindPoses ?? Array.Empty<Matrix4x4>();
            _blendShapeNames = blendShapeNames ?? Array.Empty<string>();
            _blendShapeFrameCounts = blendShapeFrameCounts ?? Array.Empty<int>();
            _blendShapeFrames = blendShapeFrames ?? Array.Empty<BlendShapeFrameSnapshot[]>();
            _boneWorldToLocalMatrices = boneWorldToLocalMatrices ?? Array.Empty<Matrix4x4>();
            BoneSignature = boneSignature ?? BoneSignature.Empty;

            _verticesView = Array.AsReadOnly(_vertices);
            _normalsView = Array.AsReadOnly(_normals);
            _tangentsView = Array.AsReadOnly(_tangents);
            _colorsView = Array.AsReadOnly(_colors);
            _topologyView = Array.AsReadOnly(_topologies);
            _boneWeightView = Array.AsReadOnly(_boneWeights);
            _bindPoseView = Array.AsReadOnly(_bindPoses);
            _blendShapeNameView = Array.AsReadOnly(_blendShapeNames);
            _blendShapeFrameCountView = Array.AsReadOnly(_blendShapeFrameCounts);
            var shapeViews = new IReadOnlyList<BlendShapeFrameSnapshot>[_blendShapeFrames.Length];
            for (var i = 0; i < shapeViews.Length; i++)
                shapeViews[i] = Array.AsReadOnly(_blendShapeFrames[i] ?? Array.Empty<BlendShapeFrameSnapshot>());
            _blendShapeFrameView = Array.AsReadOnly(shapeViews);
            _boneWorldToLocalView = Array.AsReadOnly(_boneWorldToLocalMatrices);

            var uvViews = new IReadOnlyList<Vector4>[_uvs.Length];
            for (var i = 0; i < _uvs.Length; i++)
            {
                uvViews[i] = Array.AsReadOnly(_uvs[i] ?? Array.Empty<Vector4>());
            }
            _uvViews = Array.AsReadOnly(uvViews);

            var subMeshViews = new IReadOnlyList<int>[_subMeshes.Length];
            for (var i = 0; i < _subMeshes.Length; i++)
            {
                subMeshViews[i] = Array.AsReadOnly(_subMeshes[i] ?? Array.Empty<int>());
            }
            _subMeshViews = Array.AsReadOnly(subMeshViews);
        }

        /// <summary>
        /// Creates a snapshot that copies every array it is given, so the caller keeps ownership of its own
        /// data and cannot alter the snapshot afterwards.
        /// </summary>
        /// <remarks>
        /// This is the public construction path. It copies because the caller's arrays remain the caller's: the
        /// snapshot's immutability claim would be false if it aliased them. The pipeline uses the internal
        /// ownership-taking constructor instead, because it creates the arrays itself and copying them again
        /// would double the peak allocation of every capture for no benefit.
        /// </remarks>
        public static MeshSnapshot Create(
            string name,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Color[] colors,
            Vector4[][] uvs,
            int[][] subMeshes,
            MeshTopology[] topologies,
            Bounds bounds,
            IndexFormat indexFormat,
            BoneWeight[] boneWeights = null,
            Matrix4x4[] bindPoses = null,
            string[] blendShapeNames = null,
            int[] blendShapeFrameCounts = null,
            IReadOnlyList<string> bonePaths = null,
            BlendShapeFrameSnapshot[][] blendShapeFrames = null,
            Matrix4x4[] boneWorldToLocalMatrices = null)
        {
            var uvCopy = NewUvArray();
            if (uvs != null)
            {
                var count = Mathf.Min(uvs.Length, uvCopy.Length);
                for (var i = 0; i < count; i++) uvCopy[i] = Copy(uvs[i]);
            }

            var subMeshCopy = Array.Empty<int[]>();
            if (subMeshes != null)
            {
                subMeshCopy = new int[subMeshes.Length][];
                for (var i = 0; i < subMeshes.Length; i++) subMeshCopy[i] = Copy(subMeshes[i]);
            }

            return new MeshSnapshot(
                name,
                Copy(vertices),
                Copy(normals),
                Copy(tangents),
                Copy(colors),
                uvCopy,
                subMeshCopy,
                Copy(topologies),
                bounds,
                indexFormat,
                Copy(boneWeights),
                Copy(bindPoses),
                Copy(blendShapeNames),
                Copy(blendShapeFrameCounts),
                new BoneSignature(Copy(bonePaths)),
                blendShapeFrames ?? Array.Empty<BlendShapeFrameSnapshot[]>(),
                Copy(boneWorldToLocalMatrices));
        }

        private static T[] Copy<T>(T[] source)
        {
            if (source == null || source.Length == 0) return Array.Empty<T>();
            var copy = new T[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static IReadOnlyList<string> Copy(IReadOnlyList<string> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<string>();
            var copy = new string[source.Count];
            for (var i = 0; i < source.Count; i++) copy[i] = source[i];
            return copy;
        }

        /// <summary>Creates an eight-slot UV array containing empty channels.</summary>
        public static Vector4[][] NewUvArray()
        {
            var result = new Vector4[ApaMeshLimits.MaxUvChannels][];
            for (var i = 0; i < result.Length; i++) result[i] = Array.Empty<Vector4>();
            return result;
        }

        /// <summary>
        /// True when the given UV channel has data.
        /// </summary>
        /// <remarks>
        /// A zero-vertex mesh reports <c>false</c> for every channel: a channel whose length happens to match a
        /// zero-vertex buffer carries no values, so treating it as present would create an empty output channel.
        /// </remarks>
        public bool HasUvChannel(int channel)
        {
            return channel >= 0
                   && channel < _uvs.Length
                   && _uvs[channel] != null
                   && _uvs[channel].Length == _vertices.Length
                   && _vertices.Length > 0;
        }

        /// <summary>
        /// Returns the UV channel data, or an empty list when the channel is absent.
        /// </summary>
        /// <remarks>
        /// The cached read-only view avoids copying a full channel in the assembler's inner loop.
        /// </remarks>
        public IReadOnlyList<Vector4> GetUvChannel(int channel)
        {
            if (channel < 0 || channel >= _uvViews.Count) return Array.Empty<Vector4>();
            return _uvViews[channel];
        }

        /// <summary>Total triangle count across all submeshes, counting only triangle topologies.</summary>
        public int TotalTriangleCount()
        {
            var total = 0;
            for (var i = 0; i < _subMeshes.Length; i++)
            {
                if (i < _topologies.Length && _topologies[i] != MeshTopology.Triangles) continue;
                total += _subMeshes[i].Length / ApaMeshLimits.TriangleStride;
            }

            return total;
        }

        /// <summary>
        /// The triangle count of one submesh, or -1 when the submesh index is out of range or the submesh is
        /// not a triangle list.
        /// </summary>
        public int TriangleCountIn(int subMeshIndex)
        {
            if (subMeshIndex < 0 || subMeshIndex >= _subMeshes.Length) return -1;
            if (subMeshIndex < _topologies.Length && _topologies[subMeshIndex] != MeshTopology.Triangles) return -1;
            var indices = _subMeshes[subMeshIndex];
            return indices != null ? indices.Length / ApaMeshLimits.TriangleStride : 0;
        }

        /// <summary>
        /// The list of present UV channels in ascending channel order. Deterministic by construction.
        /// </summary>
        public List<int> PresentUvChannels()
        {
            var result = new List<int>();
            for (var i = 0; i < _uvs.Length; i++)
            {
                if (HasUvChannel(i)) result.Add(i);
            }

            return result;
        }

        /// <summary>
        /// True when any submesh is a non-triangle topology. Used by the attribute rule to report what cannot
        /// be merged rather than failing later during index remapping.
        /// </summary>
        public bool HasNonTriangleTopology()
        {
            for (var i = 0; i < _topologies.Length; i++)
            {
                if (_topologies[i] != MeshTopology.Triangles) return true;
            }

            return false;
        }
    }
}

using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Caches the vertex and triangle-index arrays of one mesh for the Scene View overlay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading <c>Mesh.vertices</c> or <c>Mesh.GetIndices</c> allocates a fresh managed copy every call, and the
    /// overlay reads them on every repaint. On a 30k–100k triangle body mesh that is a multi-megabyte allocation
    /// per repaint, which is what makes the overlay the slowest thing in the Scene View exactly on the assets it
    /// targets. This cache keeps the arrays until the mesh changes.
    /// </para>
    /// <para>
    /// The cache is keyed on the mesh instance, its vertex count, and its submesh count, so switching to a
    /// different mesh, or a mesh that was rebuilt with a different size, misses and re-reads. A mesh edited in
    /// place without changing its size (an undo of a vertex edit, for example) is not visible to that key, so
    /// <see cref="Invalidate"/> exists and the window calls it through
    /// <see cref="ApaAuthoringSceneTool.InvalidateMeshCache"/> from <c>Undo.undoRedoPerformed</c>.
    /// </para>
    /// <para>
    /// An unreadable mesh, a null mesh, and an empty mesh all report <c>false</c> and are never cached, which is
    /// the same requirement the core enforces: a mesh that cannot be read cannot be highlighted.
    /// </para>
    /// <para>
    /// <b>Nothing here picks.</b> The class used to share a file with the Scene View picking helper that the
    /// removal-triangle pick mode needed; that mode, the helper, and its triangle budgets were removed with
    /// mask-only removal authoring, and only the array cache the overlays read remained.
    /// </para>
    /// </remarks>
    public sealed class ApaMeshArrayCache
    {
        private bool _valid;
        private int _meshInstanceId;
        private int _vertexCount;
        private int _subMeshCount;
        private Vector3[] _vertices;
        private int[][] _triangleIndices;

        /// <summary>
        /// Returns the cached arrays for a mesh, reading them once. <paramref name="triangleIndices"/> has one
        /// entry per submesh: the index array for a triangle list, and null for any other topology.
        /// </summary>
        public bool TryRead(Mesh mesh, out Vector3[] vertices, out int[][] triangleIndices)
        {
            vertices = null;
            triangleIndices = null;

            if (mesh == null || !mesh.isReadable) return false;

            var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);
            var instanceId = mesh.GetInstanceID();

            if (!_valid || _meshInstanceId != instanceId || _vertexCount != mesh.vertexCount
                || _subMeshCount != subMeshCount)
            {
                _vertices = mesh.vertices;
                _triangleIndices = new int[subMeshCount][];
                for (var subMesh = 0; subMesh < subMeshCount; subMesh++)
                {
                    _triangleIndices[subMesh] = mesh.GetTopology(subMesh) == MeshTopology.Triangles
                        ? mesh.GetIndices(subMesh)
                        : null;
                }

                _meshInstanceId = instanceId;
                _vertexCount = mesh.vertexCount;
                _subMeshCount = subMeshCount;
                _valid = true;
            }

            if (_vertices == null || _vertices.Length == 0) return false;

            vertices = _vertices;
            triangleIndices = _triangleIndices;
            return true;
        }

        /// <summary>Drops the cached arrays. Called when a mesh may have changed without changing identity.</summary>
        public void Invalidate()
        {
            _valid = false;
            _vertices = null;
            _triangleIndices = null;
        }

        /// <summary>
        /// Total triangle count of the cached arrays. Zero when nothing is cached.
        /// </summary>
        public static int CountTriangles(int[][] triangleIndices)
        {
            if (triangleIndices == null) return 0;

            var total = 0;
            for (var subMesh = 0; subMesh < triangleIndices.Length; subMesh++)
            {
                var indices = triangleIndices[subMesh];
                if (indices != null) total += indices.Length / ApaMeshLimits.TriangleStride;
            }

            return total;
        }
    }
}

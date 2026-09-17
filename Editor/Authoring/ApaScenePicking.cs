using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Caches the vertex and triangle-index arrays of one mesh for the Scene View tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading <c>Mesh.vertices</c> or <c>Mesh.GetIndices</c> allocates a fresh managed copy every call, and the
    /// Scene View tool reads them on every mouse move, every drag, and every repaint. On a 30k–100k triangle
    /// body mesh that is a multi-megabyte allocation per event, which is what makes the tool the slowest thing
    /// in the Scene View exactly on the assets it targets. This cache keeps the arrays until the mesh changes.
    /// </para>
    /// <para>
    /// The cache is keyed on the mesh instance, its vertex count, and its submesh count, so switching to a
    /// different mesh, or a mesh that was rebuilt with a different size, misses and re-reads. A mesh edited in
    /// place without changing its size (an undo of a vertex edit, for example) is not visible to that key, so
    /// <see cref="Invalidate"/> exists and the window calls it from <c>Undo.undoRedoPerformed</c>.
    /// </para>
    /// <para>
    /// An unreadable mesh, a null mesh, and an empty mesh all report <c>false</c> and are never cached, which is
    /// the same requirement the core enforces: a mesh that cannot be read cannot be picked or highlighted.
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
        /// Total triangle count of the cached arrays, for the hover budget. Zero when nothing is cached.
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

    /// <summary>
    /// Read-only picking helpers for the Scene View: which triangle is under the cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The helper works in the renderer's own local space, transformed from the mouse ray, because that is the
    /// space the assembly core reads: <see cref="MeshSnapshotFactory"/> captures raw mesh vertices and maps them
    /// with the renderer's transform, so a triangle picked here is the same triangle the assembler will remove.
    /// Deformed skinning is deliberately not evaluated — the core does not evaluate it either, and a highlight
    /// that disagreed with the build would be worse than no highlight.
    /// </para>
    /// <para>
    /// A mesh must be readable to be picked, which is the same requirement the core enforces. Hover picking runs
    /// on every mouse move, so it is bounded twice: by a conservative triangle budget, and by
    /// <see cref="ApaMeshArrayCache"/>, which supplies the vertex and index arrays the picks read. Clicking is
    /// one-shot and is allowed on larger meshes; it uses the same arrays, which are refreshed whenever the mesh
    /// instance or its size changes and dropped on undo.
    /// </para>
    /// <para>
    /// <b>Vertex picking is gone (M10).</b> The nearest-vertex helper existed for the seam-vertex picking modes;
    /// a seam is now generated from world positions in the authoring window, so no Scene View interaction selects
    /// a vertex and the helper and its budget were removed with it.
    /// </para>
    /// </remarks>
    public static class ApaScenePicking
    {
        /// <summary>
        /// Largest triangle count for which per-mouse-move hover picking is attempted. Deliberately far below
        /// the click budget: a hover highlight is cosmetic, and a full triangle scan per mouse event is what
        /// makes the tool crawl on the 30k–100k triangle body meshes it targets.
        /// </summary>
        public const int HoverTriangleBudget = 10000;

        /// <summary>Largest triangle count for which a click pick is attempted at all.</summary>
        public const int ClickTriangleBudget = 2000000;

        /// <summary>
        /// Total triangle count of the triangle submeshes of a mesh. A mesh that is not readable reports zero
        /// rather than reading its index buffers, because an unreadable mesh cannot be picked anyway.
        /// </summary>
        public static int CountTriangles(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return 0;

            var total = 0;
            var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);
            for (var subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(subMesh);
                if (indices != null) total += indices.Length / ApaMeshLimits.TriangleStride;
            }

            return total;
        }

        /// <summary>
        /// Picks the closest triangle of a renderer that a world-space ray hits.
        /// </summary>
        /// <param name="renderer">The renderer whose mesh is tested.</param>
        /// <param name="mesh">Its mesh. Must be readable.</param>
        /// <param name="worldRay">The ray, in world space.</param>
        /// <param name="address">The picked triangle address.</param>
        /// <param name="localDistance">
        /// The hit distance along the local-space ray. Used only to order two candidate hits; it is not a world
        /// distance.
        /// </param>
        public static bool TryPickTriangle(
            Renderer renderer,
            Mesh mesh,
            Ray worldRay,
            out RemovedTriangleAddress address,
            out float localDistance)
        {
            address = RemovedTriangleAddress.None;
            localDistance = float.PositiveInfinity;

            if (renderer == null || mesh == null || !mesh.isReadable) return false;

            var vertices = mesh.vertices;
            if (vertices.Length == 0) return false;

            return TryPickTriangle(renderer, vertices, ReadTriangleIndices(mesh), worldRay, out address,
                out localDistance);
        }

        /// <summary>
        /// The pick behind <see cref="TryPickTriangle(Renderer, Mesh, Ray, out RemovedTriangleAddress, out float)"/>,
        /// with caller-owned arrays so a per-mouse-move pick does not re-read the mesh.
        /// </summary>
        /// <param name="vertices">The mesh's vertex array, from <see cref="ApaMeshArrayCache"/>.</param>
        /// <param name="triangleIndices">
        /// One index array per submesh from the same cache, or null for a topology that is not a triangle list.
        /// </param>
        public static bool TryPickTriangle(
            Renderer renderer,
            Vector3[] vertices,
            int[][] triangleIndices,
            Ray worldRay,
            out RemovedTriangleAddress address,
            out float localDistance)
        {
            address = RemovedTriangleAddress.None;
            localDistance = float.PositiveInfinity;

            if (renderer == null || vertices == null || vertices.Length == 0) return false;
            if (triangleIndices == null) return false;

            var toLocal = renderer.transform.worldToLocalMatrix;
            var origin = toLocal.MultiplyPoint3x4(worldRay.origin);
            var direction = toLocal.MultiplyVector(worldRay.direction);

            var found = false;

            for (var subMesh = 0; subMesh < triangleIndices.Length; subMesh++)
            {
                var indices = triangleIndices[subMesh];
                if (indices == null) continue;

                var triangleCount = indices.Length / ApaMeshLimits.TriangleStride;
                for (var triangle = 0; triangle < triangleCount; triangle++)
                {
                    var offset = triangle * ApaMeshLimits.TriangleStride;
                    var a = indices[offset];
                    var b = indices[offset + 1];
                    var c = indices[offset + 2];

                    if (a < 0 || a >= vertices.Length) continue;
                    if (b < 0 || b >= vertices.Length) continue;
                    if (c < 0 || c >= vertices.Length) continue;

                    if (!RayIntersectsTriangle(origin, direction, vertices[a], vertices[b], vertices[c], out var t))
                    {
                        continue;
                    }

                    if (found && t >= localDistance) continue;

                    found = true;
                    localDistance = t;
                    address = new RemovedTriangleAddress(subMesh, triangle);
                }
            }

            return found;
        }

        /// <summary>
        /// One index array per submesh, or null for a topology that is not a triangle list. Used by the
        /// single-shot entry points; the per-event callers use <see cref="ApaMeshArrayCache"/> instead.
        /// </summary>
        private static int[][] ReadTriangleIndices(Mesh mesh)
        {
            var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);
            var result = new int[subMeshCount][];
            for (var subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                result[subMesh] = mesh.GetTopology(subMesh) == MeshTopology.Triangles
                    ? mesh.GetIndices(subMesh)
                    : null;
            }

            return result;
        }

        /// <summary>Möller–Trumbore intersection. Both faces are hit, because a body mesh is not closed.</summary>
        private static bool RayIntersectsTriangle(
            Vector3 origin,
            Vector3 direction,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            out float distance)
        {
            distance = 0f;

            var edge1 = b - a;
            var edge2 = c - a;
            var pVector = Vector3.Cross(direction, edge2);
            var determinant = Vector3.Dot(edge1, pVector);

            // A near-zero determinant means the ray is parallel to the triangle plane. Culling is disabled, so
            // a negative determinant is a back-face hit and must be accepted.
            if (Mathf.Abs(determinant) < 1e-12f) return false;

            var inverseDeterminant = 1f / determinant;
            var tVector = origin - a;

            var u = Vector3.Dot(tVector, pVector) * inverseDeterminant;
            if (u < 0f || u > 1f) return false;

            var qVector = Vector3.Cross(tVector, edge1);
            var v = Vector3.Dot(direction, qVector) * inverseDeterminant;
            if (v < 0f || u + v > 1f) return false;

            distance = Vector3.Dot(edge2, qVector) * inverseDeterminant;
            return distance > 0f;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Proof that a captured body mesh is the authored body with triangles removed, plus the address mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem it solves.</b> A profile's removal triangles, seam vertex indices, and compatibility
    /// fingerprint are all expressed against the mesh the profile was authored on. In NDMF's Scene View preview
    /// an earlier stage — Modular Avatar's Mesh Cutter — replaces the body's mesh on the proxy before this
    /// package's capture runs. The captured mesh is therefore a <i>different</i> mesh, and applying authored
    /// triangle addresses to it by position would delete or weld the wrong geometry. Refusing outright would
    /// make the preview unable to show a cut body at all, so the correspondence is proven instead.
    /// </para>
    /// <para>
    /// <b>Why it can be proven rather than guessed.</b> The cutter's own implementation makes the derivation
    /// exact: its preview path clones the mesh and rewrites only the index buffers, so every vertex survives at
    /// its authored index with identical attributes, and each submesh's triangle list is an order-preserving
    /// subsequence of the authored one; a submesh whose primitives were all removed receives one degenerate
    /// <c>(0,0,0)</c> triangle as a placeholder. The proof checks exactly those properties and nothing else — no
    /// tolerance, no heuristic, no "looks similar".
    /// </para>
    /// <para>
    /// <b>Failure is a refusal, not a fallback.</b> A derivation that cannot be proven appends a blocking
    /// <c>APA056</c> with a stable <c>reason=</c> token and returns no provenance at all, so no caller can map an
    /// address through a correspondence that does not exist. It only reads: no renderer, mesh, material, or asset
    /// is written, and no Unity object is created.
    /// </para>
    /// <para>
    /// <b>Vertex indices must be preserved.</b> The seam contract addresses the base body by authored vertex
    /// index, and the captured mesh is the one the assembler reads those indices from. A derived mesh that
    /// re-packed its vertices would need a second, separate mapping for seam indices, which this build does not
    /// have; it is refused with <c>body-provenance-vertex-index-remap</c> rather than welded at the wrong
    /// vertices. The cutter's preview path preserves indices, so this refusal does not affect it.
    /// </para>
    /// </remarks>
    public sealed class ApaBodyMeshProvenance
    {
        /// <summary>One of the two meshes is missing, so nothing can be proven.</summary>
        public const string MeshUnreadableReason = "body-provenance-mesh-unreadable";

        /// <summary>The captured mesh has more vertices than the authored mesh.</summary>
        public const string VertexCountMismatchReason = "body-provenance-vertex-count-mismatch";

        /// <summary>A captured vertex has no identical authored vertex at or after the current position.</summary>
        public const string VertexDataMismatchReason = "body-provenance-vertex-data-mismatch";

        /// <summary>The captured mesh re-packed its vertices, so authored vertex indices cannot be used on it.</summary>
        public const string VertexIndexRemapReason = "body-provenance-vertex-index-remap";

        /// <summary>The two meshes have a different number of submeshes.</summary>
        public const string SubMeshCountMismatchReason = "body-provenance-submesh-count-mismatch";

        /// <summary>A submesh is not a triangle list, or the two meshes disagree about its topology.</summary>
        public const string TopologyMismatchReason = "body-provenance-topology-mismatch";

        /// <summary>A captured triangle is not an order-preserving match for an authored triangle.</summary>
        public const string IndexOrderMismatchReason = "body-provenance-index-order-mismatch";

        /// <summary>The two meshes disagree about their blend shape names, frame counts, or frame weights.</summary>
        public const string BlendShapeMismatchReason = "body-provenance-blendshape-mismatch";

        /// <summary>The two meshes disagree about their bind poses.</summary>
        public const string BindPoseMismatchReason = "body-provenance-bindpose-mismatch";

        private readonly int[] _derivedToAuthoredVertex;
        private readonly int[][] _derivedToAuthoredTriangle;
        private readonly int[][] _authoredToDerivedTriangle;

        /// <summary>The mesh the profile's addresses are expressed against.</summary>
        public MeshSnapshot AuthoredMesh { get; }

        /// <summary>The mesh that was actually captured (the earlier stage's output).</summary>
        public MeshSnapshot DerivedMesh { get; }

        /// <summary>
        /// True when every captured vertex sits at its authored index. Always true on a successful proof.
        /// </summary>
        public bool PreservesVertexIndices { get; }

        /// <summary>How many triangles the earlier stage removed.</summary>
        public int RemovedTriangleCount { get; }

        /// <summary>How many vertices the earlier stage removed (zero for the preview cutter).</summary>
        public int RemovedVertexCount { get; }

        /// <summary>The captured mesh's triangle count.</summary>
        public int DerivedTriangleCount { get; }

        /// <summary>The authored mesh's triangle count.</summary>
        public int AuthoredTriangleCount { get; }

        private ApaBodyMeshProvenance(
            MeshSnapshot authored,
            MeshSnapshot derived,
            int[] derivedToAuthoredVertex,
            int[][] derivedToAuthoredTriangle,
            int[][] authoredToDerivedTriangle,
            bool preservesVertexIndices,
            int removedTriangleCount)
        {
            AuthoredMesh = authored;
            DerivedMesh = derived;
            _derivedToAuthoredVertex = derivedToAuthoredVertex;
            _derivedToAuthoredTriangle = derivedToAuthoredTriangle;
            _authoredToDerivedTriangle = authoredToDerivedTriangle;
            PreservesVertexIndices = preservesVertexIndices;
            RemovedTriangleCount = removedTriangleCount;
            RemovedVertexCount = authored.VertexCount - derived.VertexCount;
            DerivedTriangleCount = derived.TotalTriangleCount();
            AuthoredTriangleCount = authored.TotalTriangleCount();
        }

        /// <summary>
        /// Proves that <paramref name="derived"/> is <paramref name="authored"/> with triangles removed.
        /// </summary>
        /// <param name="authored">The mesh the profile was authored against.</param>
        /// <param name="derived">The mesh the capture actually read.</param>
        /// <param name="issues">Receives a blocking <c>APA056</c> when the derivation cannot be proven.</param>
        /// <param name="provenance">The proven correspondence, or null.</param>
        /// <returns>True only when the correspondence was proven exactly.</returns>
        public static bool TryProve(
            MeshSnapshot authored,
            MeshSnapshot derived,
            List<ValidationIssue> issues,
            out ApaBodyMeshProvenance provenance)
        {
            provenance = null;

            if (authored == null || derived == null)
            {
                Refuse(issues, MeshUnreadableReason,
                    "The body mesh derivation could not be checked because one of the two meshes could not be " +
                    "read. The preview refuses the group rather than applying authored triangle addresses to a " +
                    "mesh it cannot prove.");
                return false;
            }

            if (derived.VertexCount > authored.VertexCount)
            {
                Refuse(issues, VertexCountMismatchReason,
                    "The captured body mesh has " + derived.VertexCount + " vertices, more than the authored " +
                    "body's " + authored.VertexCount + ". A mesh with additional vertices is not the authored " +
                    "body with geometry removed, so the profile's addresses cannot be mapped onto it.");
                return false;
            }

            if (derived.SubMeshCount != authored.SubMeshCount)
            {
                Refuse(issues, SubMeshCountMismatchReason,
                    "The captured body mesh has " + derived.SubMeshCount + " submesh(es) while the authored body " +
                    "has " + authored.SubMeshCount + ". Removal addresses are per submesh, so a different submesh " +
                    "layout has no address space the profile could describe.");
                return false;
            }

            for (var subMesh = 0; subMesh < authored.SubMeshCount; subMesh++)
            {
                if (TopologyOf(authored, subMesh) != MeshTopology.Triangles
                    || TopologyOf(derived, subMesh) != MeshTopology.Triangles)
                {
                    Refuse(issues, TopologyMismatchReason,
                        "Submesh " + subMesh + " is not a triangle list on both meshes (authored=" +
                        TopologyOf(authored, subMesh) + ", captured=" + TopologyOf(derived, subMesh) + "). Only " +
                        "triangle lists can be addressed by removal triangles.");
                    return false;
                }
            }

            if (!SameBlendShapeContract(authored, derived))
            {
                Refuse(issues, BlendShapeMismatchReason,
                    "The captured body mesh does not carry the authored body's blend shapes: the names, frame " +
                    "counts, or frame weights differ. A shape whose frames changed is a different deformation " +
                    "space, so the two meshes are not one mesh with triangles removed.");
                return false;
            }

            if (!SameBindPoses(authored, derived))
            {
                Refuse(issues, BindPoseMismatchReason,
                    "The captured body mesh's bind poses differ from the authored body's. The skinning basis is " +
                    "part of the address space, so a mesh with different bind poses is not the authored body " +
                    "with geometry removed.");
                return false;
            }

            var vertexMap = new int[derived.VertexCount];
            if (!TryMapVertices(authored, derived, vertexMap, issues, out var preservesIndices)) return false;

            var derivedToAuthored = new int[derived.SubMeshCount][];
            var authoredToDerived = new int[derived.SubMeshCount][];
            var removed = 0;

            for (var subMesh = 0; subMesh < derived.SubMeshCount; subMesh++)
            {
                if (!TryMapTriangles(
                        authored, derived, subMesh, vertexMap,
                        out derivedToAuthored[subMesh], out authoredToDerived[subMesh], out var removedHere,
                        issues))
                {
                    return false;
                }

                removed += removedHere;
            }

            provenance = new ApaBodyMeshProvenance(
                authored, derived, vertexMap, derivedToAuthored, authoredToDerived, preservesIndices, removed);
            return true;
        }

        /// <summary>The authored vertex a captured vertex came from, or false when the index is out of range.</summary>
        public bool TryMapDerivedVertex(int derivedVertex, out int authoredVertex)
        {
            authoredVertex = -1;
            if (_derivedToAuthoredVertex == null) return false;
            if (derivedVertex < 0 || derivedVertex >= _derivedToAuthoredVertex.Length) return false;

            authoredVertex = _derivedToAuthoredVertex[derivedVertex];
            return true;
        }

        /// <summary>The authored triangle a captured triangle came from, or false when the index is out of range.</summary>
        public bool TryMapDerivedTriangle(int subMesh, int derivedTriangle, out int authoredTriangle)
        {
            authoredTriangle = -1;
            if (_derivedToAuthoredTriangle == null) return false;
            if (subMesh < 0 || subMesh >= _derivedToAuthoredTriangle.Length) return false;

            var map = _derivedToAuthoredTriangle[subMesh];
            if (map == null || derivedTriangle < 0 || derivedTriangle >= map.Length) return false;

            authoredTriangle = map[derivedTriangle];
            return true;
        }

        /// <summary>
        /// The captured triangle an authored triangle survived as, or <c>-1</c> when the earlier stage removed it.
        /// </summary>
        /// <remarks>
        /// This is the direction the removal plan needs: an authored address that was removed by the earlier
        /// stage is already satisfied, so the plan must drop the claim instead of deleting a different triangle.
        /// </remarks>
        public bool TryMapAuthoredTriangle(int subMesh, int authoredTriangle, out int derivedTriangle)
        {
            derivedTriangle = -1;
            if (_authoredToDerivedTriangle == null) return false;
            if (subMesh < 0 || subMesh >= _authoredToDerivedTriangle.Length) return false;

            var map = _authoredToDerivedTriangle[subMesh];
            if (map == null || authoredTriangle < 0 || authoredTriangle >= map.Length) return false;

            derivedTriangle = map[authoredTriangle];
            return true;
        }

        /// <summary>A one-line description of the correspondence, for a report or a log.</summary>
        public string Describe()
        {
            return "authored=" + AuthoredTriangleCount + "t/" + AuthoredMesh.VertexCount + "v; captured=" +
                   DerivedTriangleCount + "t/" + DerivedMesh.VertexCount + "v; removed=" + RemovedTriangleCount +
                   "t/" + RemovedVertexCount + "v; vertexIndices=" +
                   (PreservesVertexIndices ? "preserved" : "remapped");
        }

        /// <summary>Maps every captured vertex onto its authored vertex, order-preserving.</summary>
        /// <remarks>
        /// The walk is two-pointer rather than a search per vertex: the cutter keeps the authored order, so a
        /// single pass proves the subset relation in O(n) and a mismatch is reported at the first captured vertex
        /// that has no identical authored counterpart at or after the current position.
        /// </remarks>
        private static bool TryMapVertices(
            MeshSnapshot authored,
            MeshSnapshot derived,
            int[] vertexMap,
            List<ValidationIssue> issues,
            out bool preservesIndices)
        {
            preservesIndices = true;
            var authoredIndex = 0;

            for (var derivedIndex = 0; derivedIndex < derived.VertexCount; derivedIndex++)
            {
                var matched = -1;
                while (authoredIndex < authored.VertexCount)
                {
                    var candidate = authoredIndex;
                    authoredIndex++;
                    if (!SameVertexData(authored, candidate, derived, derivedIndex)) continue;

                    matched = candidate;
                    break;
                }

                if (matched < 0)
                {
                    Refuse(issues, VertexDataMismatchReason,
                        "Captured vertex " + derivedIndex + " has no identical authored vertex at or after " +
                        "authored vertex " + (authoredIndex - 1) + ". The captured mesh is not the authored body " +
                        "with geometry removed: a vertex's position, attributes, skin weights, or blend shape " +
                        "deltas changed.");
                    return false;
                }

                vertexMap[derivedIndex] = matched;
                if (matched != derivedIndex) preservesIndices = false;
            }

            if (!preservesIndices)
            {
                // The seam contract addresses the base body by authored vertex index and the assembler reads
                // those indices from the captured mesh. A re-packed mesh needs its own seam mapping, which this
                // build does not have, so it is refused instead of welded at the wrong vertices.
                Refuse(issues, VertexIndexRemapReason,
                    "The captured body mesh re-packed its vertices (captured vertex n is not authored vertex n). " +
                    "Seam vertex indices are authored indices, and this build has no separate seam mapping for a " +
                    "re-packed mesh, so the group is refused rather than welded at the wrong vertices.");
                return false;
            }

            return true;
        }

        /// <summary>Maps one submesh's captured triangles onto the authored triangles they came from.</summary>
        private static bool TryMapTriangles(
            MeshSnapshot authored,
            MeshSnapshot derived,
            int subMesh,
            int[] vertexMap,
            out int[] derivedToAuthored,
            out int[] authoredToDerived,
            out int removed,
            List<ValidationIssue> issues)
        {
            removed = 0;
            var authoredIndices = authored.SubMeshes[subMesh];
            var derivedIndices = derived.SubMeshes[subMesh];

            var authoredTriangles = authoredIndices.Length / ApaMeshLimits.TriangleStride;
            var derivedTriangles = derivedIndices.Length / ApaMeshLimits.TriangleStride;

            derivedToAuthored = new int[derivedTriangles];
            for (var i = 0; i < derivedToAuthored.Length; i++) derivedToAuthored[i] = -1;

            authoredToDerived = new int[authoredTriangles];
            for (var i = 0; i < authoredToDerived.Length; i++) authoredToDerived[i] = -1;

            var authoredTriangle = 0;
            for (var derivedTriangle = 0; derivedTriangle < derivedTriangles; derivedTriangle++)
            {
                var matched = -1;
                while (authoredTriangle < authoredTriangles)
                {
                    var candidate = authoredTriangle;
                    authoredTriangle++;

                    if (!SameTriangle(derivedIndices, derivedTriangle, authoredIndices, candidate, vertexMap))
                    {
                        continue;
                    }

                    matched = candidate;
                    break;
                }

                if (matched < 0)
                {
                    // An emptied submesh keeps one degenerate (0,0,0) triangle as a placeholder so Unity does not
                    // create a submesh with no primitives. It is admitted as "this submesh was emptied": it maps
                    // to no authored triangle, and every remaining authored triangle in the submesh is removed.
                    if (derivedTriangles == 1 && IsDegeneratePlaceholder(derivedIndices, derivedTriangle))
                    {
                        // It maps to no authored triangle; the count of removed triangles is taken from the
                        // authored map below, so nothing is added here.
                        continue;
                    }

                    Refuse(issues, IndexOrderMismatchReason,
                        "Captured triangle " + derivedTriangle + " of submesh " + subMesh + " is not an " +
                        "order-preserving match for any remaining authored triangle. The earlier stage did not " +
                        "only remove triangles from this body, so the profile's removal addresses cannot be " +
                        "mapped onto the captured mesh.");
                    return false;
                }

                derivedToAuthored[derivedTriangle] = matched;
                authoredToDerived[matched] = derivedTriangle;
            }

            removed += authoredTriangles - CountMapped(authoredToDerived);
            return true;
        }

        private static int CountMapped(int[] map)
        {
            var count = 0;
            for (var i = 0; i < map.Length; i++)
            {
                if (map[i] >= 0) count++;
            }

            return count;
        }

        /// <summary>True when the triangle is the cutter's "this submesh was emptied" placeholder.</summary>
        private static bool IsDegeneratePlaceholder(int[] indices, int triangle)
        {
            var start = triangle * ApaMeshLimits.TriangleStride;
            if (start + 2 >= indices.Length) return false;

            return indices[start] == 0 && indices[start + 1] == 0 && indices[start + 2] == 0;
        }

        private static bool SameTriangle(
            int[] derivedIndices, int derivedTriangle,
            int[] authoredIndices, int authoredTriangle,
            int[] vertexMap)
        {
            var derivedStart = derivedTriangle * ApaMeshLimits.TriangleStride;
            var authoredStart = authoredTriangle * ApaMeshLimits.TriangleStride;

            for (var corner = 0; corner < ApaMeshLimits.TriangleStride; corner++)
            {
                var derivedVertex = derivedIndices[derivedStart + corner];
                if (derivedVertex < 0 || derivedVertex >= vertexMap.Length) return false;

                if (vertexMap[derivedVertex] != authoredIndices[authoredStart + corner]) return false;
            }

            return true;
        }

        private static MeshTopology TopologyOf(MeshSnapshot mesh, int subMesh)
        {
            if (subMesh < 0 || subMesh >= mesh.Topologies.Length) return MeshTopology.Triangles;
            return mesh.Topologies[subMesh];
        }

        private static bool SameBlendShapeContract(MeshSnapshot authored, MeshSnapshot derived)
        {
            if (authored.BlendShapeCount != derived.BlendShapeCount) return false;

            for (var shape = 0; shape < authored.BlendShapeCount; shape++)
            {
                if (!string.Equals(authored.Shapes[shape], derived.Shapes[shape], StringComparison.Ordinal))
                {
                    return false;
                }

                if (authored.ShapeFrameCounts[shape] != derived.ShapeFrameCounts[shape]) return false;

                var authoredFrames = authored.BlendShapeFrames[shape];
                var derivedFrames = derived.BlendShapeFrames[shape];
                if (authoredFrames.Count != derivedFrames.Count) return false;

                for (var frame = 0; frame < authoredFrames.Count; frame++)
                {
                    if (!SameFloat(authoredFrames[frame].Weight, derivedFrames[frame].Weight)) return false;
                }
            }

            return true;
        }

        private static bool SameBindPoses(MeshSnapshot authored, MeshSnapshot derived)
        {
            if (authored.SkinBindPoses.Count != derived.SkinBindPoses.Count) return false;

            for (var i = 0; i < authored.SkinBindPoses.Count; i++)
            {
                if (!SameMatrix(authored.SkinBindPoses[i], derived.SkinBindPoses[i])) return false;
            }

            return true;
        }

        /// <summary>
        /// True when the captured vertex carries exactly the authored vertex's data.
        /// </summary>
        /// <remarks>
        /// Every attribute the assembler reads participates, including the blend shape deltas at this vertex: a
        /// vertex that moved for a shape is a different vertex even if its position matches, and treating it as
        /// the same one would weld the wrong geometry.
        /// </remarks>
        private static bool SameVertexData(MeshSnapshot authored, int authoredVertex, MeshSnapshot derived, int derivedVertex)
        {
            if (!SameVector3(authored.Vertices[authoredVertex], derived.Vertices[derivedVertex])) return false;

            if (authored.HasNormals != derived.HasNormals) return false;
            if (authored.HasNormals && !SameVector3(authored.Normals[authoredVertex], derived.Normals[derivedVertex]))
            {
                return false;
            }

            if (authored.HasTangents != derived.HasTangents) return false;
            if (authored.HasTangents && !SameVector4(authored.Tangents[authoredVertex], derived.Tangents[derivedVertex]))
            {
                return false;
            }

            if (authored.HasColors != derived.HasColors) return false;
            if (authored.HasColors && !SameColor(authored.Colors[authoredVertex], derived.Colors[derivedVertex]))
            {
                return false;
            }

            var channels = Mathf.Max(authored.UvChannelCapacity, derived.UvChannelCapacity);
            for (var channel = 0; channel < channels; channel++)
            {
                var authoredHas = authored.HasUvChannel(channel);
                if (authoredHas != derived.HasUvChannel(channel)) return false;
                if (!authoredHas) continue;

                if (!SameVector4(authored.Uvs[channel][authoredVertex], derived.Uvs[channel][derivedVertex]))
                {
                    return false;
                }
            }

            var authoredSkinned = authored.SkinWeights.Count > authoredVertex;
            var derivedSkinned = derived.SkinWeights.Count > derivedVertex;
            if (authoredSkinned != derivedSkinned) return false;
            if (authoredSkinned
                && !SameBoneWeight(authored.SkinWeights[authoredVertex], derived.SkinWeights[derivedVertex]))
            {
                return false;
            }

            for (var shape = 0; shape < authored.BlendShapeCount; shape++)
            {
                var authoredFrames = authored.BlendShapeFrames[shape];
                var derivedFrames = derived.BlendShapeFrames[shape];
                if (authoredFrames.Count != derivedFrames.Count) return false;

                for (var frame = 0; frame < authoredFrames.Count; frame++)
                {
                    var a = authoredFrames[frame];
                    var d = derivedFrames[frame];
                    if (a.VertexCount != authored.VertexCount || d.VertexCount != derived.VertexCount) return false;

                    if (!SameVector3(a.DeltaVertices[authoredVertex], d.DeltaVertices[derivedVertex])) return false;
                    if (!SameVector3(a.DeltaNormals[authoredVertex], d.DeltaNormals[derivedVertex])) return false;
                    if (!SameVector3(a.DeltaTangents[authoredVertex], d.DeltaTangents[derivedVertex])) return false;
                }
            }

            return true;
        }

        private static bool SameFloat(float a, float b)
        {
            return a.Equals(b);
        }

        private static bool SameVector3(Vector3 a, Vector3 b)
        {
            return SameFloat(a.x, b.x) && SameFloat(a.y, b.y) && SameFloat(a.z, b.z);
        }

        private static bool SameVector4(Vector4 a, Vector4 b)
        {
            return SameFloat(a.x, b.x) && SameFloat(a.y, b.y) && SameFloat(a.z, b.z) && SameFloat(a.w, b.w);
        }

        private static bool SameColor(Color a, Color b)
        {
            return SameFloat(a.r, b.r) && SameFloat(a.g, b.g) && SameFloat(a.b, b.b) && SameFloat(a.a, b.a);
        }

        private static bool SameBoneWeight(BoneWeight a, BoneWeight b)
        {
            return a.boneIndex0 == b.boneIndex0 && a.boneIndex1 == b.boneIndex1
                   && a.boneIndex2 == b.boneIndex2 && a.boneIndex3 == b.boneIndex3
                   && SameFloat(a.weight0, b.weight0) && SameFloat(a.weight1, b.weight1)
                   && SameFloat(a.weight2, b.weight2) && SameFloat(a.weight3, b.weight3);
        }

        private static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    if (!SameFloat(a[row, column], b[row, column])) return false;
                }
            }

            return true;
        }

        /// <summary>Appends the blocking refusal with its stable token.</summary>
        private static void Refuse(List<ValidationIssue> issues, string reason, string message)
        {
            if (issues == null) return;

            issues.Add(ValidationIssue.Error(
                ApaErrorCode.BodyMeshDerivationUnproven,
                ApaIssuePhase.Compatibility,
                message + " Author the profile against the mesh this stage produces, or remove the stage that " +
                "rewrites the body geometry.",
                detail: "reason=" + reason));
        }
    }
}

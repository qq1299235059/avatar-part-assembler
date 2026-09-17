using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Where one final output vertex came from.
    /// </summary>
    public enum VertexOrigin
    {
        /// <summary>A retained base body vertex.</summary>
        Base = 0,

        /// <summary>A non-seam part vertex.</summary>
        Part = 1
    }

    /// <summary>
    /// The role a part vertex plays in the seam plan.
    /// </summary>
    /// <remarks>
    /// The role is only meaningful for a <see cref="VertexOrigin.Part"/> vertex. It exists so that a preserved
    /// seam vertex is distinguishable from an ordinary part vertex without re-reading the seam plan: the two take
    /// their position from different sources, which is the one attribute a preserved vertex does not take from
    /// its own part.
    /// </remarks>
    public enum SeamVertexRole
    {
        /// <summary>The vertex is not a paired seam vertex.</summary>
        None = 0,

        /// <summary>
        /// The vertex is a part seam vertex whose triangles reference the retained base vertex instead. Never
        /// emitted: a welded pair maps onto the base vertex's final index, so no final vertex carries this role.
        /// </summary>
        Welded = 1,

        /// <summary>
        /// The vertex is a part seam vertex that keeps its own final vertex because a same-name UV semantic
        /// disagreed at the pair (M11). Its position and position deltas come from the paired base vertex so the
        /// seam stays closed; every other attribute comes from the part vertex.
        /// </summary>
        Split = 2
    }

    /// <summary>
    /// How one final vertex is produced.
    /// </summary>
    /// <remarks>
    /// The plan stores provenance rather than values so that the assembler can rebuild each attribute from the
    /// correct owner. At a welded vertex the base is the owner of position, normal, tangent, color, and skin
    /// weight; UV semantics are the exception and are resolved through <see cref="MeshAssemblyPlan.Welds"/>.
    /// A preserved (split) seam vertex is a part-owned vertex in every respect except position and position
    /// deltas, which come from the paired base vertex so the two sides of the seam cannot separate.
    /// </remarks>
    public struct FinalVertexSource
    {
        /// <summary>Whether this output vertex comes from the base or from a part.</summary>
        public VertexOrigin Origin;

        /// <summary>Original vertex index in the source mesh.</summary>
        public int SourceVertex;

        /// <summary>Owning part id, or an empty string for a base vertex.</summary>
        public string PartId;

        /// <summary>
        /// For a part vertex, the <i>source</i> index of the base seam vertex it is paired with, or -1 when it is
        /// not a paired seam vertex.
        /// </summary>
        /// <remarks>
        /// The field predates M11, when the only paired part vertex was one that had been welded away and
        /// therefore never appeared in the final list. It is load-bearing now: a preserved
        /// (<see cref="SeamVertexRole.Split"/>) vertex reads its position and its blend shape position deltas
        /// from this base vertex, so the index is a source index rather than a final one.
        /// </remarks>
        public int WeldedBaseVertex;

        /// <summary>The seam role of this vertex; <see cref="SeamVertexRole.None"/> for an unpaired vertex.</summary>
        public SeamVertexRole SeamRole;

        /// <summary>Creates a base vertex source.</summary>
        public static FinalVertexSource ForBase(int sourceVertex)
        {
            return new FinalVertexSource
            {
                Origin = VertexOrigin.Base,
                SourceVertex = sourceVertex,
                PartId = string.Empty,
                WeldedBaseVertex = -1,
                SeamRole = SeamVertexRole.None
            };
        }

        /// <summary>Creates a part vertex source.</summary>
        /// <param name="weldedBaseVertex">
        /// Source index of the paired base vertex, or -1 when the part vertex is not paired. See
        /// <see cref="WeldedBaseVertex"/>.
        /// </param>
        /// <param name="seamRole">The seam role; see <see cref="SeamRole"/>.</param>
        public static FinalVertexSource ForPart(
            string partId,
            int sourceVertex,
            int weldedBaseVertex,
            SeamVertexRole seamRole = SeamVertexRole.None)
        {
            return new FinalVertexSource
            {
                Origin = VertexOrigin.Part,
                SourceVertex = sourceVertex,
                PartId = partId ?? string.Empty,
                WeldedBaseVertex = weldedBaseVertex,
                SeamRole = seamRole
            };
        }

        /// <summary>
        /// True when this vertex is a part seam vertex that welds onto a retained base vertex. Always false for an
        /// emitted vertex: a welded pair is represented by the base vertex, not by a vertex of its own.
        /// </summary>
        public bool IsWeld => Origin == VertexOrigin.Part && SeamRole == SeamVertexRole.Welded;

        /// <summary>
        /// True when this vertex is a part seam vertex that keeps its own vertex and takes its position from the
        /// paired base vertex.
        /// </summary>
        public bool IsSplit => Origin == VertexOrigin.Part && SeamRole == SeamVertexRole.Split;
    }


    /// <summary>
    /// One part's contribution to a single retained base vertex.
    /// </summary>
    /// <remarks>
    /// A weld can fold more than one part seam vertex onto the same base vertex — two parts that share a
    /// junction, for example. The plan records every contribution rather than only the first, because whether
    /// they agree is a validation decision, and a decision cannot be made from data that was discarded while
    /// planning.
    /// </remarks>
    public struct WeldContribution
    {
        /// <summary>The part that contributes.</summary>
        public string PartId;

        /// <summary>The part's own (deleted) seam vertex index.</summary>
        public int PartVertex;

        /// <summary>Creates a contribution.</summary>
        public WeldContribution(string partId, int partVertex)
        {
            PartId = partId ?? string.Empty;
            PartVertex = partVertex;
        }
    }

    /// <summary>
    /// The attribute sources of one retained base vertex that carries at least one weld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the weld direction is fixed: the part seam vertex is deleted and its triangles are
    /// rewritten to the base vertex (section 43.3). That makes the base vertex the only output vertex, so any
    /// semantic only the part has would otherwise be written as the channel default and lost — contradicting
    /// R7 and section 43.2, which require the welded output vertex to take the matched part value.
    /// </para>
    /// <para>
    /// The contributions are ordered by part identity, which is the same stable order the rest of the plan uses,
    /// so the value chosen for a semantic is reproducible rather than dependent on the order parts happened to
    /// be supplied in.
    /// </para>
    /// </remarks>
    public sealed class WeldAttributeSource
    {
        /// <summary>Final output vertex index of the retained base vertex.</summary>
        public int FinalVertex { get; }

        /// <summary>Original base vertex index.</summary>
        public int BaseVertex { get; }

        /// <summary>Every part seam vertex that welds onto this base vertex, in canonical part order.</summary>
        public IReadOnlyList<WeldContribution> Contributions { get; }

        /// <summary>Creates a weld attribute source.</summary>
        public WeldAttributeSource(int finalVertex, int baseVertex, IReadOnlyList<WeldContribution> contributions)
        {
            FinalVertex = finalVertex;
            BaseVertex = baseVertex;
            Contributions = Freeze(contributions);
        }

        /// <summary>True when this base vertex carries a weld at all.</summary>
        public bool HasContributions => Contributions.Count > 0;

        private static IReadOnlyList<WeldContribution> Freeze(IReadOnlyList<WeldContribution> source)
        {
            var copy = new WeldContribution[source?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
        }
    }

    /// <summary>
    /// One output submesh: its final slot and the triangles feeding it, expressed in final vertex indices.
    /// </summary>
    public sealed class PlannedSubMesh
    {
        /// <summary>Final output submesh index.</summary>
        public int OutputSubMesh { get; }

        /// <summary>Semantic occupying this submesh.</summary>
        public string Semantic { get; }

        /// <summary>Material for this submesh.</summary>
        public Material Material { get; }

        /// <summary>
        /// Triangles as triples of final vertex indices. Precomputed by the planner so that the assembler has no
        /// ordering decisions left to make.
        /// </summary>
        public IReadOnlyList<int> Indices { get; }

        /// <summary>Creates a planned submesh.</summary>
        public PlannedSubMesh(int outputSubMesh, string semantic, Material material, IReadOnlyList<int> indices)
        {
            OutputSubMesh = outputSubMesh;
            Semantic = semantic ?? string.Empty;
            Material = material;
            var copy = new int[indices?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = indices[i];
            Indices = Array.AsReadOnly(copy);
        }

        /// <summary>Number of triangles in this submesh.</summary>
        public int TriangleCount => Indices.Count / ApaMeshLimits.TriangleStride;
    }

    /// <summary>
    /// The immutable result of planning: everything the mesh assembler needs, with no remaining decisions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Planning completes before any mutation, so a failed plan can never leave a half-modified mesh behind.
    /// The plan is deliberately expressed in terms of final indices and provenance, not in terms of live Unity
    /// objects, which is what lets the assembler run identically for preview and for build.
    /// </para>
    /// <para>
    /// Ordering inside the plan is fully specified. Retained base vertices come first in ascending original
    /// index, then part vertices by part order and then original index. Submesh order is base-first. Two runs
    /// over equivalent inputs therefore produce identical plans down to the index buffers.
    /// </para>
    /// <para>
    /// The index format is a plan decision, not an assembler guess: see <see cref="IndexFormat"/>.
    /// </para>
    /// </remarks>
    public sealed class MeshAssemblyPlan
    {
        /// <summary>The final vertex list in output order.</summary>
        public IReadOnlyList<FinalVertexSource> Vertices { get; }

        /// <summary>The final submeshes in output order.</summary>
        public IReadOnlyList<PlannedSubMesh> SubMeshes { get; }

        /// <summary>The final UV layout.</summary>
        public UvLayout UvLayout { get; }

        /// <summary>The final material layout.</summary>
        public MaterialLayout MaterialLayout { get; }

        /// <summary>
        /// The removed base triangles, as explicit addresses in ascending canonical order.
        /// </summary>
        public IReadOnlyList<RemovedTriangleAddress> RemovedTriangles { get; }

        /// <summary>
        /// Maps each source vertex of each part to its final vertex index. Keyed by part id, then source index.
        /// The base map is keyed by the empty string.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<int>> VertexRemap { get; }

        /// <summary>Seam correspondences per part id, for diagnostics and for the weld provenance the M2 rules read.</summary>
        public IReadOnlyDictionary<string, SeamResolution> SeamResolutions { get; }

        /// <summary>
        /// The weld/split decision for every explicit seam pair, keyed by part id (M11).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the decision validation and the plan share: a pair appears here as a true weld only when the
        /// part seam vertex was deleted, and as a preserved vertex only when it was emitted. A consumer can
        /// therefore answer "why does this part have more vertices than before" without re-deriving the UV rule.
        /// </para>
        /// <para>
        /// Never null; empty when no part declares a seam.
        /// </para>
        /// </remarks>
        public SeamWeldPlan SeamWelds { get; }

        /// <summary>
        /// Weld attribute sources, keyed by the final index of a retained base vertex that carries at least one
        /// weld. A base vertex with no weld is absent rather than present with no contributions, so the
        /// assembler's lookup is a single dictionary probe on the small minority of vertices that need one.
        /// </summary>
        public IReadOnlyDictionary<int, WeldAttributeSource> Welds { get; }

        /// <summary>
        /// The index format the generated mesh must use. At least as wide as every contributing source's format.
        /// </summary>
        public UnityEngine.Rendering.IndexFormat IndexFormat { get; }

        /// <summary>
        /// The final bone table, or null when no source carries skinning data.
        /// </summary>
        /// <remarks>
        /// A plan either carries a complete, coherent bone table or carries none at all: a partially resolved
        /// table would let the assembler write weights whose indices mean nothing.
        /// </remarks>
        public FinalBoneTable BoneTable { get; }

        /// <summary>The final blend shape set. Never null; empty when no source carries a blend shape.</summary>
        public BlendShapeMergePlan BlendShapes { get; }

        /// <summary>True when the generated mesh must be skinned.</summary>
        public bool RequiresSkinning => BoneTable != null;

        /// <summary>True when the generated mesh must carry blend shapes.</summary>
        public bool HasBlendShapes => BlendShapes != null && !BlendShapes.IsEmpty;

        /// <summary>
        /// Informational and warning diagnostics collected by the planner. These are never blocking, because a
        /// plan is only produced once every blocking issue has been cleared.
        /// </summary>
        public IReadOnlyList<ValidationIssue> NonBlockingIssues { get; }

        /// <summary>Number of output vertices.</summary>
        public int VertexCount => Vertices.Count;

        /// <summary>Number of removed base triangles.</summary>
        public int RemovedTriangleCount => RemovedTriangles.Count;

        /// <summary>
        /// The target renderer group this plan belongs to, or an empty string for a single-target plan.
        /// </summary>
        /// <remarks>
        /// Echo only: the plan already contains only this group's parts, because the context it was built from
        /// contained only them. The key exists so a consumer can name the generated mesh after the group (two
        /// groups can carry meshes with the same asset name) and can attribute a diagnostic to a group without
        /// re-resolving anything.
        /// </remarks>
        public string GroupKey { get; }

        /// <summary>True when this plan belongs to an explicitly keyed target group.</summary>
        public bool HasGroupKey => !string.IsNullOrEmpty(GroupKey);

        /// <summary>
        /// The part that owns each removed base triangle, keyed by address.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A triangle claimed by one part is owned by that part; a contested triangle resolved by an explicit
        /// unequal priority is owned by the highest-priority claimant. This is the same decision
        /// <see cref="RemovalRule"/> reports as <c>APA035</c>, exposed as a value so a consumer does not have to
        /// parse a diagnostic message to learn who owns a region of the body.
        /// </para>
        /// <para>
        /// Ownership never changes geometry: <see cref="RemovedTriangles"/> is the union of the claims either
        /// way, because removal is idempotent. Addresses with no recorded owner (no claimant at all) are absent
        /// rather than present with an empty string.
        /// </para>
        /// </remarks>
        public IReadOnlyDictionary<RemovedTriangleAddress, string> RemovalOwners { get; }

        /// <summary>
        /// The part that owns a removed base triangle, or an empty string when no owner was recorded for it.
        /// </summary>
        public string OwnerOf(RemovedTriangleAddress address)
        {
            return RemovalOwners.TryGetValue(address, out var owner) ? owner ?? string.Empty : string.Empty;
        }

        /// <summary>Creates a plan.</summary>
        public MeshAssemblyPlan(
            IReadOnlyList<FinalVertexSource> vertices,
            IReadOnlyList<PlannedSubMesh> subMeshes,
            UvLayout uvLayout,
            MaterialLayout materialLayout,
            IReadOnlyList<RemovedTriangleAddress> removedTriangles,
            IReadOnlyDictionary<string, int[]> vertexRemap,
            IReadOnlyDictionary<string, SeamResolution> seamResolutions,
            IReadOnlyDictionary<int, WeldAttributeSource> welds,
            UnityEngine.Rendering.IndexFormat indexFormat,
            IReadOnlyList<ValidationIssue> nonBlockingIssues = null,
            FinalBoneTable boneTable = null,
            BlendShapeMergePlan blendShapes = null,
            string groupKey = null,
            IReadOnlyDictionary<RemovedTriangleAddress, string> removalOwners = null,
            SeamWeldPlan seamWelds = null)
        {
            Vertices = Freeze(vertices);
            SubMeshes = Freeze(subMeshes);
            UvLayout = uvLayout ?? new UvLayout(Array.Empty<UvChannelAssignment>());
            MaterialLayout = materialLayout ?? new MaterialLayout(Array.Empty<MaterialSlotAssignment>());
            RemovedTriangles = new RemovedTriangleAddressSet(removedTriangles).Addresses;
            VertexRemap = FreezeVertexRemap(vertexRemap);
            SeamResolutions = FreezeDictionary(seamResolutions, StringComparer.Ordinal);
            Welds = FreezeDictionary(welds, EqualityComparer<int>.Default);
            SeamWelds = seamWelds ?? SeamWeldPlan.Empty;
            IndexFormat = indexFormat;
            NonBlockingIssues = Freeze(nonBlockingIssues);
            BoneTable = boneTable;
            BlendShapes = blendShapes ?? BlendShapeMergePlan.Empty;
            GroupKey = groupKey ?? string.Empty;
            RemovalOwners = FreezeOwners(removalOwners, RemovedTriangles);
        }

        /// <summary>
        /// Copies the owner map and restricts it to the addresses this plan actually removes, so the plan never
        /// exposes an owner for a triangle it did not remove.
        /// </summary>
        private static IReadOnlyDictionary<RemovedTriangleAddress, string> FreezeOwners(
            IReadOnlyDictionary<RemovedTriangleAddress, string> source,
            IReadOnlyList<RemovedTriangleAddress> removed)
        {
            var copy = new Dictionary<RemovedTriangleAddress, string>();
            if (source == null || removed == null) return new ReadOnlyDictionary<RemovedTriangleAddress, string>(copy);

            for (var i = 0; i < removed.Count; i++)
            {
                var address = removed[i];
                if (!source.TryGetValue(address, out var owner)) continue;
                if (string.IsNullOrEmpty(owner)) continue;

                copy[address] = owner;
            }

            return new ReadOnlyDictionary<RemovedTriangleAddress, string>(copy);
        }

        /// <summary>
        /// The final index of a source vertex, or -1 when the vertex was removed or never mapped.
        /// </summary>
        public int FinalIndexOf(string partId, int sourceVertex)
        {
            var key = partId ?? string.Empty;
            if (!VertexRemap.TryGetValue(key, out var map)) return -1;
            if (sourceVertex < 0 || sourceVertex >= map.Count) return -1;
            return map[sourceVertex];
        }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            var copy = new T[source?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<int>> FreezeVertexRemap(
            IReadOnlyDictionary<string, int[]> source)
        {
            var copy = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
            if (source != null)
            {
                foreach (var pair in source)
                {
                    var values = pair.Value ?? Array.Empty<int>();
                    var valueCopy = new int[values.Length];
                    Array.Copy(values, valueCopy, values.Length);
                    copy[pair.Key ?? string.Empty] = Array.AsReadOnly(valueCopy);
                }
            }

            return new ReadOnlyDictionary<string, IReadOnlyList<int>>(copy);
        }

        private static IReadOnlyDictionary<TKey, TValue> FreezeDictionary<TKey, TValue>(
            IReadOnlyDictionary<TKey, TValue> source,
            IEqualityComparer<TKey> comparer)
        {
            var copy = new Dictionary<TKey, TValue>(comparer);
            if (source != null)
            {
                foreach (var pair in source) copy[pair.Key] = pair.Value;
            }

            return new ReadOnlyDictionary<TKey, TValue>(copy);
        }

        /// <summary>Total triangle count across all output submeshes.</summary>
        public int TotalTriangleCount()
        {
            var total = 0;
            for (var i = 0; i < SubMeshes.Count; i++) total += SubMeshes[i].TriangleCount;
            return total;
        }

        /// <summary>
        /// The number of emitted vertices that are <i>welded</i> part seam vertices. Always zero for a correct
        /// plan, because a true weld deletes the part seam vertices rather than emitting duplicates. Used by tests
        /// to assert the weld actually happened.
        /// </summary>
        /// <remarks>
        /// A preserved (split) seam vertex is deliberately not counted here: it is an emitted vertex by design,
        /// and it is reported separately by <see cref="CountEmittedSplitSeamVertices"/>. Folding the two into one
        /// number would make "the weld happened" and "the UV could not be welded" indistinguishable.
        /// </remarks>
        public int CountEmittedPartSeamVertices()
        {
            var count = 0;
            for (var i = 0; i < Vertices.Count; i++)
            {
                if (Vertices[i].IsWeld) count++;
            }

            return count;
        }

        /// <summary>
        /// The number of emitted vertices that are preserved (split) part seam vertices (M11). Equals
        /// <see cref="SeamWeldPlan.SplitCount"/> when the plan is internally consistent.
        /// </summary>
        public int CountEmittedSplitSeamVertices()
        {
            var count = 0;
            for (var i = 0; i < Vertices.Count; i++)
            {
                if (Vertices[i].IsSplit) count++;
            }

            return count;
        }

        /// <summary>
        /// The weld/split decision for a part, or null when the part declares no seam.
        /// </summary>
        public PartSeamWeldPlan SeamWeldsFor(string partId)
        {
            return SeamWelds != null ? SeamWelds.For(partId) : null;
        }

        /// <summary>
        /// The weld attribute source for a final vertex, or null when the vertex carries no weld.
        /// </summary>
        public WeldAttributeSource WeldAt(int finalVertex)
        {
            return Welds.TryGetValue(finalVertex, out var weld) ? weld : null;
        }

        /// <summary>
        /// True when a part seam vertex is mapped to the given final vertex. Used by tests to assert that weld
        /// provenance was recorded.
        /// </summary>
        public bool HasWeldFrom(string partId, int partVertex)
        {
            foreach (var pair in Welds)
            {
                var contributions = pair.Value.Contributions;
                for (var i = 0; i < contributions.Count; i++)
                {
                    if (contributions[i].PartVertex != partVertex) continue;
                    if (string.Equals(contributions[i].PartId, partId ?? string.Empty, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}

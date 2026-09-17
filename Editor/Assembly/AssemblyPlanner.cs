using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The outcome of planning: either a complete plan, or the diagnostics that prevented one.
    /// </summary>
    /// <remarks>
    /// The two are mutually exclusive. On failure <see cref="Plan"/> is null and <see cref="Issues"/> carries
    /// at least one error, so a caller can never accidentally build from a partial plan. This is the mechanism
    /// that makes "no mutation on failure" a structural property rather than a convention.
    /// </remarks>
    public sealed class PlanningResult
    {
        /// <summary>The plan, or null when planning failed.</summary>
        public MeshAssemblyPlan Plan { get; }

        /// <summary>The validation and planning diagnostics, in deterministic order.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when a plan was produced.</summary>
        public bool Succeeded => Plan != null;

        /// <summary>Creates a result.</summary>
        public PlanningResult(MeshAssemblyPlan plan, ValidationResult issues)
        {
            Plan = plan;
            Issues = issues ?? ValidationResult.Empty;
        }

        /// <summary>A failed result carrying only diagnostics.</summary>
        public static PlanningResult Failure(ValidationResult issues)
        {
            return new PlanningResult(null, issues);
        }
    }

    /// <summary>
    /// Builds an immutable <see cref="MeshAssemblyPlan"/> from validated snapshots.
    /// </summary>
    /// <remarks>
    /// The planner makes every decision that affects output ordering, and it makes them once, before any mesh is
    /// touched. The assembler that follows is then a pure data transformation with no remaining choices, which
    /// is what guarantees that preview and build agree.
    /// </remarks>
    public sealed class AssemblyPlanner
    {
        /// <summary>Plans an assembly.</summary>
        public PlanningResult Plan(ValidationContext context)
        {
            if (context == null)
            {
                return PlanningResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "Planning was invoked without a context.",
                    detail: "reason=null-context")));
            }

            try
            {
                return PlanInternal(context);
            }
            catch (Exception e)
            {
                // An unexpected exception must surface as a diagnostic rather than propagate, because the
                // caller's contract is that a failed plan never leaves a modified mesh behind.
                return PlanningResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "Planning threw " + e.GetType().Name + ": " + e.Message,
                    detail: "exception=" + e.GetType().FullName)));
            }
        }

        private PlanningResult PlanInternal(ValidationContext context)
        {
            var validator = new AvatarPartValidator();
            var validation = validator.Validate(context);
            if (!validation.IsValid)
            {
                return PlanningResult.Failure(validation);
            }

            var issues = new List<ValidationIssue>(validation.Issues);
            var baseMesh = context.Base.Mesh;

            // ---- Geometry: base side ----------------------------------------------------------------

            var removed = CollectRemovedTriangles(context);

            // Removal is indexed per submesh. A single flat set would make "triangle 0" mean triangle 0 of
            // every submesh at once, which removes geometry the author never selected.
            var removedBySubMesh = new bool[baseMesh.SubMeshCount][];
            for (var subMesh = 0; subMesh < baseMesh.SubMeshCount; subMesh++)
            {
                var count = baseMesh.TriangleCountIn(subMesh);
                removedBySubMesh[subMesh] = new bool[count > 0 ? count : 0];
            }

            for (var i = 0; i < removed.Count; i++)
            {
                var address = removed[i];
                if (address.SubMeshIndex < 0 || address.SubMeshIndex >= removedBySubMesh.Length) continue;

                var flags = removedBySubMesh[address.SubMeshIndex];
                if (address.TriangleIndexWithinSubMesh < 0
                    || address.TriangleIndexWithinSubMesh >= flags.Length)
                {
                    continue;
                }

                // Validation already rejected every address that could reach this branch, so a miss here would
                // mean validation and planning disagree — which is exactly the aliasing bug the explicit
                // address exists to make impossible. The bounds checks stay because the alternative on a future
                // refactor is a silent no-op removal.
                flags[address.TriangleIndexWithinSubMesh] = true;
            }

            // A vertex is retained when at least one surviving triangle references it, or when it belongs to a
            // seam that a part welds onto. Keeping a referenced-by-nothing seam vertex would leave an orphan that
            // no triangle can ever reach.
            var seamBaseVertices = CollectBaseSeamVertices(context);
            var retained = new bool[baseMesh.VertexCount];
            MarkRetainedBaseVertices(baseMesh, removedBySubMesh, retained);

            for (var i = 0; i < seamBaseVertices.Count; i++)
            {
                var index = seamBaseVertices[i];
                if (index >= 0 && index < retained.Length) retained[index] = true;
            }

            var finalVertices = new List<FinalVertexSource>(baseMesh.VertexCount);
            var baseRemap = new int[baseMesh.VertexCount];
            for (var i = 0; i < baseRemap.Length; i++) baseRemap[i] = -1;

            for (var i = 0; i < baseMesh.VertexCount; i++)
            {
                if (!retained[i]) continue;
                baseRemap[i] = finalVertices.Count;
                finalVertices.Add(FinalVertexSource.ForBase(i));
            }

            // ---- Geometry: part side ----------------------------------------------------------------

            // The seam correspondence and the final UV layout are resolved before a single part vertex is emitted,
            // because the weld/split decision needs both: whether a pair can be a true weld is exactly the question
            // "do the same-name UV semantics of the two sides agree", and the semantic names and source channels
            // come from the layout.
            var seamResolutions = new Dictionary<string, SeamResolution>(StringComparer.Ordinal);
            var baseToAvatar = context.Base.Transforms.SourceToAvatarLocal();

            for (var p = 0; p < context.Parts.Count; p++)
            {
                var part = context.Parts[p];
                var resolution = SeamResolver.Resolve(
                    part,
                    baseMesh,
                    part.Transforms.SourceToAvatarLocal(),
                    baseToAvatar,
                    context.NumericPolicy,
                    issues);

                if (resolution == null)
                {
                    return PlanningResult.Failure(ValidationResult.Build(issues));
                }

                seamResolutions[part.PartId] = resolution;
            }

            var uvSources = UvResolver.CollectSources(context, issues);
            var uvLayout = UvResolver.Resolve(uvSources, issues);
            if (uvLayout == null) return PlanningResult.Failure(ValidationResult.Build(issues));

            // The one decision the Uv rule and this planner share. A pair is a true weld only when every shared UV
            // semantic agrees; otherwise the part seam vertex is preserved and emitted with its own attributes, so
            // validation can no longer approve a seam whose UV this planner would silently discard.
            var seamWelds = SeamWeldPlanner.Plan(context, seamResolutions, uvLayout);

            var vertexRemap = new Dictionary<string, int[]>(StringComparer.Ordinal) { { string.Empty, baseRemap } };

            // Weld contributions, accumulated per retained base vertex. Several parts may weld onto the same
            // base vertex, so the list is per base final index and ordered by canonical part order. A preserved
            // (split) pair contributes nothing: it keeps its own vertex and therefore its own values.
            var weldContributions = new Dictionary<int, List<WeldContribution>>();

            for (var p = 0; p < context.Parts.Count; p++)
            {
                var part = context.Parts[p];
                var resolution = seamResolutions[part.PartId];
                var weldPlan = seamWelds.For(part.PartId);

                var partRemap = new int[part.Mesh.VertexCount];
                for (var i = 0; i < partRemap.Length; i++) partRemap[i] = -1;

                // Welded seam vertices first, so that the "part seam is deleted" property is visible in the map as
                // a non-negative index that points at a base vertex rather than at a new part vertex.
                for (var m = 0; m < resolution.Matches.Count; m++)
                {
                    var match = resolution.Matches[m];

                    // A preserved pair is emitted like any other part vertex, in the ascending-index loop below.
                    if (weldPlan != null && weldPlan.IsSplit(match.PartVertex)) continue;

                    var baseFinal = baseRemap[match.BaseVertex];

                    // Every base seam vertex was force-retained above, so this cannot fail for a validated
                    // configuration. The check exists because the alternative on a future refactor is a part
                    // triangle silently pointing at final vertex -1, which Unity renders as garbage rather than
                    // reporting.
                    if (baseFinal < 0)
                    {
                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.SeamPositionMismatch,
                            ApaIssuePhase.Seam,
                            "Base seam vertex " + match.BaseVertex + " was not retained, so part seam vertex " +
                            match.PartVertex + " cannot weld onto it.",
                            part.PartId,
                            match.PartVertex,
                            match.BaseVertex,
                            detail: "baseVertex=" + match.BaseVertex + "; partVertex=" + match.PartVertex +
                                    "; reason=base-seam-vertex-not-retained"));
                        return PlanningResult.Failure(ValidationResult.Build(issues));
                    }

                    partRemap[match.PartVertex] = baseFinal;

                    // Record the weld so the assembler can resolve a semantic that exists only on the part.
                    // Without this the welded output vertex would be indistinguishable from an untouched base
                    // vertex, and a part-only UV layer would be written as the channel default.
                    if (!weldContributions.TryGetValue(baseFinal, out var contributions))
                    {
                        contributions = new List<WeldContribution>(1);
                        weldContributions.Add(baseFinal, contributions);
                    }

                    contributions.Add(new WeldContribution(part.PartId, match.PartVertex));
                }

                // Every remaining part vertex, which now includes the preserved seam vertices. Emitting them here
                // rather than in a separate pass keeps the output order the same rule it always was: a part's own
                // vertices in ascending source index, after the base range.
                for (var i = 0; i < part.Mesh.VertexCount; i++)
                {
                    if (partRemap[i] >= 0) continue;

                    var splitBase = -1;
                    var role = SeamVertexRole.None;
                    if (weldPlan != null && weldPlan.TryBaseVertexOf(i, out var pairedBase))
                    {
                        splitBase = pairedBase;
                        role = SeamVertexRole.Split;
                    }

                    partRemap[i] = finalVertices.Count;
                    finalVertices.Add(FinalVertexSource.ForPart(part.PartId, i, splitBase, role));
                }

                vertexRemap[part.PartId] = partRemap;
            }

            // Parts are already in canonical order, so contributions are too. Sorting again is cheap and makes
            // the order a property of this construction rather than of the caller's loop.
            var welds = BuildWeldSources(weldContributions, finalVertices);

            // ---- Material layout ---------------------------------------------------------------------

            var materialSources = MaterialResolver.CollectSources(context, issues);
            var materialLayout = MaterialResolver.Resolve(materialSources, issues);
            if (materialLayout == null) return PlanningResult.Failure(ValidationResult.Build(issues));

            // ---- Final bones and bind poses (R10) ----------------------------------------------------

            // Built after the final vertex list and remaps exist, because a bone table is only useful if every
            // retained vertex's weights can be resolved onto it. A failure here returns no plan, exactly like a
            // failed seam: the assembler is never handed a half-resolved skinning table.
            var boneTable = FinalBoneTableBuilder.Build(context, issues);
            if (boneTable != null)
            {
                BoneWeightValidator.ValidateFinalRemap(boneTable, finalVertices, context, issues);
            }

            // ---- Blend shapes (R11) ------------------------------------------------------------------

            var blendShapes = BlendShapeCatalog.Build(context, issues);
            if (blendShapes != null)
            {
                BlendShapeSeamValidator.Validate(context, blendShapes, seamResolutions, seamWelds, issues);
            }

            // ---- Submeshes --------------------------------------------------------------------------

            var subMeshes = BuildSubMeshes(
                context,
                materialLayout,
                vertexRemap,
                removedBySubMesh,
                issues);

            if (issues.Exists(i => i.IsBlocking))
            {
                return PlanningResult.Failure(ValidationResult.Build(issues));
            }

            var plan = new MeshAssemblyPlan(
                finalVertices,
                subMeshes,
                uvLayout,
                materialLayout,
                // The set is canonicalized already; the plan takes the address list and canonicalizes again so
                // that its own storage is independent of the caller's.
                removed.Addresses,
                vertexRemap,
                seamResolutions,
                welds,
                ResolveIndexFormat(context, finalVertices.Count),
                System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(issues, issue => !issue.IsBlocking)),
                boneTable,
                blendShapes ?? BlendShapeMergePlan.Empty,
                // Echo only: the group key identifies which target group this plan belongs to. It never changes a
                // decision here, and it is empty for the legacy single-target entry points.
                context.GroupKey,
                // The policy's one decision about a contested region, recorded as a value. Validation has already
                // proved every contested address is resolvable, so an unresolved overlap cannot reach a plan.
                RemovalRule.ResolveOwners(context.Parts),
                // The weld/split decision every emitted seam vertex was produced from, so a consumer can read why a
                // part vertex was kept instead of re-deriving the UV rule.
                seamWelds);

            return new PlanningResult(plan, ValidationResult.Build(issues));
        }

        /// <summary>
        /// Materializes the weld contribution map into ordered, immutable weld sources.
        /// </summary>
        /// <remarks>
        /// A base vertex that carries claims but whose final index is negative cannot happen for a validated
        /// configuration, because every welded base vertex is force-retained. It is skipped rather than
        /// asserted so that a future refactor degrades into a missing weld instead of an exception.
        /// </remarks>
        private static Dictionary<int, WeldAttributeSource> BuildWeldSources(
            Dictionary<int, List<WeldContribution>> contributions,
            List<FinalVertexSource> finalVertices)
        {
            var result = new Dictionary<int, WeldAttributeSource>(contributions.Count);

            // Iterating a Dictionary is not deterministic, so the keys are sorted before anything is emitted.
            // The values themselves are already ordered by the part loop, but the map's own order must not
            // leak into the plan.
            var keys = new List<int>(contributions.Keys);
            keys.Sort();

            for (var k = 0; k < keys.Count; k++)
            {
                var baseFinal = keys[k];
                if (baseFinal < 0 || baseFinal >= finalVertices.Count) continue;

                var list = contributions[baseFinal];
                list.Sort(CompareContributions);

                result.Add(baseFinal, new WeldAttributeSource(baseFinal, finalVertices[baseFinal].SourceVertex, list));
            }

            return result;
        }

        private static int CompareContributions(WeldContribution a, WeldContribution b)
        {
            var c = string.CompareOrdinal(a.PartId ?? string.Empty, b.PartId ?? string.Empty);
            if (c != 0) return c;
            return a.PartVertex.CompareTo(b.PartVertex);
        }

        /// <summary>
        /// Decides the output index format.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The output is never narrower than what it was built from. Unity switching a mesh from 32-bit to
        /// 16-bit indices is a silent down-conversion: it can truncate indices that other tools, other
        /// submeshes, or a later milestone still address with 32 bits, and the truncation only shows up as
        /// corrupted geometry much later.
        /// </para>
        /// <para>
        /// The rule is therefore a monotone widening: take the widest format among the base and every part, then
        /// widen further if the merged vertex count needs it. Working from the same declared mesh here means the
        /// decision is a plan output rather than something the assembler re-derives, so a reviewer can read it
        /// off the plan.
        /// </para>
        /// </remarks>
        private static IndexFormat ResolveIndexFormat(ValidationContext context, int outputVertexCount)
        {
            var format = IndexFormat.UInt16;

            if (context.Base?.Mesh != null && context.Base.Mesh.IndexFormat == IndexFormat.UInt32)
            {
                format = IndexFormat.UInt32;
            }

            for (var i = 0; i < context.Parts.Count; i++)
            {
                var mesh = context.Parts[i]?.Mesh;
                if (mesh != null && mesh.IndexFormat == IndexFormat.UInt32)
                {
                    format = IndexFormat.UInt32;
                    break;
                }
            }

            // The vertex count can force a widening even when every source was 16-bit, because merging parts
            // adds vertices. The declared source format can never force a narrowing.
            if (outputVertexCount > ApaMeshLimits.UInt16VertexLimit) format = IndexFormat.UInt32;

            return format;
        }

        /// <summary>
        /// Collects the union of every part's removal set, ascending, de-duplicated, and in canonical
        /// (submesh, triangle) order. Overlaps were already reported by the removal rule, so this is only
        /// reached for a valid configuration.
        /// </summary>
        private static RemovedTriangleAddressSet CollectRemovedTriangles(ValidationContext context)
        {
            var addresses = new List<RemovedTriangleAddress>();
            for (var p = 0; p < context.Parts.Count; p++)
            {
                var removed = context.Parts[p].RemovedTriangles;
                if (removed == null) continue;
                for (var i = 0; i < removed.Count; i++) addresses.Add(removed[i]);
            }

            // The set canonicalizes: ascending order and no duplicates. Two parts that declare the same address
            // are already a reported error, so de-duplication here cannot hide a conflict.
            return new RemovedTriangleAddressSet(addresses);
        }

        private static List<int> CollectBaseSeamVertices(ValidationContext context)
        {
            var seen = new SortedSet<int>();
            for (var p = 0; p < context.Parts.Count; p++)
            {
                var seam = context.Parts[p].Seam;
                if (seam?.Base == null) continue;
                var indices = seam.Base.VertexIndices;
                for (var i = 0; i < indices.Length; i++) seen.Add(indices[i]);
            }

            return new List<int>(seen);
        }

        /// <summary>
        /// Marks every base vertex still referenced by a surviving triangle.
        /// </summary>
        /// <remarks>
        /// Only triangle topologies participate in removal. Points and lines are carried through untouched by
        /// the current submesh planner, so their vertices are marked retained conservatively; dropping them
        /// would silently delete debug or collision geometry that the author deliberately added.
        /// </remarks>
        private static void MarkRetainedBaseVertices(
            MeshSnapshot baseMesh,
            bool[][] removedBySubMesh,
            bool[] retained)
        {
            for (var subMesh = 0; subMesh < baseMesh.SubMeshCount; subMesh++)
            {
                var indices = baseMesh.SubMeshes[subMesh];
                if (indices == null) continue;

                var topology = subMesh < baseMesh.Topologies.Length
                    ? baseMesh.Topologies[subMesh]
                    : MeshTopology.Triangles;

                if (topology != MeshTopology.Triangles)
                {
                    for (var i = 0; i < indices.Length; i++)
                    {
                        if (indices[i] >= 0 && indices[i] < retained.Length) retained[indices[i]] = true;
                    }

                    continue;
                }

                var removedFlags = subMesh < removedBySubMesh.Length ? removedBySubMesh[subMesh] : null;
                var triangleCount = indices.Length / ApaMeshLimits.TriangleStride;

                for (var t = 0; t < triangleCount; t++)
                {
                    // The removal flag is per submesh. Reading it per submesh is the whole point of the
                    // explicit address: a triangle in submesh 1 is only removed when submesh 1 says so.
                    if (removedFlags != null && t < removedFlags.Length && removedFlags[t]) continue;

                    for (var k = 0; k < ApaMeshLimits.TriangleStride; k++)
                    {
                        var vertex = indices[t * ApaMeshLimits.TriangleStride + k];
                        if (vertex >= 0 && vertex < retained.Length) retained[vertex] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Builds the output submeshes by remapping surviving base triangles and part triangles into the final
        /// slot layout.
        /// </summary>
        private static List<PlannedSubMesh> BuildSubMeshes(
            ValidationContext context,
            MaterialLayout materialLayout,
            IReadOnlyDictionary<string, int[]> vertexRemap,
            bool[][] removedBySubMesh,
            List<ValidationIssue> issues)
        {
            var buckets = new List<int>[materialLayout.SlotCount];
            for (var i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();

            var baseMesh = context.Base.Mesh;
            var baseSubMeshToSlot = MapSubMeshesToSlots(materialLayout, string.Empty, baseMesh);
            var baseRemap = vertexRemap[string.Empty];

            // Base geometry first, in ascending submesh and then triangle order, so that a body's own triangles
            // keep their relative order in the output.
            for (var subMesh = 0; subMesh < baseMesh.SubMeshCount; subMesh++)
            {
                var indices = baseMesh.SubMeshes[subMesh];
                if (indices == null) continue;

                var topology = subMesh < baseMesh.Topologies.Length
                    ? baseMesh.Topologies[subMesh]
                    : MeshTopology.Triangles;

                if (topology != MeshTopology.Triangles)
                {
                    // Non-triangle submeshes are not merged by this milestone. Report rather than silently drop,
                    // because dropping a collision or point submesh would change the avatar's behaviour
                    // invisibly.
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.UnsupportedMeshAttribute,
                        ApaIssuePhase.Attributes,
                        "Submesh " + subMesh + " on the base body uses topology " + topology +
                        ". Only triangle submeshes are merged in this milestone.",
                        detail: "submesh=" + subMesh + "; topology=" + topology));
                    continue;
                }

                var slot = SlotForSubMesh(baseSubMeshToSlot, subMesh);
                if (slot < 0)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.SubMeshWithoutMaterialSlot,
                        ApaIssuePhase.Materials,
                        "Base submesh " + subMesh + " has no final material slot, so its triangles cannot be " +
                        "emitted. The material layout must provide a slot for every source submesh.",
                        detail: "submesh=" + subMesh + "; reason=base-submesh-without-slot"));
                    continue;
                }

                var removedFlags = subMesh < removedBySubMesh.Length ? removedBySubMesh[subMesh] : null;
                var triangleCount = indices.Length / ApaMeshLimits.TriangleStride;

                for (var t = 0; t < triangleCount; t++)
                {
                    if (removedFlags != null && t < removedFlags.Length && removedFlags[t]) continue;
                    AppendRemappedTriangle(indices, t, baseRemap, buckets[slot], issues, string.Empty, subMesh, t);
                }
            }

            // Part geometry, in part order then submesh then triangle order.
            for (var p = 0; p < context.Parts.Count; p++)
            {
                var part = context.Parts[p];
                var partRemap = vertexRemap[part.PartId];
                var partSlotMap = MapSubMeshesToSlots(materialLayout, part.PartId, part.Mesh);

                for (var subMesh = 0; subMesh < part.Mesh.SubMeshCount; subMesh++)
                {
                    var indices = part.Mesh.SubMeshes[subMesh];
                    if (indices == null) continue;

                    var topology = subMesh < part.Mesh.Topologies.Length
                        ? part.Mesh.Topologies[subMesh]
                        : MeshTopology.Triangles;

                    if (topology != MeshTopology.Triangles)
                    {
                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.UnsupportedMeshAttribute,
                            ApaIssuePhase.Attributes,
                            "Submesh " + subMesh + " on part '" + part.PartId + "' uses topology " + topology +
                            ". Only triangle submeshes are merged in this milestone.",
                            part.PartId,
                            subMesh,
                            detail: "submesh=" + subMesh + "; topology=" + topology));
                        continue;
                    }

                    var slot = SlotForSubMesh(partSlotMap, subMesh);
                    if (slot < 0)
                    {
                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.SubMeshWithoutMaterialSlot,
                            ApaIssuePhase.Materials,
                            "Part submesh " + subMesh + " has no final material slot, so its triangles cannot " +
                            "be emitted. The material layout must provide a slot for every source submesh.",
                            part.PartId,
                            subMesh,
                            detail: "part=" + part.PartId + "; submesh=" + subMesh +
                                    "; reason=part-submesh-without-slot"));
                        continue;
                    }

                    var triangleCount = indices.Length / ApaMeshLimits.TriangleStride;
                    for (var t = 0; t < triangleCount; t++)
                    {
                        AppendRemappedTriangle(
                            indices, t, partRemap, buckets[slot], issues, part.PartId, subMesh, t);
                    }
                }
            }

            var result = new List<PlannedSubMesh>(materialLayout.SlotCount);
            for (var i = 0; i < materialLayout.SlotCount; i++)
            {
                result.Add(new PlannedSubMesh(
                    i,
                    materialLayout.Slots[i].Semantic,
                    materialLayout.Slots[i].Material,
                    buckets[i]));
            }

            return result;
        }

        private static Dictionary<string, int> MapSubMeshesToSlots(
            MaterialLayout layout,
            string partId,
            MeshSnapshot mesh)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var subMesh = 0; subMesh < mesh.SubMeshCount; subMesh++)
            {
                map[SubMeshKey(subMesh)] = layout.FindSlotFor(partId, subMesh);
            }

            return map;
        }

        private static int SlotForSubMesh(Dictionary<string, int> map, int subMesh)
        {
            return map.TryGetValue(SubMeshKey(subMesh), out var value) ? value : -1;
        }

        private static string SubMeshKey(int subMesh)
        {
            return "submesh#" + subMesh;
        }

        /// <summary>
        /// Appends one triangle through the vertex remap, rejecting any triangle that would reference a removed
        /// vertex or collapse to zero area.
        /// </summary>
        /// <remarks>
        /// Both checks are about turning silent corruption into a diagnostic. A triangle that references a
        /// removed vertex means the removal set and the mesh disagree; a triangle that collapses means the weld
        /// folded geometry onto itself. Neither should ever be written to the output and hoped for.
        /// </remarks>
        private static void AppendRemappedTriangle(
            int[] indices,
            int triangle,
            int[] remap,
            List<int> destination,
            List<ValidationIssue> issues,
            string partId,
            int subMesh,
            int triangleInSubMesh)
        {
            var offset = triangle * ApaMeshLimits.TriangleStride;
            var a = RemapVertex(indices[offset], remap);
            var b = RemapVertex(indices[offset + 1], remap);
            var c = RemapVertex(indices[offset + 2], remap);

            if (a < 0 || b < 0 || c < 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.RemovalIndexOutOfRange,
                    ApaIssuePhase.Removal,
                    "Triangle " + triangleInSubMesh + " in submesh " + subMesh + " references a vertex that was " +
                    "removed, so it cannot be rebuilt.",
                    partId,
                    triangleInSubMesh,
                    subMesh,
                    detail: "submesh=" + subMesh + "; triangle=" + triangleInSubMesh +
                            "; vertices=" + indices[offset] + "," + indices[offset + 1] + "," + indices[offset + 2]));
                return;
            }

            if (a == b || b == c || a == c)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.DegenerateOutputTriangle,
                    ApaIssuePhase.Assembly,
                    "Triangle " + triangleInSubMesh + " in submesh " + subMesh +
                    " collapses to a degenerate triangle after the vertex remap. Check the seam and removal " +
                    "region for overlap.",
                    partId,
                    triangleInSubMesh,
                    subMesh,
                    detail: "submesh=" + subMesh + "; triangle=" + triangleInSubMesh +
                            "; finalVertices=" + a + "," + b + "," + c));
                return;
            }

            destination.Add(a);
            destination.Add(b);
            destination.Add(c);
        }

        private static int RemapVertex(int sourceVertex, int[] remap)
        {
            if (remap == null) return -1;
            if (sourceVertex < 0 || sourceVertex >= remap.Length) return -1;
            return remap[sourceVertex];
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// One source's declaration that a semantic is served by a particular submesh and material.
    /// </summary>
    public struct MaterialSemanticSource
    {
        /// <summary>Normalized semantic name.</summary>
        public string Semantic;

        /// <summary>Submesh index in the source mesh.</summary>
        public int SourceSubMesh;

        /// <summary>The material asset for this slot, or null when the renderer left it unassigned.</summary>
        public Material Material;

        /// <summary>The part the declaration came from, or an empty string for the base.</summary>
        public string PartId;

        /// <summary>The conflict policy to apply.</summary>
        public ApaMaterialPolicyMode Policy;

        /// <summary>Creates a declaration.</summary>
        public MaterialSemanticSource(
            string semantic,
            int sourceSubMesh,
            Material material,
            string partId,
            ApaMaterialPolicyMode policy)
        {
            Semantic = semantic;
            SourceSubMesh = sourceSubMesh;
            Material = material;
            PartId = partId ?? string.Empty;
            Policy = policy;
        }
    }

    /// <summary>
    /// The final material slot list and the map from each source submesh to its output slot.
    /// </summary>
    public sealed class MaterialLayout
    {
        /// <summary>Final slots, in output order.</summary>
        public IReadOnlyList<MaterialSlotAssignment> Slots { get; }

        /// <summary>Number of final submeshes and material slots.</summary>
        public int SlotCount => Slots.Count;

        /// <summary>Creates a layout.</summary>
        public MaterialLayout(IReadOnlyList<MaterialSlotAssignment> slots)
        {
            var copy = new MaterialSlotAssignment[slots?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = slots[i];
            Slots = System.Array.AsReadOnly(copy);
        }

        /// <summary>The final material list, in slot order. Suitable for a renderer's sharedMaterials array.</summary>
        public Material[] ToMaterialArray()
        {
            var result = new Material[Slots.Count];
            for (var i = 0; i < Slots.Count; i++) result[i] = Slots[i].Material;
            return result;
        }

        /// <summary>
        /// Finds the final slot index that a given source submesh maps to, or -1 when the source submesh does
        /// not contribute to this part's layout.
        /// </summary>
        public int FindSlotFor(string partId, int sourceSubMesh)
        {
            for (var i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].ContainsSource(partId, sourceSubMesh)) return Slots[i].OutputSlot;
            }

            return -1;
        }

        /// <summary>Returns the semantic occupying a final slot.</summary>
        public string SemanticAt(int slot)
        {
            if (slot < 0 || slot >= Slots.Count) return string.Empty;
            return Slots[slot].Semantic;
        }

        /// <summary>
        /// How many final slots carry the given semantic.
        /// </summary>
        /// <remarks>
        /// More than one is legal only as the result of an explicit <see cref="ApaMaterialPolicyMode.KeepPart"/>
        /// or <see cref="ApaMaterialPolicyMode.ForceNew"/> declaration. A consumer that needs to tell a
        /// deliberate split from a bug uses <see cref="IsSemanticSplit"/> for the boolean question and this for
        /// the count.
        /// </remarks>
        public int SemanticCount(string semantic)
        {
            if (!ApaSemanticName.IsValid(ApaSemanticName.Normalize(semantic))) return 0;

            var count = 0;
            for (var i = 0; i < Slots.Count; i++)
            {
                if (ApaSemanticName.AreEqual(Slots[i].Semantic, semantic)) count++;
            }

            return count;
        }

        /// <summary>True when the semantic occupies more than one final slot, i.e. it was deliberately split.</summary>
        public bool IsSemanticSplit(string semantic)
        {
            return SemanticCount(semantic) > 1;
        }

        /// <summary>
        /// True when the semantic is claimed by at least one slot a part took by policy instead of merging with
        /// the base: a <c>KeepPart</c>/<c>ForceNew</c> slot, or the fallback slot <c>UseTarget</c> creates when
        /// the base has no such semantic.
        /// </summary>
        /// <remarks>
        /// Whether an <c>Auto</c> contribution may anchor on such a slot is decided by the resolver and is not
        /// this flag: a <c>KeepPart</c>/<c>ForceNew</c> slot blocks <c>Auto</c> with
        /// <c>reason=no-auto-anchor</c>, while the <c>UseTarget</c> fallback slot is the part anchor, because
        /// <c>UseTarget</c> explicitly declines a private slot and the fallback is the only slot the semantic
        /// has. Use <see cref="IsSemanticSplit"/> for the boolean "this semantic occupies more than one slot".
        /// </remarks>
        public bool HasExplicitlySeparatedSlot(string semantic)
        {
            for (var i = 0; i < Slots.Count; i++)
            {
                if (!Slots[i].IsExplicitlySeparated) continue;
                if (ApaSemanticName.AreEqual(Slots[i].Semantic, semantic)) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// One final material slot.
    /// </summary>
    public sealed class MaterialSlotAssignment
    {
        private readonly Dictionary<string, int> _sourceSubMeshes;
        private readonly ReadOnlyDictionary<string, int> _sourceSubMeshView;
        private readonly List<string> _sourceOrder;

        /// <summary>Normalized semantic name for this slot.</summary>
        public string Semantic { get; }

        /// <summary>Final output slot and submesh index.</summary>
        public int OutputSlot { get; }

        /// <summary>The material asset used for this slot. Never mutated by the assembler.</summary>
        public Material Material { get; }

        /// <summary>Source submesh indices feeding this slot, keyed by part id (empty string for the base).</summary>
        public IReadOnlyDictionary<string, int> SourceSubMeshes { get; }

        /// <summary>
        /// The part that first contributed to this slot, or the empty string when no part did (a base slot, or the
        /// base contribution of a mixed slot). This is the slot's anchor: contributions are resolved in canonical
        /// part order, so the first contributor is also the lowest-ordered contributor.
        /// </summary>
        /// <remarks>
        /// Recorded as an explicit order rather than read out of <see cref="SourceSubMeshes"/>, because a
        /// dictionary's enumeration order is not a contract and a diagnostic that names "the anchor part" must not
        /// depend on one.
        /// </remarks>
        public string FirstSourcePartId => _sourceOrder.Count > 0 ? _sourceOrder[0] : string.Empty;

        /// <summary>
        /// True when this slot belongs to the base body. A <see cref="ApaMaterialPolicyMode.UseTarget"/>
        /// contribution may only merge into a base slot.
        /// </summary>
        public bool IsBaseSlot { get; }

        /// <summary>
        /// True when a part took this slot by policy rather than by merging with the base: with
        /// <see cref="ApaMaterialPolicyMode.KeepPart"/> or <see cref="ApaMaterialPolicyMode.ForceNew"/>, or when
        /// the slot exists because <see cref="ApaMaterialPolicyMode.UseTarget"/> found no base semantic.
        /// </summary>
        /// <remarks>
        /// The flag marks "the base does not carry this semantic in this slot", which is what
        /// <see cref="MaterialLayout.HasExplicitlySeparatedSlot"/> and <see cref="MaterialLayout.IsSemanticSplit"/>
        /// report. It is not by itself the <c>Auto</c>-anchor decision: see
        /// <see cref="MaterialLayout.HasExplicitlySeparatedSlot"/> for which of the two kinds blocks an
        /// <c>Auto</c> contribution.
        /// </remarks>
        public bool IsExplicitlySeparated { get; }

        /// <summary>Creates an assignment.</summary>
        public MaterialSlotAssignment(
            string semantic,
            int outputSlot,
            Material material,
            IReadOnlyDictionary<string, int> sourceSubMeshes,
            bool isBaseSlot = false,
            bool isExplicitlySeparated = false)
        {
            Semantic = semantic ?? string.Empty;
            OutputSlot = outputSlot;
            Material = material;
            _sourceSubMeshes = new Dictionary<string, int>(System.StringComparer.Ordinal);
            if (sourceSubMeshes != null)
            {
                foreach (var pair in sourceSubMeshes)
                {
                    _sourceSubMeshes[pair.Key ?? string.Empty] = pair.Value;
                }
            }

            // The constructor's dictionary has no order of its own, so the seed order is ordinal: a slot built
            // directly from a map still has a deterministic anchor instead of an enumeration-order one.
            _sourceOrder = new List<string>(_sourceSubMeshes.Keys);
            _sourceOrder.Sort(StringComparer.Ordinal);

            _sourceSubMeshView = new ReadOnlyDictionary<string, int>(_sourceSubMeshes);
            SourceSubMeshes = _sourceSubMeshView;
            IsBaseSlot = isBaseSlot;
            IsExplicitlySeparated = isExplicitlySeparated;
        }

        /// <summary>Adds or replaces a source mapping while the resolver is constructing the layout.</summary>
        internal void AddSource(string partId, int sourceSubMesh)
        {
            var key = partId ?? string.Empty;
            if (!_sourceSubMeshes.ContainsKey(key)) _sourceOrder.Add(key);
            _sourceSubMeshes[key] = sourceSubMesh;
        }

        /// <summary>True when this slot receives geometry from the given source submesh.</summary>
        public bool ContainsSource(string partId, int sourceSubMesh)
        {
            return SourceSubMeshes.TryGetValue(partId ?? string.Empty, out var subMesh) && subMesh == sourceSubMesh;
        }
    }

    /// <summary>
    /// Resolves per-source material slot declarations into one deterministic final slot list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Material slots merge by semantic name, which is deliberately independent of the material asset name so
    /// that renaming or versioning a material cannot break a part. The conflict policies are:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>Auto</c>: same semantic and same asset merge; same semantic and different assets is <c>APA009</c>.</description></item>
    /// <item><description><c>UseTarget</c>: the part geometry uses the <i>base target's</i> slot material. When the base has no slot for the semantic, the part's material gets a new slot.</description></item>
    /// <item><description><c>KeepPart</c>: the part's material gets its own slot, even when the semantic matches.</description></item>
    /// <item><description><c>ForceNew</c>: the part's material always gets its own slot.</description></item>
    /// </list>
    /// <para>
    /// <b>Anchors are base-first and never chained.</b> A comparison is always made against one canonical
    /// reference: the base slot when the base declares the semantic, otherwise the lowest-ordered part that is
    /// not explicitly separated. Comparing contributors in a chain would accept two parts that both agree with a
    /// middle contributor but disagree with each other.
    /// </para>
    /// <para>
    /// <b><c>UseTarget</c> means the base target only.</b> It never redirects a part into another part's slot:
    /// when the base lacks the semantic, the part keeps its own material in a new slot (specification 43.5), and
    /// a later part is compared against that slot as a part anchor rather than silently inheriting it.
    /// </para>
    /// <para>
    /// The plugin never overwrites a base material. When a policy creates an additional slot it is because the
    /// part genuinely needs one, and the addition is reported as informational so the author can see it.
    /// </para>
    /// </remarks>
    public static class MaterialResolver
    {
        /// <summary>Builds the final material layout.</summary>
        public static MaterialLayout Resolve(IReadOnlyList<MaterialSemanticSource> sources, List<ValidationIssue> issues)
        {
            var slots = new List<MaterialSlotAssignment>();

            // semantic -> the base slot that carries it. Only a base contribution may populate this map, which
            // is what makes UseTarget mean "the base target's material" instead of "whoever claimed it first".
            var baseSlotBySemantic = new Dictionary<string, int>(System.StringComparer.Ordinal);

            // semantic -> the part slot an Auto contribution may merge into: the lowest-ordered non-separated
            // part slot for that semantic.
            var partAnchorBySemantic = new Dictionary<string, int>(System.StringComparer.Ordinal);

            // Semantics that have at least one explicitly separated part slot (KeepPart/ForceNew/UseTarget
            // without a base semantic). Auto never merges into one of those.
            var separated = new HashSet<string>(System.StringComparer.Ordinal);

            // Base first, in base order, so that a base semantic always keeps its slot numbering and a mismatch
            // is reported against the base's own material rather than the (arbitrary) last one seen.
            var orderedSources = new List<MaterialSemanticSource>(sources);
            for (var i = 0; i < orderedSources.Count; i++)
            {
                var source = orderedSources[i];
                var semantic = ApaSemanticName.Normalize(source.Semantic);

                if (!ApaSemanticName.IsValid(semantic))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSemanticName,
                        ApaIssuePhase.Materials,
                        "A material semantic name is null, empty, or whitespace. " + ApaSemanticName.RuleDescription + ".",
                        source.PartId,
                        source.SourceSubMesh,
                        detail: "semantic='" + (source.Semantic ?? string.Empty) + "'"));
                    continue;
                }

                var isBase = string.IsNullOrEmpty(source.PartId);
                var wantsOwnSlot = !isBase && (source.Policy == ApaMaterialPolicyMode.KeepPart
                                               || source.Policy == ApaMaterialPolicyMode.ForceNew);

                if (isBase)
                {
                    var slotIndex = AddSlot(slots, semantic, source.Material, source.PartId, source.SourceSubMesh, true, false);
                    baseSlotBySemantic[semantic] = slotIndex;
                    continue;
                }

                if (wantsOwnSlot)
                {
                    var ownSlot = AddSlot(slots, semantic, source.Material, source.PartId, source.SourceSubMesh, false, true);
                    var wasSplit = separated.Contains(semantic)
                                   || baseSlotBySemantic.ContainsKey(semantic)
                                   || partAnchorBySemantic.ContainsKey(semantic);
                    separated.Add(semantic);

                    issues.Add(ValidationIssue.Info(
                        ApaErrorCode.MaterialSemanticConflict,
                        ApaIssuePhase.Materials,
                        "Material semantic '" + semantic + "' on part '" + source.PartId +
                        "' was given an additional slot " + ownSlot + " by policy " + source.Policy +
                        (wasSplit ? ", so the semantic now occupies more than one final slot." : "."),
                        source.PartId,
                        source.SourceSubMesh,
                        ownSlot,
                        detail: "semantic=" + semantic + "; policy=" + source.Policy + "; finalSlot=" + ownSlot +
                                (wasSplit ? "; reason=semantic-split-by-policy" : "; reason=explicit-part-slot")));
                    continue;
                }

                if (source.Policy == ApaMaterialPolicyMode.UseTarget)
                {
                    if (baseSlotBySemantic.TryGetValue(semantic, out var baseSlot))
                    {
                        // Redirect the part geometry into the base slot. The base material wins by definition.
                        slots[baseSlot].AddSource(source.PartId, source.SourceSubMesh);
                        issues.Add(ValidationIssue.Info(
                            ApaErrorCode.MaterialSemanticConflict,
                            ApaIssuePhase.Materials,
                            "Material semantic '" + semantic + "' on part '" + source.PartId +
                            "' uses the target slot's material as requested by policy UseTarget.",
                            source.PartId,
                            source.SourceSubMesh,
                            baseSlot,
                            detail: "semantic=" + semantic + "; policy=UseTarget; finalSlot=" + baseSlot +
                                    "; reason=use-target-merged"));
                        continue;
                    }

                    // The base has no such semantic, so there is no target slot to use. Per specification 43.5
                    // the part keeps its own material in a new slot instead of being redirected into another
                    // part's slot.
                    if (partAnchorBySemantic.TryGetValue(semantic, out var existingAnchor))
                    {
                        // A part slot already carries the semantic. Merge only when the assets are the same
                        // asset; otherwise the two parts disagree and that is APA009, not a silent winner.
                        if (AreSameMaterial(slots[existingAnchor].Material, source.Material))
                        {
                            slots[existingAnchor].AddSource(source.PartId, source.SourceSubMesh);
                            issues.Add(ValidationIssue.Info(
                                ApaErrorCode.MaterialSemanticConflict,
                                ApaIssuePhase.Materials,
                                "Material semantic '" + semantic + "' on part '" + source.PartId +
                                "' uses policy UseTarget and the base has no such semantic, so it joins the " +
                                "identical part slot " + existingAnchor + ".",
                                source.PartId,
                                source.SourceSubMesh,
                                existingAnchor,
                                detail: "semantic=" + semantic + "; policy=UseTarget; finalSlot=" + existingAnchor +
                                        "; reason=use-target-without-base-semantic"));
                            continue;
                        }

                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.MaterialSemanticConflict,
                            ApaIssuePhase.Materials,
                            "Material semantic '" + semantic + "' is claimed by part '" + source.PartId +
                            "' with policy UseTarget, but the base has no such semantic and part '" +
                            DescribeOwner(slots[existingAnchor]) + "' already contributes a different material " +
                            "asset to it. UseTarget can only use the base target's material; make the assets " +
                            "match, or declare the policy the later part actually intends.",
                            source.PartId,
                            source.SourceSubMesh,
                            existingAnchor,
                            detail: "semantic=" + semantic +
                                    "; anchorMaterial=" + Describe(slots[existingAnchor].Material) +
                                    "; partMaterial=" + Describe(source.Material) +
                                    "; policy=UseTarget; reason=part-part-material-conflict"));
                        continue;
                    }

                    var created = AddSlot(slots, semantic, source.Material, source.PartId, source.SourceSubMesh, false, true);
                    partAnchorBySemantic[semantic] = created;
                    issues.Add(ValidationIssue.Info(
                        ApaErrorCode.MaterialSemanticConflict,
                        ApaIssuePhase.Materials,
                        "Material semantic '" + semantic + "' on part '" + source.PartId +
                        "' uses policy UseTarget, but the base has no such semantic, so a new slot " + created +
                        " carrying the part's material was created.",
                        source.PartId,
                        source.SourceSubMesh,
                        created,
                        detail: "semantic=" + semantic + "; policy=UseTarget; finalSlot=" + created +
                                "; reason=use-target-without-base-semantic"));
                    continue;
                }

                // Auto (or an unset policy, which serializes as Auto).
                if (baseSlotBySemantic.TryGetValue(semantic, out var autoBaseSlot))
                {
                    if (AreSameMaterial(slots[autoBaseSlot].Material, source.Material))
                    {
                        slots[autoBaseSlot].AddSource(source.PartId, source.SourceSubMesh);
                        continue;
                    }

                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.MaterialSemanticConflict,
                        ApaIssuePhase.Materials,
                        "Material semantic '" + semantic + "' resolves to different material assets on the base " +
                        "and on part '" + source.PartId + "'. Choose UseTarget, KeepPart, or ForceNew on the part " +
                        "to declare the intended behaviour.",
                        source.PartId,
                        source.SourceSubMesh,
                        autoBaseSlot,
                        detail: "semantic=" + semantic +
                                "; baseMaterial=" + Describe(slots[autoBaseSlot].Material) +
                                "; partMaterial=" + Describe(source.Material) +
                                "; policy=Auto; reason=base-part-material-conflict"));
                    continue;
                }

                if (separated.Contains(semantic))
                {
                    // The semantic has already been deliberately split from the base by another part, so Auto
                    // has nothing legitimate to merge into. Merging into the separated slot would silently
                    // override that author's decision, so it blocks and names the separating part.
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.MaterialSemanticConflict,
                        ApaIssuePhase.Materials,
                        "Material semantic '" + semantic + "' on part '" + source.PartId +
                        "' uses policy Auto, but the base has no such semantic and another part already gave " +
                        "the semantic its own slot. Auto merges by semantic, and the semantic has already been " +
                        "deliberately split, so there is no anchor to merge into. Make this part explicit too " +
                        "(UseTarget, KeepPart, or ForceNew).",
                        source.PartId,
                        source.SourceSubMesh,
                        detail: "semantic=" + semantic + "; policy=Auto; reason=no-auto-anchor"));
                    continue;
                }

                if (partAnchorBySemantic.TryGetValue(semantic, out var partAnchor))
                {
                    if (AreSameMaterial(slots[partAnchor].Material, source.Material))
                    {
                        slots[partAnchor].AddSource(source.PartId, source.SourceSubMesh);
                        continue;
                    }

                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.MaterialSemanticConflict,
                        ApaIssuePhase.Materials,
                        "Material semantic '" + semantic + "' resolves to different material assets on part '" +
                        DescribeOwner(slots[partAnchor]) + "' and on part '" + source.PartId +
                        "'. The base does not declare the semantic, so the anchor is the lowest-ordered " +
                        "contributing part; the two parts disagree and there is no defined priority between " +
                        "them. Make the assets match, or declare UseTarget, KeepPart, or ForceNew.",
                        source.PartId,
                        source.SourceSubMesh,
                        partAnchor,
                        detail: "semantic=" + semantic +
                                "; partAMaterial=" + Describe(slots[partAnchor].Material) +
                                "; partBMaterial=" + Describe(source.Material) +
                                "; anchorPart=" + DescribeOwner(slots[partAnchor]) +
                                "; policy=Auto; reason=part-part-material-conflict"));
                    continue;
                }

                var anchorSlot = AddSlot(slots, semantic, source.Material, source.PartId, source.SourceSubMesh, false, false);
                partAnchorBySemantic[semantic] = anchorSlot;
            }

            return new MaterialLayout(slots);
        }

        private static int AddSlot(
            List<MaterialSlotAssignment> slots,
            string semantic,
            Material material,
            string partId,
            int sourceSubMesh,
            bool isBaseSlot,
            bool isExplicitlySeparated)
        {
            var sources = new Dictionary<string, int> { { partId ?? string.Empty, sourceSubMesh } };
            slots.Add(new MaterialSlotAssignment(
                semantic,
                slots.Count,
                material,
                sources,
                isBaseSlot,
                isExplicitlySeparated));
            return slots.Count - 1;
        }

        /// <summary>
        /// The part that owns a slot, or "(base)" for a base slot. Used so a conflict message names the anchor.
        /// </summary>
        /// <remarks>
        /// The owner is the slot's first contributor, recorded explicitly by
        /// <see cref="MaterialSlotAssignment.AddSource"/> in canonical part order. Reading a dictionary's first
        /// non-empty key instead would make the name depend on an enumeration order that is not a contract — and a
        /// diagnostic that names the anchor part is exactly the kind of value a later consumer starts trusting.
        /// </remarks>
        private static string DescribeOwner(MaterialSlotAssignment slot)
        {
            if (slot == null) return "(unknown)";
            if (slot.IsBaseSlot) return "(base)";

            var first = slot.FirstSourcePartId;
            if (!string.IsNullOrEmpty(first)) return first;

            return "(unknown)";
        }

        /// <summary>
        /// Compares two material references. Reference equality is used deliberately: two distinct material
        /// assets that happen to have identical settings are still two assets, and treating them as one would
        /// silently discard one of them.
        /// </summary>
        public static bool AreSameMaterial(Material a, Material b)
        {
            return ReferenceEquals(a, b);
        }

        private static string Describe(Material material)
        {
            return material == null ? "(none)" : material.name;
        }

        /// <summary>
        /// Builds the source declarations for the base body and every part, in the fixed ordering that makes the
        /// final slot assignment deterministic.
        /// </summary>
        public static List<MaterialSemanticSource> CollectSources(ValidationContext context, List<ValidationIssue> issues)
        {
            var sources = new List<MaterialSemanticSource>();

            if (context.Base != null && context.Base.Mesh != null)
            {
                AppendSource(
                    sources,
                    context.Base.Mesh,
                    context.Base.ExpectedMaterialSemantics,
                    context.Base.RendererMaterials,
                    string.Empty,
                    issues);
            }

            // Base first, then parts in canonical identity order, so that final slot numbering is deterministic
            // and a conflict is always reported against the base rather than against an arbitrary part.
            var parts = ValidationContext.SortParts(context.Parts);

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part?.Mesh == null) continue;

                AppendSource(
                    sources,
                    part.Mesh,
                    part.MaterialSemantics,
                    part.RendererMaterials,
                    part.PartId,
                    issues);
            }

            return sources;
        }

        private static void AppendSource(
            List<MaterialSemanticSource> sources,
            MeshSnapshot mesh,
            IReadOnlyList<ApaMaterialSlotSemantic> declared,
            IReadOnlyList<Material> rendererMaterials,
            string partId,
            List<ValidationIssue> issues)
        {
            if (declared != null && declared.Count > 0)
            {
                for (var i = 0; i < declared.Count; i++)
                {
                    var entry = declared[i];
                    if (entry == null) continue;
                    sources.Add(new MaterialSemanticSource(
                        entry.Semantic,
                        entry.SourceSubMesh,
                        entry.Material,
                        partId,
                        entry.Policy));
                }

                return;
            }

            // No explicit declaration: derive one semantic per submesh from the renderer's material order. This
            // keeps a simple part working without authoring configuration. The derived name is the material
            // asset name only because there is nothing else to derive from at this point; it is a capture-time
            // convenience, not a matching rule, and the authoring window replaces it with an explicit semantic.
            var subMeshCount = mesh.SubMeshCount;
            for (var i = 0; i < subMeshCount; i++)
            {
                Material material = i < rendererMaterials.Count ? rendererMaterials[i] : null;
                var semantic = material != null ? material.name : "SubMesh" + i;
                sources.Add(new MaterialSemanticSource(semantic, i, material, partId, ApaMaterialPolicyMode.Auto));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// What happened to one explicit seam pair in the final vertex plan.
    /// </summary>
    /// <remarks>
    /// The decision is about the <i>vertex</i>, not about a single attribute: a vertex is atomic in Unity, so a
    /// pair whose UVs cannot be reconciled keeps the whole vertex rather than only the disagreeing channel.
    /// </remarks>
    public enum SeamPairDisposition
    {
        /// <summary>
        /// The pair is a true weld: the part seam vertex is deleted and its triangles reference the retained base
        /// vertex, exactly as they did before M11.
        /// </summary>
        Weld = 0,

        /// <summary>
        /// The pair is preserved as a split vertex: the part seam vertex is emitted as its own final vertex at the
        /// base vertex's position, so both sides keep their own attribute values.
        /// </summary>
        Split = 1
    }

    /// <summary>
    /// The weld/split decision for one explicit seam pair.
    /// </summary>
    public struct SeamPairDecision
    {
        /// <summary>Position of the pair in <see cref="SeamResolution.Matches"/>, which is the pair order.</summary>
        public int PairIndex;

        /// <summary>Index into the part mesh's vertex list.</summary>
        public int PartVertex;

        /// <summary>Index into the base mesh's vertex list.</summary>
        public int BaseVertex;

        /// <summary>Whether the pair welds or keeps its own vertex.</summary>
        public SeamPairDisposition Disposition;

        /// <summary>
        /// The same-name UV semantics whose values disagreed at this pair, in final channel order. Empty for a
        /// weld, and never null.
        /// </summary>
        public string[] DisagreeingSemantics;

        /// <summary>True when this pair keeps its own part vertex.</summary>
        public bool IsSplit => Disposition == SeamPairDisposition.Split;

        /// <summary>True when this pair is a true weld.</summary>
        public bool IsWeld => Disposition == SeamPairDisposition.Weld;

        /// <summary>Number of disagreeing semantics, or zero for a weld.</summary>
        public int DisagreeingSemanticCount => DisagreeingSemantics != null ? DisagreeingSemantics.Length : 0;
    }

    /// <summary>
    /// One part and semantic for which seam pairs were preserved, with the number of pairs it affected.
    /// </summary>
    /// <remarks>
    /// This is the aggregate R2 asks for: one record per part and semantic instead of one diagnostic per seam
    /// vertex. The record carries the largest observed difference and one sample pair so the summary stays
    /// actionable without becoming a per-vertex log.
    /// </remarks>
    public sealed class SeamUvPreservation
    {
        /// <summary>Normalized semantic name that disagreed.</summary>
        public string Semantic { get; }

        /// <summary>Final output channel the semantic occupies.</summary>
        public int OutputChannel { get; }

        /// <summary>Number of seam pairs preserved because of this semantic.</summary>
        public int PairCount { get; }

        /// <summary>Largest UV distance observed at a pair for this semantic.</summary>
        public float MaxDifference { get; }

        /// <summary>First pair (in pair order) that disagreed, for the diagnostic.</summary>
        public int SamplePartVertex { get; }

        /// <summary>Base vertex of <see cref="SamplePartVertex"/>'s pair.</summary>
        public int SampleBaseVertex { get; }

        /// <summary>The base value at the sample pair.</summary>
        public Vector4 SampleBaseUv { get; }

        /// <summary>The part value at the sample pair.</summary>
        public Vector4 SamplePartUv { get; }

        /// <summary>Creates a preservation record.</summary>
        public SeamUvPreservation(
            string semantic,
            int outputChannel,
            int pairCount,
            float maxDifference,
            int samplePartVertex,
            int sampleBaseVertex,
            Vector4 sampleBaseUv,
            Vector4 samplePartUv)
        {
            Semantic = semantic ?? string.Empty;
            OutputChannel = outputChannel;
            PairCount = pairCount;
            MaxDifference = maxDifference;
            SamplePartVertex = samplePartVertex;
            SampleBaseVertex = sampleBaseVertex;
            SampleBaseUv = sampleBaseUv;
            SamplePartUv = samplePartUv;
        }
    }

    /// <summary>
    /// The weld/split decisions for one part's seam.
    /// </summary>
    public sealed class PartSeamWeldPlan
    {
        private readonly ReadOnlyCollection<SeamPairDecision> _decisions;
        private readonly ReadOnlyCollection<SeamUvPreservation> _uvPreservations;
        private readonly Dictionary<int, SeamPairDisposition> _byPartVertex;
        private readonly Dictionary<int, int> _baseVertexByPartVertex;

        /// <summary>The part this plan belongs to.</summary>
        public string PartId { get; }

        /// <summary>Every decision, in pair order (ascending part vertex).</summary>
        public IReadOnlyList<SeamPairDecision> Decisions => _decisions;

        /// <summary>One record per semantic that caused a preservation, in final channel order.</summary>
        public IReadOnlyList<SeamUvPreservation> UvPreservations => _uvPreservations;

        /// <summary>Number of seam pairs.</summary>
        public int Count => _decisions.Count;

        /// <summary>Number of pairs that weld.</summary>
        public int WeldCount { get; }

        /// <summary>Number of pairs that keep their own part vertex.</summary>
        public int SplitCount { get; }

        /// <summary>True when every pair welds, which is the pre-M11 behaviour.</summary>
        public bool AllWelded => SplitCount == 0;

        /// <summary>True when no pair welds at all.</summary>
        public bool AllSplit => WeldCount == 0;

        /// <summary>Creates a part plan.</summary>
        public PartSeamWeldPlan(
            string partId,
            IReadOnlyList<SeamPairDecision> decisions,
            IReadOnlyList<SeamUvPreservation> uvPreservations)
        {
            PartId = partId ?? string.Empty;

            var copies = new SeamPairDecision[decisions?.Count ?? 0];
            for (var i = 0; i < copies.Length; i++) copies[i] = decisions[i];
            _decisions = Array.AsReadOnly(copies);

            var preservations = new SeamUvPreservation[uvPreservations?.Count ?? 0];
            for (var i = 0; i < preservations.Length; i++) preservations[i] = uvPreservations[i];
            _uvPreservations = Array.AsReadOnly(preservations);

            _byPartVertex = new Dictionary<int, SeamPairDisposition>(_decisions.Count);
            _baseVertexByPartVertex = new Dictionary<int, int>(_decisions.Count);
            for (var i = 0; i < _decisions.Count; i++)
            {
                var decision = _decisions[i];
                _byPartVertex[decision.PartVertex] = decision.Disposition;
                _baseVertexByPartVertex[decision.PartVertex] = decision.BaseVertex;
                if (decision.IsSplit) SplitCount++;
                else WeldCount++;
            }
        }

        /// <summary>
        /// The source index of the base vertex a part seam vertex is paired with. Returns false when the vertex is
        /// not a paired seam vertex.
        /// </summary>
        public bool TryBaseVertexOf(int partVertex, out int baseVertex)
        {
            return _baseVertexByPartVertex.TryGetValue(partVertex, out baseVertex);
        }

        /// <summary>
        /// The disposition of one part seam vertex. Returns false when the vertex is not a paired seam vertex at
        /// all, which is a different state from either disposition.
        /// </summary>
        /// <remarks>
        /// A caller that asked "should this part vertex be deleted" for an ordinary part vertex would otherwise
        /// have to guess an answer; the explicit <c>false</c> keeps "not paired" distinguishable from "welded".
        /// </remarks>
        public bool TryDispositionOf(int partVertex, out SeamPairDisposition disposition)
        {
            return _byPartVertex.TryGetValue(partVertex, out disposition);
        }

        /// <summary>True when the given part vertex is a seam vertex that keeps its own final vertex.</summary>
        public bool IsSplit(int partVertex)
        {
            return _byPartVertex.TryGetValue(partVertex, out var disposition)
                   && disposition == SeamPairDisposition.Split;
        }

        /// <summary>True when the given part vertex is a seam vertex that welds into the base vertex.</summary>
        public bool IsWeld(int partVertex)
        {
            return _byPartVertex.TryGetValue(partVertex, out var disposition)
                   && disposition == SeamPairDisposition.Weld;
        }
    }

    /// <summary>
    /// The weld/split decisions for every part of one assembly, keyed by part id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One decision, shared by validation and planning.</b> Validation must not say "this seam is usable" while
    /// the plan quietly drops the part's UV: the two now read the same decision object
    /// (<see cref="SeamWeldPlanner"/>) instead of each applying its own rule.
    /// </para>
    /// <para>
    /// The plan is keyed by part id, which is the same stable identity the rest of the plan uses, and the parts
    /// are held in the canonical order so a report is reproducible.
    /// </para>
    /// </remarks>
    public sealed class SeamWeldPlan
    {
        private readonly ReadOnlyDictionary<string, PartSeamWeldPlan> _parts;

        /// <summary>An empty plan: no part declares a seam.</summary>
        public static readonly SeamWeldPlan Empty =
            new SeamWeldPlan(new Dictionary<string, PartSeamWeldPlan>(StringComparer.Ordinal));

        /// <summary>Per-part decisions, keyed by part id.</summary>
        public IReadOnlyDictionary<string, PartSeamWeldPlan> Parts => _parts;

        /// <summary>Total number of welded pairs across every part.</summary>
        public int WeldCount { get; }

        /// <summary>Total number of preserved (split) pairs across every part.</summary>
        public int SplitCount { get; }

        /// <summary>True when no pair was preserved, which is the pre-M11 behaviour.</summary>
        public bool AllWelded => SplitCount == 0;

        /// <summary>Creates a plan.</summary>
        public SeamWeldPlan(IReadOnlyDictionary<string, PartSeamWeldPlan> parts)
        {
            var copy = new Dictionary<string, PartSeamWeldPlan>(StringComparer.Ordinal);
            if (parts != null)
            {
                var keys = new List<string>(parts.Keys);
                keys.Sort(StringComparer.Ordinal);

                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i] ?? string.Empty;
                    var value = parts[key];
                    if (value == null) continue;
                    copy[key] = value;
                    WeldCount += value.WeldCount;
                    SplitCount += value.SplitCount;
                }
            }

            _parts = new ReadOnlyDictionary<string, PartSeamWeldPlan>(copy);
        }

        /// <summary>The decisions for a part, or null when the part declares no seam.</summary>
        public PartSeamWeldPlan For(string partId)
        {
            return _parts.TryGetValue(partId ?? string.Empty, out var plan) ? plan : null;
        }

        /// <summary>
        /// True when the given part seam vertex welds. A vertex that is not a seam vertex at all reports false,
        /// because "not paired" and "welded" are different states.
        /// </summary>
        public bool IsWeld(string partId, int partVertex)
        {
            var plan = For(partId);
            return plan != null && plan.IsWeld(partVertex);
        }

        /// <summary>
        /// True when the given part seam vertex is preserved. A vertex that is not a seam vertex reports false.
        /// </summary>
        public bool IsSplit(string partId, int partVertex)
        {
            var plan = For(partId);
            return plan != null && plan.IsSplit(partVertex);
        }
    }

    /// <summary>
    /// Decides, per explicit seam pair, whether it can be a true weld or must keep its own part vertex (M11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a decision object exists.</b> Before M11 the Uv rule and the planner each applied the same-name UV
    /// rule independently: the rule reported <c>APA004</c> and the planner deleted the part seam vertex anyway.
    /// Any change to one without the other would let validation approve a seam the plan then damaged. The rule is
    /// therefore expressed once, here, as a value both callers read.
    /// </para>
    /// <para>
    /// <b>The rule.</b> A pair welds when every same-name UV semantic that <i>both</i> sides actually carry agrees
    /// within <see cref="ApaNumericPolicy.UvEpsilon"/>. If any of them disagrees, the pair keeps its own vertex:
    /// the part triangles keep referencing the part vertex and its UV, the base triangles keep the base vertex and
    /// its UV, and the two positions are made identical so the surface has no visible crack. A semantic that only
    /// one side carries is not a disagreement (there is nothing to disagree with), and a declared semantic whose
    /// channel the mesh does not actually have is skipped exactly as the pre-M11 rule skipped it.
    /// </para>
    /// <para>
    /// <b>Everything else is deliberately unchanged.</b> A seam whose shared UVs agree produces exactly the same
    /// plan it produced before, down to the vertex count, and a pair the author wrote is still a pair: the
    /// decision never re-matches positions and never rejects a displaced pair.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> The decision is a pure function of the snapshots, the resolved seam, the final UV
    /// layout, and the numeric policy. Pairs are decided in pair order and the per-semantic aggregates are ordered
    /// by final channel, so no dictionary enumeration order and no instance id can reach the result.
    /// </para>
    /// </remarks>
    public static class SeamWeldPlanner
    {
        /// <summary>
        /// Decides every pair of every part.
        /// </summary>
        /// <param name="context">The assembly being planned or validated.</param>
        /// <param name="resolutions">
        /// The resolved seam per part id. A part with no seam, or one whose resolution failed, is skipped; a
        /// failed resolution is reported by the seam rule, not here.
        /// </param>
        /// <param name="layout">
        /// The final UV layout, which supplies the semantic names and the source channel per source. Null means
        /// "no UV semantics could be resolved", in which case every pair welds, because there is no semantic left
        /// to disagree about and the layout failure is reported elsewhere.
        /// </param>
        public static SeamWeldPlan Plan(
            ValidationContext context,
            IReadOnlyDictionary<string, SeamResolution> resolutions,
            UvLayout layout)
        {
            if (context?.Base?.Mesh == null || resolutions == null || resolutions.Count == 0)
            {
                return SeamWeldPlan.Empty;
            }

            var parts = new Dictionary<string, PartSeamWeldPlan>(StringComparer.Ordinal);

            // Parts are already in canonical order in a context; the list is re-sorted anyway so the map is built
            // in a stated order rather than in whatever order the caller supplied.
            var ordered = ValidationContext.SortParts(context.Parts);
            for (var i = 0; i < ordered.Count; i++)
            {
                var part = ordered[i];
                if (part?.Mesh == null) continue;
                if (!resolutions.TryGetValue(part.PartId, out var resolution)) continue;
                if (resolution == null || resolution.Count == 0) continue;

                parts[part.PartId] = Decide(
                    part, context.Base.Mesh, resolution, layout, context.NumericPolicy);
            }

            return parts.Count == 0 ? SeamWeldPlan.Empty : new SeamWeldPlan(parts);
        }

        /// <summary>
        /// Decides one part's pairs. Pure: it reports nothing and writes nothing.
        /// </summary>
        public static PartSeamWeldPlan Decide(
            PartSnapshot part,
            MeshSnapshot baseMesh,
            SeamResolution resolution,
            UvLayout layout,
            ApaNumericPolicy policy)
        {
            if (part?.Mesh == null || baseMesh == null || resolution == null)
            {
                return new PartSeamWeldPlan(
                    part?.PartId, Array.Empty<SeamPairDecision>(), Array.Empty<SeamUvPreservation>());
            }

            var epsilon = (policy ?? ApaNumericPolicy.Default).UvEpsilon;
            var decisions = new List<SeamPairDecision>(resolution.Matches.Count);

            // One accumulator per semantic, ordered by final channel, so the per-semantic summary is emitted in
            // layout order rather than in dictionary order.
            var accumulators = new List<PreservationAccumulator>();
            var accumulatorBySemantic = new Dictionary<string, PreservationAccumulator>(StringComparer.Ordinal);

            var channels = CollectSharedChannels(part, baseMesh, layout);

            for (var m = 0; m < resolution.Matches.Count; m++)
            {
                var match = resolution.Matches[m];
                List<string> conflicts = null;

                for (var c = 0; c < channels.Count; c++)
                {
                    var channel = channels[c];
                    var baseUv = channel.BaseUvs[match.BaseVertex];
                    var partUv = channel.PartUvs[match.PartVertex];
                    var difference = Distance(baseUv, partUv);

                    if (!(difference > epsilon)) continue;

                    if (conflicts == null) conflicts = new List<string>(1);
                    conflicts.Add(channel.Semantic);

                    if (!accumulatorBySemantic.TryGetValue(channel.Semantic, out var accumulator))
                    {
                        accumulator = new PreservationAccumulator(channel.Semantic, channel.OutputChannel);
                        accumulatorBySemantic.Add(channel.Semantic, accumulator);
                        accumulators.Add(accumulator);
                    }

                    accumulator.Add(match, baseUv, partUv, difference);
                }

                decisions.Add(new SeamPairDecision
                {
                    PairIndex = m,
                    PartVertex = match.PartVertex,
                    BaseVertex = match.BaseVertex,
                    Disposition = conflicts == null ? SeamPairDisposition.Weld : SeamPairDisposition.Split,
                    DisagreeingSemantics = conflicts == null ? Array.Empty<string>() : conflicts.ToArray()
                });
            }

            accumulators.Sort(CompareAccumulators);

            var preservations = new SeamUvPreservation[accumulators.Count];
            for (var i = 0; i < accumulators.Count; i++) preservations[i] = accumulators[i].ToPreservation();

            return new PartSeamWeldPlan(part.PartId, decisions, preservations);
        }

        /// <summary>
        /// The same-name UV channels both sides actually carry, in final channel order.
        /// </summary>
        /// <remarks>
        /// "Both sides declare it" is not enough: the pre-M11 rule compared a channel only when both meshes
        /// carried it, and keeping that condition means a declaration that names an absent channel cannot
        /// manufacture a disagreement. The declarations still decide <i>which</i> channels are compared, through
        /// the resolved layout.
        /// </remarks>
        private static List<SharedUvChannel> CollectSharedChannels(
            PartSnapshot part,
            MeshSnapshot baseMesh,
            UvLayout layout)
        {
            var result = new List<SharedUvChannel>();
            if (layout == null) return result;

            for (var c = 0; c < layout.Channels.Count; c++)
            {
                var channel = layout.Channels[c];

                var baseChannel = channel.FindSourceChannel(string.Empty);
                var partChannel = channel.FindSourceChannel(part.PartId);
                if (baseChannel < 0 || partChannel < 0) continue;
                if (!baseMesh.HasUvChannel(baseChannel)) continue;
                if (!part.Mesh.HasUvChannel(partChannel)) continue;

                result.Add(new SharedUvChannel(
                    channel.Semantic,
                    channel.OutputChannel,
                    baseMesh.GetUvChannel(baseChannel),
                    part.Mesh.GetUvChannel(partChannel)));
            }

            return result;
        }

        private static int CompareAccumulators(PreservationAccumulator a, PreservationAccumulator b)
        {
            return a.OutputChannel.CompareTo(b.OutputChannel);
        }

        /// <summary>Euclidean distance over the two components every UV semantic shares.</summary>
        /// <remarks>
        /// Components <c>z</c> and <c>w</c> are excluded for the same reason the pre-M11 rule excluded them: the
        /// product rule the seam contract is built on is the two-component one, and widening the comparison would
        /// start preserving seams the specification accepts as welded.
        /// </remarks>
        private static float Distance(Vector4 a, Vector4 b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>One same-name channel both sides carry, with the arrays the comparison reads.</summary>
        private readonly struct SharedUvChannel
        {
            public readonly string Semantic;
            public readonly int OutputChannel;
            public readonly IReadOnlyList<Vector4> BaseUvs;
            public readonly IReadOnlyList<Vector4> PartUvs;

            public SharedUvChannel(
                string semantic,
                int outputChannel,
                IReadOnlyList<Vector4> baseUvs,
                IReadOnlyList<Vector4> partUvs)
            {
                Semantic = semantic ?? string.Empty;
                OutputChannel = outputChannel;
                BaseUvs = baseUvs;
                PartUvs = partUvs;
            }
        }

        /// <summary>Accumulates one semantic's preserved pairs while the pairs are walked in order.</summary>
        private sealed class PreservationAccumulator
        {
            private readonly string _semantic;
            private readonly int _outputChannel;
            private int _count;
            private float _maxDifference;
            private int _samplePartVertex;
            private int _sampleBaseVertex;
            private Vector4 _sampleBaseUv;
            private Vector4 _samplePartUv;

            public int OutputChannel => _outputChannel;

            public PreservationAccumulator(string semantic, int outputChannel)
            {
                _semantic = semantic;
                _outputChannel = outputChannel;
                _samplePartVertex = -1;
                _sampleBaseVertex = -1;
                _maxDifference = 0f;
            }

            public void Add(SeamMatch match, Vector4 baseUv, Vector4 partUv, float difference)
            {
                // The first pair in pair order is the sample, so the reported vertices do not depend on which
                // pair happened to have the largest difference.
                if (_samplePartVertex < 0)
                {
                    _samplePartVertex = match.PartVertex;
                    _sampleBaseVertex = match.BaseVertex;
                    _sampleBaseUv = baseUv;
                    _samplePartUv = partUv;
                }

                _count++;
                if (difference > _maxDifference) _maxDifference = difference;
            }

            public SeamUvPreservation ToPreservation()
            {
                return new SeamUvPreservation(
                    _semantic,
                    _outputChannel,
                    _count,
                    _maxDifference,
                    _samplePartVertex,
                    _sampleBaseVertex,
                    _sampleBaseUv,
                    _samplePartUv);
            }
        }
    }
}

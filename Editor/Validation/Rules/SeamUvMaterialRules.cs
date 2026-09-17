using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Validates UV semantics: uniqueness, the eight-channel limit, seam UV agreement, and agreement between
    /// several parts that weld onto the same base vertex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A same-name seam UV disagreement is not an error (M11).</b> A part and a body routinely use different
    /// UV atlases, so a seam that coincides in space and disagrees in UV is a normal input rather than a
    /// modelling defect. The pair is decided by <see cref="SeamWeldPlanner"/> — the same decision the planner
    /// consumes — and a disagreeing pair is preserved as a split vertex: the part keeps its own UV and the body
    /// keeps its own. The condition is reported once per part and semantic (<c>APA045</c>, informational) instead
    /// of once per seam vertex.
    /// </para>
    /// <para>
    /// <b><c>APA004</c> is now a guard.</b> Its registered condition — a same-name UV semantic disagrees at a
    /// <i>welded</i> vertex — can no longer be produced by the decision, because a disagreement is exactly what
    /// makes a pair stop being welded. It is still checked, so that a future change to the decision cannot
    /// silently start discarding a part's UV at a weld, and the check reports the condition it always reported.
    /// </para>
    /// <para>
    /// The multi-part rule exists for the same reason it always did, but it now applies only to pairs that
    /// actually weld. A welded vertex is one output vertex, so it can hold one value per semantic. When two parts
    /// contribute different values there is no basis on which to prefer either: the assembler has no priority
    /// model, and the specification forbids inventing one. Two preserved pairs never conflict, because each has
    /// its own vertex.
    /// </para>
    /// </remarks>
    public sealed class UvRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "UvRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context.Base == null || context.Base.Mesh == null) return;

            var sources = UvResolver.CollectSources(context, issues);
            var layout = UvResolver.Resolve(sources, issues);
            if (layout == null) return;

            // Every channel the resolver preserved automatically is reported once per source, before the seam
            // checks that read the same layout. The automatic channel is a real output layer, so this is the
            // "new UV layer" information section 34 describes rather than a defect: the author sees that an
            // undeclared layer was kept, and that declaring it is what gives it a name of their own.
            ReportAutoPreservedChannels(context, layout, issues);

            var baseToAvatar = context.Base.Transforms.SourceToAvatarLocal();

            // Resolving every part's seam once here means the checks below can compare actual matched positions
            // rather than re-deriving them, and it keeps the reported base vertex identity consistent with what
            // the planner will weld.
            var resolutions = new Dictionary<string, SeamResolution>(System.StringComparer.Ordinal);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null || part.Seam == null) continue;

                var resolution = SeamResolver.Resolve(
                    part,
                    context.Base.Mesh,
                    part.Transforms.SourceToAvatarLocal(),
                    baseToAvatar,
                    context.NumericPolicy,
                    new List<ValidationIssue>());

                if (resolution == null || resolution.Count == 0) continue;
                resolutions[part.PartId] = resolution;
            }

            // The one decision this rule and the planner share. Reading it here is what makes "validation says
            // the seam is usable" and "the plan keeps the part's UV" the same statement.
            var weldPlan = SeamWeldPlanner.Plan(context, resolutions, layout);

            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null || part.Seam == null) continue;
                if (!resolutions.TryGetValue(part.PartId, out var resolution)) continue;

                var partPlan = weldPlan.For(part.PartId);
                ReportPreservedSeamUvs(part, partPlan, issues);
                GuardWeldedSeamUvs(context, part, resolution, partPlan, layout, issues);
            }

            ValidateMultiPartWeldUvs(context, resolutions, weldPlan, layout, issues);
        }

        /// <summary>
        /// Reports, once per source, the UV channels the resolver preserved automatically.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the whole of the undeclared-channel story since M11: a present channel the profile does not
        /// name is no longer refused, because refusing it made a perfectly ordinary asset unusable — a part whose
        /// mesh carries UV0 while the profile declares only UV1 is a normal import, not a modelling defect. The
        /// channel is contributed under a generated passthrough semantic, so the values reach the assembled mesh
        /// and the layer takes part in the weld/split decision like any other.
        /// </para>
        /// <para>
        /// It stays reported because the alternative — silence — would leave the author unable to tell "the
        /// plugin invented a layer for me" from "the plugin has a bug and my layer moved". One line per source
        /// lists every channel that was preserved, its generated semantic, and the final channel it occupies.
        /// </para>
        /// <para>
        /// The code is <c>APA036</c> at <see cref="ApaSeverity.Info"/>: the code keeps its registered subject (a
        /// present UV channel the profile does not declare) while its severity follows the condition, which is no
        /// longer a defect. The blocking form of the code is reserved for the one case the resolver must not
        /// guess at — see <c>reason=auto-channel-name-taken</c>.
        /// </para>
        /// </remarks>
        private static void ReportAutoPreservedChannels(
            ValidationContext context,
            UvLayout layout,
            List<ValidationIssue> issues)
        {
            ReportAutoPreservedChannels(context.Base != null ? context.Base.Mesh : null, string.Empty, layout, issues);

            var parts = ValidationContext.SortParts(context.Parts);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part?.Mesh == null) continue;
                ReportAutoPreservedChannels(part.Mesh, part.PartId, layout, issues);
            }
        }

        private static void ReportAutoPreservedChannels(
            MeshSnapshot mesh,
            string partId,
            UvLayout layout,
            List<ValidationIssue> issues)
        {
            if (mesh == null) return;

            var preserved = layout.AutoPreservedFor(partId);
            if (preserved.Count == 0) return;

            var present = mesh.PresentUvChannels();
            var channels = new List<int>(preserved.Count);
            var summary = new System.Text.StringBuilder();

            for (var i = 0; i < preserved.Count; i++)
            {
                channels.Add(preserved[i].SourceChannel);
                if (i > 0) summary.Append(',');
                summary.Append(preserved[i].Semantic).Append('@').Append(preserved[i].SourceChannel);
            }

            issues.Add(ValidationIssue.Info(
                ApaErrorCode.UndeclaredUvChannel,
                ApaIssuePhase.Uv,
                "The mesh of " + DescribeSource(partId) + " carries " + preserved.Count + " UV channel(s) that " +
                "the profile does not declare (channels " + DescribeChannels(channels) + "). They are preserved " +
                "as passthrough semantic(s) " + JoinDeclared(summary) + " and written to the final mesh, so no " +
                "UV data is dropped. Declare the channel to give the layer a name of your own, or remove the " +
                "unused UV layer from the source asset.",
                partId,
                preserved[0].SourceChannel,
                detail: "reason=uv-channel-auto-preserved" +
                        "; source=" + DescribeSource(partId) +
                        "; presentChannels=" + DescribeChannels(present) +
                        "; autoPreservedChannels=" + DescribeChannels(channels) +
                        "; autoSemantics=" + JoinDeclared(summary) +
                        "; outputChannels=" + DescribeOutputChannels(preserved)));
        }

        private static string DescribeOutputChannels(IReadOnlyList<UvAutoPreservedChannel> preserved)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < preserved.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(preserved[i].OutputChannel);
            }

            return sb.ToString();
        }

        /// <summary>Joins a summary the caller already built, so one pass can produce both list and text.</summary>
        private static string JoinDeclared(System.Text.StringBuilder declaredSummary)
        {
            return declaredSummary.ToString();
        }

        private static string DescribeSource(string partId)
        {
            return string.IsNullOrEmpty(partId) ? "the base body" : "part '" + partId + "'";
        }

        private static string DescribeChannels(List<int> channels)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < channels.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(channels[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Reports the pairs that were preserved because a same-name UV semantic disagreed, once per part and
        /// semantic.
        /// </summary>
        /// <remarks>
        /// This is the whole of R2's convergence: the number of diagnostics no longer scales with the number of
        /// seam vertices, and the message states what happened in terms of the author's own assets (the part keeps
        /// its UV, the body keeps its own) rather than in terms of the assembler's internals.
        /// </remarks>
        private static void ReportPreservedSeamUvs(
            PartSnapshot part,
            PartSeamWeldPlan partPlan,
            List<ValidationIssue> issues)
        {
            if (partPlan == null) return;

            for (var i = 0; i < partPlan.UvPreservations.Count; i++)
            {
                var preservation = partPlan.UvPreservations[i];

                issues.Add(ValidationIssue.Info(
                    ApaErrorCode.SeamUvPreserved,
                    ApaIssuePhase.Uv,
                    "Part '" + part.PartId + "' keeps its own UV for semantic '" + preservation.Semantic +
                    "': " + preservation.PairCount + " seam pair(s) differ from the body by more than the UV " +
                    "epsilon, so the part's seam vertices were preserved instead of welded. The part keeps its " +
                    "own UV values and the body keeps its own, and both sides are positioned identically so the " +
                    "surface stays closed. This is normal when the part and the body use different UV atlases.",
                    part.PartId,
                    preservation.SamplePartVertex,
                    preservation.SampleBaseVertex,
                    detail: "reason=uv-seam-preserved" +
                            "; semantic=" + preservation.Semantic +
                            "; outputChannel=" + preservation.OutputChannel +
                            "; preservedPairs=" + preservation.PairCount +
                            "; weldedPairs=" + partPlan.WeldCount +
                            (preservation.HasNonFiniteUv ? "; nonFiniteUv=true" : string.Empty) +
                            "; maxDifference=" + F(preservation.MaxDifference) +
                            "; samplePartVertex=" + preservation.SamplePartVertex +
                            "; sampleBaseVertex=" + preservation.SampleBaseVertex +
                            "; sampleBaseUv=" + F(preservation.SampleBaseUv.x) + "," +
                            F(preservation.SampleBaseUv.y) +
                            "; samplePartUv=" + F(preservation.SamplePartUv.x) + "," +
                            F(preservation.SamplePartUv.y)));
            }
        }

        /// <summary>
        /// Re-checks the pairs the decision kept as welds, and reports <c>APA004</c> if one of them disagrees.
        /// </summary>
        /// <remarks>
        /// The decision cannot produce such a pair, so this is a guard rather than the primary rule: it exists so
        /// that a future change to the decision cannot silently start welding a pair whose part UV is then
        /// discarded. The diagnostic is the one <c>APA004</c> has always carried, so a report that does show it
        /// still means exactly what it always meant.
        /// </remarks>
        private static void GuardWeldedSeamUvs(
            ValidationContext context,
            PartSnapshot part,
            SeamResolution resolution,
            PartSeamWeldPlan partPlan,
            UvLayout layout,
            List<ValidationIssue> issues)
        {
            var baseMesh = context.Base.Mesh;
            var partMesh = part.Mesh;

            for (var c = 0; c < layout.Channels.Count; c++)
            {
                var channel = layout.Channels[c];

                // Only a semantic present on both sides can disagree. A semantic on one side only is legal:
                // a part-only value supplies the weld, while other missing vertices receive the channel default.
                var baseChannel = channel.FindSourceChannel(string.Empty);
                var partChannel = channel.FindSourceChannel(part.PartId);
                if (baseChannel < 0 || partChannel < 0) continue;

                if (!baseMesh.HasUvChannel(baseChannel) || !partMesh.HasUvChannel(partChannel)) continue;

                var baseUvs = baseMesh.GetUvChannel(baseChannel);
                var partUvs = partMesh.GetUvChannel(partChannel);

                for (var m = 0; m < resolution.Matches.Count; m++)
                {
                    var match = resolution.Matches[m];
                    if (partPlan != null && !partPlan.IsWeld(match.PartVertex)) continue;

                    var baseUv = baseUvs[match.BaseVertex];
                    var partUv = partUvs[match.PartVertex];

                    var difference = Distance(baseUv, partUv);
                    if (difference <= context.NumericPolicy.UvEpsilon) continue;

                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.SeamUvMismatch,
                        ApaIssuePhase.Uv,
                        "UV semantic '" + channel.Semantic + "' disagrees at a welded seam vertex: base vertex " +
                        match.BaseVertex + " is (" + F(baseUv.x) + ", " + F(baseUv.y) + ") but part vertex " +
                        match.PartVertex + " is (" + F(partUv.x) + ", " + F(partUv.y) + "), a difference of " +
                        F(difference) + ". A welded vertex holds one value per semantic, so the part's value " +
                        "cannot be represented there.",
                        part.PartId,
                        match.PartVertex,
                        match.BaseVertex,
                        detail: "semantic=" + channel.Semantic +
                                "; baseVertex=" + match.BaseVertex +
                                "; partVertex=" + match.PartVertex +
                                "; baseUv=" + F(baseUv.x) + "," + F(baseUv.y) +
                                "; partUv=" + F(partUv.x) + "," + F(partUv.y) +
                                "; difference=" + F(difference) +
                                "; epsilon=" + F(context.NumericPolicy.UvEpsilon) +
                                "; reason=base-part-seam-uv-mismatch"));
                }
            }
        }

        /// <summary>
        /// Rejects two parts that contribute different values for the same semantic to one welded base vertex.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The comparison is made on values the parts actually carry, and only for semantics at least one
        /// contributing part has. A part that simply lacks the semantic contributes nothing rather than a zero,
        /// because "absent" and "zero" are different claims: treating an absent layer as a zero value would
        /// report a conflict against every part that does have the layer.
        /// </para>
        /// <para>
        /// Only welded pairs take part. A preserved pair has its own vertex, so two parts that disagree there
        /// cannot conflict — which is exactly the case a part with a different UV atlas produces.
        /// </para>
        /// </remarks>
        private static void ValidateMultiPartWeldUvs(
            ValidationContext context,
            IReadOnlyDictionary<string, SeamResolution> resolutions,
            SeamWeldPlan weldPlan,
            UvLayout layout,
            List<ValidationIssue> issues)
        {
            if (context.Parts.Count < 2) return;

            var baseMesh = context.Base.Mesh;

            // base vertex -> semantic -> the first contribution seen and who made it.
            var seen = new Dictionary<int, Dictionary<string, WeldValue>>();

            for (var p = 0; p < context.Parts.Count; p++)
            {
                var part = context.Parts[p];
                if (part?.Mesh == null || part.Seam == null) continue;
                if (!resolutions.TryGetValue(part.PartId, out var resolution)) continue;

                var partPlan = weldPlan.For(part.PartId);

                for (var c = 0; c < layout.Channels.Count; c++)
                {
                    var channel = layout.Channels[c];
                    var partChannel = channel.FindSourceChannel(part.PartId);
                    if (partChannel < 0 || !part.Mesh.HasUvChannel(partChannel)) continue;

                    // Only welded vertices are compared. A non-seam vertex belongs to one part and cannot
                    // conflict with another part by construction, and a preserved seam vertex has a vertex of its
                    // own rather than a shared one.
                    var partUvs = part.Mesh.GetUvChannel(partChannel);
                    var baseChannel = channel.FindSourceChannel(string.Empty);
                    var hasBase = baseChannel >= 0 && baseMesh.HasUvChannel(baseChannel);
                    var baseUvs = hasBase ? baseMesh.GetUvChannel(baseChannel) : null;

                    for (var m = 0; m < resolution.Matches.Count; m++)
                    {
                        var match = resolution.Matches[m];
                        if (partPlan != null && !partPlan.IsWeld(match.PartVertex)) continue;

                        var value = partUvs[match.PartVertex];

                        if (!seen.TryGetValue(match.BaseVertex, out var bySemantic))
                        {
                            bySemantic = new Dictionary<string, WeldValue>(System.StringComparer.Ordinal);
                            seen.Add(match.BaseVertex, bySemantic);
                        }

                        if (!bySemantic.TryGetValue(channel.Semantic, out var existing))
                        {
                            bySemantic.Add(channel.Semantic, new WeldValue(part.PartId, match.PartVertex, value));
                            continue;
                        }

                        // The base's own value is the anchor when the base has the semantic: every part must
                        // then agree with the base, which the seam rule above already enforces per part. The
                        // rule here is specifically about two parts disagreeing with each other.
                        if (hasBase)
                        {
                            var baseValue = baseUvs[match.BaseVertex];
                            if (Distance(baseValue, value) <= context.NumericPolicy.UvEpsilon) continue;
                        }

                        if (Distance(existing.Value, value) <= context.NumericPolicy.UvEpsilon) continue;

                        // A base seam vertex matched by two vertices of the SAME part is either a legitimate
                        // UV/hard-edge split (the values agree, handled above) or an intra-part disagreement the
                        // author must fix. Reporting it as "parts A and A disagree" would be unreadable, so it
                        // gets its own stable reason token and a message that names the two part vertices.
                        var samePart = string.Equals(existing.PartId, part.PartId, System.StringComparison.Ordinal);
                        var reason = samePart ? "intra-part-seam-uv-split" : "part-part-seam-uv-conflict";
                        var detail = "semantic=" + channel.Semantic +
                                     "; baseVertex=" + match.BaseVertex +
                                     "; partA=" + existing.PartId + "@" + existing.PartVertex +
                                     "; partB=" + part.PartId + "@" + match.PartVertex +
                                     "; uvA=" + F(existing.Value.x) + "," + F(existing.Value.y) +
                                     "; uvB=" + F(value.x) + "," + F(value.y) +
                                     "; difference=" + F(Distance(existing.Value, value)) +
                                     "; epsilon=" + F(context.NumericPolicy.UvEpsilon) +
                                     "; reason=" + reason;

                        var message = samePart
                            ? "Part '" + part.PartId + "' welds two of its own seam vertices (" +
                              existing.PartVertex + " and " + match.PartVertex + ") onto base vertex " +
                              match.BaseVertex + " with different values for UV semantic '" + channel.Semantic +
                              "': (" + F(existing.Value.x) + ", " + F(existing.Value.y) + ") versus (" +
                              F(value.x) + ", " + F(value.y) + "). A welded vertex holds one value per semantic, " +
                              "so the part's own UV split cannot be represented there. Make the two seam UVs " +
                              "agree in the source asset, or do not weld both vertices onto this base vertex."
                            : "Parts '" + existing.PartId + "' and '" + part.PartId +
                              "' both weld onto base vertex " + match.BaseVertex +
                              " but disagree on UV semantic '" + channel.Semantic + "': (" +
                              F(existing.Value.x) + ", " + F(existing.Value.y) + ") versus (" + F(value.x) +
                              ", " + F(value.y) + "). A welded vertex holds one value per semantic, and there " +
                              "is no defined priority between parts; the comparison is anchored on the " +
                              "lowest-ordered contributor, never chained. Make the seam UVs agree in the source " +
                              "assets.";

                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.WeldUvConflict,
                            ApaIssuePhase.Uv,
                            message,
                            part.PartId,
                            match.PartVertex,
                            match.BaseVertex,
                            detail: detail));
                    }
                }
            }
        }

        /// <summary>Euclidean distance over the two components that all UV semantics share.</summary>
        /// <remarks>
        /// Components <c>z</c> and <c>w</c> are deliberately excluded. They exist for dimension-4 UV sets such
        /// as lightmaps, but the product rule the seam contract is built on is the two-component one, and
        /// widening the comparison would silently start rejecting seams that the specification accepts.
        /// </remarks>
        private static float Distance(Vector4 a, Vector4 b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static string F(float value)
        {
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        private struct WeldValue
        {
            public string PartId;
            public int PartVertex;
            public Vector4 Value;

            public WeldValue(string partId, int partVertex, Vector4 value)
            {
                PartId = partId;
                PartVertex = partVertex;
                Value = value;
            }
        }
    }

    /// <summary>
    /// Runs the strict seam contract checks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resolved correspondence is not stored on the context, because validation must be side-effect free.
    /// The planner resolves the seam again with the same inputs, which is cheap and guarantees that the plan
    /// and the validation report are derived from identical data rather than from a shared mutable cache.
    /// </para>
    /// <para>
    /// The informational line this rule emits is about <i>position pairing</i>, not about welding. Since M11 a
    /// pair is only welded when its same-name UVs agree, so calling a matched pair "welded" here would misreport
    /// every part whose UV atlas differs from the body's. The weld/split counts are appended from the same
    /// decision the planner uses.
    /// </para>
    /// </remarks>
    public sealed class SeamRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "SeamRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context.Base == null || context.Base.Mesh == null) return;

            var baseToAvatar = context.Base.Transforms.SourceToAvatarLocal();

            // Every seam is resolved before anything is reported, because the weld/split counts of one part are
            // read from a decision that spans the whole group: a partial dictionary would make the first part's
            // summary disagree with the plan.
            var resolutions = new Dictionary<string, SeamResolution>(System.StringComparer.Ordinal);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null) continue;

                var partToAvatar = part.Transforms.SourceToAvatarLocal();
                var resolution = SeamResolver.Resolve(
                    part,
                    context.Base.Mesh,
                    partToAvatar,
                    baseToAvatar,
                    context.NumericPolicy,
                    issues);

                if (resolution == null) continue;
                resolutions[part.PartId] = resolution;
            }

            // The UV layout is resolved with a scratch issue list: the Uv rule already reports everything wrong
            // with the layout, and reporting it twice would double every one of its diagnostics.
            var layout = ResolveLayoutQuietly(context);
            var weldPlan = layout != null ? SeamWeldPlanner.Plan(context, resolutions, layout) : null;

            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null) continue;
                if (!resolutions.TryGetValue(part.PartId, out var resolution)) continue;
                if (!resolution.IsComplete || resolution.Count == 0) continue;

                var partPlan = weldPlan != null ? weldPlan.For(part.PartId) : null;

                var preserved = partPlan != null ? partPlan.SplitCount : 0;
                var suffix = partPlan == null
                    ? string.Empty
                    : " " + partPlan.WeldCount + " pair(s) weld and " + preserved +
                      " pair(s) keep their own part vertex.";
                var counts = partPlan == null
                    ? "matched=" + resolution.Count
                    : "matched=" + resolution.Count + "; welded=" + partPlan.WeldCount +
                      "; preserved=" + preserved;

                issues.Add(ValidationIssue.Info(
                    ApaErrorCode.SeamPositionMismatch,
                    ApaIssuePhase.Seam,
                    resolution.Count + " seam position pair(s) established for part '" + part.PartId + "'." +
                    suffix,
                    part.PartId,
                    detail: "reason=seam-pairs-established; " + counts));
            }
        }

        /// <summary>
        /// Resolves the final UV layout without reporting anything, or null when it cannot be resolved.
        /// </summary>
        /// <remarks>
        /// Used by the informational seam summary, which must not duplicate the Uv rule's findings. The two
        /// resolvers are pure functions of the context, so a silent call produces exactly the layout the Uv rule
        /// saw.
        /// </remarks>
        private static UvLayout ResolveLayoutQuietly(ValidationContext context)
        {
            var scratch = new List<ValidationIssue>();
            var sources = UvResolver.CollectSources(context, scratch);
            return UvResolver.Resolve(sources, scratch);
        }
    }

    /// <summary>
    /// Validates material semantics: uniqueness and the conflict policies.
    /// </summary>
    public sealed class MaterialRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "MaterialRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            var sources = MaterialResolver.CollectSources(context, issues);
            MaterialResolver.Resolve(sources, issues);
        }
    }
}

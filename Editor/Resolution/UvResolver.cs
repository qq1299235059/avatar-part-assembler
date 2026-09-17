using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// One source's contribution of a semantic from a particular channel of its mesh: either a declaration the
    /// author wrote or a passthrough the resolver generated for a channel no declaration names.
    /// </summary>
    /// <remarks>
    /// <see cref="AutoPreserved"/> is set by the collection pass at the moment the contribution is generated, and
    /// it is the only thing that decides whether the layout records the contribution as automatically preserved.
    /// A declared semantic is never flagged, however much its name looks like a generated one, so the record
    /// states what happened rather than what the name resembles.
    /// </remarks>
    public struct UvSemanticSource
    {
        /// <summary>Normalized semantic name.</summary>
        public string Semantic;

        /// <summary>Channel index in the source mesh.</summary>
        public int SourceChannel;

        /// <summary>The part the contribution came from, or an empty string for the base.</summary>
        public string PartId;

        /// <summary>True when the resolver generated this contribution for a channel no declaration names.</summary>
        public bool AutoPreserved;

        /// <summary>Creates an explicit declaration.</summary>
        public UvSemanticSource(string semantic, int sourceChannel, string partId)
            : this(semantic, sourceChannel, partId, false)
        {
        }

        /// <summary>Creates a contribution, stating whether the resolver generated it.</summary>
        public UvSemanticSource(string semantic, int sourceChannel, string partId, bool autoPreserved)
        {
            Semantic = semantic;
            SourceChannel = sourceChannel;
            PartId = partId ?? string.Empty;
            AutoPreserved = autoPreserved;
        }
    }

    /// <summary>
    /// One source channel that no declaration names and that was therefore preserved automatically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record is the layout's own account of what it invented: the source it was invented for, the semantic
    /// it created, the source channel it reads, and the final channel it writes. Validation reports it once per
    /// source so the author can see that an undeclared layer was kept rather than dropped, and so the one action
    /// that gives the layer a name of its own — declaring it — is discoverable.
    /// </para>
    /// <para>
    /// The record is per <i>source</i>, not per semantic. Two parts whose meshes both carry an undeclared channel
    /// 0 share one semantic and one final channel, and each still gets a record of its own, so "what happened to
    /// this part" is answered exactly rather than inferred from the final layout.
    /// </para>
    /// </remarks>
    public sealed class UvAutoPreservedChannel
    {
        /// <summary>The generated semantic the channel is preserved under.</summary>
        public string Semantic { get; }

        /// <summary>The source channel the semantic reads.</summary>
        public int SourceChannel { get; }

        /// <summary>The final output channel the semantic occupies.</summary>
        public int OutputChannel { get; }

        /// <summary>The part the channel was preserved for, or an empty string for the base body.</summary>
        public string PartId { get; }

        /// <summary>Creates a record.</summary>
        public UvAutoPreservedChannel(string semantic, int sourceChannel, int outputChannel, string partId)
        {
            Semantic = semantic ?? string.Empty;
            SourceChannel = sourceChannel;
            OutputChannel = outputChannel;
            PartId = partId ?? string.Empty;
        }
    }

    /// <summary>
    /// The final UV layout: which semantic occupies which output channel, and which source channel feeds it
    /// for every source.
    /// </summary>
    public sealed class UvLayout
    {
        private readonly ReadOnlyCollection<UvAutoPreservedChannel> _autoPreserved;

        /// <summary>The final channel index of each semantic, in output order.</summary>
        public IReadOnlyList<UvChannelAssignment> Channels { get; }

        /// <summary>Number of final channels required.</summary>
        public int ChannelCount => Channels.Count;

        /// <summary>
        /// Every channel that was preserved automatically because no declaration named it, one record per source
        /// and channel, in final channel order and then canonical source order. Empty when every present channel
        /// of every source was declared.
        /// </summary>
        public IReadOnlyList<UvAutoPreservedChannel> AutoPreservedChannels => _autoPreserved;

        /// <summary>Creates a layout.</summary>
        public UvLayout(IReadOnlyList<UvChannelAssignment> channels)
            : this(channels, null)
        {
        }

        /// <summary>Creates a layout that also records the channels it preserved automatically.</summary>
        public UvLayout(
            IReadOnlyList<UvChannelAssignment> channels,
            IReadOnlyList<UvAutoPreservedChannel> autoPreservedChannels)
        {
            var copy = new UvChannelAssignment[channels?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = channels[i];
            Channels = System.Array.AsReadOnly(copy);

            var preserved = new UvAutoPreservedChannel[autoPreservedChannels?.Count ?? 0];
            for (var i = 0; i < preserved.Length; i++) preserved[i] = autoPreservedChannels[i];
            _autoPreserved = System.Array.AsReadOnly(preserved);
        }

        /// <summary>Returns the source channel feeding a semantic for a given source, or -1 when absent.</summary>
        public int FindSourceChannel(string semantic, string partId)
        {
            for (var i = 0; i < Channels.Count; i++)
            {
                if (!ApaSemanticName.AreEqual(Channels[i].Semantic, semantic)) continue;
                return Channels[i].FindSourceChannel(partId);
            }

            return -1;
        }

        /// <summary>Returns the final output channel for a semantic, or -1 when the semantic is absent.</summary>
        public int FindOutputChannel(string semantic)
        {
            for (var i = 0; i < Channels.Count; i++)
            {
                if (ApaSemanticName.AreEqual(Channels[i].Semantic, semantic)) return Channels[i].OutputChannel;
            }

            return -1;
        }

        /// <summary>
        /// The channels preserved automatically for one source, in final channel order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The record names the source it belongs to, so this is a filter rather than a reconstruction: a part
        /// gets exactly the records the resolver created for that part, and the base body gets its own. A source
        /// that declared every present channel returns an empty list rather than null, so a caller can iterate
        /// without a null check.
        /// </para>
        /// <para>
        /// Reading the source identity from the record is what keeps the answer exact when several sources share
        /// one generated semantic: two parts whose undeclared channel 0 merges into one final channel each report
        /// their own channel once, and a source that declared <c>UV0</c> itself reports nothing.
        /// </para>
        /// </remarks>
        public List<UvAutoPreservedChannel> AutoPreservedFor(string partId)
        {
            var key = partId ?? string.Empty;
            var result = new List<UvAutoPreservedChannel>();

            for (var i = 0; i < _autoPreserved.Count; i++)
            {
                var record = _autoPreserved[i];
                if (!string.Equals(record.PartId, key, System.StringComparison.Ordinal)) continue;
                result.Add(record);
            }

            return result;
        }
    }

    /// <summary>
    /// One final UV channel and the source channels feeding it.
    /// </summary>
    public sealed class UvChannelAssignment
    {
        private readonly ReadOnlyDictionary<string, int> _sourceChannelView;

        /// <summary>Normalized semantic name occupying this final channel.</summary>
        public string Semantic { get; }

        /// <summary>Final output channel index, 0 through 7.</summary>
        public int OutputChannel { get; }

        /// <summary>Source channel per part id; the empty string key is the base body.</summary>
        public IReadOnlyDictionary<string, int> SourceChannels { get; }

        /// <summary>Creates an assignment.</summary>
        public UvChannelAssignment(string semantic, int outputChannel, IReadOnlyDictionary<string, int> sourceChannels)
        {
            Semantic = semantic ?? string.Empty;
            OutputChannel = outputChannel;
            var copy = new Dictionary<string, int>(System.StringComparer.Ordinal);
            if (sourceChannels != null)
            {
                foreach (var pair in sourceChannels)
                {
                    copy[pair.Key ?? string.Empty] = pair.Value;
                }
            }

            _sourceChannelView = new ReadOnlyDictionary<string, int>(copy);
            SourceChannels = _sourceChannelView;
        }

        /// <summary>Returns the source channel for a source, or -1 when that source lacks the semantic.</summary>
        public int FindSourceChannel(string partId)
        {
            return SourceChannels.TryGetValue(partId ?? string.Empty, out var channel) ? channel : -1;
        }
    }

    /// <summary>
    /// Resolves per-source UV channel declarations into one deterministic final channel layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UV layers merge by semantic name, never by Unity channel index; two sources may store the same semantic
    /// in different channels. Ordering is fixed so that the same inputs always produce the same output channel
    /// numbering: base semantics first in base declaration order, then new semantics by part order and source
    /// channel.
    /// </para>
    /// <para>
    /// Merging can legitimately require more channels than the sources had, so the eight-channel limit is
    /// checked here rather than assumed.
    /// </para>
    /// </remarks>
    public static class UvResolver
    {
        /// <summary>Builds the final UV layout.</summary>
        /// <remarks>
        /// <para>
        /// Semantics are numbered in first-seen order: the base body's contributions first, in declaration order,
        /// then each part's in canonical part order. Automatic channels are contributed after the explicit ones
        /// of their own source, so an explicit declaration always precedes the channel the resolver invented.
        /// </para>
        /// <para>
        /// The eight-channel limit is checked against the semantics the layout actually needs, automatic channels
        /// included: an auto-preserved layer is a real output layer, so it counts exactly like a declared one,
        /// and the layout is refused with <c>APA005</c> rather than silently truncated.
        /// </para>
        /// </remarks>
        public static UvLayout Resolve(IReadOnlyList<UvSemanticSource> sources, List<ValidationIssue> issues)
        {
            var ordered = new List<string>();
            var perSemantic = new Dictionary<string, Dictionary<string, int>>();

            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                var semantic = ApaSemanticName.Normalize(source.Semantic);

                if (!ApaSemanticName.IsValid(semantic))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSemanticName,
                        ApaIssuePhase.Uv,
                        "A UV semantic name is null, empty, or whitespace. " + ApaSemanticName.RuleDescription + ".",
                        source.PartId,
                        source.SourceChannel,
                        detail: "semantic='" + (source.Semantic ?? string.Empty) + "'"));
                    continue;
                }

                if (!perSemantic.TryGetValue(semantic, out var byPart))
                {
                    byPart = new Dictionary<string, int>();
                    perSemantic.Add(semantic, byPart);
                    ordered.Add(semantic);
                }
                else if (byPart.ContainsKey(source.PartId))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.DuplicateSemantic,
                        ApaIssuePhase.Uv,
                        "UV semantic '" + semantic + "' is declared more than once by the same source.",
                        source.PartId,
                        source.SourceChannel,
                        detail: "semantic=" + semantic + "; channel=" + source.SourceChannel));
                    continue;
                }

                byPart[source.PartId] = source.SourceChannel;
            }

            if (ordered.Count > ApaMeshLimits.MaxUvChannels)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.UvChannelOverflow,
                    ApaIssuePhase.Uv,
                    "The final mesh requires " + ordered.Count + " UV channels. Unity supports a maximum of " +
                    ApaMeshLimits.MaxUvChannels + ". Remove a UV semantic or merge two layers before building.",
                    detail: "requiredChannels=" + ordered.Count + "; maxChannels=" + ApaMeshLimits.MaxUvChannels +
                            "; semantics=" + string.Join(",", ordered.ToArray())));
                return null;
            }

            var channels = new List<UvChannelAssignment>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                channels.Add(new UvChannelAssignment(ordered[i], i, perSemantic[ordered[i]]));
            }

            // The automatic channels were flagged while the sources were collected, so the record states what the
            // resolver actually generated. Reading the flag back — instead of recognizing a generated name — is
            // what keeps an author's own "UV0" declaration from being reported as something the plugin invented.
            var autoPreserved = CollectAutoPreservedChannels(sources, channels);

            return new UvLayout(channels, autoPreserved);
        }

        /// <summary>
        /// Builds the source declarations for the base body and every part, in the fixed ordering that makes
        /// the final channel assignment deterministic.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every present channel of every source is contributed: the ones the profile declares explicitly, and
        /// the ones it does not, under a generated passthrough semantic
        /// (<see cref="ApaWellKnownSemantics.AutoChannelNameFor"/>). A source with no UVs at all contributes
        /// nothing, so an untextured part cannot silently allocate a channel.
        /// </para>
        /// <para>
        /// The automatic channel is a real contribution rather than a diagnostic, which is what makes "a channel
        /// the profile never names" stop being a defect: the values reach the assembled mesh, the layer
        /// participates in the weld/split decision like any other semantic, and two parts that both carry an
        /// undeclared channel 0 land on the <i>same</i> final channel because they land on the same semantic.
        /// </para>
        /// <para>
        /// The one case that still blocks is a present channel that cannot be represented at all: a channel whose
        /// automatic name is already taken by a different declared semantic of the same source, and a present
        /// channel 0 that a non-empty partial declaration used to suppress (which the automatic contribution now
        /// handles instead). <c>APA005</c> remains the answer when the automatic channels push the layout past
        /// Unity's eight.
        /// </para>
        /// </remarks>
        public static List<UvSemanticSource> CollectSources(
            ValidationContext context,
            List<ValidationIssue> issues)
        {
            var sources = new List<UvSemanticSource>();

            if (context.Base != null && context.Base.Mesh != null)
            {
                AppendSource(sources, context.Base.Mesh, context.Base.ExpectedUvSemantics, string.Empty, issues);
            }

            // Base first, then parts in canonical identity order. This is the ordering that makes the final
            // channel numbering deterministic; it is applied here rather than trusted from the caller.
            var parts = ValidationContext.SortParts(context.Parts);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part?.Mesh == null) continue;
                AppendSource(sources, part.Mesh, part.UvSemantics, part.PartId, issues);
            }

            return sources;
        }

        /// <summary>
        /// Appends one source's contributions: its declarations first, then one automatic channel per present
        /// channel no declaration names.
        /// </summary>
        private static void AppendSource(
            List<UvSemanticSource> sources,
            MeshSnapshot mesh,
            IReadOnlyList<ApaUvChannelSemantic> declared,
            string partId,
            List<ValidationIssue> issues)
        {
            var declaredChannels = new List<int>();
            var declaredNames = new HashSet<string>(System.StringComparer.Ordinal);

            if (declared != null)
            {
                for (var i = 0; i < declared.Count; i++)
                {
                    var entry = declared[i];
                    if (entry == null) continue;

                    var name = ApaSemanticName.Normalize(entry.Semantic);
                    if (ApaSemanticName.IsValid(name)) declaredNames.Add(name);

                    if (entry.SourceChannel < 0 || entry.SourceChannel >= ApaMeshLimits.MaxUvChannels)
                    {
                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.InvalidSemanticName,
                            ApaIssuePhase.Uv,
                            "UV semantic '" + name + "' declares source channel " +
                            entry.SourceChannel + ", which is outside 0 through " +
                            (ApaMeshLimits.MaxUvChannels - 1) + ".",
                            partId,
                            entry.SourceChannel,
                            detail: "semantic=" + name + "; channel=" + entry.SourceChannel));
                        continue;
                    }

                    declaredChannels.Add(entry.SourceChannel);
                    sources.Add(new UvSemanticSource(entry.Semantic, entry.SourceChannel, partId));
                }
            }

            var present = mesh != null ? mesh.PresentUvChannels() : new List<int>();
            if (present.Count == 0) return;

            for (var i = 0; i < present.Count; i++)
            {
                var channel = present[i];
                if (declaredChannels.Contains(channel)) continue;

                var semantic = ApaWellKnownSemantics.AutoChannelNameFor(channel, declaredNames);
                if (semantic == null)
                {
                    // Every candidate name is already an explicit semantic of this source, so the channel cannot
                    // be given a stable passthrough identity. Substituting the other name would silently merge
                    // this channel with the layer the author declared under it, which is the one thing an
                    // automatic channel must never do.
                    issues.Add(AutoChannelNameTaken(partId, channel, present, declaredNames));
                    continue;
                }

                sources.Add(new UvSemanticSource(semantic, channel, partId, autoPreserved: true));
            }
        }

        /// <summary>
        /// The automatic channels of a resolved layout, one record per source and channel, in final channel order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A contribution is automatic when <see cref="UvSemanticSource.AutoPreserved"/> says the collection pass
        /// generated it for a channel no declaration named. Nothing here looks at the semantic's <i>name</i>: a
        /// generated name is only a convention, and an author who declares <c>UV0</c>, <c>UV1</c> or
        /// <c>PassthroughUVn</c> explicitly is not reporting a plugin invention.
        /// </para>
        /// <para>
        /// Every flagged contribution produces a record that names its own source, so two sources that share one
        /// generated semantic each report once. The contribution is matched to the final channel by semantic, and
        /// the source channel is re-read from the layout, so a record states what the layout will actually read
        /// rather than what the resolver intended.
        /// </para>
        /// </remarks>
        private static List<UvAutoPreservedChannel> CollectAutoPreservedChannels(
            IReadOnlyList<UvSemanticSource> sources,
            IReadOnlyList<UvChannelAssignment> channels)
        {
            var result = new List<UvAutoPreservedChannel>();

            for (var c = 0; c < channels.Count; c++)
            {
                var channel = channels[c];

                for (var s = 0; s < sources.Count; s++)
                {
                    var source = sources[s];
                    if (!source.AutoPreserved) continue;
                    if (!ApaSemanticName.AreEqual(source.Semantic, channel.Semantic)) continue;

                    // A source can only be recorded for the channel the layout actually reads for it. A duplicate
                    // declaration by the same source is refused during resolution, so this can only differ when a
                    // later refactor changes that rule — and then the record must follow the layout, not the list.
                    if (channel.FindSourceChannel(source.PartId) != source.SourceChannel) continue;

                    result.Add(new UvAutoPreservedChannel(
                        channel.Semantic,
                        source.SourceChannel,
                        channel.OutputChannel,
                        source.PartId));
                }
            }

            return result;
        }

        /// <summary>
        /// Reports a present UV channel that no automatic name could represent.
        /// </summary>
        /// <remarks>
        /// The automatic contribution handles every ordinary case, so this is only reached for the one
        /// configuration it must not resolve by guessing: a present channel whose passthrough name is already an
        /// explicit semantic of the same source. Reporting it as a blocking <c>APA036</c> keeps the registered
        /// meaning of the code — "a present channel would be dropped without a diagnostic" — while the ordinary
        /// undeclared channel is now an informational preservation summary instead.
        /// </remarks>
        private static ValidationIssue AutoChannelNameTaken(
            string partId,
            int channel,
            List<int> present,
            IReadOnlyCollection<string> declaredNames)
        {
            return ValidationIssue.Error(
                ApaErrorCode.UndeclaredUvChannel,
                ApaIssuePhase.Uv,
                "The mesh of " + DescribeSource(partId) + " carries UV channel " + channel +
                ", but every passthrough semantic name for it is already declared by this source, so the channel " +
                "cannot be preserved under a name of its own. Declare channel " + channel +
                " explicitly, or remove the unused UV layer from the source asset.",
                partId,
                channel,
                detail: "reason=auto-channel-name-taken" +
                        "; source=" + DescribeSource(partId) +
                        "; presentChannels=" + DescribeChannels(present) +
                        "; undeclaredChannel=" + channel +
                        "; declaredSemantics=" + JoinDeclared(declaredNames));
        }

        private static string JoinDeclared(IEnumerable<string> declaredSummary)
        {
            var sb = new System.Text.StringBuilder();
            var first = true;
            foreach (var entry in declaredSummary)
            {
                if (!first) sb.Append(',');
                sb.Append(entry);
                first = false;
            }

            return sb.ToString();
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
    }

    /// <summary>
    /// Semantic names the plugin assigns by convention when an author declares nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The names are generated per <i>physical</i> channel, never per source: two parts that both carry an
    /// undeclared channel 0 both produce <c>UV0</c>, so they land on one final channel instead of each consuming
    /// one. That alignment is the whole point — an automatic channel is a passthrough of a physical layer, and
    /// the layer is the same layer on both sides.
    /// </para>
    /// <para>
    /// <c>UV0</c> is the conventional name for the first channel and is the name the convenience path has always
    /// used; channel <c>n</c> above it is <c>UVn</c>. When the author has already declared that name for a
    /// different layer of the same source, the resolver falls back to <c>PassthroughUVn</c>, and when that is
    /// taken too the channel is reported rather than merged into a layer the author named.
    /// </para>
    /// </remarks>
    public static class ApaWellKnownSemantics
    {
        /// <summary>The default name given to a source's first UV channel.</summary>
        public const string Uv0 = "UV0";

        /// <summary>Prefix of the fallback name used when the conventional name is already declared.</summary>
        public const string PassthroughPrefix = "PassthroughUV";

        /// <summary>
        /// The semantic an undeclared present channel is preserved under, or null when every candidate name is
        /// already declared by the same source.
        /// </summary>
        /// <param name="channel">The physical source channel, 0 through 7.</param>
        /// <param name="declaredNames">
        /// The normalized semantic names the source declares explicitly. May be null. A generated name is never
        /// allowed to collide with one of them.
        /// </param>
        public static string AutoChannelNameFor(int channel, ICollection<string> declaredNames)
        {
            var conventional = channel == 0 ? Uv0 : "UV" + channel;
            if (declaredNames == null || !declaredNames.Contains(conventional)) return conventional;

            var fallback = PassthroughPrefix + channel;
            if (!declaredNames.Contains(fallback)) return fallback;

            return null;
        }

        /// <summary>True when a semantic name is one this type generates for an undeclared channel.</summary>
        /// <remarks>
        /// <para>
        /// Recognition is structural rather than a lookup of the channel list, so a name the author declared that
        /// happens to match the pattern — <c>UV0</c>, <c>UV1</c>, <c>PassthroughUVn</c> — is recognized too.
        /// </para>
        /// <para>
        /// This is a naming predicate only, and deliberately <b>not</b> how the layout decides what it preserved
        /// automatically: that question is answered by <see cref="UvLayout.AutoPreservedChannels"/>, which records
        /// the contributions the resolver generated rather than the names they happen to have. A caller asking
        /// "did the plugin invent this layer?" must read the record, not this method.
        /// </para>
        /// </remarks>
        public static bool IsAutoChannelName(string semantic)
        {
            var name = ApaSemanticName.Normalize(semantic);
            if (string.IsNullOrEmpty(name)) return false;
            if (string.Equals(name, Uv0, System.StringComparison.Ordinal)) return true;
            if (name.StartsWith(PassthroughPrefix, System.StringComparison.Ordinal))
            {
                return IsChannelSuffix(name.Substring(PassthroughPrefix.Length));
            }

            return name.Length > 2
                   && name[0] == 'U'
                   && name[1] == 'V'
                   && IsChannelSuffix(name.Substring(2));
        }

        private static bool IsChannelSuffix(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return false;

            for (var i = 0; i < suffix.Length; i++)
            {
                if (suffix[i] < '0' || suffix[i] > '9') return false;
            }

            return true;
        }
    }
}

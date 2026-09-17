using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the undeclared-UV-channel rule as M11 states it: a channel a source mesh actually carries and the
    /// profile never names is <b>preserved automatically</b> under a generated passthrough semantic, so nothing is
    /// discarded and nothing blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before M11 the condition was a blocking <c>APA036</c>. That refused an ordinary import: a part whose mesh
    /// carries UV0 while its profile declares only UV1 is a normal asset, not a modelling defect, and the
    /// blocking rule made the assembly impossible without editing the source mesh. The channel is now
    /// contributed, so section 15's "no silent discard" holds without a refusal.
    /// </para>
    /// <para>
    /// The naming is per <i>physical</i> channel, never per source: two parts that both carry an undeclared
    /// channel 0 both produce <c>UV0</c> and therefore land on one final channel instead of one channel each.
    /// That alignment is the property these tests pin.
    /// </para>
    /// <para>
    /// <c>APA036</c> keeps a blocking form for the one case the resolver must not guess at — every candidate
    /// passthrough name is already an explicit semantic of the same source — and <c>APA005</c> still refuses a
    /// layout the automatic channels push past Unity's eight.
    /// </para>
    /// </remarks>
    public sealed class UvChannelDeclarationTests
    {
        /// <summary>An undeclared present channel is preserved, not blocked, and reported once.</summary>
        [Test]
        public void UndeclaredPresentChannel_IsAutoPreserved()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var issue = FindByCodeForPart(result, ApaErrorCode.UndeclaredUvChannel, "part-a");
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Info, issue.Severity, "Preserving a channel is not a defect.");
            StringAssert.Contains("reason=uv-channel-auto-preserved", issue.Detail);
            StringAssert.Contains("autoPreservedChannels=1", issue.Detail);
            StringAssert.Contains("source=part 'part-a'", issue.Detail);

            Assert.GreaterOrEqual(
                result.Plan.UvLayout.FindOutputChannel("UV1"),
                0,
                "The undeclared channel must reach the output layout under its passthrough semantic.");
            Assert.AreEqual(
                1,
                result.Plan.UvLayout.FindSourceChannel("UV1", "part-a"),
                "The passthrough semantic reads the physical channel the mesh carries.");

            // The record names the source it was created for. The base body's own undeclared channel 0 is a
            // separate record, so neither source can be reported on the other's behalf.
            var partAuto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            Assert.AreEqual(1, partAuto.Count, "The part has exactly one automatic channel.");
            Assert.AreEqual("part-a", partAuto[0].PartId, "The record names the part it belongs to.");
            Assert.AreEqual("UV1", partAuto[0].Semantic);
            Assert.AreEqual(1, partAuto[0].SourceChannel);
            Assert.AreEqual(1, partAuto[0].OutputChannel);

            var baseAuto = result.Plan.UvLayout.AutoPreservedFor(string.Empty);
            Assert.AreEqual(1, baseAuto.Count, "The base body's undeclared channel 0 is its own record.");
            Assert.AreEqual(string.Empty, baseAuto[0].PartId, "The base body's record names the base.");
            Assert.AreEqual(ApaWellKnownSemantics.Uv0, baseAuto[0].Semantic);
            Assert.AreEqual(2, result.Plan.UvLayout.AutoPreservedChannels.Count,
                "One record per source and channel.");
        }

        /// <summary>Every undeclared present channel is preserved and listed together, not one at a time.</summary>
        [Test]
        public void SeveralUndeclaredChannels_ArePreservedTogether()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithChannels(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5),
                MultiPartFixtures.RampUv(5, 0.1f),
                MultiPartFixtures.RampUv(5, 0.2f));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var issue = FindByCodeForPart(result, ApaErrorCode.UndeclaredUvChannel, "part-a");
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Info, issue.Severity);
            StringAssert.Contains("autoPreservedChannels=1,2,3", issue.Detail);
            StringAssert.Contains("autoSemantics=UV1@1,UV2@2,UV3@3", issue.Detail);

            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel("UV1"), 0);
            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel("UV2"), 0);
            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel("UV3"), 0);

            var auto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            Assert.AreEqual(3, auto.Count, "Every undeclared channel of the part is recorded for the part.");
            for (var i = 0; i < auto.Count; i++)
            {
                Assert.AreEqual("part-a", auto[i].PartId, "Record " + i + " names the part.");
                Assert.AreEqual(i + 1, auto[i].SourceChannel, "Record " + i + " reads channel " + (i + 1) + ".");
            }
        }

        /// <summary>A fully declared source is accepted and allocates no automatic channel.</summary>
        [Test]
        public void FullyDeclaredChannels_AreAccepted()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[]
                {
                    new ApaUvChannelSemantic("UVMap", 0),
                    new ApaUvChannelSemantic("DetailUV", 1)
                });

            // The base declares its own channel 0 too, so neither source has an undeclared channel.
            var context = MeshFixtures.Context(
                body,
                new[] { part },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.UndeclaredUvChannel),
                "Nothing was undeclared, so nothing was preserved automatically.");
            Assert.AreEqual(2, result.Plan.UvLayout.ChannelCount);
            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedChannels.Count);
        }

        /// <summary>
        /// A source with only channel 0 present needs no declaration: channel 0 is implicitly <c>UV0</c>, and the
        /// record names the source that relied on the convention rather than the one that declared the name.
        /// </summary>
        [Test]
        public void Uv0OnlyConveniencePath_IsPreserved()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f);

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm);

            // The base declares channel 0, so only the part relies on the implicit path.
            var context = MeshFixtures.Context(
                body,
                new[] { part },
                baseUvSemantics: new[] { new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            // The base body declared UV0 itself, so the plugin invented nothing for it. Attributing the part's
            // automatic channel to the base — which a name-only reading does, because "UV0" looks generated —
            // would report the base for a layer the author named.
            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedFor(string.Empty).Count,
                "An explicit UV0 declaration is not an automatic channel.");

            var auto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            Assert.AreEqual(1, auto.Count, "The part's undeclared channel 0 is preserved once.");
            Assert.AreEqual("part-a", auto[0].PartId);
            Assert.AreEqual(ApaWellKnownSemantics.Uv0, auto[0].Semantic);
            Assert.AreEqual(0, auto[0].SourceChannel);
            Assert.AreEqual(1, result.Plan.UvLayout.AutoPreservedChannels.Count,
                "Exactly one source relied on the implicit path.");

            Assert.AreEqual(
                result.Plan.UvLayout.FindOutputChannel(ApaWellKnownSemantics.Uv0),
                auto[0].OutputChannel,
                "The implicit channel lands on the final channel the declared UV0 already occupies.");

            var summaries = CountByReason(result, "reason=uv-channel-auto-preserved");
            Assert.AreEqual(1, summaries, "Only the part relied on the implicit channel." + result.Issues.FormatAll());
            Assert.IsNotNull(FindByCodeForPart(result, ApaErrorCode.UndeclaredUvChannel, "part-a"));
            Assert.IsNull(
                FindByCodeForPart(result, ApaErrorCode.UndeclaredUvChannel, string.Empty),
                "The base body declared its channel 0, so it must not be reported as preserved." +
                result.Issues.FormatAll());
        }

        /// <summary>
        /// The reported defect: a part mesh that carries UV0 while its profile declares only UV1 must assemble,
        /// and the part's UV0 must reach the final mesh.
        /// </summary>
        [Test]
        public void PartWithUv0ButOnlyUv1Declared_AssemblesAndKeepsUv0()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[] { new ApaUvChannelSemantic("UV1", 1) });

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsTrue(result.Succeeded, "A partially declared source must no longer block." + result.Issues.FormatAll());
            Assert.IsFalse(
                ContainsBlockingUndeclaredChannel(result),
                "No blocking APA036 may remain for an ordinary undeclared channel." + result.Issues.FormatAll());

            Assert.GreaterOrEqual(
                result.Plan.UvLayout.FindOutputChannel("UV1"),
                0,
                "The explicit declaration reaches the output.");
            Assert.AreEqual(
                1,
                result.Plan.UvLayout.FindSourceChannel("UV1", "part-a"),
                "The explicit declaration keeps its own source channel.");
            Assert.GreaterOrEqual(
                result.Plan.UvLayout.FindOutputChannel("UV0"),
                0,
                "The part's undeclared channel 0 must be preserved.");
            Assert.AreEqual(0, result.Plan.UvLayout.FindSourceChannel("UV0", "part-a"));
        }

        /// <summary>
        /// Two parts that both carry an undeclared channel 0 share one passthrough semantic and therefore one
        /// final channel, rather than each part consuming a channel of its own.
        /// </summary>
        [Test]
        public void UndeclaredChannelZeroOfSeveralParts_LandsOnOneFinalChannel()
        {
            var body = MeshFixtures.Body(4);

            // Both parts carry the same ArmDecal values, so the only thing under test is the automatic channel:
            // two parts that disagree on a semantic they both weld would be APA025, not a UV-layout question.
            var decal = MultiPartFixtures.RampUv(5, 0.25f);

            var partA = MultiPartFixtures.Part(
                "part-a",
                MultiPartFixtures.WithUv1(MultiPartFixtures.RingMeshWithUv0("PartA", 4, -1f), decal),
                MeshFixtures.Seam(4),
                ApaPartSlot.LeftArm,
                uvSemantics: new[] { new ApaUvChannelSemantic("ArmDecal", 1) });

            var partB = MultiPartFixtures.Part(
                "part-b",
                MultiPartFixtures.WithUv1(MultiPartFixtures.RingMeshWithUv0("PartB", 4, -1f), decal),
                MeshFixtures.Seam(4),
                ApaPartSlot.RightArm,
                uvSemantics: new[] { new ApaUvChannelSemantic("ArmDecal", 1) });

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var channel = result.Plan.UvLayout.FindOutputChannel(ApaWellKnownSemantics.Uv0);
            Assert.GreaterOrEqual(channel, 0, "Both parts' undeclared channel 0 share the UV0 semantic.");

            Assert.AreEqual(0, result.Plan.UvLayout.FindSourceChannel(ApaWellKnownSemantics.Uv0, "part-a"));
            Assert.AreEqual(0, result.Plan.UvLayout.FindSourceChannel(ApaWellKnownSemantics.Uv0, "part-b"));

            // The shared semantic is one channel: UV0 plus ArmDecal.
            Assert.AreEqual(2, result.Plan.UvLayout.ChannelCount,
                "A per-part channel would have produced three." + result.Issues.FormatAll());

            // One record per source even though all three land on the same semantic and final channel: the merge
            // is what makes the layout small, and the per-source records are what make the report exact.
            Assert.AreEqual(3, result.Plan.UvLayout.AutoPreservedChannels.Count,
                "The base and both parts each contribute an automatic channel." + result.Issues.FormatAll());

            var partAAuto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            var partBAuto = result.Plan.UvLayout.AutoPreservedFor("part-b");
            var baseAuto = result.Plan.UvLayout.AutoPreservedFor(string.Empty);
            Assert.AreEqual(1, partAAuto.Count);
            Assert.AreEqual(1, partBAuto.Count);
            Assert.AreEqual(1, baseAuto.Count);

            Assert.AreEqual("part-a", partAAuto[0].PartId);
            Assert.AreEqual("part-b", partBAuto[0].PartId);
            Assert.AreEqual(string.Empty, baseAuto[0].PartId);

            Assert.AreEqual(ApaWellKnownSemantics.Uv0, partAAuto[0].Semantic);
            Assert.AreEqual(ApaWellKnownSemantics.Uv0, partBAuto[0].Semantic);
            Assert.AreEqual(channel, partAAuto[0].OutputChannel, "Both parts report the shared final channel.");
            Assert.AreEqual(channel, partBAuto[0].OutputChannel);
            Assert.AreEqual(0, partAAuto[0].SourceChannel, "Each part reads its own physical channel 0.");
            Assert.AreEqual(0, partBAuto[0].SourceChannel);
        }

        /// <summary>
        /// An automatic channel never overwrites an explicitly declared semantic: when the conventional name is
        /// taken by another layer, the channel is preserved under the passthrough fallback instead.
        /// </summary>
        [Test]
        public void AutoChannelName_DoesNotOverwriteAnExplicitSemantic()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f));

            // The author named channel 1 "UV0", which is the name the automatic pass would give to channel 0.
            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[] { new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 1) });

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(
                1,
                result.Plan.UvLayout.FindSourceChannel(ApaWellKnownSemantics.Uv0, "part-a"),
                "The explicit declaration keeps its own channel.");
            Assert.AreEqual(
                0,
                result.Plan.UvLayout.FindSourceChannel(ApaWellKnownSemantics.PassthroughPrefix + "0", "part-a"),
                "The automatic channel falls back to the passthrough name instead of merging into the declared one.");

            var auto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            Assert.AreEqual(1, auto.Count, "The fallback channel is the part's only automatic channel.");
            Assert.AreEqual("part-a", auto[0].PartId);
            Assert.AreEqual(ApaWellKnownSemantics.PassthroughPrefix + "0", auto[0].Semantic);
            Assert.AreEqual(0, auto[0].SourceChannel);
        }

        /// <summary>
        /// A declaration whose name is exactly the generated pattern is not an automatic channel: the record
        /// states what the resolver generated, not what a name looks like.
        /// </summary>
        [Test]
        public void ExplicitGeneratedLookingNames_AreNotReportedAsAutoPreserved()
        {
            var body = MeshFixtures.Body(4);

            // Channels 0 and 1 are both present and both declared, under the conventional names.
            var partMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[]
                {
                    new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 0),
                    new ApaUvChannelSemantic("UV1", 1)
                });

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { part },
                baseUvSemantics: new[] { new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 0) }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.UvLayout.ChannelCount);

            // The names look generated to the naming predicate ...
            Assert.IsTrue(
                ApaWellKnownSemantics.IsAutoChannelName(ApaWellKnownSemantics.Uv0),
                "The predicate recognizes the conventional name structurally.");
            Assert.IsTrue(ApaWellKnownSemantics.IsAutoChannelName("UV1"));

            // ... but the resolver generated nothing, so nothing is recorded and nothing is reported.
            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedChannels.Count);
            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedFor("part-a").Count);
            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedFor(string.Empty).Count);
            Assert.AreEqual(
                0,
                CountByReason(result, "reason=uv-channel-auto-preserved"),
                "An explicit UV0/UV1 declaration must not be reported as auto-preserved." +
                result.Issues.FormatAll());
        }

        /// <summary>The passthrough fallback name is a convention too: declaring it is not an automatic channel.</summary>
        [Test]
        public void ExplicitPassthroughName_IsNotReportedAsAutoPreserved()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f);

            // The author named channel 0 with the fallback name the resolver would have generated.
            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[]
                {
                    new ApaUvChannelSemantic(ApaWellKnownSemantics.PassthroughPrefix + "0", 0)
                });

            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { part },
                baseUvSemantics: new[] { new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 0) }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.UvLayout.ChannelCount);
            Assert.IsTrue(
                ApaWellKnownSemantics.IsAutoChannelName(ApaWellKnownSemantics.PassthroughPrefix + "0"),
                "The predicate recognizes the fallback name structurally.");

            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedChannels.Count);
            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedFor("part-a").Count);
            Assert.AreEqual(
                0,
                CountByReason(result, "reason=uv-channel-auto-preserved"),
                "An explicit PassthroughUV0 declaration must not be reported as auto-preserved." +
                result.Issues.FormatAll());
        }

        /// <summary>
        /// The record follows the source that relied on the convention, not the first source that happens to feed
        /// the semantic — even when the two read different physical channels.
        /// </summary>
        [Test]
        public void AutoPreservedRecord_FollowsTheUndeclaredSource()
        {
            // The base declares UV0 on channel 1. Its channel 0 is undeclared, so it is preserved under the
            // fallback name; the part's undeclared channel 0 keeps the conventional UV0 name.
            var body = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Body", 4, 1f),
                MultiPartFixtures.RampUv(5, 0.5f));

            var partMesh = MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f);
            var part = MultiPartFixtures.Part("part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm);

            var context = MeshFixtures.Context(
                body,
                new[] { part },
                baseUvSemantics: new[] { new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 1) });

            var result = ApaCore.Plan(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            // A name-only reading attributes the UV0 semantic to the base — the first source feeding it — and
            // then finds no automatic channel for the part at all. The record must instead name each source's own
            // undeclared channel.
            var baseAuto = result.Plan.UvLayout.AutoPreservedFor(string.Empty);
            Assert.AreEqual(1, baseAuto.Count, result.Issues.FormatAll());
            Assert.AreEqual(string.Empty, baseAuto[0].PartId);
            Assert.AreEqual(ApaWellKnownSemantics.PassthroughPrefix + "0", baseAuto[0].Semantic);
            Assert.AreEqual(0, baseAuto[0].SourceChannel);
            Assert.AreEqual(
                1,
                result.Plan.UvLayout.FindSourceChannel(ApaWellKnownSemantics.Uv0, string.Empty),
                "The base's declared UV0 still reads its own channel 1.");

            var partAuto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            Assert.AreEqual(1, partAuto.Count, "The part's undeclared channel 0 must be recorded for the part.");
            Assert.AreEqual("part-a", partAuto[0].PartId);
            Assert.AreEqual(ApaWellKnownSemantics.Uv0, partAuto[0].Semantic);
            Assert.AreEqual(0, partAuto[0].SourceChannel);
            Assert.AreEqual(0, partAuto[0].OutputChannel, "The part reads the final channel UV0 occupies.");

            Assert.AreEqual(
                1,
                CountByReasonForPart(result, "reason=uv-channel-auto-preserved", string.Empty),
                "The base body reports only its fallback channel." + result.Issues.FormatAll());
            Assert.AreEqual(
                1,
                CountByReasonForPart(result, "reason=uv-channel-auto-preserved", "part-a"),
                "The part reports its own automatic channel." + result.Issues.FormatAll());
        }

        /// <summary>
        /// When both candidate names are declared by the same source the channel cannot be represented, and that
        /// is the condition the blocking <c>APA036</c> is reserved for.
        /// </summary>
        [Test]
        public void EveryPassthroughNameTaken_BlocksWithApa036()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithChannels(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f),
                MultiPartFixtures.RampUv(5, 0.5f));

            // Channel 0 is undeclared, and the author has used both names it could be given for other layers.
            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[]
                {
                    new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 1),
                    new ApaUvChannelSemantic(ApaWellKnownSemantics.PassthroughPrefix + "0", 2)
                });

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsFalse(result.Succeeded, "A channel with no representable name must block.");
            var issue = FindBlockingByCode(result, ApaErrorCode.UndeclaredUvChannel);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Error, issue.Severity);
            StringAssert.Contains("reason=auto-channel-name-taken", issue.Detail);
            StringAssert.Contains("undeclaredChannel=0", issue.Detail);
            StringAssert.Contains("source=part 'part-a'", issue.Detail);
        }

        /// <summary>
        /// The automatic channels count towards the eight-channel limit: an overflow is <c>APA005</c> rather than
        /// a silent drop.
        /// </summary>
        [Test]
        public void AutoPreservedChannels_CountTowardsTheChannelLimit()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithChannels(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.1f),
                MultiPartFixtures.RampUv(5, 0.2f),
                MultiPartFixtures.RampUv(5, 0.3f),
                MultiPartFixtures.RampUv(5, 0.4f),
                MultiPartFixtures.RampUv(5, 0.5f),
                MultiPartFixtures.RampUv(5, 0.6f),
                MultiPartFixtures.RampUv(5, 0.7f));

            // Seven explicit semantics plus the automatic channel 0 is eight; one more explicit semantic pushes
            // the layout past the limit.
            var semantics = new List<ApaUvChannelSemantic>();
            for (var i = 1; i <= 7; i++) semantics.Add(new ApaUvChannelSemantic("Layer" + i, i));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: semantics.ToArray());

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));
            Assert.IsTrue(result.Succeeded, "Eight channels is the accepted boundary." + result.Issues.FormatAll());
            Assert.AreEqual(8, result.Plan.UvLayout.ChannelCount);

            semantics.Add(new ApaUvChannelSemantic("Layer8", 0));
            var overflowing = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: semantics.ToArray());

            var overflowResult = ApaCore.Plan(MeshFixtures.Context(body, new[] { overflowing }));

            Assert.IsFalse(overflowResult.Succeeded, "Nine channels must block, because Unity supports eight.");
            Assert.IsTrue(overflowResult.Issues.ContainsCode(ApaErrorCode.UvChannelOverflow));
        }

        /// <summary>The base body's channels follow the same rule.</summary>
        [Test]
        public void UndeclaredChannelOnTheBase_IsAutoPreservedAndNamesTheBase()
        {
            var baseMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Body", 4, 1f),
                MultiPartFixtures.RampUv(5, 0.5f));

            var part = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4), ApaPartSlot.LeftArm);

            var context = MeshFixtures.Context(
                baseMesh,
                new[] { part },
                baseUvSemantics: new[] { new ApaUvChannelSemantic("UVMap", 0) });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var issue = FindByCodeForPart(result, ApaErrorCode.UndeclaredUvChannel, string.Empty);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Info, issue.Severity);
            StringAssert.Contains("source=the base body", issue.Detail);
            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel("UV1"), 0);

            var baseAuto = result.Plan.UvLayout.AutoPreservedFor(string.Empty);
            Assert.AreEqual(1, baseAuto.Count, "The base body's undeclared channel 1 is recorded for the base.");
            Assert.AreEqual(string.Empty, baseAuto[0].PartId);
            Assert.AreEqual("UV1", baseAuto[0].Semantic);
            Assert.AreEqual(1, baseAuto[0].SourceChannel);

            var partAuto = result.Plan.UvLayout.AutoPreservedFor("part-a");
            Assert.AreEqual(1, partAuto.Count, "The part's own undeclared channel is a separate record.");
            Assert.AreEqual("part-a", partAuto[0].PartId);
            Assert.AreEqual(ApaWellKnownSemantics.Uv0, partAuto[0].Semantic);
        }

        /// <summary>
        /// A previously suppressed implicit channel 0 is now contributed, so a partial declaration no longer
        /// changes what the part contributes.
        /// </summary>
        [Test]
        public void PartiallyDeclaredSource_KeepsChannelZero()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[] { new ApaUvChannelSemantic("ArmDecal", 1) });

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(0, result.Plan.UvLayout.FindSourceChannel("UV0", "part-a"));
            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel("ArmDecal"), 0);
        }

        /// <summary>Declaring channel 0 as well is still the explicit form, and still accepted.</summary>
        [Test]
        public void PartiallyDeclaredSource_WithChannelZeroDeclared_IsAccepted()
        {
            var body = MeshFixtures.Body(4);
            var partMesh = MultiPartFixtures.WithUv1(
                MultiPartFixtures.RingMeshWithUv0("Part", 4, -1f),
                MultiPartFixtures.RampUv(5, 0.25f));

            var part = MultiPartFixtures.Part(
                "part-a", partMesh, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                uvSemantics: new[]
                {
                    new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 0),
                    new ApaUvChannelSemantic("ArmDecal", 1)
                });

            // The base declares its own channel 0 as well, so both sources are fully declared and neither can
            // legitimately be reported as automatically preserved.
            var result = ApaCore.Plan(MeshFixtures.Context(
                body,
                new[] { part },
                baseUvSemantics: new[] { new ApaUvChannelSemantic(ApaWellKnownSemantics.Uv0, 0) }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.UvLayout.ChannelCount, "Both declared channels must be contributed.");
            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel(ApaWellKnownSemantics.Uv0), 0);
            Assert.GreaterOrEqual(result.Plan.UvLayout.FindOutputChannel("ArmDecal"), 0);
            Assert.AreEqual(0, result.Plan.UvLayout.AutoPreservedChannels.Count);
            Assert.AreEqual(0, CountByReason(result, "reason=uv-channel-auto-preserved"),
                "A declared UV0 is not an automatic channel." + result.Issues.FormatAll());
        }

        /// <summary>Exactly eight declared and present channels is the accepted boundary.</summary>
        [Test]
        public void ExactlyEightDeclaredChannels_Succeeds()
        {
            var baseMesh = MultiPartFixtures.WithChannels(
                MeshFixtures.SnapshotWithSubMeshes(
                    "Body",
                    new[] { Vector3.zero, Vector3.right, Vector3.up },
                    new[] { new[] { 0, 1, 2 } }),
                MultiPartFixtures.RampUv(3),
                MultiPartFixtures.RampUv(3, 0.1f),
                MultiPartFixtures.RampUv(3, 0.2f),
                MultiPartFixtures.RampUv(3, 0.3f),
                MultiPartFixtures.RampUv(3, 0.4f),
                MultiPartFixtures.RampUv(3, 0.5f),
                MultiPartFixtures.RampUv(3, 0.6f),
                MultiPartFixtures.RampUv(3, 0.7f));

            var semantics = new List<ApaUvChannelSemantic>();
            for (var i = 0; i < 8; i++) semantics.Add(new ApaUvChannelSemantic("Layer" + i, i));

            var context = MeshFixtures.Context(
                baseMesh,
                new[]
                {
                    MultiPartFixtures.Part(
                        "part-a",
                        MeshFixtures.SnapshotWithSubMeshes(
                            "Part",
                            new[] { Vector3.zero, Vector3.right, Vector3.up },
                            new[] { new[] { 0, 1, 2 } }),
                        slot: ApaPartSlot.LeftArm)
                },
                baseUvSemantics: semantics);

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(8, result.Plan.UvLayout.ChannelCount);
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.UndeclaredUvChannel));
        }

        /// <summary>True when a blocking <c>APA036</c> was reported, whatever else the report contains.</summary>
        private static bool ContainsBlockingUndeclaredChannel(PlanningResult result)
        {
            return FindBlockingByCode(result, ApaErrorCode.UndeclaredUvChannel) != null;
        }

        /// <summary>
        /// The first issue with a code that belongs to one source. A report can carry an informational
        /// preservation line for the base body and one for each part, so a test that asserts on a part's line must
        /// select it by the source it names rather than take the first issue with the code.
        /// </summary>
        private static ValidationIssue FindByCodeForPart(PlanningResult result, string code, string partId)
        {
            for (var i = 0; i < result.Issues.Issues.Count; i++)
            {
                var issue = result.Issues.Issues[i];
                if (issue.Code != code) continue;
                if (!string.Equals(issue.PartId ?? string.Empty, partId ?? string.Empty, System.StringComparison.Ordinal))
                {
                    continue;
                }

                return issue;
            }

            return null;
        }

        /// <summary>Counts the issues whose detail carries a stable reason token, for one source.</summary>
        private static int CountByReasonForPart(PlanningResult result, string reason, string partId)
        {
            var count = 0;
            for (var i = 0; i < result.Issues.Issues.Count; i++)
            {
                var issue = result.Issues.Issues[i];
                if (!string.Equals(issue.PartId ?? string.Empty, partId ?? string.Empty, System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (issue.Detail != null && issue.Detail.Contains(reason)) count++;
            }

            return count;
        }

        /// <summary>Counts the issues whose detail carries a stable reason token.</summary>
        private static int CountByReason(PlanningResult result, string reason)
        {
            var count = 0;
            for (var i = 0; i < result.Issues.Issues.Count; i++)
            {
                var detail = result.Issues.Issues[i].Detail;
                if (detail != null && detail.Contains(reason)) count++;
            }

            return count;
        }

        /// <summary>
        /// The first blocking issue with a code. A report can carry both forms of <c>APA036</c> — the base body's
        /// informational preservation and a part's blocking refusal — so a test that asserts on the blocking form
        /// must not simply take the first issue with the code.
        /// </summary>
        private static ValidationIssue FindBlockingByCode(PlanningResult result, string code)
        {
            for (var i = 0; i < result.Issues.Issues.Count; i++)
            {
                var issue = result.Issues.Issues[i];
                if (issue.Code == code && issue.IsBlocking) return issue;
            }

            return null;
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for the strict seam contract: the explicit pairing, the cardinality and index-set checks, and the
    /// true weld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the M1 tests named in the task, reworked for M10. They exercise the pure core, so they run
    /// without any avatar asset, scene, or NDMF build.
    /// </para>
    /// <para>
    /// <b>Correspondence is author data now.</b> The two lists are pairs by position and the resolver consumes
    /// them as given; it no longer searches for a partner in avatar-root local space, so the position epsilon, the
    /// "no candidate within epsilon" refusal, and the "multiple candidates" refusal no longer exist. What the
    /// resolver still decides is whether the pairing it was handed is usable at all.
    /// </para>
    /// </remarks>
    public sealed class SeamResolutionTests
    {
        /// <summary>A matching seam of equal cardinality resolves and reports a complete match.</summary>
        [Test]
        public void EqualCardinality_MatchesEveryVertex()
        {
            var body = MeshFixtures.Body(32);
            var part = MeshFixtures.Part(32, apexOffset: -1f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(32))
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, "A 32 versus 32 seam must plan successfully.");
            Assert.IsTrue(result.Issues.IsValid);

            var resolution = result.Plan.SeamResolutions["part-a"];
            Assert.AreEqual(32, resolution.Count, "Every part seam vertex must be matched.");
            Assert.IsTrue(resolution.IsComplete);
        }

        /// <summary>A cardinality mismatch is <c>APA001</c> and blocks.</summary>
        [Test]
        public void CardinalityMismatch_ReportsApa001()
        {
            var body = MeshFixtures.Body(32);
            var part = MeshFixtures.Part(31, apexOffset: -1f);

            // Build an explicitly paired seam with mismatched lengths rather than relying on the fixture.
            var seam = new ApaSeamProfile();
            seam.SetPaired(Range(32), Range(31));

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, seam)
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "A cardinality mismatch must block planning.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.SeamVertexCountMismatch));
        }

        /// <summary>
        /// A seam whose lists are not in ascending index order is consumed in pair order, which is what makes the
        /// two lists a correspondence rather than two sets. Nothing sorts them or looks for a partner.
        /// </summary>
        /// <remarks>
        /// This is the M10 replacement for the M1 "matched by position" test. The pairing is still positional, but
        /// the positions are list positions the author wrote, not a nearest-neighbour result the build re-derives.
        /// </remarks>
        [Test]
        public void ExplicitPairOrder_IsConsumedAsWritten()
        {
            var body = MeshFixtures.Body(16);
            var part = MeshFixtures.Part(16, apexOffset: -1f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.ScrambledSeam(16))
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            var resolution = result.Plan.SeamResolutions["part-a"];
            Assert.AreEqual(16, resolution.Count);

            // The fixture pairs base i with part (15 - i), so part vertex (15 - i) maps onto base vertex i.
            for (var i = 0; i < 16; i++)
            {
                Assert.AreEqual(i, resolution.BaseVertexFor(15 - i),
                    "Part vertex " + (15 - i) + " must map to the base vertex at the same pair position.");
            }
        }

        /// <summary>
        /// A pair the author made is a pair even when the two vertices are not coincident: the distance is
        /// reported, never used to accept or reject the weld.
        /// </summary>
        /// <remarks>
        /// The tolerance that decided the pairing is a world-space quantity shown in the authoring window
        /// (<c>ApaSeamWorldMatcher</c>). Re-applying an avatar-root-local epsilon here is exactly the scaled
        /// re-derivation M10 removes, so a displaced pair must weld with a non-zero reported distance.
        /// </remarks>
        [Test]
        public void DisplacedPair_StillWeldsAndReportsItsDistance()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            // Move one part seam vertex clearly beyond any epsilon the build might still apply.
            var displaced = new List<Vector3>(part.Vertices);
            displaced[3] = displaced[3] + new Vector3(0.05f, 0f, 0f);
            var movedPart = MeshFixtures.Snapshot(
                "Part", displaced.ToArray(), Copy(part.SubMeshIndices[0]), Copy(part.GetUvChannel(0)));

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", movedPart, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, "An explicitly paired seam must not be re-matched by position." +
                                            result.Issues.FormatAll());

            var resolution = result.Plan.SeamResolutions["part-a"];
            Assert.AreEqual(8, resolution.Count);

            SeamMatch pair = default;
            var found = false;
            for (var i = 0; i < resolution.Matches.Count; i++)
            {
                if (resolution.Matches[i].PartVertex != 3) continue;
                pair = resolution.Matches[i];
                found = true;
                break;
            }

            Assert.IsTrue(found, "Part vertex 3 must still be paired.");
            Assert.AreEqual(3, pair.BaseVertex, "The pair is the one the profile declared.");
            Assert.AreEqual(0.05f, pair.Distance, 1e-4f, "The pair distance is reported, not enforced.");
            Assert.AreEqual(0f, pair.HashError, "The position-hash error no longer exists.");
        }

        /// <summary>
        /// A seam that carries indices but is not explicitly paired is refused with <c>APA042</c>, because reading
        /// two unordered sets positionally could weld every vertex to an unrelated one.
        /// </summary>
        /// <remarks>
        /// This is the state every profile written before M10 is in, so the refusal and its single remedy —
        /// regenerate the seam from world positions — are the documented upgrade path rather than a silent
        /// re-derivation.
        /// </remarks>
        [Test]
        public void LegacyUnpairedSeam_ReportsApa042()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            // The two lists are written directly, which leaves PairingVersion at the legacy value.
            var seam = new ApaSeamProfile
            {
                Base = new ApaSeamSide(Range(8)),
                Part = new ApaSeamSide(Range(8))
            };

            Assert.IsFalse(seam.HasExplicitPairing, "The fixture must build a legacy seam for this test to mean anything.");

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, seam)
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "A legacy, unpaired seam must block.");
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.SeamPairingRequired), result.Issues.FormatAll());

            var issue = result.Issues.FindByCode(ApaErrorCode.SeamPairingRequired);
            StringAssert.Contains("reason=seam-pairing-required", issue.Detail);
            StringAssert.Contains("pairingVersion=0", issue.Detail);
        }

        /// <summary>
        /// Two base vertices at the same position are not ambiguous when the pairing is explicit: the author said
        /// which part vertex welds onto which base vertex, so coincidence carries no meaning.
        /// </summary>
        /// <remarks>
        /// The M1 version of this test asserted <c>APA003</c> for exactly this fixture, because a
        /// nearest-neighbour search could not choose between the coincident vertices. That ambiguity is gone with
        /// the search; the same input is now a complete, unambiguous weld.
        /// </remarks>
        [Test]
        public void CoincidentBasePositions_AreNotAmbiguousWhenPaired()
        {
            var bodyPositions = new List<Vector3>(MeshFixtures.Ring(4, 1f));
            // Two base seam vertices at the identical position.
            bodyPositions.Add(bodyPositions[0]);
            bodyPositions.Add(new Vector3(0f, 0f, 1f));

            var triangles = new[]
            {
                0, 1, 5,
                1, 2, 5,
                2, 3, 5,
                3, 0, 5
            };

            var body = MeshFixtures.Snapshot("Body", bodyPositions.ToArray(), triangles, MeshFixtures.RingUvs(4));
            var part = MeshFixtures.Part(4, apexOffset: -1f);

            var seam = new ApaSeamProfile();
            // Base seam deliberately includes both coincident vertices, each in its own pair.
            seam.SetPaired(new[] { 0, 4, 1, 2 }, new[] { 0, 1, 2, 3 });

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, seam)
            });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            var resolution = result.Plan.SeamResolutions["part-a"];
            Assert.AreEqual(4, resolution.Count);
            Assert.AreEqual(4, resolution.BaseVertexFor(1),
                "Part vertex 1 must weld onto the coincident base vertex the author paired it with.");
        }

        /// <summary>A duplicated index inside one seam side is <c>APA018</c>.</summary>
        [Test]
        public void DuplicateIndexWithinSeam_ReportsApa018()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var seam = new ApaSeamProfile();
            seam.SetPaired(new[] { 0, 1, 1, 2 }, new[] { 0, 1, 2, 3 });

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, seam)
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.InvalidSeamSelection));
        }

        /// <summary>An out-of-range seam index is <c>APA018</c>.</summary>
        [Test]
        public void OutOfRangeSeamIndex_ReportsApa018()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);

            var seam = new ApaSeamProfile();
            seam.SetPaired(new[] { 0, 1, 2, 999 }, new[] { 0, 1, 2, 3 });

            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, seam)
            });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Issues.ContainsCode(ApaErrorCode.InvalidSeamSelection));
        }

        /// <summary>
        /// The true weld deletes the part seam vertices: no part seam vertex may appear in the output, and every
        /// triangle index must go through the remap.
        /// </summary>
        [Test]
        public void TrueWeld_EmitsNoPartSeamVertex()
        {
            var body = MeshFixtures.Body(8);
            var part = MeshFixtures.Part(8, apexOffset: -1f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(8))
            });

            var result = ApaCore.Plan(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            Assert.AreEqual(0, result.Plan.CountEmittedPartSeamVertices(),
                "A true weld must not emit a single part seam vertex.");

            // Every part seam vertex must map onto a retained base vertex, not onto a new part vertex.
            for (var i = 0; i < 8; i++)
            {
                var finalIndex = result.Plan.FinalIndexOf("part-a", i);
                Assert.GreaterOrEqual(finalIndex, 0, "Part seam vertex " + i + " must be remapped.");
                Assert.Less(finalIndex, body.VertexCount,
                    "Part seam vertex " + i + " must map into the retained base range.");
            }

            // The part's apex is not a seam vertex and must be appended after the base vertices.
            var apexFinal = result.Plan.FinalIndexOf("part-a", 8);
            Assert.GreaterOrEqual(apexFinal, body.VertexCount);
        }

        /// <summary>
        /// The total output vertex count equals retained base vertices plus non-seam part vertices. This is the
        /// arithmetic statement of "no duplicate seam vertices are emitted".
        /// </summary>
        [Test]
        public void OutputVertexCount_EqualsRetainedPlusNonSeamPartVertices()
        {
            var body = MeshFixtures.Body(12);
            var part = MeshFixtures.Part(12, apexOffset: -1f);
            var context = MeshFixtures.Context(body, new[]
            {
                MeshFixtures.PartSnapshot("part-a", part, MeshFixtures.Seam(12))
            });

            var result = ApaCore.Plan(context);
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            // 12 base ring vertices plus the body apex are all retained, and only the part apex is added.
            Assert.AreEqual(13 + 1, result.Plan.VertexCount,
                "Only the part's non-seam vertex may be appended to the retained base vertices.");
        }

        private static int[] Range(int count)
        {
            var result = new int[count];
            for (var i = 0; i < count; i++) result[i] = i;
            return result;
        }

        private static T[] Copy<T>(IReadOnlyList<T> source)
        {
            var result = new T[source.Count];
            for (var i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the M6 multi-part conflict policy: removal-overlap priority, slot modes, per-part blend shape
    /// policy, and the material anchor rules.
    /// </summary>
    /// <remarks>
    /// Every test here pins one of the product decisions: defaults still block ambiguity, and only an explicit
    /// author policy resolves a conflict. A test that expected a silent resolution would be asserting the
    /// failure mode the policy exists to prevent.
    /// </remarks>
    public sealed class MultiPartConflictPolicyTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        // ---- removal overlap and conflict priority ----------------------------------------------------

        /// <summary>
        /// Two parts claiming one base triangle, both declaring a distinct priority, resolve to the higher one
        /// with a warning that names the owner and the losers.
        /// </summary>
        [Test]
        public void RemovalOverlap_WithDistinctPriorities_ResolvesWithApa035()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 2, removed: MeshFixtures.Removed(0, 0));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1, removed: MeshFixtures.Removed(0, 0));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var warning = result.Issues.FindByCode(ApaErrorCode.RemovalOverlapResolvedByPriority);
            Assert.IsNotNull(warning, "A priority-resolved overlap must be reported." + result.Issues.FormatAll());
            Assert.AreEqual(ApaSeverity.Warning, warning.Severity, "A resolved overlap is not a blocking issue.");
            StringAssert.Contains("reason=removal-overlap-resolved-by-priority", warning.Detail);
            StringAssert.Contains("owner=part-a", warning.Detail);
            StringAssert.Contains("ownerPriority=2", warning.Detail);
            StringAssert.Contains("part-b@1", warning.Detail);
        }

        /// <summary>Resolution never changes the geometry: the removed set is the union either way.</summary>
        [Test]
        public void RemovalOverlap_ResolvedByPriority_RemovesTheSameUnion()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 5, removed: MeshFixtures.Removed(0, 0, 1));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1, removed: MeshFixtures.Removed(0, 1, 2));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(3, result.Plan.RemovedTriangleCount, "The union of both claims must be removed.");
            for (var triangle = 0; triangle < 3; triangle++)
            {
                Assert.IsTrue(
                    ContainsAddress(result.Plan.RemovedTriangles, new RemovedTriangleAddress(0, triangle)),
                    "Triangle " + triangle + " must be removed.");
            }
        }

        /// <summary>
        /// A resolved overlap is a value on the plan, not only prose in a warning: the plan names the owner of
        /// every removed triangle.
        /// </summary>
        /// <remarks>
        /// Ownership is what the policy actually decides about a contested region — the removed set is the union
        /// either way — so it has to be queryable by anything that later reasons about the region rather than
        /// parsed out of a diagnostic message.
        /// </remarks>
        [Test]
        public void RemovalOverlap_ResolvedByPriority_ExposesTheOwnerOnThePlan()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 2, removed: MeshFixtures.Removed(0, 0, 1));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1, removed: MeshFixtures.Removed(0, 1, 2));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(3, result.Plan.RemovalOwners.Count, "Every removed triangle must have an owner.");

            // Triangle 0 is claimed only by part-a; triangle 1 is contested and belongs to the higher priority
            // (part-a); triangle 2 is claimed only by part-b.
            Assert.AreEqual("part-a", result.Plan.OwnerOf(new RemovedTriangleAddress(0, 0)));
            Assert.AreEqual("part-a", result.Plan.OwnerOf(new RemovedTriangleAddress(0, 1)));
            Assert.AreEqual("part-b", result.Plan.OwnerOf(new RemovedTriangleAddress(0, 2)));

            // A triangle the plan does not remove has no owner, even though a part claims it elsewhere.
            Assert.AreEqual(string.Empty, result.Plan.OwnerOf(new RemovedTriangleAddress(1, 0)));
        }

        /// <summary>The single claim on a triangle is its owner, with no priority involved.</summary>
        [Test]
        public void RemovalWithoutOverlap_ExposesTheSoleClaimantAsOwner()
        {
            var body = MeshFixtures.Body(6);
            var part = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 0, removed: MeshFixtures.Removed(0, 1));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(1, result.Plan.RemovalOwners.Count);
            Assert.AreEqual("part-a", result.Plan.OwnerOf(new RemovedTriangleAddress(0, 1)));
        }

        /// <summary>
        /// <c>RemovalRule.ResolveOwners</c> is the same decision the diagnostics report, including the
        /// unresolvable cases, which have no owner.
        /// </summary>
        [Test]
        public void RemovalOwners_AgreeWithTheResolutionDiagnostics()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 4, removed: MeshFixtures.Removed(0, 0));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1, removed: MeshFixtures.Removed(0, 0));

            var owners = RemovalRule.ResolveOwners(new[] { partA, partB });
            Assert.AreEqual("part-a", owners[new RemovedTriangleAddress(0, 0)]);

            // A tie has no owner: the same configuration blocks, so no plan can expose one.
            var tied = new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 3, removed: MeshFixtures.Removed(0, 0)),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 3, removed: MeshFixtures.Removed(0, 0))
            };

            Assert.AreEqual(0, RemovalRule.ResolveOwners(tied).Count, "A tie has no owner.");
            Assert.IsFalse(
                ApaCore.Plan(MeshFixtures.Context(body, tied)).Succeeded,
                "An ownerless overlap must be blocking, so it can never reach a plan.");
        }

        /// <summary>A single declared priority resolves nothing: every claimant must declare one.</summary>
        [Test]
        public void RemovalOverlap_WithoutPriorityOnEveryClaimant_Blocks()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 9, removed: MeshFixtures.Removed(0, 0));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 0, removed: MeshFixtures.Removed(0, 0));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsFalse(result.Succeeded, "An undeclared claim must keep the overlap blocking.");
            var issue = result.Issues.FindByCode(ApaErrorCode.RemovalRegionOverlap);
            Assert.IsNotNull(issue);
            StringAssert.Contains("reason=undeclared-priority", issue.Detail);
        }

        /// <summary>Equal highest priorities have no defined order, so the overlap stays blocking.</summary>
        [Test]
        public void RemovalOverlap_WithEqualPriorities_Blocks()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 3, removed: MeshFixtures.Removed(0, 0));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 3, removed: MeshFixtures.Removed(0, 0));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsFalse(result.Succeeded, "An equal highest priority must not resolve the overlap.");
            var issue = result.Issues.FindByCode(ApaErrorCode.RemovalRegionOverlap);
            Assert.IsNotNull(issue);
            StringAssert.Contains("reason=priority-tie", issue.Detail);
        }

        /// <summary>A negative priority is outside the defined domain and blocks with its own code.</summary>
        [Test]
        public void NegativeConflictPriority_BlocksWithApa037()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, -1, removed: MeshFixtures.Removed(0, 0));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 2, removed: MeshFixtures.Removed(0, 0));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsFalse(result.Succeeded);
            var issue = result.Issues.FindByCode(ApaErrorCode.InvalidConflictPriority);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            StringAssert.Contains("reason=negative-conflict-priority", issue.Detail);
        }

        /// <summary>Priority never resolves a slot conflict; it is not a general "winner picks" mechanism.</summary>
        [Test]
        public void TwoReplaceParts_OnOneSlot_BlockEvenWithDifferentPriorities()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, ApaPartSlotMode.Replace, 5);
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, ApaPartSlotMode.Replace, 1);

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsFalse(result.Succeeded, "Two Replace parts on one slot must keep blocking.");
            var issue = result.Issues.FindByCode(ApaErrorCode.DuplicatePartSlot);
            Assert.IsNotNull(issue);
            StringAssert.Contains("reason=duplicate-replace-slot", issue.Detail);
        }

        // ---- slot modes ------------------------------------------------------------------------------

        /// <summary>One Replace plus any number of Augment parts describes one region, not a conflict.</summary>
        [Test]
        public void ReplaceAndAugment_OnOneSlot_Succeeds()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, ApaPartSlotMode.Replace);
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, ApaPartSlotMode.Augment);
            var partC = MultiPartFixtures.Part(
                "part-c", MeshFixtures.Part(6, apexOffset: -3f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, ApaPartSlotMode.Augment);

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB, partC }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.DuplicatePartSlot));
        }

        /// <summary>An augmenting part does not own the region, so it has no standing to remove one.</summary>
        [Test]
        public void AugmentPart_WithRemovalTriangles_Blocks()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, ApaPartSlotMode.Replace);
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, ApaPartSlotMode.Augment, removed: MeshFixtures.Removed(0, 3));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsFalse(result.Succeeded, "An Augment part with a removal set must block.");
            var issue = result.Issues.FindByCode(ApaErrorCode.DuplicatePartSlot);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            StringAssert.Contains("reason=augment-declares-removal", issue.Detail);
        }

        /// <summary>An out-of-range slot mode value is unchecked author data and blocks.</summary>
        [Test]
        public void UndefinedSlotModeValue_BlocksWithApa023()
        {
            var body = MeshFixtures.Body(6);
            var part = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.Head, (ApaPartSlotMode)7);

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { part }));

            Assert.IsFalse(result.Succeeded);
            var issue = result.Issues.FindByCode(ApaErrorCode.InvalidPartSlot);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            StringAssert.Contains("reason=undefined-slot-mode", issue.Detail);
        }

        // ---- material anchors ------------------------------------------------------------------------

        /// <summary><c>UseTarget</c> means the base target only: without a base semantic the part keeps its
        /// own material in a new slot (specification 43.5).</summary>
        [Test]
        public void UseTarget_WithoutBaseSemantic_CreatesSlotWithPartMaterial()
        {
            var bodyMaterial = NewMaterial("BodySkin");
            var partMaterial = NewMaterial("PartDecal");

            var body = TriangleMesh("Body");
            var part = TriangleMesh("Part");

            var context = MeshFixtures.Context(
                body,
                new[]
                {
                    MultiPartFixtures.Part(
                        "part-a", part, materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Decal", 0, partMaterial, ApaMaterialPolicyMode.UseTarget)
                        })
                },
                baseMaterials: new[] { bodyMaterial },
                baseMaterialSemantics: new[]
                {
                    new ApaMaterialSlotSemantic("Skin", 0, bodyMaterial, ApaMaterialPolicyMode.Auto)
                });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.MaterialLayout.SlotCount);
            Assert.AreSame(
                partMaterial,
                result.Plan.MaterialLayout.Slots[1].Material,
                "UseTarget without a base semantic must keep the part's own material.");
        }

        /// <summary>Two parts with different assets for one semantic the base lacks block against each other.</summary>
        [Test]
        public void TwoAutoParts_DifferentMaterials_BlockAsPartPartConflict()
        {
            var materialA = NewMaterial("PartASkin");
            var materialB = NewMaterial("PartBSkin");

            var context = MeshFixtures.Context(
                TriangleMesh("Body"),
                new[]
                {
                    MultiPartFixtures.Part(
                        "part-a", TriangleMesh("PartA"), slot: ApaPartSlot.LeftArm, materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Decal", 0, materialA, ApaMaterialPolicyMode.Auto)
                        }),
                    MultiPartFixtures.Part(
                        "part-b", TriangleMesh("PartB"), slot: ApaPartSlot.RightArm, materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Decal", 0, materialB, ApaMaterialPolicyMode.Auto)
                        })
                });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "Parts that disagree on a part-only semantic must block.");
            var issue = result.Issues.FindByCode(ApaErrorCode.MaterialSemanticConflict);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            StringAssert.Contains("reason=part-part-material-conflict", issue.Detail);
            StringAssert.Contains("anchorPart=part-a", issue.Detail);
        }

        /// <summary>Auto has no legitimate anchor once another part deliberately split the semantic.</summary>
        [Test]
        public void KeepPartThenAuto_BlocksWithoutAnAutoAnchor()
        {
            var materialA = NewMaterial("PartASkin");
            var materialB = NewMaterial("PartBSkin");

            var context = MeshFixtures.Context(
                TriangleMesh("Body"),
                new[]
                {
                    MultiPartFixtures.Part(
                        "part-a", TriangleMesh("PartA"), slot: ApaPartSlot.LeftArm, materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Decal", 0, materialA, ApaMaterialPolicyMode.KeepPart)
                        }),
                    MultiPartFixtures.Part(
                        "part-b", TriangleMesh("PartB"), slot: ApaPartSlot.RightArm, materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Decal", 0, materialB, ApaMaterialPolicyMode.Auto)
                        })
                });

            var result = ApaCore.Plan(context);

            Assert.IsFalse(result.Succeeded, "Auto must not merge into a deliberately separated slot.");
            var issue = result.Issues.FindByCode(ApaErrorCode.MaterialSemanticConflict);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            StringAssert.Contains("reason=no-auto-anchor", issue.Detail);
        }

        /// <summary>A deliberate split is visible in the layout so a consumer can tell it from a bug.</summary>
        [Test]
        public void BaseAutoPlusForceNew_SplitsTheSemantic()
        {
            var shared = NewMaterial("M_Skin");

            var context = MeshFixtures.Context(
                TriangleMesh("Body"),
                new[]
                {
                    MultiPartFixtures.Part(
                        "part-a", TriangleMesh("PartA"), materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Skin", 0, shared, ApaMaterialPolicyMode.ForceNew)
                        })
                },
                baseMaterials: new[] { shared },
                baseMaterialSemantics: new[]
                {
                    new ApaMaterialSlotSemantic("Skin", 0, shared, ApaMaterialPolicyMode.Auto)
                });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreEqual(2, result.Plan.MaterialLayout.SlotCount, "ForceNew always gets its own slot.");
            Assert.IsTrue(result.Plan.MaterialLayout.IsSemanticSplit("Skin"));
            Assert.AreEqual(2, result.Plan.MaterialLayout.SemanticCount("Skin"));
            Assert.IsTrue(result.Plan.MaterialLayout.HasExplicitlySeparatedSlot("Skin"));
            Assert.IsTrue(result.Plan.MaterialLayout.Slots[1].IsExplicitlySeparated);
        }

        /// <summary>The base material is never overwritten, whatever policy a part declares.</summary>
        [Test]
        public void PartPolicy_NeverReplacesTheBaseMaterial()
        {
            var bodyMaterial = NewMaterial("BodySkin");
            var partMaterial = NewMaterial("PartSkin");

            var context = MeshFixtures.Context(
                TriangleMesh("Body"),
                new[]
                {
                    MultiPartFixtures.Part(
                        "part-a", TriangleMesh("PartA"), materialSemantics: new[]
                        {
                            new ApaMaterialSlotSemantic("Skin", 0, partMaterial, ApaMaterialPolicyMode.KeepPart)
                        })
                },
                baseMaterials: new[] { bodyMaterial },
                baseMaterialSemantics: new[]
                {
                    new ApaMaterialSlotSemantic("Skin", 0, bodyMaterial, ApaMaterialPolicyMode.Auto)
                });

            var result = ApaCore.Plan(context);

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.AreSame(bodyMaterial, result.Plan.MaterialLayout.Slots[0].Material);
            Assert.IsTrue(result.Plan.MaterialLayout.Slots[0].IsBaseSlot);
        }

        // ---- multi-part geometry ---------------------------------------------------------------------

        /// <summary>
        /// Three parts, one body, one plan: every part lands in the same mesh, with one vertex list, one bone
        /// table, one blend shape set, and one submesh layout.
        /// </summary>
        [Test]
        public void ThreeParts_AllLandInOneMesh()
        {
            var body = MeshFixtures.Body(6);
            var parts = new[]
            {
                MultiPartFixtures.Part(
                    "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6), ApaPartSlot.LeftArm),
                MultiPartFixtures.Part(
                    "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6), ApaPartSlot.RightArm),
                MultiPartFixtures.Part(
                    "part-c", MeshFixtures.Part(6, apexOffset: -3f), MeshFixtures.Seam(6), ApaPartSlot.LeftLeg)
            };

            var result = ApaCore.Assemble(MeshFixtures.Context(body, parts));
            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsNotNull(result.Mesh);

            try
            {
                // One plan, one mesh, and no blocking issue: the three parts were assembled together rather
                // than in three independent builds.
                Assert.AreEqual(0, result.Issues.ErrorCount, result.Issues.FormatAll());

                var apexA = result.Plan.FinalIndexOf("part-a", 6);
                var apexB = result.Plan.FinalIndexOf("part-b", 6);
                var apexC = result.Plan.FinalIndexOf("part-c", 6);
                Assert.GreaterOrEqual(apexA, 0);
                Assert.GreaterOrEqual(apexB, 0);
                Assert.GreaterOrEqual(apexC, 0);
                Assert.AreNotEqual(apexA, apexB);
                Assert.AreNotEqual(apexB, apexC);

                Assert.AreEqual(result.Plan.VertexCount, result.Mesh.vertexCount);
                Assert.AreEqual(1, result.Plan.SubMeshes.Count, "One material semantic merges into one slot.");
                Assert.AreEqual(0, result.Plan.CountEmittedPartSeamVertices());
            }
            finally
            {
                Object.DestroyImmediate(result.Mesh);
            }
        }

        // ---- per-part blend shape policy --------------------------------------------------------------

        /// <summary>
        /// <c>AllowPartOnlyShapes = false</c> refuses a part-only shape even when its seam deltas are zero.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The field was serialized before M6 and nothing read it, so this test is what makes it a live policy
        /// rather than a promise the product does not keep.
        /// </para>
        /// <para>
        /// The refusal has its own code, <c>APA040</c>. It must not be reported as <c>APA029</c>, whose registered
        /// meaning is the blend-shape <i>seam delta</i> condition: the two have different remedies, and a code's
        /// meaning must never change.
        /// </para>
        /// </remarks>
        [Test]
        public void AllowPartOnlyShapes_False_BlocksPartOnlyShape()
        {
            var body = MeshFixtures.Body(4);
            var part = PartOnlyShapeMesh();
            var snapshot = MultiPartFixtures.Part(
                "part-a", part, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                ApaPartSlotMode.Replace, 0, allowPartOnlyShapes: false);

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { snapshot }));

            Assert.IsFalse(result.Succeeded, "The policy must refuse a part-only shape.");
            var issue = result.Issues.FindByCode(ApaErrorCode.PartOnlyShapeDisallowed);
            Assert.IsNotNull(issue, result.Issues.FormatAll());
            Assert.AreEqual("APA040", issue.Code);
            Assert.AreEqual(ApaSeverity.Error, issue.Severity);
            StringAssert.Contains("reason=part-only-shape-disallowed", issue.Detail);
            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.BlendShapeSeamDeltaMismatch),
                "A policy refusal must not reuse APA029, which means a seam-delta conflict: " +
                result.Issues.FormatAll());
        }

        /// <summary>The default policy still accepts a part-only shape whose seam deltas are zero.</summary>
        [Test]
        public void AllowPartOnlyShapes_True_AcceptsZeroDeltaPartOnlyShape()
        {
            var body = MeshFixtures.Body(4);
            var part = PartOnlyShapeMesh();
            var snapshot = MultiPartFixtures.Part(
                "part-a", part, MeshFixtures.Seam(4), ApaPartSlot.LeftArm,
                ApaPartSlotMode.Replace, 0, allowPartOnlyShapes: true);

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { snapshot }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());
            Assert.IsFalse(result.Issues.ContainsCode(ApaErrorCode.BlendShapeSeamDeltaMismatch));
        }

        // ---- helpers ---------------------------------------------------------------------------------

        /// <summary>True when the removed-address list contains the address. Avoids a LINQ dependency.</summary>
        private static bool ContainsAddress(IReadOnlyList<RemovedTriangleAddress> addresses, RemovedTriangleAddress address)
        {
            for (var i = 0; i < addresses.Count; i++)
            {
                if (addresses[i].Equals(address)) return true;
            }

            return false;
        }

        /// <summary>A three-vertex mesh with one triangle, for the material tests.</summary>
        private MeshSnapshot TriangleMesh(string name)
        {
            return MeshFixtures.SnapshotWithSubMeshes(
                name,
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                new[] { new[] { 0, 1, 2 } });
        }

        /// <summary>
        /// A ring-plus-apex part with one shape that exists only on the part, moving the apex (which is not a
        /// seam vertex), so the strict rules accept it and only the policy can refuse it.
        /// </summary>
        private MeshSnapshot PartOnlyShapeMesh()
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { new Vector3(0f, 0f, -1f) };

            return MeshFixtures.BlendShapedSnapshot(
                "Part",
                positions.ToArray(),
                MeshFixtures.CapTriangles(4, 0, 4),
                new[] { "PartOnly" },
                new[] { new[] { MeshFixtures.Frame(100f, positions.Count, 4, new Vector3(0f, 0f, -0.5f)) } });
        }

        private Material NewMaterial(string name)
        {
            var material = MultiPartFixtures.NewMaterial(name);
            _created.Add(material);
            return material;
        }
    }
}

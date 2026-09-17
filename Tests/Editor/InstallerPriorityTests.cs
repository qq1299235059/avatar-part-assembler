using System;
using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the M6 conflict-priority ordering: the key, the part order, and the plan-level effect.
    /// </summary>
    /// <remarks>
    /// Priority is inert until declared. Every test here that expects a change also asserts the legacy order so
    /// that a future change cannot quietly re-order every existing avatar.
    /// </remarks>
    public sealed class InstallerPriorityTests
    {
        /// <summary>A higher declared priority orders earlier.</summary>
        [Test]
        public void OrderingKey_HigherPriorityOrdersFirst()
        {
            var high = new PartOrderingKey(5, ApaPartSlot.RightArm, "part-z", "host-z");
            var low = new PartOrderingKey(1, ApaPartSlot.LeftArm, "part-a", "host-a");

            Assert.Less(high.CompareTo(low), 0, "A higher priority must be processed first.");
            Assert.Greater(low.CompareTo(high), 0);
        }

        /// <summary>With no declared priority the key degenerates to (slot, part id, display name, installer path).</summary>
        [Test]
        public void OrderingKey_WithoutPriority_KeepsTheLegacyOrder()
        {
            var head = new PartOrderingKey(0, ApaPartSlot.Head, "part-z", "part-z", "host-z");
            var leftArm = new PartOrderingKey(0, ApaPartSlot.LeftArm, "part-a", "part-a", "host-a");

            Assert.Less(head.CompareTo(leftArm), 0, "Slot still dominates when no priority is declared.");

            var sameSlotA = new PartOrderingKey(0, ApaPartSlot.LeftArm, "part-a", "part-a", "host-b");
            var sameSlotB = new PartOrderingKey(0, ApaPartSlot.LeftArm, "part-b", "part-b", "host-a");
            Assert.Less(sameSlotA.CompareTo(sameSlotB), 0, "The part id is the second component.");

            var sameNameA = new PartOrderingKey(0, ApaPartSlot.LeftArm, "part-a", "Alpha", "host-b");
            var sameNameB = new PartOrderingKey(0, ApaPartSlot.LeftArm, "part-a", "Beta", "host-a");
            Assert.Less(sameNameA.CompareTo(sameNameB), 0, "The display name is the third component.");

            var sameIdA = new PartOrderingKey(0, ApaPartSlot.LeftArm, "part-a", "Alpha", "host-a");
            var sameIdB = new PartOrderingKey(0, ApaPartSlot.LeftArm, "part-a", "Alpha", "host-b");
            Assert.Less(sameIdA.CompareTo(sameIdB), 0, "The installer path is the final tiebreaker.");
        }

        /// <summary>
        /// The display-name component is part of the key's identity, and the legacy constructors leave it empty.
        /// </summary>
        [Test]
        public void OrderingKey_DisplayNameParticipatesInOrderAndEquality()
        {
            var legacy = new PartOrderingKey(0, ApaPartSlot.Head, "part-a", "host-a");
            var noName = new PartOrderingKey(0, ApaPartSlot.Head, "part-a", string.Empty, "host-a");
            Assert.IsTrue(legacy.Equals(noName), "A key with no display name is the empty display name.");
            Assert.AreEqual(legacy.GetHashCode(), noName.GetHashCode());

            var named = new PartOrderingKey(0, ApaPartSlot.Head, "part-a", "Alpha", "host-a");
            Assert.IsFalse(legacy.Equals(named), "A display name must make two keys different.");
            Assert.Less(noName.CompareTo(named), 0, "The empty display name sorts first.");
            StringAssert.Contains("Alpha", named.ToString());
        }

        /// <summary>
        /// <c>ApaPartIdentity.Compare</c> is the profile-level projection of the ordering key's order, so the two
        /// cannot disagree about any component they share.
        /// </summary>
        /// <remarks>
        /// The key adds only the installer path, which a runtime profile has no way to know: a profile is not
        /// placed in a hierarchy. This test pins the shared components — priority descending, slot, part id,
        /// display name — in one place so a future change to either comparator has to change both.
        /// </remarks>
        [Test]
        public void IdentityCompare_AgreesWithTheOrderingKeyOnEverySharedComponent()
        {
            var cases = new[]
            {
                // priority dominates
                new[] { Identity(1, ApaPartSlot.RightArm, "part-z", "Zulu"), Identity(5, ApaPartSlot.Head, "part-a", "Alpha") },
                // then slot
                new[] { Identity(0, ApaPartSlot.RightArm, "part-a", "Alpha"), Identity(0, ApaPartSlot.Head, "part-z", "Zulu") },
                // then part id
                new[] { Identity(0, ApaPartSlot.Head, "part-b", "Alpha"), Identity(0, ApaPartSlot.Head, "part-a", "Zulu") },
                // then display name
                new[] { Identity(0, ApaPartSlot.Head, "part-a", "Beta"), Identity(0, ApaPartSlot.Head, "part-a", "Alpha") }
            };

            for (var i = 0; i < cases.Length; i++)
            {
                var first = cases[i][0];
                var second = cases[i][1];

                var keyFirst = new PartOrderingKey(
                    first.ConflictPriority, first.Slot, first.PartId, first.DisplayName, "host");
                var keySecond = new PartOrderingKey(
                    second.ConflictPriority, second.Slot, second.PartId, second.DisplayName, "host");

                var identityOrder = Math.Sign(ApaPartIdentity.Compare(first, second));
                var keyOrder = Math.Sign(keyFirst.CompareTo(keySecond));

                Assert.AreEqual(
                    identityOrder,
                    keyOrder,
                    "Case " + i + ": ApaPartIdentity.Compare and PartOrderingKey must agree (" +
                    first.PartId + "/" + first.DisplayName + " vs " + second.PartId + "/" + second.DisplayName + ").");
            }
        }

        private static ApaPartIdentity Identity(
            int priority,
            ApaPartSlot slot,
            string partId,
            string displayName)
        {
            return new ApaPartIdentity
            {
                ConflictPriority = priority,
                Slot = slot,
                PartId = partId,
                DisplayName = displayName
            };
        }

        /// <summary>The three-argument constructor is the schema-2 key, with no priority.</summary>
        [Test]
        public void OrderingKey_ThreeArgumentConstructor_HasNoPriority()
        {
            var legacy = new PartOrderingKey(ApaPartSlot.Head, "part-a", "host-a");
            var explicitZero = new PartOrderingKey(0, ApaPartSlot.Head, "part-a", "host-a");

            Assert.AreEqual(0, legacy.EffectivePriority);
            Assert.IsTrue(legacy.Equals(explicitZero));
            Assert.AreEqual(legacy.GetHashCode(), explicitZero.GetHashCode());
        }

        /// <summary>Priority participates in equality, so two keys that differ only by it are different keys.</summary>
        [Test]
        public void OrderingKey_EqualityAndHashIncludePriority()
        {
            var first = new PartOrderingKey(1, ApaPartSlot.Head, "part-a", "host-a");
            var second = new PartOrderingKey(2, ApaPartSlot.Head, "part-a", "host-a");

            Assert.IsFalse(first.Equals(second));
            Assert.AreNotEqual(first.GetHashCode(), second.GetHashCode());
            Assert.IsTrue(first.Equals(new PartOrderingKey(1, ApaPartSlot.Head, "part-a", "host-a")));
        }

        /// <summary>Sorting parts uses the same key, so the plan order follows the declared priority.</summary>
        [Test]
        public void SortParts_OrdersByDeclaredPriority()
        {
            var parts = new[]
            {
                MultiPartFixtures.Part(
                    "part-c", MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 0),
                MultiPartFixtures.Part(
                    "part-b", MeshFixtures.Part(4, apexOffset: -2f), MeshFixtures.Seam(4),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1),
                MultiPartFixtures.Part(
                    "part-a", MeshFixtures.Part(4, apexOffset: -3f), MeshFixtures.Seam(4),
                    ApaPartSlot.Head, ApaPartSlotMode.Replace, 9)
            };

            var sorted = ValidationContext.SortParts(parts);

            Assert.AreEqual("part-a", sorted[0].PartId);
            Assert.AreEqual("part-b", sorted[1].PartId);
            Assert.AreEqual("part-c", sorted[2].PartId);
        }

        /// <summary>Reversing the declared priorities mirrors the order.</summary>
        [Test]
        public void PriorityReversal_MirrorsTheOrder()
        {
            var forward = ValidationContext.SortParts(new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 2),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(4, apexOffset: -2f), MeshFixtures.Seam(4),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1)
            });

            var reversed = ValidationContext.SortParts(new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 1),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(4, apexOffset: -2f), MeshFixtures.Seam(4),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 2)
            });

            Assert.AreEqual("part-a", forward[0].PartId);
            Assert.AreEqual("part-b", forward[1].PartId);
            Assert.AreEqual("part-b", reversed[0].PartId);
            Assert.AreEqual("part-a", reversed[1].PartId);
        }

        /// <summary>A declaration on one part orders it before every undeclared part.</summary>
        [Test]
        public void DeclaredPriority_OrdersBeforeUndeclaredParts()
        {
            var sorted = ValidationContext.SortParts(new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4),
                    ApaPartSlot.Head, ApaPartSlotMode.Replace, 0),
                MultiPartFixtures.Part("part-z", MeshFixtures.Part(4, apexOffset: -2f), MeshFixtures.Seam(4),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 1)
            });

            Assert.AreEqual("part-z", sorted[0].PartId);
            Assert.AreEqual("part-a", sorted[1].PartId);
        }

        /// <summary>
        /// Declaring priority on some parts re-orders the whole group, not only the declared ones.
        /// </summary>
        /// <remarks>
        /// Priority is the first key component, so a single declaration moves its part ahead of parts whose slots
        /// and ids would otherwise order them first. This is the documented determinism change for a group that
        /// opts in, and it is pinned here next to the legacy-order test that proves a group that opts out is
        /// untouched.
        /// </remarks>
        [Test]
        public void OneDeclaredPriority_ReordersTheWholeGroup()
        {
            // The legacy order is slot-dominated: part-a (Head), part-b (LeftArm), part-c (RightArm).
            var legacy = ValidationContext.SortParts(new[]
            {
                Part("part-a", ApaPartSlot.Head, 0),
                Part("part-b", ApaPartSlot.LeftArm, 0),
                Part("part-c", ApaPartSlot.RightArm, 0)
            });

            Assert.AreEqual("part-a", legacy[0].PartId);
            Assert.AreEqual("part-b", legacy[1].PartId);
            Assert.AreEqual("part-c", legacy[2].PartId);

            // One declaration on the last part moves it in front of both, whatever their slots say.
            var oneDeclared = ValidationContext.SortParts(new[]
            {
                Part("part-a", ApaPartSlot.Head, 0),
                Part("part-b", ApaPartSlot.LeftArm, 0),
                Part("part-c", ApaPartSlot.RightArm, 1)
            });

            Assert.AreEqual("part-c", oneDeclared[0].PartId, "The declared part must be processed first.");
            Assert.AreEqual("part-a", oneDeclared[1].PartId, "The undeclared parts keep their relative order.");
            Assert.AreEqual("part-b", oneDeclared[2].PartId);

            // Declaring on two parts orders them by priority first and leaves only the remainder in legacy order.
            var mixed = ValidationContext.SortParts(new[]
            {
                Part("part-a", ApaPartSlot.Head, 2),
                Part("part-b", ApaPartSlot.LeftArm, 0),
                Part("part-c", ApaPartSlot.RightArm, 1)
            });

            Assert.AreEqual("part-a", mixed[0].PartId);
            Assert.AreEqual("part-c", mixed[1].PartId);
            Assert.AreEqual("part-b", mixed[2].PartId);
        }

        private static PartSnapshot Part(string partId, ApaPartSlot slot, int priority)
        {
            return MultiPartFixtures.Part(
                partId, MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4),
                slot, ApaPartSlotMode.Replace, priority);
        }

        /// <summary>The declared priority reaches the plan's vertex order, not only the sort.</summary>
        [Test]
        public void DeclaredPriority_ReordersTheEmittedVertices()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 2);
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 1);

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsTrue(result.Succeeded, result.Issues.FormatAll());

            var apexA = result.Plan.FinalIndexOf("part-a", 6);
            var apexB = result.Plan.FinalIndexOf("part-b", 6);
            Assert.GreaterOrEqual(apexA, 0);
            Assert.GreaterOrEqual(apexB, 0);
            Assert.Less(apexA, apexB, "The higher-priority part's vertices must be emitted first.");
        }

        /// <summary>The snapshot records the declared priority in both the policy and the ordering key.</summary>
        [Test]
        public void PartSnapshot_RecordsTheDeclaredPriority()
        {
            var part = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(4, apexOffset: -1f), MeshFixtures.Seam(4),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 7);

            Assert.AreEqual(7, part.ConflictPriority);
            Assert.AreEqual(7, part.OrderingKey.EffectivePriority);
            Assert.IsTrue(part.Policy.HasConflictPriority);
            StringAssert.Contains("7/", part.OrderingKey.ToString());
        }

        /// <summary>Reordering by priority never changes the geometry of a conflict-free configuration.</summary>
        [Test]
        public void PriorityOrdering_DoesNotChangeGeometry()
        {
            var body = MeshFixtures.Body(6);
            var parts = new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 4),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 0)
            };

            var withPriority = ApaCore.Plan(MeshFixtures.Context(body, parts));
            var withoutPriority = ApaCore.Plan(MeshFixtures.Context(body, new[]
            {
                MultiPartFixtures.Part("part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                    ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 0),
                MultiPartFixtures.Part("part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                    ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 0)
            }));

            Assert.IsTrue(withPriority.Succeeded, withPriority.Issues.FormatAll());
            Assert.IsTrue(withoutPriority.Succeeded, withoutPriority.Issues.FormatAll());
            Assert.AreEqual(withoutPriority.Plan.VertexCount, withPriority.Plan.VertexCount);
            Assert.AreEqual(withoutPriority.Plan.SubMeshes.Count, withPriority.Plan.SubMeshes.Count);
        }

        /// <summary>Removal overlap resolution requires the priority to be declared on every claimant.</summary>
        [Test]
        public void Priority_AloneDoesNotResolveAnything()
        {
            var body = MeshFixtures.Body(6);
            var partA = MultiPartFixtures.Part(
                "part-a", MeshFixtures.Part(6, apexOffset: -1f), MeshFixtures.Seam(6),
                ApaPartSlot.LeftArm, ApaPartSlotMode.Replace, 100, removed: MeshFixtures.Removed(0, 0));
            var partB = MultiPartFixtures.Part(
                "part-b", MeshFixtures.Part(6, apexOffset: -2f), MeshFixtures.Seam(6),
                ApaPartSlot.RightArm, ApaPartSlotMode.Replace, 0, removed: MeshFixtures.Removed(0, 0));

            var result = ApaCore.Plan(MeshFixtures.Context(body, new[] { partA, partB }));

            Assert.IsFalse(result.Succeeded, "One declared priority must not outrank an undeclared claimant.");
            Assert.IsFalse(
                result.Issues.ContainsCode(ApaErrorCode.RemovalOverlapResolvedByPriority),
                "Nothing may be resolved while a claimant declares no priority.");
        }
    }
}

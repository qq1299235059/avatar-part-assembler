using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests for M11's part-identity fallback: a profile that carries no stored part id resolves to a
    /// deterministic id derived from the profile asset, so a legacy profile installs instead of failing with
    /// <c>APA012 reason=missing-part-id</c>.
    /// </summary>
    /// <remarks>
    /// The asset-database half of the resolver (deriving from a real asset's GUID, and writing the id back) needs a
    /// project asset, which these tests deliberately do not create: the package's test suite never writes an asset.
    /// What is covered here is the pure derivation, the precedence of a stored id, and the draft path that a legacy
    /// profile takes when it is opened.
    /// </remarks>
    public sealed class PartIdentityFallbackTests
    {
        /// <summary>The derivation is a pure, deterministic function of the asset identity.</summary>
        [Test]
        public void DeriveFromAssetIdentity_IsDeterministicAndRecognizable()
        {
            const string guid = "0123456789abcdef0123456789abcdef";

            var first = ApaPartIdentityResolver.DeriveFromAssetIdentity(guid, 0);
            var second = ApaPartIdentityResolver.DeriveFromAssetIdentity(guid, 0);

            Assert.AreEqual(first, second, "Two derivations of one asset identity must agree.");
            StringAssert.StartsWith(ApaPartIdentityResolver.DerivedPrefix, first,
                "A derived id must be recognizable as derived rather than masquerading as an authored one.");
            StringAssert.Contains(guid, first);

            Assert.AreNotEqual(first, ApaPartIdentityResolver.DeriveFromAssetIdentity("ffffffff", 0),
                "Two different assets must not derive the same id.");
        }

        /// <summary>A sub-asset of the same file is a different part and derives a different id.</summary>
        [Test]
        public void DeriveFromAssetIdentity_DistinguishesLocalFileIds()
        {
            const string guid = "0123456789abcdef0123456789abcdef";

            var main = ApaPartIdentityResolver.DeriveFromAssetIdentity(guid, 0);
            var sub = ApaPartIdentityResolver.DeriveFromAssetIdentity(guid, 42);

            Assert.AreNotEqual(main, sub);
            StringAssert.Contains("42", sub);
        }

        /// <summary>An empty or missing asset identity derives nothing, which is reported rather than guessed.</summary>
        [Test]
        public void DeriveFromAssetIdentity_RefusesAnEmptyGuid()
        {
            Assert.AreEqual(string.Empty, ApaPartIdentityResolver.DeriveFromAssetIdentity(null, 0));
            Assert.AreEqual(string.Empty, ApaPartIdentityResolver.DeriveFromAssetIdentity(string.Empty, 0));
            Assert.AreEqual(string.Empty, ApaPartIdentityResolver.DeriveFromAssetIdentity("   ", 0));
        }

        /// <summary>A stored id is used as-is: the fallback never overrides authored data.</summary>
        [Test]
        public void ResolvePartId_PrefersTheStoredId()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            try
            {
                profile.Identity.PartId = "authored-id";

                Assert.IsTrue(ApaPartIdentityResolver.HasStoredPartId(profile));
                Assert.IsFalse(ApaPartIdentityResolver.NeedsRepair(profile));
                Assert.AreEqual("authored-id", ApaPartIdentityResolver.ResolvePartId(profile));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// A profile that is not a project asset and carries no id resolves to nothing, which the configuration
        /// rule still refuses rather than inventing an identity for.
        /// </summary>
        [Test]
        public void ResolvePartId_RefusesAProfileThatIsNotAnAsset()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            try
            {
                Assert.IsFalse(ApaPartIdentityResolver.HasStoredPartId(profile));
                Assert.IsFalse(ApaPartIdentityResolver.NeedsRepair(profile));
                Assert.AreEqual(string.Empty, ApaPartIdentityResolver.ResolvePartId(profile));
                Assert.IsFalse(ApaPartIdentityResolver.TryRepair(profile, out _, out var reason));
                Assert.AreEqual("not-persistent", reason);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>A null profile resolves to nothing rather than throwing.</summary>
        [Test]
        public void ResolvePartId_ToleratesMissingInput()
        {
            Assert.AreEqual(string.Empty, ApaPartIdentityResolver.ResolvePartId((ApaPartProfile)null));
            Assert.AreEqual(string.Empty, ApaPartIdentityResolver.ResolvePartId((AvatarPartInstaller)null));
            Assert.IsFalse(ApaPartIdentityResolver.NeedsRepair(null));
        }

        /// <summary>
        /// Opening a legacy profile that carries no id gives the draft one, so the next save persists an identity
        /// instead of writing the empty id again.
        /// </summary>
        [Test]
        public void LegacyProfile_GetsAnIdentityInTheDraft()
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            ApaPartProfile materialized = null;
            try
            {
                profile.Identity.DisplayName = "Legacy Arm";
                profile.Identity.Slot = ApaPartSlot.LeftArm;

                Assert.IsFalse(ApaPartIdentityResolver.HasStoredPartId(profile));

                var draft = ApaProfileDraft.FromProfile(profile);

                Assert.IsTrue(draft.HasStablePartId,
                    "A draft loaded from a profile without an id must still be saveable.");
                Assert.AreEqual("Legacy Arm", draft.Identity.DisplayName);

                materialized = draft.Materialize();
                Assert.AreEqual(draft.Identity.PartId, materialized.Identity.PartId,
                    "The id the draft shows is the id it writes.");
            }
            finally
            {
                if (materialized != null) Object.DestroyImmediate(materialized);
                Object.DestroyImmediate(profile);
            }
        }
    }
}

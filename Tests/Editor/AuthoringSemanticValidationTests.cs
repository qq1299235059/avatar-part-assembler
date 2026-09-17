using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the draft-level checks: the conditions that exist only while a profile is being edited and that
    /// the assembly core therefore never sees.
    /// </summary>
    public sealed class AuthoringSemanticValidationTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        [Test]
        public void UvSemantics_ValidDeclarationsReportNothing()
        {
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[]
            {
                new ApaUvChannelSemantic("UVMap", 0),
                new ApaUvChannelSemantic("DetailUV", 1)
            };

            Assert.IsEmpty(ApaAuthoringValidation.ValidateDraftData(draft, null));
        }

        [Test]
        public void UvSemantics_EmptyNameIsInvalidSemanticName()
        {
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("   ", 0) };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, null);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.InvalidSemanticName, issues[0].Code);
            Assert.AreEqual(ApaSeverity.Error, issues[0].Severity);
        }

        [Test]
        public void UvSemantics_DuplicateNameIsADuplicateSemantic()
        {
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[]
            {
                new ApaUvChannelSemantic("UVMap", 0),
                // Same name after trimming: normalization happens before comparison, never case folding.
                new ApaUvChannelSemantic("  UVMap ", 1)
            };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, null);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.DuplicateSemantic, issues[0].Code);
            StringAssert.Contains("semantic=UVMap", issues[0].Detail);
        }

        [Test]
        public void UvSemantics_CaseDifferenceIsNotADuplicate()
        {
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[]
            {
                new ApaUvChannelSemantic("Skin", 0),
                new ApaUvChannelSemantic("skin", 1)
            };

            Assert.IsEmpty(ApaAuthoringValidation.ValidateDraftData(draft, null));
        }

        [Test]
        public void UvSemantics_ChannelOutsideTheUnityRangeIsReported()
        {
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", ApaMeshLimits.MaxUvChannels) };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, null);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.InvalidSemanticName, issues[0].Code);
            StringAssert.Contains("channel=8", issues[0].Detail);
        }

        [Test]
        public void UvSemantics_ChannelThePartMeshDoesNotCarryIsRefused()
        {
            // The fixture has no UV channels at all. The assembler writes a declared channel only when the source
            // mesh carries it, so saving this declaration would produce a whole UV layer of zeros silently.
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, PartMeshWithTwoSubMeshes());

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaAuthoringErrorCode.UvSemanticChannelAbsent, issues[0].Code);
            Assert.AreEqual("APA050", issues[0].Code);
            Assert.AreEqual(ApaSeverity.Error, issues[0].Severity);
            StringAssert.Contains("reason=channel-not-present", issues[0].Detail);
            StringAssert.Contains("channel=0", issues[0].Detail);
            StringAssert.Contains("present: none", issues[0].Message);
        }

        [Test]
        public void UvSemantics_AbsentChannelMessageListsTheChannelsTheMeshDoesCarry()
        {
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("DetailUV", 1) };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, PartMeshWithUv0());

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaAuthoringErrorCode.UvSemanticChannelAbsent, issues[0].Code);
            StringAssert.Contains("present: 0", issues[0].Message);
            StringAssert.Contains("channel=1", issues[0].Detail);
        }

        [Test]
        public void UvSemantics_ChannelThePartMeshCarriesIsAccepted()
        {
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", 0) };

            Assert.IsEmpty(ApaAuthoringValidation.ValidateDraftData(draft, PartMeshWithUv0()));
        }

        [Test]
        public void UvSemantics_WithoutAPartMeshTheChannelIsNotJudged()
        {
            // With no live mesh to compare against, the channel declaration is neither confirmed nor refused;
            // refusing it would block a profile that is being edited before the part mesh is selected.
            var draft = new ApaProfileDraft();
            draft.UvSemantics = new[] { new ApaUvChannelSemantic("UVMap", 3) };

            Assert.IsEmpty(ApaAuthoringValidation.ValidateDraftData(draft, null));
        }

        [Test]
        public void DiagnosticText_RendersTheUvChannelCodeWithItsOwnTitle()
        {
            var issue = ValidationIssue.Error(
                ApaAuthoringErrorCode.UvSemanticChannelAbsent,
                ApaIssuePhase.Uv,
                "UV semantic 'UVMap' declares source channel 0, but the part mesh has no channel 0.",
                sourceIndex: 0,
                detail: "semantic=UVMap; channel=0; reason=channel-not-present");

            StringAssert.Contains("APA050 UV_SEMANTIC_CHANNEL_ABSENT [0]:", ApaDiagnosticText.Format(issue));
            Assert.IsTrue(ApaAuthoringErrorCode.IsAuthoringCode("APA050"));
        }

        [Test]
        public void MaterialSemantics_ValidDeclarationsReportNothing()
        {
            var draft = new ApaProfileDraft();
            draft.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic("Skin", 0, null, ApaMaterialPolicyMode.Auto),
                new ApaMaterialSlotSemantic("Metal", 1, null, ApaMaterialPolicyMode.ForceNew)
            };

            var mesh = PartMeshWithTwoSubMeshes();
            Assert.IsEmpty(ApaAuthoringValidation.ValidateDraftData(draft, mesh));
        }

        [Test]
        public void MaterialSemantics_EmptyNameIsInvalidSemanticName()
        {
            var draft = new ApaProfileDraft();
            draft.MaterialSemantics = new[] { new ApaMaterialSlotSemantic(string.Empty, 0, null, ApaMaterialPolicyMode.Auto) };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, null);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.InvalidSemanticName, issues[0].Code);
        }

        [Test]
        public void MaterialSemantics_DuplicateNameIsReportedBecauseTheCoreDoesNotCheckIt()
        {
            var draft = new ApaProfileDraft();
            draft.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic("Skin", 0, null, ApaMaterialPolicyMode.Auto),
                new ApaMaterialSlotSemantic("Skin", 1, null, ApaMaterialPolicyMode.Auto)
            };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, null);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaErrorCode.DuplicateSemantic, issues[0].Code);
            StringAssert.Contains("submesh=1", issues[0].Detail);
        }

        [Test]
        public void MaterialSemantics_SubMeshTheMeshDoesNotHaveIsAWarning()
        {
            var draft = new ApaProfileDraft();
            draft.MaterialSemantics = new[] { new ApaMaterialSlotSemantic("Skin", 4, null, ApaMaterialPolicyMode.Auto) };

            var issues = ApaAuthoringValidation.ValidateDraftData(draft, PartMeshWithTwoSubMeshes());

            Assert.AreEqual(1, issues.Count);

            // The condition is "this submesh ends up with no final material slot", which the core reports as
            // APA038. The pre-check differs only in severity: the authoring layer has no final layout in hand, so
            // it warns, while the build blocks with the same code.
            Assert.AreEqual(ApaErrorCode.SubMeshWithoutMaterialSlot, issues[0].Code);
            Assert.AreEqual("APA038", issues[0].Code);
            Assert.AreEqual("SUBMESH_WITHOUT_MATERIAL_SLOT", ApaErrorCode.GetTitle(issues[0].Code));
            Assert.AreNotEqual(
                ApaErrorCode.MaterialSemanticConflict,
                issues[0].Code,
                "APA009 means two material assets claim one semantic; this condition is APA038.");
            Assert.AreEqual(ApaSeverity.Warning, issues[0].Severity);
            StringAssert.Contains("reason=declared-submesh-out-of-range", issues[0].Detail);
        }

        [Test]
        public void MaterialReferences_SceneMaterialIsRefused()
        {
            var shader = Shader.Find("Sprites/Default");
            Assume.That(shader, Is.Not.Null, "No usable built-in shader is available in this project.");

            var material = Track(new Material(shader));
            var draft = new ApaProfileDraft();
            draft.MaterialSemantics = new[] { new ApaMaterialSlotSemantic("Skin", 0, material, ApaMaterialPolicyMode.Auto) };

            var issues = ApaAuthoringValidation.FindNonPersistentReferences(draft);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(ApaAuthoringErrorCode.NonPersistentReference, issues[0].Code);
            Assert.AreEqual("APA034", issues[0].Code);
            StringAssert.Contains("reason=non-persistent-material-reference", issues[0].Detail);
            StringAssert.Contains("APA034 NON_PERSISTENT_REFERENCE", ApaDiagnosticText.Format(issues[0]));
        }

        [Test]
        public void MaterialReferences_AnEmptyReferenceIsNotReported()
        {
            var draft = new ApaProfileDraft();
            draft.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic("Skin", 0, null, ApaMaterialPolicyMode.UseTarget)
            };

            Assert.IsEmpty(ApaAuthoringValidation.FindNonPersistentReferences(draft));
        }

        [Test]
        public void DiagnosticText_AlwaysLeadsWithTheStableCode()
        {
            var issue = ValidationIssue.Error(
                ApaErrorCode.SeamUvMismatch,
                ApaIssuePhase.Uv,
                "UV semantic 'UVMap' disagrees at the seam.",
                "part-1",
                5,
                2,
                "semantic=UVMap");

            var text = ApaDiagnosticText.Format(issue);

            StringAssert.StartsWith("ERROR APA004 SEAM_UV_MISMATCH [5]:", text);
            StringAssert.Contains(":: semantic=UVMap", text);
            StringAssert.Contains("APA004 SEAM_UV_MISMATCH", ApaDiagnosticText.FormatShort(issue));
        }

        [Test]
        public void DiagnosticText_UnknownCodeIsStillRendered()
        {
            var issue = ValidationIssue.Error("APA999", ApaIssuePhase.Assembly, "boom", detail: "exception=Test");

            var text = ApaDiagnosticText.Format(issue);

            StringAssert.Contains("APA999 INTERNAL_ERROR", text);
        }

        private MeshSnapshot PartMeshWithTwoSubMeshes()
        {
            return MeshFixtures.SnapshotWithSubMeshes(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                new[]
                {
                    new[] { 0, 1, 2 },
                    new[] { 1, 2, 3 }
                });
        }

        /// <summary>The same part fixture with a full UV0 channel and no other channel.</summary>
        private MeshSnapshot PartMeshWithUv0()
        {
            return MeshFixtures.SnapshotWithSubMeshes(
                "Part",
                new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                new[]
                {
                    new[] { 0, 1, 2 },
                    new[] { 1, 2, 3 }
                },
                new[]
                {
                    new Vector4(0f, 0f, 0f, 0f),
                    new Vector4(1f, 0f, 0f, 0f),
                    new Vector4(0f, 1f, 0f, 0f),
                    new Vector4(1f, 1f, 0f, 0f)
                });
        }

        private T Track<T>(T value) where T : Object
        {
            _created.Add(value);
            return value;
        }
    }
}

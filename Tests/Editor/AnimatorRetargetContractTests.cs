using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Ndmf;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Contract tests for the Merge Animator retarget step and the empty source-object cleanup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two halves, because the feature has two halves. The <i>mapping</i> half is behavioural: the processor's
    /// capture is driven with real GameObjects and real <see cref="ApaBuildGroupResult"/> values, so "every
    /// consumed source renderer maps to its own group's target renderer, and null/identical/duplicate pairs are
    /// skipped" is asserted against the code that runs in a build rather than against a description of it. The
    /// <i>ordering and API</i> half is structural: the retarget step must use NDMF's
    /// <c>AnimatorServicesContext.ObjectPathRemapper.ReplaceObject</c> and must not edit an animation clip
    /// directly, and the plugin must declare the retarget step with the animator services context required and the
    /// cleanup step after Modular Avatar's late transform stages. Those are source-level properties of a
    /// <c>Configure</c> method and of a pass body; a source scan is what pins them without standing up an NDMF
    /// build.
    /// </para>
    /// <para>
    /// The empty-object predicate is tested directly — it is public for exactly that reason — and every case that
    /// must <i>keep</i> an object is asserted beside every case that may remove one, because a predicate that
    /// removes too much is the failure mode this feature has to avoid.
    /// </para>
    /// </remarks>
    public sealed class AnimatorRetargetContractTests
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

        // ---- the capture: every consumed source maps to its own group's target -------------------------

        /// <summary>
        /// Each consumed renderer of a group maps to that group's own target renderer object, in group order and
        /// then discovery order.
        /// </summary>
        [Test]
        public void CaptureRendererReplacements_MapsEveryConsumedSourceToItsOwnGroupsTarget()
        {
            var avatar = NewGameObject("Avatar", null);
            var body = NewRenderer("Body", avatar.transform);
            var bodyAlt = NewRenderer("BodyAlt", avatar.transform);
            var partA = NewRenderer("PartA", avatar.transform);
            var partB = NewRenderer("PartB", avatar.transform);
            var extraA = NewRenderer("ExtraA", partA.transform);

            var groups = new List<ApaBuildGroupResult>
            {
                Group("Body", body, new[] { partA.GetComponent<Renderer>(), extraA.GetComponent<Renderer>() }),
                Group("BodyAlt", bodyAlt, new[] { partB.GetComponent<Renderer>() })
            };

            var mappings = ApaBuildProcessor.CaptureRendererReplacements(groups);

            Assert.AreEqual(3, mappings.Count, "One pair per consumed renderer.");
            Assert.AreSame(partA, mappings[0].Source);
            Assert.AreSame(body, mappings[0].Target, "A pair must name its own group's target, not another's.");
            Assert.AreSame(extraA, mappings[1].Source);
            Assert.AreSame(body, mappings[1].Target);
            Assert.AreSame(partB, mappings[2].Source);
            Assert.AreSame(bodyAlt, mappings[2].Target);
        }

        /// <summary>
        /// A pair that names nothing, or the same object twice, is not a mapping and is not captured; a source
        /// that two groups claim is captured once.
        /// </summary>
        [Test]
        public void CaptureRendererReplacements_SkipsNullIdenticalAndRepeatedPairs()
        {
            var avatar = NewGameObject("Avatar", null);
            var body = NewRenderer("Body", avatar.transform);
            var part = NewRenderer("Part", avatar.transform);

            var groups = new List<ApaBuildGroupResult>
            {
                // A group with no target renderer cannot produce a mapping.
                Group("NoTarget", null, new[] { part.GetComponent<Renderer>() }),

                // A consumed renderer on the target's own object is the same object on both sides: no mapping.
                Group("Self", part, new[] { part.GetComponent<Renderer>() }),

                // A null entry names nothing.
                Group("Null", body, new Renderer[] { null }),

                // A renderer two groups claim is recorded once, against the first group that claimed it.
                Group("Body", body, new[] { part.GetComponent<Renderer>() }),
                Group("BodyAlt", part, new[] { part.GetComponent<Renderer>() })
            };

            var mappings = ApaBuildProcessor.CaptureRendererReplacements(groups);

            Assert.AreEqual(1, mappings.Count, "Only the one real, distinct pair is a mapping.");
            Assert.AreSame(part, mappings[0].Source);
            Assert.AreSame(body, mappings[0].Target);
        }

        /// <summary>An absent group list is not a failure: it is "this run consumed nothing".</summary>
        [Test]
        public void CaptureRendererReplacements_WithNoGroupsProducesNoMapping()
        {
            Assert.AreEqual(0, ApaBuildProcessor.CaptureRendererReplacements(null).Count);
            Assert.AreEqual(0, ApaBuildProcessor.CaptureRendererReplacements(new List<ApaBuildGroupResult>()).Count);
        }

        /// <summary>The result carries the mappings, and a failed or empty run carries none.</summary>
        [Test]
        public void BuildResult_ExposesTheMappingsAndEmptyRunsCarryNone()
        {
            var avatar = NewGameObject("Avatar", null);
            var body = NewRenderer("Body", avatar.transform);
            var part = NewRenderer("Part", avatar.transform);

            var groups = new List<ApaBuildGroupResult>
            {
                Group("Body", body, new[] { part.GetComponent<Renderer>() })
            };

            var success = ApaBuildResult.Success(
                groups,
                ValidationResult.Empty,
                new List<AvatarPartInstaller>(),
                new List<Renderer> { part.GetComponent<Renderer>() },
                null,
                ApaBuildProcessor.CaptureRendererReplacements(groups));

            Assert.AreEqual(1, success.RetargetMappings.Count);
            Assert.AreSame(part, success.RetargetMappings[0].Source);
            Assert.AreSame(body, success.RetargetMappings[0].Target);

            Assert.AreEqual(
                0,
                ApaBuildResult.Failure(ValidationResult.Empty).RetargetMappings.Count,
                "A failed run maps nothing.");
            Assert.AreEqual(0, ApaBuildResult.NoWork.RetargetMappings.Count, "A run with no work maps nothing.");
        }

        // ---- the empty-object predicate -----------------------------------------------------------------

        /// <summary>
        /// An object the assembly stripped down to a Transform, with no children, is the one thing that may be
        /// removed.
        /// </summary>
        [Test]
        public void IsEmptySourceObject_AcceptsOnlyATransformOnlyChildlessObject()
        {
            var avatar = NewGameObject("Avatar", null);
            var consumed = NewGameObject("Part", avatar.transform);

            // The real shape: the renderer component the assembly consumed is gone, the object remains.
            var renderer = consumed.AddComponent<MeshRenderer>();
            Object.DestroyImmediate(renderer);

            Assert.IsTrue(
                ApaSourceObjectCleanup.IsEmptySourceObject(consumed, avatar),
                "A consumed renderer object left with only a Transform is empty.");
        }

        /// <summary>Anything the object still carries — a child, a component, or a broken script — keeps it.</summary>
        [Test]
        public void IsEmptySourceObject_RefusesEveryNonEmptyObject()
        {
            var avatar = NewGameObject("Avatar", null);

            var withChild = NewGameObject("WithChild", avatar.transform);
            NewGameObject("Bone", withChild.transform);
            Assert.IsFalse(
                ApaSourceObjectCleanup.IsEmptySourceObject(withChild, avatar),
                "A child may be a bone of the generated mesh; the object is not empty.");

            var withComponent = NewGameObject("WithComponent", avatar.transform);
            withComponent.AddComponent<BoxCollider>();
            Assert.IsFalse(
                ApaSourceObjectCleanup.IsEmptySourceObject(withComponent, avatar),
                "A remaining component — a constraint, a PhysBone, an authoring component — is not empty.");

            var withAuthoringComponent = NewGameObject("WithInstaller", avatar.transform);
            withAuthoringComponent.AddComponent<AvatarPartInstaller>();
            Assert.IsFalse(
                ApaSourceObjectCleanup.IsEmptySourceObject(withAuthoringComponent, avatar),
                "An installer component keeps its object alive like any other component.");
        }

        /// <summary>The build's own root, a scene root, and a destroyed object are never removable.</summary>
        [Test]
        public void IsEmptySourceObject_RefusesTheRootsAndDestroyedObjects()
        {
            var avatar = NewGameObject("Avatar", null);
            var sceneRoot = NewGameObject("SceneRoot", null);
            var detached = NewGameObject("Detached", avatar.transform);
            detached.transform.SetParent(null, false);

            Assert.IsFalse(
                ApaSourceObjectCleanup.IsEmptySourceObject(avatar, avatar),
                "The avatar root is never removed, even when it carries nothing but a Transform.");
            Assert.IsFalse(
                ApaSourceObjectCleanup.IsEmptySourceObject(sceneRoot, avatar),
                "An object outside the build's hierarchy is not the build's to remove.");
            Assert.IsFalse(
                ApaSourceObjectCleanup.IsEmptySourceObject(detached, avatar),
                "A detached object is outside the hierarchy the build owns.");
            Assert.IsFalse(ApaSourceObjectCleanup.IsEmptySourceObject(null, avatar), "Null is not empty.");

            var removed = NewGameObject("Removed", avatar.transform);
            Object.DestroyImmediate(removed);
            Assert.IsFalse(
                ApaSourceObjectCleanup.IsEmptySourceObject(removed, avatar),
                "A destroyed object compares equal to null and is not a candidate.");

            // A null avatar root must not turn every childless object into a candidate for the root rule's sake:
            // the remaining rules still apply.
            var childless = NewGameObject("Childless", avatar.transform);
            Assert.IsTrue(ApaSourceObjectCleanup.IsEmptySourceObject(childless, null));
        }

        // ---- the NDMF API and the ordering --------------------------------------------------------------

        /// <summary>
        /// The retarget step uses NDMF's object path remapper and never edits an animation clip itself.
        /// </summary>
        /// <remarks>
        /// This is requirement R1's structural half: the mapping has to ride on NDMF's own path rewriting, which
        /// is what commits the change when the animator services context deactivates. A second, hand-written clip
        /// rewriter inside this package would be a second answer to "which paths moved" — and it would have to be
        /// kept in sync with NDMF's, which is exactly what the task forbids.
        /// </remarks>
        [Test]
        public void RetargetPass_UsesTheNdmfPathRemapperAndDoesNotEditClips()
        {
            var pass = ReadNdmfSource("ApaAnimatorRetargetPass.cs");

            StringAssert.Contains("nadena.dev.ndmf.animator", pass);
            StringAssert.Contains("AnimatorServicesContext", pass);
            StringAssert.Contains("ObjectPathRemapper", pass);
            StringAssert.Contains("remapper.ReplaceObject(source, target)", pass);
            StringAssert.Contains("context.Extension<AnimatorServicesContext>()", pass);

            Assert.IsFalse(
                Regex.IsMatch(pass, @"\bAnimationClip\b"),
                "The retarget step must not name an animation clip: NDMF owns the clip rewriting.");
            Assert.IsFalse(
                Regex.IsMatch(pass, @"AnimationUtility|SetEditorCurve|SetCurve|EditorCurveBinding"),
                "The retarget step must not edit serialized curve data directly.");
            Assert.IsFalse(
                Regex.IsMatch(pass, @"nadena\.dev\.modular_avatar"),
                "The retarget step must not reference a Modular Avatar type; the task forbids changing MA and " +
                "nothing here needs one.");
        }

        /// <summary>The retarget step skips a missing or self-referential pair instead of registering it.</summary>
        [Test]
        public void RetargetPass_SkipsNullAndIdenticalPairs()
        {
            var pass = ReadNdmfSource("ApaAnimatorRetargetPass.cs");

            var guard = pass.IndexOf("if (source == null || target == null) continue;", StringComparison.Ordinal);
            var same = pass.IndexOf("if (source == target) continue;", StringComparison.Ordinal);
            var replace = pass.IndexOf("remapper.ReplaceObject(source, target)", StringComparison.Ordinal);

            Assert.GreaterOrEqual(guard, 0, "The null guard is missing.");
            Assert.GreaterOrEqual(same, 0, "The identical-pair guard is missing.");
            Assert.Greater(replace, guard, "The guards must run before a pair is registered.");
            Assert.Greater(replace, same, "The guards must run before a pair is registered.");
        }

        /// <summary>
        /// The plugin declares the retarget step with the animator services context required, and declares it
        /// after the assembly pass.
        /// </summary>
        /// <remarks>
        /// The requirement is what makes the extension live when the pass runs — Modular Avatar has already closed
        /// the context it used by the time APA's Transforming sequence starts — and the sequence order is what
        /// keeps a mapping from being registered for an assembly that never happened.
        /// </remarks>
        [Test]
        public void Plugin_RequiresTheAnimatorExtensionForTheRetargetStepAfterTheAssembly()
        {
            var plugin = ReadNdmfSource("ApaNdmfPlugin.cs");

            StringAssert.Contains("WithRequiredExtension(typeof(AnimatorServicesContext)", plugin);
            StringAssert.Contains("s.Run(ApaAnimatorRetargetPass.Instance)", plugin);

            var assembly = plugin.IndexOf(".Run(ApaAssemblyPass.Instance)", StringComparison.Ordinal);
            var retarget = plugin.IndexOf("s.Run(ApaAnimatorRetargetPass.Instance)", StringComparison.Ordinal);

            Assert.GreaterOrEqual(assembly, 0, "The assembly pass declaration was not found.");
            Assert.Greater(retarget, assembly, "The retarget step must be declared after the assembly pass.");

            // The context is declared as required, never as merely compatible: a compatible declaration would not
            // open a context Modular Avatar has already closed, and the mapping would never be committed.
            Assert.IsFalse(
                plugin.Contains("WithCompatibleExtension(typeof(AnimatorServicesContext)"),
                "The retarget step needs the context open, not merely tolerated.");
        }

        /// <summary>
        /// The cleanup step is ordered after Modular Avatar's late transform stages and after the retarget step.
        /// </summary>
        /// <remarks>
        /// The late transform stages purge the remaining Modular Avatar components, so a leftover
        /// <c>ModularAvatarMergeAnimator</c> must already be gone when the empty-object predicate runs; and the
        /// retarget step needs the source objects alive to read their recorded paths.
        /// </remarks>
        [Test]
        public void Plugin_OrdersTheCleanupAfterLateTransformAndAfterTheRetargetStep()
        {
            var plugin = ReadNdmfSource("ApaNdmfPlugin.cs");

            StringAssert.Contains("\"nadena.dev.modular-avatar.late-transform-stages\"", plugin);
            StringAssert.Contains(".AfterPlugin(ModularAvatarLateTransformPluginQualifiedName)", plugin);
            StringAssert.Contains(".WaitFor(ApaAnimatorRetargetPass.Instance)", plugin);
            StringAssert.Contains(".Run(ApaEmptySourceCleanupPass.Instance)", plugin);

            var afterLate = plugin.IndexOf(".AfterPlugin(ModularAvatarLateTransformPluginQualifiedName)", StringComparison.Ordinal);
            var wait = plugin.IndexOf(".WaitFor(ApaAnimatorRetargetPass.Instance)", StringComparison.Ordinal);
            var cleanup = plugin.IndexOf(".Run(ApaEmptySourceCleanupPass.Instance)", StringComparison.Ordinal);

            Assert.GreaterOrEqual(afterLate, 0, "The late transform ordering constraint was not found.");
            Assert.Greater(wait, afterLate, "The cleanup sequence must be ordered after the late transform plugin.");
            Assert.Greater(cleanup, wait, "The cleanup pass must be declared after the wait constraint.");
        }

        /// <summary>
        /// The cleanup pass removes an object only through the empty-object predicate.
        /// </summary>
        [Test]
        public void CleanupPass_RemovesOnlyWhatTheEmptyPredicateAccepts()
        {
            var pass = ReadNdmfSource("ApaEmptySourceCleanupPass.cs");

            StringAssert.Contains("ApaSourceObjectCleanup.IsEmptySourceObject(source, avatarRoot)", pass);
            StringAssert.Contains("Object.DestroyImmediate(source)", pass);
            StringAssert.Contains("if (!context.Successful) return;", pass);

            var predicate = pass.IndexOf("ApaSourceObjectCleanup.IsEmptySourceObject(source, avatarRoot)", StringComparison.Ordinal);
            var destroy = pass.IndexOf("Object.DestroyImmediate(source)", StringComparison.Ordinal);
            Assert.Greater(destroy, predicate, "The removal must be guarded by the predicate.");

            Assert.AreEqual(
                1,
                Regex.Matches(pass, @"DestroyImmediate").Count,
                "The cleanup pass removes exactly one thing: a source object the predicate accepted.");
        }

        /// <summary>
        /// The processor captures the pairs before it destroys the renderers, and the assembly pass records them.
        /// </summary>
        /// <remarks>
        /// A destroyed component cannot be asked for its GameObject, so a capture placed after the destruction
        /// would produce nothing at all — a silent, complete loss of the feature. The order is asserted here
        /// because it is invisible in the compiled types.
        /// </remarks>
        [Test]
        public void Processor_CapturesThePairsBeforeDestroyingTheRenderersAndThePassRecordsThem()
        {
            var processor = ReadNdmfSource("ApaBuildProcessor.cs");
            var assembly = ReadNdmfSource("ApaAssemblyPass.cs");

            var capture = processor.IndexOf("var retargetMappings = CaptureRendererReplacements(groupResults);", StringComparison.Ordinal);
            var destroy = processor.IndexOf("DestroyImmediate(consumedRenderers[i])", StringComparison.Ordinal);
            var success = processor.IndexOf("return ApaBuildResult.Success(", StringComparison.Ordinal);

            Assert.GreaterOrEqual(capture, 0, "The capture call was not found.");
            Assert.GreaterOrEqual(destroy, 0, "The renderer destruction was not found.");
            Assert.Greater(destroy, capture, "The pairs must be captured before the renderers are destroyed.");
            Assert.Greater(success, destroy, "The captured pairs must reach the result.");
            StringAssert.Contains("retargetMappings);", processor);

            StringAssert.Contains("artifacts.RecordRendererReplacements(result.RetargetMappings)", assembly);

            var artifacts = ReadNdmfSource("ApaTransientArtifacts.cs");
            StringAssert.Contains("internal void RecordRendererReplacements(", artifacts);
            StringAssert.Contains("internal IReadOnlyList<ApaRendererReplacement> RendererReplacements", artifacts);
        }

        /// <summary>
        /// Both new pass names are localized, like every other pass name in this plugin.
        /// </summary>
        [Test]
        public void NewPassNames_HaveSimplifiedChineseEntries()
        {
            var retarget = ReadNdmfSource("ApaAnimatorRetargetPass.cs");
            var cleanup = ReadNdmfSource("ApaEmptySourceCleanupPass.cs");
            var table = ReadEditorSource("Localization", "ApaLocalizationChinese.cs");

            var retargetName = Tr(retarget);
            var cleanupName = Tr(cleanup);

            StringAssert.Contains(retargetName, retarget);
            StringAssert.Contains(cleanupName, cleanup);

            StringAssert.Contains("\"" + retargetName + "\"", table);
            StringAssert.Contains("\"" + cleanupName + "\"", table);
        }

        // ---- helpers ------------------------------------------------------------------------------------

        /// <summary>Extracts the single literal passed to <c>ApaLocalization.Tr(...)</c> from a pass source.</summary>
        private static string Tr(string source)
        {
            var match = Regex.Match(source, @"ApaLocalization\.Tr\(""(?<key>[^""]+)""\)");
            Assert.IsTrue(match.Success, "The pass source does not call ApaLocalization.Tr with one literal.");
            return match.Groups["key"].Value;
        }

        private static ApaBuildGroupResult Group(string key, GameObject target, Renderer[] consumed)
        {
            return new ApaBuildGroupResult(
                key,
                target != null ? target.GetComponent<Renderer>() : null,
                null,
                null,
                consumed,
                null);
        }

        private GameObject NewGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            _created.Add(gameObject);
            return gameObject;
        }

        private GameObject NewRenderer(string name, Transform parent)
        {
            var gameObject = NewGameObject(name, parent);
            gameObject.AddComponent<MeshRenderer>();
            return gameObject;
        }

        private static string ReadNdmfSource(string fileName)
        {
            var path = Path.Combine(PackageRoot, "Editor", "NDMF", fileName);
            if (!File.Exists(path)) Assert.Ignore("NDMF source file not found: " + path);
            return File.ReadAllText(path);
        }

        private static string ReadEditorSource(string folder, string fileName)
        {
            var path = Path.Combine(PackageRoot, "Editor", folder, fileName);
            if (!File.Exists(path)) Assert.Ignore("Editor source file not found: " + path);
            return File.ReadAllText(path);
        }

        private static string PackageRoot
        {
            get
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler");
            }
        }
    }
}

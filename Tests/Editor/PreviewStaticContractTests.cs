using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Source-level contract tests for the M4 preview layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preview layer lives in its own assembly (<c>dev.avatar-part-assembler.editor.preview</c>). These tests
    /// check the layer's <i>source contract</i>: the invariants that matter for review are structural (which file
    /// may write a proxy mesh, that the overlay is off by default, that the cache is bounded and torn down, that
    /// the fingerprint avoids process-randomized hashing, that the filter is registered on the real Transforming
    /// pass, and that discovery asks for the build's grouped plan), and a structural check catches a regression in
    /// exactly those places at a fraction of the cost of standing up an NDMF preview session.
    /// </para>
    /// <para>
    /// The test assembly definition references the preview and NDMF assemblies, so behavioural tests beside these
    /// are possible; the structural checks are kept because they also pin the registration, which only exists in
    /// source form inside a plugin <c>Configure</c> method.
    /// </para>
    /// </remarks>
    public class PreviewStaticContractTests
    {
        private const string PreviewFolderName = "Preview";

        private static string PreviewDirectory
        {
            get
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(
                    projectRoot,
                    "Packages",
                    "dev.avatar-part-assembler",
                    "Editor",
                    PreviewFolderName);
            }
        }

        private static string ReadPreviewSource(string fileName)
        {
            var path = Path.Combine(PreviewDirectory, fileName);
            if (!File.Exists(path))
            {
                Assert.Ignore("Preview source file not found: " + path);
            }

            return File.ReadAllText(path);
        }

        /// <summary>
        /// Reads a source file of the NDMF assembly, which owns the preview registration.
        /// </summary>
        /// <remarks>
        /// The registration lives there because that is the only assembly that may see both the preview assembly
        /// and NDMF; the test reads it as text for the same reason it reads the preview sources as text — the
        /// invariant is structural, and these tests must not require an NDMF preview session to check it.
        /// </remarks>
        private static string ReadNdmfSource(string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", "NDMF", fileName);

            if (!File.Exists(path))
            {
                Assert.Ignore("NDMF source file not found: " + path);
            }

            return File.ReadAllText(path);
        }

        /// <summary>
        /// Reads a source file of the editor assembly outside the preview and NDMF folders.
        /// </summary>
        /// <remarks>
        /// Used by the structural contracts that are not preview-specific but are the same kind of invariant —
        /// the merge-name policy boundary, for instance, which lives in the integration layer that owns every
        /// Modular Avatar call.
        /// </remarks>
        private static string ReadEditorSource(string folder, string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", folder, fileName);

            if (!File.Exists(path))
            {
                Assert.Ignore("Editor source file not found: " + path);
            }

            return File.ReadAllText(path);
        }

        private static IEnumerable<string> AllPreviewSources()
        {
            if (!Directory.Exists(PreviewDirectory))
            {
                Assert.Ignore("Preview source directory not found: " + PreviewDirectory);
            }

            var files = Directory.GetFiles(PreviewDirectory, "*.cs", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        [Test]
        public void PreviewAssembly_IsEditorOnlyAndReferencesTheCoreAndNdmf()
        {
            var asmdef = ReadPreviewSource("dev.avatar-part-assembler.editor.preview.asmdef");

            StringAssert.Contains("\"nadena.dev.ndmf\"", asmdef);
            StringAssert.Contains("\"dev.avatar-part-assembler.editor\"", asmdef);
            StringAssert.Contains("\"dev.avatar-part-assembler.runtime\"", asmdef);
            StringAssert.Contains("\"Editor\"", asmdef);

            // The preview layer is editor-only, so it must not be compiled into a player build.
            Assert.IsFalse(
                asmdef.Contains("\"includePlatforms\": []"),
                "The preview assembly must be restricted to the Editor platform.");
        }

        [Test]
        public void PreviewFilter_ImplementsNdmfRenderFilterAndPublishesItsRegistrationSeam()
        {
            var filter = ReadPreviewSource("AvatarPartRenderFilter.cs");

            StringAssert.Contains(": IRenderFilter", filter);
            StringAssert.Contains("GetTargetGroups", filter);
            StringAssert.Contains("Instantiate", filter);

            var registration = ReadPreviewSource("PreviewRegistration.cs");
            StringAssert.Contains("PreviewingWith", registration);
            StringAssert.Contains("CreateFilter", registration);
        }

        [Test]
        public void PreviewFilter_AddsAGroupOnlyForARenderableRequest()
        {
            var filter = ReadPreviewSource("AvatarPartRenderFilter.cs");

            var renderableCheck = filter.IndexOf("if (!request.IsRenderable", StringComparison.Ordinal);
            var addGroup = filter.IndexOf("groups.Add(", StringComparison.Ordinal);

            Assert.GreaterOrEqual(renderableCheck, 0, "The renderable check is missing.");
            Assert.GreaterOrEqual(addGroup, 0, "No group is ever added.");
            Assert.Less(
                renderableCheck,
                addGroup,
                "A blocked request must not reach the group list, or a stale successful preview would survive an " +
                "invalid input.");
        }

        [Test]
        public void PreviewFilter_ReportsDiagnosticsAndClearsThemWhenUnaffected()
        {
            var filter = ReadPreviewSource("AvatarPartRenderFilter.cs");

            StringAssert.Contains("ApaPreviewDiagnostics.ReportRequest(", filter);
            StringAssert.Contains("ApaPreviewDiagnostics.ClearAvatar(", filter);
        }

        [Test]
        public void PreviewFilter_GuardsTheWholeNodeConstructionPath()
        {
            var filter = ReadPreviewSource("AvatarPartRenderFilter.cs");

            var instantiate = filter.IndexOf("public Task<IRenderFilterNode> Instantiate(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(instantiate, 0, "Instantiate was not found.");

            var guard = filter.IndexOf("catch (Exception e)", instantiate, StringComparison.Ordinal);
            Assert.Greater(guard, instantiate, "Instantiate must guard the whole node-construction path.");

            var nextMember = filter.IndexOf("private static void AddGroupsForRoot(", guard, StringComparison.Ordinal);
            Assert.Greater(nextMember, guard, "The method after Instantiate was not found.");

            var report = filter.IndexOf("ReportInternalFailure", guard, StringComparison.Ordinal);
            var empty = filter.IndexOf("ApaPreviewEmptyNode", guard, StringComparison.Ordinal);

            Assert.Greater(report, guard, "The Instantiate guard must report the failure.");
            Assert.Less(report, nextMember, "The Instantiate guard must report the failure.");
            Assert.Greater(empty, guard, "The Instantiate guard must return a safe node.");
            Assert.Less(empty, nextMember, "The Instantiate guard must return a safe node.");
        }

        [Test]
        public void PreviewSceneViewOverlay_DefaultsToOff()
        {
            var registration = ReadPreviewSource("PreviewRegistration.cs");

            var overlay = Regex.Match(
                registration,
                @"DebugOverlay[\s\S]{0,200}?TogglablePreviewNode\.Create\([\s\S]*?initialState:\s*(?<state>true|false)",
                RegexOptions.Multiline);

            Assert.IsTrue(overlay.Success, "The debug overlay toggle declaration was not found.");
            Assert.AreEqual(
                "false",
                overlay.Groups["state"].Value,
                "The seam/removal debug overlay must be off by default (specification section 29).");
        }

        [Test]
        public void PreviewSceneViewOverlay_DrawsWithoutMutatingAssets()
        {
            var overlay = ReadPreviewSource("PreviewDebugOverlay.cs");

            StringAssert.Contains("SceneView.duringSceneGui", overlay);
            StringAssert.Contains("RemovedTriangles", overlay);
            StringAssert.Contains("SeamResolutions", overlay);
        }

        [Test]
        public void PreviewMeshCache_IsBoundedAndTornDownOnDomainReload()
        {
            var cache = ReadPreviewSource("PreviewCache.cs");

            StringAssert.Contains("DefaultCapacity", cache);
            StringAssert.Contains("TrimToCapacity", cache);
            StringAssert.Contains("DestroyImmediate", cache);
            StringAssert.Contains("beforeAssemblyReload", cache);
            StringAssert.Contains("EditorApplication.quitting", cache);
            StringAssert.Contains("LeaseCount", cache);
        }

        [Test]
        public void PreviewFingerprint_AvoidsProcessRandomizedHashing()
        {
            var fingerprint = ReadPreviewSource("PreviewFingerprint.cs");

            StringAssert.Contains("BitConverter.DoubleToInt64Bits", fingerprint);
            StringAssert.Contains("Algorithm", fingerprint);
            StringAssert.Contains("apa-preview-fnv1a64-v2", fingerprint);
            Assert.IsFalse(
                fingerprint.Contains("bone-world-to-local"),
                "Live bone pose matrices must not invalidate the preview assembly cache.");
            Assert.IsFalse(
                Regex.IsMatch(fingerprint, @"\bHashCode\s*\."),
                "System.HashCode is seeded per process and cannot key a cache that must be reproducible.");
            Assert.IsFalse(
                Regex.IsMatch(fingerprint, @"\.GetHashCode\s*\("),
                "Instance hash codes are not content fingerprints; use the value-based builder.");
        }

        [Test]
        public void PreviewInputObserver_CoversEveryDocumentedInput()
        {
            var observer = ReadPreviewSource("PreviewInputObserver.cs");

            // Installer state, profile data, target and part renderers, meshes, transforms, materials, bones.
            StringAssert.Contains("EnabledForBuild", observer);
            StringAssert.Contains("Profile", observer);
            StringAssert.Contains("sharedMaterials", observer);
            StringAssert.Contains("bones", observer);
            StringAssert.Contains("rootBone", observer);
            StringAssert.Contains("ObserveMesh", observer);
            StringAssert.Contains("ObserveTransform", observer);
            StringAssert.Contains("ObserveBoneStructure", observer);
            StringAssert.Contains("ObservePath", observer);

            // Blend shape weights are deliberately not observed: they are applied per frame, not baked.
            StringAssert.Contains("Blend shape weights are deliberately not observed", observer);
        }

        [Test]
        public void PreviewInputObserver_DoesNotRebuildOnBonePoseEdits()
        {
            var observer = ReadPreviewSource("PreviewInputObserver.cs");

            StringAssert.Contains("Bone TRS is pose state, not assembly input", observer);
            StringAssert.Contains("BoneStructureToken", observer);
            StringAssert.Contains("Do not poll localPosition/localRotation/localScale here", observer);
            StringAssert.Contains("BoneStructures", observer);
            StringAssert.Contains("generic transform observation", observer);
        }

        [Test]
        public void PlayModePrebuild_UsesEditorTransitionGateBeforeAwake()
        {
            var source = ReadNdmfSource("ApaPlayModeCompatibility.cs");

            StringAssert.Contains("EditorApplication.isPlayingOrWillChangePlaymode", source);
            StringAssert.Contains("early prebuild is skipped", source);
            Assert.IsFalse(
                source.Contains("if (!Application.isPlaying) return;"),
                "The temporary Play Mode scene may be processed before Application.isPlaying flips true.");
        }

        [Test]
        public void FinalBoneTable_PrefersAuthoredSourceBindPoses()
        {
            var source = ReadEditorSource("Assembly", "FinalBoneTable.cs");

            StringAssert.Contains("sourceBindPose * targetToSource", source);
            StringAssert.Contains("legacy snapshots", source);
            StringAssert.Contains("ComputeBindPose", source);
        }

        [Test]
        public void PlayModeFingerprint_IsValidatedBeforeArmatureMerge()
        {
            var merge = ReadNdmfSource("ApaMergeArmaturePass.cs");
            var assembly = ReadNdmfSource("ApaAssemblyPass.cs");

            StringAssert.Contains("ValidatePartMeshBeforeMerge", merge);
            StringAssert.Contains("CompatibilityRule.ValidatePartMeshFingerprint", merge);
            StringAssert.Contains("partMeshFingerprintsVerifiedBeforeMerge: true", assembly);
        }

        [Test]
        public void PreviewLayer_WritesOnlyProxyRenderers()
        {
            var meshWriters = new List<string>();
            var materialWriters = new List<string>();
            var boneWriters = new List<string>();

            foreach (var file in AllPreviewSources())
            {
                var text = File.ReadAllText(file);
                if (Regex.IsMatch(text, @"\.sharedMesh\s*=")) meshWriters.Add(Path.GetFileName(file));
                if (Regex.IsMatch(text, @"\.sharedMaterials\s*=")) materialWriters.Add(Path.GetFileName(file));
                if (Regex.IsMatch(text, @"\.bones\s*=")) boneWriters.Add(Path.GetFileName(file));
            }

            CollectionAssert.AreEqual(
                new[] { "PreviewProxyApplier.cs" },
                meshWriters,
                "Only the proxy applier may assign a shared mesh.");
            CollectionAssert.AreEqual(
                new[] { "PreviewProxyApplier.cs" },
                materialWriters,
                "Only the proxy applier may assign shared materials.");
            CollectionAssert.AreEqual(
                new[] { "PreviewProxyApplier.cs" },
                boneWriters,
                "Only the proxy applier may assign a bones array.");

            var applier = ReadPreviewSource("PreviewProxyApplier.cs");
            StringAssert.Contains("public static void Apply(", applier);
            StringAssert.Contains("Renderer proxy", applier);
        }

        [Test]
        public void PreviewLayer_DoesNotMutateAssets()
        {
            foreach (var file in AllPreviewSources())
            {
                var text = File.ReadAllText(file);
                var name = Path.GetFileName(file);

                Assert.IsFalse(
                    Regex.IsMatch(text, @"AssetDatabase\.(CreateAsset|SaveAssets|DeleteAsset|ImportAsset|Refresh|AddObjectToAsset)"),
                    name + " performs an asset database mutation.");
                Assert.IsFalse(
                    Regex.IsMatch(text, @"EditorUtility\.SetDirty"),
                    name + " marks an asset dirty.");
                Assert.IsFalse(
                    Regex.IsMatch(text, @"\bUndo\.(RecordObject|RegisterCompleteObjectUndo|DestroyObjectImmediate)"),
                    name + " records a mutation; the preview must not modify the scene or assets.");
            }
        }

        [Test]
        public void PreviewNode_ReleasesItsLeaseOnDisposeAndOnFailedConstruction()
        {
            var node = ReadPreviewSource("PreviewNode.cs");

            StringAssert.Contains("_lease.Dispose()", node);
            StringAssert.Contains("ApaPreviewDebugOverlay.Unregister", node);

            // Create takes the cache lease and nothing else can release it if construction throws: Dispose is only
            // reachable through a constructed node, and a leased cache entry is never evicted.
            var create = node.IndexOf("public static IRenderFilterNode Create(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(create, 0, "Create must be non-throwing and must return IRenderFilterNode.");

            var guard = node.IndexOf("catch (Exception e)", create, StringComparison.Ordinal);
            Assert.Greater(guard, create, "Create must guard the whole construction path.");

            var nextMember = node.IndexOf("private static ApaPreviewLease BuildThroughCore(", guard, StringComparison.Ordinal);
            Assert.Greater(nextMember, guard, "The method after Create was not found.");

            var release = node.IndexOf("lease.Dispose()", guard, StringComparison.Ordinal);
            var report = node.IndexOf("ReportInternalFailure", guard, StringComparison.Ordinal);
            var empty = node.IndexOf("ApaPreviewEmptyNode", guard, StringComparison.Ordinal);

            Assert.Greater(release, guard, "A failed construction must release the lease it took.");
            Assert.Less(release, nextMember, "A failed construction must release the lease it took.");
            Assert.Greater(report, guard, "A failed construction must report an internal diagnostic.");
            Assert.Less(report, nextMember, "A failed construction must report an internal diagnostic.");
            Assert.Greater(empty, guard, "A failed construction must return a safe node.");
            Assert.Less(empty, nextMember, "A failed construction must return a safe node.");
        }

        [Test]
        public void PreviewNode_GuardsTheRefreshPathToo()
        {
            var node = ReadPreviewSource("PreviewNode.cs");

            var refresh = node.IndexOf("public Task<IRenderFilterNode> Refresh(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(refresh, 0, "Refresh was not found.");

            var guard = node.IndexOf("catch (Exception e)", refresh, StringComparison.Ordinal);
            Assert.Greater(guard, refresh, "Refresh runs on NDMF's build task and must be guarded as well.");

            var core = node.IndexOf("private IRenderFilterNode RefreshCore(", refresh, StringComparison.Ordinal);
            Assert.Greater(core, guard, "Refresh must delegate to the guarded core.");

            var empty = node.IndexOf("ApaPreviewEmptyNode", guard, StringComparison.Ordinal);
            Assert.Greater(empty, guard, "A failed refresh must return a safe node.");
            Assert.Less(empty, core, "A failed refresh must return a safe node.");
        }

        [Test]
        public void PreviewEmptyNode_RetriesFromLiveInputInsteadOfReturningNull()
        {
            var node = ReadPreviewSource("PreviewNode.cs");

            var empty = node.IndexOf("public sealed class ApaPreviewEmptyNode", StringComparison.Ordinal);
            Assert.GreaterOrEqual(empty, 0, "The empty node was not found.");

            var body = node.Substring(empty);
            StringAssert.Contains("ApaPreviewDiscovery.DiscoverGroups(", body);
            Assert.IsFalse(
                body.Contains("Task.FromResult<IRenderFilterNode>(null)"),
                "An empty node must not answer Refresh with null: the pipeline would rebuild from the group data " +
                "the node could not use.");
        }

        [Test]
        public void PreviewNode_NeverClaimsToInvalidateAContextItCannotInvalidate()
        {
            var node = ReadPreviewSource("PreviewNode.cs");

            // The context passed to Refresh belongs to the *replacement* controller, so invalidating it only
            // drops that controller's subscriptions; and returning null makes the pipeline rebuild from the group
            // data the refresh just rejected. The node must recapture the live inputs instead.
            Assert.IsFalse(
                node.Contains("context.Invalidate()"),
                "A node's Refresh context is the replacement controller's; invalidating it cannot re-discover the " +
                "target set.");
            Assert.IsFalse(
                node.Contains("InvalidateAndRebuild"),
                "The removed no-op must not come back.");

            StringAssert.Contains("ApaPreviewDiscovery.DiscoverGroups(", node);
            StringAssert.Contains("RebuildFromLiveInput", node);
        }

        [Test]
        public void PreviewDiagnostics_KeyReportsBySceneAndObjectIdentityAndDropOrphans()
        {
            var diagnostics = ReadPreviewSource("PreviewDiagnostics.cs");

            StringAssert.Contains("public int SceneHandle", diagnostics);
            StringAssert.Contains("public int ObjectId", diagnostics);
            StringAssert.Contains("GetInstanceID()", diagnostics);
            StringAssert.Contains("PruneOrphans", diagnostics);

            // Reports must not be dictionary-keyed by the hierarchy path, which is not unique across scenes.
            Assert.IsFalse(
                diagnostics.Contains("Dictionary<string, ApaPreviewDiagnosticReport>"),
                "Diagnostics must be keyed by scene plus object identity, not by path.");

            var request = ReadPreviewSource("PreviewDiscovery.cs");
            StringAssert.Contains("ApaPreviewDiagnosticKey.For(", request);
            StringAssert.Contains("CompareAvatarRoots", request);
        }

        [Test]
        public void PreviewDiagnostics_DeduplicatesInternalFailureLogging()
        {
            var diagnostics = ReadPreviewSource("PreviewDiagnostics.cs");

            StringAssert.Contains("s_loggedInternalFailures.Add(message)", diagnostics);
        }

        [Test]
        public void PreviewInputObserver_ObservesEveryNodeOfTheTransformPath()
        {
            var observer = ReadPreviewSource("PreviewInputObserver.cs");

            var observeTransform = observer.IndexOf(
                "private static void ObserveTransform(ComputeContext context, Transform transform, ObservationScope scope)",
                StringComparison.Ordinal);
            Assert.GreaterOrEqual(observeTransform, 0, "The transform observation method was not found.");

            var walk = observer.IndexOf(
                "foreach (var node in context.ObservePath(transform))",
                observeTransform,
                StringComparison.Ordinal);
            Assert.Greater(walk, observeTransform, "Every ancestor of an observed transform must be walked.");

            var observe = observer.IndexOf("context.Observe(node, TransformToken)", walk, StringComparison.Ordinal);
            Assert.Greater(observe, walk, "Every node of the path must be polled for its local TRS.");
        }

        [Test]
        public void PreviewSceneViewOverlay_DeduplicatesFailuresAndAvoidsTheOneShotToggleEvent()
        {
            var overlay = ReadPreviewSource("PreviewDebugOverlay.cs");

            // duringSceneGui runs on every repaint: a persistent drawing failure must be logged once per distinct
            // failure, not once per frame.
            StringAssert.Contains("s_lastFailure", overlay);

            // PublishedValue clears its OnChange subscribers after firing, so subscribing to the toggle works
            // exactly once and is then dead. NDMF repaints the Scene View on every value change anyway.
            Assert.IsFalse(
                overlay.Contains(".OnChange +="),
                "A direct PublishedValue.OnChange subscription is one-shot; rely on the repaint NDMF requests.");
        }

        [Test]
        public void PreviewRegistration_NamesTheRealPassAndThePassInstanceOverload()
        {
            var registration = ReadPreviewSource("PreviewRegistration.cs");

            StringAssert.Contains("ApaAssemblyPass.Instance", registration);
            Assert.IsFalse(
                registration.Contains("MeshAssemblyPass"),
                "MeshAssemblyPass was the M4 placeholder name; the real Transforming pass is ApaAssemblyPass.");
            Assert.IsFalse(
                registration.Contains(".Run<ApaAssemblyPass>()"),
                "Sequence.Run<T> has no parameterless overload; the pass instance is required.");
        }

        [Test]
        public void PreviewIsRegisteredOnTheRealTransformingPass()
        {
            var plugin = ReadNdmfSource("ApaNdmfPlugin.cs");

            // The registration is a pass declaration, not a direct session call: NDMF collects a pass's render
            // filters, which is also what makes the filter appear in Configure Previews.
            StringAssert.Contains("PreviewingWith(", plugin);
            StringAssert.Contains("ApaPreviewRegistration.CreateFilter()", plugin);
            StringAssert.Contains("ApaAssemblyPass.Instance", plugin);

            var transformPhase = plugin.IndexOf("BuildPhase.Transforming", StringComparison.Ordinal);
            var run = plugin.IndexOf(".Run(ApaAssemblyPass.Instance)", StringComparison.Ordinal);
            var preview = plugin.IndexOf("PreviewingWith(", StringComparison.Ordinal);

            Assert.GreaterOrEqual(transformPhase, 0, "The Transforming phase declaration was not found.");
            Assert.Greater(run, transformPhase, "The assembly pass must be declared in Transforming.");
            Assert.Greater(preview, run, "PreviewingWith must be chained onto the assembly pass declaration.");

            // The registration assembly must reference the preview assembly; the reverse reference is a cycle.
            var ndmfAsmdef = ReadNdmfSource("dev.avatar-part-assembler.editor.ndmf.asmdef");
            StringAssert.Contains("\"dev.avatar-part-assembler.editor.preview\"", ndmfAsmdef);

            var previewAsmdef = ReadPreviewSource("dev.avatar-part-assembler.editor.preview.asmdef");
            Assert.IsFalse(
                previewAsmdef.Contains("\"dev.avatar-part-assembler.editor.ndmf\""),
                "The preview assembly must not reference the NDMF assembly: that reference is an assembly cycle.");
        }

        [Test]
        public void PreviewDiscovery_UsesTheBuildsGroupedPlanAndInstallerSet()
        {
            var discovery = ReadPreviewSource("PreviewDiscovery.cs");

            // Preview uses the same grouped transaction and the same installer discovery as the build.
            StringAssert.Contains("ApaCore.PlanGroups(", discovery);
            StringAssert.Contains("ContextBuilder.CollectInstallers(", discovery);
            StringAssert.Contains("ContextBuilder.CollectAllInstallers(", discovery);
            StringAssert.Contains("DiscoverGroups(", discovery);
        }

        [Test]
        public void PreviewFilter_OneGroupPerTargetAndHidesTheConsumedPartGeometry()
        {
            var filter = ReadPreviewSource("AvatarPartRenderFilter.cs");

            // One render group per discovered target group, not one per avatar.
            StringAssert.Contains("DiscoverGroups(", filter);
            StringAssert.Contains("request.ConsumedRenderers", filter);

            var node = ReadPreviewSource("PreviewNode.cs");
            StringAssert.Contains("proxy.enabled = false", node);
            StringAssert.Contains("TargetGroupAssembly.BuildGroup(", node);
        }

        [Test]
        public void PreviewInputObserver_PollsContentStableTokensInsteadOfFreshArrays()
        {
            var observer = ReadPreviewSource("PreviewInputObserver.cs");

            // Unity's sharedMaterials and bones getters allocate a new array on every access, and the poll token
            // is compared with the default equality comparer once per registered object per editor frame: a token
            // that carried the arrays themselves would compare as changed every frame and rebuild the whole
            // preview pipeline. The token therefore carries element-identity hashes.
            var token = observer.IndexOf(
                "private static (Mesh mesh, int materialsHash, int bonesHash,", StringComparison.Ordinal);
            Assert.GreaterOrEqual(
                token,
                0,
                "The renderer poll token must summarize its collections as stable hashes, not as arrays.");
            StringAssert.Contains("ElementIdentityHash(renderer.sharedMaterials)", observer);
            StringAssert.Contains("ElementIdentityHash(skinned != null ? skinned.bones : null)", observer);
            Assert.IsFalse(
                Regex.IsMatch(observer, @"private static \(Mesh mesh, Material\[\] materials"),
                "The renderer token must not compare the freshly allocated material array.");
            Assert.IsFalse(
                Regex.IsMatch(observer, @"private static \(Mesh mesh, Material\[\] materials, Transform\[\] bones"),
                "The renderer token must not compare the freshly allocated bone array.");

            // The poll runs on a preview path, so the profile must be read through the non-mutating accessor: the
            // materializing Identity getter would assign a nested object into the shared authoring asset.
            StringAssert.Contains("profile.IdentityOrNull", observer);
            Assert.IsFalse(
                Regex.IsMatch(observer, @"profile\.(Identity|Bones|Compatibility|Removal|Seam|BlendShapes)\b"),
                "The preview poll must not use a materializing profile accessor.");
        }

        [Test]
        public void PreviewDiagnosticLabels_AnchorOnTheirOwnTargetNotOnTheAvatarRoot()
        {
            var overlay = ReadPreviewSource("PreviewDebugOverlay.cs");

            // One avatar has one report per target group; anchoring every report on the avatar root stacks the
            // labels on one pixel and hides every group but the last.
            StringAssert.Contains("report.TargetRenderer", overlay);
            StringAssert.Contains("LabelPosition(target.bounds.center)", overlay);
        }

        [Test]
        public void AssemblyPass_RefusesABuildTheGeneratingPassNeverConfigured()
        {
            var pass = ReadNdmfSource("ApaAssemblyPass.cs");
            var artifacts = ReadNdmfSource("ApaTransientArtifacts.cs");

            // An empty configuration list is "nothing needed merging" only when the Generating pass ran; when it
            // did not, no part's bones were merged and the empty postcondition would pass silently.
            StringAssert.Contains("internal bool Configured", artifacts);
            StringAssert.Contains("!artifacts.Configured", pass);
            StringAssert.Contains("reason=merge-pass-did-not-run", pass);

            var generating = ReadNdmfSource("ApaMergeArmaturePass.cs");
            StringAssert.Contains("MarkConfigured()", generating);
        }

        [Test]
        public void GeneratingPass_ReadsTheProfileWithoutMaterializingIt()
        {
            var generating = ReadNdmfSource("ApaMergeArmaturePass.cs");

            StringAssert.Contains("profile.BonesOrNull", generating);
            Assert.IsFalse(
                Regex.IsMatch(generating, @"profile\.(Identity|Bones|Compatibility|Removal|Seam|BlendShapes)\b"),
                "The Generating pass runs inside a build and must not write to the shared profile asset.");
        }

        [Test]
        public void MergeNamePolicy_IsAbsentFromTheGeneratingPath()
        {
            var generator = ReadEditorSource("Integration", "MergeArmatureGenerator.cs");

            // M10 removed the name policy from the build. A rewritten bone name could make Modular Avatar match
            // two bones whose paths relative to their own armatures differ — that is, a bone this pipeline
            // considers a different joint — so the component is always written for exact-name matching, and the
            // profile's legacy prefix/suffix/inference fields are never read on this path. There is no inference
            // call left to guard: Modular Avatar's InferPrefixSuffix() is not invoked at all.
            StringAssert.Contains("merge.prefix = string.Empty;", generator);
            StringAssert.Contains("merge.suffix = string.Empty;", generator);
            Assert.IsFalse(
                generator.Contains("InferPrefixSuffix"),
                "Inference rewrites the names exact matching relies on and must not be called.");
            Assert.IsFalse(
                Regex.IsMatch(generator, @"Bones\.(MergePrefix|MergeSuffix|InferMergeNames)\b"),
                "The generating path must not consult the legacy merge-name policy; the two selected armatures " +
                "decide the merge.");
        }
    }
}

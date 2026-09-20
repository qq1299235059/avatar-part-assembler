using System;
using System.IO;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor.Localization;
using AvatarPartAssembler.Editor.Preview;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Contract tests for the installer Inspector's end-user surface and for the preview-only culling fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of promise are pinned here. The behavioural ones are driven directly: a newly added installer
    /// must serialize both bone-fit preferences as enabled, and the preview proxy applier must union the copied
    /// source bounds with the generated mesh bounds and enable offscreen updates on the skinned proxy alone.
    /// The structural ones read the sources, because what they pin — that the developer detail block is gone
    /// from the installer Inspector, that the health verdict is cached instead of recomputed per repaint, and
    /// that the follow session is synchronized from the persisted preferences — is a shape of the code rather
    /// than a value a unit test can call.
    /// </para>
    /// <para>
    /// The sources are read relative to <see cref="Application.dataPath"/>, exactly as the other source-contract
    /// suites do, so the tests run from the repository checkout without an asset reference.
    /// </para>
    /// </remarks>
    public class InstallerUxContractTests
    {
        private static string PackageRoot
        {
            get
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler");
            }
        }

        private static string ReadSource(string relativePath)
        {
            var path = Path.Combine(PackageRoot, relativePath);
            if (!File.Exists(path))
            {
                Assert.Ignore("Package source not found: " + path);
            }

            return File.ReadAllText(path);
        }

        private static string InstallerSource => ReadSource(Path.Combine("Runtime", "Components", "AvatarPartInstaller.cs"));

        private static string InspectorSource => ReadSource(Path.Combine("Editor", "Authoring", "ApaInstallerInspector.cs"));

        private static string PreviewApplierSource => ReadSource(Path.Combine("Editor", "Preview", "PreviewProxyApplier.cs"));

        // ---- The persisted bone-fit preferences --------------------------------------------------------

        [Test]
        public void NewInstaller_DefaultsBothBoneFitPreferencesToEnabled()
        {
            var host = new GameObject("apa-installer-defaults");
            try
            {
                var installer = host.AddComponent<AvatarPartInstaller>();

                Assert.IsTrue(
                    installer.FollowAvatarBones,
                    "A newly added installer must default to following the avatar's bones.");
                Assert.IsTrue(
                    installer.IncludeScale,
                    "A newly added installer must default to including the avatar bone scale.");
                Assert.IsTrue(installer.EnabledForBuild, "The build switch keeps its own enabled default.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Installer_SerializesBothPreferencesWithEnabledDefaults()
        {
            var source = InstallerSource;

            StringAssert.Contains(
                "[SerializeField] private bool _followAvatarBones = true;",
                source,
                "The follow preference must be serialized on the component, not kept in Inspector state.");
            StringAssert.Contains(
                "[SerializeField] private bool _includeScale = true;",
                source,
                "The scale preference must be serialized on the component, not kept in Inspector state.");
            StringAssert.Contains("public bool FollowAvatarBones", source);
            StringAssert.Contains("public bool IncludeScale", source);

            // Reset is the editor's own "this component was just added" hook; it must not undo the defaults.
            StringAssert.Contains("_followAvatarBones = true;", source);
            StringAssert.Contains("_includeScale = true;", source);
        }

        [Test]
        public void RuntimeInstaller_KeepsTheEditorOut()
        {
            Assert.IsFalse(
                InstallerSource.Contains("using UnityEditor"),
                "The runtime component must stay free of editor dependencies.");
            Assert.IsFalse(
                InstallerSource.Contains("UnityEditor."),
                "The runtime component must stay free of editor dependencies.");

            var asmdef = ReadSource(Path.Combine("Runtime", "dev.avatar-part-assembler.runtime.asmdef"));
            Assert.IsFalse(
                asmdef.Contains("dev.avatar-part-assembler.editor"),
                "The runtime assembly must not reference an editor assembly.");
            Assert.IsFalse(
                asmdef.Contains("UnityEditor"),
                "The runtime assembly must not reference UnityEditor.");
        }

        // ---- The concise health indicator --------------------------------------------------------------

        [Test]
        public void InstallerInspector_NoLongerDrawsTheDeveloperDetailBlock()
        {
            var source = InspectorSource;

            Assert.IsFalse(source.Contains("DrawStatus"), "The old developer status block must be gone.");
            Assert.IsFalse(source.Contains("DrawPartIdStatus"), "The part-id block must be gone.");
            Assert.IsFalse(source.Contains("RepairPartId"), "The part-id repair shortcut must be gone.");
            Assert.IsFalse(source.Contains("DescribeSeam"), "The seam-count line must be gone.");
            Assert.IsFalse(source.Contains("DescribeResolvedTarget"), "The resolved-target line must be gone.");
            Assert.IsFalse(
                source.Contains("DescribeArmatures"),
                "The armature-name line must be gone.");

            var removedLabels = new[]
            {
                "Part Id", "Armatures", "Schema", "Signature", "Target Path", "Removal", "Seam",
                "UV Semantics", "Material Semantics", "Resolved Target"
            };

            foreach (var label in removedLabels)
            {
                Assert.IsFalse(
                    Regex.IsMatch(source, @"\bTr\(\s*""" + Regex.Escape(label) + @"""\s*\)"),
                    "The installer Inspector must no longer draw the developer label '" + label + "'.");
            }
        }

        [Test]
        public void InstallerInspector_ShowsAGreenOrRedVerdictDerivedFromValidation()
        {
            var source = InspectorSource;

            StringAssert.Contains("private void DrawHealth(", source);
            StringAssert.Contains("ApaInstallerHealth.Ready", source);
            StringAssert.Contains("ApaInstallerHealth.Problem", source);
            StringAssert.Contains("s_readyBackground", source);
            StringAssert.Contains("s_problemBackground", source);
            StringAssert.Contains("DrawHealthBox", source);

            // The verdict is the validation's own answer, not a second opinion about the profile's fields.
            StringAssert.Contains(
                "_health = _validation.HasErrors ? ApaInstallerHealth.Problem : ApaInstallerHealth.Ready;",
                source,
                "The verdict must come from the cached validation result.");

            // Both verdicts name the part in the user's words and keep the diagnostics below.
            StringAssert.Contains("This part is ready: validation reported {0}.", source);
            StringAssert.Contains("This part has a problem: validation reported {0}. See the details below.", source);

            // The detailed diagnostics stay, drawn from the same cached result.
            StringAssert.Contains("private void DrawDiagnostics()", source);
            StringAssert.Contains("_validation.Issues", source);
        }

        [Test]
        public void InstallerInspector_ValidatesOncePerRelevantChangeInsteadOfPerRepaint()
        {
            var source = InspectorSource;

            StringAssert.Contains("private void RefreshValidation(", source);
            StringAssert.Contains("_validatedSignature", source);
            StringAssert.Contains("EditorUtility.GetDirtyCount(profile)", source);
            StringAssert.Contains(
                "if (string.Equals(signature, _validatedSignature, StringComparison.Ordinal)) return;",
                source,
                "A repaint whose inputs are unchanged must reuse the cached verdict.");

            // The health line must go through the cached refresh, never through a direct validation call.
            var health = source.IndexOf("private void DrawHealth(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(health, 0, "DrawHealth was not found.");

            var nextMember = source.IndexOf("private static void DrawHealthBox(", health, StringComparison.Ordinal);
            Assert.Greater(nextMember, health, "The method after DrawHealth was not found.");

            var body = source.Substring(health, nextMember - health);
            StringAssert.Contains("RefreshValidation(installer);", body);
            Assert.IsFalse(
                body.Contains("RunValidation("),
                "The health line must not validate directly; that is what the signature cache prevents.");

            // The explicit Validate action still validates and reports.
            StringAssert.Contains("RunValidation(installer, true);", source);
        }

        // ---- Follow-session synchronization ------------------------------------------------------------

        [Test]
        public void InstallerInspector_PersistsAndSynchronizesTheBoneFitPreferences()
        {
            var source = InspectorSource;

            StringAssert.Contains("serializedObject.FindProperty(\"_followAvatarBones\")", source);
            StringAssert.Contains("serializedObject.FindProperty(\"_includeScale\")", source);
            StringAssert.Contains("SynchronizeFollowSession(installer, pairs.Count)", source);
            StringAssert.Contains("ApaBoneFollowRuntime.SetFollowing(installer, true, installer.IncludeScale)", source);
            StringAssert.Contains("ApaBoneFollowRuntime.SetFollowing(installer, false, false)", source);

            Assert.IsFalse(
                source.Contains("_boneFitIncludeScale"),
                "The transient Inspector-only scale state must be gone; the preference lives on the component.");
            Assert.IsFalse(
                source.Contains("EditorGUILayout.Toggle(Tr(\"Follow Avatar Bones\")"),
                "The toggles must be bound to the serialized preferences.");
            Assert.IsFalse(
                source.Contains("EditorGUILayout.Toggle(Tr(\"Include Scale\")"),
                "The toggles must be bound to the serialized preferences.");
        }

        // ---- The preview-only culling fix --------------------------------------------------------------

        [Test]
        public void PreviewSkinnedProxy_UnionsTheSourceBoundsWithTheGeneratedMeshBounds()
        {
            var host = new GameObject("apa-preview-bounds");
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, -0.5f),
                    new Vector3(0.5f, 0.5f, 0.5f)
                };
                mesh.RecalculateBounds();

                var proxyObject = new GameObject("proxy");
                proxyObject.transform.SetParent(host.transform, false);
                var proxy = proxyObject.AddComponent<SkinnedMeshRenderer>();

                // NDMF copies the source renderer's bounds onto the proxy every frame; this is that state.
                var sourceBounds = new Bounds(new Vector3(0f, 10f, 0f), new Vector3(2f, 2f, 2f));
                proxy.localBounds = sourceBounds;

                ApaPreviewProxyApplier.Apply(proxy, mesh, null, null, null);

                var expected = sourceBounds;
                expected.Encapsulate(mesh.bounds);

                Assert.AreEqual(expected.center.x, proxy.localBounds.center.x, 1e-5f, "center.x");
                Assert.AreEqual(expected.center.y, proxy.localBounds.center.y, 1e-5f, "center.y");
                Assert.AreEqual(expected.center.z, proxy.localBounds.center.z, 1e-5f, "center.z");
                Assert.AreEqual(expected.size.x, proxy.localBounds.size.x, 1e-5f, "size.x");
                Assert.AreEqual(expected.size.y, proxy.localBounds.size.y, 1e-5f, "size.y");
                Assert.AreEqual(expected.size.z, proxy.localBounds.size.z, 1e-5f, "size.z");

                // The source bounds must not have been replaced by the tight generated rest-pose bounds.
                Assert.IsTrue(
                    proxy.localBounds.Contains(sourceBounds.center),
                    "The copied source renderer's animation-safe bounds must survive the union.");
                Assert.IsTrue(
                    proxy.localBounds.Contains(mesh.bounds.center),
                    "The generated mesh bounds must be part of the union.");
                Assert.Greater(
                    proxy.localBounds.size.y,
                    mesh.bounds.size.y,
                    "The union must be larger than the generated mesh bounds alone.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void PreviewSkinnedProxy_KeepsItsBoundsUpToDateOffscreen()
        {
            var host = new GameObject("apa-preview-offscreen");
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, -0.5f),
                    new Vector3(0.5f, 0.5f, 0.5f)
                };
                mesh.RecalculateBounds();

                var proxyObject = new GameObject("proxy");
                proxyObject.transform.SetParent(host.transform, false);
                var proxy = proxyObject.AddComponent<SkinnedMeshRenderer>();
                proxy.updateWhenOffscreen = false;

                ApaPreviewProxyApplier.Apply(proxy, mesh, null, null, null);

                Assert.IsTrue(
                    proxy.updateWhenOffscreen,
                    "A preview proxy whose bounds do not track animation is culled at some view angles.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void PreviewMeshProxy_KeepsTheTightGeneratedBounds()
        {
            var host = new GameObject("apa-preview-mesh-bounds");
            var mesh = new Mesh();
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, -0.5f),
                    new Vector3(0.5f, 0.5f, 0.5f)
                };
                mesh.RecalculateBounds();

                var proxyObject = new GameObject("proxy");
                proxyObject.transform.SetParent(host.transform, false);
                proxyObject.AddComponent<MeshFilter>();
                var proxy = proxyObject.AddComponent<MeshRenderer>();
                proxy.localBounds = new Bounds(new Vector3(0f, 10f, 0f), new Vector3(2f, 2f, 2f));

                ApaPreviewProxyApplier.Apply(proxy, mesh, null, null, null);

                Assert.AreEqual(mesh.bounds.center.x, proxy.localBounds.center.x, 1e-5f);
                Assert.AreEqual(mesh.bounds.center.y, proxy.localBounds.center.y, 1e-5f);
                Assert.AreEqual(mesh.bounds.center.z, proxy.localBounds.center.z, 1e-5f);
                Assert.AreEqual(mesh.bounds.size.x, proxy.localBounds.size.x, 1e-5f);
                Assert.AreEqual(mesh.bounds.size.y, proxy.localBounds.size.y, 1e-5f);
                Assert.AreEqual(mesh.bounds.size.z, proxy.localBounds.size.z, 1e-5f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void PreviewCullingFix_IsConfinedToThePreviewApplier()
        {
            var preview = PreviewApplierSource;
            StringAssert.Contains("bounds.Encapsulate(mesh.bounds);", preview);
            StringAssert.Contains("skinned.updateWhenOffscreen = true;", preview);

            // The union must be built from the proxy's copied source bounds, not from a fresh guess.
            Assert.Less(
                preview.IndexOf("var bounds = proxy.localBounds;", StringComparison.Ordinal),
                preview.IndexOf("bounds.Encapsulate(mesh.bounds);", StringComparison.Ordinal),
                "The source bounds must be read before they are overwritten.");

            // Offscreen updates and bounds writes must exist nowhere else in the shipped code: no build-time mesh
            // inflation and no writes to a real avatar renderer. The scan covers the Runtime and Editor sources,
            // not the tests, which necessarily write the proxy they assert on.
            var offenders = new System.Collections.Generic.List<string>();
            var files = new System.Collections.Generic.List<string>();
            files.AddRange(Directory.GetFiles(Path.Combine(PackageRoot, "Runtime"), "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(Path.Combine(PackageRoot, "Editor"), "*.cs", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            Assert.IsNotEmpty(files, "The source scan would be vacuous.");

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);

                if (!string.Equals(name, "PreviewProxyApplier.cs", StringComparison.Ordinal)
                    && text.Contains("updateWhenOffscreen"))
                {
                    offenders.Add(name + " sets updateWhenOffscreen");
                }

                var isPreview = file.Contains(Path.Combine("Editor", "Preview"));
                if (!isPreview && Regex.IsMatch(text, @"\.localBounds\s*="))
                {
                    offenders.Add(name + " writes localBounds outside the preview layer");
                }
            }

            Assert.IsEmpty(
                offenders,
                "The culling fix must stay in the preview layer: " + string.Join(" | ", offenders.ToArray()));
        }

        [Test]
        public void PrefabInstanceSourcesAreUnpackedBeforeSaving()
        {
            var source = ReadSource(Path.Combine("Editor", "Authoring", "ApaPrefabGenerator.cs"));

            StringAssert.Contains("CloneUnpackedInstanceRoot(selection.PartRoot)", source);
            StringAssert.Contains("PrefabUnpackMode.OutermostRoot", source);
            StringAssert.Contains("InteractionMode.AutomatedAction", source);
            StringAssert.Contains("PrefabUtility.SaveAsPrefabAsset(\n                    clone != null ? clone : selection.PartRoot",
                source,
                "The save must use the unpacked clone when the selected part is a prefab instance.");
            StringAssert.Contains("DestroyImmediate(clone)", source,
                "The temporary clone must be cleaned up on every exit path.");

            var cloneIndex = source.IndexOf("CloneUnpackedInstanceRoot(selection.PartRoot)", StringComparison.Ordinal);
            var saveIndex = source.IndexOf("PrefabUtility.SaveAsPrefabAsset(", cloneIndex, StringComparison.Ordinal);
            Assert.Greater(saveIndex, cloneIndex, "The clone must be prepared before the prefab save.");
        }

        // ---- Localization, version, and changelog ------------------------------------------------------

        [Test]
        public void InstallerHealthText_HasSimplifiedChineseEntries()
        {
            var keys = new[]
            {
                "This part is ready: validation reported {0}.",
                "This part has a problem: validation reported {0}. See the details below.",
                "This part has not been validated yet.",
                "Validation results were cleared. Press Validate to check this part again.",
                "This installer belongs to a prefab asset, so it can only be validated after it is placed " +
                "under an avatar in a scene.",
                "Keep this part's bones on the avatar's current pose while the part is placed. Stored on this " +
                "installer, so the choice survives closing the Inspector and a domain reload.",
                "Also copy the avatar bones' scale onto the matching part bones. Needed when the avatar's " +
                "bones have been rescaled."
            };

            foreach (var key in keys)
            {
                Assert.IsTrue(ApaLocalization.HasTranslation(key), "No Simplified Chinese entry for: " + key);
            }

            Assert.AreEqual(
                "该部件已就绪：验证结果为 {0}。",
                ApaLocalization.Translate(ApaLanguage.SimplifiedChinese, "This part is ready: validation reported {0}."));
            Assert.AreEqual(
                "该部件存在问题：验证结果为 {0}。请查看下方的详细信息。",
                ApaLocalization.Translate(
                    ApaLanguage.SimplifiedChinese,
                    "This part has a problem: validation reported {0}. See the details below."));
        }

        [Test]
        public void PackageMovesToRc8WithAChangelogEntry()
        {
            StringAssert.Contains(
                "\"version\": \"0.3.0-rc.8\"",
                ReadSource("package.json"),
                "The package prerelease version must be 0.3.0-rc.8.");

            var changelog = ReadSource("CHANGELOG.md");
            StringAssert.Contains("## [0.3.0-rc.8]", changelog, "The changelog must record the new version.");
            StringAssert.Contains("PrefabUnpackMode.OutermostRoot", changelog,
                "The changelog must record the unpack mode.");
            StringAssert.Contains("independent prefab", changelog,
                "The changelog must record that the generated prefab is independent.");
            StringAssert.Contains("## [0.3.0-rc.7]", changelog,
                "The previous milestone must remain recorded.");
        }
    }
}

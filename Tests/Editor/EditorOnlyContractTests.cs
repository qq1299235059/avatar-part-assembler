using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor;
using nadena.dev.ndmf;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Pins the property that removes the VRChat SDK's <i>"The following component types are found on the Avatar
    /// and will be removed by the client: AvatarPartInstaller"</i> warning: the installer is editor-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The warning comes from the SDK panel's own scan of the <b>scene</b> avatar
    /// (<c>VRCSdkControlPanelAvatarBuilder</c> calls <c>SDK3.Validation.AvatarValidation.FindIllegalComponents</c>,
    /// which calls <c>ValidationUtils.FindIllegalComponents(target, whitelist, excludeEditorOnly: true)</c>). That
    /// predicate drops a component when it is whitelisted <i>or</i> — with <c>excludeEditorOnly</c> — when
    /// <c>ValidationUtils.IsEditorOnly</c> says so, and that helper tests <c>component is
    /// VRC.SDKBase.IEditorOnly</c> first. So the fix is a declaration, and the only interesting question is whether
    /// the declaration really resolves to the type the SDK tests.
    /// </para>
    /// <para>
    /// <see cref="SdkIllegalComponentScan_NoLongerReportsTheInstaller"/> answers that by driving the SDK's own
    /// public predicate through reflection — with an <b>empty</b> whitelist, so the only way the installer can be
    /// absent from the result is the editor-only exclusion, and a control component on the same object proves the
    /// scan really ran. That is the user-visible symptom, tested end to end, without the test assembly taking a
    /// compile-time dependency on the SDK (the test asmdef deliberately references only this package and NUnit).
    /// </para>
    /// <para>
    /// The remaining tests pin what the fix must <i>not</i> change: the GameObject is never tagged
    /// <c>EditorOnly</c> (that tag deletes the whole part hierarchy in NDMF's <c>RemoveEditorOnlyPass</c> and in
    /// the SDK's strip), the serialized field set is unchanged (old prefabs keep their data), the build's own
    /// discovery walk still finds the installer, and the runtime assembly reaches editor-only status through
    /// NDMF's compatibility interface rather than by hard-referencing the VRChat SDK or UnityEditor.
    /// </para>
    /// </remarks>
    public sealed class EditorOnlyContractTests
    {
        /// <summary>The serialized fields the component shipped with, as <c>type name</c> pairs.</summary>
        /// <remarks>
        /// A declaration-only change must not move this set: an installer saved by an earlier package version is
        /// deserialized by field name, so a rename or a retype silently drops the author's data.
        /// </remarks>
        private static readonly string[] s_serializedFields =
        {
            "ApaPartProfile _profile",
            "GameObject _partRoot",
            "GameObject _targetRendererObject",
            "ApaProtectedMeshAsset _protectedMesh",
            "bool _enabledForBuild",
            "bool _followAvatarBones",
            "bool _includeScale"
        };

        /// <summary>A component used as the scan's control: not whitelisted, not editor-only, therefore illegal.</summary>
        private sealed class ScanControlBehaviour : MonoBehaviour
        {
        }

        private static string PackageRoot
        {
            get
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler");
            }
        }

        private static string ReadSource(params string[] relativeParts)
        {
            var path = Path.Combine(PackageRoot, Path.Combine(relativeParts));
            if (!File.Exists(path))
            {
                Assert.Ignore("Package source not found: " + path);
            }

            return File.ReadAllText(path);
        }

        private static string InstallerSource => ReadSource("Runtime", "Components", "AvatarPartInstaller.cs");

        private static string RuntimeAsmdef => ReadSource("Runtime", "dev.avatar-part-assembler.runtime.asmdef");

        // ---- The declaration -------------------------------------------------------------------------

        [Test]
        public void Installer_ImplementsTheNdmfEditorOnlyCompatibilityInterface()
        {
            Assert.IsTrue(
                typeof(INDMFEditorOnly).IsAssignableFrom(typeof(AvatarPartInstaller)),
                "AvatarPartInstaller must implement nadena.dev.ndmf.INDMFEditorOnly; without it the VRChat SDK " +
                "reports the component as one the client will remove.");
        }

        [Test]
        public void NdmfEditorOnly_ResolvesToTheVrchatSdkEditorOnlyInterface()
        {
            var sdkInterface = FindType("VRC.SDKBase.IEditorOnly");
            if (sdkInterface == null)
            {
                Assert.Ignore("The VRChat SDK is not installed, so INDMFEditorOnly is NDMF's no-op interface here.");
            }

            CollectionAssert.Contains(
                typeof(INDMFEditorOnly).GetInterfaces().ToList(),
                sdkInterface,
                "The installed NDMF build must compile INDMFEditorOnly as a VRC.SDKBase.IEditorOnly derivative " +
                "(its NDMF_VRCSDK3_AVATARS define); otherwise the SDK never sees the installer as editor-only.");

            CollectionAssert.Contains(
                typeof(AvatarPartInstaller).GetInterfaces().ToList(),
                sdkInterface,
                "The installer must be an IEditorOnly component, which is what ValidationUtils.IsEditorOnly tests.");
        }

        [Test]
        public void SdkIllegalComponentScan_NoLongerReportsTheInstaller()
        {
            var validationUtils = FindType("VRC.SDKBase.Validation.ValidationUtils");
            if (validationUtils == null)
            {
                Assert.Ignore("The VRChat SDK is not installed, so there is no illegal-component scan to drive.");
            }

            var method = validationUtils.GetMethod(
                "FindIllegalComponents",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(GameObject), typeof(HashSet<Type>), typeof(bool) },
                null);
            Assert.IsNotNull(
                method,
                "ValidationUtils.FindIllegalComponents(GameObject, HashSet<Type>, bool) was not found; the SDK " +
                "scan this test drives has changed shape.");

            var host = new GameObject("apa-editor-only-scan");
            try
            {
                var installer = host.AddComponent<AvatarPartInstaller>();
                var control = host.AddComponent<ScanControlBehaviour>();

                // An empty whitelist is the point: nothing is legal by name, so the only components missing from
                // the result are the ones the editor-only exclusion dropped.
                var reported = new List<Component>();
                var result = (System.Collections.IEnumerable)method.Invoke(
                    null, new object[] { host, new HashSet<Type>(), true });
                foreach (Component component in result)
                {
                    reported.Add(component);
                }

                Assert.IsTrue(
                    reported.Contains(control),
                    "The control component must be reported: it proves the scan ran and that an empty whitelist " +
                    "does not simply report nothing.");
                Assert.IsFalse(
                    reported.Contains(installer),
                    "The SDK's own illegal-component scan must exclude AvatarPartInstaller as editor-only; if it " +
                    "reports it, the VRChat SDK panel shows 'will be removed by the client' again.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // ---- What the fix must not change ------------------------------------------------------------

        [Test]
        public void InstallerObject_IsNeverTaggedEditorOnly()
        {
            var host = new GameObject("apa-editor-only-tag");
            try
            {
                host.AddComponent<AvatarPartInstaller>();

                Assert.IsFalse(
                    host.CompareTag("EditorOnly"),
                    "Tagging the object would silence the scan too, but it deletes the part hierarchy in NDMF's " +
                    "RemoveEditorOnlyPass and in the SDK's strip.");

                for (var current = host.transform; current != null; current = current.parent)
                {
                    Assert.IsFalse(
                        current.CompareTag("EditorOnly"),
                        "No ancestor of an installer may carry the EditorOnly tag.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }

            var source = InstallerSource;
            Assert.IsFalse(source.Contains(".tag ="), "The component must never write a GameObject tag.");
            Assert.IsFalse(source.Contains("gameObject.tag"), "The component must never write a GameObject tag.");
        }

        [Test]
        public void Installer_KeepsEverySerializedFieldItShippedWith()
        {
            var declared = Regex.Matches(
                    InstallerSource,
                    @"\[SerializeField\]\s+private\s+([A-Za-z0-9_.<>]+)\s+(_[A-Za-z0-9_]+)\s*[;=]")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value + " " + match.Groups[2].Value)
                .ToArray();

            CollectionAssert.AreEquivalent(
                s_serializedFields,
                declared,
                "The serialized field set is the component's on-disk format: adding, removing, renaming, or " +
                "retyping a field breaks every installer saved by an earlier package version.");
        }

        [Test]
        public void Installer_IsStillDiscoveredByTheBuildWalk()
        {
            var avatar = new GameObject("apa-editor-only-discovery");
            try
            {
                var host = new GameObject("Part");
                host.transform.SetParent(avatar.transform, false);
                var installer = host.AddComponent<AvatarPartInstaller>();

                CollectionAssert.Contains(
                    ContextBuilder.CollectAllInstallers(avatar),
                    installer,
                    "The observation walk must still see an editor-only installer.");
                CollectionAssert.Contains(
                    ContextBuilder.CollectInstallers(avatar),
                    installer,
                    "The build's active-installer walk must still see an editor-only installer; editor-only is " +
                    "about what reaches the client, not about what the pipeline processes.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(avatar);
            }
        }

        // ---- The assembly that makes the declaration possible ----------------------------------------

        [Test]
        public void RuntimeAssembly_ReferencesTheNdmfRuntimeAssemblyOnly()
        {
            var asmdef = RuntimeAsmdef;

            StringAssert.Contains(
                "nadena.dev.ndmf.runtime",
                asmdef,
                "INDMFEditorOnly lives in NDMF's runtime assembly, so the runtime asmdef must reference it.");

            Assert.IsFalse(
                asmdef.Contains("VRC.SDKBase"),
                "Editor-only status must come from NDMF's compatibility interface, not from a hard reference to " +
                "the VRChat SDK's assembly.");
            Assert.IsFalse(
                asmdef.Contains("UnityEditor"),
                "The runtime assembly must stay free of editor dependencies.");
            Assert.IsFalse(
                asmdef.Contains("dev.avatar-part-assembler.editor"),
                "The runtime assembly must not reference an editor assembly.");
        }

        [Test]
        public void InstallerSource_DeclaresTheCompatibilityInterfaceInsteadOfTheSdkType()
        {
            var source = InstallerSource;

            StringAssert.Contains("using nadena.dev.ndmf;", source);
            StringAssert.Contains(
                "public sealed class AvatarPartInstaller : MonoBehaviour, INDMFEditorOnly",
                source,
                "The interface must be declared on the component itself; a wrapper, a proxy, or a build-time " +
                "component swap would leave the scene avatar reported.");

            // The remarks explain the SDK type on purpose, so these are the *code* shapes a hard dependency would
            // take, and none of them can appear in prose.
            Assert.IsFalse(
                source.Contains("using VRC.SDKBase"),
                "The runtime component must reach editor-only status through NDMF's compatibility interface, not " +
                "by importing the VRChat SDK's namespace.");
            Assert.IsFalse(
                source.Contains(": VRC.SDKBase.IEditorOnly"),
                "Declaring the SDK interface directly would make this runtime assembly unbuildable in a project " +
                "without the SDK.");
            Assert.IsFalse(source.Contains("using UnityEditor"), "The runtime component must stay editor-free.");
            Assert.IsFalse(source.Contains("UnityEditor."), "The runtime component must stay editor-free.");
        }

        /// <summary>A type from the VRChat SDK, or from any loaded assembly, by full name; null when absent.</summary>
        /// <remarks>
        /// The test assembly deliberately references no SDK assembly — its asmdef turns precompiled references off
        /// and lists only NUnit — so the SDK's types are reached by name. The assembly-qualified probe is what
        /// makes the lookup independent of which test ran first: it loads <c>VRCSDKBase</c> by name when the SDK is
        /// installed. The AppDomain scan is the fallback for a type that lives somewhere else.
        /// </remarks>
        private static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName) ?? Type.GetType(fullName + ", VRCSDKBase");
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName);
                if (type != null) return type;
            }

            return null;
        }
    }
}

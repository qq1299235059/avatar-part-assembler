using System.Collections.Generic;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests the R1 invariant on a real hierarchy: a successful build clone carries no APA installer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assembly consumes an installer only when the part's geometry was assembled, so a parked installer, an
    /// installer whose part contributed nothing, and every installer of an avatar that was never assembled all
    /// survive it. <see cref="AvatarPartInstaller"/> is a plain runtime <c>MonoBehaviour</c>, which is why the
    /// VRChat client reports every survivor as a component it will remove.
    /// </para>
    /// <para>
    /// These tests exercise the removal itself — the one part of the feature that needs no NDMF build — through
    /// the same <see cref="ApaInstallerCleanup.RemoveAll"/> the pass calls, so "the pass removes them" and "the
    /// removal works" cannot be two different claims.
    /// </para>
    /// </remarks>
    public sealed class BuildInstallerCleanupTests
    {
        /// <summary>Objects created by a test, destroyed in reverse order in <see cref="TearDown"/>.</summary>
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

        /// <summary>A component used to prove that a registered boundary type stops the removal walk.</summary>
        /// <remarks>
        /// The production rule matches the VRChat descriptor by full type name through reflection, so the test can
        /// register its own stand-in instead of depending on the SDK being installed.
        /// </remarks>
        private sealed class TestAvatarBoundary : MonoBehaviour
        {
        }

        /// <summary>
        /// The ordinary case: an active installer, a parked one, and one on an inactive object are all removed,
        /// and the objects that carried them survive.
        /// </summary>
        [Test]
        public void RemoveAll_RemovesActiveAndParkedInstallersAndKeepsTheirObjects()
        {
            var avatar = NewGameObject("Avatar", null);
            var active = NewInstallerHost(avatar, "Active Part", enabledForBuild: true, activeInHierarchy: true);
            var parked = NewInstallerHost(avatar, "Parked Part", enabledForBuild: false, activeInHierarchy: true);
            var inactive = NewInstallerHost(avatar, "Inactive Part", enabledForBuild: true, activeInHierarchy: false);

            // The premise: discovery sees three installers, while the build would process only one. The two the
            // build never processes are exactly the ones that used to reach the client.
            Assert.AreEqual(3, ContextBuilder.CollectAllInstallers(avatar).Count);
            Assert.AreEqual(1, ContextBuilder.CollectInstallers(avatar).Count);

            var removed = ApaInstallerCleanup.RemoveAll(avatar);

            Assert.AreEqual(3, removed.Count, "Every installer under the root must be removed.");
            Assert.IsEmpty(
                avatar.GetComponentsInChildren<AvatarPartInstaller>(true),
                "A successful clone must carry no AvatarPartInstaller at any depth.");

            Assert.IsNotNull(active, "The removal must not destroy the object that carried the installer.");
            Assert.IsNotNull(parked);
            Assert.IsNotNull(inactive);
        }

        /// <summary>A nested avatar's installers belong to that avatar's build and are left alone.</summary>
        [Test]
        public void RemoveAll_StopsAtANestedAvatarBoundary()
        {
            ContextBuilder.RegisterAvatarBoundaryType(typeof(TestAvatarBoundary));

            var avatar = NewGameObject("Avatar", null);
            NewInstallerHost(avatar, "Outer Part", enabledForBuild: true, activeInHierarchy: true);

            var nested = NewGameObject("Nested Avatar", avatar.transform);
            nested.AddComponent<TestAvatarBoundary>();
            NewInstallerHost(nested, "Nested Part", enabledForBuild: true, activeInHierarchy: true);

            var removed = ApaInstallerCleanup.RemoveAll(avatar);

            Assert.AreEqual(1, removed.Count, "Only this avatar's installer may be removed.");
            Assert.IsEmpty(avatar.GetComponents<AvatarPartInstaller>());
            Assert.AreEqual(
                1,
                nested.GetComponentsInChildren<AvatarPartInstaller>(true).Length,
                "The nested avatar's installer belongs to that avatar's own build.");
        }

        /// <summary>A clone with nothing to remove, and a second call, are both no-ops.</summary>
        [Test]
        public void RemoveAll_IsIdempotentAndToleratesAnEmptyHierarchy()
        {
            Assert.IsEmpty(ApaInstallerCleanup.RemoveAll(null));

            var avatar = NewGameObject("Avatar", null);
            Assert.IsEmpty(ApaInstallerCleanup.RemoveAll(avatar));

            NewInstallerHost(avatar, "Part", enabledForBuild: true, activeInHierarchy: true);
            Assert.AreEqual(1, ApaInstallerCleanup.RemoveAll(avatar).Count);
            Assert.IsEmpty(ApaInstallerCleanup.RemoveAll(avatar));
        }

        /// <summary>Adds a host object carrying one installer, and returns the object.</summary>
        private GameObject NewInstallerHost(
            GameObject parent, string name, bool enabledForBuild, bool activeInHierarchy)
        {
            var host = NewGameObject(name, parent.transform);
            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.EnabledForBuild = enabledForBuild;

            if (!activeInHierarchy) host.SetActive(false);
            return host;
        }

        private GameObject NewGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            _created.Add(gameObject);
            return gameObject;
        }
    }
}

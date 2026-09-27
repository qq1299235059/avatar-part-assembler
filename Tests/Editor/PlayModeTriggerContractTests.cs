using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AvatarPartAssembler.Editor.Ndmf;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Pins the Play Mode trigger and processing path: which callback processes an avatar, when it runs, and why
    /// the assembly must find an installer there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression these tests exist for was not a wrong mesh — it was a build that never started. Ordinary
    /// Play Mode assembles through one of two paths: this package's <c>ApaPlayModeScenePrebuild</c>, which runs
    /// during scene processing (before any component is awakened), or NDMF's Apply On Play, which runs from
    /// <c>ApplyOnPlayGlobalActivator.Awake</c> at execution order -9995. Both execute before Unity has finished
    /// activating the scene, and discovery uses <see cref="AvatarPartInstaller.IsActiveForBuild"/>, so a
    /// predicate that reads Unity's activation bookkeeping answers "parked" for every installer in exactly that
    /// window: the assembly pass returns at its discovery gate without reporting anything, the parts are never
    /// merged, and Play Mode shows the authoring hierarchy.
    /// </para>
    /// <para>
    /// A unit test cannot construct a component that Unity has not awakened, so the predicate is pinned two ways:
    /// its three real states are driven on a live component, and the declaration is read from source to prove it
    /// asks <c>enabled &amp;&amp; gameObject.activeInHierarchy</c> rather than delegating to
    /// <c>isActiveAndEnabled</c>. The trigger is pinned the same way — <see cref="ApaPlayModePrebuildGate"/> is a
    /// pure function of five values, so the whole decision table is testable, and the callback that calls it is
    /// read from source to prove it reports every refusal instead of returning silently.
    /// </para>
    /// <para>
    /// The cleanup pass is pinned because it was the second half of the failure: on the Apply On Play path there
    /// is no clone, so the removal used to run against the author's live Play Mode object and delete the
    /// installers of an avatar that had just failed to assemble.
    /// </para>
    /// </remarks>
    public sealed class PlayModeTriggerContractTests
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

        private static string PrebuildSource => ReadSource("Editor", "NDMF", "ApaPlayModeCompatibility.cs");

        private static string CleanupPassSource => ReadSource("Editor", "NDMF", "ApaInstallerCleanupPass.cs");

        private static string PluginSource => ReadSource("Editor", "NDMF", "ApaNdmfPlugin.cs");

        /// <summary>Creates an active object carrying one installer and returns the component.</summary>
        private AvatarPartInstaller NewInstaller()
        {
            var host = new GameObject("Part");
            _created.Add(host);
            return host.AddComponent<AvatarPartInstaller>();
        }

        // ---- The activity predicate ------------------------------------------------------------------

        /// <summary>
        /// The predicate's positive case: an enabled installer on an active object is a build input.
        /// </summary>
        [Test]
        public void InstallerActivity_IsTrueForAnEnabledInstallerOnAnActiveObject()
        {
            var installer = NewInstaller();

            Assert.IsTrue(installer.isActiveAndEnabled, "Premise: an awakened component reports active.");
            Assert.IsTrue(
                installer.IsActiveForBuild,
                "An enabled installer on an active object must be a build input; discovery, planning and the " +
                "assembly all read this one predicate.");
            Assert.IsEmpty(installer.DescribeInactiveReason());
        }

        /// <summary>
        /// Each of the three parked states answers false, and each one names itself. In particular the skip
        /// diagnostic must never fall through to <c>reason=unknown-inactive-state</c> for a state the component
        /// can see: that token is what a Play Mode run logged while the assembly silently assembled nothing.
        /// </summary>
        [Test]
        public void InstallerActivity_IsFalseForEachParkedStateAndNamesIt()
        {
            var enabledForBuild = NewInstaller();
            enabledForBuild.EnabledForBuild = false;
            Assert.IsFalse(enabledForBuild.IsActiveForBuild);
            Assert.AreEqual("enabledForBuild=false", enabledForBuild.DescribeInactiveReason());

            var disabledComponent = NewInstaller();
            disabledComponent.enabled = false;
            Assert.IsFalse(disabledComponent.IsActiveForBuild);
            Assert.AreEqual("componentDisabled=true", disabledComponent.DescribeInactiveReason());

            var inactiveObject = NewInstaller();
            inactiveObject.gameObject.SetActive(false);
            Assert.IsFalse(inactiveObject.IsActiveForBuild);
            Assert.AreEqual("gameObjectInactive=true", inactiveObject.DescribeInactiveReason());
        }

        /// <summary>
        /// The declaration itself: the predicate must ask the component's own switch and the hierarchy, not
        /// Unity's <c>isActiveAndEnabled</c>, which is false for a component Unity has not awakened yet — the
        /// exact window both Play Mode entry points run in.
        /// </summary>
        [Test]
        public void InstallerActivity_DoesNotDependOnUnitysActivationBookkeeping()
        {
            var declaration = Regex.Match(
                InstallerSource,
                @"public\s+bool\s+IsActiveForBuild\s*=>\s*(?<expression>[^;]*);");
            Assert.IsTrue(
                declaration.Success,
                "AvatarPartInstaller.IsActiveForBuild must stay an expression-bodied predicate this contract can " +
                "read.");

            var expression = declaration.Groups["expression"].Value;
            StringAssert.DoesNotContain(
                "isActiveAndEnabled",
                expression,
                "isActiveAndEnabled is false for a component Unity has not awakened yet, so Play Mode discovery " +
                "would see every installer as parked and the assembly would return without assembling anything.");
            StringAssert.Contains("_enabledForBuild", expression);
            StringAssert.Contains("enabled", expression);
            StringAssert.Contains(
                "gameObject.activeInHierarchy",
                expression,
                "The hierarchy term must be written out: a part parked anywhere in a disabled subtree has to be " +
                "reported as parked rather than installed.");

            StringAssert.Contains(
                "reason=unknown-inactive-state",
                InstallerSource,
                "The skip diagnostic keeps its defensive default; the predicate above is what makes it " +
                "unreachable for a state the component can see.");
        }

        // ---- The prebuild trigger --------------------------------------------------------------------

        /// <summary>The whole decision table, including the two refusals that must stay silent.</summary>
        [Test]
        public void PrebuildGate_RunsOnlyWhenAPlayModeSceneHasAnActiveInstaller()
        {
            Assert.AreEqual(
                ApaPlayModePrebuildDecision.Run,
                ApaPlayModePrebuildGate.Decide(true, true, true, 2, 1));

            Assert.AreEqual(
                ApaPlayModePrebuildDecision.NotPlayModeTransition,
                ApaPlayModePrebuildGate.Decide(false, true, true, 2, 1),
                "A player build processes scenes too; the prebuild must not run there.");
            Assert.AreEqual(
                ApaPlayModePrebuildDecision.FeatureDisabled,
                ApaPlayModePrebuildGate.Decide(true, false, true, 2, 1));
            Assert.AreEqual(
                ApaPlayModePrebuildDecision.NoInstaller,
                ApaPlayModePrebuildGate.Decide(true, true, true, 0, 0),
                "A scene without an APA part must not produce a line per Play Mode entry.");
            Assert.AreEqual(
                ApaPlayModePrebuildDecision.ApplyOnPlayOff,
                ApaPlayModePrebuildGate.Decide(true, true, false, 2, 1));
            Assert.AreEqual(
                ApaPlayModePrebuildDecision.NoActiveInstaller,
                ApaPlayModePrebuildGate.Decide(true, true, true, 2, 0),
                "Installers that are all parked must not trigger a build.");

            Assert.IsTrue(ApaPlayModePrebuildGate.IsSilent(ApaPlayModePrebuildDecision.NotPlayModeTransition));
            Assert.IsTrue(ApaPlayModePrebuildGate.IsSilent(ApaPlayModePrebuildDecision.NoInstaller));
            Assert.IsFalse(
                ApaPlayModePrebuildGate.IsSilent(ApaPlayModePrebuildDecision.NoActiveInstaller),
                "A scene that has APA parts and will not be assembled early is exactly what must be reported.");
        }

        /// <summary>The reported reason is the first condition that applies, so the log names one remedy.</summary>
        [Test]
        public void PrebuildGate_ReportsTheFirstConditionThatApplies()
        {
            Assert.AreEqual(
                ApaPlayModePrebuildDecision.FeatureDisabled,
                ApaPlayModePrebuildGate.Decide(true, false, false, 5, 0),
                "The feature switch is the outermost gate and the one the user most likely changed.");

            Assert.AreEqual(
                ApaPlayModePrebuildDecision.NoInstaller,
                ApaPlayModePrebuildGate.Decide(true, true, false, 0, 0),
                "A scene with no APA part is not an Apply On Play problem.");

            Assert.AreEqual(
                ApaPlayModePrebuildDecision.ApplyOnPlayOff,
                ApaPlayModePrebuildGate.Decide(true, true, false, 5, 5),
                "With NDMF's Apply On Play off the fallback would not run either, so that is the condition to " +
                "report, not the root count.");
        }

        /// <summary>The tokens are the log vocabulary: stable, distinct, and never reused.</summary>
        [Test]
        public void PrebuildGate_TokensAreStableAndUnique()
        {
            var decisions = new[]
            {
                ApaPlayModePrebuildDecision.NotPlayModeTransition,
                ApaPlayModePrebuildDecision.FeatureDisabled,
                ApaPlayModePrebuildDecision.NoInstaller,
                ApaPlayModePrebuildDecision.ApplyOnPlayOff,
                ApaPlayModePrebuildDecision.NoActiveInstaller,
                ApaPlayModePrebuildDecision.Run
            };

            var tokens = new List<string>();
            for (var i = 0; i < decisions.Length; i++)
            {
                var token = ApaPlayModePrebuildGate.Describe(decisions[i]);
                StringAssert.StartsWith("reason=", token);
                CollectionAssert.DoesNotContain(tokens, token, "Two decisions must not share a reason token.");
                tokens.Add(token);
            }

            Assert.AreEqual("reason=not-a-play-mode-transition", ApaPlayModePrebuildGate.Describe(
                ApaPlayModePrebuildDecision.NotPlayModeTransition));
            Assert.AreEqual("reason=play-mode-compatibility-disabled", ApaPlayModePrebuildGate.Describe(
                ApaPlayModePrebuildDecision.FeatureDisabled));
            Assert.AreEqual("reason=no-avatar-part-installer-in-scene", ApaPlayModePrebuildGate.Describe(
                ApaPlayModePrebuildDecision.NoInstaller));
            Assert.AreEqual("reason=ndmf-apply-on-play-off", ApaPlayModePrebuildGate.Describe(
                ApaPlayModePrebuildDecision.ApplyOnPlayOff));
            Assert.AreEqual("reason=no-active-avatar-part-installer", ApaPlayModePrebuildGate.Describe(
                ApaPlayModePrebuildDecision.NoActiveInstaller));
            Assert.AreEqual("reason=play-mode-prebuild", ApaPlayModePrebuildGate.Describe(
                ApaPlayModePrebuildDecision.Run));
        }

        /// <summary>
        /// The callback: registered as a scene processor, ordered before the VRCFury play-mode processors, and
        /// driving NDMF directly so that Play Mode does not consume VRCFury's one allowed preprocess pass.
        /// </summary>
        [Test]
        public void Prebuild_RunsBeforeVrcFuryAndProcessesTheAvatarDirectly()
        {
            StringAssert.Contains("IProcessSceneWithReport", PrebuildSource);
            StringAssert.Contains(
                "public int callbackOrder => int.MinValue + 50;",
                PrebuildSource,
                "The prebuild must stay ahead of VRCFury's own Play Mode scene processor, which declares " +
                "int.MinValue + 100: whoever runs second finds the avatar already processed.");
            StringAssert.Contains(
                "int.MinValue + 100",
                PrebuildSource,
                "The ordering rationale names the VRCFury callback order it is racing, so a reader can check it.");
            StringAssert.Contains(
                "AvatarProcessor.ProcessAvatar(avatarRoot)",
                PrebuildSource,
                "NDMF must be driven directly from the scene callback; going through the VRChat preprocess chain " +
                "would spend VRCFury's one Play Mode preprocess pass before the scene is even awake.");
        }

        /// <summary>
        /// No silent refusals: the callback must decide through the gate and log the reason, so a Play Mode entry
        /// that assembles nothing says why instead of leaving an empty log.
        /// </summary>
        [Test]
        public void Prebuild_ReportsEveryRefusalInsteadOfReturningSilently()
        {
            StringAssert.Contains("ApaPlayModePrebuildGate.Decide(", PrebuildSource);
            StringAssert.Contains("ApaPlayModePrebuildGate.Describe(decision)", PrebuildSource);
            StringAssert.Contains(
                "Debug.LogWarning(",
                PrebuildSource,
                "A refused prebuild is a user-visible behaviour change and must be reported.");

            StringAssert.DoesNotContain(
                "if (!ApaPlayModeCompatibility.IsEnabled) return;",
                PrebuildSource,
                "The feature switch is a gate of the decision now; a bare return here is the silent refusal this " +
                "contract forbids.");
            StringAssert.DoesNotContain(
                "if (!Config.ApplyOnPlay) return;",
                PrebuildSource,
                "Apply On Play is a gate of the decision now; a bare return here is the silent refusal this " +
                "contract forbids.");
        }

        // ---- The cleanup pass ------------------------------------------------------------------------

        /// <summary>
        /// The removal is for the uploaded result, so it must not run while the object being processed is a Play
        /// Mode scene object, and it must keep its failed-build gate for the paths where it does run.
        /// </summary>
        [Test]
        public void InstallerCleanup_SkipsPlayModeTransitionsAndKeepsItsBuildGate()
        {
            StringAssert.Contains("EditorApplication.isPlayingOrWillChangePlaymode", CleanupPassSource);
            StringAssert.Contains("if (!context.Successful) return;", CleanupPassSource);
            StringAssert.Contains("ApaInstallerCleanup.RemoveAll(avatarRoot)", CleanupPassSource);

            var playModeGuard = CleanupPassSource.IndexOf(
                "if (EditorApplication.isPlayingOrWillChangePlaymode) return;", System.StringComparison.Ordinal);
            var successGate = CleanupPassSource.IndexOf(
                "if (!context.Successful) return;", System.StringComparison.Ordinal);

            Assert.GreaterOrEqual(playModeGuard, 0, "The Play Mode guard must be present in the pass body.");
            Assert.GreaterOrEqual(successGate, 0);
            Assert.Less(
                playModeGuard,
                successGate,
                "The Play Mode guard is the outermost condition: it says whether this object may be mutated at " +
                "all, and the report gate only says whether this build earned the mutation.");
        }

        /// <summary>The pass stays registered as the last Transforming step of a build.</summary>
        [Test]
        public void InstallerCleanup_IsStillRegisteredLastInTheTransformingSequence()
        {
            StringAssert.Contains(
                ".Then.Run(ApaInstallerCleanupPass.Instance)",
                PluginSource,
                "The cleanup must stay declared after the empty-source cleanup it depends on; removing the " +
                "registration would leave authoring components on uploaded avatars.");
        }
    }
}


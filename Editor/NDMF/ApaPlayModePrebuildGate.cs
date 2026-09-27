namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// The answer <see cref="ApaPlayModeScenePrebuild"/> reached for one Play Mode scene.
    /// </summary>
    /// <remarks>
    /// The prebuild used to answer this question with a chain of silent early returns, which is why a Play Mode
    /// entry that assembled nothing left no trace at all in the editor log: the reader could not tell "the
    /// callback never ran" apart from "the callback ran and every installer was parked". Each outcome now has a
    /// name and a stable token, and the pass logs the token whenever it declines to run.
    /// </remarks>
    public enum ApaPlayModePrebuildDecision
    {
        /// <summary>Scene processing during a player build, not a Play Mode entry. Silent by design.</summary>
        NotPlayModeTransition,

        /// <summary>The user turned the feature off from the Tools menu.</summary>
        FeatureDisabled,

        /// <summary>The scene carries no <c>AvatarPartInstaller</c> at all. Silent by design.</summary>
        NoInstaller,

        /// <summary>
        /// NDMF's own Apply On Play setting is off, so the forced value this package relies on is not in effect
        /// and NDMF would not process the avatar either.
        /// </summary>
        ApplyOnPlayOff,

        /// <summary>
        /// The scene carries installers, but every one of them is parked — disabled, on an inactive object, or
        /// with <c>EnabledForBuild</c> false — so there is no avatar this prebuild should process.
        /// </summary>
        NoActiveInstaller,

        /// <summary>At least one active installer belongs to an avatar root: the prebuild processes it.</summary>
        Run
    }

    /// <summary>
    /// The Play Mode prebuild's trigger decision, as a pure function of the facts it is allowed to look at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists so the trigger is testable without an editor.</b> The interesting part of the Play Mode path
    /// is not the NDMF call — that is one line — but the gate in front of it, and a gate that can only be
    /// exercised by entering Play Mode in a project with a real avatar is a gate that regresses unnoticed. The
    /// decision is therefore computed from five plain values, and the pass is a caller of it rather than a second
    /// copy of the rule.
    /// </para>
    /// <para>
    /// <b>The tokens are a contract.</b> <see cref="Describe"/> returns one stable <c>reason=…</c> token per
    /// outcome, the same vocabulary <c>ApaNdmfDiagnostics</c> uses, so a log line, a support request, and a test
    /// can all name the same condition. A token is never reused for a different outcome and never reworded.
    /// </para>
    /// <para>
    /// <b>Two outcomes are deliberately silent.</b> A player build processes scenes too, and a scene with no APA
    /// part is the common case in every project; logging either would put a line in the log for every build and
    /// every unrelated scene. Every outcome that means "this scene has APA parts and they will not be assembled
    /// early" is logged by the caller.
    /// </para>
    /// </remarks>
    public static class ApaPlayModePrebuildGate
    {
        /// <summary>Token for <see cref="ApaPlayModePrebuildDecision.NotPlayModeTransition"/>.</summary>
        public const string NotPlayModeTransitionToken = "reason=not-a-play-mode-transition";

        /// <summary>Token for <see cref="ApaPlayModePrebuildDecision.FeatureDisabled"/>.</summary>
        public const string FeatureDisabledToken = "reason=play-mode-compatibility-disabled";

        /// <summary>Token for <see cref="ApaPlayModePrebuildDecision.NoInstaller"/>.</summary>
        public const string NoInstallerToken = "reason=no-avatar-part-installer-in-scene";

        /// <summary>Token for <see cref="ApaPlayModePrebuildDecision.ApplyOnPlayOff"/>.</summary>
        public const string ApplyOnPlayOffToken = "reason=ndmf-apply-on-play-off";

        /// <summary>Token for <see cref="ApaPlayModePrebuildDecision.NoActiveInstaller"/>.</summary>
        public const string NoActiveInstallerToken = "reason=no-active-avatar-part-installer";

        /// <summary>Token for <see cref="ApaPlayModePrebuildDecision.Run"/>.</summary>
        public const string RunToken = "reason=play-mode-prebuild";

        /// <summary>
        /// Decides what the prebuild does for one scene.
        /// </summary>
        /// <param name="isPlayModeTransition">
        /// True while Unity is entering Play Mode. Unity processes scenes during a player build as well, and this
        /// package must not build anything there.
        /// </param>
        /// <param name="featureEnabled">The user's Tools-menu switch for the whole feature.</param>
        /// <param name="applyOnPlay">
        /// NDMF's Apply On Play value <i>after</i> <c>ApaPlayModeCompatibility</c> armed the session. It is the
        /// same value NDMF's own activator reads, so "off" here means the fallback is off too, and running the
        /// prebuild would make Play Mode depend on this package alone.
        /// </param>
        /// <param name="installerCount">Installers found anywhere in the scene, parked ones included.</param>
        /// <param name="avatarRootCount">
        /// Avatar roots reached from an installer that is active for build. Parked parts do not trigger a build,
        /// exactly as they contribute nothing to one.
        /// </param>
        /// <remarks>
        /// The order of the checks is part of the contract: the cheapest and most common refusal comes first, and
        /// the reported reason is the first condition that actually applies, so the log names the one thing the
        /// reader has to change.
        /// </remarks>
        public static ApaPlayModePrebuildDecision Decide(
            bool isPlayModeTransition,
            bool featureEnabled,
            bool applyOnPlay,
            int installerCount,
            int avatarRootCount)
        {
            if (!isPlayModeTransition) return ApaPlayModePrebuildDecision.NotPlayModeTransition;
            if (!featureEnabled) return ApaPlayModePrebuildDecision.FeatureDisabled;
            if (installerCount <= 0) return ApaPlayModePrebuildDecision.NoInstaller;
            if (!applyOnPlay) return ApaPlayModePrebuildDecision.ApplyOnPlayOff;
            if (avatarRootCount <= 0) return ApaPlayModePrebuildDecision.NoActiveInstaller;
            return ApaPlayModePrebuildDecision.Run;
        }

        /// <summary>The stable <c>reason=…</c> token for a decision.</summary>
        public static string Describe(ApaPlayModePrebuildDecision decision)
        {
            switch (decision)
            {
                case ApaPlayModePrebuildDecision.NotPlayModeTransition:
                    return NotPlayModeTransitionToken;
                case ApaPlayModePrebuildDecision.FeatureDisabled:
                    return FeatureDisabledToken;
                case ApaPlayModePrebuildDecision.NoInstaller:
                    return NoInstallerToken;
                case ApaPlayModePrebuildDecision.ApplyOnPlayOff:
                    return ApplyOnPlayOffToken;
                case ApaPlayModePrebuildDecision.NoActiveInstaller:
                    return NoActiveInstallerToken;
                default:
                    return RunToken;
            }
        }

        /// <summary>
        /// True for the two outcomes that must not write a line: a player build's scene processing, and a scene
        /// that carries no APA part at all.
        /// </summary>
        public static bool IsSilent(ApaPlayModePrebuildDecision decision)
        {
            return decision == ApaPlayModePrebuildDecision.NotPlayModeTransition
                   || decision == ApaPlayModePrebuildDecision.NoInstaller;
        }
    }
}


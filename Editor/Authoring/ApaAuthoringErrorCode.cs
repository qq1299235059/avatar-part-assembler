using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AvatarPartAssembler.Editor.Localization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The authoring layer's names for its diagnostic codes, and the title lookup for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Aliases, not a second allocation table.</b> The codes the authoring layer emits are allocated in
    /// <see cref="ApaErrorCode"/> and enumerated in <see cref="ApaReservedCodes.Milestone5Authoring"/>, so there
    /// is exactly one place a code is defined and no way for a second table to hand the same number to a second
    /// meaning. This type keeps the authoring-facing names because the window, the inspector, the writer, and the
    /// tests all read them; every constant here is the core constant.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>APA033</b> — an output path the author typed cannot name a Unity asset: it is empty, absolute, outside
    /// <c>Assets/</c>, contains a parent segment, or does not end with the extension the asset type requires.
    /// The core never sees a path, so no existing code covers it.
    /// </description></item>
    /// <item><description>
    /// <b>APA034</b> — a value that must be serialized into a reusable asset (a material reference in the
    /// profile, or an object reference in the part hierarchy that is about to become a prefab) resolves to a
    /// scene object. The value cannot survive outside the scene, so writing it would silently drop or corrupt
    /// the reference.
    /// </description></item>
    /// <item><description>
    /// <b>APA050</b> — a UV semantic declares a source channel the part mesh does not carry. The assembler
    /// writes a channel only when the source mesh has it (otherwise every vertex silently receives the channel
    /// default), so the declaration is refused rather than saved as data that does nothing.
    /// </description></item>
    /// <item><description>
    /// <b>APA041</b> — a black/white texture mask could not be converted into a removal triangle selection:
    /// the mesh, the mask texture, the UV channel, or the authored threshold is unusable, or the Editor's
    /// texture readback refused the mask. Both are authoring <i>inputs</i> the core never sees — the core
    /// validates the <c>RemovedTriangleAddress</c> set the conversion produces — which is why they are
    /// authoring-layer codes rather than assembly codes.
    /// </description></item>
    /// </list>
    /// <para>
    /// All of them are authored-data defects, not "unsupported mesh attribute" defects, which is why they are not
    /// reported as <c>APA014</c>. They are emitted by the authoring layer only; the build pipeline keeps
    /// emitting the codes the specification allocated to it.
    /// </para>
    /// </remarks>
    public static class ApaAuthoringErrorCode
    {
        /// <summary>An authoring output path cannot name a Unity asset. Alias of <see cref="ApaErrorCode.InvalidAuthoringPath"/>.</summary>
        public const string InvalidAuthoringPath = ApaErrorCode.InvalidAuthoringPath;

        /// <summary>
        /// A reference that must be serialized into a reusable asset resolves to a scene object. Alias of
        /// <see cref="ApaErrorCode.NonPersistentReference"/>.
        /// </summary>
        public const string NonPersistentReference = ApaErrorCode.NonPersistentReference;

        /// <summary>
        /// A UV semantic declares a source channel the part mesh does not carry, so the channel would be
        /// silently zero-filled instead of authored. Alias of
        /// <see cref="ApaErrorCode.UvSemanticChannelAbsent"/>.
        /// </summary>
        public const string UvSemanticChannelAbsent = ApaErrorCode.UvSemanticChannelAbsent;

        /// <summary>
        /// A black/white texture mask could not be converted into a removal triangle selection. Alias of
        /// <see cref="ApaErrorCode.RemovalMaskTextureFailed"/>.
        /// </summary>
        public const string RemovalMaskTextureFailed = ApaErrorCode.RemovalMaskTextureFailed;

        /// <summary>
        /// Returns the stable title of a code. Delegates to <see cref="ApaErrorCode.GetTitle"/>, which knows every
        /// code this package allocates; a code from a newer build returns an empty string rather than throwing.
        /// </summary>
        public static string GetTitle(string code)
        {
            return ApaErrorCode.GetTitle(code);
        }

        /// <summary>True when the code belongs to the authoring layer.</summary>
        public static bool IsAuthoringCode(string code)
        {
            return ApaReservedCodes.IsMilestone5AuthoringCode(code)
                   || ApaReservedCodes.IsMilestone9AuthoringCode(code);
        }
    }

    /// <summary>
    /// Renders diagnostics in one shape everywhere an author sees them: the authoring window, the installer
    /// inspector, and the console output of an action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rendered line begins with the stable code, because the code — not the prose — is the contract the
    /// specification and the tests rely on. The title is appended through
    /// <see cref="ApaAuthoringErrorCode.GetTitle"/> so that authoring-only codes are rendered with a title too.
    /// </para>
    /// <para>
    /// <b>What localization does and does not change here.</b> The severity word is localized; the code, the
    /// stable English mnemonic title, the issue message, and the <c>reason=</c>-carrying detail are not, because
    /// they are the tokens a search, a bug report, and a test assertion depend on. In Simplified Chinese a short
    /// localized description of the code is inserted beside the mnemonic, so the reader gets an explanation
    /// without losing the searchable token. A code this build does not describe renders exactly as it always
    /// did.
    /// </para>
    /// </remarks>
    public static class ApaDiagnosticText
    {
        /// <summary>
        /// Formats a single issue as <c>CODE TITLE: message :: detail</c>. A code with no known title is still
        /// rendered, because an unknown code must remain visible rather than being dropped.
        /// </summary>
        public static string Format(ValidationIssue issue)
        {
            if (issue == null) return string.Empty;

            var title = ApaAuthoringErrorCode.GetTitle(issue.Code);
            var code = string.IsNullOrEmpty(title)
                ? issue.Code
                : issue.Code + " " + title;

            var text = new StringBuilder();
            text.Append(ApaLocalization.SeverityLabel(issue.Severity)).Append(' ').Append(code);
            AppendCodeDescription(text, issue.Code);

            if (issue.SourceIndex >= 0)
            {
                text.Append(" [").Append(issue.SourceIndex.ToString(CultureInfo.InvariantCulture)).Append(']');
            }

            text.Append(": ").Append(issue.Message);

            if (!string.IsNullOrEmpty(issue.Detail))
            {
                text.Append(" :: ").Append(issue.Detail);
            }

            return text.ToString();
        }

        /// <summary>Formats an issue without its detail, for a space-constrained row.</summary>
        public static string FormatShort(ValidationIssue issue)
        {
            if (issue == null) return string.Empty;

            var title = ApaAuthoringErrorCode.GetTitle(issue.Code);
            var code = string.IsNullOrEmpty(title) ? issue.Code : issue.Code + " " + title;

            var text = new StringBuilder();
            text.Append(code);
            AppendCodeDescription(text, issue.Code);
            text.Append(": ").Append(issue.Message);
            return text.ToString();
        }

        /// <summary>
        /// Appends the localized description of a code in parentheses, when the resolved language has one.
        /// </summary>
        /// <remarks>
        /// Nothing is appended in English, which is what keeps the English diagnostic byte-identical to the text
        /// this package printed before it was localized — including the assertions the existing tests make.
        /// </remarks>
        private static void AppendCodeDescription(StringBuilder text, string code)
        {
            var description = ApaLocalization.ErrorCodeDescription(code);
            if (string.IsNullOrEmpty(description)) return;

            text.Append("（").Append(description).Append('）');
        }

        /// <summary>Formats every issue in order, one per line.</summary>
        public static string FormatAll(IReadOnlyList<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return string.Empty;

            var text = new StringBuilder();
            for (var i = 0; i < issues.Count; i++)
            {
                if (i > 0) text.AppendLine();
                text.Append(Format(issues[i]));
            }

            return text.ToString();
        }

        /// <summary>A one-line count summary, used as the concise status line.</summary>
        public static string Summarize(ValidationResult result)
        {
            if (result == null) return ApaLocalization.Tr("not validated");
            return ApaLocalization.TrFormat(
                "{0} error(s), {1} warning(s), {2} info",
                result.ErrorCount,
                result.WarningCount,
                result.InfoCount);
        }

        /// <summary>The display color of a severity, shared by every authoring surface.</summary>
        public static Color ColorOf(ApaSeverity severity)
        {
            switch (severity)
            {
                case ApaSeverity.Error: return new Color(1f, 0.42f, 0.42f);
                case ApaSeverity.Warning: return new Color(1f, 0.78f, 0.35f);
                default: return new Color(0.72f, 0.85f, 1f);
            }
        }
    }
}

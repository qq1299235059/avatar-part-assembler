using System;
using System.Collections.Generic;
using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Reports Avatar Part Assembler diagnostics through NDMF's error report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The core's <see cref="ValidationIssue"/> is deliberately independent of NDMF, so one adapter converts it
    /// into an <see cref="IError"/> and registers it with <see cref="ErrorReport.ReportError(IError)"/>. That is
    /// the mechanism NDMF exposes for plugin diagnostics: the error appears in the NDMF error report window,
    /// carries clickable object references, and an <see cref="ErrorSeverity.Error"/> entry makes
    /// <c>BuildContext.Successful</c> false, which is what blocks the VRChat upload
    /// (<c>BuildFrameworkPreprocessHook.BuildFrameworkOptimizeHook</c> returns <c>context.Successful</c>).
    /// </para>
    /// <para>
    /// Severity maps one-to-one, because both enums already carry the same three-way meaning:
    /// <see cref="ApaSeverity.Error"/> → <see cref="ErrorSeverity.Error"/> (blocks),
    /// <see cref="ApaSeverity.Warning"/> → <see cref="ErrorSeverity.NonFatal"/> (reported, does not block), and
    /// <see cref="ApaSeverity.Info"/> → <see cref="ErrorSeverity.Information"/>.
    /// </para>
    /// <para>
    /// Issues are sorted and deduplicated by <see cref="ValidationResult.Build"/> before they are reported, so
    /// the report window shows them in the same deterministic order the validator produced, and an issue that
    /// both validation and planning detected appears once.
    /// </para>
    /// <para>
    /// <b>Object references are resolved before anything is mutated.</b> A run consumes (destroys) the installer
    /// components of the parts it assembled, and NDMF resolves a reference by asking the object for its path, so
    /// resolving late would either throw on a destroyed component or silently point every per-part issue at the
    /// avatar root. <see cref="CaptureReferences"/> therefore runs first and the report path only reads the
    /// values it captured; nothing here ever hands a Unity object to the report.
    /// </para>
    /// </remarks>
    internal static class ApaNdmfDiagnostics
    {
        /// <summary>
        /// Resolves the object reference every reported issue will point at, while every object is still alive.
        /// </summary>
        /// <remarks>
        /// Must be called before the processor mutates or consumes anything. A caller with no registry (preview,
        /// or a processor call outside an NDMF build) gets <see cref="ApaDiagnosticReferences.Empty"/>: the
        /// diagnostics are still reported, only without a clickable object.
        /// </remarks>
        /// <param name="avatarRoot">The build clone's root, the fallback reference. May be null.</param>
        /// <param name="installers">The installers this run will process. May be null.</param>
        /// <param name="registry">NDMF's registry, or null when the caller has none.</param>
        internal static ApaDiagnosticReferences CaptureReferences(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> installers,
            IObjectRegistry registry)
        {
            if (registry == null) return ApaDiagnosticReferences.Empty;

            var avatarReference = avatarRoot != null ? registry.GetReference(avatarRoot) : null;
            var byPart = new Dictionary<string, ObjectReference>(StringComparer.Ordinal);

            if (installers != null)
            {
                for (var i = 0; i < installers.Count; i++)
                {
                    var installer = installers[i];
                    if (installer == null) continue;

                    // The same resolution the context builder used, so an NDMF report and the core report name a
                    // part identically (including a legacy profile's derived id).
                    var partId = ApaPartIdentityResolver.ResolvePartId(installer);
                    if (string.IsNullOrEmpty(partId)) continue;

                    // The first installer in canonical order wins, which is the same installer the core reports a
                    // duplicate identity against; a second one with the same id cannot be told apart by the id
                    // alone.
                    if (byPart.ContainsKey(partId)) continue;

                    byPart.Add(partId, registry.GetReference(installer.gameObject));
                }
            }

            return new ApaDiagnosticReferences(avatarReference, byPart);
        }

        /// <summary>
        /// Reports every issue, in deterministic order, against the build's error report.
        /// </summary>
        /// <param name="issues">The core's diagnostics. May be null or empty.</param>
        /// <param name="references">
        /// The references <see cref="CaptureReferences"/> resolved before mutation. May be null.
        /// </param>
        /// <remarks>
        /// Only pre-resolved references are read here, never a Unity object, so no diagnostic path can touch a
        /// component the run has already consumed.
        /// </remarks>
        internal static void Report(IReadOnlyList<ValidationIssue> issues, ApaDiagnosticReferences references)
        {
            if (issues == null || issues.Count == 0) return;

            var ordered = ValidationResult.Build(new List<ValidationIssue>(issues));

            for (var i = 0; i < ordered.Issues.Count; i++)
            {
                var issue = ordered.Issues[i];
                var reference = references != null ? references.For(issue) : null;
                ErrorReport.ReportError(new ApaNdmfDiagnostic(issue, reference));
            }
        }

        /// <summary>The NDMF severity that carries the same blocking meaning as the core's severity.</summary>
        internal static ErrorSeverity ToNdmfSeverity(ApaSeverity severity)
        {
            switch (severity)
            {
                case ApaSeverity.Warning: return ErrorSeverity.NonFatal;
                case ApaSeverity.Info: return ErrorSeverity.Information;
                default: return ErrorSeverity.Error;
            }
        }
    }

    /// <summary>
    /// The object references a run's diagnostics point at, resolved while every object they name was still alive.
    /// </summary>
    /// <remarks>
    /// The type exists so reporting cannot accidentally resolve a destroyed object: resolution happens once,
    /// before mutation, and the report path only looks up the values captured here.
    /// </remarks>
    public sealed class ApaDiagnosticReferences
    {
        private readonly Dictionary<string, ObjectReference> _byPart;

        /// <summary>No reference at all: every diagnostic is reported without a clickable object.</summary>
        public static readonly ApaDiagnosticReferences Empty = new ApaDiagnosticReferences(null, null);

        /// <summary>The avatar root's reference, used by every issue that carries no part identity.</summary>
        public ObjectReference AvatarRoot { get; }

        internal ApaDiagnosticReferences(ObjectReference avatarRoot, Dictionary<string, ObjectReference> byPart)
        {
            AvatarRoot = avatarRoot;
            _byPart = byPart;
        }

        /// <summary>The reference the given issue points at, or the avatar root's when it has none of its own.</summary>
        public ObjectReference For(ValidationIssue issue)
        {
            if (issue != null && !string.IsNullOrEmpty(issue.PartId) && _byPart != null
                && _byPart.TryGetValue(issue.PartId, out var partReference) && partReference != null)
            {
                return partReference;
            }

            return AvatarRoot;
        }
    }

    /// <summary>
    /// One Avatar Part Assembler diagnostic, presented by NDMF's standard error UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The title, details, and hint are formatted from the core's stable code and message rather than looked up
    /// from localization assets. That is a deliberate limit, not an omission: the diagnostics are a
    /// machine-checkable contract (a code plus a stable <c>reason=</c> detail token) that the validator, the
    /// tests, and this report all agree on, and the core already produces its message text. Translating the
    /// payload while the token it is keyed by stays English would be worse than not translating either.
    /// </para>
    /// <para>
    /// What the localization layer does add here is a one-line Simplified Chinese description of the known code,
    /// appended beside the unchanged code and English mnemonic. The token stays searchable in either language and
    /// the Chinese reader gets an explanation of it; a code this build does not describe renders exactly as it
    /// always did.
    /// </para>
    /// <para>
    /// Because every formatted string is overridden, the <see cref="Localizer"/> that
    /// <see cref="SimpleError"/> requires is never consulted. It is an empty localizer rather than a null one so
    /// the inherited UI (icon, title, details, object references, language-change handling) keeps working
    /// unchanged.
    /// </para>
    /// </remarks>
    internal sealed class ApaNdmfDiagnostic : SimpleError
    {
        private static readonly Localizer ApaLocalizer =
            new Localizer("en-US", () => new List<(string, Func<string, string>)>());

        private readonly string _code;
        private readonly string _title;
        private readonly string _partId;
        private readonly string _message;
        private readonly string _detail;
        private readonly ErrorSeverity _severity;

        /// <summary>Creates a diagnostic from a core issue.</summary>
        /// <param name="issue">The core diagnostic. Must not be null.</param>
        /// <param name="reference">An optional object reference the user can click to reach the culprit.</param>
        internal ApaNdmfDiagnostic(ValidationIssue issue, ObjectReference reference)
        {
            if (issue == null) throw new ArgumentNullException(nameof(issue));

            _code = issue.Code ?? string.Empty;
            _partId = issue.PartId ?? string.Empty;
            _message = issue.Message ?? string.Empty;
            _detail = issue.Detail ?? string.Empty;
            _severity = ApaNdmfDiagnostics.ToNdmfSeverity(issue.Severity);

            var symbol = ApaErrorCode.GetTitle(_code);
            var title = string.IsNullOrEmpty(symbol) ? _code : _code + " " + symbol;

            // The same localized description the authoring window and the preview overlay render, so one code
            // explains itself identically in every surface. Empty in English.
            var description = ApaLocalization.ErrorCodeDescription(_code);
            if (!string.IsNullOrEmpty(description)) title += "（" + description + "）";

            if (issue.SourceIndex >= 0) title += " [" + issue.SourceIndex + "]";
            if (!string.IsNullOrEmpty(_partId)) title += " (" + _partId + ")";
            _title = title;

            AddReference(reference);
        }

        /// <inheritdoc />
        public override Localizer Localizer => ApaLocalizer;

        /// <inheritdoc />
        public override string TitleKey => _code;

        /// <inheritdoc />
        public override ErrorSeverity Severity => _severity;

        /// <inheritdoc />
        public override string FormatTitle() => _title;

        /// <inheritdoc />
        public override string FormatDetails()
        {
            if (string.IsNullOrEmpty(_detail)) return _message;
            return _message + "\n" + _detail;
        }

        /// <inheritdoc />
        /// <remarks>No hint is produced: the message already states the defect and the detail names the reason.</remarks>
        public override string FormatHint() => null;
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// Identity of one previewed unit: the scene it lives in, the object identity of its avatar root, and — once
    /// an avatar has several target groups — the group's key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hierarchy path is not an identity. Two loaded scenes can each hold an <c>Avatar/Body</c> hierarchy, and
    /// the path then names two different avatars: their reports would overwrite each other and
    /// <see cref="ApaPreviewDiagnostics.Clear"/> for one would erase the other's diagnostics. The scene handle
    /// separates them, and the instance id separates two avatars inside one scene — including a nested avatar and
    /// the avatar that contains it, which share a path prefix but not a root.
    /// </para>
    /// <para>
    /// The group key is the third component because one avatar can legitimately have more than one target
    /// renderer, and each target group produces its own preview, its own diagnostics, and its own overlay entry.
    /// A group key is the target renderer's avatar-root-relative path, so it is unique inside an avatar and empty
    /// for the single-target case, which is why a key without a group keeps its M4 meaning.
    /// </para>
    /// <para>
    /// The path is display text only and is not part of equality, so renaming an avatar keeps its report (the
    /// report is replaced under the same key) instead of orphaning it under the old name.
    /// </para>
    /// </remarks>
    public readonly struct ApaPreviewDiagnosticKey
        : IEquatable<ApaPreviewDiagnosticKey>, IComparable<ApaPreviewDiagnosticKey>
    {
        /// <summary>The key of "no avatar": no scene and no object.</summary>
        public static readonly ApaPreviewDiagnosticKey None = default;

        /// <summary>Handle of the scene the avatar root lives in, or 0 when it is not in a loaded scene.</summary>
        public int SceneHandle { get; }

        /// <summary>Instance id of the avatar root.</summary>
        public int ObjectId { get; }

        /// <summary>
        /// Target group this report belongs to: the target renderer's avatar-root-relative path, or an empty
        /// string for the single-target case.
        /// </summary>
        public string GroupKey { get; }

        /// <summary>Avatar-root-relative hierarchy path, for display only; never part of equality.</summary>
        public string Path { get; }

        /// <summary>True when this key names an avatar.</summary>
        public bool IsValid => ObjectId != 0;

        internal ApaPreviewDiagnosticKey(int sceneHandle, int objectId, string path, string groupKey)
        {
            SceneHandle = sceneHandle;
            ObjectId = objectId;
            Path = path ?? string.Empty;
            GroupKey = groupKey ?? string.Empty;
        }

        /// <summary>Builds the key of an avatar root, or <see cref="None"/> when there is no root.</summary>
        public static ApaPreviewDiagnosticKey For(GameObject avatarRoot)
        {
            return For(avatarRoot, null);
        }

        /// <summary>
        /// Builds the key of one target group of an avatar, or <see cref="None"/> when there is no root.
        /// </summary>
        public static ApaPreviewDiagnosticKey For(GameObject avatarRoot, string groupKey)
        {
            if (avatarRoot == null) return None;

            var scene = avatarRoot.scene;
            return new ApaPreviewDiagnosticKey(
                scene.IsValid() ? scene.handle : 0,
                avatarRoot.GetInstanceID(),
                ApaPreviewDiscovery.DescribePath(avatarRoot.transform),
                groupKey);
        }

        /// <summary>True when two keys belong to the same avatar, whatever group they name.</summary>
        public bool SameAvatar(ApaPreviewDiagnosticKey other)
        {
            return SceneHandle == other.SceneHandle && ObjectId == other.ObjectId;
        }

        /// <inheritdoc />
        public bool Equals(ApaPreviewDiagnosticKey other)
        {
            return SceneHandle == other.SceneHandle
                   && ObjectId == other.ObjectId
                   && string.Equals(GroupKey, other.GroupKey, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is ApaPreviewDiagnosticKey other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (SceneHandle * 397) ^ ObjectId;
                var group = GroupKey;
                if (group != null)
                {
                    for (var i = 0; i < group.Length; i++) hash = (hash * 397) ^ group[i];
                }

                return hash;
            }
        }

        /// <summary>True when two keys name the same avatar and target group.</summary>
        public static bool operator ==(ApaPreviewDiagnosticKey a, ApaPreviewDiagnosticKey b)
        {
            return a.Equals(b);
        }

        /// <summary>True when two keys name different avatars or different target groups.</summary>
        public static bool operator !=(ApaPreviewDiagnosticKey a, ApaPreviewDiagnosticKey b)
        {
            return !a.Equals(b);
        }

        /// <summary>
        /// Deterministic total order: the scene first, then the object's identity, then the group key.
        /// </summary>
        /// <remarks>
        /// Both identity components are stable for as long as the objects live and neither depends on a hash code
        /// or on Unity's internal ordering, so two recomputations over an unchanged set of scenes produce the same
        /// order. The path is deliberately not a component of the order: it is not unique across scenes.
        /// </remarks>
        public int CompareTo(ApaPreviewDiagnosticKey other)
        {
            var scene = SceneHandle.CompareTo(other.SceneHandle);
            if (scene != 0) return scene;

            var identity = ObjectId.CompareTo(other.ObjectId);
            if (identity != 0) return identity;

            return string.CompareOrdinal(GroupKey ?? string.Empty, other.GroupKey ?? string.Empty);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            if (!IsValid) return Tr("(no avatar)");

            var name = string.IsNullOrEmpty(Path)
                ? TrFormat("scene {0} object {1}", SceneHandle, ObjectId)
                : Path;
            return string.IsNullOrEmpty(GroupKey) ? name : name + " -> " + GroupKey;
        }
    }

    /// <summary>
    /// One avatar's current preview diagnostic state.
    /// </summary>
    /// <remarks>
    /// A report is a snapshot, not a live view: the preview pipeline rebuilds its inputs on every invalidation,
    /// so a report describes the inputs of one discovery pass. It is immutable so that a Scene View repaint
    /// cannot observe a half-updated list.
    /// </remarks>
    public sealed class ApaPreviewDiagnosticReport
    {
        private readonly List<ValidationIssue> _issues;
        private readonly List<string> _notes;
        private readonly string _signature;

        /// <summary>Identity of the avatar this report belongs to (scene plus root object).</summary>
        public ApaPreviewDiagnosticKey Key { get; }

        /// <summary>The avatar root, or null when it has been destroyed.</summary>
        public GameObject AvatarRoot { get; }

        /// <summary>The resolved target body renderer, or null when no target could be resolved.</summary>
        public Renderer TargetRenderer { get; }

        /// <summary>Avatar-root-relative path of the target renderer, or an empty string.</summary>
        public string RendererPath { get; }

        /// <summary>Validation and planning issues, in the core's deterministic order.</summary>
        public IReadOnlyList<ValidationIssue> Issues => _issues;

        /// <summary>
        /// Preview-level notes that are not APA validation issues. An unsupported proxy renderer type is the
        /// canonical example: the asset is legal, but NDMF cannot preview it.
        /// </summary>
        public IReadOnlyList<string> Notes => _notes;

        /// <summary>True when the preview is blocked and no proxy is produced for this avatar.</summary>
        public bool Blocked { get; }

        /// <summary>Number of blocking errors.</summary>
        public int ErrorCount { get; }

        /// <summary>Number of warnings.</summary>
        public int WarningCount { get; }

        /// <summary>Number of informational issues.</summary>
        public int InfoCount { get; }

        /// <summary>True when the report carries something a user must read.</summary>
        public bool HasContent => ErrorCount > 0 || WarningCount > 0 || _notes.Count > 0;

        internal string Signature => _signature;

        internal ApaPreviewDiagnosticReport(
            ApaPreviewDiagnosticKey key,
            GameObject avatarRoot,
            Renderer targetRenderer,
            string rendererPath,
            List<ValidationIssue> issues,
            List<string> notes,
            bool blocked)
        {
            Key = key;
            AvatarRoot = avatarRoot;
            TargetRenderer = targetRenderer;
            RendererPath = rendererPath ?? string.Empty;
            _issues = issues ?? new List<ValidationIssue>();
            _notes = notes ?? new List<string>();
            Blocked = blocked;

            CountSeverities(_issues, out var errors, out var warnings, out var infos);
            ErrorCount = errors;
            WarningCount = warnings;
            InfoCount = infos;

            var signatureBuilder = new StringBuilder();
            signatureBuilder.Append(blocked ? "blocked|" : "ok|");
            for (var i = 0; i < _issues.Count; i++)
            {
                signatureBuilder.Append(_issues[i].Code).Append('|').Append(_issues[i].Severity).Append('|')
                    .Append(_issues[i].Message).Append('\n');
            }

            for (var i = 0; i < _notes.Count; i++) signatureBuilder.Append("note|").Append(_notes[i]).Append('\n');
            _signature = signatureBuilder.ToString();
        }

        private static void CountSeverities(List<ValidationIssue> issues, out int errors, out int warnings, out int infos)
        {
            errors = 0;
            warnings = 0;
            infos = 0;
            for (var i = 0; i < issues.Count; i++)
            {
                switch (issues[i].Severity)
                {
                    case ApaSeverity.Error:
                        errors++;
                        break;
                    case ApaSeverity.Warning:
                        warnings++;
                        break;
                    default:
                        infos++;
                        break;
                }
            }
        }

        /// <summary>One-line state, used as the Scene View overlay headline.</summary>
        public string Summary
        {
            get
            {
                var text = Blocked ? Tr("Preview blocked") : Tr("Preview active");
                if (ErrorCount > 0) text += TrFormat(" — {0} ERROR", ErrorCount);
                if (WarningCount > 0) text += TrFormat(" — {0} WARNING", WarningCount);
                if (InfoCount > 0) text += TrFormat(" — {0} INFO", InfoCount);
                return text;
            }
        }

        /// <summary>Formats the report as one line per issue, capped so a Scene View label stays readable.</summary>
        public string ToDisplayString(int maxIssues)
        {
            var builder = new StringBuilder();
            builder.Append(TrFormat("APA preview: {0}", Summary));
            if (!string.IsNullOrEmpty(RendererPath)) builder.Append(" [").Append(RendererPath).Append(']');

            var budget = maxIssues < 0 ? int.MaxValue : maxIssues;
            var written = 0;
            for (var i = 0; i < _issues.Count && written < budget; i++)
            {
                builder.Append('\n').Append(ApaPreviewDiagnostics.FormatIssue(_issues[i]));
                written++;
            }

            for (var i = 0; i < _notes.Count && written < budget; i++)
            {
                // A note describes what the preview layer did rather than what the asset is, so it is rendered
                // as localized UI copy with the technical text kept verbatim inside it.
                builder.Append(TrFormat("\nNOTE: {0}", _notes[i]));
                written++;
            }

            var remaining = _issues.Count + _notes.Count - written;
            if (remaining > 0) builder.Append(TrFormat("\n… {0} more", remaining));
            return builder.ToString();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return ToDisplayString(-1);
        }
    }

    /// <summary>
    /// The editor-facing diagnostic surface of the APA preview.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preview diagnostics have to satisfy one property that build diagnostics do not: they must disappear when
    /// the configuration becomes valid again. A stale "successful" preview or a stale error message is worse than
    /// no message, because the user cannot tell which one describes the current scene. This class therefore
    /// stores at most one report per avatar — keyed by the avatar's scene and object identity, not by its path —
    /// and replaces it on every discovery pass; a pass that finds the avatar unaffected calls <see cref="Clear"/>.
    /// </para>
    /// <para>
    /// Logging is deduplicated by content signature. The preview pipeline rebuilds on every relevant editor
    /// change, and logging the same message on each rebuild would flood the console and hide the change that
    /// actually matters. Internal failures are deduplicated the same way.
    /// </para>
    /// <para>
    /// Blocked previews log a warning rather than an error: the asset is rejected, but nothing has failed in the
    /// editor, and an error-level log would trip error-pause configurations for a state the user is expected to
    /// iterate on. Internal failures do log as errors, because they are bugs rather than asset defects.
    /// </para>
    /// </remarks>
    public static class ApaPreviewDiagnostics
    {
        /// <summary>Prefix of every console message this class emits.</summary>
        public const string LogPrefix = "[APA Preview] ";

        private static readonly Dictionary<ApaPreviewDiagnosticKey, ApaPreviewDiagnosticReport> s_reports =
            new Dictionary<ApaPreviewDiagnosticKey, ApaPreviewDiagnosticReport>();

        /// <summary>
        /// Messages already logged by <see cref="ReportInternalFailure"/>. A persistent internal failure would
        /// otherwise log an error on every pipeline rebuild, i.e. on every relevant editor change.
        /// </summary>
        private static readonly HashSet<string> s_loggedInternalFailures = new HashSet<string>(StringComparer.Ordinal);

        private static readonly List<ValidationIssue> s_emptyIssues = new List<ValidationIssue>();

        /// <summary>Number of avatars with a stored report.</summary>
        public static int Count => s_reports.Count;

        /// <summary>
        /// All current reports, ordered by avatar identity so that consumers see a deterministic sequence.
        /// </summary>
        /// <remarks>Orphaned reports (a destroyed avatar root) are removed before the snapshot is taken.</remarks>
        public static IReadOnlyList<ApaPreviewDiagnosticReport> Snapshot()
        {
            PruneOrphans();

            var keys = new List<ApaPreviewDiagnosticKey>(s_reports.Keys);
            keys.Sort();

            var result = new List<ApaPreviewDiagnosticReport>(keys.Count);
            for (var i = 0; i < keys.Count; i++) result.Add(s_reports[keys[i]]);
            return result;
        }

        /// <summary>
        /// Removes reports whose avatar root has been destroyed.
        /// </summary>
        /// <remarks>
        /// An avatar that is merely renamed or no longer affected is cleared or replaced by the pass that sees it.
        /// A <i>deleted</i> avatar is never seen again, so without this sweep its report would stay in the store
        /// and keep being returned by <see cref="Snapshot"/> for the rest of the session.
        /// </remarks>
        /// <returns>Number of orphaned reports removed.</returns>
        public static int PruneOrphans()
        {
            List<ApaPreviewDiagnosticKey> orphans = null;
            foreach (var pair in s_reports)
            {
                if (pair.Value != null && pair.Value.AvatarRoot != null) continue;

                if (orphans == null) orphans = new List<ApaPreviewDiagnosticKey>();
                orphans.Add(pair.Key);
            }

            if (orphans == null) return 0;

            for (var i = 0; i < orphans.Count; i++) s_reports.Remove(orphans[i]);
            return orphans.Count;
        }

        /// <summary>
        /// Builds a report. Issues are copied and re-sorted through <see cref="ValidationResult"/> so that the
        /// order matches the order the build report would use.
        /// </summary>
        public static ApaPreviewDiagnosticReport Build(
            ApaPreviewDiagnosticKey key,
            GameObject avatarRoot,
            Renderer targetRenderer,
            string rendererPath,
            ValidationResult issues,
            IReadOnlyList<string> notes,
            bool blocked)
        {
            var issueList = new List<ValidationIssue>();
            if (issues != null)
            {
                for (var i = 0; i < issues.Issues.Count; i++) issueList.Add(issues.Issues[i]);
            }

            var noteList = new List<string>();
            if (notes != null)
            {
                for (var i = 0; i < notes.Count; i++)
                {
                    if (!string.IsNullOrEmpty(notes[i])) noteList.Add(notes[i]);
                }
            }

            return new ApaPreviewDiagnosticReport(key, avatarRoot, targetRenderer, rendererPath, issueList, noteList, blocked);
        }

        /// <summary>
        /// Stores and logs the report of one discovery result, under the request's identity key.
        /// </summary>
        /// <remarks>
        /// Both the filter's discovery pass and a node that recaptures live inputs during refresh report through
        /// this method, so a report always describes the inputs the preview is currently showing.
        /// </remarks>
        public static ApaPreviewDiagnosticReport ReportRequest(ApaPreviewRequest request)
        {
            if (request == null) return null;

            return Report(Build(
                request.DiagnosticKey,
                request.AvatarRoot,
                request.TargetRenderer,
                request.TargetRendererPath,
                request.Issues,
                request.Notes,
                !request.IsRenderable));
        }

        /// <summary>
        /// Stores and logs the report of one discovery result with additional preview-level notes.
        /// </summary>
        /// <remarks>
        /// Used by the render filter for notes that only exist once a group is turned into a render group — an
        /// unsupported proxy renderer type, or a renderer another avatar's group already claimed. The discovery
        /// result itself is unchanged; the report describes everything the user has to know about this group.
        /// </remarks>
        public static ApaPreviewDiagnosticReport Report(ApaPreviewRequest request, IReadOnlyList<string> extraNotes)
        {
            if (request == null) return null;

            List<string> notes = null;
            if (extraNotes != null && extraNotes.Count > 0)
            {
                notes = new List<string>(request.Notes);
                for (var i = 0; i < extraNotes.Count; i++)
                {
                    if (!string.IsNullOrEmpty(extraNotes[i])) notes.Add(extraNotes[i]);
                }
            }

            return Report(Build(
                request.DiagnosticKey,
                request.AvatarRoot,
                request.TargetRenderer,
                request.TargetRendererPath,
                request.Issues,
                notes ?? request.Notes,
                !request.IsRenderable));
        }

        /// <summary>
        /// Stores a report and logs it when its content signature changed since the previous pass.
        /// </summary>
        /// <returns>The stored report.</returns>
        public static ApaPreviewDiagnosticReport Report(ApaPreviewDiagnosticReport report)
        {
            if (report == null) return null;

            ApaPreviewDiagnosticReport previous;
            var hadPrevious = s_reports.TryGetValue(report.Key, out previous);
            s_reports[report.Key] = report;

            if (!report.HasContent) return report;

            // Deduplication is per live avatar: a report left behind by a destroyed avatar must not silence the
            // first message of a new avatar that happens to have been given the same instance id.
            if (hadPrevious
                && ReferenceEquals(previous.AvatarRoot, report.AvatarRoot)
                && string.Equals(previous.Signature, report.Signature, StringComparison.Ordinal))
            {
                return report;
            }

            var message = report.ToDisplayString(16);
            if (report.Blocked)
            {
                Debug.LogWarning(LogPrefix + message);
            }
            else if (report.ErrorCount > 0)
            {
                Debug.LogError(LogPrefix + message);
            }
            else
            {
                Debug.LogWarning(LogPrefix + message);
            }

            return report;
        }

        /// <summary>Removes the report for an avatar, if any. Used when the avatar no longer needs a preview.</summary>
        public static void Clear(ApaPreviewDiagnosticKey key)
        {
            if (!key.IsValid) return;
            s_reports.Remove(key);
        }

        /// <summary>
        /// Removes every report of one avatar root, across all of its target groups.
        /// </summary>
        /// <remarks>
        /// The unit a discovery pass sees is an avatar, not a group: an avatar whose installers were all deleted
        /// or disabled has no group left to clear its own report, so the pass that observes "this root is no
        /// longer affected" has to sweep every group of it.
        /// </remarks>
        /// <returns>Number of reports removed.</returns>
        public static int ClearAvatar(GameObject avatarRoot)
        {
            if (avatarRoot == null) return 0;

            var key = ApaPreviewDiagnosticKey.For(avatarRoot);
            if (!key.IsValid)
            {
                // The root is not in a loaded scene, so its keys carry scene handle 0; match on the object id.
                return RemoveWhere(other => other.ObjectId == avatarRoot.GetInstanceID());
            }

            return RemoveWhere(other => other.SameAvatar(key));
        }

        private static int RemoveWhere(Func<ApaPreviewDiagnosticKey, bool> predicate)
        {
            List<ApaPreviewDiagnosticKey> matches = null;
            foreach (var key in s_reports.Keys)
            {
                if (!predicate(key)) continue;
                if (matches == null) matches = new List<ApaPreviewDiagnosticKey>();
                matches.Add(key);
            }

            if (matches == null) return 0;
            for (var i = 0; i < matches.Count; i++) s_reports.Remove(matches[i]);
            return matches.Count;
        }

        /// <summary>Removes every report. Used when the preview filter is disabled as a whole.</summary>
        public static void ClearAll()
        {
            s_reports.Clear();

            // A re-enabled preview must report a persistent failure again rather than stay silent because the
            // previous session already logged it.
            s_loggedInternalFailures.Clear();
        }

        /// <summary>Issues of the report for an avatar, or an empty list.</summary>
        public static IReadOnlyList<ValidationIssue> IssuesFor(ApaPreviewDiagnosticKey key)
        {
            ApaPreviewDiagnosticReport report;
            if (key.IsValid && s_reports.TryGetValue(key, out report)) return report.Issues;
            return s_emptyIssues;
        }

        /// <summary>Formats one issue the way the console and the overlay both show it.</summary>
        public static string FormatIssue(ValidationIssue issue)
        {
            if (issue == null) return string.Empty;

            var title = ApaErrorCode.GetTitle(issue.Code);
            var builder = new StringBuilder();
            builder.Append(issue.Code);
            if (!string.IsNullOrEmpty(title)) builder.Append(' ').Append(title);

            // The localized description the authoring renderer also uses, so the same code explains itself the
            // same way in the console, in the overlay, and in the authoring window. Empty in English.
            var description = ErrorCodeDescription(issue.Code);
            if (!string.IsNullOrEmpty(description)) builder.Append("（").Append(description).Append('）');

            builder.Append(": ").Append(issue.Message);

            if (!string.IsNullOrEmpty(issue.PartId)) builder.Append(" (part ").Append(issue.PartId).Append(')');
            if (!string.IsNullOrEmpty(issue.Detail)) builder.Append(" — ").Append(issue.Detail);
            return builder.ToString();
        }

        /// <summary>
        /// Formats an unexpected exception as a blocking report line. Preview code must never let an exception
        /// escape into the pipeline, because the pipeline would fault and stop previewing rather than report.
        /// </summary>
        public static string FormatException(string context, Exception exception)
        {
            if (exception == null) return context ?? string.Empty;
            return ApaErrorCode.InternalError + ": " + (context ?? Tr("preview")) + " threw " +
                   exception.GetType().Name + ": " + exception.Message;
        }

        /// <summary>
        /// Logs an internal preview failure at error level; always error level because it is a defect, not an
        /// asset. The message is logged once per distinct failure: the pipeline recomputes on every relevant
        /// editor change, and a persistent defect must not bury the change that caused it in repeated errors.
        /// </summary>
        public static void ReportInternalFailure(string context, Exception exception)
        {
            var message = FormatException(context, exception);
            if (!s_loggedInternalFailures.Add(message)) return;

            Debug.LogError(LogPrefix + message);
        }
    }
}

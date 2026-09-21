using System;
using System.Collections.Generic;
using System.Text;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// What an authoring write should do with the asset it was pointed at.
    /// </summary>
    /// <remarks>
    /// The decision is a value rather than a boolean because "the file exists and I may not overwrite it" is a
    /// distinct outcome from "the file exists and updating it is exactly what was asked for". Collapsing the two
    /// into <c>bool</c> is how a tool ends up overwriting silently.
    /// </remarks>
    public enum ApaAssetWriteAction
    {
        /// <summary>No asset exists at the path; create one.</summary>
        Create = 0,

        /// <summary>An asset exists and the caller explicitly allowed replacing its contents.</summary>
        Update = 1,

        /// <summary>An asset exists and the caller did not allow replacing it. Nothing may be written.</summary>
        RefuseOverwrite = 2
    }

    /// <summary>
    /// Pure path and overwrite policy for authoring output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Path validation lives here, and only here, so that the profile writer and the prefab generator cannot
    /// disagree about what a usable path is, and so that the decision can be tested without touching the asset
    /// database. Nothing in this type reads or writes an asset.
    /// </para>
    /// <para>
    /// A path is normalized before it is checked: separators are unified, duplicate separators and a trailing
    /// separator are removed, and the <c>Assets</c> segment is written with its canonical capitalization.
    /// Normalization never repairs a genuinely wrong path — an absolute path or a parent segment is reported,
    /// not rewritten — because a silently rewritten path writes an asset somewhere the author did not name.
    /// </para>
    /// </remarks>
    public static class ApaAuthoringAssetPaths
    {
        /// <summary>The asset database root segment.</summary>
        public const string AssetsSegment = "Assets";

        /// <summary>Extension of a serialized profile asset.</summary>
        public const string ProfileExtension = ".asset";

        /// <summary>Extension of a prefab asset.</summary>
        public const string PrefabExtension = ".prefab";

        /// <summary>Extension of a protected-mesh payload asset.</summary>
        public const string ProtectedMeshExtension = ".asset";

        /// <summary>
        /// File-name suffix of a protected-mesh payload, so the asset is recognizable beside its prefab.
        /// </summary>
        /// <remarks>
        /// The payload is derived from the prefab path rather than typed separately, and the suffix is what makes
        /// the pairing visible in the Project window: a creator assembling a delivery folder can see at a glance
        /// which payload belongs to which prefab.
        /// </remarks>
        public const string ProtectedMeshSuffix = "_ProtectedMesh";

        /// <summary>Default folder offered for newly authored parts.</summary>
        public const string DefaultOutputFolder = "Assets/AvatarPartAssembler";

        /// <summary>
        /// Unifies separators, removes duplicate and trailing separators, and canonicalizes the
        /// <c>Assets</c> prefix. Returns an empty string for null or whitespace input.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            var trimmed = path.Trim();
            if (trimmed.Length == 0) return string.Empty;

            var unified = trimmed.Replace('\\', '/');

            var parts = new List<string>();
            var segments = unified.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0) continue;
                parts.Add(segment);
            }

            if (parts.Count == 0) return string.Empty;

            // The project folder name is fixed and case-insensitive on the platforms Unity supports, so the
            // canonical spelling is restored rather than rejected. Every other segment keeps the author's case.
            if (string.Equals(parts[0], AssetsSegment, StringComparison.OrdinalIgnoreCase))
            {
                parts[0] = AssetsSegment;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0) builder.Append('/');
                builder.Append(parts[i]);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Validates that a path can name an asset of the expected kind, and returns its normalized form.
        /// </summary>
        /// <param name="path">The author-supplied path.</param>
        /// <param name="requiredExtension">The extension the asset type requires, for example <c>.asset</c>.</param>
        /// <param name="normalized">The normalized path on success; an empty string on failure.</param>
        /// <param name="reason">
        /// A stable token on failure: <c>empty-path</c>, <c>absolute-path</c>, <c>parent-segment</c>,
        /// <c>outside-assets-folder</c>, <c>missing-file-name</c>, <c>wrong-extension</c>, or
        /// <c>invalid-character</c>. A path whose last segment has no extension at all reports
        /// <c>wrong-extension</c>, because the remedy — end the path with the required extension — is the same.
        /// </param>
        /// <returns>True when the path can be used for an asset write.</returns>
        public static bool TryValidateAssetPath(
            string path,
            string requiredExtension,
            out string normalized,
            out string reason)
        {
            normalized = string.Empty;

            // The UNC form is detected on the raw input, before normalization collapses its leading empty
            // segments: "//server/share/x.asset" would otherwise be reported as merely outside the Assets folder,
            // which points the author at the wrong problem.
            var raw = (path ?? string.Empty).Trim().Replace('\\', '/');
            if (raw.StartsWith("//", StringComparison.Ordinal))
            {
                reason = "absolute-path";
                return false;
            }

            var candidate = Normalize(path);
            if (candidate.Length == 0)
            {
                reason = "empty-path";
                return false;
            }

            // A drive letter means the author pasted a filesystem path. It cannot be normalized into an asset
            // path without guessing which part of it is the project, so it is reported instead.
            if (candidate.Length > 1 && candidate[1] == ':')
            {
                reason = "absolute-path";
                return false;
            }

            if (HasParentSegment(candidate))
            {
                reason = "parent-segment";
                return false;
            }

            var prefix = AssetsSegment + "/";
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                // A path that is exactly the asset root names a folder, not a file. Reporting the more specific
                // reason matters because the remedies differ: one needs a file name, the other needs the project.
                reason = string.Equals(candidate, AssetsSegment, StringComparison.Ordinal)
                    ? "missing-file-name"
                    : "outside-assets-folder";
                return false;
            }

            var relative = candidate.Substring(prefix.Length);
            if (relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal))
            {
                reason = "missing-file-name";
                return false;
            }

            if (HasInvalidFileNameCharacter(relative))
            {
                reason = "invalid-character";
                return false;
            }

            if (!string.IsNullOrEmpty(requiredExtension)
                && !candidate.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                reason = "wrong-extension";
                return false;
            }

            normalized = candidate;
            reason = string.Empty;
            return true;
        }

        /// <summary>Builds the diagnostic message for a rejected path, carrying the stable reason token.</summary>
        public static ValidationIssue InvalidPathIssue(string path, string extension, string reason, string what)
        {
            return ValidationIssue.Error(
                ApaAuthoringErrorCode.InvalidAuthoringPath,
                ApaIssuePhase.Configuration,
                "The " + what + " path '" + (path ?? string.Empty) + "' cannot name a Unity asset. " +
                DescribePathReason(reason, extension) + " Use a project-relative path such as '" +
                DefaultOutputFolder + "/MyPart" + (extension ?? string.Empty) + "'.",
                detail: "path=" + (path ?? string.Empty) + "; reason=" + reason);
        }

        /// <summary>A human-readable sentence for a path rejection token.</summary>
        public static string DescribePathReason(string reason, string extension)
        {
            switch (reason)
            {
                case "empty-path":
                    return Localization.ApaLocalization.Tr("The path is empty.");
                case "absolute-path":
                    return Localization.ApaLocalization.Tr(
                        "It is an absolute filesystem path rather than a project-relative asset path.");
                case "parent-segment":
                    return Localization.ApaLocalization.Tr(
                        "It contains a '..' segment, which could resolve outside the project.");
                case "outside-assets-folder":
                    return Localization.ApaLocalization.Tr("It does not start with 'Assets/'.");
                case "missing-file-name":
                    return Localization.ApaLocalization.Tr("It names a folder rather than a file.");
                case "invalid-character":
                    return Localization.ApaLocalization.Tr(
                        "It contains a character that is not allowed in an asset file name.");
                case "wrong-extension":
                    return Localization.ApaLocalization.TrFormat(
                        "It does not end with '{0}'.", extension ?? string.Empty);
                default:
                    return Localization.ApaLocalization.Tr("It is not a usable asset path.");
            }
        }

        /// <summary>True when any path segment is a parent (<c>..</c>) segment.</summary>
        public static bool HasParentSegment(string normalizedPath)
        {
            var segments = normalizedPath.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "..", StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>True when any file-name segment contains a character Unity forbids in an asset name.</summary>
        public static bool HasInvalidFileNameCharacter(string relativePath)
        {
            var segments = relativePath.Split('/');
            // Unity rejects the platform path separators and the Windows-reserved set. A colon is included
            // because it makes the segment an alternate data stream or a drive-relative path on Windows.
            var invalid = new[] { ':', '*', '?', '"', '<', '>', '|' };
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0) return true;
                for (var c = 0; c < invalid.Length; c++)
                {
                    if (segment.IndexOf(invalid[c]) >= 0) return true;
                }

                if (segment[segment.Length - 1] == '.') return true;
            }

            return false;
        }

        /// <summary>
        /// The overwrite decision. <paramref name="exists"/> is the only fact about the file system this type
        /// needs, which is what keeps the decision testable.
        /// </summary>
        public static ApaAssetWriteAction DecideWriteAction(bool exists, bool allowOverwrite)
        {
            if (!exists) return ApaAssetWriteAction.Create;
            return allowOverwrite ? ApaAssetWriteAction.Update : ApaAssetWriteAction.RefuseOverwrite;
        }

        /// <summary>Joins a folder and a file name into a normalized asset path.</summary>
        public static string Combine(string folder, string fileName)
        {
            var left = Normalize(folder);
            var right = (fileName ?? string.Empty).Trim().Trim('/');
            if (left.Length == 0) return right;
            if (right.Length == 0) return left;
            return left + "/" + right;
        }

        /// <summary>
        /// Turns a display name into a file name that Unity accepts, without touching a name that is already
        /// valid. An empty result is replaced by <c>AvatarPart</c> because a nameless asset is not usable.
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "AvatarPart";

            var builder = new StringBuilder(name.Length);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') builder.Append(c);
                else if (c == ' ' || c == '/' || c == '\\') builder.Append('_');
            }

            var result = builder.ToString().Trim('_', '.', ' ');
            return result.Length == 0 ? "AvatarPart" : result;
        }

        /// <summary>Default profile asset path for a part name.</summary>
        public static string DefaultProfilePath(string folder, string partName)
        {
            return Combine(folder, SanitizeFileName(partName) + "Profile" + ProfileExtension);
        }

        /// <summary>Default prefab asset path for a part name.</summary>
        public static string DefaultPrefabPath(string folder, string partName)
        {
            return Combine(folder, SanitizeFileName(partName) + PrefabExtension);
        }

        /// <summary>Ordinal, case-insensitive equality over normalized asset paths.</summary>
        public static bool PathsEqual(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when <paramref name="path"/> is the folder itself or an asset inside it.</summary>
        public static bool IsUnderFolder(string path, string folder)
        {
            var candidate = Normalize(path);
            var root = Normalize(folder);
            if (candidate.Length == 0 || root.Length == 0) return false;
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
            return candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Every folder segment of an asset path, excluding the file name.</summary>
        public static string[] FolderSegments(string assetPath)
        {
            var normalized = Normalize(assetPath);
            if (normalized.Length == 0) return Array.Empty<string>();

            var segments = normalized.Split('/');
            if (segments.Length <= 1) return Array.Empty<string>();

            var folders = new string[segments.Length - 1];
            Array.Copy(segments, folders, folders.Length);
            return folders;
        }
    }
}

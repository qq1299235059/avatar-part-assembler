using System;
using UnityEditor.PackageManager;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// How a profile's recorded package version compares with the installed Avatar Part Assembler package.
    /// </summary>
    /// <remarks>
    /// The verdict is the whole decision the installer's version line makes, kept as a value so it can be checked
    /// without a window: only <see cref="Matches"/> is silent, and everything else states its own reason.
    /// </remarks>
    public enum ApaPackageVersionVerdict
    {
        /// <summary>The profile records exactly the version that is installed. Nothing to say, nothing to draw.</summary>
        Matches = 0,

        /// <summary>The profile records no version at all: it was written before the stamp existed.</summary>
        NotRecorded = 1,

        /// <summary>The profile records a version, and it is not the installed one.</summary>
        Mismatch = 2,

        /// <summary>The installed version could not be read, so no comparison is possible.</summary>
        Unverifiable = 3
    }

    /// <summary>
    /// The installed Avatar Part Assembler package version, read from the Unity Package Manager manifest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The manifest is the source of truth, not a constant.</b> A version literal compiled into the package
    /// would have to be edited in two places on every release and would report a stale version the moment the two
    /// disagreed; the resolved package manifest is what the Package Manager itself believes is installed, which is
    /// exactly what the profile's recorded version has to be compared against.
    /// </para>
    /// <para>
    /// <b>Unknown is a real answer.</b> When the package cannot be resolved (the manifest is unavailable, or the
    /// Package Manager has not finished resolving), <see cref="Current"/> is an empty string and
    /// <see cref="Compare"/> answers <see cref="ApaPackageVersionVerdict.Unverifiable"/> rather than guessing. A
    /// caller must state that reason instead of substituting a schema number or treating the profile as broken.
    /// </para>
    /// <para>
    /// The value is resolved once per domain load and cached: a package version can only change through a package
    /// operation, which reloads the domain, so a repaint never pays for the lookup twice.
    /// </para>
    /// </remarks>
    public static class ApaPackageVersion
    {
        /// <summary>The package this editor code belongs to.</summary>
        public const string PackageName = "dev.avatar-part-assembler";

        /// <summary>The package's manifest inside the project, as the Package Manager addresses it.</summary>
        public const string ManifestAssetPath = "Packages/dev.avatar-part-assembler/package.json";

        private static bool s_resolved;
        private static string s_current = string.Empty;

        /// <summary>
        /// The installed package version, or an empty string when it cannot be read.
        /// </summary>
        public static string Current
        {
            get
            {
                if (!s_resolved)
                {
                    s_current = ResolveCurrent();
                    s_resolved = true;
                }

                return s_current;
            }
        }

        /// <summary>True when the installed package version is known.</summary>
        public static bool IsKnown => !string.IsNullOrEmpty(Current);

        /// <summary>
        /// Compares the version a profile records with the installed one.
        /// </summary>
        /// <remarks>
        /// The comparison is ordinal and exact. A version string is an identifier, not a number to order: two
        /// versions are the same version when they are the same text, and anything else is a difference the user
        /// has to be told about rather than a range to reason over.
        /// </remarks>
        /// <param name="recorded">The version stored on the profile. Empty means "not recorded".</param>
        /// <param name="current">The installed version. Empty means "could not be read".</param>
        public static ApaPackageVersionVerdict Compare(string recorded, string current)
        {
            // An unreadable installed version is decided first: without it there is nothing to compare, whatever
            // the profile carries, and reporting a mismatch against an unknown value would be a fabrication.
            if (string.IsNullOrEmpty(current)) return ApaPackageVersionVerdict.Unverifiable;
            if (string.IsNullOrEmpty(recorded)) return ApaPackageVersionVerdict.NotRecorded;

            return string.Equals(recorded, current, StringComparison.Ordinal)
                ? ApaPackageVersionVerdict.Matches
                : ApaPackageVersionVerdict.Mismatch;
        }

        /// <summary>Reads the installed version from the resolved package manifest.</summary>
        /// <remarks>
        /// The direct manifest lookup is the normal path; the registered-package scan is the fallback for a
        /// Package Manager state in which the manifest path is not addressable. Both are wrapped, because a
        /// package lookup must never take an Inspector down: an unreadable version degrades to "unverifiable",
        /// which is a stated answer.
        /// </remarks>
        private static string ResolveCurrent()
        {
            try
            {
                var info = PackageInfo.FindForAssetPath(ManifestAssetPath);
                if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
            }
            catch (Exception)
            {
                // Falls through to the scan below; an unavailable Package Manager is not an error to report here.
            }

            try
            {
                var packages = PackageInfo.GetAllRegisteredPackages();
                for (var i = 0; i < packages.Length; i++)
                {
                    var package = packages[i];
                    if (package != null
                        && string.Equals(package.name, PackageName, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(package.version))
                    {
                        return package.version;
                    }
                }
            }
            catch (Exception)
            {
                // Falls through to "unknown".
            }

            return string.Empty;
        }
    }
}

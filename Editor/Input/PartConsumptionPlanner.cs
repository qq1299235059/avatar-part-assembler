using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Which live objects one group's assembly consumes, decided from the plan and the group's installer set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan is the authority on which parts contributed geometry: a part's identity in the plan is its
    /// avatar-root-relative installer path, so a caller never re-derives "which parts fed this mesh" from
    /// hierarchy order or from a profile field.
    /// </para>
    /// <para>
    /// The set is a value, not a mutation: nothing here destroys anything. The build processor applies it after a
    /// successful assignment, and the preview uses the same set to hide the source geometry it stands in for, so
    /// "what the build removes" and "what the preview hides" cannot drift apart.
    /// </para>
    /// </remarks>
    public sealed class ApaPartConsumptionPlan
    {
        private static readonly Renderer[] s_noRenderers = new Renderer[0];
        private static readonly AvatarPartInstaller[] s_noInstallers = new AvatarPartInstaller[0];

        /// <summary>The part renderers whose geometry the generated mesh already contains, in discovery order.</summary>
        public IReadOnlyList<Renderer> Renderers { get; }

        /// <summary>The installers whose parts contributed geometry, in the order they were processed.</summary>
        public IReadOnlyList<AvatarPartInstaller> Installers { get; }

        /// <summary>True when this group consumed nothing.</summary>
        public bool IsEmpty => Renderers.Count == 0 && Installers.Count == 0;

        /// <summary>Creates a plan.</summary>
        public ApaPartConsumptionPlan(
            IReadOnlyList<Renderer> renderers,
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            Renderers = renderers ?? s_noRenderers;
            Installers = installers ?? s_noInstallers;
        }

        /// <summary>Nothing to consume.</summary>
        public static readonly ApaPartConsumptionPlan Empty =
            new ApaPartConsumptionPlan(s_noRenderers, s_noInstallers);
    }

    /// <summary>
    /// Resolves the part renderers and installers one assembly consumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One implementation, two callers.</b> The NDMF build pass and the M4 preview must agree on which source
    /// geometry an assembly replaces: the build destroys those renderers, and the preview hides them, and a
    /// disagreement is exactly the "preview does not match the build" failure the product contract forbids. The
    /// decision therefore lives here, in the assembly both of them reference, rather than in the NDMF processor
    /// (which preview cannot reference without an assembly cycle) or in a second copy inside the preview filter.
    /// </para>
    /// <para>
    /// <b>Scope is one target group, closure is the whole run.</b> The plan passed in is a group's plan and
    /// <paramref name="processedInstallers"/> is the full set of installers this run processes (not only the
    /// group's). The parked-renderer test asks "does another installer this build does not process own this
    /// renderer?", which is only answerable against the whole run; and <b>no group may consume another group's
    /// target renderer</b>, which is why the caller also passes every target renderer of the run. A group whose
    /// part root contained another group's target would otherwise destroy that renderer when the run consumes
    /// the part, silently removing the other group's geometry while still reporting success.
    /// </para>
    /// </remarks>
    public static class ApaPartConsumptionPlanner
    {
        /// <summary>
        /// The renderers and installers one group's generated mesh replaces.
        /// </summary>
        /// <param name="avatarRoot">The avatar root the paths are relative to.</param>
        /// <param name="context">The group's captured context. Its parts decide which installers contributed.</param>
        /// <param name="processedInstallers">Every installer this run processes, in canonical order.</param>
        /// <param name="targetRenderers">
        /// Every target renderer of this run, including this group's own. None of them is ever consumed: a target
        /// belongs to its own group, and a group that consumed another group's target would delete that group's
        /// geometry after the run has already reported success. Required — a caller that passes null (or only its
        /// own target) reopens exactly that defect.
        /// </param>
        /// <param name="issues">
        /// Receives one Info per additional renderer under a consumed part root and one Warning per renderer that
        /// belongs to an installer this run does not process. May be null.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>The assembled renderer is identified, not guessed.</b> Which renderer under a part root the core
        /// read geometry from is recorded when the capture happens
        /// (<see cref="PartSnapshot.SourceRenderer"/>), because the capture expression and an enumeration do not
        /// agree on their first element. Every renderer under the part root that is <i>not</i> that one belongs to
        /// the part whose geometry was consumed as a whole: it can only draw geometry that is already inside the
        /// assembled mesh, and leaving one behind would draw that geometry twice with nothing reported. All of
        /// them are therefore consumed too (component only, so bones, children, and authoring assets survive), and
        /// each additional one is reported as an Info so the removal is never silent.
        /// </para>
        /// <para>
        /// Two exceptions. A renderer that another installer owns while this build does not process that installer
        /// (a parked part — <c>IsActiveForBuild</c> false, or filtered out by the caller) is <i>not</i> consumed,
        /// because its geometry was never assembled and removing it would be a silent loss; it is reported as a
        /// Warning instead. And a target renderer of <i>any</i> group is never consumed, because it is not part
        /// geometry at all; the configurations where one group's part root contains another group's target are
        /// refused as a blocking conflict rather than resolved here, see
        /// <see cref="CollectTargetsInsideForeignPartRoots"/>.
        /// </para>
        /// </remarks>
        public static ApaPartConsumptionPlan ForGroup(
            GameObject avatarRoot,
            ValidationContext context,
            IReadOnlyList<AvatarPartInstaller> processedInstallers,
            IReadOnlyList<Renderer> targetRenderers,
            List<ValidationIssue> issues)
        {
            if (avatarRoot == null || context == null || context.Parts.Count == 0)
            {
                return ApaPartConsumptionPlan.Empty;
            }

            var contributing = new HashSet<string>(StringComparer.Ordinal);

            // The installer path is the part's identity in the plan, and the renderer the capture read from is
            // recorded beside it, so the loop below never has to re-derive "which renderer is the part's".
            var capturedRenderers = new Dictionary<string, Renderer>(StringComparer.Ordinal);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                var path = part.OrderingKey.InstallerPath ?? string.Empty;
                if (!contributing.Add(path)) continue;

                capturedRenderers[path] = part.SourceRenderer;
            }

            var consumedRenderers = new List<Renderer>();
            var consumedInstallers = new List<AvatarPartInstaller>();

            if (processedInstallers == null) return ApaPartConsumptionPlan.Empty;

            for (var i = 0; i < processedInstallers.Count; i++)
            {
                var installer = processedInstallers[i];
                if (installer == null) continue;

                var installerPath = MeshSnapshotFactory.RelativePath(avatarRoot.transform, installer.transform);
                if (!contributing.Contains(installerPath ?? string.Empty)) continue;

                var partRoot = installer.ResolvePartRoot();
                if (partRoot == null) continue;

                // The same resolution the context builder used, so a report names the part the way the plan does.
                var partId = ApaPartIdentityResolver.ResolvePartId(installer);
                var parkedRenderers = CollectParkedPartRenderers(partRoot, installer, processedInstallers);
                var partRenderers = partRoot.GetComponentsInChildren<Renderer>(true);

                // The reference the capture recorded. A snapshot built without a live renderer (a pure-core
                // fixture) falls back to the capture's own expression, which is the renderer the core would have
                // read from in this hierarchy — never "the first enumerated element".
                Renderer capturedRenderer;
                if (!capturedRenderers.TryGetValue(installerPath ?? string.Empty, out capturedRenderer)
                    || capturedRenderer == null)
                {
                    capturedRenderer = partRoot.GetComponentInChildren<Renderer>(true);
                }

                for (var j = 0; j < partRenderers.Length; j++)
                {
                    var renderer = partRenderers[j];
                    if (renderer == null) continue;
                    if (IsTargetRenderer(targetRenderers, renderer)) continue;
                    if (consumedRenderers.Contains(renderer)) continue;

                    if (parkedRenderers.Contains(renderer))
                    {
                        if (issues != null)
                        {
                            issues.Add(ValidationIssue.Warning(
                                ApaErrorCode.TargetRendererNotFound,
                                ApaIssuePhase.Compatibility,
                                "Renderer '" + renderer.name + "' lives under the consumed part root '" +
                                partRoot.name + "' but belongs to another installer this build does not process, " +
                                "so it was left in place: its geometry is not part of the assembled mesh, and " +
                                "removing it would lose it. Enable that installer, or move the renderer out of the " +
                                "consumed part root.",
                                partId,
                                detail: "reason=parked-part-renderer; renderer=" + renderer.name));
                        }

                        continue;
                    }

                    // Any other renderer under the consumed part root belongs to the part as a whole; only the
                    // capture's own renderer is the part's assembled mesh, so every other one is called out.
                    if (renderer != capturedRenderer && issues != null)
                    {
                        issues.Add(ValidationIssue.Info(
                            ApaErrorCode.TargetRendererNotFound,
                            ApaIssuePhase.Compatibility,
                            "Renderer '" + renderer.name + "' also lives under the consumed part root '" +
                            partRoot.name + "'. It is not the renderer the part's geometry was assembled from, " +
                            "but it belongs to the consumed part root, so it is removed with the part to keep " +
                            "that geometry from being drawn twice.",
                            partId,
                            detail: "reason=additional-part-renderer; renderer=" + renderer.name));
                    }

                    consumedRenderers.Add(renderer);
                }

                if (!consumedInstallers.Contains(installer)) consumedInstallers.Add(installer);
            }

            return new ApaPartConsumptionPlan(consumedRenderers, consumedInstallers);
        }

        /// <summary>
        /// Reports every target renderer that lives inside another target group's consumed part root.
        /// </summary>
        /// <param name="avatarRoot">The avatar root the reported path is relative to. May be null.</param>
        /// <param name="groups">Every planned target group of the run, in group-key order.</param>
        /// <param name="issues">Receives one blocking issue per conflicting pair of groups.</param>
        /// <remarks>
        /// <para>
        /// A group's consumption set is every renderer under its parts' roots (minus the exclusions
        /// <see cref="ForGroup"/> applies). A target renderer of another group is not part geometry: it is the
        /// body or clothing mesh that group assembles <i>into</i>, so consuming it would delete that group's
        /// target after the run reported success. The two configurations contradict each other — one group's part
        /// geometry encloses another group's body — and there is no non-arbitrary way to pick a winner, so the
        /// whole avatar is refused with a blocking diagnostic instead of being assembled.
        /// </para>
        /// <para>
        /// This is checked before anything is planned or built, by both the build and the preview
        /// (<c>ApaCore.PlanGroups</c>), so both see the same verdict and no mesh, proxy, or destruction happens
        /// on the failure path. <see cref="ForGroup"/> additionally never consumes any target renderer, which
        /// keeps the rule true even for a caller that plans a group without this check.
        /// </para>
        /// </remarks>
        internal static void CollectTargetsInsideForeignPartRoots(
            GameObject avatarRoot,
            IReadOnlyList<ApaTargetGroupPlan> groups,
            List<ValidationIssue> issues)
        {
            if (groups == null || issues == null) return;

            for (var g = 0; g < groups.Count; g++)
            {
                var target = groups[g].TargetRenderer;
                if (target == null) continue;

                for (var h = 0; h < groups.Count; h++)
                {
                    if (h == g) continue;

                    var consumingGroup = groups[h];
                    var reported = false;

                    for (var i = 0; i < consumingGroup.Installers.Count && !reported; i++)
                    {
                        var installer = consumingGroup.Installers[i];
                        if (installer == null) continue;

                        var partRoot = installer.ResolvePartRoot();
                        if (partRoot == null) continue;

                        var root = partRoot.transform;
                        if (target.transform != root && !target.transform.IsChildOf(root)) continue;

                        var rootPath = avatarRoot != null
                            ? MeshSnapshotFactory.RelativePath(avatarRoot.transform, root)
                            : partRoot.name;

                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.TargetRendererNotFound,
                            ApaIssuePhase.Compatibility,
                            "The target renderer of group '" + groups[g].GroupKey + "' lives inside the part " +
                            "root '" + rootPath + "' consumed by group '" + consumingGroup.GroupKey + "'. A " +
                            "group's assembly consumes every renderer under its parts' roots, so this " +
                            "configuration would destroy the other group's target renderer and silently drop " +
                            "that group's geometry from the avatar. Move the target renderer out of that part " +
                            "root, or move the part so its root does not contain another group's target.",
                            detail: "reason=target-inside-another-groups-part-root; target=" +
                                    groups[g].GroupKey + "; partRoot=" + rootPath + "; consumedByGroup=" +
                                    consumingGroup.GroupKey));

                        reported = true;
                    }
                }
            }
        }

        /// <summary>True when <paramref name="renderer"/> is one of the run's target renderers.</summary>
        private static bool IsTargetRenderer(IReadOnlyList<Renderer> targetRenderers, Renderer renderer)
        {
            if (targetRenderers == null) return false;

            for (var i = 0; i < targetRenderers.Count; i++)
            {
                if (targetRenderers[i] == renderer) return true;
            }

            return false;
        }

        /// <summary>
        /// The renderers under a consumed part root that belong to an installer this run does not process.
        /// </summary>
        /// <remarks>
        /// Found through the components under the consumed part root rather than through the caller's installer
        /// list, because a parked installer is exactly the one the list does not contain: the runtime component
        /// is still there to be found, and its own part root says which renderer the parked geometry lives on.
        /// </remarks>
        private static HashSet<Renderer> CollectParkedPartRenderers(
            GameObject partRoot,
            AvatarPartInstaller processed,
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            var result = new HashSet<Renderer>();
            var components = partRoot.GetComponentsInChildren<AvatarPartInstaller>(true);

            for (var i = 0; i < components.Length; i++)
            {
                var candidate = components[i];
                if (candidate == null || candidate == processed) continue;

                var isProcessed = false;
                for (var j = 0; j < installers.Count; j++)
                {
                    if (installers[j] != candidate) continue;
                    isProcessed = true;
                    break;
                }

                if (isProcessed) continue;

                var candidateRoot = candidate.ResolvePartRoot();
                var candidateRenderer = candidateRoot != null
                    ? candidateRoot.GetComponentInChildren<Renderer>(true)
                    : null;

                if (candidateRenderer != null) result.Add(candidateRenderer);
            }

            return result;
        }
    }
}

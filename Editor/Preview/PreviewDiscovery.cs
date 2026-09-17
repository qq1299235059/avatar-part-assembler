using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// One discovery result: one target group's captured inputs, and whether the preview can be produced from them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This object is the data attached to a <c>RenderGroup</c>, so it is also the identity of the preview node
    /// that will be built from it. Discovery captures the inputs once and carries them forward; the node does not
    /// re-read the scene, which is what keeps the preview and the build reading the same immutable snapshot
    /// through the same core entry point.
    /// </para>
    /// <para>
    /// <b>One request per target group.</b> An avatar may weld parts to a body renderer and other parts to a
    /// separate clothing renderer; each of those targets is a group with its own context, its own plan, its own
    /// generated mesh, and therefore its own preview proxy. A part belongs to exactly one group, so a group's
    /// request never contains another group's geometry and no part is assembled twice.
    /// </para>
    /// <para>
    /// Two requests are equal when they resolve to the same group with the same content fingerprint and the same
    /// renderability. Every input that can change the generated mesh is inside the fingerprint, so equality is
    /// exactly "the preview output would be identical". That is what lets the NDMF pipeline reuse a node instead
    /// of rebuilding one on every editor change.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewRequest
    {
        private static readonly AvatarPartInstaller[] s_noInstallers = new AvatarPartInstaller[0];
        private static readonly Renderer[] s_noRenderers = new Renderer[0];
        private static readonly string[] s_noNotes = new string[0];

        /// <summary>The avatar root this request was discovered under.</summary>
        public GameObject AvatarRoot { get; }

        /// <summary>
        /// The target group this request describes: the resolved target renderer's avatar-root-relative path, or
        /// an empty string for a request that carries diagnostics only.
        /// </summary>
        public string GroupKey { get; }

        /// <summary>This group's installers, in canonical order. The context carries the same parts.</summary>
        public IReadOnlyList<AvatarPartInstaller> Installers { get; }

        /// <summary>
        /// Every installer found under the root, including ones that are not active for build. Parked installers
        /// contribute nothing to the plan, but they are observed so that enabling one invalidates the preview.
        /// </summary>
        public IReadOnlyList<AvatarPartInstaller> AllInstallers { get; }

        /// <summary>
        /// Every installer this avatar's build processes, across all of its target groups, in canonical order.
        /// </summary>
        /// <remarks>
        /// The consumption plan needs the whole run's installer set, not only this group's: the question "does an
        /// installer this build does not process own this renderer?" is only answerable against the full set.
        /// </remarks>
        public IReadOnlyList<AvatarPartInstaller> ActiveInstallers { get; }

        /// <summary>The numeric policy the capture was taken with.</summary>
        public ApaNumericPolicy NumericPolicy { get; }

        /// <summary>The captured, immutable inputs of this group, or null when capture failed.</summary>
        public ValidationContext Context { get; }

        /// <summary>
        /// This group's validated plan, or null when the group was not planned. It is the same plan the build
        /// would assemble for this group, produced by the same validator and planner.
        /// </summary>
        public PlanningResult Planning { get; }

        /// <summary>
        /// Content fingerprint of <see cref="Context"/> plus this group's key, or
        /// <see cref="ApaPreviewFingerprint.UncachedMarker"/> when nothing was captured.
        /// </summary>
        public string Fingerprint { get; }

        /// <summary>
        /// Cheap revision of the structural inputs (which installers exist, in what order, with what references).
        /// Used by <c>ApaPreviewNode.Refresh</c> to decide whether a node can be reused without re-reading every
        /// mesh. It is deliberately not a content hash.
        /// </summary>
        public string StructuralKey { get; }

        /// <summary>The group's resolved target renderer, or null when it could not be resolved.</summary>
        public Renderer TargetRenderer { get; }

        /// <summary>Avatar-root-relative path of the target renderer, or an empty string.</summary>
        public string TargetRendererPath { get; }

        /// <summary>
        /// The part renderers this group's assembly replaces: the geometry the build destroys, and therefore the
        /// geometry the preview must stop drawing so the part is not drawn twice.
        /// </summary>
        public IReadOnlyList<Renderer> ConsumedRenderers { get; }

        /// <summary>True when a proxy can be produced from this request.</summary>
        public bool IsRenderable { get; }

        /// <summary>Diagnostics from capture and validation, in the core's deterministic order.</summary>
        public ValidationResult Issues { get; }

        /// <summary>Preview-level notes that are not APA issues (for example an unsupported proxy renderer type).</summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>Creates a request.</summary>
        public ApaPreviewRequest(
            GameObject avatarRoot,
            string groupKey,
            IReadOnlyList<AvatarPartInstaller> installers,
            IReadOnlyList<AvatarPartInstaller> allInstallers,
            IReadOnlyList<AvatarPartInstaller> activeInstallers,
            ApaNumericPolicy numericPolicy,
            ValidationContext context,
            PlanningResult planning,
            string fingerprint,
            string structuralKey,
            Renderer targetRenderer,
            string targetRendererPath,
            IReadOnlyList<Renderer> consumedRenderers,
            bool isRenderable,
            ValidationResult issues,
            IReadOnlyList<string> notes)
        {
            AvatarRoot = avatarRoot;
            GroupKey = groupKey ?? string.Empty;
            Installers = installers ?? s_noInstallers;
            AllInstallers = allInstallers ?? s_noInstallers;
            ActiveInstallers = activeInstallers ?? s_noInstallers;
            NumericPolicy = numericPolicy ?? ApaNumericPolicy.Default;
            Context = context;
            Planning = planning;
            Fingerprint = fingerprint ?? ApaPreviewFingerprint.UncachedMarker;
            StructuralKey = structuralKey ?? string.Empty;
            TargetRenderer = targetRenderer;
            TargetRendererPath = targetRendererPath ?? string.Empty;
            ConsumedRenderers = consumedRenderers ?? s_noRenderers;
            IsRenderable = isRenderable;
            Issues = issues ?? ValidationResult.Empty;
            Notes = notes ?? s_noNotes;
        }

        /// <summary>
        /// Identity of this avatar: the scene it lives in plus the identity of its root object.
        /// </summary>
        /// <remarks>
        /// Not the hierarchy path: two loaded scenes can contain the same avatar hierarchy, and a path-keyed store
        /// would then let one avatar's diagnostics overwrite (and clear) the other's.
        /// </remarks>
        public ApaPreviewDiagnosticKey DiagnosticKey => ApaPreviewDiagnosticKey.For(AvatarRoot, GroupKey);

        /// <summary>The key of the avatar as a whole, for sweeping every group's report at once.</summary>
        public ApaPreviewDiagnosticKey AvatarKey => ApaPreviewDiagnosticKey.For(AvatarRoot);

        /// <summary>True when this request describes a real target group rather than a diagnostics-only report.</summary>
        public bool HasGroup => !string.IsNullOrEmpty(GroupKey) && TargetRenderer != null;

        /// <inheritdoc />
        public override string ToString()
        {
            return "ApaPreviewRequest(" + DiagnosticKey + " fingerprint=" +
                   Fingerprint + (IsRenderable ? ", renderable" : ", blocked") +
                   (ConsumedRenderers.Count > 0 ? ", consumes " + ConsumedRenderers.Count + " renderer(s)" : "") +
                   ")";
        }
    }

    /// <summary>
    /// Equality of discovery results, used as the <c>RenderGroup.WithData</c> semantics.
    /// </summary>
    /// <remarks>
    /// The comparer and the hash are consistent with each other and with the fingerprint: equal requests hash
    /// equally. The hash code is used by the preview pipeline to order groups deterministically within a session,
    /// which is why it is derived from the renderer instance, the group key, and the fingerprint rather than from
    /// anything unordered. The group key is part of the identity because two groups of one avatar can share a
    /// target renderer type and even a mesh asset while producing different output.
    /// </remarks>
    public sealed class ApaPreviewRequestComparer : IEqualityComparer<ApaPreviewRequest>
    {
        /// <summary>The shared instance; the type is stateless.</summary>
        public static readonly ApaPreviewRequestComparer Instance = new ApaPreviewRequestComparer();

        private ApaPreviewRequestComparer()
        {
        }

        /// <inheritdoc />
        public bool Equals(ApaPreviewRequest x, ApaPreviewRequest y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;

            return x.TargetRenderer == y.TargetRenderer
                   && string.Equals(x.GroupKey, y.GroupKey, StringComparison.Ordinal)
                   && x.IsRenderable == y.IsRenderable
                   && string.Equals(x.Fingerprint, y.Fingerprint, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public int GetHashCode(ApaPreviewRequest obj)
        {
            if (obj == null) return 0;

            unchecked
            {
                var hash = obj.TargetRenderer != null ? obj.TargetRenderer.GetInstanceID() : 0;
                hash = (hash * 397) ^ (obj.IsRenderable ? 1 : 0);

                var group = obj.GroupKey;
                if (group != null)
                {
                    for (var i = 0; i < group.Length; i++) hash = (hash * 397) ^ group[i];
                }

                var fingerprint = obj.Fingerprint;
                if (fingerprint != null)
                {
                    for (var i = 0; i < fingerprint.Length; i++) hash = (hash * 397) ^ fingerprint[i];
                }

                return hash;
            }
        }
    }

    /// <summary>
    /// Resolves which avatars, target groups, and renderers the preview must process, and captures their inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovery never touches a core rule: it resolves the avatar's target groups through
    /// <see cref="ApaCore.PlanGroups"/> — the same entry point the build uses — and reads each group's captured
    /// context and planned result straight off that result. Blocking an invalid configuration is therefore the
    /// same decision the build makes, not a second opinion, which is required by section 26 ("Preview = Blocked"
    /// for ERROR) and by the single-algorithm principle of section 25.
    /// </para>
    /// <para>
    /// <b>The installer set is the build's set.</b> Discovery asks
    /// <see cref="ContextBuilder.CollectInstallers(GameObject, List{ValidationIssue})"/> for the active
    /// installers, which is the one place the shared activity predicate
    /// (<see cref="AvatarPartInstaller.IsActiveForBuild"/>) and the avatar-ownership boundary are applied, and it
    /// asks <see cref="ContextBuilder.CollectAllInstallers"/> for the wider observation set. A part that the
    /// build would park is therefore never previewed, and a nested avatar's parts are never welded into this
    /// avatar's proxy.
    /// </para>
    /// <para>
    /// The target renderer is not resolved by a second, parallel copy of the core's resolution rules. It is read
    /// back from the group the core resolved, and the path it is keyed by is the path the core itself recorded.
    /// </para>
    /// <para>
    /// Nothing here mutates a Unity object. Every value that leaves this class is either an immutable snapshot or
    /// a read-only reference used for observation.
    /// </para>
    /// </remarks>
    public static class ApaPreviewDiscovery
    {
        /// <summary>
        /// Collects every installer under an avatar root, including inactive ones, stopping at a nested avatar.
        /// </summary>
        /// <remarks>
        /// The walk is the build's own walk (<see cref="ContextBuilder.CollectAllInstallers"/>): depth-first, in a
        /// deterministic order, and it does not descend into a GameObject that carries another avatar's
        /// descriptor, because those installers belong to that avatar's build. A preview that collected them
        /// would show a part the build will never install.
        /// </remarks>
        public static List<AvatarPartInstaller> CollectAllInstallers(GameObject avatarRoot)
        {
            return ContextBuilder.CollectAllInstallers(avatarRoot);
        }

        /// <summary>
        /// Captures the inputs of every target group of one avatar.
        /// </summary>
        /// <remarks>
        /// Returning an empty list for an unaffected avatar is deliberate: it means "no preview and no
        /// diagnostics", which is what makes deleting the installer restore the original avatar without leaving
        /// an error behind (section 25). A configuration that cannot be planned produces exactly one
        /// non-renderable request whose diagnostics describe every group, so the failure is visible without any
        /// proxy being created.
        /// </remarks>
        public static IReadOnlyList<ApaPreviewRequest> DiscoverGroups(GameObject avatarRoot, ApaNumericPolicy numericPolicy)
        {
            if (avatarRoot == null) return new List<ApaPreviewRequest>();
            return DiscoverGroups(avatarRoot, CollectAllInstallers(avatarRoot), numericPolicy);
        }

        /// <summary>
        /// Captures the inputs of every target group of one avatar from an explicit installer scan.
        /// </summary>
        /// <param name="avatarRoot">The avatar root.</param>
        /// <param name="allInstallers">
        /// Every installer under the root, active or not, in <see cref="CollectAllInstallers"/> order. The order
        /// is load-bearing: it is hashed into the structural key that <c>ApaPreviewNode.Refresh</c> recomputes, so
        /// a list in hierarchy order would make every refresh look like a change and rebuild the preview forever.
        /// </param>
        /// <param name="numericPolicy">Tolerances, or null for the defaults.</param>
        public static IReadOnlyList<ApaPreviewRequest> DiscoverGroups(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> allInstallers,
            ApaNumericPolicy numericPolicy)
        {
            var requests = new List<ApaPreviewRequest>();
            if (avatarRoot == null) return requests;

            var policy = numericPolicy ?? ApaNumericPolicy.Default;
            var all = allInstallers ?? CollectAllInstallers(avatarRoot);
            var structuralKey = ComputeStructuralKey(avatarRoot, all);

            // The active set is the build's own set. Its diagnostics (a parked installer, APA039) are avatar-level
            // and are carried by every request of this avatar.
            var discoveryIssues = new List<ValidationIssue>();
            List<AvatarPartInstaller> active;
            try
            {
                active = ContextBuilder.CollectInstallers(avatarRoot, discoveryIssues);
            }
            catch (Exception e)
            {
                requests.Add(Failure(
                    avatarRoot, all, policy, structuralKey,
                    ApaPreviewDiagnostics.FormatException("ContextBuilder.CollectInstallers", e),
                    "exception=" + e.GetType().FullName));
                return requests;
            }

            if (active.Count == 0)
            {
                // No active installer: the avatar is not affected at all, so there is nothing to say about it.
                return requests;
            }

            // The same grouped planning entry point the build pass calls: every group is resolved, validated, and
            // planned, and a failure in any group means the avatar has no preview at all — exactly as a failure in
            // any group means the build produces no partial avatar.
            TargetGroupPlanResult planned;
            try
            {
                planned = ApaCore.PlanGroups(avatarRoot, policy, out _);
            }
            catch (Exception e)
            {
                requests.Add(Failure(
                    avatarRoot, all, policy, structuralKey,
                    ApaPreviewDiagnostics.FormatException("ApaCore.PlanGroups", e),
                    "exception=" + e.GetType().FullName));
                return requests;
            }

            if (planned == null || planned.NotApplicable) return requests;

            if (!planned.Succeeded)
            {
                var blockedIssues = new List<ValidationIssue>(discoveryIssues);
                blockedIssues.AddRange(planned.Issues.Issues);

                requests.Add(new ApaPreviewRequest(
                    avatarRoot,
                    string.Empty,
                    active,
                    all,
                    active,
                    policy,
                    null,
                    null,
                    ApaPreviewFingerprint.UncachedMarker,
                    structuralKey,
                    null,
                    string.Empty,
                    Array.Empty<Renderer>(),
                    false,
                    ValidationResult.Build(blockedIssues),
                    new List<string>
                    {
                        "The configuration cannot be planned, so no proxy is produced and the avatar is shown " +
                        "unchanged. The diagnostics name the group that blocked."
                    }));

                return requests;
            }

            // Every target renderer of this avatar. The consumption planner excludes all of them, not only the
            // group's own, so the preview hides exactly what the build consumes and never hides another group's
            // target; see ApaPartConsumptionPlanner.ForGroup. The same reason the build collects this list.
            var targetRenderers = new List<Renderer>(planned.GroupCount);
            for (var g = 0; g < planned.GroupCount; g++)
            {
                var targetRenderer = planned.Groups[g].TargetRenderer;
                if (targetRenderer != null) targetRenderers.Add(targetRenderer);
            }

            for (var g = 0; g < planned.GroupCount; g++)
            {
                requests.Add(CreateGroupRequest(
                    avatarRoot, planned.Groups[g], planned.Plans[g], all, active, policy, structuralKey,
                    discoveryIssues, targetRenderers));
            }

            return requests;
        }

        /// <summary>
        /// Captures the primary (lowest-key) target group of one avatar, or a diagnostics-only request.
        /// </summary>
        /// <remarks>
        /// Kept for callers that only ever need one group — a test, or a diagnostic that wants "what is previewed
        /// for this avatar". Everything that has to be correct for a multi-target avatar uses
        /// <see cref="DiscoverGroups"/>.
        /// </remarks>
        public static ApaPreviewRequest Discover(GameObject avatarRoot, ApaNumericPolicy numericPolicy)
        {
            if (avatarRoot == null) return null;
            return Discover(avatarRoot, CollectAllInstallers(avatarRoot), numericPolicy);
        }

        /// <summary>
        /// Captures the primary (lowest-key) target group of one avatar from an explicit installer scan.
        /// </summary>
        public static ApaPreviewRequest Discover(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> allInstallers,
            ApaNumericPolicy numericPolicy)
        {
            var groups = DiscoverGroups(avatarRoot, allInstallers, numericPolicy);
            return groups.Count > 0 ? groups[0] : null;
        }

        /// <summary>
        /// Finds one group's request in a discovery result, by group key and then by target renderer.
        /// </summary>
        /// <remarks>
        /// A node that refreshes must find <i>its own</i> group and nothing else. Matching by key first handles
        /// the normal case; matching by target renderer reference covers a group whose key changed because the
        /// target was renamed or reparented, which the pipeline rebuilds the target set for anyway. A group that
        /// is not present returns null, and the caller draws nothing, which is the safe answer: drawing another
        /// group's mesh onto this proxy would put the wrong geometry on this renderer.
        /// </remarks>
        public static ApaPreviewRequest FindGroup(
            IReadOnlyList<ApaPreviewRequest> groups,
            string groupKey,
            Renderer targetRenderer)
        {
            if (groups == null || groups.Count == 0) return null;

            if (!string.IsNullOrEmpty(groupKey))
            {
                for (var i = 0; i < groups.Count; i++)
                {
                    if (string.Equals(groups[i].GroupKey, groupKey, StringComparison.Ordinal)) return groups[i];
                }
            }

            if (targetRenderer != null)
            {
                for (var i = 0; i < groups.Count; i++)
                {
                    if (groups[i].TargetRenderer == targetRenderer) return groups[i];
                }
            }

            return null;
        }

        /// <summary>Builds the request of one planned target group.</summary>
        private static ApaPreviewRequest CreateGroupRequest(
            GameObject avatarRoot,
            ApaTargetGroupPlan group,
            PlanningResult planning,
            IReadOnlyList<AvatarPartInstaller> all,
            IReadOnlyList<AvatarPartInstaller> active,
            ApaNumericPolicy policy,
            string structuralKey,
            IReadOnlyList<ValidationIssue> discoveryIssues,
            IReadOnlyList<Renderer> targetRenderers)
        {
            var context = group.Context;
            var target = group.TargetRenderer;
            var issues = new List<ValidationIssue>(discoveryIssues);
            var notes = new List<string>();

            if (planning != null) issues.AddRange(planning.Issues.Issues);

            var fingerprint = ApaPreviewFingerprint.UncachedMarker;
            if (context != null)
            {
                fingerprint = ApaPreviewFingerprint.OfContext(context) + "|group=" + group.GroupKey;
            }

            var consumed = Array.Empty<Renderer>();
            if (target == null)
            {
                notes.Add("The group's target renderer '" + group.GroupKey +
                          "' did not resolve to a Renderer in this hierarchy.");
            }
            else if (!IsSupportedProxyRenderer(target))
            {
                notes.Add("The NDMF preview system proxies MeshRenderer and SkinnedMeshRenderer only; '" +
                          DescribePath(target.transform) + "' is a " + target.GetType().Name +
                          ". The build is unaffected, but no preview is shown for it.");
            }
            else if (context != null)
            {
                // The same consumption contract the build applies: the part renderers whose geometry the
                // generated mesh contains, and never any group's target renderer. The preview hides exactly
                // these, so a part is never drawn twice and another group's target is never hidden.
                try
                {
                    consumed = ToArray(ApaPartConsumptionPlanner.ForGroup(
                            avatarRoot, context, active, targetRenderers, null)
                        .Renderers);
                }
                catch (Exception e)
                {
                    ApaPreviewDiagnostics.ReportInternalFailure(
                        "Resolving the preview consumption set for '" + group.GroupKey + "'", e);
                }
            }

            var result = ValidationResult.Build(issues);
            var renderable = context != null
                             && planning != null
                             && planning.Succeeded
                             && target != null
                             && IsSupportedProxyRenderer(target)
                             && !result.HasErrors;

            return new ApaPreviewRequest(
                avatarRoot,
                group.GroupKey,
                group.Installers,
                all,
                active,
                policy,
                context,
                planning,
                fingerprint,
                structuralKey,
                target,
                group.GroupKey,
                consumed,
                renderable,
                result,
                notes);
        }

        /// <summary>A request that carries a discovery-level failure and no group.</summary>
        private static ApaPreviewRequest Failure(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> all,
            ApaNumericPolicy policy,
            string structuralKey,
            string message,
            string detail)
        {
            return new ApaPreviewRequest(
                avatarRoot,
                string.Empty,
                all,
                all,
                all,
                policy,
                null,
                null,
                ApaPreviewFingerprint.UncachedMarker,
                structuralKey,
                null,
                string.Empty,
                Array.Empty<Renderer>(),
                false,
                ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    message,
                    detail: detail)),
                new List<string> { "Preview discovery failed; the avatar is shown unchanged." });
        }

        private static Renderer[] ToArray(IReadOnlyList<Renderer> renderers)
        {
            if (renderers == null || renderers.Count == 0) return Array.Empty<Renderer>();

            var result = new Renderer[renderers.Count];
            for (var i = 0; i < renderers.Count; i++) result[i] = renderers[i];
            return result;
        }

        /// <summary>
        /// Cheap revision of the structural inputs, used to decide whether a node may be reused.
        /// </summary>
        /// <remarks>
        /// This is a revision of <i>what exists</i>, not of mesh contents: which installers are present, in which
        /// order, with which references, and which target each resolves to. Content changes are carried by the
        /// captured fingerprint and by the observations registered on the node, which is why this check can stay
        /// cheap enough to run whenever the preview pipeline asks a node to refresh.
        /// </remarks>
        public static string ComputeStructuralKey(GameObject avatarRoot, IReadOnlyList<AvatarPartInstaller> allInstallers)
        {
            var builder = new ApaFingerprintBuilder();
            builder.Add("apa-preview-structural-v2");
            builder.AddObjectIdentity(avatarRoot);
            builder.Add(allInstallers == null ? -1 : allInstallers.Count);

            if (allInstallers != null)
            {
                var rootTransform = avatarRoot != null ? avatarRoot.transform : null;
                for (var i = 0; i < allInstallers.Count; i++)
                {
                    var installer = allInstallers[i];
                    if (installer == null)
                    {
                        builder.Add("null-installer");
                        continue;
                    }

                    builder.Add(MeshSnapshotFactory.RelativePath(rootTransform, installer.transform));
                    builder.Add(installer.EnabledForBuild);
                    builder.AddObjectIdentity(installer.Profile);
                    builder.AddObjectIdentity(installer.TargetRendererObject);

                    var partRoot = installer.ResolvePartRoot();
                    builder.AddObjectIdentity(partRoot);
                    if (partRoot != null)
                    {
                        // The part renderer is a reference check only; its mesh content is fingerprinted when the
                        // context is captured.
                        builder.AddObjectIdentity(partRoot.GetComponentInChildren<Renderer>(true));
                    }
                }
            }

            return builder.Value;
        }

        /// <summary>Resolves an avatar-root-relative renderer path, handling the reserved root token.</summary>
        public static Renderer ResolveRenderer(GameObject avatarRoot, string rendererPath)
        {
            if (avatarRoot == null || !ApaAvatarPath.HasIdentity(rendererPath)) return null;

            // Transform.Find reserves ".", so the root token must be handled before the lookup rather than
            // passed through it. This mirrors the core's fallback resolution exactly.
            var transform = ApaAvatarPath.IsRoot(rendererPath)
                ? avatarRoot.transform
                : avatarRoot.transform.Find(rendererPath);

            return transform == null ? null : transform.GetComponent<Renderer>();
        }

        /// <summary>True when the NDMF preview system can proxy this renderer type.</summary>
        public static bool IsSupportedProxyRenderer(Renderer renderer)
        {
            return renderer is SkinnedMeshRenderer || renderer is MeshRenderer;
        }

        /// <summary>
        /// Maps part id to the part renderer the capture would read, for the sources that carry blend shapes.
        /// </summary>
        /// <remarks>
        /// The mapping mirrors <c>ContextBuilder.CaptureParts</c>: the part renderer is the first renderer under
        /// the part root. When two installers declare the same part id the first one in canonical order wins; the
        /// duplicate is a validation error (<c>APA013</c>) and the preview is blocked, so the ambiguity never
        /// reaches a rendered result.
        /// </remarks>
        public static Dictionary<string, Renderer> BuildPartRendererMap(
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            var map = new Dictionary<string, Renderer>(StringComparer.Ordinal);
            if (installers == null) return map;

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                if (installer == null) continue;

                var partRoot = installer.ResolvePartRoot();
                var renderer = partRoot != null ? partRoot.GetComponentInChildren<Renderer>(true) : null;
                var partId = ApaPartIdentityResolver.ResolvePartId(installer);
                if (string.IsNullOrEmpty(partId)) continue;

                if (!map.ContainsKey(partId)) map.Add(partId, renderer);
            }

            return map;
        }

        /// <summary>
        /// Deterministic order of two avatar roots: scene first, then object identity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The order decides which of two roots claims a shared body renderer when avatars are nested — "first
        /// claim wins". Comparing full scene paths would make two identical hierarchies in two loaded scenes
        /// compare equal, and <c>List.Sort</c> is not a stable sort, so the claim could flip between passes and
        /// the preview would flip with it. A scene handle and an instance id form a total order that is stable for
        /// as long as the objects live, and neither is a hash code or an internal Unity ordering.
        /// </para>
        /// <para>
        /// The path is only a final tiebreak, for readability in the impossible case that both keys are equal.
        /// </para>
        /// </remarks>
        public static int CompareAvatarRoots(GameObject a, GameObject b)
        {
            var identity = ApaPreviewDiagnosticKey.For(a).CompareTo(ApaPreviewDiagnosticKey.For(b));
            if (identity != 0) return identity;

            return string.CompareOrdinal(
                DescribePath(a != null ? a.transform : null),
                DescribePath(b != null ? b.transform : null));
        }

        /// <summary>Full scene path of a transform, used for display and for path resolution.</summary>
        public static string DescribePath(Transform transform)
        {
            if (transform == null) return string.Empty;

            var segments = new List<string>();
            var current = transform;
            while (current != null)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }
    }
}

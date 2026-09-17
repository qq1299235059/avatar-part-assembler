using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// One target renderer group: the renderer every part in the group is planned against, and the context that
    /// carries those parts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A group exists because a real avatar can legitimately carry parts welded to a body renderer and parts
    /// welded to a separate clothing renderer. Each group gets its own context, plan, and mesh, because parts in
    /// different groups do not share a body: slot uniqueness, removal overlap, the UV layout, the material
    /// layout, and the bone table are all group-scoped.
    /// </para>
    /// <para>
    /// The plan is produced for every group before any group is mutated, and the group owns the renderer it was
    /// resolved against so a caller never has to re-resolve it (which is how preview and build would drift).
    /// </para>
    /// </remarks>
    public sealed class ApaTargetGroupPlan
    {
        /// <summary>
        /// The group key: the resolved target renderer's avatar-root-relative path, ordinal. Unique by
        /// construction, because two transforms cannot share one hierarchy path.
        /// </summary>
        public string GroupKey { get; }

        /// <summary>The renderer this group's parts are planned against.</summary>
        public Renderer TargetRenderer { get; }

        /// <summary>The group's context, carrying only this group's parts.</summary>
        public ValidationContext Context { get; }

        /// <summary>The active installers assigned to this group, in canonical installer order.</summary>
        public IReadOnlyList<AvatarPartInstaller> Installers { get; }

        /// <summary>Creates a group plan.</summary>
        public ApaTargetGroupPlan(
            string groupKey,
            Renderer targetRenderer,
            ValidationContext context,
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            GroupKey = groupKey ?? string.Empty;
            TargetRenderer = targetRenderer;
            Context = context;

            var copy = new AvatarPartInstaller[installers != null ? installers.Count : 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = installers[i];
            Installers = Array.AsReadOnly(copy);
        }
    }

    /// <summary>
    /// Builds a <see cref="ValidationContext"/> from an avatar hierarchy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the boundary between Unity objects and the pure core. Everything it reads is copied into
    /// snapshots, so from this point on no Unity object can be modified by accident and no code beyond this
    /// class needs to know about hierarchy traversal.
    /// </para>
    /// <para>
    /// Installer discovery is explicitly sorted. <c>GetComponentsInChildren</c> returns components in hierarchy
    /// order, which is an implementation detail that can change when an object is reparented. Because ordering
    /// reaches the output mesh, it is normalized here rather than trusted.
    /// </para>
    /// <para>
    /// <b>An installer belongs to exactly one avatar.</b> The walk is depth-first and stops descending at a
    /// nested avatar descriptor, so avatar A's parts can never be welded into avatar B's body when one avatar is
    /// nested inside another. The descriptor type is matched by full name through reflection rather than by a
    /// compile-time reference, because the SDK type is not guaranteed to exist in every project that compiles
    /// this package; a project that wants to be explicit can register its own boundary type with
    /// <see cref="RegisterAvatarBoundaryType"/>.
    /// </para>
    /// <para>
    /// <b>Only active installers are installed.</b> The shared predicate is
    /// <see cref="AvatarPartInstaller.IsActiveForBuild"/>; a parked installer is reported (<c>APA039</c>,
    /// informational) rather than silently missing, and it never deletes body triangles.
    /// </para>
    /// <para>
    /// <b>One context has exactly one target body renderer; one avatar may have several groups.</b> The legacy
    /// <see cref="Build"/> and <see cref="BuildFromInstallers"/> entry points keep the single-target contract and
    /// block when two installers name different targets. <see cref="BuildGroups"/> resolves every installer's
    /// target first and produces one context per resolved renderer, so a body group and a clothing group can
    /// coexist. Two different recorded targets form two valid groups; an installer whose explicit target
    /// disagrees with the target its profile recorded blocks, because that is one assembly naming two bodies.
    /// </para>
    /// <para>
    /// Every path this class records or compares is avatar-root-relative
    /// (<see cref="MeshSnapshotFactory.RelativePath"/>), because that is the path the compatibility signature
    /// stores and the path the pipeline resolves against the avatar root. A full scene path would include the
    /// avatar's own ancestors and would stop resolving as soon as the avatar is nested below another object.
    /// </para>
    /// <para>
    /// A renderer, bone, or installer that sits on the avatar root itself records
    /// <see cref="ApaAvatarPath.Root"/> (<c>"."</c>), which is a valid identity and resolves to the root
    /// transform. The empty string is reserved for "missing" and is never resolved: a profile that recorded the
    /// avatar root as an empty renderer path predates the token and cannot be told apart from one that recorded
    /// nothing at all, so it stays unsupported rather than being guessed at.
    /// </para>
    /// <para>
    /// <b>Bone identity is armature-relative and the two armatures are explicit (M10).</b> A profile selects the
    /// target armature (relative to the avatar root) and the part armature (relative to the part root); each
    /// renderer's bones are recorded as paths relative to its own selected armature, so two bones with the same
    /// relative path are the same joint whatever the part's placement is. A selection that is missing,
    /// unresolvable, or outside its root blocks (<c>APA043</c>), and a weighted bone outside the selected
    /// armature blocks (<c>APA044</c>) — nothing is derived from the old avatar-root path.
    /// </para>
    /// <para>
    /// <b>Reads do not mutate authoring assets.</b> The profile is a <c>ScriptableObject</c> shared with the
    /// authoring scene, so this class reads the non-mutating <c>*OrNull</c> accessors and never calls
    /// <c>EnsureInitialized</c> or rewrites the schema version.
    /// </para>
    /// </remarks>
    public static class ContextBuilder
    {
        /// <summary>
        /// Full names of components that mark the root of a nested avatar. Matched by name through reflection so
        /// that compiling this package never requires the VRChat SDK.
        /// </summary>
        /// <remarks>
        /// The SDK3 name is the one the installed VRChat SDK (3.10.4) and NDMF 1.14.0 use
        /// (<c>VRC.SDK3.Avatars.Components.VRCAvatarDescriptor</c>). The legacy base name is kept so that an
        /// SDK2-era avatar is still treated as an ownership boundary rather than having its parts welded into
        /// whatever avatar contains it.
        /// </remarks>
        private static readonly List<string> s_avatarBoundaryTypeNames = new List<string>
        {
            "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor",
            "VRC.SDKBase.VRC_AvatarDescriptor"
        };

        private static readonly Dictionary<Type, bool> s_avatarBoundaryTypeCache = new Dictionary<Type, bool>();

        /// <summary>
        /// Registers an avatar-root component type as an ownership boundary for installer discovery.
        /// </summary>
        /// <remarks>
        /// This is the explicit wiring point for a platform whose avatar root type this package cannot reference
        /// at compile time. Registering a type that is already known by default is a no-op. The registry is
        /// process-wide and intentionally tiny; it is read on the main thread only, like every other Unity
        /// object access in this class.
        /// </remarks>
        public static void RegisterAvatarBoundaryType(Type type)
        {
            if (type == null) return;

            var name = type.FullName;
            if (string.IsNullOrEmpty(name)) return;
            if (s_avatarBoundaryTypeNames.Contains(name)) return;

            s_avatarBoundaryTypeNames.Add(name);
            s_avatarBoundaryTypeCache.Clear();
        }

        /// <summary>
        /// True when the GameObject carries a component that marks the root of a nested avatar. Such an object's
        /// subtree is another avatar's, and this avatar's build must not install parts found there.
        /// </summary>
        public static bool IsAvatarBoundary(GameObject gameObject)
        {
            if (gameObject == null) return false;

            var components = gameObject.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) continue;
                if (IsAvatarBoundaryType(component.GetType())) return true;
            }

            return false;
        }

        private static bool IsAvatarBoundaryType(Type type)
        {
            if (type == null) return false;
            if (s_avatarBoundaryTypeCache.TryGetValue(type, out var cached)) return cached;

            var result = false;

            // Base types are walked so that a platform that subclasses its descriptor (or renames the concrete
            // type while keeping a known base) is still recognised.
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var fullName = current.FullName;
                if (string.IsNullOrEmpty(fullName)) continue;
                if (!s_avatarBoundaryTypeNames.Contains(fullName)) continue;

                result = true;
                break;
            }

            s_avatarBoundaryTypeCache[type] = result;
            return result;
        }

        /// <summary>
        /// Resolves every active installer under the avatar root and builds a snappable context.
        /// </summary>
        /// <param name="avatarRoot">The avatar root GameObject.</param>
        /// <param name="numericPolicy">Numeric tolerances, or null for the defaults.</param>
        /// <param name="issues">Receives discovery diagnostics.</param>
        /// <returns>A context, or null when the avatar root is unusable.</returns>
        /// <remarks>
        /// This single-target entry point keeps its M2 contract: an avatar root with no active installer blocks
        /// with <c>reason=no-installers</c>, and two different target renderers block. A caller that must treat
        /// "this avatar has no parts" as "not applicable" uses <see cref="TryBuild"/> instead.
        /// </remarks>
        public static ValidationContext Build(
            GameObject avatarRoot,
            ApaNumericPolicy numericPolicy,
            out List<ValidationIssue> issues)
        {
            issues = new List<ValidationIssue>();
            if (avatarRoot == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The avatar root is null.",
                    detail: "reason=null-avatar-root"));
                return null;
            }

            var policy = numericPolicy ?? ApaNumericPolicy.Default;
            var installers = CollectInstallers(avatarRoot, issues);

            if (installers.Count == 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.PartProfileIncompatible,
                    ApaIssuePhase.Configuration,
                    "No active AvatarPartInstaller components were found under '" + avatarRoot.name + "'.",
                    detail: "reason=no-installers"));
                return null;
            }

            return BuildFromInstallers(avatarRoot, installers, policy, out issues);
        }

        /// <summary>
        /// Builds a context when this avatar has parts, and reports "not applicable" without an error when it
        /// does not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An NDMF pass is invoked for every avatar in a build, so a pass that treated "no parts" as an error
        /// would turn every unrelated avatar red. This entry point is the documented early-out: it returns false
        /// and adds only an informational <c>APA039</c> when no installer is active.
        /// </para>
        /// <para>
        /// A genuine failure (a conflicting target, a missing profile, an unusable transform) still returns
        /// false with blocking issues; the caller distinguishes the two cases by
        /// <c>issues.Exists(issue =&gt; issue.IsBlocking)</c>, which is the same test the rest of the pipeline
        /// uses.
        /// </para>
        /// </remarks>
        /// <returns>True when a context was produced.</returns>
        public static bool TryBuild(
            GameObject avatarRoot,
            ApaNumericPolicy numericPolicy,
            out ValidationContext context,
            out List<ValidationIssue> issues)
        {
            context = null;
            issues = new List<ValidationIssue>();

            if (avatarRoot == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The avatar root is null.",
                    detail: "reason=null-avatar-root"));
                return false;
            }

            var policy = numericPolicy ?? ApaNumericPolicy.Default;
            var installers = CollectInstallers(avatarRoot, issues);

            if (installers.Count == 0)
            {
                issues.Add(ValidationIssue.Info(
                    ApaErrorCode.InactiveInstallerSkipped,
                    ApaIssuePhase.Configuration,
                    "Avatar '" + avatarRoot.name + "' has no active AvatarPartInstaller, so there is nothing to " +
                    "assemble for it.",
                    detail: "reason=no-active-installers"));
                return false;
            }

            context = BuildFromInstallers(avatarRoot, installers, policy, out issues);
            return context != null;
        }

        /// <summary>
        /// Builds a context from an explicit installer list, or from the installers under the avatar root.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The list is copied and re-sorted into the canonical installer order before anything reads it, so a
        /// caller that supplies installers in hierarchy order gets the same plan as one that sorts them itself.
        /// Resolving the shared target renderer, picking the representative compatibility signature, and
        /// ordering parts all depend on that order, and none of them may depend on how the caller built its
        /// list.
        /// </para>
        /// <para>
        /// Every installer's target is resolved before anything is captured: an explicit assignment wins over
        /// the profile's recorded path, two different renderers block, and an installer that names no usable
        /// target blocks rather than being welded into another installer's body.
        /// </para>
        /// </remarks>
        public static ValidationContext BuildFromInstallers(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> installers,
            ApaNumericPolicy numericPolicy,
            out List<ValidationIssue> issues)
        {
            issues = new List<ValidationIssue>();
            var policy = numericPolicy ?? ApaNumericPolicy.Default;

            if (avatarRoot == null || installers == null || installers.Count == 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.PartProfileIncompatible,
                    ApaIssuePhase.Configuration,
                    "No installers were supplied to the context builder.",
                    detail: "reason=no-installers"));
                return null;
            }

            var orderedInstallers = new List<AvatarPartInstaller>(installers);
            SortInstallers(avatarRoot.transform, orderedInstallers);
            installers = orderedInstallers;

            // The target renderer is resolved once for the whole plan. Every part contributes geometry into the
            // same renderer, which is what makes a single-mesh build possible. A conflict between two targets is
            // reported and produces no context: mixing them would weld parts against one body while emitting
            // them into another.
            var targetRenderer = ResolveSingleTargetRenderer(avatarRoot, installers, issues);
            if (targetRenderer == null)
            {
                // A conflict or an unusable explicit target has already reported itself. The generic message is
                // only added when nothing else explained the failure, so the report holds one reason, not two.
                if (!issues.Exists(issue => issue.IsBlocking))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "No target body renderer could be resolved for this avatar.",
                        detail: "reason=missing-target-renderer"));
                }

                return null;
            }

            // The legacy single-target entry points leave the group key empty, so their generated mesh names and
            // their diagnostics are byte-identical to M2. Only BuildGroups keys a group.
            return BuildGroupContext(
                avatarRoot,
                installers,
                targetRenderer,
                string.Empty,
                policy,
                false,
                issues);
        }

        /// <summary>
        /// Resolves every active installer's target renderer and builds one context per distinct target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two installers that resolve to two different renderers form two valid groups: a real avatar can carry
        /// parts welded to a body renderer and parts welded to a separate clothing renderer. Every group is
        /// resolved and validated before the caller assembles anything, so a failure in any group is reported
        /// against the whole avatar and no partial avatar is produced.
        /// </para>
        /// <para>
        /// This entry point is additive. <see cref="Build"/> and <see cref="BuildFromInstallers"/> keep their
        /// single-target contracts, so callers written against M2 keep compiling and behaving identically until
        /// they switch over.
        /// </para>
        /// </remarks>
        /// <returns>The group plans in ordinal group-key order, or an empty list on any blocking issue.</returns>
        public static IReadOnlyList<ApaTargetGroupPlan> BuildGroups(
            GameObject avatarRoot,
            ApaNumericPolicy numericPolicy,
            out List<ValidationIssue> issues,
            bool allowPostMergePartArmatureScope = false)
        {
            issues = new List<ValidationIssue>();

            if (avatarRoot == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The avatar root is null.",
                    detail: "reason=null-avatar-root"));
                return EmptyGroups;
            }

            var policy = numericPolicy ?? ApaNumericPolicy.Default;
            var installers = CollectInstallers(avatarRoot, issues);

            if (installers.Count == 0)
            {
                issues.Add(ValidationIssue.Info(
                    ApaErrorCode.InactiveInstallerSkipped,
                    ApaIssuePhase.Configuration,
                    "Avatar '" + avatarRoot.name + "' has no active AvatarPartInstaller, so there is nothing to " +
                    "assemble for it.",
                    detail: "reason=no-active-installers"));
                return EmptyGroups;
            }

            // ---- Assignment: resolve every installer's target before anything is planned ------------------
            var resolved = new List<ResolvedInstaller>(installers.Count);
            var unresolved = false;

            for (var i = 0; i < installers.Count; i++)
            {
                var target = ResolveInstallerTarget(avatarRoot, installers[i], issues);
                if (target == null)
                {
                    unresolved = true;
                    continue;
                }

                resolved.Add(new ResolvedInstaller(installers[i], target));
            }

            if (unresolved || issues.Exists(issue => issue.IsBlocking)) return EmptyGroups;

            // ---- Grouping: one group per distinct resolved renderer, keyed by its avatar-root-relative path --
            var groupKeys = new List<string>();
            var byKey = new Dictionary<string, List<ResolvedInstaller>>(StringComparer.Ordinal);

            for (var i = 0; i < resolved.Count; i++)
            {
                var entry = resolved[i];
                if (!byKey.TryGetValue(entry.Target.Path, out var group))
                {
                    group = new List<ResolvedInstaller>();
                    byKey.Add(entry.Target.Path, group);
                    groupKeys.Add(entry.Target.Path);
                }

                group.Add(entry);
            }

            // Ordinal group-key order, so the report and the output are deterministic regardless of hierarchy
            // scan order or how the caller built its installer list.
            groupKeys.Sort(StringComparer.Ordinal);

            // ---- Plan: build every group's context, and only then let the caller assemble -------------------
            var plans = new List<ApaTargetGroupPlan>(groupKeys.Count);
            var failed = false;

            for (var g = 0; g < groupKeys.Count; g++)
            {
                var key = groupKeys[g];
                var group = byKey[key];

                var groupInstallers = new List<AvatarPartInstaller>(group.Count);
                for (var i = 0; i < group.Count; i++) groupInstallers.Add(group[i].Installer);
                SortInstallers(avatarRoot.transform, groupInstallers);

                var context = BuildGroupContext(
                    avatarRoot,
                    groupInstallers,
                    group[0].Target.Renderer,
                    key,
                    policy,
                    allowPostMergePartArmatureScope,
                    issues);

                // A failing group does not stop the walk: the report must describe every group, so the caller
                // sees all of the avatar's problems in one pass rather than one per rebuild. No group plan is
                // returned unless every group succeeded.
                if (context == null)
                {
                    failed = true;
                    continue;
                }

                plans.Add(new ApaTargetGroupPlan(key, group[0].Target.Renderer, context, groupInstallers));
            }

            if (failed || issues.Exists(issue => issue.IsBlocking)) return EmptyGroups;

            return plans.AsReadOnly();
        }

        private static readonly ApaTargetGroupPlan[] EmptyGroups = new ApaTargetGroupPlan[0];

        /// <summary>
        /// Collects active installers under the avatar root in a deterministic order, skipping nested avatars.
        /// </summary>
        /// <remarks>
        /// The one-argument overload is the M2 entry point and reports nothing, because it has nowhere to put a
        /// diagnostic. Callers that want parked installers reported use the overload that takes an issue list.
        /// </remarks>
        public static List<AvatarPartInstaller> CollectInstallers(GameObject avatarRoot)
        {
            return CollectInstallers(avatarRoot, null);
        }

        /// <summary>
        /// Collects active installers under the avatar root, reporting each installer that was skipped because
        /// it is not active for build.
        /// </summary>
        /// <remarks>
        /// The walk deliberately still visits inactive objects: a part parked on a disabled GameObject must be
        /// reported rather than silently missing, so the author can tell "parked" apart from "deleted". It stops
        /// descending at a nested avatar descriptor, because those installers belong to that avatar's own build.
        /// </remarks>
        public static List<AvatarPartInstaller> CollectInstallers(GameObject avatarRoot, List<ValidationIssue> issues)
        {
            var result = new List<AvatarPartInstaller>();
            if (avatarRoot == null) return result;

            CollectInstallersRecursive(avatarRoot.transform, result, issues, false);
            SortInstallers(avatarRoot.transform, result);
            return result;
        }

        /// <summary>
        /// Collects every installer under the avatar root, including ones that are not active for build.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same walk as <see cref="CollectInstallers(GameObject, List{ValidationIssue})"/> with the activity
        /// filter removed, so a caller that has to <i>observe</i> parked installers — the preview, which must
        /// notice that one was enabled — gets the same order, the same avatar-ownership boundary, and the same
        /// absence of hierarchy-scan nondeterminism as the build's list.
        /// </para>
        /// <para>
        /// The boundary still applies: an installer that belongs to a nested avatar is not returned, because it
        /// is that avatar's build input and not this one's.
        /// </para>
        /// </remarks>
        public static List<AvatarPartInstaller> CollectAllInstallers(GameObject avatarRoot)
        {
            var result = new List<AvatarPartInstaller>();
            if (avatarRoot == null) return result;

            CollectInstallersRecursive(avatarRoot.transform, result, null, true);
            SortInstallers(avatarRoot.transform, result);
            return result;
        }

        private static void CollectInstallersRecursive(
            Transform current,
            List<AvatarPartInstaller> result,
            List<ValidationIssue> issues,
            bool includeInactive)
        {
            var installers = current.GetComponents<AvatarPartInstaller>();
            for (var i = 0; i < installers.Length; i++)
            {
                var installer = installers[i];
                if (installer == null) continue;

                if (includeInactive)
                {
                    // Observation mode: a parked installer is part of the set precisely so that enabling it is
                    // a change the caller can see.
                    result.Add(installer);
                    continue;
                }

                if (!installer.IsActiveForBuild)
                {
                    if (issues != null)
                    {
                        issues.Add(ValidationIssue.Info(
                            ApaErrorCode.InactiveInstallerSkipped,
                            ApaIssuePhase.Configuration,
                            "Installer on '" + current.name + "' is not active for build and was not installed. " +
                            "Enable the component, activate its GameObject, and set EnabledForBuild to install " +
                            "this part.",
                            PartIdOf(installer),
                            detail: "reason=inactive-installer; " + installer.DescribeInactiveReason() +
                                    "; installer=" + current.name));
                    }

                    continue;
                }

                result.Add(installer);
            }

            for (var i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (child == null) continue;

                // A nested avatar descriptor is an ownership boundary: everything below it belongs to that
                // avatar, and installing it here would weld another avatar's part into this body.
                if (IsAvatarBoundary(child.gameObject)) continue;

                CollectInstallersRecursive(child, result, issues, includeInactive);
            }
        }

        /// <summary>
        /// Sorts installers into the canonical order, in place.
        /// </summary>
        /// <remarks>
        /// The comparer needs the avatar root because the final tiebreaker is the installer's
        /// <i>avatar-root-relative</i> path: a full scene path would change when the avatar is moved under
        /// another object, and it is not the path the renderer fallback resolves with. Callers that want the
        /// canonical order use <see cref="CollectInstallers"/>, which applies this.
        /// </remarks>
        private static void SortInstallers(Transform avatarRoot, List<AvatarPartInstaller> installers)
        {
            if (installers == null) return;
            installers.Sort((a, b) => CompareInstallers(avatarRoot, a, b));
        }

        /// <summary>
        /// The canonical installer comparison, which is the same key the part ordering uses.
        /// </summary>
        /// <remarks>
        /// Keeping one comparator (<see cref="PartOrderingKey"/>: declared priority descending, slot, part id,
        /// display name, avatar-root-relative installer path) is what stops the installer order and the part order
        /// from drifting apart: the plan is built from part ordering keys, and those keys are derived from this
        /// same comparison. The display name is resolved here as well as in <see cref="CaptureParts"/>, so an
        /// installer is ordered by exactly the key its part snapshot will carry.
        /// </remarks>
        private static int CompareInstallers(Transform avatarRoot, AvatarPartInstaller a, AvatarPartInstaller b)
        {
            var aKey = OrderingKeyFor(avatarRoot, a);
            var bKey = OrderingKeyFor(avatarRoot, b);
            return aKey.CompareTo(bKey);
        }

        private static PartOrderingKey OrderingKeyFor(Transform avatarRoot, AvatarPartInstaller installer)
        {
            if (installer == null) return new PartOrderingKey(ApaPartSlot.Custom, string.Empty, string.Empty);

            var profile = installer.Profile;
            var identity = profile != null ? profile.IdentityOrNull : null;

            return new PartOrderingKey(
                installer.ResolveConflictPriority(),
                installer.ResolveSlot(),
                PartIdOf(installer),
                identity != null ? identity.DisplayName : string.Empty,
                MeshSnapshotFactory.RelativePath(avatarRoot, installer.transform));
        }

        /// <summary>
        /// Resolves the one renderer every part is planned against for the single-target entry points, or
        /// reports why there is not exactly one.
        /// </summary>
        /// <remarks>
        /// Every installer's target is resolved first, whether it came from an explicit assignment or from the
        /// profile's recorded path. Two different resolved renderers are a configuration the single-mesh contract
        /// cannot express: assembling both would weld parts against one body and emit them into another, so the
        /// conflict blocks and no context is produced. Resolving every installer is what removes the old
        /// "first path that happens to resolve wins" fallback, which could silently weld a part into a renderer
        /// the author never selected when two bodies shared one mesh asset.
        /// </remarks>
        private static Renderer ResolveSingleTargetRenderer(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> installers,
            List<ValidationIssue> issues)
        {
            Renderer resolved = null;

            for (var i = 0; i < installers.Count; i++)
            {
                var target = ResolveInstallerTarget(avatarRoot, installers[i], issues);
                if (target == null) return null;

                if (resolved == null)
                {
                    resolved = target.Renderer;
                    continue;
                }

                if (target.Renderer == resolved) continue;

                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "Two installers resolve to different target body renderers ('" + resolved.name + "' and '" +
                    target.Renderer.name + "'). The single-target assembly contract plans every part against one " +
                    "original body and assembles it into one mesh, so all installers must resolve the same " +
                    "target renderer. Use the target-group entry point to assemble parts for two bodies, or " +
                    "remove the conflicting target assignment.",
                    installers[i] != null ? PartIdOf(installers[i]) : string.Empty,
                    detail: "reason=conflicting-target-renderers; first=" +
                            MeshSnapshotFactory.RelativePath(avatarRoot.transform, resolved.transform) +
                            "; conflicting=" + target.Path));
                return null;
            }

            return resolved;
        }

        /// <summary>
        /// Resolves one installer's target renderer from its explicit assignment and its recorded path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An explicit assignment wins over the recorded path, because the author picked it. The recorded path is
        /// still resolved, because the two must agree: an installer whose explicit target and recorded target are
        /// different renderers is one assembly naming two bodies, which blocks.
        /// </para>
        /// <para>
        /// When the explicit target agrees with the recorded path, or when the recorded path is stale but an
        /// explicit target exists, the explicit target is authoritative. When there is no explicit target the
        /// recorded path must resolve; an empty or unresolvable path blocks with <c>reason=missing-target-renderer</c>
        /// rather than being guessed at.
        /// </para>
        /// </remarks>
        private static ResolvedTarget ResolveInstallerTarget(
            GameObject avatarRoot,
            AvatarPartInstaller installer,
            List<ValidationIssue> issues)
        {
            if (installer == null) return null;

            var root = avatarRoot.transform;
            var partId = PartIdOf(installer);
            var installerPath = MeshSnapshotFactory.RelativePath(root, installer.transform);
            var profile = installer.Profile;
            var compatibility = profile != null ? profile.CompatibilityOrNull : null;
            var recordedPath = compatibility != null ? compatibility.RendererPath : string.Empty;
            var hasRecordedPath = ApaAvatarPath.HasIdentity(recordedPath);

            Renderer explicitRenderer = null;
            if (installer.TargetRendererObject != null)
            {
                explicitRenderer = installer.TargetRendererObject.GetComponent<Renderer>();
                if (explicitRenderer == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "The configured target object '" + installer.TargetRendererObject.name +
                        "' has no Renderer component.",
                        partId,
                        detail: "reason=explicit-target-without-renderer; target=" +
                                installer.TargetRendererObject.name + "; installer=" + installerPath));
                    return null;
                }
            }

            Renderer recordedRenderer = null;
            var recordedResolved = false;
            if (hasRecordedPath)
            {
                recordedResolved = TryResolveRenderer(root, recordedPath, out recordedRenderer);
            }

            if (explicitRenderer != null)
            {
                if (hasRecordedPath && recordedResolved && recordedRenderer != explicitRenderer)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Installer on '" + installer.transform.name + "' names target renderer '" +
                        explicitRenderer.name + "', but its profile recorded '" + recordedPath +
                        "', which resolves to '" + recordedRenderer.name + "'. One part cannot be authored " +
                        "against one body and installed into another; fix the target assignment or re-author the " +
                        "profile against this body.",
                        partId,
                        detail: "reason=conflicting-target-renderers; explicit=" +
                                MeshSnapshotFactory.RelativePath(root, explicitRenderer.transform) +
                                "; recorded=" + recordedPath + "; installer=" + installerPath));
                    return null;
                }

                if (hasRecordedPath && !recordedResolved)
                {
                    // The explicit target is authoritative for resolution, but the stale record is still
                    // reported: the recorded path is part of the compatibility signature, and the compatibility
                    // rule refuses a signature whose recorded path differs from the resolved body, so a reviewer
                    // must not read this warning as "the profile will be accepted anyway".
                    issues.Add(ValidationIssue.Warning(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Installer on '" + installer.transform.name + "' has an explicit target, but the " +
                        "renderer path its profile recorded ('" + recordedPath + "') no longer resolves under " +
                        "the avatar root. Target resolution uses the explicit target; the recorded path is part " +
                        "of the profile's compatibility signature, so the compatibility check still refuses this " +
                        "profile until it is re-authored against the current body.",
                        partId,
                        detail: "reason=recorded-target-unresolved; recorded=" + recordedPath +
                                "; installer=" + installerPath));
                }

                return FinishTarget(avatarRoot, explicitRenderer, partId, installerPath, issues);
            }

            if (hasRecordedPath)
            {
                if (!recordedResolved)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "The renderer path recorded by the profile ('" + recordedPath + "') does not resolve to " +
                        "a Renderer under the avatar root, and the installer names no explicit target. Assign " +
                        "the target renderer explicitly, or re-author the profile against this body.",
                        partId,
                        detail: "reason=missing-target-renderer; recorded=" + recordedPath +
                                "; installer=" + installerPath));
                    return null;
                }

                return FinishTarget(avatarRoot, recordedRenderer, partId, installerPath, issues);
            }

            issues.Add(ValidationIssue.Error(
                ApaErrorCode.TargetRendererNotFound,
                ApaIssuePhase.Compatibility,
                "Installer on '" + installer.transform.name + "' names no target renderer and its profile " +
                "recorded no renderer path, so there is no evidence of which body this part was authored " +
                "against. Assign the target renderer explicitly, or re-author the profile against this body.",
                partId,
                detail: "reason=missing-target-renderer; installer=" + installerPath));
            return null;
        }

        /// <summary>
        /// Applies the avatar-ownership rule to a resolved renderer.
        /// </summary>
        /// <remarks>
        /// A target outside the avatar root records an absolute scene path, which stops resolving the moment the
        /// avatar is moved or cloned, and its bones cannot carry a root-relative identity. It is refused rather
        /// than being silently used, because a build that welds into it will produce an avatar that detaches
        /// from its own geometry as soon as NDMF relocates the clone.
        /// </remarks>
        private static ResolvedTarget FinishTarget(
            GameObject avatarRoot,
            Renderer renderer,
            string partId,
            string installerPath,
            List<ValidationIssue> issues)
        {
            var root = avatarRoot.transform;
            if (renderer.transform != root && !renderer.transform.IsChildOf(root))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target renderer '" + renderer.name + "' is not inside the avatar root. A target outside " +
                    "the avatar has no avatar-root-relative identity and no stable bone identity, so the parts " +
                    "welded to it would not survive the build clone. Move the renderer under the avatar, or move " +
                    "the installer to the avatar the renderer belongs to.",
                    partId,
                    detail: "reason=target-outside-avatar-root; installer=" + installerPath));
                return null;
            }

            return new ResolvedTarget(renderer, MeshSnapshotFactory.RelativePath(root, renderer.transform));
        }

        private static bool TryResolveRenderer(Transform root, string path, out Renderer renderer)
        {
            renderer = null;
            if (!ApaAvatarPath.HasIdentity(path)) return false;

            // The root token is resolved from the avatar root transform itself: Transform.Find reserves ".",
            // so passing the token through would resolve nothing.
            var found = ApaAvatarPath.IsRoot(path) ? root : root.Find(path);
            if (found == null) return false;

            renderer = found.GetComponent<Renderer>();
            return renderer != null;
        }

        /// <summary>
        /// Captures the base, the parts, and the compatibility evidence for one target group.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The group key is attached to the context, which is what makes the generated mesh name and the group
        /// diagnostics unique per group. A legacy caller passes an empty key and gets exactly the M2 context.
        /// </para>
        /// <para>
        /// <b>One issue stream per group.</b> Everything this method captures or checks belongs to the group it
        /// is building, so it is collected locally and appended to the shared report once, tagged with the group
        /// key. A group that fails reports its own diagnostics with the same tag the successful groups' planning
        /// diagnostics carry, so a report that spans several groups never states one condition twice.
        /// </para>
        /// <para>
        /// <b>One explicit compatibility pass per installer.</b> Each installer's own captured signature is
        /// compared with the resolved base here, anchored on that installer's part id. The context is then marked
        /// <see cref="ValidationContext.CompatibilityVerified"/> so the validator does not run the same comparison
        /// again through an arbitrary representative signature.
        /// </para>
        /// <para>
        /// <b>The target armature is resolved before anything is captured (M10)</b>, because it decides the scope
        /// every body bone path is recorded in. A group whose installers disagree about it, or whose body is
        /// skinned and selects none, blocks rather than producing a bone table nobody can compare.
        /// </para>
        /// </remarks>
        private static ValidationContext BuildGroupContext(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> installers,
            Renderer targetRenderer,
            string groupKey,
            ApaNumericPolicy policy,
            bool allowPostMergePartArmatureScope,
            List<ValidationIssue> issues)
        {
            var groupIssues = new List<ValidationIssue>();

            var targetArmature = ResolveGroupTargetArmature(avatarRoot, targetRenderer, installers, groupIssues);

            var baseSnapshot = CaptureBase(avatarRoot, targetRenderer, targetArmature, policy, groupIssues);
            if (baseSnapshot == null)
            {
                AppendTagged(issues, groupIssues, groupKey);
                return null;
            }

            var partSnapshots = CaptureParts(
                avatarRoot,
                targetRenderer,
                targetArmature,
                installers,
                policy,
                allowPostMergePartArmatureScope,
                groupIssues);
            var ordered = ValidationContext.SortParts(partSnapshots);

            ValidateInstallerCompatibility(baseSnapshot, ordered, policy, installers, groupIssues);

            if (groupIssues.Exists(issue => issue.IsBlocking))
            {
                AppendTagged(issues, groupIssues, groupKey);
                return null;
            }

            AppendTagged(issues, groupIssues, groupKey);

            // The context carries no representative signature: every installer was checked against the base with
            // its own signature above, which is strictly more than one representative could prove, and there is no
            // arbitrary "first captured signature" left to anchor a second, redundant comparison on.
            return new ValidationContext(baseSnapshot, ordered, policy, null, groupKey, compatibilityVerified: true);
        }

        /// <summary>
        /// Resolves the one target armature a group's bone identities are scoped to, or reports why there is not
        /// exactly one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every installer in a group shares one base body, so they must share one target armature: the body's
        /// bones are recorded once, and two installers that name different armatures are one assembly making two
        /// different claims about which body bone is which joint. Two declared paths that differ therefore block,
        /// and a declared path that does not resolve blocks.
        /// </para>
        /// <para>
        /// When no installer declares one, the body only needs a scope if it is actually skinned. A body renderer
        /// with no bone list has no bone identity to scope, so it is left alone; a skinned one blocks with the one
        /// action that fixes it (select both armatures in the Part Authoring window).
        /// </para>
        /// </remarks>
        private static Transform ResolveGroupTargetArmature(
            GameObject avatarRoot,
            Renderer targetRenderer,
            IReadOnlyList<AvatarPartInstaller> installers,
            List<ValidationIssue> issues)
        {
            var root = avatarRoot != null ? avatarRoot.transform : null;
            var declared = string.Empty;
            var declaredBy = string.Empty;

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                var bones = installer != null && installer.Profile != null
                    ? installer.Profile.BonesOrNull
                    : null;
                var path = bones != null ? bones.TargetArmaturePath : string.Empty;
                if (!ApaAvatarPath.HasIdentity(path)) continue;

                var partId = PartIdOf(installer);
                if (declared.Length == 0)
                {
                    declared = path;
                    declaredBy = partId;
                    continue;
                }

                if (string.Equals(declared, path, StringComparison.Ordinal)) continue;

                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "Two installers in this target group select different target armatures ('" + declared +
                    "' and '" + path + "'). Every part welded to one body shares that body's bone identities, " +
                    "so the whole group must select the same target armature. Select the same armature for " +
                    "every part in the Part Authoring window.",
                    partId,
                    detail: "reason=conflicting-target-armature-paths; first=" + declared + "; firstPart=" +
                            declaredBy + "; conflicting=" + path));
                return null;
            }

            if (declared.Length == 0)
            {
                if (!ApaArmatureScope.RequiresScope(targetRenderer)) return null;

                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "The target body is skinned, but no part in this group selects a target armature, so the " +
                    "body's bones have no scope to be recorded in and no part bone can be identified as the " +
                    "same joint. Open the Part Authoring window, select the target armature inside the avatar " +
                    "and the part armature inside the part, then save the profile.",
                    detail: "reason=missing-target-armature; renderer=" +
                            MeshSnapshotFactory.RelativePath(root, targetRenderer.transform)));
                return null;
            }

            return ApaArmatureScope.TryResolve(
                root,
                declared,
                "target",
                declaredBy,
                "the avatar root",
                issues,
                out var armature)
                ? armature
                : null;
        }

        /// <summary>
        /// Appends a group's diagnostics to the shared report, tagging each with the group key.
        /// </summary>
        /// <remarks>
        /// The key is appended to the detail rather than replacing the existing reason token, so a test or a tool
        /// that keys on <c>reason=</c> keeps working. An empty key (the legacy single-target entry points) appends
        /// nothing, which keeps their diagnostics byte-identical to M2.
        /// </remarks>
        private static void AppendTagged(
            List<ValidationIssue> destination,
            List<ValidationIssue> groupIssues,
            string groupKey)
        {
            if (destination == null || groupIssues == null || groupIssues.Count == 0) return;

            for (var i = 0; i < groupIssues.Count; i++)
            {
                destination.Add(Tag(groupIssues[i], groupKey));
            }
        }

        private static ValidationIssue Tag(ValidationIssue issue, string groupKey)
        {
            if (issue == null) return null;
            if (string.IsNullOrEmpty(groupKey)) return issue;

            var tag = "group=" + groupKey;
            var detail = string.IsNullOrEmpty(issue.Detail) ? tag : issue.Detail + "; " + tag;

            return new ValidationIssue(
                issue.Code,
                issue.Severity,
                issue.Phase,
                issue.Message,
                issue.PartId,
                issue.SourceIndex,
                issue.SecondaryIndex,
                detail);
        }

        /// <summary>
        /// Runs the compatibility rule once per installer, against that installer's own captured signature.
        /// </summary>
        /// <remarks>
        /// This is the only compatibility comparison the group path performs. Every installer is checked whether
        /// or not another installer already passed, because the rule compares one signature with one base and a
        /// group's installers may legitimately disagree about the hierarchy they recorded. The issues are
        /// re-anchored on the installer's part id so a multi-installer group's report names the part that needs
        /// re-authoring.
        /// </remarks>
        private static void ValidateInstallerCompatibility(
            BaseSnapshot baseSnapshot,
            IReadOnlyList<PartSnapshot> parts,
            ApaNumericPolicy policy,
            IReadOnlyList<AvatarPartInstaller> installers,
            List<ValidationIssue> issues)
        {
            var rule = new CompatibilityRule();
            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                var profile = installer != null ? installer.Profile : null;
                if (profile == null) continue;

                var localIssues = new List<ValidationIssue>();
                rule.Validate(
                    new ValidationContext(baseSnapshot, parts, policy, profile.CompatibilityOrNull),
                    localIssues);

                var partId = PartIdOf(installer);
                for (var j = 0; j < localIssues.Count; j++)
                {
                    var issue = localIssues[j];
                    issues.Add(new ValidationIssue(
                        issue.Code,
                        issue.Severity,
                        issue.Phase,
                        issue.Message,
                        partId,
                        issue.SourceIndex,
                        issue.SecondaryIndex,
                        issue.Detail));
                }
            }
        }

        private static BaseSnapshot CaptureBase(
            GameObject avatarRoot,
            Renderer targetRenderer,
            Transform targetArmature,
            ApaNumericPolicy policy,
            List<ValidationIssue> issues)
        {
            var mesh = targetRenderer is SkinnedMeshRenderer skinned
                ? skinned.sharedMesh
                : targetRenderer.GetComponent<MeshFilter>()?.sharedMesh;

            if (mesh == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target renderer '" + targetRenderer.name + "' has no mesh assigned.",
                    detail: "renderer=" + targetRenderer.name));
                return null;
            }

            // The bone signature is scoped to the selected target armature: a body bone's identity is its path
            // relative to that armature, which is the scope the final bone table merges part bones in.
            var boneSignature = MeshSnapshotFactory.CaptureBoneSignature(targetArmature, targetRenderer);

            var snapshot = MeshSnapshotFactory.Capture(
                mesh,
                boneSignature.Paths,
                out var captureIssues,
                MeshSnapshotFactory.CaptureBoneWorldToLocalMatrices(targetRenderer));
            issues.AddRange(captureIssues);
            if (snapshot == null) return null;

            ApaArmatureScope.ValidateWeightedBoneScopes(
                targetRenderer,
                targetArmature,
                snapshot,
                policy,
                string.Empty,
                "the target body",
                issues);

            if (!MeshSnapshotFactory.TryCaptureTransforms(
                    avatarRoot.transform,
                    targetRenderer.transform,
                    targetRenderer.transform,
                    string.Empty,
                    "the target renderer '" + targetRenderer.name + "'",
                    issues,
                    out var transforms))
            {
                return null;
            }

            var materials = MeshSnapshotFactory.CaptureMaterials(targetRenderer);
            // The avatar root records ApaAvatarPath.Root; the record is diagnostic and is what the authoring
            // capture stores, so it must use the canonical routine rather than a special case here.
            var path = MeshSnapshotFactory.RelativePath(avatarRoot.transform, targetRenderer.transform);

            return new BaseSnapshot(
                snapshot,
                transforms,
                materials,
                path,
                ApaCore.InferUvSemantics(snapshot),
                ApaCore.InferMaterialSemantics(snapshot.SubMeshCount, materials),
                MeshSnapshotFactory.CaptureRendererLocalToWorld(targetRenderer),
                targetArmature != null
                    ? MeshSnapshotFactory.RelativePath(avatarRoot.transform, targetArmature)
                    : string.Empty);
        }

        /// <summary>
        /// Captures every part against the one resolved target renderer.
        /// </summary>
        /// <remarks>
        /// The target renderer is passed in rather than re-resolved per installer. A part snapshot's
        /// source-to-target matrix is what maps its vertices, normals, tangents, and blend shape deltas into the
        /// renderer whose mesh the result replaces; substituting the part's own transform would make the mapping
        /// the identity for a part that is not at the target's origin, which is wrong in exactly the way that
        /// detaches a part at build time.
        /// </remarks>
        private static List<PartSnapshot> CaptureParts(
            GameObject avatarRoot,
            Renderer targetRenderer,
            Transform targetArmature,
            IReadOnlyList<AvatarPartInstaller> installers,
            ApaNumericPolicy policy,
            bool allowPostMergePartArmatureScope,
            List<ValidationIssue> issues)
        {
            var result = new List<PartSnapshot>(installers.Count);

            // One informational line per profile whose identity had to be derived, so a legacy profile is
            // explained once instead of once per diagnostic that names the part.
            var reportedDerivedIds = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];

                if (installer.Profile == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.PartProfileIncompatible,
                        ApaIssuePhase.Configuration,
                        "Installer on '" + installer.gameObject.name + "' has no profile assigned.",
                        detail: "installer=" +
                                MeshSnapshotFactory.RelativePath(avatarRoot.transform, installer.transform)));
                    continue;
                }

                var profile = installer.Profile;
                ReportDerivedPartId(profile, reportedDerivedIds, issues);

                // The profile is a shared authoring asset. Reading it must not materialize missing nested
                // objects or rewrite its schema version, so the non-mutating accessors are used throughout and
                // the migration check is read-only.
                var migrationMessage = string.Empty;
                if (!profile.TryMigrate(out migrationMessage))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.UnknownProfileSchema,
                        ApaIssuePhase.Configuration,
                        migrationMessage,
                        PartIdOf(installer),
                        detail: "schemaVersion=" + profile.SchemaVersion));
                    continue;
                }

                var partRoot = installer.ResolvePartRoot();
                var partRenderer = partRoot != null ? partRoot.GetComponentInChildren<Renderer>(true) : null;
                if (partRenderer == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Part root '" + (partRoot != null ? partRoot.name : "(null)") +
                        "' has no Renderer to take geometry from.",
                        PartIdOf(installer),
                        detail: "partRoot=" + (partRoot != null ? partRoot.name : "(null)")));
                    continue;
                }

                var partMesh = partRenderer is SkinnedMeshRenderer partSkinned
                    ? partSkinned.sharedMesh
                    : partRenderer.GetComponent<MeshFilter>()?.sharedMesh;

                if (partMesh == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Part renderer '" + partRenderer.name + "' has no mesh assigned.",
                        PartIdOf(installer),
                        detail: "renderer=" + partRenderer.name));
                    continue;
                }

                // The part's bones are recorded relative to the armature the author selected for this part, which
                // is what makes a part bone and a body bone with the same relative path one joint. A part whose
                // renderer carries bones must select one; a part with no skeleton at all needs no scope.
                var bones = profile.BonesOrNull;
                Transform partArmature = null;
                if (ApaArmatureScope.RequiresScope(partRenderer))
                {
                    var partArmaturePath = bones != null ? bones.PartArmaturePath : string.Empty;
                    if (!ApaArmatureScope.TryResolvePartCaptureScope(
                            partRoot != null ? partRoot.transform : null,
                            partArmaturePath,
                            targetArmature,
                            partRenderer,
                            policy,
                            allowPostMergePartArmatureScope,
                            PartIdOf(installer),
                            issues,
                            out partArmature))
                    {
                        continue;
                    }
                }

                var partBoneSignature = MeshSnapshotFactory.CaptureBoneSignature(partArmature, partRenderer);

                var meshSnapshot = MeshSnapshotFactory.Capture(
                    partMesh,
                    partBoneSignature.Paths,
                    out var captureIssues,
                    MeshSnapshotFactory.CaptureBoneWorldToLocalMatrices(partRenderer));
                issues.AddRange(captureIssues);
                if (meshSnapshot == null) continue;

                ApaArmatureScope.ValidateWeightedBoneScopes(
                    partRenderer,
                    partArmature,
                    meshSnapshot,
                    policy,
                    PartIdOf(installer),
                    "part '" + PartIdOf(installer) + "'",
                    issues);

                if (!MeshSnapshotFactory.TryCaptureTransforms(
                        avatarRoot.transform,
                        partRenderer.transform,
                        targetRenderer.transform,
                        PartIdOf(installer),
                        "part '" + PartIdOf(installer) + "'",
                        issues,
                        out var transforms))
                {
                    continue;
                }

                var materials = MeshSnapshotFactory.CaptureMaterials(partRenderer);

                var uvSemantics = profile.UvSemantics.Length > 0
                    ? profile.UvSemantics
                    : ApaCore.InferUvSemantics(meshSnapshot);

                var materialSemantics = profile.MaterialSemantics.Length > 0
                    ? profile.MaterialSemantics
                    : ApaCore.InferMaterialSemantics(meshSnapshot.SubMeshCount, materials);

                var policySnapshot = PartPolicySnapshot.FromProfile(profile);
                var identity = profile.IdentityOrNull;

                var orderingKey = new PartOrderingKey(
                    policySnapshot.ConflictPriority,
                    installer.ResolveSlot(),
                    PartIdOf(installer),
                    identity != null ? identity.DisplayName : string.Empty,
                    MeshSnapshotFactory.RelativePath(avatarRoot.transform, installer.transform));

                var removal = profile.RemovalOrNull;
                if (removal != null && removal.HasCorruptStorage)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidTriangleAddress,
                        ApaIssuePhase.Removal,
                        "The removal profile has mismatched submesh and triangle address arrays. " +
                        "The missing address component cannot be reconstructed without guessing; re-author " +
                        "the removal selection.",
                        PartIdOf(installer),
                        detail: "reason=corrupt-removal-storage"));
                    continue;
                }

                result.Add(new PartSnapshot(
                    PartIdOf(installer),
                    identity != null ? identity.DisplayName : string.Empty,
                    installer.ResolveSlot(),
                    orderingKey,
                    meshSnapshot,
                    transforms,
                    uvSemantics,
                    materialSemantics,
                    removal != null
                        ? removal.RemovedTriangles.Addresses
                        : Array.Empty<RemovedTriangleAddress>(),
                    profile.SeamOrNull,
                    materials,
                    policySnapshot,
                    partRenderer));
            }

            return result;
        }

        /// <summary>
        /// The stable part identity of an installer: the profile's stored id, or the deterministic id derived from
        /// the profile asset's GUID (M11).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every read of a part identity in this builder goes through here, so the snapshot, the ordering key, and
        /// every diagnostic name a part the same way. Resolving in some places and not others would let a legacy
        /// profile sort and report under two different identities within one build.
        /// </para>
        /// <para>
        /// The derivation is a pure read of the profile asset; nothing is written. See
        /// <see cref="ApaPartIdentityResolver"/>.
        /// </para>
        /// </remarks>
        private static string PartIdOf(AvatarPartInstaller installer)
        {
            return ApaPartIdentityResolver.ResolvePartId(installer);
        }

        /// <summary>
        /// Reports, once per profile, that a part identity was derived rather than read from the asset.
        /// </summary>
        /// <remarks>
        /// Informational, not a defect: the part installs and the build is reproducible. It is reported so that the
        /// one action which makes the identity explicit — saving the profile, or repairing it from the installer
        /// inspector — is discoverable, and so a report does not show an id the author cannot find in the asset.
        /// </remarks>
        private static void ReportDerivedPartId(
            ApaPartProfile profile,
            HashSet<string> reportedIds,
            List<ValidationIssue> issues)
        {
            if (issues == null || reportedIds == null) return;
            if (!ApaPartIdentityResolver.NeedsRepair(profile)) return;

            var partId = ApaPartIdentityResolver.ResolvePartId(profile);
            if (string.IsNullOrEmpty(partId)) return;
            if (!reportedIds.Add(partId)) return;

            issues.Add(ValidationIssue.Info(
                ApaErrorCode.PartIdDerived,
                ApaIssuePhase.Configuration,
                "This profile carries no stored part id, so the build derived one from the profile asset's GUID: '" +
                partId + "'. The derived id is stable across preview, validation, sorting, the build, and a domain " +
                "reload, so the part installs normally. Save the profile once in the Part Authoring window, or " +
                "press Repair Part Id in the installer inspector, to store the same id on the asset.",
                partId,
                detail: "reason=" + ApaPartIdentityResolver.DerivedReason + "; partId=" + partId));
        }

        /// <summary>An installer whose target renderer was resolved successfully.</summary>
        private sealed class ResolvedInstaller
        {
            public AvatarPartInstaller Installer { get; }
            public ResolvedTarget Target { get; }

            public ResolvedInstaller(AvatarPartInstaller installer, ResolvedTarget target)
            {
                Installer = installer;
                Target = target;
            }
        }

        /// <summary>A resolved target renderer and its avatar-root-relative key.</summary>
        private sealed class ResolvedTarget
        {
            public Renderer Renderer { get; }
            public string Path { get; }

            public ResolvedTarget(Renderer renderer, string path)
            {
                Renderer = renderer;
                Path = path ?? string.Empty;
            }
        }
    }
}

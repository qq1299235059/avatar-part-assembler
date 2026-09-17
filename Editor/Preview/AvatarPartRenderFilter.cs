using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// The NDMF Scene View preview filter for Avatar Part Assembler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter answers one question for NDMF: <i>which target groups does an active
    /// <c>AvatarPartInstaller</c> affect, and what must the proxy for each of them draw?</i> Everything else is
    /// delegated to the same deterministic core the build uses, so a preview cannot drift from a build by being
    /// implemented twice (section 25, section 41).
    /// </para>
    /// <para>
    /// <b>One group per target renderer.</b> Each request <see cref="ApaPreviewDiscovery.DiscoverGroups"/>
    /// produces becomes one <c>RenderGroup</c> whose renderers are the group's target plus the part renderers the
    /// build replaces. A part belongs to exactly one group, so no part's geometry is assembled into two groups'
    /// meshes, and the node disables the part proxies so a replaced part is not drawn twice.
    /// </para>
    /// <para>
    /// <b>Only proxies are written.</b> The node writes the assembled mesh, materials, bones, and blend shape
    /// weights onto the proxy renderer and toggles the replaced parts' proxies off. The original renderers' mesh,
    /// materials, hierarchy, and profile assets are never written to, which is what makes deleting the installer
    /// restore the avatar exactly (section 25, section 43.10).
    /// </para>
    /// <para>
    /// <b>Blocked means absent.</b> When the current inputs are invalid, the filter returns no group for that
    /// renderer. NDMF then has no proxy for it, so the original body renders unchanged and the previous preview
    /// stops being drawn — there is no state in which a stale successful preview survives an invalid input. The
    /// reason is reported through <see cref="ApaPreviewDiagnostics"/>.
    /// </para>
    /// <para>
    /// <b>Registration.</b> NDMF discovers filters through a build pass's <c>PreviewingWith</c> declaration,
    /// which <c>ApaNdmfPlugin.Configure</c> makes on the Transforming assembly pass; see
    /// <see cref="ApaPreviewRegistration"/>.
    /// </para>
    /// </remarks>
    public sealed class AvatarPartRenderFilter : IRenderFilter
    {
        /// <summary>
        /// False: this filter replaces geometry, it does not reveal a renderer the author hid. A group whose
        /// renderer is disabled or inactive is therefore skipped by the pipeline rather than forced visible,
        /// which is what "the preview shows what the avatar shows" requires.
        /// </summary>
        public bool CanEnableRenderers => false;

        /// <summary>
        /// False: every group this filter returns holds exactly one target renderer plus the part renderers the
        /// build replaces, and each of those parts is only ever removed together with the target it was welded
        /// into. There is no group member whose absence could change the meaning of the others.
        /// </summary>
        public bool StrictRenderGroup => false;

        /// <summary>The switches shown in NDMF's preview configuration window.</summary>
        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return ApaPreviewToggles.MainPreview;
            yield return ApaPreviewToggles.DebugOverlay;
        }

        /// <inheritdoc />
        public bool IsEnabled(ComputeContext context)
        {
            var enabled = context != null && context.Observe(ApaPreviewToggles.MainPreview.IsEnabled);
            if (!enabled)
            {
                // With the preview switched off there is nothing to diagnose; leaving stale reports behind would
                // make the Scene View overlay describe a state that is no longer being previewed.
                ApaPreviewDiagnostics.ClearAll();
            }

            return enabled;
        }

        /// <inheritdoc />
        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            if (context == null) return groups.ToImmutable();

            // A deleted avatar never comes back to be seen (and cleared) by the loop below, so its report has to
            // be swept explicitly or it stays in the store for the rest of the session.
            ApaPreviewDiagnostics.PruneOrphans();

            // One renderer may be reachable from two avatar roots when avatars are nested. NDMF drops *every*
            // group of a filter that returns the same renderer twice, so the duplicates are filtered here.
            var claimed = new HashSet<Renderer>();

            var roots = CollectAvatarRoots(context);
            for (var i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (root == null) continue;

                try
                {
                    AddGroupsForRoot(context, root, claimed, groups);
                }
                catch (Exception e)
                {
                    // An exception escaping here would fault the whole preview build, taking every other filter's
                    // work with it. It is reported as a diagnostic for this avatar instead.
                    ApaPreviewDiagnostics.ReportInternalFailure(
                        "GetTargetGroups for '" + ApaPreviewDiagnosticKey.For(root) + "'", e);
                }
            }

            return groups.ToImmutable();
        }

        /// <inheritdoc />
        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            // The whole path is guarded, because an exception escaping Instantiate faults NDMF's build task and
            // stops *every* filter's preview, not just this one, on every repaint (ProxySession.OnPreCull logs
            // and discards the generation). A node that writes nothing is the safe stand-in, and the failure is
            // reported once per distinct message.
            ApaPreviewRequest request = null;
            try
            {
                try
                {
                    request = group != null ? group.GetData<ApaPreviewRequest>() : null;
                }
                catch (InvalidCastException e)
                {
                    // The group belongs to another filter's convention, or to an older generation of this one.
                    ApaPreviewDiagnostics.ReportInternalFailure("RenderGroup data", e);
                }

                if (request == null)
                {
                    return Task.FromResult<IRenderFilterNode>(new ApaPreviewEmptyNode(
                        "the render group carried no APA preview request"));
                }

                // The node's own observations are registered on the controller's context: this is the context whose
                // invalidation reaches the pipeline, so a change to any input re-discovers the target set.
                ApaPreviewInputObserver.Observe(context, request.AvatarRoot, request.AllInstallers, request.TargetRenderer);

                return Task.FromResult(ApaPreviewNode.Create(request));
            }
            catch (Exception e)
            {
                ApaPreviewDiagnostics.ReportInternalFailure(
                    "Instantiate for '" + (request != null ? request.DiagnosticKey.ToString() : "unknown group") + "'",
                    e);

                return Task.FromResult<IRenderFilterNode>(new ApaPreviewEmptyNode(
                    "preview node construction failed: " + e.GetType().Name,
                    RenderAspects.Mesh | RenderAspects.Material | RenderAspects.Shapes,
                    request));
            }
        }

        private static void AddGroupsForRoot(
            ComputeContext context,
            GameObject root,
            HashSet<Renderer> claimed,
            ImmutableList<RenderGroup>.Builder groups)
        {
            if (!context.ActiveInHierarchy(root))
            {
                ApaPreviewDiagnostics.ClearAvatar(root);
                return;
            }

            // The scan itself is the observation that notices an installer being added or removed: NDMF's
            // component queries register a component-structure listener on the hierarchy. Its result is used only
            // for that side effect; the list discovery reads is collected separately, because it must be in a
            // deterministic order that refresh can reproduce exactly.
            context.GetComponentsInChildren<AvatarPartInstaller>(root, true);
            var installerList = ApaPreviewDiscovery.CollectAllInstallers(root);
            var requests = ApaPreviewDiscovery.DiscoverGroups(root, installerList, ApaNumericPolicy.Default);

            // Observation happens once per root, with every target renderer the groups resolved, so a failure to
            // capture still leaves every input that could make the next attempt succeed observed.
            ApaPreviewInputObserver.Observe(context, root, installerList, TargetsOf(requests));

            if (requests.Count == 0)
            {
                // No active installer: the avatar is not affected at all, so there is nothing to say about it.
                ApaPreviewDiagnostics.ClearAvatar(root);
                return;
            }

            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];

                ApaPreviewDiagnostics.ReportRequest(request);

                if (!request.IsRenderable || request.TargetRenderer == null) continue;

                // A renderer may be reachable from two avatar roots when avatars are nested, and a filter that
                // returns the same renderer in two groups is dropped by NDMF entirely, so the whole group's
                // renderer set is filtered through one claim set.
                if (!claimed.Add(request.TargetRenderer)) continue;

                // The group's renderer set cannot fail to be expressed: the target is non-null here and every
                // consumed renderer that cannot be proxied is skipped rather than rejected. There is therefore
                // no "claimed but not grouped" state to unwind, and no claim to release.
                AddGroup(request, claimed, groups);
            }
        }

        /// <summary>
        /// Adds one target group's render group: the target renderer plus the part renderers the build replaces.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The group carries the source geometry too.</b> The build assembles the part into the target and then
        /// destroys the part renderers, so the uploaded avatar draws the part once. A preview that proxied only
        /// the target would leave the original part renderer drawing on top of the assembled mesh and show the
        /// part twice. The group therefore contains every renderer the build would consume, and the node disables
        /// those proxies — the same visibility the build produces, expressed with the proxy state NDMF resets and
        /// re-applies every frame.
        /// </para>
        /// <para>
        /// A consumed renderer NDMF cannot proxy (anything that is not a MeshRenderer or a
        /// SkinnedMeshRenderer) is left out and reported as a note rather than silently producing the doubled
        /// geometry the group exists to prevent.
        /// </para>
        /// <para>
        /// <b>Adding a group cannot fail, so this method returns nothing.</b> It used to return a bool that was
        /// always <c>true</c>, and the call site carried a failure branch — release the claim, report an internal
        /// failure — that no input could reach. A branch that cannot run is worse than no branch: it reads like
        /// a state the preview handles, so nobody notices that the unwind it performs is untested and
        /// unreachable. The conditions that could have justified it are all handled where they happen: the
        /// target is checked for null before the claim, and an unproxyable consumed renderer is reported and
        /// skipped rather than failing the group.
        /// </para>
        /// </remarks>
        private static void AddGroup(
            ApaPreviewRequest request,
            HashSet<Renderer> claimed,
            ImmutableList<RenderGroup>.Builder groups)
        {
            var renderers = new List<Renderer> { request.TargetRenderer };

            for (var i = 0; i < request.ConsumedRenderers.Count; i++)
            {
                var renderer = request.ConsumedRenderers[i];
                if (renderer == null || renderer == request.TargetRenderer) continue;
                if (claimed.Contains(renderer)) continue;

                if (!ApaPreviewDiscovery.IsSupportedProxyRenderer(renderer))
                {
                    ApaPreviewDiagnostics.Report(request, new[]
                    {
                        "Renderer '" + ApaPreviewDiscovery.DescribePath(renderer.transform) + "' is consumed by " +
                        "this assembly (the build removes it) but is a " + renderer.GetType().Name +
                        ", which the preview system cannot proxy, so its geometry is still drawn in the Scene " +
                        "View. The build is unaffected."
                    });
                    continue;
                }

                claimed.Add(renderer);
                renderers.Add(renderer);
            }

            groups.Add(RenderGroup.For(renderers).WithData(request, ApaPreviewRequestComparer.Instance));
        }

        /// <summary>The target renderers of the groups that resolved one, in discovery order.</summary>
        private static List<Renderer> TargetsOf(IReadOnlyList<ApaPreviewRequest> requests)
        {
            var targets = new List<Renderer>(requests.Count);
            for (var i = 0; i < requests.Count; i++)
            {
                var target = requests[i].TargetRenderer;
                if (target != null && !targets.Contains(target)) targets.Add(target);
            }

            return targets;
        }

        /// <summary>
        /// Returns the avatar roots in a deterministic order.
        /// </summary>
        /// <remarks>
        /// NDMF returns the roots in scene order, which is stable for a given scene but not a documented contract.
        /// The sort makes the order a property of the scene contents rather than of scene internals, so two
        /// rebuilds over an unchanged scene produce the same group sequence. The order also decides which root
        /// claims a shared renderer, so it must be a total one across scenes:
        /// <see cref="ApaPreviewDiscovery.CompareAvatarRoots"/> compares scene and object identity, never the
        /// hierarchy path alone.
        /// </remarks>
        private static List<GameObject> CollectAvatarRoots(ComputeContext context)
        {
            var roots = context.GetAvatarRoots();
            var result = new List<GameObject>(roots.Count);
            for (var i = 0; i < roots.Count; i++)
            {
                if (roots[i] != null) result.Add(roots[i]);
            }

            result.Sort(ApaPreviewDiscovery.CompareAvatarRoots);

            return result;
        }
    }
}

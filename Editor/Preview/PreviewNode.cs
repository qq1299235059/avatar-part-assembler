using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// The preview node for one avatar body: it owns the generated mesh and writes it onto the proxy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lifecycle.</b> NDMF creates a node from <c>IRenderFilter.Instantiate</c> when a render group first
    /// appears, calls <c>OnFrame</c> for every proxy pair on every frame, offers <c>Refresh</c> when the pipeline
    /// is rebuilt, and calls <c>Dispose</c> when the last pipeline referencing it goes away. Each of those is
    /// implemented here with one rule: a node never renders something its inputs do not justify.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>Instantiate</c> takes a lease on a cached generated mesh, or on a failed build. A failed build produces
    /// a node that writes nothing, which leaves the proxy showing the original body — the correct "preview
    /// blocked" state — while the diagnostics carry the reason.
    /// </description></item>
    /// <item><description>
    /// <c>OnFrame</c> re-applies the mesh, materials, bones, bounds, and blend shape weights, because NDMF resets
    /// all of them from the original at the start of every frame.
    /// </description></item>
    /// <item><description>
    /// <c>Refresh</c> re-registers the node's observations on the new compute context and then decides, from live
    /// values rather than from a guess, whether the node may be reused. Reuse requires that the structural inputs
    /// are unchanged and that a fresh capture of the live scene still produces the same fingerprint. When it does
    /// not, the node returns a replacement built from the live capture — never <c>null</c>, because NDMF answers
    /// <c>null</c> by rebuilding from the same group data the refresh just rejected.
    /// </description></item>
        /// <item><description>
        /// <c>Dispose</c> releases the lease, unregisters the debug entry, and destroys the shadow bones this node
        /// created. It does not destroy the mesh: the cache owns it, and a replaced node may still be drawn by the
        /// pipeline generation that is being retired.
        /// </description></item>
    /// </list>
    /// <para>
    /// <b>Nothing escapes into the pipeline.</b> Node construction runs inside NDMF's build task; an exception
    /// thrown from <c>IRenderFilter.Instantiate</c> faults that task and stops every filter's preview, not just
    /// this one. <see cref="Create"/> therefore catches everything, releases the cache lease it took, reports one
    /// deduplicated internal diagnostic, and returns <see cref="ApaPreviewEmptyNode"/>, which draws nothing and
    /// leaves the original body visible.
    /// </para>
    /// <para>
    /// <b>What is never touched.</b> The original renderer's <c>sharedMesh</c>, <c>sharedMaterials</c>, bones,
    /// transform, and every asset behind them. Only the proxy renderer handed in by the pipeline is written to.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewNode : IRenderFilterNode
    {
        private readonly GameObject _avatarRoot;
        private readonly Renderer _targetRenderer;
        private readonly string _groupKey;
        private readonly ApaNumericPolicy _policy;
        private readonly IReadOnlyList<AvatarPartInstaller> _allInstallers;
        private readonly IReadOnlyList<Renderer> _consumedRenderers;
        private readonly string _fingerprint;
        private readonly string _structuralKey;
        private readonly ApaPreviewLease _lease;
        private readonly ApaPreviewBoneMap _boneMap;
        private readonly IReadOnlyList<ApaPreviewBlendShapeBinding> _shapeBindings;
        private readonly ApaPreviewDebugData _debugData;
        private readonly List<GameObject> _shadowObjects;

        /// <summary>
        /// The request this node was built from. It is kept for one purpose: a node that fails later can hand it to
        /// an <see cref="ApaPreviewEmptyNode"/>, which is then able to retry from a fresh capture of the live
        /// scene. Only the avatar root, the group key, and the numeric policy are read from it for that.
        /// </summary>
        private readonly ApaPreviewRequest _request;

        /// <summary>
        /// True when the assembly succeeded but its result cannot be expressed on the target renderer type (a
        /// skinned result on a non-skinned target). Such a node draws nothing and says why.
        /// </summary>
        private readonly bool _cannotRepresent;

        private bool _disposed;

        /// <summary>
        /// Which aspects of the proxy this node changed. Set to everything on construction, because a node that
        /// was just created is responsible for the whole renderer state, and to zero when a refresh proves the
        /// output is unchanged so that downstream nodes are not rebuilt for nothing.
        /// </summary>
        public RenderAspects WhatChanged { get; private set; }

        /// <summary>The fingerprint of the inputs this node was built from.</summary>
        public string Fingerprint => _fingerprint;

        /// <summary>The generated mesh this node draws, or null when the build was blocked.</summary>
        public Mesh PreviewMesh => _lease != null ? _lease.Mesh : null;

        /// <summary>True when this node has a generated mesh to draw.</summary>
        public bool IsRendering => !_disposed && !_cannotRepresent && _lease != null && _lease.IsValid;

        private ApaPreviewNode(
            ApaPreviewRequest request,
            ApaPreviewLease lease,
            ApaPreviewBoneMap boneMap,
            IReadOnlyList<ApaPreviewBlendShapeBinding> shapeBindings,
            ApaPreviewDebugData debugData,
            bool cannotRepresent,
            List<GameObject> shadowObjects)
        {
            _avatarRoot = request.AvatarRoot;
            _targetRenderer = request.TargetRenderer;
            _groupKey = request.GroupKey;
            _policy = request.NumericPolicy;
            _allInstallers = request.AllInstallers;
            _consumedRenderers = request.ConsumedRenderers;
            _request = request;
            _fingerprint = request.Fingerprint;
            _structuralKey = request.StructuralKey;
            _lease = lease;
            _boneMap = boneMap ?? ApaPreviewBoneMap.Empty;
            _shapeBindings = shapeBindings ?? new List<ApaPreviewBlendShapeBinding>();
            _debugData = debugData;
            _cannotRepresent = cannotRepresent;
            _shadowObjects = shadowObjects != null ? shadowObjects : new List<GameObject>();

            WhatChanged = RenderAspects.Mesh | RenderAspects.Material | RenderAspects.Shapes;
        }

        /// <summary>
        /// Builds a node for a discovered request: assembles through the core, takes a cache lease, and resolves
        /// the bone table and blend shape sources.
        /// </summary>
        /// <remarks>
        /// <b>This method never throws.</b> It runs on NDMF's node-construction path, where an escaping exception
        /// faults the pipeline's build task and takes every filter's preview down with it. A failure after the
        /// cache lease has been taken releases the lease — a leased entry is never evicted, so a leaked lease pins
        /// a generated mesh until process teardown — reports one deduplicated internal diagnostic, and returns an
        /// <see cref="ApaPreviewEmptyNode"/>, which draws nothing and leaves the original body visible.
        /// </remarks>
        public static IRenderFilterNode Create(ApaPreviewRequest request)
        {
            if (request == null)
            {
                // Not reachable from Instantiate (which answers this case first), but Create is public and must
                // not throw into a pipeline under any input.
                ApaPreviewDiagnostics.ReportInternalFailure(
                    "Preview node construction was called without a request", null);

                return new ApaPreviewEmptyNode("no preview request was supplied");
            }

            ApaPreviewLease lease = null;
            List<GameObject> shadowObjects = new List<GameObject>();
            try
            {
                lease = BuildThroughCore(request);

                ApaPreviewBoneMap boneMap = ApaPreviewBoneMap.Empty;
                List<ApaPreviewBlendShapeBinding> shapeBindings = new List<ApaPreviewBlendShapeBinding>();
                var cannotRepresent = false;

                if (lease.IsValid)
                {
                    var partRenderers = ApaPreviewDiscovery.BuildPartRendererMap(request.Installers);
                    boneMap = ApaPreviewBoneMap.Resolve(lease.Plan, request.AvatarRoot, request.Installers);

                    // The authoring hierarchy has not merged the part armature into the avatar's, so part-side
                    // bones in the table would ignore avatar motion and the drawn mesh would drift away from the
                    // posed skeleton. Shadow bones parented under the avatar counterparts reproduce the build's
                    // merge, and the hierarchy makes the preview follow the skeleton without per-frame work.
                    boneMap = ApaPreviewShadowBones.Attach(boneMap, shadowObjects);

                    shapeBindings = ApaPreviewBlendShapeMap.Build(lease.Plan, request.TargetRenderer, partRenderers);

                    ReportUnresolvedBones(request, boneMap);
                    ReportMaterialMismatch(request, lease);

                    // A skinned result cannot be shown on a non-skinned target: a MeshRenderer has no bones and no
                    // bind poses, so the assembled mesh would render in rest pose while the build would not. The
                    // preview refuses to show a result it cannot show faithfully, and reports the reason.
                    if (lease.Plan != null && lease.Plan.RequiresSkinning && !(request.TargetRenderer is SkinnedMeshRenderer))
                    {
                        cannotRepresent = true;
                        ReportUnrepresentableSkinning(request);
                    }
                }
                else
                {
                    ReportBuildFailure(request, lease);
                }

                var debugData = ApaPreviewDebugData.Create(request, lease.Plan, lease.Issues, boneMap);

                var node = new ApaPreviewNode(
                    request, lease, boneMap, shapeBindings, debugData, cannotRepresent, shadowObjects);

                if (debugData != null) ApaPreviewDebugOverlay.Register(request.TargetRenderer, debugData);

                return node;
            }
            catch (Exception e)
            {
                // The lease is released before any node exists, because nothing else can release it: Dispose is
                // only reachable through a constructed node. Shadow bones created along the way die with it.
                if (lease != null) lease.Dispose();
                DestroyShadowBones(shadowObjects);

                ApaPreviewDiagnostics.ReportInternalFailure(
                    "Preview node construction for '" + request.DiagnosticKey + "'", e);

                return new ApaPreviewEmptyNode(
                    "preview node construction failed: " + e.GetType().Name,
                    RenderAspects.Mesh | RenderAspects.Material | RenderAspects.Shapes,
                    request);
            }
        }

        private static ApaPreviewLease BuildThroughCore(ApaPreviewRequest request)
        {
            // The same per-group entry point the build transaction calls. The cache is keyed by the fingerprint of
            // the captured inputs, so two pipelines over equivalent inputs share one generated mesh instead of
            // rebuilding it.
            return ApaPreviewMeshCache.Shared.Acquire(request.Fingerprint, () =>
            {
                if (request.Context == null || request.Planning == null || !request.Planning.Succeeded)
                {
                    return ApaPreviewGeneratedMesh.Failure(ValidationResult.Single(ValidationIssue.Error(
                        ApaErrorCode.InternalError,
                        ApaIssuePhase.Assembly,
                        "The preview node was created without captured inputs, so nothing could be assembled.",
                        detail: "reason=missing-context")));
                }

                var group = new ApaTargetGroupPlan(
                    request.GroupKey, request.TargetRenderer, request.Context, request.Installers);

                var result = TargetGroupAssembly.BuildGroup(group, request.Planning, null);
                return ApaPreviewGeneratedMesh.From(result);
            });
        }

        private static void ReportUnresolvedBones(ApaPreviewRequest request, ApaPreviewBoneMap boneMap)
        {
            if (boneMap == null || !boneMap.HasMissingBones) return;

            var notes = new List<string>();
            for (var i = 0; i < boneMap.MissingPaths.Count && i < 8; i++)
            {
                notes.Add("Bone path '" + boneMap.MissingPaths[i] +
                          "' from the final bone table did not resolve to a transform in this hierarchy.");
            }

            ApaPreviewDiagnostics.Report(ApaPreviewDiagnostics.Build(
                request.DiagnosticKey,
                request.AvatarRoot,
                request.TargetRenderer,
                request.TargetRendererPath,
                request.Issues,
                notes,
                false));
        }

        private static void ReportMaterialMismatch(ApaPreviewRequest request, ApaPreviewLease lease)
        {
            var mesh = lease.Mesh;
            if (mesh == null) return;
            if (lease.Materials.Length == 0 || lease.Materials.Length == mesh.subMeshCount) return;

            ApaPreviewDiagnostics.Report(ApaPreviewDiagnostics.Build(
                request.DiagnosticKey,
                request.AvatarRoot,
                request.TargetRenderer,
                request.TargetRendererPath,
                request.Issues,
                new[]
                {
                    "The assembly produced " + lease.Materials.Length + " material(s) for " +
                    mesh.subMeshCount + " submesh(es); the material list was not applied."
                },
                false));
        }

        private static void ReportBuildFailure(ApaPreviewRequest request, ApaPreviewLease lease)
        {
            var notes = new List<string>
            {
                "The preview assembly produced no mesh, so the original body mesh is shown unchanged."
            };

            ApaPreviewDiagnostics.Report(ApaPreviewDiagnostics.Build(
                request.DiagnosticKey,
                request.AvatarRoot,
                request.TargetRenderer,
                request.TargetRendererPath,
                lease != null ? lease.Issues : ValidationResult.Empty,
                notes,
                true));
        }

        private static void ReportUnrepresentableSkinning(ApaPreviewRequest request)
        {
            ApaPreviewDiagnostics.Report(ApaPreviewDiagnostics.Build(
                request.DiagnosticKey,
                request.AvatarRoot,
                request.TargetRenderer,
                request.TargetRendererPath,
                request.Issues,
                new[]
                {
                    "The assembled result carries skinning (a part contributes bone weights) but the target body " +
                    "renderer '" + (request.TargetRenderer != null
                        ? ApaPreviewDiscovery.DescribePath(request.TargetRenderer.transform)
                        : request.TargetRendererPath) +
                    "' is not a SkinnedMeshRenderer, so a preview proxy cannot show it faithfully. The original " +
                    "body is shown unchanged."
                },
                true));
        }

        /// <inheritdoc />
        public void OnFrame(Renderer original, Renderer proxy)
        {
            if (_disposed || proxy == null || original == null) return;

            // A part renderer this group's assembly replaced. The build destroys it, so a proxy that kept drawing
            // it would show the part twice: once inside the assembled mesh and once as its own renderer. NDMF
            // restores the original's enabled state onto the proxy at the start of every frame, so the hidden
            // state has to be re-applied here rather than written once.
            if (original != _targetRenderer)
            {
                // Only an assembly that is actually being drawn replaces the part. A blocked preview shows the
                // untouched avatar, in which every source renderer still belongs to the picture.
                if (_cannotRepresent || !_lease.IsValid) return;
                if (!IsConsumedRenderer(original)) return;

                proxy.enabled = false;
                return;
            }

            // A result the target renderer type cannot express is never drawn: showing an unskinned rest pose where
            // the build would show a skinned body would be a wrong preview, which is worse than none.
            if (_cannotRepresent) return;

            if (!_lease.IsValid)
            {
                // Blocked: write nothing. NDMF has already restored the original mesh onto the proxy for this
                // frame, so the user sees the untouched body rather than a stale assembled one.
                return;
            }

            ApaPreviewProxyApplier.Apply(
                proxy,
                _lease.Mesh,
                _lease.Materials,
                _boneMap,
                _shapeBindings);
        }

        /// <summary>True when this group's assembly replaced the given renderer.</summary>
        private bool IsConsumedRenderer(Renderer renderer)
        {
            if (renderer == null) return false;

            for (var i = 0; i < _consumedRenderers.Count; i++)
            {
                if (_consumedRenderers[i] == renderer) return true;
            }

            return false;
        }

        /// <inheritdoc />
        public Task<IRenderFilterNode> Refresh(
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context,
            RenderAspects updatedAspects)
        {
            if (_disposed) return Task.FromResult<IRenderFilterNode>(null);

            try
            {
                return Task.FromResult(RefreshCore(proxyPairs, context, updatedAspects));
            }
            catch (Exception e)
            {
                // Like construction, a refresh runs on NDMF's build task: an exception escaping here faults it and
                // stops every filter's preview. The node stops drawing instead.
                ApaPreviewDiagnostics.ReportInternalFailure(
                    "Refreshing the preview node for '" + _structuralKey + "'", e);

                return Task.FromResult<IRenderFilterNode>(new ApaPreviewEmptyNode(
                    "preview node refresh failed: " + e.GetType().Name,
                    RenderAspects.Mesh | RenderAspects.Material | RenderAspects.Shapes,
                    _request));
            }
        }

        private IRenderFilterNode RefreshCore(
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context,
            RenderAspects updatedAspects)
        {
            // Observations are registered on the context of the controller that is being created, not on the one
            // that is being retired: a node that returns itself must keep observing its inputs through the new
            // context, or the next change to them would not invalidate anything.
            ApaPreviewInputObserver.Observe(context, _avatarRoot, _allInstallers, _targetRenderer);

            // A node cannot make NDMF re-discover the target set. The context passed here belongs to the
            // replacement controller (NodeController.Refresh creates it), so invalidating it only releases that
            // controller's own subscriptions; and returning null makes the pipeline build a replacement from the
            // group data this refresh was asked to validate. Whenever the node cannot prove that the group data is
            // still the live data, it therefore recaptures the live inputs and returns a node built from them.
            if (_avatarRoot == null || _targetRenderer == null || _lease == null || !_lease.IsValid)
            {
                return RebuildFromLiveInput(context, null, "this node has no usable target or generated mesh");
            }

            if (!IsProxySetIntact(proxyPairs))
            {
                // The pipeline did not hand this node a proxy for its target. A replacement built from the live
                // inputs is still the safe answer: the group data cannot be trusted to be newer than this node.
                return RebuildFromLiveInput(context, null, "this node's target is missing from the proxy set");
            }

            // Structural check: which installers exist, in what order, with which references. Cheap enough to run
            // on every refresh, and it catches "the avatar changed shape" without re-reading a single mesh.
            var liveKey = ApaPreviewDiscovery.ComputeStructuralKey(
                _avatarRoot,
                ApaPreviewDiscovery.CollectAllInstallers(_avatarRoot));

            if (!string.Equals(liveKey, _structuralKey, StringComparison.Ordinal))
            {
                return RebuildFromLiveInput(context, null, "the avatar's installer structure changed");
            }

            // The output also depends on inputs that no upstream aspect flag describes: a local transform, an
            // installer field, a profile's content, a bone. A node's own context invalidation arrives with no
            // aspect flags at all (IRenderFilter.cs:361-364), and an upstream Mesh/Material/Shapes change can
            // alter the originals without changing the group identity. Both cases are decided by recapturing the
            // live inputs and comparing the content fingerprint; only an unchanged fingerprint allows reuse.
            var upstreamChanged =
                (updatedAspects & (RenderAspects.Mesh | RenderAspects.Material | RenderAspects.Shapes)) != 0;
            if (upstreamChanged || updatedAspects == 0)
            {
                ApaPreviewRequest live = null;
                try
                {
                    live = DiscoverLive();
                }
                catch (Exception e)
                {
                    ApaPreviewDiagnostics.ReportInternalFailure(
                        "Re-capturing preview inputs for '" + _structuralKey + "'", e);
                }

                if (live != null && live.IsRenderable &&
                    string.Equals(live.Fingerprint, _fingerprint, StringComparison.Ordinal))
                {
                    // Nothing that reaches the output changed, so there is nothing to rebuild and nothing to
                    // report to downstream nodes.
                    WhatChanged = 0;
                    return this;
                }

                return RebuildFromLiveInput(context, live, "the live inputs no longer match this node's fingerprint");
            }

            WhatChanged = 0;
            return this;
        }

        /// <summary>
        /// Re-captures the live scene and returns this node's own target group, or null when it is gone.
        /// </summary>
        /// <remarks>
        /// A node is bound to one target group, so a re-capture that finds the avatar's groups must resolve the
        /// same group rather than "the avatar's preview": with several targets, taking the first group would draw
        /// another renderer's mesh onto this proxy.
        /// </remarks>
        private ApaPreviewRequest DiscoverLive()
        {
            if (_avatarRoot == null) return null;

            var groups = ApaPreviewDiscovery.DiscoverGroups(_avatarRoot, _policy);
            return ApaPreviewDiscovery.FindGroup(groups, _groupKey, _targetRenderer);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ApaPreviewDebugOverlay.Unregister(_targetRenderer, _debugData);

            // Releasing the lease never destroys the mesh outright: the cache keeps it for reuse and destroys it
            // on eviction or teardown, which is what keeps a pipeline swap from destroying a mesh the outgoing
            // generation is still drawing.
            if (_lease != null) _lease.Dispose();

            // The shadow bones are scene objects this node owns, unlike the cached mesh: they die with it.
            DestroyShadowBones(_shadowObjects);
        }

        /// <summary>
        /// Destroys the shadow bones the node created, tolerating repeats and already-destroyed objects.
        /// </summary>
        private static void DestroyShadowBones(List<GameObject> shadows)
        {
            if (shadows == null) return;

            for (var i = 0; i < shadows.Count; i++)
            {
                if (shadows[i] != null) UnityEngine.Object.Destroy(shadows[i]);
            }

            shadows.Clear();
        }

        /// <summary>
        /// Builds this node's replacement from the live scene, as a node that returns itself would.
        /// </summary>
        /// <param name="context">The replacement controller's context, for observations the re-capture adds.</param>
        /// <param name="live">
        /// The live discovery result, or null when the caller has none and it must be recaptured here.
        /// </param>
        /// <param name="reason">Why the node was not reused, for the empty node's description.</param>
        private IRenderFilterNode RebuildFromLiveInput(ComputeContext context, ApaPreviewRequest live, string reason)
        {
            if (live == null)
            {
                try
                {
                    live = DiscoverLive();
                }
                catch (Exception e)
                {
                    ApaPreviewDiagnostics.ReportInternalFailure(
                        "Re-capturing preview inputs for '" + ApaPreviewDiagnosticKey.For(_avatarRoot, _groupKey)
                            + "'", e);
                    live = null;
                }
            }

            if (live == null)
            {
                // Nothing under this root asks for this group's assembly any more (the installer was deleted or
                // disabled, or the target no longer has parts). Drawing nothing is the only safe answer: returning
                // null would make the pipeline rebuild a node from the group data this refresh rejected, and the
                // group itself disappears on the next target-set rebuild.
                ApaPreviewDiagnostics.Clear(ApaPreviewDiagnosticKey.For(_avatarRoot, _groupKey));
                return new ApaPreviewEmptyNode(
                    reason, RenderAspects.Mesh | RenderAspects.Material | RenderAspects.Shapes, _request);
            }

            // Report the live state, so the overlay and the console describe the inputs this preview now shows
            // rather than the ones the retired group data described.
            ApaPreviewDiagnostics.ReportRequest(live);

            if (!live.IsRenderable || live.TargetRenderer == null)
            {
                return new ApaPreviewEmptyNode(
                    reason, RenderAspects.Mesh | RenderAspects.Material | RenderAspects.Shapes, live);
            }

            // A re-capture can involve objects this node never observed (an installer added since it was built),
            // so the observations of the replacement are registered on the context that will keep it alive.
            if (!SameObservationSet(live))
            {
                ApaPreviewInputObserver.Observe(context, live.AvatarRoot, live.AllInstallers, live.TargetRenderer);
            }

            // Create is exception-safe: a failure here returns the empty node rather than escaping into the
            // pipeline's build task.
            return ApaPreviewNode.Create(live);
        }

        /// <summary>True when a re-captured request observes exactly the objects this node already registered.</summary>
        private bool SameObservationSet(ApaPreviewRequest live)
        {
            if (!ReferenceEquals(live.AvatarRoot, _avatarRoot)) return false;
            if (!ReferenceEquals(live.TargetRenderer, _targetRenderer)) return false;

            var installers = live.AllInstallers;
            var known = _allInstallers;
            if (ReferenceEquals(installers, known)) return true;
            if (installers == null || known == null) return false;
            if (installers.Count != known.Count) return false;

            for (var i = 0; i < installers.Count; i++)
            {
                if (!ReferenceEquals(installers[i], known[i])) return false;
            }

            return true;
        }

        private bool IsProxySetIntact(IEnumerable<(Renderer, Renderer)> proxyPairs)
        {
            if (proxyPairs == null) return false;

            var found = false;
            foreach (var pair in proxyPairs)
            {
                if (pair.Item1 != _targetRenderer) continue;
                found = true;
                if (pair.Item2 == null) return false;
            }

            return found;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "ApaPreviewNode(" + (_targetRenderer != null ? _targetRenderer.name : "(no target)") +
                   ", fingerprint=" + _fingerprint + ", rendering=" + IsRendering + ")";
        }
    }

    /// <summary>
    /// A node that applies nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IRenderFilter.Instantiate</c> must never return null: the pipeline wraps the result in a
    /// <c>NodeController</c> and calls into it immediately, so a null node faults the preview build instead of
    /// showing the original avatar. This node is the safe stand-in for the cases where a group exists but no
    /// preview can be built from it; it reports the reason and leaves the proxy alone.
    /// </para>
    /// <para>
    /// <b>Refresh never returns null either.</b> Null would make the pipeline rebuild a node from the same group
    /// data this node could not use, and that rebuild could draw a result the live inputs do not justify. When
    /// this node knows the request it replaced it retries from a fresh capture of the live scene, so a transient
    /// failure cannot leave the preview permanently empty; otherwise it keeps drawing nothing, which is safe
    /// because the target set observes the inputs and rebuilds the group as soon as anything changes.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewEmptyNode : IRenderFilterNode
    {
        private readonly string _reason;

        /// <summary>
        /// The discovery result this node stands in for, or null when there was none. It is what makes a retry
        /// possible: without it an empty node has no way to name the avatar whose inputs it would have to read.
        /// </summary>
        private readonly ApaPreviewRequest _request;

        private RenderAspects _whatChanged;

        /// <summary>Creates a do-nothing node.</summary>
        public ApaPreviewEmptyNode(string reason)
            : this(reason, 0)
        {
        }

        /// <summary>
        /// Creates a do-nothing node that reports which aspects it stopped changing.
        /// </summary>
        /// <remarks>
        /// The aspects matter when this node replaces a drawing node during <c>Refresh</c>: the proxy reverts to
        /// the original mesh, so the nodes downstream of this one must be rebuilt even though this node itself
        /// writes nothing. On the <c>Instantiate</c> path NDMF ignores the value.
        /// </remarks>
        public ApaPreviewEmptyNode(string reason, RenderAspects whatChanged)
            : this(reason, whatChanged, null)
        {
        }

        /// <summary>Creates a do-nothing node that can retry from the live inputs of a known avatar.</summary>
        public ApaPreviewEmptyNode(string reason, RenderAspects whatChanged, ApaPreviewRequest request)
        {
            _reason = reason ?? string.Empty;
            _whatChanged = whatChanged;
            _request = request;
        }

        /// <summary>The reason this node does nothing, for diagnostics.</summary>
        public string Reason => _reason;

        /// <inheritdoc />
        public RenderAspects WhatChanged => _whatChanged;

        /// <inheritdoc />
        public Task<IRenderFilterNode> Refresh(
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context,
            RenderAspects updatedAspects)
        {
            if (_request == null || _request.AvatarRoot == null)
            {
                // Nothing to retry from. Keeping this node is safe: it draws nothing, and the group is rebuilt
                // (and Instantiate re-run) as soon as an input that could produce a preview changes.
                _whatChanged = 0;
                return Task.FromResult<IRenderFilterNode>(this);
            }

            try
            {
                ApaPreviewInputObserver.Observe(
                    context, _request.AvatarRoot, _request.AllInstallers, _request.TargetRenderer);

                var groups = ApaPreviewDiscovery.DiscoverGroups(_request.AvatarRoot, _request.NumericPolicy);
                var live = ApaPreviewDiscovery.FindGroup(groups, _request.GroupKey, _request.TargetRenderer);

                if (live != null && live.IsRenderable && live.TargetRenderer != null)
                {
                    ApaPreviewDiagnostics.ReportRequest(live);

                    // Create is exception-safe: a failure returns another empty node rather than escaping.
                    return Task.FromResult(ApaPreviewNode.Create(live));
                }
            }
            catch (Exception e)
            {
                ApaPreviewDiagnostics.ReportInternalFailure("Retrying preview node construction", e);
            }

            _whatChanged = 0;
            return Task.FromResult<IRenderFilterNode>(this);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "ApaPreviewEmptyNode(" + _reason + ")";
        }
    }
}

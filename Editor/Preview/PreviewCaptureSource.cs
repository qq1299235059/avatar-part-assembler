using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// The preview's capture source: it reads the proxy NDMF prepared for a renderer, not the author's renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it is for.</b> NDMF runs render filters in pass order over one proxy per renderer, and each stage's
    /// <c>OnFrame</c> runs before the next stage's node is created (<c>ProxyPipeline.Build</c>,
    /// <c>NodeController</c>). Modular Avatar's preview filters are earlier stages, so the proxy this package's
    /// filter is handed already carries their result: a Mesh Cutter's cut mesh, a Material Setter's material list,
    /// a Shape Changer's blend-shape weights. Reading the proxy is what makes the preview show what the avatar
    /// will actually look like; reading the original renderer is the defect this type exists to remove.
    /// </para>
    /// <para>
    /// <b>The fallback is the live renderer, and it is not a silent one.</b> A proxy that does not exist for a
    /// renderer (the renderer is not in any earlier stage's group), or one that has been destroyed, reads as
    /// "unknown" and the live value is used instead. When a proxy <i>is</i> used, the capture reports
    /// <c>APA055</c> with <see cref="UpstreamMeshModifiedReason"/> or
    /// <see cref="UpstreamMaterialsModifiedReason"/>, so a substitution is never invisible in the report.
    /// </para>
    /// <para>
    /// <b>Read-only by construction.</b> Every member returns a value the caller snapshots. This type assigns no
    /// mesh, material, weight, or asset, and destroys nothing; the proxies belong to NDMF's pipeline.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewCaptureSource : ApaCaptureSource
    {
        /// <summary>
        /// Stable token for "an earlier preview stage replaced the geometry this capture read".
        /// </summary>
        /// <remarks>
        /// The same token a diagnostic, a test, and a log compare. It is deliberately about the <i>stage</i>
        /// rather than about the mesh: the capture cannot know which component wrote the substitute, only that
        /// the value it read is not the author's own.
        /// </remarks>
        public const string UpstreamMeshModifiedReason = "upstream-preview-mesh-modified";

        /// <summary>Stable token for "an earlier preview stage replaced only the material list".</summary>
        public const string UpstreamMaterialsModifiedReason = "upstream-preview-materials-modified";

        private readonly Dictionary<Renderer, Renderer> _proxies = new Dictionary<Renderer, Renderer>();

        /// <summary>How many renderers this source has a substitute for.</summary>
        public int Count => _proxies.Count;

        /// <summary>
        /// Builds a source from the proxy pairs NDMF hands a filter's <c>Instantiate</c>/<c>Refresh</c>.
        /// </summary>
        /// <remarks>
        /// A null, missing, or identical pair is skipped rather than recorded: there is nothing to substitute for
        /// it, and recording it would make the source claim a substitution that does not exist. Never returns
        /// null, so a caller can use the result without a null check and an empty source behaves exactly like
        /// <see cref="ApaCaptureSource.Live"/>.
        /// </remarks>
        public static ApaPreviewCaptureSource FromProxyPairs(IEnumerable<(Renderer, Renderer)> proxyPairs)
        {
            var source = new ApaPreviewCaptureSource();
            if (proxyPairs == null) return source;

            foreach (var pair in proxyPairs)
            {
                source.Record(pair.Item1, pair.Item2);
            }

            return source;
        }

        /// <summary>Records one original-to-proxy pair, ignoring an unusable one.</summary>
        public void Record(Renderer original, Renderer proxy)
        {
            if (original == null || proxy == null || original == proxy) return;
            _proxies[original] = proxy;
        }

        /// <summary>
        /// True when at least one proxy's mesh differs from its original's live mesh.
        /// </summary>
        /// <remarks>
        /// This is the question a node has to ask before it may reuse inputs captured without a proxy: a
        /// substituted <i>mesh</i> changes the geometry the assembly is built from, so the inputs must be
        /// re-captured through this source. A substituted material list does not: materials are resolved from the
        /// live renderers on every frame, so they need no rebuild.
        /// </remarks>
        public bool SubstitutesUpstreamState
        {
            get
            {
                foreach (var pair in _proxies)
                {
                    if (SubstitutesMesh(pair.Key, pair.Value)) return true;
                }

                return false;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The proxy's mesh is preferred only when it has one. A proxy whose mesh is null is a renderer the earlier
        /// stages did not give geometry to, and using null there would refuse a capture the live renderer can
        /// answer.
        /// </remarks>
        public override Mesh MeshFor(Renderer renderer, Mesh liveMesh)
        {
            var proxyMesh = ReadProxyMesh(renderer);
            return proxyMesh != null ? proxyMesh : liveMesh;
        }

        /// <inheritdoc />
        /// <remarks>
        /// An empty proxy list is treated as "no substitute": a proxy always carries at least the original's
        /// materials, so an empty list means the value could not be read rather than that the renderer should
        /// render with nothing.
        /// </remarks>
        public override Material[] MaterialsFor(Renderer renderer, Material[] liveMaterials)
        {
            var proxyMaterials = ReadProxyMaterials(renderer);
            return proxyMaterials.Length > 0 ? proxyMaterials : liveMaterials;
        }

        /// <inheritdoc />
        /// <remarks>
        /// A destroyed proxy compares equal to null and reads as "unknown", so a frame that outlives its pipeline
        /// generation falls back to the live renderer instead of throwing.
        /// </remarks>
        public override Renderer SubstitutedRendererFor(Renderer renderer)
        {
            return ProxyFor(renderer);
        }

        /// <inheritdoc />
        public override string SubstitutionReasonFor(Renderer renderer)
        {
            var proxy = ProxyFor(renderer);
            if (proxy == null) return null;

            if (SubstitutesMesh(renderer, proxy)) return UpstreamMeshModifiedReason;

            var live = ApaCaptureSource.ReadLiveMaterials(renderer);
            var proxyMaterials = ApaCaptureSource.ReadLiveMaterials(proxy);
            return MaterialsDiffer(live, proxyMaterials) ? UpstreamMaterialsModifiedReason : null;
        }

        /// <summary>Drops every recorded proxy, for a node that is being disposed.</summary>
        public void Clear()
        {
            _proxies.Clear();
        }

        /// <summary>
        /// An independent copy of this map, for a replacement node that must outlive this one.
        /// </summary>
        /// <remarks>
        /// A rebuilt node is handed its own map rather than this instance: the node it replaces is disposed
        /// afterwards and clears its map, and sharing one mutable dictionary would let that disposal blind the
        /// replacement.
        /// </remarks>
        public ApaPreviewCaptureSource Copy()
        {
            var copy = new ApaPreviewCaptureSource();
            foreach (var pair in _proxies)
            {
                copy.Record(pair.Key, pair.Value);
            }

            return copy;
        }

        /// <summary>The live proxy for a renderer, or null when there is none or it has been destroyed.</summary>
        private Renderer ProxyFor(Renderer renderer)
        {
            if (renderer == null) return null;
            if (!_proxies.TryGetValue(renderer, out var proxy)) return null;

            // Unity's overloaded null check: a destroyed proxy is "unknown", never a value to read.
            return proxy != null ? proxy : null;
        }

        private Mesh ReadProxyMesh(Renderer renderer)
        {
            var proxy = ProxyFor(renderer);
            return proxy != null ? ApaCaptureSource.ReadLiveMesh(proxy) : null;
        }

        private Material[] ReadProxyMaterials(Renderer renderer)
        {
            var proxy = ProxyFor(renderer);
            return proxy != null ? ApaCaptureSource.ReadLiveMaterials(proxy) : Array.Empty<Material>();
        }

        /// <summary>
        /// True when the proxy's mesh is a different object from the renderer's own live mesh.
        /// </summary>
        /// <remarks>
        /// Reference identity, not content: an earlier stage that produced a mesh with the same content still
        /// produced a different mesh, and the capture has to read the one that is actually being drawn. A null
        /// live mesh (a protected part's renderer) with a non-null proxy mesh is a substitution too.
        /// </remarks>
        private static bool SubstitutesMesh(Renderer original, Renderer proxy)
        {
            var liveMesh = ApaCaptureSource.ReadLiveMesh(original);
            var proxyMesh = ApaCaptureSource.ReadLiveMesh(proxy);
            if (proxyMesh == null) return false;

            return !ReferenceEquals(proxyMesh, liveMesh);
        }

        private static bool MaterialsDiffer(Material[] live, Material[] proxy)
        {
            if (live == null || proxy == null) return false;
            if (live.Length != proxy.Length) return true;

            for (var i = 0; i < live.Length; i++)
            {
                if (!ReferenceEquals(live[i], proxy[i])) return true;
            }

            return false;
        }
    }
}

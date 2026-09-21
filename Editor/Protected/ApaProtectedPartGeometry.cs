using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Finds the protected payload that stands in for a part renderer's mesh, and exposes its geometry to the
    /// consumers that are not the build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists separately from the context builder.</b> The build discovers its installers by walking an
    /// avatar root, so it always knows which payload belongs to which part. The authoring window does not: it holds
    /// a selection of scene objects, and the prefab under it may be a protected prefab whose renderer carries no
    /// mesh at all. This class is the one place that answers "which installer owns this renderer, and what payload
    /// does it reference", so the selection check, the authoring context builder, and the Scene View overlays all
    /// resolve the same asset the same way instead of each inventing a lookup.
    /// </para>
    /// <para>
    /// <b>It reads; it never writes.</b> No field is assigned, no asset is dirtied, and no mesh is attached to a
    /// scene object. The only object created here is the transient overlay mesh owned by
    /// <see cref="ApaProtectedOverlayGeometry"/>, which is destroyed by its owner.
    /// </para>
    /// </remarks>
    public static class ApaProtectedPartGeometry
    {
        /// <summary>
        /// The installer that owns a part renderer, or null when no installer in its hierarchy does.
        /// </summary>
        /// <remarks>
        /// The search starts at the renderer and walks outwards, so the closest owning installer wins when a
        /// hierarchy carries more than one. An installer whose <see cref="AvatarPartInstaller.ResolvePartRoot"/>
        /// does not contain the renderer is skipped: it describes a different part, and using its payload would
        /// assemble geometry the author never selected.
        /// </remarks>
        public static AvatarPartInstaller FindInstaller(Renderer partRenderer, GameObject partRoot)
        {
            if (partRenderer == null && partRoot == null) return null;

            var found = FirstOwningInstaller(
                partRenderer != null ? partRenderer.GetComponentsInParent<AvatarPartInstaller>(true) : null,
                partRenderer);
            if (found != null) return found;

            return FirstOwningInstaller(
                partRoot != null ? partRoot.GetComponentsInChildren<AvatarPartInstaller>(true) : null,
                partRenderer);
        }

        /// <summary>
        /// The protected asset that stands in for a part renderer's mesh, or null when the part is ordinary.
        /// </summary>
        public static ApaProtectedMeshAsset ResolveAsset(
            Renderer partRenderer,
            GameObject partRoot,
            out AvatarPartInstaller installer)
        {
            installer = FindInstaller(partRenderer, partRoot);
            return installer != null ? installer.ProtectedMesh : null;
        }

        /// <summary>
        /// A cheap identity of everything a decoded overlay geometry depends on.
        /// </summary>
        /// <remarks>
        /// The renderer instance, the referenced asset, the part id the payload is decoded for, and the payload's
        /// cached content revision — the same revision <see cref="ApaProtectedMeshCache"/> keys on. Comparing this
        /// string is therefore a handful of byte reads for an unchanged payload and a full hash pass only when the
        /// payload actually changed, which is what lets a Scene View repaint decide "the geometry I hold is still
        /// current" without decoding anything. An unprotected part has its own stable value, so "the part lost its
        /// payload" and "the part gained one" are both detected.
        /// </remarks>
        public static string IdentityOf(Renderer partRenderer, GameObject partRoot)
        {
            var rendererId = partRenderer != null ? partRenderer.GetInstanceID() : 0;
            var asset = ResolveAsset(partRenderer, partRoot, out var installer);
            if (asset == null) return rendererId + "|(none)";

            return rendererId
                   + "|" + ApaProtectedMeshCache.ContentIdentityOf(asset)
                   + "|" + ApaPartIdentityResolver.ResolvePartId(installer);
        }

        /// <summary>
        /// Decodes the protected payload of a part through the shared cache.
        /// </summary>
        /// <param name="partRenderer">The part renderer whose payload stands in for its mesh.</param>
        /// <param name="partRoot">The part root the renderer belongs to.</param>
        /// <param name="data">Receives the decoded payload data on success.</param>
        /// <param name="installer">Receives the owning installer, or null when the part is unprotected.</param>
        /// <param name="issue">
        /// Receives the codec's blocking diagnostic when the part is protected but its payload cannot be used, or
        /// null when the part simply has no payload (which is the ordinary path, not a failure).
        /// </param>
        /// <returns>True when a payload was decoded; false when the part is unprotected or its payload failed.</returns>
        public static bool TryDecode(
            Renderer partRenderer,
            GameObject partRoot,
            out ApaProtectedMeshData data,
            out AvatarPartInstaller installer,
            out ValidationIssue issue)
        {
            data = null;
            issue = null;
            installer = FindInstaller(partRenderer, partRoot);

            if (installer == null || !installer.HasProtectedMesh)
            {
                // Not a protected part at all. The caller keeps its existing behaviour, which for a mesh-less
                // renderer is the ordinary APA006 diagnostic.
                installer = null;
                return false;
            }

            return ApaProtectedMeshCache.TryDecode(
                installer.ProtectedMesh,
                ApaPartIdentityResolver.ResolvePartId(installer),
                out data,
                out issue);
        }

        private static AvatarPartInstaller FirstOwningInstaller(
            AvatarPartInstaller[] candidates,
            Renderer partRenderer)
        {
            if (candidates == null) return null;

            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate == null) continue;
                if (!Owns(candidate, partRenderer)) continue;
                return candidate;
            }

            return null;
        }

        private static bool Owns(AvatarPartInstaller installer, Renderer partRenderer)
        {
            var partRoot = installer.ResolvePartRoot();
            if (partRoot == null) return partRenderer == null;
            if (partRenderer == null) return true;

            return partRenderer.transform == partRoot.transform
                   || partRenderer.transform.IsChildOf(partRoot.transform);
        }
    }

    /// <summary>
    /// A transient mesh decoded from a protected payload, for the authoring window's Scene View overlays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it is for.</b> The seam, merge-check, and removal overlays read a <see cref="Mesh"/>: vertex
    /// positions, colors, submesh counts. A protected prefab has none — that is the point of the feature — so the
    /// overlays would draw nothing for exactly the parts that need them most. This type reconstructs the payload's
    /// geometry in memory so those overlays can compute what the build would compute.
    /// </para>
    /// <para>
    /// <b>It is never attached to anything.</b> The mesh is created with <see cref="HideFlags.HideAndDontSave"/>,
    /// is not assigned to the scene renderer, is not written through <c>AssetDatabase</c>, and is destroyed by
    /// <see cref="Dispose"/>. The authoring scene is left byte-for-byte as the author left it, and the decoded
    /// geometry cannot reach a saved scene or a project asset even if the author saves while a window is open.
    /// </para>
    /// <para>
    /// <b>One decode, one mesh, released on invalidation.</b> The payload is read through
    /// <see cref="ApaProtectedMeshCache"/>, so it is decrypted once per payload content identity, and the mesh is
    /// built from that decoded data. <see cref="IsCurrentFor"/> is the cheap check the window uses to decide
    /// whether the geometry it holds still describes the part; anything else invalidates and disposes it.
    /// </para>
    /// </remarks>
    public sealed class ApaProtectedOverlayGeometry : IDisposable
    {
        private Mesh _mesh;
        private bool _disposed;

        /// <summary>The decoded payload data, still plain managed arrays.</summary>
        public ApaProtectedMeshData Data { get; private set; }

        /// <summary>The transient mesh built from <see cref="Data"/>, or null when the payload could not be used.</summary>
        public Mesh Mesh => _mesh;

        /// <summary>The protected asset the geometry was decoded from.</summary>
        public ApaProtectedMeshAsset Asset { get; private set; }

        /// <summary>The part id the payload was decoded for.</summary>
        public string PartId { get; private set; }

        /// <summary>The blocking diagnostic when the payload could not be decoded or reconstructed.</summary>
        public ValidationIssue Issue { get; private set; }

        /// <summary>True when a transient mesh is available.</summary>
        public bool IsValid => !_disposed && _mesh != null;

        /// <summary>True when the part has a payload but it could not be used.</summary>
        public bool Failed => Issue != null;

        /// <summary>
        /// The identity of the inputs this geometry was built from, as
        /// <see cref="ApaProtectedPartGeometry.IdentityOf"/> reports it.
        /// </summary>
        public string Identity { get; private set; }

        private ApaProtectedOverlayGeometry()
        {
        }

        /// <summary>
        /// Builds overlay geometry for a protected part, or returns null when the part is unprotected.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A null result means "this is an ordinary part": the caller keeps its existing live-mesh path and its
        /// existing diagnostics. A result whose <see cref="Failed"/> is true means the part <i>is</i> protected and
        /// its payload is unusable; the caller reports <see cref="Issue"/> — the codec's own blocking
        /// <c>APA053</c> — rather than falling back to the ordinary mesh-less diagnostic, because the two
        /// conditions have different remedies.
        /// </para>
        /// <para>
        /// The failure is carried on the returned object rather than thrown or dropped so that the caller can cache
        /// it: a corrupt payload must not be re-decoded once per repaint, and it must start working again as soon
        /// as the payload is repaired, which the identity check gives for free.
        /// </para>
        /// </remarks>
        public static ApaProtectedOverlayGeometry TryCreate(
            Renderer partRenderer,
            GameObject partRoot,
            out ValidationIssue issue)
        {
            issue = null;

            var asset = ApaProtectedPartGeometry.ResolveAsset(partRenderer, partRoot, out var installer);
            if (asset == null) return null;

            var geometry = new ApaProtectedOverlayGeometry
            {
                Asset = asset,
                PartId = ApaPartIdentityResolver.ResolvePartId(installer),
                Identity = ApaProtectedPartGeometry.IdentityOf(partRenderer, partRoot)
            };

            if (!ApaProtectedMeshCache.TryDecode(
                    asset, geometry.PartId, out var data, out var decodeIssue))
            {
                geometry.Issue = decodeIssue;
                issue = decodeIssue;
                return geometry;
            }

            geometry.Data = data;
            geometry._mesh = ApaProtectedMeshHydration.TryBuildTransientMesh(data.CreateSnapshot(), out var buildIssue);
            if (geometry._mesh == null)
            {
                geometry.Issue = buildIssue;
                issue = buildIssue;
            }

            return geometry;
        }

        /// <summary>
        /// True when this geometry still describes the part's current payload.
        /// </summary>
        /// <remarks>
        /// The check is cheap by construction: it compares the identity
        /// <see cref="ApaProtectedPartGeometry.IdentityOf"/> produces, which is the renderer instance, the asset
        /// reference, the part id, and the payload's cached content revision — the same identity the decode cache
        /// keys on. "The window's geometry is current" and "the cache would serve the same bytes" therefore cannot
        /// disagree.
        /// </remarks>
        public bool IsCurrentFor(Renderer partRenderer, GameObject partRoot)
        {
            if (_disposed) return false;
            return string.Equals(
                ApaProtectedPartGeometry.IdentityOf(partRenderer, partRoot), Identity, StringComparison.Ordinal);
        }

        /// <summary>Destroys the transient mesh. Idempotent, and safe on a partially built instance.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var mesh = _mesh;
            _mesh = null;
            if (mesh == null) return;

            if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
            else UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}

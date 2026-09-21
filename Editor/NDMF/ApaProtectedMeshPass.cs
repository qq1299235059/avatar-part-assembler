using System.Collections.Generic;
using AvatarPartAssembler.Editor.Localization;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Restores a protected part's geometry in memory on the build clone before Modular Avatar reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this pass exists.</b> A protected prefab is saved with its part renderer carrying no mesh, because
    /// that is what removes the prefab's dependency on the source mesh and its model file. Modular Avatar's
    /// armature merge, however, inspects the part renderer's mesh — its bone weights, bind poses, and vertex
    /// count — before APA ever captures it. Something therefore has to put the geometry back for the duration of
    /// the build, and this pass is that something.
    /// </para>
    /// <para>
    /// <b>In memory only, for the duration of one build.</b> The mesh is built by
    /// <see cref="ApaProtectedMeshHydration"/> with <see cref="HideFlags.HideAndDontSave"/>, attached through a
    /// lease, and destroyed when the lease is released. Nothing here calls <c>AssetDatabase</c>, <c>SaveAssets</c>,
    /// or any file API, and the decoded payload never becomes a project asset.
    /// </para>
    /// <para>
    /// <b>A live mesh always wins.</b> Hydration happens only when the renderer's serialized mesh is missing
    /// <i>and</i> the installer references a payload. A prefab that still carries a real mesh — an ordinary part,
    /// or a protected part an author repaired by hand — is left exactly as it is, so the unprotected path keeps
    /// its current behaviour and its current diagnostics.
    /// </para>
    /// <para>
    /// <b>One decode, shared with the rest of the build.</b> The payload is read through
    /// <see cref="ApaProtectedMeshCache"/>, the same cache the context builder and the pre-merge fingerprint gate
    /// use, so a build decodes each protected part once per payload content identity rather than once per
    /// consumer.
    /// </para>
    /// <para>
    /// <b>Failures are reported once, here.</b> A payload that is missing, tampered with, truncated, or written
    /// for another part blocks the build with the codec's own <c>APA053</c> diagnostic. The later passes then
    /// short-circuit on <c>BuildContext.Successful</c> instead of repeating the same message, and the assembly
    /// pass reports it if this pass never ran.
    /// </para>
    /// <para>
    /// <b>Lease lifetime.</b> The leases are recorded in <see cref="ApaTransientArtifacts"/>, released by the
    /// assembly pass as soon as it has consumed them, and swept by
    /// <see cref="ApaProtectedMeshLeaseCleanupPass"/> if an aborted build never reached the assembly. This pass
    /// additionally sweeps leases left by a previous aborted run before it attaches anything, so a transient
    /// decrypted mesh can never outlive the build that created it.
    /// </para>
    /// </remarks>
    internal sealed class ApaProtectedMeshPass : Pass<ApaProtectedMeshPass>
    {
        /// <inheritdoc />
        /// <remarks>Resolved when NDMF draws the build report, so the pass name follows the selected language.</remarks>
        public override string DisplayName => ApaLocalization.Tr("Restore protected part meshes in memory");

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var avatarRoot = context.AvatarRootObject;
            if (avatarRoot == null) return;

            // A lease from a build that was aborted before its cleanup ran would otherwise stay attached to a
            // clone nobody owns any more, holding a decrypted mesh alive. The sweep is cheap and idempotent.
            ApaProtectedMeshLease.ReleaseAll();

            var installers = ContextBuilder.CollectInstallers(avatarRoot);
            if (installers.Count == 0) return;

            var artifacts = ApaTransientArtifacts.For(context);

            // Resolved before anything is attached, while every installer is alive: the report uses these
            // references after the assembly pass has consumed (destroyed) the part renderers.
            var references = ApaNdmfDiagnostics.CaptureReferences(avatarRoot, installers, context.ObjectRegistry);
            var issues = new List<ValidationIssue>();

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                if (installer == null || !installer.HasProtectedMesh) continue;

                var partRoot = installer.ResolvePartRoot();
                var renderer = partRoot != null ? partRoot.GetComponentInChildren<Renderer>(true) : null;
                var partId = ApaPartIdentityResolver.ResolvePartId(installer);

                if (renderer == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.ProtectedMeshInvalid,
                        ApaIssuePhase.Attributes,
                        "Installer '" + partId + "' carries a protected mesh payload, but its part root '" +
                        (partRoot != null ? partRoot.name : "(null)") +
                        "' has no Renderer to restore the geometry onto. The build cannot read the part's skinning " +
                        "without it. Restore the part renderer, or recreate the protected prefab.",
                        partId,
                        detail: "reason=protected-mesh-missing; renderer=(none); part=" + partId));
                    continue;
                }

                // A live mesh wins: this is the ordinary part path, and a protected part whose renderer was
                // repaired by hand must not be overwritten by its payload.
                if (MeshOf(renderer) != null) continue;

                if (!ApaProtectedMeshCache.TryDecode(
                        installer.ProtectedMesh, partId, out var data, out var decodeIssue))
                {
                    issues.Add(decodeIssue);
                    continue;
                }

                // The payload's own recorded bone paths are used for the transient mesh. They are only ever read
                // by Modular Avatar's merge, which matches bones by name; the authoritative live bone identity is
                // captured later, by the assembly pass, from the merged hierarchy.
                var snapshot = data.CreateSnapshot();
                if (!ApaProtectedMeshHydration.TryHydrate(renderer, snapshot, out var lease, out var hydrateIssue))
                {
                    issues.Add(hydrateIssue);
                    continue;
                }

                artifacts.RecordProtectedMeshLease(lease);
            }

            ApaNdmfDiagnostics.Report(issues, references);
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }
    }

    /// <summary>
    /// Releases any protected-mesh lease a build is still holding when the build reaches the optimizing phase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A safety net, not the primary cleanup.</b> The assembly pass releases the leases it consumed, and its
    /// <c>finally</c> covers its own failure paths. This pass covers the case that pass never ran at all — an
    /// exception in another plugin's Transforming pass, a disabled APA pass, or a build that errored earlier in
    /// the pipeline. Without it a transient decrypted mesh would still be attached to the clone when NDMF
    /// serializes the avatar, and NDMF's asset serializer would write it into the project as a real mesh asset.
    /// That is precisely the leak this feature exists to prevent, so the release is declared as its own pass in a
    /// later phase rather than left to a code path that might not execute.
    /// </para>
    /// <para>
    /// The pass never reports and never blocks: releasing a lease cannot fail in a way the author could act on,
    /// and a build that already failed must not gain a second, unrelated diagnostic.
    /// </para>
    /// </remarks>
    internal sealed class ApaProtectedMeshLeaseCleanupPass : Pass<ApaProtectedMeshLeaseCleanupPass>
    {
        /// <inheritdoc />
        public override string DisplayName => ApaLocalization.Tr("Release transient protected part meshes");

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var artifacts = ApaTransientArtifacts.For(context);
            artifacts.ReleaseProtectedMeshLeases();

            // Also sweeps leases whose build context was discarded without this pass running, for example a
            // preview run torn down mid-pipeline.
            ApaProtectedMeshLease.ReleaseAll();
        }
    }
}

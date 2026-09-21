using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The lifetime of one transient mesh reconstructed from a protected payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a lease exists at all.</b> Modular Avatar's armature merge inspects the part renderer's mesh — bone
    /// weights, bind poses, vertex count — before APA ever captures it. A protected prefab deliberately has no
    /// mesh, so the build has to put one back for the duration of the merge. That reconstructed mesh is transient
    /// by definition: it must never be written to the project, must never survive the build, and must be destroyed
    /// on the failure path as well as the success path.
    /// </para>
    /// <para>
    /// <b>Dispose is the whole cleanup contract.</b> It restores whatever mesh the renderer had before the lease
    /// took it, destroys the transient mesh, and removes itself from the live registry. It is idempotent, and it
    /// tolerates a renderer or mesh that Unity has already destroyed (both compare equal to null after
    /// destruction), so a lease can be released from a pass that runs after the assembly consumed the renderer.
    /// </para>
    /// <para>
    /// <b>HideAndDontSave, never a project asset.</b> The reconstructed mesh is created in memory with
    /// <see cref="HideFlags.HideAndDontSave"/>, which is also what keeps NDMF's end-of-build asset serializer from
    /// picking it up: a protected part's geometry reaches the uploaded avatar through the assembly pass's own
    /// generated mesh, not through this transient copy.
    /// </para>
    /// </remarks>
    public sealed class ApaProtectedMeshLease : IDisposable
    {
        /// <summary>Every lease that has not been disposed yet, so a defensive sweep can reach them.</summary>
        private static readonly List<ApaProtectedMeshLease> s_live = new List<ApaProtectedMeshLease>();

        private Mesh _previousMesh;
        private Renderer _renderer;

        /// <summary>The transient mesh this lease owns.</summary>
        public Mesh Mesh { get; private set; }

        /// <summary>The renderer the transient mesh was attached to, or null once disposed.</summary>
        public Renderer Renderer => _renderer;

        /// <summary>True once the lease has released its mesh.</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>Number of leases that have not been disposed yet.</summary>
        public static int LiveCount => s_live.Count;

        internal ApaProtectedMeshLease(Renderer renderer, Mesh mesh)
        {
            _renderer = renderer;
            Mesh = mesh;
            _previousMesh = MeshOf(renderer);
            s_live.Add(this);
        }

        /// <summary>
        /// Restores the renderer's previous mesh and destroys the transient mesh.
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            var renderer = _renderer;
            if (renderer != null)
            {
                try
                {
                    AssignMesh(renderer, _previousMesh);
                }
                catch (Exception)
                {
                    // A renderer that cannot be restored is already being destroyed, or belongs to a clone the
                    // build is discarding. The transient mesh is still destroyed below, which is the part of the
                    // contract that matters: nothing may leak.
                }
            }

            _renderer = null;
            _previousMesh = null;

            var mesh = Mesh;
            Mesh = null;
            if (mesh != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                else UnityEngine.Object.DestroyImmediate(mesh);
            }

            s_live.Remove(this);
        }

        /// <summary>
        /// Disposes every lease that is still live.
        /// </summary>
        /// <remarks>
        /// A defensive sweep, not the primary cleanup: each pass releases the leases it created. It exists because
        /// a transient mesh that outlives its build is invisible — it holds memory, it is never saved, and nothing
        /// reports it — so a build that failed between hydration and the assembly must still leave nothing behind.
        /// </remarks>
        public static void ReleaseAll()
        {
            // A lease's Dispose removes it from the list, so the sweep walks a snapshot rather than the live list.
            var leases = s_live.ToArray();
            for (var i = 0; i < leases.Length; i++) leases[i].Dispose();
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        internal static void AssignMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer == null) return;

            if (renderer is SkinnedMeshRenderer skinned)
            {
                skinned.sharedMesh = mesh;
                return;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = mesh;
        }
    }

    /// <summary>
    /// Reconstructs a transient mesh from a decoded protected payload and attaches it to a build clone's renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One implementation, shared by every consumer.</b> The NDMF hydration pass and any direct caller use this
    /// class, so a protected part's geometry reaches Modular Avatar, the assembly context, and the preview through
    /// exactly the same reconstruction — there is no second builder that could disagree about a vertex attribute.
    /// </para>
    /// <para>
    /// <b>Nothing is persisted.</b> No <c>AssetDatabase</c> call is made here and none may be added: the mesh is
    /// an in-memory object with <see cref="HideFlags.HideAndDontSave"/>, owned by the returned lease.
    /// </para>
    /// </remarks>
    public static class ApaProtectedMeshHydration
    {
        /// <summary>
        /// Builds a transient mesh from a decoded snapshot.
        /// </summary>
        /// <param name="snapshot">The decoded payload data.</param>
        /// <param name="issue">Receives a blocking diagnostic when the mesh cannot be built.</param>
        /// <returns>The transient mesh, or null when it could not be built.</returns>
        public static Mesh TryBuildTransientMesh(MeshSnapshot snapshot, out ValidationIssue issue)
        {
            issue = null;

            if (snapshot == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshInvalid,
                    ApaIssuePhase.Attributes,
                    "No decoded mesh data was supplied to the protected-mesh hydration step.",
                    detail: "reason=" + ApaProtectedMeshReasons.EmptyPayload);
                return null;
            }

            Mesh mesh = null;
            try
            {
                mesh = new Mesh
                {
                    name = string.IsNullOrEmpty(snapshot.Name)
                        ? "APA Protected Part Mesh"
                        : snapshot.Name + " (APA protected)",
                    hideFlags = HideFlags.HideAndDontSave
                };

                // The declared index format is preserved, because the reconstructed mesh must fingerprint exactly
                // like the payload it came from. It is only overridden upward when the vertex count cannot be
                // addressed by a 16-bit index: a transient mesh that silently truncated every index would be worse
                // than a fingerprint difference, and the assembly pass re-derives the output format anyway.
                mesh.indexFormat = ApaMeshLimits.IndexFormatForVertexCount(snapshot.VertexCount) ==
                                   UnityEngine.Rendering.IndexFormat.UInt32
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : snapshot.IndexFormat;

                mesh.SetVertices(ToList(snapshot.Vertices));
                if (snapshot.HasNormals) mesh.SetNormals(ToList(snapshot.Normals));
                if (snapshot.HasTangents) mesh.SetTangents(ToList(snapshot.Tangents));
                if (snapshot.HasColors) mesh.SetColors(ToList(snapshot.Colors));

                for (var channel = 0; channel < snapshot.UvChannelCapacity; channel++)
                {
                    if (!snapshot.HasUvChannel(channel)) continue;
                    mesh.SetUVs(channel, ToList(snapshot.GetUvChannel(channel)));
                }

                mesh.subMeshCount = snapshot.SubMeshCount;
                for (var subMesh = 0; subMesh < snapshot.SubMeshCount; subMesh++)
                {
                    mesh.SetIndices(
                        ToArray(snapshot.SubMeshIndices[subMesh]),
                        subMesh < snapshot.TopologyList.Count
                            ? snapshot.TopologyList[subMesh]
                            : MeshTopology.Triangles,
                        subMesh);
                }

                if (snapshot.SkinWeights.Count > 0) mesh.boneWeights = ToArray(snapshot.SkinWeights);
                if (snapshot.SkinBindPoses.Count > 0) mesh.bindposes = ToArray(snapshot.SkinBindPoses);

                for (var shape = 0; shape < snapshot.BlendShapeCount; shape++)
                {
                    var name = snapshot.Shapes[shape];
                    var frames = snapshot.BlendShapeFrames[shape];
                    for (var frame = 0; frame < frames.Count; frame++)
                    {
                        var value = frames[frame];
                        if (value == null) continue;

                        mesh.AddBlendShapeFrame(
                            name,
                            value.Weight,
                            ToArray(value.DeltaVertices),
                            ToArray(value.DeltaNormals),
                            ToArray(value.DeltaTangents));
                    }
                }

                mesh.bounds = snapshot.Bounds;
                return mesh;
            }
            catch (Exception e)
            {
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);

                issue = ValidationIssue.Error(
                    ApaErrorCode.ProtectedMeshInvalid,
                    ApaIssuePhase.Attributes,
                    "The protected part mesh could not be reconstructed in memory: " + e.GetType().Name + ": " +
                    e.Message,
                    detail: "reason=hydration-failed; exception=" + e.GetType().FullName);
                return null;
            }
        }

        /// <summary>
        /// Attaches a transient mesh built from a snapshot to a renderer and returns the lease that owns it.
        /// </summary>
        /// <param name="renderer">The build clone's part renderer. It is only touched for the lease's lifetime.</param>
        /// <param name="snapshot">The decoded payload data.</param>
        /// <param name="lease">Receives the lease on success.</param>
        /// <param name="issue">Receives a blocking diagnostic on failure.</param>
        /// <returns>True when the transient mesh is attached.</returns>
        /// <remarks>
        /// The caller must dispose the lease. On the failure path nothing is attached and nothing is allocated: the
        /// mesh is destroyed before this method returns.
        /// </remarks>
        public static bool TryHydrate(
            Renderer renderer,
            MeshSnapshot snapshot,
            out ApaProtectedMeshLease lease,
            out ValidationIssue issue)
        {
            lease = null;

            if (renderer == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The part root has no renderer to hydrate a protected mesh onto.",
                    detail: "reason=missing-part-renderer");
                return false;
            }

            if (!(renderer is SkinnedMeshRenderer) && renderer.GetComponent<MeshFilter>() == null)
            {
                issue = ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "Renderer '" + renderer.name + "' has neither a SkinnedMeshRenderer nor a MeshFilter, so a " +
                    "protected mesh cannot be attached to it.",
                    detail: "reason=unsupported-part-renderer; renderer=" + renderer.name);
                return false;
            }

            var mesh = TryBuildTransientMesh(snapshot, out issue);
            if (mesh == null) return false;

            try
            {
                ApaProtectedMeshLease.AssignMesh(renderer, mesh);
            }
            catch (Exception e)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                issue = ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Attributes,
                    "Attaching the transient protected mesh to '" + renderer.name + "' failed: " + e.Message,
                    detail: "reason=hydration-attach-failed; exception=" + e.GetType().FullName);
                return false;
            }

            lease = new ApaProtectedMeshLease(renderer, mesh);
            return true;
        }

        private static List<T> ToList<T>(IReadOnlyList<T> values)
        {
            var result = new List<T>(values.Count);
            for (var i = 0; i < values.Count; i++) result.Add(values[i]);
            return result;
        }

        private static T[] ToArray<T>(IReadOnlyList<T> values)
        {
            var result = new T[values.Count];
            for (var i = 0; i < values.Count; i++) result[i] = values[i];
            return result;
        }
    }
}

using System;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>Where a preview vertex array came from.</summary>
    public enum ApaPreviewPositionSource
    {
        /// <summary>The mesh's own <c>vertices</c> array: the rest/bind pose the build reads.</summary>
        RestPose = 0,

        /// <summary>The renderer's current evaluated geometry, baked from its pose and blend-shape weights.</summary>
        Evaluated = 1
    }

    /// <summary>
    /// Pure decisions behind the Scene View preview's evaluated-geometry overlay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem.</b> A <see cref="SkinnedMeshRenderer"/> draws its <i>deformed</i> geometry: the current
    /// pose, plus every non-zero blend-shape weight. The authoring overlay used to draw the mesh's
    /// <c>vertices</c> — the rest pose — so with an active blend shape the red predicted-removal triangles sat
    /// visibly beside the surface they describe. The overlay and the picking path therefore read the renderer's
    /// current evaluated geometry instead.
    /// </para>
    /// <para>
    /// <b>What this does not change.</b> Seam generation and every build-time decision stay on the rest pose:
    /// the mesh that is assembled is built from the bind geometry, and a pairing derived from a posed body would
    /// move the moment the pose changed. Only the preview reads evaluated positions, and the two are never mixed
    /// inside one decision.
    /// </para>
    /// <para>
    /// <b>When the evaluated geometry is needed.</b> Only when the renderer actually deforms through blend
    /// shapes (<see cref="HasActiveBlendShapeWeights"/>). With every weight at zero the evaluated geometry is the
    /// rest pose, so baking would cost a full deformed-vertex copy per repaint to produce the same positions. The
    /// functions here are pure so the decision is testable without a Scene View, a repaint, or a graphics device.
    /// </para>
    /// </remarks>
    public static class ApaPreviewGeometry
    {
        /// <summary>FNV-1a 64-bit offset basis, the accumulator this package uses everywhere.</summary>
        public const ulong OffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64-bit prime.</summary>
        public const ulong Prime = 1099511628211UL;

        /// <summary>
        /// True when the renderer is a skinned renderer of this mesh with at least one non-zero blend-shape
        /// weight, so its drawn geometry differs from the mesh's rest pose.
        /// </summary>
        public static bool NeedsEvaluatedGeometry(Renderer renderer, Mesh mesh)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null || mesh == null) return false;
            if (skinned.sharedMesh != mesh) return false;
            return HasActiveBlendShapeWeights(skinned, mesh);
        }

        /// <summary>
        /// True when any blend-shape weight of the renderer's own mesh is non-zero.
        /// </summary>
        /// <remarks>
        /// The renderer must be the one that carries <paramref name="mesh"/>: <c>GetBlendShapeWeight</c> answers
        /// for the renderer's own mesh, so asking it about a mesh it does not hold would compare unrelated
        /// values. A negative weight counts as active, because it deforms the mesh exactly as a positive one
        /// does.
        /// </remarks>
        public static bool HasActiveBlendShapeWeights(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            if (renderer == null || mesh == null) return false;
            if (renderer.sharedMesh != mesh) return false;

            var count = mesh.blendShapeCount;
            for (var shape = 0; shape < count; shape++)
            {
                if (renderer.GetBlendShapeWeight(shape) != 0f) return true;
            }

            return false;
        }

        /// <summary>
        /// A deterministic fingerprint of the renderer's blend-shape weights and bone pose, used as the cache key
        /// of the baked geometry.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The fingerprint answers "is the cached bake still describing what the renderer draws?" without
        /// allocating: a weight change or a moved bone must invalidate the bake, while moving the
        /// <i>renderer's own transform</i> must not, because baked vertices are in the renderer's local space and
        /// the transform is applied when they are drawn.
        /// </para>
        /// <para>
        /// Floats are hashed through <see cref="BitConverter.DoubleToInt64Bits"/>, the same convention
        /// <c>ApaFingerprintBuilder</c> uses: the mapping is fixed by the framework and independent of
        /// <c>float.GetHashCode</c>.
        /// </para>
        /// <para>
        /// Every bone's world matrix is included because a posed skeleton is what the bake evaluates. This makes a
        /// spine or hand edit invalidate the cache immediately, not only an edit to the root bone; the explicit
        /// <c>Invalidate</c> remains available for undo and selection changes that alter mesh state without a pose
        /// key changing.
        /// </para>
        /// </remarks>
        public static ulong Fingerprint(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var hash = OffsetBasis;
            if (renderer == null || mesh == null) return hash;

            hash = Mix(hash, (ulong)(uint)mesh.blendShapeCount);
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(renderer.GetBlendShapeWeight(shape)));
            }

            var bones = renderer.bones;
            var boneCount = bones != null ? bones.Length : 0;
            hash = Mix(hash, (ulong)(uint)boneCount);
            for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                var bone = bones[boneIndex];
                var pose = bone != null ? bone.localToWorldMatrix : Matrix4x4.zero;
                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 4; column++)
                    {
                        hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(pose[row, column]));
                    }
                }
            }

            return hash;
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            unchecked
            {
                return (hash ^ value) * Prime;
            }
        }
    }

    /// <summary>
    /// Bakes a skinned renderer's current evaluated geometry into local-space vertex positions.
    /// </summary>
    /// <remarks>
    /// An interface so the cache below can be tested without a graphics device, a posed rig, or a repaint: the
    /// production implementation is <see cref="ApaSkinnedMeshBaker"/>, and a test supplies a deterministic
    /// stand-in.
    /// </remarks>
    public interface IApaEvaluatedMeshBaker
    {
        /// <summary>
        /// Writes the renderer's evaluated vertex positions, in the renderer's local space, into
        /// <paramref name="localPositions"/>. False when the renderer cannot be baked right now.
        /// </summary>
        bool TryBakeLocalPositions(SkinnedMeshRenderer renderer, Mesh mesh, out Vector3[] localPositions);
    }

    /// <summary>
    /// The production baker: one reusable destination mesh and <see cref="SkinnedMeshRenderer.BakeMesh"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The destination mesh belongs to this object</b> and is created with
    /// <see cref="HideFlags.HideAndDontSave"/>, so it is never saved into a scene and never shows up in the
    /// Project window. It is reused across bakes because <c>BakeMesh</c> overwrites its contents; it is disposed
    /// with <see cref="Dispose"/> so closing the authoring window does not leave a native mesh behind.
    /// </para>
    /// <para>
    /// <b>The bake is requested without the transform's scale</b> (<c>useScale: false</c>), which keeps the
    /// vertices in the same local space as the mesh's own <c>vertices</c> array: the caller then maps them to
    /// world space with <c>transform.localToWorldMatrix</c>, exactly as the rest-pose path does. Neither the
    /// source mesh nor the renderer is modified — <c>BakeMesh</c> only reads them.
    /// </para>
    /// </remarks>
    public sealed class ApaSkinnedMeshBaker : IApaEvaluatedMeshBaker, IDisposable
    {
        /// <summary>Name of the transient destination mesh, so a leak is identifiable in a memory report.</summary>
        public const string BakedMeshName = "ApaPreviewBakedMesh";

        private Mesh _baked;

        /// <inheritdoc />
        public bool TryBakeLocalPositions(SkinnedMeshRenderer renderer, Mesh mesh, out Vector3[] localPositions)
        {
            localPositions = null;
            if (renderer == null || mesh == null) return false;

            // The renderer's own mesh is what BakeMesh evaluates; a different mesh would be baked against the
            // wrong blend shape list, so the caller's mesh must be the one the renderer holds.
            if (renderer.sharedMesh != mesh) return false;

            try
            {
                if (_baked == null)
                {
                    _baked = new Mesh
                    {
                        name = BakedMeshName,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }

                renderer.BakeMesh(_baked, false);

                var vertices = _baked.vertices;
                if (vertices == null || vertices.Length != mesh.vertexCount) return false;

                localPositions = vertices;
                return true;
            }
            catch (Exception)
            {
                // A renderer the editor cannot bake — destroyed mid-repaint, no graphics device, a mesh state the
                // bake refuses — must degrade to the rest-pose overlay rather than throw out of a Scene View
                // repaint. The caller sees "not evaluated" and falls back.
                return false;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_baked == null) return;

            UnityEngine.Object.DestroyImmediate(_baked);
            _baked = null;
        }
    }

    /// <summary>
    /// Caches the preview positions of one renderer's mesh: evaluated geometry when the renderer deforms through
    /// blend shapes, the mesh's rest-pose vertices otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One cache per renderer role.</b> The Scene View tool holds one for the target body and one for the
    /// part, because a single repaint reads both and a shared cache would miss on every switch and re-bake — or
    /// re-read — both meshes every frame.
    /// </para>
    /// <para>
    /// <b>The rest-pose half is delegated</b> to the <see cref="ApaMeshArrayCache"/> the picking path already
    /// uses, so the overlay, the hover pick, and the click pick read one array per mesh instead of three copies
    /// of it.
    /// </para>
    /// <para>
    /// <b>Invalidation.</b> The evaluated positions are re-baked when the renderer instance, the mesh instance,
    /// the vertex count, the blend-shape weights, or any bone's pose change
    /// (<see cref="ApaPreviewGeometry.Fingerprint"/>), and when <see cref="Invalidate"/> is called — which the
    /// window does on undo and on a selection change, because an undo can change a mesh or a weight without
    /// changing any of those keys. A bake that fails, or that returns a vertex count other than the mesh's, is
    /// never cached and the caller falls back to the rest pose: an overlay drawn at the bind pose is exactly what
    /// this class replaced, so degrading to it is always safe.
    /// </para>
    /// <para>
    /// <b>An unreadable or missing mesh reports false</b> and nothing is cached, which is the same rule the rest
    /// of the tool follows: a mesh that cannot be read cannot be highlighted or picked.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewPositionCache : IDisposable
    {
        private readonly ApaMeshArrayCache _restPose;
        private readonly IApaEvaluatedMeshBaker _baker;
        private readonly bool _ownsBaker;

        private bool _evaluatedValid;
        private int _rendererInstanceId;
        private int _meshInstanceId;
        private int _vertexCount;
        private ulong _fingerprint;
        private Vector3[] _evaluatedPositions;

        /// <summary>Creates a cache.</summary>
        /// <param name="baker">
        /// The evaluated-geometry baker, or null for <see cref="ApaSkinnedMeshBaker"/>. A caller-supplied baker is
        /// not disposed by this cache.
        /// </param>
        /// <param name="restPose">
        /// The rest-pose array cache to delegate to, or null for a private one. Passing the cache the picking
        /// path already uses is what keeps one vertex array per mesh.
        /// </param>
        public ApaPreviewPositionCache(IApaEvaluatedMeshBaker baker = null, ApaMeshArrayCache restPose = null)
        {
            _ownsBaker = baker == null;
            _baker = baker ?? new ApaSkinnedMeshBaker();
            _restPose = restPose ?? new ApaMeshArrayCache();
        }

        /// <summary>
        /// The positions to draw and pick with, and which of the two sources supplied them.
        /// </summary>
        /// <returns>False for a null, unreadable, or empty mesh; nothing is cached in that case.</returns>
        public bool TryRead(
            Renderer renderer,
            Mesh mesh,
            out Vector3[] positions,
            out ApaPreviewPositionSource source)
        {
            positions = null;
            source = ApaPreviewPositionSource.RestPose;

            if (mesh == null || !mesh.isReadable) return false;

            if (TryReadEvaluated(renderer, mesh, out positions))
            {
                source = ApaPreviewPositionSource.Evaluated;
                return true;
            }

            return _restPose.TryRead(mesh, out positions, out _);
        }

        /// <summary>Drops the cached evaluated positions; the rest-pose arrays are owned by their own cache.</summary>
        public void Invalidate()
        {
            _evaluatedValid = false;
            _evaluatedPositions = null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Invalidate();
            if (_ownsBaker && _baker is IDisposable disposable) disposable.Dispose();
        }

        private bool TryReadEvaluated(Renderer renderer, Mesh mesh, out Vector3[] positions)
        {
            positions = null;

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null) return false;
            if (skinned.sharedMesh != mesh) return false;
            if (!ApaPreviewGeometry.HasActiveBlendShapeWeights(skinned, mesh)) return false;

            var rendererInstanceId = skinned.GetInstanceID();
            var meshInstanceId = mesh.GetInstanceID();
            var fingerprint = ApaPreviewGeometry.Fingerprint(skinned, mesh);

            if (_evaluatedValid
                && _evaluatedPositions != null
                && _rendererInstanceId == rendererInstanceId
                && _meshInstanceId == meshInstanceId
                && _vertexCount == mesh.vertexCount
                && _fingerprint == fingerprint)
            {
                positions = _evaluatedPositions;
                return true;
            }

            if (!_baker.TryBakeLocalPositions(skinned, mesh, out var baked)) return false;
            if (baked == null || baked.Length != mesh.vertexCount) return false;

            _evaluatedPositions = baked;
            _rendererInstanceId = rendererInstanceId;
            _meshInstanceId = meshInstanceId;
            _vertexCount = mesh.vertexCount;
            _fingerprint = fingerprint;
            _evaluatedValid = true;
            positions = baked;
            return true;
        }
    }
}

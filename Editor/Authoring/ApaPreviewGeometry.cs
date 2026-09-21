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
    /// <c>vertices</c> — the rest pose — so with an active blend shape, or merely a moved bone, the discs sat
    /// visibly beside the surface they describe. Every overlay therefore reads the renderer's current evaluated
    /// geometry instead.
    /// </para>
    /// <para>
    /// <b>What this does not change.</b> Seam generation and every build-time decision stay on the rest pose:
    /// the mesh that is assembled is built from the bind geometry, and a pairing derived from a posed body would
    /// move the moment the pose changed. Only the preview reads evaluated positions, and the two are never mixed
    /// inside one decision: an index or a classification always comes from the bind pose, and only the coordinate
    /// it is drawn at follows the pose.
    /// </para>
    /// <para>
    /// <b>When the evaluated geometry is needed.</b> Whenever the renderer is a <see cref="SkinnedMeshRenderer"/>,
    /// not only when a blend shape is active: its bones are evaluated on every frame, so a bone that was moved,
    /// rotated, or scaled changes the drawn position of a vertex even with every blend-shape weight at zero, and
    /// the mesh's own <c>vertices</c> are only what is drawn while the skeleton happens to sit in its bind pose.
    /// The result is cached (<see cref="ApaPreviewPositionCache"/>), so the cost is one deformed-vertex copy per
    /// pose change rather than one per repaint. The functions here are pure so the decision is testable without a
    /// Scene View, a repaint, or a graphics device.
    /// </para>
    /// </remarks>
    public static class ApaPreviewGeometry
    {
        /// <summary>FNV-1a 64-bit offset basis, the accumulator this package uses everywhere.</summary>
        public const ulong OffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64-bit prime.</summary>
        public const ulong Prime = 1099511628211UL;

        /// <summary>
        /// The blend-shape section of a fingerprint for a renderer that may not be asked for weights, folded in
        /// place of a count and a weight list.
        /// </summary>
        /// <remarks>
        /// The live section always begins with the mesh's blend-shape count, a <see cref="uint"/> that can never be
        /// <see cref="ulong.MaxValue"/>, so the marker is unreachable from the live half: a renderer that loses its
        /// mesh (a live bake becoming a protected solve) cannot reuse an entry keyed the other way.
        /// </remarks>
        public const ulong NoBlendShapeWeightsRead = ulong.MaxValue;

        /// <summary>
        /// True when the renderer draws geometry that differs from the mesh's rest pose, so the overlay has to
        /// read the evaluated positions instead of the mesh's own <c>vertices</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A skinned renderer always qualifies.</b> Its drawn vertices are the current pose: a bone that was
        /// moved, rotated, or scaled moves them even when every blend-shape weight is zero. Gating this on an
        /// active blend shape (which the first version of this decision did) therefore left the overlay at the
        /// bind pose for exactly the case the author was looking at — a posed body with no gesture applied.
        /// </para>
        /// <para>
        /// <b>Two meshes qualify.</b> The renderer's own mesh is the ordinary case. A <c>null</c>
        /// <c>sharedMesh</c> is a protected part: the prefab is saved with no mesh on purpose and the geometry is
        /// the transient decode, which the renderer would draw if it held it. Any other mesh is refused, because a
        /// mesh the renderer does not hold cannot be evaluated against its blend shape list or its bones.
        /// </para>
        /// <para>
        /// A non-skinned renderer keeps <c>vertices + localToWorld</c>: it has no bones and no weights, so its
        /// drawn geometry is its mesh's geometry.
        /// </para>
        /// </remarks>
        public static bool NeedsEvaluatedGeometry(Renderer renderer, Mesh mesh)
        {
            if (mesh == null) return false;

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null) return false;

            return skinned.sharedMesh == mesh || skinned.sharedMesh == null;
        }

        /// <summary>
        /// True when the renderer may be asked for blend-shape weights at all: only for the mesh it currently
        /// holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The index contract is defined against the renderer's own mesh.</b> Unity documents
        /// <c>GetBlendShapeWeight(index)</c> as requiring an index smaller than the <c>blendShapeCount</c> of the
        /// mesh <i>attached to that renderer</i>. A renderer holding no mesh therefore has no index that satisfies
        /// it, and a protected part is exactly that state: the prefab is saved with <c>sharedMesh == null</c> on
        /// purpose and its geometry is a transient decode the renderer never receives. Asking such a renderer for
        /// the decode's weights is a call outside the API's contract, made from inside a Scene View repaint, which
        /// is precisely what must not happen.
        /// </para>
        /// <para>
        /// One predicate answers that question for both the weight query
        /// (<see cref="HasActiveBlendShapeWeights"/>) and the cache fingerprint (<see cref="Fingerprint"/>), so
        /// "which renderer may be asked" cannot drift between the two.
        /// </para>
        /// </remarks>
        public static bool CanReadBlendShapeWeights(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            return renderer != null && mesh != null && renderer.sharedMesh == mesh;
        }

        /// <summary>
        /// True when any blend-shape weight of the renderer's own mesh is non-zero.
        /// </summary>
        /// <remarks>
        /// The renderer must be the one that carries <paramref name="mesh"/>
        /// (<see cref="CanReadBlendShapeWeights"/>): <c>GetBlendShapeWeight</c> answers for the renderer's own
        /// mesh, so a mesh it does not hold — a protected part's transient decode in particular — is never queried.
        /// A negative weight counts as active, because it deforms the mesh exactly as a positive one does.
        /// </remarks>
        public static bool HasActiveBlendShapeWeights(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            if (!CanReadBlendShapeWeights(renderer, mesh)) return false;

            var count = mesh.blendShapeCount;
            for (var shape = 0; shape < count; shape++)
            {
                if (renderer.GetBlendShapeWeight(shape) != 0f) return true;
            }

            return false;
        }

        /// <summary>
        /// A deterministic fingerprint of the renderer's blend-shape weights and bone pose, used as the cache key
        /// of the evaluated positions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The fingerprint answers "is the cached evaluation still describing what the renderer draws?" without
        /// allocating: a weight change, a moved bone, or a change to the renderer's own transform must invalidate
        /// it, because the cached positions are expressed in that renderer-local space.
        /// </para>
        /// <para>
        /// Floats are hashed through <see cref="BitConverter.DoubleToInt64Bits"/>, the same convention
        /// <c>ApaFingerprintBuilder</c> uses: the mapping is fixed by the framework and independent of
        /// <c>float.GetHashCode</c>.
        /// </para>
        /// <para>
        /// <b>The renderer's own space is part of the key.</b> The evaluated positions are local to the renderer —
        /// Unity documents a <c>BakeMesh</c> result as "relative to the SkinnedMeshRenderer Transform component",
        /// and the protected solver inverts that same transform — so the same world point has different local
        /// coordinates under a different transform. A renderer that was moved or scaled while its bones stayed
        /// where they were would otherwise be served the array from the old space and draw it through the new
        /// matrix: at three times the scale, a disc three times too far from the mesh. Moving the whole rig
        /// invalidates through the bones anyway; this section is what covers the renderer moving on its own.
        /// </para>
        /// <para>
        /// Every bone's world matrix is included because a posed skeleton is what the evaluation reads — a bake
        /// for a live mesh, the solver for a protected one. This makes a
        /// spine or hand edit invalidate the cache immediately, not only an edit to the root bone; the explicit
        /// <c>Invalidate</c> remains available for undo and selection changes that alter mesh state without a pose
        /// key changing.
        /// </para>
        /// <para>
        /// <b>Blend-shape weights are read only from a renderer that holds the mesh</b>
        /// (<see cref="CanReadBlendShapeWeights"/>): <c>GetBlendShapeWeight</c>'s index contract is defined against
        /// the renderer's own mesh, and a protected part's renderer holds none. That half of the key is then
        /// <see cref="NoBlendShapeWeightsRead"/>, which is also the truth of the protected path: the in-memory
        /// solver (<see cref="ApaPreviewSkinning"/>) deforms by bones only, so no weight could change the positions
        /// the cache stores. The mesh instance and its vertex count stay part of the cache key, so a different
        /// decode still invalidates the entry.
        /// </para>
        /// </remarks>
        public static ulong Fingerprint(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var hash = OffsetBasis;
            if (renderer == null || mesh == null) return hash;

            hash = FingerprintBlendShapeWeights(hash, renderer, mesh);

            // The space the positions are expressed in. It is hashed before the bones because it is the frame the
            // bones are read against, and because a transform-only edit has to invalidate on its own.
            var transform = renderer.transform;
            hash = MixMatrix(hash, transform != null ? transform.localToWorldMatrix : Matrix4x4.zero);

            var bones = renderer.bones;
            var boneCount = bones != null ? bones.Length : 0;
            hash = Mix(hash, (ulong)(uint)boneCount);
            for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                var bone = bones[boneIndex];
                hash = MixMatrix(hash, bone != null ? bone.localToWorldMatrix : Matrix4x4.zero);
            }

            return hash;
        }

        /// <summary>
        /// Folds the blend-shape weights of the renderer's own mesh into the fingerprint, or
        /// <see cref="NoBlendShapeWeightsRead"/> when the renderer may not be asked.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The guard comes first, and it is the reason this is a method at all.</b> A protected part's renderer
        /// holds no mesh (<c>sharedMesh == null</c>) while the caller supplies the transient decode, so
        /// <c>mesh.blendShapeCount</c> describes a mesh the renderer cannot answer for; the weight query is never
        /// reached in that state. Keeping the read in one guarded method is what makes the rule checkable:
        /// <see cref="Fingerprint"/> itself contains no weight read at all.
        /// </para>
        /// <para>
        /// The live path is unchanged: the count and every weight are folded in, so an animation or a gesture
        /// preview that changes a weight re-evaluates the drawn positions.
        /// </para>
        /// </remarks>
        private static ulong FingerprintBlendShapeWeights(ulong hash, SkinnedMeshRenderer renderer, Mesh mesh)
        {
            if (!CanReadBlendShapeWeights(renderer, mesh)) return Mix(hash, NoBlendShapeWeightsRead);

            var count = mesh.blendShapeCount;
            hash = Mix(hash, (ulong)(uint)count);
            for (var shape = 0; shape < count; shape++)
            {
                hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(renderer.GetBlendShapeWeight(shape)));
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

        /// <summary>Folds a matrix into the fingerprint, one element at a time, in the package's FNV-1a convention.</summary>
        private static ulong MixMatrix(ulong hash, Matrix4x4 matrix)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(matrix[row, column]));
                }
            }

            return hash;
        }
    }

    /// <summary>
    /// Produces the vertex positions a skinned renderer currently draws, in the renderer's local space.
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
        /// <paramref name="localPositions"/>. False when the renderer cannot be evaluated right now.
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
    /// <b>The bake includes the transform's scale</b> (<c>useScale: true</c>), which is what puts its vertices in
    /// the renderer's own local space: Unity's <c>SkinnedMeshRenderer.BakeMesh</c> documentation states that "the
    /// vertices are relative to the SkinnedMeshRenderer Transform component", and that with <c>useScale</c> true
    /// the bake uses that transform's position, rotation, <i>and scale</i> (with false it uses the position and
    /// rotation but not the scale). The result is therefore exactly the array <c>transform.localToWorldMatrix</c>
    /// maps to the world position the renderer draws — the same space as the mesh's own <c>vertices</c> — so that
    /// matrix applies the scale once and not twice. A scale-free bake would be mapped by it as if the scale had
    /// already been applied, and the overlay would sit beside the mesh exactly as it does for the rest-pose bug
    /// this class exists to fix. Neither the source mesh nor the renderer is modified — <c>BakeMesh</c> only reads
    /// them.
    /// </para>
    /// <para>
    /// <b>A renderer that holds no mesh is solved instead of baked.</b> That is the protected part: the prefab is
    /// saved with an empty <c>sharedMesh</c> on purpose, so there is nothing for <c>BakeMesh</c> to evaluate, and
    /// assigning the decoded mesh to the scene renderer in order to bake it would write to the prefab instance.
    /// <see cref="ApaPreviewSkinning"/> solves the same pose from the decoded mesh's bind poses and weights and
    /// the renderer's bones, in the same local space, without touching any scene object.
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
            if (renderer.sharedMesh == mesh) return TryBakeHeldMesh(renderer, mesh, out localPositions);

            // A protected part carries no mesh at all: the geometry is the transient decode, and the renderer is
            // the only object that knows the skeleton it was authored against. The pose is solved in memory.
            if (renderer.sharedMesh == null)
            {
                return ApaPreviewSkinning.TrySolveLocalPositions(renderer, mesh, out localPositions);
            }

            return false;
        }

        private bool TryBakeHeldMesh(
            SkinnedMeshRenderer renderer,
            Mesh mesh,
            out Vector3[] localPositions)
        {
            localPositions = null;

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

                // useScale: true — the result is in the renderer's own local space, so the caller's
                // localToWorldMatrix maps it to the world position the renderer actually draws.
                renderer.BakeMesh(_baked, true);

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
    /// Solves the skinned pose of a mesh a renderer does not hold, without writing to the scene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a solver exists.</b> A protected part's prefab is saved with its renderer carrying no mesh: the
    /// geometry lives in the payload and is decoded into a transient mesh the renderer never receives.
    /// <see cref="SkinnedMeshRenderer.BakeMesh"/> can only bake the mesh a renderer holds, and assigning the
    /// decoded mesh to the scene renderer to bake it would dirty the prefab instance — the one write this feature
    /// must never perform. The pose is therefore solved from the same three inputs Unity's own skinning uses: the
    /// mesh's bind poses, its bone weights, and the bones' current world matrices. Nothing is created, assigned,
    /// parented, or saved; the only allocation is the returned array.
    /// </para>
    /// <para>
    /// <b>The result is in the renderer's own local space, exactly like a <c>BakeMesh(…, useScale: true)</c>
    /// result.</b> A caller maps the positions to world space with the renderer's <c>localToWorldMatrix</c>,
    /// precisely as it maps a bake or the mesh's rest-pose <c>vertices</c>, so a scaled renderer's discs still
    /// land on the surface. One rule therefore covers the candidate overlay, the merge check, the stored seam,
    /// and the removal overlay, and the solver cannot place a protected part's discs in a different space than
    /// the bake places a live one's. Being local to the renderer, the returned array is tied to the transform that
    /// produced it — the same world point has different coordinates under a scaled renderer — which is why the
    /// evaluated-position cache keys on that transform as well as on the bones.
    /// </para>
    /// <para>
    /// <b>Nothing is written, not even for the duration of a call.</b> The solver reads the mesh's arrays and the
    /// bones' world matrices and returns a new array. It never assigns <c>sharedMesh</c> — not temporarily, not
    /// inside a <c>try</c>/<c>finally</c> — never calls <c>BakeMesh</c>, never creates, parents, or activates a
    /// scene object, and never touches <c>AssetDatabase</c>. The renderer is therefore left exactly as the
    /// protected prefab saved it, with no mesh and no dirty flag, and this path creates no mesh at all, transient
    /// or persistent. That is the whole reason it exists instead of a bake:
    /// <see cref="SkinnedMeshRenderer.BakeMesh"/> can only evaluate the mesh a renderer holds.
    /// </para>
    /// <para>
    /// <b>Blend shapes are not re-applied.</b> The payload's blend shapes reach the assembled mesh through the
    /// normal pipeline, and the overlay's question is where a <i>vertex index</i> currently is. A protected
    /// prefab's renderer weights are the ones an animation would drive rather than ones the author set while
    /// authoring the seam, and solving the bone pose is what keeps the discs on the surface the author is looking
    /// at — strictly more than the rest pose the protected path drew before. This is also why the evaluated cache's
    /// key carries no blend-shape weight for this path: no weight is read, and none could change the result.
    /// </para>
    /// <para>
    /// <b>Degenerate inputs are answered, never thrown.</b> A mesh with no skinning data, a vertex no usable bone
    /// influences, and a bone entry that is null or beyond the bind-pose list all have defined answers: the vertex
    /// keeps its own position, because a vertex with no influence has nothing to follow and inventing a transform
    /// for it would move a disc off the geometry it names.
    /// </para>
    /// </remarks>
    public static class ApaPreviewSkinning
    {
        /// <summary>
        /// Writes the mesh's skinned vertex positions, in the renderer's own local space, into
        /// <paramref name="localPositions"/>. False when the mesh cannot be read or carries no vertices.
        /// </summary>
        public static bool TrySolveLocalPositions(
            SkinnedMeshRenderer renderer,
            Mesh mesh,
            out Vector3[] localPositions)
        {
            localPositions = null;
            if (renderer == null || mesh == null || !mesh.isReadable) return false;

            Vector3[] vertices;
            BoneWeight[] weights;
            Matrix4x4[] bindPoses;
            try
            {
                vertices = mesh.vertices;
                weights = mesh.boneWeights;
                bindPoses = mesh.bindposes;
            }
            catch (Exception)
            {
                // A mesh the editor refuses to read mid-repaint degrades to the rest pose rather than throwing
                // out of a Scene View repaint, exactly as a failed bake does.
                return false;
            }

            if (vertices == null || vertices.Length == 0) return false;

            // No skinning data at all: nothing deforms the mesh, so its own vertices are what would be drawn.
            if (weights == null || weights.Length != vertices.Length || bindPoses == null || bindPoses.Length == 0)
            {
                localPositions = vertices;
                return true;
            }

            if (!TryBuildSkinMatrices(renderer, bindPoses, out var skinMatrices, out var usable)) return false;

            var solved = new Vector3[vertices.Length];
            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                var weight = weights[vertex];
                var source = vertices[vertex];
                var position = Vector3.zero;
                var total = 0f;

                Accumulate(skinMatrices, usable, weight.boneIndex0, weight.weight0, source, ref position, ref total);
                Accumulate(skinMatrices, usable, weight.boneIndex1, weight.weight1, source, ref position, ref total);
                Accumulate(skinMatrices, usable, weight.boneIndex2, weight.weight2, source, ref position, ref total);
                Accumulate(skinMatrices, usable, weight.boneIndex3, weight.weight3, source, ref position, ref total);

                // Unity's skinning blends the influences without renormalizing, so the stored weights are
                // consumed as they are. Only a vertex no usable bone influences falls back to its own position.
                solved[vertex] = total != 0f ? position : source;
            }

            localPositions = solved;
            return true;
        }

        /// <summary>
        /// Builds each bone's renderer-local skin matrix, and records which entries are usable.
        /// </summary>
        /// <remarks>
        /// The composition is <c>rendererWorldToLocal * boneWorld * bindPose</c>, the standard skin matrix, which
        /// lands in the renderer's own local space — the same space a <c>BakeMesh(…, useScale: true)</c> result
        /// and the mesh's own <c>vertices</c> are in. A bone that is null, or whose index is beyond the
        /// renderer's bone list, is marked unusable rather than substituted: a substituted transform would place
        /// the vertices of that influence somewhere the rig never described.
        /// </remarks>
        private static bool TryBuildSkinMatrices(
            SkinnedMeshRenderer renderer,
            Matrix4x4[] bindPoses,
            out Matrix4x4[] skinMatrices,
            out bool[] usable)
        {
            skinMatrices = null;
            usable = null;

            var transform = renderer.transform;
            if (transform == null) return false;

            // The renderer's own inverse, including its scale: the caller maps the result back with
            // localToWorldMatrix, so the two cancel and the disc lands where the renderer draws the vertex.
            var worldToLocal = transform.worldToLocalMatrix;

            var bones = renderer.bones;
            skinMatrices = new Matrix4x4[bindPoses.Length];
            usable = new bool[bindPoses.Length];

            for (var bone = 0; bone < bindPoses.Length; bone++)
            {
                var boneTransform = bones != null && bone < bones.Length ? bones[bone] : null;
                if (boneTransform == null) continue;

                skinMatrices[bone] = worldToLocal * boneTransform.localToWorldMatrix * bindPoses[bone];
                usable[bone] = true;
            }

            return true;
        }

        /// <summary>Folds one bone influence into a vertex, skipping influences that cannot be evaluated.</summary>
        private static void Accumulate(
            Matrix4x4[] skinMatrices,
            bool[] usable,
            int boneIndex,
            float weight,
            Vector3 vertex,
            ref Vector3 position,
            ref float total)
        {
            if (weight == 0f) return;
            if (boneIndex < 0 || boneIndex >= skinMatrices.Length) return;
            if (!usable[boneIndex]) return;

            position += skinMatrices[boneIndex].MultiplyPoint3x4(vertex) * weight;
            total += weight;
        }
    }

    /// <summary>
    /// Caches the preview positions of one renderer's mesh: the evaluated pose of a skinned renderer, the mesh's
    /// rest-pose vertices otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One cache per renderer role.</b> The Scene View tool holds one for the target body and one for the
    /// part, because a single repaint reads both and a shared cache would miss on every switch and re-bake — or
    /// re-read — both meshes every frame.
    /// </para>
    /// <para>
    /// <b>The rest-pose half is delegated</b> to the <see cref="ApaMeshArrayCache"/> the overlays already use, so
    /// a repaint reads one vertex array per mesh rather than one copy per overlay.
    /// </para>
    /// <para>
    /// <b>Every skinned renderer is evaluated, cached by its pose.</b> The evaluated positions are re-solved when
    /// the renderer instance, the mesh instance, the vertex count, the blend-shape weights, the renderer's own
    /// space, or any bone's pose change (<see cref="ApaPreviewGeometry.Fingerprint"/>), and when
    /// <see cref="Invalidate"/> is called — which the window does on undo and on a selection change, because an
    /// undo can change a mesh or a weight without changing any of those keys. A repaint that changed no bone, no
    /// weight, and no transform therefore reuses the array instead of paying for a deformed-vertex copy per frame.
    /// A bake that fails, or that returns a vertex count other than the mesh's, is never cached and the caller
    /// falls back to the rest pose: an overlay drawn at the bind pose is exactly what this class replaced, so
    /// degrading to it is always safe.
    /// </para>
    /// <para>
    /// <b>A protected part's renderer holds no mesh</b> and the caller supplies the transient decode. The
    /// evaluated half then comes from <see cref="ApaPreviewSkinning"/> rather than from a bake, because the
    /// decoded mesh is never attached to the scene renderer, and that difference is part of the key: the
    /// fingerprint's blend-shape section is <see cref="ApaPreviewGeometry.NoBlendShapeWeightsRead"/> for this
    /// state instead of a weight list, because a renderer holding no mesh may not be asked for weights at all
    /// (<see cref="ApaPreviewGeometry.CanReadBlendShapeWeights"/>) and the solver applies none. A renderer that
    /// gains or loses its mesh therefore cannot reuse an entry computed the other way.
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

            // A skinned renderer's drawn vertices are its current pose, so the evaluated positions are the ones
            // the overlay must use — a moved bone changes them with every blend-shape weight at zero. A mesh the
            // renderer does not hold (and that is not the protected null-mesh case) is refused by the baker.
            if (!ApaPreviewGeometry.NeedsEvaluatedGeometry(skinned, mesh)) return false;

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

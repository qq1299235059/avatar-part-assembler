using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Reads Unity meshes and renderers into immutable snapshots.
    /// </summary>
    /// <remarks>
    /// This is the only place in the pipeline that touches a live Unity mesh, and it only reads. Everything
    /// downstream operates on the copies, which is what makes the non-mutation guarantee structural rather
    /// than a matter of discipline.
    /// </remarks>
    public static class MeshSnapshotFactory
    {
        /// <summary>
        /// Reads a mesh into a snapshot. Returns null when the mesh is null or unreadable.
        /// </summary>
        /// <param name="mesh">The mesh to read. May be null.</param>
        /// <param name="bonePaths">
        /// Avatar-root-relative bone paths for the renderer that owns this mesh, or null when the source has no
        /// bone data. A bone that is the avatar root is recorded as <see cref="ApaAvatarPath.Root"/>, and a null
        /// bone entry as the empty string, which means "missing". Supplied by the caller because resolving a
        /// bone path needs the renderer transform and the avatar root, which a bare mesh does not know about.
        /// </param>
        /// <param name="issues">Receives capture diagnostics.</param>
        /// <param name="boneWorldToLocalMatrices">
        /// Each bone's <c>worldToLocalMatrix</c>, in bone order, from <see cref="CaptureBoneWorldToLocalMatrices"/>.
        /// Null or empty for a mesh whose renderer is not skinned.
        /// </param>
        public static MeshSnapshot Capture(Mesh mesh, IReadOnlyList<string> bonePaths, out List<ValidationIssue> issues,
            IReadOnlyList<Matrix4x4> boneWorldToLocalMatrices = null)
        {
            issues = new List<ValidationIssue>();
            if (mesh == null) return null;

            if (!mesh.isReadable)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.UnsupportedMeshAttribute,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + mesh.name + "' is not readable. Enable Read/Write in its import settings.",
                    detail: "mesh=" + mesh.name + "; reason=not-readable"));
                return null;
            }

            var vertices = mesh.vertices ?? Array.Empty<Vector3>();
            var normals = mesh.normals ?? Array.Empty<Vector3>();
            var tangents = mesh.tangents ?? Array.Empty<Vector4>();
            var colors = mesh.colors ?? Array.Empty<Color>();

            var uvs = MeshSnapshot.NewUvArray();
            for (var channel = 0; channel < ApaMeshLimits.MaxUvChannels; channel++)
            {
                uvs[channel] = ReadUvChannel(mesh, channel);
            }

            var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);
            var subMeshes = new int[subMeshCount][];
            var topologies = new MeshTopology[subMeshCount];
            for (var i = 0; i < subMeshCount; i++)
            {
                subMeshes[i] = mesh.GetIndices(i) ?? Array.Empty<int>();
                topologies[i] = mesh.GetTopology(i);
            }

            var blendShapeNames = Array.Empty<string>();
            var blendShapeFrameCounts = Array.Empty<int>();
            var blendShapeFrames = Array.Empty<BlendShapeFrameSnapshot[]>();
            var blendShapeCount = mesh.blendShapeCount;
            if (blendShapeCount > 0)
            {
                blendShapeNames = new string[blendShapeCount];
                blendShapeFrameCounts = new int[blendShapeCount];
                blendShapeFrames = new BlendShapeFrameSnapshot[blendShapeCount][];
                for (var i = 0; i < blendShapeCount; i++)
                {
                    blendShapeNames[i] = mesh.GetBlendShapeName(i) ?? string.Empty;
                    blendShapeFrameCounts[i] = mesh.GetBlendShapeFrameCount(i);
                    blendShapeFrames[i] = new BlendShapeFrameSnapshot[blendShapeFrameCounts[i]];
                    for (var frame = 0; frame < blendShapeFrames[i].Length; frame++)
                    {
                        var dv = new Vector3[mesh.vertexCount];
                        var dn = new Vector3[mesh.vertexCount];
                        var dt = new Vector3[mesh.vertexCount];
                        mesh.GetBlendShapeFrameVertices(i, frame, dv, dn, dt);
                        blendShapeFrames[i][frame] = new BlendShapeFrameSnapshot(
                            mesh.GetBlendShapeFrameWeight(i, frame), dv, dn, dt);
                    }
                }
            }

            var bones = ToArray(boneWorldToLocalMatrices);

            // Ownership of every array above passes to the snapshot: it was created here and is not retained,
            // so the internal constructor is used rather than the copying factory method.
            return new MeshSnapshot(
                mesh.name,
                vertices,
                normals,
                tangents,
                colors,
                uvs,
                subMeshes,
                topologies,
                mesh.bounds,
                mesh.indexFormat,
                mesh.boneWeights ?? Array.Empty<BoneWeight>(),
                mesh.bindposes ?? Array.Empty<Matrix4x4>(),
                blendShapeNames,
                blendShapeFrameCounts,
                new BoneSignature(bonePaths),
                blendShapeFrames,
                bones);
        }

        private static Matrix4x4[] ToArray(IReadOnlyList<Matrix4x4> values)
        {
            if (values == null || values.Count == 0) return Array.Empty<Matrix4x4>();
            var result = new Matrix4x4[values.Count];
            for (var i = 0; i < result.Length; i++) result[i] = values[i];
            return result;
        }

        /// <summary>
        /// Captures each bone's <c>worldToLocalMatrix</c>, in bone order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These are the transforms the final bind pose is computed from (M2, section 44.3): the bind pose is
        /// <c>bone.worldToLocalMatrix * renderer.localToWorldMatrix</c>, evaluated for the final hierarchy.
        /// </para>
        /// <para>
        /// A <b>bind pose is never substituted here.</b> A bind pose is the transform that maps a mesh vertex
        /// into bone space at bind time; a bone's world-to-local matrix is the transform of the bone right now.
        /// They coincide only for a bone at the origin with no rotation, so falling back to
        /// <c>mesh.bindposes</c> would produce a plausible-looking table whose every entry is wrong, and the
        /// avatar would only reveal it once an animation plays (section 21).
        /// </para>
        /// <para>
        /// A null bone is recorded as <see cref="Matrix4x4.zero"/> rather than skipped, so the array stays
        /// index-aligned with the bone signature. <c>zero</c> is not a usable transform and the bone table
        /// builder rejects it with <c>APA011</c> rather than emitting a mesh that cannot deform.
        /// </para>
        /// </remarks>
        public static Matrix4x4[] CaptureBoneWorldToLocalMatrices(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null || skinned.bones == null) return Array.Empty<Matrix4x4>();
            var result = new Matrix4x4[skinned.bones.Length];
            for (var i = 0; i < result.Length; i++)
                result[i] = skinned.bones[i] != null ? skinned.bones[i].worldToLocalMatrix : Matrix4x4.zero;
            return result;
        }

        /// <summary>
        /// Captures the final renderer's <c>localToWorldMatrix</c>, which the bind-pose formula multiplies by.
        /// </summary>
        public static Matrix4x4 CaptureRendererLocalToWorld(Renderer renderer)
        {
            return renderer != null ? renderer.transform.localToWorldMatrix : Matrix4x4.identity;
        }

        private static Vector4[] ReadUvChannel(Mesh mesh, int channel)
        {
            var list = new List<Vector4>();
            try
            {
                mesh.GetUVs(channel, list);
            }
            catch (ArgumentException)
            {
                // A channel index outside the mesh's channel range throws on some Unity versions and returns an
                // empty list on others. Treat both as "channel absent" so behaviour is version-independent.
                return Array.Empty<Vector4>();
            }

            return list.Count > 0 ? list.ToArray() : Array.Empty<Vector4>();
        }

        /// <summary>
        /// Reads the material assignments of a renderer into a stable array. Returns an empty array for a null
        /// renderer. Null entries are preserved, because a missing material is meaningful and must not be
        /// silently compacted away.
        /// </summary>
        public static Material[] CaptureMaterials(Renderer renderer)
        {
            if (renderer == null) return Array.Empty<Material>();
            return renderer.sharedMaterials ?? Array.Empty<Material>();
        }

        /// <summary>
        /// Builds the deterministic bone signature of a renderer, scoped to one armature root.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The signature is the list of bone paths <i>relative to the armature root the caller selected</i>, in
        /// the renderer's own bone order. It is built from transform paths rather than from
        /// <c>GetInstanceID</c>, object references, or a hash of pointers, because a signature has to be
        /// identical on a reimport, in a fresh Editor session, and on a different machine for the comparison to
        /// mean anything.
        /// </para>
        /// <para>
        /// <b>The scope is the identity (M10).</b> The base body's bones are recorded relative to the selected
        /// target armature and the part's bones relative to the selected part armature, so two bones whose
        /// relative paths are equal are the same joint even when the two armatures sit at different depths under
        /// the avatar. The old avatar-root-relative form made a part bone's identity depend on where the part
        /// was placed, which is a placement detail rather than a property of the joint.
        /// </para>
        /// <para>
        /// A null bone entry is recorded as an empty path rather than skipped: skipping it would change the bone
        /// count and silently shift every later bone's index, which is the same class of off-by-one the removal
        /// address fix exists to eliminate. Empty therefore means "this entry has no bone", and it is the one
        /// value the final bone table refuses.
        /// </para>
        /// <para>
        /// A bone that <i>is</i> the armature root records <see cref="ApaAvatarPath.Root"/> instead of an empty
        /// path. The two are not interchangeable: the armature root is a real joint with a real transform, so it
        /// is a valid bone identity and a weight can be remapped onto it, while an empty entry has nothing to
        /// remap onto.
        /// </para>
        /// <para>
        /// A bone that is <b>not</b> inside the armature root also records an empty path. It has no
        /// armature-relative identity, and recording the scene-absolute path it would otherwise receive would
        /// produce an identity that changes the moment the avatar is cloned — the exact failure the
        /// armature-relative scheme removes. Whether such a bone is a defect depends on whether a weight reaches
        /// it, so the check belongs to the caller that knows the weights
        /// (<c>ContextBuilder.ValidateBoneIdentities</c> reports <c>APA044</c> for a <i>weighted</i> one) rather
        /// than to this reader.
        /// </para>
        /// </remarks>
        public static BoneSignature CaptureBoneSignature(Transform armatureRoot, Renderer renderer)
        {
            if (renderer == null) return BoneSignature.Empty;

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null) return BoneSignature.Empty;

            var bones = skinned.bones;
            if (bones == null || bones.Length == 0) return BoneSignature.Empty;

            var paths = new string[bones.Length];
            for (var i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null)
                {
                    paths[i] = string.Empty;
                    continue;
                }

                paths[i] = IsSelfOrDescendant(bone, armatureRoot)
                    ? RelativePath(armatureRoot, bone)
                    : string.Empty;
            }

            return new BoneSignature(paths);
        }

        /// <summary>True when a transform is the root itself or one of its descendants.</summary>
        private static bool IsSelfOrDescendant(Transform transform, Transform root)
        {
            if (transform == null || root == null) return false;
            if (transform == root) return true;
            return transform.IsChildOf(root);
        }

        /// <summary>
        /// Builds the avatar-root-relative path of a transform.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one canonical path routine for the whole binding architecture: renderer paths, bone
        /// signature paths, installer ordering tiebreakers, and the compatibility signature all use it. The path
        /// stops at the avatar root instead of including the avatar's own ancestors, because a full scene path
        /// would stop resolving the moment the avatar is nested below another scene object.
        /// </para>
        /// <para>
        /// Three results, three distinct meanings, and no two of them alias:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <c>null</c> transform → <see cref="string.Empty"/>, meaning <b>missing</b>. Nothing may be resolved
        /// from it and no identity may be invented for it.
        /// </description></item>
        /// <item><description>
        /// the avatar root itself → <see cref="ApaAvatarPath.Root"/> (<c>"."</c>), a valid identity whose
        /// hierarchy lookup is the root transform. <c>Transform.Find</c> reserves <c>"."</c>, so a caller that
        /// resolves a path back to a transform must special-case the token; see
        /// <see cref="ApaAvatarPath"/>.
        /// </description></item>
        /// <item><description>
        /// anything else → the slash-separated path below the root (<c>"Body"</c>, <c>"Armature/Hips"</c>),
        /// exactly what <c>avatarRoot.transform.Find(path)</c> accepts.
        /// </description></item>
        /// </list>
        /// <para>
        /// When the transform is not under the avatar root the absolute path is returned instead of failing,
        /// because a part prefab may legitimately be authored outside the avatar it will be installed into.
        /// </para>
        /// </remarks>
        public static string RelativePath(Transform avatarRoot, Transform target)
        {
            if (target == null) return string.Empty;

            // The avatar root is the one transform whose relative path has no name segments at all, so it needs
            // its own token rather than falling out of the loop as an empty string.
            if (avatarRoot != null && target == avatarRoot) return ApaAvatarPath.Root;

            var segments = new List<string>();
            var current = target;
            while (current != null)
            {
                if (avatarRoot != null && current == avatarRoot) break;
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }

        /// <summary>
        /// Captures the compatibility signature of a mesh and its renderer.
        /// </summary>
        /// <param name="mesh">The target mesh. May be null, in which case an uncaptured signature is returned.</param>
        /// <param name="rendererPath">Avatar-root-relative path of the renderer.</param>
        /// <param name="meshGuid">
        /// Asset GUID of the mesh, or an empty string when the mesh is not a persisted asset. The caller
        /// supplies this because resolving a GUID requires the Editor asset database.
        /// </param>
        /// <param name="boneSignature">
        /// The renderer's bone signature, or null for a mesh without skinning. Captured by
        /// <see cref="CaptureBoneSignature"/> <i>against the selected target armature root</i>, so the recorded
        /// paths are armature-relative identities rather than avatar-root-relative placements.
        /// </param>
        /// <remarks>
        /// Every field this records is compared again before any topology-indexed authoring data is used. The
        /// safety-relevant ones — vertex count, per-submesh index counts and topologies, blend shape names and
        /// frame counts — are recorded unconditionally, including when a mesh GUID is available, because a GUID
        /// identifies an asset rather than its contents (section 43.4).
        /// </remarks>
        public static ApaAvatarCompatibilityProfile CaptureSignature(
            Mesh mesh,
            string rendererPath,
            string meshGuid,
            BoneSignature boneSignature = null)
        {
            var profile = new ApaAvatarCompatibilityProfile
            {
                MeshName = mesh != null ? mesh.name : string.Empty,
                RendererPath = rendererPath ?? string.Empty,
                MeshGuid = meshGuid ?? string.Empty,
                IsCaptured = mesh != null
            };

            if (mesh == null) return profile;

            profile.VertexCount = mesh.vertexCount;

            var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);
            var indexCounts = new int[subMeshCount];
            var topologies = new int[subMeshCount];
            for (var i = 0; i < subMeshCount; i++)
            {
                indexCounts[i] = mesh.GetIndices(i)?.Length ?? 0;
                topologies[i] = (int)mesh.GetTopology(i);
            }

            profile.SubMeshIndexCounts = indexCounts;
            profile.SubMeshTopologyValues = topologies;

            var blendShapeCount = mesh.blendShapeCount;
            var names = new string[blendShapeCount];
            var frames = new int[blendShapeCount];
            for (var i = 0; i < blendShapeCount; i++)
            {
                names[i] = mesh.GetBlendShapeName(i) ?? string.Empty;
                frames[i] = mesh.GetBlendShapeFrameCount(i);
            }

            profile.BlendShapeNames = names;
            profile.BlendShapeFrameCounts = frames;

            profile.BonePaths = boneSignature != null ? ToArray(boneSignature.Paths) : Array.Empty<string>();

            return profile;
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0) return Array.Empty<string>();
            var result = new string[values.Count];
            for (var i = 0; i < values.Count; i++) result[i] = values[i];
            return result;
        }

        /// <summary>
        /// Captures the immutable space matrices for one source renderer, or reports why they are unusable.
        /// </summary>
        /// <param name="avatarRoot">The avatar root transform. Null means the source is already root-relative.</param>
        /// <param name="source">Transform of the renderer whose mesh is being read.</param>
        /// <param name="target">Transform of the renderer the generated mesh replaces.</param>
        /// <param name="partId">Owning part id, or an empty string for the target body.</param>
        /// <param name="who">Human-readable description of the source, used in the diagnostic.</param>
        /// <param name="issues">Receives a blocking diagnostic when the matrices are unusable.</param>
        /// <param name="transforms">Receives the captured matrices on success.</param>
        /// <returns>True when the matrices were captured; false when a blocking issue was added.</returns>
        /// <remarks>
        /// <para>
        /// This is the boundary where live Unity transforms are copied into plain matrices. Nothing downstream
        /// retains a <see cref="Transform"/>, so the context cannot be changed by moving the hierarchy after it
        /// was built.
        /// </para>
        /// <para>
        /// Validation happens here rather than at first use because the normal matrix is derived at capture
        /// time: a singular source-to-target transform has no inverse transpose, and computing one anyway would
        /// put a NaN into every normal and every blend shape delta that reads it.
        /// </para>
        /// </remarks>
        public static bool TryCaptureTransforms(
            Transform avatarRoot,
            Transform source,
            Transform target,
            string partId,
            string who,
            List<ValidationIssue> issues,
            out SpaceTransforms transforms)
        {
            transforms = default;

            if (source == null || target == null)
            {
                issues.Add(InvalidSpaceTransform(partId, who, "missing-transform"));
                return false;
            }

            var sourceToWorld = source.localToWorldMatrix;
            var sourceToAvatar = avatarRoot != null
                ? avatarRoot.worldToLocalMatrix * sourceToWorld
                : sourceToWorld;
            var sourceToTarget = target.worldToLocalMatrix * sourceToWorld;

            if (!SpaceTransforms.TryCreate(sourceToAvatar, sourceToTarget, out transforms, out var reason))
            {
                issues.Add(InvalidSpaceTransform(partId, who, reason));
                return false;
            }

            return true;
        }

        private static ValidationIssue InvalidSpaceTransform(string partId, string who, string reason)
        {
            return ValidationIssue.Error(
                ApaErrorCode.InvalidSpaceTransform,
                ApaIssuePhase.Compatibility,
                "The space transform of " + who + " is not usable, so its geometry, normals, and blend shape " +
                "deltas cannot be mapped into the target renderer's local space. A missing transform, a " +
                "non-finite value, or a zero scale on the part or the target renderer is the usual cause.",
                partId,
                detail: "reason=" + reason + "; source=" + who);
        }
    }
}

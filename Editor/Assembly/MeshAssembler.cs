using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// The result of a mesh build: either a generated mesh, or the diagnostics that prevented one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated mesh is always a new transient object. It is never the caller's mesh, and it is never
    /// assigned to an authoring asset. Ownership passes to the caller, who is responsible for destroying it —
    /// and who can now do so through <see cref="Destroy"/>, which makes the ownership explicit instead of a
    /// comment. Preview must destroy the mesh it replaces; a build lets NDMF persist it.
    /// </para>
    /// <para>
    /// <see cref="Destroy"/> is idempotent and nulls <see cref="Mesh"/>, so a double destroy (a caller's own
    /// cleanup plus a group orchestrator's) is harmless and a later read cannot resurrect a destroyed object.
    /// </para>
    /// </remarks>
    public sealed class AssemblyResult : IDisposable
    {
        private Mesh _mesh;
        private bool _destroyed;

        /// <summary>The generated mesh, or null when the build failed or the mesh was destroyed.</summary>
        public Mesh Mesh => _mesh;

        /// <summary>The final material list matching the generated submeshes.</summary>
        public Material[] Materials { get; }

        /// <summary>The plan the mesh was built from, or null on failure.</summary>
        public MeshAssemblyPlan Plan { get; }

        /// <summary>Diagnostics, in deterministic order.</summary>
        public ValidationResult Issues { get; }

        /// <summary>
        /// The target group this mesh was generated for, or an empty string for a single-target build. Read from
        /// the plan, so the mesh name and the group identity cannot disagree.
        /// </summary>
        public string GroupKey { get; }

        /// <summary>True when a mesh was produced and has not been destroyed.</summary>
        public bool Succeeded => _mesh != null;

        /// <summary>True once <see cref="Destroy"/> has released the generated mesh.</summary>
        public bool IsDestroyed => _destroyed;

        /// <summary>Creates a result.</summary>
        public AssemblyResult(Mesh mesh, Material[] materials, MeshAssemblyPlan plan, ValidationResult issues)
        {
            _mesh = mesh;
            Materials = materials;
            Plan = plan;
            Issues = issues ?? ValidationResult.Empty;
            GroupKey = plan != null ? plan.GroupKey : string.Empty;
        }

        /// <summary>
        /// Releases the generated mesh. Idempotent, and safe to call on a failed result.
        /// </summary>
        /// <remarks>
        /// The caller owns the mesh: a preview filter calls this when it replaces or discards the mesh it was
        /// showing, and a group orchestrator calls it for every group whose mesh was never assigned. Unity does
        /// not collect unreferenced meshes, so the one leak this codebase can produce is a mesh nobody destroys.
        /// </remarks>
        public void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;

            SafeDestroy(_mesh);
            _mesh = null;
        }

        /// <summary>Releases the mesh. Equivalent to <see cref="Destroy"/>.</summary>
        public void Dispose()
        {
            Destroy();
        }

        private static void SafeDestroy(UnityEngine.Object unityObject)
        {
            if (unityObject == null) return;

            // Fully qualified because this file imports both System and UnityEngine, which makes the bare name
            // Object ambiguous.
            if (Application.isPlaying) UnityEngine.Object.Destroy(unityObject);
            else UnityEngine.Object.DestroyImmediate(unityObject);
        }

        /// <summary>A failed result carrying only diagnostics.</summary>
        public static AssemblyResult Failure(ValidationResult issues)
        {
            return new AssemblyResult(null, Array.Empty<Material>(), null, issues);
        }
    }

    /// <summary>
    /// Builds the final mesh from an immutable plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This assembler is the core processor. It is intentionally free of any NDMF, Modular Avatar, or preview
    /// dependency so that the same instance can serve preview and build, which is the only way to guarantee that
    /// what a user sees is what gets uploaded.
    /// </para>
    /// <para>
    /// Attribute handling follows the ownership contract. A welded vertex is the retained base vertex, so the
    /// base owns position, normal, tangent, and color there. UVs are the exception: a semantic present only on
    /// the part takes the part value at the weld, because otherwise the weld would destroy data the base never
    /// had. Every vertex missing a semantic receives <c>Vector4.zero</c>, which defines all four components
    /// rather than leaving <c>w</c> undefined.
    /// </para>
    /// <para>
    /// A seam pair whose same-name UVs disagree is not welded at all (M11): the part seam vertex is emitted as a
    /// preserved vertex that owns every attribute except position, which is snapped to the paired base vertex so
    /// the surface stays closed. Nothing here decides that; the plan does, and this assembler only honours the
    /// provenance it was handed.
    /// </para>
    /// <para>
    /// No normal or tangent recalculation is performed. Toon avatars routinely depend on authored normals, and
    /// the product principle is that the assembler assembles rather than repairs.
    /// </para>
    /// </remarks>
    public sealed class MeshAssembler
    {
        private readonly ISkinningProvider _skinning;
        private readonly IBlendShapeProvider _blendShapes;

        /// <summary>Creates an assembler using the M2 providers, which remap skinning and blend shape data.</summary>
        public MeshAssembler()
            : this(new FinalBoneTableSkinningProvider(), new RemappedBlendShapeProvider())
        {
        }

        /// <summary>
        /// Creates an assembler with explicit providers. Passing null selects the refusing providers, so a
        /// caller can never accidentally disable the no-silent-loss guarantee by omitting one.
        /// </summary>
        public MeshAssembler(ISkinningProvider skinning, IBlendShapeProvider blendShapes)
        {
            _skinning = skinning ?? new BlockingSkinningProvider();
            _blendShapes = blendShapes ?? new BlockingBlendShapeProvider();
        }

        /// <summary>
        /// Builds the final mesh. On failure no mesh is created and no caller object is modified.
        /// </summary>
        public AssemblyResult Build(MeshAssemblyPlan plan, ValidationContext context)
        {
            if (plan == null || context == null)
            {
                return AssemblyResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "The mesh assembler was invoked without a plan or context.",
                    detail: "reason=null-input")));
            }

            try
            {
                return BuildInternal(plan, context);
            }
            catch (Exception e)
            {
                // Report rather than propagate. A thrown exception here would leave the caller unable to tell a
                // failed build from a successful one that produced nothing.
                return AssemblyResult.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "Mesh assembly threw " + e.GetType().Name + ": " + e.Message,
                    detail: "exception=" + e.GetType().FullName)));
            }
        }

        private AssemblyResult BuildInternal(MeshAssemblyPlan plan, ValidationContext context)
        {
            var issues = new List<ValidationIssue>(plan.NonBlockingIssues);

            // Provider output is produced and checked before a single mesh object is allocated. Building first
            // and rejecting afterwards would leave a half-written mesh to destroy and, worse, would tempt a
            // future caller to keep the partial result.
            BoneWeight[] boneWeights = null;
            Matrix4x4[] bindPoses = null;

            if (plan.RequiresSkinning)
            {
                if (!_skinning.CanProvide(plan, context))
                {
                    issues.Add(BlockingSkinningProvider.CreateIssue());
                    return AssemblyResult.Failure(ValidationResult.Build(issues));
                }

                boneWeights = _skinning.BuildBoneWeights(plan, context);
                if (boneWeights != null && boneWeights.Length != plan.VertexCount)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBoneWeight,
                        ApaIssuePhase.Attributes,
                        "The skinning provider returned " + boneWeights.Length + " bone weight(s) for " +
                        plan.VertexCount + " vertices. A weight array must have exactly one entry per vertex.",
                        detail: "reason=provider-weight-count; provider=" + _skinning.GetType().Name));
                    return AssemblyResult.Failure(ValidationResult.Build(issues));
                }

                bindPoses = _skinning.BuildBindPoses(plan, context);
                if (bindPoses == null || bindPoses.Length != plan.BoneTable.Count)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBindPose,
                        ApaIssuePhase.Attributes,
                        "The skinning provider returned " + (bindPoses?.Length ?? 0) + " bind pose(s) for a final " +
                        "bone table of " + plan.BoneTable.Count + " bone(s). Bind poses and bones must line up " +
                        "or every skinned vertex deforms against the wrong transform.",
                        detail: "reason=provider-bindpose-count; provider=" + _skinning.GetType().Name));
                    return AssemblyResult.Failure(ValidationResult.Build(issues));
                }
            }

            if (plan.HasBlendShapes && !_blendShapes.CanProvide(plan, context))
            {
                issues.Add(BlockingBlendShapeProvider.CreateIssue());
                return AssemblyResult.Failure(ValidationResult.Build(issues));
            }

            var mesh = new Mesh
            {
                name = BuildMeshName(context, plan)
            };
            // Everything below this point can throw, including inside a caller-supplied provider. The mesh is
            // already allocated, so an escaping exception must take it with it: otherwise a failed build would
            // leak a transient mesh that the caller has no reference to and therefore can never destroy.
            var applied = false;
            try
            {
                applied = WriteMesh(mesh, plan, context, boneWeights, bindPoses, issues);
            }
            catch
            {
                SafeDestroy(mesh);
                throw;
            }

            if (!applied)
            {
                // WriteMesh reported that it could not finish. It has already destroyed the mesh; destroying
                // again is a no-op on a destroyed object and keeps this path correct even if that changes.
                SafeDestroy(mesh);
                return AssemblyResult.Failure(ValidationResult.Build(issues));
            }

            return new AssemblyResult(
                mesh,
                plan.MaterialLayout.ToMaterialArray(),
                plan,
                ValidationResult.Build(issues));
        }

        /// <summary>
        /// Writes every attribute of the generated mesh.
        /// </summary>
        /// <remarks>
        /// Separated from <see cref="BuildInternal"/> so that the mesh's lifetime is governed by one try/catch
        /// rather than by each return path, and so that "the build failed after the mesh existed" has exactly one
        /// representation: a <c>false</c> return.
        /// </remarks>
        /// <returns>True when the mesh was written completely; false when it was discarded.</returns>
        private bool WriteMesh(
            Mesh mesh,
            MeshAssemblyPlan plan,
            ValidationContext context,
            BoneWeight[] boneWeights,
            Matrix4x4[] bindPoses,
            List<ValidationIssue> issues)
        {
            // The format is read off the plan rather than re-derived from the vertex count. The planner already
            // folded in every source's declared format, so re-deriving here is how a 32-bit source would be
            // silently down-converted to 16 bits whenever the merged mesh happened to stay small.
            mesh.indexFormat = plan.IndexFormat;

            var positions = BuildPositions(plan, context);
            mesh.vertices = positions;

            var normals = BuildNormals(plan, context);
            if (normals != null) mesh.normals = normals;

            var tangents = BuildTangents(plan, context);
            if (tangents != null) mesh.tangents = tangents;

            var colors = BuildColors(plan, context);
            if (colors != null) mesh.colors = colors;

            WriteUvChannels(mesh, plan, context);

            // Skinning is written before submeshes for the same reason as the shared vertex data: Unity reads a
            // bone weight array against the vertex buffer, which must already be in place.
            if (boneWeights != null) mesh.boneWeights = boneWeights;
            if (bindPoses != null && bindPoses.Length > 0) mesh.bindposes = bindPoses;

            // Submeshes are written after all shared vertex data, because SetTriangles on some Unity versions
            // recalculates bounds from the vertex buffer and would otherwise see the previous content.
            mesh.subMeshCount = plan.SubMeshes.Count;
            for (var i = 0; i < plan.SubMeshes.Count; i++)
            {
                var subMesh = plan.SubMeshes[i];
                var indices = new int[subMesh.Indices.Count];
                for (var k = 0; k < indices.Length; k++) indices[k] = subMesh.Indices[k];
                mesh.SetTriangles(indices, i, false);
            }

            mesh.bounds = BuildBounds(positions);

            if (plan.HasBlendShapes && !_blendShapes.ApplyBlendShapes(mesh, plan, context))
            {
                // The mesh exists by now, so it must be destroyed before the failure is returned. Returning it
                // to the caller would be the partial output the pipeline promises never to produce.
                SafeDestroy(mesh);
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    "The blend shape provider reported that it could supply blend shapes but failed to apply " +
                    "them, so the generated mesh was discarded.",
                    detail: "reason=provider-apply-failed; provider=" + _blendShapes.GetType().Name));
                return false;
            }

            if (normals == null || tangents == null)
            {
                // A source that lacked normals or tangents leaves the generated mesh without them. Recalculating
                // would replace authored toon normals with generated ones on bodies that do have them, so the
                // shortfall is reported instead of silently repaired.
                issues.Add(ValidationIssue.Warning(
                    ApaErrorCode.UnsupportedMeshAttribute,
                    ApaIssuePhase.Attributes,
                    "The generated mesh has no " + (normals == null ? "normals" : "tangents") +
                    " because a source mesh did not provide them. Normals are not recalculated, because doing so " +
                    "would overwrite authored toon normals.",
                    detail: "attribute=" + (normals == null ? "normals" : "tangents")));
            }

            return true;
        }

        private static void SafeDestroy(UnityEngine.Object unityObject)
        {
            if (unityObject == null) return;

            // Fully qualified because this file imports both System and UnityEngine, which makes the bare name
            // Object ambiguous.
            if (Application.isPlaying) UnityEngine.Object.Destroy(unityObject);
            else UnityEngine.Object.DestroyImmediate(unityObject);
        }

        /// <summary>
        /// Names the generated mesh after the base it replaces, and after the target group when there is one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two groups can carry meshes with the same asset name — a body and a clothing body both named "Body" —
        /// so a group's mesh name must include the group key or the two generated meshes become indistinguishable
        /// in the profiler, in a bug report, and in any tool that resolves a mesh by name. The key is the target
        /// renderer's hierarchy path, which is unique by construction.
        /// </para>
        /// <para>
        /// A single-target context has no key and keeps the exact M2 name (<c>&lt;base&gt;_Assembled</c>), so
        /// nothing that already depends on that name changes.
        /// </para>
        /// </remarks>
        private static string BuildMeshName(ValidationContext context, MeshAssemblyPlan plan)
        {
            var baseName = context.Base?.Mesh?.Name;
            if (string.IsNullOrEmpty(baseName)) baseName = "Body";

            var groupKey = plan != null ? plan.GroupKey : string.Empty;
            if (string.IsNullOrEmpty(groupKey)) return baseName + "_Assembled";

            return baseName + "_" + SanitizeGroupKey(groupKey) + "_Assembled";
        }

        /// <summary>
        /// Turns a hierarchy path into a mesh-name-safe token without losing its identity.
        /// </summary>
        /// <remarks>
        /// Path separators become dashes so a nested group key stays one readable token; the avatar-root token
        /// becomes <c>root</c> so the name does not read as a file-extension artefact. Two different keys can
        /// only collide if their paths collide, which is impossible for transforms under one root.
        /// </remarks>
        private static string SanitizeGroupKey(string groupKey)
        {
            if (ApaAvatarPath.IsRoot(groupKey)) return "root";

            var builder = new System.Text.StringBuilder(groupKey.Length);
            for (var i = 0; i < groupKey.Length; i++)
            {
                var c = groupKey[i];
                if (c == '/' || c == '\\') builder.Append('-');
                else if (c < ' ') builder.Append('_');
                else builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Builds final positions in the target renderer's local space.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Writing into the target's local space rather than the base mesh's local space is what makes the
        /// generated mesh a drop-in replacement for the target renderer's mesh even when the two transforms
        /// differ, which they routinely do.
        /// </para>
        /// <para>
        /// <b>A preserved seam vertex takes the base vertex's position (M11).</b> Its own position is deliberately
        /// not used: the two sides of the seam must coincide exactly, and the pair was matched within a world
        /// tolerance, so snapping to the base vertex is what keeps the surface closed. Every other attribute of a
        /// preserved vertex still comes from the part, which is the whole point of preserving it.
        /// </para>
        /// </remarks>
        private static Vector3[] BuildPositions(MeshAssemblyPlan plan, ValidationContext context)
        {
            var result = new Vector3[plan.VertexCount];
            var baseToTarget = context.Base.Transforms.SourceToTargetLocal();

            Dictionary<string, Matrix4x4> partMatrices = null;

            for (var i = 0; i < plan.Vertices.Count; i++)
            {
                var source = plan.Vertices[i];

                if (source.Origin == VertexOrigin.Base)
                {
                    result[i] = baseToTarget.MultiplyPoint3x4(context.Base.Mesh.Vertices[source.SourceVertex]);
                    continue;
                }

                if (source.IsSplit && source.WeldedBaseVertex >= 0)
                {
                    result[i] = baseToTarget.MultiplyPoint3x4(
                        context.Base.Mesh.Vertices[source.WeldedBaseVertex]);
                    continue;
                }

                if (partMatrices == null) partMatrices = BuildPartMatrices(context);
                var matrix = partMatrices[source.PartId];
                var local = context.FindPart(source.PartId).Mesh.Vertices[source.SourceVertex];

                // A welded vertex keeps the base position by construction: the remap already pointed it at the
                // base vertex, so a part vertex is only ever a non-seam vertex here.
                result[i] = matrix.MultiplyPoint3x4(local);
            }

            return result;
        }

        private static Dictionary<string, Matrix4x4> BuildPartMatrices(ValidationContext context)
        {
            var map = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                map[part.PartId] = part.Transforms.SourceToTargetLocal();
            }

            return map;
        }

        private static Dictionary<string, Matrix4x4> BuildPartNormalMatrices(ValidationContext context)
        {
            var map = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                map[part.PartId] = part.Transforms.SourceToTargetNormalMatrix();
            }

            return map;
        }

        /// <summary>
        /// Builds final normals, or null when any contributing source lacks normals.
        /// </summary>
        /// <remarks>
        /// Normals are transformed with the inverse transpose, which is the correct operator under non-uniform
        /// scale. A zero-length result would produce a NaN after normalization, so it is left as zero and the
        /// normal is not normalized rather than being forced into an invalid unit vector.
        /// </remarks>
        private static Vector3[] BuildNormals(MeshAssemblyPlan plan, ValidationContext context)
        {
            if (!context.Base.Mesh.HasNormals) return null;
            for (var i = 0; i < context.Parts.Count; i++)
            {
                if (!context.Parts[i].Mesh.HasNormals) return null;
            }

            var result = new Vector3[plan.VertexCount];

            // The normal matrix is read from the captured snapshot rather than inverted here: inverting a
            // singular matrix would produce NaN, and the capture step already rejected any transform whose
            // normal matrix cannot be derived.
            var baseNormalMatrix = context.Base.Transforms.SourceToTargetNormalMatrix();
            Dictionary<string, Matrix4x4> partNormalMatrices = null;

            for (var i = 0; i < plan.Vertices.Count; i++)
            {
                var source = plan.Vertices[i];

                if (source.Origin == VertexOrigin.Base)
                {
                    result[i] = TransformNormal(
                        baseNormalMatrix,
                        context.Base.Mesh.Normals[source.SourceVertex]);
                    continue;
                }

                if (partNormalMatrices == null) partNormalMatrices = BuildPartNormalMatrices(context);
                var part = context.FindPart(source.PartId);
                result[i] = TransformNormal(
                    partNormalMatrices[source.PartId],
                    part.Mesh.Normals[source.SourceVertex]);
            }

            return result;
        }

        private static Vector3 TransformNormal(Matrix4x4 normalMatrix, Vector3 normal)
        {
            var transformed = normalMatrix.MultiplyVector(normal);
            var magnitude = transformed.magnitude;
            if (magnitude <= 1e-12f) return Vector3.zero;
            return transformed / magnitude;
        }

        /// <summary>
        /// Builds final tangents, or null when any contributing source lacks tangents.
        /// </summary>
        /// <remarks>
        /// The <c>w</c> component is handedness, not a direction. It is carried through unchanged unless the
        /// transform mirrors the geometry, in which case the sign is inverted exactly once so that the handedness
        /// encoded in the tangent basis still describes the same surface orientation.
        /// </remarks>
        private static Vector4[] BuildTangents(MeshAssemblyPlan plan, ValidationContext context)
        {
            if (!context.Base.Mesh.HasTangents) return null;
            for (var i = 0; i < context.Parts.Count; i++)
            {
                if (!context.Parts[i].Mesh.HasTangents) return null;
            }

            var result = new Vector4[plan.VertexCount];
            var baseToTarget = context.Base.Transforms.SourceToTargetLocal();
            var baseFlipped = baseToTarget.determinant < 0f;
            Dictionary<string, Matrix4x4> partMatrices = null;
            Dictionary<string, bool> partFlipped = null;

            for (var i = 0; i < plan.Vertices.Count; i++)
            {
                var source = plan.Vertices[i];

                if (source.Origin == VertexOrigin.Base)
                {
                    result[i] = TransformTangent(baseToTarget, baseFlipped, context.Base.Mesh.Tangents[source.SourceVertex]);
                    continue;
                }

                if (partMatrices == null)
                {
                    partMatrices = BuildPartMatrices(context);
                    partFlipped = BuildPartFlipFlags(context);
                }

                var part = context.FindPart(source.PartId);
                result[i] = TransformTangent(
                    partMatrices[source.PartId],
                    partFlipped[source.PartId],
                    part.Mesh.Tangents[source.SourceVertex]);
            }

            return result;
        }

        private static Dictionary<string, bool> BuildPartFlipFlags(ValidationContext context)
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                map[part.PartId] = part.Transforms.SourceToTargetLocal().determinant < 0f;
            }

            return map;
        }

        private static Vector4 TransformTangent(Matrix4x4 matrix, bool flipped, Vector4 tangent)
        {
            var direction = new Vector3(tangent.x, tangent.y, tangent.z);
            var transformed = matrix.MultiplyVector(direction);
            var magnitude = transformed.magnitude;
            if (magnitude > 1e-12f) transformed /= magnitude;

            var w = tangent.w;
            if (flipped) w = -w;

            return new Vector4(transformed.x, transformed.y, transformed.z, w);
        }

        private static Color[] BuildColors(MeshAssemblyPlan plan, ValidationContext context)
        {
            if (!context.Base.Mesh.HasColors) return null;
            for (var i = 0; i < context.Parts.Count; i++)
            {
                if (!context.Parts[i].Mesh.HasColors) return null;
            }

            var result = new Color[plan.VertexCount];

            for (var i = 0; i < plan.Vertices.Count; i++)
            {
                var source = plan.Vertices[i];

                if (source.Origin == VertexOrigin.Base)
                {
                    result[i] = context.Base.Mesh.Colors[source.SourceVertex];
                    continue;
                }

                var part = context.FindPart(source.PartId);
                result[i] = part.Mesh.Colors[source.SourceVertex];
            }

            return result;
        }

        /// <summary>
        /// Writes every final UV channel.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Attribute ownership at a weld is asymmetric, and this method is where that asymmetry is honoured. A
        /// welded output vertex <i>is</i> the retained base vertex, so it is reported as
        /// <see cref="VertexOrigin.Base"/> — which means its base value alone would be written, and a semantic
        /// that exists only on the part would silently become the channel default. That contradicts R7 and
        /// section 43.2, which require a part-only semantic to take the matched part seam value at the weld.
        /// </para>
        /// <para>
        /// The fix is to consult the plan's weld provenance. For a welded vertex the base value is used when the
        /// base has the semantic, and a contributing part's value is used when it does not. When several parts
        /// contribute, validation has already established that their values agree within epsilon, so the first
        /// contribution in canonical part order is a deterministic and correct choice rather than an arbitrary
        /// one. A disagreement is <c>APA025</c> and never reaches this method.
        /// </para>
        /// <para>
        /// The weld lookup is a single dictionary probe, and the provenance it returns is short: it exists only
        /// for base vertices that a part welds onto, which is a small minority of a body's vertices.
        /// </para>
        /// </remarks>
        private static void WriteUvChannels(Mesh mesh, MeshAssemblyPlan plan, ValidationContext context)
        {
            var layout = plan.UvLayout;

            for (var c = 0; c < layout.Channels.Count; c++)
            {
                var channel = layout.Channels[c];
                var values = new Vector4[plan.VertexCount];

                var baseChannel = channel.FindSourceChannel(string.Empty);
                var hasBase = baseChannel >= 0 && context.Base.Mesh.HasUvChannel(baseChannel);
                var baseUvs = hasBase ? context.Base.Mesh.GetUvChannel(baseChannel) : null;

                for (var i = 0; i < plan.Vertices.Count; i++)
                {
                    var source = plan.Vertices[i];

                    if (source.Origin == VertexOrigin.Base)
                    {
                        if (hasBase)
                        {
                            values[i] = baseUvs[source.SourceVertex];
                            continue;
                        }

                        // The base lacks this semantic. It may still be a weld, in which case a contributing
                        // part that does have it supplies the value; otherwise the channel default applies.
                        values[i] = WeldUvOrZero(plan, context, i, channel, source.SourceVertex);
                        continue;
                    }

                    var part = context.FindPart(source.PartId);
                    var partChannel = channel.FindSourceChannel(source.PartId);

                    // A welded part vertex is represented by its base vertex and therefore never reaches this
                    // branch; every part vertex here is either a genuine part vertex or a preserved (split) seam
                    // vertex, and both take the part value. That is exactly what makes the preservation work: the
                    // part's own UV survives because the vertex that carries it survives.
                    if (partChannel >= 0 && part != null && part.Mesh.HasUvChannel(partChannel))
                    {
                        values[i] = part.Mesh.GetUvChannel(partChannel)[source.SourceVertex];
                    }
                    else
                    {
                        values[i] = Vector4.zero;
                    }
                }

                mesh.SetUVs(channel.OutputChannel, new List<Vector4>(values));
            }
        }

        /// <summary>
        /// Finds the part value a welded base vertex should take for a semantic the base does not have.
        /// </summary>
        /// <remarks>
        /// Contributions are already in canonical part order, and validation has established that any two
        /// contributions to the same semantic agree within the UV epsilon. The first contribution that actually
        /// carries the semantic is therefore both deterministic and representative; a contribution from a part
        /// that lacks the layer is skipped rather than treated as a zero, because "absent" is not a value the
        /// author asserted.
        /// </remarks>
        private static Vector4 WeldUvOrZero(
            MeshAssemblyPlan plan,
            ValidationContext context,
            int finalVertex,
            UvChannelAssignment channel,
            int baseVertex)
        {
            var weld = plan.WeldAt(finalVertex);
            if (weld == null) return Vector4.zero;

            for (var i = 0; i < weld.Contributions.Count; i++)
            {
                var contribution = weld.Contributions[i];
                var partChannel = channel.FindSourceChannel(contribution.PartId);
                if (partChannel < 0) continue;

                var part = context.FindPart(contribution.PartId);
                if (part == null || !part.Mesh.HasUvChannel(partChannel)) continue;

                var partUvs = part.Mesh.GetUvChannel(partChannel);
                if (contribution.PartVertex < 0 || contribution.PartVertex >= partUvs.Count) continue;

                return partUvs[contribution.PartVertex];
            }

            // A part contributed a seam vertex but not this semantic, so the channel default is correct: the
            // semantic exists nowhere at this vertex.
            return Vector4.zero;
        }

        /// <summary>
        /// Computes the output bounds from the actual generated geometry.
        /// </summary>
        /// <remarks>
        /// Unioning the source bounds would over-report in a way that matters: Unity uses bounds for culling, and
        /// a too-large bound makes the avatar render when it should not. Computing from the generated positions
        /// is both cheaper to reason about and always correct.
        /// </remarks>
        private static Bounds BuildBounds(Vector3[] positions)
        {
            if (positions.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);

            var min = positions[0];
            var max = positions[0];

            for (var i = 1; i < positions.Length; i++)
            {
                var p = positions[i];
                if (p.x < min.x) min.x = p.x;
                if (p.y < min.y) min.y = p.y;
                if (p.z < min.z) min.z = p.z;
                if (p.x > max.x) max.x = p.x;
                if (p.y > max.y) max.y = p.y;
                if (p.z > max.z) max.z = p.z;
            }

            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }
    }
}

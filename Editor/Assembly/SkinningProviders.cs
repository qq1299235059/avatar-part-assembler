using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Supplies skinning data to the assembler.
    /// </summary>
    /// <remarks>
    /// The plan carries the final bone table (R10); a provider's job is to materialize it into the arrays a
    /// Unity mesh wants. The interface exists so that the assembler never has to know how the table was built,
    /// and so a caller can substitute a provider that refuses (see <see cref="BlockingSkinningProvider"/>)
    /// without touching the pipeline.
    /// </remarks>
    public interface ISkinningProvider
    {
        /// <summary>True when this provider can produce skinning data for the given plan.</summary>
        bool CanProvide(MeshAssemblyPlan plan, ValidationContext context);

        /// <summary>
        /// Produces the bone weights for the final vertex list, or null when the plan has no weights to write.
        /// </summary>
        BoneWeight[] BuildBoneWeights(MeshAssemblyPlan plan, ValidationContext context);

        /// <summary>
        /// Produces the bind poses for the final bone table, or an empty array when there is no bone table.
        /// </summary>
        Matrix4x4[] BuildBindPoses(MeshAssemblyPlan plan, ValidationContext context);
    }

    /// <summary>
    /// Supplies blend shape data to the assembler.
    /// </summary>
    public interface IBlendShapeProvider
    {
        /// <summary>True when this provider can produce blend shape data for the given plan.</summary>
        bool CanProvide(MeshAssemblyPlan plan, ValidationContext context);

        /// <summary>
        /// Applies the final blend shape set to the generated mesh, or returns false when unsupported.
        /// </summary>
        bool ApplyBlendShapes(Mesh mesh, MeshAssemblyPlan plan, ValidationContext context);
    }

    /// <summary>
    /// The M2 skinning provider: remaps every retained vertex's weights onto the plan's final bone table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Weight ownership follows section 20. A welded output vertex <i>is</i> the retained base vertex, so it
    /// carries the base body's weight by construction: the planner maps the part seam vertex onto the base
    /// final index and emits no vertex of its own for it, which means there is no code path here that could
    /// give a welded vertex a part weight. A non-seam part vertex keeps its own weight, remapped through the
    /// part's bone remap.
    /// </para>
    /// <para>
    /// Every index is resolved with the same helper the validator uses, so a weight that validation accepted
    /// cannot fail here; if one somehow does, the provider returns null and the assembler refuses to build
    /// rather than writing an index that means nothing.
    /// </para>
    /// </remarks>
    public sealed class FinalBoneTableSkinningProvider : ISkinningProvider
    {
        /// <summary>True when the plan carries a final bone table.</summary>
        public bool CanProvide(MeshAssemblyPlan plan, ValidationContext context)
        {
            return plan != null && plan.BoneTable != null;
        }

        /// <summary>Builds the remapped weight array, or null when no source carries weights.</summary>
        public BoneWeight[] BuildBoneWeights(MeshAssemblyPlan plan, ValidationContext context)
        {
            if (plan == null || context == null || plan.BoneTable == null) return null;
            if (!BoneWeightValidator.AnyWeights(context)) return null;

            var weights = new BoneWeight[plan.VertexCount];

            for (var i = 0; i < plan.Vertices.Count; i++)
            {
                var source = plan.Vertices[i];
                var mesh = source.Origin == VertexOrigin.Base
                    ? context.Base?.Mesh
                    : context.FindPart(source.PartId)?.Mesh;

                if (mesh == null) return null;
                if (source.SourceVertex < 0 || source.SourceVertex >= mesh.SkinWeights.Count) return null;

                if (!BoneWeightValidator.TryRemapWeight(
                        mesh.SkinWeights[source.SourceVertex],
                        plan.BoneTable,
                        source.PartId,
                        out var remapped,
                        context.NumericPolicy))
                {
                    return null;
                }

                weights[i] = remapped;
            }

            return weights;
        }

        /// <summary>Returns the final bind poses, in final bone order.</summary>
        public Matrix4x4[] BuildBindPoses(MeshAssemblyPlan plan, ValidationContext context)
        {
            if (plan?.BoneTable == null) return new Matrix4x4[0];
            return plan.BoneTable.BindPoseArray;
        }
    }

    /// <summary>
    /// The M2 blend shape provider: writes every frame of every final shape over the final vertex list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Delta ownership follows the same provenance the geometry uses. A final vertex that came from the base
    /// reads the base mesh's delta at its original index; a final vertex that came from a part reads that
    /// part's delta. A vertex whose source does not have the shape receives zero, which is the documented value
    /// for "this shape does not move that vertex" (section 43.2). Welded vertices are base vertices, so a part
    /// delta at a seam vertex is never read — and the planner has already refused any shape where that delta
    /// was not zero, so nothing is silently discarded.
    /// </para>
    /// <para>
    /// <b>A preserved seam vertex is the one exception, and only for position (M11).</b> Its static position is
    /// the base vertex's position, so its position delta must be the base vertex's delta as well; taking the
    /// part's own delta would make the two sides of the seam move apart under animation while coinciding at rest.
    /// Normal and tangent deltas still come from the part, because they are directions: they cannot open a gap,
    /// and they are part of what preserving the vertex is for.
    /// </para>
    /// <para>
    /// Deltas are transformed into the target renderer's local space with the same matrices the geometry uses:
    /// the linear part for position offsets (a delta is an offset, so the translation column must not apply),
    /// the inverse transpose for normal offsets, and the linear part for tangent offsets, because a tangent's
    /// <c>xyz</c> is a direction and not a covector. The operator per delta kind is defined once on
    /// <see cref="SpaceTransforms.SourceToTargetDeltaMatrix"/> so that this provider and
    /// <see cref="BlendShapeSeamValidator"/>, which compares seam deltas, cannot disagree about it. Skipping
    /// this step is the classic shape-key bug: the base pose looks right and every shape key moves the part in
    /// the wrong direction.
    /// </para>
    /// </remarks>
    public sealed class RemappedBlendShapeProvider : IBlendShapeProvider
    {
        /// <summary>True when the plan carries at least one final shape.</summary>
        public bool CanProvide(MeshAssemblyPlan plan, ValidationContext context)
        {
            return plan != null && plan.HasBlendShapes;
        }

        /// <summary>Adds every frame of every final shape to the generated mesh.</summary>
        public bool ApplyBlendShapes(Mesh mesh, MeshAssemblyPlan plan, ValidationContext context)
        {
            if (mesh == null || plan == null || context == null) return false;

            var shapes = plan.BlendShapes;
            if (shapes == null || shapes.IsEmpty) return true;

            var matrices = new DeltaMatrices(context);

            for (var s = 0; s < shapes.Shapes.Count; s++)
            {
                var shape = shapes.Shapes[s];

                // A shape with no frames carries no deltas at all, so there is nothing to write and Unity has
                // no frame to hang the name on. It is kept in the plan so ordering and diagnostics still
                // account for it.
                for (var frame = 0; frame < shape.FrameCount; frame++)
                {
                    var positions = new Vector3[plan.VertexCount];
                    var normals = new Vector3[plan.VertexCount];
                    var tangents = new Vector3[plan.VertexCount];

                    for (var i = 0; i < plan.Vertices.Count; i++)
                    {
                        var vertex = plan.Vertices[i];
                        var sourceKey = vertex.Origin == VertexOrigin.Base ? string.Empty : vertex.PartId;
                        var sourceMesh = vertex.Origin == VertexOrigin.Base
                            ? context.Base?.Mesh
                            : context.FindPart(sourceKey)?.Mesh;

                        if (sourceMesh == null) return false;

                        var shapeIndex = shape.SourceIndexFor(sourceKey);

                        // A preserved (split) seam vertex takes its position deltas from the base vertex it was
                        // snapped to, not from its own part vertex. Without this the static positions would
                        // coincide at rest and separate as soon as a shape moved the base and the part seam
                        // differently, which is the crack the preservation exists to avoid. Normal and tangent
                        // deltas stay with the part, because they are directions and cannot open the surface.
                        var positionMesh = sourceMesh;
                        var positionShape = shapeIndex;
                        var positionVertex = vertex.SourceVertex;
                        var positionKey = sourceKey;

                        if (vertex.IsSplit && vertex.WeldedBaseVertex >= 0 && context.Base?.Mesh != null)
                        {
                            positionMesh = context.Base.Mesh;
                            positionShape = shape.SourceIndexFor(string.Empty);
                            positionVertex = vertex.WeldedBaseVertex;
                            positionKey = string.Empty;
                        }

                        positions[i] = matrices.Transform(
                            positionKey,
                            BlendShapeDeltas.Delta(
                                positionMesh, positionShape, frame, positionVertex, BlendShapeDeltaKind.Position),
                            BlendShapeDeltaKind.Position);
                        normals[i] = matrices.Transform(
                            sourceKey,
                            BlendShapeDeltas.Delta(
                                sourceMesh, shapeIndex, frame, vertex.SourceVertex, BlendShapeDeltaKind.Normal),
                            BlendShapeDeltaKind.Normal);
                        tangents[i] = matrices.Transform(
                            sourceKey,
                            BlendShapeDeltas.Delta(
                                sourceMesh, shapeIndex, frame, vertex.SourceVertex, BlendShapeDeltaKind.Tangent),
                            BlendShapeDeltaKind.Tangent);
                    }

                    mesh.AddBlendShapeFrame(shape.Name, shape.FrameWeights[frame], positions, normals, tangents);
                }
            }

            return true;
        }

        /// <summary>
        /// The captured per-source transforms a delta is transformed with.
        /// </summary>
        /// <remarks>
        /// The provider keeps <see cref="SpaceTransforms"/> values rather than picking matrices per delta kind
        /// itself: the operator for position, normal, and tangent deltas is defined in exactly one place, so this
        /// provider cannot drift from the seam validator that compares the same deltas.
        /// </remarks>
        private sealed class DeltaMatrices
        {
            private readonly SpaceTransforms _base;
            private readonly Dictionary<string, SpaceTransforms> _parts =
                new Dictionary<string, SpaceTransforms>(StringComparer.Ordinal);

            public DeltaMatrices(ValidationContext context)
            {
                _base = context.Base != null ? context.Base.Transforms : default;

                for (var i = 0; i < context.Parts.Count; i++)
                {
                    var part = context.Parts[i];
                    if (part == null) continue;
                    _parts[part.PartId] = part.Transforms;
                }
            }

            public Vector3 Transform(string sourceKey, Vector3 delta, BlendShapeDeltaKind kind)
            {
                var key = sourceKey ?? string.Empty;
                SpaceTransforms transforms;

                if (string.IsNullOrEmpty(key))
                {
                    transforms = _base;
                }
                else if (!_parts.TryGetValue(key, out transforms))
                {
                    // An unknown source contributes no delta of its own; the identity mapping keeps that case
                    // explicit rather than inventing a space for it.
                    transforms = default;
                }

                // MultiplyVector applies the linear part only, which is what a delta needs: a delta is an
                // offset, so the translation column must not be added to it.
                return transforms.SourceToTargetDeltaMatrix(kind).MultiplyVector(delta);
            }
        }
    }

    /// <summary>
    /// The refusing skinning provider: reports that it cannot supply skinning data.
    /// </summary>
    /// <remarks>
    /// Kept after M2 landed, because "refuse rather than lose data" is a property of the pipeline rather than
    /// of a milestone. It is what a caller selects when it wants the assembler to reject skinned input
    /// outright, and it is the fallback the assembler uses when it is handed a null provider.
    /// </remarks>
    public sealed class BlockingSkinningProvider : ISkinningProvider
    {
        /// <summary>Always false: this provider never supplies skinning data.</summary>
        public bool CanProvide(MeshAssemblyPlan plan, ValidationContext context) => false;

        /// <summary>Always null.</summary>
        public BoneWeight[] BuildBoneWeights(MeshAssemblyPlan plan, ValidationContext context) => null;

        /// <summary>Always empty.</summary>
        public Matrix4x4[] BuildBindPoses(MeshAssemblyPlan plan, ValidationContext context) => new Matrix4x4[0];

        /// <summary>The diagnostic emitted when skinning would have been needed.</summary>
        public static ValidationIssue CreateIssue() => ValidationIssue.Error(
            ApaErrorCode.UnsupportedMeshAttribute,
            ApaIssuePhase.Attributes,
            "The configured skinning provider cannot supply bone weights and bind poses. The assembler refuses " +
            "to emit a mesh whose bone weights and bind poses would be lost.",
            detail: "attribute=skinning; provider=blocking");
    }

    /// <summary>
    /// The refusing blend shape provider: reports that it cannot supply blend shape data.
    /// </summary>
    public sealed class BlockingBlendShapeProvider : IBlendShapeProvider
    {
        /// <summary>Always false: this provider never supplies blend shape data.</summary>
        public bool CanProvide(MeshAssemblyPlan plan, ValidationContext context) => false;

        /// <summary>Always false.</summary>
        public bool ApplyBlendShapes(Mesh mesh, MeshAssemblyPlan plan, ValidationContext context) => false;

        /// <summary>The diagnostic emitted when blend shapes would have been needed.</summary>
        public static ValidationIssue CreateIssue() => ValidationIssue.Error(
            ApaErrorCode.UnsupportedMeshAttribute,
            ApaIssuePhase.Attributes,
            "The configured blend shape provider cannot supply remapped frames and deltas. The assembler " +
            "refuses to emit a mesh whose blend shape frames and deltas would be lost.",
            detail: "attribute=blendshape; provider=blocking");
    }
}

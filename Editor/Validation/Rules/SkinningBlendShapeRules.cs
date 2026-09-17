using System;
using System.Collections.Generic;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Validates skinning input and the final bone table (R10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two layers are checked. The first is the data each source carries: a weight array must have exactly one
    /// entry per vertex, every weight must be finite and non-negative, a vertex must have at least one
    /// non-zero influence, and every reference must name a bone inside the source's own bone list. The second
    /// is the mapping onto the final table: every bone a weight reaches needs a stable identity, one identity
    /// must not be claimed by two bones of one source (or by two body bones a part weights), and each bone's
    /// bind pose must be computable. A part bone that shares a path with a body bone is <i>not</i> an ambiguity
    /// — the body is authoritative for every path it declares — and is reported as one Info summary instead.
    /// </para>
    /// <para>
    /// Running the table builder here as well as in the planner is deliberate. A user who presses Validate
    /// must see a bone problem without having to plan a build, and the planner's report deduplicates against
    /// this one because both construct their diagnostics in the same place.
    /// </para>
    /// </remarks>
    public sealed class SkinningRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "SkinningRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context?.Base?.Mesh == null) return;
            if (!FinalBoneTableBuilder.HasSkinningInputs(context)) return;

            BoneWeightValidator.ValidateSourceData(context, BoneWeightValidator.AnyWeights(context), issues);

            // The table is built for its diagnostics only; the planner builds the instance it stores in the plan.
            FinalBoneTableBuilder.Build(context, issues);
        }
    }

    /// <summary>
    /// Validates blend shape data and the rules for merging same-named shapes (R11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This rule covers everything that can be decided from the shapes themselves: duplicate names, frames
    /// whose delta arrays cannot be indexed by vertex, non-finite deltas, and the frame count and frame weight
    /// agreement that merging requires. The seam-delta rules need the resolved seam correspondence — matched by
    /// position, not by index order — so they run in the planner instead, where that correspondence exists.
    /// </para>
    /// </remarks>
    public sealed class BlendShapeRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "BlendShapeRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context?.Base?.Mesh == null) return;
            if (!HasBlendShapes(context)) return;

            BlendShapeCatalog.Build(context, issues);
        }

        /// <summary>True when any source carries at least one blend shape.</summary>
        public static bool HasBlendShapes(ValidationContext context)
        {
            if (context?.Base?.Mesh != null && context.Base.Mesh.BlendShapeCount > 0) return true;
            if (context?.Parts == null) return false;
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var mesh = context.Parts[i]?.Mesh;
                if (mesh != null && mesh.BlendShapeCount > 0) return true;
            }

            return false;
        }
    }
}

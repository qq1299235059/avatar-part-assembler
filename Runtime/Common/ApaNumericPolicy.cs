using System;
using UnityEngine;

namespace AvatarPartAssembler
{
    /// <summary>
    /// The numeric tolerances used by every comparison in the assembler. A single policy object is passed
    /// through validation and planning so that a test can pin exact behaviour without touching global state.
    /// </summary>
    /// <remarks>
    /// Per the specification these are developer settings, not user-facing options. The defaults are the ones
    /// the specification recommends: 1e-5 for both positions and UVs.
    /// </remarks>
    public sealed class ApaNumericPolicy
    {
        /// <summary>Default position epsilon (avatar-root local units).</summary>
        public const float DefaultPositionEpsilon = 1e-5f;

        /// <summary>Default UV epsilon.</summary>
        public const float DefaultUvEpsilon = 1e-5f;

        /// <summary>
        /// How far a vertex may move during assembly before the output triangle is considered degenerate.
        /// A remap that collapses a triangle to zero area is a bug in the profile, not a valid result.
        /// </summary>
        public const float DefaultDegenerateAreaEpsilon = 1e-12f;

        /// <summary>
        /// Bone influence weight at or below which the influence counts as absent.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One threshold, used in both directions. A bone index is <i>required</i> to have a stable identity, and
        /// to enter the final bone table, only when some vertex references it with a weight above this value;
        /// and a remap clears an influence at or below it to index 0 / weight 0 rather than resolving it. The two
        /// directions have to agree: if "unused" were stricter than "cleared", a source could pass validation
        /// with an influence that the remap then cannot resolve, which is exactly the inconsistency this
        /// threshold removes.
        /// </para>
        /// <para>
        /// Unity ignores a negligible influence for all practical purposes, so a value below this threshold names
        /// no bone that actually deforms the vertex. It is deliberately the same magnitude as the position
        /// epsilon: both answer "is this difference real" for a single-precision quantity.
        /// </para>
        /// </remarks>
        public const float DefaultWeightEpsilon = 1e-5f;

        private static readonly ApaNumericPolicy s_default = new ApaNumericPolicy(
            DefaultPositionEpsilon,
            DefaultUvEpsilon,
            DefaultDegenerateAreaEpsilon,
            DefaultWeightEpsilon);

        /// <summary>The shared immutable default policy.</summary>
        public static ApaNumericPolicy Default => s_default;

        /// <summary>Distance tolerance used when matching seam vertices in avatar-root local space.</summary>
        public float PositionEpsilon { get; }

        /// <summary>Tolerance used when comparing same-name UV values at a welded seam vertex.</summary>
        public float UvEpsilon { get; }

        /// <summary>Squared-area threshold below which an output triangle is degenerate.</summary>
        public float DegenerateAreaEpsilon { get; }

        /// <summary>
        /// Bone influence weight at or below which an influence counts as absent.
        /// </summary>
        /// <remarks>
        /// Read by the final bone table (which bones must have an identity and enter the table) and by the bone
        /// weight remap (which influences are cleared). See <see cref="DefaultWeightEpsilon"/> for why the two
        /// must use one threshold.
        /// </remarks>
        public float WeightEpsilon { get; }

        /// <summary>Creates a policy. Throws when any value is not a positive finite number.</summary>
        /// <param name="weightEpsilon">
        /// The bone influence threshold. Optional so that every existing three-argument call site keeps compiling
        /// and keeps the documented default.
        /// </param>
        public ApaNumericPolicy(
            float positionEpsilon,
            float uvEpsilon,
            float degenerateAreaEpsilon,
            float weightEpsilon = DefaultWeightEpsilon)
        {
            PositionEpsilon = positionEpsilon;
            UvEpsilon = uvEpsilon;
            DegenerateAreaEpsilon = degenerateAreaEpsilon;
            WeightEpsilon = weightEpsilon;
        }

        /// <summary>
        /// Validates the policy itself. A NaN epsilon would make every comparison false and produce a
        /// misleading "no match" diagnostic, so an invalid policy is reported before it is used.
        /// </summary>
        public bool TryValidate(out string message)
        {
            if (!IsPositiveFinite(PositionEpsilon))
            {
                message = "Position epsilon must be a positive finite number but was " + PositionEpsilon + ".";
                return false;
            }

            if (!IsPositiveFinite(UvEpsilon))
            {
                message = "UV epsilon must be a positive finite number but was " + UvEpsilon + ".";
                return false;
            }

            if (!IsPositiveFinite(DegenerateAreaEpsilon))
            {
                message = "Degenerate area epsilon must be a positive finite number but was " + DegenerateAreaEpsilon + ".";
                return false;
            }

            if (!IsPositiveFinite(WeightEpsilon))
            {
                message = "Weight epsilon must be a positive finite number but was " + WeightEpsilon + ".";
                return false;
            }

            message = string.Empty;
            return true;
        }

        /// <summary>Creates a policy that overrides only the position epsilon.</summary>
        public ApaNumericPolicy WithPositionEpsilon(float value)
        {
            return new ApaNumericPolicy(value, UvEpsilon, DegenerateAreaEpsilon, WeightEpsilon);
        }

        /// <summary>Creates a policy that overrides only the UV epsilon.</summary>
        public ApaNumericPolicy WithUvEpsilon(float value)
        {
            return new ApaNumericPolicy(PositionEpsilon, value, DegenerateAreaEpsilon, WeightEpsilon);
        }

        /// <summary>Creates a policy that overrides only the bone influence threshold.</summary>
        public ApaNumericPolicy WithWeightEpsilon(float value)
        {
            return new ApaNumericPolicy(PositionEpsilon, UvEpsilon, DegenerateAreaEpsilon, value);
        }

        /// <summary>
        /// True when an influence of the given weight counts as absent under this policy.
        /// </summary>
        /// <remarks>
        /// The one predicate the bone table, the validator, and the remap all read, so "unused" cannot mean one
        /// thing where identities are required and another where weights are rewritten.
        /// </remarks>
        public bool IsNegligibleWeight(float weight)
        {
            return !(weight > WeightEpsilon);
        }

        /// <summary>
        /// True when any of a BoneWeight's four influences is above the influence threshold.
        /// </summary>
        /// <remarks>
        /// Allocation-free and used per vertex of every source, so it is written as a straight-line test rather
        /// than through a temporary array.
        /// </remarks>
        public bool HasEffectiveInfluence(BoneWeight weight)
        {
            return weight.weight0 > WeightEpsilon
                   || weight.weight1 > WeightEpsilon
                   || weight.weight2 > WeightEpsilon
                   || weight.weight3 > WeightEpsilon;
        }

        /// <summary>True when the value is finite and greater than zero.</summary>
        public static bool IsPositiveFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        /// <summary>True when the value is finite.</summary>
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

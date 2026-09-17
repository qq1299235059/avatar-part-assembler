using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Guards every use of topology-indexed authoring data behind a verified mesh signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removal triangle addresses, seam vertex indices, and source channel mappings are only meaningful against
    /// the exact mesh they were authored against. Applying them to a different mesh — a different body, an
    /// updated body, a body with a modified vertex order — produces silent corruption: triangles vanish from
    /// the wrong place, seams weld to unrelated vertices, and UVs shift. This rule is what prevents that.
    /// </para>
    /// <para>
    /// The check is a strict guard, not a repair. It decides whether the profile may be applied; it never
    /// rewrites the profile to match whatever mesh it found. A mismatch is reported with the exact differing
    /// fields so the author can re-author rather than guess.
    /// </para>
    /// <para>
    /// <b>The GUID is diagnostic context, not an exemption.</b> The comparison below always runs in full. A mesh
    /// asset keeps its GUID across a reimport that changes its vertex order, submesh split, or blend shape set,
    /// so a GUID match proves only that the asset was not replaced. It never proves that stored topology indices
    /// are still safe. The recorded GUID remains useful in the mismatch detail when diagnosing replacement
    /// versus reimport.
    /// </para>
    /// <para>
    /// The safety fields are the vertex count, the per-submesh index counts and topologies, the blend shape names
    /// and frame counts, the recorded renderer path, and — whenever a source in the configuration carries
    /// skinning data — the bone signature. The bone signature is the one field whose severity depends on the
    /// configuration, because it is only load-bearing where weights exist.
    /// </para>
    /// <para>
    /// <b>One explicit pass per installer.</b> The rule is a comparison of one signature with one base, so a
    /// configuration with several installers needs one run per installer, each with that installer's own
    /// signature (<see cref="ContextBuilder"/> does this and marks the context
    /// <see cref="ValidationContext.CompatibilityVerified"/>). A context that carries a single representative
    /// signature is still compared here, which is the M2 contract every legacy caller and hand-built test
    /// context relies on.
    /// </para>
    /// </remarks>
    public sealed class CompatibilityRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "CompatibilityRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context.Base == null || context.Base.Mesh == null) return;

            // The per-installer pass (ContextBuilder) already ran this rule once per installer, each time
            // against that installer's own captured signature and anchored on that installer's part id. Running
            // it again here through one representative signature cannot add information — the representative was
            // the first installer's, which is arbitrary when a group's installers disagree — and would report
            // every non-blocking verdict a second time with a different anchor. One explicit pass, one report.
            //
            // <b>The flag is a claim, not a proof, so it is honoured only while the claim is coherent.</b> A
            // context the per-installer pass produced carries no representative signature: that pass is the only
            // thing that ever decided its compatibility, and there is no second signature left to compare. A
            // context that claims verification <i>and</i> still carries a signature contradicts itself — the two
            // cannot both be the source of one group's verdict — so the rule runs instead of trusting the flag.
            // That is the entire safety argument for skipping: a call site that sets the flag without doing the
            // work gets the check, never a silent pass, and the failure mode of a future refactor is a duplicate
            // report rather than a compatibility gate that quietly stopped existing.
            if (context.CompatibilityVerified && context.ExpectedCompatibility == null) return;

            var expected = context.ExpectedCompatibility;
            if (expected == null)
            {
                // A profile authored before signatures existed cannot be safely applied. This is reported
                // rather than assumed compatible, because assuming compatibility is how corruption ships.
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.PartProfileIncompatible,
                    ApaIssuePhase.Compatibility,
                    "The profile has no captured target signature, so its triangle and seam indices cannot be " +
                    "verified against the target mesh. Re-author the profile against the target body.",
                    detail: "reason=signature-missing"));
                return;
            }

            if (!expected.IsCaptured)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.PartProfileIncompatible,
                    ApaIssuePhase.Compatibility,
                    "The profile's target signature was never captured. Re-author the profile against the target body.",
                    detail: "reason=signature-not-captured"));
                return;
            }

            var actual = context.Base.Mesh;

            // A signature written before the safety fields existed cannot be compared field by field, because
            // the absent fields have no "expected" value to differ from. Reporting this as its own code keeps
            // "your body changed" and "your profile is too old to check" from collapsing into one message.
            if (!expected.HasCompleteSafetyData)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.IncompleteCompatibilitySignature,
                    ApaIssuePhase.Compatibility,
                    "The captured target signature is incomplete, so the profile's triangle and seam indices " +
                    "cannot be verified. It is missing target mesh topology, blend shape frame counts, or the " +
                    "per-submesh index counts they must agree with. Re-author the profile against this body.",
                    detail: "target=" + context.Base.RendererPath + "; " + expected.DescribeCapture()));
                return;
            }

            var mismatches = new List<string>();
            var advisories = new List<string>();

            // ---- Safety fields: any difference here blocks -----------------------------------------------
            CompareVertexCount(expected, actual, mismatches);
            CompareSubMeshes(expected, actual, mismatches);
            CompareBlendShapes(expected, actual, mismatches);
            var pathMismatch = CompareRendererPath(expected, context, mismatches);

            // ---- Bone signature ---------------------------------------------------------------------------
            // Blocking whenever any source in this configuration carries skinning data (spec 43.4): after the
            // armature merge the final bone table is rebuilt from the live hierarchy, so a part authored against
            // a different bone hierarchy is merged against a hierarchy it was never authored for. The signature
            // stores no bone index, so the comparison cannot mis-resolve an index; what it prevents is a
            // skinning-visible mismatch that the planner would otherwise accept silently.
            var hasSkinning = HasSkinningData(context);
            var mismatchCountBeforeBones = mismatches.Count;

            // A live bone whose entry is null has no identity at all. That is reported — and blocked — by the
            // bone table builder as APA008 reason=bone-without-identity, which is the actionable diagnostic for
            // it (it names the bone and the null entry), so the compatibility rule defers it rather than
            // restating it as a signature mismatch. Every other difference still blocks here when a source is
            // skinned.
            CompareBones(expected, actual, hasSkinning ? mismatches : advisories, hasSkinning);

            if (mismatches.Count == 0)
            {
                if (advisories.Count > 0)
                {
                    issues.Add(ValidationIssue.Warning(
                        ApaErrorCode.PartProfileIncompatible,
                        ApaIssuePhase.Compatibility,
                        "The target mesh matches the profile's topology signature, but the renderer's bone " +
                        "hierarchy differs from the one captured at authoring time. No source in this " +
                        "configuration carries skinning data, so the stored bone paths are a staleness signal " +
                        "rather than a remap input and the build continues. Re-author the profile if the change " +
                        "was not intended.",
                        detail: "target=" + context.Base.RendererPath + "; reason=bone-signature-advisory; " +
                                Join(advisories)));
                }

                return;
            }

            var detail = new StringBuilder();
            detail.Append("target=").Append(context.Base.RendererPath);
            detail.Append("; guid=").Append(DescribeGuid(expected));
            if (pathMismatch) detail.Append("; reason=renderer-path-mismatch");
            for (var i = 0; i < mismatches.Count; i++)
            {
                detail.Append("; ").Append(mismatches[i]);
            }

            // A bone-only mismatch is called out with its own message, because the remedy is different: the
            // mesh topology is the one the profile was authored against, and it is the skinning hierarchy that
            // moved. Folding it into "the target mesh does not match" would send the author looking for a body
            // replacement that did not happen.
            var boneOnly = hasSkinning
                           && mismatchCountBeforeBones == 0
                           && mismatches.Count > 0;

            if (boneOnly)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.PartProfileIncompatible,
                    ApaIssuePhase.Compatibility,
                    "The renderer's bone hierarchy no longer matches the profile's captured bone signature, and " +
                    "a source in this configuration carries skinning data, so the weights cannot be merged " +
                    "safely. Re-author the profile against this body, or restore the bone hierarchy it was " +
                    "authored against.",
                    detail: "reason=bone-signature-mismatch; " + detail));
                return;
            }

            issues.Add(ValidationIssue.Error(
                ApaErrorCode.PartProfileIncompatible,
                ApaIssuePhase.Compatibility,
                "The target mesh does not match the profile's captured signature, so the profile's triangle and " +
                "seam indices cannot be trusted. Re-author the profile against this body.",
                detail: "reason=signature-mismatch; " + detail));
        }

        /// <summary>
        /// True when any source in the configuration carries skinning data.
        /// </summary>
        /// <remarks>
        /// The bone comparison is blocking exactly when this is true. It is computed from the captured snapshots
        /// rather than from the profile, because "does this build consume bone data" is a property of the meshes
        /// being assembled, not of the authoring signature.
        /// </remarks>
        private static bool HasSkinningData(ValidationContext context)
        {
            if (context.Base?.Mesh != null && context.Base.Mesh.SkinWeights.Count > 0) return true;

            for (var i = 0; i < context.Parts.Count; i++)
            {
                var mesh = context.Parts[i]?.Mesh;
                if (mesh != null && mesh.SkinWeights.Count > 0) return true;
            }

            return false;
        }

        /// <summary>
        /// Compares the recorded target renderer path with the path the pipeline resolved.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The path is part of <c>AvatarMeshSignature</c> (spec 1440) and of the architecture's signature
        /// contract, and it is the field that tells two renderers sharing one mesh asset apart: a duplicated
        /// body, a prefab variant, or a body and its clothed copy all report the same vertex count, index
        /// counts, topologies and blend shape names. Without this comparison the compatibility rule passes for
        /// the wrong renderer and the part is welded into a body the author never selected.
        /// </para>
        /// <para>
        /// Only a recorded path is compared. The empty string means "no path was recorded" and has no expected
        /// value to differ from; that case stays the business of target resolution, which already blocks when no
        /// renderer can be resolved without guessing.
        /// </para>
        /// </remarks>
        /// <returns>True when the recorded path differs from the resolved one.</returns>
        private static bool CompareRendererPath(
            ApaAvatarCompatibilityProfile expected,
            ValidationContext context,
            List<string> mismatches)
        {
            var recorded = expected.RendererPath;
            if (!ApaAvatarPath.HasIdentity(recorded)) return false;

            var actual = context.Base != null ? context.Base.RendererPath : string.Empty;
            if (!ApaAvatarPath.HasIdentity(actual)) return false;
            if (string.Equals(recorded, actual, StringComparison.Ordinal)) return false;

            mismatches.Add("renderer path expected '" + recorded + "' but found '" + actual + "'");
            return true;
        }

        /// <summary>
        /// Renders the profile's recorded mesh GUID for the diagnostic.
        /// </summary>
        /// <remarks>
        /// A recorded GUID tells the author which asset the profile was authored against, which is what
        /// distinguishes "the body was replaced" from "this body asset was reimported with different contents".
        /// Both block; only the remedy differs, so the value is reported rather than interpreted.
        /// </remarks>
        private static string DescribeGuid(ApaAvatarCompatibilityProfile expected)
        {
            return expected.HasMeshGuid ? "recorded:" + expected.MeshGuid : "not-recorded";
        }

        private static void CompareVertexCount(
            ApaAvatarCompatibilityProfile expected,
            MeshSnapshot actual,
            List<string> mismatches)
        {
            if (expected.VertexCount == actual.VertexCount) return;

            mismatches.Add(
                "vertex count expected " + expected.VertexCount + " but found " + actual.VertexCount);
        }

        /// <summary>
        /// Compares submesh count, per-submesh index counts, and per-submesh topology.
        /// </summary>
        /// <remarks>
        /// All three are needed. Index counts alone cannot tell two submeshes apart when a model is re-exported
        /// with its submeshes reordered, and a topology is a safety field because a triangle address only
        /// resolves to a triangle in a triangle list.
        /// </remarks>
        private static void CompareSubMeshes(
            ApaAvatarCompatibilityProfile expected,
            MeshSnapshot actual,
            List<string> mismatches)
        {
            var expectedCounts = expected.SubMeshIndexCounts;
            var actualCounts = new int[actual.SubMeshCount];
            for (var i = 0; i < actual.SubMeshCount; i++)
            {
                actualCounts[i] = actual.SubMeshes[i] != null ? actual.SubMeshes[i].Length : 0;
            }

            if (expectedCounts.Length != actualCounts.Length)
            {
                mismatches.Add(
                    "submesh count expected " + expectedCounts.Length + " but found " + actualCounts.Length);
            }
            else if (!IntArraysEqual(expectedCounts, actualCounts))
            {
                mismatches.Add(
                    "submesh index counts expected [" + Join(expectedCounts) + "] but found [" +
                    Join(actualCounts) + "]");
            }

            var expectedTopologies = expected.SubMeshTopologyValues;
            if (expectedTopologies.Length != actual.SubMeshCount)
            {
                mismatches.Add(
                    "submesh topology count expected " + expectedTopologies.Length + " but found " +
                    actual.SubMeshCount);
                return;
            }

            for (var i = 0; i < expectedTopologies.Length; i++)
            {
                var actualTopology = i < actual.Topologies.Length ? (int)actual.Topologies[i] : -1;

                if (expectedTopologies[i] == actualTopology) continue;

                mismatches.Add(
                    "submesh " + i + " topology expected " + DescribeTopology(expectedTopologies[i]) +
                    " but found " + DescribeTopology(actualTopology));
            }
        }

        /// <summary>
        /// Compares blend shape names and frame counts position by position.
        /// </summary>
        /// <remarks>
        /// Order matters as much as content: M2 references a shape by index, so the same set of names in a
        /// different order still means a different shape at every stored index. Frame counts are compared
        /// because a renamed shape with a different number of frames is a different animation curve.
        /// </remarks>
        private static void CompareBlendShapes(
            ApaAvatarCompatibilityProfile expected,
            MeshSnapshot actual,
            List<string> mismatches)
        {
            var expectedNames = expected.BlendShapeNames;
            var actualNames = actual.Shapes;

            if (expectedNames.Length != actualNames.Count)
            {
                mismatches.Add(
                    "blend shape count expected " + expectedNames.Length + " but found " + actualNames.Count);
            }
            else
            {
                for (var i = 0; i < expectedNames.Length; i++)
                {
                    if (string.CompareOrdinal(
                            ApaSemanticName.Normalize(expectedNames[i]),
                            ApaSemanticName.Normalize(actualNames[i])) == 0)
                    {
                        continue;
                    }

                    mismatches.Add(
                        "blend shape " + i + " expected '" + expectedNames[i] + "' but found '" +
                        actualNames[i] + "'");
                }
            }

            var expectedFrames = expected.BlendShapeFrameCounts;
            var actualFrames = actual.ShapeFrameCounts;

            if (expectedFrames.Length != actualFrames.Count)
            {
                mismatches.Add(
                    "blend shape frame count list expected " + expectedFrames.Length + " entries but found " +
                    actualFrames.Count);
                return;
            }

            for (var i = 0; i < expectedFrames.Length; i++)
            {
                if (expectedFrames[i] == actualFrames[i]) continue;

                var name = i < actualNames.Count ? actualNames[i] : "index " + i;
                mismatches.Add(
                    "blend shape '" + name + "' expected " + expectedFrames[i] + " frame(s) but found " +
                    actualFrames[i]);
            }
        }

        /// <summary>
        /// Compares the bone signature. Blocking when any source carries skinning data, advisory otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The profile stores no bone index, so a mismatch can never mis-resolve a stored index. What it can do
        /// is merge a part authored against one hierarchy against a different hierarchy: the armature merge
        /// rebuilds the final bone table from the post-merge clone, so a part whose bone paths do not line up is
        /// appended as new bones and its weights follow transforms the author never authored against. The
        /// specification requires this comparison to be blocking for exactly that reason (section 43.4), and M6
        /// promotes it whenever a source in the configuration carries skinning data.
        /// </para>
        /// <para>
        /// When nothing in the configuration is skinned, a mismatch genuinely cannot change any output: there are
        /// no weights to remap and no bone table to build. It stays a warning there — it is a real staleness
        /// signal — and the caller passes it through <c>advisories</c> instead of <c>mismatches</c>.
        /// </para>
        /// <para>
        /// Paths are compared verbatim, including the avatar root's <see cref="ApaAvatarPath.Root"/> token and
        /// the empty string that means "null bone". The token is therefore a real comparison value here: a
        /// profile captured before it existed recorded a root bone as empty and reports one difference, which is
        /// the intended staleness signal rather than a silence.
        /// </para>
        /// </remarks>
        private static void CompareBones(
            ApaAvatarCompatibilityProfile expected,
            MeshSnapshot actual,
            List<string> destination,
            bool deferMissingLiveIdentities)
        {
            // A profile captured before bone signatures existed is not "different"; it is simply unknown, and
            // reporting an unknown as a difference would send the author looking for a bone problem that may
            // not exist. The capture helper always records a bone signature, so this only affects profiles
            // written by an older build.
            if (!expected.HasBonePaths && actual.BoneSignature.Count == 0) return;
            if (!expected.HasBonePaths)
            {
                destination.Add(
                    "bone signature was not captured by this profile, but the target renderer has " +
                    actual.BoneSignature.Count + " bone path(s)");
                return;
            }

            var expectedPaths = expected.BonePaths;
            var actualPaths = actual.BoneSignature.Paths;

            if (expectedPaths.Length != actualPaths.Count)
            {
                destination.Add(
                    "bone count expected " + expectedPaths.Length + " but found " + actualPaths.Count);
                return;
            }
            for (var i = 0; i < expectedPaths.Length; i++)
            {
                if (string.CompareOrdinal(expectedPaths[i] ?? string.Empty, actualPaths[i] ?? string.Empty) == 0)
                {
                    continue;
                }

                // Deferred to the bone table: see the call site. Only a *live* missing identity is deferred; a
                // recorded path that is empty while the live one has a value is a real staleness mismatch.
                if (deferMissingLiveIdentities && !ApaAvatarPath.HasIdentity(actualPaths[i])) continue;

                destination.Add(
                    "bone " + i + " expected '" + expectedPaths[i] + "' but found '" + actualPaths[i] + "'");
            }
        }

        private static bool IntArraysEqual(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }

        /// <summary>
        /// Renders a serialized topology value. An out-of-range value is shown numerically rather than as an
        /// undefined enum name, so a malformed signature is legible in the diagnostic.
        /// </summary>
        private static string DescribeTopology(int value)
        {
            if (value < 0) return "unknown";
            if (!Enum.IsDefined(typeof(MeshTopology), value)) return "value " + value;
            return ((MeshTopology)value).ToString();
        }

        private static string Join(int[] values)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i]);
            }

            return sb.ToString();
        }

        private static string Join(IReadOnlyList<string> values)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(values[i]);
            }

            return sb.ToString();
        }
    }
}

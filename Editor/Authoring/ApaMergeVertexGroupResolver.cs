using System;
using System.Collections.Generic;
using AvatarPartAssembler.Editor.Localization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>Where a resolved <c>merge vertex</c> group came from.</summary>
    public enum ApaMergeVertexGroupSource
    {
        /// <summary>No group was resolved.</summary>
        None = 0,

        /// <summary>The indices were listed by an <see cref="ApaMergeVertexGroup"/> component.</summary>
        MetadataComponent = 1,

        /// <summary>The indices are the vertices the <c>merge vertex</c> bone positively weights.</summary>
        SkinnedBone = 2
    }

    /// <summary>
    /// The outcome of resolving the <c>merge vertex</c> group of one renderer.
    /// </summary>
    /// <remarks>
    /// A result rather than an exception or a bare array, because the window has to show what happened: how many
    /// vertices are eligible, which of the two representations supplied them, and — when the group cannot be
    /// used — the blocking diagnostic that says why. A failure never carries a fallback index list: the whole
    /// point of the contract is that "no group" is refused instead of quietly becoming "every vertex".
    /// </remarks>
    public sealed class ApaMergeVertexGroupResult
    {
        /// <summary>True when the group was resolved to at least one candidate vertex.</summary>
        public bool Succeeded => Issue == null;

        /// <summary>The blocking diagnostic, or null on success.</summary>
        public ValidationIssue Issue { get; }

        /// <summary>
        /// The eligible vertex indices, strictly ascending and duplicate-free. Empty on failure.
        /// </summary>
        public int[] Indices { get; }

        /// <summary>Which representation supplied the indices.</summary>
        public ApaMergeVertexGroupSource Source { get; }

        /// <summary>Vertex count of the mesh the indices were resolved against, or 0 when there was no mesh.</summary>
        public int VertexCount { get; }

        private ApaMergeVertexGroupResult(
            ValidationIssue issue,
            int[] indices,
            ApaMergeVertexGroupSource source,
            int vertexCount)
        {
            Issue = issue;
            Indices = indices ?? Array.Empty<int>();
            Source = source;
            VertexCount = vertexCount;
        }

        internal static ApaMergeVertexGroupResult Failure(ValidationIssue issue)
        {
            return new ApaMergeVertexGroupResult(issue, null, ApaMergeVertexGroupSource.None, 0);
        }

        internal static ApaMergeVertexGroupResult Success(
            int[] indices,
            ApaMergeVertexGroupSource source,
            int vertexCount)
        {
            return new ApaMergeVertexGroupResult(null, indices, source, vertexCount);
        }

        /// <summary>A one-line summary of the resolved group, for the window's seam block.</summary>
        public string Describe()
        {
            if (!Succeeded) return ApaLocalization.Tr("no merge vertex group");

            return Source == ApaMergeVertexGroupSource.MetadataComponent
                ? ApaLocalization.TrFormat(
                    "{0} merge-vertex candidate(s) from the ApaMergeVertexGroup component",
                    Indices.Length)
                : ApaLocalization.TrFormat(
                    "{0} merge-vertex candidate(s) from the '{1}' bone",
                    Indices.Length,
                    ApaMergeVertexGroup.GroupName);
        }
    }

    /// <summary>
    /// Resolves the named <c>merge vertex</c> group of a renderer into explicit candidate vertex indices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The contract, in full.</b> Automatic seam generation considers only the vertices of the named group
    /// <c>merge vertex</c> (spelled exactly, compared ordinally). The group has exactly two representations,
    /// tried in this order:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>An <see cref="ApaMergeVertexGroup"/> component on the renderer's GameObject.</b> Its list is the group,
    /// verbatim. It is the only representation a non-skinned mesh can carry, and the one an import script can
    /// write. Because presence is authoritative, an empty list, an out-of-range index, a repeated index, or a
    /// list recorded against a mesh of a different size is a <b>defect</b>, not a cue to try the other
    /// representation.
    /// </description></item>
    /// <item><description>
    /// <b>A skinned bone named exactly <c>merge vertex</c>.</b> This is the representation an FBX round trip
    /// preserves: a vertex group authored in the modelling tool arrives in Unity as a bone of that name plus
    /// per-vertex weights. A vertex is in the group when at least one of its four influences names that bone
    /// with a <b>strictly positive weight</b>; a zero weight is not membership, because importers routinely
    /// write zero-weight entries. Two distinct bones sharing the name make the group ambiguous and are refused
    /// rather than resolved to a guess.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Determinism.</b> The returned indices are strictly ascending and duplicate-free on both paths: the
    /// bone path visits vertices in ascending index order, and the component path is validated and then sorted.
    /// The matcher turns the list into a mask and visits part vertices in ascending order, so the generated
    /// pairing depends only on the two meshes, the tolerance, and the group — never on the order the indices
    /// happened to be written in.
    /// </para>
    /// <para>
    /// <b>No invented encoding.</b> Nothing here reads a UV channel, a vertex colour, or an attribute to guess a
    /// group, and no other group name is accepted. A mesh that carries neither representation is refused with
    /// <c>APA051 MERGE_VERTEX_GROUP_INVALID</c>, because the only alternative would be to pair vertices the
    /// author never nominated.
    /// </para>
    /// </remarks>
    public static class ApaMergeVertexGroupResolver
    {
        /// <summary>The stable detail token for the target side.</summary>
        public const string TargetSide = "target";

        /// <summary>The stable detail token for the part side.</summary>
        public const string PartSide = "part";

        /// <summary>
        /// Lowest weight a skinned influence must exceed to make its vertex a member of the group.
        /// </summary>
        /// <remarks>
        /// Zero, compared with <c>&gt;</c>: the rule is "strictly positive weight". A zero weight is what an
        /// importer writes for a vertex that was assigned to a group but given no influence, and treating it as
        /// membership would silently widen the group to vertices the author never weighted.
        /// </remarks>
        public const float MinimumWeight = 0f;

        /// <summary>
        /// Resolves the group of one renderer.
        /// </summary>
        /// <param name="renderer">The renderer whose mesh is being paired.</param>
        /// <param name="mesh">The renderer's mesh, as the authoring selection resolved it.</param>
        /// <param name="side">
        /// <see cref="TargetSide"/> or <see cref="PartSide"/>. Used for the stable <c>side=</c> detail token and
        /// for the two messages that mirror the matcher's missing-mesh diagnostics.
        /// </param>
        public static ApaMergeVertexGroupResult Resolve(Renderer renderer, Mesh mesh, string side)
        {
            var isPart = string.Equals(side, PartSide, StringComparison.Ordinal);

            if (renderer == null || mesh == null)
            {
                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    isPart
                        ? "Select a part renderer with a mesh before generating the seam."
                        : "Select a target body renderer with a mesh before generating the seam: the seam pairs " +
                          "the part's vertices with the body's.",
                    detail: "reason=missing-" + (isPart ? PartSide : TargetSide) + "-mesh"));
            }

            if (!mesh.isReadable) return ApaMergeVertexGroupResult.Failure(ApaCompatibilityCapture.NotReadableIssue(mesh));

            var component = renderer.GetComponent<ApaMergeVertexGroup>();
            if (component != null) return FromComponent(component, mesh, side);

            return FromSkinnedBone(renderer, mesh, side);
        }

        /// <summary>
        /// The group an <see cref="ApaMergeVertexGroup"/> component lists, validated against the mesh.
        /// </summary>
        private static ApaMergeVertexGroupResult FromComponent(ApaMergeVertexGroup component, Mesh mesh, string side)
        {
            var vertexCount = mesh.vertexCount;

            if (component.IsEmpty)
            {
                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The ApaMergeVertexGroup component on the " + side + " renderer lists no vertex, so no vertex " +
                    "is eligible for seam pairing. Fill the list with the candidate indices, or remove the " +
                    "component to let the '" + ApaMergeVertexGroup.GroupName + "' bone define the group.",
                    detail: "side=" + side + "; reason=merge-vertex-group-empty; group=" +
                            ApaMergeVertexGroup.GroupName));
            }

            if (component.RecordedVertexCount >= 0 && component.RecordedVertexCount != vertexCount)
            {
                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The ApaMergeVertexGroup component on the " + side + " renderer lists indices for a mesh of " +
                    component.RecordedVertexCount + " vertex(es), but the mesh now has " + vertexCount + ". Vertex " +
                    "indices only address the mesh they were authored against, so the group is refused rather " +
                    "than paired against unrelated vertices.",
                    detail: "side=" + side + "; reason=merge-vertex-group-vertex-count-mismatch; recorded=" +
                            component.RecordedVertexCount + "; actual=" + vertexCount));
            }

            var authored = component.CopyVertexIndices();
            for (var i = 0; i < authored.Length; i++)
            {
                var index = authored[i];
                if (index >= 0 && index < vertexCount) continue;

                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The ApaMergeVertexGroup component on the " + side + " renderer lists vertex " + index +
                    ", which is outside the mesh's " + vertexCount + " vertex(es).",
                    sourceIndex: index,
                    detail: "side=" + side + "; reason=merge-vertex-group-index-out-of-range; index=" + index +
                            "; vertexCount=" + vertexCount));
            }

            Array.Sort(authored);
            for (var i = 1; i < authored.Length; i++)
            {
                if (authored[i] != authored[i - 1]) continue;

                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The ApaMergeVertexGroup component on the " + side + " renderer lists vertex " + authored[i] +
                    " more than once.",
                    sourceIndex: authored[i],
                    detail: "side=" + side + "; reason=merge-vertex-group-duplicate-index; index=" + authored[i]));
            }

            return ApaMergeVertexGroupResult.Success(
                authored,
                ApaMergeVertexGroupSource.MetadataComponent,
                vertexCount);
        }

        /// <summary>
        /// The group a skinned bone named <c>merge vertex</c> positively weights.
        /// </summary>
        /// <remarks>
        /// The bone is matched by <see cref="Transform"/> identity, not by slot: a rig that lists one transform in
        /// two slots still names one bone. Two <i>distinct</i> transforms with the name are ambiguous, because
        /// their weights are two different groups and this resolver has no basis to prefer one.
        /// </remarks>
        private static ApaMergeVertexGroupResult FromSkinnedBone(Renderer renderer, Mesh mesh, string side)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null)
            {
                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The " + side + " renderer carries no skinning, so it cannot declare a '" +
                    ApaMergeVertexGroup.GroupName + "' vertex group and no ApaMergeVertexGroup component is " +
                    "attached to list the candidate indices. Name the group in the source as a bone of that name, " +
                    "or attach the component.",
                    detail: "side=" + side + "; reason=merge-vertex-group-not-skinned; group=" +
                            ApaMergeVertexGroup.GroupName));
            }

            var bones = skinned.bones;
            var groupBone = FindGroupBone(bones, out var ambiguous);
            if (ambiguous)
            {
                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The " + side + " renderer has more than one bone named exactly '" +
                    ApaMergeVertexGroup.GroupName + "', so the group they define is ambiguous. Keep one bone of " +
                    "that name, or attach an ApaMergeVertexGroup component that lists the indices explicitly.",
                    detail: "side=" + side + "; reason=merge-vertex-group-ambiguous-bone; group=" +
                            ApaMergeVertexGroup.GroupName));
            }

            if (groupBone == null)
            {
                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The " + side + " renderer declares no '" + ApaMergeVertexGroup.GroupName + "' vertex group, " +
                    "so no vertex is eligible for seam pairing. Name a vertex group exactly '" +
                    ApaMergeVertexGroup.GroupName + "' on the source (a skinned bone of that name with a positive " +
                    "weight), or attach an ApaMergeVertexGroup component that lists the candidate indices.",
                    detail: "side=" + side + "; reason=merge-vertex-group-missing; group=" +
                            ApaMergeVertexGroup.GroupName));
            }

            var vertexCount = mesh.vertexCount;
            var weights = mesh.boneWeights;
            if (weights == null || weights.Length == 0)
            {
                return NoWeightedVertices(side, vertexCount, "reason=merge-vertex-group-no-weighted-vertices");
            }

            if (weights.Length != vertexCount)
            {
                return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                    ApaErrorCode.MergeVertexGroupInvalid,
                    ApaIssuePhase.Seam,
                    "The " + side + " mesh carries " + weights.Length + " bone weight(s) for " + vertexCount +
                    " vertex(es), so its skinning cannot be indexed by vertex and the '" +
                    ApaMergeVertexGroup.GroupName + "' group cannot be read.",
                    detail: "side=" + side + "; reason=merge-vertex-group-weight-count-mismatch; weights=" +
                            weights.Length + "; vertexCount=" + vertexCount));
            }

            var indices = new List<int>();
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                if (!IsWeightedTo(weights[vertex], bones, groupBone)) continue;
                indices.Add(vertex);
            }

            if (indices.Count == 0)
            {
                return NoWeightedVertices(side, vertexCount, "reason=merge-vertex-group-no-weighted-vertices");
            }

            return ApaMergeVertexGroupResult.Success(
                indices.ToArray(),
                ApaMergeVertexGroupSource.SkinnedBone,
                vertexCount);
        }

        /// <summary>
        /// True when at least one influence of a vertex names the group bone with a strictly positive weight.
        /// </summary>
        /// <remarks>
        /// An influence whose bone index is outside the renderer's bone list names no bone, so it cannot be the
        /// group bone; the core reports that defect as <c>APA007</c> when the mesh is actually assembled, and this
        /// read must not invent a membership from it.
        /// </remarks>
        private static bool IsWeightedTo(BoneWeight weight, Transform[] bones, Transform groupBone)
        {
            if (bones == null) return false;

            return IsInfluence(weight.boneIndex0, weight.weight0, bones, groupBone)
                   || IsInfluence(weight.boneIndex1, weight.weight1, bones, groupBone)
                   || IsInfluence(weight.boneIndex2, weight.weight2, bones, groupBone)
                   || IsInfluence(weight.boneIndex3, weight.weight3, bones, groupBone);
        }

        private static bool IsInfluence(int boneIndex, float weight, Transform[] bones, Transform groupBone)
        {
            if (weight <= MinimumWeight) return false;
            if (boneIndex < 0 || boneIndex >= bones.Length) return false;
            return bones[boneIndex] == groupBone;
        }

        /// <summary>
        /// The one bone named <c>merge vertex</c>, or null. <paramref name="ambiguous"/> reports a second,
        /// distinct transform under the same name.
        /// </summary>
        private static Transform FindGroupBone(Transform[] bones, out bool ambiguous)
        {
            ambiguous = false;
            Transform found = null;

            if (bones == null) return null;

            for (var i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;
                if (!string.Equals(bone.name, ApaMergeVertexGroup.GroupName, StringComparison.Ordinal)) continue;

                if (found == null)
                {
                    found = bone;
                    continue;
                }

                if (found != bone)
                {
                    ambiguous = true;
                    return found;
                }
            }

            return found;
        }

        private static ApaMergeVertexGroupResult NoWeightedVertices(string side, int vertexCount, string reason)
        {
            return ApaMergeVertexGroupResult.Failure(ValidationIssue.Error(
                ApaErrorCode.MergeVertexGroupInvalid,
                ApaIssuePhase.Seam,
                "The '" + ApaMergeVertexGroup.GroupName + "' bone of the " + side + " renderer weights no vertex " +
                "with a positive weight, so the group is empty. Assign the seam vertices to that vertex group in " +
                "the source and export the skinning, or attach an ApaMergeVertexGroup component that lists the " +
                "candidate indices.",
                detail: "side=" + side + "; " + reason + "; group=" + ApaMergeVertexGroup.GroupName +
                        "; vertexCount=" + vertexCount));
        }
    }
}

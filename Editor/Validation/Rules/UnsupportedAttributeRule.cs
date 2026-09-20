using System;
using System.Collections.Generic;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Rejects values that cannot be interpreted at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until M2 this rule also blocked skinning and blend shapes, because M1 could not preserve them. M2
    /// processes both, so those checks moved to <see cref="SkinningRule"/> and <see cref="BlendShapeRule"/>,
    /// which validate the data instead of refusing it. What remains here is the check that no rule or
    /// comparison can recover from: a non-finite value.
    /// </para>
    /// <para>
    /// A NaN or Infinity is rejected before any comparison because it makes every epsilon comparison false. A
    /// NaN position would otherwise surface as a confusing "no seam vertex within epsilon", sending the author
    /// to look for a seam defect that does not exist. <c>APA016</c> is the accurate answer.
    /// </para>
    /// </remarks>
    public sealed class UnsupportedAttributeRule : IApaValidationRule
    {
        /// <summary>The stable rule name.</summary>
        public const string RuleName = "UnsupportedAttributeRule";

        /// <inheritdoc />
        public string Name => RuleName;

        /// <inheritdoc />
        public void Validate(ValidationContext context, List<ValidationIssue> issues)
        {
            if (context.Base != null && context.Base.Mesh != null)
            {
                InspectMesh(context.Base.Mesh, string.Empty, string.Empty, issues);
            }

            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null) continue;
                InspectMesh(part.Mesh, part.PartId, string.Empty, issues);
            }
        }

        private static void InspectMesh(
            MeshSnapshot mesh,
            string partId,
            string displayName,
            List<ValidationIssue> issues)
        {
            var who = string.IsNullOrEmpty(displayName) ? "the base body" : "'" + displayName + "'";
            var meshName = string.IsNullOrEmpty(mesh.Name) ? "(unnamed mesh)" : mesh.Name;

            // Non-finite values are rejected before any comparison, because a NaN position would make every
            // epsilon comparison false and surface as a confusing "no seam match" instead of the real defect.
            var nonFiniteVertex = FindNonFiniteVertex(mesh);
            if (nonFiniteVertex >= 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.NonFiniteValue,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + meshName + "' on " + who + " has a non-finite position at vertex " + nonFiniteVertex + ".",
                    partId,
                    nonFiniteVertex,
                    detail: "attribute=position; vertex=" + nonFiniteVertex));
            }

            var nonFiniteNormal = FindNonFiniteNormal(mesh);
            if (nonFiniteNormal >= 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.NonFiniteValue,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + meshName + "' on " + who + " has a non-finite normal at vertex " + nonFiniteNormal + ".",
                    partId,
                    nonFiniteNormal,
                    detail: "attribute=normal; vertex=" + nonFiniteNormal));
            }

            var nonFiniteTangent = FindNonFiniteTangent(mesh);
            if (nonFiniteTangent >= 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.NonFiniteValue,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + meshName + "' on " + who + " has a non-finite tangent at vertex " + nonFiniteTangent + ".",
                    partId,
                    nonFiniteTangent,
                    detail: "attribute=tangent; vertex=" + nonFiniteTangent));
            }

            var nonFiniteUv = FindNonFiniteUv(mesh, out var uvChannel);
            if (nonFiniteUv >= 0)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.NonFiniteValue,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + meshName + "' on " + who + " has a non-finite UV at vertex " + nonFiniteUv +
                    " in channel " + uvChannel + ".",
                    partId,
                    nonFiniteUv,
                    uvChannel,
                    detail: "attribute=uv; channel=" + uvChannel + "; vertex=" + nonFiniteUv));
            }
        }

        private static int FindNonFiniteVertex(MeshSnapshot mesh)
        {
            for (var i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];
                if (!ApaNumericPolicy.IsFinite(v.x) || !ApaNumericPolicy.IsFinite(v.y) || !ApaNumericPolicy.IsFinite(v.z))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindNonFiniteNormal(MeshSnapshot mesh)
        {
            for (var i = 0; i < mesh.Normals.Count; i++)
            {
                var v = mesh.Normals[i];
                if (!ApaNumericPolicy.IsFinite(v.x) || !ApaNumericPolicy.IsFinite(v.y) || !ApaNumericPolicy.IsFinite(v.z))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindNonFiniteTangent(MeshSnapshot mesh)
        {
            for (var i = 0; i < mesh.Tangents.Count; i++)
            {
                var v = mesh.Tangents[i];
                if (!ApaNumericPolicy.IsFinite(v.x) || !ApaNumericPolicy.IsFinite(v.y)
                    || !ApaNumericPolicy.IsFinite(v.z) || !ApaNumericPolicy.IsFinite(v.w))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindNonFiniteUv(MeshSnapshot mesh, out int channel)
        {
            for (var c = 0; c < mesh.UvChannelCapacity; c++)
            {
                var data = mesh.Uvs[c];
                if (data == null) continue;

                for (var i = 0; i < data.Length; i++)
                {
                    var v = data[i];
                    if (!ApaNumericPolicy.IsFinite(v.x) || !ApaNumericPolicy.IsFinite(v.y)
                        || !ApaNumericPolicy.IsFinite(v.z) || !ApaNumericPolicy.IsFinite(v.w))
                    {
                        channel = c;
                        return i;
                    }
                }
            }

            channel = -1;
            return -1;
        }
    }
}

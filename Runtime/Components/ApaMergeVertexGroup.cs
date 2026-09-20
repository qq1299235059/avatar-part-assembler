using System;
using UnityEngine;

namespace AvatarPartAssembler
{
    /// <summary>
    /// Authoring metadata that names the vertices eligible for automatic seam pairing on one renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this component exists.</b> Unity's <see cref="Mesh"/> has no generic named vertex-group API: a
    /// vertex group authored in a modelling tool survives an FBX import only as <i>skinning</i> — a bone of that
    /// name plus per-vertex weights — so a mesh that is not skinned (a static part, or a body the author does not
    /// want to re-rig) has no native way to carry the group. This component is the APA-native representation for
    /// exactly that case: it stores the candidate vertex indices of the renderer's mesh, it is a serialized
    /// component so it survives a prefab, and it is inert at runtime.
    /// </para>
    /// <para>
    /// <b>It is authoring data, never build data.</b> The build consumes the explicit seam pairs stored in
    /// <c>ApaPartProfile</c>; nothing in the assembly pipeline reads this component. It is read by the Part
    /// Authoring window when it generates a seam, so removing it after the seam is generated changes nothing
    /// about an already authored part.
    /// </para>
    /// <para>
    /// <b>The indices belong to one mesh.</b> They are indices into the mesh the renderer carried when the group
    /// was authored, which is why <see cref="RecordedVertexCount"/> is stored beside them: a mesh whose vertex
    /// count no longer matches cannot be addressed by the same indices, and the authoring window refuses the
    /// group instead of silently pairing unrelated vertices.
    /// </para>
    /// <para>
    /// <b>Presence is authoritative.</b> When this component is attached to a renderer, its list is the group:
    /// an empty list is a defect the window blocks on, never an instruction to fall back to another source. That
    /// is what keeps "which vertices are eligible" a single, inspectable answer.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Part Assembler/Merge Vertex Group")]
    public sealed class ApaMergeVertexGroup : MonoBehaviour
    {
        /// <summary>
        /// The one group name the seam contract recognizes, spelled exactly.
        /// </summary>
        /// <remarks>
        /// The comparison is ordinal and case-sensitive: <c>Merge Vertex</c>, <c>merge_vertex</c>, and
        /// <c>merge vertex </c> are different names and are not the group. A name that is almost right must fail
        /// loudly rather than quietly match, because the group decides which vertices may be welded.
        /// </remarks>
        public const string GroupName = "merge vertex";

        [SerializeField] private int[] _vertexIndices = Array.Empty<int>();
        [SerializeField] private int _recordedVertexCount = -1;

        /// <summary>Number of indices the group lists.</summary>
        public int IndexCount => _vertexIndices != null ? _vertexIndices.Length : 0;

        /// <summary>True when the group lists no index at all.</summary>
        public bool IsEmpty => IndexCount == 0;

        /// <summary>
        /// Vertex count of the mesh these indices were authored against, or -1 when it was never recorded.
        /// </summary>
        public int RecordedVertexCount => _recordedVertexCount;

        /// <summary>
        /// The indices as a private copy, in the order they were authored.
        /// </summary>
        /// <remarks>
        /// A copy rather than the serialized array itself: the array is Unity's serialized state, and a caller
        /// that sorted or deduplicated it in place would rewrite the author's data as a side effect of a read.
        /// </remarks>
        public int[] CopyVertexIndices()
        {
            if (_vertexIndices == null) return Array.Empty<int>();

            var copy = new int[_vertexIndices.Length];
            Array.Copy(_vertexIndices, copy, _vertexIndices.Length);
            return copy;
        }

        /// <summary>
        /// Replaces the group with a copy of <paramref name="vertexIndices"/> and records the mesh size it
        /// addresses.
        /// </summary>
        /// <param name="vertexIndices">The candidate vertex indices, or null to clear the group.</param>
        /// <param name="meshVertexCount">
        /// Vertex count of the mesh the indices address, or -1 when the caller cannot know it.
        /// </param>
        /// <remarks>
        /// The values are stored exactly as given: no sorting, no deduplication, and no range check. Normalizing
        /// here would hide an invalid index from the authoring window, which is the surface that has to report
        /// it.
        /// </remarks>
        public void SetVertexIndices(int[] vertexIndices, int meshVertexCount)
        {
            if (vertexIndices == null || vertexIndices.Length == 0)
            {
                _vertexIndices = Array.Empty<int>();
                _recordedVertexCount = meshVertexCount;
                return;
            }

            var copy = new int[vertexIndices.Length];
            Array.Copy(vertexIndices, copy, vertexIndices.Length);
            _vertexIndices = copy;
            _recordedVertexCount = meshVertexCount;
        }
    }
}

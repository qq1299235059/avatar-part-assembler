using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Hard limits imposed by Unity and by the product specification, gathered in one place so that no rule
    /// hard-codes a magic number.
    /// </summary>
    public static class ApaMeshLimits
    {
        /// <summary>Unity supports UV0 through UV7.</summary>
        public const int MaxUvChannels = 8;

        /// <summary>Vertices in a mesh must be a multiple of three for a triangle list.</summary>
        public const int TriangleStride = 3;

        /// <summary>
        /// Unity switches from 16-bit to 32-bit indices at 65535 vertices. The output index format is chosen
        /// from the actual output vertex count rather than inherited, so that a merged mesh that grew past the
        /// 16-bit boundary is still valid.
        /// </summary>
        public const int UInt16VertexLimit = 65535;

        /// <summary>Returns the index format required for a vertex count.</summary>
        public static UnityEngine.Rendering.IndexFormat IndexFormatForVertexCount(int vertexCount)
        {
            return vertexCount > UInt16VertexLimit
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
        }
    }
}

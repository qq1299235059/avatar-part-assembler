using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// What to do with the triangles a mask produced, relative to the removal set the author already has.
    /// </summary>
    /// <remarks>
    /// The names describe the operation on the <i>removal set</i>, not on the mask's colors: "add" means "also
    /// remove these triangles". The serialized enumeration values are part of the window's own state only —
    /// nothing here is written to a profile — but they are numbered explicitly so a reload cannot renumber them.
    /// </remarks>
    public enum ApaMaskApplyMode
    {
        /// <summary>The removal set becomes exactly the generated set.</summary>
        ReplaceSelection = 0,

        /// <summary>The generated set is unioned into the removal set.</summary>
        AddToSelection = 1,

        /// <summary>The generated set is removed from the removal set.</summary>
        SubtractFromSelection = 2
    }

    /// <summary>
    /// Why a mask-to-triangle conversion produced no selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One stable token per condition, rendered as <c>reason=&lt;token&gt;</c> in the diagnostic detail, so a bug
    /// report, a test, and a log line agree on what happened without depending on the prose around it. Every
    /// member is a hard refusal: the conversion never guesses, never clamps an out-of-range threshold into range,
    /// and never reinterprets a topology it does not support.
    /// </para>
    /// <para>
    /// <b>Every member has its own explicit value, and the values are never reused.</b> They are numbered rather
    /// than left implicit so a reload, a reorder, or a deleted member cannot silently give two conditions the
    /// same number; a failure recorded as <c>6</c> means <see cref="TextureReadbackFailed"/> today and must mean
    /// the same thing in every later build.
    /// </para>
    /// </remarks>
    public enum ApaMaskSampleFailure
    {
        /// <summary>The conversion produced a selection.</summary>
        None = 0,

        /// <summary>No target mesh was supplied.</summary>
        MissingMesh = 1,

        /// <summary>The target mesh cannot be read, so its UVs and index buffers are unavailable.</summary>
        MeshNotReadable = 2,

        /// <summary>No mask texture was supplied.</summary>
        MissingTexture = 3,

        /// <summary>The texture is a type this conversion cannot sample (not a 2D texture).</summary>
        UnsupportedTextureType = 4,

        /// <summary>The texture's size is not usable (a zero dimension).</summary>
        UnsupportedTextureSize = 5,

        /// <summary>The texture readback failed inside the graphics device.</summary>
        TextureReadbackFailed = 6,

        /// <summary>The UV channel index is outside 0 through 7.</summary>
        InvalidUvChannel = 7,

        /// <summary>The mesh has no data for the requested UV channel, or its entry count does not match the mesh.</summary>
        UvChannelAbsent = 8,

        /// <summary>The threshold is not a finite number.</summary>
        NonFiniteThreshold = 9,

        /// <summary>The threshold is finite but outside 0 through 1.</summary>
        ThresholdOutOfRange = 10,

        /// <summary>A submesh's topology is not a triangle list.</summary>
        UnsupportedTopology = 11,

        /// <summary>A triangle references a vertex outside the mesh's vertex range.</summary>
        VertexIndexOutOfRange = 12,

        /// <summary>A UV coordinate used by the selection is not finite.</summary>
        NonFiniteUv = 13
    }

    /// <summary>
    /// The outcome of converting one target mesh, one mask texture, and one UV channel into a removal selection.
    /// </summary>
    /// <remarks>
    /// A result with <see cref="Succeeded"/> and an empty <see cref="Addresses"/> is a valid, intentionally
    /// representable answer: "nothing in this mask crosses the threshold". It is deliberately not an error,
    /// because Replace may clear a selection the author no longer wants, and a mask that selects nothing is how
    /// the author says exactly that.
    /// </remarks>
    public sealed class ApaMaskSampleResult
    {
        private readonly RemovedTriangleAddress[] _addresses;

        /// <summary>True when the conversion ran to completion and produced <see cref="Addresses"/>.</summary>
        public bool Succeeded { get; }

        /// <summary>
        /// The generated addresses, in canonical order (ascending submesh, then ascending triangle within the
        /// submesh), without duplicates. Empty both when the conversion failed and when it selected nothing;
        /// <see cref="Succeeded"/> is what distinguishes the two.
        /// </summary>
        public IReadOnlyList<RemovedTriangleAddress> Addresses => _addresses;

        /// <summary>Why the conversion failed, or <see cref="ApaMaskSampleFailure.None"/> when it succeeded.</summary>
        public ApaMaskSampleFailure Failure { get; }

        /// <summary>
        /// The actionable diagnostic, or null when the conversion succeeded. The same instance the window
        /// renders, so a caller never has to reconstruct the message.
        /// </summary>
        public ValidationIssue Issue { get; }

        /// <summary>Triangles in the mesh that the conversion considered: every triangle of every triangle-list submesh.</summary>
        public int ConsideredTriangleCount { get; }

        private ApaMaskSampleResult(
            RemovedTriangleAddress[] addresses,
            ApaMaskSampleFailure failure,
            ValidationIssue issue,
            int consideredTriangleCount)
        {
            _addresses = addresses ?? Array.Empty<RemovedTriangleAddress>();
            Succeeded = failure == ApaMaskSampleFailure.None;
            Failure = failure;
            Issue = issue;
            ConsideredTriangleCount = consideredTriangleCount;
        }

        internal static ApaMaskSampleResult Success(RemovedTriangleAddress[] addresses, int consideredTriangleCount)
        {
            return new ApaMaskSampleResult(
                addresses,
                ApaMaskSampleFailure.None,
                null,
                consideredTriangleCount);
        }

        internal static ApaMaskSampleResult Failed(ApaMaskSampleFailure failure, ValidationIssue issue)
        {
            return new ApaMaskSampleResult(null, failure, issue, 0);
        }
    }

    /// <summary>
    /// Converts a black/white mask texture, through the UVs of a target mesh, into a deterministic removal
    /// triangle selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this is.</b> Clicking removal triangles one at a time in the Scene View does not scale. A mask is
    /// the authoring input that does: paint the region white, point at the mesh UV channel the tool should read,
    /// and get the exact triangle set back. The output is the <i>existing</i> canonical
    /// <see cref="RemovedTriangleAddress"/> form — a submesh index and a triangle index within that submesh — so
    /// everything downstream is unchanged: the result flows through <see cref="ApaRemovalMask"/>'s
    /// canonicalization, the profile writer, the removal rule, and the assembler exactly as a hand-picked
    /// triangle does. The mask texture is an authoring input; it is never stored in the profile, never a build
    /// dependency, and never referenced by the prefab.
    /// </para>
    /// <para>
    /// <b>The rule is fixed, and here it is.</b> Determinism is the whole point of converting a picture into a
    /// triangle set, so nothing about the conversion is configurable beyond the inputs the author already sees.
    /// For every triangle of every triangle-list submesh:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>7 sample points</b> are taken from the triangle's UVs: the three vertices, the three edge midpoints, and
    /// the centroid. A single vertex sample would miss a mask boundary that cuts a triangle in half; seven points
    /// with a majority vote resolves a boundary to within roughly a quarter of a triangle and cannot be swayed by
    /// one unlucky corner.
    /// </description></item>
    /// <item><description>
    /// Each sample's <b>brightness</b> is the RGB luminance <c>0.299·R + 0.587·G + 0.114·B</c> of the sampled
    /// color. <b>Alpha is ignored</b>: the mask's meaning is its luminance, and letting alpha participate would
    /// make a fully transparent white pixel and an opaque black pixel agree. The coefficients are the Rec.601
    /// luma weights, which is what "grayscale" means for a black/white mask, and they are applied to the texture's
    /// stored 8-bit values without a color-space conversion, so the same PNG selects the same triangles in a
    /// linear project and in a gamma project.
    /// </description></item>
    /// <item><description>
    /// With <b>invert</b> off, a sample passes when its brightness is greater than or equal to the threshold;
    /// with invert on, the brightness is replaced by <c>1 − brightness</c> first. Invert therefore reverses what
    /// white and black mean without touching any other part of the rule.
    /// </description></item>
    /// <item><description>
    /// The triangle is selected when <b>at least 4 of the 7</b> samples pass — a strict majority. Ties are
    /// impossible with seven samples, so no tie-breaking rule is needed and none exists.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Sampling respects the mask's own import settings.</b> Bilinear filtering is used for a bilinear or
    /// trilinear texture and nearest-texel sampling for a point-filtered texture. The interpolation position is
    /// derived from the UV exactly as the hardware derives it — texel centers at <c>(i + 0.5) / size</c> — and the
    /// texture's wrap mode addresses the integer texel indices: Repeat wraps the index around the edge, so a
    /// sample at <c>u = 0</c> interpolates the last and the first texel; Mirror reflects the index through a
    /// doubled period; Clamp and <c>MirrorOnce</c> stop at the border texel. The result is a pure function of the
    /// mesh, the texture's pixels and import settings, and the four parameters.
    /// </para>
    /// <para>
    /// <b>The readback boundary, and the only mutable thing in this file.</b> Reading a texture's pixels
    /// normally requires Read/Write Enabled, and requiring the author to change a mask's import settings would
    /// defeat the point of the workflow — it also makes the tool's behaviour depend on an importer flag rather
    /// than on the picture. So the mask is never read directly and no importer setting is ever touched: it is
    /// rendered into a temporary render texture and read back from there, and every temporary object is released
    /// on every path, so a failure halfway through leaves nothing behind. The input mesh and the input texture are
    /// only ever read.
    /// </para>
    /// <para>
    /// <b>What it refuses.</b> A null mesh or texture, an unreadable mesh, a UV channel outside 0–7 or absent from
    /// the mesh, a UV array whose length disagrees with the vertex count, a non-finite or out-of-range threshold,
    /// a non-triangle-list submesh, a vertex index outside the mesh, a non-finite UV, and a readback the device
    /// refused all stop the conversion with an <c>APA041</c> issue that names the condition. Nothing is silently
    /// reinterpreted: unsupported topology is an error rather than "treated as triangles", because guessing there
    /// would select triangles the author never painted. A texture's pixel format is never a refusal: a compressed,
    /// crunched, or non-readable mask is decoded by the render path like any other picture, and only a source the
    /// device itself cannot render and read back fails, as <c>reason=texture-readback-failed</c>.
    /// </para>
    /// </remarks>
    public static class ApaRemovalMaskSampler
    {
        /// <summary>
        /// The number of sample points per triangle: three vertices, three edge midpoints, one centroid.
        /// Part of the fixed rule; documented here so no caller has to guess it.
        /// </summary>
        public const int SampleCount = 7;

        /// <summary>
        /// How many of the <see cref="SampleCount"/> samples must pass for the triangle to be selected: a strict
        /// majority. Part of the fixed rule.
        /// </summary>
        public const int MajoritySampleCount = 4;

        /// <summary>Weight of the red channel in the RGB luminance the rule uses. Part of the fixed rule.</summary>
        public const float LuminanceRedWeight = 0.299f;

        /// <summary>Weight of the green channel in the RGB luminance the rule uses. Part of the fixed rule.</summary>
        public const float LuminanceGreenWeight = 0.587f;

        /// <summary>Weight of the blue channel in the RGB luminance the rule uses. Part of the fixed rule.</summary>
        public const float LuminanceBlueWeight = 0.114f;

        /// <summary>
        /// Converts a mask texture into removal triangle addresses for one UV channel of one mesh.
        /// </summary>
        /// <param name="mesh">The target mesh whose UVs address the mask. Read only; never mutated.</param>
        /// <param name="mask">The black/white mask texture. Read only; its importer settings are never changed.</param>
        /// <param name="uvChannel">UV channel to read, 0 through 7.</param>
        /// <param name="threshold">
        /// Brightness at or above which a sample passes. Must be a finite number in 0 through 1; the caller clamps
        /// its control, but this method refuses rather than clamps so that a bad value can never silently become a
        /// different one.
        /// </param>
        /// <param name="invert">True to reverse the meaning of white and black.</param>
        /// <returns>
        /// A result that either carries the canonical address list or an <c>APA041</c> issue explaining exactly
        /// what stopped the conversion. <see cref="ApaMaskSampleResult.Succeeded"/> is the single predicate to
        /// branch on; an empty successful result means the mask selected nothing.
        /// </returns>
        public static ApaMaskSampleResult TryGenerate(
            Mesh mesh,
            Texture2D mask,
            int uvChannel,
            float threshold,
            bool invert)
        {
            var validation = ValidateInputs(mesh, mask, uvChannel, threshold);
            if (validation != null) return validation;

            if (!TryReadMask(mask, out var pixels, out var width, out var height, out var wrap, out var filter,
                    out var readbackFailure))
            {
                return readbackFailure;
            }

            var uvs = ReadUvChannel(mesh, uvChannel, out var uvFailure);
            if (uvFailure != null) return uvFailure;

            var addresses = new List<RemovedTriangleAddress>();
            var considered = 0;
            var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);

            for (var subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                var topology = mesh.GetTopology(subMesh);
                if (topology != MeshTopology.Triangles)
                {
                    // A submesh the conversion cannot address is refused, never treated as triangles: the
                    // triangles of a line or point list are not a triangle set, and reinterpreting them would
                    // select geometry the author never painted.
                    return Failure(
                        ApaMaskSampleFailure.UnsupportedTopology,
                        "The target mesh submesh " + subMesh + " uses " + topology + " topology. Only triangle " +
                        "lists can be converted into a removal selection.",
                        "reason=unsupported-topology; submesh=" + subMesh + "; topology=" + topology);
                }

                var indices = mesh.GetIndices(subMesh);
                if (indices == null) indices = Array.Empty<int>();

                var triangleCount = indices.Length / ApaMeshLimits.TriangleStride;
                considered += triangleCount;

                for (var triangle = 0; triangle < triangleCount; triangle++)
                {
                    var baseIndex = triangle * ApaMeshLimits.TriangleStride;
                    var first = indices[baseIndex];
                    var second = indices[baseIndex + 1];
                    var third = indices[baseIndex + 2];

                    if (!IsVertexIndex(first, uvs.Length)
                        || !IsVertexIndex(second, uvs.Length)
                        || !IsVertexIndex(third, uvs.Length))
                    {
                        return Failure(
                            ApaMaskSampleFailure.VertexIndexOutOfRange,
                            "Triangle " + triangle + " of submesh " + subMesh + " references vertex " + first +
                            ", " + second + ", or " + third + " but the mesh has " + uvs.Length + " vertex " +
                            "UV entry(ies).",
                            "reason=vertex-index-out-of-range; submesh=" + subMesh + "; triangle=" + triangle +
                            "; meshVertexCount=" + uvs.Length);
                    }

                    if (!TryEvaluateTriangle(
                            uvs[first], uvs[second], uvs[third], pixels, width, height, wrap, filter, threshold,
                            invert, out var passedSamples, out var badUv))
                    {
                        return Failure(
                            ApaMaskSampleFailure.NonFiniteUv,
                            "Triangle " + triangle + " of submesh " + subMesh + " has the non-finite UV " +
                            Describe(badUv) + ", so the mask cannot be sampled there.",
                            "reason=non-finite-uv; submesh=" + subMesh + "; triangle=" + triangle + "; uv=" +
                            Describe(badUv));
                    }

                    if (passedSamples >= MajoritySampleCount)
                    {
                        // Submesh-major, triangle-minor: the loop order is already the canonical order, so no
                        // sort is needed and the result is deterministic by construction.
                        addresses.Add(new RemovedTriangleAddress(subMesh, triangle));
                    }
                }
            }

            return ApaMaskSampleResult.Success(addresses.ToArray(), considered);
        }

        /// <summary>
        /// The brightness of one UV coordinate of a mask, using this class's fixed sampling rule.
        /// </summary>
        /// <remarks>
        /// Exposed because it is the part of the rule a reviewer or a debug view wants to check against the
        /// picture: given the same mesh, the same texture, and the same settings, this returns the number the
        /// conversion compares. The UV is addressed through the texture's own wrap and filter modes, and the call
        /// performs the same temporary readback as <see cref="TryGenerate(Mesh, Texture2D, int, float, bool)"/>
        /// and the same release.
        /// </remarks>
        /// <returns>
        /// False when the texture cannot be sampled, with an <c>APA041</c> issue in <paramref name="issue"/>;
        /// true otherwise, with the luminance before inversion in <paramref name="luminance"/>.
        /// </returns>
        public static bool TrySampleLuminance(
            Texture2D mask,
            Vector2 uv,
            out float luminance,
            out ValidationIssue issue)
        {
            luminance = 0f;
            issue = null;

            if (mask == null)
            {
                issue = Failure(
                    ApaMaskSampleFailure.MissingTexture,
                    "No mask texture was supplied.",
                    "reason=missing-texture").Issue;
                return false;
            }

            if (!TryReadMask(mask, out var pixels, out var width, out var height, out var wrap, out var filter,
                    out var readbackFailure))
            {
                issue = readbackFailure.Issue;
                return false;
            }

            luminance = SampleLuminance(uv, pixels, width, height, wrap, filter);
            return true;
        }

        // ---- Input validation ------------------------------------------------------------------------------

        /// <summary>
        /// Returns a failed result for the first input defect, or null when every input is usable.
        /// </summary>
        /// <remarks>
        /// The order is the order the author would check things in: which object is missing, then whether it can
        /// be read, then which channel, then the number they typed. Reporting the first defect rather than all of
        /// them keeps the status line actionable; the remaining ones surface on the next attempt.
        /// </remarks>
        private static ApaMaskSampleResult ValidateInputs(Mesh mesh, Texture2D mask, int uvChannel, float threshold)
        {
            if (mesh == null)
            {
                return Failure(
                    ApaMaskSampleFailure.MissingMesh,
                    "No target mesh is selected, so there are no UVs to read the mask through. Select the target " +
                    "body renderer first.",
                    "reason=missing-target-mesh");
            }

            if (!mesh.isReadable)
            {
                return Failure(
                    ApaMaskSampleFailure.MeshNotReadable,
                    "Mesh '" + mesh.name + "' is not readable, so its UVs and index buffers cannot be read. " +
                    "Enable Read/Write in its import settings.",
                    "reason=not-readable; mesh=" + mesh.name);
            }

            if (mask == null)
            {
                return Failure(
                    ApaMaskSampleFailure.MissingTexture,
                    "No mask texture is assigned, so there is nothing to convert. Assign a black/white texture " +
                    "whose UVs match the target mesh.",
                    "reason=missing-mask-texture");
            }

            // The check runs against the reference as an Object rather than as the declared Texture2D parameter, so
            // it is real runtime dispatch: a caller that reached this method through a derived type (a render
            // texture, a cubemap or 3D texture subclass, or a future type) is refused rather than silently
            // sampled as if it were a 2D texture. The conversion addresses a mask by UV, and only a 2D texture has
            // one.
            var textureObject = (UnityEngine.Object)mask;
            if (textureObject is RenderTexture || mask.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
            {
                return Failure(
                    ApaMaskSampleFailure.UnsupportedTextureType,
                    "Mask '" + mask.name + "' is a " + mask.dimension + " texture. The mask must be a 2D texture, " +
                    "because the conversion samples it through UV coordinates.",
                    "reason=unsupported-texture-type; texture=" + mask.name + "; dimension=" + mask.dimension);
            }

            if (mask.width <= 0 || mask.height <= 0)
            {
                return Failure(
                    ApaMaskSampleFailure.UnsupportedTextureSize,
                    "Mask '" + mask.name + "' has size " + mask.width + "x" + mask.height + " and cannot be " +
                    "sampled.",
                    "reason=unsupported-texture-size; texture=" + mask.name + "; width=" + mask.width +
                    "; height=" + mask.height);
            }

            if (uvChannel < 0 || uvChannel >= ApaMeshLimits.MaxUvChannels)
            {
                return Failure(
                    ApaMaskSampleFailure.InvalidUvChannel,
                    "UV channel " + uvChannel + " is outside 0 through " + (ApaMeshLimits.MaxUvChannels - 1) +
                    ". A mesh has at most " + ApaMeshLimits.MaxUvChannels + " UV channels.",
                    "reason=invalid-uv-channel; channel=" + uvChannel + "; maxChannels=" +
                    ApaMeshLimits.MaxUvChannels);
            }

            if (float.IsNaN(threshold) || float.IsInfinity(threshold))
            {
                return Failure(
                    ApaMaskSampleFailure.NonFiniteThreshold,
                    "The grayscale threshold is not a finite number, so no sample can be classified by it.",
                    "reason=non-finite-threshold; threshold=" + threshold.ToString("R",
                        System.Globalization.CultureInfo.InvariantCulture));
            }

            if (threshold < 0f || threshold > 1f)
            {
                return Failure(
                    ApaMaskSampleFailure.ThresholdOutOfRange,
                    "The grayscale threshold " + threshold + " is outside 0 through 1.",
                    "reason=threshold-out-of-range; threshold=" + threshold.ToString("R",
                        System.Globalization.CultureInfo.InvariantCulture));
            }

            return null;
        }

        /// <summary>Reads the UV channel, or reports why it cannot be used.</summary>
        /// <remarks>
        /// Both "the channel is absent" and "the channel's entry count disagrees with the vertex count" are
        /// refused. The second is not pedantry: Unity fills a mismatched array partially and silently, so reading
        /// it would sample the mask at a UV of zero for every vertex the array does not cover and quietly select
        /// whatever the mask says at the UV origin.
        /// </remarks>
        private static Vector4[] ReadUvChannel(Mesh mesh, int uvChannel, out ApaMaskSampleResult failure)
        {
            failure = null;

            var vertexCount = mesh.vertexCount;
            if (vertexCount <= 0)
            {
                failure = Failure(
                    ApaMaskSampleFailure.MeshNotReadable,
                    "Mesh '" + mesh.name + "' has no vertices, so no triangle can be converted.",
                    "reason=mesh-has-no-vertices; mesh=" + mesh.name);
                return Array.Empty<Vector4>();
            }

            var uvs = new List<Vector4>(vertexCount);
            try
            {
                mesh.GetUVs(uvChannel, uvs);
            }
            catch (ArgumentException)
            {
                // A channel index outside the mesh's channel range throws on some Unity versions and returns an
                // empty list on others. Both mean "the channel is absent", which is what is reported.
                uvs.Clear();
            }

            if (uvs.Count == 0)
            {
                failure = Failure(
                    ApaMaskSampleFailure.UvChannelAbsent,
                    "Mesh '" + mesh.name + "' carries no UV channel " + uvChannel + ". Choose a channel the mesh " +
                    "actually has, or paint the mask against one it does.",
                    "reason=uv-channel-absent; mesh=" + mesh.name + "; channel=" + uvChannel);
                return Array.Empty<Vector4>();
            }

            if (uvs.Count != vertexCount)
            {
                failure = Failure(
                    ApaMaskSampleFailure.UvChannelAbsent,
                    "UV channel " + uvChannel + " of mesh '" + mesh.name + "' has " + uvs.Count + " entry(ies) " +
                    "for " + vertexCount + " vertex(es), so it cannot be indexed by vertex.",
                    "reason=uv-channel-length-mismatch; mesh=" + mesh.name + "; channel=" + uvChannel +
                    "; entries=" + uvs.Count + "; vertices=" + vertexCount);
                return Array.Empty<Vector4>();
            }

            return uvs.ToArray();
        }

        // ---- The sampling rule -----------------------------------------------------------------------------

        /// <summary>
        /// Evaluates one triangle's seven samples and reports how many passed.
        /// </summary>
        /// <remarks>
        /// The production path of the conversion: it is called once per triangle, so it allocates nothing. The
        /// seven positions are resolved one at a time from the three vertex UVs, and the comparison itself is the
        /// whole per-triangle cost.
        /// </remarks>
        /// <returns>
        /// False when a sample's UV is not finite, with the offending coordinate in <paramref name="badUv"/>;
        /// true otherwise, with the number of passing samples in <paramref name="passedSamples"/>.
        /// </returns>
        public static bool TryEvaluateTriangle(
            Vector4 uv0,
            Vector4 uv1,
            Vector4 uv2,
            Color32[] pixels,
            int width,
            int height,
            TextureWrapMode wrap,
            FilterMode filter,
            float threshold,
            bool invert,
            out int passedSamples,
            out Vector2 badUv)
        {
            passedSamples = 0;
            badUv = Vector2.zero;

            // The three vertex UVs are extracted once as values. Sample positions are then evaluated one at a
            // time: no array, and therefore no per-triangle heap allocation on a mesh with a hundred thousand
            // triangles, where an array per triangle would be pure cost.
            var first = new Vector2(uv0.x, uv0.y);
            var second = new Vector2(uv1.x, uv1.y);
            var third = new Vector2(uv2.x, uv2.y);

            for (var i = 0; i < SampleCount; i++)
            {
                ResolveSample(i, first, second, third, out var uv);
                if (!IsFinite(uv.x) || !IsFinite(uv.y))
                {
                    badUv = uv;
                    return false;
                }

                var luminance = SampleLuminance(uv, pixels, width, height, wrap, filter);
                var brightness = invert ? 1f - luminance : luminance;
                if (brightness >= threshold) passedSamples++;
            }

            return true;
        }

        /// <summary>
        /// The <paramref name="index"/>-th of the seven fixed sample UVs of a triangle.
        /// </summary>
        /// <remarks>
        /// The single definition of the rule's sample positions: <see cref="TryEvaluateTriangle"/> and
        /// <see cref="SamplePoints"/> both resolve their positions here, so a diagnostic view cannot show a
        /// different triangle than the one the conversion judged. Index 0–2 are the vertex UVs, 3–5 the edge
        /// midpoints, 6 the centroid. The order is part of the contract only in the sense that it is fixed; the
        /// majority vote is order-independent.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is outside 0 through <see cref="SampleCount"/> - 1.
        /// </exception>
        public static Vector2 SamplePoint(Vector4 uv0, Vector4 uv1, Vector4 uv2, int index)
        {
            ResolveSample(
                index,
                new Vector2(uv0.x, uv0.y),
                new Vector2(uv1.x, uv1.y),
                new Vector2(uv2.x, uv2.y),
                out var uv);
            return uv;
        }

        /// <summary>
        /// The seven sample UVs of a triangle, in the fixed order: three vertices, three edge midpoints, one
        /// centroid.
        /// </summary>
        /// <remarks>
        /// A convenience view for a reviewer or a diagnostic overlay, assembled from
        /// <see cref="SamplePoint"/> so there is only one definition of where the samples are. The conversion
        /// itself never calls this: it evaluates the same seven positions through
        /// <see cref="ResolveSample"/>, which allocates nothing, because this method allocates one array per call.
        /// </remarks>
        public static Vector2[] SamplePoints(Vector4 uv0, Vector4 uv1, Vector4 uv2)
        {
            var samples = new Vector2[SampleCount];
            for (var i = 0; i < SampleCount; i++) samples[i] = SamplePoint(uv0, uv1, uv2, i);
            return samples;
        }

        /// <summary>
        /// Writes one sample position from three already-extracted vertex UVs, without allocating.
        /// </summary>
        private static void ResolveSample(int index, Vector2 first, Vector2 second, Vector2 third, out Vector2 uv)
        {
            switch (index)
            {
                case 0:
                    uv = first;
                    return;
                case 1:
                    uv = second;
                    return;
                case 2:
                    uv = third;
                    return;
                case 3:
                    uv = (first + second) * 0.5f;
                    return;
                case 4:
                    uv = (second + third) * 0.5f;
                    return;
                case 5:
                    uv = (third + first) * 0.5f;
                    return;
                case 6:
                    uv = (first + second + third) / 3f;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        /// <summary>
        /// The RGB luminance of a sampled byte color, in 0 through 1. Alpha does not participate.
        /// </summary>
        public static float Luminance(Color32 color)
        {
            var value = LuminanceRedWeight * color.r
                        + LuminanceGreenWeight * color.g
                        + LuminanceBlueWeight * color.b;
            return value / 255f;
        }

        /// <summary>
        /// Luminance of one UV coordinate of a readable mask, with the texture's wrap and filter modes applied.
        /// </summary>
        /// <remarks>
        /// The texel position is derived from the unmodified UV, exactly as a GPU derives it, so the bilinear
        /// weights are the hardware's weights; the wrap mode is applied where the hardware applies it — to the
        /// integer texel indices — so a Repeat sample at the texture border interpolates the last and the first
        /// texel instead of reading the border texel twice, and a Mirror sample reflects its neighbours.
        /// </remarks>
        private static float SampleLuminance(
            Vector2 uv,
            Color32[] pixels,
            int width,
            int height,
            TextureWrapMode wrap,
            FilterMode filter)
        {
            if (filter == FilterMode.Point)
            {
                // Nearest sampling selects the texel whose center is closest: floor(uv * size), then the wrap
                // mode addresses that index. Addressing the index rather than the UV is what keeps Repeat from
                // clamping to the last texel.
                var nearestX = Math.Floor((double)uv.x * width);
                var nearestY = Math.Floor((double)uv.y * height);
                return Luminance(Texel(pixels, width, height, nearestX, nearestY, wrap));
            }

            // Texel centers sit at (i + 0.5) / size, exactly as a GPU samples them, so a UV on a texel center
            // reads that texel and a UV on an edge interpolates the two neighbours. The fractional part is the
            // interpolation weight and never changes under wrapping; the integer part is what the wrap mode
            // addresses.
            var positionX = ((double)uv.x * width) - 0.5d;
            var positionY = ((double)uv.y * height) - 0.5d;
            var baseX = Math.Floor(positionX);
            var baseY = Math.Floor(positionY);
            var fractionX = (float)(positionX - baseX);
            var fractionY = (float)(positionY - baseY);

            var c00 = Luminance(Texel(pixels, width, height, baseX, baseY, wrap));
            var c10 = Luminance(Texel(pixels, width, height, baseX + 1d, baseY, wrap));
            var c01 = Luminance(Texel(pixels, width, height, baseX, baseY + 1d, wrap));
            var c11 = Luminance(Texel(pixels, width, height, baseX + 1d, baseY + 1d, wrap));

            var top = c00 + ((c10 - c00) * fractionX);
            var bottom = c01 + ((c11 - c01) * fractionX);
            return top + ((bottom - top) * fractionY);
        }

        /// <summary>Reads a texel by texel coordinate, addressed through the texture's wrap mode.</summary>
        private static Color32 Texel(
            Color32[] pixels,
            int width,
            int height,
            double x,
            double y,
            TextureWrapMode wrap)
        {
            // Row-major, bottom row first: GetPixels32 is indexed as y * width + x. Both addresses are inside
            // 0..size-1 by construction, so the index below is always in range.
            return pixels[(Address(y, height, wrap) * width) + Address(x, width, wrap)];
        }

        /// <summary>
        /// Addresses one texel coordinate through a wrap mode: Repeat wraps modulo the size, Mirror reflects
        /// through a doubled period, and Clamp with <c>MirrorOnce</c> stops at the border texel.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The address is computed on the integer texel coordinate, which is where a GPU applies the wrap mode;
        /// the interpolation weight comes from the fractional part of the unmodified UV, so the wrap cannot shift
        /// the sample. Repeat therefore turns a coordinate of -1 into the last texel and a coordinate of
        /// <c>size</c> into the first, and Mirror turns <c>size</c> into <c>size - 1</c>.
        /// </para>
        /// <para>
        /// The arithmetic runs on <see cref="double"/>, so a UV far outside 0–1 lands on a valid texel instead of
        /// overflowing an <see cref="int"/>. A coordinate that is itself non-finite — only reachable from a UV
        /// beyond the range of a double, which the conversion does not treat as an error — is addressed as texel 0
        /// so the function stays total and deterministic.
        /// </para>
        /// </remarks>
        private static int Address(double index, int size, TextureWrapMode wrap)
        {
            switch (wrap)
            {
                case TextureWrapMode.Repeat:
                    return (int)PositiveModulo(index, size);
                case TextureWrapMode.Mirror:
                    return (int)MirrorIndex(index, size);
                default:
                    // Clamp, and MirrorOnce treated as clamp: a mask is a tile, and a single reflection has no
                    // useful meaning for a selection region.
                    if (double.IsNaN(index) || index < 0d) return 0;
                    return index >= size ? size - 1 : (int)index;
            }
        }

        /// <summary>The non-negative remainder of a texel coordinate modulo a size.</summary>
        private static double PositiveModulo(double value, int size)
        {
            if (!IsAddressable(value)) return 0d;

            var remainder = value - (Math.Floor(value / size) * size);
            if (!IsAddressable(remainder)) return 0d;
            if (remainder < 0d) return remainder + size;
            return remainder >= size ? remainder - size : remainder;
        }

        /// <summary>A texel coordinate reflected into 0..size-1 through a doubled period.</summary>
        private static double MirrorIndex(double value, int size)
        {
            if (!IsAddressable(value)) return 0d;

            var period = size * 2d;
            var position = value - (Math.Floor(value / period) * period);
            if (!IsAddressable(position)) return 0d;
            if (position < 0d) position += period;
            else if (position >= period) position -= period;

            return position < size ? position : (period - 1d) - position;
        }

        /// <summary>True when a texel coordinate is a finite number the wrap arithmetic can reduce.</summary>
        private static bool IsAddressable(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // ---- The texture readback boundary ------------------------------------------------------------------

        /// <summary>
        /// Reads a mask's pixels into CPU memory without requiring Read/Write Enabled and without touching its
        /// import settings.
        /// </summary>
        /// <remarks>
        /// <para>
        /// There is exactly one path, and it is the rendering one: the mask is blitted into a temporary
        /// <c>ARGB32</c> render texture, which asks the device to draw the texture it holds — decoding an
        /// imported, non-readable, block-compressed, crunched, or platform-compressed source — and the result is
        /// read back with <see cref="Texture2D.ReadPixels"/>. The format is deliberately not inspected: the
        /// device decides what it can draw, and the conversion only reports whether the read succeeded.
        /// </para>
        /// <para>
        /// There is deliberately no <c>Graphics.CopyTexture</c> shortcut. A device-side copy into a temporary
        /// <see cref="Texture2D"/> updates the GPU's copy of the texture but not the CPU-side pixel buffer that
        /// <c>GetPixels32</c> returns, so that route can hand back a correctly sized buffer that is entirely
        /// blank — a silently all-black mask. One render-and-read path cannot disagree with itself about which
        /// pixels it returned.
        /// </para>
        /// <para>
        /// Every temporary is released on every path, including the failing ones, and nothing is released that
        /// was never acquired.
        /// </para>
        /// </remarks>
        private static bool TryReadMask(
            Texture2D mask,
            out Color32[] pixels,
            out int width,
            out int height,
            out TextureWrapMode wrap,
            out FilterMode filter,
            out ApaMaskSampleResult failure)
        {
            pixels = null;
            width = mask != null ? mask.width : 0;
            height = mask != null ? mask.height : 0;
            wrap = mask != null ? mask.wrapMode : TextureWrapMode.Repeat;
            filter = mask != null ? mask.filterMode : FilterMode.Bilinear;
            failure = null;

            if (mask == null || width <= 0 || height <= 0) return false;

            if (TryRenderPixels(mask, out pixels)) return true;

            failure = Failure(
                ApaMaskSampleFailure.TextureReadbackFailed,
                "Mask '" + mask.name + "' could not be rendered and read back from the graphics device. Reimport " +
                "the texture, or use a different format or a smaller size.",
                "reason=texture-readback-failed; texture=" + mask.name + "; width=" + width + "; height=" +
                height + "; format=" + mask.format);
            return false;
        }

        /// <summary>The rendering readback: blit into a temporary render texture, then ReadPixels from it.</summary>
        /// <remarks>
        /// Allocation happens inside the <c>try</c>, the render texture may come back null, and
        /// <see cref="RenderTexture.active"/> is restored on every path — a refused allocation is a readback
        /// failure like any other, not an exception escaping into the window's repaint.
        /// </remarks>
        private static bool TryRenderPixels(Texture2D mask, out Color32[] pixels)
        {
            pixels = null;
            var previous = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D readback = null;

            try
            {
                temporary = RenderTexture.GetTemporary(
                    mask.width, mask.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                if (temporary == null) return false;

                Graphics.Blit(mask, temporary);

                RenderTexture.active = temporary;
                readback = new Texture2D(mask.width, mask.height, TextureFormat.RGBA32, false, false);
                if (readback == null) return false;

                // ReadPixels fills the temporary texture's CPU-side pixel buffer directly; GetPixels32 below reads
                // exactly what this call copied. Apply is not needed for that read and is not called, so nothing
                // can overwrite the readback between the copy and the read.
                readback.ReadPixels(new Rect(0f, 0f, mask.width, mask.height), 0, 0, false);

                var read = readback.GetPixels32();
                if (read == null || read.Length != mask.width * mask.height) return false;

                pixels = read;
                return true;
            }
            catch (Exception)
            {
                // Any failure is one readback failure, reported by the caller as one actionable APA041. The
                // device's exception text is swallowed on purpose: it is not a stable token, and the picture, the
                // size, and the format in the diagnostic are what the author can act on.
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            }
        }

        // ---- Failure construction ---------------------------------------------------------------------------

        /// <summary>
        /// Builds a failed result carrying one stable <c>APA041</c> error.
        /// </summary>
        /// <remarks>
        /// Message bodies stay English in both languages, like every other diagnostic in this package: the code
        /// and the <c>reason=</c> token are the contract, and a Chinese reader gets a translated description of
        /// the code beside the unchanged token. What the window localizes is its own labels and its summary of
        /// the outcome.
        /// </remarks>
        private static ApaMaskSampleResult Failure(
            ApaMaskSampleFailure failure,
            string message,
            string detail)
        {
            return ApaMaskSampleResult.Failed(
                failure,
                ValidationIssue.Error(
                    ApaErrorCode.RemovalMaskTextureFailed,
                    ApaIssuePhase.Removal,
                    message,
                    detail: detail));
        }

        private static bool IsVertexIndex(int index, int vertexCount)
        {
            return index >= 0 && index < vertexCount;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string Describe(Vector2 uv)
        {
            return "(" + uv.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                   uv.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }
}

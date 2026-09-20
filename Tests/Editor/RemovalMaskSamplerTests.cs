using System;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Authoring;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Authoring
{
    /// <summary>
    /// Tests for the mask-to-triangle conversion: the fixed sampling rule, and the refusals that keep it from
    /// guessing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is a pure function of a triangle's UVs, the mask's pixels, the texture's wrap and filter modes,
    /// and the threshold, so it is asserted directly against hand-built pixel arrays. That is the same code the
    /// conversion runs — <see cref="ApaRemovalMaskSampler.TryEvaluateTriangle"/> is the production per-triangle
    /// path, not a test double — and it needs no graphics device, which is what lets every assertion here hold in
    /// a batch-mode runner.
    /// </para>
    /// <para>
    /// Only the readback boundary itself is device dependent. Those assertions live at the end and are skipped
    /// with a stated reason when no device can render and read a texture back, because a failure there would say
    /// something about the runner and nothing about the rule.
    /// </para>
    /// </remarks>
    public sealed class RemovalMaskSamplerTests
    {
        // ---- The fixed rule --------------------------------------------------------------------------------

        [Test]
        public void Rule_IsSevenSamplesWithAStrictMajorityOfFour()
        {
            Assert.AreEqual(7, ApaRemovalMaskSampler.SampleCount);
            Assert.AreEqual(4, ApaRemovalMaskSampler.MajoritySampleCount);

            // Seven is odd, which is why no tie-breaking rule exists and none is needed.
            Assert.AreEqual(1, ApaRemovalMaskSampler.SampleCount % 2);
            Assert.AreEqual(
                ApaRemovalMaskSampler.SampleCount / 2 + 1,
                ApaRemovalMaskSampler.MajoritySampleCount);
        }

        [Test]
        public void Luminance_UsesRec601WeightsAndIgnoresAlpha()
        {
            Assert.AreEqual(1f, ApaRemovalMaskSampler.Luminance(new Color32(255, 255, 255, 0)), 1e-5f);
            Assert.AreEqual(0f, ApaRemovalMaskSampler.Luminance(new Color32(0, 0, 0, 255)), 1e-5f);

            Assert.AreEqual(
                ApaRemovalMaskSampler.LuminanceRedWeight,
                ApaRemovalMaskSampler.Luminance(new Color32(255, 0, 0, 255)),
                1e-6f);
            Assert.AreEqual(
                ApaRemovalMaskSampler.LuminanceGreenWeight,
                ApaRemovalMaskSampler.Luminance(new Color32(0, 255, 0, 255)),
                1e-6f);
            Assert.AreEqual(
                ApaRemovalMaskSampler.LuminanceBlueWeight,
                ApaRemovalMaskSampler.Luminance(new Color32(0, 0, 255, 255)),
                1e-6f);

            // Alpha does not participate: a fully transparent white pixel and an opaque white pixel are the same
            // brightness, so letting alpha in would make a transparent white agree with an opaque black.
            Assert.AreEqual(
                ApaRemovalMaskSampler.Luminance(new Color32(200, 40, 90, 255)),
                ApaRemovalMaskSampler.Luminance(new Color32(200, 40, 90, 0)),
                1e-6f);
        }

        [Test]
        public void SamplePoints_AreThreeVerticesThreeMidpointsAndTheCentroid()
        {
            var points = ApaRemovalMaskSampler.SamplePoints(Uv(0f, 0f), Uv(1f, 0f), Uv(0f, 1f));

            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, points.Length);
            AssertVector(points[0], 0f, 0f, "vertex 0");
            AssertVector(points[1], 1f, 0f, "vertex 1");
            AssertVector(points[2], 0f, 1f, "vertex 2");
            AssertVector(points[3], 0.5f, 0f, "midpoint of edge 0-1");
            AssertVector(points[4], 0.5f, 0.5f, "midpoint of edge 1-2");
            AssertVector(points[5], 0f, 0.5f, "midpoint of edge 2-0");
            AssertVector(points[6], 1f / 3f, 1f / 3f, "centroid");

            // One definition of the sample positions: the named accessor must agree with the array.
            for (var i = 0; i < points.Length; i++)
            {
                Assert.AreEqual(
                    points[i],
                    ApaRemovalMaskSampler.SamplePoint(Uv(0f, 0f), Uv(1f, 0f), Uv(0f, 1f), i));
            }
        }

        [Test]
        public void SamplePoint_RefusesAnIndexOutsideTheFixedSet()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ApaRemovalMaskSampler.SamplePoint(Uv(0f, 0f), Uv(1f, 0f), Uv(0f, 1f), -1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ApaRemovalMaskSampler.SamplePoint(
                    Uv(0f, 0f), Uv(1f, 0f), Uv(0f, 1f), ApaRemovalMaskSampler.SampleCount));
        }

        // ---- The vote --------------------------------------------------------------------------------------

        [Test]
        public void AllWhiteMask_PassesEverySample()
        {
            var pixels = Mask(2, 2, 0, 1, 2, 3);
            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, Passing(pixels, 0.5f, false));
        }

        [Test]
        public void AllBlackMask_PassesNoSample()
        {
            var pixels = Mask(2, 2);
            Assert.AreEqual(0, Passing(pixels, 0.5f, false));
        }

        [Test]
        public void Invert_ReversesTheMeaningOfWhiteAndBlack()
        {
            var black = Mask(2, 2);
            var white = Mask(2, 2, 0, 1, 2, 3);

            Assert.AreEqual(0, Passing(black, 0.5f, false));
            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, Passing(black, 0.5f, true));

            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, Passing(white, 0.5f, false));
            Assert.AreEqual(0, Passing(white, 0.5f, true));
        }

        [Test]
        public void Threshold_IsInclusiveAtTheBoundary()
        {
            // Luminance of a mid gray is exactly value/255, because the Rec.601 weights sum to one. The mask is
            // 2x2 so it matches the geometry every other vote test uses.
            var gray = new Color32[4];
            for (var i = 0; i < gray.Length; i++) gray[i] = new Color32(128, 128, 128, 255);
            var luminance = 128f / 255f;

            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, Passing(gray, luminance, false),
                "A sample whose brightness equals the threshold passes: the rule is 'at or above'.");
            Assert.AreEqual(0, Passing(gray, luminance + 1e-3f, false));
            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, Passing(gray, luminance - 1e-3f, false));
        }

        /// <summary>
        /// The majority is what actually decides: three passing samples do not select the triangle and four do.
        /// </summary>
        /// <remarks>
        /// The triangle straddles a 2x2 mask so its seven samples land on four different texels — two on
        /// texel (0,0), two on (1,0), two on (0,1), one on (1,1) — which is what makes 3 and 4 reachable
        /// without moving the geometry.
        /// </remarks>
        [Test]
        public void Majority_FourOfSevenSelectsAndThreeDoesNot()
        {
            // White texel (0,0) carries samples 0 and 6; white texel (1,1) carries sample 4. Three in total.
            var three = Mask(2, 2, 0, 3);
            Assert.AreEqual(3, Passing(three, 0.5f, false));

            // White texels (0,0) and (1,0) carry samples 0, 6, 1 and 3. Four in total.
            var four = Mask(2, 2, 0, 1);
            Assert.AreEqual(4, Passing(four, 0.5f, false));

            Assert.AreEqual(ApaRemovalMaskSampler.MajoritySampleCount, Passing(four, 0.5f, false));
            Assert.Less(Passing(three, 0.5f, false), ApaRemovalMaskSampler.MajoritySampleCount);
        }

        // ---- Addressing ------------------------------------------------------------------------------------

        /// <summary>
        /// The wrap mode is applied to the integer texel index, which is where a GPU applies it.
        /// </summary>
        /// <remarks>
        /// A 2x1 mask whose second texel is white, sampled at a negative U. Repeat wraps -1 onto the last texel
        /// and reads white; Clamp stops at the border and reads black; Mirror reflects -1 back onto texel 0 and
        /// also reads black. At a U further out, Mirror reflects onto the last texel while Repeat and Clamp both
        /// land on texel 0, which is what separates Mirror from the other two.
        /// </remarks>
        [Test]
        public void WrapMode_AddressesTheTexelIndexNotTheUv()
        {
            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, PassingAt(-0.25f, TextureWrapMode.Repeat));
            Assert.AreEqual(0, PassingAt(-0.25f, TextureWrapMode.Clamp));
            Assert.AreEqual(0, PassingAt(-0.25f, TextureWrapMode.Mirror));

            Assert.AreEqual(0, PassingAt(-0.75f, TextureWrapMode.Repeat));
            Assert.AreEqual(0, PassingAt(-0.75f, TextureWrapMode.Clamp));
            Assert.AreEqual(ApaRemovalMaskSampler.SampleCount, PassingAt(-0.75f, TextureWrapMode.Mirror));
        }

        /// <summary>A point-filtered mask reads the nearest texel; a filtered one interpolates.</summary>
        [Test]
        public void FilterMode_InterpolatesOnlyWhenTheTextureIsFiltered()
        {
            // The sample sits 30% of the way from the black texel to the white one, so a bilinear read returns
            // 0.3 and a nearest read returns 0.
            Assert.AreEqual(0, PassingAt(0.4f, TextureWrapMode.Clamp, FilterMode.Point));
            Assert.AreEqual(
                ApaRemovalMaskSampler.SampleCount,
                PassingAt(0.4f, TextureWrapMode.Clamp, FilterMode.Bilinear, threshold: 0.2f));
            Assert.AreEqual(
                0,
                PassingAt(0.4f, TextureWrapMode.Clamp, FilterMode.Point, threshold: 0.2f));
        }

        // ---- Refusals --------------------------------------------------------------------------------------

        /// <summary>
        /// A non-finite UV is refused rather than sampled.
        /// </summary>
        /// <remarks>
        /// A UV of NaN would otherwise be addressed as texel 0 and silently vote on the strength of a coordinate
        /// that does not exist, which is exactly the kind of guess this conversion refuses to make.
        /// </remarks>
        [Test]
        public void NonFiniteUv_IsRefusedRatherThanSampled()
        {
            var pixels = Mask(1, 1, 0);

            Assert.IsFalse(ApaRemovalMaskSampler.TryEvaluateTriangle(
                new Vector4(float.NaN, 0.5f, 0f, 0f), Uv(0.5f, 0.5f), Uv(0.5f, 0.5f),
                pixels, 1, 1, TextureWrapMode.Clamp, FilterMode.Point, 0.5f, false,
                out var nanPassed, out var nanUv));
            Assert.AreEqual(0, nanPassed);
            Assert.IsTrue(float.IsNaN(nanUv.x));

            // A midpoint is a sample too: an infinite vertex makes the edge midpoint non-finite even though both
            // endpoints the author picked are inside the triangle.
            Assert.IsFalse(ApaRemovalMaskSampler.TryEvaluateTriangle(
                Uv(0.5f, 0.5f), new Vector4(float.PositiveInfinity, 0.5f, 0f, 0f), Uv(0.5f, 0.5f),
                pixels, 1, 1, TextureWrapMode.Clamp, FilterMode.Point, 0.5f, false,
                out var infPassed, out var infUv));
            Assert.AreEqual(0, infPassed);
            Assert.IsTrue(float.IsInfinity(infUv.x));
        }

        [Test]
        public void MissingMesh_IsRefusedWithAStableToken()
        {
            var result = ApaRemovalMaskSampler.TryGenerate(null, null, 0, 0.5f, false);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(ApaMaskSampleFailure.MissingMesh, result.Failure);
            Assert.AreEqual(ApaErrorCode.RemovalMaskTextureFailed, result.Issue.Code);
            StringAssert.Contains("reason=missing-target-mesh", result.Issue.Detail);
        }

        [Test]
        public void MissingTexture_IsRefusedWithAStableToken()
        {
            var mesh = new Mesh();
            try
            {
                if (!mesh.isReadable)
                {
                    Assert.Ignore("A mesh built in code is not readable on this Unity version.");
                }

                var result = ApaRemovalMaskSampler.TryGenerate(mesh, null, 0, 0.5f, false);

                Assert.IsFalse(result.Succeeded);
                Assert.AreEqual(ApaMaskSampleFailure.MissingTexture, result.Failure);
                StringAssert.Contains("reason=missing-mask-texture", result.Issue.Detail);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        // ---- The readback boundary -------------------------------------------------------------------------
        //
        // Everything below reaches the texture readback, so it needs a graphics device. The rule itself is
        // already covered above without one; what these add is the plumbing around it and the refusals that
        // happen after the mask has been read.

        [Test]
        public void WholeMask_SelectsTheWholeTriangleList()
        {
            var mesh = TriangleMesh(new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f),
                new Vector2(0.25f, 0.75f));
            var texture = WhiteTexture();
            RequireReadback(mesh);
            try
            {
                var result = ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, 0.5f, false);

                Assert.IsTrue(result.Succeeded, result.Issue != null ? result.Issue.Detail : "failed");
                Assert.AreEqual(1, result.ConsideredTriangleCount);
                Assert.AreEqual(1, result.Addresses.Count);
                Assert.AreEqual(new RemovedTriangleAddress(0, 0), result.Addresses[0]);

                var empty = ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, 0.5f, true);
                Assert.IsTrue(empty.Succeeded, "Inverting a white mask selects nothing.");
                Assert.AreEqual(0, empty.Addresses.Count);
            }
            finally
            {
                Destroy(mesh, texture);
            }
        }

        [Test]
        public void RefusalsAfterTheReadback_CarryTheirOwnTokens()
        {
            var mesh = TriangleMesh(new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f),
                new Vector2(0.25f, 0.75f));
            var texture = WhiteTexture();
            RequireReadback(mesh);
            try
            {
                Assert.AreEqual(
                    ApaMaskSampleFailure.InvalidUvChannel,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, ApaMeshLimits.MaxUvChannels, 0.5f, false)
                        .Failure);
                Assert.AreEqual(
                    ApaMaskSampleFailure.InvalidUvChannel,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, -1, 0.5f, false).Failure);

                // A channel the mesh does not carry is not silently read as zero: Unity fills a mismatched array
                // partially, and sampling it would vote on the mask's UV origin for every uncovered vertex.
                Assert.AreEqual(
                    ApaMaskSampleFailure.UvChannelAbsent,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, 1, 0.5f, false).Failure);

                Assert.AreEqual(
                    ApaMaskSampleFailure.NonFiniteThreshold,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, float.NaN, false).Failure);
                Assert.AreEqual(
                    ApaMaskSampleFailure.NonFiniteThreshold,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, float.PositiveInfinity, false).Failure);
                Assert.AreEqual(
                    ApaMaskSampleFailure.ThresholdOutOfRange,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, -0.01f, false).Failure);
                Assert.AreEqual(
                    ApaMaskSampleFailure.ThresholdOutOfRange,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, 1.01f, false).Failure);

                // The conversion refuses rather than clamps, so a threshold the caller let out of range never
                // silently becomes a different one.
                Assert.AreEqual(
                    ApaMaskSampleFailure.ThresholdOutOfRange,
                    ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, 4f, false).Failure);
            }
            finally
            {
                Destroy(mesh, texture);
            }
        }

        [Test]
        public void NonTriangleTopology_IsRefusedRatherThanReinterpreted()
        {
            var mesh = new Mesh();
            var texture = WhiteTexture();
            try
            {
                mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f) };
                mesh.uv = new[] { new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f) };
                mesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
                RequireReadback(mesh);

                var result = ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, 0.5f, false);

                Assert.IsFalse(result.Succeeded);
                Assert.AreEqual(ApaMaskSampleFailure.UnsupportedTopology, result.Failure);
                StringAssert.Contains("reason=unsupported-topology", result.Issue.Detail);
            }
            finally
            {
                Destroy(mesh, texture);
            }
        }

        [Test]
        public void NonFiniteUvInTheMesh_IsRefusedWithTheOffendingCoordinate()
        {
            var mesh = TriangleMesh(new Vector2(float.NaN, 0.25f), new Vector2(0.75f, 0.25f),
                new Vector2(0.25f, 0.75f));
            var texture = WhiteTexture();
            RequireReadback(mesh);
            try
            {
                var result = ApaRemovalMaskSampler.TryGenerate(mesh, texture, 0, 0.5f, false);

                Assert.IsFalse(result.Succeeded);
                Assert.AreEqual(ApaMaskSampleFailure.NonFiniteUv, result.Failure);
                StringAssert.Contains("reason=non-finite-uv", result.Issue.Detail);
            }
            finally
            {
                Destroy(mesh, texture);
            }
        }

        // ---- Helpers ---------------------------------------------------------------------------------------

        /// <summary>A black mask of the given size with the given texel indices painted white.</summary>
        private static Color32[] Mask(int width, int height, params int[] whiteTexels)
        {
            var pixels = new Color32[width * height];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 255);

            for (var i = 0; i < whiteTexels.Length; i++)
            {
                pixels[whiteTexels[i]] = new Color32(255, 255, 255, 255);
            }

            return pixels;
        }

        /// <summary>
        /// The number of samples that pass for one triangle straddling a 2x2 mask, with nearest sampling.
        /// </summary>
        /// <remarks>
        /// The three vertices are placed so the seven samples land on four texels: (0,0) twice, (1,0) twice,
        /// (0,1) twice and (1,1) once. Painting texels is therefore how a test chooses how many samples pass.
        /// </remarks>
        private static int Passing(Color32[] pixels, float threshold, bool invert)
        {
            return Passing(
                Uv(0.1f, 0.1f), Uv(0.9f, 0.1f), Uv(0.1f, 0.9f),
                pixels, 2, 2, TextureWrapMode.Clamp, FilterMode.Point, threshold, invert);
        }

        private static int Passing(
            Vector4 uv0,
            Vector4 uv1,
            Vector4 uv2,
            Color32[] pixels,
            int width,
            int height,
            TextureWrapMode wrap,
            FilterMode filter,
            float threshold,
            bool invert)
        {
            Assert.IsTrue(ApaRemovalMaskSampler.TryEvaluateTriangle(
                uv0, uv1, uv2, pixels, width, height, wrap, filter, threshold, invert,
                out var passed, out _));

            return passed;
        }

        /// <summary>
        /// The number of passing samples when all seven sample positions coincide, which isolates the texel
        /// addressing from the geometry.
        /// </summary>
        private static int PassingAt(
            float u,
            TextureWrapMode wrap,
            FilterMode filter = FilterMode.Point,
            float threshold = 0.5f)
        {
            // A 2x1 mask: texel 0 is black, texel 1 is white.
            var pixels = Mask(2, 1, 1);
            var uv = new Vector4(u, 0.5f, 0f, 0f);

            return Passing(uv, uv, uv, pixels, 2, 1, wrap, filter, threshold, false);
        }

        private static Vector4 Uv(float x, float y)
        {
            return new Vector4(x, y, 0f, 0f);
        }

        private static void AssertVector(Vector2 actual, float x, float y, string what)
        {
            Assert.AreEqual(x, actual.x, 1e-5f, what);
            Assert.AreEqual(y, actual.y, 1e-5f, what);
        }

        private static Mesh TriangleMesh(params Vector2[] uvs)
        {
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) },
                uv = uvs,
                triangles = new[] { 0, 1, 2 }
            };

            mesh.RecalculateBounds();
            return mesh;
        }

        private static Texture2D WhiteTexture()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            for (var y = 0; y < texture.height; y++)
            {
                for (var x = 0; x < texture.width; x++) texture.SetPixel(x, y, Color.white);
            }

            texture.Apply();
            return texture;
        }

        private static void Destroy(Mesh mesh, Texture2D texture)
        {
            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// Skips the calling test when the readback path cannot run here: either no graphics device can render
        /// and read a texture back, or a mesh built in code is not readable on this Unity version.
        /// </summary>
        private static void RequireReadback(Mesh mesh)
        {
            if (!mesh.isReadable)
            {
                Assert.Ignore(
                    "A mesh built in code is not readable on this Unity version, so the mesh-dependent " +
                    "refusals cannot be reached.");
            }

            var probe = WhiteTexture();
            try
            {
                if (ApaRemovalMaskSampler.TrySampleLuminance(
                        probe, new Vector2(0.5f, 0.5f), out _, out var issue))
                {
                    return;
                }

                Assert.Ignore(
                    "No graphics device can render and read a texture back on this runner, so the readback " +
                    "boundary cannot be exercised here. The sampling rule is covered by the tests above, " +
                    "which need no device. Readback failure: " + (issue != null ? issue.Detail : "unknown"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }
    }
}

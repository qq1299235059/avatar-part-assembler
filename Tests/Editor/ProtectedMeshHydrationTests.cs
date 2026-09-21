using System;
using System.Collections.Generic;
using AvatarPartAssembler.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Tests.Protected
{
    /// <summary>
    /// Tests for the transient in-memory reconstruction of a protected part mesh and its lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lease is the whole safety story of the protected build path: the decoded mesh exists only for as long
    /// as the build needs it, the renderer is restored afterwards, and the mesh is destroyed on every path —
    /// including the failure paths. These tests drive each of those directly, without an NDMF build, because the
    /// lease is what a build relies on and it must be correct on its own.
    /// </para>
    /// <para>
    /// The "never a project asset" assertions are part of the feature rather than hygiene: a transient decrypted
    /// mesh that became a project asset would defeat the protection entirely.
    /// </para>
    /// </remarks>
    public sealed class ProtectedMeshHydrationTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            ApaProtectedMeshLease.ReleaseAll();

            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        // ---- Reconstruction ----------------------------------------------------------------------------

        /// <summary>
        /// The transient mesh carries every attribute the payload held, because Modular Avatar and the assembly
        /// capture both read it as if it were the part's own mesh.
        /// </summary>
        [Test]
        public void TryBuildTransientMesh_CarriesEveryAttribute()
        {
            var snapshot = FullSnapshot();

            var mesh = ApaProtectedMeshHydration.TryBuildTransientMesh(snapshot, out var issue);
            Assert.IsNotNull(mesh, issue != null ? issue.Message : "the transient mesh was not built");
            _created.Add(mesh);

            Assert.AreEqual(HideFlags.HideAndDontSave, mesh.hideFlags);
            Assert.AreEqual(snapshot.VertexCount, mesh.vertexCount);
            Assert.AreEqual(IndexFormat.UInt32, mesh.indexFormat);
            Assert.AreEqual(snapshot.SubMeshCount, mesh.subMeshCount);
            Assert.AreEqual(MeshTopology.Triangles, mesh.GetTopology(0));
            Assert.AreEqual(MeshTopology.Lines, mesh.GetTopology(1));
            Assert.AreEqual(snapshot.Bounds, mesh.bounds);

            Assert.AreEqual(snapshot.Vertices.Count, mesh.vertices.Length);
            Assert.AreEqual(snapshot.Normals.Count, mesh.normals.Length);
            Assert.AreEqual(snapshot.Tangents.Count, mesh.tangents.Length);
            Assert.AreEqual(snapshot.Colors.Count, mesh.colors.Length);

            for (var channel = 0; channel < snapshot.UvChannelCapacity; channel++)
            {
                var expected = new List<Vector4>();
                mesh.GetUVs(channel, expected);
                Assert.AreEqual(
                    snapshot.HasUvChannel(channel),
                    expected.Count > 0,
                    "UV channel " + channel + " presence must match the payload.");
            }

            Assert.AreEqual(snapshot.SkinWeights.Count, mesh.boneWeights.Length);
            Assert.AreEqual(snapshot.SkinBindPoses.Count, mesh.bindposes.Length);
            Assert.AreEqual(snapshot.BlendShapeCount, mesh.blendShapeCount);
            Assert.AreEqual(snapshot.Shapes[0], mesh.GetBlendShapeName(0));
            Assert.AreEqual(snapshot.ShapeFrameCounts[0], mesh.GetBlendShapeFrameCount(0));
        }

        [Test]
        public void TryBuildTransientMesh_RefusesANullSnapshotWithoutAllocating()
        {
            Assert.IsNull(ApaProtectedMeshHydration.TryBuildTransientMesh(null, out var issue));
            Assert.IsNotNull(issue);
            StringAssert.Contains(ApaProtectedMeshReasons.EmptyPayload, issue.Detail);
        }

        /// <summary>
        /// A transient mesh must not be a project asset: the whole point of the payload is that no decrypted mesh
        /// is written to disk.
        /// </summary>
        [Test]
        public void TransientMesh_IsNeverAProjectAsset()
        {
            var mesh = ApaProtectedMeshHydration.TryBuildTransientMesh(FullSnapshot(), out _);
            Assert.IsNotNull(mesh);
            _created.Add(mesh);

            Assert.IsFalse(EditorUtility.IsPersistent(mesh), "The reconstructed mesh must not be a project asset.");
            Assert.IsEmpty(AssetDatabase.GetAssetPath(mesh), "The reconstructed mesh must have no asset path.");
            Assert.AreEqual(HideFlags.HideAndDontSave, mesh.hideFlags);
        }

        // ---- The lease ---------------------------------------------------------------------------------

        [Test]
        public void TryHydrate_AttachesTheTransientMeshAndDisposeRestoresThePreviousOne()
        {
            var renderer = NewMeshRenderer(out var filter);
            var previous = NewMesh("Previous");
            filter.sharedMesh = previous;

            var snapshot = MinimalSnapshot();
            Assert.IsTrue(
                ApaProtectedMeshHydration.TryHydrate(renderer, snapshot, out var lease, out var issue),
                issue != null ? issue.Message : "hydration failed");

            Assert.IsNotNull(lease);
            Assert.AreEqual(1, ApaProtectedMeshLease.LiveCount);
            Assert.IsNotNull(filter.sharedMesh);
            Assert.AreNotEqual(previous, filter.sharedMesh);
            Assert.AreEqual(snapshot.VertexCount, filter.sharedMesh.vertexCount);

            var transient = lease.Mesh;

            lease.Dispose();

            Assert.IsTrue(lease.IsDisposed);
            Assert.AreEqual(0, ApaProtectedMeshLease.LiveCount);
            Assert.AreEqual(previous, filter.sharedMesh, "The renderer must be restored to its previous mesh.");
            Assert.IsTrue(transient == null, "The transient mesh must be destroyed by the release.");
        }

        [Test]
        public void TryHydrate_LeavesNothingBehindWhenItFails()
        {
            var renderer = NewMeshRenderer(out var filter);
            var previous = NewMesh("Previous");
            filter.sharedMesh = previous;

            Assert.IsFalse(ApaProtectedMeshHydration.TryHydrate(renderer, null, out var lease, out var issue));
            Assert.IsNull(lease);
            Assert.IsNotNull(issue);
            Assert.AreEqual(0, ApaProtectedMeshLease.LiveCount);
            Assert.AreEqual(previous, filter.sharedMesh, "A failed hydration must not touch the renderer.");
        }

        [Test]
        public void TryHydrate_RefusesARendererThatCannotCarryAMesh()
        {
            var go = new GameObject("Line");
            _created.Add(go);

            // A LineRenderer is a Renderer with no SkinnedMeshRenderer and no MeshFilter, which is exactly the
            // shape the hydration step cannot attach a mesh to.
            var lineRenderer = go.AddComponent<LineRenderer>();

            Assert.IsFalse(
                ApaProtectedMeshHydration.TryHydrate(lineRenderer, MinimalSnapshot(), out var lease, out var issue));
            Assert.IsNull(lease);
            Assert.IsNotNull(issue);
            Assert.AreEqual(0, ApaProtectedMeshLease.LiveCount);
        }

        [Test]
        public void TryHydrate_RefusesANullRenderer()
        {
            Assert.IsFalse(ApaProtectedMeshHydration.TryHydrate(null, MinimalSnapshot(), out _, out var issue));
            Assert.IsNotNull(issue);
            StringAssert.Contains("missing-part-renderer", issue.Detail);
        }

        [Test]
        public void Lease_DisposeIsIdempotentAndReleaseAllSweepsWhatIsLeft()
        {
            var renderer = NewMeshRenderer(out var filter);

            Assert.IsTrue(ApaProtectedMeshHydration.TryHydrate(renderer, MinimalSnapshot(), out var first, out _));
            Assert.IsTrue(ApaProtectedMeshHydration.TryHydrate(renderer, MinimalSnapshot(), out var second, out _));
            Assert.AreEqual(2, ApaProtectedMeshLease.LiveCount);

            var firstMesh = first.Mesh;
            first.Dispose();
            first.Dispose();
            Assert.AreEqual(1, ApaProtectedMeshLease.LiveCount, "A second Dispose must not double-release.");

            ApaProtectedMeshLease.ReleaseAll();

            Assert.AreEqual(0, ApaProtectedMeshLease.LiveCount);
            Assert.IsTrue(firstMesh == null);
            Assert.IsTrue(second == null || second.IsDisposed);
            Assert.IsTrue(filter.sharedMesh == null, "The renderer must end up with the mesh it started with.");
        }

        /// <summary>
        /// A lease whose renderer was destroyed by the build must still release its mesh rather than throwing:
        /// the assembly pass destroys the part renderer before the lease is released.
        /// </summary>
        [Test]
        public void Lease_SurvivesARendererDestroyedBeforeTheRelease()
        {
            var renderer = NewMeshRenderer(out _);
            var owner = renderer.gameObject;

            Assert.IsTrue(ApaProtectedMeshHydration.TryHydrate(renderer, MinimalSnapshot(), out var lease, out _));
            var transient = lease.Mesh;

            UnityEngine.Object.DestroyImmediate(owner);
            _created.Remove(owner);

            Assert.DoesNotThrow(() => lease.Dispose());
            Assert.IsTrue(transient == null, "The transient mesh must still be destroyed.");
            Assert.AreEqual(0, ApaProtectedMeshLease.LiveCount);
        }

        // ---- Fixtures ----------------------------------------------------------------------------------

        private Renderer NewMeshRenderer(out MeshFilter filter)
        {
            var go = new GameObject("Part");
            _created.Add(go);
            filter = go.AddComponent<MeshFilter>();
            return go.AddComponent<MeshRenderer>();
        }

        private Mesh NewMesh(string name)
        {
            var mesh = new Mesh { name = name };
            _created.Add(mesh);
            return mesh;
        }

        private static MeshSnapshot MinimalSnapshot()
        {
            return MeshSnapshot.Create(
                "Minimal",
                new[] { Vector3.zero, Vector3.right, Vector3.up },
                Array.Empty<Vector3>(),
                Array.Empty<Vector4>(),
                Array.Empty<Color>(),
                MeshSnapshot.NewUvArray(),
                new[] { new[] { 0, 1, 2 } },
                new[] { MeshTopology.Triangles },
                new Bounds(Vector3.zero, Vector3.one),
                IndexFormat.UInt16);
        }

        private static MeshSnapshot FullSnapshot()
        {
            var vertices = new[]
            {
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one, Vector3.left
            };

            var normals = new Vector3[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            var colors = new Color[vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                normals[i] = Vector3.up;
                tangents[i] = new Vector4(1f, 0f, 0f, 1f);
                colors[i] = new Color(0.5f, 0.25f, 0f, 1f);
            }

            var uvs = MeshSnapshot.NewUvArray();
            for (var channel = 0; channel < 8; channel++)
            {
                var values = new Vector4[vertices.Length];
                for (var i = 0; i < values.Length; i++) values[i] = new Vector4(i, channel, 0f, 0f);
                uvs[channel] = values;
            }

            var weights = new BoneWeight[vertices.Length];
            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }

            var frames = new[]
            {
                new[]
                {
                    new BlendShapeFrameSnapshot(
                        0f,
                        new Vector3[vertices.Length],
                        new Vector3[vertices.Length],
                        new Vector3[vertices.Length])
                }
            };

            return MeshSnapshot.Create(
                "Full",
                vertices,
                normals,
                tangents,
                colors,
                uvs,
                new[] { new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0, 1, 2, 3 } },
                new[] { MeshTopology.Triangles, MeshTopology.Lines },
                new Bounds(new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f)),
                IndexFormat.UInt32,
                weights,
                new[] { Matrix4x4.identity },
                new[] { "Blink" },
                new[] { 1 },
                new[] { "Armature/Hips" },
                frames);
        }
    }
}

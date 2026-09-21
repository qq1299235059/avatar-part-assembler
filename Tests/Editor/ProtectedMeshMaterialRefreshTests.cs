using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Preview;
using NUnit.Framework;
using UnityEngine;

namespace AvatarPartAssembler.Tests.Protected
{
    /// <summary>
    /// Tests that the preview's material list follows the live renderers and profiles while the assembled
    /// geometry stays cached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The behaviour under test is the one an author notices immediately: they swap a material on the prefab, or
    /// point a profile's material declaration at another asset, and the preview must show it. The mesh is the
    /// expensive half — the whole body plus every part, and a decryption for a protected part — and none of it
    /// changes when a material reference does, so the two halves are deliberately separated: the geometry stays
    /// cached, the material list is re-resolved.
    /// </para>
    /// <para>
    /// The tests therefore check three things together: the material list follows the live state, it is not
    /// recomputed when nothing changed, and the geometry identity used to decide "reuse or rebuild" ignores
    /// material assets while still following every input that really moves geometry.
    /// </para>
    /// </remarks>
    public sealed class ProtectedMeshMaterialRefreshTests
    {
        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            ApaProtectedMeshCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ApaProtectedMeshCache.Clear();

            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) UnityEngine.Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        /// <summary>
        /// A frame that changed nothing must not re-resolve: the poll runs on every repaint, so an unchanged
        /// preview has to cost a token comparison rather than a second layout resolution.
        /// </summary>
        [Test]
        public void LiveMaterials_UnchangedInputs_AreResolvedOnce()
        {
            var rig = CreateRig();
            var resolver = NewResolver(rig);

            var first = resolver.Resolve();
            var second = resolver.Resolve();

            Assert.AreSame(first, second, "An unchanged frame must reuse the resolved array.");
            Assert.AreEqual(1, resolver.Revision, "The material list must be resolved exactly once.");
        }

        /// <summary>
        /// Swapping the material in a part renderer's slot is visible on the next frame, without a second
        /// assembly: the layout is unchanged, so only the asset behind the slot moved. This is the no-declaration
        /// case, where the profile leaves the slot's material to the renderer — which is what a simple part does.
        /// </summary>
        [Test]
        public void LiveMaterials_FollowASwappedRendererMaterial()
        {
            var rig = CreateRig(declareSemantics: false);
            var resolver = NewResolver(rig);

            var before = resolver.Resolve();
            Assert.AreEqual(rig.PartMaterialA, before[1], "The part slot must start with the renderer's material.");

            rig.PartRenderer.sharedMaterials = new[] { rig.PartMaterialB };
            var after = resolver.Resolve();

            Assert.AreEqual(rig.PartMaterialB, after[1], "The swapped material must be applied.");
            Assert.AreEqual(2, resolver.Revision, "The swap must trigger exactly one re-resolution.");
            Assert.IsTrue(resolver.ReproducedLayout, "The swap must not be treated as a layout change.");
        }

        /// <summary>
        /// The reported regression: a profile that declares a slot's material must not pin the asset behind it.
        /// The renderer's own slot is the current material — an installer's swap lives there — so replacing it on
        /// the prefab is what the preview shows, and the build reads the same rule.
        /// </summary>
        [Test]
        public void LiveMaterials_FollowASwappedRendererMaterialEvenWhenTheProfileDeclaresTheOldOne()
        {
            var rig = CreateRig();
            var resolver = NewResolver(rig);

            Assert.AreEqual(
                rig.PartMaterialA,
                resolver.Resolve()[1],
                "The declared asset is also the one the renderer holds, so both rules agree to start with.");

            rig.PartRenderer.sharedMaterials = new[] { rig.PartMaterialB };
            var after = resolver.Resolve();

            Assert.AreEqual(
                rig.PartMaterialB,
                after[1],
                "The renderer's slot is the current material asset; the profile's stale reference must not win.");
            Assert.AreEqual(2, resolver.Revision, "The swap must trigger exactly one re-resolution.");
            Assert.IsTrue(resolver.ReproducedLayout, "The swap must not be treated as a layout change.");
        }

        /// <summary>
        /// The declaration remains the fallback for a slot the renderer cannot name: a part whose materials live in
        /// the profile, with nothing in the renderer's slots, keeps building exactly as it did.
        /// </summary>
        [Test]
        public void LiveMaterials_FallBackToTheDeclaredMaterialWhenTheRendererSlotIsEmpty()
        {
            var rig = CreateRig();
            var resolver = NewResolver(rig);

            rig.PartRenderer.sharedMaterials = new Material[0];
            Assert.AreEqual(
                rig.PartMaterialA,
                resolver.Resolve()[1],
                "A slot the renderer does not have falls back to the declaration.");

            // The declaration is then the only statement about the slot, so repointing it is visible.
            rig.Profile.MaterialSemantics[0].Material = rig.PartMaterialB;
            var after = resolver.Resolve();

            Assert.AreEqual(rig.PartMaterialB, after[1], "The profile's current declaration must be applied.");
            Assert.IsTrue(resolver.ReproducedLayout);
        }

        /// <summary>
        /// Editing a referenced material asset is visible without any refresh at all, because the preview holds
        /// the author's own asset — no clone, no copy of its properties. This test pins that property: the
        /// resolved entry is reference-equal to the asset, so a colour, texture, shader, or keyword edit is in the
        /// picture the moment the author makes it.
        /// </summary>
        [Test]
        public void LiveMaterials_ReferenceTheAuthorsAssets_SoContentEditsAreVisibleImmediately()
        {
            var rig = CreateRig();
            var resolver = NewResolver(rig);

            var materials = resolver.Resolve();

            Assert.AreSame(rig.PartMaterialA, materials[1]);
            Assert.AreSame(rig.BodyMaterial, materials[0]);

            // A content edit changes no reference, so it must not invalidate anything either.
            rig.PartMaterialA.color = new Color(0.25f, 0.5f, 0.75f, 1f);
            var afterEdit = resolver.Resolve();

            Assert.AreSame(materials, afterEdit, "A material content edit must not re-resolve the list.");
            Assert.AreEqual(1, resolver.Revision);
        }

        /// <summary>
        /// The geometry identity ignores material assets: a swap that leaves the slot structure alone produces the
        /// same geometry fingerprint, which is what lets the node reuse its cached mesh.
        /// </summary>
        [Test]
        public void GeometryFingerprint_IgnoresMaterialIdentity()
        {
            var rig = CreateRig(declareSemantics: false);
            var before = BuildContext(rig);

            rig.PartRenderer.sharedMaterials = new[] { rig.PartMaterialB };
            var after = BuildContext(rig);

            Assert.AreNotEqual(
                ApaPreviewFingerprint.OfContext(before),
                ApaPreviewFingerprint.OfContext(after),
                "The full fingerprint must notice the swapped material.");
            Assert.AreEqual(
                ApaPreviewFingerprint.OfGeometryContext(before),
                ApaPreviewFingerprint.OfGeometryContext(after),
                "The geometry fingerprint must not change when only the material asset did.");
        }

        /// <summary>
        /// Repointing a profile's material declaration is a material-only change too: the declaration is an
        /// assembly input, but it decides which asset a slot names, not where any triangle goes.
        /// </summary>
        [Test]
        public void GeometryFingerprint_IgnoresADeclaredMaterialChange()
        {
            var rig = CreateRig();
            var before = BuildContext(rig);

            rig.Profile.MaterialSemantics[0].Material = rig.PartMaterialB;
            var after = BuildContext(rig);

            Assert.AreNotEqual(
                ApaPreviewFingerprint.OfContext(before),
                ApaPreviewFingerprint.OfContext(after),
                "The full fingerprint must notice the new declaration.");
            Assert.AreEqual(
                ApaPreviewFingerprint.OfGeometryContext(before),
                ApaPreviewFingerprint.OfGeometryContext(after),
                "Repointing a declaration must not invalidate the geometry.");
        }

        /// <summary>
        /// The counterpart of the two tests above: a change that really does reshape the layout must still
        /// invalidate the cached geometry, even though a slot's label is no longer hashed as geometry on its own.
        /// The declaration here keeps the same source submesh and the same policy as the inferred one it replaces
        /// — only the resolved slot structure differs, so only the layout hash can report the change.
        /// </summary>
        [Test]
        public void GeometryFingerprint_FollowsASemanticChangeThatReshapesTheLayout()
        {
            var rig = CreateRig(declareSemantics: false);
            var before = BuildContext(rig);

            // The base's inferred semantic is its material's name, so declaring the part's semantic under the same
            // name makes Auto compare the two assets. They differ, so the part's contribution cannot merge and the
            // generated mesh loses the slot it used to have. (The conflict itself is reported by the assembly, not
            // by a fingerprint.)
            rig.Profile.MaterialSemantics = new[]
            {
                new ApaMaterialSlotSemantic
                {
                    Semantic = rig.BodyMaterial.name,
                    SourceSubMesh = 0,
                    Material = rig.PartMaterialA,
                    Policy = ApaMaterialPolicyMode.Auto
                }
            };

            var after = BuildContext(rig);

            Assert.AreNotEqual(
                ApaPreviewFingerprint.OfGeometryContext(before),
                ApaPreviewFingerprint.OfGeometryContext(after),
                "A change that moves a contribution out of the layout must invalidate the cached geometry.");
        }

        /// <summary>A mesh edit is a geometry change, so the geometry identity must follow it.</summary>
        [Test]
        public void GeometryFingerprint_FollowsTheMesh()
        {
            var rig = CreateRig();
            var before = BuildContext(rig);

            var vertices = rig.PartMesh.vertices;
            vertices[0] += new Vector3(0.125f, 0f, 0f);
            rig.PartMesh.vertices = vertices;

            var after = BuildContext(rig);

            Assert.AreNotEqual(
                ApaPreviewFingerprint.OfGeometryContext(before),
                ApaPreviewFingerprint.OfGeometryContext(after),
                "A moved vertex must invalidate the cached geometry.");
        }

        /// <summary>
        /// A change that would reshape the layout — here a second submesh with its own declaration — is a
        /// geometry change, because the generated mesh's submesh count comes from the layout.
        /// </summary>
        [Test]
        public void GeometryFingerprint_FollowsTheSlotStructure()
        {
            var rig = CreateRig();
            var before = BuildContext(rig);

            AddPartSubMesh(rig);
            var after = BuildContext(rig);

            Assert.AreNotEqual(
                ApaPreviewFingerprint.OfGeometryContext(before),
                ApaPreviewFingerprint.OfGeometryContext(after),
                "A new submesh must invalidate the cached geometry.");
        }

        /// <summary>
        /// The structural comparison behind "reuse the geometry" ignores slot labels and follows the source
        /// mapping: a semantic that changed with a material's name is not a geometry change, while a contribution
        /// that moved to another slot is.
        /// </summary>
        [Test]
        public void MatchesLayout_IgnoresSemanticsButNotTheSourceMapping()
        {
            var rig = CreateRig();
            var resolver = NewResolver(rig);
            resolver.Resolve();

            var reference = resolver.Plan.MaterialLayout;

            // Two layouts with the same single source and two different semantic labels: the label is not part of
            // the geometry contract, so they must compare equal.
            var alpha = MaterialResolver.Resolve(
                new List<MaterialSemanticSource>
                {
                    new MaterialSemanticSource("Alpha", 0, rig.BodyMaterial, string.Empty,
                        ApaMaterialPolicyMode.Auto)
                },
                new List<ValidationIssue>());

            var beta = MaterialResolver.Resolve(
                new List<MaterialSemanticSource>
                {
                    new MaterialSemanticSource("Beta", 0, rig.BodyMaterial, string.Empty,
                        ApaMaterialPolicyMode.Auto)
                },
                new List<ValidationIssue>());

            Assert.IsTrue(
                ApaPreviewLiveMaterials.MatchesLayout(alpha, beta),
                "A slot's semantic label must not participate in the geometry comparison.");

            // UseTarget moves the part's contribution into the base slot, which is a real geometry change: the
            // part's triangles end up in another submesh.
            var merged = MaterialResolver.Resolve(
                new List<MaterialSemanticSource>
                {
                    new MaterialSemanticSource("BodyMat", 0, rig.BodyMaterial, string.Empty,
                        ApaMaterialPolicyMode.Auto),
                    new MaterialSemanticSource("BodyMat", 0, rig.BodyMaterial, "part-a",
                        ApaMaterialPolicyMode.UseTarget)
                },
                new List<ValidationIssue>());

            Assert.IsFalse(
                ApaPreviewLiveMaterials.MatchesLayout(merged, reference),
                "A contribution that moved into another slot must not be treated as a material-only change.");
        }

        // ---- rig -------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public GameObject Body;
            public GameObject PartHost;
            public Renderer PartRenderer;
            public Mesh PartMesh;
            public Material BodyMaterial;
            public Material PartMaterialA;
            public Material PartMaterialB;
            public AvatarPartInstaller Installer;
            public ApaPartProfile Profile;
        }

        private Rig CreateRig(bool declareSemantics = true)
        {
            var avatar = NewGameObject("Avatar", null);

            var bodyMaterial = NewMaterial("BodyMat", new Color(1f, 0f, 0f, 1f));
            var body = NewRenderer("Body", avatar.transform, NewQuadMesh("BodyMesh"), bodyMaterial);

            var partMaterialA = NewMaterial("Cloth", new Color(0f, 1f, 0f, 1f));
            var partMaterialB = NewMaterial("ClothAlt", new Color(0f, 0f, 1f, 1f));

            var host = NewGameObject("Part Host", avatar.transform);
            var partMesh = NewQuadMesh("PartMesh");
            var partRenderer = NewRenderer("Part", host.transform, partMesh, partMaterialA);

            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);
            profile.Identity.PartId = "part-a";
            profile.Identity.Slot = ApaPartSlot.LeftArm;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(body.GetComponent<MeshFilter>().sharedMesh,
                "Body", string.Empty);

            if (declareSemantics)
            {
                // An explicit declaration with KeepPart: the part keeps its own slot whatever the material is, so a
                // material swap changes the asset behind the slot and nothing about the layout. The declaration is
                // the assembly input, so the renderer's slot is not what decides the material.
                profile.MaterialSemantics = new[]
                {
                    new ApaMaterialSlotSemantic
                    {
                        Semantic = "Cloth",
                        SourceSubMesh = 0,
                        Material = partMaterialA,
                        Policy = ApaMaterialPolicyMode.KeepPart
                    }
                };
            }

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = profile;
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = body;

            return new Rig
            {
                Avatar = avatar,
                Body = body,
                PartHost = host,
                PartRenderer = partRenderer.GetComponent<Renderer>(),
                PartMesh = partMesh,
                BodyMaterial = bodyMaterial,
                PartMaterialA = partMaterialA,
                PartMaterialB = partMaterialB,
                Installer = installer,
                Profile = profile
            };
        }

        /// <summary>Adds a second submesh and a second declaration, so the layout gains a slot.</summary>
        private void AddPartSubMesh(Rig rig)
        {
            var mesh = rig.PartMesh;
            var vertices = new List<Vector3>(mesh.vertices);
            var offset = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, 1f));
            vertices.Add(new Vector3(1f, 0f, 1f));
            vertices.Add(new Vector3(1f, 1f, 1f));
            vertices.Add(new Vector3(0f, 1f, 1f));
            mesh.vertices = vertices.ToArray();
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.SetTriangles(new[] { offset, offset + 1, offset + 2, offset, offset + 2, offset + 3 }, 1);

            rig.Profile.MaterialSemantics = new[]
            {
                rig.Profile.MaterialSemantics[0],
                new ApaMaterialSlotSemantic
                {
                    Semantic = "Trim",
                    SourceSubMesh = 1,
                    Material = rig.PartMaterialB,
                    Policy = ApaMaterialPolicyMode.KeepPart
                }
            };
        }

        private ValidationContext BuildContext(Rig rig)
        {
            var context = ContextBuilder.Build(rig.Avatar, null, out var issues);
            Assert.IsNotNull(context, Describe(issues));
            return context;
        }

        private ApaPreviewLiveMaterials NewResolver(Rig rig)
        {
            var context = BuildContext(rig);
            var assembly = ApaCore.Assemble(context);
            Assert.IsTrue(assembly.Succeeded, assembly.Issues.FormatAll());

            try
            {
                return ApaPreviewLiveMaterials.Create(
                    assembly.Plan,
                    context,
                    rig.Body.GetComponent<Renderer>(),
                    new List<AvatarPartInstaller> { rig.Installer });
            }
            finally
            {
                // The assembled mesh is not what this test is about, and it is not owned by the resolver.
                if (assembly.Mesh != null) UnityEngine.Object.DestroyImmediate(assembly.Mesh);
            }
        }

        private GameObject NewGameObject(string name, Transform parent)
        {
            var gameObject = new GameObject(name);
            if (parent != null) gameObject.transform.SetParent(parent, false);
            _created.Add(gameObject);
            return gameObject;
        }

        private GameObject NewRenderer(string name, Transform parent, Mesh mesh, Material material)
        {
            var gameObject = NewGameObject(name, parent);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { material };
            return gameObject;
        }

        private Material NewMaterial(string name, Color color)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = name, color = color };
            _created.Add(material);
            return material;
        }

        /// <summary>A readable one-submesh quad, so the layout has something to place.</summary>
        private Mesh NewQuadMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f)
            };
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _created.Add(mesh);
            return mesh;
        }

        private static string Describe(IReadOnlyList<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return " (no issues)";

            var builder = new StringBuilder();
            for (var i = 0; i < issues.Count; i++) builder.Append('\n').Append(issues[i]);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Source-level contract tests for the preview's material refresh wiring.
    /// </summary>
    /// <remarks>
    /// The node's refresh decision runs inside NDMF's pipeline, where a behavioural test would be testing NDMF.
    /// The two properties that matter — the frame applies the live list, and a material-only change reuses the
    /// geometry — are pinned here instead, beside the behavioural tests that cover the resolution itself.
    /// </remarks>
    public sealed class PreviewMaterialRefreshContractTests
    {
        [Test]
        public void Node_AppliesLiveMaterialsAndReusesGeometryForMaterialOnlyChanges()
        {
            var node = ReadPreviewSource("PreviewNode.cs");
            var fingerprint = ReadPreviewSource("PreviewFingerprint.cs");
            var materials = ReadPreviewSource("PreviewLiveMaterials.cs");

            // The frame applies the live list, not the list captured with the mesh.
            StringAssert.Contains("_liveMaterials.Resolve()", node);

            // A material-only change is decided by the geometry fingerprint and reuses the cached mesh.
            StringAssert.Contains("GeometryFingerprint", node);
            StringAssert.Contains("OfGeometryContext", fingerprint);
            StringAssert.Contains("RenderAspects.Material", node);

            // The live list is resolved through the same resolver the assembly uses, and never clones an asset.
            StringAssert.Contains("MaterialResolver.CollectLiveSources", materials);
            StringAssert.Contains("MaterialResolver.Resolve(", materials);
            Assert.IsFalse(
                materials.Contains("new Material("),
                "The live material list must reference the author's assets, never clone them.");
        }

        private static string ReadPreviewSource(string fileName)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var full = Path.Combine(
                projectRoot, "Packages", "dev.avatar-part-assembler", "Editor", "Preview", fileName);

            if (!File.Exists(full)) Assert.Ignore("Source file not found: " + full);
            return File.ReadAllText(full);
        }
    }
}

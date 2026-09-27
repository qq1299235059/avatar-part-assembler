using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using AvatarPartAssembler.Editor;
using AvatarPartAssembler.Editor.Preview;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarPartAssembler.Tests
{
    /// <summary>
    /// Tests that an APA preview reads the state Modular Avatar's preview stages wrote, not the author's original
    /// renderers, and that a substituted body is proven before any authored address is mapped onto it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NDMF runs render filters in pass order over one proxy per renderer, and every earlier stage's
    /// <c>OnFrame</c> has already run when a later stage is instantiated. Modular Avatar's Mesh Cutter, Shape
    /// Changer, and Material Setter are those earlier stages, so the proxy APA is handed carries their result.
    /// These tests cover the two halves of reading it: the capture source (which mesh and material list a capture
    /// uses) and the frame reads (which renderer a blend-shape weight comes from).
    /// </para>
    /// <para>
    /// The behavioural tests use a real hierarchy and real runtime meshes, because the defect they cover is about
    /// which object a capture reads. The structural tests read the production sources as text, which is how this
    /// repository pins an ordering or a wiring invariant that needs no Unity session to check.
    /// </para>
    /// </remarks>
    public sealed class PreviewMaCompatibilityTests
    {
        /// <summary>Objects created by a test, destroyed in reverse order in <see cref="TearDown"/>.</summary>
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }

            _created.Clear();
        }

        // ---- the capture source ----------------------------------------------------------------------

        /// <summary>The proxy's mesh and materials are preferred, and the substitution is named.</summary>
        [Test]
        public void CaptureSource_ReadsTheProxyAndFallsBackToTheLiveRenderer()
        {
            var liveMesh = NewRingMesh("LiveBody", new Vector3(0f, 0f, 1f));
            var cutMesh = NewRingMesh("CutBody", new Vector3(0f, 0f, 1f));
            var liveMaterial = NewMaterial("LiveMaterial");
            var swappedMaterial = NewMaterial("SwappedMaterial");

            var body = NewRenderer("Body", null, liveMesh, liveMaterial);
            var proxy = NewRenderer("Proxy renderer for Body", null, cutMesh, swappedMaterial);
            var unpaired = NewRenderer("Unpaired", null, liveMesh, liveMaterial);

            var source = ApaPreviewCaptureSource.FromProxyPairs(new List<(Renderer, Renderer)>
            {
                (body.GetComponent<Renderer>(), proxy.GetComponent<Renderer>())
            });

            Assert.IsTrue(source.SubstitutesUpstreamState, "A different proxy mesh is a substitution.");
            Assert.AreSame(cutMesh, source.MeshFor(body.GetComponent<Renderer>(), liveMesh));
            Assert.AreSame(swappedMaterial, source.MaterialsFor(body.GetComponent<Renderer>(), new[] { liveMaterial })[0]);
            Assert.AreSame(proxy.GetComponent<Renderer>(), source.SubstitutedRendererFor(body.GetComponent<Renderer>()));
            Assert.AreEqual(
                ApaPreviewCaptureSource.UpstreamMeshModifiedReason,
                source.SubstitutionReasonFor(body.GetComponent<Renderer>()));

            // A renderer this source has no pair for is not a substitution, and reads its own live values.
            var unpairedRenderer = unpaired.GetComponent<Renderer>();
            Assert.AreEqual(1, source.Count, "Only the recorded pair is a substitute.");
            Assert.AreSame(liveMesh, source.MeshFor(unpairedRenderer, liveMesh));
            Assert.IsNull(source.SubstitutedRendererFor(unpairedRenderer));
            Assert.IsNull(source.SubstitutionReasonFor(unpairedRenderer));
        }

        /// <summary>A materials-only substitution does not force a geometry re-capture.</summary>
        [Test]
        public void CaptureSource_ReportsAMaterialsOnlySubstitutionWithoutClaimingGeometry()
        {
            var liveMesh = NewRingMesh("LiveBody", new Vector3(0f, 0f, 1f));
            var liveMaterial = NewMaterial("LiveMaterial");
            var swappedMaterial = NewMaterial("SwappedMaterial");

            var body = NewRenderer("Body", null, liveMesh, liveMaterial);
            var proxy = NewRenderer("Proxy renderer for Body", null, liveMesh, swappedMaterial);

            var source = ApaPreviewCaptureSource.FromProxyPairs(new List<(Renderer, Renderer)>
            {
                (body.GetComponent<Renderer>(), proxy.GetComponent<Renderer>())
            });

            Assert.IsFalse(source.SubstitutesUpstreamState, "The mesh is the same object, so no rebuild is needed.");
            Assert.AreEqual(
                ApaPreviewCaptureSource.UpstreamMaterialsModifiedReason,
                source.SubstitutionReasonFor(body.GetComponent<Renderer>()));
        }

        /// <summary>A destroyed proxy reads as unknown and the live renderer answers.</summary>
        [Test]
        public void CaptureSource_IgnoresADestroyedProxy()
        {
            var liveMesh = NewRingMesh("LiveBody", new Vector3(0f, 0f, 1f));
            var cutMesh = NewRingMesh("CutBody", new Vector3(0f, 0f, 1f));
            var body = NewRenderer("Body", null, liveMesh, null);
            var proxy = NewRenderer("Proxy renderer for Body", null, cutMesh, null);

            var source = ApaPreviewCaptureSource.FromProxyPairs(new List<(Renderer, Renderer)>
            {
                (body.GetComponent<Renderer>(), proxy.GetComponent<Renderer>())
            });

            Object.DestroyImmediate(proxy);

            Assert.IsFalse(source.SubstitutesUpstreamState);
            Assert.AreSame(liveMesh, source.MeshFor(body.GetComponent<Renderer>(), liveMesh));
            Assert.IsNull(source.SubstitutedRendererFor(body.GetComponent<Renderer>()));
            Assert.IsNull(source.SubstitutionReasonFor(body.GetComponent<Renderer>()));
        }

        // ---- the capture through the core -------------------------------------------------------------

        /// <summary>
        /// A cut body is captured, proven, and assembled: the preview's mesh is smaller than the uncut assembly's
        /// by exactly the triangle the cutter removed.
        /// </summary>
        [Test]
        public void GroupCapture_ReadsTheCutMeshProvesItAndAssemblesIt()
        {
            var rig = CreateRig();
            var cutMesh = NewCutRingMesh("CutBody", rig.BodyMesh);

            var cutProxy = NewRenderer("Proxy renderer for Body", null, cutMesh, null);
            var source = ApaPreviewCaptureSource.FromProxyPairs(new List<(Renderer, Renderer)>
            {
                (rig.Body, cutProxy.GetComponent<Renderer>())
            });

            var request = ApaPreviewDiscovery.DiscoverGroups(
                rig.Avatar, ContextBuilder.CollectAllInstallers(rig.Avatar), null, source)[0];

            Assert.IsTrue(request.IsRenderable, Describe(request.Issues.Issues));
            Assert.IsNotNull(request.Context, "The group must have been captured.");
            Assert.IsNotNull(request.Context.Base.BodyProvenance, "The cut body must have been proven.");
            Assert.AreEqual(
                cutMesh.name,
                request.Context.Base.Mesh.Name,
                "The captured mesh must be the proxy's cut mesh, not the author's original.");
            Assert.AreEqual(
                rig.BodyMesh.vertexCount,
                request.Context.Base.BodyProvenance.AuthoredMesh.VertexCount,
                "The authored mesh is the address space the profile was written against.");
            Assert.AreEqual(
                1,
                request.Context.Base.BodyProvenance.RemovedTriangleCount,
                "Exactly the triangle the cutter removed must be reported as removed.");

            Assert.IsTrue(
                HasDetail(request.Issues.Issues, "reason=upstream-preview-mesh-modified"),
                "The substitution must be reported with its stable token: " + Describe(request.Issues.Issues));
            Assert.IsTrue(
                HasDetail(request.Issues.Issues, "reason=body-provenance-proven-derivation"),
                "The proven derivation must be reported: " + Describe(request.Issues.Issues));

            // The cut geometry is what gets assembled, and it differs from the uncut assembly by the removed
            // triangle. Both runs go through the same welding, so the difference is the cutter's own removal.
            var cutTriangles = AssembleTriangleCount(rig.Avatar, source);
            var uncutTriangles = AssembleTriangleCount(rig.Avatar, null);

            Assert.AreEqual(
                uncutTriangles - 1,
                cutTriangles,
                "The assembled preview must be built from the cut body, not from the author's original mesh.");
        }

        /// <summary>A substituted body changes the request's fingerprint, so a refresh rebuilds.</summary>
        [Test]
        public void PreviewRequest_FollowsTheSubstitutedMesh()
        {
            var rig = CreateRig();
            var cutMesh = NewCutRingMesh("CutBody", rig.BodyMesh);
            var cutProxy = NewRenderer("Proxy renderer for Body", null, cutMesh, null);

            var source = ApaPreviewCaptureSource.FromProxyPairs(new List<(Renderer, Renderer)>
            {
                (rig.Body, cutProxy.GetComponent<Renderer>())
            });

            var installers = ContextBuilder.CollectAllInstallers(rig.Avatar);
            var uncut = ApaPreviewDiscovery.DiscoverGroups(rig.Avatar, installers, null, null)[0];
            var cut = ApaPreviewDiscovery.DiscoverGroups(rig.Avatar, installers, null, source)[0];

            Assert.AreNotEqual(
                uncut.Fingerprint,
                cut.Fingerprint,
                "A differently cut body is a different input set and must not reuse a cached mesh.");
        }

        // ---- the frame reads --------------------------------------------------------------------------

        /// <summary>A Shape Changer's weight lives on the proxy, and that is the weight the frame applies.</summary>
        [Test]
        public void BlendShapeWeight_PrefersTheProxyValue()
        {
            var bodyMesh = NewShapeMesh("BodyWithShape");
            var proxyMesh = NewShapeMesh("ProxyBodyWithShape");

            var body = NewSkinnedRenderer("Body", bodyMesh);
            var proxy = NewSkinnedRenderer("Proxy renderer for Body", proxyMesh);
            body.SetBlendShapeWeight(0, 5f);
            proxy.SetBlendShapeWeight(0, 42f);

            var binding = new ApaPreviewBlendShapeBinding(0, string.Empty, 0, body, "Smile");
            var source = ApaPreviewCaptureSource.FromProxyPairs(new List<(Renderer, Renderer)>
            {
                (body, proxy)
            });

            Assert.AreEqual(5f, ApaPreviewProxyApplier.ReadWeight(binding), 0.0001f, "Without a source the original answers.");
            Assert.AreEqual(
                42f,
                ApaPreviewProxyApplier.ReadWeight(binding, source),
                0.0001f,
                "With the proxy map the earlier stage's weight is the one that must reach the assembled mesh.");
        }

        /// <summary>The proxy's weight is written onto the assembled mesh by <c>Apply</c>.</summary>
        [Test]
        public void Apply_WritesTheProxyDerivedWeightOntoTheAssembledMesh()
        {
            var bodyMesh = NewShapeMesh("BodyWithShape");
            var proxyMesh = NewShapeMesh("ProxyBodyWithShape");
            var assembledMesh = NewShapeMesh("AssembledWithShape");

            var body = NewSkinnedRenderer("Body", bodyMesh);
            var proxy = NewSkinnedRenderer("Proxy renderer for Body", proxyMesh);
            body.SetBlendShapeWeight(0, 5f);
            proxy.SetBlendShapeWeight(0, 42f);

            var bindings = new List<ApaPreviewBlendShapeBinding>
            {
                new ApaPreviewBlendShapeBinding(0, string.Empty, 0, body, "Smile")
            };

            var source = ApaPreviewCaptureSource.FromProxyPairs(new List<(Renderer, Renderer)>
            {
                (body, proxy)
            });

            ApaPreviewProxyApplier.Apply(proxy, assembledMesh, null, null, bindings, source);

            Assert.AreEqual(42f, proxy.GetBlendShapeWeight(0), 0.0001f);
        }

        // ---- the provenance proof ---------------------------------------------------------------------

        /// <summary>A triangle subset is proven, and both address directions map.</summary>
        [Test]
        public void Provenance_ProvesATriangleSubsetAndMapsBothDirections()
        {
            var authoredMesh = NewRingMesh("Authored", new Vector3(0f, 0f, 1f));
            var derivedMesh = NewCutRingMesh("Derived", authoredMesh);

            var authored = Capture(authoredMesh);
            var derived = Capture(derivedMesh);

            var issues = new List<ValidationIssue>();
            Assert.IsTrue(
                ApaBodyMeshProvenance.TryProve(authored, derived, issues, out var provenance),
                Describe(issues));

            Assert.IsNotNull(provenance);
            Assert.IsTrue(provenance.PreservesVertexIndices);
            Assert.AreEqual(1, provenance.RemovedTriangleCount);

            // The removed authored triangle maps to nothing; every other one maps to its captured position.
            Assert.IsTrue(provenance.TryMapAuthoredTriangle(0, 3, out var removedTriangle));
            Assert.AreEqual(-1, removedTriangle, "A triangle the cutter removed is already satisfied.");

            Assert.IsTrue(provenance.TryMapAuthoredTriangle(0, 0, out var firstTriangle));
            Assert.AreEqual(0, firstTriangle);

            Assert.IsTrue(provenance.TryMapDerivedTriangle(0, 0, out var authoredTriangle));
            Assert.AreEqual(0, authoredTriangle);

            Assert.IsTrue(provenance.TryMapDerivedVertex(4, out var authoredVertex));
            Assert.AreEqual(4, authoredVertex);
        }

        /// <summary>A mesh that is not the authored mesh with triangles removed is refused, with a token.</summary>
        [Test]
        public void Provenance_RefusesAMeshThatIsNotATriangleSubset()
        {
            var authoredMesh = NewRingMesh("Authored", new Vector3(0f, 0f, 1f));
            var otherMesh = NewRingMesh("Other", new Vector3(0f, 0f, 5f));

            var issues = new List<ValidationIssue>();
            var proven = ApaBodyMeshProvenance.TryProve(
                Capture(authoredMesh), Capture(otherMesh), issues, out var provenance);

            Assert.IsFalse(proven, "A mesh with different vertex positions is not a derivation.");
            Assert.IsNull(provenance, "No correspondence may be handed out for an unproven derivation.");
            Assert.IsTrue(
                HasDetail(issues, "reason=" + ApaBodyMeshProvenance.VertexDataMismatchReason),
                Describe(issues));
        }

        /// <summary>A re-packed mesh is refused rather than welded at the wrong vertices.</summary>
        [Test]
        public void Provenance_RefusesARePackedVertexOrder()
        {
            var authoredMesh = NewRingMesh("Authored", new Vector3(0f, 0f, 1f));
            var rePackedMesh = NewRePackedRingMesh("RePacked", authoredMesh);

            var issues = new List<ValidationIssue>();
            var proven = ApaBodyMeshProvenance.TryProve(
                Capture(authoredMesh), Capture(rePackedMesh), issues, out _);

            Assert.IsFalse(proven);
            Assert.IsTrue(
                HasDetail(issues, "reason=" + ApaBodyMeshProvenance.VertexIndexRemapReason),
                Describe(issues));
        }

        /// <summary>The substitution diagnostic is allocated, titled, and localized.</summary>
        [Test]
        public void SubstitutionDiagnostic_IsAllocatedWithAStableToken()
        {
            Assert.AreEqual("APA055", ApaErrorCode.PreviewUpstreamModification);
            Assert.AreEqual("APA056", ApaErrorCode.BodyMeshDerivationUnproven);
            Assert.AreEqual("APA057", ApaErrorCode.BodyMeshDerivationProven);
            Assert.AreEqual("APA058", ApaErrorCode.InstallerRemovedFromBuild);

            Assert.IsNotEmpty(ApaErrorCode.GetTitle(ApaErrorCode.PreviewUpstreamModification));
            Assert.IsNotEmpty(ApaErrorCode.GetTitle(ApaErrorCode.BodyMeshDerivationUnproven));
            Assert.IsNotEmpty(ApaErrorCode.GetTitle(ApaErrorCode.InstallerRemovedFromBuild));
            Assert.IsTrue(ApaReservedCodes.IsMilestone16Code(ApaErrorCode.PreviewUpstreamModification));

            Assert.AreEqual(
                "upstream-preview-mesh-modified",
                ApaPreviewCaptureSource.UpstreamMeshModifiedReason);
        }

        // ---- structural contracts ---------------------------------------------------------------------

        /// <summary>The filter captures through the proxy pairs NDMF hands it.</summary>
        [Test]
        public void RenderFilter_CapturesThroughTheProxyPairs()
        {
            var filter = ReadPackageSource("Editor", "Preview", "AvatarPartRenderFilter.cs");

            StringAssert.Contains("ApaPreviewCaptureSource.FromProxyPairs(proxyPairs)", filter);
            StringAssert.Contains("ApaPreviewNode.Create(request, captureSource)", filter);
        }

        /// <summary>The node reads the frame through the proxy map and re-binds it on every refresh.</summary>
        [Test]
        public void PreviewNode_ReadsTheFrameThroughTheProxyMap()
        {
            var node = ReadPackageSource("Editor", "Preview", "PreviewNode.cs");

            StringAssert.Contains("_frameProxies.Record(original, proxy);", node);
            StringAssert.Contains("RebindFrameProxies(proxyPairs)", node);
            StringAssert.Contains("_frameProxies);", node);
        }

        /// <summary>The assembly pass is still ordered after Modular Avatar, so its filter runs after theirs.</summary>
        [Test]
        public void AssemblyPass_IsDeclaredAfterModularAvatar()
        {
            var plugin = ReadPackageSource("Editor", "NDMF", "ApaNdmfPlugin.cs");

            StringAssert.Contains(".AfterPlugin(ModularAvatarPluginQualifiedName)", plugin);
            StringAssert.Contains(".PreviewingWith(ApaPreviewRegistration.CreateFilter())", plugin);
            StringAssert.Contains(".Then.Run(ApaInstallerCleanupPass.Instance)", plugin);
        }

        /// <summary>The capture reports a substitution through the core, with the source's stable token.</summary>
        [Test]
        public void ContextBuilder_ReportsTheSubstitutionWithAStableToken()
        {
            var builder = ReadPackageSource("Editor", "Input", "ContextBuilder.cs");

            StringAssert.Contains("ReportSubstitution(source, targetRenderer, liveMesh, issues);", builder);
            StringAssert.Contains("ReportSubstitution(source, partRenderer, livePartMesh, issues, PartIdOf(installer));", builder);
            StringAssert.Contains("detail: \"reason=\" + reason + \"; renderer=\" + renderer.name", builder);
            StringAssert.Contains("ApaErrorCode.PreviewUpstreamModification", builder);
            StringAssert.Contains("ApaBodyMeshProvenance.TryProve(authored, captured, issues, out var provenance)", builder);
        }

        /// <summary>The authored address space is what the compatibility and removal rules compare against.</summary>
        [Test]
        public void CoreRules_UseTheAuthoredAddressSpace()
        {
            var compatibility = ReadPackageSource("Editor", "Validation", "Rules", "CompatibilityRule.cs");
            var removal = ReadPackageSource("Editor", "Validation", "Rules", "RemovalRule.cs");
            var planner = ReadPackageSource("Editor", "Assembly", "AssemblyPlanner.cs");

            StringAssert.Contains("var actual = context.Base.AuthoredMesh;", compatibility);
            StringAssert.Contains("var mesh = context.Base.AuthoredMesh;", removal);
            StringAssert.Contains("provenance.TryMapAuthoredTriangle(", planner);
            StringAssert.Contains("if (capturedTriangle < 0) continue;", planner);
        }

        // ---- rig --------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public GameObject Avatar;
            public Renderer Body;
            public Mesh BodyMesh;
        }

        /// <summary>
        /// "Avatar" with a body renderer named "Body" and one installer whose profile records the body mesh's
        /// signature exactly the way authoring captures it.
        /// </summary>
        private Rig CreateRig()
        {
            var avatar = NewGameObject("Avatar", null);
            var bodyMesh = NewRingMesh("BodyMesh", new Vector3(0f, 0f, 1f));
            var bodyObject = NewRenderer("Body", avatar.transform, bodyMesh, null);

            var host = NewGameObject("Part Host", avatar.transform);
            var partRenderer = NewRenderer(
                "Part", host.transform, NewRingMesh("PartMesh", new Vector3(0f, 0f, -1f)), null);

            var installer = host.AddComponent<AvatarPartInstaller>();
            installer.Profile = NewProfile(bodyMesh, "part-a");
            installer.PartRoot = partRenderer;
            installer.TargetRendererObject = bodyObject;

            return new Rig
            {
                Avatar = avatar,
                Body = bodyObject.GetComponent<Renderer>(),
                BodyMesh = bodyMesh
            };
        }

        /// <summary>A profile whose signature and seam match the body mesh, as authoring records them.</summary>
        private ApaPartProfile NewProfile(Mesh bodyMesh, string partId)
        {
            var profile = ScriptableObject.CreateInstance<ApaPartProfile>();
            _created.Add(profile);

            profile.Identity.PartId = partId;
            profile.Identity.Slot = ApaPartSlot.Head;
            profile.Compatibility = MeshSnapshotFactory.CaptureSignature(bodyMesh, "Body", string.Empty);

            profile.Seam = new ApaSeamProfile();
            profile.Seam.SetPaired(Range(4), Range(4));
            return profile;
        }

        /// <summary>Plans and assembles the avatar through the core, returning the generated triangle count.</summary>
        /// <remarks>
        /// The generated mesh is released by disposing the result: this is a test, so nothing takes ownership of
        /// it, and a leaked mesh would outlive the test in the editor session.
        /// </remarks>
        private int AssembleTriangleCount(GameObject avatar, ApaCaptureSource captureSource)
        {
            var planned = ApaCore.PlanGroups(avatar, null, out var planIssues, captureSource: captureSource);
            Assert.IsTrue(planned.Succeeded, Describe(planIssues));

            using (var assembled = ApaCore.AssembleGroups(planned, null))
            {
                Assert.IsTrue(assembled.Succeeded, "The assembly must produce a mesh.");
                var mesh = assembled.Groups[0].Assembly.Mesh;
                Assert.IsNotNull(mesh);
                return mesh.triangles.Length / 3;
            }
        }

        private static MeshSnapshot Capture(Mesh mesh)
        {
            return MeshSnapshotFactory.Capture(mesh, null, out _);
        }

        // ---- fixtures ---------------------------------------------------------------------------------

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
            if (material != null) renderer.sharedMaterial = material;
            return gameObject;
        }

        private SkinnedMeshRenderer NewSkinnedRenderer(string name, Mesh mesh)
        {
            var gameObject = NewGameObject(name, null);
            var renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }

        /// <summary>A readable ring plus apex: five vertices and four cap triangles.</summary>
        private Mesh NewRingMesh(string name, Vector3 apex)
        {
            var positions = new List<Vector3>(MeshFixtures.Ring(4, 1f)) { apex };
            var mesh = new Mesh { name = name };
            mesh.vertices = positions.ToArray();
            mesh.SetTriangles(MeshFixtures.CapTriangles(4, 0, 4), 0);
            _created.Add(mesh);
            return mesh;
        }

        /// <summary>The same ring with one cap triangle removed: the shape a Mesh Cutter's preview produces.</summary>
        private Mesh NewCutRingMesh(string name, Mesh authored)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = authored.vertices;
            mesh.normals = authored.normals;
            mesh.tangents = authored.tangents;
            mesh.colors = authored.colors;
            mesh.uv = authored.uv;

            var triangles = authored.GetTriangles(0);
            var kept = new int[triangles.Length - 3];
            for (var i = 0; i < kept.Length; i++) kept[i] = triangles[i];

            mesh.SetTriangles(kept, 0);
            _created.Add(mesh);
            return mesh;
        }

        /// <summary>The same geometry with one vertex and its triangles dropped and the rest re-packed.</summary>
        /// <remarks>
        /// This is the shape a cutter with a vertex-compacting path produces: the geometry is a subset, but the
        /// vertex indices moved. Seam indices are authored indices, so the proof must refuse it rather than weld
        /// the part at whatever now sits at those indices.
        /// </remarks>
        private Mesh NewRePackedRingMesh(string name, Mesh authored)
        {
            var vertices = authored.vertices;

            // Authored: ring 0..3 plus apex 4, with cap triangles (0,1,4) (1,2,4) (2,3,4) (3,0,4). Dropping
            // vertex 1 leaves 0,2,3,4 and the two triangles that do not reference it.
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { vertices[0], vertices[2], vertices[3], vertices[4] };
            mesh.SetTriangles(new[] { 1, 2, 3, 2, 0, 3 }, 0);
            _created.Add(mesh);
            return mesh;
        }

        /// <summary>A one-shape skinned mesh, for the blend-shape read tests.</summary>
        private Mesh NewShapeMesh(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);

            var deltas = new[] { Vector3.zero, Vector3.zero, new Vector3(0f, 0f, 1f) };
            var zeroes = new[] { Vector3.zero, Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame("Smile", 100f, deltas, zeroes, zeroes);
            _created.Add(mesh);
            return mesh;
        }

        private Material NewMaterial(string name)
        {
            var material = new Material(Shader.Find("Standard")) { name = name };
            _created.Add(material);
            return material;
        }

        private static int[] Range(int count)
        {
            var result = new int[count];
            for (var i = 0; i < count; i++) result[i] = i;
            return result;
        }

        // ---- helpers ----------------------------------------------------------------------------------

        private static bool HasDetail(IReadOnlyList<ValidationIssue> issues, string token)
        {
            if (issues == null) return false;

            for (var i = 0; i < issues.Count; i++)
            {
                if (issues[i].Detail != null && issues[i].Detail.Contains(token)) return true;
            }

            return false;
        }

        private static string Describe(IReadOnlyList<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0) return " (no issues)";

            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < issues.Count; i++) builder.Append('\n').Append(issues[i]);
            return builder.ToString();
        }

        /// <summary>Reads one production source file of this package, relative to the package root.</summary>
        private static string ReadPackageSource(params string[] relativeParts)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.Combine(projectRoot, "Packages", "dev.avatar-part-assembler");
            for (var i = 0; i < relativeParts.Length; i++) path = Path.Combine(path, relativeParts[i]);

            if (!File.Exists(path)) Assert.Ignore("Package source file not found: " + path);
            return File.ReadAllText(path);
        }
    }
}

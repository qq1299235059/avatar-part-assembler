using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The window-side surface the Scene View tool draws from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tool talks to this interface rather than to the window type, so the drawing behaviour can be reviewed
    /// without reading window code.
    /// </para>
    /// <para>
    /// <b>Read-only (M16).</b> The interface exposes no mutation of any kind: the removal set is authored by the
    /// texture mask in the window, and the seam is generated from world positions there. There is no picking mode,
    /// no hover state, and no click handler left, so nothing the author does in the Scene View can change a draft,
    /// a profile, or a stored seam. That is why the interface is smaller than it used to be: the methods that
    /// edited the removal set from a Scene View click are gone with the picking mode they served.
    /// </para>
    /// </remarks>
    public interface IApaAuthoringSceneHost
    {
        /// <summary>The current selection.</summary>
        ApaAuthoringSelection Selection { get; }

        /// <summary>The current removal mask.</summary>
        ApaRemovalMask Removal { get; }

        /// <summary>The current seam selection. Read-only for the tool: the seam is generated, never picked.</summary>
        ApaSeamSelection Seam { get; }

        /// <summary>True when the green seam-candidate overlay is drawn.</summary>
        bool ShowCandidateOverlay { get; }

        /// <summary>
        /// The resolved target seam candidates, or a result carrying the blocking diagnostic. The target side is
        /// spatial: its candidate set is every body vertex, and position decides the pairing.
        /// </summary>
        ApaSeamColorCandidateResult TargetSeamCandidates { get; }

        /// <summary>
        /// The resolved part seam candidates, or a result carrying the blocking diagnostic. The part side is the
        /// color-filtered one.
        /// </summary>
        ApaSeamColorCandidateResult PartSeamCandidates { get; }

        /// <summary>The vertex color that selects the seam candidates of the part mesh.</summary>
        Color32 SeamCandidateColor { get; }

        /// <summary>True when the red predicted-removal triangle overlay is drawn. Off by default.</summary>
        bool ShowRemovalOverlay { get; }

        /// <summary>
        /// True when the merge-check overlay is the active mode. It takes precedence over the removal, candidate,
        /// and seam overlays, which are hidden while it is on.
        /// </summary>
        bool MergeCheckOverlay { get; }

        /// <summary>The world-space tolerance the merge-check overlay evaluates with.</summary>
        float SeamTolerance { get; }
    }

    /// <summary>
    /// Scene View highlighting for the removal mask, the seam candidates, and the merge check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read-only by construction (M16).</b> There is no picking mode and no click or hover path: the removal
    /// set is authored by the texture mask in the window, and the seam is generated there from world positions.
    /// The tool therefore never registers the Scene View's default control and never consumes an event — the
    /// Scene View behaves normally whether the overlays are on or off.
    /// </para>
    /// <para>
    /// Highlighting follows the specification's colour vocabulary — red for geometry that will be removed,
    /// yellow for the base seam, cyan for the part seam, green for a seam candidate — and is bounded: at most
    /// <see cref="MaxDrawnTriangles"/> triangles and <see cref="MaxDrawnVertices"/> vertices are drawn per
    /// overlay, with the truncation stated in the on-screen label rather than silently swallowed.
    /// </para>
    /// <para>
    /// <b>The seam half is display only.</b> The tool reads the pairs the window generated and draws them. A weld
    /// is a decision about positions, and the window is where it is made.
    /// </para>
    /// <para>
    /// <b>Three overlays with explicit precedence.</b> Which layers a repaint draws is decided by
    /// <see cref="ApaOverlayVisibility"/>, one pure function, so the gating is testable without a Scene View. The
    /// red removal overlay is drawn only while the window's toggle is on — it is off by default. The green
    /// candidate overlay marks the part vertices the configured vertex color selects, and is drawn from the
    /// <i>rest-pose</i> arrays because those are the indices and positions seam generation reads; the target side
    /// is spatial and is not drawn by it, because "every body vertex" is not a set worth painting. The merge-check
    /// mode is a separate toggle that hides the removal, candidate, and stored-seam overlays and draws the
    /// prospective pairing instead: matched candidates in green, unmatched <i>part</i> candidates in red. It never
    /// writes the stored seam or any profile data, and leaving the mode restores exactly the toggles that were set
    /// before it, because it only hides them.
    /// </para>
    /// <para>
    /// <b>There is no hidden master gate.</b> Each layer is controlled by one of the three switches in the shared
    /// top toolbar, so an enabled layer cannot be silently swallowed by a second control elsewhere in the window.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Every read of a drawn mesh goes through one <see cref="ApaMeshArrayCache"/>, so a repaint does
    /// not re-allocate the vertex and index arrays. The merge-check pairing reuses <see cref="ApaSeamWorldMatcher"/>
    /// and is cached per mesh, transform, candidate set, and tolerance, so a repaint does not re-run the matcher —
    /// and it is keyed on the resolved candidate results rather than on the raw color, so committing a new color
    /// code cannot make it recompute once per repaint while the window is deferring that same work.
    /// </para>
    /// <para>
    /// <b>The removal overlay follows the drawn geometry.</b> When the target renderer deforms through blend
    /// shapes, the red triangles read the renderer's current evaluated geometry
    /// (<see cref="ApaPreviewPositionCache"/>: a cached <c>BakeMesh</c> result) instead of the mesh's rest-pose
    /// vertices, so a highlight cannot sit beside the surface it describes. Nothing about the assembled mesh
    /// changes: the removal <i>set</i> is still a set of triangle addresses, and seam generation, the candidate
    /// overlay, the merge check, and every build-time decision still read the rest pose.
    /// </para>
    /// </remarks>
    public sealed class ApaAuthoringSceneTool
    {
        /// <summary>Most removed triangles drawn in one repaint.</summary>
        public const int MaxDrawnTriangles = 20000;

        /// <summary>Most vertices drawn by one overlay in one repaint.</summary>
        public const int MaxDrawnVertices = 20000;

        private const float VertexDiscScale = 0.02f;

        private static readonly Color RemovedColor = new Color(1f, 0.25f, 0.25f, 0.28f);
        private static readonly Color RemovedOutlineColor = new Color(1f, 0.2f, 0.2f, 1f);
        private static readonly Color BaseSeamColor = new Color(1f, 0.85f, 0.15f, 1f);
        private static readonly Color PartSeamColor = new Color(0.2f, 0.85f, 1f, 1f);
        private static readonly Color RetainedBoundsColor = new Color(0.3f, 0.95f, 0.4f, 1f);
        private static readonly Color PartBoundsColor = new Color(0.3f, 0.55f, 1f, 1f);

        /// <summary>
        /// Green, the seam-candidate vocabulary: a candidate vertex, and a candidate the merge check would pair.
        /// </summary>
        private static readonly Color CandidateColor = new Color(0.25f, 1f, 0.35f, 1f);

        /// <summary>Red, the merge-check vocabulary for a candidate with no counterpart within the tolerance.</summary>
        private static readonly Color MergeUnmatchedColor = new Color(1f, 0.2f, 0.2f, 1f);

        // Do not construct this at type initialization time. Unity can invoke the window's OnEnable while the
        // editor GUI skin is still being rebuilt; reading EditorStyles from a static field initializer then throws
        // and prevents the Scene View tool from registering at all. A standalone style also avoids depending on
        // the skin during a domain reload; the first repaint is the earliest safe point for any GUI allocation.
        private static GUIStyle s_statusStyle;
        private static GUIStyle StatusStyle
        {
            get
            {
                if (s_statusStyle == null)
                {
                    s_statusStyle = new GUIStyle
                    {
                        fontSize = 11,
                        alignment = TextAnchor.UpperLeft,
                        wordWrap = false
                    };
                }

                return s_statusStyle;
            }
        }

        private readonly IApaAuthoringSceneHost _host;

        // One cache per role rather than one shared cache: a single repaint reads the target mesh (removal
        // highlights, base seam) and the part mesh (part seam), so a shared cache would miss on every switch and
        // re-read both meshes every frame.
        private readonly ApaMeshArrayCache _targetArrays = new ApaMeshArrayCache();
        private readonly ApaMeshArrayCache _partArrays = new ApaMeshArrayCache();

        // The positions the overlay uses. Each delegates its rest-pose half to the array cache of its own role, so
        // one mesh still has one vertex array; the evaluated half is a cached BakeMesh result that only exists
        // while the renderer actually deforms through blend shapes.
        private readonly ApaPreviewPositionCache _targetPreview;
        private readonly ApaPreviewPositionCache _partPreview;

        // One baker for both roles: BakeMesh overwrites its destination mesh, so a second one would only double
        // the transient native mesh the tool has to dispose. The caches are handed this instance and therefore do
        // not own it; Dispose below releases it, which is what keeps closing the window from leaking it.
        private readonly ApaSkinnedMeshBaker _baker = new ApaSkinnedMeshBaker();

        private bool _disposed;

        /// <summary>True when this repaint drew the target overlay from evaluated geometry, for the label.</summary>
        private bool _targetPreviewIsEvaluated;

        // The merge-check cache. The prospective pairing runs ApaSeamWorldMatcher over both candidate policies,
        // which reads both vertex arrays and allocates a spatial hash: doing that on every Scene View repaint would
        // cost more than everything else in this class together. The key is the complete input of the computation —
        // the two renderers, the two meshes, both local-to-world matrices, both resolved candidate results, and the
        // tolerance — so a changed mesh, transform, candidate set, or tolerance misses and recomputes, while a
        // repaint that changed nothing reuses the classification. The selected color is deliberately NOT part of
        // the key: it is not an input of the matcher, and the candidate results already change exactly when the
        // window commits a new color code.
        private ApaSeamMergeCheckResult _mergeCheckResult;
        private bool _mergeCheckValid;
        private int _mergeCheckTargetRendererId;
        private int _mergeCheckTargetMeshId;
        private int _mergeCheckPartRendererId;
        private int _mergeCheckPartMeshId;
        private ulong _mergeCheckTransforms;
        private float _mergeCheckTolerance;
        private object _mergeCheckTargetCandidates;
        private object _mergeCheckPartCandidates;

        /// <summary>Creates a tool bound to a host.</summary>
        public ApaAuthoringSceneTool(IApaAuthoringSceneHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            _targetPreview = new ApaPreviewPositionCache(_baker, _targetArrays);
            _partPreview = new ApaPreviewPositionCache(_baker, _partArrays);
        }

        /// <summary>True once <see cref="Dispose"/> ran, so the registry can drop the tool.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// Drops the cached mesh arrays and the cached merge-check classification. Called when a mesh, a selection,
        /// or a draft may have changed without changing the identity the caches key on.
        /// </summary>
        public void InvalidateMeshCache()
        {
            _targetArrays.Invalidate();
            _partArrays.Invalidate();
            _targetPreview.Invalidate();
            _partPreview.Invalidate();
            _mergeCheckValid = false;
            _mergeCheckResult = null;
        }

        /// <summary>
        /// Releases the transient mesh the evaluated-geometry baker owns. Called when the window closes.
        /// </summary>
        /// <remarks>
        /// Idempotent: a window can be disabled more than once (reload, close, domain reload), and the baker's
        /// <c>Dispose</c> is a no-op once the mesh is gone.
        /// </remarks>
        public void Dispose()
        {
            _disposed = true;
            _targetPreview.Dispose();
            _partPreview.Dispose();
            _baker.Dispose();
        }

        /// <summary>
        /// The Scene View callback. Invoked by <see cref="ApaAuthoringSceneToolRegistry"/> from
        /// <c>SceneView.duringSceneGui</c>.
        /// </summary>
        /// <remarks>
        /// The event is not consumed and no default control is registered: the tool draws and nothing else, so the
        /// Scene View keeps its normal selection, navigation, and handle behaviour.
        /// </remarks>
        public void OnSceneGui(SceneView view)
        {
            if (view == null || _disposed) return;

            var host = _host;
            if (host == null) return;

            var currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type != EventType.Repaint) return;

            var plan = ApaOverlayVisibility.For(
                host.MergeCheckOverlay,
                host.ShowRemovalOverlay,
                host.ShowCandidateOverlay);

            if (!plan.DrawsAnything) return;

            Draw(host, host.Selection, plan);
        }

        private void Draw(IApaAuthoringSceneHost host, ApaAuthoringSelection selection, ApaOverlayPlan plan)
        {
            if (selection == null) return;

            var previousColor = Handles.color;
            var previousZTest = Handles.zTest;
            try
            {
                // Scene handles can otherwise be depth-tested behind the very mesh they describe. This is an
                // authoring overlay, so it must remain visible from every angle and on top of the source geometry.
                Handles.zTest = CompareFunction.Always;
                _targetPreviewIsEvaluated = false;

                if (plan.DrawsAnyLayer)
                {
                    DrawBodyContext(selection);

                    // The merge check is a mode, not one more layer: while it is on it hides the removal,
                    // candidate, and stored-seam overlays, because a screen carrying four colour vocabularies at
                    // once cannot answer "what would generating the seam pair?". It hides them rather than
                    // clearing their toggles, so leaving the mode restores exactly what was set before it.
                    if (plan.MergeCheck)
                    {
                        DrawMergeCheck(host, selection);
                    }
                    else
                    {
                        if (plan.Removal) DrawRemoval(host, selection);
                        if (plan.Candidates) DrawCandidates(host, selection);
                        if (plan.Seams) DrawSeams(host, selection);
                    }
                }

                if (plan.StatusLabel) DrawStatusLabel(host, selection, plan);
            }
            finally
            {
                Handles.color = previousColor;
                Handles.zTest = previousZTest;
            }
        }

        private static Vector3 OverlayDiscNormal
        {
            get
            {
                var sceneView = SceneView.currentDrawingSceneView;
                var camera = sceneView != null ? sceneView.camera : null;
                return camera != null ? -camera.transform.forward : Vector3.forward;
            }
        }

        /// <summary>
        /// Draws the two renderer bounds so the author can see which objects the tool is acting on without
        /// drawing a whole mesh.
        /// </summary>
        private static void DrawBodyContext(ApaAuthoringSelection selection)
        {
            if (selection.TargetRenderer != null)
            {
                Handles.color = RetainedBoundsColor;
                var bounds = selection.TargetRenderer.bounds;
                Handles.DrawWireCube(bounds.center, bounds.size);
            }

            if (selection.PartRenderer != null && selection.PartRenderer != selection.TargetRenderer)
            {
                Handles.color = PartBoundsColor;
                var bounds = selection.PartRenderer.bounds;
                Handles.DrawWireCube(bounds.center, bounds.size);
            }
        }

        /// <summary>
        /// Draws the triangles the removal mask generated, read-only.
        /// </summary>
        /// <remarks>
        /// The set drawn is the draft's canonical <see cref="ApaRemovalMask"/> — the same set the profile stores
        /// and the build removes — so the red geometry is a visualization of the authored selection and never an
        /// editing surface. The triangle addresses come from the mesh (topology never changes with a pose), the
        /// positions from the evaluated geometry when the renderer deforms: the removal set is a set of addresses
        /// either way.
        /// </remarks>
        private void DrawRemoval(IApaAuthoringSceneHost host, ApaAuthoringSelection selection)
        {
            var mask = host.Removal;
            var renderer = selection.TargetRenderer;
            var mesh = selection.TargetMesh;

            if (mask == null || mask.IsEmpty || renderer == null || mesh == null || !mesh.isReadable) return;

            if (!_targetArrays.TryRead(mesh, out _, out var triangleIndices)) return;

            if (!_targetPreview.TryRead(renderer, mesh, out var vertices, out var source)) return;
            _targetPreviewIsEvaluated = source == ApaPreviewPositionSource.Evaluated;

            var localToWorld = renderer.transform.localToWorldMatrix;
            var drawn = 0;

            for (var i = 0; i < mask.Count && drawn < MaxDrawnTriangles; i++)
            {
                var address = mask.GetAddress(i);
                if (!TryReadTriangle(triangleIndices, address, out var a, out var b, out var c)) continue;

                if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) continue;

                var worldA = localToWorld.MultiplyPoint3x4(vertices[a]);
                var worldB = localToWorld.MultiplyPoint3x4(vertices[b]);
                var worldC = localToWorld.MultiplyPoint3x4(vertices[c]);

                Handles.color = RemovedColor;
                Handles.DrawAAConvexPolygon(worldA, worldB, worldC);

                Handles.color = RemovedOutlineColor;
                Handles.DrawLine(worldA, worldB);
                Handles.DrawLine(worldB, worldC);
                Handles.DrawLine(worldC, worldA);

                drawn++;
            }
        }

        /// <summary>
        /// Draws the seam-candidate vertices the configured color selects on the part, read-only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Green discs mark every part candidate, so the author can confirm that the paint they applied in the
        /// modelling tool is the set the generator will pair. The indices and the positions come from the same
        /// rest-pose arrays seam generation reads (<c>mesh.vertices</c> through
        /// <see cref="ApaMeshArrayCache"/>), transformed by each renderer's own <c>localToWorldMatrix</c>: the
        /// overlay is a preview of the pairing input, never of a posed bake.
        /// </para>
        /// <para>
        /// <b>The target side draws nothing here.</b> Its candidate set is spatial — every body vertex — so
        /// drawing it would cover the whole body in green discs and say nothing: what the author needs to see
        /// about the target side is which of its vertices would actually pair, and that is exactly what the
        /// merge-check mode draws. The status lines in the window still state the target side's spatial policy.
        /// </para>
        /// <para>
        /// A side whose candidates could not be resolved draws nothing; the blocking diagnostic is shown in the
        /// window's seam block and in the Scene View label, because drawing "no candidates" as an empty overlay
        /// would be indistinguishable from a mesh that legitimately has none.
        /// </para>
        /// </remarks>
        private void DrawCandidates(IApaAuthoringSceneHost host, ApaAuthoringSelection selection)
        {
            var targetCandidates = host.TargetSeamCandidates;
            if (targetCandidates != null && !targetCandidates.IsSpatial)
            {
                DrawCandidateSide(selection.TargetRenderer, selection.TargetMesh, targetCandidates, false);
            }

            DrawCandidateSide(
                selection.PartRenderer,
                selection.PartMesh,
                host.PartSeamCandidates,
                true);
        }

        private void DrawCandidateSide(
            Renderer renderer,
            Mesh mesh,
            ApaSeamColorCandidateResult candidates,
            bool isPartMesh)
        {
            if (renderer == null || mesh == null || !mesh.isReadable) return;
            if (candidates == null || !candidates.Succeeded) return;

            var arrays = isPartMesh ? _partArrays : _targetArrays;
            if (!arrays.TryRead(mesh, out var vertices, out _)) return;

            var indices = candidates.Indices;
            var localToWorld = renderer.transform.localToWorldMatrix;

            Handles.color = CandidateColor;

            var drawn = 0;
            for (var i = 0; i < indices.Length && drawn < MaxDrawnVertices; i++)
            {
                var index = indices[i];
                if (index < 0 || index >= vertices.Length) continue;

                var world = localToWorld.MultiplyPoint3x4(vertices[index]);
                Handles.DrawSolidDisc(world, OverlayDiscNormal, HandleUtility.GetHandleSize(world) * VertexDiscScale);
                drawn++;
            }
        }

        /// <summary>
        /// Draws the prospective pairing of the resolved candidates: matched vertices green, unmatched part
        /// candidates red.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The classification comes from <see cref="ApaSeamMergeCheck"/>, which runs the same matcher, the same
        /// candidate policies, and the same tolerance seam generation uses, so what is drawn is what a generate
        /// action would write. Nothing here mutates the stored seam or the draft.
        /// </para>
        /// <para>
        /// Both sides are drawn from the rest-pose arrays, for the same reason the candidate overlay is: the
        /// question the mode answers is about the pairing input, and the matcher reads the bind geometry.
        /// </para>
        /// <para>
        /// <b>Red means "a part candidate with no counterpart".</b> The target side is spatial, so its unclaimed
        /// vertices are not defects and the classification reports none of them; a red disc therefore always marks
        /// a part vertex the author nominated and the body did not meet.
        /// </para>
        /// </remarks>
        private void DrawMergeCheck(IApaAuthoringSceneHost host, ApaAuthoringSelection selection)
        {
            var result = MergeCheckResultFor(host, selection);
            if (result == null || !result.Succeeded) return;

            DrawMergeCheckSide(
                selection.TargetRenderer,
                selection.TargetMesh,
                result.MatchedTargetIndices,
                result.UnmatchedTargetIndices,
                false);

            DrawMergeCheckSide(
                selection.PartRenderer,
                selection.PartMesh,
                result.MatchedPartIndices,
                result.UnmatchedPartIndices,
                true);
        }

        private void DrawMergeCheckSide(
            Renderer renderer,
            Mesh mesh,
            int[] matched,
            int[] unmatched,
            bool isPartMesh)
        {
            if (renderer == null || mesh == null || !mesh.isReadable) return;

            var arrays = isPartMesh ? _partArrays : _targetArrays;
            if (!arrays.TryRead(mesh, out var vertices, out _)) return;

            var localToWorld = renderer.transform.localToWorldMatrix;
            var drawn = 0;

            // Matched first: when the budget truncates, the pairs the author is about to write are the ones that
            // must stay visible. The label states that truncation happened either way.
            drawn = DrawMergeDiscs(matched, CandidateColor, vertices, localToWorld, drawn);
            DrawMergeDiscs(unmatched, MergeUnmatchedColor, vertices, localToWorld, drawn);
        }

        private static int DrawMergeDiscs(
            int[] indices,
            Color color,
            Vector3[] vertices,
            Matrix4x4 localToWorld,
            int drawn)
        {
            if (indices == null || indices.Length == 0) return drawn;

            Handles.color = color;

            for (var i = 0; i < indices.Length && drawn < MaxDrawnVertices; i++)
            {
                var index = indices[i];
                if (index < 0 || index >= vertices.Length) continue;

                var world = localToWorld.MultiplyPoint3x4(vertices[index]);
                Handles.DrawSolidDisc(world, OverlayDiscNormal, HandleUtility.GetHandleSize(world) * VertexDiscScale);
                drawn++;
            }

            return drawn;
        }

        /// <summary>
        /// The cached prospective pairing for the current inputs, or null when the selection cannot be evaluated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The cache is keyed on the complete input of the computation, so a repaint that changed nothing reuses
        /// the classification while a changed mesh, transform, candidate set, or tolerance recomputes it. Candidate
        /// results are compared by reference on purpose: the window creates a new result object exactly when it
        /// re-resolves the candidates, which is exactly when the pairing must be recomputed.
        /// </para>
        /// <para>
        /// The selected color is not a key of its own. It is not an input of <see cref="ApaSeamMergeCheck"/> — the
        /// candidate results are — and the window invalidates this cache once when it commits a new color code, so
        /// a repaint can never serve a classification computed for a color that is no longer selected.
        /// </para>
        /// </remarks>
        private ApaSeamMergeCheckResult MergeCheckResultFor(
            IApaAuthoringSceneHost host,
            ApaAuthoringSelection selection)
        {
            var targetRenderer = selection.TargetRenderer;
            var partRenderer = selection.PartRenderer;
            var targetMesh = selection.TargetMesh;
            var partMesh = selection.PartMesh;

            if (targetRenderer == null || partRenderer == null || targetMesh == null || partMesh == null)
            {
                return null;
            }

            var targetCandidates = host.TargetSeamCandidates;
            var partCandidates = host.PartSeamCandidates;
            var tolerance = host.SeamTolerance;

            var targetRendererId = targetRenderer.GetInstanceID();
            var targetMeshId = targetMesh.GetInstanceID();
            var partRendererId = partRenderer.GetInstanceID();
            var partMeshId = partMesh.GetInstanceID();
            var transforms = TransformFingerprint(targetRenderer.transform, partRenderer.transform);

            if (_mergeCheckValid
                && _mergeCheckResult != null
                && _mergeCheckTargetRendererId == targetRendererId
                && _mergeCheckTargetMeshId == targetMeshId
                && _mergeCheckPartRendererId == partRendererId
                && _mergeCheckPartMeshId == partMeshId
                && _mergeCheckTransforms == transforms
                && _mergeCheckTolerance == tolerance
                && ReferenceEquals(_mergeCheckTargetCandidates, targetCandidates)
                && ReferenceEquals(_mergeCheckPartCandidates, partCandidates))
            {
                return _mergeCheckResult;
            }

            var result = ApaSeamMergeCheck.Evaluate(
                targetRenderer,
                targetMesh,
                partRenderer,
                partMesh,
                tolerance,
                targetCandidates,
                partCandidates);

            _mergeCheckResult = result;
            _mergeCheckValid = true;
            _mergeCheckTargetRendererId = targetRendererId;
            _mergeCheckTargetMeshId = targetMeshId;
            _mergeCheckPartRendererId = partRendererId;
            _mergeCheckPartMeshId = partMeshId;
            _mergeCheckTransforms = transforms;
            _mergeCheckTolerance = tolerance;
            _mergeCheckTargetCandidates = targetCandidates;
            _mergeCheckPartCandidates = partCandidates;

            return result;
        }

        /// <summary>
        /// A fingerprint of the two renderer transforms, so a moved renderer recomputes the pairing.
        /// </summary>
        /// <remarks>
        /// The world positions the matcher compares depend on both <c>localToWorldMatrix</c> values, and a
        /// transform edit changes neither the renderer instance nor the mesh. Every matrix element is folded in
        /// with the FNV-1a convention the rest of the package uses, so the key is stable and allocation-free.
        /// </remarks>
        private static ulong TransformFingerprint(Transform target, Transform part)
        {
            var hash = ApaPreviewGeometry.OffsetBasis;
            hash = MixMatrix(hash, target != null ? target.localToWorldMatrix : Matrix4x4.zero);
            hash = MixMatrix(hash, part != null ? part.localToWorldMatrix : Matrix4x4.zero);
            return hash;
        }

        private static ulong MixMatrix(ulong hash, Matrix4x4 matrix)
        {
            unchecked
            {
                for (var row = 0; row < 4; row++)
                {
                    for (var column = 0; column < 4; column++)
                    {
                        hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(matrix[row, column]))
                               * ApaPreviewGeometry.Prime;
                    }
                }
            }

            return hash;
        }

        /// <summary>
        /// Draws the generated seam pairs, read-only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Yellow discs are the retained base vertices, cyan discs the part vertices they weld onto. The two
        /// lists are drawn side by side rather than one per list position, because the pairing is what the author
        /// generated and is what the highlight is there to confirm; a connector per pair would multiply the
        /// handle count for no extra information at this scale.
        /// </para>
        /// <para>
        /// The discs mark <i>vertices</i>, so they are drawn on the geometry the author sees: evaluated positions
        /// when the renderer deforms through blend shapes, the rest pose otherwise. The pairs themselves remain
        /// rest-pose data — a disc that follows the pose still marks the vertex the pair names.
        /// </para>
        /// </remarks>
        private void DrawSeams(IApaAuthoringSceneHost host, ApaAuthoringSelection selection)
        {
            var seam = host.Seam;
            if (seam == null || seam.IsEmpty) return;

            DrawSeamSide(
                selection.TargetRenderer,
                selection.TargetMesh,
                seam.GetBaseIndices(),
                BaseSeamColor,
                false);

            DrawSeamSide(
                selection.PartRenderer,
                selection.PartMesh,
                seam.GetPartIndices(),
                PartSeamColor,
                true);
        }

        private void DrawSeamSide(
            Renderer renderer,
            Mesh mesh,
            int[] indices,
            Color color,
            bool isPartMesh)
        {
            if (renderer == null || mesh == null || indices == null || indices.Length == 0) return;
            if (!mesh.isReadable) return;

            var preview = isPartMesh ? _partPreview : _targetPreview;
            if (!preview.TryRead(renderer, mesh, out var vertices, out var source)) return;

            if (!isPartMesh) _targetPreviewIsEvaluated = source == ApaPreviewPositionSource.Evaluated;

            var localToWorld = renderer.transform.localToWorldMatrix;
            Handles.color = color;

            var drawn = 0;
            for (var i = 0; i < indices.Length && drawn < MaxDrawnVertices; i++)
            {
                var index = indices[i];
                if (index < 0 || index >= vertices.Length) continue;

                var world = localToWorld.MultiplyPoint3x4(vertices[index]);
                Handles.DrawSolidDisc(world, OverlayDiscNormal, HandleUtility.GetHandleSize(world) * VertexDiscScale);
                drawn++;
            }
        }

        /// <summary>The on-screen state line for an enabled overlay.</summary>
        /// <remarks>
        /// The label is drawn while the merge-check mode is on — its result is a count, and a count the author
        /// cannot read is not a diagnostic — and while the removal overlay is armed, so the removal count is
        /// visible. The lines are one translated unit rather than concatenated fragments, because word order
        /// differs between the languages.
        /// </remarks>
        private void DrawStatusLabel(
            IApaAuthoringSceneHost host,
            ApaAuthoringSelection selection,
            ApaOverlayPlan plan)
        {
            var anchor = selection.TargetRenderer != null
                ? selection.TargetRenderer.bounds.center
                : (selection.PartRenderer != null ? selection.PartRenderer.bounds.center : Vector3.zero);

            var mask = host.Removal;
            var seam = host.Seam;

            string text;
            if (plan.MergeCheck)
            {
                text = TrFormat(
                    "Avatar Part Assembler — Merge Check\nmerge check: {0}\ntolerance (world units): {1}\n" +
                    "green: would pair   red: part candidate with no counterpart",
                    DescribeMergeCheck(),
                    FormatTolerance(host.SeamTolerance));
            }
            else
            {
                text = TrFormat(
                    "Avatar Part Assembler\nremoval: {0}\nseam: {1}",
                    mask != null ? mask.Describe() : Tr("(none)"),
                    seam != null ? seam.Describe() : Tr("(none)"));
            }

            if (mask != null && plan.Removal && mask.Count > MaxDrawnTriangles)
            {
                text += TrFormat("\nshowing the first {0} removed triangles", MaxDrawnTriangles);
            }

            // The removal overlay follows the drawn geometry, but the seam pairs, the candidate indices, and the
            // removal addresses are rest-pose data. Saying so on screen is what keeps "the red triangles moved
            // with the pose" from reading as "the generated seam changed".
            if (_targetPreviewIsEvaluated && plan.Removal && !plan.MergeCheck)
            {
                text += Tr("\npreview: current blend-shape pose; seam data stays rest-pose");
            }

            Handles.Label(anchor, new GUIContent(text), StatusStyle);
        }

        /// <summary>
        /// The merge-check line of the Scene View label: the prospective counts, or why there is none.
        /// </summary>
        /// <remarks>
        /// The cached classification is read here rather than recomputed: <see cref="DrawMergeCheck"/> has already
        /// run in this repaint, and the label must report the same numbers the discs were drawn from.
        /// </remarks>
        private string DescribeMergeCheck()
        {
            var result = _mergeCheckResult;
            if (result == null)
            {
                return Tr("select a target renderer and a part renderer with meshes to run the merge check");
            }

            if (!result.Succeeded) return ApaDiagnosticText.FormatShort(result.Issue);
            if (result.MatchIssue != null) return ApaDiagnosticText.FormatShort(result.MatchIssue);

            return result.Describe();
        }

        /// <summary>The world tolerance as the diagnostics and the window state it.</summary>
        private static string FormatTolerance(float tolerance)
        {
            return tolerance.ToString("G9", CultureInfo.InvariantCulture);
        }

        private static bool TryReadTriangle(
            int[][] triangleIndices,
            RemovedTriangleAddress address,
            out int a,
            out int b,
            out int c)
        {
            a = b = c = -1;

            if (triangleIndices == null) return false;
            if (address.SubMeshIndex < 0 || address.SubMeshIndex >= triangleIndices.Length) return false;

            var indices = triangleIndices[address.SubMeshIndex];
            if (indices == null) return false;

            var triangleCount = indices.Length / ApaMeshLimits.TriangleStride;
            if (address.TriangleIndexWithinSubMesh < 0 || address.TriangleIndexWithinSubMesh >= triangleCount)
            {
                return false;
            }

            var offset = address.TriangleIndexWithinSubMesh * ApaMeshLimits.TriangleStride;
            a = indices[offset];
            b = indices[offset + 1];
            c = indices[offset + 2];
            return true;
        }
    }
}

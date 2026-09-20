using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>What the Scene View tool is currently selecting.</summary>
    /// <remarks>
    /// The two seam-vertex modes are gone (M10). A seam vertex is no longer something the author clicks: the
    /// pairing is generated once from world-coincident positions, and a per-vertex picking mode could not express
    /// which base vertex a part vertex belongs to anyway. The seam is still <i>drawn</i> — read-only — so the
    /// author can see what was generated.
    /// </remarks>
    public enum ApaSceneToolMode
    {
        /// <summary>The tool is off; the Scene View behaves normally.</summary>
        Off = 0,

        /// <summary>Clicking toggles the body triangle under the cursor in the removal set.</summary>
        RemovalTriangles = 1
    }

    /// <summary>
    /// The window-side surface the Scene View tool drives.
    /// </summary>
    /// <remarks>
    /// The tool talks to this interface rather than to the window type, so the picking and drawing behaviour can
    /// be reviewed without reading window code, and so the mutations it triggers all go through the window's
    /// undo-aware methods instead of being applied to the draft directly from a scene callback.
    /// </remarks>
    public interface IApaAuthoringSceneHost
    {
        /// <summary>The current selection.</summary>
        ApaAuthoringSelection Selection { get; }

        /// <summary>The current removal mask.</summary>
        ApaRemovalMask Removal { get; }

        /// <summary>The current seam selection. Read-only for the tool: the seam is generated, never picked.</summary>
        ApaSeamSelection Seam { get; }

        /// <summary>The active tool mode.</summary>
        ApaSceneToolMode ToolMode { get; }

        /// <summary>True when highlights are drawn.</summary>
        bool ShowHighlights { get; }

        /// <summary>
        /// Adds the triangle when it is absent from the removal set and removes it when it is present, recording
        /// undo. This is what a plain click does, and it is a real toggle: clicking a removed triangle puts it
        /// back, which is what the window and the on-screen label promise.
        /// </summary>
        void ToggleRemovalTriangle(RemovedTriangleAddress address);

        /// <summary>Removes one body triangle from the removal set, recording undo.</summary>
        void RemoveRemovalTriangle(RemovedTriangleAddress address);

        /// <summary>Repaints the authoring window after a change made from the Scene View.</summary>
        void RepaintAuthoringWindow();
    }

    /// <summary>
    /// Scene View picking and highlighting for the removal mask, and read-only seam highlighting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tool is modal: while a mode is active it registers the Scene View's default control, which captures
    /// left clicks and stops the Scene View from changing the selection underneath the author. Alt-drag and
    /// right-drag camera navigation keep working. Switching the mode off restores normal behaviour entirely.
    /// </para>
    /// <para>
    /// Highlighting follows the specification's colour vocabulary — red for geometry that will be removed,
    /// yellow for the base seam, cyan for the part seam — and is bounded: at most
    /// <see cref="MaxDrawnTriangles"/> triangles and <see cref="MaxDrawnVertices"/> vertices are drawn, with the
    /// truncation stated in the on-screen label rather than silently swallowed.
    /// </para>
    /// <para>
    /// <b>The seam half is display only (M10).</b> There is no seam picking mode, so no seam vertex is ever
    /// selected, hovered, or mutated from the Scene View: the tool reads the pairs the window generated and draws
    /// them. A weld is a decision about positions, and the window is where it is made.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Every read of the picked mesh goes through one <see cref="ApaMeshArrayCache"/>, so a mouse
    /// move, a drag, or a repaint does not re-allocate the vertex and index arrays. Hover picking is additionally
    /// skipped while the author is navigating the Scene View (alt held, or a non-left drag) and is bounded by
    /// conservative budgets; a click is one-shot and keeps the larger budget, because that is where correctness
    /// matters.
    /// </para>
    /// <para>
    /// <b>The removal overlay follows the drawn geometry.</b> When the target renderer deforms through blend
    /// shapes, the red triangles and the hover/click picks read the renderer's current evaluated geometry
    /// (<see cref="ApaPreviewPositionCache"/>: a cached <c>BakeMesh</c> result) instead of the mesh's rest-pose
    /// vertices, so a highlight cannot sit beside the surface it describes. Nothing about the assembled mesh
    /// changes: the removal <i>set</i> is still a set of triangle addresses, and seam generation and every
    /// build-time decision still read the rest pose.
    /// </para>
    /// </remarks>
    public sealed class ApaAuthoringSceneTool
    {
        /// <summary>Most removed triangles drawn in one repaint.</summary>
        public const int MaxDrawnTriangles = 20000;

        /// <summary>Most seam vertices drawn in one repaint.</summary>
        public const int MaxDrawnVertices = 20000;

        private const float VertexDiscScale = 0.02f;

        private static readonly Color RemovedColor = new Color(1f, 0.25f, 0.25f, 0.28f);
        private static readonly Color RemovedOutlineColor = new Color(1f, 0.2f, 0.2f, 1f);
        private static readonly Color HoverColor = new Color(1f, 0.65f, 0.1f, 1f);
        private static readonly Color BaseSeamColor = new Color(1f, 0.85f, 0.15f, 1f);
        private static readonly Color PartSeamColor = new Color(0.2f, 0.85f, 1f, 1f);
        private static readonly Color RetainedBoundsColor = new Color(0.3f, 0.95f, 0.4f, 1f);
        private static readonly Color PartBoundsColor = new Color(0.3f, 0.55f, 1f, 1f);

        /// <summary>Built once: a GUI style allocated per repaint would leak one allocation per frame.</summary>
        private static readonly GUIStyle StatusStyle = new GUIStyle(EditorStyles.helpBox)
        {
            fontSize = 11,
            alignment = TextAnchor.UpperLeft,
            wordWrap = false
        };

        private readonly IApaAuthoringSceneHost _host;

        // One cache per role rather than one shared cache: a single repaint reads the target mesh (removal
        // highlights, base seam) and the part mesh (part seam), so a shared cache would miss on every switch and
        // re-read both meshes every frame.
        private readonly ApaMeshArrayCache _targetArrays = new ApaMeshArrayCache();
        private readonly ApaMeshArrayCache _partArrays = new ApaMeshArrayCache();

        // The positions the overlay and the picks use. Each delegates its rest-pose half to the array cache of
        // its own role, so one mesh still has one vertex array; the evaluated half is a cached BakeMesh result
        // that only exists while the renderer actually deforms through blend shapes.
        private readonly ApaPreviewPositionCache _targetPreview;
        private readonly ApaPreviewPositionCache _partPreview;

        // One baker for both roles: BakeMesh overwrites its destination mesh, so a second one would only double
        // the transient native mesh the tool has to dispose. The caches are handed this instance and therefore do
        // not own it; Dispose below releases it, which is what keeps closing the window from leaking it.
        private readonly ApaSkinnedMeshBaker _baker = new ApaSkinnedMeshBaker();

        private RemovedTriangleAddress _hoveredTriangle = RemovedTriangleAddress.None;

        /// <summary>True when this repaint drew the target overlay from evaluated geometry, for the label.</summary>
        private bool _targetPreviewIsEvaluated;

        /// <summary>Creates a tool bound to a host.</summary>
        public ApaAuthoringSceneTool(IApaAuthoringSceneHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            _targetPreview = new ApaPreviewPositionCache(_baker, _targetArrays);
            _partPreview = new ApaPreviewPositionCache(_baker, _partArrays);
        }

        /// <summary>
        /// Drops the cached mesh arrays. Called when a mesh may have changed without changing its identity, such
        /// as after an undo.
        /// </summary>
        public void InvalidateMeshCache()
        {
            _targetArrays.Invalidate();
            _partArrays.Invalidate();
            _targetPreview.Invalidate();
            _partPreview.Invalidate();
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
            _targetPreview.Dispose();
            _partPreview.Dispose();
            _baker.Dispose();
        }

        /// <summary>The preview cache that owns a renderer role: the part mesh has its own, every other mesh shares one.</summary>
        private ApaPreviewPositionCache PreviewFor(bool isPartMesh)
        {
            return isPartMesh ? _partPreview : _targetPreview;
        }

        /// <summary>
        /// The Scene View callback. Must be invoked from <c>SceneView.duringSceneGui</c>.
        /// </summary>
        public void OnSceneGui(SceneView view)
        {
            var host = _host;
            if (view == null || host == null) return;

            var mode = host.ToolMode;
            var selection = host.Selection;
            var currentEvent = Event.current;

            if (mode != ApaSceneToolMode.Off && currentEvent.type == EventType.Layout)
            {
                // Registering the default control is what stops the Scene View from clearing the selection on the
                // clicks this tool consumes. Unity requires it during Layout.
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            }

            if (mode == ApaSceneToolMode.Off)
            {
                ResetHover();
            }
            else if (IsHoverEvent(currentEvent))
            {
                UpdateHover(selection, currentEvent.mousePosition);
            }
            else if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && !currentEvent.alt)
            {
                if (HandleClick(host, selection, currentEvent))
                {
                    currentEvent.Use();
                    host.RepaintAuthoringWindow();
                }
            }

            if (host.ShowHighlights && currentEvent.type == EventType.Repaint)
            {
                Draw(host, selection, view);
            }
        }

        /// <summary>
        /// True when an event should update the hover highlight.
        /// </summary>
        /// <remarks>
        /// Alt-drag is camera orbit and right-drag is camera pan; both move the mouse over the mesh hundreds of
        /// times without the author pointing at anything, so neither pays for a pick. A left drag on a handle is
        /// an interaction too, so hover follows only a plain mouse move or a left-button drag, mirroring the
        /// click path's alt exclusion.
        /// </remarks>
        private static bool IsHoverEvent(Event currentEvent)
        {
            if (currentEvent.alt) return false;
            if (currentEvent.type == EventType.MouseMove) return true;
            return currentEvent.type == EventType.MouseDrag && currentEvent.button == 0;
        }

        private void ResetHover()
        {
            _hoveredTriangle = RemovedTriangleAddress.None;
        }

        private void UpdateHover(ApaAuthoringSelection selection, Vector2 mousePosition)
        {
            var previousTriangle = _hoveredTriangle;

            ResetHover();

            if (_host.ToolMode != ApaSceneToolMode.RemovalTriangles) return;

            var mesh = selection != null ? selection.TargetMesh : null;
            if (mesh == null || !_targetArrays.TryRead(mesh, out _, out var triangleIndices)) return;
            if (ApaMeshArrayCache.CountTriangles(triangleIndices) > ApaScenePicking.HoverTriangleBudget) return;

            // The pick tests the same positions the overlay draws, so a hover highlight lands on the triangle the
            // author sees under the cursor even while a blend shape deforms the body.
            if (!_targetPreview.TryRead(selection.TargetRenderer, mesh, out var vertices, out _)) return;

            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            if (ApaScenePicking.TryPickTriangle(
                    selection.TargetRenderer, vertices, triangleIndices, ray, out var address, out _))
            {
                _hoveredTriangle = address;
            }

            if (_hoveredTriangle != previousTriangle) SceneView.RepaintAll();
        }

        private bool HandleClick(
            IApaAuthoringSceneHost host,
            ApaAuthoringSelection selection,
            Event currentEvent)
        {
            if (selection == null) return false;
            if (host.ToolMode != ApaSceneToolMode.RemovalTriangles) return false;

            var mesh = selection.TargetMesh;
            if (mesh == null) return false;
            if (!_targetArrays.TryRead(mesh, out _, out var triangleIndices)) return false;
            if (ApaMeshArrayCache.CountTriangles(triangleIndices) > ApaScenePicking.ClickTriangleBudget) return false;

            if (!_targetPreview.TryRead(selection.TargetRenderer, mesh, out var vertices, out _)) return false;

            var ray = HandleUtility.GUIPointToWorldRay(currentEvent.mousePosition);
            if (!ApaScenePicking.TryPickTriangle(
                    selection.TargetRenderer, vertices, triangleIndices, ray, out var address, out _))
            {
                return false;
            }

            var remove = currentEvent.shift || currentEvent.control || currentEvent.command;
            var mask = host.Removal;
            if (remove)
            {
                // Shift or control removes; removing something that is not there is a no-op, so the click is
                // left to the Scene View rather than consumed.
                if (mask == null || !mask.Contains(address)) return false;
                host.RemoveRemovalTriangle(address);
            }
            else
            {
                // A plain click toggles, so a mis-click is undone by clicking again.
                host.ToggleRemovalTriangle(address);
            }

            _hoveredTriangle = address;
            return true;
        }

        private void Draw(
            IApaAuthoringSceneHost host,
            ApaAuthoringSelection selection,
            SceneView view)
        {
            if (selection == null) return;

            var previousColor = Handles.color;
            try
            {
                _targetPreviewIsEvaluated = false;

                DrawBodyContext(selection);
                DrawRemoval(host, selection);
                DrawSeams(host, selection);
                DrawHover();
                DrawStatusLabel(host, selection, view);
            }
            finally
            {
                Handles.color = previousColor;
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

        private void DrawRemoval(IApaAuthoringSceneHost host, ApaAuthoringSelection selection)
        {
            var mask = host.Removal;
            var renderer = selection.TargetRenderer;
            var mesh = selection.TargetMesh;

            if (mask == null || mask.IsEmpty || renderer == null || mesh == null || !mesh.isReadable) return;

            if (!_targetArrays.TryRead(mesh, out _, out var triangleIndices)) return;

            // The triangle addresses come from the mesh (topology never changes with a pose), the positions from
            // the evaluated geometry when the renderer deforms: the removal set is a set of addresses either way.
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

            var preview = PreviewFor(isPartMesh);
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
                Handles.DrawSolidDisc(world, Vector3.up, HandleUtility.GetHandleSize(world) * VertexDiscScale);
                drawn++;
            }
        }

        private void DrawHover()
        {
            if (_hoveredTriangle == RemovedTriangleAddress.None) return;
            if (_host.ToolMode != ApaSceneToolMode.RemovalTriangles) return;

            var selection = _host.Selection;
            var mesh = selection != null ? selection.TargetMesh : null;
            var renderer = selection != null ? selection.TargetRenderer : null;

            if (mesh != null && renderer != null && mesh.isReadable
                && _targetArrays.TryRead(mesh, out _, out var triangleIndices)
                && _targetPreview.TryRead(renderer, mesh, out var vertices, out _)
                && TryReadTriangle(triangleIndices, _hoveredTriangle, out var a, out var b, out var c)
                && a < vertices.Length && b < vertices.Length && c < vertices.Length)
            {
                var localToWorld = renderer.transform.localToWorldMatrix;
                var worldA = localToWorld.MultiplyPoint3x4(vertices[a]);
                var worldB = localToWorld.MultiplyPoint3x4(vertices[b]);
                var worldC = localToWorld.MultiplyPoint3x4(vertices[c]);

                Handles.color = HoverColor;
                Handles.DrawLine(worldA, worldB);
                Handles.DrawLine(worldB, worldC);
                Handles.DrawLine(worldC, worldA);
            }
        }

        private void DrawStatusLabel(
            IApaAuthoringSceneHost host,
            ApaAuthoringSelection selection,
            SceneView view)
        {
            var mode = host.ToolMode;
            if (mode == ApaSceneToolMode.Off) return;

            var anchor = selection.TargetRenderer != null
                ? selection.TargetRenderer.bounds.center
                : (selection.PartRenderer != null ? selection.PartRenderer.bounds.center : Vector3.zero);

            var mask = host.Removal;
            var seam = host.Seam;

            // The four lines are one translated unit rather than four concatenated fragments: word order differs
            // between the languages, so the whole label is the key and the dynamic values are its placeholders.
            var text = TrFormat(
                "Avatar Part Assembler — {0}\nremoval: {1}\nseam: {2}\nleft click: toggle   shift/ctrl click: remove",
                Localization.ApaLocalization.DisplayName(mode),
                mask != null ? mask.Describe() : Tr("(none)"),
                seam != null ? seam.Describe() : Tr("(none)"));

            if (mask != null && mask.Count > MaxDrawnTriangles)
            {
                text += TrFormat("\nshowing the first {0} removed triangles", MaxDrawnTriangles);
            }

            // The overlay follows the drawn geometry, but the seam pairs and the removal addresses are rest-pose
            // data. Saying so on screen is what keeps "the red triangles moved with the pose" from reading as
            // "the generated seam changed".
            if (_targetPreviewIsEvaluated)
            {
                text += Tr("\npreview: current blend-shape pose; seam data stays rest-pose");
            }

            var style = StatusStyle;

            Handles.Label(anchor, new GUIContent(text), style);
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

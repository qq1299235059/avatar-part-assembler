using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>One part's contribution to the seam/removal debug drawing.</summary>
    public sealed class ApaPreviewDebugPart
    {
        /// <summary>Stable part id.</summary>
        public string PartId { get; }

        /// <summary>The captured part mesh.</summary>
        public MeshSnapshot Mesh { get; }

        /// <summary>The captured transforms that place the part's data into the target's space.</summary>
        public SpaceTransforms Transforms { get; }

        /// <summary>Creates a debug part.</summary>
        public ApaPreviewDebugPart(string partId, MeshSnapshot mesh, SpaceTransforms transforms)
        {
            PartId = partId ?? string.Empty;
            Mesh = mesh;
            Transforms = transforms;
        }
    }

    /// <summary>
    /// The read-only debug view of one preview node: what was removed, where the seam landed, and what the
    /// diagnostics say.
    /// </summary>
    /// <remarks>
    /// The overlay draws from captured snapshots and the immutable plan, never from live mesh reads, so drawing
    /// cannot race a reimport or allocate. Nothing in this type can write to any object.
    /// </remarks>
    public sealed class ApaPreviewDebugData
    {
        /// <summary>The original body renderer this data describes.</summary>
        public Renderer Original { get; }

        /// <summary>The plan that was built, or null when nothing was built.</summary>
        public MeshAssemblyPlan Plan { get; }

        /// <summary>The captured body mesh.</summary>
        public MeshSnapshot BaseMesh { get; }

        /// <summary>The captured body transforms.</summary>
        public SpaceTransforms BaseTransforms { get; }

        /// <summary>The captured parts, in canonical order.</summary>
        public IReadOnlyList<ApaPreviewDebugPart> Parts { get; }

        /// <summary>Diagnostics of the build this node produced.</summary>
        public IReadOnlyList<ValidationIssue> Issues { get; }

        /// <summary>The resolved bone table, for bone-mapping diagnostics.</summary>
        public ApaPreviewBoneMap Bones { get; }

        /// <summary>The fingerprint of the inputs this node was built from.</summary>
        public string Fingerprint { get; }

        private ApaPreviewDebugData(
            Renderer original,
            MeshAssemblyPlan plan,
            MeshSnapshot baseMesh,
            SpaceTransforms baseTransforms,
            IReadOnlyList<ApaPreviewDebugPart> parts,
            IReadOnlyList<ValidationIssue> issues,
            ApaPreviewBoneMap bones,
            string fingerprint)
        {
            Original = original;
            Plan = plan;
            BaseMesh = baseMesh;
            BaseTransforms = baseTransforms;
            Parts = parts;
            Issues = issues;
            Bones = bones;
            Fingerprint = fingerprint ?? string.Empty;
        }

        /// <summary>Builds debug data from a discovery request and the plan a node built.</summary>
        public static ApaPreviewDebugData Create(
            ApaPreviewRequest request,
            MeshAssemblyPlan plan,
            ValidationResult issues,
            ApaPreviewBoneMap bones)
        {
            if (request == null) return null;

            var parts = new List<ApaPreviewDebugPart>();
            var context = request.Context;
            if (context != null && context.Parts != null)
            {
                for (var i = 0; i < context.Parts.Count; i++)
                {
                    var part = context.Parts[i];
                    if (part == null) continue;
                    parts.Add(new ApaPreviewDebugPart(part.PartId, part.Mesh, part.Transforms));
                }
            }

            var issueList = new List<ValidationIssue>();
            if (issues != null)
            {
                for (var i = 0; i < issues.Issues.Count; i++) issueList.Add(issues.Issues[i]);
            }

            var baseMesh = context != null && context.Base != null ? context.Base.Mesh : null;
            var baseTransforms = context != null && context.Base != null ? context.Base.Transforms : default;

            return new ApaPreviewDebugData(
                request.TargetRenderer,
                plan,
                baseMesh,
                baseTransforms,
                parts,
                issueList,
                bones,
                request.Fingerprint);
        }
    }

    /// <summary>
    /// An off-by-default Scene View overlay that draws seam and removed-region diagnostics for the current
    /// preview.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overlay implements the seam/removal half of the Scene View debug display described by section 29:
    /// removed base triangles are drawn in red, seam correspondences in yellow. Nothing else in the preview
    /// changes, and no asset, mesh, material, or scene object is written to — every value drawn comes from an
    /// immutable snapshot captured by discovery.
    /// </para>
    /// <para>
    /// It is off by default and stays off across sessions: the switch is a
    /// <c>TogglablePreviewNode</c> created with an initial state of <c>false</c> and a qualified name, so NDMF
    /// persists the user's choice and lists it under <i>Tools/NDM Framework/Configure Previews</i>.
    /// </para>
    /// <para>
    /// Drawing is bounded. A pathological avatar with tens of thousands of removed triangles would otherwise cost
    /// more than the preview it describes, so each primitive category has a cap and the overlay reports how much
    /// it left out.
    /// </para>
    /// </remarks>
    public static class ApaPreviewDebugOverlay
    {
        /// <summary>Maximum removed triangles drawn per renderer.</summary>
        public const int MaxRemovedTrianglesDrawn = 4000;

        /// <summary>Maximum seam correspondences drawn per renderer.</summary>
        public const int MaxSeamMatchesDrawn = 4000;

        /// <summary>Maximum diagnostic lines drawn in one label.</summary>
        public const int MaxLabelLines = 8;

        private static readonly Color s_removedColor = new Color(1f, 0.25f, 0.2f, 1f);
        private static readonly Color s_seamColor = new Color(1f, 0.9f, 0.2f, 1f);
        private static readonly Color s_diagnosticColor = new Color(1f, 0.6f, 0.2f, 1f);

        private static readonly Dictionary<int, ApaPreviewDebugData> s_entries =
            new Dictionary<int, ApaPreviewDebugData>();

        private static bool s_initialized;

        /// <summary>
        /// The last drawing failure that was logged, so a persistent fault is reported once rather than once per
        /// repaint. Cleared as soon as a draw succeeds.
        /// </summary>
        private static string s_lastFailure;

        /// <summary>True when the debug switch is on.</summary>
        public static bool IsEnabled => ApaPreviewToggles.DebugOverlay.IsEnabled.Value;

        /// <summary>Number of renderers with debug data registered.</summary>
        public static int RegisteredCount => s_entries.Count;

        /// <summary>
        /// Registers debug data for a renderer. Replaces any previous registration for the same renderer.
        /// </summary>
        public static void Register(Renderer renderer, ApaPreviewDebugData data)
        {
            EnsureInitialized();
            if (renderer == null || data == null) return;

            s_entries[renderer.GetInstanceID()] = data;
        }

        /// <summary>Removes the registration for a renderer, if any.</summary>
        public static void Unregister(Renderer renderer)
        {
            if (renderer == null) return;
            s_entries.Remove(renderer.GetInstanceID());
        }

        /// <summary>
        /// Removes the registration for a renderer only when it is the given data instance.
        /// </summary>
        /// <remarks>
        /// During a pipeline swap the incoming generation registers its data before the outgoing generation is
        /// disposed. The outgoing node must not remove the incoming node's entry, so it unregisters by identity
        /// rather than by renderer alone.
        /// </remarks>
        public static void Unregister(Renderer renderer, ApaPreviewDebugData expected)
        {
            if (renderer == null || expected == null) return;

            var id = renderer.GetInstanceID();
            ApaPreviewDebugData current;
            if (!s_entries.TryGetValue(id, out current)) return;
            if (!ReferenceEquals(current, expected)) return;

            s_entries.Remove(id);
        }

        /// <summary>Removes every registration. Used by tests and by teardown.</summary>
        public static void Clear()
        {
            s_entries.Clear();
        }

        /// <summary>Registered data for a renderer, or null.</summary>
        public static ApaPreviewDebugData DataFor(Renderer renderer)
        {
            if (renderer == null) return null;
            ApaPreviewDebugData data;
            return s_entries.TryGetValue(renderer.GetInstanceID(), out data) ? data : null;
        }

        [InitializeOnLoadMethod]
        private static void EnsureInitialized()
        {
            if (s_initialized) return;
            s_initialized = true;

            SceneView.duringSceneGui += OnSceneGui;

            // No OnChange subscription on ApaPreviewToggles.DebugOverlay.IsEnabled: PublishedValue.Value clears
            // its OnChange subscribers after firing them (PublishedValue.cs:38-42), so a direct subscription works
            // exactly once and is dead afterwards. It is also unnecessary — assigning the value already requests a
            // Scene View repaint (PublishedValue.cs:44, RepaintTrigger.cs:11-28), which is all this overlay needs.
        }

        private static void OnSceneGui(SceneView view)
        {
            if (view == null) return;
            if (!IsEnabled) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var previousColor = Handles.color;
            try
            {
                DrawRegisteredMeshData();
                DrawDiagnosticLabels();
                s_lastFailure = null;
            }
            catch (Exception e)
            {
                // `duringSceneGui` runs on every repaint, so a persistent fault would log an error per frame. The
                // message is remembered and the overlay stays quiet until drawing succeeds again, or until the
                // failure changes to a different one.
                var failure = e.GetType().Name + ": " + e.Message;
                if (!string.Equals(failure, s_lastFailure, StringComparison.Ordinal))
                {
                    s_lastFailure = failure;
                    Debug.LogError(ApaPreviewDiagnostics.LogPrefix + TrFormat("debug overlay failed: {0}", failure));
                }
            }
            finally
            {
                Handles.color = previousColor;
            }
        }

        private static void DrawRegisteredMeshData()
        {
            if (s_entries.Count == 0) return;

            List<int> stale = null;
            foreach (var pair in s_entries)
            {
                var data = pair.Value;
                var renderer = data != null ? data.Original : null;
                if (renderer == null)
                {
                    // The original was destroyed; the node that owned this entry is about to unregister it.
                    if (stale == null) stale = new List<int>();
                    stale.Add(pair.Key);
                    continue;
                }

                DrawMeshData(data, renderer);
            }

            if (stale == null) return;
            for (var i = 0; i < stale.Count; i++) s_entries.Remove(stale[i]);
        }

        private static void DrawMeshData(ApaPreviewDebugData data, Renderer renderer)
        {
            var localToWorld = renderer.localToWorldMatrix;

            DrawRemovedTriangles(data, localToWorld);
            DrawSeamMatches(data, localToWorld);
            DrawHeadline(data, renderer);
        }

        private static void DrawRemovedTriangles(ApaPreviewDebugData data, Matrix4x4 localToWorld)
        {
            var plan = data.Plan;
            var baseMesh = data.BaseMesh;
            if (plan == null || baseMesh == null) return;

            var addresses = plan.RemovedTriangles;
            if (addresses == null || addresses.Count == 0) return;

            Handles.color = s_removedColor;
            var toTarget = data.BaseTransforms.SourceToTargetLocal();
            var drawn = 0;

            for (var i = 0; i < addresses.Count && drawn < MaxRemovedTrianglesDrawn; i++)
            {
                var address = addresses[i];
                if (address.SubMeshIndex < 0 || address.SubMeshIndex >= baseMesh.SubMeshIndices.Count) continue;

                var indices = baseMesh.SubMeshIndices[address.SubMeshIndex];
                var start = address.TriangleIndexWithinSubMesh * 3;
                if (start < 0 || start + 2 >= indices.Count) continue;

                var a = ToWorld(localToWorld, toTarget, baseMesh.Vertices, indices[start]);
                var b = ToWorld(localToWorld, toTarget, baseMesh.Vertices, indices[start + 1]);
                var c = ToWorld(localToWorld, toTarget, baseMesh.Vertices, indices[start + 2]);

                Handles.DrawLine(a, b);
                Handles.DrawLine(b, c);
                Handles.DrawLine(c, a);
                drawn++;
            }
        }

        private static void DrawSeamMatches(ApaPreviewDebugData data, Matrix4x4 localToWorld)
        {
            var plan = data.Plan;
            var baseMesh = data.BaseMesh;
            if (plan == null || baseMesh == null || plan.SeamResolutions == null) return;
            if (data.Parts == null || data.Parts.Count == 0) return;

            Handles.color = s_seamColor;
            var toTarget = data.BaseTransforms.SourceToTargetLocal();
            var drawn = 0;

            for (var partIndex = 0; partIndex < data.Parts.Count && drawn < MaxSeamMatchesDrawn; partIndex++)
            {
                var part = data.Parts[partIndex];
                if (part == null) continue;

                SeamResolution resolution;
                if (!plan.SeamResolutions.TryGetValue(part.PartId ?? string.Empty, out resolution)) continue;
                if (resolution == null || resolution.Matches == null) continue;

                var partToTarget = part.Transforms.SourceToTargetLocal();
                for (var i = 0; i < resolution.Matches.Count && drawn < MaxSeamMatchesDrawn; i++)
                {
                    var match = resolution.Matches[i];
                    if (match.BaseVertex < 0 || match.BaseVertex >= baseMesh.Vertices.Count) continue;

                    var baseWorld = ToWorld(localToWorld, toTarget, baseMesh.Vertices, match.BaseVertex);
                    var partWorld = baseWorld;

                    if (part.Mesh != null && match.PartVertex >= 0 && match.PartVertex < part.Mesh.Vertices.Count)
                    {
                        partWorld = ToWorld(localToWorld, partToTarget, part.Mesh.Vertices, match.PartVertex);
                    }

                    // A matched pair is drawn as a line between the two authored positions: the line's length is
                    // the match error, so a seam authored against a moved body is visible immediately. A perfect
                    // weld draws as a cross at the shared position.
                    Handles.DrawLine(baseWorld, partWorld);
                    DrawCross(baseWorld);
                    drawn++;
                }
            }
        }

        private static void DrawHeadline(ApaPreviewDebugData data, Renderer renderer)
        {
            var removed = data.Plan != null ? data.Plan.RemovedTriangleCount : 0;
            var seams = CountSeamMatches(data);
            var missingBones = data.Bones != null ? data.Bones.MissingPaths.Count : 0;

            var text = TrFormat("APA preview: {0} removed tri, {1} seam match", removed, seams);
            if (missingBones > 0) text += TrFormat(", {0} unresolved bone(s)", missingBones);
            text += TrFormat("\nfingerprint {0}", data.Fingerprint);

            if (data.Issues != null && data.Issues.Count > 0)
            {
                var shown = 0;
                for (var i = 0; i < data.Issues.Count && shown < MaxLabelLines; i++)
                {
                    text += "\n" + ApaPreviewDiagnostics.FormatIssue(data.Issues[i]);
                    shown++;
                }

                if (data.Issues.Count > shown) text += TrFormat("\n… {0} more", data.Issues.Count - shown);
            }

            Handles.Label(LabelPosition(renderer), text);
        }

        private static void DrawDiagnosticLabels()
        {
            var reports = ApaPreviewDiagnostics.Snapshot();
            for (var i = 0; i < reports.Count; i++)
            {
                var report = reports[i];
                if (report == null || !report.HasContent) continue;
                if (report.AvatarRoot == null) continue;

                // Anchor on the report's own target renderer, not on the avatar root: one avatar has one report
                // per target group, and anchoring every group on the root would stack N labels on the same pixel,
                // hiding every group but the last. The root stays the fallback for a report whose target did not
                // resolve (a blocked group has no renderer to point at).
                var target = report.TargetRenderer;
                var position = target != null
                    ? LabelPosition(target.bounds.center)
                    : LabelPosition(report.AvatarRoot.transform.position);

                Handles.color = s_diagnosticColor;
                Handles.Label(position, report.ToDisplayString(MaxLabelLines));
            }
        }

        private static int CountSeamMatches(ApaPreviewDebugData data)
        {
            if (data.Plan == null || data.Plan.SeamResolutions == null || data.Parts == null) return 0;

            var total = 0;
            for (var i = 0; i < data.Parts.Count; i++)
            {
                var part = data.Parts[i];
                if (part == null) continue;

                SeamResolution resolution;
                if (!data.Plan.SeamResolutions.TryGetValue(part.PartId ?? string.Empty, out resolution)) continue;
                if (resolution != null && resolution.Matches != null) total += resolution.Matches.Count;
            }

            return total;
        }

        private static Vector3 ToWorld(
            Matrix4x4 localToWorld,
            Matrix4x4 sourceToTarget,
            IReadOnlyList<Vector3> vertices,
            int index)
        {
            if (vertices == null || index < 0 || index >= vertices.Count) return Vector3.zero;
            return localToWorld.MultiplyPoint3x4(sourceToTarget.MultiplyPoint3x4(vertices[index]));
        }

        private static void DrawCross(Vector3 position)
        {
            var size = HandleUtility.GetHandleSize(position) * 0.02f;
            Handles.DrawLine(position + Vector3.left * size, position + Vector3.right * size);
            Handles.DrawLine(position + Vector3.down * size, position + Vector3.up * size);
            Handles.DrawLine(position + Vector3.back * size, position + Vector3.forward * size);
        }

        private static Vector3 LabelPosition(Renderer renderer)
        {
            return renderer != null ? LabelPosition(renderer.bounds.center) : Vector3.zero;
        }

        private static Vector3 LabelPosition(Vector3 anchor)
        {
            return anchor + Vector3.up * HandleUtility.GetHandleSize(anchor) * 0.35f;
        }
    }
}

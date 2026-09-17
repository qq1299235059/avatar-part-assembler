using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>Which delta array of a blend shape frame is being read.</summary>
    public enum BlendShapeDeltaKind
    {
        /// <summary>Position deltas, in the source mesh's local space.</summary>
        Position = 0,

        /// <summary>Normal deltas, as directions.</summary>
        Normal = 1,

        /// <summary>Tangent deltas, as directions.</summary>
        Tangent = 2
    }

    /// <summary>
    /// Reads blend shape frame data defensively.
    /// </summary>
    /// <remarks>
    /// Every read is bounds-checked and returns zero for an absent frame or vertex. Returning zero rather than
    /// throwing is what lets the assembler treat "this source does not have that shape" and "this source has no
    /// delta here" as the same, well-defined case: zero delta is exactly the documented value for a vertex a
    /// shape does not move.
    /// </remarks>
    public static class BlendShapeDeltas
    {
        /// <summary>Number of frames of a shape, or 0 when the shape or mesh is absent.</summary>
        public static int FrameCount(MeshSnapshot mesh, int shapeIndex)
        {
            if (mesh == null) return 0;
            if (shapeIndex < 0 || shapeIndex >= mesh.BlendShapeFrames.Count) return 0;
            var frames = mesh.BlendShapeFrames[shapeIndex];
            return frames?.Count ?? 0;
        }

        /// <summary>Reads one frame, or null when the shape or frame is absent.</summary>
        public static BlendShapeFrameSnapshot Frame(MeshSnapshot mesh, int shapeIndex, int frameIndex)
        {
            if (mesh == null) return null;
            if (shapeIndex < 0 || shapeIndex >= mesh.BlendShapeFrames.Count) return null;
            var frames = mesh.BlendShapeFrames[shapeIndex];
            if (frames == null || frameIndex < 0 || frameIndex >= frames.Count) return null;
            return frames[frameIndex];
        }

        /// <summary>
        /// Reads one vertex delta of one frame, in the source mesh's local space, or <see cref="Vector3.zero"/>
        /// when the shape, frame, or vertex is absent.
        /// </summary>
        public static Vector3 Delta(
            MeshSnapshot mesh,
            int shapeIndex,
            int frameIndex,
            int vertexIndex,
            BlendShapeDeltaKind kind)
        {
            var frame = Frame(mesh, shapeIndex, frameIndex);
            if (frame == null) return Vector3.zero;

            IReadOnlyList<Vector3> values;
            switch (kind)
            {
                case BlendShapeDeltaKind.Normal:
                    values = frame.DeltaNormals;
                    break;
                case BlendShapeDeltaKind.Tangent:
                    values = frame.DeltaTangents;
                    break;
                default:
                    values = frame.DeltaVertices;
                    break;
            }

            if (values == null || vertexIndex < 0 || vertexIndex >= values.Count) return Vector3.zero;
            return values[vertexIndex];
        }
    }

    /// <summary>
    /// One final blend shape: its name, its frames, and which source shape feeds each frame.
    /// </summary>
    /// <remarks>
    /// Frame indices are shared across every contributing source, which is only meaningful because the catalog
    /// refuses to merge two same-named shapes whose frame counts differ (section 43.8). The per-source shape
    /// index is what differs: the base may hold <c>Blink</c> at shape 3 while a part holds it at shape 0.
    /// </remarks>
    public sealed class BlendShapePlan
    {
        private readonly ReadOnlyCollection<float> _frameWeights;
        private readonly ReadOnlyDictionary<string, int> _sourceShapeIndex;

        /// <summary>Final shape name, exactly as the owning source spells it.</summary>
        public string Name { get; }

        /// <summary>Frame weights, in frame order.</summary>
        public IReadOnlyList<float> FrameWeights => _frameWeights;

        /// <summary>Number of frames.</summary>
        public int FrameCount => _frameWeights.Count;

        /// <summary>Source shape index per source key: an empty string for the target body, else the part id.</summary>
        public IReadOnlyDictionary<string, int> SourceShapeIndex => _sourceShapeIndex;

        /// <summary>The source that gave the shape its name and frame weights.</summary>
        public string PrimarySource { get; }

        /// <summary>Creates a shape plan.</summary>
        public BlendShapePlan(
            string name,
            string primarySource,
            IReadOnlyList<float> frameWeights,
            IReadOnlyDictionary<string, int> sourceShapeIndex)
        {
            Name = name ?? string.Empty;
            PrimarySource = primarySource ?? string.Empty;

            var weights = new float[frameWeights?.Count ?? 0];
            for (var i = 0; i < weights.Length; i++) weights[i] = frameWeights[i];
            _frameWeights = Array.AsReadOnly(weights);

            var sources = new Dictionary<string, int>(StringComparer.Ordinal);
            if (sourceShapeIndex != null)
            {
                foreach (var pair in sourceShapeIndex) sources[pair.Key ?? string.Empty] = pair.Value;
            }

            _sourceShapeIndex = new ReadOnlyDictionary<string, int>(sources);
        }

        /// <summary>The source's own shape index for a source key, or -1 when that source has no such shape.</summary>
        public int SourceIndexFor(string sourceKey)
        {
            return _sourceShapeIndex.TryGetValue(sourceKey ?? string.Empty, out var index) ? index : -1;
        }

        /// <summary>True when the given source contributes to this shape.</summary>
        public bool HasSource(string sourceKey)
        {
            return _sourceShapeIndex.ContainsKey(sourceKey ?? string.Empty);
        }

        /// <summary>Source keys in ascending ordinal order, for deterministic diagnostics.</summary>
        public List<string> OrderedSourceKeys()
        {
            var keys = new List<string>(_sourceShapeIndex.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }

    /// <summary>
    /// The final blend shape set: what shapes the generated mesh will carry, in final order.
    /// </summary>
    /// <remarks>
    /// Ordering is fixed by the specification (section 43.3): every shape of the target body first, in the
    /// body's own order, then the shapes new parts introduce, in stable part order and then source order. Two
    /// sources that name a shape identically contribute to one final shape rather than creating a duplicate.
    /// </remarks>
    public sealed class BlendShapeMergePlan
    {
        private readonly ReadOnlyCollection<BlendShapePlan> _shapes;

        /// <summary>An empty set, used when no source carries a blend shape.</summary>
        public static readonly BlendShapeMergePlan Empty =
            new BlendShapeMergePlan(Array.Empty<BlendShapePlan>());

        /// <summary>The final shapes, in final order.</summary>
        public IReadOnlyList<BlendShapePlan> Shapes => _shapes;

        /// <summary>Number of final shapes.</summary>
        public int Count => _shapes.Count;

        /// <summary>True when there is nothing to write.</summary>
        public bool IsEmpty => _shapes.Count == 0;

        /// <summary>Creates a merge plan.</summary>
        public BlendShapeMergePlan(IReadOnlyList<BlendShapePlan> shapes)
        {
            var copy = new BlendShapePlan[shapes?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++) copy[i] = shapes[i];
            _shapes = Array.AsReadOnly(copy);
        }
    }

    /// <summary>
    /// Builds the final blend shape set, or reports why it cannot be built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split in two on purpose. This class decides <i>what</i> the final shape set is: structural validity,
    /// duplicate names, frame agreement, and deterministic order. The seam-delta rules are decided by
    /// <see cref="BlendShapeSeamValidator"/>, because they need the resolved seam correspondence, which only
    /// exists once the planner has matched seam vertices by position. Splitting them keeps both callers honest:
    /// the validator rule can run the catalog without pretending it has a seam, and the planner adds the part
    /// only it can decide.
    /// </para>
    /// <para>
    /// Names are compared with the shared semantic rule (trim, then ordinal, never case-folded) so that a
    /// shape named <c>"Blink"</c> and one named <c>"Blink "</c> merge rather than producing two shapes an
    /// animation curve cannot tell apart. The <i>output</i> name is the first contributor's spelling, which is
    /// the base body's whenever the base has the shape.
    /// </para>
    /// </remarks>
    public static class BlendShapeCatalog
    {
        /// <summary>
        /// Builds the final blend shape set, or returns null when a blocking defect was reported.
        /// </summary>
        public static BlendShapeMergePlan Build(ValidationContext context, List<ValidationIssue> issues)
        {
            if (issues == null) throw new ArgumentNullException(nameof(issues));
            if (context?.Base?.Mesh == null) return BlendShapeMergePlan.Empty;

            var failed = !ValidateSource(context.Base.Mesh, string.Empty, "the target body", issues);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null) continue;
                failed |= !ValidateSource(part.Mesh, part.PartId, DescribePart(part), issues);
            }

            if (failed) return null;

            var builder = new CatalogBuilder();
            builder.AddBase(context.Base.Mesh, issues);
            for (var i = 0; i < context.Parts.Count; i++)
            {
                var part = context.Parts[i];
                if (part?.Mesh == null) continue;
                builder.AddPart(part.Mesh, part.PartId, DescribePart(part), issues);
            }

            if (issues.Exists(issue => issue.IsBlocking)) return null;

            return builder.ToPlan();
        }

        /// <summary>
        /// Validates the structural integrity of one source's blend shape data.
        /// </summary>
        /// <remarks>
        /// A frame whose delta arrays disagree with each other, or with the mesh's vertex count, cannot be
        /// indexed by vertex at all. That is a different defect from a NaN inside a well-formed array, and it
        /// gets its own code so that a reader is not sent looking for a bad number that does not exist.
        /// </remarks>
        private static bool ValidateSource(MeshSnapshot mesh, string partId, string who, List<ValidationIssue> issues)
        {
            var ok = true;
            var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var shape = 0; shape < mesh.BlendShapeCount; shape++)
            {
                var rawName = mesh.Shapes[shape];
                var name = ApaSemanticName.Normalize(rawName);

                if (!ApaSemanticName.IsValid(rawName))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidSemanticName,
                        ApaIssuePhase.Attributes,
                        "Blend shape " + shape + " of " + who + " has an empty or whitespace-only name. A blend " +
                        "shape is addressed by name at animation time, so it needs one.",
                        partId,
                        shape,
                        detail: "reason=invalid-blendshape-name; shape=" + shape));
                    ok = false;
                    continue;
                }

                if (seenNames.TryGetValue(name, out var earlier))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.BlendShapeDuplicateName,
                        ApaIssuePhase.Attributes,
                        "Mesh '" + MeshName(mesh) + "' on " + who + " declares the blend shape name '" + rawName +
                        "' twice (shapes " + earlier + " and " + shape + "). Two shapes with one name cannot be " +
                        "merged without silently dropping one of them.",
                        partId,
                        shape,
                        earlier,
                        detail: "reason=duplicate-blendshape-name; name=" + name + "; first=" + earlier));
                    ok = false;
                    continue;
                }

                seenNames.Add(name, shape);

                var declaredFrames = shape < mesh.ShapeFrameCounts.Count ? mesh.ShapeFrameCounts[shape] : 0;
                var actualFrames = BlendShapeDeltas.FrameCount(mesh, shape);

                if (declaredFrames != actualFrames)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.InvalidBlendShapeDelta,
                        ApaIssuePhase.Attributes,
                        "Blend shape '" + rawName + "' on " + who + " reports " + declaredFrames +
                        " frame(s) but carries " + actualFrames + " frame(s) of delta data.",
                        partId,
                        shape,
                        detail: "reason=frame-count-disagreement; declared=" + declaredFrames +
                                "; actual=" + actualFrames));
                    ok = false;
                    continue;
                }

                for (var frame = 0; frame < actualFrames; frame++)
                {
                    var snapshot = BlendShapeDeltas.Frame(mesh, shape, frame);
                    if (snapshot == null) continue;

                    if (snapshot.VertexCount != mesh.VertexCount)
                    {
                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.InvalidBlendShapeDelta,
                            ApaIssuePhase.Attributes,
                            "Frame " + frame + " of blend shape '" + rawName + "' on " + who + " has " +
                            DescribeFrameLengths(snapshot) + " delta value(s) for " + mesh.VertexCount +
                            " vertices. Every delta array must have exactly one entry per vertex.",
                            partId,
                            shape,
                            frame,
                            detail: "reason=delta-length-mismatch; frame=" + frame +
                                    "; vertices=" + mesh.VertexCount));
                        ok = false;
                        break;
                    }

                    var nonFinite = FindNonFiniteDelta(snapshot);
                    if (nonFinite < 0) continue;

                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.NonFiniteValue,
                        ApaIssuePhase.Attributes,
                        "Frame " + frame + " of blend shape '" + rawName + "' on " + who +
                        " has a non-finite delta at vertex " + nonFinite + ".",
                        partId,
                        nonFinite,
                        frame,
                        detail: "attribute=blendshape-delta; frame=" + frame + "; vertex=" + nonFinite));
                    ok = false;
                    break;
                }
            }

            return ok;
        }

        private static string DescribeFrameLengths(BlendShapeFrameSnapshot frame)
        {
            return frame.DeltaVertices.Count + "/" + frame.DeltaNormals.Count + "/" + frame.DeltaTangents.Count;
        }

        private static int FindNonFiniteDelta(BlendShapeFrameSnapshot frame)
        {
            for (var i = 0; i < frame.DeltaVertices.Count; i++)
            {
                if (!IsFinite(frame.DeltaVertices[i])) return i;
            }

            for (var i = 0; i < frame.DeltaNormals.Count; i++)
            {
                if (!IsFinite(frame.DeltaNormals[i])) return i;
            }

            for (var i = 0; i < frame.DeltaTangents.Count; i++)
            {
                if (!IsFinite(frame.DeltaTangents[i])) return i;
            }

            return -1;
        }

        private static bool IsFinite(Vector3 value)
        {
            return ApaNumericPolicy.IsFinite(value.x)
                   && ApaNumericPolicy.IsFinite(value.y)
                   && ApaNumericPolicy.IsFinite(value.z);
        }

        private static string DescribePart(PartSnapshot part)
        {
            return string.IsNullOrEmpty(part.DisplayName) ? "'" + part.PartId + "'" : "'" + part.DisplayName + "'";
        }

        private static string MeshName(MeshSnapshot mesh)
        {
            return string.IsNullOrEmpty(mesh?.Name) ? "(unnamed mesh)" : mesh.Name;
        }

        /// <summary>
        /// Accumulates the final shape order while merging same-named contributions.
        /// </summary>
        private sealed class CatalogBuilder
        {
            private readonly List<MutableShape> _shapes = new List<MutableShape>();
            private readonly Dictionary<string, int> _indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

            public void AddBase(MeshSnapshot mesh, List<ValidationIssue> issues)
            {
                for (var shape = 0; shape < mesh.BlendShapeCount; shape++)
                {
                    var name = ApaSemanticName.Normalize(mesh.Shapes[shape]);
                    var frames = ReadFrameWeights(mesh, shape);
                    var index = _shapes.Count;

                    // The base is a contributor like any other, so its own shape index must be registered here.
                    // Without this entry the base would look like a source that does not have the shape at all,
                    // which turns every same-named base/part shape into a part-only one and reads zero deltas at
                    // every body vertex.
                    var entry = new MutableShape(mesh.Shapes[shape], string.Empty, frames);
                    entry.AddSource(string.Empty, shape);
                    _shapes.Add(entry);
                    _indexByName[name] = index;
                }
            }

            public void AddPart(MeshSnapshot mesh, string partId, string who, List<ValidationIssue> issues)
            {
                for (var shape = 0; shape < mesh.BlendShapeCount; shape++)
                {
                    var name = ApaSemanticName.Normalize(mesh.Shapes[shape]);
                    var frames = ReadFrameWeights(mesh, shape);

                    if (!_indexByName.TryGetValue(name, out var index))
                    {
                        index = _shapes.Count;
                        _shapes.Add(new MutableShape(mesh.Shapes[shape], partId, frames));
                        _indexByName.Add(name, index);
                        _shapes[index].AddSource(partId, shape);
                        continue;
                    }

                    var existing = _shapes[index];

                    // A frame index is a position in an array, not an identity (section 43.8). Merging two
                    // same-named shapes whose frames do not line up would silently drive the wrong pose, so the
                    // disagreement blocks rather than picking one author's frames.
                    if (existing.FrameWeights.Count != frames.Length)
                    {
                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.BlendShapeFrameMismatch,
                            ApaIssuePhase.Attributes,
                            "Blend shape '" + existing.Name + "' has " + existing.FrameWeights.Count +
                            " frame(s) on " + DescribeSource(existing.PrimarySource) + " but " + frames.Length +
                            " frame(s) on " + who + ". Same-named shapes can only merge when their frames " +
                            "line up.",
                            partId,
                            shape,
                            detail: "reason=frame-count-mismatch; name=" + name +
                                    "; frames=" + existing.FrameWeights.Count + "; otherFrames=" + frames.Length));
                        continue;
                    }

                    var mismatch = -1;
                    for (var frame = 0; frame < frames.Length; frame++)
                    {
                        if (existing.FrameWeights[frame] == frames[frame]) continue;
                        mismatch = frame;
                        break;
                    }

                    if (mismatch >= 0)
                    {
                        issues.Add(ValidationIssue.Error(
                            ApaErrorCode.BlendShapeFrameMismatch,
                            ApaIssuePhase.Attributes,
                            "Blend shape '" + existing.Name + "' has frame weight " +
                            existing.FrameWeights[mismatch] + " at frame " + mismatch + " on " +
                            DescribeSource(existing.PrimarySource) + " but " + frames[mismatch] + " on " + who +
                            ". Frame weights must agree exactly before the shapes can merge.",
                            partId,
                            shape,
                            mismatch,
                            detail: "reason=frame-weight-mismatch; name=" + name + "; frame=" + mismatch));
                        continue;
                    }

                    existing.AddSource(partId, shape);
                }
            }

            public BlendShapeMergePlan ToPlan()
            {
                var result = new BlendShapePlan[_shapes.Count];
                for (var i = 0; i < result.Length; i++) result[i] = _shapes[i].ToPlan();
                return new BlendShapeMergePlan(result);
            }

            private static float[] ReadFrameWeights(MeshSnapshot mesh, int shape)
            {
                var count = BlendShapeDeltas.FrameCount(mesh, shape);
                var weights = new float[count];
                for (var frame = 0; frame < count; frame++)
                {
                    var snapshot = BlendShapeDeltas.Frame(mesh, shape, frame);
                    weights[frame] = snapshot != null ? snapshot.Weight : 0f;
                }

                return weights;
            }

            private static string DescribeSource(string sourceKey)
            {
                return string.IsNullOrEmpty(sourceKey) ? "the target body" : "'" + sourceKey + "'";
            }
        }

        /// <summary>A shape under construction: its order, name, frames, and contributing sources.</summary>
        private sealed class MutableShape
        {
            private readonly List<float> _frameWeights;
            private readonly Dictionary<string, int> _sourceShapeIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            public string Name { get; }

            public string PrimarySource { get; }

            public IReadOnlyList<float> FrameWeights => _frameWeights;

            public MutableShape(string name, string primarySource, float[] frameWeights)
            {
                Name = name ?? string.Empty;
                PrimarySource = primarySource ?? string.Empty;
                _frameWeights = new List<float>(frameWeights ?? Array.Empty<float>());
            }

            public void AddSource(string sourceKey, int shapeIndex)
            {
                _sourceShapeIndex[sourceKey ?? string.Empty] = shapeIndex;
            }

            public BlendShapePlan ToPlan()
            {
                return new BlendShapePlan(Name, PrimarySource, _frameWeights, _sourceShapeIndex);
            }
        }
    }

    /// <summary>
    /// Applies the seam rules of section 43.8 to a merged blend shape set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These rules need the resolved seam correspondence, which is established by matching positions rather
    /// than by index order, so they run in the planner after <see cref="SeamResolver"/>. The two rules are:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A shape that exists only on a part must have exactly zero deltas at every <i>welded</i> seam vertex. The
    /// welded output vertex <i>is</i> the base vertex, so a part delta there has no representation; accepting one
    /// would silently ignore author animation intent.
    /// </description></item>
    /// <item><description>
    /// A shape that exists on both sides must agree at every <i>welded</i> seam vertex within the configured
    /// epsilon, because only the base value survives the weld.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>A preserved (split) seam vertex is exempt (M11).</b> It is a part-owned vertex: its own deltas are
    /// emitted, and its position follows the base vertex it was snapped to, so no author delta is discarded and
    /// the seam cannot open. Reporting <c>APA029</c> for such a pair would block exactly the normal input —
    /// a part whose UV atlas differs from the body's — that the preservation exists to support. The weld/split
    /// decision therefore comes from the same <see cref="SeamWeldPlan"/> the geometry was planned with, not from a
    /// second reading of the UV rule.
    /// </para>
    /// <para>
    /// The same-name comparison happens in the target renderer's local space, with the operator the output
    /// materialization uses for each delta kind. Part-local and base-local deltas live in different bases, so
    /// comparing them raw would report a false disagreement for a rotated or scaled part and could hide a real
    /// one behind a transform that happens to cancel it.
    /// </para>
    /// </remarks>
    public static class BlendShapeSeamValidator
    {
        /// <summary>
        /// Applies the seam delta rules. Appends blocking issues; returns true when nothing blocked.
        /// </summary>
        /// <param name="seamWelds">
        /// The weld/split decision per part. Only welded pairs are checked; a preserved pair is representable by
        /// construction. Null means "no pair is known to be preserved", which keeps every existing call site
        /// checking exactly what it checked before.
        /// </param>
        public static bool Validate(
            ValidationContext context,
            BlendShapeMergePlan plan,
            IReadOnlyDictionary<string, SeamResolution> seamResolutions,
            SeamWeldPlan seamWelds,
            List<ValidationIssue> issues)
        {
            if (issues == null) throw new ArgumentNullException(nameof(issues));
            if (plan == null || plan.IsEmpty || context == null) return true;

            var ok = true;

            for (var s = 0; s < plan.Shapes.Count; s++)
            {
                var shape = plan.Shapes[s];

                for (var p = 0; p < context.Parts.Count; p++)
                {
                    var part = context.Parts[p];
                    if (part?.Mesh == null) continue;

                    var partShape = shape.SourceIndexFor(part.PartId);
                    if (partShape < 0) continue;

                    if (seamResolutions == null || !seamResolutions.TryGetValue(part.PartId, out var seam) || seam == null)
                    {
                        continue;
                    }

                    var weldPlan = seamWelds != null ? seamWelds.For(part.PartId) : null;
                    var baseShape = shape.SourceIndexFor(string.Empty);
                    ok &= baseShape >= 0
                        ? ValidateSharedShape(context, part, shape, baseShape, partShape, seam, weldPlan, issues)
                        : ValidatePartOnlyShape(context, part, shape, partShape, seam, weldPlan, issues);
                }
            }

            return ok;
        }

        /// <summary>
        /// A part-only shape: every delta at a welded seam vertex must be exactly zero.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The check runs on the source-local delta. A zero delta stays zero under any linear map, so a
        /// transformed comparison would accept exactly the same shapes, and a non-zero delta that a degenerate
        /// transform happened to flatten must keep blocking: the author's intent would otherwise be silently
        /// discarded at output. The transform itself is validated when the context is built
        /// (<see cref="ApaErrorCode.InvalidSpaceTransform"/>), so this cannot be the only guard on a bad matrix.
        /// </para>
        /// <para>
        /// A preserved (split) pair is skipped: that vertex keeps its own deltas and takes its position from the
        /// base, so a part-only shape moving it is representable rather than discarded.
        /// </para>
        /// </remarks>
        private static bool ValidatePartOnlyShape(
            ValidationContext context,
            PartSnapshot part,
            BlendShapePlan shape,
            int partShape,
            SeamResolution seam,
            PartSeamWeldPlan weldPlan,
            List<ValidationIssue> issues)
        {
            var ok = true;

            for (var f = 0; f < shape.FrameCount; f++)
            {
                for (var m = 0; m < seam.Matches.Count; m++)
                {
                    var partVertex = seam.Matches[m].PartVertex;
                    if (weldPlan != null && !weldPlan.IsWeld(partVertex)) continue;

                    var position = BlendShapeDeltas.Delta(
                        part.Mesh, partShape, f, partVertex, BlendShapeDeltaKind.Position);
                    var normal = BlendShapeDeltas.Delta(
                        part.Mesh, partShape, f, partVertex, BlendShapeDeltaKind.Normal);
                    var tangent = BlendShapeDeltas.Delta(
                        part.Mesh, partShape, f, partVertex, BlendShapeDeltaKind.Tangent);

                    if (IsZero(position) && IsZero(normal) && IsZero(tangent)) continue;

                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.BlendShapeSeamDeltaMismatch,
                        ApaIssuePhase.Attributes,
                        "Blend shape '" + shape.Name + "' exists only on part '" + part.PartId + "' but moves " +
                        "welded seam vertex " + partVertex + " at frame " + f + ". A welded vertex is the base " +
                        "body's vertex, so a part-only delta there cannot be represented; the shape would " +
                        "silently ignore it.",
                        part.PartId,
                        partVertex,
                        f,
                        detail: "reason=part-only-seam-delta; shape=" + shape.Name + "; frame=" + f +
                                "; partVertex=" + partVertex + "; position=" + Describe(position) +
                                "; normal=" + Describe(normal) + "; tangent=" + Describe(tangent)));
                    ok = false;
                    break;
                }
            }

            return ok;
        }

        /// <summary>
        /// A same-name shape: the base and part deltas must agree at every welded seam vertex.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both deltas are compared in the target renderer's local space, which is the space the emitted frame
        /// is written in. A raw part-local comparison would reject a part whose transform rotates or scales it
        /// (the two deltas describe the same motion but are written in different bases) and would accept a pair
        /// that only happens to be written identically while moving the surface differently after the part's
        /// transform is applied.
        /// </para>
        /// <para>
        /// A preserved (split) pair is skipped: the part keeps its own deltas and its position follows the base,
        /// so neither side's animation is discarded and the seam cannot open.
        /// </para>
        /// </remarks>
        private static bool ValidateSharedShape(
            ValidationContext context,
            PartSnapshot part,
            BlendShapePlan shape,
            int baseShape,
            int partShape,
            SeamResolution seam,
            PartSeamWeldPlan weldPlan,
            List<ValidationIssue> issues)
        {
            var ok = true;
            var epsilon = context.NumericPolicy.PositionEpsilon;
            var partTransforms = part.Transforms;
            var baseTransforms = context.Base.Transforms;

            for (var f = 0; f < shape.FrameCount; f++)
            {
                for (var m = 0; m < seam.Matches.Count; m++)
                {
                    var match = seam.Matches[m];
                    if (weldPlan != null && !weldPlan.IsWeld(match.PartVertex)) continue;

                    if (Agrees(partTransforms, part.Mesh, partShape, match.PartVertex,
                            baseTransforms, context.Base.Mesh, baseShape, match.BaseVertex,
                            f, BlendShapeDeltaKind.Position, epsilon)
                        && Agrees(partTransforms, part.Mesh, partShape, match.PartVertex,
                            baseTransforms, context.Base.Mesh, baseShape, match.BaseVertex,
                            f, BlendShapeDeltaKind.Normal, epsilon)
                        && Agrees(partTransforms, part.Mesh, partShape, match.PartVertex,
                            baseTransforms, context.Base.Mesh, baseShape, match.BaseVertex,
                            f, BlendShapeDeltaKind.Tangent, epsilon))
                    {
                        continue;
                    }

                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.BlendShapeSeamDeltaMismatch,
                        ApaIssuePhase.Attributes,
                        "Blend shape '" + shape.Name + "' exists on both the target body and part '" + part.PartId +
                        "' but their deltas disagree at welded seam vertex " + match.PartVertex + " (base " +
                        match.BaseVertex + ") at frame " + f +
                        ". Only the base value survives the weld, so a disagreement would silently discard the " +
                        "part's animation.",
                        part.PartId,
                        match.PartVertex,
                        f,
                        detail: "reason=shared-shape-seam-delta; shape=" + shape.Name + "; frame=" + f +
                                "; partVertex=" + match.PartVertex + "; baseVertex=" + match.BaseVertex));
                    ok = false;
                    break;
                }
            }

            return ok;
        }

        /// <summary>
        /// True when a part and a base delta describe the same motion in the target renderer's local space.
        /// </summary>
        /// <remarks>
        /// The transform operator per delta kind comes from <see cref="SpaceTransforms.SourceToTargetDeltaMatrix"/>,
        /// which is the same operator <see cref="RemappedBlendShapeProvider"/> materializes frames with. Sharing
        /// it is what makes this rule a statement about the output rather than about how the two assets happened
        /// to be authored: a part that authors an equivalent delta in a rotated or scaled local frame passes,
        /// and a part that authors the identical triple while its transform moves the surface elsewhere fails.
        /// </remarks>
        private static bool Agrees(
            SpaceTransforms partTransforms,
            MeshSnapshot partMesh,
            int partShape,
            int partVertex,
            SpaceTransforms baseTransforms,
            MeshSnapshot baseMesh,
            int baseShape,
            int baseVertex,
            int frame,
            BlendShapeDeltaKind kind,
            float epsilon)
        {
            var partDelta = ToTargetSpace(
                partTransforms,
                BlendShapeDeltas.Delta(partMesh, partShape, frame, partVertex, kind),
                kind);
            var baseDelta = ToTargetSpace(
                baseTransforms,
                BlendShapeDeltas.Delta(baseMesh, baseShape, frame, baseVertex, kind),
                kind);

            if (Mathf.Abs(partDelta.x - baseDelta.x) > epsilon) return false;
            if (Mathf.Abs(partDelta.y - baseDelta.y) > epsilon) return false;
            if (Mathf.Abs(partDelta.z - baseDelta.z) > epsilon) return false;
            return true;
        }

        /// <summary>
        /// Maps one delta from its source-local space into the target renderer's local space.
        /// </summary>
        /// <remarks>
        /// <see cref="Matrix4x4.MultiplyVector"/> applies the linear part only, which is what an offset needs:
        /// the translation column must not be added to a delta.
        /// </remarks>
        private static Vector3 ToTargetSpace(SpaceTransforms transforms, Vector3 delta, BlendShapeDeltaKind kind)
        {
            return transforms.SourceToTargetDeltaMatrix(kind).MultiplyVector(delta);
        }

        private static bool IsZero(Vector3 value)
        {
            return value.x == 0f && value.y == 0f && value.z == 0f;
        }

        private static string Describe(Vector3 value)
        {
            return "(" + value.x + "," + value.y + "," + value.z + ")";
        }
    }
}

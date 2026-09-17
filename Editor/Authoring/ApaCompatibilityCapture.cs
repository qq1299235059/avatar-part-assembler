using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Captures the schema-v2 compatibility signature of a selected target body renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capture itself is core behaviour (<see cref="MeshSnapshotFactory.CaptureSignature"/>) and is not
    /// reimplemented here. What this type adds is the authoring half the core deliberately does not own: which
    /// renderer path is recorded, where the mesh GUID comes from, and whether the captured signature carries
    /// every safety field this build requires.
    /// </para>
    /// <para>
    /// <b>Path convention.</b> The renderer path is produced by the one canonical routine
    /// (<see cref="MeshSnapshotFactory.RelativePath"/>): the avatar root itself records
    /// <see cref="ApaAvatarPath.Root"/> (<c>"."</c>), a missing transform records the empty string, and a
    /// renderer that is not under the avatar root records a scene-absolute path — which is reported as a
    /// blocking issue, because a path that does not resolve from the avatar root at install time would make the
    /// profile non-portable.
    /// </para>
    /// <para>
    /// <b>The GUID is diagnostic context, never a bypass.</b> It is recorded when the mesh is a persisted asset,
    /// and the topology fields are recorded unconditionally, exactly as the core requires.
    /// </para>
    /// </remarks>
    public static class ApaCompatibilityCapture
    {
        /// <summary>
        /// The mesh of a renderer, or null. A <see cref="SkinnedMeshRenderer"/> contributes its
        /// <c>sharedMesh</c>, any other renderer its <see cref="MeshFilter"/> mesh.
        /// </summary>
        public static Mesh ResolveMesh(Renderer renderer)
        {
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>
        /// The asset GUID of a mesh, or an empty string when the mesh is not a persisted asset.
        /// </summary>
        /// <remarks>
        /// A mesh instantiated at runtime — an authoring preview mesh, or a mesh created by another tool — has no
        /// asset path, and the empty result is the correct, honest answer. It never means "matches everything":
        /// the signature's topology fields are what the compatibility rule compares.
        /// </remarks>
        public static string ResolveMeshGuid(Mesh mesh)
        {
            if (mesh == null) return string.Empty;

            var path = UnityEditor.AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path)) return string.Empty;

            var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
            return guid ?? string.Empty;
        }

        /// <summary>
        /// The avatar-root-relative path of a renderer, using the canonical routine. Empty means the renderer is
        /// null; <c>"."</c> means the renderer sits on the avatar root itself.
        /// </summary>
        public static string ResolveRendererPath(GameObject avatarRoot, Renderer renderer)
        {
            var root = avatarRoot != null ? avatarRoot.transform : null;
            return MeshSnapshotFactory.RelativePath(root, renderer != null ? renderer.transform : null);
        }

        /// <summary>
        /// Captures the full signature of a target renderer against an avatar root.
        /// </summary>
        /// <param name="avatarRoot">The avatar root, or null when none is selected.</param>
        /// <param name="targetRenderer">The target body renderer, or null when none is selected.</param>
        /// <param name="issues">Receives blocking diagnostics for anything that makes the capture unusable.</param>
        /// <param name="targetArmature">
        /// The armature the body's bones are recorded relative to, or null when the author selected none.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>The bone signature is scoped to the selected target armature (M10)</b>, because that is the scope the
        /// build records the live body's bones in and what the compatibility rule compares. Capturing against the
        /// avatar root instead would make every profile mismatch its own body on the first build.
        /// </para>
        /// <para>
        /// A null armature is not guessed at: every bone then records no identity, which is a truthful statement
        /// that no scope was selected. The build reports that state as <c>APA043</c> before the compatibility rule
        /// is ever reached, so the profile is refused with the actionable diagnostic rather than a bone mismatch.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The captured signature. A signature is returned even when an issue was reported, so that the authoring
        /// window can display exactly what was recorded next to the reason it must not be saved yet.
        /// </returns>
        public static ApaAvatarCompatibilityProfile Capture(
            GameObject avatarRoot,
            Renderer targetRenderer,
            out List<ValidationIssue> issues,
            Transform targetArmature = null)
        {
            issues = new List<ValidationIssue>();

            if (targetRenderer == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "No target body renderer is selected, so no compatibility signature can be captured.",
                    detail: "reason=missing-target-renderer"));
                return new ApaAvatarCompatibilityProfile();
            }

            var mesh = ResolveMesh(targetRenderer);
            if (mesh == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target renderer '" + targetRenderer.name + "' has no mesh assigned.",
                    detail: "renderer=" + targetRenderer.name));
                return new ApaAvatarCompatibilityProfile();
            }

            // Readability is checked before the capture, not after it: CaptureSignature reads indices, blend
            // shape names and frame counts, and on the default FBX import setting (Read/Write off) those reads
            // are exactly what an author would otherwise trigger — with Unity error output and empty arrays —
            // before the diagnostic that explains it. The message and detail token are the ones the selection
            // check and MeshSnapshotFactory produce, so one defect is described one way everywhere.
            if (!mesh.isReadable)
            {
                issues.Add(NotReadableIssue(mesh));
                return new ApaAvatarCompatibilityProfile();
            }

            var path = ResolveRendererPath(avatarRoot, targetRenderer);
            ValidateRendererPath(avatarRoot, targetRenderer, path, issues);

            var guid = ResolveMeshGuid(mesh);
            var bones = MeshSnapshotFactory.CaptureBoneSignature(targetArmature, targetRenderer);

            var profile = MeshSnapshotFactory.CaptureSignature(mesh, path, guid, bones);

            var captureIssues = ValidateCapturedProfile(profile, mesh);
            if (captureIssues.Count > 0) issues.AddRange(captureIssues);

            return profile;
        }

        /// <summary>
        /// Captures a signature without a live scene, from values a caller already holds. Used by tests and by
        /// callers that capture from a serialized description.
        /// </summary>
        public static ApaAvatarCompatibilityProfile CaptureFrom(
            Mesh mesh,
            string rendererPath,
            string meshGuid,
            BoneSignature boneSignature)
        {
            return MeshSnapshotFactory.CaptureSignature(mesh, rendererPath, meshGuid, boneSignature);
        }

        /// <summary>
        /// Reports what makes a captured signature unusable for this build: an unreadable mesh, or a signature
        /// that is missing a safety field.
        /// </summary>
        /// <remarks>
        /// An unreadable mesh is reported here as well as by the core because the authoring window must say so
        /// before the author picks triangles, not after the first validation run. The message and detail token
        /// are the ones <see cref="MeshSnapshotFactory.Capture"/> uses, so the two reports cannot disagree.
        /// </remarks>
        public static List<ValidationIssue> ValidateCapturedProfile(ApaAvatarCompatibilityProfile profile, Mesh mesh)
        {
            var issues = new List<ValidationIssue>();

            if (mesh != null && !mesh.isReadable)
            {
                issues.Add(NotReadableIssue(mesh));
            }

            if (profile == null || !profile.IsCaptured)
            {
                // The code and wording match CompatibilityRule, which reports the same condition when a live
                // target is available. Exactly one of the two runs for a given validation, so the author always
                // sees this defect in one form.
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.PartProfileIncompatible,
                    ApaIssuePhase.Compatibility,
                    "The profile's target signature was never captured. Re-author the profile against the target body.",
                    detail: "reason=signature-not-captured"));
                return issues;
            }

            if (!profile.HasCompleteSafetyData)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.IncompleteCompatibilitySignature,
                    ApaIssuePhase.Compatibility,
                    "The captured target signature is incomplete, so the profile's triangle and seam indices " +
                    "cannot be verified. It is missing target mesh topology, blend shape frame counts, or the " +
                    "per-submesh index counts they must agree with. Re-author the profile against this body.",
                    detail: "missing=" + DescribeMissing(profile) + "; " + profile.DescribeCapture()));
            }

            return issues;
        }

        /// <summary>
        /// True when the live target no longer matches the captured signature, so re-capturing is required.
        /// </summary>
        /// <remarks>
        /// This is a convenience for the authoring window, not a validation rule: the authoritative comparison is
        /// <see cref="CompatibilityRule"/>, which also decides which mismatch blocks and which is advisory. The
        /// predicate exists so the window can offer "recapture" instead of letting the author discover the
        /// mismatch through a build-time <c>APA012</c>. Callers that have to explain <i>why</i> a recapture is
        /// wanted should use <see cref="SafetyFieldsDiffer"/> and <see cref="IdentityFieldsDiffer"/>, which are
        /// the two halves of this predicate and have different consequences.
        /// </remarks>
        public static bool NeedsRecapture(
            ApaAvatarCompatibilityProfile profile,
            Mesh mesh,
            string rendererPath,
            string meshGuid)
        {
            if (profile == null || !profile.IsCaptured || mesh == null) return true;

            return IdentityFieldsDiffer(profile, mesh, rendererPath, meshGuid) || SafetyFieldsDiffer(profile, mesh);
        }

        /// <summary>
        /// True when the live mesh's safety fields (vertex count, submesh index counts and topologies, blend
        /// shape names and frame counts) no longer match the captured signature.
        /// </summary>
        /// <remarks>
        /// These are the fields <see cref="CompatibilityRule"/> compares. A difference here is what makes a build
        /// block with <c>APA012</c>: the profile's triangle and seam indices cannot be trusted against a
        /// different topology. An uncaptured profile or a missing mesh reports no difference: there is nothing to
        /// compare, and "not captured" is reported as its own condition.
        /// </remarks>
        public static bool SafetyFieldsDiffer(ApaAvatarCompatibilityProfile profile, Mesh mesh)
        {
            if (profile == null || !profile.IsCaptured || mesh == null) return false;
            return !SafetyFieldsMatch(profile, mesh);
        }

        /// <summary>
        /// True when the recorded renderer path or mesh asset GUID no longer matches the live target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These are <b>not</b> compared by <see cref="CompatibilityRule"/>: the renderer path is resolved from
        /// the avatar root at install time, and the mesh GUID is diagnostic context. Renaming or reparenting the
        /// body renderer, or reimporting the mesh under a new GUID with identical topology, therefore produces a
        /// difference that is worth an advisory note and nothing more. Treating it as a blocking condition
        /// (as <see cref="NeedsRecapture"/> alone does) would teach the author to ignore a message that is
        /// sometimes right.
        /// </para>
        /// <para>
        /// A profile that was never captured or a missing mesh reports no difference here, because there is no
        /// recorded identity to compare; the caller reports that case as "not captured".
        /// </para>
        /// </remarks>
        public static bool IdentityFieldsDiffer(
            ApaAvatarCompatibilityProfile profile,
            Mesh mesh,
            string rendererPath,
            string meshGuid)
        {
            if (profile == null || !profile.IsCaptured || mesh == null) return false;

            if (!string.Equals(profile.RendererPath ?? string.Empty, rendererPath ?? string.Empty,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return !string.Equals(profile.MeshGuid ?? string.Empty, meshGuid ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>
        /// The core's "mesh is not readable" diagnostic, in one place so the capture path, the selection check,
        /// and the window cannot describe the same defect differently.
        /// </summary>
        public static ValidationIssue NotReadableIssue(Mesh mesh)
        {
            var name = mesh != null ? mesh.name : string.Empty;
            return ValidationIssue.Error(
                ApaErrorCode.UnsupportedMeshAttribute,
                ApaIssuePhase.Attributes,
                "Mesh '" + name + "' is not readable. Enable Read/Write in its import settings.",
                detail: "mesh=" + name + "; reason=not-readable");
        }

        /// <summary>Compares only the fields the compatibility rule treats as safety-relevant.</summary>
        public static bool SafetyFieldsMatch(ApaAvatarCompatibilityProfile profile, Mesh mesh)
        {
            if (profile == null || mesh == null) return false;
            if (profile.VertexCount != mesh.vertexCount) return false;
            if (!HasSameTopology(profile, mesh)) return false;
            if (!HasSameBlendShapes(profile, mesh)) return false;
            return true;
        }

        /// <summary>A human list of the safety fields a signature is missing.</summary>
        public static string DescribeMissing(ApaAvatarCompatibilityProfile profile)
        {
            if (profile == null) return Localization.ApaLocalization.Tr("signature");

            var missing = new List<string>();
            if (!profile.IsCaptured) missing.Add(Localization.ApaLocalization.Tr("capture flag"));
            if (profile.VertexCount < 0) missing.Add(Localization.ApaLocalization.Tr("vertex count"));
            if (profile.SubMeshIndexCounts.Length == 0)
            {
                missing.Add(Localization.ApaLocalization.Tr("submesh index counts"));
            }

            if (!profile.HasSubMeshTopologies) missing.Add(Localization.ApaLocalization.Tr("submesh topologies"));
            if (profile.BlendShapeNames.Length != profile.BlendShapeFrameCounts.Length)
            {
                missing.Add(Localization.ApaLocalization.Tr("blend shape frame counts"));
            }

            return missing.Count == 0
                ? Localization.ApaLocalization.Tr("none")
                : string.Join(", ", missing.ToArray());
        }

        /// <summary>A compact report of what a signature recorded, for the window and the inspector.</summary>
        public static string Describe(ApaAvatarCompatibilityProfile profile)
        {
            if (profile == null) return Localization.ApaLocalization.Tr("no signature");

            // The captured values are rendered as a stable technical summary (captured=…; vertices=…; guid=…), so
            // only the surrounding shape is localized: paths and counts are data, and a translated key inside the
            // summary would break the correspondence with the detail strings diagnostics carry.
            return Localization.ApaLocalization.TrFormat(
                "path='{0}'; mesh='{1}'; {2}",
                profile.RendererPath ?? string.Empty,
                profile.MeshName ?? string.Empty,
                profile.DescribeCapture());
        }

        private static void ValidateRendererPath(
            GameObject avatarRoot,
            Renderer targetRenderer,
            string path,
            List<ValidationIssue> issues)
        {
            if (avatarRoot == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "No avatar root is selected, so the target renderer has no avatar-root-relative path and the " +
                    "signature could not be resolved at install time.",
                    detail: "reason=missing-avatar-root"));
                return;
            }

            if (!ApaAvatarPath.HasIdentity(path))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target renderer '" + targetRenderer.name + "' has no avatar-root-relative path.",
                    detail: "reason=missing-renderer-path"));
                return;
            }

            if (ApaAvatarPath.IsRoot(path)) return;

            if (!IsUnder(targetRenderer.transform, avatarRoot.transform))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target renderer '" + targetRenderer.name + "' is not inside the selected avatar root '" +
                    avatarRoot.name + "'. The recorded path '" + path + "' would be a scene path that stops " +
                    "resolving once the part is installed under a different avatar.",
                    detail: "reason=target-renderer-outside-avatar-root; path=" + path));
            }
        }

        private static bool IsUnder(Transform candidate, Transform ancestor)
        {
            if (candidate == null || ancestor == null) return false;

            var current = candidate;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = current.parent;
            }

            return false;
        }

        private static bool HasSameTopology(ApaAvatarCompatibilityProfile profile, Mesh mesh)
        {
            var expectedCounts = profile.SubMeshIndexCounts;
            var expectedTopologies = profile.SubMeshTopologyValues;
            var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);

            if (expectedCounts.Length != subMeshCount) return false;
            if (expectedTopologies.Length != subMeshCount) return false;

            for (var i = 0; i < subMeshCount; i++)
            {
                var indices = mesh.GetIndices(i);
                var count = indices != null ? indices.Length : 0;
                if (expectedCounts[i] != count) return false;
                if (expectedTopologies[i] != (int)mesh.GetTopology(i)) return false;
            }

            return true;
        }

        private static bool HasSameBlendShapes(ApaAvatarCompatibilityProfile profile, Mesh mesh)
        {
            var expectedNames = profile.BlendShapeNames;
            var expectedFrames = profile.BlendShapeFrameCounts;
            var count = mesh.blendShapeCount;

            if (expectedNames.Length != count) return false;
            if (expectedFrames.Length != count) return false;

            for (var i = 0; i < count; i++)
            {
                if (!string.Equals(expectedNames[i] ?? string.Empty, mesh.GetBlendShapeName(i) ?? string.Empty,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (expectedFrames[i] != mesh.GetBlendShapeFrameCount(i)) return false;
            }

            return true;
        }
    }
}

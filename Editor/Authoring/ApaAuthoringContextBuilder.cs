using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The outcome of building an authoring validation context.
    /// </summary>
    /// <remarks>
    /// <see cref="Context"/> is null exactly when a snapshot could not be taken at all. It is deliberately not
    /// null merely because <em>rule</em> validation fails: the authoring window needs a context so that
    /// <c>ApaCore.Validate</c> can report the precise, code-carrying reason, and returning no context would
    /// replace that report with a single generic failure.
    /// </remarks>
    public sealed class ApaAuthoringContextResult
    {
        /// <summary>The context, or null when no snapshot could be built.</summary>
        public ValidationContext Context { get; }

        /// <summary>Structural diagnostics produced while building the snapshots.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when a context was produced.</summary>
        public bool Succeeded => Context != null;

        /// <summary>Creates a result.</summary>
        public ApaAuthoringContextResult(ValidationContext context, ValidationResult issues)
        {
            Context = context;
            Issues = issues ?? ValidationResult.Empty;
        }
    }

    /// <summary>
    /// Builds a <see cref="ValidationContext"/> from the authoring selection instead of from installed
    /// components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The build pipeline discovers installers under an avatar with <see cref="ContextBuilder"/>. The authoring
    /// window has the opposite situation: the part exists in the scene but is not installed yet, and the profile
    /// may not exist as an asset at all. This builder therefore takes exactly one target renderer and one part
    /// from the selection and snapshots them with the same factory and the same space-capture routine the build
    /// pipeline uses, so validation runs against byte-for-byte the same kind of input.
    /// </para>
    /// <para>
    /// It makes no rule decisions. Everything that can be decided by a rule is left to
    /// <c>ApaCore.Validate</c>, which keeps one authority for diagnostics instead of two implementations that
    /// drift.
    /// </para>
    /// </remarks>
    public static class ApaAuthoringContextBuilder
    {
        /// <summary>
        /// Builds a context for one authoring part, or reports why no context could be built.
        /// </summary>
        /// <param name="selection">The selected avatar, target renderer, part root, and part renderer.</param>
        /// <param name="profile">
        /// The materialized profile draft: a transient object the caller owns, never a saved asset. The builder
        /// may materialize a missing nested object on it and otherwise only reads it — the removal set, the
        /// seam, the semantics, and the expected compatibility signature.
        /// </param>
        /// <param name="numericPolicy">Tolerances, or null for the defaults.</param>
        public static ApaAuthoringContextResult Build(
            ApaAuthoringSelection selection,
            ApaPartProfile profile,
            ApaNumericPolicy numericPolicy = null)
        {
            var issues = new List<ValidationIssue>();
            var policy = numericPolicy ?? ApaNumericPolicy.Default;

            if (selection == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "No authoring selection was supplied.",
                    detail: "reason=null-selection"));
                return new ApaAuthoringContextResult(null, ValidationResult.Build(issues));
            }

            if (profile == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Configuration,
                    "No profile draft was supplied.",
                    detail: "reason=null-profile"));
                return new ApaAuthoringContextResult(null, ValidationResult.Build(issues));
            }

            // The profile is a transient object built by ApaProfileDraft.Materialize, not a saved asset, so
            // materializing missing members here writes only to that transient copy. Authoring reads of a saved
            // profile (the inspector, ApaProfileDraft.SetFromProfile) use the non-mutating *OrNull accessors.
            profile.EnsureInitialized();

            // The selection's own structural report is included so that a missing or unreadable mesh is explained
            // once, in the same words the core would use if it saw the same defect during a build.
            issues.AddRange(selection.Validate());

            var migrationMessage = string.Empty;
            if (!profile.TryMigrate(out migrationMessage))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.UnknownProfileSchema,
                    ApaIssuePhase.Configuration,
                    migrationMessage,
                    profile.Identity != null ? profile.Identity.PartId : string.Empty,
                    detail: "schemaVersion=" + profile.SchemaVersion));
                return new ApaAuthoringContextResult(null, ValidationResult.Build(issues));
            }

            if (profile.Removal != null && profile.Removal.HasCorruptStorage)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.InvalidTriangleAddress,
                    ApaIssuePhase.Removal,
                    "The removal profile has mismatched submesh and triangle address arrays. The missing address " +
                    "component cannot be reconstructed without guessing; re-author the removal selection.",
                    profile.Identity != null ? profile.Identity.PartId : string.Empty,
                    detail: "reason=corrupt-removal-storage"));
                return new ApaAuthoringContextResult(null, ValidationResult.Build(issues));
            }

            if (!CanSnapshot(selection))
            {
                return new ApaAuthoringContextResult(null, ValidationResult.Build(issues));
            }

            var avatarRoot = selection.AvatarRoot;
            var targetRenderer = selection.TargetRenderer;
            var partRenderer = selection.PartRenderer;

            // The two armature selections decide the scope every bone path is recorded in, so they are resolved
            // before anything is captured, with the same rules and the same diagnostics the build applies. A
            // missing or unresolvable selection produces no context, which is what makes the window's verdict
            // and a build's verdict the same verdict.
            var bones = profile.BonesOrNull;
            var targetArmature = ResolveArmature(
                avatarRoot != null ? avatarRoot.transform : null,
                targetRenderer,
                bones != null ? bones.TargetArmaturePath : string.Empty,
                "target",
                "the avatar root",
                issues);

            var partArmature = ResolveArmature(
                selection.PartRoot != null ? selection.PartRoot.transform : null,
                partRenderer,
                bones != null ? bones.PartArmaturePath : string.Empty,
                "part",
                "the part root",
                issues);

            var baseSnapshot = CaptureBase(avatarRoot, targetRenderer, targetArmature, policy, issues);
            if (baseSnapshot == null) return new ApaAuthoringContextResult(null, ValidationResult.Build(issues));

            var partSnapshot = CapturePart(
                avatarRoot,
                targetRenderer,
                partRenderer,
                partArmature,
                profile,
                policy,
                selection.PartMesh == null ? selection.ProtectedData : null,
                issues);
            if (partSnapshot == null) return new ApaAuthoringContextResult(null, ValidationResult.Build(issues));

            var context = new ValidationContext(
                baseSnapshot,
                ValidationContext.SortParts(new List<PartSnapshot> { partSnapshot }),
                policy,
                profile.Compatibility);

            return new ApaAuthoringContextResult(context, ValidationResult.Build(issues));
        }

        /// <summary>
        /// Resolves one armature selection, or returns null when the source does not need one.
        /// </summary>
        /// <remarks>
        /// A renderer with no bone list has no bone identity to scope, so no selection is demanded for it and the
        /// empty scope is correct. Everything else goes through <see cref="ApaArmatureScope.TryResolve"/>, which
        /// is the same routine the build uses and reports the same <c>APA043</c> diagnostics.
        /// </remarks>
        private static Transform ResolveArmature(
            Transform root,
            Renderer renderer,
            string path,
            string side,
            string rootLabel,
            List<ValidationIssue> issues)
        {
            if (!ApaArmatureScope.RequiresScope(renderer)) return null;

            var partId = string.Empty;
            ApaArmatureScope.TryResolve(root, path, side, partId, rootLabel, issues, out var armature);
            return armature;
        }

        /// <summary>
        /// True when every object a snapshot needs is present and readable.
        /// </summary>
        /// <remarks>
        /// Only the defects that literally prevent reading a mesh stop the build. A blocking selection defect
        /// that the snapshot can survive — a renderer outside its root, or a target outside the avatar root — is
        /// deliberately <i>not</i> checked here: the context is still built so that the rule set can report every
        /// remaining problem in one pass, and the selection defect itself is already in the issue list. The
        /// author then sees the whole picture instead of clearing defects one at a time.
        /// </remarks>
        private static bool CanSnapshot(ApaAuthoringSelection selection)
        {
            if (selection.AvatarRoot == null) return false;
            if (selection.TargetRenderer == null || selection.TargetMesh == null) return false;
            if (!selection.TargetMesh.isReadable) return false;
            if (selection.PartRenderer == null) return false;

            // A protected part has no live mesh by construction. Its payload is the geometry, and it is read
            // through the same decode cache the build uses; a payload that cannot be used leaves the context
            // unbuilt, exactly as an unreadable mesh does, and the selection check has already reported why.
            if (selection.PartMesh == null) return selection.PartGeometryMesh != null;
            if (!selection.PartMesh.isReadable) return false;

            return true;
        }

        private static BaseSnapshot CaptureBase(
            GameObject avatarRoot,
            Renderer targetRenderer,
            Transform targetArmature,
            ApaNumericPolicy policy,
            List<ValidationIssue> issues)
        {
            var mesh = ApaCompatibilityCapture.ResolveMesh(targetRenderer);
            if (mesh == null) return null;

            // Scoped to the selected target armature: a body bone's identity is its path relative to that
            // armature, which is the scope the final bone table merges part bones in.
            var boneSignature = MeshSnapshotFactory.CaptureBoneSignature(targetArmature, targetRenderer);

            var snapshot = MeshSnapshotFactory.Capture(
                mesh,
                boneSignature.Paths,
                out var captureIssues,
                MeshSnapshotFactory.CaptureBoneWorldToLocalMatrices(targetRenderer));
            issues.AddRange(captureIssues);
            if (snapshot == null) return null;

            ApaArmatureScope.ValidateWeightedBoneScopes(
                targetRenderer, targetArmature, snapshot, policy, string.Empty, "the target body", issues);

            if (!MeshSnapshotFactory.TryCaptureTransforms(
                    avatarRoot.transform,
                    targetRenderer.transform,
                    targetRenderer.transform,
                    string.Empty,
                    "the target renderer '" + targetRenderer.name + "'",
                    issues,
                    out var transforms))
            {
                return null;
            }

            var materials = MeshSnapshotFactory.CaptureMaterials(targetRenderer);
            var path = MeshSnapshotFactory.RelativePath(avatarRoot.transform, targetRenderer.transform);

            return new BaseSnapshot(
                snapshot,
                transforms,
                materials,
                path,
                ApaCore.InferUvSemantics(snapshot),
                ApaCore.InferMaterialSemantics(snapshot.SubMeshCount, materials),
                MeshSnapshotFactory.CaptureRendererLocalToWorld(targetRenderer),
                targetArmature != null
                    ? MeshSnapshotFactory.RelativePath(avatarRoot.transform, targetArmature)
                    : string.Empty);
        }

        private static PartSnapshot CapturePart(
            GameObject avatarRoot,
            Renderer targetRenderer,
            Renderer partRenderer,
            Transform partArmature,
            ApaPartProfile profile,
            ApaNumericPolicy policy,
            ApaProtectedMeshData protectedData,
            List<ValidationIssue> issues)
        {
            var mesh = ApaCompatibilityCapture.ResolveMesh(partRenderer);
            if (mesh == null && protectedData == null) return null;

            var partId = profile.Identity != null ? profile.Identity.PartId ?? string.Empty : string.Empty;

            // Scoped to the selected part armature, which is what makes a part bone and a body bone with the same
            // relative path one joint.
            var partBoneSignature = MeshSnapshotFactory.CaptureBoneSignature(partArmature, partRenderer);

            MeshSnapshot snapshot;
            if (protectedData != null)
            {
                // The payload is the geometry, and the bone identity is the live one, exactly as the unprotected
                // capture records it. Decoding straight to a snapshot would have made the payload's authoring-time
                // bone paths authoritative; passing the live paths keeps both paths identical.
                snapshot = protectedData.CreateSnapshot(
                    partBoneSignature.Paths,
                    MeshSnapshotFactory.CaptureBoneWorldToLocalMatrices(partRenderer));
            }
            else
            {
                snapshot = MeshSnapshotFactory.Capture(
                    mesh,
                    partBoneSignature.Paths,
                    out var captureIssues,
                    MeshSnapshotFactory.CaptureBoneWorldToLocalMatrices(partRenderer));
                issues.AddRange(captureIssues);
            }

            if (snapshot == null) return null;

            ApaArmatureScope.ValidateWeightedBoneScopes(
                partRenderer, partArmature, snapshot, policy, partId, "part '" + partId + "'", issues);

            if (!MeshSnapshotFactory.TryCaptureTransforms(
                    avatarRoot.transform,
                    partRenderer.transform,
                    targetRenderer.transform,
                    partId,
                    "part '" + partId + "'",
                    issues,
                    out var transforms))
            {
                return null;
            }

            var materials = MeshSnapshotFactory.CaptureMaterials(partRenderer);

            // An empty declaration means "infer the conventional name", exactly as the installer-driven builder
            // treats it, so a simple part validates without any semantic configuration.
            var uvSemantics = profile.UvSemantics.Length > 0
                ? profile.UvSemantics
                : ApaCore.InferUvSemantics(snapshot);

            var materialSemantics = profile.MaterialSemantics.Length > 0
                ? profile.MaterialSemantics
                : ApaCore.InferMaterialSemantics(snapshot.SubMeshCount, materials);

            var orderingKey = new PartOrderingKey(
                partId,
                MeshSnapshotFactory.RelativePath(avatarRoot.transform, partRenderer.transform));

            return new PartSnapshot(
                partId,
                string.Empty,
                ApaPartSlot.Custom,
                orderingKey,
                snapshot,
                transforms,
                uvSemantics,
                materialSemantics,
                profile.Removal != null
                    ? profile.Removal.RemovedTriangles.Addresses
                    : Array.Empty<RemovedTriangleAddress>(),
                profile.Seam,
                materials,
                PartPolicySnapshot.FromProfile(profile),
                partRenderer,
                profile.PartMeshFingerprint);
        }
    }
}

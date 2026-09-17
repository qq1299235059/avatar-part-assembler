using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor
{
    /// <summary>
    /// Resolves and checks the two explicit Armature selections a profile carries (M10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The build pipeline (<see cref="ContextBuilder"/>) and the authoring window's context builder both need the
    /// same answers — is a scope needed at all, does the recorded path resolve inside the root it belongs to, and
    /// is every weighted bone inside the resolved armature — and they must give the same one, because otherwise
    /// the window would say a profile is ready and the build would refuse it. The rules therefore live here once.
    /// </para>
    /// <para>
    /// A bone identity is the bone's path relative to its own selected armature root, so the selection is
    /// load-bearing rather than advisory: without it there is no scope to record a path in and no defensible way
    /// to decide which part bone is the same joint as which body bone. Nothing here guesses one.
    /// </para>
    /// </remarks>
    public static class ApaArmatureScope
    {
        /// <summary>
        /// True when a renderer has bones whose identities need an armature scope.
        /// </summary>
        /// <remarks>
        /// A renderer with no bone list produces an empty bone signature: there is nothing to record, nothing to
        /// merge, and no weight to remap, so demanding a selection for it would be a requirement with no
        /// consequence.
        /// </remarks>
        public static bool RequiresScope(Renderer renderer)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null) return false;
            var bones = skinned.bones;
            return bones != null && bones.Length > 0;
        }

        /// <summary>
        /// Resolves the effective bone-identity scope for a part renderer before or after Modular Avatar armature merging.
        /// </summary>
        /// <remarks>
        /// The authoring contract remains strict: the stored part-armature path must be a real selection under the
        /// part root before the merge. During an actual NDMF build, Modular Avatar may then retarget every weighted
        /// bone onto the selected target armature while the part renderer itself survives for APA to consume.
        /// In that verified post-merge state the target armature is the correct live scope for capturing the same
        /// relative bone identities. Nothing is inferred from names and a missing selection is never accepted.
        /// </remarks>
        public static bool TryResolvePartCaptureScope(
            Transform partRoot,
            string partArmaturePath,
            Transform targetArmature,
            Renderer renderer,
            ApaNumericPolicy policy,
            bool allowPostMergeTargetScope,
            string partId,
            List<ValidationIssue> issues,
            out Transform armature)
        {
            armature = null;

            if (!allowPostMergeTargetScope)
            {
                return TryResolve(
                    partRoot,
                    partArmaturePath,
                    "part",
                    partId,
                    "the part root",
                    issues,
                    out armature);
            }

            Transform selectedPartArmature = null;
            if (partRoot != null && ApaAvatarPath.HasIdentity(partArmaturePath))
            {
                selectedPartArmature = ApaAvatarPath.IsRoot(partArmaturePath)
                    ? partRoot
                    : partRoot.Find(partArmaturePath);

                if (selectedPartArmature != null
                    && selectedPartArmature != partRoot
                    && !selectedPartArmature.IsChildOf(partRoot))
                {
                    selectedPartArmature = null;
                }
            }

            if (selectedPartArmature != null)
            {
                // Normal authoring / preview state: the renderer is still skinned to the selected part armature.
                // A selected armature with no effective weighted influences is also valid and should preserve the
                // existing behavior rather than manufacturing a post-merge fallback.
                if (!HasEffectiveWeightedInfluence(renderer, policy)
                    || WeightedBonesAreInside(renderer, selectedPartArmature, policy))
                {
                    armature = selectedPartArmature;
                    return true;
                }

                // Important for the case where PartArmaturePath == "." or MA retains the selected object as an
                // intermediate because it carries components: the path can still resolve even though MA has moved
                // every effective weighted bone into the target armature. Prefer the proven live target scope.
                if (WeightedBonesAreInside(renderer, targetArmature, policy))
                {
                    armature = targetArmature;
                    return true;
                }

                // Keep the selected scope so the existing APA044 weighted-bone validation reports the precise
                // authoring defect instead of turning it into an unrelated path error.
                armature = selectedPartArmature;
                return true;
            }

            // After a successful MA merge the selected part armature can legitimately disappear from beneath the
            // part root. Accept that state only when a real selection was serialized and the live renderer proves
            // that all effective weighted bones are now inside the already-resolved target armature.
            if (ApaAvatarPath.HasIdentity(partArmaturePath)
                && WeightedBonesAreInside(renderer, targetArmature, policy))
            {
                armature = targetArmature;
                return true;
            }

            // Not a verified post-merge state: preserve the strict M10 diagnostic and remedy.
            return TryResolve(
                partRoot,
                partArmaturePath,
                "part",
                partId,
                "the part root",
                issues,
                out armature);
        }

        private static bool HasEffectiveWeightedInfluence(Renderer renderer, ApaNumericPolicy policy)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null || skinned.sharedMesh == null || !skinned.sharedMesh.isReadable) return false;

            var weights = skinned.sharedMesh.boneWeights;
            if (weights == null || weights.Length == 0) return false;

            var effective = policy ?? ApaNumericPolicy.Default;
            for (var i = 0; i < weights.Length; i++)
            {
                var weight = weights[i];
                if (!effective.IsNegligibleWeight(weight.weight0)
                    || !effective.IsNegligibleWeight(weight.weight1)
                    || !effective.IsNegligibleWeight(weight.weight2)
                    || !effective.IsNegligibleWeight(weight.weight3))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool WeightedBonesAreInside(
            Renderer renderer,
            Transform armature,
            ApaNumericPolicy policy)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null || armature == null) return false;

            var mesh = skinned.sharedMesh;
            var bones = skinned.bones;
            if (mesh == null || !mesh.isReadable || bones == null || bones.Length == 0) return false;

            var weights = mesh.boneWeights;
            if (weights == null || weights.Length == 0) return false;

            var effective = policy ?? ApaNumericPolicy.Default;
            var sawWeightedBone = false;

            for (var i = 0; i < weights.Length; i++)
            {
                var weight = weights[i];
                if (!InfluenceIsInside(bones, armature, weight.boneIndex0, weight.weight0, effective, ref sawWeightedBone)
                    || !InfluenceIsInside(bones, armature, weight.boneIndex1, weight.weight1, effective, ref sawWeightedBone)
                    || !InfluenceIsInside(bones, armature, weight.boneIndex2, weight.weight2, effective, ref sawWeightedBone)
                    || !InfluenceIsInside(bones, armature, weight.boneIndex3, weight.weight3, effective, ref sawWeightedBone))
                {
                    return false;
                }
            }

            return sawWeightedBone;
        }

        private static bool InfluenceIsInside(
            Transform[] bones,
            Transform armature,
            int index,
            float weight,
            ApaNumericPolicy policy,
            ref bool sawWeightedBone)
        {
            if (policy.IsNegligibleWeight(weight)) return true;
            if (index < 0 || index >= bones.Length) return false;

            sawWeightedBone = true;
            var bone = bones[index];
            if (bone == null) return false;
            return bone == armature || bone.IsChildOf(armature);
        }

        /// <summary>
        /// Resolves one selected armature path under the root it must live in, with a strict ancestor check.
        /// </summary>
        /// <param name="root">The root the path is relative to: the avatar root, or the part root.</param>
        /// <param name="path">The recorded path. <c>"."</c> is the root itself; the empty string is "missing".</param>
        /// <param name="side">A stable token naming the selection: <c>target</c> or <c>part</c>.</param>
        /// <param name="partId">Part identity used to anchor the diagnostic.</param>
        /// <param name="rootLabel">Human-readable description of the root, used in the message.</param>
        /// <param name="issues">Receives the blocking diagnostic.</param>
        /// <param name="armature">Receives the resolved transform on success.</param>
        /// <remarks>
        /// <para>
        /// Only a path that resolves <i>inside</i> the root is accepted. A path that resolves somewhere else — a
        /// scene-absolute path recorded by an older build, or a root-relative path that now names an object
        /// outside the root — is refused rather than used, because the point of the selection is that the two
        /// armatures are the corresponding bone levels of the part and of the body.
        /// </para>
        /// <para>
        /// A path that resolves from the scene root but not from this root is reported as "outside" rather than
        /// "not found": the two have different remedies (move the object, or re-select the armature), and the
        /// distinction is what the author needs to see.
        /// </para>
        /// </remarks>
        /// <returns>True when the armature resolved inside the root.</returns>
        public static bool TryResolve(
            Transform root,
            string path,
            string side,
            string partId,
            string rootLabel,
            List<ValidationIssue> issues,
            out Transform armature)
        {
            armature = null;

            if (!ApaAvatarPath.HasIdentity(path))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "No " + side + " armature is selected" +
                    (root != null ? " for " + rootLabel + " '" + root.name + "'" : string.Empty) +
                    ". A bone identity is recorded relative to the armature the author selects, so the " +
                    "selection is required before any bone can be matched. Open the Part Authoring window and " +
                    "select both armatures.",
                    partId,
                    detail: "reason=missing-" + side + "-armature"));
                return false;
            }

            if (root == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "The " + side + " armature path '" + path + "' cannot be resolved because there is no " +
                    rootLabel + ".",
                    partId,
                    detail: "reason=" + side + "-armature-not-found; path=" + path));
                return false;
            }

            var found = ApaAvatarPath.IsRoot(path) ? root : root.Find(path);
            if (found != null && (found == root || found.IsChildOf(root)))
            {
                armature = found;
                return true;
            }

            var outsideRoot = found == null && !ApaAvatarPath.IsRoot(path) && root.root.Find(path) != null;

            issues.Add(ValidationIssue.Error(
                ApaErrorCode.ArmatureSelectionInvalid,
                ApaIssuePhase.Compatibility,
                outsideRoot
                    ? "The " + side + " armature '" + path + "' exists in the scene but is not inside " +
                      rootLabel + " '" + root.name + "'. A bone identity is recorded relative to the selected " +
                      "armature, so an armature outside " + rootLabel + " has no path this pipeline can resolve " +
                      "after the build clone is relocated. Select the object inside " + rootLabel + " instead."
                    : "The " + side + " armature path '" + path + "' does not resolve to an object under " +
                      rootLabel + " '" + root.name + "'. Re-select the armature in the Part Authoring window; the " +
                      "hierarchy it was recorded against has changed.",
                partId,
                detail: "reason=" + (outsideRoot ? side + "-armature-outside-root" : side + "-armature-not-found") +
                        "; path=" + path + "; root=" + root.name));
            return false;
        }

        /// <summary>
        /// Reports every <i>weighted</i> bone that is not inside the armature selected for its source.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A weighted bone outside the selected armature records no identity (the signature records an empty path
        /// for it), so the vertex's weights could never be remapped onto the body bone they belong to; the part's
        /// bones would be appended as duplicates and the part would deform against transforms the author never
        /// authored against.
        /// </para>
        /// <para>
        /// <b>Only weighted bones are checked (the APA008 fix).</b> A renderer's bone list routinely carries
        /// slots no vertex references, and an unreferenced slot that happens to sit outside the armature cannot
        /// change any output: no weight reaches it, so it needs no identity. The check reads the effective
        /// influence set from the captured weights, with the same threshold the remap uses.
        /// </para>
        /// <para>
        /// A <c>null</c> bone entry is deliberately not reported here. It has no transform at all, which is a
        /// different defect with a different remedy, and the bone table builder already reports it as
        /// <c>APA008 reason=bone-without-identity</c>. One condition, one diagnostic.
        /// </para>
        /// </remarks>
        public static void ValidateWeightedBoneScopes(
            Renderer renderer,
            Transform armature,
            MeshSnapshot snapshot,
            ApaNumericPolicy policy,
            string partId,
            string sourceLabel,
            List<ValidationIssue> issues)
        {
            if (snapshot == null || armature == null || issues == null) return;

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null) return;

            var bones = skinned.bones;
            if (bones == null || bones.Length == 0) return;

            var weights = snapshot.SkinWeights;
            if (weights.Count == 0) return;

            var effective = policy ?? ApaNumericPolicy.Default;
            var reported = new SortedSet<int>();

            for (var v = 0; v < weights.Count; v++)
            {
                var weight = weights[v];
                ReportOutOfScope(reported, bones, armature, weight.boneIndex0, weight.weight0, effective);
                ReportOutOfScope(reported, bones, armature, weight.boneIndex1, weight.weight1, effective);
                ReportOutOfScope(reported, bones, armature, weight.boneIndex2, weight.weight2, effective);
                ReportOutOfScope(reported, bones, armature, weight.boneIndex3, weight.weight3, effective);
            }

            if (reported.Count == 0) return;

            var armaturePath = MeshSnapshotFactory.RelativePath(armature, armature);

            foreach (var index in reported)
            {
                var boneName = index < bones.Length && bones[index] != null ? bones[index].name : "(null)";
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.BoneOutsideSelectedArmature,
                    ApaIssuePhase.Compatibility,
                    "Bone " + index + " ('" + boneName + "') of " + sourceLabel + " carries weight but is not " +
                    "inside the armature selected for it ('" + armaturePath + "'). Its identity is recorded " +
                    "relative to that armature, so a bone outside it has no identity to merge on. Select the " +
                    "armature that contains the bones the mesh is actually skinned to, or move the bone into it.",
                    partId,
                    index,
                    detail: "reason=bone-outside-armature; bone=" + index + "; name=" + boneName +
                            "; armature=" + armaturePath));
            }
        }

        private static void ReportOutOfScope(
            SortedSet<int> reported,
            Transform[] bones,
            Transform armature,
            int index,
            float weight,
            ApaNumericPolicy policy)
        {
            if (index < 0 || index >= bones.Length) return;
            if (policy.IsNegligibleWeight(weight)) return;

            var bone = bones[index];
            if (bone == null) return;
            if (bone == armature || bone.IsChildOf(armature)) return;

            reported.Add(index);
        }
    }
}

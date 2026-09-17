using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// The final bone table resolved onto live transforms, in final bone order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan identifies a bone by its <b>armature-relative</b> path (M10): a base body bone's path is relative
    /// to the selected target armature, and a part bone's path is relative to the selected part armature. The
    /// preview runs on the <i>authoring</i> hierarchy, where the part's armature is still the part's own object,
    /// so each bone is resolved against the armature of its own side — the target armature for a body bone and for
    /// a bone the body already owns, the owning part's armature for a bone only that part contributes. Resolving
    /// everything against one root would leave every part bone unresolved, which is exactly what the previous
    /// avatar-root-relative identity hid.
    /// </para>
    /// <para>
    /// A <c>SkinnedMeshRenderer</c> needs <see cref="Transform"/> references in exactly the final bone order, so
    /// the resolution has to happen somewhere; it happens once per node, here, rather than on every frame.
    /// </para>
    /// <para>
    /// A path that does not resolve produces a null entry rather than a guess. Unity accepts a null in a bones
    /// array and renders that vertex against the renderer's own transform, which shows the user that something is
    /// wrong instead of silently skinning to the wrong joint. The missing paths are retained so the node can
    /// report them.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewBoneMap
    {
        private static readonly Transform[] s_noBones = new Transform[0];
        private static readonly string[] s_noPaths = new string[0];

        /// <summary>Bones in final table order. Entries may be null when a path did not resolve.</summary>
        public Transform[] Bones { get; }

        /// <summary>
        /// The armature root a root-token bone resolved to, else null.
        /// </summary>
        /// <remarks>
        /// Since M10 a <see cref="ApaAvatarPath.Root"/> path names the <i>selected armature</i>, not the avatar
        /// root, so this is the armature that actually carries a bone entry for it.
        /// </remarks>
        public Transform RootBone { get; }

        /// <summary>Paths, in final order, that did not resolve to a transform.</summary>
        public IReadOnlyList<string> MissingPaths { get; }

        /// <summary>True when the plan carries a bone table at all.</summary>
        public bool HasSkinning => Bones.Length > 0;

        /// <summary>True when at least one bone path did not resolve.</summary>
        public bool HasMissingBones => MissingPaths.Count > 0;

        private ApaPreviewBoneMap(Transform[] bones, Transform rootBone, IReadOnlyList<string> missingPaths)
        {
            Bones = bones;
            RootBone = rootBone;
            MissingPaths = missingPaths;
        }

        /// <summary>A map for a plan without skinning.</summary>
        public static ApaPreviewBoneMap Empty { get; } = new ApaPreviewBoneMap(s_noBones, null, s_noPaths);

        /// <summary>Resolves a plan's final bone table against the two selected armatures.</summary>
        /// <param name="plan">The plan whose table is resolved.</param>
        /// <param name="avatarRoot">The avatar root the selections are resolved under.</param>
        /// <param name="installers">
        /// The group's installers, in canonical order. Their profiles carry the two armature selections, which is
        /// the only statement about which hierarchy a bone path is relative to.
        /// </param>
        public static ApaPreviewBoneMap Resolve(
            MeshAssemblyPlan plan,
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            if (plan == null || plan.BoneTable == null || plan.BoneTable.Count == 0) return Empty;

            var targetArmature = ResolveTargetArmature(avatarRoot, installers);
            var partArmatures = ResolvePartArmatures(installers);

            var bones = plan.BoneTable.Bones;
            var transforms = new Transform[bones.Count];
            var missing = new List<string>();
            Transform rootBone = null;

            for (var i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                var path = bone != null ? bone.Path : string.Empty;
                var scope = ResolveScope(bone, targetArmature, partArmatures);
                var transform = ResolvePath(scope, path);
                transforms[i] = transform;

                if (transform == null)
                {
                    missing.Add(ApaAvatarPath.HasIdentity(path) ? path : "(missing identity)");
                    continue;
                }

                if (ApaAvatarPath.IsRoot(path)) rootBone = transform;
            }

            return new ApaPreviewBoneMap(transforms, rootBone, missing);
        }

        /// <summary>
        /// The target armature the group selected, or null when none is selected or it does not resolve.
        /// </summary>
        /// <remarks>
        /// A resolution failure is not reported here: the preview draws whatever the plan produced, and a plan
        /// whose selection is missing was already blocked with <c>APA043</c> by the capture that produced it. The
        /// diagnostic list this routine would fill is therefore discarded rather than duplicated into the overlay.
        /// </remarks>
        private static Transform ResolveTargetArmature(
            GameObject avatarRoot,
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            if (avatarRoot == null || installers == null) return null;

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                var bones = installer != null && installer.Profile != null ? installer.Profile.BonesOrNull : null;
                var path = bones != null ? bones.TargetArmaturePath : string.Empty;
                if (!ApaAvatarPath.HasIdentity(path)) continue;

                return ApaArmatureScope.TryResolve(
                    avatarRoot.transform,
                    path,
                    "target",
                    ApaPartIdentityResolver.ResolvePartId(installer),
                    "the avatar root",
                    new List<ValidationIssue>(),
                    out var armature)
                    ? armature
                    : null;
            }

            return null;
        }

        /// <summary>Part id to the part armature that part's own bones are relative to.</summary>
        private static Dictionary<string, Transform> ResolvePartArmatures(
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (installers == null) return map;

            for (var i = 0; i < installers.Count; i++)
            {
                var installer = installers[i];
                if (installer == null) continue;

                // The bone scope table is keyed by the same part identity the plan uses, so a legacy profile's
                // derived id has to resolve here too or its bones would lose their scope in preview only.
                var partId = ApaPartIdentityResolver.ResolvePartId(installer);
                if (string.IsNullOrEmpty(partId) || map.ContainsKey(partId)) continue;

                var bones = installer.Profile != null ? installer.Profile.BonesOrNull : null;
                var path = bones != null ? bones.PartArmaturePath : string.Empty;
                if (!ApaAvatarPath.HasIdentity(path)) continue;

                var partRoot = installer.ResolvePartRoot();
                if (partRoot == null) continue;

                if (ApaArmatureScope.TryResolve(
                        partRoot.transform,
                        path,
                        "part",
                        partId,
                        "the part root",
                        new List<ValidationIssue>(),
                        out var armature))
                {
                    map.Add(partId, armature);
                }
            }

            return map;
        }

        /// <summary>The armature a bone's path is relative to.</summary>
        /// <remarks>
        /// A bone the target body owns — including one a part merged onto — is relative to the target armature. A
        /// bone only a part contributes is relative to that part's armature, because the merge has not happened in
        /// the hierarchy the preview draws on.
        /// </remarks>
        private static Transform ResolveScope(
            FinalBone bone,
            Transform targetArmature,
            IReadOnlyDictionary<string, Transform> partArmatures)
        {
            if (bone == null) return targetArmature;
            if (string.IsNullOrEmpty(bone.OwnerPartId)) return targetArmature;
            if (partArmatures == null) return targetArmature;

            return partArmatures.TryGetValue(bone.OwnerPartId, out var armature) && armature != null
                ? armature
                : targetArmature;
        }

        /// <summary>Resolves one armature-relative path, handling the reserved root token.</summary>
        /// <remarks>
        /// <c>Transform.Find</c> reserves <c>"."</c>, so the root token is handled before the lookup rather than
        /// passed through it. Since M10 the token names the selected armature rather than the avatar root, so this
        /// takes the armature as its root.
        /// </remarks>
        public static Transform ResolvePath(Transform armatureRoot, string path)
        {
            if (armatureRoot == null || !ApaAvatarPath.HasIdentity(path)) return null;

            return ApaAvatarPath.IsRoot(path) ? armatureRoot : armatureRoot.Find(path);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "ApaPreviewBoneMap(" + Bones.Length + " bones, " + MissingPaths.Count + " unresolved)";
        }
    }

    /// <summary>
    /// One final blend shape, and where its weight is read from on each frame.
    /// </summary>
    /// <remarks>
    /// A generated mesh can carry shapes contributed by the body and by parts. The final shape order is fixed by
    /// the plan (body shapes first, then part shapes), so the mapping has to be explicit: copying weights by index
    /// from the body alone would drive a part-only shape with the body's weight at that index, or leave it at
    /// zero.
    /// </remarks>
    public readonly struct ApaPreviewBlendShapeBinding
    {
        /// <summary>Index on the generated mesh.</summary>
        public readonly int FinalShape;

        /// <summary>Source key: an empty string for the target body, otherwise the part id.</summary>
        public readonly string SourceKey;

        /// <summary>Shape index inside the source mesh, or -1 when the source has no such shape.</summary>
        public readonly int SourceShape;

        /// <summary>The live renderer the weight is read from, or null when it no longer exists.</summary>
        public readonly Renderer SourceRenderer;

        /// <summary>Creates a binding.</summary>
        public ApaPreviewBlendShapeBinding(int finalShape, string sourceKey, int sourceShape, Renderer sourceRenderer)
        {
            FinalShape = finalShape;
            SourceKey = sourceKey ?? string.Empty;
            SourceShape = sourceShape;
            SourceRenderer = sourceRenderer;
        }
    }

    /// <summary>Builds the final-shape to source-weight mapping from a plan.</summary>
    public static class ApaPreviewBlendShapeMap
    {
        /// <summary>
        /// Maps every final shape of a plan onto the renderer and shape index its weight comes from.
        /// </summary>
        /// <param name="plan">The assembly plan.</param>
        /// <param name="bodyRenderer">The target body renderer, used for shapes whose primary source is the body.</param>
        /// <param name="partRenderers">Part id to part renderer, as resolved by discovery.</param>
        public static List<ApaPreviewBlendShapeBinding> Build(
            MeshAssemblyPlan plan,
            Renderer bodyRenderer,
            Dictionary<string, Renderer> partRenderers)
        {
            var bindings = new List<ApaPreviewBlendShapeBinding>();
            if (plan == null || plan.BlendShapes == null || plan.BlendShapes.IsEmpty) return bindings;

            var shapes = plan.BlendShapes.Shapes;
            for (var i = 0; i < shapes.Count; i++)
            {
                var shape = shapes[i];
                if (shape == null)
                {
                    bindings.Add(new ApaPreviewBlendShapeBinding(i, string.Empty, -1, null));
                    continue;
                }

                var sourceKey = shape.PrimarySource ?? string.Empty;
                Renderer sourceRenderer;
                if (sourceKey.Length == 0)
                {
                    sourceRenderer = bodyRenderer;
                }
                else if (partRenderers == null || !partRenderers.TryGetValue(sourceKey, out sourceRenderer))
                {
                    sourceRenderer = null;
                }

                bindings.Add(new ApaPreviewBlendShapeBinding(
                    i,
                    sourceKey,
                    shape.SourceIndexFor(sourceKey),
                    sourceRenderer));
            }

            return bindings;
        }
    }

    /// <summary>
    /// Writes the assembled mesh, materials, bones, and blend shape weights onto a proxy renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs from <c>IRenderFilterNode.OnFrame</c>, not from <c>Instantiate</c>. NDMF re-copies the original's
    /// mesh, materials, bones, root bone, bounds, and blend shape weights onto every proxy at the start of every
    /// frame, so a value written once during instantiation would be gone before the first draw. Running here is
    /// also what makes the preview track live renderer state — a blend shape weight the user is scrubbing, or a
    /// bone the user is dragging — without rebuilding the assembly.
    /// </para>
    /// <para>
    /// Nothing here writes to the original renderer, its mesh, its materials, or any asset. The only objects
    /// touched are the proxy renderer handed in by the pipeline and, for a <c>MeshRenderer</c> proxy, that proxy's
    /// own <c>MeshFilter</c>.
    /// </para>
    /// </remarks>
    public static class ApaPreviewProxyApplier
    {
        /// <summary>Applies a generated mesh and its associated state to one proxy renderer.</summary>
        /// <param name="proxy">The proxy renderer created by NDMF for the original body renderer.</param>
        /// <param name="mesh">The generated mesh. Null means "blocked"; nothing is written.</param>
        /// <param name="materials">Final materials; asset references, assigned but never owned.</param>
        /// <param name="boneMap">Final bones resolved onto live transforms, or null for a static mesh.</param>
        /// <param name="shapeBindings">Blend shape weight sources, or null when the mesh has no shapes.</param>
        public static void Apply(
            Renderer proxy,
            Mesh mesh,
            Material[] materials,
            ApaPreviewBoneMap boneMap,
            IReadOnlyList<ApaPreviewBlendShapeBinding> shapeBindings)
        {
            if (proxy == null || mesh == null) return;

            var skinned = proxy as SkinnedMeshRenderer;
            if (skinned != null)
            {
                skinned.sharedMesh = mesh;
                ApplyBones(skinned, boneMap);

                // NDMF copies the original's local bounds onto the proxy every frame. The assembled mesh can be
                // larger than the body it replaces (it contains the part geometry), and stale bounds cull the
                // part region at the edges of the view, so the generated bounds win.
                skinned.localBounds = mesh.bounds;

                ApplyBlendShapeWeights(skinned, shapeBindings);
            }
            else
            {
                var filter = proxy.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = mesh;
                proxy.localBounds = mesh.bounds;
            }

            // A material list that does not line up with the generated submeshes would make Unity render unrelated
            // slots, so it is refused rather than partially applied. An empty generated list means "leave the
            // renderer's materials alone".
            if (materials != null && materials.Length > 0 && materials.Length == mesh.subMeshCount)
            {
                proxy.sharedMaterials = materials;
            }
        }

        /// <summary>Reads the current weight of a binding's source shape, or zero when the source is gone.</summary>
        public static float ReadWeight(ApaPreviewBlendShapeBinding binding)
        {
            var source = binding.SourceRenderer as SkinnedMeshRenderer;
            if (source == null) return 0f;

            var mesh = source.sharedMesh;
            if (mesh == null) return 0f;
            if (binding.SourceShape < 0 || binding.SourceShape >= mesh.blendShapeCount) return 0f;

            return source.GetBlendShapeWeight(binding.SourceShape);
        }

        private static void ApplyBones(SkinnedMeshRenderer proxy, ApaPreviewBoneMap boneMap)
        {
            if (boneMap == null || !boneMap.HasSkinning) return;

            proxy.bones = boneMap.Bones;
            if (boneMap.RootBone != null) proxy.rootBone = boneMap.RootBone;
        }

        private static void ApplyBlendShapeWeights(
            SkinnedMeshRenderer proxy,
            IReadOnlyList<ApaPreviewBlendShapeBinding> shapeBindings)
        {
            if (shapeBindings == null || shapeBindings.Count == 0) return;

            for (var i = 0; i < shapeBindings.Count; i++)
            {
                var binding = shapeBindings[i];
                proxy.SetBlendShapeWeight(binding.FinalShape, ReadWeight(binding));
            }
        }
    }
}

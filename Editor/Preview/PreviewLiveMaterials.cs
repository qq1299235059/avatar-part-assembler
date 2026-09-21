using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// The material list a preview node applies, resolved from the live renderers and profiles on every frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem this solves.</b> A preview node holds a generated mesh and the material list the assembly
    /// produced. The mesh is expensive to build — it is the whole body plus every part, and for a protected part
    /// it also cost a decryption — while the material list is four or five asset references. When an author swaps
    /// a material on the prefab renderer, or points a profile's material declaration at a different asset, the
    /// geometry is unchanged and only the references are stale. Applying the list captured at assembly time means
    /// the preview keeps showing the old material until something else forces a full rebuild.
    /// </para>
    /// <para>
    /// <b>What it does instead.</b> It re-resolves the assembly's material layout from the <i>current</i> renderer
    /// materials and the <i>current</i> profile declarations, through the same
    /// <see cref="MaterialResolver.CollectLiveSources"/> and <see cref="MaterialResolver.Resolve"/> the assembly
    /// uses. No mesh is read, no payload is decrypted, and no asset is created: the result is an array of
    /// references to materials the project already owns.
    /// </para>
    /// <para>
    /// <b>Cheap when nothing changed.</b> <see cref="Resolve"/> first compares a token of the live material
    /// identities — the renderers' <c>sharedMaterials</c> and every declared profile material — and returns the
    /// array it already has when the token is unchanged. An unchanged frame therefore costs a handful of array
    /// reads and no allocation.
    /// </para>
    /// <para>
    /// <b>It never clones and never writes.</b> Every entry is the author's own <see cref="Material"/> asset, which
    /// is also what makes editing one — its shader, textures, colours, or keywords — visible immediately: the
    /// proxy draws the same asset the author is editing, so the change is in the picture before any code runs.
    /// </para>
    /// <para>
    /// <b>A layout it cannot reproduce falls back.</b> If the live declarations resolve to a different slot
    /// structure than the mesh was built from — a semantic renamed by a material swap, a new conflict, a policy
    /// change — the material list that belongs to this mesh is the one captured with it, and the pipeline's
    /// fingerprint comparison is already rebuilding the node. The fallback keeps the frame coherent until that
    /// rebuild lands instead of applying a list whose length does not match the submeshes.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewLiveMaterials
    {
        private readonly MeshAssemblyPlan _plan;
        private readonly ValidationContext _context;
        private readonly Renderer _targetRenderer;
        private readonly IReadOnlyList<AvatarPartInstaller> _installers;
        private readonly Dictionary<string, Renderer> _partRenderers;

        private Material[] _materials;
        private long _token;
        private bool _valid;

        /// <summary>Number of times the material list was recomputed. Zero until the first change.</summary>
        public int Revision { get; private set; }

        /// <summary>The plan whose layout the resolved list must line up with.</summary>
        public MeshAssemblyPlan Plan => _plan;

        /// <summary>The material list most recently resolved, never null.</summary>
        public Material[] Materials => _materials ?? Array.Empty<Material>();

        /// <summary>True when the last resolution reproduced the plan's own layout.</summary>
        public bool ReproducedLayout { get; private set; }

        private ApaPreviewLiveMaterials(
            MeshAssemblyPlan plan,
            ValidationContext context,
            Renderer targetRenderer,
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            _plan = plan;
            _context = context;
            _targetRenderer = targetRenderer;
            _installers = installers ?? Array.Empty<AvatarPartInstaller>();
            _partRenderers = ApaPreviewDiscovery.BuildPartRendererMap(_installers);
            _materials = plan != null ? plan.MaterialLayout.ToMaterialArray() : Array.Empty<Material>();
            _valid = false;
        }

        /// <summary>Creates a resolver for one node's plan and captured inputs.</summary>
        public static ApaPreviewLiveMaterials Create(
            MeshAssemblyPlan plan,
            ValidationContext context,
            Renderer targetRenderer,
            IReadOnlyList<AvatarPartInstaller> installers)
        {
            return new ApaPreviewLiveMaterials(plan, context, targetRenderer, installers);
        }

        /// <summary>
        /// The material list for the current frame, recomputed only when a live material reference changed.
        /// </summary>
        public Material[] Resolve()
        {
            var token = ComputeToken();
            if (_valid && token == _token) return _materials;

            _token = token;
            _valid = true;
            Revision++;

            if (_plan == null || _context == null)
            {
                ReproducedLayout = false;
                return _materials;
            }

            try
            {
                // The issues are collected and dropped: a conflict is reported once by the assembly that consumes
                // the layout, and a per-frame re-resolution is not a second authority on the diagnostics.
                var layout = MaterialResolver.Resolve(
                    MaterialResolver.CollectLiveSources(
                        _context, LiveMaterials(_targetRenderer), LivePartMaterials(), LivePartSemantics(),
                        new List<ValidationIssue>()),
                    new List<ValidationIssue>());

                if (MatchesLayout(layout, _plan.MaterialLayout))
                {
                    _materials = layout.ToMaterialArray();
                    ReproducedLayout = true;
                }
                else
                {
                    // The live declarations describe a different slot structure, so this mesh's own list is the
                    // only one that lines up with its submeshes. The fingerprint comparison in the node is what
                    // turns that state into a rebuild.
                    _materials = _plan.MaterialLayout.ToMaterialArray();
                    ReproducedLayout = false;
                }
            }
            catch (Exception)
            {
                // A resolver failure must not escape into a per-frame callback: the captured list is still a
                // coherent answer for the mesh that is being drawn, and the diagnostics path reports the cause
                // when the assembly is rebuilt.
                _materials = _plan.MaterialLayout.ToMaterialArray();
                ReproducedLayout = false;
            }

            return _materials;
        }

        /// <summary>
        /// A token of every live material identity this list depends on.
        /// </summary>
        /// <remarks>
        /// FNV-1a over instance ids, the convention the rest of the preview uses. Reading
        /// <c>sharedMaterials</c> allocates an array per call, which is why the token exists: the resolution that
        /// follows it is the expensive half, and it runs only when this value changes.
        /// </remarks>
        private long ComputeToken()
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                hash = MixArray(hash, _targetRenderer);

                for (var i = 0; i < _installers.Count; i++)
                {
                    var installer = _installers[i];
                    if (installer == null) continue;

                    hash = Mix(hash, installer.GetInstanceID());
                    if (_partRenderers.TryGetValue(
                            ApaPartIdentityResolver.ResolvePartId(installer), out var renderer))
                    {
                        hash = MixArray(hash, renderer);
                    }

                    var profile = installer.Profile;
                    var semantics = profile != null ? profile.MaterialSemantics : null;
                    if (semantics == null) continue;

                    for (var s = 0; s < semantics.Length; s++)
                    {
                        var semantic = semantics[s];
                        if (semantic == null) continue;

                        hash = Mix(hash, semantic.SourceSubMesh);
                        hash = Mix(hash, semantic.Material != null ? semantic.Material.GetInstanceID() : 0);
                    }
                }

                return (long)hash;
            }
        }

        private static ulong MixArray(ulong hash, Renderer renderer)
        {
            if (renderer == null) return Mix(hash, 0);

            hash = Mix(hash, renderer.GetInstanceID());

            var materials = renderer.sharedMaterials;
            hash = Mix(hash, materials != null ? materials.Length : -1);
            if (materials == null) return hash;

            for (var i = 0; i < materials.Length; i++)
            {
                hash = Mix(hash, materials[i] != null ? materials[i].GetInstanceID() : 0);
            }

            return hash;
        }

        private static ulong Mix(ulong hash, long value)
        {
            unchecked
            {
                for (var shift = 0; shift < 64; shift += 8)
                {
                    hash ^= (byte)((value >> shift) & 0xFF);
                    hash *= 1099511628211UL;
                }

                return hash;
            }
        }

        private IReadOnlyList<Material> LiveMaterials(Renderer renderer)
        {
            if (renderer == null) return null;
            return renderer.sharedMaterials ?? Array.Empty<Material>();
        }

        private Dictionary<string, IReadOnlyList<Material>> LivePartMaterials()
        {
            var map = new Dictionary<string, IReadOnlyList<Material>>(StringComparer.Ordinal);

            for (var i = 0; i < _installers.Count; i++)
            {
                var installer = _installers[i];
                if (installer == null) continue;

                var partId = ApaPartIdentityResolver.ResolvePartId(installer);
                if (string.IsNullOrEmpty(partId) || map.ContainsKey(partId)) continue;

                _partRenderers.TryGetValue(partId, out var renderer);
                var materials = LiveMaterials(renderer);
                if (materials != null) map.Add(partId, materials);
            }

            return map;
        }

        private Dictionary<string, IReadOnlyList<ApaMaterialSlotSemantic>> LivePartSemantics()
        {
            var map = new Dictionary<string, IReadOnlyList<ApaMaterialSlotSemantic>>(StringComparer.Ordinal);

            for (var i = 0; i < _installers.Count; i++)
            {
                var installer = _installers[i];
                var profile = installer != null ? installer.Profile : null;
                if (profile == null) continue;

                var partId = ApaPartIdentityResolver.ResolvePartId(installer);
                if (string.IsNullOrEmpty(partId) || map.ContainsKey(partId)) continue;

                // The non-materializing accessor: a preview read must never assign a nested object into a shared
                // authoring asset, and an empty declaration means "the profile declares none", which the source
                // builder reads as "infer from the materials".
                map.Add(partId, profile.MaterialSemantics ?? Array.Empty<ApaMaterialSlotSemantic>());
            }

            return map;
        }

        /// <summary>
        /// True when two layouts place the same source submeshes in the same final slots.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The comparison is structural on purpose, and deliberately ignores the slot <i>semantic</i>. A semantic
        /// labels a slot; the assembler decides every submesh from the source-to-slot mapping, which is what is
        /// compared here. A material swap therefore does not break the comparison merely because the fallback
        /// semantic — the material's own name — changed with it, while a swap that genuinely moves geometry
        /// (merging a contribution into another slot, or separating one out) changes the mapping and is caught.
        /// </para>
        /// <para>
        /// The material asset behind a slot is exactly what is allowed to differ; everything that decides which
        /// submesh ends up in which slot is not.
        /// </para>
        /// </remarks>
        public static bool MatchesLayout(MaterialLayout live, MaterialLayout reference)
        {
            if (live == null || reference == null) return false;
            if (live.SlotCount != reference.SlotCount) return false;

            for (var i = 0; i < reference.SlotCount; i++)
            {
                var a = live.Slots[i];
                var b = reference.Slots[i];

                if (a.SourceSubMeshes.Count != b.SourceSubMeshes.Count) return false;

                foreach (var pair in b.SourceSubMeshes)
                {
                    if (!a.SourceSubMeshes.TryGetValue(pair.Key, out var subMesh)) return false;
                    if (subMesh != pair.Value) return false;
                }
            }

            return true;
        }
    }
}

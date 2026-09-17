using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AvatarPartAssembler.Editor.Preview
{
    /// <summary>
    /// One generated preview mesh and everything that must travel with it.
    /// </summary>
    /// <remarks>
    /// The mesh is always a fresh transient object produced by <c>ApaCore</c>. The materials are references to
    /// existing assets — the core never clones a material — so they are never destroyed here. Ownership of the
    /// mesh passes to whichever <see cref="ApaPreviewMeshCache"/> stores the value; a value that is never stored
    /// (because the build failed) owns nothing.
    /// </remarks>
    public sealed class ApaPreviewGeneratedMesh
    {
        /// <summary>The generated mesh, or null when the assembly was blocked.</summary>
        public Mesh Mesh { get; }

        /// <summary>Final materials, matching the generated submeshes. Asset references, never owned.</summary>
        public Material[] Materials { get; }

        /// <summary>The plan the mesh was built from, or null on failure.</summary>
        public MeshAssemblyPlan Plan { get; }

        /// <summary>Diagnostics from the assembly, in deterministic order.</summary>
        public ValidationResult Issues { get; }

        /// <summary>True when a mesh was produced.</summary>
        public bool Succeeded => Mesh != null;

        /// <summary>Creates a value around an assembly result.</summary>
        public ApaPreviewGeneratedMesh(Mesh mesh, Material[] materials, MeshAssemblyPlan plan, ValidationResult issues)
        {
            Mesh = mesh;
            Materials = materials ?? Array.Empty<Material>();
            Plan = plan;
            Issues = issues ?? ValidationResult.Empty;
        }

        /// <summary>Wraps an <see cref="AssemblyResult"/>.</summary>
        public static ApaPreviewGeneratedMesh From(AssemblyResult result)
        {
            if (result == null) return Failure(ValidationResult.Empty);
            return new ApaPreviewGeneratedMesh(result.Mesh, result.Materials, result.Plan, result.Issues);
        }

        /// <summary>A failed value that owns no resources.</summary>
        public static ApaPreviewGeneratedMesh Failure(ValidationResult issues)
        {
            return new ApaPreviewGeneratedMesh(null, Array.Empty<Material>(), null, issues);
        }
    }

    /// <summary>
    /// A cache entry: one generated mesh, keyed by the fingerprint of the inputs that produced it.
    /// </summary>
    public sealed class ApaPreviewCacheEntry
    {
        /// <summary>The input fingerprint this entry was built from.</summary>
        public string Fingerprint { get; }

        /// <summary>The generated mesh; owned by the cache and destroyed on eviction or teardown.</summary>
        public Mesh Mesh { get; }

        /// <summary>Final materials; asset references, never destroyed.</summary>
        public Material[] Materials { get; }

        /// <summary>The plan the mesh was built from.</summary>
        public MeshAssemblyPlan Plan { get; }

        /// <summary>Assembly diagnostics.</summary>
        public ValidationResult Issues { get; }

        /// <summary>How many live nodes are currently using this entry.</summary>
        public int LeaseCount { get; internal set; }

        /// <summary>True while at least one node is using this entry; a leased entry is never evicted.</summary>
        public bool IsLeased => LeaseCount > 0;

        internal long LastUseSequence { get; set; }

        internal ApaPreviewCacheEntry(
            string fingerprint,
            Mesh mesh,
            Material[] materials,
            MeshAssemblyPlan plan,
            ValidationResult issues,
            long sequence)
        {
            Fingerprint = fingerprint ?? string.Empty;
            Mesh = mesh;
            Materials = materials ?? new Material[0];
            Plan = plan;
            Issues = issues ?? ValidationResult.Empty;
            LeaseCount = 0;
            LastUseSequence = sequence;
        }

        internal void DestroyOwnedObjects()
        {
            if (Mesh == null) return;

            // Matches the core's own destruction policy: transient objects created by editor code are destroyed
            // immediately, because the editor is not in a play session where deferred destruction is meaningful.
            if (Application.isPlaying)
            {
                Object.Destroy(Mesh);
            }
            else
            {
                Object.DestroyImmediate(Mesh);
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "ApaPreviewCacheEntry(" + Fingerprint + ", leases=" + LeaseCount + ")";
        }
    }

    /// <summary>
    /// A node's claim on one cache entry.
    /// </summary>
    /// <remarks>
    /// Leases exist so that a cache eviction can never destroy a mesh that a live node is still drawing, and so
    /// that "who releases this mesh" has exactly one answer. <see cref="Dispose"/> is idempotent: NDMF may dispose
    /// a node during a pipeline swap while the previous pipeline's node is still referenced.
    /// </remarks>
    public sealed class ApaPreviewLease : IDisposable
    {
        private ApaPreviewMeshCache _owner;
        private readonly ApaPreviewCacheEntry _entry;
        private readonly ApaPreviewGeneratedMesh _uncached;

        /// <summary>The leased entry, or null for a lease over a failed (uncached) build.</summary>
        public ApaPreviewCacheEntry Entry => _entry;

        /// <summary>The mesh, or null when the build failed or the entry was destroyed by teardown.</summary>
        public Mesh Mesh => _entry != null ? _entry.Mesh : (_uncached != null ? _uncached.Mesh : null);

        /// <summary>The materials, never null.</summary>
        public Material[] Materials =>
            _entry != null ? _entry.Materials : (_uncached != null ? _uncached.Materials : Array.Empty<Material>());

        /// <summary>The plan, or null when the build failed.</summary>
        public MeshAssemblyPlan Plan => _entry != null ? _entry.Plan : (_uncached != null ? _uncached.Plan : null);

        /// <summary>Diagnostics of the build this lease holds, including a failed build's diagnostics.</summary>
        public ValidationResult Issues =>
            _entry != null ? _entry.Issues : (_uncached != null ? _uncached.Issues : ValidationResult.Empty);

        /// <summary>True when this lease holds a usable generated mesh.</summary>
        public bool IsValid => Mesh != null;

        /// <summary>The fingerprint of the leased entry, or an empty string for an uncached build.</summary>
        public string Fingerprint => _entry != null ? _entry.Fingerprint : string.Empty;

        internal ApaPreviewLease(ApaPreviewMeshCache owner, ApaPreviewCacheEntry entry, ApaPreviewGeneratedMesh uncached)
        {
            _owner = owner;
            _entry = entry;
            _uncached = uncached;
        }

        /// <summary>A lease over a build that produced no mesh; it owns nothing and releases nothing.</summary>
        internal static ApaPreviewLease Uncached(ApaPreviewGeneratedMesh failed)
        {
            return new ApaPreviewLease(null, null, failed);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            var owner = _owner;
            if (owner == null) return;

            _owner = null;
            owner.Release(_entry);
        }
    }

    /// <summary>
    /// A bounded cache of generated preview meshes, keyed by input fingerprint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache exists because the preview pipeline rebuilds its node graph whenever anything in the scene
    /// changes, including things that do not change the assembled mesh. Rebuilding a body mesh on every such
    /// rebuild would make the preview unusable; keying by the content fingerprint means an unchanged input set
    /// reuses the exact mesh object the previous node drew.
    /// </para>
    /// <para>
    /// <b>Bounded.</b> At most <see cref="Capacity"/> entries are retained. Eviction is least-recently-used over
    /// entries with no live lease; an entry that a node is still drawing is never destroyed, so the cache can
    /// temporarily exceed its capacity by the number of live nodes, which is bounded by the number of visible
    /// avatar render groups rather than by edit history. Every evicted mesh is destroyed immediately.
    /// </para>
    /// <para>
    /// <b>Teardown.</b> Meshes created with <c>new Mesh()</c> are not assets, and Unity does not guarantee that a
    /// script-created object is collected on a domain reload. <see cref="ApaPreviewCacheTeardown"/> destroys the
    /// whole cache before an assembly reload, on quit, and on a play mode transition, so a reload cannot leak a
    /// mesh that no code can reach any more. A lease taken before a teardown observes a destroyed mesh
    /// (<see cref="ApaPreviewLease.IsValid"/> becomes false) rather than a dangling one.
    /// </para>
    /// <para>
    /// <b>Failures are not cached.</b> A build that produced no mesh is returned as an uncached lease. There is
    /// nothing to reuse, and caching the failure would keep a transient condition alive past the point where it
    /// was fixed.
    /// </para>
    /// <para>
    /// This class is main-thread only, like the rest of the preview system.
    /// </para>
    /// </remarks>
    public sealed class ApaPreviewMeshCache
    {
        /// <summary>Default number of retained entries.</summary>
        /// <remarks>
        /// Four is chosen so that a scene with the body plus a couple of parts, and one stale generation during a
        /// pipeline swap, all fit without an eviction, while an accidental per-edit accumulation cannot grow
        /// without bound.
        /// </remarks>
        public const int DefaultCapacity = 4;

        private readonly Dictionary<string, ApaPreviewCacheEntry> _entries =
            new Dictionary<string, ApaPreviewCacheEntry>(StringComparer.Ordinal);

        private readonly int _capacity;
        private long _sequence;

        /// <summary>Creates a cache with the given capacity.</summary>
        public ApaPreviewMeshCache(int capacity)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        /// <summary>The shared cache used by the preview filter.</summary>
        public static ApaPreviewMeshCache Shared { get; } = new ApaPreviewMeshCache(DefaultCapacity);

        /// <summary>The maximum number of retained entries.</summary>
        public int Capacity => _capacity;

        /// <summary>Number of retained entries, including leased ones.</summary>
        public int Count => _entries.Count;

        /// <summary>Number of entries currently leased by a node.</summary>
        public int LeasedCount
        {
            get
            {
                var count = 0;
                foreach (var entry in _entries.Values)
                {
                    if (entry.IsLeased) count++;
                }

                return count;
            }
        }

        /// <summary>Retained fingerprints, sorted, for review and diagnostics.</summary>
        public IReadOnlyList<string> Fingerprints()
        {
            var keys = new List<string>(_entries.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>
        /// Returns a lease over the entry for a fingerprint, building it when it is not cached.
        /// </summary>
        /// <param name="fingerprint">Content fingerprint of the inputs.</param>
        /// <param name="build">
        /// Produces the mesh when the fingerprint is not cached. It must not throw; if it does, the exception is
        /// reported as a failure rather than escaping into the preview pipeline.
        /// </param>
        public ApaPreviewLease Acquire(string fingerprint, Func<ApaPreviewGeneratedMesh> build)
        {
            var key = fingerprint ?? string.Empty;

            ApaPreviewCacheEntry entry;
            if (_entries.TryGetValue(key, out entry))
            {
                entry.LeaseCount++;
                entry.LastUseSequence = ++_sequence;
                return new ApaPreviewLease(this, entry, null);
            }

            ApaPreviewGeneratedMesh generated;
            try
            {
                generated = build != null ? build() : null;
            }
            catch (Exception e)
            {
                ApaPreviewDiagnostics.ReportInternalFailure("ApaPreviewMeshCache build", e);
                generated = ApaPreviewGeneratedMesh.Failure(ValidationResult.Single(ValidationIssue.Error(
                    ApaErrorCode.InternalError,
                    ApaIssuePhase.Assembly,
                    ApaPreviewDiagnostics.FormatException("mesh assembly", e),
                    detail: "exception=" + e.GetType().FullName)));
            }

            if (generated == null || !generated.Succeeded)
            {
                // Not cached: there is no mesh to own, and a later rebuild should try again.
                return ApaPreviewLease.Uncached(generated);
            }

            entry = new ApaPreviewCacheEntry(
                key,
                generated.Mesh,
                generated.Materials,
                generated.Plan,
                generated.Issues,
                ++_sequence);

            entry.LeaseCount = 1;
            _entries[key] = entry;
            TrimToCapacity();
            return new ApaPreviewLease(this, entry, null);
        }

        /// <summary>Releases one lease. Called by <see cref="ApaPreviewLease.Dispose"/>.</summary>
        internal void Release(ApaPreviewCacheEntry entry)
        {
            if (entry == null) return;

            ApaPreviewCacheEntry stored;
            if (!_entries.TryGetValue(entry.Fingerprint, out stored) || !ReferenceEquals(stored, entry)) return;

            if (entry.LeaseCount > 0) entry.LeaseCount--;
            entry.LastUseSequence = ++_sequence;
        }

        /// <summary>
        /// Evicts least-recently-used entries until the cache is within capacity. Leased entries are never
        /// evicted.
        /// </summary>
        /// <returns>Number of entries destroyed.</returns>
        public int TrimToCapacity()
        {
            var destroyed = 0;
            while (_entries.Count > _capacity)
            {
                ApaPreviewCacheEntry victim = null;
                foreach (var entry in _entries.Values)
                {
                    if (entry.IsLeased) continue;
                    if (victim == null || entry.LastUseSequence < victim.LastUseSequence) victim = entry;
                }

                if (victim == null) break;

                _entries.Remove(victim.Fingerprint);
                victim.DestroyOwnedObjects();
                destroyed++;
            }

            return destroyed;
        }

        /// <summary>
        /// Destroys every entry, leased or not, and empties the cache.
        /// </summary>
        /// <remarks>
        /// Only for teardown and for tests: a leased entry is still being drawn by a live node, so clearing it
        /// during normal operation would blank a preview. Teardown is exactly the case where the node is about to
        /// be disposed by the same reload that destroys the mesh.
        /// </remarks>
        /// <returns>Number of entries destroyed.</returns>
        public int Clear()
        {
            var destroyed = 0;
            foreach (var entry in _entries.Values)
            {
                entry.DestroyOwnedObjects();
                entry.LeaseCount = 0;
                destroyed++;
            }

            _entries.Clear();
            return destroyed;
        }

        /// <summary>Clears the shared cache. Used by the domain teardown hook.</summary>
        public static void ClearShared()
        {
            Shared.Clear();
        }
    }

    /// <summary>
    /// Destroys the shared preview mesh cache when the editor state it belongs to goes away.
    /// </summary>
    /// <remarks>
    /// The preview pipeline already disposes its nodes on a domain reload; this hook covers the window where a
    /// reload or a quit happens while a node is still holding a generated mesh, and the code that would have
    /// released it never runs.
    /// </remarks>
    internal static class ApaPreviewCacheTeardown
    {
        [InitializeOnLoadMethod]
        private static void Init()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ApaPreviewMeshCache.ClearShared;
            EditorApplication.quitting += ApaPreviewMeshCache.ClearShared;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Preview is off in play mode (NDMF forces it off), so a mesh generated for the editor is dead weight
            // from the moment the transition starts.
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                ApaPreviewMeshCache.ClearShared();
            }
        }
    }
}

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Holds the resolved seam candidate sets of both sides, and decides when they may be resolved again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Resolving the part candidates reads <c>Mesh.colors32</c>, which allocates a full
    /// <see cref="UnityEngine.Color32"/> copy of the mesh's color channel, and the Scene View merge check then
    /// runs the world-position matcher over both meshes. None of that may happen once per repaint, and none of it
    /// may happen once per keystroke in the candidate-color field: the field accepts a complete color code, and
    /// the cache is invalidated exactly once when a valid code commits.
    /// </para>
    /// <para>
    /// <b>The rule, in one sentence.</b> The cached sets are reused until the inputs actually changed
    /// (<see cref="NeedsRefresh"/>), and a committed color edit or a changed selection drops them
    /// (<see cref="Invalidate"/>) so the next read resolves exactly once.
    /// </para>
    /// <para>
    /// <b>Deferral is gone with the color wheel.</b> The window used to keep serving the previous sets while the
    /// Unity color picker was being dragged, because the picker emitted a change for every intermediate value.
    /// The field is a text field now, and a malformed or incomplete code never reaches this cache at all — the
    /// window keeps the previous valid color — so there is no intermediate-value stream left to defer. A
    /// deferred state that nothing can enter would be dead code, and dead code in the rule that decides when an
    /// expensive read happens is worse than no code.
    /// </para>
    /// <para>
    /// <b>Invalidation drops the results, not only the flag.</b> <see cref="Target"/> and <see cref="Part"/> are
    /// null after <see cref="Invalidate"/>, so a caller that forgot to consult <see cref="NeedsRefresh"/> cannot
    /// serve a set resolved for a mesh, a selection, or a color that is no longer current.
    /// </para>
    /// <para>
    /// <b>No Unity dependency.</b> The class is plain state so the rule can be tested directly, without a window,
    /// a GUI event, or a mesh: a test can drive a hundred repaints and assert that the expensive path was
    /// requested zero times.
    /// </para>
    /// </remarks>
    public sealed class ApaSeamCandidateCache
    {
        private ApaSeamColorCandidateResult _target;
        private ApaSeamColorCandidateResult _part;
        private bool _hasValue;

        /// <summary>True when both sides hold a resolved result.</summary>
        public bool HasValue => _hasValue;

        /// <summary>The cached target-side result, or null before the first resolution and after invalidation.</summary>
        public ApaSeamColorCandidateResult Target => _target;

        /// <summary>The cached part-side result, or null before the first resolution and after invalidation.</summary>
        public ApaSeamColorCandidateResult Part => _part;

        /// <summary>
        /// True when the caller must resolve both sides again before reading them.
        /// </summary>
        /// <param name="inputsDirty">
        /// Whether something the resolution reads changed outside the color — the selection, a mesh, the draft, an
        /// undo. The window passes its own live-check dirty flag, so one invalidation still serves a whole repaint.
        /// </param>
        public bool NeedsRefresh(bool inputsDirty)
        {
            return !_hasValue || inputsDirty;
        }

        /// <summary>Stores a freshly resolved pair of results.</summary>
        /// <remarks>
        /// A null side stores "no value", so a caller that failed to resolve cannot leave a half-filled cache
        /// behind: the next read asks again rather than serving one side of a pair that was never resolved
        /// together.
        /// </remarks>
        public void Store(ApaSeamColorCandidateResult target, ApaSeamColorCandidateResult part)
        {
            _target = target;
            _part = part;
            _hasValue = target != null && part != null;
        }

        /// <summary>
        /// Drops the cached sets so the next allowed read resolves once.
        /// </summary>
        /// <remarks>
        /// Used for every change that is not a plain repaint: a committed color code, a selection change, a mesh
        /// edited in place, an undo, and a profile load. The results are dropped with the flag, so nothing that
        /// was resolved for the previous inputs can be served afterwards.
        /// </remarks>
        public void Invalidate()
        {
            _hasValue = false;
            _target = null;
            _part = null;
        }
    }
}

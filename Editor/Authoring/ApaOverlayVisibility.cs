namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Which Scene View overlay layers one repaint draws, and whether the status label is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value rather than four independent <c>if</c> statements in the draw path: the precedence between the
    /// three toolbar switches and the merge-check mode is the whole contract of the Scene View tool, and a reader
    /// — or a test — can evaluate it without a Scene View, a repaint, or a window.
    /// </para>
    /// <para>
    /// <see cref="StatusLabel"/> is enabled only for an active switch, so a completely clean Scene View remains
    /// clean while any enabled overlay explains what it is showing.
    /// </para>
    /// </remarks>
    public readonly struct ApaOverlayPlan
    {
        internal ApaOverlayPlan(
            bool removal,
            bool candidates,
            bool seams,
            bool mergeCheck,
            bool statusLabel)
        {
            Removal = removal;
            Candidates = candidates;
            Seams = seams;
            MergeCheck = mergeCheck;
            StatusLabel = statusLabel;
        }

        /// <summary>True when the red predicted-removal triangles are drawn.</summary>
        public bool Removal { get; }

        /// <summary>True when the green part seam-candidate vertices are drawn.</summary>
        public bool Candidates { get; }

        /// <summary>True when the stored seam pairs are drawn.</summary>
        public bool Seams { get; }

        /// <summary>True when the prospective pairing is drawn instead of the ordinary layers.</summary>
        public bool MergeCheck { get; }

        /// <summary>True when the on-screen status label is drawn.</summary>
        public bool StatusLabel { get; }

        /// <summary>True when at least one overlay layer is drawn.</summary>
        public bool DrawsAnyLayer => Removal || Candidates || Seams || MergeCheck;

        /// <summary>True when the repaint has anything at all to draw: a layer, or the status label.</summary>
        public bool DrawsAnything => DrawsAnyLayer || StatusLabel;
    }

    /// <summary>
    /// The one place the authoring overlay precedence is decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The three toolbar switches are the complete control surface.</b> There is no hidden master gate: removal,
    /// candidates, and merge check each have one visible switch in the top toolbar.
    /// </para>
    /// <para>
    /// <b>The merge check is a mode, not a layer.</b> While it is on it hides the removal, candidate, and
    /// stored-seam layers and draws the prospective pairing instead. It hides them rather than clearing their
    /// toggles, so leaving the mode restores exactly what was set before it.
    /// </para>
    /// <para>
    /// <b>The label is not a layer.</b> It is drawn whenever the merge check is on — the merge check's result is
    /// a count, and a count the author cannot read is not a diagnostic — and whenever either ordinary overlay is
    /// armed.
    /// </para>
    /// <para>
    /// The function is pure, so the gating is covered by offline tests instead of only by reading the draw path.
    /// </para>
    /// </remarks>
    public static class ApaOverlayVisibility
    {
        /// <summary>Evaluates the overlay plan for the current toggles.</summary>
        /// <param name="mergeCheck">Whether the merge-check mode is on.</param>
        /// <param name="showRemovalOverlay">Whether the red removal overlay is enabled.</param>
        /// <param name="showCandidateOverlay">Whether the green candidate and stored-seam overlays are enabled.</param>
        public static ApaOverlayPlan For(
            bool mergeCheck,
            bool showRemovalOverlay,
            bool showCandidateOverlay)
        {
            // The merge check wins over the ordinary layers: a screen carrying four colour vocabularies at once
            // cannot answer "what would generating the seam pair?".
            var merge = mergeCheck;
            var ordinary = !mergeCheck;

            return new ApaOverlayPlan(
                ordinary && showRemovalOverlay,
                ordinary && showCandidateOverlay,
                ordinary && showCandidateOverlay,
                merge,
                mergeCheck || showRemovalOverlay || showCandidateOverlay);
        }
    }
}

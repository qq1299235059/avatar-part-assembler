using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AvatarPartAssembler.Editor.Localization.ApaLocalization;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The Part Authoring window: the guided workflow that turns a scene part into a profile asset and a
    /// portable part prefab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why IMGUI.</b> UI Toolkit is available in Unity 2022.3, and this window was assessed against it. It is
    /// implemented in IMGUI because the window is a modal Scene View tool as much as it is a form: removal
    /// triangles and seam vertices are picked in the Scene View through <c>Handles</c>, which is IMGUI whatever
    /// the window is built with, and the dense numeric fallbacks next to those picks are exactly what
    /// <c>EditorGUILayout</c> and <c>ReorderableList</c> exist for. Building the window in UI Toolkit would add
    /// a second UI paradigm, hand-written reorder and drag behaviour, and UXML/USS assets that cannot be
    /// validated without opening the Editor — while leaving the Scene View half in IMGUI anyway. The
    /// specification allows either, and this choice keeps one paradigm and no new asset types.
    /// </para>
    /// <para>
    /// <b>Thin view over reviewable parts.</b> Every decision lives outside this class:
    /// <see cref="ApaAuthoringSelection"/> holds what is selected, <see cref="ApaProfileDraft"/> holds what is
    /// authored, <see cref="ApaAuthoringValidation"/> validates, <see cref="ApaProfileWriter"/> writes the
    /// profile, and <see cref="ApaPrefabGenerator"/> generates the prefab. This class draws controls, calls
    /// those parts, and reports what they returned.
    /// </para>
    /// <para>
    /// <b>Safety.</b> Nothing here writes an asset without passing
    /// <see cref="ApaAuthoringValidation.Validate"/> first, an existing asset is never replaced without an
    /// explicit confirmation, and every edit that touches author data goes through <see cref="Undo"/> so the
    /// author can undo a mis-click.
    /// </para>
    /// </remarks>
    public sealed class ApaAuthoringWindow : EditorWindow, IApaAuthoringSceneHost
    {
        /// <summary>Menu path of the window.</summary>
        /// <remarks>
        /// <b>One door, not two.</b> This is the plugin's documented authoring entry, and it is the English
        /// spelling of a single menu item. A second, permanently Chinese path used to sit next to it, which made
        /// the <c>Tools</c> menu list the same window twice; it was removed.
        /// <para>
        /// Localization happens in the attribute, not at runtime: the item label comes from the named constants
        /// below, and the Chinese spelling is compiled in only when <c>APA_CHINESE_MENU</c> is defined (see
        /// <see cref="OpenFromChineseMenu"/>). Exactly one of the two is ever registered.
        /// </para>
        /// <para>
        /// The root keeps Unity's canonical <c>Tools</c> segment in both spellings: registering a separate
        /// top-level <c>工具</c> path can collide with Unity's localized Tools menu and make its normal entries
        /// disappear. The <c>Tools</c> root is therefore never localized.
        /// </para>
        /// </remarks>
        public const string MenuPath = "Tools/Avatar Part Assembler/Part Authoring";

        /// <summary>English label of the submenu the window lives under.</summary>
        public const string MenuGroupLabel = "Avatar Part Assembler";

        /// <summary>Simplified Chinese label of the same submenu, registered instead of the English one.</summary>
        public const string ChineseMenuGroupLabel = "部件装配器";

        private const string WindowTitle = "Avatar Part Assembler";
        private const string UndoLabel = "Edit Avatar Part";

        /// <summary>Most rows any list in this window draws in one repaint.</summary>
        /// <remarks>
        /// The window-wide ceiling, deliberately left at its original value: it is the guarantee that no list can
        /// make a repaint draw an unbounded number of rows, and lowering it would silently cut off a list that
        /// legitimately has a few hundred entries.
        /// </remarks>
        private const int MaxListedRows = 200;

        /// <summary>Most removal addresses drawn when the list is expanded.</summary>
        /// <remarks>
        /// Much smaller than <see cref="MaxListedRows"/> on purpose: a mask-generated selection routinely holds
        /// thousands of addresses, and the list is a review affordance rather than the editing surface, so the
        /// expanded view shows a bounded sample and states how many more there are. The shared ceiling still
        /// applies on top of it, so raising this value alone can never make the list unbounded.
        /// </remarks>
        private const int MaxExpandedAddressRows = 28;

        /// <summary>Default grayscale threshold of the texture-mask selection.</summary>
        private const float DefaultMaskThreshold = 0.5f;

        [SerializeField] private ApaAuthoringSelection _selection = new ApaAuthoringSelection();
        [SerializeField] private ApaProfileDraft _draft = new ApaProfileDraft();
        [SerializeField] private string _profilePath = string.Empty;
        [SerializeField] private string _prefabPath = string.Empty;
        [SerializeField] private bool _captureSignatureOnTargetChange = true;
        [SerializeField] private bool _allowOverwrite;

        /// <summary>
        /// Whether <c>Create Part Prefab</c> publishes the part mesh as a protected payload. Off by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Window state, never profile data.</b> The toggle decides what one creation action writes; the
        /// resulting fact — the installer's reference to the payload — is stored on the prefab, and the profile
        /// keeps its ordinary shape. Adding this to <see cref="ApaPartProfile"/> would make every profile in every
        /// project carry a flag that says nothing about the part.
        /// </para>
        /// <para>
        /// <b>It changes creation only.</b> Saving a profile, validating, previewing, building, and
        /// <c>Update Installer On Prefab</c> are unaffected: they read whatever the prefab already carries, so an
        /// existing unprotected prefab keeps working exactly as before and a protected one keeps working after
        /// the toggle is switched back off.
        /// </para>
        /// <para>
        /// <b>It defaults to off.</b> Protection is a distribution decision with a real consequence — the source
        /// mesh must be kept out of the delivery and the payload must ship beside the prefab — so it is never
        /// chosen for the author by a remembered default.
        /// </para>
        /// </remarks>
        [SerializeField] private bool _protectPartMesh;
        // These are the complete Scene View overlay controls. They live in the top toolbar so the author can see
        // the active visual mode without scrolling to a mesh-specific section.
        [SerializeField] private bool _showCandidateOverlay = true;

        /// <summary>World-space tolerance of the seam generator. Window state, never profile data.</summary>
        /// <remarks>
        /// The tolerance is an input of one authoring action; the profile stores the pairs it produced. Keeping
        /// it on the window is what makes the saved profile independent of the value the author happened to try.
        /// The merge-check overlay reads the same value, so the prospective pairing is evaluated under exactly
        /// the rule the generate action would use.
        /// </remarks>
        [SerializeField] private float _seamTolerance = ApaSeamWorldMatcher.DefaultTolerance;

        /// <summary>
        /// The vertex color that selects the seam candidates of the <b>part</b> mesh.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Window state, never profile data: the profile stores the pairs the generator wrote. Changing the color
        /// therefore changes which vertices the <i>next</i> generation may pair and never rewrites an already
        /// authored seam.
        /// </para>
        /// <para>
        /// Stored as a <see cref="Color32"/> rather than a <see cref="Color"/> because that is the value the
        /// comparison uses: the matcher compares all four 8-bit channels of <c>Mesh.colors32</c> exactly, so
        /// keeping the author's choice in the same space removes any question of what a picker's float value
        /// rounded to. The field that edits it is a text field reading a color code
        /// (<see cref="ApaSeamColorCode"/>), so the value the author types is the value that is matched.
        /// </para>
        /// <para>
        /// <b>Only the part is filtered by it.</b> The target body is matched spatially: every body vertex may
        /// pair and position decides which ones do, so a body mesh without vertex colors is a normal body mesh.
        /// The default is opaque black (<see cref="ApaSeamVertexColorCandidates.DefaultColor"/>), displayed as
        /// <c>#000000</c>.
        /// </para>
        /// </remarks>
        [SerializeField] private Color32 _seamCandidateColor = ApaSeamVertexColorCandidates.DefaultColor;

        /// <summary>
        /// Whether the red predicted-removal triangle overlay is drawn. Off by default.
        /// </summary>
        /// <remarks>
        /// The overlay is a preview of one authoring input, not a permanent part of the Scene View, and on a
        /// masked body it covers a large part of the mesh. It is drawn only while this is on, and it is the only
        /// way the mask-generated removal set is visualized in the Scene View. Its switch is in the shared top
        /// toolbar with the other two overlay modes.
        /// </remarks>
        [SerializeField] private bool _showRemovalOverlay;

        /// <summary>
        /// Whether the merge-check overlay is the active Scene View mode. Off by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mode hides the removal, candidate, and stored-seam overlays and draws the prospective pairing of
        /// the selected candidate colors instead: matched candidates in green, candidates with no counterpart
        /// within the tolerance in red. It computes the pairing through the same matcher and tolerance seam
        /// generation uses, and writes nothing — enabling it can never change the stored seam or the profile.
        /// </para>
        /// <para>
        /// <b>It hides, it does not clear.</b> The removal and candidate toggles keep their values while the mode
        /// is on, so switching the mode off restores exactly the overlays that were set before it. That is
        /// deliberate: a mode that silently reset the author's toggles would make "leave the mode" a second,
        /// invisible edit.
        /// </para>
        /// </remarks>
        [SerializeField] private bool _mergeCheckOverlay;

        /// <summary>Whether the removal address list is expanded. Collapsed by default.</summary>
        [SerializeField] private bool _showAllAddresses;

        // Texture-mask selection settings. These live on the window, not in the profile: the mask is an authoring
        // input whose only lasting product is the canonical RemovedTriangleAddress set it generates. Keeping them
        // serialized means a reload does not lose the mask the author is working on; keeping them out of the
        // profile is what makes the profile independent of the texture and of this tool.
        [SerializeField] private Texture2D _maskTexture;
        [SerializeField] private int _maskUvChannel;
        [SerializeField] private float _maskThreshold = DefaultMaskThreshold;
        [SerializeField] private bool _maskInvert;
        [SerializeField] private ApaMaskApplyMode _maskApplyMode = ApaMaskApplyMode.ReplaceSelection;

        [NonSerialized] private ApaAuthoringSceneTool _sceneTool;
        [NonSerialized] private ValidationResult _validation;
        [NonSerialized] private ApaAuthoringPlanResult _dryRun;
        [NonSerialized] private ValidationResult _lastWriteIssues;
        [NonSerialized] private string _status = string.Empty;
        [NonSerialized] private string _maskResultText = string.Empty;
        [NonSerialized] private MessageType _maskResultType = MessageType.None;
        [NonSerialized] private string _seamResultText = string.Empty;
        [NonSerialized] private MessageType _seamResultType = MessageType.None;
        [NonSerialized] private Vector2 _scroll;

        // Cached live checks. Recomputing them on every repaint would re-read mesh index buffers and compare the
        // saved profile's serialized content many times a second; they are recomputed on input events and after
        // every action instead.
        [NonSerialized] private ValidationResult _selectionIssues;
        [NonSerialized] private ValidationResult _removalCheck;
        [NonSerialized] private ValidationResult _seamCheck;
        [NonSerialized] private bool _liveChecksDirty = true;

        // The resolved seam candidates of each side, cached per change. Resolving the part side reads
        // Mesh.colors32, which allocates a full Color32 copy of the mesh's color channel, and the Scene View merge
        // check then runs the world-position matcher: doing that on every repaint — or on every keystroke of the
        // color-code field — would allocate megabytes per second on a real part mesh. The cache owns the reuse
        // rule (see ApaSeamCandidateCache), the Scene View overlay reads the same cached results through the host
        // properties, and the generate action re-resolves unconditionally because that is the one place the
        // candidates are authoritative input.
        [NonSerialized] private ApaSeamCandidateCache _seamCandidates = new ApaSeamCandidateCache();

        /// <summary>The candidate cache, materialized on first use after a deserialization.</summary>
        private ApaSeamCandidateCache SeamCandidates =>
            _seamCandidates ?? (_seamCandidates = new ApaSeamCandidateCache());

        /// <summary>
        /// The text the candidate-color code field is editing.
        /// </summary>
        /// <remarks>
        /// A buffer rather than the color itself: the author's partially typed text has to stay visible while the
        /// field is being edited, and the committed <see cref="Color32"/> has to stay valid while it is malformed.
        /// It is derived from <see cref="_seamCandidateColor"/> the first time it is drawn and after any state
        /// reset, so it can never disagree with the color that is actually matched.
        /// </remarks>
        [NonSerialized] private string _seamColorInput;

        /// <summary>The localized reason the current text is not a color code, or empty.</summary>
        [NonSerialized] private string _seamColorError = string.Empty;

        [NonSerialized] private string _existingProfileState;
        [NonSerialized] private bool _signatureSafetyStale;
        [NonSerialized] private bool _signatureIdentityStale;
        [NonSerialized] private List<ValidationIssue> _draftDataIssues;

        /// <summary>
        /// Shows the authoring window. The menu label follows the language the editor was compiled with.
        /// </summary>
        /// <remarks>
        /// <c>MenuPath</c> is the documented English path and is what this method's callers use. The
        /// <c>[MenuItem]</c> attribute below registers the English spelling and is replaced by
        /// <see cref="OpenFromChineseMenu"/> when <c>APA_CHINESE_MENU</c> is defined. Exactly one of the two
        /// items exists, so the <c>Tools</c> menu never shows this window twice.
        /// </remarks>
#if !APA_CHINESE_MENU
        [MenuItem(MenuPath, false, 10)]
#endif
        public static ApaAuthoringWindow Open()
        {
            var window = GetWindow<ApaAuthoringWindow>(false, WindowTitle, true);
            window.Show();
            window.Focus();
            return window;
        }

#if APA_CHINESE_MENU
        /// <summary>Opens the window from the Chinese spelling of the same path.</summary>
        /// <remarks>
        /// Registered <b>instead of</b> the English item when <c>APA_CHINESE_MENU</c> is defined, never alongside
        /// it. The symbol is set in <c>dev.avatar-part-assembler.editor.asmdef</c>; removing it from the assembly's
        /// define list switches the menu back to English after Unity recompiles.
        /// </remarks>
        [MenuItem(
            "Tools/" + ChineseMenuGroupLabel + "/" + ApaLocalization.MenuPartAuthoringChinese,
            false,
            10)]
        public static void OpenFromChineseMenu()
        {
            Open();
        }
#endif

        /// <summary>
        /// Opens the window on an existing profile, replacing whatever the window was editing.
        /// </summary>
        /// <remarks>
        /// Used by the installer inspector's "Open Part Authoring" shortcut. The profile is read into a draft
        /// through the same transition the window's own load control uses
        /// (<see cref="ApaProfileLoader"/>), so the two entry points cannot produce different state; the asset
        /// itself is not modified until the author writes it explicitly.
        /// </remarks>
        public static ApaAuthoringWindow OpenWith(
            ApaPartProfile profile,
            GameObject partRoot,
            GameObject avatarRoot,
            SkinnedMeshRenderer targetRenderer)
        {
            var window = Open();

            if (partRoot != null)
            {
                window._selection.PartRoot = partRoot;
                window._selection.PartRenderer = partRoot.GetComponentInChildren<Renderer>(true);
            }

            if (avatarRoot != null) window._selection.AvatarRoot = avatarRoot;
            if (targetRenderer != null) window._selection.TargetRenderer = targetRenderer;

            if (profile != null)
            {
                // The selection roots are assigned first, because the profile's recorded armature paths are
                // resolved against them: the window must open with the selections the profile carries rather than
                // two empty fields that the next edit would re-record as "no selection".
                window.ApplyLoadedProfile(profile, AssetDatabase.GetAssetPath(profile));
            }
            else
            {
                window.InvalidateDerivedState();
            }

            window.Repaint();
            return window;
        }

        // ---- IApaAuthoringSceneHost ------------------------------------------------------------------

        /// <inheritdoc />
        public ApaAuthoringSelection Selection => _selection;

        /// <inheritdoc />
        public ApaRemovalMask Removal => _draft.Removal;

        /// <inheritdoc />
        public ApaSeamSelection Seam => _draft.Seam;

        /// <inheritdoc />
        public bool ShowCandidateOverlay => _showCandidateOverlay;

        /// <inheritdoc />
        public bool ShowRemovalOverlay => _showRemovalOverlay;

        /// <inheritdoc />
        public bool MergeCheckOverlay => _mergeCheckOverlay;

        /// <inheritdoc />
        public Color32 SeamCandidateColor => _seamCandidateColor;

        /// <inheritdoc />
        public float SeamTolerance => _seamTolerance;

        /// <inheritdoc />
        public ApaSeamColorCandidateResult TargetSeamCandidates
        {
            get
            {
                EnsureSeamCandidates();
                return _seamCandidates.Target;
            }
        }

        /// <inheritdoc />
        public ApaSeamColorCandidateResult PartSeamCandidates
        {
            get
            {
                EnsureSeamCandidates();
                return _seamCandidates.Part;
            }
        }

        // ---- Lifetime -------------------------------------------------------------------------------

        private void OnEnable()
        {
            titleContent = new GUIContent(Tr(WindowTitle));
            minSize = new Vector2(520f, 480f);

            if (_selection == null) _selection = new ApaAuthoringSelection();
            if (_draft == null) _draft = new ApaProfileDraft();
            if (_seamCandidates == null) _seamCandidates = new ApaSeamCandidateCache();
            _draft.EnsureInitialized();

            // A reopened window — after a close, a layout restore, or a domain reload — can come back with the
            // draft's recorded armature paths intact while the live Transform references it holds are gone. The
            // paths are the durable half of the selection, so they are re-resolved here, before the first repaint
            // draws the two object fields as empty and before any edit in the Selection block could re-record
            // them. Nothing is invented: a path that does not resolve leaves the field empty and is reported by
            // the selection check, and a selection the author made is never overwritten.
            RestoreArmatureSelections();

            // The Scene View callback is installed once per domain load by the registry, not by this window, so a
            // reload can never leave the overlays without a callback. The window contributes its tool instance.
            _sceneTool = new ApaAuthoringSceneTool(this);
            ApaAuthoringSceneToolRegistry.Register(_sceneTool);

            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;

            // The tab title is not reachable by the repaint sweep the language switch performs (it is window
            // chrome, not drawn content), so it is refreshed from the change notification instead.
            Changed -= OnLanguageChanged;
            Changed += OnLanguageChanged;

            MarkSelectionDirty();
        }

        private void OnDisable()
        {
            ApaAuthoringSceneToolRegistry.Unregister(_sceneTool);
            Undo.undoRedoPerformed -= OnUndoRedo;
            Changed -= OnLanguageChanged;

            // The Scene View tool owns a transient destination mesh for the evaluated-geometry bake. Dropping the
            // reference without disposing it would leave that native mesh behind on every close/reopen, so the
            // tool is torn down here rather than merely forgotten.
            _sceneTool?.Dispose();
            _sceneTool = null;

            // The selection owns the transient mesh decoded from a protected payload. A closed window must not
            // leave decoded geometry behind: nothing can reach it any more, and the next open would build a second
            // copy. Disposing here is the end of the session's lifetime contract for that object.
            _selection?.Dispose();
        }

        /// <summary>Refreshes the window chrome after a language switch.</summary>
        /// <remarks>
        /// The cached localized verdicts (the "existing profile" line, the signature staleness lines) are
        /// recomputed on the next repaint; the last status message is a composed sentence and stays as it was
        /// written, because it records what an action reported rather than describing the current state.
        /// </remarks>
        private void OnLanguageChanged()
        {
            titleContent = new GUIContent(Tr(WindowTitle));
            MarkSelectionDirty();
            Repaint();
        }

        private void OnUndoRedo()
        {
            // An undo can change a mesh in place without changing its instance or its size, and every cached
            // answer about the previous state — the vertex arrays, the resolved candidates, the merge-check
            // classification — would then describe a mesh that no longer exists.
            InvalidateDerivedState();
            Repaint();
            SceneView.RepaintAll();
        }

        private void MarkSelectionDirty()
        {
            _liveChecksDirty = true;
        }

        /// <summary>
        /// Re-resolves the two live armature references from the draft's recorded paths.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The recorded <see cref="ApaBoneProfile.TargetArmaturePath"/> and
        /// <see cref="ApaBoneProfile.PartArmaturePath"/> are the durable form of the armature selection: they
        /// survive a domain reload, a window reopen, and a profile load, while the live <see cref="Transform"/>
        /// references the object fields show do not. Every point at which the draft and the roots are both known —
        /// <see cref="OnEnable"/>, a profile load, and a root change — therefore re-resolves the paths into the
        /// live references before anything else reads the selection.
        /// </para>
        /// <para>
        /// <b>It fills, it never replaces.</b> Only an empty slot is filled, so a selection the author made is
        /// untouched and the suggestion flow stays what it is — a proposal for a genuinely empty selection. A path
        /// that does not resolve is left in the draft and the field stays empty; the selection check reports it as
        /// the missing armature it is, rather than the path being cleared or a different object being invented.
        /// </para>
        /// </remarks>
        /// <returns>True when at least one live reference was restored.</returns>
        private bool RestoreArmatureSelections(bool replaceExisting = false)
        {
            if (_selection == null || _draft == null) return false;

            _draft.EnsureInitialized();

            return !string.IsNullOrEmpty(_selection.RestoreArmatures(
                _draft.Bones.TargetArmaturePath,
                _draft.Bones.PartArmaturePath,
                replaceExisting));
        }

        /// <summary>
        /// Drops every cached value derived from the previous draft, selection, or mesh.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One entry point, because a stale derived value is not a cosmetic problem: the resolved seam candidates,
        /// the merge-check classification, the cached mesh arrays, the live checks, and the status lines are all
        /// answers about <i>some</i> inputs, and every one of them would keep being served after the inputs were
        /// replaced by a profile load, a new draft, an undo, or a selection change. A Scene View overlay reading a
        /// stale answer is exactly the "the overlay is wrong or missing and nothing says why" failure this exists
        /// to remove.
        /// </para>
        /// <para>
        /// The candidate cache is invalidated rather than re-resolved: the resolution reads
        /// <c>Mesh.colors32</c> and runs the world-position matcher, so it happens on the next read that actually
        /// needs it — one resolution per change, not one per invalidation.
        /// </para>
        /// <para>
        /// Nothing profile-owned is touched: the draft, the paths, the selection, and the author's candidate color
        /// are the caller's to set. Only derived state is dropped.
        /// </para>
        /// </remarks>
        private void InvalidateDerivedState()
        {
            SeamCandidates.Invalidate();
            _sceneTool?.InvalidateMeshCache();

            // The transient mesh decoded from a protected payload is derived state in the strongest sense: it
            // exists only so the overlays can read geometry a protected prefab deliberately does not carry. Every
            // reason to drop the other derived answers — a selection change, an undo, a profile load — is a reason
            // to drop this one too, and dropping it destroys the mesh rather than leaving it for the next repaint.
            _selection?.InvalidateProtectedGeometry();

            _validation = null;
            _dryRun = null;
            _lastWriteIssues = null;
            _selectionIssues = null;
            _removalCheck = null;
            _seamCheck = null;
            _draftDataIssues = null;
            _existingProfileState = null;
            _signatureSafetyStale = false;
            _signatureIdentityStale = false;

            // Status lines describe an action that was taken on the previous state, so they are dropped with it.
            _maskResultText = string.Empty;
            _maskResultType = MessageType.None;
            _seamResultText = string.Empty;
            _seamResultType = MessageType.None;
            _seamColorError = string.Empty;
            _seamColorInput = null;
            _showAllAddresses = false;

            MarkSelectionDirty();
        }

        // ---- Drawing --------------------------------------------------------------------------------

        private void OnGUI()
        {
            RecordUndoForInputEvent();
            _draft.EnsureInitialized();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // The section order is deliberate and is asserted by a source-contract test. Loading an existing
            // profile is the first decision an author makes, so its control sits directly under the toolbar;
            // Output sits between the bone/blend-shape policy it belongs to and the Actions that write it.
            DrawToolbar();
            DrawProfileLoadSection();
            DrawSelectionSection();
            DrawSignatureSection();
            DrawIdentitySection();
            DrawRemovalSection();
            DrawSeamSection();
            DrawUvSection();
            DrawMaterialSection();
            DrawPolicySection();
            DrawOutputSection();
            DrawActionsSection();
            DrawDiagnosticsSection();

            // Every live check for this repaint has now been served from, or refreshed into, the cache. Clearing
            // the flag here means one recomputation per change rather than one per section. GUI.changed is the
            // safety net: any control or button that changed something during this pass sets it, so a mutation
            // whose handler forgot to call MarkSelectionDirty still invalidates the cache for the next pass. The
            // explicit calls remain where the mutation happens outside OnGUI entirely (an undo, a profile load) or
            // where the same pass must already see the refreshed value.
            _liveChecksDirty = GUI.changed;
            if (_liveChecksDirty)
            {
                _draftDataIssues = null;
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Records one undo step for the changes an input event may make to the draft.
        /// </summary>
        /// <remarks>
        /// <see cref="Undo.RecordObject"/> only produces an undo entry when the object is actually modified
        /// during the event, so recording on the events that can modify a field covers every control in the
        /// window with one call and no per-control bookkeeping. Recording on every frame instead would push an
        /// entry per repaint; recording per control would need a snapshot before each assignment.
        /// </remarks>
        private void RecordUndoForInputEvent()
        {
            var currentEvent = Event.current;
            if (currentEvent == null) return;

            switch (currentEvent.type)
            {
                case EventType.MouseDown:
                case EventType.MouseDrag:
                case EventType.KeyDown:
                case EventType.ExecuteCommand:
                    Undo.RecordObject(this, Tr(UndoLabel));
                    MarkSelectionDirty();
                    break;
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button(Tr("New Draft"), EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                Undo.RecordObject(this, Tr("New Part Draft"));
                _draft = ApaProfileDraft.New("New Part", ApaPartSlot.Custom);
                _profilePath = ApaAuthoringAssetPaths.DefaultProfilePath(_selection.OutputFolder, "New Part");
                _prefabPath = ApaAuthoringAssetPaths.DefaultPrefabPath(_selection.OutputFolder, "New Part");
                InvalidateDerivedState();
                ClearResults(Tr("Started a new draft."));
            }

            if (GUILayout.Button(Tr("Load Profile…"), EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                LoadProfileFromPanel();
            }

            if (GUILayout.Button(Tr("Use Unity Selection"), EditorStyles.toolbarButton, GUILayout.Width(140)))
            {
                Undo.RecordObject(this, Tr("Adopt Unity Selection"));
                if (_selection.AdoptFromUnitySelection(UnityEditor.Selection.activeGameObject))
                {
                    CaptureSignatureIfNeeded(true);
                    InvalidateDerivedState();
                    SetStatus(Tr("Adopted the Unity selection."));
                }
                else
                {
                    SetStatus(Tr("Nothing to adopt: select a part with an AvatarPartInstaller, or a target body " +
                                 "renderer."));
                }
            }

            // Keep all three visual switches in the open toolbar area. They are intentionally next to one another:
            // removal (red), candidates/seams (green/cyan), and merge check (green/red prospective pairs).
            var removalOverlay = GUILayout.Toggle(
                _showRemovalOverlay,
                Content(
                    "Removal Overlay",
                    "Show the red triangles selected by the removal mask in the Scene View."),
                EditorStyles.toolbarButton,
                GUILayout.Width(108));
            if (removalOverlay != _showRemovalOverlay)
            {
                _showRemovalOverlay = removalOverlay;
                SceneView.RepaintAll();
            }

            var candidateOverlay = GUILayout.Toggle(
                _showCandidateOverlay,
                Content(
                    "Candidate Overlay",
                    "Show the green seam-candidate vertices and the stored seam points in the Scene View."),
                EditorStyles.toolbarButton,
                GUILayout.Width(118));
            if (candidateOverlay != _showCandidateOverlay)
            {
                _showCandidateOverlay = candidateOverlay;
                SceneView.RepaintAll();
            }

            var mergeCheck = GUILayout.Toggle(
                _mergeCheckOverlay,
                Content(
                    "Merge Check Overlay",
                    "Show matched seam candidates in green and unmatched part candidates in red."),
                EditorStyles.toolbarButton,
                GUILayout.Width(122));
            if (mergeCheck != _mergeCheckOverlay)
            {
                _mergeCheckOverlay = mergeCheck;
                SceneView.RepaintAll();
            }

            GUILayout.FlexibleSpace();

            // The language selector stays at the right edge. Selecting a language repaints every open window, which
            // is why the control the user just clicked is redrawn in the new language on the same frame.
            LanguagePopup(null, GUILayout.Width(110));

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, MessageType.None);
            }
        }

        /// <summary>
        /// The <c>Load Existing Profile</c> control, drawn directly under the toolbar.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It sits above every other section because loading is the first decision an author makes: everything
        /// below it — the selection, the signature, the removal region, the seam, the semantics — is either read
        /// from the loaded profile or written into the draft that will replace it, so the control that chooses the
        /// profile belongs before all of them rather than buried in the Output section.
        /// </para>
        /// <para>
        /// The field always draws empty, and a value assigned during a drag loads that profile into the draft.
        /// The asset is read, never written. There is deliberately no second copy of this control in the Output
        /// section: two controls for one decision would be two places to look and two places to keep in step.
        /// </para>
        /// </remarks>
        private void DrawProfileLoadSection()
        {
            EditorGUILayout.Space();

            var loadTarget = (ApaPartProfile)EditorGUILayout.ObjectField(
                Content(
                    "Load Existing Profile",
                    "Read a saved profile asset into the draft. The asset itself is never written until you save " +
                    "it, and loading replaces the whole draft, so the fields below show the loaded profile."),
                null,
                typeof(ApaPartProfile),
                false);

            if (loadTarget != null) LoadProfile(loadTarget);
        }

        private void DrawSelectionSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Selection"), EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            var avatarRoot = (GameObject)EditorGUILayout.ObjectField(
                Tr("Avatar Root"), _selection.AvatarRoot, typeof(GameObject), true);
            var target = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                Tr("Target Body Renderer"), _selection.TargetRenderer, typeof(SkinnedMeshRenderer), true);
            var partRoot = (GameObject)EditorGUILayout.ObjectField(
                Tr("Part Root"), _selection.PartRoot, typeof(GameObject), true);
            var partRenderer = (Renderer)EditorGUILayout.ObjectField(
                Tr("Part Renderer"), _selection.PartRenderer, typeof(Renderer), true);

            // The two Armature selections. They are the only thing that decides bone identity (M10): a bone's
            // path relative to its own armature root, with two identical paths meaning one joint. They are shown
            // as plain object fields rather than text paths, because the path is derived from the object.
            var targetArmature = (Transform)EditorGUILayout.ObjectField(
                Content(
                    "Target Armature",
                    "The armature inside the avatar that owns the body's bones. A body bone's identity is its " +
                    "path relative to this object, so it must be the bone level the part's armature corresponds " +
                    "to. It must be the avatar root itself or one of its descendants."),
                _selection.TargetArmature,
                typeof(Transform),
                true);

            var partArmature = (Transform)EditorGUILayout.ObjectField(
                Content(
                    "Part Armature",
                    "The armature inside the part that owns the part's bones. A part bone is the same joint as " +
                    "a body bone exactly when their paths relative to the two selected armatures are identical. " +
                    "It must be the part root itself or one of its descendants."),
                _selection.PartArmature,
                typeof(Transform),
                true);

            var outputFolder = EditorGUILayout.TextField(Tr("Output Folder"), _selection.OutputFolder);

            if (EditorGUI.EndChangeCheck())
            {
                var targetChanged = !ReferenceEquals(target, _selection.TargetRenderer);
                var rootChanged = !ReferenceEquals(avatarRoot, _selection.AvatarRoot);
                var rootsChanged = rootChanged || !ReferenceEquals(partRoot, _selection.PartRoot);
                var armatureChanged = !ReferenceEquals(targetArmature, _selection.TargetArmature)
                                      || !ReferenceEquals(partArmature, _selection.PartArmature);

                _selection.AvatarRoot = avatarRoot;
                _selection.TargetRenderer = target;
                _selection.PartRoot = partRoot;
                _selection.PartRenderer = partRenderer;
                _selection.TargetArmature = targetArmature;
                _selection.PartArmature = partArmature;
                _selection.OutputFolder = outputFolder;

                // The paths are recorded from the live objects here, which is the one moment both the objects and
                // the roots they belong to are known — but only when the armature selection itself was edited, or
                // when a root moved so the recorded paths have to be re-based. Rewriting them on every unrelated
                // edit in this block (the output folder, the part renderer) would erase a path whose object could
                // not be restored — a reopened window, or a profile loaded before its hierarchy was selected —
                // which is author data the draft must keep and the selection check reports.
                if (armatureChanged || rootsChanged)
                {
                    Undo.RecordObject(this, Tr("Select Armatures"));

                    // A root moved without the armature fields being touched: re-resolve the recorded paths
                    // against the new root first, so a selection that still resolves is re-recorded rather than
                    // dropped.
                    if (!armatureChanged) RestoreArmatureSelections();

                    _draft.SetArmatures(
                        _selection.AvatarRootTransform,
                        _selection.TargetArmature,
                        _selection.PartRoot != null ? _selection.PartRoot.transform : null,
                        _selection.PartArmature);
                }

                // The selection is the input every derived answer is about — the candidates are resolved from
                // these meshes and the merge check pairs these two renderers — so a changed selection drops all of
                // them here rather than leaving a Scene View overlay to serve the previous pair's answer.
                if (targetChanged || rootsChanged || armatureChanged) InvalidateDerivedState();

                if (rootChanged || targetChanged) CaptureSignatureIfNeeded(false);
                if (armatureChanged) SetStatus(TrFormat("Armatures: {0}.", _draft.Bones.DescribeArmatures()));
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                Tr("Resolved avatar root"), DescribeTransform(_selection.AvatarRootTransform), EditorStyles.miniLabel);
            if (GUILayout.Button(Tr("Find From Part"), GUILayout.Width(120)))
            {
                var anchor = _selection.PartRoot != null ? _selection.PartRoot.transform : null;
                if (anchor != null)
                {
                    Undo.RecordObject(this, Tr("Find Avatar Root"));
                    _selection.AvatarRoot = ApaAuthoringSelection.FindAvatarRoot(anchor).gameObject;

                    // The recorded target-armature path is relative to the avatar root, so a new root re-bases it.
                    // A selection that no longer resolves is kept as it was: the selection check reports it
                    // rather than the path being silently dropped.
                    RestoreArmatureSelections();
                    _draft.SetArmatures(
                        _selection.AvatarRootTransform,
                        _selection.TargetArmature,
                        _selection.PartRoot != null ? _selection.PartRoot.transform : null,
                        _selection.PartArmature);

                    InvalidateDerivedState();
                    CaptureSignatureIfNeeded(false);
                }
            }

            if (GUILayout.Button(Tr("Suggest Armatures"), GUILayout.Width(140)))
            {
                Undo.RecordObject(this, Tr("Suggest Armatures"));
                var proposal = _selection.SuggestArmatures();
                _draft.SetArmatures(
                    _selection.AvatarRootTransform,
                    _selection.TargetArmature,
                    _selection.PartRoot != null ? _selection.PartRoot.transform : null,
                    _selection.PartArmature);
                InvalidateDerivedState();
                SetStatus(TrFormat("Armature proposal: {0}.", proposal));
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                TrFormat("Selected armatures: {0}", _draft.Bones.DescribeArmatures()), EditorStyles.miniLabel);

            EditorGUILayout.LabelField(
                Tr("A bone's identity is its path relative to its own Armature, so two Armatures must be the " +
                   "corresponding bone levels of the body and of the part. The legacy merge path, prefix, " +
                   "suffix, and inference controls are gone: the two selections decide the merge."),
                EditorStyles.miniLabel);

            _captureSignatureOnTargetChange = EditorGUILayout.Toggle(
                Tr("Capture signature when target changes"), _captureSignatureOnTargetChange);

            DrawIssueList(CurrentSelectionIssues(), Tr("The selection is not usable yet"));
        }

        private void DrawSignatureSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                TrFormat("Target Signature (schema v{0})", ApaPartProfile.CurrentSchemaVersion),
                EditorStyles.boldLabel);

            var profile = _draft.Compatibility;

            EditorGUILayout.LabelField(
                Tr("Renderer Path"), string.IsNullOrEmpty(profile.RendererPath) ? Tr("(none)") : profile.RendererPath);
            EditorGUILayout.LabelField(
                Tr("Mesh"), string.IsNullOrEmpty(profile.MeshName) ? Tr("(none)") : profile.MeshName);
            EditorGUILayout.LabelField(Tr("Captured"), profile.IsCaptured ? Tr("yes") : Tr("no"));
            EditorGUILayout.LabelField(
                Tr("Safety data complete"), profile.HasCompleteSafetyData ? Tr("yes") : Tr("no"));
            EditorGUILayout.LabelField(Tr("Vertex Count"), profile.VertexCount.ToString());
            EditorGUILayout.LabelField(Tr("Submeshes"), profile.SubMeshIndexCounts.Length.ToString());
            EditorGUILayout.LabelField(Tr("Blend Shapes"), profile.BlendShapeNames.Length.ToString());
            EditorGUILayout.LabelField(Tr("Bones"), profile.BonePaths.Length.ToString());
            EditorGUILayout.LabelField(
                Tr("Mesh GUID"), profile.HasMeshGuid ? profile.MeshGuid : Tr("(not an asset)"));
            EditorGUILayout.LabelField(
                Tr("Mesh Fingerprint"), profile.HasMeshFingerprint ? profile.MeshFingerprint : Tr("(missing)"));

            if (!profile.HasCompleteSafetyData)
            {
                EditorGUILayout.HelpBox(
                    TrFormat(
                        "Missing signature data: {0}. A profile with an incomplete signature is refused by the " +
                        "writer and blocked at build time (APA024).",
                        ApaCompatibilityCapture.DescribeMissing(profile)),
                    MessageType.Warning);
            }

            var mesh = _selection.TargetMesh;
            if (profile.IsCaptured && mesh != null)
            {
                RefreshSignatureStaleness();

                if (_signatureSafetyStale)
                {
                    // The safety fields are the ones CompatibilityRule compares, so this difference really does
                    // block at build time.
                    EditorGUILayout.HelpBox(
                        Tr("The selected target mesh changed: its vertex count, submesh layout, or blend shapes no " +
                           "longer match the captured signature. Recapture before saving, or the build will block " +
                           "with APA012 / APA024."),
                        MessageType.Warning);
                }
                else if (_signatureIdentityStale)
                {
                    // The renderer path and the mesh GUID are recorded for context only: the target is resolved
                    // from the avatar root at install time and the compatibility rule never compares either
                    // value, so this is advice, not a build failure. Claiming otherwise would teach the author
                    // to ignore the warning that is sometimes right.
                    EditorGUILayout.HelpBox(
                        Tr("The recorded renderer path or mesh asset GUID no longer matches the live target. This is " +
                           "advisory: the safety fields still match, so the profile remains usable and the build " +
                           "will not block because of it. Recapture to store the current path and GUID."),
                        MessageType.Info);
                }
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Capture Signature"))) CaptureSignatureIfNeeded(true);
            if (GUILayout.Button(Tr("Clear Signature")))
            {
                Undo.RecordObject(this, Tr("Clear Signature"));
                _draft.Compatibility = new ApaAvatarCompatibilityProfile();
                MarkSelectionDirty();
                SetStatus(Tr("Cleared the captured signature."));
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawIdentitySection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Part Identity"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Tr("Stable Part Id"),
                string.IsNullOrEmpty(_draft.Identity.PartId) ? Tr("(none)") : _draft.Identity.PartId);
            if (GUILayout.Button(Tr("Assign"), GUILayout.Width(70)))
            {
                Undo.RecordObject(this, Tr("Assign Part Id"));
                _draft.EnsureStablePartId();
                MarkSelectionDirty();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                Tr("The stable id is the ordering key. It survives renaming and hierarchy moves, so it is never " +
                   "derived from the name."), EditorStyles.miniLabel);
        }

        /// <summary>
        /// The Output section: where the profile asset and the part prefab are written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It is drawn directly below <c>Bones And Blend Shapes</c> and directly above <c>Actions</c>, because
        /// that is the order the work happens in: the policy above it is the last authoring decision, this section
        /// names what will be written, and the Actions below it perform the writes. Nothing here validates or
        /// writes on its own; the path fields only feed <see cref="ApaProfileWriter"/> and
        /// <see cref="ApaPrefabGenerator"/>.
        /// </para>
        /// <para>
        /// The <c>Load Existing Profile</c> control is <b>not</b> duplicated here: it is drawn once, at the top of
        /// the window, because loading replaces the whole draft rather than only the output paths.
        /// </para>
        /// </remarks>
        private void DrawOutputSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Output"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            var profilePath = EditorGUILayout.TextField(Tr("Profile Asset"), _profilePath);
            if (GUILayout.Button("…", GUILayout.Width(24)))
            {
                var picked = EditorUtility.SaveFilePanelInProject(
                    Tr("Profile asset path"), "AvatarPartProfile", "asset",
                    Tr("Choose where the profile is written"));
                if (!string.IsNullOrEmpty(picked)) profilePath = picked;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            var prefabPath = EditorGUILayout.TextField(Tr("Part Prefab"), _prefabPath);
            if (GUILayout.Button("…", GUILayout.Width(24)))
            {
                var picked = EditorUtility.SaveFilePanelInProject(
                    Tr("Part prefab path"), "AvatarPart", "prefab", Tr("Choose where the prefab is written"));
                if (!string.IsNullOrEmpty(picked)) prefabPath = picked;
            }

            EditorGUILayout.EndHorizontal();

            if (!ApaAuthoringAssetPaths.PathsEqual(profilePath, _profilePath)
                || !ApaAuthoringAssetPaths.PathsEqual(prefabPath, _prefabPath))
            {
                Undo.RecordObject(this, Tr("Change Output Paths"));
                _profilePath = profilePath;
                _prefabPath = prefabPath;

                // The cached "Existing profile" line and the Signature/Output checks are keyed on the path, so a
                // path change has to invalidate them like any other mutation.
                MarkSelectionDirty();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Default Paths"), GUILayout.Width(110)))
            {
                Undo.RecordObject(this, Tr("Reset Output Paths"));
                _profilePath = ApaAuthoringAssetPaths.DefaultProfilePath(
                    _selection.OutputFolder, _selection.PartRoot != null ? _selection.PartRoot.name : "AvatarPart");
                _prefabPath = ApaAuthoringAssetPaths.DefaultPrefabPath(
                    _selection.OutputFolder, _selection.PartRoot != null ? _selection.PartRoot.name : "AvatarPart");
                MarkSelectionDirty();
            }

            _allowOverwrite = EditorGUILayout.ToggleLeft(
                Tr("Allow replacing an existing asset (asked again when unchecked)"), _allowOverwrite);

            EditorGUILayout.EndHorizontal();

            var existingProfileDescription = DescribeExistingProfile();
            if (!string.IsNullOrEmpty(existingProfileDescription))
            {
                EditorGUILayout.LabelField(Tr("Existing profile"), existingProfileDescription, EditorStyles.miniLabel);
            }
            else if (!string.IsNullOrEmpty(_profilePath)
                     && !ApaAuthoringAssetPaths.TryValidateAssetPath(
                         _profilePath, ApaAuthoringAssetPaths.ProfileExtension, out _, out var reason))
            {
                EditorGUILayout.HelpBox(
                    TrFormat(
                        "Profile path problem: {0}",
                        ApaAuthoringAssetPaths.DescribePathReason(
                            reason, ApaAuthoringAssetPaths.ProfileExtension)), MessageType.Error);
            }

            DrawProtectedMeshControls();
        }

        /// <summary>
        /// The protected-mesh option: the toggle, the payload path it implies, and the rule it enforces.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The payload path is derived, not typed.</b> It is the prefab path with a
        /// <c>_ProtectedMesh</c> suffix (see <see cref="ApaProtectedMeshAssetWriter.DefaultPathFor"/>), which is
        /// what keeps the two files visibly paired in the Project window. A second editable path field would be
        /// one more thing that can disagree with the prefab it protects, and the pairing is the delivery rule.
        /// </para>
        /// <para>
        /// <b>The consequence is stated, not implied.</b> Turning the toggle on changes what the author must
        /// deliver: the prefab alone stops being a complete part, and the source mesh must not be shipped. That is
        /// a sentence in the window, not a tooltip, because it is the one thing about this feature that can go
        /// wrong after the fact.
        /// </para>
        /// </remarks>
        private void DrawProtectedMeshControls()
        {
            EditorGUILayout.Space();

            _protectPartMesh = EditorGUILayout.ToggleLeft(
                Tr("Protect the part mesh (write an encrypted payload instead of the mesh)"), _protectPartMesh);

            if (!_protectPartMesh)
            {
                EditorGUILayout.LabelField(
                    Tr("Off: the prefab references the source mesh exactly as it always has."),
                    EditorStyles.miniLabel);
                return;
            }

            var protectedPath = ApaProtectedMeshAssetWriter.DefaultPathFor(_prefabPath);
            EditorGUILayout.LabelField(Tr("Protected Mesh Asset"), protectedPath, EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(protectedPath))
            {
                var existingPayload = ApaAssetDatabaseUtility.Load<ApaProtectedMeshAsset>(protectedPath);
                if (existingPayload != null)
                {
                    EditorGUILayout.LabelField(
                        Tr("A protected mesh asset already exists at this path. Replacing it is asked for again " +
                           "when the prefab replacement is confirmed."),
                        EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.HelpBox(
                Tr("The part mesh is written to the protected asset as an authenticated, encrypted payload and " +
                   "the prefab's part renderer is saved with no mesh, so the prefab no longer depends on the " +
                   "source mesh or its model file. The payload must be delivered together with the prefab: a " +
                   "prefab whose payload is missing cannot be assembled. This protects the distribution format " +
                   "and detects tampering; it is not unextractable DRM, because a build that runs in the Editor " +
                   "can be observed while it runs. Materials, textures, bones, and animation remain ordinary " +
                   "assets. Recreate the protected prefab after the source mesh changes."),
                MessageType.Info);
        }

        /// <summary>
        /// The Removal Region: the texture mask, the selection it generated, and the read-only overlay toggle.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Mask-only (M16).</b> The removal set is authored by the texture mask and by nothing else. The Scene
        /// View pick mode, the per-triangle toggle and remove controls, the submesh and triangle number fields,
        /// the address text field, the Add Address / Add List buttons, and the submesh bulk-delete button are all
        /// gone, along with the backend that served them: a removal region is a painted region, and every manual
        /// path was a second way to describe the same set that could disagree with the mask and with itself.
        /// </para>
        /// <para>
        /// What remains is exactly the mask workflow: the mask inputs and the Apply action
        /// (<see cref="DrawTextureMaskSection"/>), the generated selection's summary and bounded review
        /// (<see cref="DrawAddressList"/>), the structural check, and the two bulk operations that act on the
        /// canonical set as a whole — <c>Clear</c> and the mask's own Replace / Add / Subtract modes. None of them
        /// selects an individual triangle, and none of them is a second authoring surface for the mask.
        /// </para>
        /// <para>
        /// The red overlay toggle lives here because this is the only place the removal set is authored: it is a
        /// visualization of the mask's result, never an editing surface.
        /// </para>
        /// </remarks>
        private void DrawRemovalSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Removal Region"), EditorStyles.boldLabel);

            var mask = _draft.Removal;
            EditorGUILayout.LabelField(Tr("Selected"), mask.Describe());

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Clear"), GUILayout.Width(60)))
            {
                Undo.RecordObject(this, Tr("Clear Removal Mask"));
                mask.Clear();
                MarkSelectionDirty();
                SceneView.RepaintAll();
            }

            EditorGUILayout.EndHorizontal();

            DrawTextureMaskSection(mask);

            DrawAddressList(mask);
            DrawIssueList(ValidateRemoval(), Tr("Removal set problems"));
        }

        /// <summary>
        /// The Texture Mask block: a black/white texture, read through the target mesh's UVs, converted into the
        /// removal triangle set.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The conversion itself lives in <see cref="ApaRemovalMaskSampler"/>; this method only draws controls and
        /// reports what that utility returned. This block is the <b>only</b> way a removal set is authored (M16):
        /// the Scene View pick mode and the numeric address list it used to sit beside were removed with
        /// mask-only removal authoring, and what they wrote went into the same canonical
        /// <see cref="ApaRemovalMask"/> this block writes through the same methods.
        /// </para>
        /// <para>
        /// Applying is disabled while the mask cannot possibly run — no target mesh, no mask texture, an
        /// unreadable target mesh, or a UV channel the mesh does not carry — and <see cref="DescribeMaskBlocker"/>
        /// states that reason next to the button rather than only after a failed click. The mask settings are on
        /// the window and are never written into the profile: the profile stores the triangle set they generate,
        /// which keeps a part reproducible without the texture.
        /// </para>
        /// </remarks>
        private void DrawTextureMaskSection(ApaRemovalMask mask)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Texture Mask"), EditorStyles.boldLabel);

            var targetMesh = _selection.TargetMesh;
            var uvChannelPresent = targetMesh != null
                                   && ApaAuthoringValidation.MeshHasUvChannel(targetMesh, _maskUvChannel);
            var ready = targetMesh != null && _maskTexture != null && uvChannelPresent;

            EditorGUI.BeginChangeCheck();

            _maskTexture = (Texture2D)EditorGUILayout.ObjectField(
                Content(
                    "Mask Texture",
                    "A black/white texture sampled through the target mesh's UVs. White selects removal triangles " +
                    "and black keeps them; Invert reverses that. The texture is only an authoring input: the " +
                    "profile stores the resulting triangle set, never the texture."),
                _maskTexture,
                typeof(Texture2D),
                false);

            _maskUvChannel = Mathf.Clamp(
                EditorGUILayout.IntSlider(
                    Content(
                        "UV Channel",
                        "The target mesh UV channel the mask addresses. A mask painted against UV1 does nothing " +
                        "when UV0 is read, so the channel is part of the input rather than a preference."),
                    _maskUvChannel,
                    0,
                    ApaMeshLimits.MaxUvChannels - 1),
                0,
                ApaMeshLimits.MaxUvChannels - 1);

            _maskThreshold = Mathf.Clamp01(
                EditorGUILayout.Slider(
                    Content(
                        "Threshold",
                        "Brightness at or above which a sample counts as selected. A sample must reach the " +
                        "threshold to pass; with Invert on, its brightness is replaced by 1 minus the brightness " +
                        "first."),
                    _maskThreshold,
                    0f,
                    1f));

            _maskInvert = EditorGUILayout.Toggle(
                Content("Invert", "Treat black as the removal color and white as the keep color."), _maskInvert);

            // A localized popup rather than EnumPopup: the serialized enum member names (ReplaceSelection and
            // friends) are not user interface text, and a Chinese author must not be shown them. The popup maps a
            // picked index back through the enum's own values, so the stored value is never renumbered.
            _maskApplyMode = Localization.ApaLocalization.MaskApplyModePopup(
                Content(
                    "Apply Mode",
                    "How the generated triangles are combined with the current removal set. Replace is the " +
                    "default; Add and Subtract adjust the existing selection, which is how several masks build one " +
                    "region."),
                _maskApplyMode);

            if (EditorGUI.EndChangeCheck()) MarkSelectionDirty();

            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(!ready);
            if (GUILayout.Button(Tr("Apply Mask"), GUILayout.Width(120)))
            {
                ApplyTextureMask(mask);
            }

            EditorGUI.EndDisabledGroup();

            if (!ready)
            {
                EditorGUILayout.LabelField(
                    DescribeMaskBlocker(targetMesh, _maskTexture, _maskUvChannel),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                Tr("Sample rule: 7 points per triangle (3 vertices, 3 edge midpoints, centroid); the triangle is " +
                   "selected when at least 4 samples are at or above the threshold; brightness is RGB luminance " +
                   "and alpha is ignored."),
                EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(_maskResultText))
            {
                EditorGUILayout.HelpBox(_maskResultText, _maskResultType);
            }
        }

        /// <summary>
        /// The one line that says why the mask cannot be applied yet, checked before the click rather than after
        /// it.
        /// </summary>
        /// <remarks>
        /// The conditions are listed in the order the author would fix them, and each names the object or the
        /// channel it is about. Readability comes before the UV-channel check because
        /// <see cref="ApaAuthoringValidation.MeshHasUvChannel"/> answers false for an unreadable mesh — its UV data
        /// is not available at all — so testing the channel first would tell the author to pick a different
        /// channel when the real remedy is enabling Read/Write. The method never returns an empty string while the
        /// Apply button is disabled.
        /// </remarks>
        private static string DescribeMaskBlocker(Mesh targetMesh, Texture2D maskTexture, int uvChannel)
        {
            if (targetMesh == null)
            {
                return Tr("Select a target body renderer first: the mask is read through its mesh UVs.");
            }

            if (maskTexture == null) return Tr("Assign a mask texture to generate a selection from.");

            if (!targetMesh.isReadable)
            {
                return TrFormat(
                    "Mesh '{0}' is not readable, so its UVs cannot be read. Enable Read/Write in its import " +
                    "settings.", targetMesh.name);
            }

            if (!ApaAuthoringValidation.MeshHasUvChannel(targetMesh, uvChannel))
            {
                return TrFormat(
                    "Mesh '{0}' carries no UV channel {1}. Choose a channel the mesh has, or paint the mask " +
                    "against one it does.", targetMesh.name, uvChannel);
            }

            return string.Empty;
        }

        /// <summary>
        /// Converts the mask and merges the result into the removal set, as one undoable action.
        /// </summary>
        /// <remarks>
        /// The whole edit is one <see cref="Undo.RecordObject"/> because it is one decision the author made: a
        /// single Apply either replaces, adds, or subtracts, and half of it is not a state anyone asked for. An
        /// empty generated set is a valid outcome — under Replace it deliberately clears the selection, which is
        /// how an author says "this mask defines nothing".
        /// </remarks>
        private void ApplyTextureMask(ApaRemovalMask mask)
        {
            var generated = ApaRemovalMaskSampler.TryGenerate(
                _selection.TargetMesh, _maskTexture, _maskUvChannel, _maskThreshold, _maskInvert);

            if (!generated.Succeeded)
            {
                var issue = generated.Issue;
                _lastWriteIssues = issue != null
                    ? ValidationResult.Single(issue)
                    : ValidationResult.Empty;

                // The whole stable diagnostic — code, mnemonic title, message, and the reason= token — is rendered
                // in this block, not only in the write section further down, because this is where the author
                // pressed the button that failed.
                _maskResultText = issue != null
                    ? TrFormat("Mask apply failed: {0}", ApaDiagnosticText.Format(issue))
                    : Tr("Mask apply failed: the mask could not be read.");
                _maskResultType = MessageType.Error;
                SetStatus(Tr("Texture mask: no selection was generated. The refusal is shown in the Texture " +
                             "Mask block."));
                MarkSelectionDirty();
                Repaint();
                return;
            }

            var before = mask.Count;
            var count = generated.Addresses.Count;

            // The same single-entry point every other removal edit uses, so the mask's canonical invariant
            // (sorted, de-duplicated, parallel arrays) holds whatever the mask produced.
            Undo.RecordObject(this, Tr("Apply Texture Mask"));

            switch (_maskApplyMode)
            {
                case ApaMaskApplyMode.AddToSelection:
                    mask.AddRange(generated.Addresses, null);
                    break;
                case ApaMaskApplyMode.SubtractFromSelection:
                    for (var i = 0; i < count; i++) mask.Remove(generated.Addresses[i]);
                    break;
                default:
                    mask.SetFromAddresses(generated.Addresses);
                    break;
            }

            _lastWriteIssues = ValidationResult.Empty;
            _maskResultText = DescribeMaskApplication(_maskApplyMode, before, count, mask.Count);
            _maskResultType = MessageType.Info;
            SetStatus(TrFormat(
                "Texture mask: {0} of {1} triangle(s) matched.",
                count, generated.ConsideredTriangleCount));

            // The selection changed, so every cached verdict that depends on it — the address list, the structural
            // removal check, the dry-run result — is recomputed on the next repaint, and the Scene View is
            // repainted so the highlighted triangles match the new set.
            MarkSelectionDirty();
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>The per-mode result line: what the mode did, with the counts it actually moved.</summary>
        private static string DescribeMaskApplication(ApaMaskApplyMode mode, int before, int generated, int after)
        {
            switch (mode)
            {
                case ApaMaskApplyMode.AddToSelection:
                    return generated == 0
                        ? TrFormat("The mask generated no triangles, so the selection is unchanged at {0}.", before)
                        : TrFormat(
                            "Added {0} triangle(s) to the selection: {1} before, {2} after ({3} were already " +
                            "selected).", after - before, before, after, generated - (after - before));

                case ApaMaskApplyMode.SubtractFromSelection:
                    return after == before
                        ? TrFormat(
                            "None of the {0} generated triangle(s) were in the selection, so it is unchanged at {1}.",
                            generated, before)
                        : TrFormat(
                            "Removed {0} triangle(s) from the selection: {1} before, {2} after.",
                            before - after, before, after);

                default:
                    return generated == 0
                        ? TrFormat(
                            "The mask matched no triangle, so the selection was replaced with an empty set " +
                            "({0} address(es) cleared).", before)
                        : TrFormat(
                            "Replaced the selection with {0} generated triangle(s), {1} address(es) before.",
                            generated, before);
            }
        }

        /// <summary>
        /// The seam block: one action that generates the pairing from world-coincident vertex positions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The per-vertex picking controls and the two index-list text fields are gone (M10). Welding is a
        /// decision about which part vertex sits on which body vertex, and the only thing an author can know
        /// about that decision is where the vertices are — so the window offers exactly the input it can justify
        /// (a world-space tolerance) and the one action that turns it into pairs. Editing two index lists by hand
        /// could not express which index went with which, which is what produced the block-time position search
        /// this replaces.
        /// </para>
        /// <para>
        /// <b>Which part vertices may pair is a color code (M16).</b> The block exposes the candidate color as a
        /// strict <c>#RRGGBB</c> / <c>#RRGGBBAA</c> text field and states the part's candidate count — or the
        /// blocking <c>APA052</c> diagnostic — before the action runs, and the merge-check toggle below the action
        /// draws the prospective pairing in the Scene View without writing anything. The Unity color wheel is gone:
        /// it named a value in float space that had to be rounded back into the 8-bit space the comparison uses,
        /// and it emitted a change per frame while it was dragged. The previous named <c>merge vertex</c> group (a
        /// component or a bone) is gone as well: Unity import pipelines routinely drop non-bone vertex groups
        /// while preserving mesh vertex colors.
        /// </para>
        /// <para>
        /// <b>The target side is spatial (M15).</b> The body is not required to carry vertex colors: every target
        /// vertex may pair and the world-position matcher decides which ones do, so the status line for that side
        /// describes a spatial candidate set rather than a color-filtered one, and a color-less body generates a
        /// seam normally. Only the part side is restricted by the selected color, and it never falls back to every
        /// part vertex when the color does not resolve.
        /// </para>
        /// <para>
        /// <b>Typing does not drive the expensive path.</b> The code is parsed strictly, and only a well-formed
        /// code that names a different color commits: it invalidates the resolved candidates and the merge-check
        /// classification exactly once, and the next read re-resolves. Malformed or incomplete text commits
        /// nothing and keeps the previous valid color, so no keystroke can rebuild the candidate arrays or select a
        /// color the author did not type.
        /// </para>
        /// <para>
        /// The whole edit is one undo step: generating either replaces the seam or leaves it alone, and a half
        /// applied pairing is not a state anyone asked for.
        /// </para>
        /// </remarks>
        private void DrawSeamSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Seam (paired loops)"), EditorStyles.boldLabel);

            var seam = _draft.Seam;

            // A summary, not one row per vertex: a real seam has hundreds of pairs, and drawing them buries the
            // controls that matter. The counts and the tolerance are the reviewable facts.
            EditorGUILayout.LabelField(Tr("Pairs"), seam.Describe());
            EditorGUILayout.LabelField(
                TrFormat("Seam pairing: {0}", DescribeSeamPairingState(seam)), EditorStyles.miniLabel);

            _seamTolerance = Mathf.Clamp(
                EditorGUILayout.FloatField(
                    Content(
                        "Tolerance (world units)",
                        "How close two vertices must be in world space to be paired. The comparison uses the " +
                        "rest pose, not the current animated pose, and the value is in world units because that " +
                        "is the quantity a seam is authored in."),
                    _seamTolerance),
                ApaSeamWorldMatcher.MinimumTolerance,
                ApaSeamWorldMatcher.MaximumTolerance);

            DrawCandidateColorCode();

            // The candidates are what makes "which vertices may pair" an inspectable answer, so the window states
            // both sides before the action runs: the part's colored candidates, the target's spatial candidate
            // set, and — when a side has no usable candidate policy — the blocking diagnostic, in the same words
            // the action will report. The part side never says "all vertices", because that state does not exist.
            EnsureSeamCandidates();
            EditorGUILayout.LabelField(
                TrFormat("Target seam candidates: {0}", DescribeCandidates(SeamCandidates.Target)),
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                TrFormat("Part seam candidates: {0}", DescribeCandidates(SeamCandidates.Part)),
                EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Generate From World Positions"), GUILayout.Width(220)))
            {
                GenerateWorldSeam(seam);
            }

            if (GUILayout.Button(Tr("Clear"), GUILayout.Width(60)))
            {
                Undo.RecordObject(this, Tr("Clear Seam"));
                seam.Clear();
                _seamResultText = string.Empty;
                _seamResultType = MessageType.None;
                MarkSelectionDirty();
                SetStatus(Tr("Cleared the seam. This part now declares no seam."));
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                Tr("Both meshes are read in their rest pose (the shared mesh, never a baked pose) and every " +
                   "world-coincident pair within the tolerance is written as one weld. Only part vertices that " +
                   "carry the selected candidate color may pair; the target body is matched spatially and needs " +
                   "no vertex colors. Leave the seam empty when this part does not weld to the body."),
                EditorStyles.miniLabel);

            if (!seam.IsEmpty && !seam.IsPaired)
            {
                EditorGUILayout.HelpBox(
                    Tr("This seam was authored before explicit pairing: its two lists are unordered sets and the " +
                       "build refuses them (APA042). Generate it from world positions to write the pairing."),
                    MessageType.Error);
            }

            if (!string.IsNullOrEmpty(_seamResultText))
            {
                EditorGUILayout.HelpBox(_seamResultText, _seamResultType);
            }

            DrawIssueList(ValidateSeam(), Tr("Seam problems"));
        }

        /// <summary>
        /// The candidate color code field: the strict text field that replaced the Unity color wheel.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The field shows <c>#RRGGBB</c> — <c>#RRGGBBAA</c> when the stored alpha is not opaque — and accepts
        /// exactly those two forms (see <see cref="ApaSeamColorCode"/>). It is drawn beside a read-only swatch of
        /// the value that is actually in effect, so the author can see at a glance which color the matcher will
        /// compare even while the text is malformed.
        /// </para>
        /// <para>
        /// The swatch is a plain <see cref="EditorGUI.DrawRect"/> rather than a read-only
        /// A disabled color-picker widget would still be the color-wheel control this iteration removed from the
        /// seam UI, and leaving one in place would keep the wheel one click away.
        /// </para>
        /// <para>
        /// The text buffer is derived from the committed color whenever it is absent, so it can never display a
        /// code that disagrees with the color the matcher uses — after a reset, or after a commit that
        /// canonicalized the spelling.
        /// </para>
        /// </remarks>
        private void DrawCandidateColorCode()
        {
            if (_seamColorInput == null) _seamColorInput = ApaSeamColorCode.Format(_seamCandidateColor);

            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();

            var text = EditorGUILayout.TextField(
                Content(
                    "Candidate Color Code",
                    "The vertex color that marks a seam candidate on the part mesh, written as #RRGGBB. " +
                    "#RRGGBBAA is accepted as well, and is shown when the alpha is not opaque. A part vertex may " +
                    "pair only when all four channels of its stored Mesh.colors32 entry equal this color exactly " +
                    "— there is no tolerance, and the alpha channel participates. The target body mesh is not " +
                    "filtered by color: its vertices are matched by world position. Paint the part's seam ring " +
                    "with this color in the modelling tool, or write Mesh.colors32 before generating the seam."),
                _seamColorInput);

            if (EditorGUI.EndChangeCheck()) CommitSeamColorCode(text);

            // A read-only swatch of the committed color. Plain rect: no color-wheel control is drawn anywhere in
            // this window any more.
            var swatch = GUILayoutUtility.GetRect(
                24f,
                EditorGUIUtility.singleLineHeight,
                GUILayout.Width(24f));
            EditorGUI.DrawRect(swatch, _seamCandidateColor);

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_seamColorError))
            {
                EditorGUILayout.HelpBox(_seamColorError, MessageType.Error);
            }

            EditorGUILayout.LabelField(
                TrFormat(
                    "Matched color: {0} (exact Color32 equality, alpha included)",
                    ApaSeamVertexColorCandidates.Format(_seamCandidateColor)),
                EditorStyles.miniLabel);
        }

        /// <summary>
        /// Commits a typed color code: the parsed color on a valid edit, the previous color on malformed input.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rule lives in <see cref="ApaSeamColorCode.TryApply"/> so it can be tested without a window; this
        /// method only applies its verdict to the window's state and reports it.
        /// </para>
        /// <para>
        /// <b>One invalidation per committed edit.</b> A valid code that names a different color drops the
        /// resolved candidates and the merge-check classification exactly once, and the next read re-resolves.
        /// Malformed text and a code that merely re-states the current color invalidate nothing, so no keystroke
        /// of an incomplete code and no retyped code can make the window read <c>Mesh.colors32</c> or run the
        /// world-position matcher.
        /// </para>
        /// </remarks>
        private void CommitSeamColorCode(string text)
        {
            _seamColorInput = text ?? string.Empty;

            if (!ApaSeamColorCode.TryApply(_seamColorInput, _seamCandidateColor, out var applied, out var changed))
            {
                _seamColorError = TrFormat(
                    "Color code '{0}' is not a color. Write #RRGGBB, or #RRGGBBAA to include the alpha channel; " +
                    "the previous color {1} is still in effect.",
                    _seamColorInput,
                    ApaSeamColorCode.Format(_seamCandidateColor));
                return;
            }

            _seamColorError = string.Empty;

            if (!changed)
            {
                // The code names the color already in effect: canonicalize the spelling and leave every cached
                // answer alone, so a repeated edit costs nothing.
                _seamColorInput = ApaSeamColorCode.Format(_seamCandidateColor);
                return;
            }

            _seamCandidateColor = applied;

            // The mesh arrays and the merge-check classification are keyed on the resolved candidate results, so
            // dropping the results is what invalidates the classification too; the meshes themselves did not
            // change, so their vertex arrays are deliberately kept.
            SeamCandidates.Invalidate();
            MarkSelectionDirty();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// One line describing whether the seam is paired, legacy, or empty, and what the profile will store.
        /// </summary>
        private static string DescribeSeamPairingState(ApaSeamSelection seam)
        {
            if (seam.IsEmpty) return Tr("no seam (the part does not weld)");
            return seam.IsPaired ? Tr("explicit, written by this window") : Tr("legacy, unpaired (APA042)");
        }

        /// <summary>
        /// Runs the world-position matcher and writes the resulting pairs as one undoable edit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Only the part's selected candidate color restricts the pairing.</b> The part's candidate list is
        /// resolved from the vertex-color contract (<see cref="ApaSeamVertexColorCandidates"/>) and handed to the
        /// matcher's candidate overload, so a part vertex that does not carry the selected color is never paired
        /// no matter how well it coincides. A part mesh that carries no color, carries a malformed color array, or
        /// carries no vertex of that color is refused with <c>APA052</c> before the matcher runs; there is
        /// deliberately no path from "no candidate" to "every vertex", because that fallback is the defect this
        /// contract exists to remove.
        /// </para>
        /// <para>
        /// <b>The target side is spatial.</b> Its candidate policy is "every body vertex"
        /// (<see cref="ApaSeamSpatialTargetCandidates"/>), which the matcher receives as its documented all-vertices
        /// argument. A body mesh without vertex colors therefore generates a seam normally — the target side has no
        /// color to be missing.
        /// </para>
        /// <para>
        /// A refusal leaves the existing selection untouched: the action reports its diagnostic in the seam
        /// block and the profile keeps whatever it had, so a failed attempt can never damage authored data.
        /// </para>
        /// </remarks>
        private void GenerateWorldSeam(ApaSeamSelection seam)
        {
            // Re-resolved here rather than read from the display cache: the candidate color and the meshes are
            // the authoritative inputs of this action, and a mesh edited outside the window changes its colors
            // without any repaint-time signal. Storing the fresh results also refreshes the display cache, so the
            // counts drawn after this action describe exactly what was matched.
            RefreshSeamCandidates();

            var targetCandidates = SeamCandidates.Target;
            var partCandidates = SeamCandidates.Part;

            if (targetCandidates == null || !targetCandidates.Succeeded)
            {
                FailSeamGeneration(targetCandidates != null ? targetCandidates.Issue : null);
                return;
            }

            if (partCandidates == null || !partCandidates.Succeeded)
            {
                FailSeamGeneration(partCandidates != null ? partCandidates.Issue : null);
                return;
            }

            var result = ApaSeamWorldMatcher.Match(
                _selection.TargetRenderer,
                _selection.TargetMesh,
                _selection.PartRenderer,
                _selection.PartGeometryMesh,
                _seamTolerance,
                targetCandidates.MatcherCandidateIndices,
                partCandidates.Indices);

            if (!result.Succeeded)
            {
                FailSeamGeneration(result.Issue);
                return;
            }

            Undo.RecordObject(this, Tr("Generate Seam From World Positions"));
            _draft.SetPairedSeam(result.BaseIndices, result.PartIndices);

            _lastWriteIssues = ValidationResult.Empty;
            _seamResultText = result.Describe();
            _seamResultType = MessageType.Info;
            SetStatus(TrFormat("Seam: {0}", result.Describe()));

            MarkSelectionDirty();
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Reports a refused seam generation without touching the authored seam.
        /// </summary>
        /// <remarks>
        /// The diagnostic is kept as the last write result as well as shown in the seam block, so a failure is
        /// still visible in the diagnostics section after the block scrolls out of view — and it keeps the stable
        /// code and <c>reason=</c> token that the contract is asserted on.
        /// </remarks>
        private void FailSeamGeneration(ValidationIssue issue)
        {
            _lastWriteIssues = issue == null ? ValidationResult.Empty : ValidationResult.Single(issue);
            _seamResultText = TrFormat("Seam generation failed: {0}", ApaDiagnosticText.Format(issue));
            _seamResultType = MessageType.Error;
            SetStatus(Tr("Seam generation produced no pairs. The refusal is shown in the Seam block."));
            MarkSelectionDirty();
            Repaint();
        }

        /// <summary>
        /// Re-resolves both sides' seam candidates: the target spatially, the part from the selected color.
        /// </summary>
        /// <remarks>
        /// The two sides deliberately use two different policies — see
        /// <see cref="ApaSeamSpatialTargetCandidates"/> and <see cref="ApaSeamVertexColorCandidates"/> — and the
        /// results are stored in the cache together, so a reader never sees one side of a pair that was resolved
        /// against different inputs.
        /// </remarks>
        private void RefreshSeamCandidates()
        {
            var target = ApaSeamSpatialTargetCandidates.Resolve(_selection.TargetRenderer, _selection.TargetMesh);
            var part = ApaSeamVertexColorCandidates.ResolvePartCandidates(
                _selection.PartRenderer, _selection.PartGeometryMesh, _seamCandidateColor);

            SeamCandidates.Store(target, part);
        }

        /// <summary>
        /// Resolves both candidate policies at most once per change, for the seam block's status lines and the
        /// Scene View overlay.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Scene View tool reads the results through the host properties, so a repaint that also draws the
        /// overlay still reads one resolution per change rather than one per surface.
        /// </para>
        /// <para>
        /// The expensive path — reading <c>Mesh.colors32</c> and rebuilding the candidate arrays — is entered
        /// only when the cache says the inputs changed. The color-code field commits at most one change per valid
        /// code (see <see cref="CommitSeamColorCode"/>), so typing an incomplete code resolves nothing, and the
        /// generate action re-resolves unconditionally.
        /// </para>
        /// </remarks>
        private void EnsureSeamCandidates()
        {
            if (!SeamCandidates.NeedsRefresh(_liveChecksDirty)) return;
            RefreshSeamCandidates();
        }

        /// <summary>
        /// One line describing a resolved candidate set: its count and the policy that produced it, or the
        /// blocking diagnostic that stops the generator. Never a fallback description, because there is no
        /// fallback.
        /// </summary>
        private static string DescribeCandidates(ApaSeamColorCandidateResult candidates)
        {
            if (candidates == null) return Tr("not resolved");
            return candidates.Succeeded ? candidates.Describe() : ApaDiagnosticText.FormatShort(candidates.Issue);
        }

        private void DrawUvSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("UV Semantics"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Infer From Part Mesh"), GUILayout.Width(170)))
            {
                var mesh = _selection.PartGeometryMesh;
                if (mesh == null || !mesh.isReadable)
                {
                    SetStatus(Tr("Select a readable part mesh before inferring UV semantics."));
                }
                else
                {
                    Undo.RecordObject(this, Tr("Infer UV Semantics"));
                    _draft.InferUvSemanticsFrom(MeshSnapshotFactory.Capture(mesh, null, out _));
                    MarkSelectionDirty();
                    SetStatus(TrFormat(
                        "Inferred {0} UV semantic(s) from the part mesh.", _draft.UvSemantics.Length));
                }
            }

            if (GUILayout.Button(Tr("Add Row"), GUILayout.Width(80)))
            {
                Undo.RecordObject(this, Tr("Add UV Semantic"));
                _draft.AddUvSemantic();
                MarkSelectionDirty();
            }

            EditorGUILayout.EndHorizontal();

            var semantics = _draft.UvSemantics;
            var lastUvRow = semantics.Length - 1;
            for (var i = 0; i < semantics.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();

                var entry = semantics[i];
                EditorGUI.BeginChangeCheck();
                var name = EditorGUILayout.TextField(entry.Semantic, GUILayout.MinWidth(120));
                var channel = EditorGUILayout.IntField(entry.SourceChannel, GUILayout.Width(40));

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, Tr("Edit UV Semantic"));
                    entry.Semantic = name;
                    entry.SourceChannel = channel;
                }

                // A row at the end of the list has nowhere to move to, so the button is disabled rather than
                // drawn enabled and doing nothing.
                EditorGUI.BeginDisabledGroup(i == 0);
                if (GUILayout.Button("↑", GUILayout.Width(24)))
                {
                    Undo.RecordObject(this, Tr("Move UV Semantic"));
                    _draft.MoveUvSemantic(i, i - 1);
                    MarkSelectionDirty();
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(i == lastUvRow);
                if (GUILayout.Button("↓", GUILayout.Width(24)))
                {
                    Undo.RecordObject(this, Tr("Move UV Semantic"));
                    _draft.MoveUvSemantic(i, i + 1);
                    MarkSelectionDirty();
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("x", GUILayout.Width(24)))
                {
                    Undo.RecordObject(this, Tr("Remove UV Semantic"));
                    _draft.RemoveUvSemanticAt(i);
                    MarkSelectionDirty();
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.LabelField(
                Tr("Source channel of the part mesh that carries each semantic. Names are trimmed and compared " +
                   "ordinally (case-sensitive)."), EditorStyles.miniLabel);

            // The section carries its own diagnostics (APA018/APA019/APA020, and the channel-absence error this
            // window adds) instead of leaving them to the global block, because this is where the mistake is
            // made and where it is cheapest to fix.
            DrawIssuesForPhase(DraftDataIssues(), ApaIssuePhase.Uv, Tr("UV semantic problems"));
        }

        private void DrawMaterialSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Material Semantics"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Infer From Materials"), GUILayout.Width(170)))
            {
                var renderer = _selection.PartRenderer;
                if (renderer == null)
                {
                    SetStatus(Tr("Select a part renderer before inferring material semantics."));
                }
                else
                {
                    Undo.RecordObject(this, Tr("Infer Material Semantics"));
                    var subMeshCount = _selection.PartGeometryMesh != null
                        ? _selection.PartGeometryMesh.subMeshCount
                        : 0;
                    _draft.InferMaterialSemanticsFrom(subMeshCount, renderer.sharedMaterials);
                    MarkSelectionDirty();
                    SetStatus(TrFormat("Inferred {0} material semantic(s).", _draft.MaterialSemantics.Length));
                }
            }

            if (GUILayout.Button(Tr("Add Row"), GUILayout.Width(80)))
            {
                Undo.RecordObject(this, Tr("Add Material Semantic"));
                _draft.AddMaterialSemantic();
                MarkSelectionDirty();
            }

            EditorGUILayout.EndHorizontal();

            var semantics = _draft.MaterialSemantics;
            var lastMaterialRow = semantics.Length - 1;
            for (var i = 0; i < semantics.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();

                var entry = semantics[i];
                EditorGUI.BeginChangeCheck();
                var name = EditorGUILayout.TextField(entry.Semantic, GUILayout.MinWidth(100));
                var subMesh = EditorGUILayout.IntField(entry.SourceSubMesh, GUILayout.Width(36));
                var material = (Material)EditorGUILayout.ObjectField(entry.Material, typeof(Material), false);
                var policy = MaterialPolicyPopup(entry.Policy, GUILayout.Width(90));

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, Tr("Edit Material Semantic"));
                    entry.Semantic = name;
                    entry.SourceSubMesh = subMesh;
                    entry.Material = material;
                    entry.Policy = policy;
                }

                // Declaration order is what the material resolver preserves when it numbers new slots, so it is
                // author-visible behaviour rather than cosmetics, exactly as it is for UV semantics. A row at the
                // end of the list has nowhere to move to, so the button is disabled rather than doing nothing.
                EditorGUI.BeginDisabledGroup(i == 0);
                if (GUILayout.Button("↑", GUILayout.Width(24)))
                {
                    Undo.RecordObject(this, Tr("Move Material Semantic"));
                    _draft.MoveMaterialSemantic(i, i - 1);
                    MarkSelectionDirty();
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(i == lastMaterialRow);
                if (GUILayout.Button("↓", GUILayout.Width(24)))
                {
                    Undo.RecordObject(this, Tr("Move Material Semantic"));
                    _draft.MoveMaterialSemantic(i, i + 1);
                    MarkSelectionDirty();
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("x", GUILayout.Width(24)))
                {
                    Undo.RecordObject(this, Tr("Remove Material Semantic"));
                    _draft.RemoveMaterialSemanticAt(i);
                    MarkSelectionDirty();
                    EditorGUILayout.EndHorizontal();
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.LabelField(
                Tr("The material field is this profile's authoring default. The build takes the part renderer's " +
                   "own material in that slot and falls back to this asset only when the renderer has none " +
                   "there, so replacing a material on the renderer is what changes the built avatar and is " +
                   "never written back into this profile. A material must be a project asset: a scene material " +
                   "cannot be stored in a reusable profile (APA034). The asset name is never used as the " +
                   "semantic."), EditorStyles.miniLabel);

            DrawIssuesForPhase(DraftDataIssues(), ApaIssuePhase.Materials, Tr("Material semantic problems"));
        }

        /// <summary>
        /// The bone and blend shape policy.
        /// </summary>
        /// <remarks>
        /// <b>The legacy merge-name controls are gone (M10).</b> The merge target path text field, the prefix and
        /// suffix fields, and the inference toggle all described a name-matching policy that no longer decides
        /// anything: the two Armature selections in the Selection block are the merge configuration now, and the
        /// generated Modular Avatar component is written with an empty prefix and suffix so its exact-name
        /// matching mirrors the armature-relative identity. The serialized fields are still read and written so
        /// an existing asset round-trips, but they are not author-facing.
        /// </remarks>
        private void DrawPolicySection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Bones And Blend Shapes"), EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            var mergeArmature = EditorGUILayout.Toggle(
                Content(
                    "Merge Armature",
                    "When enabled, a transient Modular Avatar merge configuration is generated at build time on " +
                    "the selected part armature, targeting the selected target armature. It is written with no " +
                    "prefix, no suffix, and no inference, so it matches bones by exact name — which is the same " +
                    "relation the armature-relative identity describes."),
                _draft.Bones.MergeArmature);

            var allowPartOnly = EditorGUILayout.Toggle(
                Tr("Allow Part-Only Blend Shapes"), _draft.BlendShapes.AllowPartOnlyShapes);

            if (EditorGUI.EndChangeCheck())
            {
                _draft.Bones.MergeArmature = mergeArmature;
                _draft.BlendShapes.AllowPartOnlyShapes = allowPartOnly;

                // The bone policy is part of the plannable configuration (it decides the merge postcondition and
                // the final bone table), so a cached live check is stale.
                MarkSelectionDirty();
            }

            EditorGUILayout.LabelField(
                TrFormat("Armatures: {0}", _draft.Bones.DescribeArmatures()) +
                (mergeArmature ? string.Empty : Tr(" (armature merge is off, so no merge is generated)")),
                EditorStyles.miniLabel);

            EditorGUILayout.LabelField(
                Tr("The legacy merge target path, prefix, suffix, and inference fields are no longer shown: the " +
                   "two Armature selections decide the merge, and nothing is inferred from names."),
                EditorStyles.miniLabel);

            EditorGUILayout.LabelField(
                Tr("The strict blend shape rules (frame agreement, zero seam deltas for part-only shapes) are " +
                   "always enforced and are not configurable."), EditorStyles.miniLabel);
        }

        private void DrawActionsSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Actions"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Validate"), GUILayout.Height(24))) RunValidation();
            if (GUILayout.Button(Tr("Dry-Run Assembly"), GUILayout.Height(24))) RunDryRun();
            EditorGUILayout.EndHorizontal();

            if (_dryRun != null)
            {
                EditorGUILayout.HelpBox(
                    _dryRun.Succeeded
                        ? TrFormat("Plan succeeded: {0}. No mesh was created.", _dryRun.Describe())
                        : Tr("Plan failed. Nothing would be built."),
                    _dryRun.Succeeded ? MessageType.Info : MessageType.Error);
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Save Profile Asset"), GUILayout.Height(24))) WriteProfile();
            var profileAsset = ApaAssetDatabaseUtility.Load<ApaPartProfile>(_profilePath);
            EditorGUI.BeginDisabledGroup(profileAsset == null);
            if (GUILayout.Button(Tr("Select Profile"), GUILayout.Height(24), GUILayout.Width(110)))
            {
                UnityEditor.Selection.activeObject = profileAsset;
                EditorGUIUtility.PingObject(profileAsset);
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Create Part Prefab"), GUILayout.Height(24))) CreatePrefab();
            if (GUILayout.Button(Tr("Update Installer On Prefab"), GUILayout.Height(24))) UpdatePrefabInstaller();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                Tr("The prefab references the saved profile and resolves the target body through the captured " +
                   "renderer path. The authoring scene's target renderer is never serialized into the prefab."),
                EditorStyles.miniLabel);

            if (_lastWriteIssues != null && _lastWriteIssues.Issues.Count > 0)
            {
                DrawIssueList(_lastWriteIssues, Tr("Last write reported"));
            }
        }

        private void DrawDiagnosticsSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Tr("Diagnostics"), EditorStyles.boldLabel);

            if (_validation == null)
            {
                EditorGUILayout.LabelField(Tr("Not validated yet."), EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField(ApaDiagnosticText.Summarize(_validation));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Tr("Copy"), GUILayout.Width(60)))
            {
                EditorGUIUtility.systemCopyBuffer = ApaDiagnosticText.FormatAll(_validation.Issues);
                SetStatus(TrFormat("Copied {0} diagnostic(s) to the clipboard.", _validation.Issues.Count));
            }

            EditorGUILayout.EndHorizontal();

            for (var i = 0; i < _validation.Issues.Count; i++)
            {
                var issue = _validation.Issues[i];
                var previous = GUI.color;
                GUI.color = ApaDiagnosticText.ColorOf(issue.Severity);
                EditorGUILayout.LabelField(ApaDiagnosticText.Format(issue), EditorStyles.wordWrappedMiniLabel);
                GUI.color = previous;
            }
        }

        private void DrawIssueList(ValidationResult result, string emptyMessage)
        {
            if (result == null || result.Issues.Count == 0)
            {
                if (!string.IsNullOrEmpty(emptyMessage))
                {
                    EditorGUILayout.LabelField(emptyMessage + ": " + Tr("none"), EditorStyles.miniLabel);
                }

                return;
            }

            for (var i = 0; i < result.Issues.Count; i++)
            {
                var issue = result.Issues[i];
                var previous = GUI.color;
                GUI.color = ApaDiagnosticText.ColorOf(issue.Severity);
                EditorGUILayout.LabelField(ApaDiagnosticText.Format(issue), EditorStyles.wordWrappedMiniLabel);
                GUI.color = previous;
            }
        }

        private void DrawIssueList(List<ValidationIssue> issues, string emptyMessage)
        {
            if (issues == null || issues.Count == 0)
            {
                EditorGUILayout.LabelField(emptyMessage + ": " + Tr("none"), EditorStyles.miniLabel);
                return;
            }

            for (var i = 0; i < issues.Count; i++)
            {
                var issue = issues[i];
                var previous = GUI.color;
                GUI.color = ApaDiagnosticText.ColorOf(issue.Severity);
                EditorGUILayout.LabelField(ApaDiagnosticText.Format(issue), EditorStyles.wordWrappedMiniLabel);
                GUI.color = previous;
            }
        }

        /// <summary>
        /// Draws the draft-data issues belonging to one phase, so a section shows its own problems. The list is
        /// always drawn (with a "none" line) because the section it belongs to is always drawn; an empty list is
        /// itself information here.
        /// </summary>
        private void DrawIssuesForPhase(List<ValidationIssue> issues, ApaIssuePhase phase, string emptyMessage)
        {
            List<ValidationIssue> matching = null;
            if (issues != null)
            {
                for (var i = 0; i < issues.Count; i++)
                {
                    if (issues[i].Phase != phase) continue;
                    matching = matching ?? new List<ValidationIssue>();
                    matching.Add(issues[i]);
                }
            }

            DrawIssueList(matching ?? new List<ValidationIssue>(), emptyMessage);
        }

        /// <summary>
        /// The removal address list: a read-only review of the set the mask generated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A mask-generated selection routinely holds thousands of addresses, and drawing one row per address
        /// buried every control below it. The list is therefore a summary line — the label carries the total, so
        /// the count is always visible — plus an explicit expansion that draws at most
        /// <see cref="MaxExpandedAddressRows"/> rows and states how many are not shown.
        /// </para>
        /// <para>
        /// <b>Review only (M16).</b> The rows carry no per-triangle remove button: individual triangle editing is
        /// gone with the pick mode and the address entry fields, and a remove button here would be exactly the
        /// manual path this iteration removed. The set is corrected by re-applying the mask — Replace, Add To
        /// Selection, or Subtract From Selection — which is the one operation that acts on the whole canonical
        /// set, and by <c>Clear</c>.
        /// </para>
        /// <para>
        /// <see cref="MaxListedRows"/> is deliberately left alone: it is shared with the other lists in the
        /// window, and lowering it to suit this one would silently truncate them.
        /// </para>
        /// </remarks>
        private void DrawAddressList(ApaRemovalMask mask)
        {
            if (mask.IsEmpty) return;

            EditorGUILayout.BeginHorizontal();
            _showAllAddresses = EditorGUILayout.Foldout(
                _showAllAddresses,
                TrFormat("Addresses ({0})", mask.Count),
                true);

            if (GUILayout.Button(Tr("Collapse"), GUILayout.Width(80)))
            {
                _showAllAddresses = false;
                GUI.changed = true;
            }

            EditorGUILayout.EndHorizontal();

            if (!_showAllAddresses)
            {
                EditorGUILayout.LabelField(
                    TrFormat("Showing the summary only: {0} address(es) are selected.", mask.Count),
                    EditorStyles.miniLabel);
                return;
            }

            // The shared ceiling applies on top of the local budget, so no future change to the local one can
            // make this list unbounded.
            var budget = Mathf.Min(MaxExpandedAddressRows, MaxListedRows);
            var shown = Mathf.Min(mask.Count, budget);
            for (var i = 0; i < shown; i++)
            {
                EditorGUILayout.LabelField(mask.GetAddress(i).ToString(), EditorStyles.miniLabel);
            }

            if (mask.Count > shown)
            {
                EditorGUILayout.LabelField(
                    TrFormat("… and {0} more (re-apply the mask to change the whole set)", mask.Count - shown),
                    EditorStyles.miniLabel);
            }
        }

        // ---- Actions --------------------------------------------------------------------------------

        /// <summary>
        /// Captures the target compatibility signature, scoped to the selected target armature.
        /// </summary>
        /// <remarks>
        /// The armature selection is passed through because the bone half of the signature is recorded relative to
        /// it: capturing against the avatar root instead would record a scope the build never compares in, and
        /// every profile would report a bone mismatch against its own body.
        /// </remarks>
        private void CaptureSignatureIfNeeded(bool force)
        {
            if (!force && !_captureSignatureOnTargetChange) return;

            var profile = ApaCompatibilityCapture.Capture(
                _selection.AvatarRoot, _selection.TargetRenderer, out var issues, _selection.TargetArmature);

            Undo.RecordObject(this, Tr("Capture Compatibility Signature"));
            _draft.Compatibility = profile;
            MarkSelectionDirty();

            SetStatus(issues.Count == 0
                ? TrFormat("Captured the target signature: {0}.", ApaCompatibilityCapture.Describe(profile))
                : TrFormat(
                    "Captured the target signature with {0} issue(s); see the diagnostics below.", issues.Count));

            if (issues.Count > 0)
            {
                _lastWriteIssues = ValidationResult.Build(issues);
            }
        }

        private void LoadProfileFromPanel()
        {
            var path = EditorUtility.OpenFilePanelWithFilters(
                Tr("Open an Avatar Part profile"), Application.dataPath, new[] { Tr("Profile asset"), "asset" });

            if (string.IsNullOrEmpty(path)) return;

            var projectRelative = ToProjectRelative(path);
            var asset = ApaAssetDatabaseUtility.Load<ApaPartProfile>(projectRelative);
            if (asset == null)
            {
                SetStatus(TrFormat(
                    "No profile asset found at '{0}'. Profiles must live inside the project's Assets folder.",
                    projectRelative));
                return;
            }

            LoadProfile(asset);
        }

        /// <summary>Reads a profile asset into the draft. The asset itself is never written.</summary>
        /// <remarks>
        /// <para>
        /// A profile that carries no stored part id is reported here rather than only at build time: the draft
        /// receives the id the build already derives from the profile asset's GUID
        /// (<see cref="ApaPartIdentityResolver"/>), so saving the profile stores the identity the pipeline has
        /// been using instead of replacing it.
        /// </para>
        /// <para>
        /// <b>Loading is a replacement, not a merge.</b> Everything the profile owns comes from the asset, and
        /// every value derived from the previous draft, selection, or mesh is dropped in the same edit
        /// (<see cref="InvalidateDerivedState"/>), so nothing the previous profile resolved — its candidates, its
        /// merge-check classification, its cached mesh arrays, its status lines — can be served after this one is
        /// loaded. The transition itself is <see cref="ApaProfileLoader"/>, shared with the installer inspector's
        /// shortcut so the two entry points cannot diverge.
        /// </para>
        /// <para>
        /// <b>The armature selections are restored, not cleared.</b> The profile carries the two armature
        /// selections as paths, and the live object fields are re-resolved from them as soon as the draft holds
        /// them, so loading a profile shows the selections it declares. A path that does not resolve leaves the
        /// field empty — the previous profile's live reference is never retained — and the path stays in the draft
        /// for the selection check to report.
        /// </para>
        /// </remarks>
        private void LoadProfile(ApaPartProfile asset)
        {
            if (asset == null) return;

            Undo.RecordObject(this, Tr("Load Avatar Part Profile"));
            ApplyLoadedProfile(asset, AssetDatabase.GetAssetPath(asset));
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Applies a loaded profile to the window's state. The caller records undo and repaints.
        /// </summary>
        /// <remarks>
        /// The order is the contract: the draft and the two paths come from the asset, then the derived state is
        /// dropped, and only then is the status line written — so the line describes the state that is actually in
        /// effect rather than being cleared by the reset that follows it.
        /// </remarks>
        private void ApplyLoadedProfile(ApaPartProfile asset, string profileAssetPath)
        {
            var loaded = ApaProfileLoader.Load(asset, _selection, profileAssetPath);

            _draft = loaded.Draft;
            _profilePath = loaded.ProfilePath;
            _prefabPath = loaded.PrefabPath;

            InvalidateDerivedState();
            ClearResults(loaded.NeedsStablePartId
                ? TrFormat(
                    "Loaded profile '{0}'. The asset is only read until you save. It carries no stored part id, " +
                    "so the draft holds the stable id derived from the profile asset's GUID; saving stores it.",
                    _profilePath)
                : TrFormat("Loaded profile '{0}'. The asset is only read until you save.", _profilePath));
        }

        private static string ToProjectRelative(string absolutePath)
        {
            var normalized = absolutePath.Replace('\\', '/');
            var dataPath = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                return ApaAuthoringAssetPaths.Normalize("Assets" + normalized.Substring(dataPath.Length));
            }

            return ApaAuthoringAssetPaths.Normalize(normalized);
        }

        private void RunValidation()
        {
            if (!EnsureSignatureCaptured(Tr("Validating"))) return;

            var result = ApaAuthoringValidation.Validate(_selection, _draft);
            _validation = result.Validation;
            SetStatus(TrFormat("Validation: {0}", ApaDiagnosticText.Summarize(result.Validation)) +
                      (result.HasContext
                          ? "."
                          : Tr(" (no live target was captured, so only authoring data was checked)")));
        }

        private void RunDryRun()
        {
            if (!EnsureSignatureCaptured(Tr("The dry run"))) return;

            _dryRun = ApaAuthoringValidation.DryRun(_selection, _draft);
            _validation = _dryRun.Validation;
            SetStatus(TrFormat(
                "Dry run: {0} — {1}", _dryRun.Describe(), ApaDiagnosticText.Summarize(_dryRun.Validation)));
        }

        /// <summary>
        /// Captures the target compatibility signature once, when the profile has never captured one and the live
        /// target is usable, before an action that needs it.
        /// </summary>
        /// <param name="action">The action being attempted, used in the status line.</param>
        /// <remarks>
        /// <para>
        /// A brand-new draft has no signature, so every first action used to block with <c>APA012</c> and the
        /// author had to know to press Capture Signature first. Capturing it here removes that dead end without
        /// weakening any rule: the capture is exactly the one the button performs, it reports the same
        /// diagnostics, and the action then continues with the same validation it would have run.
        /// </para>
        /// <para>
        /// <b>Only the genuine initial state is auto-captured.</b> An already captured signature is never
        /// touched, so a profile whose body changed still blocks with the mismatch it has — silently re-capturing
        /// there would overwrite the evidence of exactly the condition the signature exists to detect. A capture
        /// that fails leaves the profile uncaptured and reports its issues, so the failure state survives rather
        /// than being papered over.
        /// </para>
        /// <para>
        /// When the selection is not complete enough to capture at all, this returns true and lets the action's
        /// own validation explain the missing selection, in the words the selection check already uses.
        /// </para>
        /// </remarks>
        /// <returns>True when the action may proceed.</returns>
        private bool EnsureSignatureCaptured(string action)
        {
            if (_draft.Compatibility.IsCaptured) return true;

            if (_selection.AvatarRoot == null || _selection.TargetRenderer == null) return true;

            var mesh = _selection.TargetMesh;
            if (mesh == null || !mesh.isReadable) return true;

            CaptureSignatureIfNeeded(true);

            if (_draft.Compatibility.IsCaptured) return true;

            SetStatus(TrFormat(
                "{0} blocked: the target signature could not be captured, and a profile without one cannot be " +
                "verified. The refusal is shown in the 'Last write reported' block.",
                action));
            return false;
        }

        private void WriteProfile()
        {
            if (!EnsureSignatureCaptured(Tr("Saving the profile"))) return;
            if (!PassesPreWriteValidation(Tr("Saving the profile"))) return;

            var result = ApaProfileWriter.Save(_draft, _profilePath, _allowOverwrite);

            if (result.RequiresOverwriteConfirmation)
            {
                var replace = EditorUtility.DisplayDialog(
                    Tr("Replace existing profile?"),
                    TrFormat(
                        "A profile already exists at\n\n{0}\n\nReplacing it keeps the asset's GUID, so prefabs " +
                        "that reference it keep working. Replace its contents?", result.Path),
                    Tr("Replace"),
                    Tr("Cancel"));

                if (!replace)
                {
                    SetStatus(TrFormat("Profile write cancelled. Nothing was written to '{0}'.", result.Path));
                    return;
                }

                result = ApaProfileWriter.Save(_draft, _profilePath, true);
            }

            _lastWriteIssues = result.Issues;
            _profilePath = string.IsNullOrEmpty(result.Path) ? _profilePath : result.Path;
            MarkSelectionDirty();

            if (result.Succeeded)
            {
                UnityEditor.Selection.activeObject = result.Asset;
                SetStatus(result.Message);
            }
            else
            {
                SetStatus(result.Message);
            }
        }

        private void CreatePrefab()
        {
            if (!EnsureSignatureCaptured(Tr("Creating the prefab"))) return;
            if (!PassesPreWriteValidation(Tr("Creating the prefab"))) return;

            var profileAsset = ResolveSavedProfile();
            if (profileAsset == null) return;

            // The payload path is derived from the prefab path at the moment of creation, so the two files always
            // travel together. It is computed once and reused for the confirmation retry: a path that changed
            // between the two calls would write the payload somewhere the author never saw.
            var protectPartMesh = _protectPartMesh;
            var protectedMeshPath = protectPartMesh
                ? ApaProtectedMeshAssetWriter.DefaultPathFor(_prefabPath)
                : null;

            var result = ApaPrefabGenerator.CreateFromScene(
                _selection, profileAsset, _prefabPath, _allowOverwrite, protectPartMesh, protectedMeshPath);

            if (result.RequiresOverwriteConfirmation)
            {
                // Two different files can be in the way, and they need two different sentences: "a prefab already
                // exists" sends the author to the prefab, "a protected mesh asset already exists" sends them to
                // the payload. The generator reports which one it refused on. When both exist, one dialog names
                // both, because a single "Replace" that silently rewrote a second file would be a confirmation
                // the author never gave.
                var payloadBlocked = result.Status == ApaPrefabStatus.RefusedProtectedOverwrite;
                var existingPayload = protectPartMesh && !string.IsNullOrEmpty(protectedMeshPath)
                    ? ApaAssetDatabaseUtility.Load<ApaProtectedMeshAsset>(protectedMeshPath)
                    : null;

                string title;
                string message;
                if (payloadBlocked)
                {
                    title = Tr("Replace existing protected mesh asset?");
                    message = TrFormat(
                        "A protected mesh asset already exists at\n\n{0}\n\nReplacing it keeps the asset's GUID, " +
                        "so a prefab that already references it keeps working, and it is restored if the prefab " +
                        "cannot be written. Replace it?", result.Path);
                }
                else if (existingPayload != null)
                {
                    title = Tr("Replace existing prefab and protected mesh asset?");
                    message = TrFormat(
                        "A prefab already exists at\n\n{0}\n\nThe protected mesh asset at\n\n{1}\n\nis replaced " +
                        "as well, keeping its GUID so a prefab that already references it keeps working. Use " +
                        "'Update installer on prefab' instead to keep the prefab's own content. Replace both?",
                        result.Path, protectedMeshPath);
                }
                else
                {
                    title = Tr("Replace existing prefab?");
                    message = TrFormat(
                        "A prefab already exists at\n\n{0}\n\nReplacing it writes the current scene part over " +
                        "the existing prefab content. Use 'Update installer on prefab' instead to keep the " +
                        "prefab's own content. Replace it?", result.Path);
                }

                var replace = EditorUtility.DisplayDialog(title, message, Tr("Replace"), Tr("Cancel"));

                if (!replace)
                {
                    SetStatus(TrFormat("Prefab creation cancelled. Nothing was written to '{0}'.", result.Path));
                    return;
                }

                result = ApaPrefabGenerator.CreateFromScene(
                    _selection, profileAsset, _prefabPath, true, protectPartMesh, protectedMeshPath);
            }

            ReportPrefabResult(result);
        }

        private void UpdatePrefabInstaller()
        {
            if (!EnsureSignatureCaptured(Tr("Updating the prefab"))) return;
            if (!PassesPreWriteValidation(Tr("Updating the prefab"))) return;

            var profileAsset = ResolveSavedProfile();
            if (profileAsset == null) return;

            ReportPrefabResult(ApaPrefabGenerator.UpdateInstallerOnPrefab(_prefabPath, profileAsset));
        }

        private void ReportPrefabResult(ApaPrefabResult result)
        {
            _lastWriteIssues = result.Issues;
            MarkSelectionDirty();

            if (result.Succeeded)
            {
                UnityEditor.Selection.activeObject = result.Prefab;
                EditorGUIUtility.PingObject(result.Prefab);
            }

            SetStatus(result.Message);
        }

        /// <summary>
        /// The profile asset the prefab must reference: it has to exist and match the draft, so that a prefab can
        /// never reference a stale or unsaved profile.
        /// </summary>
        private ApaPartProfile ResolveSavedProfile()
        {
            var asset = ApaAssetDatabaseUtility.Load<ApaPartProfile>(_profilePath);
            if (asset == null)
            {
                SetStatus(TrFormat("Save the profile first: no profile asset exists at '{0}'.", _profilePath));
                return null;
            }

            if (ApaProfileWriter.HasUnsavedChanges(asset, _draft))
            {
                SetStatus(TrFormat(
                    "Save the profile first: the draft differs from '{0}', and the prefab must reference what was " +
                    "validated.", _profilePath));
                return null;
            }

            return asset;
        }

        /// <summary>
        /// Runs the full validation, then proves the part can actually be planned, and refuses the write when
        /// either step blocks or when the selection is not complete enough to validate against a live body.
        /// </summary>
        /// <remarks>
        /// The validator checks rules; only the planner proves the assembly can be produced. The blocking
        /// material-coverage verdict (APA009) lives in the planner, not in the rule set, so a profile with a
        /// submesh that has no material slot validates clean and can never be planned. Requiring a successful
        /// dry run here is what stops such a profile — and a prefab generated from it — from being written. The
        /// extra cost is one snapshot pass on a button press, and the plan is kept so the Actions section can
        /// show what was proven.
        /// </remarks>
        private bool PassesPreWriteValidation(string action)
        {
            // Schema-5 profiles carry a part-mesh fingerprint so attribute-only reimports are caught just like
            // target-body reimports. Capture it lazily for a new/legacy draft, but never overwrite an existing
            // value: a non-empty mismatch is evidence that the author must explicitly recapture the profile.
            EnsurePartFingerprintCaptured();

            var validation = ApaAuthoringValidation.Validate(_selection, _draft);
            _validation = validation.Validation;

            if (!_selection.IsComplete)
            {
                SetStatus(TrFormat(
                    "{0} needs a complete selection: avatar root, target body renderer, part root, and part " +
                    "renderer. A profile that cannot be validated against a body must not be written.", action));
                return false;
            }

            if (!validation.IsValid)
            {
                SetStatus(TrFormat(
                    "{0} blocked: {1}. Fix the diagnostics below; nothing was written.",
                    action, ApaDiagnosticText.Summarize(validation.Validation)));
                return false;
            }

            var plan = ApaAuthoringValidation.DryRun(_selection, _draft);
            _dryRun = plan;
            _validation = plan.Validation;

            if (!plan.Succeeded)
            {
                SetStatus(TrFormat(
                    "{0} blocked: the assembly cannot be planned. {1}. Nothing was written.",
                    action, ApaDiagnosticText.Summarize(plan.Validation)));
                return false;
            }

            return true;
        }

        private bool EnsurePartFingerprintCaptured()
        {
            if (!string.IsNullOrEmpty(_draft.PartMeshFingerprint)) return true;

            // The geometry is the live mesh when the part has one, and the decoded payload when it is protected.
            // Both are the same bytes the build would fingerprint, so a profile captured from a protected part
            // records the fingerprint of the mesh that payload was created from.
            var mesh = _selection.PartGeometryMesh;
            if (mesh == null || !mesh.isReadable) return false;

            var fingerprint = ApaMeshFingerprint.OfMesh(mesh);
            if (string.IsNullOrEmpty(fingerprint)) return false;

            Undo.RecordObject(this, Tr("Capture Part Mesh Fingerprint"));
            _draft.PartMeshFingerprint = fingerprint;
            MarkSelectionDirty();
            return true;
        }

        /// <summary>
        /// A cached description of the asset at the profile path, or an empty string when there is none.
        /// </summary>
        /// <remarks>
        /// The comparison behind this line materializes the draft and serializes both profiles to JSON, which is
        /// far too much work for a repaint. It is recomputed only when something changed.
        /// </remarks>
        private string DescribeExistingProfile()
        {
            if (_existingProfileState != null && !_liveChecksDirty) return _existingProfileState;

            var asset = ApaAssetDatabaseUtility.Load<ApaPartProfile>(_profilePath);
            if (asset == null)
            {
                _existingProfileState = string.Empty;
                return _existingProfileState;
            }

            _existingProfileState = ApaProfileWriter.HasUnsavedChanges(asset, _draft)
                ? Tr("exists — the draft differs from it")
                : Tr("exists — the draft matches it");
            return _existingProfileState;
        }

        /// <summary>
        /// Refreshes the two cached staleness verdicts for the captured signature.
        /// </summary>
        /// <remarks>
        /// The two halves are cached separately because they have different consequences: a difference in the
        /// safety fields is what CompatibilityRule compares and what blocks a build with APA012, while a
        /// difference in the recorded renderer path or mesh GUID is advisory. <see cref="NeedsRecapture"/> is
        /// their union, and is what a future caller that only wants "should I offer a recapture" should use.
        /// </remarks>
        private void RefreshSignatureStaleness()
        {
            if (!_liveChecksDirty) return;

            var mesh = _selection.TargetMesh;
            var profile = _draft.Compatibility;

            _signatureSafetyStale = ApaCompatibilityCapture.SafetyFieldsDiffer(profile, mesh);
            _signatureIdentityStale = mesh != null && ApaCompatibilityCapture.IdentityFieldsDiffer(
                profile,
                mesh,
                _selection.TargetRendererPath,
                ApaCompatibilityCapture.ResolveMeshGuid(mesh));
        }

        private ValidationResult CurrentSelectionIssues()
        {
            if (_selectionIssues == null || _liveChecksDirty)
            {
                _selectionIssues = ValidationResult.Build(_selection.Validate());
            }

            return _selectionIssues;
        }

        /// <summary>
        /// The draft-level checks against the live part mesh, cached per change, for the section-local issue
        /// lists. The draft-only checks are cheap; the only mesh read is the UV channel probe, which is not a
        /// full snapshot capture.
        /// </summary>
        private List<ValidationIssue> DraftDataIssues()
        {
            if (_draftDataIssues == null || _liveChecksDirty)
            {
                _draftDataIssues = ApaAuthoringValidation.ValidateDraftDataAgainstMesh(
                    _draft, _selection.PartGeometryMesh);
            }

            return _draftDataIssues;
        }

        private ValidationResult ValidateRemoval()
        {
            if (_removalCheck != null && !_liveChecksDirty) return _removalCheck;

            var mask = _draft.Removal;
            if (mask.IsEmpty && !mask.HasCorruptStorage)
            {
                _removalCheck = ValidationResult.Empty;
                return _removalCheck;
            }

            var mesh = _selection.TargetMesh;

            // Reading an unreadable mesh's index buffers produces Unity error output and empty arrays, which
            // would make the range check below answer "no triangles" and accuse the author of an out-of-range
            // address that is really a Read/Write setting. The mesh is reported as the problem instead.
            if (mesh != null && !mesh.isReadable)
            {
                _removalCheck = ValidationResult.Single(ApaCompatibilityCapture.NotReadableIssue(mesh));
                return _removalCheck;
            }

            List<int> counts = null;
            List<MeshTopology> topologies = null;

            if (mesh != null)
            {
                var subMeshCount = Mathf.Max(mesh.subMeshCount, 0);
                counts = new List<int>(subMeshCount);
                topologies = new List<MeshTopology>(subMeshCount);
                for (var i = 0; i < subMeshCount; i++)
                {
                    var indices = mesh.GetIndices(i);
                    counts.Add(indices != null ? indices.Length / ApaMeshLimits.TriangleStride : 0);
                    topologies.Add(mesh.GetTopology(i));
                }
            }

            _removalCheck = ValidationResult.Build(
                mask.Validate(counts, topologies, _draft.Identity.PartId));
            return _removalCheck;
        }

        private ValidationResult ValidateSeam()
        {
            if (_seamCheck != null && !_liveChecksDirty) return _seamCheck;

            var seam = _draft.Seam;
            if (seam.IsEmpty)
            {
                _seamCheck = ValidationResult.Empty;
                return _seamCheck;
            }

            var baseMesh = _selection.TargetMesh;
            var partMesh = _selection.PartGeometryMesh;

            _seamCheck = ValidationResult.Build(seam.Validate(
                baseMesh != null ? baseMesh.vertexCount : -1,
                partMesh != null ? partMesh.vertexCount : -1,
                _draft.Identity.PartId));
            return _seamCheck;
        }

        private void ClearResults(string status)
        {
            _validation = null;
            _dryRun = null;
            _lastWriteIssues = null;
            MarkSelectionDirty();
            SetStatus(status);
        }

        private void SetStatus(string status)
        {
            _status = status ?? string.Empty;
        }

        private static string DescribeTransform(Transform transform)
        {
            if (transform == null) return Tr("(none)");
            return MeshSnapshotFactory.RelativePath(transform.root, transform);
        }
    }
}

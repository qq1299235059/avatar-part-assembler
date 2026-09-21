using System;
using System.Collections.Generic;
using AvatarPartAssembler.Editor.Integration;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The objects and output paths the author has selected in the authoring window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selection state is kept apart from the profile draft, the validation, and the writers so that each can be
    /// reviewed on its own: this type answers "what is being authored", the draft answers "what is the part",
    /// and nothing here writes an asset or mutates a scene object.
    /// </para>
    /// <para>
    /// Every member is serializable, so a window that holds one survives an assembly reload without the author
    /// losing their place. Scenes references are held as plain object references and are never copied into the
    /// profile: the profile records the <i>path</i> of the target renderer, and the reference is only used to
    /// capture that path while the authoring scene is open.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ApaAuthoringSelection
    {
        [SerializeField] private GameObject _avatarRoot;
        [SerializeField] private SkinnedMeshRenderer _targetRenderer;
        [SerializeField] private GameObject _partRoot;
        [SerializeField] private Renderer _partRenderer;
        [SerializeField] private Transform _targetArmature;
        [SerializeField] private Transform _partArmature;
        [SerializeField] private string _outputFolder = ApaAuthoringAssetPaths.DefaultOutputFolder;

        /// <summary>The avatar the part will be installed into.</summary>
        public GameObject AvatarRoot
        {
            get => _avatarRoot;
            set => _avatarRoot = value;
        }

        /// <summary>
        /// The target body renderer whose mesh is replaced. A <see cref="SkinnedMeshRenderer"/> is required
        /// because the schema-v2 signature records a bone signature, which only a skinned renderer has.
        /// </summary>
        public SkinnedMeshRenderer TargetRenderer
        {
            get => _targetRenderer;
            set => _targetRenderer = value;
        }

        /// <summary>Root of the part geometry, as authored in the scene.</summary>
        public GameObject PartRoot
        {
            get => _partRoot;
            set => _partRoot = value;
        }

        /// <summary>The renderer the part geometry is read from.</summary>
        public Renderer PartRenderer
        {
            get => _partRenderer;
            set => _partRenderer = value;
        }

        /// <summary>
        /// The armature inside the avatar that the part's bones merge into (M10).
        /// </summary>
        /// <remarks>
        /// Stored as a live reference while authoring and persisted as
        /// <see cref="TargetArmaturePath"/> — the path relative to the avatar root — because the reference is a
        /// scene object and the profile is a reusable asset. It must be the avatar root itself or a descendant of
        /// it; <see cref="Validate"/> refuses anything else.
        /// </remarks>
        public Transform TargetArmature
        {
            get => _targetArmature;
            set => _targetArmature = value;
        }

        /// <summary>
        /// The armature inside the part that owns the part's own bones (M10).
        /// </summary>
        /// <remarks>
        /// Persisted as <see cref="PartArmaturePath"/> — the path relative to the part root. It must be the part
        /// root itself or a descendant of it.
        /// </remarks>
        public Transform PartArmature
        {
            get => _partArmature;
            set => _partArmature = value;
        }

        /// <summary>Folder offered for newly authored profiles and prefabs.</summary>
        public string OutputFolder
        {
            get => string.IsNullOrEmpty(_outputFolder)
                ? ApaAuthoringAssetPaths.DefaultOutputFolder
                : _outputFolder;
            set => _outputFolder = value ?? string.Empty;
        }

        /// <summary>The selected target mesh, or null.</summary>
        public Mesh TargetMesh => ApaCompatibilityCapture.ResolveMesh(_targetRenderer);

        /// <summary>The selected part mesh, or null.</summary>
        public Mesh PartMesh => ApaCompatibilityCapture.ResolveMesh(_partRenderer);

        // --- Protected part geometry ---------------------------------------------------------------------
        //
        // A protected prefab is saved with its part renderer carrying no mesh at all: the geometry lives in an
        // encrypted payload the installer references. Everything below resolves that payload lazily, keeps the
        // transient decoded mesh for as long as it describes the part, and drops it the moment anything that
        // decided it changes. Nothing here is serialized: the fields are [NonSerialized] and the mesh is created
        // with HideAndDontSave, so a window that is closed, reloaded, or saved never carries decoded geometry
        // into a scene or a project asset.

        [NonSerialized] private bool _protectedResolved;
        [NonSerialized] private string _protectedIdentity = string.Empty;
        [NonSerialized] private bool _protectedPresent;
        [NonSerialized] private ApaProtectedOverlayGeometry _protectedGeometry;
        [NonSerialized] private ValidationIssue _protectedIssue;

        /// <summary>
        /// The protected payload that stands in for the part renderer's mesh, or null for an ordinary part.
        /// </summary>
        public ApaProtectedMeshAsset ProtectedMesh =>
            ApaProtectedPartGeometry.ResolveAsset(_partRenderer, _partRoot, out _);

        /// <summary>True when the part root carries an installer that references a protected payload.</summary>
        public bool HasProtectedMesh => ProtectedMesh != null;

        /// <summary>
        /// The geometry the authoring overlays read: the live part mesh when there is one, otherwise the transient
        /// mesh decoded from the part's protected payload.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The overlays — seam candidates, merge check, removal picking — read vertex positions, colors, and
        /// submesh counts from a <see cref="Mesh"/>. A protected prefab has no live mesh, so without this the
        /// overlays would silently draw nothing for exactly the parts the feature was used on.
        /// </para>
        /// <para>
        /// <b>A live mesh always wins.</b> A prefab that still carries a real mesh is the ordinary path and is
        /// returned unchanged; the payload is only consulted when there is nothing else to read, which is the same
        /// precedence the build applies.
        /// </para>
        /// <para>
        /// The returned mesh is never assigned to the renderer, never saved, and destroyed by
        /// <see cref="InvalidateProtectedGeometry"/>. Reading it is safe for every overlay because the payload
        /// carries the same attributes <c>MeshSnapshotFactory</c> reads from a real mesh.
        /// </para>
        /// </remarks>
        public Mesh PartGeometryMesh
        {
            get
            {
                var live = PartMesh;
                if (live != null) return live;

                EnsureProtectedGeometry();
                return _protectedGeometry != null ? _protectedGeometry.Mesh : null;
            }
        }

        /// <summary>The decoded protected payload of the part, or null when the part is ordinary or unusable.</summary>
        public ApaProtectedMeshData ProtectedData
        {
            get
            {
                EnsureProtectedGeometry();
                return _protectedGeometry != null ? _protectedGeometry.Data : null;
            }
        }

        /// <summary>
        /// The blocking diagnostic when the part is protected but its payload cannot be used, or null.
        /// </summary>
        /// <remarks>
        /// Null means "no payload" as well as "a usable payload": a caller that must distinguish the two asks
        /// <see cref="HasProtectedMesh"/>.
        /// </remarks>
        public ValidationIssue ProtectedGeometryIssue
        {
            get
            {
                EnsureProtectedGeometry();
                return _protectedIssue;
            }
        }

        /// <summary>
        /// Drops the transient decoded geometry and destroys its mesh.
        /// </summary>
        /// <remarks>
        /// The window calls this from its derived-state invalidation — on a selection change, an undo, a profile
        /// load, and a language switch — and from <c>OnDisable</c>, which is the whole lifetime contract: decoded
        /// geometry never outlives the session that asked for it. Disposing is idempotent, so an invalidation that
        /// races a close is harmless.
        /// </remarks>
        public void InvalidateProtectedGeometry()
        {
            _protectedResolved = false;
            _protectedIdentity = string.Empty;
            _protectedPresent = false;
            _protectedIssue = null;

            var geometry = _protectedGeometry;
            _protectedGeometry = null;
            if (geometry != null) geometry.Dispose();
        }

        /// <summary>Releases the transient decoded geometry this selection owns.</summary>
        public void Dispose()
        {
            InvalidateProtectedGeometry();
        }

        /// <summary>
        /// Resolves the protected overlay geometry, reusing it while the payload identity is unchanged.
        /// </summary>
        /// <remarks>
        /// The identity check is the cheap half of the contract: an unchanged payload costs one identity string
        /// built from a few byte reads, and a changed renderer, asset, part id, or payload revision costs one
        /// cached decode plus one transient mesh. A payload that fails to decode is cached as a failure too, so a
        /// corrupt asset is not re-decrypted once per repaint — and it starts working again the moment the asset
        /// is repaired, because the repaired bytes produce a different identity.
        /// </remarks>
        private void EnsureProtectedGeometry()
        {
            var identity = ApaProtectedPartGeometry.IdentityOf(_partRenderer, _partRoot);

            if (_protectedResolved && string.Equals(_protectedIdentity, identity, StringComparison.Ordinal))
            {
                return;
            }

            InvalidateProtectedGeometry();

            _protectedResolved = true;
            _protectedIdentity = identity;
            _protectedPresent = ApaProtectedPartGeometry.ResolveAsset(_partRenderer, _partRoot, out _) != null;
            if (!_protectedPresent) return;

            _protectedGeometry = ApaProtectedOverlayGeometry.TryCreate(_partRenderer, _partRoot, out var issue);
            _protectedIssue = issue;
        }

        /// <summary>The avatar root transform, or null.</summary>
        public Transform AvatarRootTransform => _avatarRoot != null ? _avatarRoot.transform : null;

        /// <summary>True when a target body renderer is selected.</summary>
        public bool HasTarget => _targetRenderer != null;

        /// <summary>True when a part root and renderer are selected.</summary>
        public bool HasPart => _partRoot != null && _partRenderer != null;

        /// <summary>True when every object a full validation needs is selected.</summary>
        public bool IsComplete => _avatarRoot != null && _targetRenderer != null && HasPart;

        /// <summary>
        /// Avatar-root-relative path the signature would record for the target armature, or empty when none is
        /// selected.
        /// </summary>
        public string TargetArmaturePath =>
            MeshSnapshotFactory.RelativePath(AvatarRootTransform, _targetArmature);

        /// <summary>
        /// Part-root-relative path the profile would record for the part armature, or empty when none is
        /// selected.
        /// </summary>
        public string PartArmaturePath =>
            MeshSnapshotFactory.RelativePath(PartRootTransform, _partArmature);

        /// <summary>True when both armature selections are made.</summary>
        public bool HasArmatureSelection => _targetArmature != null && _partArmature != null;

        /// <summary>
        /// True when the selected target body renderer carries bones, so an armature selection is required.
        /// </summary>
        /// <remarks>
        /// The same rule the build applies (<see cref="ApaArmatureScope.RequiresScope"/>): a renderer with no bone
        /// list has no bone identity to scope, so no selection is demanded for it.
        /// </remarks>
        public bool TargetNeedsArmature => ApaArmatureScope.RequiresScope(_targetRenderer);

        /// <summary>True when the selected part renderer carries bones, so an armature selection is required.</summary>
        public bool PartNeedsArmature => ApaArmatureScope.RequiresScope(_partRenderer);

        /// <summary>Avatar-root-relative path the signature would record for the target renderer.</summary>
        public string TargetRendererPath => ApaCompatibilityCapture.ResolveRendererPath(_avatarRoot, _targetRenderer);

        /// <summary>Avatar-root-relative path of the part root, used in diagnostics only.</summary>
        public string PartRootPath => MeshSnapshotFactory.RelativePath(AvatarRootTransform, PartRootTransform);

        /// <summary>Avatar-root-relative path of the part renderer, used in diagnostics only.</summary>
        public string PartRendererPath => MeshSnapshotFactory.RelativePath(AvatarRootTransform, PartRendererTransform);

        private Transform PartRootTransform => _partRoot != null ? _partRoot.transform : null;

        private Transform PartRendererTransform => _partRenderer != null ? _partRenderer.transform : null;

        /// <summary>
        /// Reports what stops the selection from being used, with the same codes and detail tokens the assembly
        /// core uses for the same conditions.
        /// </summary>
        /// <remarks>
        /// The messages for a missing mesh, an unreadable mesh, and a missing part renderer are the ones
        /// <see cref="ContextBuilder"/> and <see cref="MeshSnapshotFactory"/> produce, so an author sees one
        /// explanation for one defect whether it is reported by the window or by a build.
        /// </remarks>
        public List<ValidationIssue> Validate()
        {
            var issues = new List<ValidationIssue>();

            if (_avatarRoot == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The avatar root is null.",
                    detail: "reason=null-avatar-root"));
            }

            if (_targetRenderer == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "No target body renderer is selected.",
                    detail: "reason=missing-target-renderer"));
            }
            else if (TargetMesh == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target renderer '" + _targetRenderer.name + "' has no mesh assigned.",
                    detail: "renderer=" + _targetRenderer.name));
            }
            else if (!TargetMesh.isReadable)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.UnsupportedMeshAttribute,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + TargetMesh.name + "' is not readable. Enable Read/Write in its import settings.",
                    detail: "mesh=" + TargetMesh.name + "; reason=not-readable"));
            }

            if (_targetRenderer != null && _avatarRoot != null
                && !IsUnder(_targetRenderer.transform, _avatarRoot.transform))
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The target renderer '" + _targetRenderer.name + "' is not inside the selected avatar root '" +
                    _avatarRoot.name + "'. The signature records an avatar-root-relative path, so a renderer " +
                    "outside the root would be recorded as a scene path that stops resolving once the part is " +
                    "installed.",
                    detail: "reason=target-renderer-outside-avatar-root; path=" + TargetRendererPath));
            }

            if (_partRoot == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "No part root is selected.",
                    detail: "reason=missing-part-root"));
            }
            else if (_partRenderer == null)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "Part root '" + _partRoot.name + "' has no Renderer to take geometry from.",
                    detail: "partRoot=" + _partRoot.name));
            }
            else if (PartMesh == null)
            {
                // A protected prefab is saved with its part renderer carrying no mesh on purpose, so a null mesh
                // is expected rather than a defect when the installer references a payload that decodes. The
                // payload is decoded through the shared cache here — the same decode the build and the preview
                // use — and a payload that is missing, corrupt, truncated, or written for another part fails
                // closed with the codec's own APA053 instead of the ordinary mesh-less diagnostic. An ordinary
                // mesh-less part still reports APA006, because for it the message and the remedy are both right.
                EnsureProtectedGeometry();
                if (_protectedPresent)
                {
                    if (_protectedIssue != null) issues.Add(_protectedIssue);
                }
                else
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.TargetRendererNotFound,
                        ApaIssuePhase.Compatibility,
                        "Part renderer '" + _partRenderer.name + "' has no mesh assigned, so there is no geometry to " +
                        "assemble. Assign the source mesh, or create the part prefab in protected mode so its mesh " +
                        "travels in a protected payload the build can decode.",
                        detail: "reason=missing-part-mesh; renderer=" + _partRenderer.name));
                }
            }
            else if (!PartMesh.isReadable)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.UnsupportedMeshAttribute,
                    ApaIssuePhase.Attributes,
                    "Mesh '" + PartMesh.name + "' is not readable. Enable Read/Write in its import settings.",
                    detail: "mesh=" + PartMesh.name + "; reason=not-readable"));
            }

            if (_partRoot != null && _partRenderer != null
                && !IsUnder(_partRenderer.transform, _partRoot.transform))
            {
                // The core resolves the part renderer with GetComponentInChildren from the part root, so a
                // renderer outside the root would silently be replaced by a different one at validation time.
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.TargetRendererNotFound,
                    ApaIssuePhase.Compatibility,
                    "The selected part renderer '" + _partRenderer.name + "' is not inside the part root '" +
                    _partRoot.name + "'. The part geometry would be read from a different renderer than the one " +
                    "selected.",
                    detail: "reason=part-renderer-outside-part-root; partRoot=" + _partRoot.name +
                            "; renderer=" + _partRenderer.name));
            }

            ValidateArmatureSelections(issues);

            return issues;
        }

        /// <summary>
        /// Reports the armature selections that are missing or outside the root they must belong to (M10).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A selection is required exactly when the renderer on that side carries bones, which is the same rule
        /// the build applies (<see cref="ApaArmatureScope.RequiresScope"/>): a renderer with no bone list has no
        /// bone identity to scope, so nothing is demanded of it.
        /// </para>
        /// <para>
        /// The messages match <see cref="ApaArmatureScope.TryResolve"/> and <see cref="ContextBuilder"/>, so the
        /// window and the build describe one defect one way. The codes are the same (<c>APA043</c>) and the detail
        /// carries the same <c>reason=</c> tokens.
        /// </para>
        /// </remarks>
        private void ValidateArmatureSelections(List<ValidationIssue> issues)
        {
            if (_targetArmature == null && TargetNeedsArmature)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "The target body is skinned, but no target armature is selected, so the body's bones have no " +
                    "scope to be recorded in. Select the armature inside the avatar that owns the body's bones.",
                    detail: "reason=missing-target-armature"));
            }
            else if (_targetArmature != null)
            {
                if (_avatarRoot == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.ArmatureSelectionInvalid,
                        ApaIssuePhase.Compatibility,
                        "A target armature is selected but no avatar root is, so its avatar-root-relative path " +
                        "cannot be recorded.",
                        detail: "reason=target-armature-not-found; path=" + TargetArmaturePath));
                }
                else if (!IsUnder(_targetArmature, _avatarRoot.transform))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.ArmatureSelectionInvalid,
                        ApaIssuePhase.Compatibility,
                        "The selected target armature '" + _targetArmature.name + "' is not inside the avatar root " +
                        "'" + _avatarRoot.name + "'. A bone identity is recorded relative to the selected " +
                        "armature, so an armature outside the avatar has no path this pipeline can resolve after " +
                        "the build clone is relocated.",
                        detail: "reason=target-armature-outside-root; path=" + TargetArmaturePath));
                }
            }

            if (_partArmature == null && PartNeedsArmature)
            {
                issues.Add(ValidationIssue.Error(
                    ApaErrorCode.ArmatureSelectionInvalid,
                    ApaIssuePhase.Compatibility,
                    "The part is skinned, but no part armature is selected, so its bones have no scope to be " +
                    "recorded in and cannot be identified as the body's joints. Select the armature inside the " +
                    "part that owns the part's bones.",
                    detail: "reason=missing-part-armature"));
            }
            else if (_partArmature != null)
            {
                if (_partRoot == null)
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.ArmatureSelectionInvalid,
                        ApaIssuePhase.Compatibility,
                        "A part armature is selected but no part root is, so its part-root-relative path cannot " +
                        "be recorded.",
                        detail: "reason=part-armature-not-found; path=" + PartArmaturePath));
                }
                else if (!IsUnder(_partArmature, _partRoot.transform))
                {
                    issues.Add(ValidationIssue.Error(
                        ApaErrorCode.ArmatureSelectionInvalid,
                        ApaIssuePhase.Compatibility,
                        "The selected part armature '" + _partArmature.name + "' is not inside the part root '" +
                        _partRoot.name + "'. The part's bones are recorded relative to the selected armature, so " +
                        "an armature outside the part root belongs to a different object than the one being " +
                        "assembled.",
                        detail: "reason=part-armature-outside-root; path=" + PartArmaturePath));
                }
            }
        }

        /// <summary>
        /// Fills any empty slot from the current Unity selection, without overwriting a slot the author already
        /// chose. Returns true when something was adopted.
        /// </summary>
        /// <remarks>
        /// The heuristic is narrow on purpose: a selected <see cref="AvatarPartInstaller"/> identifies a part, a
        /// selected <see cref="SkinnedMeshRenderer"/> identifies a target body, and nothing else is guessed.
        /// </remarks>
        public bool AdoptFromUnitySelection(GameObject active)
        {
            if (active == null) return false;

            var changed = false;

            if (_partRoot == null)
            {
                var installer = active.GetComponentInParent<AvatarPartInstaller>();
                var candidate = installer != null ? installer.gameObject : null;
                if (candidate != null)
                {
                    _partRoot = candidate;
                    _partRenderer = candidate.GetComponentInChildren<Renderer>(true);
                    changed = true;
                }
            }

            if (_targetRenderer == null)
            {
                var skinned = active.GetComponentInParent<SkinnedMeshRenderer>();
                if (skinned != null)
                {
                    _targetRenderer = skinned;
                    changed = true;
                }
            }

            if (_avatarRoot == null)
            {
                var anchor = _targetRenderer != null
                    ? _targetRenderer.transform
                    : (_partRoot != null ? _partRoot.transform : active.transform);

                var root = FindAvatarRoot(anchor);
                if (root != null)
                {
                    _avatarRoot = root.gameObject;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>Adopts a renderer as the target body and reports whether anything changed.</summary>
        public bool AdoptTarget(Renderer renderer)
        {
            if (renderer == null) return false;

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned == null || _targetRenderer == skinned) return false;

            _targetRenderer = skinned;
            return true;
        }

        /// <summary>Adopts a GameObject as the part root and its first renderer as the part renderer.</summary>
        public bool AdoptPart(GameObject partRoot)
        {
            if (partRoot == null) return false;
            if (_partRoot == partRoot) return false;

            _partRoot = partRoot;
            _partRenderer = partRoot.GetComponentInChildren<Renderer>(true);
            return true;
        }

        /// <summary>
        /// Restores the two live armature references from the paths a draft or profile recorded.
        /// </summary>
        /// <param name="targetArmaturePath">
        /// Avatar-root-relative path of the target armature, as <see cref="TargetArmaturePath"/> records it. The
        /// empty string means "no selection was recorded" and restores nothing.
        /// </param>
        /// <param name="partArmaturePath">
        /// Part-root-relative path of the part armature, as <see cref="PartArmaturePath"/> records it.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Why a restore is needed at all.</b> The live <see cref="Transform"/> references are what the two
        /// object fields show, but they are not what survives: a closed and reopened window, a layout restore, a
        /// domain reload, and a freshly loaded profile can all come back with the paths intact and the references
        /// gone. Without this the fields would draw as empty and the next edit anywhere in the Selection block
        /// would re-record that emptiness, silently discarding a selection the profile still declares.
        /// </para>
        /// <para>
        /// <b>Fill only by default.</b> A slot that already holds a live selection is left exactly as it is, so
        /// reopening the window cannot override an author's current choice. Profile loading passes
        /// <paramref name="replaceExisting"/> so that the newly loaded profile cannot accidentally retain the
        /// previous draft's live references. This never substitutes for the explicit <see cref="SuggestArmatures"/>
        /// flow, which stays the only thing that proposes an armature that was never selected.
        /// </para>
        /// <para>
        /// <b>Nothing is invented.</b> A path that does not resolve — the hierarchy moved, the object was deleted,
        /// or the root is not selected yet — is left in the draft, where the selection check reports it as the
        /// missing armature it is
        /// (<c>APA043 reason=missing-target-armature</c> / <c>reason=missing-part-armature</c>). Replacing it with
        /// a guess or erasing the path would both hide a real defect. In replacement mode the stale live reference
        /// is cleared, while the unresolved path remains available for validation and repair.
        /// </para>
        /// </remarks>
        /// <param name="replaceExisting">
        /// Replace the current live references from the supplied paths. Used when a different profile replaces the
        /// draft; false keeps valid live choices and only fills missing references.
        /// </param>
        /// <returns>
        /// A short description of what was restored — for the window's status line — or an empty string when
        /// nothing was.
        /// </returns>
        public string RestoreArmatures(
            string targetArmaturePath,
            string partArmaturePath,
            bool replaceExisting = false)
        {
            var restored = new List<string>();

            if (replaceExisting || _targetArmature == null)
            {
                var resolved = ResolveArmaturePath(AvatarRootTransform, targetArmaturePath);
                if (replaceExisting) _targetArmature = resolved;
                if (resolved != null)
                {
                    _targetArmature = resolved;
                    restored.Add("target='" + TargetArmaturePath + "'");
                }
            }

            if (replaceExisting || _partArmature == null)
            {
                var resolved = ResolveArmaturePath(PartRootTransform, partArmaturePath);
                if (replaceExisting) _partArmature = resolved;
                if (resolved != null)
                {
                    _partArmature = resolved;
                    restored.Add("part='" + PartArmaturePath + "'");
                }
            }

            return restored.Count == 0 ? string.Empty : string.Join("; ", restored.ToArray());
        }

        /// <summary>
        /// Resolves a recorded armature path under the root it is relative to.
        /// </summary>
        /// <param name="root">The avatar root or the part root, or null when it is not selected.</param>
        /// <param name="path">
        /// The recorded path. <see cref="ApaAvatarPath.Root"/> (<c>"."</c>) is the root itself; the empty string
        /// is "no selection".
        /// </param>
        /// <remarks>
        /// The same two rules <see cref="ApaArmatureScope.TryResolve"/> applies, without its diagnostic: the token
        /// <c>"."</c> resolves to the root, and anything else must resolve <i>inside</i> the root. A path that
        /// resolves elsewhere in the scene is refused rather than used, because an armature outside its own root
        /// has no identity this pipeline could record. Returning null here is not a verdict — the caller keeps the
        /// path and the selection check reports the condition with its own code and <c>reason=</c> token.
        /// </remarks>
        /// <returns>The resolved transform, or null when the path does not resolve inside the root.</returns>
        public static Transform ResolveArmaturePath(Transform root, string path)
        {
            if (root == null || !ApaAvatarPath.HasIdentity(path)) return null;

            var found = ApaAvatarPath.IsRoot(path) ? root : root.Find(path);
            if (found == null) return null;

            return found == root || found.IsChildOf(root) ? found : null;
        }

        /// <summary>
        /// Proposes the two armature selections from the live hierarchy, filling only the empty ones.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is a suggestion, not a decision.</b> It is driven by an explicit button, it never overwrites a
        /// selection the author made, and what it proposes is immediately visible in the two object fields and can
        /// be changed before anything is saved. The build never runs it: by the time a profile is validated or
        /// assembled, the selection is serialized author data
        /// (<see cref="ApaBoneProfile.TargetArmaturePath"/> and <see cref="ApaBoneProfile.PartArmaturePath"/>).
        /// </para>
        /// <para>The proposal rule itself is <see cref="ProposeArmatures"/>, shared with the shortcut that creates
        /// a profile from the installer inspector, so the two surfaces cannot propose different armatures for the
        /// same hierarchy.</para>
        /// </remarks>
        /// <returns>A short description of what was proposed, for the window's status line.</returns>
        public string SuggestArmatures()
        {
            var proposed = new List<string>();

            ProposeArmatures(
                _avatarRoot != null ? _avatarRoot.transform : null,
                _targetRenderer,
                _partRoot,
                out var targetArmature,
                out var partArmature);

            if (_targetArmature == null && targetArmature != null)
            {
                _targetArmature = targetArmature;
                proposed.Add("target='" + TargetArmaturePath + "'");
            }

            if (_partArmature == null && partArmature != null)
            {
                _partArmature = partArmature;
                proposed.Add("part='" + PartArmaturePath + "'");
            }

            return proposed.Count == 0
                ? Localization.ApaLocalization.Tr("nothing to propose: both armatures are already selected")
                : string.Join("; ", proposed.ToArray());
        }

        /// <summary>
        /// The two armature levels a merge is defined over, read from the live hierarchy.
        /// </summary>
        /// <param name="avatarRoot">The avatar root, or null.</param>
        /// <param name="targetRenderer">The target body renderer, or null.</param>
        /// <param name="partRoot">The part root, or null.</param>
        /// <param name="targetArmature">Receives the proposed target armature, or null.</param>
        /// <param name="partArmature">Receives the proposed part armature, or null.</param>
        /// <remarks>
        /// <para>
        /// The target side is the object that owns the avatar's skeleton
        /// (<see cref="MergeArmatureGenerator.ResolveAvatarArmatureRoot"/>: the humanoid hips' parent for a
        /// humanoid avatar); the part side is the object whose children are the part's highest bone
        /// (<see cref="MergeArmatureGenerator.TryFindPartSkeleton"/>). Both are the bone level a Modular Avatar
        /// merge is defined over, which is exactly what the armature-relative identity needs.
        /// </para>
        /// <para>
        /// <b>It is a proposal, never a substitute for a selection.</b> The result is written into a visible object
        /// field or into a draft the author is about to review; nothing in the build path calls it, and a proposal
        /// that fails to resolve simply yields null, which leaves the selection empty and the build blocking with
        /// <c>APA043</c> as it would for any other missing selection.
        /// </para>
        /// </remarks>
        public static void ProposeArmatures(
            Transform avatarRoot,
            Renderer targetRenderer,
            GameObject partRoot,
            out Transform targetArmature,
            out Transform partArmature)
        {
            targetArmature = null;
            partArmature = null;

            if (avatarRoot != null && ApaArmatureScope.RequiresScope(targetRenderer))
            {
                var candidate = MergeArmatureGenerator.ResolveAvatarArmatureRoot(avatarRoot.gameObject, null);
                if (candidate != null) targetArmature = candidate.transform;
            }

            if (partRoot != null && ApaArmatureScope.RequiresScope(partRoot.GetComponentInChildren<Renderer>(true)))
            {
                if (MergeArmatureGenerator.TryFindPartSkeleton(partRoot, out _, out var mergeRoot)
                    && mergeRoot != null)
                {
                    partArmature = mergeRoot.transform;
                }
            }
        }

        /// <summary>
        /// The avatar root that contains a transform: the nearest ancestor carrying an <see cref="Animator"/>,
        /// falling back to the scene root object.
        /// </summary>
        /// <remarks>
        /// A VRChat avatar descriptor requires an Animator on the descriptor object, so the nearest Animator
        /// ancestor is the best dependency-free identity for "the avatar" that does not require this package to
        /// reference the VRChat SDK. The fallback keeps an unrigged test hierarchy usable. The chosen root is
        /// always visible in the window and can be overridden, so the heuristic never becomes a hidden decision.
        /// </remarks>
        public static Transform FindAvatarRoot(Transform start)
        {
            if (start == null) return null;

            var current = start;
            while (current != null)
            {
                if (current.GetComponent<Animator>() != null) return current;
                current = current.parent;
            }

            return start.root;
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
    }
}

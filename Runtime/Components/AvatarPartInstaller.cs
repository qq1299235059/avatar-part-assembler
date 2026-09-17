using UnityEngine;

namespace AvatarPartAssembler
{
    /// <summary>
    /// The component a user adds to an avatar to install a modular part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The intended end-user experience is: download a part prefab, drag it under the avatar, and the part
    /// installs itself. Deleting the prefab must restore the original avatar exactly. That guarantee is
    /// achieved by the fact that this component never modifies any authoring asset — all work happens on the
    /// transient clone that NDMF creates for preview and for build.
    /// </para>
    /// <para>
    /// This component deliberately holds only data. All validation, planning, and mesh generation live in the
    /// Editor assembly so that the Runtime assembly stays free of UnityEditor dependencies.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Part Assembler/Avatar Part Installer")]
    public sealed class AvatarPartInstaller : MonoBehaviour
    {
        [SerializeField] private ApaPartProfile _profile;
        [SerializeField] private GameObject _partRoot;
        [SerializeField] private GameObject _targetRendererObject;
        [SerializeField] private bool _enabledForBuild = true;

        /// <summary>
        /// The authoring profile describing this part. May be assigned directly, or left null when the profile
        /// is discovered by convention next to this component.
        /// </summary>
        public ApaPartProfile Profile
        {
            get => _profile;
            set => _profile = value;
        }

        /// <summary>
        /// Root of the part geometry. When null, the part root is the GameObject this component lives on.
        /// </summary>
        public GameObject PartRoot
        {
            get => _partRoot;
            set => _partRoot = value;
        }

        /// <summary>
        /// The body renderer whose mesh is replaced. When null, the target renderer is resolved from the
        /// profile's compatibility signature against the avatar hierarchy.
        /// </summary>
        public GameObject TargetRendererObject
        {
            get => _targetRendererObject;
            set => _targetRendererObject = value;
        }

        /// <summary>
        /// When false, this installer is skipped entirely and contributes nothing to the plan. Used by authors
        /// to park a part without deleting it.
        /// </summary>
        /// <remarks>
        /// This is one of three conditions in <see cref="IsActiveForBuild"/>. It is deliberately not the only
        /// switch: an author who disables the component or deactivates the GameObject also expects the part to
        /// stop being installed, and a build that deleted body triangles anyway would be a destructive surprise.
        /// </remarks>
        public bool EnabledForBuild
        {
            get => _enabledForBuild;
            set => _enabledForBuild = value;
        }

        /// <summary>
        /// The single activity predicate every consumer must use: the component is enabled, its GameObject is
        /// active in the hierarchy, and <see cref="EnabledForBuild"/> is true.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Preview and build must agree on which installers are active, so this predicate exists once, here, and
        /// the pipeline calls it rather than re-deriving the rule. A part that fails it is not installed, but it
        /// is still reported (<c>APA039</c>, informational) so that a parked part cannot look like an installed
        /// one.
        /// </para>
        /// <para>
        /// <c>isActiveAndEnabled</c> is Unity's own "enabled and active in hierarchy" property, which keeps this
        /// predicate correct for a part parked anywhere in a disabled subtree rather than only on the object the
        /// component sits on.
        /// </para>
        /// </remarks>
        public bool IsActiveForBuild => isActiveAndEnabled && _enabledForBuild;

        /// <summary>
        /// A stable description of why <see cref="IsActiveForBuild"/> is false, for the skip diagnostic.
        /// Returns an empty string when the installer is active.
        /// </summary>
        public string DescribeInactiveReason()
        {
            if (IsActiveForBuild) return string.Empty;

            // Ordered so that the author's own switch is reported first: it is the condition they most likely
            // changed on purpose, and the one the remedy is written for.
            if (!_enabledForBuild) return "enabledForBuild=false";
            if (!gameObject.activeInHierarchy) return "gameObjectInactive=true";
            if (!enabled) return "componentDisabled=true";
            return "reason=unknown-inactive-state";
        }

        /// <summary>
        /// The effective part root, falling back to this component's own GameObject.
        /// </summary>
        public GameObject ResolvePartRoot()
        {
            return _partRoot != null ? _partRoot : gameObject;
        }

        /// <summary>
        /// The stable identity of this part, or an empty string when no profile is assigned. Callers must treat
        /// the empty string as "unidentifiable", which is a validation error rather than a license to guess.
        /// </summary>
        /// <remarks>
        /// Reads the serialized identity without materializing it, so sorting and path resolution cannot mutate
        /// a profile asset that the authoring scene also holds.
        /// </remarks>
        public string ResolvePartId()
        {
            var identity = _profile != null ? _profile.IdentityOrNull : null;
            return identity != null ? identity.PartId ?? string.Empty : string.Empty;
        }

        /// <summary>
        /// The declared body slot of this part, or <see cref="ApaPartSlot.Custom"/> when no profile is assigned.
        /// </summary>
        public ApaPartSlot ResolveSlot()
        {
            var identity = _profile != null ? _profile.IdentityOrNull : null;
            return identity != null ? identity.Slot : ApaPartSlot.Custom;
        }

        /// <summary>
        /// The declared slot mode of this part, or <see cref="ApaPartSlotMode.Replace"/> when no profile is
        /// assigned. Replace is the strict default: it claims exclusive ownership of the slot.
        /// </summary>
        public ApaPartSlotMode ResolveSlotMode()
        {
            var identity = _profile != null ? _profile.IdentityOrNull : null;
            return identity != null ? identity.SlotMode : ApaPartSlotMode.Replace;
        }

        /// <summary>
        /// The declared conflict priority of this part, or zero when no profile is assigned. Zero is inert: it
        /// neither resolves a conflict nor changes ordering.
        /// </summary>
        public int ResolveConflictPriority()
        {
            var identity = _profile != null ? _profile.IdentityOrNull : null;
            return identity != null ? identity.ConflictPriority : 0;
        }

        private void Reset()
        {
            _partRoot = gameObject;
        }

        private void OnValidate()
        {
            if (_partRoot == null) _partRoot = gameObject;
        }
    }
}

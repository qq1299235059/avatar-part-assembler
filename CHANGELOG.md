# Changelog

## [0.4.0] — vertex-color seam candidates, explicit overlays, mask-only removal

This release publishes [0.3.0-rc.10], [0.3.0-rc.11], and [0.3.0-rc.12] together.

- **Seam candidates are a vertex color** (`APA052 SEAM_CANDIDATE_COLOR_INVALID`). All four channels must match the
  selected color exactly. The named `merge vertex` representation is retired: `ApaMergeVertexGroup` and
  `ApaMergeVertexGroupResolver` are removed together with their tests, and `APA051 MERGE_VERTEX_GROUP_INVALID` keeps
  its registered slot but is never emitted. The stored explicit seam pairs and the build-time seam decisions are
  unchanged.
- **Scene View overlays are explicit.** The red removal overlay is off by default, a green candidate overlay marks
  what the selected color selects, and a merge-check mode previews a pairing without writing it. Overlays now run
  through a registered callback with an explicit visibility plan and invalidate derived state when the selection,
  draft, or Profile changes.
- **Automatic seam generation filters only the part mesh by vertex color** and finds the target body spatially, so
  target meshes no longer need vertex colors. Part-side missing, malformed, or unmatched colors still block with
  `APA052` instead of falling back to every vertex.
- **Removal Region is authored only through the texture mask.** Scene View triangle picking, numeric address entry,
  submesh bulk-delete controls, and their manual-edit backend are removed; the stored removal set and mask sampler
  are unchanged.
- The candidate color is entered as strict `#RRGGBB` (optionally `#RRGGBBAA`) text, defaulting to opaque black
  `#000000`. Profile loading runs in one isolated transition for both entry points.
- The GitHub documentation was rewritten around the current workflow, and
  `Documentation~/PART_AUTHORING_GUIDE.zh-CN.md` now gives creators the complete asset checklist, step-by-step
  authoring process, hard constraints, animation checks, and release checklist.

## [0.3.0-rc.12] — mask-only removal authoring and reliable preview overlays

- Removal Region is now authored only through the texture mask. Scene View triangle picking, numeric address
  entry, submesh bulk-delete controls, and their manual-edit backend have been removed; the stored removal set and
  mask sampler remain unchanged.
- Scene View overlays now use a registered callback and an explicit visibility plan. Removal, seam candidate, and
  merge-check overlays invalidate derived state when the selection, draft, or Profile changes and reopen the master
  Highlights gate when enabled.
- The seam candidate color wheel was replaced by a strict `#RRGGBB` text field (with optional `#RRGGBBAA`). Invalid
  codes keep the previous valid color and do not touch mesh caches; the default remains opaque black `#000000`.
- Profile loading now uses one isolated transition for both entry points, replacing the draft and clearing all
  derived caches before displaying the loaded asset.

## [0.3.0-rc.11] — spatial target seams and authoring workflow polish

- Automatic seam generation now filters only the **part** mesh by the selected vertex color. The target body is
  searched spatially across all readable vertices, so target meshes do not need vertex colors. Part-side missing,
  malformed, or unmatched colors still block with `APA052` and never fall back to all part vertices.
- The default candidate color is opaque black `#000000`. Candidate resolution and merge-check work are deferred
  while the Unity color picker is being dragged, avoiding a full `Mesh.colors32` read and spatial match per wheel
  repaint.
- Target and part armature object fields are restored from the profile's recorded relative paths when the window is
  reopened or a profile is loaded.
- `Load Existing Profile` is now at the top of the Part Authoring window. `Output` is below Bones and Blend Shapes
  and above Actions.

## [0.3.0-rc.10] — vertex-color seam candidates and controllable authoring overlays

**Automatic seam generation now reads mesh vertex colors instead of a named `merge vertex` group, and the
authoring Scene View overlays are explicit: the red removal overlay is off by default, a green candidate
overlay marks what the selected color selects, and a merge-check mode previews the pairing without writing it.
The assembled mesh, the stored seam pairs, and every build-time decision are unchanged.**

### Changed

- **Seam candidates are a vertex color (`APA052 SEAM_CANDIDATE_COLOR_INVALID`).** The Part Authoring window
  exposes a `Candidate Color`; a vertex may pair only when all four channels of its `Mesh.colors32` entry equal
  that color **exactly** — no tolerance, alpha included. Both the target body mesh and the part mesh are read in
  their rest pose. A mesh that carries no vertex color, a color array whose length disagrees with the vertex
  count, and a color no vertex carries all **block** with a stable `reason=` token
  (`vertex-color-missing`, `vertex-color-count-mismatch`, `no-vertex-color-candidates`); there is no fallback to
  every vertex. The stored explicit seam pairs and the public `ApaSeamWorldMatcher` overloads are unchanged.
- **The named `merge vertex` representation is gone.** `ApaMergeVertexGroup` (the runtime component) and
  `ApaMergeVertexGroupResolver` (the component/named-bone resolver) were removed, along with their tests. Unity
  import pipelines routinely drop non-bone vertex groups while preserving mesh vertex colors, so a runtime
  component or a bone name was never a contract the workflow could rely on. `APA051 MERGE_VERTEX_GROUP_INVALID`
  is **retired**: the constant keeps its registered meaning and title so the code is never reused, and this build
  never emits it. A scene that still carries the component shows Unity's missing-script notice; nothing else has
  to be migrated, because the seam itself was always stored as explicit pairs.
- **The red removal overlay is off by default.** A new `Removal Overlay` toggle in the Removal Region block
  governs the draw, hover, and click paths, and arming `Pick Triangles In Scene` turns it on so a pick is never
  blind. Its blend-shape-safe evaluated geometry is unchanged: when enabled, the triangles and picks still follow
  a cached `BakeMesh` result while the removal set, the seam, and the build stay rest-pose data.
- **The green candidate overlay** marks the vertices the selected color selects on both renderers, drawn from the
  same rest-pose indices and positions seam generation reads, through each renderer's own transform, bounded by
  the existing draw budget. A side whose candidates cannot be resolved draws nothing and reports the blocking
  diagnostic in the window and in the Scene View label.
- **The merge-check mode** is a separate toggle that hides the removal, candidate, and stored-seam overlays and
  draws the prospective pairing of the selected candidates instead: matched target and part candidates in green,
  candidates with no counterpart within the seam tolerance in red. It runs the same matcher with the same
  candidate lists and the same tolerance, is cached per mesh, color, transform, candidate set, and tolerance, and
  **writes nothing** — the stored seam and the profile are untouched. The mode *hides* the other overlays rather
  than clearing their toggles, so switching it off restores exactly what was set before it.

### Documentation and tests

- Added focused editor tests for the Color32 comparison (including the alpha channel), missing and malformed
  color data, candidate filtering, the merge-check matched/unmatched classification, the matcher-parity and
  determinism of that classification, the "no write" property, and the overlay toggle precedence; the
  evaluated-geometry overlay test now pins which half of the tool reads which pose.
- README, README.zh-CN, `Documentation~/OVERVIEW.md`, the acceptance checklist, and the Simplified Chinese table
  document the vertex-color contract, the selected color, the overlay controls, the merge-check mode, and the
  `APA051` retirement.

## [0.3.0-rc.9] — named merge-vertex seam candidates and blend-shape-safe authoring preview

### Added

- Automatic world-position seam generation now accepts candidates only from the exact `merge vertex` group on
  both renderers. Unity imports that preserve the group as a skinned bone can use a bone named exactly `merge
  vertex` with positive per-vertex weights; non-skinned or explicit imported index data can use the
  `ApaMergeVertexGroup` component on the renderer's GameObject. Missing, empty, ambiguous, stale, duplicated or
  out-of-range data blocks generation with `APA051 MERGE_VERTEX_GROUP_INVALID` instead of falling back to every
  vertex.
- The authoring Scene View removal overlay, hover picking and click picking now follow the target renderer's
  evaluated blend-shape geometry through a cached `BakeMesh` result. Triangle addresses and all build/seam data
  remain rest-pose data, so the preview moves visually without changing what will be assembled.

### Documentation and tests

- Added focused resolver and preview-cache editor tests, the `merge vertex` authoring contract, and the explicit
  note that Unity has no generic named vertex-group API.

All notable changes to Avatar Part Assembler are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
semantic versioning.

Milestones are recorded newest first. Each entry says what the milestone added and what
it did **not** do, because a milestone boundary the reader cannot see is a defect.

## [0.3.0-rc.8] — standalone prefabs from prefab instances

**Creating a part prefab from a scene Prefab Instance now produces an independent regular Prefab, preserving the
instance's current scene state without changing the original instance.**

### Fixed

- `ApaPrefabGenerator` copies a Prefab Instance to a temporary parentless clone, unpacks only its outermost
  connection with `PrefabUnpackMode.OutermostRoot`, and saves that clone. The original scene object is never
  unpacked or reparented, nested prefab instances remain nested, and the temporary clone is destroyed on every
  success and failure path.
- The authoring note now explicitly says the generated asset is an independent prefab rather than a variant of the
  source prefab.

### Notes

- Static contract coverage pins the clone/unpack/save ordering and cleanup. A live Unity Editor integration run is
  still recommended to verify `PrefabUtility.GetPrefabAssetType` on a real scene instance.

## [0.3.0-rc.7] — installer health line, persisted bone-fit defaults, preview culling fix

**The installer Inspector now answers one question at a glance — is this part ready? — and the preview no
longer disappears at some camera angles. Built and uploaded renderers are untouched.**

### Changed

- **The installer Inspector leads with one concise health line.** Green and "this part is ready" when the full
  validation reports no errors; red and "this part has a problem" when it reports errors or the build context
  cannot be built. The detailed `APA` diagnostics stay below the line so a failure can still be understood. The
  verdict is computed from the same context and core a build uses, cached against the inputs it was computed
  from, and refreshed on a relevant serialized change or the Validate action — never on every repaint.
- **The developer detail block is gone from the installer Inspector.** Part id (and its repair button), armature
  names, schema and signature, recorded target path, removal and seam counts, UV and material semantics, and the
  resolved target are authoring-tooling detail; they are no longer drawn for end users. Nothing about the
  profile, the build, or the authoring window changed.
- **`Follow Avatar Bones` and `Include Scale` are now preferences on `AvatarPartInstaller`, both defaulting to
  enabled for a newly added installer.** The Inspector binds its toggles to the serialized fields and starts or
  synchronizes `ApaBoneFollowRuntime` from them, so the choice survives closing the Inspector and a domain
  reload, and turning either option off stops the session. Runtime/editor assembly boundaries are unchanged.

### Fixed

- **The preview mesh no longer vanishes at some view angles** (`Editor/Preview/PreviewProxyApplier.cs`). For the
  preview proxy `SkinnedMeshRenderer`, the copied source renderer's animation-safe `localBounds` is preserved and
  unioned with the generated mesh bounds instead of being replaced by the tight generated rest-pose bounds, and
  `updateWhenOffscreen` is set to `true` on the proxy only. Correctness beats the preview-only cost. No build
  mesh bounds are inflated and no real avatar renderer is modified.

### Notes

- Simplified Chinese entries were added for the new concise status text and the two bone-fit tooltips; focused
  contract tests cover the removed detail block, the cached verdict, the persisted defaults, the preview bounds
  union, the localization, and the version move. Not run: Unity's in-editor domain reload, the EditMode Test
  Runner, and an NDMF preview session — the static compile and source-contract checks only.

## [0.3.0-rc.6] — stable-ID-only part identity

- Removed the editable part display name, slot, slot mode, and conflict-priority identity module. New authoring keeps only the stable part ID.
- Part ordering now uses stable part ID plus installer path. Legacy identity fields remain readable for old profiles but are ignored by validation and build decisions.

## [Unreleased] — Merge Animator retargeting and empty source-object cleanup

**A part's recorded animation now follows the geometry the assembler moved, and an object the assembly emptied is
removed from the build clone. Nothing in Modular Avatar or NDMF was changed.**

### Added

- **Blend-shape animation recorded on a part follows the assembled geometry**
  (`Editor/NDMF/ApaAnimatorRetargetPass.cs`). A part prefab may carry a Modular Avatar *Merge Animator* — typically
  in Relative path mode on the part root — whose controller animates the part renderer, most often its blend
  shapes. Modular Avatar virtualizes that controller and prefixes every recorded path with the part root's
  avatar-relative path, so the clip addresses the part renderer by the path it had when the animator services
  context was opened. APA assembles that renderer's geometry into the target body renderer and consumes the part
  renderer, which used to leave the animation addressing an object the assembly had replaced: the blend shape
  stopped responding. APA now registers the consumed part renderer object → its group's target renderer object
  with NDMF's `AnimatorServicesContext.ObjectPathRemapper.ReplaceObject`, and NDMF commits the mapping into the
  generated clips when the context deactivates — the same mechanism Modular Avatar itself uses when it merges a
  bone away. No animation clip is edited directly, no serialized controller is touched, and no Modular Avatar file
  is changed.

- **The retarget step is ordered so the mapping is actually committed**
  (`Editor/NDMF/ApaNdmfPlugin.cs`). Modular Avatar opens the animator services context, processes the Merge
  Animator and Merge Armature components, and closes it again inside its own Transforming sequence — its later
  passes do not require the context — so by the time APA's Transforming sequence runs (it is ordered after
  Modular Avatar's plugin end) the context is already closed. The retarget step therefore declares the context as
  a **required** extension: NDMF opens it before the pass executes, the pass registers the pairs against a fresh
  snapshot of the current hierarchy, and that activation's own deactivation commits them. Reopening the context is
  a supported NDMF flow; Modular Avatar itself opens it once in Resolving and again in Transforming, and the
  committed controllers are reused rather than re-created.

- **Empty source objects are removed from the build clone** (`Editor/NDMF/ApaEmptySourceCleanupPass.cs`,
  `Editor/NDMF/ApaSourceObjectCleanup.cs`). The assembly consumes a part renderer by destroying its component
  only, deliberately leaving the object and everything under it alone, because the generated mesh may be skinned
  to bones that live there. The renderer's own object is often left carrying nothing but a Transform. The cleanup
  step removes exactly those objects and nothing else: the predicate requires the object to be alive, not the
  avatar root, parented, childless, and to carry no component other than its Transform. A child, a constraint, a
  PhysBone, an authoring component, or a missing script reference keeps the object. The step runs after Modular
  Avatar's late transform stages (`nadena.dev.modular-avatar.late-transform-stages`), which purge the remaining
  Modular Avatar components — otherwise a leftover `ModularAvatarMergeAnimator` would make an otherwise empty part
  object survive — and after the retarget step, which needs the source objects alive to read their recorded paths.

- **The consumed renderer objects are captured before they are destroyed**
  (`Editor/NDMF/ApaBuildProcessor.cs`, `Editor/NDMF/ApaTransientArtifacts.cs`, `Editor/NDMF/ApaAssemblyPass.cs`).
  A destroyed component cannot be asked for its GameObject, so the assembly pass records the source → target
  object pairs — every consumed renderer of a group against that group's own target renderer, with null and
  identical pairs skipped and a repeated source recorded once — in the per-build NDMF state before consumption.
  Both later steps read that ledger instead of re-deriving it from a hierarchy the run has already rewritten.

### Notes

- **What the cleanup deliberately does not remove.** A part renderer that is a `MeshRenderer` leaves its
  `MeshFilter` behind, because the assembly consumes the renderer component and nothing else; such an object is
  not empty and is kept. This is the pre-existing consumption contract ("only the component is destroyed"), not a
  new exception: the cleanup removes the objects the assembly can actually leave empty, and never guesses.
- **The retarget mapping is a path mapping.** NDMF matches on the recorded path string. It moves a recorded
  animation path onto the target renderer when the part renderer's recorded path is the path the clip addresses —
  which is exactly the Relative-mode Merge Animator shape — and does nothing when it is not. No clip is rewritten
  by this package, so a path this mapping does not match is left exactly as Modular Avatar produced it.
- **No Modular Avatar or NDMF file was modified**, and no new assembly reference, `InternalsVisibleTo`, or global
  state was introduced. Both new steps read the per-build NDMF state, so nothing leaks between avatars or builds.
- **Not run.** The Roslyn compile check and the focused source contract check were run; Unity's in-editor domain
  reload, the EditMode Test Runner, and a real NDMF/Modular Avatar build with a Merge Animator part were **not**
  executed by the harness. `AnimatorRetargetContractTests` is compiled by the check and remains to be run in
  Unity.

## [0.3.0-rc.5] — stable authored skinning bind poses

### Fixed

- A live bone pose edited in the Scene view is no longer captured as a fresh bind pose during a preview rebuild
  or Play Mode prebuild. Source mesh bind poses are converted into the target renderer's local basis; snapshots
  without source bind data retain the legacy transform fallback.
- The NDMF Play Mode path now validates each part mesh fingerprint in Generating, before Modular Avatar can
  rewrite temporary part skin weights and bind poses. APA no longer compares that intentionally rewritten mesh
  against the authoring fingerprint after the merge; `APA048` at the pre-merge checkpoint still blocks a genuine
  source-mesh change and asks the author to recapture the profile.

## [0.3.0-rc.4] — profile content identity and seam authoring guards

### Added

- Schema 5 stores deterministic FNV-1a content fingerprints for the target and part meshes. A profile now
  blocks on `APA047 PROFILE_MESH_FINGERPRINT_MISSING` or `APA048 PROFILE_MESH_FINGERPRINT_MISMATCH` when a
  reimport changes attributes that a topology summary cannot see (UVs, skin weights, bind poses, or blend-shape
  deltas). New authoring saves capture the part fingerprint automatically; an existing non-empty fingerprint is
  never overwritten silently.
- Seam validation reports `APA049 SEAM_WEIGHT_BONE_NOT_IN_TARGET` when a seam vertex carries a live influence for a
  bone absent from the target avatar's bone scope. This makes the authoring contract explicit: seam vertices may
  not depend on part-only or unbound bones.
- World-position seam generation accepts explicit candidate vertex indices for integrations that import a named seam
  vertex group, validates candidate lists deterministically, and refuses tolerances above `1e-3` world units. The
  existing all-vertex overload remains source-compatible.
- Preview bone observation now treats local position/rotation/scale as live pose state. Moving or scaling a spine
  therefore deforms the generated proxy through its live bone references instead of rebuilding bind poses from the
  edited pose and snapping the mesh back to rest. Bone parent/name changes remain rebuild inputs.
- Final skinning now prefers each source mesh's authored bind poses, converted into the target renderer's local
  basis. A live editor pose is no longer captured as a fresh bind pose during preview rebuilds or Play Mode
  prebuilds; legacy snapshots without source bind data keep the previous transform-based fallback.
- Bone hierarchy observations use a separate structural registry, so a bone that is also a renderer or part-root
  transform cannot accidentally re-enable pose-driven preview rebuilds through a generic transform observation.
- Part mesh fingerprints are now checked during NDMF Generating, before Modular Avatar's armature merge can
  rewrite temporary skin weights/bind poses. The later APA Transforming pass skips only that duplicate part check;
  preview and direct core builds still validate their live source mesh normally.

### Documentation

- The author guide now recommends a dedicated seam candidate group in the source DCC, while treating the imported
  candidate index list—not an arbitrary Unity-side group name—as the stable contract. It also documents the
  target-bone-only seam weight rule and the fingerprint recapture workflow.

## [Unreleased] — review findings

**Four defects found in a line-by-line review: one unsafe comparison, one unreachable branch, one unaudited
skip, and one untested module.**

### Fixed

- **`SeamWeldPlanner` no longer welds a pair whose UVs cannot be compared.** The weld/split test was
  `!(difference > epsilon)`, and a distance of `float.NaN` is not greater than anything — so a corrupt (NaN) UV
  read as *agreement* and the pair welded, deleting the part's seam vertex and its UV on the strength of a
  number that does not exist. A non-finite coordinate has no distance to the other side, so the pair is now
  preserved instead: the same direction every other disagreement takes, because keeping both sides cannot
  destroy data. Two identically infinite UVs are now preserved too, since `infinity - infinity` is NaN and used
  to fail the same way. The condition is visible in the diagnostic as `nonFiniteUv=true` and on
  `SeamUvPreservation.HasNonFiniteUv`, so the author is told "this data is corrupt" rather than "your UVs differ
  by NaN".

- **`AddGroup` no longer returns a bool that is always `true`.** The preview filter's group builder ended with
  `groups.Add(...); return true;` and its call site carried a failure branch — release the renderer claim,
  report an internal failure — that no input could reach. The method is now `void` and the branch is gone. A
  branch that cannot run reads like a state the preview handles, so nobody notices the unwind inside it is
  untested; the conditions that could have justified it are already handled where they happen.

- **`CompatibilityRule`'s one-pass skip is now fail-safe.** The skip was a bare `return` guarded only by
  `ValidationContext.CompatibilityVerified`, which is a *claim* rather than a proof: a call site that set the
  flag without doing the work would have silently disabled the compatibility gate. The rule now honours the
  claim only while it is coherent — verified **and** carrying no representative signature, which is exactly what
  the per-installer pass produces. A contradictory context gets the check instead of a pass, so the worst
  outcome of a future refactor is a duplicate report rather than a gate that no longer exists.

### Added

- **Tests for `ApaRemovalMaskSampler`, which had none** (`Tests/Editor/RemovalMaskSamplerTests.cs`). Covers the
  fixed rule — seven sample points, a strict majority of four, Rec.601 luminance with alpha excluded, an
  inclusive threshold boundary, invert — the texel addressing through Repeat / Mirror / Clamp and through point
  versus bilinear filtering, the refusal of a non-finite UV, and every input refusal that does not need a
  graphics device. The readback-dependent assertions skip with a stated reason on a runner without a device;
  the rule itself is asserted with hand-built pixel arrays and needs none.

- **Contract tests for the compatibility one-pass skip**, pinning both halves: a verified context is not
  compared a second time, and a verified context that still carries a signature is.

- **Two seam tests for non-finite UVs**: a NaN UV and two identical infinite UVs are preserved rather than
  welded, and the preservation record carries `HasNonFiniteUv`.

### Notes

- The compatibility check itself was never skipped on the build path. `ContextBuilder` runs the rule once per
  installer against that installer's own captured signature and only then marks the group's context verified,
  which is strictly more than one representative signature could prove. What was missing was a test saying so,
  and a guard against the flag being set without the work.

## [Unreleased] — menu consolidation

**One authoring menu entry, and the `Tools` submenu follows the interface language.**

### Changed

- **The permanent Simplified Chinese authoring alias was merged into the English entry.** The window used to
  register two live menu items — `Tools/Avatar Part Assembler/Part Authoring` and
  `Tools/部件装配器/部件编辑` — which made the `Tools` menu list the same window twice. There is now exactly one
  authoring item; its submenu label is `部件装配器` and its item label is `部件编辑` when the
  `APA_CHINESE_MENU` symbol is defined (which `Editor/dev.avatar-part-assembler.editor.asmdef` does by default),
  and the English spelling otherwise. Both spellings call the same `Open()`.

- The two spellings are swapped by the `APA_CHINESE_MENU` preprocessor symbol rather than by a runtime branch.
  A `[MenuItem]` argument must be a compile-time constant and the language preference lives in `EditorPrefs`,
  which is not readable while the attribute is being constructed. Remove the symbol from the assembly's
  `defines` list to compile the English menu; Unity recompiles and the menu follows.

- The `Tools` root is still never localized. Only APA's own submenu and item labels change, so installing APA
  cannot hide the editor's existing `Tools` entries.

### Notes

- `Tools/部件装配器/Play Mode + Gesture Manager 兼容` and its English counterpart are both registered on purpose.
  They occupy two different submenus, so nothing is duplicated and a user keeps the item where they learned it;
  the checkmark is mirrored across both so the state cannot disagree.

## [0.3.0-rc.3] — runtime build hardening and Play Mode / Gesture Manager compatibility

**Release candidate; the real NDMF runtime path has now been exercised in Unity.** The remaining acceptance
checklist is still not complete, but this candidate is no longer source-review-only.

### Added

- **Play Mode / Gesture Manager compatibility** (`Editor/NDMF/ApaPlayModeCompatibility.cs`). When a loaded
  scene contains an `AvatarPartInstaller`, APA temporarily enables NDMF's own **Apply On Play** switch before
  entering Play Mode.
- **Pre-Awake Play Mode scene prebuild.** APA now implements `IProcessSceneWithReport` and runs NDMF directly on
  Unity's temporary Play Mode scene copy at `int.MinValue + 50`, before scene components receive `Awake` / `Start`
  and before VRCFury's Play Mode scene processor (`int.MinValue + 100`). The early prebuild intentionally avoids
  the full VRChat preprocess callback chain, so VRCFury's single-run Play Mode guard cannot block APA. Gesture
  Manager therefore receives an already-assembled avatar instead of posing the armature first.
- The user's original NDMF setting is preserved through `SessionState` and restored automatically on return to
  Edit Mode. The compatibility layer is enabled by default and can be toggled from
  `Tools > Avatar Part Assembler > Play Mode + Gesture Manager Compatibility`.

### Fixed

- **Removed the late `EnteredPlayMode` rebuild / `Animator.Rebind()` fallback.** Rebuilding an APA skinned mesh
  after Gesture Manager had already placed the skeleton into its standing pose could mix T-pose vertex data with
  bind data captured from a posed armature. The next Animator update then produced severe arm/mesh deformation.
  APA now completes Play Mode construction before any emulator can pose the avatar.

- **Unity top-level Tools menu collision on localized editors.** The Simplified Chinese authoring alias now uses
  `Tools/部件装配器/部件编辑` instead of registering a separate top-level `工具/...` path. Only APA's submenu labels
  are localized; Unity's canonical `Tools` root is left untouched so installing APA cannot hide the editor's
  existing Tools-menu entries.
- **Post-Modular-Avatar armature scope.** The Generating pass still requires the serialized part armature to be
  under the part root, but after Modular Avatar has verifiably applied the transient merge-armature plan the
  Transforming pass may use the selected target armature as the live weighted-bone identity scope. This fixes the
  real build failure where the original `PartArmaturePath` no longer existed below the part root after MA had
  correctly retargeted the part's bones.
- The post-merge fallback is strictly gated to the formal NDMF Transforming build after `CollectUnapplied` proves
  the merge occurred. Preview, authoring validation, and ordinary planning remain strict and therefore still catch
  invalid armature authoring instead of hiding it.
- The temporary runtime probe source was corrected after an extra closing brace produced `CS1022`.

### Verified

- Compiled the Editor, NDMF, Preview, and Editor Test assemblies against Unity 2022.3.22f1's actual Bee/Roslyn
  response files with zero C# compiler errors.
- Ran a real NDMF avatar build probe through `AvatarProcessor`. The build completed successfully, replaced the
  target body mesh with the assembled mesh, consumed the active installer/part renderer, remapped matching part
  bones to target-body bones, and produced only informational seam/UV/bone diagnostics.
- The probe confirmed the target changed from 17,749 to 19,957 vertices and from one to two submeshes; the active
  installer count became zero and the part renderer was consumed. UV-atlas differences intentionally preserved
  all 80 tested seam pairs as coincident split vertices rather than welding them.

## [0.3.0-rc.2] — M10: explicit armature selections, world-position seams, and authoring corrections

**Still a release candidate, still not run.** M1–M10 are implemented and source-review
accepted. The package has **never been run**: no Unity Editor compile, no Test Runner, no
NDMF build, no preview session, and no VRChat upload was executed by the harness.
"Code-review accepted" is the strongest claim this file makes.

This milestone replaces the two guesses the authoring flow could not defend — a bone identity
derived from where the part happened to sit in the avatar, and a seam pairing searched by
position at build time — with two declarations the author makes: the target armature and the
part armature that a bone path is recorded relative to, and a seam generated once from world
positions and stored as explicit pairs. It also stops `APA008` from firing for bone slots no
vertex references, captures the compatibility signature automatically on first use without
ever overwriting a captured one, and collapses the removal address list. Schema version moves
3 → 4, and `APA042`, `APA043`, and `APA044` are allocated.

### Added

**The two Armature selections (`ApaBoneProfile`, `Editor/Input/ApaArmatureScope.cs`)**

- `ApaBoneProfile.TargetArmaturePath` (avatar-root-relative; `ApaAvatarPath.Root` — `.` —
  means the avatar root itself; empty means not selected) and
  `ApaBoneProfile.PartArmaturePath` (part-root-relative; `.` means the part root itself;
  empty means not selected).
- Bone identity is now **armature-relative**: both roots are resolved at build time, each
  renderer's bones are recorded as paths relative to the armature selected for its own side,
  and two bones whose relative paths are byte-identical are the same joint and merge. A bone
  that *is* the armature root records `ApaAvatarPath.Root`. The full outer path from the
  avatar root to a part bone is no longer a bone identity, because it described where the
  part was placed rather than which joint the bone is.
- `Suggest Armatures` proposes both selections from the hierarchy. The window states that a
  proposal is a proposal: the two selections must be the corresponding bone levels of the
  body and of the part, and the author confirms them.
- Every installer that shares one body must select the **same** target armature; the group
  otherwise blocks with `APA043 reason=conflicting-target-armature-paths`.
- The generated `ModularAvatarMergeArmature` is added to the **selected part armature**,
  targets the **selected target armature**, and is always written with an empty prefix, an
  empty suffix, and no inference, so Modular Avatar's exact-name matching reproduces the
  armature-relative identity rather than adding a second rule beside it.

**World-position seam generation (`Editor/Authoring/ApaSeamWorldMatcher.cs`)**

- One action generates the whole seam: both meshes are read in their **rest pose**
  (`sharedMesh.vertices` transformed by `transform.localToWorldMatrix`, never `BakeMesh`),
  and a uniform spatial hash finds every world-coincident pair within a tolerance stated in
  **world units** (default `1e-4`, i.e. 0.1 mm; the window accepts `1e-7` through `1e-2`).
- The result is written as explicit pairs into the existing `ApaSeamSelection`: the entries
  at one index of `Base.VertexIndices` and `Part.VertexIndices` are one weld.
- Determinism and one-to-one: part vertices are visited in ascending index order, each takes
  the nearest still-free target vertex with ties broken by the lower target index, and no
  target vertex is claimed twice.
- `ApaSeamProfile.PairingVersion` records the semantics: `0` is the pre-M10 unordered sets,
  `1` is explicit pairs.
- The window shows the tolerance, the pair count, a short preview, and Clear / Regenerate; it
  no longer lists hundreds of seam vertices, and the Base/Part picking controls, the two
  index-list text fields, and the two Scene View picking modes are gone.

**Weighted-bone identity (`ApaNumericPolicy.WeightEpsilon`, `FinalBoneTable`)**

- `FinalBoneTable` collects the bone indices some vertex references with a weight above the
  new policy value `ApaNumericPolicy.WeightEpsilon` (default `1e-5`) and requires a stable
  identity only for those. An unreferenced slot — including a `null` entry — no longer
  blocks and no longer enters the final bone table; its remap entry stays `-1`.
- The weight remap uses the same threshold, so "the source is valid" and "the remap resolves
  every surviving influence" cannot disagree: an influence at or below the threshold is
  cleared to index 0 with weight 0.
- `APA044 BONE_OUTSIDE_SELECTED_ARMATURE` (`reason=bone-outside-armature`) blocks a bone that
  carries weight and is not inside the armature selected for its renderer.

**Automatic compatibility capture (`ApaAuthoringWindow`)**

- When the profile has never captured a signature (`IsCaptured == false`) and the live target
  renderer and mesh are usable, the window captures it once at the start of Validate,
  Dry-Run, Save Profile, Create Part Prefab, and Update Installer On Prefab, and then
  continues with the action it was asked to perform.
- A capture that fails leaves the profile uncaptured and reports the failure; an **already
  captured** signature is never overwritten, so a profile whose body changed still blocks
  with the mismatch it has. `Capture signature when target changes` and the explicit
  `Capture Signature` / `Clear Signature` buttons still exist.

**Collapsed removal address list (`ApaAuthoringWindow`)**

- The list shows `Addresses (N)` as a foldout and draws at most 28 rows when expanded, each
  still removable, with a summary of how many are not shown. The global `MaxListedRows`
  (200) shared with the other lists is deliberately unchanged; the numeric address fields and
  the mask's Replace / Add To Selection / Subtract From Selection modes remain the precise
  correction tools.

**Diagnostics and localization**

- `APA042 SEAM_PAIRING_REQUIRED` (`reason=seam-pairing-required`),
  `APA043 ARMATURE_SELECTION_INVALID` (`reason=missing-target-armature`,
  `missing-part-armature`, `target-armature-not-found`, `part-armature-not-found`,
  `target-armature-outside-root`, `part-armature-outside-root`,
  `conflicting-target-armature-paths`), and `APA044 BONE_OUTSIDE_SELECTED_ARMATURE` — three
  **core** codes declared in `ApaErrorCode`, enumerated in the new
  `ApaReservedCodes.Milestone10` and reachable through `IsMilestone10Code`. `APA045` is the
  next free code.
- Simplified Chinese entries for every new or changed label, help text, warning and error,
  including the two armature pickers, `Suggest Armatures`, the seam tolerance and pair count,
  the collapsed address list, and one description for each of the three codes. No English
  enum member name is shown to the user.

### Changed

- **Schema version 4.** `TryMigrate` accepts 2, 3, and 4 as a no-op and still writes nothing.
  A version-3 profile loads but is refused **at build time** by `APA043` (no armature
  selected) and `APA042` (the seam has no pairing) until it is re-authored: the missing
  information is a declaration only the author can make, not data a migration can invent.
- The build no longer reads `MergeTargetPath`, `MergePrefix`, `MergeSuffix`, or
  `InferMergeNames`, and the authoring window no longer shows those controls. The fields
  remain on `ApaBoneProfile` and still round-trip in an asset, so an existing profile loads
  unchanged; they are serialization compatibility, not a fallback path.
- `SeamResolver` consumes the stored pairs and verifies the index sets, the cardinality, the
  finiteness of the paired positions, and the one-to-one base claims
  (`APA003 reason=duplicate-base-claim`). It performs no position search at all: an
  avatar-root-local epsilon is a different world distance on every scaled level, and a
  position search is a guess whenever two vertices are close together.
- `APA002 SEAM_POSITION_MISMATCH` is now emitted by the generator when no part vertex is
  within the tolerance of any target vertex (`reason=no-world-coincident-vertices`), instead
  of by a build-time search.
- Nothing about the M9 mask rule changed: still 7 sample points per triangle with a majority
  of 4, RGB luminance with alpha ignored, one readback path with no format refusal, and the
  texture's own wrap mode addressing the integer texel indices.

### Fixed

- **`APA008` no longer fires for an unreferenced bone slot.** A `null` or identity-less slot
  that no weighted vertex references blocked the whole part while affecting no vertex; it is
  now neither blocking nor part of the final bone table. A *weighted* bone with no identity
  still blocks with `APA008 reason=bone-without-identity`.
- **A vertex whose whole weight set is negligible** is reported as
  `APA031 reason=zero-weight-sum` rather than being passed to the remap as a vertex with no
  influence.
- **First use no longer reports a missing signature as a defect**: the automatic capture
  removes the friction while leaving the captured signature immutable once it exists.
- **A scaled hierarchy no longer changes which vertices pair**: the seam tolerance is a world
  distance, so the same weld is found at any avatar scale.

### Documentation

- `README.md` — the authoring flow is rewritten around the two armature selections and the
  world-position seam; new "Armature selection and bone identity" section with the
  `APA043`/`APA044` tables; the seam section, the bone table, the collapse of the address
  list, the automatic capture, the migration table, and the diagnostic registry (`APA042` to
  `APA044`, next free `APA045`) are updated; the known-gaps table gains the M10 rows.
- `README.zh-CN.md` — the same content in Chinese, in the flow order the window uses, plus
  the glossary entries for the two armatures, the armature-relative identity, world-position
  seam generation, the pairing version, and the weight threshold.
- `USER_ACCEPTANCE_CHECKLIST.md` — new group 12 (ten rows) covering the two pickers and their
  refusals, the `APA042` re-authoring path, the unreferenced bone slot, the automatic capture
  and the never-overwritten signature, world-position generation on a scaled hierarchy, the
  collapsed address list, and both UI languages; group 7 is re-stated for schema 4 and the
  acceptance summary is renumbered to 13.
- `vrc_avatar_part_assembler_spec.md` — §52 (the armature-relative identity, the two
  selections and their validation, world-position generation with its determinism and
  one-to-one rule, the pairing version and `APA042`, the weighted-bone rule and the `APA008`
  correction, the automatic capture, the collapsed address list, the allocation record, and
  schema version 4), plus §43.11, §47.3, §48.7, and §49.1 now record the M10 allocation and
  the `APA045` free-code mark.

### Known limitations at this version

- Nothing has been run in Unity. The two selections, the world tolerance, the generated
  pairing, the refusals, and the automatic capture are source-reviewed and statically
  checked only. Checklist group 12 is the observation.
- A pre-M10 seam is **not** migrated. The two unordered lists are refused with `APA042`
  because pairing them by position is the guess M10 exists to remove; the author regenerates
  the seam once.
- `Suggest Armatures` is a proposal, never a decision. There is no automatic armature
  selection and no inference of the level correspondence: the author confirms both
  selections, and a wrong level produces a wrong bone table rather than an error.
- One group has one bone identity scope. Two installers that share a body cannot name
  different target armatures, even in a configuration where each part could be described
  relative to its own armature.
- The legacy `MergeTargetPath` / `MergePrefix` / `MergeSuffix` / `InferMergeNames` fields
  remain serialized and inert: they round-trip, they are never read, and nothing in the
  window sets them.

## [0.3.0-rc.2] — M9: black/white texture-mask removal selection

**Still a release candidate, still not run.** M1–M9 are implemented and source-review
accepted. The package has **never been run**: no Unity Editor compile, no Test Runner, no
NDMF build, no preview session, and no VRChat upload was executed by the harness.
"Code-review accepted" is the strongest claim this file makes.

This milestone adds the authoring input that makes a large removal region practical: a
black/white mask texture, converted through the target mesh's UVs into the **existing**
canonical removal triangle set. It changes no profile field, no schema version, no error
code's meaning, no asset path, and no assembly reference; the mask is an authoring input and
never reaches the profile, the prefab, or the build.

**M9 review fixes (same unreleased version).** A source review of the M9 delivery found seven
defects in it, all repaired here: the failure enumeration had two members sharing the value
`11`; the texture readback tried a `Graphics.CopyTexture` route that can silently return a
correctly sized but blank CPU buffer; compressed formats were refused before the device was
ever asked to decode them; the wrap mode was applied to the floating UV and then clamped, so a
bilinear sample at the texture border read the wrong neighbours; the per-triangle sample loop
allocated an array per triangle; the Apply Mode popup showed the raw enum member names in both
languages; and an unreadable target mesh disabled Apply while the line that is supposed to say
why printed nothing. The readback now has exactly one path, the format is no longer inspected,
the wrap mode addresses integer texel indices, the triangle evaluation allocates nothing, the
popup is localized, and a disabled Apply always states its reason. Nothing about the profile,
the schema, or the retained rule changed.

### Added

**Mask-to-triangle conversion (`Editor/Authoring/ApaRemovalMaskSampler.cs`)**

- `ApaRemovalMaskSampler.TryGenerate(Mesh, Texture2D, uvChannel, threshold, invert)` — the
  whole conversion, callable and reviewable on its own: it returns either the canonical
  `RemovedTriangleAddress` list or one `APA041` diagnostic, and never touches the window.
- The **fixed** rule, documented at the call site and in §51.3 of the specification: 7
  sample points per triangle (3 vertex UVs, 3 edge midpoints, the centroid), selected when
  **at least 4 of the 7** samples reach the threshold; brightness is the RGB luminance
  `0.299 R + 0.587 G + 0.114 B` over the texture's stored 8-bit values, **alpha ignored**;
  `invert` replaces the brightness with `1 − brightness` before the comparison. Nothing
  about the rule is configurable, so the same picture always selects the same triangles.
- Sampling honors the mask's own import settings: bilinear or nearest by the texture's filter
  mode. The texel position is derived from the UV exactly as the hardware derives it (texel
  centers at `(i + 0.5) / size`), and the texture's wrap mode addresses the integer texel
  indices — repeat wraps the index around the edge, so a bilinear sample at `u = 0`
  interpolates the last and the first texel; mirror reflects the index through a doubled
  period; clamp and `MirrorOnce` stop at the border texel.
- The seven sample positions are evaluated as values, one at a time: the production path
  allocates nothing per triangle. `SamplePoints` and `SamplePoint` remain public for a
  reviewer or a diagnostic view, and resolve their positions through the same definition.
- Triangle-list submeshes only, in submesh-then-triangle order, so the result is canonical by
  construction and needs no sort. A non-triangle-list submesh is a refusal, never
  reinterpreted.
- **No Read/Write Enabled, no importer change, no format gate.** The mask is never read
  directly and no `TextureImporter` is referenced. There is exactly one readback path: blit
  into a temporary `ARGB32` render texture and `ReadPixels` back — which is what decodes an
  imported, non-readable, or block-compressed mask. A `Graphics.CopyTexture` shortcut is
  deliberately absent: it updates the GPU's copy but not the CPU buffer a pixel read returns,
  and can silently yield a correctly sized, all-black mask. Allocation happens inside the
  `try`, the temporary may be null, `RenderTexture.active` is restored on every path, and
  nothing is released that was never acquired. Neither the mesh nor the texture is mutated.
- Refusals are actionable and stable: a null mesh or texture, an unreadable mesh, a UV
  channel outside 0–7 or absent from the mesh, a UV array whose length disagrees with the
  vertex count, a non-finite or out-of-range threshold, an unsupported topology, a vertex
  index outside the mesh, a non-finite UV, and a readback the device refused each stop the
  conversion with the condition named by a `reason=` token. A pixel format is never a refusal:
  a source the device cannot render and read back is `reason=texture-readback-failed`.

**Texture Mask block (`ApaAuthoringWindow` → Removal Region)**

- Texture2D field, UV channel 0–7 (default 0), grayscale threshold 0–1 (default 0.5), Invert
  toggle, and Apply Mode as a **localized** popup with the visible labels **Replace
  Selection** (default), **Add To Selection**, and **Subtract From Selection** — the
  serialized members behind them are still `ReplaceSelection`, `AddToSelection`, and
  `SubtractFromSelection`, and the popup maps a picked index back through the enum's own
  values, so no stored value is renumbered. Plus a one-line statement of the sampling rule.
- `Apply Mask` is disabled while the mask cannot run, and the reason — no target mesh, no
  mask texture, an unreadable target mesh (whose UVs cannot be read at all), or a UV channel
  the mesh does not carry — is printed next to the button, so a disabled Apply always says
  what to fix. The status reports the selected count and the number of triangles the
  conversion considered. An empty generated set is reported as a valid result, not as an
  error.
- A refusal renders its whole stable diagnostic — code, mnemonic title, message, and the
  `reason=` token — in the Texture Mask block where Apply was pressed (and again in the write
  section's "Last write reported" list); the status line no longer points at a list the issue
  is not in.
- One Apply is **one undo record**; the selection, the live removal check, and the Scene View
  highlight are marked dirty and repainted afterwards. Added and removed counts come from the
  set sizes before and after, so an already-selected address is reported as already present.
- The mask settings are `[SerializeField]` fields of the `EditorWindow` (they survive a
  reload) and are never written into the profile, the prefab, or any runtime type.
- The Scene View picker and the numeric address list are unchanged and remain the tools for
  small corrections and exact, reproducible entries.

**Diagnostics and localization**

- `APA041 REMOVAL_MASK_TEXTURE_FAILED` — a fourth authoring-layer code, allocated in
  `ApaErrorCode` (the single allocation table), enumerated in the new
  `ApaReservedCodes.Milestone9Authoring`, and aliased by
  `ApaAuthoringErrorCode.RemovalMaskTextureFailed`. `APA042` is the next free code. The
  `ApaMaskSampleFailure` members each carry a unique explicit value; there is no
  format-refusal member, because a pixel format is no longer a refusal condition.
- Simplified Chinese entries for the block's labels, tooltips, status lines, the three apply
  mode labels, and one description for `APA041`, plus the terminology in `README.zh-CN.md`'s
  glossary.

**Documentation**

- `README.md` — the Removal section gains "Black/white mask selection" (color meaning, UV
  channel, the fixed 7-sample rule, the readback guarantee, "only the triangle set is
  saved", the feedback and refusal behaviour), the authoring workflow's removal step names
  all three tools, and the diagnostic registry documents `APA041` and every `reason=` token.
- `README.zh-CN.md` — the same content in Chinese, plus the glossary entries for the mask,
  the apply-mode labels, and the texture readback.
- `USER_ACCEPTANCE_CHECKLIST.md` — new group 11 (nine rows) covering the conversion, Invert,
  the no-Read/Write guarantee, "the texture is not saved anywhere", the apply modes and
  undo, the empty result, the refusals, end-to-end determinism, and the other two authoring
  tools; the acceptance summary is renumbered to 12.
- `vrc_avatar_part_assembler_spec.md` — §51 (the mask model, the controls, the fixed rule,
  the readback boundary, the refusals, apply/undo, and what M9 explicitly does not do), plus
  §8 and §28 now name UV-assisted mask selection, and §43.11/§49.1 record the `APA041`
  allocation.

### Known limitations at this version

- Nothing has been run in Unity. The conversion, the readback, and the block's layout are
  source-reviewed and statically checked only — no mask has ever been sampled in the Editor.
  Checklist group 11 is the observation.
- The sampling rule is deliberately not configurable: there is no per-triangle override, no
  feathering, no smoothing pass, and no threshold falloff.
- The conversion reads one UV channel of one mesh at a time. A mask is not mirrored or copied
  to the other side of a symmetric mesh, and no mask is generated for the author.
- A mask selects triangles of the **target body** mesh only. The part mesh's own UVs are not
  consulted; the removal region is always expressed in the target's topology.

## [0.3.0-rc.2] — M8: English / Simplified Chinese localization

**Still a release candidate, still not run.** M1–M8 are implemented and source-review
accepted. The package has **never been run**: no Unity Editor compile, no Test Runner, no
NDMF build, no preview session, and no VRChat upload was executed. "Code-review accepted"
is the strongest claim this file makes.

This milestone makes the plugin's user interface bilingual and adds the Chinese user
documentation. It changes no serialized data, no error code, no `reason=` token, no asset
path, and no assembly reference.

### Added

**Localization layer (`Editor/Localization/**`, Editor core assembly, no new dependency)**

- `ApaLocalization` — the language preference (`Auto` / `English` / `SimplifiedChinese`),
  the `Tr` / `TrFormat` / `Content` lookups, the localized enum display names and popups,
  the severity labels, and the per-code Chinese description lookup. `Auto` resolves through
  `Application.systemLanguage` (Simplified Chinese and generic Chinese) at read time; the
  choice is stored in `EditorPrefs` under the stable key
  `dev.avatar-part-assembler/localization/language`, so selecting a language can never
  dirty a project asset. The default, until a choice has been stored, is English.
- `ApaLocalizationChinese` — the Simplified Chinese string table (337 entries) and one
  short Chinese description for each of the 42 allocated `APAxxx` codes. **English is the
  lookup key**: a missing entry renders the English sentence rather than an empty label, so
  English cannot go missing and a partial table degrades string by string. Composite
  entries keep their English key's `{n}` placeholders; a contract test asserts that.
- The language selector appears in the Part Authoring window's toolbar and at the top of
  the `AvatarPartInstaller` inspector. Both write the same global preference, and a switch
  repaints every open editor window and the Scene View on the same frame.
- A Chinese menu alias `Tools/部件装配器/部件编辑` for the authoring window. The canonical Unity `Tools` root is intentionally kept in English to avoid colliding with Unity's localized top-level menu. The documented
  English path `Tools/Avatar Part Assembler/Part Authoring` is unchanged, and both
  attributes call the same method.

**Localized surfaces**

- `ApaAuthoringWindow` — every section, field, button, tooltip, HelpBox, empty-state line,
  status message, undo label, file dialog and overwrite confirmation.
- `ApaInstallerInspector` — fields and tooltips, status block, shortcuts, validation
  verdicts, and the profile-creation dialog.
- `ApaAuthoringSceneTool` — the Scene View picking label, the removal/seam summary, and the
  operation hint.
- Preview: the diagnostic headline and severity summary, the debug overlay's headline and
  truncation notes, and both `TogglablePreviewNode` titles.
- `ApaDiagnosticText` — the severity word and the one-line summary. The `APAxxx` code, its
  stable English mnemonic title, the issue message, and the `reason=…` detail are
  deliberately **not** translated; in Chinese a short description is appended beside the
  unchanged token, so nothing is lost and the token stays searchable.
- Operation statuses and notes from `ApaProfileWriter` and `ApaPrefabGenerator`, the
  removal/seam/plan descriptions, the compatibility-capture descriptions, and the asset
  path rejection sentences.
- NDMF: the plugin and both pass display names, resolved when NDMF draws them.

**Documentation and tests**

- `README.zh-CN.md` (+ `.meta`): installation, the two workflows, profile and prefab
  creation, language switching, preview, build, the policy tables, the diagnostic registry,
  known limitations, the acceptance entry point, the assembly layout, and a terminology
  glossary. The English `README.md` links to it and its "English only" limitation is
  replaced by the real scope of the localization.
- `Tests/Editor/LocalizationContractTests.cs`: English fallback, table completeness and
  placeholder parity, the `EditorPrefs` key and its round trip, the enum display map (with
  the member names and serialized values asserted unchanged), the preserved diagnostic
  tokens, the range of codes carrying a Chinese description, and two source-scan contracts
  — every literal passed through the layer has an entry, and no authoring-UI label is an
  inline English literal.
- `USER_ACCEPTANCE_CHECKLIST.md` gains group 10 (language), and the acceptance summary is
  renumbered to 11. The checklist, README, and `package.json` move to `0.3.0-rc.2`.
- `vrc_avatar_part_assembler_spec.md` gains §50, which records the language model, the
  "English is the key" lookup rule, the table of what is localized against what stays
  English, the constraints this milestone keeps, and what M8 explicitly does not do.

### Changed

- Package version `0.3.0-rc.1` → `0.3.0-rc.2`, and the `package.json` description now
  mentions the bilingual interface.
- The English UI text, the English diagnostics, and the English documentation are
  **byte-identical** to `0.3.0-rc.1`. English is the fallback by construction, not a
  translation of a Chinese source.

### Known limitations at this version

- Nothing has been run in Unity. The language switch has never been drawn: the layer, the
  table, and the wiring are source-reviewed and statically checked only. Checklist group 10
  is the observation.
- Validation message bodies, exception text, asset names, hierarchy paths, and Unity API
  names stay English in both languages by design. A Chinese user reads a Chinese severity,
  section, control, and code description around an English sentence.
- Traditional Chinese is not served: `Auto` maps only Simplified Chinese and the generic
  `Chinese` system language onto the Simplified table, because offering Simplified copy to
  a Traditional reader would misrepresent the translation.

## [0.3.0-rc.1] — M7: release candidate, documentation, acceptance package

**Release candidate, not a 1.0.** M1–M7 are implemented and source-review accepted. The
package has still **never been run**: no Unity Editor compile, no Test Runner, no NDMF
build, no preview session, and no VRChat upload was executed by the harness. "Code-review
accepted" is the strongest claim any entry in this file makes.

This milestone is the stabilization and documentation delivery. It closes the package
for source review and hands the user one executable acceptance plan.

### Added

**User acceptance package**

- `USER_ACCEPTANCE_CHECKLIST.md` — the complete user-run plan, in nine groups: compile
  gate, EditMode suite gate, preview gate, build gate, deletion/restoration gate,
  determinism and repeatability gate, schema v3 migration gate, performance and resource
  gate, and the ecosystem gate (Modular Avatar, AAO, lilToon, PhysBone, Contacts,
  Animator, prefab variants, Build & Test, VRChat upload). Every row has exact steps and
  pass criteria, and the file ends with the facts that were reconciled against the frozen
  package, together with the one remaining declarative limitation
  (`IsModularAvatarAvailable`).
- The checklist's build group contains the one check no source review can substitute
  for: **preview result equals build result**, compared on vertex counts, per-submesh
  index counts, material slots, bone counts, bind poses, and blend shape names and frame
  weights.

**Documentation**

- `README.md` rewritten for the release candidate: release-candidate status, both
  workflows (avatar user and part author), the full policy truth tables (multi-target,
  installer activity, conflict priority, slots, removal, UV, materials, blend shapes,
  bones, merge names), the build and preview flow, the assembly layout and its reference
  direction, the installed NDMF/Modular Avatar API attribution, schema v3 migration, the
  complete APA registry, and an explicit "known limitations and honest gaps" section.
- The registry is now stated in one place: `APA001`–`APA040` plus `APA999`, with the
  allocation owner of each range, the severity of the non-error codes, and the next free
  code (`APA041`). `APA033`, `APA034`, and `APA050` are documented as authoring-layer
  codes beside the core ones so a reader cannot conclude a code is missing.
- `Third Party Notices.md` now records the verified dependency versions, the exact NDMF
  and Modular Avatar members relied upon, and — deliberately — the members that do
  **not** exist in the pinned versions, so a future reader does not restore them.
- `vrc_avatar_part_assembler_spec.md` gains the M3, M4, M5, and M6 implementation
  clarifications (§45–§48) and the M7 consolidation (§49), so the intent document and the
  shipped behaviour describe the same product.
- `work/.../architecture.md` corrected and extended: the unknown-schema code is `APA015`
  (not `APA012`), and the group model, the "one plan per group, one mesh per group"
  transaction, and the release-candidate status are recorded as architecture decisions.

### Changed

- Package version `0.2.0-m2` → `0.3.0-rc.1`. A prerelease version is used on purpose: it
  sorts before `0.3.0`, so no resolver can mistake this build for a stable release.
- `package.json` description updated to describe the delivered product (authoring window,
  grouped build, shared preview/build processor) and to state the release-candidate
  status.

### Known limitations at this version

- Nothing has been run in Unity. See the checklist.
- Preview/build equivalence, merge verification, mesh-lifetime behaviour on preview error
  paths, the `EditorOnly` interaction, and the whole ecosystem matrix are unverified at
  runtime.
- Whether a bone-signature mismatch is advisory or blocking is recorded as an open
  product question in the M6/M7 plan and must be reconciled against the frozen code.

## [0.3.0-m6] — M6: multi-part policy, target grouping, schema v3

### Added

**Target grouping (product-level single build)**

- `ApaTargetGroupPlan` and `ContextBuilder.BuildGroups`: one context per resolved target
  renderer, so a body group and a clothing group can coexist on one avatar.
- `TargetGroupAssembly` (new): the orchestrator that resolves, captures, validates, and
  plans **every** group before allocating a single mesh, then builds exactly one mesh per
  group. A blocking issue in any group fails the whole avatar and returns no mesh, so a
  partially assembled avatar cannot exist. Its result owns every generated mesh
  (`MarkAssigned` / `ReleaseUnassigned`), which makes a failed or partially consumed run
  leak-free.
- Group keys are the resolved target renderer's avatar-root-relative path; group-scoped
  issues carry `group=<path>` in their detail, appended to (never replacing) the existing
  `reason=` token.

**Conflict policy**

- `ApaPartSlotMode` (`Replace` / `Augment`) on `ApaPartIdentity`. `Augment` is the
  explicit way to attach a part to a body region another part owns; an augmenting part may
  not declare a removal set.
- `ApaPartIdentity.ConflictPriority` (serialized on the profile, so it travels with the
  prefab). Zero is inert. It orders parts (descending, first) and resolves removal-region
  overlap ownership when every claimant declares a distinct highest value.
- `ApaBoneProfile.MergePrefix` / `MergeSuffix` / `InferMergeNames`: the merge-name policy
  is serialized data, not an editor-only inspector side effect.
- `ApaBlendShapeProfile.AllowPartOnlyShapes` is now honoured instead of inert: declaring
  it false refuses a part-only shape outright.

**Diagnostics**

- `APA035 REMOVAL_OVERLAP_RESOLVED_BY_PRIORITY` (Warning), `APA036 UNDECLARED_UV_CHANNEL`,
  `APA037 INVALID_CONFLICT_PRIORITY`, `APA038 SUBMESH_WITHOUT_MATERIAL_SLOT`,
  `APA039 INACTIVE_INSTALLER_SKIPPED` (Info), and — added by the review fixes —
  `APA040 PART_ONLY_SHAPE_DISALLOWED`.

**Tests**

- `MultiPartConflictPolicyTests`, `TargetGroupingTests`, `InstallerPriorityTests`,
  `PolicyDeterminismTests`, `UvChannelDeclarationTests`, `MultiPartCompatibilityTests`,
  and the multi-part fixture builders.

### Changed

- Profile schema version 2 → 3. The 2 → 3 migration is an explicit **no-op accept**: every
  added field defaults to the version-2 behaviour, so an existing profile means exactly
  what it meant before and is not rewritten on read. Schema 1 stays refused.
- Installer discovery is avatar-scoped: the walk stops descending at a nested avatar
  descriptor, so avatar A's parts can never be welded into avatar B's body.
- One activity predicate (`AvatarPartInstaller.IsActiveForBuild`) decides whether a part
  participates, and preview and build both call it. A parked part is reported (`APA039`)
  rather than silently ignored.
- A source submesh that produces no final material slot is reported as `APA038` instead of
  borrowing `APA009`, whose registered meaning is a different condition.
- `CompatibilityRule` compares the recorded renderer path as a safety field, so a profile
  whose target cannot be identified blocks instead of silently welding into a renderer
  that merely shares a mesh asset.

## [0.3.0-m5] — M5: Part Authoring window, profile and prefab generation

### Added

- `ApaAuthoringWindow` (`Tools/Avatar Part Assembler/Part Authoring`): selection, target
  capture, removal picking, seam picking, UV and material semantic editors, bone/blend
  shape policy, validation, dry-run planning, profile writing, prefab generation, and
  prefab-installer updating — as a thin view over separately reviewable parts.
- Scene View tooling: `ApaAuthoringSceneTool` (modes, highlighting, hover picking),
  `ApaRemovalMask`, `ApaSeamSelection`, `ApaScenePicking`, and `ApaMeshArrayCache`.
- `ApaProfileDraft` (draft model, stable part id assignment, no shared state with the
  asset), `ApaProfileWriter` (create/update with explicit overwrite confirmation),
  `ApaPrefabGenerator` (create/update, portability scanning, installer creation),
  `ApaInstallerInspector`, `ApaCompatibilityCapture`, `ApaAuthoringValidation`,
  `ApaAuthoringAssetPaths`, `ApaAssetDatabaseUtility`, `ApaAuthoringContextBuilder`.
- `APA033 INVALID_AUTHORING_PATH` and `APA034 NON_PERSISTENT_REFERENCE` in the
  authoring-layer allocation table, with titles rendered through one diagnostic
  formatter.
- `APA050 UV_SEMANTIC_CHANNEL_ABSENT`, added by the M5 review fixes. It sits deliberately
  outside the core's sequential range: `APA035` and above belong to the core, and the
  authoring layer must not consume a code the core may allocate next.

### Changed (asset safety)

- Every write passes `ApaAuthoringValidation` first; an existing asset is never replaced
  without explicit confirmation; the decision uses the **main asset** at the path, not a
  same-type load, so an unrelated asset at a colliding path cannot be silently replaced.
- The profile update path normalizes `hideFlags` and the name after `CopySerialized`, so a
  second save cannot leave the asset hidden and excluded from the build.
- A declared UV semantic whose source channel the part mesh does not carry is refused
  (`APA050`) instead of being saved as a channel that would be silently zero-filled.
- The authoring-layer validation reports a scene-object reference that must become part
  of a serialized asset (`APA034`) rather than writing a reference that cannot survive.

## [0.3.0-m4] — M4: NDMF preview over the same processor

### Added

- `AvatarPartRenderFilter` (`IRenderFilter`) and `ApaPreviewNode`
  (`IRenderFilterNode`): one proxy renderer per affected target body renderer. The node
  writes the assembled mesh, materials, bones, and blend shape weights onto the proxy the
  pipeline hands it; the original renderer is never written to.
- `ApaPreviewDiscovery` / `ApaPreviewRequest` (avatar-scoped discovery and capture),
  `ApaPreviewFingerprint` + `ApaFingerprintBuilder` (FNV-1a content fingerprint
  `apa-preview-fnv1a64-v1` over the captured inputs, mesh attributes, profile
  serialization, transforms, material identities, and numeric tolerances),
  `ApaPreviewMeshCache` + `ApaPreviewLease` (bounded, lease-aware, destroys evicted
  meshes, torn down before an assembly reload / on quit / on a play-mode transition, and
  never caches a failure), `ApaPreviewInputObserver`,
  `ApaPreviewProxyApplier` (bone and blend shape mapping onto the proxy),
  `ApaPreviewDiagnostics`, and `ApaPreviewDebugOverlay`.
- `ApaPreviewToggles`: `dev.avatar-part-assembler/preview/Main` (on by default) and
  `dev.avatar-part-assembler/preview/DebugOverlay` (off by default), both visible in
  NDMF's *Configure Previews*.
- `PreviewStaticContractTests`: source-contract tests for the invariants that matter to
  review (which file may write a proxy mesh, overlay default, bounded cache, teardown,
  non-randomized fingerprint), because the preview assembly is not referenced by the test
  assembly.

### Changed

- **Blocked means absent.** An invalid input produces no render group, so NDMF draws the
  original body and no stale successful preview can survive an edit.
- Exceptions in `GetTargetGroups` and `Instantiate` are caught and reported as diagnostics
  instead of faulting the whole preview build for every filter.
- Duplicate renderers across nested avatar roots are filtered, because NDMF drops every
  group of a filter that returns one renderer twice.

### Known limitations at this version

- The filter compiles and is testable but is not registered with any preview session until
  the NDMF pass publishes it with `PreviewingWith`. Until then NDMF shows no APA preview.

## [0.3.0-m3] — M3: NDMF build integration, transient Modular Avatar merge armature

### Added

- `ApaNdmfPlugin` and `ApaAssemblyPass` (Transforming) / `ApaMergeArmaturePass`
  (Generating), with `AfterPlugin("nadena.dev.modular-avatar")` declared by qualified
  name because Modular Avatar's plugin class is internal to its own assembly.
- `ApaBuildProcessor` (`ApaBuildRequest` → `ApaBuildResult`): discover, capture, validate,
  plan, resolve every live object the plan needs, then write and consume. A failure
  returns no mesh and a complete issue list, and restores the renderer if assignment
  failed part-way.
- `ApaTransientArtifacts`: a per-build ledger of the merge configurations created and the
  bone each merge must move, so the assembly pass can prove the merge happened instead of
  assuming it. Two blocking reasons: `reason=merge-not-consumed` (the configuration is
  still alive: Modular Avatar's merging pass did not run) and `reason=merge-not-applied`
  (consumed, but the part's top bone still lives under a part root).
- `ApaNdmfDiagnostics` + `ApaNdmfDiagnostic` + `ApaDiagnosticReferences`: the core's
  `ValidationIssue` mapped onto `ErrorReport`, with `Error` → `ErrorSeverity.Error`
  (blocks the upload), `Warning` → `NonFatal`, `Info` → `Information`, and every object
  reference resolved **before** anything is mutated or consumed.
- `ApaBuildTargets`: resolution of the renderer, mesh filter, skinned renderer, and final
  bone array as one all-or-nothing step.
- `MergeArmatureGenerator` activated: the single file that names Modular Avatar types.
- `BuildPipelineAssemblySeamTests` and `NdmfMergeArmaturePlanTests`.

### Changed

- The generated mesh is written to the **NDMF build clone** only, with
  `HideFlags.None` so NDMF's own serialization persists it into the uploaded avatar.
- The part renderers and installer components that contributed geometry are consumed
  (component only) after the assignment succeeds, so part geometry is not drawn twice.
  A renderer that belongs to another installer this build does not process is **left in
  place** and reported as a warning instead.
- The pass returns immediately when the build has already failed, and when the avatar has
  no active installer.
- UV distribution recalculation is disabled for a generated mesh that carries no UV0,
  using the switch NDMF exposes for exactly that case.
- Both passes are VRChat-avatar-only, NDMF's default for a plugin without
  `RunsOnAllPlatforms`.

## [0.2.0-m2] — M2: skinning, final bones, bind poses, blend shapes

Second milestone delivery. The core no longer refuses skinned or blend-shaped input — it
processes both. There is still no NDMF pass at this version, so nothing is assembled on an
avatar yet.

### Added

**Skinning (R10)**

- `FinalBoneTable` and `FinalBoneTableBuilder`: one final bone table built after the final
  hierarchy is known. The target body's bones come first in the body's own order, then
  bones new parts contribute in stable part order and source order. A part bone whose
  avatar-root-relative identity already exists **merges** onto it, provided the transforms
  agree within the configured position epsilon.
- `FinalBoneTableSkinningProvider`, now the default: remaps every retained vertex's
  `BoneWeight` onto the final table. A welded vertex keeps the base body's weight by
  construction, because the planner emits no vertex of its own for a part seam vertex.
- `BoneWeightValidator`: one place that both validates weights and performs the remap, so
  "what was validated" and "what was written" cannot drift apart. Validates the array
  length, finite values, negative weights, zero weight sums, and out-of-range bone indices.
- Bind poses are computed as `finalBone.worldToLocalMatrix *
  finalRenderer.localToWorldMatrix` for the final renderer. A source bind pose is never
  reused. `MeshSnapshot` now carries each bone's real `worldToLocalMatrix` (the previous
  capture fell back to `mesh.bindposes`, which is a different matrix), and
  `BaseSnapshot` carries the renderer's captured `localToWorldMatrix`.

**Blend shapes (R11)**

- `BlendShapeCatalog` and `BlendShapeMergePlan`: one final shape set, ordered body shapes
  first and then new part shapes by stable part and source order. Same-named contributions
  merge into one shape and must agree on frame count and frame weights.
- `BlendShapeSeamValidator`: a part-only shape must have **exactly zero** deltas at every
  welded seam vertex, and a same-named base/part shape must agree at the seam within the
  position epsilon.
- `RemappedBlendShapeProvider`, now the default: writes every frame of every final shape
  over every retained vertex, for position, normal, and tangent deltas, transformed into
  the target renderer's local space with the same matrices the geometry uses.
- `BlendShapeFrameSnapshot` now exposes `VertexCount`, so a frame whose delta arrays
  cannot be indexed by vertex is reported instead of being silently clamped.

**Diagnostics**

- New stable codes: `APA027`–`APA031`, and — added by the review fixes —
  `APA032 INVALID_SPACE_TRANSFORM`.
- `APA007`, `APA008`, and `APA011` — allocated for M2 by the specification — are now
  emitted. `ApaReservedCodes` records the allocation.
- `SkinningRule` and `BlendShapeRule` join the validator, so pressing Validate reports bone
  and shape problems without planning a build.

**Tests and verification**

- `SkinningTests`, `BlendShapeTests`, `ContextBuilderTests`, `TransformSpaceTests`,
  `AvatarRootPathTests`, plus M2 fixture builders in `MeshFixtures`.
- A static compile check and an executable harness run outside Unity compile all three
  assemblies against Unity's own reference assemblies and execute the pure validation,
  planning, bone-table, weight-remap, and bind-pose paths. That harness found and fixed a
  defect where a base blend shape was not registered as a source, which would have written
  zero deltas at every body vertex.

### Changed

- `MeshAssembler` uses the real skinning and blend shape providers by default. It produces
  and validates provider output **before** allocating a mesh, and destroys the mesh before
  returning a failure if blend shape application fails, so a failed build cannot hand back
  a partial result.
- `UnsupportedAttributeRule` no longer blocks skinning or blend shapes; it reports only
  values that cannot be interpreted at all (non-finite positions, normals, tangents, UVs).
- `BlockingSkinningProvider` / `BlockingBlendShapeProvider` are kept but are no longer the
  defaults: they are the providers a caller selects to have skinned input refused.
- `SpaceTransforms` is an immutable value holding captured matrices instead of live
  `Transform` references, and defines the transform operator for each blend shape delta
  kind once for both validation and output.

### Fixed

- **Compile error:** `Runtime/Profiles/ApaProfileParts.cs` referenced `BoneSignature`, which
  was defined in the Editor assembly. `BoneSignature` is now a Runtime type and the Editor
  duplicate is gone.
- **Pre-existing compile error:** `AssemblyPlanner` passed a `RemovedTriangleAddressSet`
  where a `List<RemovedTriangleAddress>` was expected. M1 was never compiled, so this had
  not been caught.
- **Pre-existing compile error:** five test sources used Editor types without a
  `using AvatarPartAssembler.Editor;` directive.
- A base mesh's blend shape was not registered as a contributor to its own merged shape.
- Every part is now captured against the **resolved** target body renderer; a part whose
  installer named no target used its own transform as the target.
- Two installers naming different target renderers now block
  (`APA006 reason=conflicting-target-renderers`) with no context.
- Renderer paths, bone identity, and the installer ordering tiebreaker are recorded
  avatar-root-relative, so a recorded path still resolves when the avatar is nested.
- Same-named blend shape seam deltas are compared in the target renderer's local space with
  the operator the emitted frame uses.
- Tangent blend shape deltas use the linear source-to-target matrix instead of the inverse
  transpose used for normals.
- A missing, non-finite, or singular space transform blocks with `APA032` before any
  normal matrix or delta reads it.
- The avatar root is recorded as the canonical token `.` (`ApaAvatarPath.Root`) instead of
  an empty path; empty is reserved for **missing** (a null transform or a null bone entry)
  and still blocks (`APA008 reason=bone-without-identity`).

## [0.1.0-m1] — M1: contracts and deterministic mesh core

First milestone delivery. The package skeleton, the authoring data contracts, and a
deterministic validation, planning, and mesh-assembly core. Skinning, blend shapes, and
NDMF build integration follow in later milestones.

### Added

**Specification**

- An "Implementation clarifications" section appended to
  `vrc_avatar_part_assembler_spec.md`, reconciling the Phase 0–8 roadmap with the M1–M7
  milestones and recording the coordinate contract, seam attribute ownership,
  deterministic ordering, versioning, material policy, blendshape, and
  unsupported-attribute rules.

**Package**

- Embedded VPM package at `Packages/dev.avatar-part-assembler/` with valid `package.json`
  metadata and explicit `vpmDependencies` on VRChat SDK 3.10.4, NDMF 1.14.0, and Modular
  Avatar 1.18.0-beta.0.
- Three assemblies: `Runtime`, `Editor` (Editor-only), and `Tests.Editor` (gated behind
  `UNITY_INCLUDE_TESTS`, not auto-referenced).
- `README.md`, `LICENSE.md`, `Third Party Notices.md`, and this changelog.

**Runtime contracts**

- `ApaPartProfile`, a versioned `ScriptableObject` carrying part identity, the
  compatibility signature, the removal triangle set, the seam loops, UV and material
  semantics, and bone and blend shape configuration. Usable with no Editor dependency, so
  a built part prefab is self-describing.
- `AvatarPartInstaller`, the data-only component a user adds to an avatar.
- `ApaNumericPolicy` (1e-5 position and UV epsilons) and `ApaSemanticName` (trim then
  ordinal, never case-folded).
- `ApaErrorCode` with the `APA001`–`APA026` allocation, plus `APA999`.

**Editor core**

- `ApaCore`, the single facade preview and build must both call.
- Immutable input snapshots and the one class that reads live Unity meshes, which is what
  makes the non-mutation guarantee structural.
- Validation: `AvatarPartValidator` with independently maintainable rules for
  configuration, compatibility, attribute support, removal, part slots, seams, UVs, and
  materials.
- `SeamResolver`: strict cardinality, unique one-to-one position matching in avatar-root
  local space, and a 27-bucket quantized hash that narrows candidates but never decides a
  match.
- `UvResolver` and `MaterialResolver`: semantic-name merge with deterministic layout and
  the `Auto` / `UseTarget` / `KeepPart` / `ForceNew` policies.
- `AssemblyPlanner` and the immutable `MeshAssemblyPlan`: every ordering decision is made
  once, before any mutation.
- `MeshAssembler`: true weld with the part seam vertices deleted, full vertex remap,
  index-format selection, inverse-transpose normals, preserved tangent handedness,
  recomputed bounds, and `(0,0,0,0)` defaults for absent UV semantics. Normals and tangents
  are never recalculated.

**Future integration seam**

- `MergeArmatureGenerator`, an inactive M3-ready wrapper over the verified Modular Avatar
  merge-armature API.
- M1 registers no NDMF plugin or build pass.

**Tests**

- EditMode test sources covering seam determinism and ambiguity, removal validation, the
  true weld, UV merge and mismatch and overflow, material policies and conflicts,
  unsupported data, compatibility and versioning, determinism and non-mutation, and
  attribute preservation. **The tests have not been run** — see the README.

### Known limitations

- The sources have **not been compiled**. They were reviewed and passed non-Unity static
  structural checks only.
- `Packages/manifest.json` was not modified, so the test assembly is not yet visible to the
  Unity Test Runner. The README gives the one-line change required.
- Skinned and blend-shaped input is blocked with `APA014` rather than assembled. This is
  the intended M1 behaviour, not a defect.
- `APA007`, `APA008`, and `APA011` are allocated for M2 and are not produced by M1.

# Avatar Part Assembler

Non-destructive, deterministic assembly of modular VRChat avatar body parts.

A part author publishes a prefab. A user drags that prefab under their avatar. The
plugin removes the declared region of the original body, welds the part's seam onto
the body's seam at the vertex level, and merges geometry, skinning, blend shapes, UV
semantics, materials, and bones into one generated mesh per target renderer. Delete the
prefab and the avatar is exactly as it was, because nothing was ever written to an
authoring asset.

> **Builder 不猜，Validator 负责阻止错误资产进入构建。**
> The builder does not guess; the validator keeps bad assets out of the build.

> **简体中文文档：[`README.zh-CN.md`](README.zh-CN.md)**
> The Simplified Chinese user documentation lives in [`README.zh-CN.md`](README.zh-CN.md).
> The plugin's UI is available in English and Simplified Chinese; see
> [Language](#language). This file stays the English source of truth for the product's
> behaviour, its policy tables, and its diagnostic registry.

---

## Release-candidate status

**This is release candidate `0.3.0-rc.6` (`0.3.0-rc.6` in `package.json`). It is not a
1.0 release and the full acceptance checklist is still incomplete.**

The core build path has now been exercised in Unity 2022.3.22f1 rather than only reviewed
statically. A real NDMF `AvatarProcessor` run completed successfully on the project test
avatar: the target body mesh was replaced by the assembled mesh, the installer/part renderer
were consumed, matching part bones were redirected to body bones, and the resulting build
reported only informational seam/UV/bone diagnostics. The same source was also compiled
against Unity's actual Bee/Roslyn response files for the Editor, NDMF, Preview, and Editor
Test assemblies.

The complete user verification plan is still in
[`USER_ACCEPTANCE_CHECKLIST.md`](USER_ACCEPTANCE_CHECKLIST.md). Preview rendering, broader
ecosystem combinations, and VRChat upload still need the remaining checklist coverage.
Where this document and the running Editor disagree, the Editor is right and the document
is a bug.

| Milestone | Contents | Status |
| --- | --- | --- |
| M1 | Specification clarifications, package skeleton, serializable contracts, deterministic validation/planning/mesh core for geometry, UV, and materials | Implemented |
| M2 | Skinning, final bone table, bind poses, blend shape remapping | Implemented |
| M3 | NDMF Generating/Transforming passes, transient Modular Avatar merge-armature setup, post-merge verification | **Runtime build validated** |
| M4 | NDMF Scene View preview over the same process, fingerprint cache with invalidation, debug overlay | Implemented; full visual acceptance pending |
| M5 | Part Authoring window, removal/seam pickers, UV and material semantics editors, profile writer, prefab generator, installer inspector | Implemented |
| M6 | Multi-target grouping, slot modes, conflict priority, material anchors, undeclared-UV and part-only-shape policies, schema v3 | Implemented |
| M7 | Stabilization, release-candidate versioning, consolidated documentation, user acceptance checklist | Implemented |
| M8 | English / Simplified Chinese UI localization, `README.zh-CN.md`, localization contract tests | Implemented |
| M9 | Black/white texture-mask removal selection: mask → deterministic triangle set, `APA041`, fixed 7-sample rule | Implemented |
| M10+ | Explicit armature selections, stored seam pairs, body-authoritative bone remapping, UV-seam preservation, post-MA armature scope hardening, and Play Mode / Gesture Manager compatibility | **Runtime build path validated; remaining acceptance pending** |

The current runtime result does **not** imply that every ecosystem combination has been
accepted. In particular, complete Scene View preview acceptance, every optional third-party
plugin combination, and an end-to-end VRChat upload remain checklist items.
- that the package is ecosystem-compatible (Modular Avatar, AAO, lilToon, PhysBone,
  Contacts, Animator, prefab variants, upload).

---

## Requirements

| Requirement | Version | Declared by |
| --- | --- | --- |
| Unity | **2022.3.22f1** (the `2022.3` stream) | `package.json` → `unity` |
| VRChat SDK — Avatars | **3.10.4** | `package.json` → `vpmDependencies` |
| NDMF (*Non-Destructive Modular Framework*) | **1.14.0** | `package.json` → `vpmDependencies` |
| Modular Avatar | **1.18.0-beta.0** | `package.json` → `vpmDependencies` |
| `com.unity.modules.animation` | 1.0.0 | `package.json` → `dependencies` |
| `com.unity.test-framework` | any 1.1.x shipped with 2022.3 | **not** declared by this package — it is a *project* dependency, needed only to run the EditMode suite |

The VPM ranges are `>=3.10.4 <3.11.0`, `>=1.14.0 <2.0.0-a`, and
`>=1.18.0-beta.0 <2.0.0-a`. The API attribution in
[Installed NDMF and Modular Avatar API attribution](#installed-ndmf-and-modular-avatar-api-attribution)
was verified against exactly those installed versions; a different version is an
unreviewed combination.

## Installation

### As an embedded package (this project)

The package already lives at `Packages/dev.avatar-part-assembler/`. Unity picks it up
automatically on the next domain reload. No manifest edit is needed.

### As a VPM package (for distribution)

Add the repository to your VPM client (VCC or ALCOM), or install the `.zip` release.
The package declares its dependencies under `vpmDependencies`, so NDMF, Modular Avatar,
and the VRChat SDK are resolved automatically.

### Making the tests visible

The test assembly is gated behind `UNITY_INCLUDE_TESTS` and is **not**
auto-referenced. To make it visible to the Test Runner, add the package to the project
manifest's `testables` list:

```json
{
  "testables": [
    "dev.avatar-part-assembler"
  ]
}
```

That is a project-level change and was deliberately **not** made by the package work,
because the package is scoped to `Packages/dev.avatar-part-assembler/**`. The test
assembly is still *compiled* by Unity without it — it is an Editor assembly, so a
compile error in a test source shows up in the Console. What `testables` controls is
whether the Test Runner can see and run it.

---

## Play Mode and Gesture Manager

`0.3.0-rc.6` enables Play Mode compatibility by default. When a loaded scene contains an
`AvatarPartInstaller`, APA temporarily enables NDMF's official **Apply On Play** setting before
entering Play Mode.

APA now prebuilds on Unity's **temporary Play Mode scene copy** through
`IProcessSceneWithReport`, running NDMF directly before scene components receive `Awake` / `Start`.
Gesture Manager and other avatar emulators therefore cannot pose the armature before NDMF
Generating → Modular Avatar Transforming → APA Transforming has assembled the mesh. The early
prebuild deliberately does not invoke the whole VRChat preprocess callback chain, so it does not
consume or collide with VRCFury's one allowed Play Mode preprocess pass.

There is no longer an `EnteredPlayMode` second build and no late `Animator.Rebind()` recovery.
Rebuilding after an emulator has already posed the bones can combine T-pose vertex data with bind
data captured from a posed armature, producing severe deformation on the next animation update.

During edit-time preview, local position/rotation/scale changes on bones are treated as live pose state rather than
assembly inputs. The proxy keeps the live bone references, so dragging or scaling a Spine deforms the assembled mesh
without rebuilding bind poses from that edited pose. Bone parent/name/list changes still invalidate the preview because
they change armature-relative resolution. If Play Mode still reports a rest mesh driven by edited bones, inspect the
console for `Play Mode scene prebuild failed` or an APA blocking diagnostic: that means the early temporary-scene
prebuild did not complete before the animator/emulator ran.

Part mesh fingerprints are checked in NDMF's **Generating** phase, before Modular Avatar consumes the
transient merge-armature configuration. Modular Avatar may rewrite a temporary part renderer's skin weights and
bind poses during its Transforming phase; that post-merge mesh is therefore intentionally not compared with the
authoring fingerprint a second time. An `APA048` from Generating means the source part really changed and the
profile should be recaptured; an `APA048` after the merge would indicate a regression in this ordering contract.

Only Unity's temporary Play Mode scene copy is modified; the edit-time scene, prefab, and model
assets are not written. APA remembers the user's previous NDMF Apply On Play value across the
Play Mode domain reload and restores it on return to Edit Mode. To intentionally test the raw,
unprocessed authoring avatar, toggle
`Tools > Avatar Part Assembler > Play Mode + Gesture Manager Compatibility` off.

---

## Modular Avatar Merge Animator compatibility

A part prefab may carry a Modular Avatar **Merge Animator** — typically in Relative path mode on the part root —
whose controller animates the part renderer, most often its blend shapes. Modular Avatar virtualizes that
controller and prefixes every recorded path with the part root's avatar-relative path, so the clip addresses the
part renderer by the path it had when the animator services context was opened. APA assembles that renderer's
geometry into the target body renderer and consumes the part renderer; without a retarget the animation would keep
addressing an object the assembly replaced, and the blend shape would stop responding.

APA registers the consumed part renderer object → its group's target renderer object with NDMF's
`AnimatorServicesContext.ObjectPathRemapper.ReplaceObject`, and NDMF commits the mapping into the generated clips
when the context deactivates — the same mechanism Modular Avatar uses when it merges a bone away. No animation
clip is edited by this package, no serialized controller is touched, and no Modular Avatar file is changed.

The timing is the contract, and it is why the step is a separate NDMF pass:

| Step | Why it is there |
| --- | --- |
| The pairs are captured in the assembly pass, before the consumed renderer components are destroyed | A destroyed component cannot be asked for its GameObject, and the mapping needs objects |
| The retarget step **requires** `AnimatorServicesContext` | Modular Avatar has already closed the context it used by the time APA's Transforming sequence runs, so NDMF opens it for this pass and that activation's own deactivation commits the mappings |
| The retarget step runs after the assembly pass | A mapping is only ever registered for an assembly that happened |
| The cleanup step runs after `nadena.dev.modular-avatar.late-transform-stages` and after the retarget step | The late stages purge the remaining Modular Avatar components (so a leftover `ModularAvatarMergeAnimator` cannot keep an empty object alive), and the retarget step needs the source objects alive |

The animation-retarget step is skipped when the build assembled nothing, and neither step mutates a build whose
report already carries an error.

### Empty source-object cleanup

The assembly consumes a part renderer by destroying its **component** only, leaving the object and everything
under it alone, because the generated mesh may be skinned to bones that live there. The renderer's own object is
often left carrying nothing but a Transform. APA removes exactly those objects, and only those: the object must be
alive, not the avatar root, parented, childless, and carry no component other than its Transform. A child, a
constraint, a PhysBone, an authoring component, or a missing script reference keeps it.

A part renderer that is a `MeshRenderer` leaves its `MeshFilter` behind — the assembly consumes the renderer
component and nothing else — so such an object is not empty and is kept. The cleanup removes what the assembly can
actually leave empty and never guesses.

---

## Language

The plugin's user interface is available in **English** and **简体中文 (Simplified
Chinese)**. The switch has three positions:

| Option | Meaning |
| --- | --- |
| `Auto (follow system)` / `自动（跟随系统语言）` | Follows `Application.systemLanguage`: a Simplified Chinese system gets Simplified Chinese, every other system language gets English |
| `English` | Always English. The fallback language |
| `简体中文` | Always Simplified Chinese |

- **Where.** The language selector is at the right end of the Part Authoring window's
  toolbar (`Tools > Avatar Part Assembler > Part Authoring`, or the Chinese menu alias
  `工具 > 部件装配器 > 部件编辑`) and at the top of the `AvatarPartInstaller` inspector.
  Both use one global setting. Switching repaints every open editor window and the Scene
  View on the same frame; no domain reload or restart is needed.
- **Default.** English, until a choice has been stored. A fresh install therefore shows
  an existing user exactly the interface and diagnostics they had before the feature
  existed, and English can never go missing.
- **Storage.** The choice is per-user editor state in `EditorPrefs` under the stable key
  `dev.avatar-part-assembler/localization/language`. It is never written to a project
  asset, a scene, or a profile, so selecting a language cannot dirty version control.
- **What stays English on purpose.** The `APAxxx` codes and their stable mnemonic titles
  (`UV_SEMANTIC_CHANNEL_ABSENT`), `reason=…` tokens, validation message bodies, exception
  text, asset paths, and Unity API names. Those are the searchable, testable contract
  rather than UI copy; translating them would make a Chinese bug report harder to compare
  with an English one. In Simplified Chinese a one-line Chinese description of the code
  is appended beside the unchanged token:

  ```text
  错误 APA050 UV_SEMANTIC_CHANNEL_ABSENT（UV 语义声明的通道在部件网格上不存在） [3]: ... :: reason=channel-absent
  ```

- **Scope of the layer.** `Editor/Localization/ApaLocalization.cs` (the API,
  the language preference, the enum display names, the diagnostic labels) plus
  `ApaLocalizationChinese.cs` (the string table and one description per allocated code).
  English is the lookup *key*, so a missing entry renders the English sentence instead of
  an empty label — a partially translated table degrades string by string. The layer adds
  no package dependency (Unity Localization in particular) and lives in the Editor core
  assembly, which the preview and NDMF assemblies already reference; the Runtime assembly
  is untouched and still contains no `UnityEditor`.
- **Tests.** `Tests/Editor/LocalizationContractTests.cs` asserts the English fallback, the
  placeholder parity of every entry, the preference key and its round trip, the enum
  display map, the preservation of the stable diagnostic tokens, and — by scanning the
  sources — that no user-visible English literal in the authoring UI bypasses the layer.
- **Coverage of M10.** Every label, help text, warning, and error M10 added or changed has a
  Simplified Chinese entry — the two armature pickers, `Suggest Armatures`, the seam
  tolerance and pair count, the world-position generate action, the collapsed address list,
  and one description for each of `APA042`, `APA043`, and `APA044` — and no English enum
  member name is shown to the user. A new string that has no entry falls back to its English
  sentence rather than rendering empty.

---

## Two workflows

### As an avatar user (install a part)

1. Download or copy the part prefab into the project.
2. Drag the prefab under the avatar.
3. Done. The part's `AvatarPartInstaller` declares what it replaces, and the build and
   the Scene View preview install it. You never configure Modular Avatar by hand.
4. To uninstall: delete the prefab. The avatar returns to exactly its previous state.

The part is *not* installed destructively: the installer only takes effect on the
transient clone that NDMF builds for preview and for upload. Your authoring scene is
never modified.

### As a part author (create a part)

Open `Tools > Avatar Part Assembler > Part Authoring`. The window guides the flow:

1. **Selection** — assign the avatar root, then the target body renderer.
2. **Part** — assign the part root and the part renderer.
3. **The two armatures** — select the **Target Armature** (the level under the avatar root
   that owns the body's bones) and the **Part Armature** (the level under the part root
   that owns the part's bones). `Suggest Armatures` proposes them from the hierarchy, but
   a proposal is only a proposal: you confirm both. A bone's identity is its path
   **relative to its own selected armature**, so the two armatures must be the
   corresponding bone levels of the body and of the part; a wrong level does not raise an
   error, it makes bones that should have merged each become a new bone. The legacy merge
   path, prefix, suffix, and inference controls are gone.
4. **Seam** — set a tolerance stated in **world units**, press `Generate From World
   Positions` once to write explicit pairs, and check the pair count and the preview. The
   seam is no longer picked vertex by vertex and no longer typed as two index lists.
5. **Compatibility** — press `Capture Signature` to record the target body's signature
   (mesh GUID when available, vertex count, per-submesh index counts and topologies,
   blend shape names and frame counts, bone paths). Every topology-indexed value below
   is only valid against that exact mesh. If you skip this step and go straight to a later
   action, the window captures the signature once at the start of `Validate`, `Dry-Run
   Assembly`, `Save Profile Asset`, `Create Part Prefab`, and `Update Installer On Prefab`
   — but only while the profile has **never** captured one and the live target is usable.
   An already captured signature is never overwritten.
6. **Removal** — declare the base triangles the part replaces. Three tools, one set:
   `Pick Triangles In Scene` arms a Scene View tool for individual triangles, the
   **Texture Mask** block converts a black/white texture into a whole region at once
   (white selects, black keeps; 7 samples per triangle, majority of 4; the texture is an
   authoring input and is never saved), and `Remove All In Submesh`, `Add Address`, and
   `Add List` are the keyboard/numeric alternatives. The removal address list collapses to
   `Addresses (N)` and draws at most 28 rows when expanded, so a few thousand addresses
   cannot bury the window; the numeric address field and the mask's apply modes still edit
   the whole set precisely.
7. **UV semantics** — declare which `(semantic name → source channel)` pairs the part
   carries. `Infer From Part Mesh` fills the rows; `APA050` refuses a declared channel
   the mesh does not actually have.
8. **Material semantics** — declare `(semantic name → source submesh → material →
   policy)` rows. `Infer From Materials` fills the rows. The material *asset name* is
   never used as the semantic.
9. **Bones and blend shapes** — set `Merge Armature` (a transient Modular Avatar merge
   configuration generated on the selected part armature and targeting the selected target
   armature, written with no prefix, no suffix, and no inference) and `Allow Part Only
   Shapes`.
10. **Validate** — runs the real validator. **`Dry-Run Assembly`** plans without
    creating a mesh, so you can see whether the part would actually build.
11. **`Save Profile Asset`** — writes the `ApaPartProfile` (schema version 4). An
    existing asset at the path is only replaced after an explicit overwrite
    confirmation.
12. **`Create Part Prefab`** — generates the portable prefab and adds/updates its
    `AvatarPartInstaller`. `Update Installer On Prefab` re-points an existing prefab at
    the current profile.

The `AvatarPartInstaller` inspector shows the same information the build uses — part
identity, slot, schema version, signature state, target path, removal/seam/semantic
counts, the resolved target — and offers `Validate`, `Create Profile Asset…`,
`Open Profile`, and `Edit In Part Authoring` shortcuts.

Nothing in the authoring flow writes to an asset before `ApaAuthoringValidation`
passes, no existing asset is replaced without explicit confirmation, and every edit
that touches author data goes through `Undo`.

---

## Armature selection and bone identity

Before M10, a part bone's "identity" was its full path from the avatar root; that path
depended on where the part happened to sit, which is a placement detail rather than a
property of the joint. The identity is now scoped by two armatures you select explicitly:

| Selection | Path recorded relative to | Normalized value | Constraint |
| --- | --- | --- | --- |
| Target Armature | the avatar root | `ApaAvatarPath.Root` (`.`) means the avatar root itself | must be the avatar root or one of its descendants |
| Part Armature | the part root | `.` means the part root itself | must be the part root or one of its descendants |

- At build time both roots are resolved, and each renderer's bones are recorded as paths
  **relative to the armature selected for its own side**; two bones whose relative paths
  are byte-identical are the same joint and merge. A bone that *is* the armature root
  records `.`. The full outer path from the avatar root to a part bone is no longer a bone
  identity.
- The two selections must therefore be the **corresponding bone levels** of the body and
  of the part: whichever level of the body's armature the target armature names, the part
  armature must name the same level of the part's. A position and a name are guesses; the
  level correspondence is a statement the author makes, which is exactly why two selections
  replaced the name heuristics.
- Every installer that shares one body must select the **same** target armature, or the
  group blocks with `APA043 reason=conflicting-target-armature-paths`.
- The generated Modular Avatar merge component is added to the **selected part armature**
  and targets the **selected target armature**, and it is always written with an empty
  prefix, an empty suffix, and no inference. Modular Avatar's exact-name matching then
  describes the same relation as the armature-relative identity instead of adding a second
  heuristic beside it.
- The legacy `MergeTargetPath`, `MergePrefix`, `MergeSuffix`, and `InferMergeNames` fields
  still exist and still round-trip in an asset, but the build no longer reads any of them
  and the window no longer shows those controls. They are kept so an old asset still loads,
  not as a fallback path.

### Armature-selection diagnostics

| Code | Condition | Stable `reason=` tokens |
| --- | --- | --- |
| `APA043 ARMATURE_SELECTION_INVALID` | the selection is missing, its recorded path does not resolve, or the selected object is outside the root it must belong to | `missing-target-armature`, `missing-part-armature`, `target-armature-not-found`, `part-armature-not-found`, `target-armature-outside-root`, `part-armature-outside-root`, `conflicting-target-armature-paths` |
| `APA044 BONE_OUTSIDE_SELECTED_ARMATURE` | a bone that **carries weight** is not inside the armature selected for its renderer | `reason=bone-outside-armature` |

Only **weighted** bones are checked: a bone slot no vertex references above the threshold —
including a `null` entry — is not required to have an identity. A weighted bone outside the
selected armature is a real defect, because its path cannot be recorded relative to the
armature, so a part bone could never merge with the body bone it belongs to and the weights
would follow an appended duplicate instead.

---

## Current capabilities

### What the plugin actually does

- **Validates** an assembly configuration and reports stable, ordered diagnostics.
- **Plans** an assembly into an immutable `MeshAssemblyPlan`: retained base vertices,
  part vertices, the seam weld map, the vertex remap, UV channel layout, material slot
  layout, final index buffers, the final bone table, and the final blend shape set.
- **Groups** the installers by resolved target renderer and plans/validates **every**
  group before **any** group is mutated. All-or-nothing: a blocking issue in one group
  fails the whole avatar, so a half-installed avatar cannot exist.
- **Builds** exactly one generated `Mesh` per target group from that plan, with
  positions written in the target renderer's local space, normals transformed by the
  inverse transpose, tangent handedness preserved, and recomputed bounds.
- **Welds** seams truly: a part seam vertex is deleted and its triangles are rewritten
  to the retained base vertex. No duplicate seam vertices are emitted.
- **Merges** UV channels and material slots by semantic name, independently of Unity
  channel indices and of material asset names.
- **Skins** the result: every retained vertex's `BoneWeight` is remapped onto one final
  bone table (the body's own bones first, then new part bones in canonical part order).
  Bind poses come from each source mesh's authored rest-pose bind data and are converted
  into the target renderer's local basis, so moving a bone in the editor cannot become a
  new bind pose. Legacy snapshots without source bind data retain the live-transform fallback.
- **Remaps blend shapes**: every frame of every shape, over every retained vertex, for
  position, normal, and tangent deltas.
- **Registers with NDMF**: a `Generating` pass creates the transient Modular Avatar
  merge-armature configuration; a `Transforming` pass (after
  `nadena.dev.modular-avatar`) assembles, verifies that the merge actually happened,
  and writes the result to the clone.
- **Previews in the Scene View** through NDMF's render-filter pipeline, over the same
  capture/validate/plan/build code path the build uses, with a content fingerprint and
  a bounded mesh cache.
- **Blocks** anything it cannot preserve, rather than producing a plausible-looking
  mesh that breaks later.

### Repository layout

```text
Runtime/                     dev.avatar-part-assembler.runtime   (no Editor dependency)
  Common/                    stable codes, severity, numeric policy, name rules, path vocabulary
  Components/                AvatarPartInstaller — the data-only component a user adds
                             Profiles/                  ApaPartProfile and its serializable parts (schema v5)

Editor/                      dev.avatar-part-assembler.editor    (Editor only)
  ApaCore.cs                 the single core facade
  Localization/              the language preference, the Simplified Chinese table, enum display
                             names, and the diagnostic labels (Editor-only; no package dependency)
  Input/                     snapshots, the Unity-object reader, target-group discovery
  Validation/                issues, results, rules, the validator
  Resolution/                UV and material semantic resolvers
  Assembly/                  seam resolver, planner, plan, mesh assembler, final bone table,
                             blend shape merge plan, skinning providers, TargetGroupAssembly
  Integration/               the only file that names a Modular Avatar type
  NDMF/                      dev.avatar-part-assembler.editor.ndmf
                             plugin, Generating/Transforming passes, build processor,
                             transient-artifact ledger, NDMF diagnostics
  Preview/                   dev.avatar-part-assembler.editor.preview
                             render filter, node, fingerprint, cache, discovery, diagnostics, overlay
  Authoring/                 the Part Authoring window and everything it calls

Tests/Editor/                dev.avatar-part-assembler.tests.editor (UNITY_INCLUDE_TESTS)
```

`Runtime` has no UnityEditor, NDMF, or Modular Avatar dependency, so a built part
prefab is self-describing and its authoring data survives outside the Editor. That is
why `BoneSignature` and everything the profile serializes live in `Runtime`.

> **Two files to read before you trust this layout.** The folder tree is the release
> candidate's layout, but the **assembly definition references** are the one part of the
> package that the integration session had to change last: `editor.ndmf` needs a
> reference to `editor.preview` so it can name the render filter, and the test assembly
> needs references to both so it can see their types. Check
> `Editor/NDMF/dev.avatar-part-assembler.editor.ndmf.asmdef` and
> `Tests/Editor/dev.avatar-part-assembler.tests.editor.asmdef` in the tree you are
> building. If either reference is missing, the preview is not registered and
> `USER_ACCEPTANCE_CHECKLIST.md` rows 1.2, 1.3, and 3.1 fail.

### The pipeline

```text
resolve target groups  ->  discover active installers (avatar-scoped)
   ->  compatibility check  ->  immutable snapshots  ->  per-group validation
   ->  per-group AssemblyPlan  ->  one transient Mesh per group  ->  write to the clone
```

Planning completes **before** any mutation — for every group, not just the current one.
That is what makes "no partial result on failure" structural rather than a matter of
discipline.

### The one core entry point

Everything goes through `ApaCore`:

```csharp
var result = ApaCore.Assemble(validationContext);
if (!result.Succeeded)
{
    // result.Issues is complete, ordered, and contains at least one error.
    // result.Mesh is null. Nothing was modified.
}

// On success the caller owns result.Mesh and must destroy it.
```

`ApaCore.Validate` and `ApaCore.Plan` expose the earlier stages for a validator UI, and
`ApaCore.PlanGroups` / `AssembleGroups` expose the grouped form. The build path adds one
layer above the core — `TargetGroupAssembly.Plan` then `TargetGroupAssembly.Assemble` —
which is the entry point the NDMF build processor and the preview both must consume.
Preview and build must never grow a second processor: two implementations kept in sync
by discipline is exactly how "looks right in Unity, wrong after upload" happens.

### Deterministic ordering

No output byte may depend on dictionary iteration, hash-set order, or
`GetComponentsInChildren` scan order. The fixed order is:

```text
target groups     ascending ordinal group key (the resolved target renderer's
                  avatar-root-relative path)
installers        (stable part id, avatar-root-relative installer path)
removed triangles ascending (submesh index, triangle index within submesh)
base vertices     ascending original index
part vertices     (part order, then original index)
material slots    base submesh order first, then new part semantics
UV channels       base semantic order first, then new part semantics
bones             body bone order first (including a body-declared path only a part
                  weights), then new part bones (part order, source order)
blend shapes      body shape order first, then new part shapes (part order, source order)
issues            (phase, code, part identity, source index, detail)
```

A part's ordering key is its **serialized stable part id**, never a hierarchy path,
because a path changes when an object is reparented and the ordering must not. The path
is the final tiebreaker. Legacy display names, slots, slot modes, and conflict priorities
are retained only for old asset deserialization and do not affect ordering or validation.

### Seam generation and matching

The seam is produced by **one action** from world positions and stored as **explicit
pairs**: both meshes are read in their **rest pose** (`sharedMesh.vertices` transformed by
`transform.localToWorldMatrix`, never `BakeMesh`'s current animated pose), and a uniform
spatial hash finds every world-coincident pair within a tolerance stated in **world
units**.

| Property | Rule |
| --- | --- |
| Tolerance | default `1e-4` (0.1 mm); the window accepts `1e-7` through `1e-3`, and refuses larger values. World units, because a seam is authored in world space |
| Pairing | `Base.VertexIndices[i]` and `Part.VertexIndices[i]` are one weld; the result *is* the pairing, so no "pair the two unordered sets by position" step is left |
| Determinism | part vertices are visited in ascending index order and each takes the nearest still-free target vertex; ties go to the lower target index |
| One-to-one | no target vertex is claimed twice (`APA003 reason=duplicate-base-claim`) |
| No match | when no part vertex is within the tolerance of any target vertex, the generator reports `APA002 reason=no-world-coincident-vertices` instead of a build-time search |

The build only **consumes** those pairs: `SeamResolver` verifies the index sets, the
cardinality, the finiteness of the paired positions, and the one-to-one base claims, and
performs no search at all. An avatar-root-local epsilon is a different world distance on
every scaled level, and a position search is a guess whenever two vertices sit close
together.

The pairing carries a version: `PairingVersion` is `0` for the pre-M10 unordered sets and
`1` for explicit pairs. A profile written before M10 keeps `0` and is refused with
**blocking `APA042 SEAM_PAIRING_REQUIRED` (`reason=seam-pairing-required`)** until the seam
is regenerated once. The window shows the tolerance, the pair count, and a short preview,
with Clear and Regenerate; it does not list hundreds of vertices.

Picking the base and part seam vertices by hand, the two index-list text fields, and the
two Scene View picking modes are gone: they asked the author to maintain one
correspondence across two spaces, which is exactly the correspondence the tool computes in
one action.

Equality is never used for floats: the generator compares squared world distances against
the tolerance, and the position and UV epsilons used elsewhere keep their `1e-5` defaults,
settable through `ApaNumericPolicy`, which is a developer setting rather than a
user-facing option.

### Attribute ownership at a weld

The retained **base** vertex owns position, normal, tangent, color, and skin weight.
UVs are the one exception, because a semantic may exist on only one side:

```text
semantic on both sides   -> values must agree within epsilon, else APA004; base value used
semantic on base only    -> base value used at the weld
semantic on part only    -> part value used at the weld
```

A vertex missing a semantic receives `Vector4.zero` — all four components defined,
which matters for dimension-4 UV sets such as lightmaps.

`RecalculateNormals` is **never** called. Toon avatars depend on authored normals, and
the product principle is that the assembler assembles rather than repairs. If a source
lacks normals or tangents, the output lacks them and a warning says so.

### Non-destructive guarantee

All validation and planning happens on **immutable snapshots**. The pipeline reads a
Unity mesh exactly once, copies what it needs into plain arrays, and never writes back.
Transforms are copied the same way: the space matrices are captured as values at
context build time and no live `Transform` is retained, so a hierarchy change after
validation cannot alter a plan or a build. Public entry points return an explicit
result; on failure they return a null mesh and a complete issue list, never a partial
result. A generated mesh is always a new transient object, assigned only to the NDMF
build clone or to a preview proxy — never to an authoring asset.

Two more properties the pipeline maintains deliberately:

- **The pipeline never mutates the profile asset.** `ApaPartProfile` is a
  `ScriptableObject` shared with the authoring scene, so the pipeline reads the
  non-mutating `*OrNull` accessors and never calls `EnsureInitialized`, rewrites a
  schema version, or materializes a nested object on read.
- **Disposal of what a run did not use.** `TargetGroupAssemblyResult` owns every mesh
  it generated; the caller marks the ones it assigned and releases the rest, so a
  failed or partially consumed run is leak-free without the core having to guess when a
  renderer stopped using a mesh.

---

## Multi-target and priority policy

### Target groups

A real avatar can legitimately carry parts welded to a body renderer *and* parts welded
to a separate clothing renderer. Each resolved renderer is a **group**, and a group has
its own context, plan, and mesh, because slot uniqueness, removal overlap, the UV
layout, the material layout, the bone table, and the blend shape catalog are all
group-scoped.

| Situation | Result |
| --- | --- |
| One target renderer | One group, one plan, one mesh |
| Two renderers, each named explicitly by different installers | Two groups, two plans, two meshes; group key is the renderer's avatar-root-relative path |
| Two renderers, one reached through the profile's recorded `RendererPath` | Two groups; a recorded path is a valid target |
| Two installers **in one group** naming different renderers | Blocking `APA006 reason=conflicting-target-renderers`, no context for that group |
| Two installers **in one group** whose profiles select different target armatures | Blocking `APA043 reason=conflicting-target-armature-paths`: a group has one bone identity scope, so it cannot have two |
| An installer whose explicit target disagrees with the target its profile recorded | Blocking — that is one assembly naming two bodies |
| Explicit target null, profile `RendererPath` empty | Blocking `APA006 reason=missing-target-renderer` |
| A recorded path that resolves to a non-`Renderer` or to nothing | Blocking `APA006 reason=missing-target-renderer` |
| Group A valid, group B invalid | **The whole avatar fails.** The report carries both groups' issues; group-scoped issues carry `group=<path>` in the detail |
| No active installer at all | "Not applicable": nothing is validated, nothing is mutated, nothing is reported against the avatar |
| Two renderers sharing one mesh asset, no explicit targets | Blocked rather than welded into an arbitrary one of them |

### Installer activity

One predicate decides whether an installer participates, and preview and build both
call it (`AvatarPartInstaller.IsActiveForBuild`):

| Condition | Effect |
| --- | --- |
| Component disabled, GameObject inactive, or `EnabledForBuild` false | The part is **not** installed, deletes no body triangles, and is reported as Info `APA039 reason=inactive-installer` with which of the three conditions held |

Reporting a parked part is deliberate: a silently ignored part looks exactly like an
installed part.

### Legacy identity policy fields

Older versions serialized display name, slot, slot mode, and conflict priority in the
part identity object. These fields are now ignored. New authoring exposes only the stable
part ID; old fields remain as a read-only compatibility shim so existing profiles can be
opened and rewritten without losing unrelated data.

---

## Removal, UV, material, blend shape, and bone policies
| S4 | Non-`Custom` slot, one `Replace` + N `Augment` | OK |
| S5 | Non-`Custom` slot, N `Augment`, no `Replace` | OK — nothing claims exclusive ownership |
| S6 | An `Augment` part that declares a non-empty removal set | Blocking `APA013 reason=augment-declares-removal`. An augmenting part may still weld, merge UV and material semantics, contribute bones, and contribute blend shapes |
| S7 | Slot value outside the defined list | Blocking `APA023 INVALID_PART_SLOT` (also how an undefined `SlotMode` value is reported) |

### Removal

| Situation | Result |
| --- | --- |
| A removal address inside its submesh | That triangle is removed, and only that triangle |
| A non-negative triangle index outside its submesh | Blocking `APA017 REMOVAL_INDEX_OUT_OF_RANGE` |
| A malformed address (negative submesh/triangle, or a non-triangle submesh) | Blocking `APA026 INVALID_TRIANGLE_ADDRESS` |
| Two parts declare the same base triangle | `APA010` (or `APA035` when an explicit priority resolves it — see the priority table) |
| A seam vertex whose triangles are all removed | The seam vertex **survives**: it is a weld target, not removable geometry |

#### Black/white mask selection

Picking removal triangles one at a time does not scale to a region of a few thousand
triangles. The **Texture Mask** block in the Removal Region section converts a black/white
texture into the removal set through the target mesh's UVs:

| Setting | Meaning |
| --- | --- |
| Mask Texture | A `Texture2D` sampled through the target mesh's UVs |
| UV Channel | 0–7, default 0 — the channel the mask was painted against |
| Threshold | 0–1, default 0.5 — brightness at or above which a sample counts as selected |
| Invert | Swap the meaning of white and black |
| Apply Mode | Replace Selection (default), Add To Selection, Subtract From Selection |

**Which color means what.** With Invert off, **white selects the triangle for removal and
black keeps it** — the intuitive reading of "paint the region you want gone". Invert reverses
it. The brightness of a sample is its **RGB luminance** (`0.299 R + 0.587 G + 0.114 B`,
Rec.601, applied to the texture's stored 8-bit values); **alpha is ignored**, so a
fully transparent white pixel and an opaque white pixel select the same triangles.

**The sampling rule is fixed, not configurable.** Every triangle of every triangle-list
submesh gets **7 sample points** — the three vertex UVs, the three edge midpoints, and the
centroid — and the triangle is selected when **at least 4 of the 7** samples are at or above
the threshold. A majority vote resolves a mask boundary to roughly a quarter of a triangle
and cannot be decided by one unlucky corner. Nothing else about the conversion is a setting,
so the same picture always selects the same triangles. Sampling honors the mask's own import
settings: bilinear or nearest by the texture's filter mode, with the texel position derived
from the UV exactly as the hardware derives it (texel centers at `(i + 0.5) / size`), and the
texture's wrap mode addresses the integer texel indices — repeat wraps the index around the
edge, so a sample at `u = 0` interpolates the last and the first texel; mirror reflects the
index through a doubled period; clamp and `MirrorOnce` stop at the border texel.

**No Read/Write Enabled, and no importer changes.** The tool never asks the author to change
the mask's import settings and never writes them. It reads the mask with one path and one
path only: the texture is blitted into a temporary render texture and read back from there,
which is exactly what decodes an imported, non-readable, or block-compressed mask without
touching the asset. There is deliberately no device-side `CopyTexture` shortcut — such a copy
updates the GPU's copy but not the CPU buffer a pixel read returns, which can silently produce
a correctly sized, all-black mask — and the format is never inspected before the attempt: if
the device cannot render and read a particular source, the conversion reports
`reason=texture-readback-failed` rather than refusing the format up front. Every temporary
object is released whether the conversion succeeds or refuses, so a failure leaks nothing.
The mask texture and the mesh are only ever read.

**Only the triangle set is saved.** The texture is an *authoring input*, not a build
dependency: it is never stored in `ApaPartProfile`, never referenced by the prefab, and never
needed to rebuild the part. What the conversion produces is the same canonical
`RemovedTriangleAddress` set the Scene View picker produces, canonicalized by
`ApaRemovalMask` and written to the profile as submesh index plus triangle index within that
submesh. Move the texture, delete it, or change its import settings afterwards and every
already-authored part is unaffected.

**Feedback, and the other two tools.** Apply is disabled while the mask cannot run — no
target mesh, no mask texture, an unreadable target mesh, or a UV channel the mesh does not
carry — and that reason is printed next to the button, so a disabled Apply always says what to
fix. The status line reports the selected count and how many triangles the conversion
considered; an empty generated set is a *valid* result and is reported as one (under Replace
it deliberately clears the selection, which is how an author says "this mask defines
nothing"). A conversion that cannot run — unreadable mesh, absent or mismatched UV channel,
non-finite or out-of-range threshold, unsupported topology, or a readback the device refused —
stops with **blocking `APA041 REMOVAL_MASK_TEXTURE_FAILED`**, and the whole diagnostic (code,
mnemonic title, message, and the stable `reason=` token) is rendered in the Texture Mask block
where Apply was pressed, and again in the write section's "Last write reported" list. The
existing selection is left untouched. The Scene View picker and the numeric address list
remain available throughout: the mask is for the bulk of a region, the picker for the two
triangles it got wrong, and the list for an exact, reproducible entry.

Applying a mask is **one undo record** — one Apply, one Undo, whichever mode ran.

#### Removal address list (collapsed)

A real removal region can hold a few thousand triangle addresses, and drawing all of them
turns the window into a spreadsheet. The list therefore collapses to `Addresses (N)`:
expanded, it draws at most **28 rows**, each still removable individually, with a summary
line for the rest (the summary also states that the numeric address field and the mask
modes edit the whole set). The global `MaxListedRows` (200) shared with the other lists is
deliberately **unchanged** — it is the display bound of the other lists, and lowering it
would only lose information elsewhere. The list is for review and single corrections; bulk
corrections are the numeric address field and the texture mask's Replace / Add To Selection
/ Subtract From Selection modes.

### UV

| Situation | Result |
| --- | --- |
| Same semantic in different source channels | Merges into one final channel; the channel index is not the identity |
| Same semantic, different values at a welded vertex | Blocking `APA004 SEAM_UV_MISMATCH` with the reported difference |
| Multiple parts contribute different values for one part-only semantic at one welded vertex | Blocking `APA025 WELD_UV_CONFLICT`. There is **no priority escape hatch**: a welded vertex is one output vertex holding one value per semantic, so a "winner" would silently overwrite another author's mapping |
| A present source channel that the profile neither declares nor implicitly names | Blocking `APA036 reason=undeclared-present-channel`, listing the present, declared, and undeclared channels |
| A present channel 0 suppressed because a non-empty partial declaration does not claim it | Blocking `APA036 reason=implicit-uv0-suppressed-by-declaration`. Channel 0 is implicitly named `UV0`; without this check the values would be silently replaced by the channel default |
| A part with only channel 0 and no declaration | Accepted — the convenience path |
| More than eight final channels | Blocking `APA005 UV_CHANNEL_OVERFLOW` |
| A duplicate semantic in one source | Blocking `APA019 DUPLICATE_SEMANTIC` |
| An empty/whitespace semantic name | Blocking `APA020 INVALID_SEMANTIC_NAME` |
| An authoring declaration of a source channel the part mesh does not carry | Refused by the authoring layer as `APA050 UV_SEMANTIC_CHANNEL_ABSENT` |

Semantic names are trimmed and compared **ordinal** (case-sensitive): `Skin` and
`skin` are different semantics. They are never case-folded.

### Materials

`Auto`, `UseTarget`, `KeepPart`, and `ForceNew` are per-semantic. The base slot for a
semantic is the **anchor** whenever the base declares it.

| Policy | Base has the semantic | Base does not have it |
| --- | --- | --- |
| `Auto` | Same material asset → merge into the base slot; different asset → blocking `APA009 reason=base-part-material-conflict` | The anchor becomes the lowest-ordered part contributor that is also `Auto`: no anchor yet → create it; same material → merge; different material → blocking `APA009 reason=part-part-material-conflict` naming both parts and both materials |
| `UseTarget` | Part geometry is redirected into the base slot | A **new** slot is created carrying the **part's** material (`reason=use-target-without-base-semantic`) |
| `KeepPart` | The part keeps its own additional slot | Same — its own slot |
| `ForceNew` | The part always gets its own additional slot | Same — its own slot |

Two more rules the table implies:

- A `KeepPart`/`ForceNew` slot is recorded as *explicitly separated* and is never used
  as an `Auto` anchor. If an `Auto` contribution arrives for a semantic that has
  already been deliberately split and the base does not declare it, that is blocking
  `APA009 reason=no-auto-anchor`: `Auto`'s contract is "merge by semantic", and there is
  nothing legitimate left to merge into. The remedy is to make the later part explicit
  too.
- A source submesh that produces **no** final material slot is blocking
  `APA038 SUBMESH_WITHOUT_MATERIAL_SLOT` — a different condition from `APA009`, so it
  is a different code. The authoring window reports the same condition under the same
  code as an `APA038` **Warning** (`reason=declared-submesh-out-of-range`): the
  pre-check runs while editing, where the final slot layout is not yet known, and the
  final core validation blocks with `APA038` once it is. The pre-write guard means a
  profile that would block cannot be saved as if it were valid.

The base renderer's own materials are **never** mutated or replaced; they are
referenced, and the final slot list is attached to the generated mesh's renderer. A
material asset name is never parsed, pattern-matched, or used as a semantic.

### Blend shapes

| Situation | Result |
| --- | --- |
| A shape on the base only | Survives at retained base vertices; deltas at removed vertices vanish with them |
| A shape on a part only, all seam deltas exactly zero | Accepted (unless `AllowPartOnlyShapes` is false — see below) |
| A shape on a part only, a non-zero seam delta | Blocking `APA029 reason=part-only-seam-delta` |
| A shape on a part only while the profile sets `AllowPartOnlyShapes` false | Blocking `APA040 PART_ONLY_SHAPE_DISALLOWED reason=part-only-shape-disallowed`, naming the shape and the policy value. `APA029` keeps its single meaning |
| Same-named base/part shapes | Merge; frame count and every frame weight must be **exactly** equal, else blocking `APA028 BLENDSHAPE_FRAME_MISMATCH`; seam deltas must agree within the position epsilon in the target renderer's local space, else `APA029` |
| Same name twice in **one** mesh | Blocking `APA027 BLENDSHAPE_DUPLICATE_NAME`. Unity addresses a shape by name at animation time, so two shapes with one name are not two shapes, and merging them would pick a winner |
| A frame whose delta array is not one entry per vertex | Blocking `APA030 INVALID_BLENDSHAPE_DELTA` |
| A non-finite delta | Blocking `APA016 NON_FINITE_VALUE` |
| A shape with zero frames | Kept in the plan for ordering and diagnostics; writes no frames (Unity has no frame to attach the name to) |

Frame data is never policy-resolvable. A frame index is a position in an array, not an
identity, so merging two shapes whose frames disagree would silently re-target an
animation curve.

The same-named comparison happens in the **target renderer's local space**, with the
per-kind operator below. Part-local and base-local deltas live in different bases;
comparing them raw would reject a rotated part that authors an equivalent motion and
could accept one whose transform moves the surface elsewhere.

### Bones

| Situation | Result |
| --- | --- |
| Final table order | Target body bones first in the body renderer's own order, then new part bones in canonical part order and source bone order |
| A part bone and a body bone whose paths relative to their own selected armatures are byte-identical | The same joint: the part bone **redirects onto the body's bone**. The body's bind pose, world transform, and final Transform are authoritative and the part's own bind transform is discarded, whether or not a body vertex weights that bone; the redirect is reported once per part as `APA007 reason=part-bone-remapped-to-target` |
| A bone the body's signature **declares** but no body vertex weights, weighted by a part | The body still owns it: its entry is created from the body's own transform, owner, and source bone index before any part is processed, and the part redirects onto it. A path the body declares is the same joint as a part bone with that path |
| Two body bones sharing one path, when a part weights that path | Blocking `APA008 reason=duplicate-bone-identity`: there is no single body bone the weights could follow |
| Two body bones sharing one path that no weight reaches | **Not blocking** and not in the table — the same "unreferenced slots are not defects" rule |
| A bone that carries weight and has no identity (an empty path — which is what a null bone entry records) | Blocking `APA008 reason=bone-without-identity` |
| A bone that carries weight but is not inside the armature selected for its renderer | Blocking `APA044 BONE_OUTSIDE_SELECTED_ARMATURE reason=bone-outside-armature` |
| A bone slot no vertex references above the weight threshold (including a `null` entry) | **Not blocking**, and it does not enter the final bone table; its remap entry stays `-1` |
| A weight referencing a bone outside its own source's bone list, or skin data with no captured bone signature | Blocking `APA007` |
| A weight array that is not one entry per vertex, a negative weight, or a vertex whose weights sum to zero | Blocking `APA031 INVALID_BONE_WEIGHT` |
| A vertex whose whole weight set is at or below the weight threshold | Blocking `APA031 reason=zero-weight-sum` |
| A bone whose world-to-local transform is missing or unusable, or an unusable computed bind pose | Blocking `APA011 INVALID_BINDPOSE` |
| An unskinned input | Assembles successfully with no bone table |
| A welded vertex | Keeps the retained base vertex's skin weight by construction |

The weight threshold is `ApaNumericPolicy.WeightEpsilon` (a new policy value, default
`1e-5`), and the same threshold decides both "which bones need an identity" and "which
influences survive the remap": an influence at or below it is cleared to index 0 with weight
0, so "the source is valid" and "the remap can resolve every surviving influence" cannot
disagree. Unreferenced slots — including `null` ones — no longer need an identity; before
M10 an empty slot blocked the whole part with `APA008` while affecting no vertex at all.

A bone that *is* its selected armature root records the normalized token `.`, so the
armature root merges like any other joint. M2 alone could not merge armatures, so a part
bone colliding with a body bone while describing a different transform blocked with
`APA008`; M3 removed that case by merging the armature first and rebuilding the table from
the post-merge clone, and M10 removed the remaining path dependence by scoping the
identity to the armature the author selects.

**A different transform is never an ambiguity.** Since M11 the body is authoritative for
every path its signature declares, so an identical armature-relative path means the same
joint and the part's own bind transform is discarded rather than compared — a part authored
somewhere else in the scene no longer blocks and no longer produces a duplicate bone. The
same rule covers a path no body vertex weights: the body's entry is created from the body's
own transform before any part is processed, so preview and the NDMF build resolve one
identity to one Transform. `APA008` therefore survives only for conditions that really are
ambiguous: a weighted bone with no identity, two weighted bones of one source sharing a
path, and two body bones sharing a path that a part weights.

### Blend shape delta transform operators

```text
position delta    linear part of source→target   (an offset must not receive the translation column)
normal delta      inverse transpose
tangent delta     linear part                    (a tangent's xyz is a direction, not a covector)
```

Using the normal matrix for a tangent is a quiet defect: the two operators agree under a
uniform scale and diverge under a non-uniform one.

### Merge-name policy

Modular Avatar merges a part bone into a target bone by byte-identical name (optionally
with a prefix or suffix applied), and Modular Avatar's own build path does not infer these
values. The M10 merge component is therefore always written with an **empty prefix, an
empty suffix, and no inference**: with the two armatures selected, exact-name matching is
the same relation the armature-relative identity describes, so nothing has to be derived
from names at all. A part whose bone names are not identical to the target's merges
nothing and appends every part bone as a new bone, which is a wrong bone table rather than
a silent policy — and the two selections are the declaration that makes it visible.

`MergePrefix`, `MergeSuffix`, and `InferMergeNames` remain on `ApaBoneProfile` and still
round-trip in an asset, so a profile written by an earlier build loads unchanged. The build
no longer reads them and the authoring window no longer shows them.

---

## Build and preview flow

### NDMF passes

```text
Generating     ApaMergeArmaturePass  "Create transient merge-armature configuration"
               - one transient ModularAvatarMergeArmature per part that needs one
               - records each configuration and the bone the merge must move

Transforming   (nadena.dev.modular-avatar runs here)
               ApaAssemblyPass       "Assemble part geometry into the target body mesh"
               - declared AfterPlugin("nadena.dev.modular-avatar")
               - returns immediately if the build has already failed
               - returns immediately if the avatar has no active installer
               - verifies the merge postcondition, then processes
               - records the consumed part renderer objects → their group's target objects

               ApaAnimatorRetargetPass "Retarget consumed part animation onto the target renderer"
               - requires AnimatorServicesContext, so NDMF opens it for this pass
               - ObjectPathRemapper.ReplaceObject(source, target) per recorded pair
               - the context's deactivation commits the mappings into the generated clips

               (nadena.dev.modular-avatar.late-transform-stages runs here)
               ApaEmptySourceCleanupPass "Remove consumed part objects left empty"
               - ordered after the late transform stages and after the retarget pass
               - removes a recorded source object only when ApaSourceObjectCleanup says it is empty
```

Four properties of that arrangement are deliberate:

- **The merge is verified, not assumed.** Modular Avatar destroys a configuration it
  *skipped* exactly like one it merged, so the component's absence is only half the
  check. The pass additionally requires that the part's top bone has left the part root.
  If not, the build blocks with `reason=merge-not-applied` (or
  `reason=merge-not-consumed` when the configuration is still alive, which means Modular
  Avatar's merging pass did not run at all). Assembling otherwise would ship a part whose
  bones are not part of the avatar's armature — correct at rest, detached the moment the
  avatar animates.
- **A build that already failed is not mutated.** `BuildContext.Successful` false is the
  single gate, so no second bookkeeping flag can disagree with NDMF's own report.
- **An avatar with no enabled installer is not touched and not reported against.** The
  gate is the same installer discovery the processor uses, so this package cannot turn
  an unrelated avatar's build red.
- **Ordering is declared, not incidental.** `AfterPlugin` is by qualified name
  (`"nadena.dev.modular-avatar"`) because Modular Avatar's plugin class is internal to
  its own assembly. NDMF constraints are phase-local and a missing target is optional, so
  the constraint orders the passes when Modular Avatar is present and does nothing when
  it is not. "Before the optimizers" is expressed by the phase: every `Transforming` pass
  runs before every `Optimizing` pass, which `BeforePlugin` cannot express across phases.

The transient merge configuration is added to the **selected part armature** and targets the
**selected target armature**, and it is always written with an empty prefix, an empty
suffix, and no inference: Modular Avatar's byte-identical-name matching then reproduces the
relation the armature-relative identity describes, rather than adding a name heuristic
beside it.

All four passes are VRChat-avatar-only, which is NDMF's default for a plugin without
`RunsOnAllPlatforms`. The two steps that finish the assembly declare their own ordering as
well — the retarget step requires the animator services context and the cleanup step is
declared after Modular Avatar's late transform plugin and after the retarget step; see
[Modular Avatar Merge Animator compatibility](#modular-avatar-merge-animator-compatibility).

### Preview

Preview is an NDMF render filter attached to the **same** `Transforming` pass:

```text
seq.Run(ApaAssemblyPass.Instance).PreviewingWith(ApaPreviewRegistration.CreateFilter())
```

- One proxy renderer per target group; the node writes the assembled mesh, materials,
  bones, and blend shape weights **onto the proxy** the pipeline hands it. The original
  renderer's mesh, materials, hierarchy, and profile assets are never written to.
- **Blocked means absent.** When the current inputs are invalid the filter returns no
  group for that renderer, so NDMF has no proxy: the original body renders unchanged and
  a previously successful preview cannot survive an invalid edit. The reason is reported
  as a diagnostic.
- Cache keys are a content **fingerprint** (`apa-preview-fnv1a64-v2`, FNV-1a over exact
  scalar representations, never process-randomized hashing) covering the captured input
  set, each captured mesh attribute by attribute, the profile serialization, the
  transforms, the material identities, and the numeric tolerances. Live bone pose matrices
  are excluded because proxy bones remain live and pose edits must not rebuild bind poses.
  Failures are **not**
  cached; an uncached build is retried.
- The mesh cache is bounded (default capacity 4, plus live leases), every evicted mesh is
  destroyed immediately, and the whole cache is destroyed before an assembly reload, on
  quit, and on a play-mode transition.
- The overlay is a separate switch and starts **off**: a preview that painted extra
  geometry by default would misrepresent the result it is previewing.
- Both switches appear under *Configure Previews* as
  `dev.avatar-part-assembler/preview/Main` and
  `dev.avatar-part-assembler/preview/DebugOverlay`.

The overlay draws removed base triangles in red and seam correspondences in yellow as a line between the two
authored positions, so the **line length is the match error** and a perfect weld draws as a cross at the shared
position. It is bounded (at most 4000 primitives per category, at most 8 label lines), writes nothing, and draws
from captured snapshots and the immutable plan rather than from live mesh reads.

The preview layer is `dev.avatar-part-assembler.editor.preview` and deliberately does
**not** reference `dev.avatar-part-assembler.editor.ndmf`; the registration lives in the
NDMF assembly, so the two assemblies stay acyclic.

### Assembly layout (assemblies and their references)

```text
dev.avatar-part-assembler.runtime
    (no package references)

dev.avatar-part-assembler.editor                      -> runtime, nadena.dev.modular-avatar.core
dev.avatar-part-assembler.editor.preview              -> runtime, editor, nadena.dev.ndmf
dev.avatar-part-assembler.editor.ndmf                 -> runtime, editor, nadena.dev.ndmf
                                                         (and editor.preview, to name the filter)
dev.avatar-part-assembler.tests.editor                -> runtime, editor, ndmf, preview,
                                                         UnityEngine.TestRunner, UnityEditor.TestRunner
                                                         (Editor-only, UNITY_INCLUDE_TESTS)
```

The direction is what keeps the graph acyclic: `ndmf` may reference `preview`, and
`preview` must never reference `ndmf`. Preview owns the filter; NDMF owns the
registration.

---

## Installed NDMF and Modular Avatar API attribution

This package links against, but does not redistribute, the following packages. They are
resolved by VPM at install time and remain under their own licenses.

| Package | Version verified against | Role | License |
| --- | --- | --- | --- |
| `com.vrchat.avatars` | 3.10.4 | Target platform | VRChat SDK license |
| `nadena.dev.ndmf` | 1.14.0 | Build lifecycle (Generating/Transforming passes, error report, object registry, preview session and render filters) | MIT |
| `nadena.dev.modular-avatar` | 1.18.0-beta.0 | Transient armature merge (`ModularAvatarMergeArmature`) | MIT |
| `com.unity.modules.animation` | Unity 2022.3.22f1 built-in | Required by the package manifest | Unity Companion License |
| `com.unity.test-framework` | 1.1.x (project dependency) | Runs the EditMode suite | Unity Companion License |

The exact members relied upon, verified by reading the installed sources:

**From `nadena.dev.ndmf` (1.14.0)**

- `[assembly: ExportsPlugin(typeof(...))]`, `Plugin<T>` (`QualifiedName`, `DisplayName`,
  `Configure`), `Sequence InPhase(BuildPhase)`, `BuildPhase.Generating` /
  `BuildPhase.Transforming`, `Sequence.AfterPlugin(string)`, `Sequence.Run(IPass)`,
  `Sequence.PreviewingWith(params IRenderFilter[])`
- `Pass<T>` with `DisplayName` and `Execute(BuildContext)`
- `BuildContext`: `AvatarRootObject`, `Successful`, `ObjectRegistry`, `GetState<T>()`
- `IObjectRegistry.GetReference(Object)`, `RegisterReplacedObject(Object, Object)`
- `ErrorReport.ReportError(IError)`, `ErrorSeverity.{Information,NonFatal,Error}`,
  `SimpleError`
- `nadena.dev.ndmf.localization.Localizer`
- Preview: `IRenderFilter` (`IsEnabled`, `GetTargetGroups`, `Instantiate`,
  `WhatChanged`, `Refresh`, `OnFrame`, `OnFrameGroup`, `Dispose`), `IRenderFilterNode`,
  `RenderGroup`, `ComputeContext`, `TogglablePreviewNode.Create`,
  `PreviewSession.Current` / `AddMutator`

The single call that publishes the filter is
`seq.Run(ApaAssemblyPass.Instance).PreviewingWith(ApaPreviewRegistration.CreateFilter())`.

**From `nadena.dev.modular-avatar` (1.18.0-beta.0)** — all of it in one file,
`Editor/Integration/MergeArmatureGenerator.cs`:

- `nadena.dev.modular_avatar.core.ModularAvatarMergeArmature`, with the public
  `mergeTarget` **field** of type `AvatarObjectReference`, the public `LockMode` field,
  the public `prefix` and `suffix` string fields, the public `InferPrefixSuffix()`
  method, and the public `ArmatureLockMode` enum (`NotLocked`)
- `nadena.dev.modular_avatar.core.AvatarObjectReference.Set(GameObject)`

`prefix`, `suffix`, and `InferPrefixSuffix()` are the merge-name policy **of the API, not of
this package**: the M10 merge component is written with an empty prefix, an empty suffix,
and no inference, so `InferPrefixSuffix()` is no longer called and `InferMergeNames` no
longer has any effect on a build. The three legacy profile fields still round-trip in an
asset so an older profile loads unchanged; the two armature selections decide the merge.

Members that **do not exist** in this version and are therefore not used:
`mergeBones`, `mergeBlendShapes`, `mergeGeometry`, `renameBeforeMerge`, `boneMap`,
`AllowedBones`, and a settable `mergeTargetObject`. The component is
`[ExecuteInEditMode]` and Modular Avatar destroys it after consuming it, so no reference
to it is retained past the pass that created it.

---

## Versioning and schema migration

Two version numbers, never conflated:

| Version | Meaning | Current value |
| --- | --- | --- |
| `ApaPartProfile.SchemaVersion` | The shape of the serialized authoring data | **5** (`ApaPartProfile.CurrentSchemaVersion`) |
| Package version in `package.json` | The shipped build | **0.3.0-rc.6** |

Migration policy:

| From | To | Behaviour |
| --- | --- | --- |
| 1 | 2 | **Refused.** Version 1 stored removal as flat triangle indices with no submesh component and omitted the topology, frame-count, and bone data needed to verify what it references. Neither can be reconstructed without guessing, so the profile must be re-authored |
| 2 | 3 | **Accepted as a no-op.** Version 3 adds `ConflictPriority`, `SlotMode`, and the serialized merge-name policy (`MergePrefix`, `MergeSuffix`, `InferMergeNames`). Every one of them defaults to the version-2 behaviour, so a version-2 profile means exactly the same thing under this build |
| 3 | 4 | **Accepted as a no-op.** Version 4 adds the two armature paths and the seam pairing version. A version-3 profile still loads and is still not rewritten — but it is **refused at build time** by `APA043` (no armature selected) and `APA042` (the seam has no pairing) until it is re-authored |
| 3 → older build | — | Blocks with `APA015 UNKNOWN_PROFILE_SCHEMA`, the fail-closed direction: an older build cannot honour a declared policy it does not understand |
| 4 → a pre-M10 build | — | Blocks with `APA015` |
| newer than 4 | — | Blocks with `APA015` |

`TryMigrate` deliberately **writes nothing**, even for the 2 → 3 and 3 → 4 accepts. The
pipeline may be reading a `ScriptableObject` shared with the authoring scene, and rewriting a
version number on read is a hidden, non-undoable mutation of the author's asset. The
`MinimumMigratableSchemaVersion` is 2.

A version-3 profile's refusal at build time is **not** a migration failure: the schema is
accepted and nothing is rewritten. What is missing is information only the author can
declare — which armature scopes each side's bone identity, and which part vertex welds to
which base vertex. Guessing either is exactly what M10 removed, so the remedy is one pass
through the window: select both armatures, regenerate the seam once, save.

Topology-indexed data — removal triangles, seam indices, per-semantic source channel
mappings — is only valid against the exact mesh it was authored against. The
compatibility signature guards every use of it:

```text
mesh GUID (diagnostic provenance when the mesh is an asset)
→ vertex count → per-submesh index counts and topologies
→ blend shape names and frame counts → bone-path signature
```

The guard **verifies and blocks**; it never rewrites the profile to match whatever mesh
it found. A signature that lacks required safety fields blocks with
`APA024 INCOMPLETE_COMPATIBILITY_SIGNATURE` — distinct from `APA012`, because "the
target is a different mesh" and "this profile was captured before the signature recorded
enough to prove it" have different remedies. A mesh GUID is provenance, never a bypass.

---

## Diagnostic code registry

Stable, documented, searchable. A code's meaning never changes and a retired code is
never reused for a different condition. `ApaErrorCode` is the **single allocation table
for the whole package**, the authoring layer included: it declares every code, renders
every title, and owns the allocation record (`ApaReservedCodes`). `ApaAuthoringErrorCode`
owns no code of its own — it carries aliases to the three authoring-layer constants and
delegates every other code to `ApaErrorCode.GetTitle`, so a code cannot be allocated
twice and cannot acquire two meanings.

Severity decides behaviour: `Error` blocks preview and build; `Warning` and `Info` do
not. Issues are sorted deterministically and exact repeats are deduplicated, so an
identical input produces an identical report — including when the validator and the
planner independently detect the same defect.

### Core codes (`ApaErrorCode`)

```text
APA001 SEAM_VERTEX_COUNT_MISMATCH        APA021 DEGENERATE_OUTPUT_TRIANGLE
APA002 SEAM_POSITION_MISMATCH            APA022 INVALID_EPSILON
APA003 SEAM_DUPLICATE_POSITION_MATCH     APA023 INVALID_PART_SLOT
APA004 SEAM_UV_MISMATCH                  APA024 INCOMPLETE_COMPATIBILITY_SIGNATURE
APA005 UV_CHANNEL_OVERFLOW               APA025 WELD_UV_CONFLICT
APA006 TARGET_RENDERER_NOT_FOUND         APA026 INVALID_TRIANGLE_ADDRESS
APA007 TARGET_BONE_NOT_FOUND             APA027 BLENDSHAPE_DUPLICATE_NAME
APA008 BONE_HIERARCHY_CONFLICT           APA028 BLENDSHAPE_FRAME_MISMATCH
APA009 MATERIAL_SEMANTIC_CONFLICT        APA029 BLENDSHAPE_SEAM_DELTA_MISMATCH
APA010 REMOVAL_REGION_OVERLAP            APA030 INVALID_BLENDSHAPE_DELTA
APA011 INVALID_BINDPOSE                  APA031 INVALID_BONE_WEIGHT
APA012 PART_PROFILE_INCOMPATIBLE         APA032 INVALID_SPACE_TRANSFORM
APA013 DUPLICATE_PART_SLOT               APA035 REMOVAL_OVERLAP_RESOLVED_BY_PRIORITY   (Warning)
APA014 UNSUPPORTED_MESH_ATTRIBUTE        APA036 UNDECLARED_UV_CHANNEL
APA015 UNKNOWN_PROFILE_SCHEMA            APA037 INVALID_CONFLICT_PRIORITY
APA016 NON_FINITE_VALUE                  APA038 SUBMESH_WITHOUT_MATERIAL_SLOT
APA017 REMOVAL_INDEX_OUT_OF_RANGE        APA039 INACTIVE_INSTALLER_SKIPPED              (Info)
APA018 INVALID_SEAM_SELECTION            APA040 PART_ONLY_SHAPE_DISALLOWED
APA019 DUPLICATE_SEMANTIC                APA999 INTERNAL_ERROR
APA020 INVALID_SEMANTIC_NAME             APA042 SEAM_PAIRING_REQUIRED
APA043 ARMATURE_SELECTION_INVALID        APA044 BONE_OUTSIDE_SELECTED_ARMATURE
```

### Authoring-layer codes (same table, emitted by `Editor/Authoring/**`)

```text
APA033 INVALID_AUTHORING_PATH            the output path cannot name a Unity asset
APA034 NON_PERSISTENT_REFERENCE          a value that must serialize resolves to a scene object
APA041 REMOVAL_MASK_TEXTURE_FAILED       a black/white mask could not be converted into a triangle set
APA050 UV_SEMANTIC_CHANNEL_ABSENT        a UV declaration names a channel the mesh does not carry
```

All four are declared in `ApaErrorCode` (the single allocation table), enumerated in
`ApaReservedCodes.Milestone5Authoring` (APA033, APA034, APA050) and
`ApaReservedCodes.Milestone9Authoring` (APA041), and aliased by `ApaAuthoringErrorCode` so
the authoring code reads naturally. They are blocking when they fire, and they are the only
codes the authoring layer **allocates** — the authoring layer also emits core codes, and M10
in particular emits `APA042` and `APA043` from the window, while `APA043` and `APA044` also
come from the build pipeline.

`APA041` carries its condition in a stable `reason=` token rather than in a second code,
because every one of its conditions has the same remedy — fix the mask source or the
target selection:

```text
reason=missing-target-mesh          no target mesh is selected
reason=not-readable                 the target mesh has no Read/Write, so its UVs cannot be read
reason=missing-mask-texture         no mask texture is assigned
reason=missing-texture              (sampling helper) no mask texture was supplied
reason=unsupported-texture-type     the mask is not a 2D texture
reason=unsupported-texture-size     the mask has a zero dimension
reason=texture-readback-failed      the device could not render and read the mask back
reason=invalid-uv-channel           the channel index is outside 0-7
reason=uv-channel-absent            the mesh has no such channel
reason=uv-channel-length-mismatch   the channel's entry count differs from the vertex count
reason=non-finite-threshold         the threshold is NaN or infinite
reason=threshold-out-of-range       the threshold is outside 0-1
reason=unsupported-topology         a submesh is not a triangle list
reason=vertex-index-out-of-range    a triangle references a vertex the mesh does not have
reason=mesh-has-no-vertices         the mesh has no vertices
reason=non-finite-uv                a sampled UV is NaN or infinite
```

There is no `reason=unsupported-texture-format`: the conversion does not judge a mask by its
pixel format. A compressed, crunched, or non-readable texture is what the render-and-read path
exists for; only a source the device itself cannot draw and read back fails, and that is
`reason=texture-readback-failed`.

There is no `reason=triangle-index-out-of-range`: a triangle index is derived from the index
buffer's own length, so it cannot fall outside its submesh. The only way an index can be
unusable is a vertex index that the mesh does not have, which has its own token.

`APA042`, `APA043`, and `APA044` are **core** codes allocated by M10 (see
[Armature selection and bone identity](#armature-selection-and-bone-identity) and the seam
section above), enumerated in `ApaReservedCodes.Milestone10` and reachable through
`IsMilestone10Code`. They carry their condition in a stable `reason=` token rather than in a
second code:

```text
APA042 SEAM_PAIRING_REQUIRED              the seam predates explicit pairing
reason=seam-pairing-required              the two lists are unordered sets; regenerate the seam

APA043 ARMATURE_SELECTION_INVALID         the merge-armature selection is unusable
reason=missing-target-armature            no target armature is selected
reason=missing-part-armature              no part armature is selected
reason=target-armature-not-found          the recorded target path does not resolve
reason=part-armature-not-found            the recorded part path does not resolve
reason=target-armature-outside-root       the target object is not inside the avatar root
reason=part-armature-outside-root         the part object is not inside the part root
reason=conflicting-target-armature-paths  installers sharing one body disagree about the target
reason=null-target-armature               no target object was handed to the merge planner
reason=null-part-armature                 no part object was handed to the merge planner
reason=part-top-bone-outside-armature     the part's highest bone is not under the selected part armature
reason=target-armature-inside-part-armature  the target is the part's own armature, or below it (circular)
reason=merge-target-is-part               the target is inside the part being merged, so nothing would merge

APA044 BONE_OUTSIDE_SELECTED_ARMATURE     a weighted bone has no armature-relative identity

APA047 PROFILE_MESH_FINGERPRINT_MISSING   schema-5 profile has no content fingerprint
APA048 PROFILE_MESH_FINGERPRINT_MISMATCH  target or part mesh attributes no longer match the captured content
APA049 SEAM_WEIGHT_BONE_NOT_IN_TARGET     a seam vertex is weighted to a bone absent from the target avatar
reason=bone-outside-armature              the bone is outside its renderer's selected armature
```

`APA045` and `APA046` are the M11 informational/identity codes; `APA047`–`APA049` are allocated by M12 for
mesh-content and seam-skinning safety. The allocation record is machine-checkable
through `ApaReservedCodes.Milestone2` / `.Milestone5Authoring` / `.Milestone6` /
`.Milestone9Authoring` / `.Milestone10` and `IsMilestone2Code` /
`IsMilestone5AuthoringCode` / `IsMilestone6Code` / `IsMilestone9AuthoringCode` /
`IsMilestone10Code` / `IsReservedForLaterMilestone` (`LaterMilestones` is empty at the
release candidate: every allocated code is emitted by the code that owns it).

Every code above appears with its meaning and the situation that produces it in the policy
tables earlier in this document; where a code carries `reason=` tokens, those are listed
too. That is the registry. The authoring-layer codes are documented in the authoring
sections (`APA033` and `APA034` under "Part Authoring" and prefab generation, `APA050` in
the UV table above, `APA041` under "Removal" above), so no code in the table is
undocumented.

`APA039` is the only new **informational** code: a parked part is a legitimate
authoring action, but it is reported so it cannot look installed.

---

## Tests, verification, and acceptance

### What has been run

| Check | Where it ran | Result |
| --- | --- | --- |
| Offline static compile of Runtime, Editor, NDMF, Preview, Authoring, and Tests against Unity's own Roslyn compiler and reference assemblies | run folders under `work/.../runs/**` | Reviewed as clean by the milestone runs; re-run at M8 for all five assemblies and at M9 for the touched assemblies, exit 0 each |
| Offline localization parity check (`work/.../runs/12-m9-review-fixes/localization-check.ps1`): every English literal passed through the layer has a Chinese entry, no entry is unreferenced, and every entry keeps its English key's format placeholders | M8, re-run at M9 and again after the M9 review fixes | 337 string entries at M8; 364 entries at M9; 368 entries after the review fixes added the apply-mode labels and the repaired status lines, 368 localized literals, 0 missing, 0 unreferenced, 0 placeholder mismatches |
| Offline M9 source contracts (`work/.../runs/12-m9-review-fixes/staticcheck/SourceChecks.ps1`, the repaired successor of the run 11 script): the APA041 allocation record, the failure enum's unique values, "the mask is window state and no Runtime type references it", the fixed 7-sample rule as written in the source (including the allocation-free triangle evaluation and the wrap-mode texel addressing), no inline English label in the window, the localized Apply Mode popup, the single exception-safe readback path with no `CopyTexture`, and the documentation's statements | M9 review fixes | 142 of 142 checks passed |
| Executable harnesses over the pure core (validation, planning, bone table, weight remap, bind poses, policies, path policy, removal mask, seam selection, diagnostics) | same | Passing at the time of each milestone |
| Meta/GUID audit of the package | M5 review | No orphan meta, no duplicate GUID |
| Attribute audit (one `[CustomEditor]`, one `[CreateAssetMenu]`, and `[MenuItem]` entries) | M5 review; re-stated by M8 | Exactly one of each. M8 adds a **second** `[MenuItem]` on purpose — the Chinese menu alias `Tools/部件装配器/部件编辑` beside the documented English path — and both attributes call the same `Open()` method, so the window still has one implementation |
| Unity Editor compilation | **never run** | — |
| Unity Test Runner / EditMode suite | **never run** | — |
| NDMF build, Scene View preview | **never run** | — |
| VRChat upload | **never run** | — |

An offline compile is Unity's compiler with Unity's reference assemblies. It is **not**
the Editor's own compilation: `Mesh`, `ScriptableObject`, `AssetDatabase`,
`PrefabUtility`, `Undo`, and the preview pipeline were never executed. Treat every
"passing" above as "reviewed", not "verified".

### The suite

`Tests/Editor/**` is an EditMode suite that exercises the real shipped types: fixtures
for seam, removal, UV, material, bone, blend shape, and multi-part cases, plus
source-contract tests for the preview layer and static checks on the asmdef graph. The
test assembly is gated behind `UNITY_INCLUDE_TESTS` and is not auto-referenced, so it
must be added to `testables` (above) to be visible in the Test Runner.

Test-count and per-file coverage are deliberately not quoted here as acceptance
criteria: the suite is part of the same release candidate, and the authoritative count
is the one in the sources you are running. Compare the executed count with the number of
`[Test]` methods — checklist row 2.3.

### What you must run

**Everything behavioural.** The full user-run plan is
[`USER_ACCEPTANCE_CHECKLIST.md`](USER_ACCEPTANCE_CHECKLIST.md), grouped as:

1. **Compile** — Editor compilation, assembly load, testables.
2. **EditMode suite** — the whole suite, twice, with no Console noise.
3. **Preview** — proxy appears, authoring assets untouched, toggles, live invalidation
   (including child transforms and material swaps), invalidation *to invalid*, two
   groups, disabled installers, nested avatars, mesh-lifetime soak.
4. **Build** — build succeeds, **preview result equals build result**, mesh/material/
   bone/bind-pose/blend-shape inspection, blocking diagnostics actually block, no
   partial avatar, Modular Avatar ordering, merge verification, `EditorOnly` trap.
5. **Deletion and restoration** — delete the prefab, save/reopen, undo/redo, domain
   reload, untouched source assets.
6. **Determinism** — build twice, reorder installers, rename, priority reversal,
   two avatars, prefab variant.
7. **Schema migration (v2/v3 → v4)** — a new profile is v4, a v2 and a v3 profile are each
   accepted without rewriting, a v1 profile is refused, a newer schema is refused, and the
   policy fields round-trip.
8. **Performance and resources** — refresh cost, memory after many refreshes, build
   cost, cache bounds, optimizers after assembly.
9. **Ecosystem** — Modular Avatar, AAO, lilToon, PhysBone, Contacts, Animator, prefab
   variants, Build & Test, and a real VRChat upload.
10. **Language (M8)** — the selector is present in both surfaces, a switch is immediate,
    it survives a restart without touching version control, the English diagnostic text is
    unchanged in English, and the English menu path still opens the window.
11. **Texture mask (M9)** — a black/white mask produces the painted region, Invert reverses
    it, the mask needs no Read/Write Enabled and no importer change, the texture is not
    saved into the profile, each Apply is one undo, an empty result is not an error, the
    refusals are actionable `APA041`, the result is byte-identical across reruns and domain
    reloads, and the Scene View picker and address list still edit the same set.
12. **Armature selection and seams (M10)** — the two armature pickers and their refusals
    (`APA043`), the pre-M10 profile refused with `APA042` and fixed by regenerating the seam,
    an unreferenced bone slot no longer blocking with `APA008`, the automatic first-use
    signature capture that never overwrites a captured one, world-position seam generation on
    a scaled hierarchy, the collapsed address list, and both UI languages.
13. **Body bone authority (M11)** — a part that weights a body-declared but body-unweighted
    path follows the body's Transform in preview and in the build, the redirect is one Info
    summary per part, a duplicated body path a part weights blocks with `APA008`, and an
    unreferenced duplicated slot still blocks nothing.

Do not skip group 4's row "preview result equals build result". It is the product's
core promise, and it is the one property no amount of source review can prove.

---

## Known limitations and honest gaps

These are milestone boundaries and deliberate product decisions, not oversights. Each
one is enforced by a diagnostic rather than by silence.

### Not implemented

| Not implemented | Diagnostic / behaviour |
| --- | --- |
| Non-triangle submesh topologies (points, lines, non-triangle lists) | `APA014 UNSUPPORTED_MESH_ATTRIBUTE` |
| More than one part **replacing** one non-`Custom` slot | `APA013`; use `Augment` to attach instead |
| Priority-based resolution of UV or blend shape conflicts | `APA025` / `APA028` / `APA029` block. Permanently by design: a welded vertex holds one value, and a frame index is a position, not an identity |
| Automatic retopology, seam bridging across differing vertex counts, automatic UV seam repair, automatic same-name material conflict resolution, guessing removal regions from bone weights, repairing broken bone hierarchies | Reported, never guessed at — out of scope by product decision |
| A progressive "soft match by name" target-identification ladder | Superseded by the compatibility guard: an ambiguous target **blocks** |
| Automatic armature selection or inference | Not: `Suggest Armatures` proposes from the hierarchy, and both selections must be confirmed by the author. A level correspondence is a declaration, not a fact that can be derived |
| Migrating a pre-M10 unordered seam into explicit pairs | Not: it is refused with `APA042` and regenerated in one action. Pairing two unordered sets by position is exactly the guess that can weld a seam vertex to an unrelated one |
| Localization of APAs' messages | **Implemented in M8 for the UI, the severities, the section and control labels, and the operation statuses — English and Simplified Chinese.** The validation *messages*, exception text, asset paths, and the stable `APAxxx` / `reason=…` tokens stay English on purpose: they are the searchable contract a bug report and a test assertion rely on, and a Chinese reader gets a one-line Chinese description of each code beside them. See [Language](#language) |

### Honest gaps and risks that are not code defects

| Gap | Why it matters | Where it is checked |
| --- | --- | --- |
| Nothing has been run in Unity | The whole behavioural surface is unverified; a compile-clean package can still fail to load, and a preview can still differ from a build | Checklist groups 1–9 |
| Preview/build equivalence is a design property, not yet an observation | R14 is satisfied by one code path plus one processor; only the checklist's comparison row proves it end to end | Checklist 4.2 |
| The merge postcondition depends on Modular Avatar's runtime behaviour | The pass verifies "the bone left the part root"; whether Modular Avatar relocates it differently in your scene is unverified | Checklist 4.14, 4.15 |
| Mesh lifetime on preview error paths | Disposal is refcounted and main-thread; a *faulted* pipeline build may not dispose its nodes, so generated meshes can leak on preview errors | Checklist 3.15, 8.2 |
| `EditorOnly` interaction | NDMF removes `EditorOnly` objects before Modular Avatar merges; a part parked under one can lose its geometry before assembly | Checklist 4.18 |
| Armature-lock side effects | `ModularAvatarMergeArmature` is `[ExecuteInEditMode]` and its `OnEnable` touches armature-lock state; the transient component declares `NotLocked` and is destroyed by Modular Avatar, but a lock job surviving the lifecycle is a runtime fact | Checklist 4.17 |
| Real-world performance is unmeasured | Seam generation is a uniform spatial hash over the two meshes, near O(n) over the vertices involved, but the capture cost per refresh is a full attribute copy of every source | Checklist 8.1–8.4 |
| Ecosystem matrix unverified | AAO, lilToon, PhysBone, Contacts, Animator, prefab variants, and the upload path are all checklist rows, not results | Checklist group 9 |
| The mask conversion is unobserved | The sampling rule, the wrap/filter handling, and the texture readback are deterministic source logic, but no mask has ever been sampled in the Editor, so "the painted region is the selected region" is a design claim until you press `Apply Mask` | Checklist group 11 |
| The mask readback is transiently large | One `Color32` per mask pixel plus one temporary render texture of the same size; both are released when Apply returns, and nothing is cached between applies | Checklist 11.3, 8.2 |
| The armature selections and the world-position seam are unobserved | The two pickers, the tolerance, the pairing, and the display and timing of `APA042`/`APA043`/`APA044` are deterministic source logic, but only a running Editor shows whether the refusal arrives when an author expects it; the same applies to the automatic capture and to "a captured signature is never overwritten" | Checklist group 12 |

The bone-signature rule is **not** an open question: a mismatch blocks (`APA012
reason=bone-signature-mismatch`) whenever any source in the configuration carries
skinning data, because the final bone table is rebuilt from the live hierarchy after the
merge, and it stays an advisory warning (`reason=bone-signature-advisory`) when nothing
is skinned, where the stored paths are a staleness signal rather than a remap input. Both
sides are implemented in `Editor/Validation/Rules/CompatibilityRule.cs`; only the runtime
observation of them (checklist 6.7) is still open.

### Release-candidate versioning

`0.3.0-rc.6` is a **prerelease**. Per semver it sorts before `0.3.0`, so no VPM
resolution will treat it as the stable `0.3.0`. The version will move to `1.0.0` only
after the acceptance checklist has been executed and its results recorded. Until then,
no document, changelog entry, or commit message in this package may describe the
package as stable, tested, or user-accepted.

---

## License

MIT. See `LICENSE.md`. Third-party dependencies and the exact API surface relied upon
are listed in `Third Party Notices.md` and in
[Installed NDMF and Modular Avatar API attribution](#installed-ndmf-and-modular-avatar-api-attribution).

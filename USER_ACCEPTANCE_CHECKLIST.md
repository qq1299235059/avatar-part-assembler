# User acceptance checklist — 0.3.0-rc.6 release candidate

**Status: partially exercised; full acceptance remains open.** The package has now compiled
against Unity 2022.3.22f1's real Bee/Roslyn references and a real NDMF `AvatarProcessor`
build has completed successfully on the project test avatar. That run replaced the target
mesh, consumed the APA part renderer/installer, and verified the post-Modular-Avatar armature
path. The rows below are still the authoritative full acceptance plan: any row not explicitly
run and recorded is still not a pass, and end-to-end VRChat upload has not yet been accepted.

- Package version under test: **`0.3.0-rc.6`** (`Packages/dev.avatar-part-assembler/package.json`)
- Profile schema version under test: **5** (`ApaPartProfile.CurrentSchemaVersion`)
- Project: `C:\\编辑中工程\\niu11`
- Project: `C:\编辑中工程\niu11`
- Expected environment: Unity **2022.3.22f1**, VRChat SDK Avatars **3.10.4**,
  NDMF **1.14.0**, Modular Avatar **1.18.0-beta.0**
- Harness that wrote this file: documentation session in an isolated copy
  (`work/.../isolated/m7docs`); the code session (`runs/07-m7-integration`) owns the
  package sources, `runs/10-m8-zh-cn-localization` added group 10 (language) and the
  localization layer, `runs/11-m9-mask-selection` added group 11 (the black/white
  texture-mask removal selection) and its `APA041`, and `runs/12-m9-review-fixes` repaired
  the seven defects a source review found in that milestone (readback path, format refusal,
  wrap addressing, per-triangle allocation, Apply Mode labels, blocker wording, and the
  duplicated failure enum value) and revised group 11 accordingly;
  `runs/13-m10-authoring-simplification` added group 12 (explicit armature selections,
  world-position seam generation, weighted-bone identity, automatic signature capture, and
  the collapsed address list) and its `APA042`/`APA043`/`APA044`. The facts this file once
  listed as
  unresolved have since been reconciled against the frozen package — see
  [Facts reconciled against the frozen package](#facts-reconciled-against-the-frozen-package).
  Every behavioural claim is still **unobserved until you run the row**.

Legend for the Result column: `pass` / `fail` / `blocked` / `not run`.

---

## 0. Before you start

| # | Step | Why |
| --- | --- | --- |
| 0.1 | Read `README.md` → "Release-candidate status" and "Known limitations and honest gaps". | The limitations are acceptance-relevant; several checks below exist precisely because a limitation is expected, not because a bug is suspected. |
| 0.2 | Copy `Packages/dev.avatar-part-assembler/**` into a branch or a backup folder. | The checks include builds and an upload. The package is non-destructive by design, but an acceptance run should never be the first thing that has no way back. |
| 0.3 | Confirm the versions in `Packages/manifest.json` / `Packages/packages-lock.json` match the expected environment above. | A different NDMF or Modular Avatar version invalidates the API attribution this release was reviewed against. |
| 0.4 | Keep the Unity Console visible with **Clear on Play** off and **Error Pause** off. | A row that hides Console output cannot be judged. |

---

## 1. Compile gate

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 1.1 | Editor compile | Open `C:\编辑中工程\niu11` in Unity 2022.3.22f1 and wait for the domain reload. | Console shows **zero errors and zero warnings** from `dev.avatar-part-assembler.*`. | |
| 1.2 | Assembly load | In the Console, confirm no "The type or namespace name … could not be found", no "Assembly … will not be loaded", and no asmdef cycle error. | All five package assemblies load: `runtime`, `editor`, `editor.ndmf`, `editor.preview`, `tests.editor`. | |
| 1.3 | Nested editor assemblies present | Confirm the types are visible: `ApaAssemblyPass` and `ApaPreviewRegistration` resolve, and the NDMF pass list is populated. | The NDMF pass registered by this package appears in the NDMF plugin/pass listing. | |
| 1.4 | Test assembly visible | Add `"dev.avatar-part-assembler"` to `testables` in `Packages/manifest.json`, then re-open the Test Runner. | `AvatarPartAssembler.Tests` appears under EditMode. | |
| 1.5 | Compile from a clean library (optional but recommended) | Delete `Library/ScriptAssemblies` and let Unity rebuild, or reimport the package. | Same as 1.1: a clean rebuild compiles with no errors. | |

> 1.4 is a project-level change and was **not** made by the package work: the package is
> scoped to `Packages/dev.avatar-part-assembler/**`. See the README.

---

## 2. EditMode suite gate

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 2.1 | Whole suite | `Window > General > Test Runner` → **EditMode** → run `AvatarPartAssembler.Tests`. | Every test passes. **Zero failures, zero errors, zero ignored tests.** | |
| 2.2 | No Console noise from the suite | Watch the Console during 2.1. | No exceptions, no leaked-object warnings, no `DestroyImmediate` errors, no "destroying object multiple times". | |
| 2.3 | Test count sanity | Compare the number of executed tests with the number of `[Test]` methods in `Tests/Editor/**` (two files, `MeshFixtures.cs` and `MultiPartFixtures.cs`, are fixture builders with no tests). | Executed count equals the `[Test]` count. A test silently not discovered is a gap, not a pass. | |
| 2.4 | Authoring suite | Run the `Authoring*` test classes specifically: `AuthoringAssetPathTests`, `AuthoringProfileDraftTests`, `AuthoringRemovalMaskTests`, `AuthoringSeamSelectionTests`, `AuthoringSemanticValidationTests`. | All pass. These are the asset-safety rules; a failure here is a release blocker. | |
| 2.5 | Policy suite | Run `MultiPartConflictPolicyTests`, `TargetGroupingTests`, `InstallerPriorityTests`, `PolicyDeterminismTests`, `UvChannelDeclarationTests`. | All pass. These carry the M6 policy truth table the README documents. | |
| 2.6 | Preview contract suite | Run `PreviewStaticContractTests`. | All pass, **none ignored**. Note that several of these tests `Assert.Ignore` when a preview source file cannot be found — an ignored test here means the contract was not actually checked. | |
| 2.7 | NDMF seam suite | Run `NdmfMergeArmaturePlanTests`, `BuildPipelineAssemblySeamTests`, `AnimatorRetargetContractTests`. | All pass. | |
| 2.8 | Repeat the suite | Run the whole suite a second time in the same session. | Identical result. No cross-test state, no order dependence, no leakage between runs. | |

---

## 3. Preview gate (Scene View, non-Play-Mode)

Prerequisite: 1.1 passes. Use a body with at least one part prefab and an
`AvatarPartInstaller`.

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 3.1 | Preview appears | Put the avatar in the Scene View with the installer active. | The assembled body is drawn: the declared body region is gone and the part is welded in place. | |
| 3.2 | Authoring assets untouched | With the preview visible, click the body renderer and expand the Inspector. | `sharedMesh` and `sharedMaterials` are still the **original** ones. No `*_Assembled` mesh is assigned to any authoring object. | |
| 3.3 | Preview toggle | `Tools/NDM Framework/Configure Previews` → untick **Avatar Part Assembler**. | The preview disappears and the original body renders. Re-tick: it returns. | |
| 3.4 | Debug overlay toggle | Tick **APA seam/removal overlay** in the same window. | The seam/removal overlay is drawn. Untick: it disappears. It must be off by default after a domain reload. | |
| 3.5 | Live invalidation — transform | Move the part prefab in the hierarchy. | The preview refreshes within about one frame; no stale geometry is left behind. | |
| 3.6 | Live invalidation — child transform | Move or rename a **child** of the part root (not the root itself). | The preview refreshes. This is the check that catches "watch the object, not its subtree". | |
| 3.7 | Live invalidation — profile | Edit a field on `ApaPartProfile` (for example a semantic name). | The preview refreshes; there is no frame where a stale successful preview survives the edit. | |
| 3.8 | Live invalidation — material | Swap a material referenced by a `MaterialSemantics` row. | The preview refreshes. | |
| 3.9 | Invalidation to invalid | Break the input (for example delete the seam selection, or make the base and part seam cardinality differ). | The proxy **disappears** and the original body comes back; the reason is reported as a diagnostic, not swallowed. | |
| 3.10 | Two target groups | Use an avatar with parts welded to the body renderer and parts welded to a second renderer (for example clothing). | Each group previews against its own target; no cross-group geometry appears. | |
| 3.11 | Disabled installer | Untick the component's `enabled`, then deactivate its GameObject, then clear `EnabledForBuild` — one at a time. | Each condition removes the preview **and deletes no body triangles**; the skip is reported (informational), not silent. | |
| 3.12 | Re-enable | Restore each condition from 3.11. | The preview returns exactly as before. | |
| 3.13 | Nested avatar | If you have two avatars, one nested inside the other, put an installer on the inner one. | The inner avatar's part does **not** install into the outer body. | |
| 3.14 | Unrelated avatar | Have a second avatar in the scene with no installer. | It is untouched and produces no APA diagnostics. | |
| 3.15 | Mesh lifetime | Toggle a part on/off ≥ 20 times and edit the profile repeatedly, watching the Profiler's `Mesh` count. | No unbounded growth; no `*_Assembled` mesh accumulates in the hierarchy. | |
| 3.16 | Preview does not mutate assets | After all of the above, run `git status` (or compare file hashes) over the base body, the part assets, and the profile. | No file changed. | |

---

## 4. Build gate (NDMF build / Build & Test)

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 4.1 | Build succeeds | Build the avatar through the SDK's build path (`Build & Test` or the NDMF build while in Play Mode). | The build completes; no APA error blocks it. | |
| 4.2 | Preview equals build | Compare the build result with the preview result for the same input: vertex count, per-submesh index counts, material slot list, bone count, bind-pose values, blend-shape names and frame weights. | **Identical.** This is the product's core promise ("looks right in Unity → identical after upload"). Use the Inspector on the built clone and, where practical, a scripted array comparison. | |
| 4.3 | Assembled mesh is on the clone only | Inspect the built clone (the NDMF-generated avatar), then the authoring scene. | The clone's renderer carries the generated mesh; the authoring renderer still carries the original. | |
| 4.4 | Material slots | Count the renderer's material slots and compare with the documented policy result for your configuration. | Matches the README's material truth table; the base material asset is never replaced. | |
| 4.5 | Bone table | Inspect the built renderer's `bones`. | Body bones first in body order, then new part bones by canonical part order; a merged part bone appears once, not twice. | |
| 4.6 | Bind poses | Inspect the generated mesh's bind poses after moving a source bone before Play Mode, and compare them with the source mesh's authored bind data. | Bind poses remain stable across the live pose edit and are converted into the target renderer's local basis; a current edited pose must never become a new bind pose. Legacy snapshots without source bind data may use the documented compatibility fallback. | |
| 4.7 | Blend shapes | Expand the built mesh's blend shapes. | Body shapes first, then new part shapes; same-named base/part shapes merged; every frame present. | |
| 4.8 | Part geometry is not drawn twice | Look at the built avatar. | The part appears once; the consumed part renderers are gone from the clone. | |
| 4.9 | Blocking diagnostic is visible and blocks | Introduce a blocking condition (for example two parts claiming one non-Custom slot, or a removal overlap with no declared priority on every claimant) and build. | The NDMF error panel shows the APA code and the **build/upload is stopped**, not merely logged. | |
| 4.10 | No partial avatar on failure | With the blocking condition from 4.9 still in place, inspect the clone after the failed build. | No group's mesh was assigned; the hierarchy was not half-mutated. | |
| 4.11 | Group failure fails the whole avatar | Make one group invalid and leave another valid. | The whole avatar fails, and the report names the failing group (`group=<path>` in the detail). | |
| 4.12 | No installers → no interference | Build an avatar with no `AvatarPartInstaller` at all. | The build succeeds with no APA diagnostics. | |
| 4.13 | Modular Avatar ordering | Read the NDMF build log / pass listing for this build. | The assembly pass runs **after** `nadena.dev.modular-avatar` and **before** the optimizers. Do not judge this from memory of the docs. | |
| 4.14 | Merge verification | Build a part whose armature should merge into the body's. Then temporarily disable Modular Avatar's plugin and build again. | First build: parts merge, no "merge not applied" diagnostic. Second build: the build is **blocked** with the "transient merge-armature configuration was not consumed" diagnostic, not silently assembled. | |
| 4.15 | Bone-name correspondence | Build a part whose bone names match the target's, and one whose names do not. | Matching names genuinely merge (bone count does not grow by the part's bone count). Non-matching names either use the profile's serialized prefix/suffix / inference policy, or are reported — they must not silently append every part bone as a new bone. | |
| 4.16 | No leftover merge component | Inspect the built clone's saved hierarchy. | No `ModularAvatarMergeArmature` remains. | |
| 4.17 | No armature-lock job | After the build, watch the avatar in the Editor for a few seconds. | No merged bone transform is being rewritten every editor update; no armature-lock artefact. | |
| 4.18 | `EditorOnly` trap | Place a part under an object tagged `EditorOnly` (or confirm no part is). | The part's geometry is either correctly absent **and reported**, or correctly present. It must not silently disappear from the build. | |
| 4.19 | Object references in diagnostics | Click the object reference in an APA diagnostic in the NDMF error panel. | It selects the avatar or the reported part; no destroyed-object exception. | |
| 4.20 | Merge Animator retargeting | On a part prefab that carries a Modular Avatar **Merge Animator** (Relative path mode, on the part root) whose controller animates the part renderer's blend shapes, build the avatar, then inspect the built FX controller's clip binding paths (and toggle the blend shape in Play Mode / Gesture Manager). | The clip's binding path is the **target body renderer's** path after the build, not the consumed part renderer's path, and the blend shape responds. The build log lists `Retarget consumed part animation onto the target renderer` after the assembly pass. | |
| 4.21 | Empty source object cleanup | Build, then inspect the clone around the consumed part. | A consumed part renderer object left carrying only a Transform, with no children, is **gone**; an object that still carries a component or a child is **still there**. A `MeshRenderer` part keeps its `MeshFilter`, so its object stays — that is the documented boundary of the predicate, not a failure. The build log lists `Remove consumed part objects left empty` after Modular Avatar's late transform stages. | |

---

## 5. Deletion and restoration gate

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 5.1 | Delete the part prefab | Delete the part prefab from the avatar, then look at the Scene View and the body renderer. | The body renderer returns to the original mesh and materials. Nothing about the scene was rewritten. | |
| 5.2 | Save, close, reopen | Save the scene, close Unity, reopen the project and the scene. | The avatar is exactly as it was before the part was ever added. | |
| 5.3 | Disable → delete → re-add | Disable the installer, delete the prefab, re-add it, re-enable. | Each step behaves as documented; no residue from an earlier attempt. | |
| 5.4 | Untouched sources | Compare hashes / `git status` of the base body mesh, its materials, and the part assets after the whole session. | Unchanged. | |
| 5.5 | Undo / Redo | Undo and redo every authoring action you performed (adding an installer, editing the profile, generating a prefab). | Each undoes and redoes cleanly; no hidden pipeline mutation appears as a surprise undo step. | |
| 5.6 | Domain reload | Reload scripts with parts installed. | No re-capture, no duplicated parts, no `APA015` for a profile written by this same build. | |

---

## 6. Determinism and repeatability gate

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 6.1 | Build twice | Build the same avatar twice from the same inputs. | Byte-identical generated mesh data (vertices, indices, UVs, bone weights, bind poses, blend-shape frames) and an identical issue list. | |
| 6.2 | Reorder installers | Reparent / reorder the installer objects in the hierarchy without changing their data. | The same plan and the same output. Ordering is canonical, never hierarchy-scan order. | |
| 6.3 | Rename inside the avatar | Rename the installer object and its ancestors inside the avatar root. | The same plan; only a rename that breaks a *recorded* `RendererPath` blocks, and it blocks with `APA006`. | |
| 6.4 | Priority reversal | Give two conflicting parts explicit priorities, build, then swap the two priorities and build again. | The part order mirrors the priority reversal; nothing else changes silently. | |
| 6.5 | No priority declared | Remove all declared priorities and build. | The output is identical to the pre-priority (schema-2-equivalent) order. This is the determinism regression guard. | |
| 6.6 | Two avatars in one scene | Build with two avatars, only one carrying parts. | The other avatar is untouched and produces no diagnostics. | |
| 6.7 | Prefab variant as the base body | Use a prefab variant as the base body, and a variant with a different mesh. | The matching variant validates; the different-mesh variant blocks with `APA012`. | |

---

## 7. Schema migration gate (v2/v3 → v4)

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 7.1 | New profile is v4 | Create a profile through `Tools/Avatar Part Assembler/Part Authoring` → `Save Profile Asset`. | The asset's `SchemaVersion` reads **4**. | |
| 7.2 | Schema-2 profile accepted, not rewritten, then refused at build | Load a profile authored before this release (schema 2), then run `Validate` and a build. | It loads, its serialized `SchemaVersion` is still **2** (a read that rewrites the author's asset is a defect, not a convenience), and it keeps its old behaviour (`SlotMode = Replace`, `ConflictPriority = 0`). The build then refuses it with `APA043 ARMATURE_SELECTION_INVALID` (no armature selected) and `APA042 SEAM_PAIRING_REQUIRED reason=seam-pairing-required` (unordered seam) until it is re-authored. | |
| 7.3 | Schema-3 profile accepted, not rewritten, then refused at build | Load a v3 profile (the previous release's output), check the asset's serialized `SchemaVersion`, then run `Validate` and a build. | Still **3**: loading it neither rewrites nor silently upgrades it. The build then refuses it with `APA043 ARMATURE_SELECTION_INVALID` (no armature selected) and `APA042 SEAM_PAIRING_REQUIRED reason=seam-pairing-required` (unordered seam); it validates only after both armatures are selected and the seam is regenerated. | |
| 7.4 | Schema-1 profile rejected | Load a schema-1 profile (or hand-edit `_schemaVersion: 1`). | Blocked with the migration refusal message; the profile must be re-authored. It must not be guessed at. | |
| 7.5 | Newer schema rejected | Hand-edit `_schemaVersion` to a value above **4**. | Blocked with `APA015 UNKNOWN_PROFILE_SCHEMA`. | |
| 7.6 | Policy and legacy fields round-trip | Set `SlotMode`, `ConflictPriority`, `AllowPartOnlyShapes`, the two armature selections (the target armature path and the part armature path), and the legacy `MergePrefix`, `MergeSuffix`, `InferMergeNames` fields, save, close, reopen. | Every value round-trips; nothing resets to a default. The two armature paths and the three legacy merge fields all serialize: the armature paths are what the build reads, while `MergePrefix`/`MergeSuffix`/`InferMergeNames` are round-trip-only — stored and restored, no longer read by the build and no longer shown in the window. | |
| 7.7 | Older build sees v4 | (If you have a pre-M10 build available) open a v4 profile with it. | It blocks with `APA015` rather than silently ignoring a profile it cannot read. | |

---

## 8. Performance and resource gate

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 8.1 | Preview refresh cost | On a realistic avatar (≥ 50k vertices, ≥ 4 blend shapes), edit one profile field and watch the refresh. | The refresh is well under a second; seam generation is a single explicit action that stays near O(n) over the two meshes' vertices and is not part of the per-frame refresh path. | |
| 8.2 | Memory after many refreshes | Watch the Profiler's mesh/material counts across ≥ 50 refreshes. | No unbounded growth. | |
| 8.3 | Build cost | Time the assembly portion of the build. | It is a small fraction of the whole NDMF build; no per-part full-mesh rebuild. | |
| 8.4 | Cache bounds | Leave the editor open and previewing, then inspect the preview cache size through the preview diagnostics. | Bounded by the documented capacity plus live leases; evictions destroy their meshes. | |
| 8.5 | Optimizers after assembly | Build with an optimizer enabled (for example AAO). | The optimizer still runs and does not fight the generated mesh. | |

---

## 9. Ecosystem compatibility gate (Phase 8)

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 9.1 | Modular Avatar | Covered by 4.13–4.17. | As above. | |
| 9.2 | AAO (Avatar Optimizer) | Build an avatar with AAO enabled. | No conflict over the body mesh or materials; the assembled result survives optimization. | |
| 9.3 | lilToon avatar | Use a lilToon-materialed body and part. Build and inspect shading. | Materials and UV semantics survive assembly; toon shading looks correct. | |
| 9.4 | PhysBone | Put a PhysBone on the part and one on the body region. | Both still work after the merge; references follow the merged bones. | |
| 9.5 | Contacts | Put a Contact on the part. | It still fires after the merge. | |
| 9.6 | Animator | Drive a body blend shape that was merged with a same-named part shape. | The existing animation curve drives the merged shape. | |
| 9.7 | Prefab Variant | Covered by 6.7. | As above. | |
| 9.8 | Undo / Redo and Domain Reload | Covered by 5.5 and 5.6. | As above. | |
| 9.9 | Build & Test | Run the SDK's `Build & Test`. | The avatar enters the test instance with no errors. | |
| 9.10 | VRChat upload | Upload the avatar. | The uploaded avatar matches the preview: geometry, materials, UVs, skinning, blend shapes, dynamics. | |
| 9.11 | Second avatar / second project | Repeat 9.10 for a second avatar or a second Unity project. | The result is not specific to one asset set. | |

---

## 10. Language and localization (M8)

Added with `0.3.0-rc.2`. The layer is a pure lookup plus an `EditorPrefs` preference, so
these rows are about what the editor actually draws and stores — not about the translation
table, which `LocalizationContractTests` checks statically.

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 10.1 | Selector present on both surfaces | Open `Tools > Avatar Part Assembler > Part Authoring`, then select a GameObject with an `AvatarPartInstaller`. | The window toolbar and the top of the inspector each show a language selector offering `Auto (follow system)`, `English`, `简体中文`. | |
| 10.2 | The English menu path still works | Open the window from the English menu path, close it, then open it from the Chinese alias `工具 > 部件装配器 > 部件编辑`. | Both entries open the same window; the tab title is not duplicated and no second window is created. | |
| 10.3 | A switch is immediate | With the window open and a part selected, switch `English` → `简体中文`. | Every section title, field label, button, tooltip, HelpBox, and status line, the window tab title, the installer inspector, and the Scene View picking label are redrawn in Chinese on the same frame. No restart, no reselect. | |
| 10.4 | The choice persists without touching the project | Switch to `简体中文`, record `git status` (or the file list under the package), close and reopen the editor. | The window and the inspector come back in Chinese. **No project file changed**: the preference is `EditorPrefs` state under `dev.avatar-part-assembler/localization/language`. | |
| 10.5 | `Auto` follows the system language | Select `自动（跟随系统语言）`. Repeat on a system whose Unity language is not Chinese, if one is available. | Simplified Chinese on a Simplified Chinese system, English otherwise. Traditional Chinese systems get English: the Simplified table is not offered as a substitute. | |
| 10.6 | Stable tokens survive translation | In Chinese, force a failing diagnostic (for example press `试运行装配` with no target body renderer selected) and read the line. | The line starts with the Chinese severity word, then the **unchanged** `APAxxx` code and English mnemonic title, then a Chinese one-line description, then the **unchanged** message and `reason=…` detail. Example: `错误 APA006 TARGET_RENDERER_NOT_FOUND（无法解析目标渲染器或其网格）: … :: reason=…`. | |
| 10.7 | English output is unchanged | Switch back to `English` and repeat the checks above. | The interface and every rendered diagnostic are byte-identical to the English text the previous release showed, including `ERROR`/`WARNING`/`INFO` and the absence of any Chinese description. | |

> 10.4 is the row that matters for a shared repository: a language choice that dirtied the
> working tree would be a defect, not a convenience.

---

## 11. Texture-mask removal selection (M9)

Added with the M9 mask workflow. These rows are about the conversion actually running in the
Editor — the pixel readback, the UVs it samples, and the triangle set it produces — none of
which any source check can prove. Prepare a mask first: duplicate the target body's base
color texture, paint the region you want removed as **white on black**, and import it with
**Read/Write Enabled left unchecked**. Any ordinary color import is acceptable — uncompressed
(`RGBA32`/`ARGB32`/`RGB24`/`R8`) and block-compressed (`DXT`/`ETC`/`ASTC`/`PVRTC`/`EAC`,
crunched included) both go through the same render-and-read path, because the conversion never
judges a mask by its pixel format.

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 11.1 | The mask produces the painted region | Select the target body renderer, open `Tools > Avatar Part Assembler > Part Authoring`, expand **Removal Region → Texture Mask**, assign the mask, leave UV Channel 0, Threshold 0.5, Invert off, Apply Mode `Replace Selection`, press `Apply Mask`. | The Apply Mode popup shows `Replace Selection`, `Add To Selection`, `Subtract From Selection` (not `ReplaceSelection` and friends), reads `替换选择` / `添加到选择` / `从选择中减去` in Chinese, and the selected count matches the painted region; the Scene View highlight (with `Highlights` on) covers the painted triangles and not the rest. | |
| 11.2 | White selects, black keeps, Invert reverses | Note the count, tick `Invert`, press `Apply Mask` again, then untick it and apply once more. | The inverted result is the complement of the original; the un-inverted result reproduces the first count exactly. | |
| 11.3 | No Read/Write Enabled required, no importer change, no format refusal | With the mask imported with Read/Write **off**, run 11.1. Then repeat once with a block-compressed copy of the same mask (for example `DXT5`/`BC7` or the platform default), and once with a mask whose texture wrap mode is `Repeat`. Afterwards open the mask's import settings and confirm Read/Write is still off, and check `git status` / the file's timestamp. | Both conversions succeed without an `APA041`; the importer settings and the asset file are untouched; the status line reports the selected and considered triangle counts (it does not report a readback path, because there is only one). For the `Repeat` mask, a region painted across the UV border is selected on both sides, because the sampler interpolates the last and the first texel instead of clamping. | |
| 11.4 | The texture is not saved anywhere | After 11.1, press `Save Profile Asset`, then inspect the saved `ApaPartProfile` in the Inspector and search its YAML (`.asset` file) for the mask's GUID. | The profile contains the removal triangle set and **no** reference to the mask texture. Delete the mask asset and re-run `Validate` / `Dry-Run Assembly`: the part still builds. | |
| 11.5 | Apply modes and one undo per Apply | With a non-empty selection, run `Add To Selection`, `Subtract From Selection`, and `Replace Selection`, pressing **Ctrl+Z once** after each. | Add only grows the set, Subtract only shrinks it, Replace replaces it; each single `Apply Mask` is undone by one Ctrl+Z, and the reported before/after/added/removed counts match what you observe. | |
| 11.6 | An empty result is not an error | Paint a fully black mask (Invert off) and apply it in `Replace Selection` mode, then in `Add To Selection` mode. | Replace clears the selection after an explicit Apply and says so; Add leaves it unchanged and says so. Neither raises `APA041`. | |
| 11.7 | Refusals are actionable, and a disabled Apply says why | Try each of: no target renderer selected; a mesh without Read/Write; UV Channel 3 when the mesh only has UV0; a threshold driven to a boundary; a mask whose triangles you know cross a boundary at 50% grey. | The first three disable Apply **and** print the reason next to the button — the unreadable mesh names the mesh and says to enable Read/Write, and the absent channel names the channel. A refusal from a press reports **`APA041 REMOVAL_MASK_TEXTURE_FAILED`** with its full `reason=…` line inside the Texture Mask block (and again under "Last write reported"), and the status line points there. The 50% boundary behaves consistently across two runs of the same mask. | |
| 11.8 | Determinism end to end | Record the address list from 11.1, clear the selection, apply the same mask again, and compare. Repeat after a domain reload (reload scripts) and after reimporting the mask. | Byte-identical address lists and counts every time. | |
| 11.9 | The picker and the address list still work | After a mask application, use `Pick Triangles In Scene` to toggle one triangle and `Add List` to add `0:1`, then undo each. | Both tools edit the same set the mask produced; each edit is separately undoable and does not disturb the rest of the selection. | |

> 11.3 and 11.4 are this milestone's product promises: a mask that does not require the
> author to change an import setting, and a profile that does not depend on the texture.
> A failure there is a design failure, not a cosmetic one.

---

## 12. Armature selection, world-position seams, and authoring ergonomics (M10)

Added with `runs/13-m10-authoring-simplification`. These are the manual observations for
M10: two explicit armature selections replaced every bone name and path heuristic, bone
identity is now the bone path relative to its own selected armature, the seam is generated
from world positions and stored as explicit pairs, an uncaptured compatibility signature is
captured automatically on first use, and the removal address list is collapsed. A profile
authored before M10 must be re-authored — both armatures selected and the seam regenerated —
before it builds; until then the build refuses it with `APA042` and `APA043`.

> The pre-M10 aids (name-based merging, `MergeTargetPath`/`MergePrefix`/`MergeSuffix`/
> `InferMergeNames`, the two seam index-list fields and the two Scene View vertex pickers) are
> gone by design. A row that finds one of them still deciding a bone identity or a seam pair is
> a failure of this milestone, not a missing convenience. The legacy merge fields still
> round-trip in an asset (7.6), but nothing in the window shows them and nothing in the build
> reads them.

| # | Check | Exact steps | Pass criteria | Result |
| --- | --- | --- | --- | --- |
| 12.1 | The two armature pickers replace every bone heuristic | On a part whose profile was authored before M10, open `Tools > Avatar Part Assembler > Part Authoring`, press `Suggest Armatures` in the Selection area, and read the two pickers: `Target Armature` (the armature under the avatar root that owns the body's bones) and `Part Armature` (the armature under the part root that owns the part's bones). Confirm both explicitly, press `Save Profile Asset`, close the window, reload the profile, and reopen the window. Then press `Update Installer On Prefab` and inspect the generated `ModularAvatarMergeArmature`. | Both pickers still hold the confirmed references after the save and the reload, and `Suggest Armatures` only proposes them from the hierarchy — it applies nothing by itself, the author confirms both. The generated merge component sits on the selected part armature, targets the selected target armature, and is written with empty prefix/suffix and no name inference. No `MergeTargetPath`, `MergePrefix`, `MergeSuffix` or `InferMergeNames` control appears anywhere in the window. | |
| 12.2 | A missing armature blocks with `APA043` | With everything else valid, clear `Target Armature` and run `Validate`, then `Dry-Run Assembly`, then a build. Restore it, clear `Part Armature`, and repeat the three actions. | Each of the six actions **blocks** (does not merely log) with `APA043 ARMATURE_SELECTION_INVALID` and `reason=missing-target-armature`, respectively `reason=missing-part-armature`, and nothing is assembled: no generated mesh, no touched group renderer, no written merge configuration. The window does not fall back to a name, prefix or path heuristic, and the diagnostic names the selection that is missing. | |
| 12.3 | An armature outside its root blocks, and one group must agree | Select a `Target Armature` that is not under the avatar root (for example a helper armature elsewhere in the scene) and run `Validate` and a build. Select a `Part Armature` that is not under the part root and repeat. Then put two installers in one group with different target armatures and validate and build. | `APA043 ARMATURE_SELECTION_INVALID` with `reason=target-armature-outside-root` in the first case and `reason=part-armature-outside-root` in the second, each time before anything is assembled. The disagreeing group blocks with `reason=conflicting-target-armature-paths` and names the group; there is no first-one-wins and no silently shared selection. | |
| 12.4 | A pre-M10 profile is refused with `APA042` and fixed by regenerating the seam | Load a profile saved before M10 — unordered seam index sets, `PairingVersion` 0 or absent — and run `Validate`, then a build. Then press the world-position seam generate action once, read the reported pair count and preview, and press `Save Profile Asset`. | Validation and the build refuse the profile with `APA042 SEAM_PAIRING_REQUIRED reason=seam-pairing-required` and nothing is assembled; the same profile also reports `APA043` for its missing armature selections until those are set. One generate action stores the seam as explicit pairs (`PairingVersion` 1), after which the profile validates and saves. After generation the build consumes the stored pairs and never re-derives a pairing from positions: removing or corrupting the pairs restores the `APA042` refusal. | |
| 12.5 | An unreferenced bone slot no longer blocks with `APA008` | Use a mesh whose bone array contains a slot no vertex references, including a `null` entry; validate and build, then inspect the built renderer's `bones`. Then give a **weighted** bone no stable identity, then move a **weighted** bone outside the armature selected for its renderer, then zero a vertex's whole weight set (or set every influence at or below `1e-5`) and validate each time. | The unreferenced and `null` slots raise no `APA008` and do not enter the built bone table. `APA008` still blocks a **weighted** bone with no stable identity, and only weighted bones are checked. A weighted bone outside the armature selected for its renderer blocks with `APA044 BONE_OUTSIDE_SELECTED_ARMATURE reason=bone-outside-armature`, while an unweighted bone outside it does not. The zeroed vertex reports `APA031 reason=zero-weight-sum`, and an influence at or below `ApaNumericPolicy.WeightEpsilon` (default `1e-5`) is cleared to index 0 with weight 0 rather than remapped. | |
| 12.6 | The signature is captured automatically on first use | With a fresh profile whose compatibility signature has never been captured, press `Validate`. Then, from an uncaptured state again (clear the signature or recreate the profile first if the previous action captured it), press `Save Profile Asset`, then `Create Part Prefab`, then `Update Installer On Prefab`, one at a time. Finally, make the target renderer or its mesh unusable on an uncaptured profile and press `Validate`. | Each action captures the signature once at its start and then performs the action, so no first-use `APA024` and no spurious `APA012` mismatch appears. A capture that cannot run is reported as a diagnostic and leaves the profile uncaptured — no empty or partial signature is written, and the action it preceded does not proceed as if the capture had succeeded. | |
| 12.7 | An already captured signature is not overwritten | Capture a signature (first-use capture, or the explicit `Capture Signature` button), then change the body so it mismatches — swap the base mesh for one with different topology, or edit its skinning data — and press `Validate`. Then press `Capture Signature` and, separately, `Clear Signature`. | Validation blocks with the mismatch the profile has (`APA012`, `reason=bone-signature-mismatch` when skinning data is present) and the stored signature is **unchanged**: the mismatch is reported, never silently repaired by a re-capture. `Capture Signature` overwrites deliberately, and `Clear Signature` returns the profile to the uncaptured state, so the next action captures again. | |
| 12.8 | World-position seam generation on a scaled hierarchy | Put the part under an avatar level scaled non-uniformly (for example `(2, 1, 0.5)`), pair a body region with it, run the world-position generate action, and record the pair count and the preview. Change the level's scale and generate again. Run generate twice in a row with nothing changed in between. Try tolerance `1e-4`, `1e-7` and the hard limit `1e-3`; verify `1e-2` is refused. Point the part at a body region whose meshes share no coincident vertex and generate. | The pairs follow **world** distance, not avatar-local distance, so rescaling the level changes which vertices fall within tolerance. The tolerance is stated in world units, defaults to `1e-4` (0.1 mm) and accepts `1e-7` through `1e-3`; larger values report `APA022 INVALID_EPSILON` with `reason=seam-tolerance-too-large`. The pairing is one-to-one — no target vertex is claimed twice — and identical on two consecutive runs. When the meshes share no world-coincident vertex, the generator reports `APA002 SEAM_POSITION_MISMATCH reason=no-world-coincident-vertices` instead of writing an empty or partial seam. | |
| 12.9 | The collapsed removal address list | With a removal set far larger than the display cap (apply a mask, or add addresses), open **Removal Region** and read the list header, then expand it and remove one of the drawn rows. Edit the same set through the numeric address field and through each mask Apply Mode (`Replace Selection`, `Add To Selection`, `Subtract From Selection`). | Collapsed, the list reads `Addresses (N)` with the true count. Expanded, it draws at most 28 rows — each one still removable — plus a summary of how many addresses are not shown, so nothing about the underlying set is truncated: the numeric address field and all three Apply Modes still edit the whole selection. The 28-row cap is this list's foldout limit only; the shared `MaxListedRows` (200) used by the other lists is unchanged. | |
| 12.10 | Both UI languages | With the M10 surfaces in view (both armature pickers, `Suggest Armatures`, the seam tolerance, pair count and preview, the world-position generate action, the collapsed address list) and with a failure forced for `APA042`, `APA043` and `APA044`, switch `English` → `简体中文` and read every new or changed label, tooltip, warning and error. Switch back to `English`. | Every new or changed string is drawn in Simplified Chinese, and no raw English enum member name is shown anywhere. The `APAxxx` codes, their English mnemonic titles and every `reason=…` token stay untranslated, as group 10 requires. Switching back to English reproduces the English text byte-for-byte, including the armature picker labels, `Suggest Armatures`, the seam pair count and tolerance, the world-position generate action, the folded address list and the `APA042`/`APA043`/`APA044` descriptions. | |
| 12.11 | The body owns a path it declares but does not weight (M11) | Give the body renderer a bone list of `Hips`, `Spine` while every body vertex weights only `Hips`, and weight a part vertex to the part's own `Spine` at a different position. Run `Validate`, `Dry-Run Assembly`, then a build, and inspect the built renderer's `bones` and the final bone table in the report. Then weight a part vertex to a path the body does **not** declare, and separately add a second body bone with the same path as `Spine` and weight the part to that path. | The built `bones` array contains the body's `Spine` Transform — not the part's — for the part's `Spine` weight, the table entry for `Spine` is body-owned (empty owner, the body's source bone index) with the body's bind pose, and exactly one `APA007 reason=part-bone-remapped-to-target` **Info** line names the part and `redirectedBones=2`. No `APA008` appears. A path the body does not declare is still appended as a part bone, and the duplicated body path blocks with `APA008 reason=duplicate-bone-identity`; an unreferenced duplicated slot still blocks nothing. | |

---

## 13. Acceptance summary

Fill this in when every group has been run. A group with any `fail` is not accepted.

| Group | Rows | Pass | Fail | Not run |
| --- | --- | --- | --- | --- |
| 1. Compile | 5 | | | |
| 2. EditMode suite | 8 | | | |
| 3. Preview | 16 | | | |
| 4. Build | 19 | | | |
| 5. Deletion / restoration | 6 | | | |
| 6. Determinism | 7 | | | |
| 7. Schema migration (v2/v3/v4 → v5) | 7 | | | |
| 8. Performance | 5 | | | |
| 9. Ecosystem | 11 | | | |
| 10. Language | 7 | | | |
| 11. Texture mask | 9 | | | |
| 12. Armature / seam (M10, plus the M11 body-authority row 12.11) | 11 | | | |

Verdict: ☐ accepted as release candidate ☐ accepted with recorded exceptions
☐ not accepted

Tested by: ____________________  Date: ____________  Unity version: ____________
Package version: ____________  NDMF: ____________  Modular Avatar: ____________
VRChat SDK: ____________

---

## Facts reconciled against the frozen package

This checklist originated in an isolated documentation copy while the M7 code integration ran
elsewhere. The statements below were later reconciled against the package and remain useful
for the current `0.3.0-rc.6` candidate; where runtime validation has since occurred, the
newer status at the top of this file takes precedence.

1. **Assembly references.** The asmdefs are wired: `dev.avatar-part-assembler.editor.ndmf`
   references `dev.avatar-part-assembler.editor.preview`, and the Tests asmdef references
   `runtime`, `editor`, `preview`, `editor.ndmf`, `nadena.dev.ndmf`, and the two TestRunner
   assemblies. The graph is acyclic (`runtime → editor → preview → ndmf → tests`), and
   `AssemblyGraphContractTests` pins it. Checks 1.2/1.3 are still worth running: they
   confirm Unity loads the assemblies, which no source check can.
2. **Preview registration.** The registration is wired in `ApaNdmfPlugin.Configure`:
   `seq.Run(ApaAssemblyPass.Instance).PreviewingWith(ApaPreviewRegistration.CreateFilter())`
   on the real Transforming pass. `PreviewStaticContractTests` locks the statement in
3. **Version string.** `package.json` now reads **`0.3.0-rc.6`**, matching this document and
   the README.
4. **Test count.** The suite is part of this release candidate. No count is quoted in
   prose anywhere; check 2.3 compares the executed count against the `[Test]` methods in
   the sources you actually have.
5. **Error-code registry.** The codes are consolidated, not duplicated: `ApaErrorCode`
   is the single allocation table and declares `APA033`, `APA034`, `APA041`, and `APA050`
   beside the core codes, `ApaReservedCodes.Milestone5Authoring` records the M5 authoring
   codes and `ApaReservedCodes.Milestone9Authoring` records `APA041`, and
   `Editor/Authoring/ApaAuthoringErrorCode.cs` carries aliases (not a second table) and
   delegates titles. M10's `APA042`, `APA043`, and `APA044` are **core** codes recorded in
   `ApaReservedCodes.Milestone10` and reachable through `IsMilestone10Code`; the next free
   codes are `APA045`–`APA049`. The README's registry section documents all of them.
6. **`IsModularAvatarAvailable` is still a declarative capability check.** It is a
   hard-coded `true` (`Editor/Integration/MergeArmatureGenerator.cs`), justified by
   `package.json` declaring Modular Avatar as a required VPM dependency. It does **not**
   probe the installed package: if Modular Avatar is missing or stripped from a project
   without VPM resolving the dependency, the invariant is asserted rather than verified.
   The build-time consequences of a missing Modular Avatar are still caught at runtime by
   check 4.14 (the un-consumed configuration blocks the build).
7. **Compatibility severity is settled in code.** A bone-signature mismatch with skinning
   data present blocks (`APA012 reason=bone-signature-mismatch`); the same difference with
   nothing skinned stays an advisory warning (`reason=bone-signature-advisory`). Both
   sides live in `Editor/Validation/Rules/CompatibilityRule.cs`. Check 6.7 is the runtime
   observation of the blocking half.
8. **Everything behavioural is still unobserved.** Pass order, merge verification, cache
   bounds, the policy tables, and preview/build equality were read from sources, never
   executed. Rows 4.13, 4.14, 3.15, 8.4, and 4.2 are the ones that decide whether those
   claims hold, and none of them has a result until you run it.
9. **The localization layer (M8) is Editor-only and writes no asset.** The preference is
   `EditorPrefs["dev.avatar-part-assembler/localization/language"]`; the string table is
   `Editor/Localization/ApaLocalizationChinese.cs`; the Runtime assembly is unchanged and
   still contains no `UnityEditor`. `LocalizationContractTests` pins the English fallback,
   the placeholder parity of every entry, the preference key, the enum display map, the
   preserved `APAxxx` / `reason=…` tokens, and — by scanning the sources — that no
   user-visible English literal in the authoring UI bypasses the layer. Whether the switch
   *looks* right in a running editor is group 10, and only your run answers it.
10. **The M10 facts are settled in code, and unobserved in the Editor.** The profile is
    schema **4** with `TargetArmaturePath` / `PartArmaturePath` and
    `ApaSeamProfile.PairingVersion`; `TryMigrate` accepts 2, 3, and 4 as a no-op and writes
    nothing, while a version-3 profile is refused at build time by `APA043` and `APA042`
    until it is re-authored. Bone identity is the path relative to the armature selected for
    that side, the seam is generated once from world positions into explicit pairs, and only
    weighted bones are required to have an identity
    (`ApaNumericPolicy.WeightEpsilon`, default `1e-5`). Rows 12.1–12.10 are the runtime
    observation of all of it, and none of them has a result until you run it.

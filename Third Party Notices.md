# Third Party Notices

Avatar Part Assembler is an independent plugin. It links against, but does not
redistribute, the following packages. They are resolved by VPM at install time and
remain under their own licenses.

| Package | Version verified against | Role | License |
| --- | --- | --- | --- |
| `com.vrchat.avatars` | 3.10.4 | Target platform | [VRChat SDK license](https://vrchat.com/legal) |
| `nadena.dev.ndmf` | 1.14.0 | Build lifecycle (Generating/Transforming passes, error report, object registry, preview session and render filters) | MIT |
| `nadena.dev.modular-avatar` | 1.18.0-beta.0 | Transient armature merge (`ModularAvatarMergeArmature`) | MIT |
| `com.unity.modules.animation` | Unity 2022.3.22f1 built-in | Required by the package manifest | Unity Companion License |
| `com.unity.test-framework` | 1.1.x shipped with 2022.3 | Runs the EditMode suite. A **project** dependency, not declared by this package's `package.json` | Unity Companion License |

The versions above are the ones the release candidate was reviewed against, read from
the installed sources under `Packages/`. A different version is an unreviewed
combination: the APIs below are version-specific.

## API surface relied upon

### From `nadena.dev.ndmf` (1.14.0)

Plugin and pass authoring:

- `[assembly: ExportsPlugin(typeof(...))]`
- `nadena.dev.ndmf.Plugin<T>` — `QualifiedName`, `DisplayName`, `Configure()`
- `nadena.dev.ndmf.fluent.Sequence` — `InPhase(BuildPhase)`, `Run(IPass)`,
  `AfterPlugin(string)`, `PreviewingWith(params IRenderFilter[])`
- `nadena.dev.ndmf.BuildPhase.Generating`, `BuildPhase.Transforming`, and the phase
  ordering in `BuildPhase.BuiltInPhases`
- `nadena.dev.ndmf.Pass<T>` — `DisplayName`, `Execute(BuildContext)`
- `nadena.dev.ndmf.BuildContext` — `AvatarRootObject`, `Successful`, `ObjectRegistry`,
  `GetState<T>()`
- `nadena.dev.ndmf.IObjectRegistry` — `GetReference(Object)`,
  `RegisterReplacedObject(Object, Object)`

Diagnostics:

- `nadena.dev.ndmf.ErrorReport.ReportError(IError)`
- `nadena.dev.ndmf.ErrorSeverity.{Information, NonFatal, Error}`
- `nadena.dev.ndmf.SimpleError`
- `nadena.dev.ndmf.localization.Localizer`

Preview:

- `nadena.dev.ndmf.preview.IRenderFilter` — `IsEnabled`, `GetTargetGroups`,
  `Instantiate`, `WhatChanged`, `Refresh`, `OnFrame`, `OnFrameGroup`, `Dispose`
- `nadena.dev.ndmf.preview.IRenderFilterNode`
- `nadena.dev.ndmf.preview.RenderGroup`
- `nadena.dev.ndmf.preview.ComputeContext`
- `nadena.dev.ndmf.preview.TogglablePreviewNode.Create`
- `nadena.dev.ndmf.preview.PreviewSession.Current` and `AddMutator`

One call publishes the preview filter, and it is the only registration path:

```csharp
seq.Run(ApaAssemblyPass.Instance).PreviewingWith(ApaPreviewRegistration.CreateFilter());
```

### From `nadena.dev.modular-avatar` (1.18.0-beta.0)

All Modular Avatar API calls live in one file,
`Editor/Integration/MergeArmatureGenerator.cs`, so a dependency upgrade has one file to
review:

- `nadena.dev.modular_avatar.core.ModularAvatarMergeArmature`
  - the public **field** `mergeTarget` of type `AvatarObjectReference`
  - the public field `LockMode` of type `ArmatureLockMode`
  - the public **fields** `prefix` and `suffix` (the merge-name policy written from the
    profile)
  - the public method `InferPrefixSuffix()`, called when the profile sets
    `InferMergeNames`
- `nadena.dev.modular_avatar.core.AvatarObjectReference.Set(GameObject)`
- `nadena.dev.modular_avatar.core.ArmatureLockMode.NotLocked`

`InferPrefixSuffix()` is public in 1.18.0-beta.0 and **is** part of this package's
integration surface: the build calls it on the transient configuration when the profile's
bone policy asks for inference (this package creates the component itself, so Modular
Avatar's editor UI never sees it). In this version the method reads a static
`boneNamePatterns` table that Modular Avatar's editor assembly injects at load, so the
call is wrapped in an exception boundary: a throw keeps the empty prefix/suffix, which is
the documented exact-name rule, and is reported as one non-blocking
`APA012 reason=merge-name-inference-failed` warning rather than being allowed to abort
the Generating pass.

Members that **do not exist** in 1.18.0-beta.0 and are therefore not used, recorded so
a future reader does not "restore" them: `mergeBones`, `mergeBlendShapes`,
`mergeGeometry`, `renameBeforeMerge`, `boneMap`, `AllowedBones`, and a settable
`mergeTargetObject` property.

`ModularAvatarMergeArmature` is `[ExecuteInEditMode]`, and Modular Avatar destroys the
component while consuming it (`MergeArmatureHook`). The transient configuration this
package creates is therefore never retained past the pass that created it, and the
component is created without recording undo state.

### Not used

`nadena.dev.ndmf`'s `Dependencies~` payload, its localization resource pipeline beyond
the `Localizer` type, and Modular Avatar's editor-only inspectors and `Setup Outfit` are
not part of this package's integration surface. (`InferPrefixSuffix()` **is** used; see
above.)

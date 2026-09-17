using System;
using System.Collections.Generic;
using AvatarPartAssembler;
using nadena.dev.ndmf;
using nadena.dev.ndmf.config;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarPartAssembler.Editor.Ndmf
{
    /// <summary>
    /// Makes ordinary Unity Play Mode (including Gesture Manager) see the same NDMF-built avatar that a real
    /// VRChat build sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// APA temporarily enables NDMF Apply On Play whenever a loaded scene contains an APA installer. In addition,
    /// <see cref="ApaPlayModeScenePrebuild"/> runs NDMF directly on Unity's temporary Play Mode scene copy through
    /// <see cref="IProcessSceneWithReport"/>. That callback happens before scene components receive Awake/Start,
    /// so Gesture Manager cannot pose the armature before APA captures and assembles the mesh.
    /// </para>
    /// <para>
    /// This ordering is important for skinned meshes. Rebuilding after Gesture Manager or another emulator has
    /// already moved the bones can mix a T-pose vertex basis with bind data captured from a posed armature, which
    /// produces severe deformation on the next Animator update. APA therefore never performs a second build from
    /// EnteredPlayMode and never uses Animator.Rebind as a late-build recovery path.
    /// </para>
    /// <para>
    /// SessionState carries the Apply On Play restore information across Unity's Play Mode domain reload. The
    /// prebuild mutates only Unity's temporary Play Mode scene copy; authoring scenes, prefabs, profiles, and model
    /// assets are not written. The feature can be disabled from the Tools menu when raw authoring-state Play Mode
    /// behavior is desired.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    internal static class ApaPlayModeCompatibility
    {
        private const string MenuPath =
            "Tools/Avatar Part Assembler/Play Mode + Gesture Manager Compatibility";

        private const string EnabledPreferenceKey =
            "AvatarPartAssembler.PlayModeCompatibility.Enabled";

        private const string SessionArmedKey =
            "AvatarPartAssembler.PlayModeCompatibility.SessionArmed";

        private const string SessionForcedKey =
            "AvatarPartAssembler.PlayModeCompatibility.SessionForced";

        private const string SessionOriginalApplyOnPlayKey =
            "AvatarPartAssembler.PlayModeCompatibility.OriginalApplyOnPlay";

        private static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPreferenceKey, true);
            set => EditorPrefs.SetBool(EnabledPreferenceKey, value);
        }

        internal static bool IsEnabled => Enabled;

        static ApaPlayModeCompatibility()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // A script/domain reload while already back in Edit Mode can leave SessionState populated before the
            // EnteredEditMode callback from the old domain had a chance to run. Recover that transient state on
            // the next editor tick. During a genuine Play Mode transition isPlayingOrWillChangePlaymode is true,
            // so we do not undo the setting before NDMF's Awake-time processor gets to use it.
            EditorApplication.delayCall += RestoreIfStrandedInEditMode;
        }

        [MenuItem(MenuPath, false, 2150)]
        private static void ToggleCompatibility()
        {
            Enabled = !Enabled;
            Menu.SetChecked(MenuPath, Enabled);
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateCompatibilityMenu()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    ArmForPlayMode();
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    RestoreNdmfApplyOnPlay();
                    break;
            }
        }

        private static void ArmForPlayMode()
        {
            if (!Enabled || !SceneContainsApaInstaller())
            {
                // If a previous transition was interrupted after APA forced Apply On Play, restore that value
                // rather than merely forgetting the restore record.
                RestoreNdmfApplyOnPlay();
                return;
            }

            // Do not overwrite a restore record if Unity reports the transition more than once.
            if (SessionState.GetBool(SessionArmedKey, false)) return;

            var original = Config.ApplyOnPlay;
            var forced = !original;

            SessionState.SetBool(SessionArmedKey, true);
            SessionState.SetBool(SessionForcedKey, forced);
            SessionState.SetBool(SessionOriginalApplyOnPlayKey, original);

            if (!forced) return;

            Config.ApplyOnPlay = true;
            Debug.Log(
                "[Avatar Part Assembler] Enabled NDMF Apply On Play for this Play Mode session so Gesture " +
                "Manager and ordinary Play Mode use the built APA/Modular Avatar result. The previous NDMF " +
                "setting will be restored automatically when Play Mode ends.");
        }

        private static void RestoreIfStrandedInEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            RestoreNdmfApplyOnPlay();
        }

        private static void RestoreNdmfApplyOnPlay()
        {
            if (!SessionState.GetBool(SessionArmedKey, false)) return;

            var forced = SessionState.GetBool(SessionForcedKey, false);
            var original = SessionState.GetBool(SessionOriginalApplyOnPlayKey, false);

            if (forced && Config.ApplyOnPlay != original)
            {
                Config.ApplyOnPlay = original;
            }

            ClearSessionState();
        }

        private static void ClearSessionState()
        {
            SessionState.EraseBool(SessionArmedKey);
            SessionState.EraseBool(SessionForcedKey);
            SessionState.EraseBool(SessionOriginalApplyOnPlayKey);
        }

        /// <summary>
        /// True when at least one APA installer belongs to a loaded, non-preview scene. Prefab assets returned by
        /// Resources.FindObjectsOfTypeAll have an invalid scene and are intentionally ignored; opening a prefab in
        /// isolation must not change the project's Play Mode behavior.
        /// </summary>
        private static bool SceneContainsApaInstaller()
        {
            var installers = Resources.FindObjectsOfTypeAll<AvatarPartInstaller>();
            for (var i = 0; i < installers.Length; i++)
            {
                var installer = installers[i];
                if (installer == null) continue;

                var scene = installer.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;

                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Prebuilds APA avatars on Unity's temporary Play Mode scene copy before any scene component receives Awake.
    /// This is deliberately earlier than Gesture Manager / avatar emulator initialization and earlier than
    /// VRCFury's own Play Mode scene processor (int.MinValue + 100).
    /// </summary>
    internal sealed class ApaPlayModeScenePrebuild : IProcessSceneWithReport
    {
        public int callbackOrder => int.MinValue + 50;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!Application.isPlaying) return;
            if (!ApaPlayModeCompatibility.IsEnabled) return;
            if (!Config.ApplyOnPlay) return;
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) return;

            var avatarRoots = CollectApaAvatarRoots(scene);
            for (var i = 0; i < avatarRoots.Count; i++)
            {
                PreprocessAvatarBeforeAwake(avatarRoots[i]);
            }
        }

        private static List<GameObject> CollectApaAvatarRoots(Scene scene)
        {
            var result = new List<GameObject>();
            var sceneRoots = scene.GetRootGameObjects();

            for (var rootIndex = 0; rootIndex < sceneRoots.Length; rootIndex++)
            {
                var installers = sceneRoots[rootIndex].GetComponentsInChildren<AvatarPartInstaller>(true);
                for (var installerIndex = 0; installerIndex < installers.Length; installerIndex++)
                {
                    var installer = installers[installerIndex];
                    if (installer == null || !installer.IsActiveForBuild) continue;

                    var avatarRoot = FindNearestAvatarRoot(installer.transform);
                    if (avatarRoot == null || result.Contains(avatarRoot)) continue;
                    result.Add(avatarRoot);
                }
            }

            return result;
        }

        private static GameObject FindNearestAvatarRoot(Transform from)
        {
            for (var current = from; current != null; current = current.parent)
            {
                if (ContextBuilder.IsAvatarBoundary(current.gameObject)) return current.gameObject;
            }

            return null;
        }

        private static void PreprocessAvatarBeforeAwake(GameObject avatarRoot)
        {
            if (avatarRoot == null) return;
            if (ContextBuilder.CollectInstallers(avatarRoot).Count == 0) return;

            var avatarName = avatarRoot.name;

            try
            {
                // Run NDMF directly here instead of invoking the whole VRChat preprocess callback chain.
                // VRCFury patches VRCBuildPipelineCallbacks.OnPreprocessAvatar so it can only run once per
                // Play Mode object; invoking that chain from this early scene callback can therefore be rejected
                // before NDMF/APA gets a chance to execute. Direct NDMF processing still runs Modular Avatar and
                // APA in their normal NDMF phases, but it does not consume VRCFury's one allowed preprocess pass.
                //
                // This callback runs on Unity's temporary Play Mode scene copy before scene Awake/Start, so the
                // armature is still in its authoring/rest pose when APA captures bind data and assembles the mesh.
                AvatarProcessor.ProcessAvatar(avatarRoot);

                var remaining = ContextBuilder.CollectInstallers(avatarRoot).Count;
                if (remaining > 0)
                {
                    Debug.LogWarning(
                        "[Avatar Part Assembler] Play Mode scene prebuild ran NDMF for '" + avatarName +
                        "', but " + remaining + " active APA installer(s) remain. APA was likely blocked by a " +
                        "validation/build error; inspect the NDMF/APA build report.");
                    return;
                }

                Debug.Log(
                    "[Avatar Part Assembler] Play Mode scene prebuild completed for '" + avatarName +
                    "' before Awake. Gesture Manager will receive the already-assembled avatar.");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[Avatar Part Assembler] Play Mode scene prebuild failed for '" + avatarName +
                    "' before Awake. The authoring scene was not modified.");
                Debug.LogException(exception);
            }
        }
    }
}

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// Owns the Scene View callback for every live authoring tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a registry rather than a subscription in the window.</b> The overlay only draws while
    /// <c>SceneView.duringSceneGui</c> carries a callback, and a callback subscribed from
    /// <see cref="ApaAuthoringWindow.OnEnable"/> exists exactly as long as that one window instance does: a
    /// domain reload, a window that was reloaded from a serialized layout, or a failed <c>OnEnable</c> leaves the
    /// overlays silently undrawn with nothing on screen to say why. The hook is therefore installed once per
    /// domain load by <see cref="Install"/> — the same pattern the preview debug overlay uses — and the window
    /// only registers the tool instance it owns.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> <see cref="Install"/> removes its own delegate before adding it, and
    /// <see cref="Register"/> calls it, so a tool registered before the attribute fired (a test, or a reload
    /// ordering the attribute did not cover) still gets a live callback, and a second installation cannot make
    /// the tool draw twice.
    /// </para>
    /// <para>
    /// <b>Disposed tools are dropped.</b> A tool whose window was destroyed without an <c>OnDisable</c> is
    /// skipped and removed on the next dispatch, so a stale host can never be called.
    /// </para>
    /// </remarks>
    public static class ApaAuthoringSceneToolRegistry
    {
        private static readonly List<ApaAuthoringSceneTool> s_tools = new List<ApaAuthoringSceneTool>();
        private static bool s_installed;

        /// <summary>Number of tools currently registered. Used by tests and diagnostics.</summary>
        public static int RegisteredCount => s_tools.Count;

        /// <summary>True once the <c>duringSceneGui</c> hook has been installed in this domain.</summary>
        public static bool IsInstalled => s_installed;

        /// <summary>
        /// Installs the Scene View hook, once per domain load.
        /// </summary>
        /// <remarks>
        /// Public and callable so a test can install it explicitly; the attribute makes the production path
        /// independent of any window.
        /// </remarks>
        [InitializeOnLoadMethod]
        public static void Install()
        {
            s_installed = true;

            SceneView.duringSceneGui -= Dispatch;
            SceneView.duringSceneGui += Dispatch;
        }

        /// <summary>Registers a tool so the Scene View callback reaches it.</summary>
        public static void Register(ApaAuthoringSceneTool tool)
        {
            if (tool == null) return;

            Install();
            if (s_tools.Contains(tool)) return;

            s_tools.Add(tool);
        }

        /// <summary>Removes a tool from the callback.</summary>
        public static void Unregister(ApaAuthoringSceneTool tool)
        {
            if (tool == null) return;
            s_tools.Remove(tool);
        }

        /// <summary>Removes every registration. Used by tests and by teardown.</summary>
        public static void Clear()
        {
            s_tools.Clear();
        }

        /// <summary>
        /// The Scene View callback: hands the event to every registered, live tool.
        /// </summary>
        /// <remarks>
        /// Iterated backwards so a tool that turns out to be disposed can be removed in the same pass without
        /// disturbing the walk.
        /// </remarks>
        public static void Dispatch(SceneView view)
        {
            if (s_tools.Count == 0) return;

            for (var i = s_tools.Count - 1; i >= 0; i--)
            {
                var tool = s_tools[i];
                if (tool == null || tool.IsDisposed)
                {
                    s_tools.RemoveAt(i);
                    continue;
                }

                tool.OnSceneGui(view);
            }
        }
    }
}

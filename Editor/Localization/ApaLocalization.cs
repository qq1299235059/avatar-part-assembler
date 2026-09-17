using System;
using System.Collections.Generic;
using AvatarPartAssembler.Editor.Authoring;
using UnityEditor;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Localization
{
    /// <summary>
    /// The language the user selected for the plugin's user interface.
    /// </summary>
    /// <remarks>
    /// The enum is a preference, not a resolved language: <see cref="Auto"/> resolves through
    /// <see cref="ApaLocalization.Resolve"/> at read time, so a machine whose system language changes between
    /// sessions picks up the new language without the stored preference changing.
    /// </remarks>
    public enum ApaLanguage
    {
        /// <summary>Follow <see cref="Application.systemLanguage"/>: Chinese systems get Simplified Chinese.</summary>
        Auto = 0,

        /// <summary>English. The fallback language, and the default when no preference has been stored.</summary>
        English = 1,

        /// <summary>Simplified Chinese (简体中文).</summary>
        SimplifiedChinese = 2
    }

    /// <summary>
    /// The package's lightweight localization layer: one language preference, one string lookup, and the label
    /// helpers the editor UI needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>English is the key, so English cannot go missing.</b> Every translatable string is looked up by its
    /// English text, and <see cref="Translate"/> returns that text unchanged whenever the target language is
    /// English or the entry is absent. There is therefore no such thing as a missing English string: a key that
    /// was never translated still renders exactly the English sentence the source carries, and a partially
    /// translated table degrades string by string rather than failing. The English text is also the reviewable
    /// source of truth in the UI code.
    /// </para>
    /// <para>
    /// <b>No project asset is written.</b> The preference is stored in <see cref="EditorPrefs"/> under
    /// <see cref="LanguagePrefsKey"/>, which is per-user editor state. A language choice must never dirty a
    /// scene, a profile, or the package, so nothing here touches <c>AssetDatabase</c>, <c>EditorUtility</c>, or
    /// any serialized field. <see cref="ApaLanguage.Auto"/> is stored as a value like any other, so selecting it
    /// is itself persistent.
    /// </para>
    /// <para>
    /// <b>No external dependency.</b> The layer is a dictionary lookup plus a handful of switch statements, so it
    /// adds no package dependency (Unity Localization in particular) and works in an Editor-only assembly.
    /// </para>
    /// <para>
    /// <b>Dynamic text stays as it is.</b> Validation messages, exception text, asset names, hierarchy paths, and
    /// the stable <c>APAxxx</c> codes and <c>reason=</c> tokens are data, not UI copy. They are rendered
    /// unchanged in both languages so that a Chinese user's bug report carries the same searchable tokens as an
    /// English one; the localization layer localizes the labels, severities, section titles, and help text around
    /// them.
    /// </para>
    /// </remarks>
    public static class ApaLocalization
    {
        /// <summary>
        /// Stable <see cref="EditorPrefs"/> key that stores the language preference.
        /// </summary>
        /// <remarks>
        /// The key is part of the persisted contract: renaming it silently resets every user's choice to the
        /// default. It follows the package's reverse-DNS id and lives under a <c>localization/</c> segment so a
        /// future setting cannot collide with it.
        /// </remarks>
        public const string LanguagePrefsKey = "dev.avatar-part-assembler/localization/language";

        /// <summary>The name of English, spelled in English. Language names are never translated.</summary>
        public const string EnglishDisplayName = "English";

        /// <summary>The name of Simplified Chinese, spelled in Chinese. Language names are never translated.</summary>
        public const string SimplifiedChineseDisplayName = "简体中文";

        /// <summary>Text returned by a lookup that has no entry in the target language.</summary>
        public const string MissingTranslationFallback = "";

        private static readonly List<string> s_translationKeys = new List<string>(ApaLocalizationChinese.Strings.Keys);
        private static readonly string[] s_languageOptions =
        {
            "Auto",
            EnglishDisplayName,
            SimplifiedChineseDisplayName
        };

        private static bool s_loaded;
        private static ApaLanguage s_language = ApaLanguage.English;

        /// <summary>
        /// Raised after the preference changed and the cached resolution was refreshed.
        /// </summary>
        /// <remarks>
        /// Subscribers are surfaces that cannot be reached by a repaint — a cached GUIContent, a static style, or
        /// an open wizard. The authoring window and the installer inspector do not need it: the setter repaints
        /// every open editor window, which is what makes a switch visible immediately.
        /// </remarks>
        public static event Action Changed;

        /// <summary>
        /// The stored language preference.
        /// </summary>
        /// <remarks>
        /// Reading loads the preference from <see cref="EditorPrefs"/> once per domain load. Assigning persists
        /// it, refreshes the resolved language, repaints every open editor window and the Scene View so the
        /// switch is immediate, and then raises <see cref="Changed"/>.
        /// </remarks>
        public static ApaLanguage Language
        {
            get
            {
                EnsureLoaded();
                return s_language;
            }
            set => SetLanguage(value);
        }

        /// <summary>The language the UI is actually rendered in, with <see cref="ApaLanguage.Auto"/> resolved.</summary>
        public static ApaLanguage ResolvedLanguage => Resolve(Language, Application.systemLanguage);

        /// <summary>True when the resolved language is Simplified Chinese.</summary>
        public static bool IsSimplifiedChinese => ResolvedLanguage == ApaLanguage.SimplifiedChinese;

        /// <summary>Number of entries the Simplified Chinese table carries.</summary>
        public static int TranslationCount => s_translationKeys.Count;

        /// <summary>
        /// Every English key the Simplified Chinese table carries, in table order. Used by the contract tests so
        /// that coverage can be asserted without reaching into the assembly.
        /// </summary>
        public static IReadOnlyList<string> TranslationKeys => s_translationKeys;

        /// <summary>True when the Simplified Chinese table carries an entry for an English string.</summary>
        public static bool HasTranslation(string english)
        {
            return !string.IsNullOrEmpty(english) && ApaLocalizationChinese.Strings.ContainsKey(english);
        }

        /// <summary>
        /// Resolves a preference against a system language.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="ApaLanguage.Auto"/> answers Chinese for <see cref="SystemLanguage.ChineseSimplified"/> and
        /// for the generic <see cref="SystemLanguage.Chinese"/> value, which is what Unity reports on a Chinese
        /// system whose script variant it cannot distinguish. Every other system language answers English, which
        /// is the package's fallback. Traditional Chinese is deliberately <b>not</b> mapped onto the Simplified
        /// table: the two scripts differ in more than glyph shapes, and showing a Traditional reader Simplified
        /// copy would misrepresent the translation rather than serve it.
        /// </para>
        /// <para>
        /// The function is pure so that the auto-selection rule is testable without an editor locale.
        /// </para>
        /// </remarks>
        public static ApaLanguage Resolve(ApaLanguage preference, SystemLanguage systemLanguage)
        {
            switch (preference)
            {
                case ApaLanguage.English:
                    return ApaLanguage.English;
                case ApaLanguage.SimplifiedChinese:
                    return ApaLanguage.SimplifiedChinese;
                default:
                    return systemLanguage == SystemLanguage.ChineseSimplified
                           || systemLanguage == SystemLanguage.Chinese
                        ? ApaLanguage.SimplifiedChinese
                        : ApaLanguage.English;
            }
        }

        /// <summary>
        /// Translates an English string into the resolved language, returning the English text itself when the
        /// target language is English or the entry is absent.
        /// </summary>
        public static string Translate(ApaLanguage resolved, string english)
        {
            if (string.IsNullOrEmpty(english)) return english ?? string.Empty;
            if (resolved != ApaLanguage.SimplifiedChinese) return english;

            string translated;
            return ApaLocalizationChinese.Strings.TryGetValue(english, out translated) && !string.IsNullOrEmpty(translated)
                ? translated
                : english;
        }

        /// <summary>
        /// Translates a composite format string and formats it.
        /// </summary>
        /// <remarks>
        /// A translated entry must carry the same placeholders as its English key; the contract test asserts that,
        /// and <see cref="string.Format(string, object[])"/> would throw on a mismatch, so the failure would be
        /// loud rather than silent. English formatting is byte-identical to the pre-localization output, which is
        /// what keeps the existing tests and the diagnostic contract intact.
        /// </remarks>
        public static string TranslateFormat(ApaLanguage resolved, string englishFormat, params object[] args)
        {
            var format = Translate(resolved, englishFormat);
            if (args == null || args.Length == 0) return format;
            return string.Format(format, args);
        }

        /// <summary>Translates an English string into the currently resolved language.</summary>
        public static string Tr(string english)
        {
            return Translate(ResolvedLanguage, english);
        }

        /// <summary>Translates and formats a composite English format string.</summary>
        public static string TrFormat(string englishFormat, params object[] args)
        {
            return TranslateFormat(ResolvedLanguage, englishFormat, args);
        }

        /// <summary>
        /// Builds a localized <see cref="GUIContent"/> from English text and an optional English tooltip.
        /// </summary>
        public static GUIContent Content(string english, string tooltip = null)
        {
            var resolved = ResolvedLanguage;
            var text = Translate(resolved, english);
            if (string.IsNullOrEmpty(tooltip)) return new GUIContent(text);
            return new GUIContent(text, Translate(resolved, tooltip));
        }

        /// <summary>The display name of a language option: the Auto label, or the language's own name.</summary>
        public static string LanguageDisplayName(ApaLanguage language)
        {
            switch (language)
            {
                case ApaLanguage.SimplifiedChinese:
                    return SimplifiedChineseDisplayName;
                case ApaLanguage.English:
                    return EnglishDisplayName;
                default:
                    return Tr("Auto (follow system)");
            }
        }

        /// <summary>
        /// Draws the language selector used by the authoring window's toolbar and the installer inspector.
        /// </summary>
        /// <param name="label">
        /// The field label, or null for the toolbar form, which has no room for one. The labels themselves are
        /// never translated: a language is listed under its own name so a user who cannot read the current
        /// language can still find their own.
        /// </param>
        /// <remarks>
        /// The selector writes the preference through <see cref="Language"/>, which repaints every open editor
        /// window, so the control the user just clicked is redrawn in the new language on the same frame.
        /// </remarks>
        public static ApaLanguage LanguagePopup(GUIContent label, params GUILayoutOption[] options)
        {
            var current = Language;
            var labels = new string[s_languageOptions.Length];
            for (var i = 0; i < labels.Length; i++)
            {
                labels[i] = LanguageDisplayName((ApaLanguage)i);
            }

            var index = (int)current;
            if (index < 0 || index >= labels.Length) index = (int)ApaLanguage.English;

            var picked = label == null
                ? EditorGUILayout.Popup(index, labels, options)
                : EditorGUILayout.Popup(label, index, labels, options);

            if (picked == index) return current;

            Language = (ApaLanguage)picked;
            return Language;
        }

        // ---- Enum display names ----------------------------------------------------------------------------

        /// <summary>
        /// The localized display name of a part slot. The enum member and the serialized value are never changed;
        /// only what the popup and the inspector print.
        /// </summary>
        public static string DisplayName(ApaPartSlot slot)
        {
            switch (slot)
            {
                case ApaPartSlot.Head: return Tr("Head");
                case ApaPartSlot.Torso: return Tr("Torso");
                case ApaPartSlot.LeftArm: return Tr("Left Arm");
                case ApaPartSlot.RightArm: return Tr("Right Arm");
                case ApaPartSlot.LeftHand: return Tr("Left Hand");
                case ApaPartSlot.RightHand: return Tr("Right Hand");
                case ApaPartSlot.LeftLeg: return Tr("Left Leg");
                case ApaPartSlot.RightLeg: return Tr("Right Leg");
                case ApaPartSlot.LeftFoot: return Tr("Left Foot");
                case ApaPartSlot.RightFoot: return Tr("Right Foot");
                case ApaPartSlot.Custom: return Tr("Custom");
                default: return slot + " (" + Tr("undefined") + ")";
            }
        }

        /// <summary>The localized display name of a part slot mode.</summary>
        public static string DisplayName(ApaPartSlotMode mode)
        {
            switch (mode)
            {
                case ApaPartSlotMode.Replace: return Tr("Replace");
                case ApaPartSlotMode.Augment: return Tr("Augment");
                default: return mode + " (" + Tr("undefined") + ")";
            }
        }

        /// <summary>The localized display name of a material policy.</summary>
        public static string DisplayName(ApaMaterialPolicyMode policy)
        {
            switch (policy)
            {
                case ApaMaterialPolicyMode.Auto: return Tr("Auto");
                case ApaMaterialPolicyMode.UseTarget: return Tr("Use Target");
                case ApaMaterialPolicyMode.KeepPart: return Tr("Keep Part");
                case ApaMaterialPolicyMode.ForceNew: return Tr("Force New");
                default: return policy + " (" + Tr("undefined") + ")";
            }
        }

        /// <summary>The localized display name of a Scene View picking mode.</summary>
        /// <remarks>
        /// Only the removal-triangle mode is pickable since M10: the seam-vertex modes are gone, because a seam is
        /// generated from world positions rather than clicked vertex by vertex.
        /// </remarks>
        public static string DisplayName(ApaSceneToolMode mode)
        {
            switch (mode)
            {
                case ApaSceneToolMode.RemovalTriangles: return Tr("Pick Removal Triangles");
                default: return Tr("Off");
            }
        }

        /// <summary>
        /// The localized display name of a texture-mask apply mode.
        /// </summary>
        /// <remarks>
        /// The serialized enum members stay <c>ReplaceSelection</c>, <c>AddToSelection</c>, and
        /// <c>SubtractFromSelection</c>; what the popup shows — and what the documentation names — is these three
        /// labels. An out-of-domain value is reported as undefined rather than silently rewritten to a legal one,
        /// exactly as the other enum popups do.
        /// </remarks>
        public static string DisplayName(ApaMaskApplyMode mode)
        {
            switch (mode)
            {
                case ApaMaskApplyMode.ReplaceSelection: return Tr("Replace Selection");
                case ApaMaskApplyMode.AddToSelection: return Tr("Add To Selection");
                case ApaMaskApplyMode.SubtractFromSelection: return Tr("Subtract From Selection");
                default: return mode + " (" + Tr("undefined") + ")";
            }
        }

        // ---- Localized enum popups -------------------------------------------------------------------------

        private static readonly ApaPartSlot[] s_partSlots = (ApaPartSlot[])Enum.GetValues(typeof(ApaPartSlot));
        private static readonly ApaPartSlotMode[] s_partSlotModes = (ApaPartSlotMode[])Enum.GetValues(typeof(ApaPartSlotMode));
        private static readonly ApaMaterialPolicyMode[] s_materialPolicies =
            (ApaMaterialPolicyMode[])Enum.GetValues(typeof(ApaMaterialPolicyMode));
        private static readonly ApaMaskApplyMode[] s_maskApplyModes =
            (ApaMaskApplyMode[])Enum.GetValues(typeof(ApaMaskApplyMode));

        /// <summary>
        /// A slot popup with localized option labels.
        /// </summary>
        /// <remarks>
        /// A value outside the defined list is drawn as a read-only line naming the raw value rather than being
        /// silently rewritten to a legal one: an out-of-domain slot is reported as <c>APA023</c>, and replacing it
        /// here would hide that from the author. The defined cases are written back as the same enum member, so the
        /// serialized value of a legal slot is unchanged by a round trip through this control.
        /// </remarks>
        public static ApaPartSlot SlotPopup(string label, ApaPartSlot current)
        {
            var index = Array.IndexOf(s_partSlots, current);
            if (index < 0)
            {
                EditorGUILayout.LabelField(label, current + " (" + Tr("undefined") + ")");
                return current;
            }

            var labels = new string[s_partSlots.Length];
            for (var i = 0; i < labels.Length; i++) labels[i] = DisplayName(s_partSlots[i]);

            return s_partSlots[EditorGUILayout.Popup(label, index, labels)];
        }

        /// <summary>A slot-mode popup with localized option labels and a localized tooltip.</summary>
        public static ApaPartSlotMode SlotModePopup(GUIContent label, ApaPartSlotMode current)
        {
            var index = Array.IndexOf(s_partSlotModes, current);
            if (index < 0)
            {
                EditorGUILayout.LabelField(label, current + " (" + Tr("undefined") + ")");
                return current;
            }

            var labels = new string[s_partSlotModes.Length];
            for (var i = 0; i < labels.Length; i++) labels[i] = DisplayName(s_partSlotModes[i]);

            return s_partSlotModes[EditorGUILayout.Popup(label, index, labels)];
        }

        /// <summary>A material-policy popup with localized option labels, used inside a material semantic row.</summary>
        public static ApaMaterialPolicyMode MaterialPolicyPopup(
            ApaMaterialPolicyMode current,
            params GUILayoutOption[] options)
        {
            var index = Array.IndexOf(s_materialPolicies, current);
            if (index < 0)
            {
                EditorGUILayout.LabelField(current + " (" + Tr("undefined") + ")", options);
                return current;
            }

            var labels = new string[s_materialPolicies.Length];
            for (var i = 0; i < labels.Length; i++) labels[i] = DisplayName(s_materialPolicies[i]);

            return s_materialPolicies[EditorGUILayout.Popup(index, labels, options)];
        }

        /// <summary>
        /// A texture-mask apply-mode popup with localized option labels.
        /// </summary>
        /// <remarks>
        /// The picked index is mapped back through <c>Enum.GetValues</c>, so the option order and the serialized
        /// values are the same table rather than two lists that can drift apart. A current value outside the
        /// defined members is drawn as a read-only line instead of being rewritten to Replace Selection, which
        /// keeps a value written by a newer build visible rather than silently discarded.
        /// </remarks>
        public static ApaMaskApplyMode MaskApplyModePopup(GUIContent label, ApaMaskApplyMode current)
        {
            var index = Array.IndexOf(s_maskApplyModes, current);
            if (index < 0)
            {
                EditorGUILayout.LabelField(label, current + " (" + Tr("undefined") + ")");
                return current;
            }

            var labels = new string[s_maskApplyModes.Length];
            for (var i = 0; i < labels.Length; i++) labels[i] = DisplayName(s_maskApplyModes[i]);

            return s_maskApplyModes[EditorGUILayout.Popup(label, index, labels)];
        }

        // ---- Diagnostics -----------------------------------------------------------------------------------

        /// <summary>The localized severity word used by every diagnostic renderer.</summary>
        /// <remarks>
        /// English keeps the upper-case tokens (<c>ERROR</c>, <c>WARNING</c>, <c>INFO</c>) the package has always
        /// printed, so a log line is byte-identical to the pre-localization output.
        /// </remarks>
        public static string SeverityLabel(ApaSeverity severity)
        {
            return SeverityLabel(ResolvedLanguage, severity);
        }

        /// <summary>The localized severity word for an explicit language.</summary>
        public static string SeverityLabel(ApaLanguage resolved, ApaSeverity severity)
        {
            if (resolved != ApaLanguage.SimplifiedChinese)
            {
                return severity.ToString().ToUpperInvariant();
            }

            switch (severity)
            {
                case ApaSeverity.Error: return "错误";
                case ApaSeverity.Warning: return "警告";
                default: return "信息";
            }
        }

        /// <summary>
        /// A short localized description of a known <c>APAxxx</c> code, or an empty string for a code this build
        /// does not know.
        /// </summary>
        /// <remarks>
        /// This is an <i>addition</i> to the rendered diagnostic, never a replacement: the renderer keeps the raw
        /// code and the stable English mnemonic title so that a search, a bug report, or a test assertion keeps
        /// working in either language, and appends the localized description beside them.
        /// </remarks>
        public static string ErrorCodeDescription(string code)
        {
            return ErrorCodeDescription(ResolvedLanguage, code);
        }

        /// <summary>A short localized description of a code for an explicit language.</summary>
        public static string ErrorCodeDescription(ApaLanguage resolved, string code)
        {
            if (resolved != ApaLanguage.SimplifiedChinese || string.IsNullOrEmpty(code)) return string.Empty;

            string description;
            return ApaLocalizationChinese.ErrorCodeDescriptions.TryGetValue(code, out description)
                ? description
                : string.Empty;
        }

        // ---- Preference plumbing ---------------------------------------------------------------------------

        /// <summary>
        /// Assigns and persists the language preference, then repaints every open editor window.
        /// </summary>
        public static void SetLanguage(ApaLanguage language)
        {
            EnsureLoaded();

            var changed = s_language != language;
            s_language = language;

            var stored = EditorPrefs.GetInt(LanguagePrefsKey, (int)ApaLanguage.English);
            if (stored != (int)language) EditorPrefs.SetInt(LanguagePrefsKey, (int)language);

            if (!changed) return;

            RepaintAllEditorSurfaces();
            var handler = Changed;
            if (handler != null) handler();
        }

        /// <summary>
        /// Re-reads the preference from <see cref="EditorPrefs"/>, discarding the cached value.
        /// </summary>
        /// <remarks>
        /// Used by the persistence contract test to prove that the stored value survives a domain load without
        /// having to reload the domain.
        /// </remarks>
        public static void ReloadFromPreferences()
        {
            s_loaded = false;
            EnsureLoaded();
        }

        /// <summary>
        /// Repaints every surface that shows a localized string.
        /// </summary>
        /// <remarks>
        /// <see cref="SceneView.RepaintAll"/> covers the Scene View labels; the editor-window sweep covers the
        /// authoring window, the installer inspector, and anything else currently open. A language switch is a
        /// rare user action, so the cost of the sweep is irrelevant next to the promise that the switch is
        /// visible immediately.
        /// </remarks>
        private static void RepaintAllEditorSurfaces()
        {
            SceneView.RepaintAll();

            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            for (var i = 0; i < windows.Length; i++)
            {
                var window = windows[i];
                if (window != null) window.Repaint();
            }
        }

        private static void EnsureLoaded()
        {
            if (s_loaded) return;
            s_loaded = true;

            // The default is English, not Auto: the package's UI, its diagnostics, and its tests have always been
            // English, and a fresh install must not silently change what an existing user sees. Auto is one click
            // away in the authoring window and in the installer inspector, and the choice is remembered.
            var stored = EditorPrefs.GetInt(LanguagePrefsKey, (int)ApaLanguage.English);
            s_language = IsDefinedLanguage(stored) ? (ApaLanguage)stored : ApaLanguage.English;
        }

        private static bool IsDefinedLanguage(int value)
        {
            return value == (int)ApaLanguage.Auto
                   || value == (int)ApaLanguage.English
                   || value == (int)ApaLanguage.SimplifiedChinese;
        }
    }
}

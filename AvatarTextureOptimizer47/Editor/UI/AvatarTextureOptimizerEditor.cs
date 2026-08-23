using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Fosa.AvatarTextureOptimizer.Editor.UI
{
    /// <summary>
    /// EN: Beginner-oriented inspector with opt-in advanced controls. ZH: 面向新手、按需展开高级选项的 Inspector。
    /// </summary>
    [CustomEditor(typeof(Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer))]
    internal sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private static bool _commonFoldout;
        private static bool _pcFoldout;
        private static bool _androidFoldout;
        private static bool _iosFoldout;
        private static readonly Dictionary<string, bool> Advanced = new Dictionary<string, bool>();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer)target;
            var settings = serializedObject.FindProperty("settings");
            var language = settings.FindPropertyRelative("language");
            DrawLanguage(language);
            var locale = language.stringValue;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(I18nService.Tr(locale, "component.title"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(I18nService.Tr(locale, "component.help"), MessageType.Info);
            DrawPlacementValidation(component, locale);

            EditorGUILayout.PropertyField(settings.FindPropertyRelative("previewPlatform"),
                new GUIContent(I18nService.Tr(locale, "settings.platform")));

            _commonFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_commonFoldout,
                I18nService.Tr(locale, "settings.common"));
            if (_commonFoldout)
                DrawProfile(settings.FindPropertyRelative("common"), OptimizerPlatform.Auto, locale, "common");
            EditorGUILayout.EndFoldoutHeaderGroup();

            DrawOverride(settings.FindPropertyRelative("pc"), OptimizerPlatform.PC,
                I18nService.Tr(locale, "settings.override.pc"), locale, ref _pcFoldout, "pc");
            DrawOverride(settings.FindPropertyRelative("android"), OptimizerPlatform.Android,
                I18nService.Tr(locale, "settings.override.android"), locale, ref _androidFoldout, "android");
            DrawOverride(settings.FindPropertyRelative("ios"), OptimizerPlatform.IOS,
                I18nService.Tr(locale, "settings.override.ios"), locale, ref _iosFoldout, "ios");

            EditorGUILayout.Space(5);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("deduplicateTextures"),
                new GUIContent(I18nService.Tr(locale, "settings.dedupeTexture")));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("deduplicateMaterials"),
                new GUIContent(I18nService.Tr(locale, "settings.dedupeMaterial")));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("whitelist"),
                new GUIContent(I18nService.Tr(locale, "settings.whitelist")), true);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("verboseLogging"),
                new GUIContent(I18nService.Tr(locale, "settings.verbose")));

            if (serializedObject.ApplyModifiedProperties())
            {
                component.settings.Validate();
                EditorUtility.SetDirty(component);
            }
        }

        private static void DrawLanguage(SerializedProperty language)
        {
            var languages = I18nService.Languages;
            var values = new List<string> { "Auto" };
            values.AddRange(languages.Select(x => x.Locale));
            var labels = new List<string> { "Auto" };
            labels.AddRange(languages.Select(x => x.DisplayName));
            var current = Mathf.Max(0, values.FindIndex(x => x.Equals(language.stringValue, StringComparison.OrdinalIgnoreCase)));
            var selected = EditorGUILayout.Popup(I18nService.Tr(language.stringValue, "settings.language"), current, labels.ToArray());
            language.stringValue = values[Mathf.Clamp(selected, 0, values.Count - 1)];
        }

        private static void DrawPlacementValidation(Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer component, string locale)
        {
            var descriptor = component.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
                EditorGUILayout.HelpBox(I18nService.Tr(locale, "validation.root"), MessageType.Error);

            var root = descriptor != null ? descriptor.transform : component.transform.root;
            var count = root.GetComponentsInChildren<Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer>(true).Length;
            if (count != 1)
                EditorGUILayout.HelpBox(I18nService.Tr(locale, "validation.single"), MessageType.Error);
        }

        private static void DrawOverride(SerializedProperty property, OptimizerPlatform platform, string title,
            string locale, ref bool foldout, string key)
        {
            foldout = EditorGUILayout.BeginFoldoutHeaderGroup(foldout, title);
            if (foldout)
            {
                var enabled = property.FindPropertyRelative("enabled");
                EditorGUILayout.PropertyField(enabled, GUIContent.none);
                if (enabled.boolValue)
                    DrawProfile(property.FindPropertyRelative("profile"), platform, locale, key);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private static void DrawProfile(SerializedProperty profile, OptimizerPlatform platform, string locale, string key)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                var preset = profile.FindPropertyRelative("qualityPreset");
                EditorGUILayout.PropertyField(preset, new GUIContent(I18nService.Tr(locale, "settings.quality")));
                DrawQuality(profile.FindPropertyRelative("quality"), locale, key + ".quality");
                DrawDensity(profile.FindPropertyRelative("minimumPixelDensity"), I18nService.Tr(locale, "settings.densityMin"));
                DrawDensity(profile.FindPropertyRelative("maximumPixelDensity"), I18nService.Tr(locale, "settings.densityMax"));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("generateAtlases"),
                    new GUIContent(I18nService.Tr(locale, "settings.atlas")));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("experimentalNpotAtlases"),
                    new GUIContent(I18nService.Tr(locale, "settings.npot")));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("maximumAtlasSize"),
                    new GUIContent(I18nService.Tr(locale, "settings.maxAtlas")));
                EditorGUILayout.PropertyField(profile.FindPropertyRelative("minimumPadding"),
                    new GUIContent(I18nService.Tr(locale, "settings.padding")));

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(I18nService.Tr(locale, "settings.output"), EditorStyles.boldLabel);
                var npot = profile.FindPropertyRelative("experimentalNpotAtlases").boolValue;
                DrawCategory(profile.FindPropertyRelative("opaque"), TextureSemantic.ColorOpaque, platform, npot,
                    I18nService.Tr(locale, "settings.opaque"), locale);
                DrawCategory(profile.FindPropertyRelative("alpha"), TextureSemantic.ColorAlpha, platform, npot,
                    I18nService.Tr(locale, "settings.alpha"), locale);
                DrawCategory(profile.FindPropertyRelative("normal"), TextureSemantic.Normal, platform, npot,
                    I18nService.Tr(locale, "settings.normal"), locale);
                DrawCategory(profile.FindPropertyRelative("grayscale"), TextureSemantic.Grayscale, platform, npot,
                    I18nService.Tr(locale, "settings.gray"), locale);
            }
        }

        private static void DrawDensity(SerializedProperty property, string label)
        {
            var values = new[] { 512, 1024, 2048, 4096, 8192 };
            var names = values.Select(x => x + " px/m").ToArray();
            property.intValue = EditorGUILayout.IntPopup(label, property.intValue, names, values);
        }

        private static void DrawQuality(SerializedProperty quality, string locale, string key)
        {
            Advanced.TryGetValue(key, out var expanded);
            expanded = EditorGUILayout.Foldout(expanded, I18nService.Tr(locale, "settings.qualityAdvanced"), true);
            Advanced[key] = expanded;
            if (!expanded) return;
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(quality.FindPropertyRelative("structuralFidelity"),
                    new GUIContent(I18nService.Tr(locale, "quality.structural")));
                EditorGUILayout.PropertyField(quality.FindPropertyRelative("colorFidelity"),
                    new GUIContent(I18nService.Tr(locale, "quality.color")));
                EditorGUILayout.PropertyField(quality.FindPropertyRelative("alphaFidelity"),
                    new GUIContent(I18nService.Tr(locale, "quality.alpha")));
                EditorGUILayout.PropertyField(quality.FindPropertyRelative("normalFidelity"),
                    new GUIContent(I18nService.Tr(locale, "quality.normal")));
                EditorGUILayout.PropertyField(quality.FindPropertyRelative("grayscaleFidelity"),
                    new GUIContent(I18nService.Tr(locale, "quality.gray")));
            }
        }

        private static void DrawCategory(SerializedProperty category, TextureSemantic semantic,
            OptimizerPlatform platform, bool npot, string title, string locale)
        {
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(category.FindPropertyRelative("mipmapsAndStreaming"),
                    new GUIContent(I18nService.Tr(locale, "settings.mipmap")));
                DrawSafeFormat(category.FindPropertyRelative("compression"), semantic, platform, npot,
                    I18nService.Tr(locale, "settings.format"));
            }
        }

        private static void DrawSafeFormat(SerializedProperty property, TextureSemantic semantic,
            OptimizerPlatform platform, bool npot, string label)
        {
            var allowed = GetAllowedFormats(semantic, platform, npot);
            var current = (SafeTextureFormat)property.enumValueIndex;
            var index = Mathf.Max(0, allowed.IndexOf(current));
            var selected = EditorGUILayout.Popup(label, index, allowed.Select(x => x.ToString()).ToArray());
            property.enumValueIndex = (int)allowed[Mathf.Clamp(selected, 0, allowed.Count - 1)];
        }

        internal static List<SafeTextureFormat> GetAllowedFormats(TextureSemantic semantic,
            OptimizerPlatform platform, bool npot)
        {
            var formats = new List<SafeTextureFormat>
                { SafeTextureFormat.Automatic, SafeTextureFormat.UncompressedRGBA32 };
            if (platform == OptimizerPlatform.PC || platform == OptimizerPlatform.Auto)
            {
                if (semantic == TextureSemantic.Normal) formats.Add(SafeTextureFormat.BC5);
                else if (semantic == TextureSemantic.ColorOpaque || semantic == TextureSemantic.Grayscale)
                    formats.Add(SafeTextureFormat.BC1);
                if (semantic != TextureSemantic.Normal) formats.Add(SafeTextureFormat.BC3);
                formats.Add(SafeTextureFormat.BC7);
                if (semantic == TextureSemantic.ColorOpaque || semantic == TextureSemantic.Grayscale)
                    formats.Add(SafeTextureFormat.DXT1Crunched);
                if (semantic == TextureSemantic.ColorAlpha) formats.Add(SafeTextureFormat.DXT5Crunched);
            }
            if (platform == OptimizerPlatform.Android || platform == OptimizerPlatform.IOS || platform == OptimizerPlatform.Auto)
            {
                formats.Add(SafeTextureFormat.ASTC4x4);
                formats.Add(SafeTextureFormat.ASTC6x6);
                formats.Add(SafeTextureFormat.ASTC8x8);
            }
            if (platform == OptimizerPlatform.Android || platform == OptimizerPlatform.Auto)
            {
                if (semantic == TextureSemantic.ColorOpaque || semantic == TextureSemantic.Grayscale)
                    formats.Add(SafeTextureFormat.ETC2RGB);
                if (semantic != TextureSemantic.Normal) formats.Add(SafeTextureFormat.ETC2RGBA8);
                if (semantic == TextureSemantic.ColorOpaque || semantic == TextureSemantic.Grayscale)
                    formats.Add(SafeTextureFormat.ETC1Crunched);
                if (semantic == TextureSemantic.ColorAlpha) formats.Add(SafeTextureFormat.ETC2RGBA8Crunched);
            }
            if (platform == OptimizerPlatform.IOS && !npot)
            {
                if (semantic == TextureSemantic.ColorOpaque || semantic == TextureSemantic.Grayscale)
                    formats.Add(SafeTextureFormat.PVRTCRGB4);
                if (semantic != TextureSemantic.Normal) formats.Add(SafeTextureFormat.PVRTCRGBA4);
            }
            return formats.Distinct().ToList();
        }
    }
}

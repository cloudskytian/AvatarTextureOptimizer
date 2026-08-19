// Custom Inspector for AvatarTextureOptimizerComponent
// AvatarTextureOptimizerComponent的自定义检查器

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.Runtime;

namespace net.fosa.avatar_texture_optimizer.Editor.UI
{
    /// <summary>
    /// Custom inspector for the ATO component with i18n support and platform overrides.
    /// ATO组件的自定义检查器，支持i18n和平台覆写。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizerComponent))]
    public class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private static string _currentLanguage = "auto";
        private static Dictionary<string, Dictionary<string, string>> _localizations;
        private static bool _initialized = false;

        private bool _showAdvancedQuality = false;
        private bool _showPlatformOverrides = false;
        private bool _showFormatSettings = false;
        private bool _showWhitelist = false;

        private SerializedProperty _generateAtlas;
        private SerializedProperty _qualityPreset;
        private SerializedProperty _targetPlatform;
        private SerializedProperty _minPixelDensity;
        private SerializedProperty _maxPixelDensity;
        private SerializedProperty _pixelDensityPreset;
        private SerializedProperty _maxAtlasSizePC;
        private SerializedProperty _maxAtlasSizeMobile;
        private SerializedProperty _minPadding;
        private SerializedProperty _enableNPOTAtlas;
        private SerializedProperty _deduplicateMaterials;
        private SerializedProperty _deduplicateTextures;
        private SerializedProperty _enableMipStreaming;
        private SerializedProperty _whitelist;
        private SerializedProperty _showAdvancedQualityProp;
        private SerializedProperty _qualityParams;
        private SerializedProperty _formatSettings;
        private SerializedProperty _enablePlatformOverrides;
        private SerializedProperty _platformOverrides;
        private SerializedProperty _verboseLogging;

        private void OnEnable()
        {
            _generateAtlas = serializedObject.FindProperty("generateAtlas");
            _qualityPreset = serializedObject.FindProperty("qualityPreset");
            _targetPlatform = serializedObject.FindProperty("targetPlatform");
            _minPixelDensity = serializedObject.FindProperty("minPixelDensity");
            _maxPixelDensity = serializedObject.FindProperty("maxPixelDensity");
            _pixelDensityPreset = serializedObject.FindProperty("pixelDensityPreset");
            _maxAtlasSizePC = serializedObject.FindProperty("maxAtlasSizePC");
            _maxAtlasSizeMobile = serializedObject.FindProperty("maxAtlasSizeMobile");
            _minPadding = serializedObject.FindProperty("minPadding");
            _enableNPOTAtlas = serializedObject.FindProperty("enableNPOTAtlas");
            _deduplicateMaterials = serializedObject.FindProperty("deduplicateMaterials");
            _deduplicateTextures = serializedObject.FindProperty("deduplicateTextures");
            _enableMipStreaming = serializedObject.FindProperty("enableMipStreaming");
            _whitelist = serializedObject.FindProperty("whitelist");
            _showAdvancedQualityProp = serializedObject.FindProperty("showAdvancedQuality");
            _qualityParams = serializedObject.FindProperty("qualityParams");
            _formatSettings = serializedObject.FindProperty("formatSettings");
            _enablePlatformOverrides = serializedObject.FindProperty("enablePlatformOverrides");
            _platformOverrides = serializedObject.FindProperty("platformOverrides");
            _verboseLogging = serializedObject.FindProperty("verboseLogging");

            if (!_initialized)
            {
                LoadLocalizations();
                _initialized = true;
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (AvatarTextureOptimizerComponent)target;

            // Language selector
            DrawLanguageSelector();

            EditorGUILayout.Space(8);

            // Header
            EditorGUILayout.LabelField(L("title"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(L("description"), MessageType.Info);

            EditorGUILayout.Space(8);

            // General settings
            EditorGUILayout.LabelField(L("general_settings"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_generateAtlas, new GUIContent(L("generate_atlas")));
            EditorGUILayout.PropertyField(_qualityPreset, new GUIContent(L("quality_preset")));
            EditorGUILayout.PropertyField(_targetPlatform, new GUIContent(L("target_platform")));

            EditorGUILayout.Space(4);

            // Pixel density
            EditorGUILayout.LabelField(L("pixel_density"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_pixelDensityPreset, new GUIContent(L("density_preset")));
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_minPixelDensity, new GUIContent(L("min_density")));
            EditorGUILayout.PropertyField(_maxPixelDensity, new GUIContent(L("max_density")));
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4);

            // Atlas settings
            if (_generateAtlas.boolValue)
            {
                EditorGUILayout.LabelField(L("atlas_settings"), EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(_maxAtlasSizePC, new GUIContent(L("max_atlas_pc")));
                EditorGUILayout.PropertyField(_maxAtlasSizeMobile, new GUIContent(L("max_atlas_mobile")));
                EditorGUILayout.PropertyField(_minPadding, new GUIContent(L("min_padding")));
                EditorGUILayout.PropertyField(_enableNPOTAtlas, new GUIContent(L("enable_npot")));
            }

            EditorGUILayout.Space(4);

            // Deduplication
            EditorGUILayout.LabelField(L("deduplication"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_deduplicateMaterials, new GUIContent(L("dedup_materials")));
            EditorGUILayout.PropertyField(_deduplicateTextures, new GUIContent(L("dedup_textures")));

            EditorGUILayout.Space(4);

            // MipStreaming
            EditorGUILayout.PropertyField(_enableMipStreaming, new GUIContent(L("mip_streaming")));

            EditorGUILayout.Space(4);

            // Whitelist
            _showWhitelist = EditorGUILayout.Foldout(_showWhitelist, L("whitelist"), true);
            if (_showWhitelist)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_whitelist, new GUIContent(L("whitelist_objects")));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);

            // Advanced quality parameters (folded)
            _showAdvancedQualityProp.boolValue = EditorGUILayout.Foldout(
                _showAdvancedQualityProp.boolValue, L("advanced_quality"), true);
            if (_showAdvancedQualityProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_qualityParams, true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);

            // Format settings
            _showFormatSettings = EditorGUILayout.Foldout(_showFormatSettings, L("compression"), true);
            if (_showFormatSettings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_formatSettings, true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);

            // Platform overrides
            EditorGUILayout.PropertyField(_enablePlatformOverrides, new GUIContent(L("platform_overrides")));
            if (_enablePlatformOverrides.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_platformOverrides, true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);

            // Debug
            EditorGUILayout.LabelField(L("debug"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_verboseLogging, new GUIContent(L("verbose_logging")));

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLanguageSelector()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Language / 语言:", GUILayout.Width(100));

            var languages = new[] { "auto", "en", "zh-CN" };
            int currentIdx = System.Array.IndexOf(languages, _currentLanguage);
            if (currentIdx < 0) currentIdx = 0;

            int newIdx = EditorGUILayout.Popup(currentIdx, languages);
            if (newIdx != currentIdx)
            {
                _currentLanguage = languages[newIdx];
                LoadLocalizations();
            }

            EditorGUILayout.EndHorizontal();
        }

        private string L(string key)
        {
            if (_localizations == null) return key;

            // Try current language
            string lang = _currentLanguage;
            if (lang == "auto")
            {
                lang = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "zh"
                    ? "zh-CN" : "en";
            }

            if (_localizations.TryGetValue(lang, out var dict))
            {
                if (dict.TryGetValue(key, out var value))
                    return value;
            }

            // Fallback to English
            if (_localizations.TryGetValue("en", out var enDict))
            {
                if (enDict.TryGetValue(key, out var value))
                    return value;
            }

            return key;
        }

        private static void LoadLocalizations()
        {
            _localizations = new Dictionary<string, Dictionary<string, string>>();

            // Load from JSON files in the i18n directory
            string[] guids = AssetDatabase.FindAssets("ato_i18n", new[] { "Packages/net.fosa.avatar-texture-optimizer/Editor/i18n" });
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".json")) continue;

                string filename = System.IO.Path.GetFileNameWithoutExtension(path);
                string lang = filename.Replace("ato_i18n_", "");

                string json = System.IO.File.ReadAllText(path);
                var dict = JsonUtility.FromJson<LocalizationData>(json);
                if (dict?.entries != null)
                {
                    _localizations[lang] = dict.entries.ToDictionary(e => e.key, e => e.value);
                }
            }

            // If no files found, use built-in defaults
            if (_localizations.Count == 0)
            {
                _localizations["en"] = GetEnglishDefaults();
                _localizations["zh-CN"] = GetChineseDefaults();
            }
        }

        private static Dictionary<string, string> GetEnglishDefaults()
        {
            return new Dictionary<string, string>
            {
                { "title", "Avatar Texture Optimizer" },
                { "description", "Optimizes avatar textures by re-UVing and atlasing with quality-aware scaling." },
                { "general_settings", "General Settings" },
                { "generate_atlas", "Generate Atlas" },
                { "quality_preset", "Quality Preset" },
                { "target_platform", "Target Platform" },
                { "pixel_density", "Pixel Density" },
                { "density_preset", "Density Preset" },
                { "min_density", "Min Density (px/m)" },
                { "max_density", "Max Density (px/m)" },
                { "atlas_settings", "Atlas Settings" },
                { "max_atlas_pc", "Max Atlas Size (PC)" },
                { "max_atlas_mobile", "Max Atlas Size (Mobile)" },
                { "min_padding", "Min Padding (px)" },
                { "enable_npot", "Enable NPOT Atlas" },
                { "deduplication", "Deduplication" },
                { "dedup_materials", "Deduplicate Materials" },
                { "dedup_textures", "Deduplicate Textures" },
                { "mip_streaming", "Enable MipStreaming" },
                { "whitelist", "Whitelist" },
                { "whitelist_objects", "Whitelisted Objects" },
                { "advanced_quality", "Advanced Quality Parameters" },
                { "compression", "Compression Format" },
                { "platform_overrides", "Platform Overrides" },
                { "debug", "Debug" },
                { "verbose_logging", "Verbose Logging" }
            };
        }

        private static Dictionary<string, string> GetChineseDefaults()
        {
            return new Dictionary<string, string>
            {
                { "title", "Avatar贴图优化器" },
                { "description", "通过质量感知的UV重拆和图集化来优化Avatar贴图。" },
                { "general_settings", "通用设置" },
                { "generate_atlas", "生成图集" },
                { "quality_preset", "质量挡位" },
                { "target_platform", "目标平台" },
                { "pixel_density", "像素密度" },
                { "density_preset", "密度挡位" },
                { "min_density", "最小密度（px/m）" },
                { "max_density", "最大密度（px/m）" },
                { "atlas_settings", "图集设置" },
                { "max_atlas_pc", "最大图集尺寸（PC）" },
                { "max_atlas_mobile", "最大图集尺寸（移动端）" },
                { "min_padding", "最小间距（px）" },
                { "enable_npot", "启用NPOT图集" },
                { "deduplication", "去重" },
                { "dedup_materials", "材质去重" },
                { "dedup_textures", "贴图去重" },
                { "mip_streaming", "启用Mip流式传输" },
                { "whitelist", "白名单" },
                { "whitelist_objects", "白名单对象" },
                { "advanced_quality", "高级质量参数" },
                { "compression", "压缩格式" },
                { "platform_overrides", "平台覆写" },
                { "debug", "调试" },
                { "verbose_logging", "详细日志" }
            };
        }

        [System.Serializable]
        private class LocalizationData
        {
            public List<LocalizationEntry> entries;
        }

        [System.Serializable]
        private class LocalizationEntry
        {
            public string key;
            public string value;
        }
    }
}

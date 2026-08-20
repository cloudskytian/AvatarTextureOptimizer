// AvatarTextureOptimizer - AvatarTextureOptimizerInspector
// EN: Inspector UI: collapsible sections; platform override sections appear only when enabled.
// CN: Inspector 界面：折叠分区；平台覆盖区仅在勾选后显示。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerInspector : Editor
    {
        private bool _showGeneral = true;
        private bool _showQuality = false;   // 质量挡位折叠在高级选项（按需求）
        private bool _showAtlas = true;
        private bool _showImport = true;
        private bool _showCompression = true;
        private bool _showPost = false;
        private bool _showWhitelist = false;
        private bool _showPlatforms = false;
        private bool _showDiagnostics = false;

        private readonly Dictionary<string, bool> _platformFoldouts = new Dictionary<string, bool>
        {
            ["PC"] = false, ["Android"] = false, ["iOS"] = false
        };

        public override void OnInspectorGUI()
        {
            var c = (AvatarTextureOptimizer)target;
            serializedObject.Update();

            EditorGUILayout.Space();
            var style = new GUIStyle(EditorStyles.boldLabel) { richText = true };
            EditorGUILayout.LabelField("<color=#6f9fff>Avatar Texture Optimizer</color>", style);

            // ------------------------------------------------------------ 校验提示
            string err = c.ValidateMounting();
            if (err != null)
            {
                EditorGUILayout.HelpBox(err, MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Runs after Modular Avatar, before AAO. One component per avatar; mounted on the VRCAvatarDescriptor object.",
                    MessageType.Info);
            }

            _showGeneral = EditorGUILayout.Foldout(_showGeneral, "General / 基础", true);
            if (_showGeneral)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.generateAtlases)));
            }

            // EN: Quality tier lives in a collapsible "advanced" section (spec: 折叠在高级选项里供用户修改).
            // CN: 质量挡位折叠在高级选项区（按需求）。
            _showQuality = EditorGUILayout.Foldout(_showQuality, "Quality / 质量（高级）", false);
            if (_showQuality)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.qualityPreset)));
                if (c.qualityPreset == QualityPresetEnum.Custom)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.customQuality)), true);
                }
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.minPixelDensity)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.maxPixelDensity)));
            }

            _showAtlas = EditorGUILayout.Foldout(_showAtlas, "Atlas / 图集", true);
            if (_showAtlas)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.padding)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.experimentalNpot)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.maxAtlasSize)));
                if (c.experimentalNpot)
                    EditorGUILayout.HelpBox("NPOT: 64px steps. MipStreaming & Crunch supported; PVRTC excluded on iOS automatically.", MessageType.None);
            }

            _showImport = EditorGUILayout.Foldout(_showImport, "Texture Import / 贴图导入", true);
            if (_showImport)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.mipmaps)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.useGpuMetrics)));
            }

            _showCompression = EditorGUILayout.Foldout(_showCompression, "Compression / 压缩", true);
            if (_showCompression)
            {
                var comp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.compression));
                EditorGUILayout.PropertyField(comp, true);
            }

            _showPost = EditorGUILayout.Foldout(_showPost, "Post Optimization / 优化后处理", false);
            if (_showPost)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.enableDedup)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.enableSlotMerge)));
            }

            _showWhitelist = EditorGUILayout.Foldout(_showWhitelist, I18n.T("ui.whitelist"), false);
            if (_showWhitelist)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.whitelist)), true);
                EditorGUILayout.HelpBox(
                    "Whitelisted objects' textures skip ALL optimization (including import params). " +
                    "Same-UV partners skip atlas but keep whole-texture scaling + import optimization.",
                    MessageType.None);
            }

            _showPlatforms = EditorGUILayout.Foldout(_showPlatforms, "Platform Overrides / 平台覆盖", false);
            if (_showPlatforms)
            {
                // EN: Overrides appear only when checked (spec).
                // CN: 覆盖区仅在勾选后显示（按需求）。
                PlatformSection(c, "PC", serializedObject.FindProperty(nameof(AvatarTextureOptimizer.pcOverride)));
                PlatformSection(c, "Android", serializedObject.FindProperty(nameof(AvatarTextureOptimizer.androidOverride)));
                PlatformSection(c, "iOS", serializedObject.FindProperty(nameof(AvatarTextureOptimizer.iosOverride)));
            }

            _showDiagnostics = EditorGUILayout.Foldout(_showDiagnostics, "Diagnostics / 调试", false);
            if (_showDiagnostics)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.detailedLogs)));
                // EN: Manual language picker (Auto = NDMF current language).
                // CN: 手动语言选择（Auto = NDMF 当前语言）。
                var langs = new List<string> { "Auto" };
                langs.AddRange(I18n.AvailableLanguages);
                int cur = 0;
                string manual = I18n.ManualLanguage;
                for (int i = 0; i < langs.Count; i++)
                    if (langs[i].Equals(manual, StringComparison.OrdinalIgnoreCase) || (manual == null && i == 0)) { cur = i; break; }
                int next = EditorGUILayout.Popup("Language / 语言", cur, langs.ToArray());
                if (next != cur)
                {
                    I18n.ManualLanguage = next == 0 ? null : langs[next];
                    AtoLocalization.Reload();
                }
                if (GUILayout.Button("Reload language files / 重载语言文件"))
                {
                    AtoLocalization.Reload();
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void PlatformSection(AvatarTextureOptimizer c, string label, SerializedProperty prop)
        {
            var enabledProp = prop.FindPropertyRelative("enabled");
            bool was = _platformFoldouts[label];
            bool now = EditorGUILayout.Foldout(was, $"{label} ({(enabledProp.boolValue ? "enabled" : "disabled")})", true);
            _platformFoldouts[label] = now;
            if (!now) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(enabledProp);
            if (enabledProp.boolValue)
            {
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("overrideQuality"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("preset"));
                var preset = (QualityPresetEnum)prop.FindPropertyRelative("preset").enumValueIndex;
                if (preset == QualityPresetEnum.Custom)
                    EditorGUILayout.PropertyField(prop.FindPropertyRelative("customParams"), true);
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("padding"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("experimentalNpot"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("maxAtlasSize"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("mipmaps"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("useGpuMetrics"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("minPixelDensity"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("maxPixelDensity"));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("compression"), true);
            }
            else
            {
                EditorGUILayout.HelpBox("Check to override all optimization parameters for this platform.", MessageType.None);
            }
            EditorGUI.indentLevel--;
        }
    }
}

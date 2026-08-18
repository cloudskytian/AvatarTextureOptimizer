// ATOEditor.cs / ATOEditor.cs
// Custom Inspector for AvatarTextureOptimizer. Provides general settings, quality preset,
// platform overrides, advanced options, whitelist management, and i18n language picker.
// AvatarTextureOptimizer自定义Inspector。提供通用设置、质量挡位、平台覆盖、高级选项、白名单管理和i18n语言选择。

using net.fosa.avatar_texture_optimizer;
using net.fosa.avatar_texture_optimizer.Editor.Util;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.UI
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class ATOEditor : UnityEditor.Editor
    {
        private bool _showAdvanced = false;
        private bool _showPlatformPC = false;
        private bool _showPlatformAndroid = false;
        private bool _showPlatformIOS = false;
        private bool _showWhitelist = false;

        // Safe format options per platform / 每个平台的安全格式选项
        private static readonly CompressionFormat[] PcFormats = {
            CompressionFormat.Auto, CompressionFormat.DXT1, CompressionFormat.DXT5,
            CompressionFormat.BC7, CompressionFormat.BC5, CompressionFormat.RGBA32, CompressionFormat.R8
        };
        private static readonly CompressionFormat[] AndroidFormats = {
            CompressionFormat.Auto, CompressionFormat.ASTC_4x4, CompressionFormat.ASTC_6x6,
            CompressionFormat.ASTC_8x8, CompressionFormat.ETC2, CompressionFormat.ETC2_Alpha,
            CompressionFormat.RGBA32, CompressionFormat.R8
        };
        private static readonly CompressionFormat[] IosFormats = {
            CompressionFormat.Auto, CompressionFormat.ASTC_4x4, CompressionFormat.ASTC_6x6,
            CompressionFormat.PVRTC_RGB, CompressionFormat.PVRTC_RGBA,
            CompressionFormat.RGBA32, CompressionFormat.R8
        };
        private static readonly string[] PcFormatLabels = {
            "Auto", "DXT1 (BC1, opaque)", "DXT5 (BC3, alpha)", "BC7 (HQ)", "BC5 (Normal)",
            "RGBA32 (Uncompressed)", "R8 (Grayscale)"
        };
        private static readonly string[] AndroidFormatLabels = {
            "Auto", "ASTC 4x4", "ASTC 6x6", "ASTC 8x8", "ETC2", "ETC2 + Alpha",
            "RGBA32 (Uncompressed)", "R8 (Grayscale)"
        };
        private static readonly string[] IosFormatLabels = {
            "Auto", "ASTC 4x4", "ASTC 6x6", "PVRTC 4bpp RGB", "PVRTC 4bpp RGBA",
            "RGBA32 (Uncompressed)", "R8 (Grayscale)"
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var t = (AvatarTextureOptimizer)target;

            EditorGUILayout.LabelField(ATOLocalization.T("component.name"), EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // General / 通用设置
            EditorGUILayout.LabelField(ATOLocalization.T("component.header.general"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("generateAtlas"),
                new GUIContent(ATOLocalization.T("component.general.generateAtlas"), ATOLocalization.T("component.general.generateAtlas.tooltip")));

            var qualityProp = serializedObject.FindProperty("qualityPreset");
            EditorGUILayout.PropertyField(qualityProp, new GUIContent(ATOLocalization.T("component.general.qualityPreset"), ATOLocalization.T("component.general.qualityPreset.tooltip")));

            var platformProp = serializedObject.FindProperty("targetPlatform");
            EditorGUILayout.PropertyField(platformProp, new GUIContent(ATOLocalization.T("component.general.targetPlatform"), ATOLocalization.T("component.general.targetPlatform.tooltip")));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLocalization.T("component.header.pixelDensity"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("minPixelDensity"),
                new GUIContent(ATOLocalization.T("component.density.min"), ATOLocalization.T("component.density.min.tooltip")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxPixelDensity"),
                new GUIContent(ATOLocalization.T("component.density.max"), ATOLocalization.T("component.density.max.tooltip")));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLocalization.T("component.header.atlas"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("atlasPadding"),
                new GUIContent(ATOLocalization.T("component.atlas.padding"), ATOLocalization.T("component.atlas.padding.tooltip")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("allowNPOT"),
                new GUIContent(ATOLocalization.T("component.atlas.npot"), ATOLocalization.T("component.atlas.npot.tooltip")));
            if (t.allowNPOT && t.targetPlatform == TargetPlatform.iOS)
            {
                EditorGUILayout.HelpBox(ATOLocalization.T("warning.npotPVRTC"), MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLocalization.T("component.header.dedup"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicate"),
                new GUIContent(ATOLocalization.T("component.dedup.enabled"), ATOLocalization.T("component.dedup.enabled.tooltip")));

            // Advanced / 高级选项
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, ATOLocalization.T("component.header.advanced"));
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("verboseLogging"),
                    new GUIContent(ATOLocalization.T("component.adv.verbose"), ATOLocalization.T("component.adv.verbose.tooltip")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("useGPU"),
                    new GUIContent(ATOLocalization.T("component.adv.gpu"), ATOLocalization.T("component.adv.gpu.tooltip")));

                if (t.qualityPreset == QualityPreset.Custom)
                {
                    EditorGUILayout.LabelField(ATOLocalization.T("component.adv.customThresholds"), EditorStyles.boldLabel);
                    var ct = serializedObject.FindProperty("customThresholds");
                    EditorGUILayout.PropertyField(ct.FindPropertyRelative("msSSIM"), new GUIContent(ATOLocalization.T("component.adv.custom.msSSIM")));
                    EditorGUILayout.PropertyField(ct.FindPropertyRelative("deltaE"), new GUIContent(ATOLocalization.T("component.adv.custom.deltaE")));
                    EditorGUILayout.PropertyField(ct.FindPropertyRelative("normalAngleDeg"), new GUIContent(ATOLocalization.T("component.adv.custom.normalAngle")));
                    EditorGUILayout.PropertyField(ct.FindPropertyRelative("alphaRMSE"), new GUIContent(ATOLocalization.T("component.adv.custom.alphaRMSE")));
                    EditorGUILayout.PropertyField(ct.FindPropertyRelative("cutoutIoU"), new GUIContent(ATOLocalization.T("component.adv.custom.cutoutIoU")));
                    EditorGUILayout.PropertyField(ct.FindPropertyRelative("grayscaleRMSE"), new GUIContent(ATOLocalization.T("component.adv.custom.grayscaleRMSE")));
                }
                EditorGUI.indentLevel--;
            }

            // Platform overrides / 平台覆盖
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLocalization.T("component.header.platformOverride"), EditorStyles.boldLabel);
            DrawPlatformOverride("component.platform.pc", ref _showPlatformPC, serializedObject.FindProperty("pcOverride"), PcFormats, PcFormatLabels, false);
            DrawPlatformOverride("component.platform.android", ref _showPlatformAndroid, serializedObject.FindProperty("androidOverride"), AndroidFormats, AndroidFormatLabels, false);
            DrawPlatformOverride("component.platform.ios", ref _showPlatformIOS, serializedObject.FindProperty("iosOverride"), IosFormats, IosFormatLabels, t.allowNPOT);

            // Whitelist / 白名单
            EditorGUILayout.Space();
            _showWhitelist = EditorGUILayout.Foldout(_showWhitelist, ATOLocalization.T("component.header.whitelist"));
            if (_showWhitelist)
            {
                EditorGUI.indentLevel++;
                var wl = serializedObject.FindProperty("whitelist");
                EditorGUILayout.HelpBox(ATOLocalization.T("component.whitelist.tooltip"), MessageType.Info);
                EditorGUILayout.PropertyField(wl, true);
                EditorGUI.indentLevel--;
            }

            // Language picker / 语言选择
            EditorGUILayout.Space();
            var langs = ATOLocalization.AvailableLanguages;
            int idx = System.Array.IndexOf(langs, ATOLocalization.CurrentLanguage);
            int newIdx = EditorGUILayout.Popup("Language / 语言", idx, langs);
            if (newIdx >= 0 && newIdx != idx) ATOLocalization.CurrentLanguage = langs[newIdx];

            // Validation / 验证
            if (!t.IsValidAvatarRoot())
            {
                EditorGUILayout.HelpBox(ATOLocalization.T("error.noAvatarDescriptor"), MessageType.Error);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawPlatformOverride(string labelKey, ref bool fold, SerializedProperty prop,
            CompressionFormat[] formats, string[] labels, bool isIOSNPOT)
        {
            fold = EditorGUILayout.Foldout(fold, ATOLocalization.T(labelKey));
            if (!fold) return;
            EditorGUI.indentLevel++;
            var enabled = prop.FindPropertyRelative("enabled");
            EditorGUILayout.PropertyField(enabled, new GUIContent(ATOLocalization.T("component.platform.enabled")));
            using (new EditorGUI.DisabledScope(!enabled.boolValue))
            {
                DrawFormatPopup(prop.FindPropertyRelative("opaqueFormat"), "component.platform.opaqueFmt", formats, labels, isIOSNPOT);
                DrawFormatPopup(prop.FindPropertyRelative("alphaFormat"), "component.platform.alphaFmt", formats, labels, isIOSNPOT);
                DrawFormatPopup(prop.FindPropertyRelative("normalFormat"), "component.platform.normalFmt", formats, labels, isIOSNPOT);
                DrawFormatPopup(prop.FindPropertyRelative("grayscaleFormat"), "component.platform.grayFmt", formats, labels, isIOSNPOT);
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("mipmapEnabled"), new GUIContent(ATOLocalization.T("component.platform.mipmap")));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("crunchCompression"), new GUIContent(ATOLocalization.T("component.platform.crunch")));
                using (new EditorGUI.DisabledScope(!prop.FindPropertyRelative("crunchCompression").boolValue))
                    EditorGUILayout.PropertyField(prop.FindPropertyRelative("crunchCompressorQuality"), new GUIContent(ATOLocalization.T("component.platform.crunchQuality")));
                EditorGUILayout.PropertyField(prop.FindPropertyRelative("maxAtlasSize"), new GUIContent(ATOLocalization.T("component.platform.maxAtlas")));
            }
            EditorGUI.indentLevel--;
        }

        private void DrawFormatPopup(SerializedProperty prop, string labelKey, CompressionFormat[] formats, string[] labels, bool disablePVRTC)
        {
            int cur = prop.enumValueIndex;
            CompressionFormat curVal = (CompressionFormat)cur;
            // Filter out PVRTC when NPOT / NPOT时过滤PVRTC
            var filteredFormats = new System.Collections.Generic.List<CompressionFormat>();
            var filteredLabels = new System.Collections.Generic.List<string>();
            for (int i = 0; i < formats.Length; i++)
            {
                if (disablePVRTC && (formats[i] == CompressionFormat.PVRTC_RGB || formats[i] == CompressionFormat.PVRTC_RGBA))
                    continue;
                filteredFormats.Add(formats[i]);
                filteredLabels.Add(labels[i]);
            }
            int selIdx = filteredFormats.IndexOf(curVal);
            if (selIdx < 0) selIdx = 0;
            int newIdx = EditorGUILayout.Popup(ATOLocalization.T(labelKey), selIdx, filteredLabels.ToArray());
            if (newIdx >= 0 && newIdx < filteredFormats.Count)
                prop.enumValueIndex = (int)filteredFormats[newIdx];
        }
    }
}

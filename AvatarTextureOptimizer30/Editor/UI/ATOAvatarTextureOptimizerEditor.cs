// ATOAvatarTextureOptimizerEditor.cs — 组件检查器（IMGUI）/ Component inspector (IMGUI).
// 说明：默认把用户当成小白（常规选项直观呈现），同时支持高级用户（质量挡位参数等折叠在高级选项里）。
// 平台覆盖：勾选对应平台才显示其参数；全平台通用参数默认折叠，使用通用的最优解。
// 语言：读取包内 i18n json 配置（有几个语言显示几个），默认 Auto 跟随 NDMF 语言。
// Note: novice-friendly defaults with advanced options (quality tier parameters etc.) folded away;
// platform overrides are shown only when their toggle is on; global parameters use optimal defaults.
// Language: driven by the in-package i18n json files, default Auto follows NDMF's language.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>组件检查器。/ Component inspector.</summary>
    [CustomEditor(typeof(ATOAvatarTextureOptimizer))]
    public sealed class ATOAvatarTextureOptimizerEditor : Editor
    {
        private static bool _advancedFold;
        private static bool _platformFold;
        private static readonly int[] DensityOptions = { 512, 1024, 2048, 4096, 8192 };

        public override void OnInspectorGUI()
        {
            var component = (ATOAvatarTextureOptimizer)target;
            var config = component.config;
            if (config == null) config = component.config = new ATOConfig();

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.HelpBox(
                "Optimizes the avatar's textures by scaling UV islands to the target quality, trimming unused regions and repacking them into atlases. Runs during the NDMF build, after Modular Avatar and before Avatar Optimizer.\n" +
                "(在 NDMF 构建时按目标质量缩放 UV 岛、剔除未使用区域并重排图集；运行于 Modular Avatar 之后、Avatar Optimizer 之前。)",
                MessageType.Info);

            // ---- 语言（来自 i18n json，有几个文件显示几个）/ language (from i18n json files; one per file) ----
            var i18nProp = serializedObject.FindProperty(nameof(ATOAvatarTextureOptimizer.i18nLanguage));
            var langs = new List<string> { "" }; // "" = Auto
            langs.AddRange(ATOI18n.Languages);
            var labels = new List<string> { "Auto" };
            foreach (var l in ATOI18n.Languages) labels.Add(l);
            var idx = langs.IndexOf(component.i18nLanguage);
            if (idx < 0) idx = 0;
            idx = EditorGUILayout.Popup("Language / 语言", idx, labels.ToArray());
            i18nProp.stringValue = langs[idx];
            ATOI18n.SetForcedLanguage(langs[idx]);

            EditorGUILayout.Space();

            // ---- 基础 / basics ----
            var cfg = serializedObject.FindProperty(nameof(ATOAvatarTextureOptimizer.config));
            EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.generateAtlases)),
                new GUIContent(ATOI18n.Tr("ui.generateAtlases")));
            if (config.generateAtlases)
                EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.minPadding)),
                    new GUIContent(ATOI18n.Tr("ui.minPadding")));

            EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.qualityTier)),
                new GUIContent(ATOI18n.Tr("ui.qualityTier")));
            EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.mipmapAndStreaming)),
                new GUIContent(ATOI18n.Tr("ui.mipmapStreaming")));

            // 像素密度 / pixel density
            var minD = cfg.FindPropertyRelative(nameof(ATOConfig.minPixelDensity));
            var maxD = cfg.FindPropertyRelative(nameof(ATOConfig.maxPixelDensity));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(ATOI18n.Tr("ui.pixelDensity"), GUILayout.Width(200));
            minD.intValue = EditorGUILayout.IntPopup(minD.intValue, DensityStrings(), DensityOptions);
            EditorGUILayout.LabelField(" ~ ", GUILayout.Width(20));
            maxD.intValue = EditorGUILayout.IntPopup(maxD.intValue, DensityStrings(), DensityOptions);
            EditorGUILayout.LabelField("px/m", GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();
            if (minD.intValue > maxD.intValue)
            {
                EditorGUILayout.HelpBox(ATOI18n.Tr("ui.densityInvalid"), MessageType.Warning);
                maxD.intValue = minD.intValue;
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.deduplicateTextures)),
                new GUIContent(ATOI18n.Tr("ui.dedupTextures")));
            EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.deduplicateMaterials)),
                new GUIContent(ATOI18n.Tr("ui.dedupMaterials")));
            EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.autoEnableReadWrite)),
                new GUIContent(ATOI18n.Tr("ui.autoRW")));
            EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.logVerbosity)),
                new GUIContent(ATOI18n.Tr("ui.verbosity")));

            // 白名单资产 / whitelist assets
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ATOAvatarTextureOptimizer.whitelistAssets)),
                new GUIContent(ATOI18n.Tr("ui.whitelistAssets")), true);

            // ---- 高级选项 / advanced ----
            _advancedFold = EditorGUILayout.Foldout(_advancedFold, ATOI18n.Tr("ui.advanced"), true);
            if (_advancedFold)
            {
                EditorGUI.indentLevel++;
                DrawTierParameters(cfg, config, config.qualityTier);
                EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.experimentalNPOT)),
                    new GUIContent(ATOI18n.Tr("ui.npot")));
                EditorGUI.indentLevel--;
            }

            // ---- 平台选项 / platform options ----
            _platformFold = EditorGUILayout.Foldout(_platformFold, ATOI18n.Tr("ui.platform"), true);
            if (_platformFold)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(cfg.FindPropertyRelative(nameof(ATOConfig.currentPlatform)),
                    new GUIContent(ATOI18n.Tr("ui.currentPlatform")));
                DrawPlatform(cfg.FindPropertyRelative(nameof(ATOConfig.platformPC)), "PC");
                DrawPlatform(cfg.FindPropertyRelative(nameof(ATOConfig.platformAndroid)), "Android");
                DrawPlatform(cfg.FindPropertyRelative(nameof(ATOConfig.platformIOS)), "iOS");
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(target);
            }
        }

        private static string[] DensityStrings()
        {
            var arr = new string[DensityOptions.Length];
            for (int i = 0; i < DensityOptions.Length; i++) arr[i] = DensityOptions[i].ToString();
            return arr;
        }

        private void DrawTierParameters(SerializedProperty cfg, ATOConfig config, ATOQualityTier tier)
        {
            string field;
            switch (tier)
            {
                case ATOQualityTier.Ultra: field = nameof(ATOConfig.ultra); break;
                case ATOQualityTier.High: field = nameof(ATOConfig.high); break;
                case ATOQualityTier.Performance: field = nameof(ATOConfig.performance); break;
                case ATOQualityTier.Custom: field = nameof(ATOConfig.custom); break;
                default: field = nameof(ATOConfig.standard); break;
            }
            var values = cfg.FindPropertyRelative(field);
            EditorGUILayout.LabelField(ATOI18n.Tr("ui.tierParams") + " (" + tier + ")", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(values.FindPropertyRelative(nameof(ATOQualityTierValues.msSsim)),
                new GUIContent(ATOI18n.Tr("ui.msSsim")));
            EditorGUILayout.PropertyField(values.FindPropertyRelative(nameof(ATOQualityTierValues.deltaEP95)),
                new GUIContent(ATOI18n.Tr("ui.deltaE")));
            EditorGUILayout.PropertyField(values.FindPropertyRelative(nameof(ATOQualityTierValues.normalAngleP95)),
                new GUIContent(ATOI18n.Tr("ui.normalAngle")));
            EditorGUILayout.PropertyField(values.FindPropertyRelative(nameof(ATOQualityTierValues.alphaIoU)),
                new GUIContent(ATOI18n.Tr("ui.alphaIoU")));
            EditorGUILayout.PropertyField(values.FindPropertyRelative(nameof(ATOQualityTierValues.alphaLinearRmse)),
                new GUIContent(ATOI18n.Tr("ui.alphaRmse")));
            EditorGUILayout.PropertyField(values.FindPropertyRelative(nameof(ATOQualityTierValues.grayLinearRmse)),
                new GUIContent(ATOI18n.Tr("ui.grayRmse")));
            if (values.FindPropertyRelative(nameof(ATOQualityTierValues.msSsim)).floatValue >= 1f - 1e-6f)
                EditorGUILayout.HelpBox(ATOI18n.Tr("ui.losslessHint"), MessageType.Info);
            EditorGUI.indentLevel--;
        }

        private void DrawPlatform(SerializedProperty platformProp, string label)
        {
            var enabled = platformProp.FindPropertyRelative(nameof(ATOPlatformConfig.enabled));
            EditorGUILayout.BeginHorizontal();
            var newEnabled = EditorGUILayout.ToggleLeft(label + " " + ATOI18n.Tr("ui.override"), enabled.boolValue);
            enabled.boolValue = newEnabled;
            EditorGUILayout.EndHorizontal();
            if (!newEnabled) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(platformProp.FindPropertyRelative(nameof(ATOPlatformConfig.atlasMaxSide)),
                new GUIContent(ATOI18n.Tr("ui.maxSide")));
            EditorGUILayout.PropertyField(platformProp.FindPropertyRelative(nameof(ATOPlatformConfig.experimentalNPOT)),
                new GUIContent(ATOI18n.Tr("ui.npot")));
            EditorGUILayout.PropertyField(platformProp.FindPropertyRelative(nameof(ATOPlatformConfig.transparentFormat)),
                new GUIContent(ATOI18n.Tr("ui.fmtTransparent")));
            EditorGUILayout.PropertyField(platformProp.FindPropertyRelative(nameof(ATOPlatformConfig.opaqueFormat)),
                new GUIContent(ATOI18n.Tr("ui.fmtOpaque")));
            EditorGUILayout.PropertyField(platformProp.FindPropertyRelative(nameof(ATOPlatformConfig.normalFormat)),
                new GUIContent(ATOI18n.Tr("ui.fmtNormal")));
            EditorGUILayout.PropertyField(platformProp.FindPropertyRelative(nameof(ATOPlatformConfig.grayscaleFormat)),
                new GUIContent(ATOI18n.Tr("ui.fmtGrayscale")));
            EditorGUI.indentLevel--;
        }

        /// <summary>语言切换时刷新检查器。/ Refresh the inspector on language change.</summary>
        [InitializeOnLoadMethod]
        private static void Hook()
        {
            ATOI18n.Reload();
        }
    }
}

// ATOEditor.cs
// Custom inspector for the ATO component: quality presets, advanced foldouts, platform
// overrides, whitelist, compression settings and language selection — all localized.
// ATO 组件自定义 Inspector:质量挡位、高级折叠、平台覆盖、白名单、压缩与语言选择(全本地化)。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    internal sealed class ATOEditor : UnityEditor.Editor
    {
        private bool _advFold;
        private bool _platformFold;
        private bool _compressionFold;
        private bool _whitelistFold;
        private bool _debugFold;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var comp = (AvatarTextureOptimizer)target;

            DrawLanguageBar();
            EditorGUILayout.Space(2);

            // ---------------- Basic / 基础 ----------------
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.generateAtlas)),
                new GUIContent(T("ato.ui.generateAtlas"), T("ato.ui.generateAtlas.tip")));

            DrawPresetSelector(comp);

            EditorGUILayout.Space(4);
            _whitelistFold = EditorGUILayout.Foldout(_whitelistFold, T("ato.ui.whitelist"), true);
            if (_whitelistFold)
            {
                EditorGUILayout.HelpBox(T("ato.ui.whitelist.tip"), MessageType.Info);
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.whitelist)),
                    new GUIContent(T("ato.ui.whitelist.list")), true);
            }

            // ---------------- Advanced / 高级 ----------------
            EditorGUILayout.Space(4);
            _advFold = EditorGUILayout.Foldout(_advFold, T("ato.ui.advanced"), true);
            if (_advFold)
            {
                DrawThresholds(comp);
                EditorGUILayout.Space(3);
                DrawDensityAndPadding();
            }

            // ---------------- Compression / 压缩 ----------------
            EditorGUILayout.Space(4);
            _compressionFold = EditorGUILayout.Foldout(_compressionFold, T("ato.ui.compression"), true);
            if (_compressionFold) DrawCompression(comp);

            // ---------------- Platform overrides / 平台覆盖 ----------------
            EditorGUILayout.Space(4);
            _platformFold = EditorGUILayout.Foldout(_platformFold, T("ato.ui.platform"), true);
            if (_platformFold) DrawPlatformOverrides(comp);

            // ---------------- Debug / 调试 ----------------
            EditorGUILayout.Space(4);
            _debugFold = EditorGUILayout.Foldout(_debugFold, T("ato.ui.debug"), true);
            if (_debugFold)
            {
                var verbose = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.verboseLogging));
                EditorGUILayout.PropertyField(verbose, new GUIContent(T("ato.ui.verbose")));
                var save = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.debugSaveAtlases));
                EditorGUILayout.PropertyField(save, new GUIContent(T("ato.ui.debugSave")));
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------ //
        private void DrawLanguageBar()
        {
            var langs = ATOLocalization.Languages;
            if (langs == null || langs.Count == 0) return;
            var current = EditorPrefs.GetString("ato.language", "auto");
            var options = new string[langs.Count + 1];
            options[0] = "Auto";
            for (int i = 0; i < langs.Count; i++) options[i + 1] = ATOLocalization.LangDisplayName(langs[i]);
            int sel = 0;
            for (int i = 0; i < langs.Count; i++) if (langs[i] == current) sel = i + 1;
            int next = EditorGUILayout.Popup(T("ato.ui.language"), sel, options);
            if (next != sel)
            {
                EditorPrefs.SetString("ato.language", next == 0 ? "auto" : langs[next - 1]);
                if (next != 0) nadena.dev.ndmf.localization.LanguagePrefs.Language = langs[next - 1];
                GUIUtility.ExitGUI();
            }
        }

        private void DrawPresetSelector(AvatarTextureOptimizer comp)
        {
            var presetProp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.qualityPreset));
            var newPreset = (QualityPreset)EditorGUILayout.Popup(T("ato.ui.preset"), presetProp.intValue,
                new[]
                {
                    T("ato.preset.nearlossless"), T("ato.preset.high"), T("ato.preset.medium"),
                    T("ato.preset.low"), T("ato.preset.custom"),
                });
            if (newPreset != comp.qualityPreset)
            {
                presetProp.intValue = (int)newPreset;
                if (newPreset != QualityPreset.Custom)
                {
                    // Preset change refreshes thresholds; Custom never overwritten.
                    // 切挡位刷新阈值;Custom 不被覆盖。
                    var t = QualityThresholds.ForPreset(newPreset);
                    var th = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.thresholds));
                    CopyThresholds(th, t);
                }
                serializedObject.ApplyModifiedProperties();
            }
            if (comp.qualityPreset == QualityPreset.NearLossless || comp.thresholds.IsNearLossless)
                EditorGUILayout.HelpBox(T("ato.ui.preset.nearlossless.tip"), MessageType.Info);
        }

        private static void CopyThresholds(SerializedProperty th, QualityThresholds t)
        {
            th.FindPropertyRelative(nameof(QualityThresholds.msSsimMin)).floatValue = t.msSsimMin;
            th.FindPropertyRelative(nameof(QualityThresholds.deltaEMax)).floatValue = t.deltaEMax;
            th.FindPropertyRelative(nameof(QualityThresholds.alphaIoUMin)).floatValue = t.alphaIoUMin;
            th.FindPropertyRelative(nameof(QualityThresholds.alphaRmseMax)).floatValue = t.alphaRmseMax;
            th.FindPropertyRelative(nameof(QualityThresholds.normalAngleMeanMax)).floatValue = t.normalAngleMeanMax;
            th.FindPropertyRelative(nameof(QualityThresholds.normalAngleP95Max)).floatValue = t.normalAngleP95Max;
            th.FindPropertyRelative(nameof(QualityThresholds.grayRmseMax)).floatValue = t.grayRmseMax;
        }

        private void DrawThresholds(AvatarTextureOptimizer comp)
        {
            var th = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.thresholds));
            EditorGUILayout.LabelField(T("ato.ui.thresholds"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            Slider(th.FindPropertyRelative(nameof(QualityThresholds.msSsimMin)), 0.5f, 1f, "ato.th.msssim", "{0:0.000}");
            Slider(th.FindPropertyRelative(nameof(QualityThresholds.deltaEMax)), 0f, 10f, "ato.th.deltae", "{0:0.00}");
            Slider(th.FindPropertyRelative(nameof(QualityThresholds.alphaIoUMin)), 0.9f, 1f, "ato.th.iou", "{0:0.000}");
            Slider(th.FindPropertyRelative(nameof(QualityThresholds.alphaRmseMax)), 0f, 0.1f, "ato.th.armse", "{0:0.000}");
            Slider(th.FindPropertyRelative(nameof(QualityThresholds.normalAngleMeanMax)), 0f, 15f, "ato.th.nmean", "{0:0.0}°");
            Slider(th.FindPropertyRelative(nameof(QualityThresholds.normalAngleP95Max)), 0f, 45f, "ato.th.np95", "{0:0.0}°");
            Slider(th.FindPropertyRelative(nameof(QualityThresholds.grayRmseMax)), 0f, 0.2f, "ato.th.grmse", "{0:0.000}");
            EditorGUI.indentLevel--;
        }

        private void Slider(SerializedProperty p, float min, float max, string key, string fmt)
        {
            var label = new GUIContent(T(key));
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.Slider(label, p.floatValue, min, max);
            if (EditorGUI.EndChangeCheck())
            {
                p.floatValue = v;
                // manual edit → becomes Custom / 手动修改→转 Custom
                var presetProp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.qualityPreset));
                if (presetProp.intValue != (int)QualityPreset.Custom) presetProp.intValue = (int)QualityPreset.Custom;
            }
        }

        private void DrawDensityAndPadding()
        {
            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField(T("ato.ui.density"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            PopupInt(nameof(AvatarTextureOptimizer.minDensity), "ato.ui.density.min", 512, 1024, 2048, 4096, 8192);
            PopupInt(nameof(AvatarTextureOptimizer.maxDensity), "ato.ui.density.max", 512, 1024, 2048, 4096, 8192);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.padding)),
                new GUIContent(T("ato.ui.padding")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.experimentalNpotAtlas)),
                new GUIContent(T("ato.ui.npot"), T("ato.ui.npot.tip")));
            EditorGUI.indentLevel--;
        }

        private void PopupInt(string prop, string key, params int[] values)
        {
            var p = serializedObject.FindProperty(prop);
            int sel = Array.IndexOf(values, p.intValue);
            if (sel < 0) sel = 0;
            int next = EditorGUILayout.Popup(T(key), sel, values.Select(v => v.ToString()).ToArray());
            p.intValue = values[next];
        }

        private void DrawCompression(AvatarTextureOptimizer comp)
        {
            EditorGUILayout.HelpBox(T("ato.ui.compression.tip"), MessageType.Info);
            DrawCompressionFor(null); // current platform / 当前平台
        }

        private void DrawCompressionFor(ATOPlatform? platform)
        {
            // Global (current platform): Auto defaults + note; explicit formats live in the
            // platform override section. / 全局(当前平台)为 Auto 默认;显式格式在平台覆盖区设置。
            EditorGUILayout.LabelField(
                platform == null ? T("ato.ui.compression.current") : T("ato.ui.compression.for") + " " + platform,
                EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(T("ato.ui.compression.autoNote"), MessageType.None);
            EditorGUI.indentLevel--;
        }

        private SerializedProperty ProfileProp(AvatarTextureOptimizer comp, ATOPlatform platform)
        {
            var name = platform == ATOPlatform.Windows ? nameof(AvatarTextureOptimizer.windowsProfile)
                : platform == ATOPlatform.Android ? nameof(AvatarTextureOptimizer.androidProfile)
                : nameof(AvatarTextureOptimizer.iosProfile);
            return serializedObject.FindProperty(name);
        }

        private void StreamingToggle(string path, string key)
        {
            var p = serializedObject.FindProperty(path);
            if (p == null) return;
            EditorGUILayout.PropertyField(p, new GUIContent(T(key)));
        }

        private void DrawPlatformOverrides(AvatarTextureOptimizer comp)
        {
            EditorGUILayout.HelpBox(T("ato.ui.platform.tip"), MessageType.Info);
            DrawOnePlatformOverride(comp, ATOPlatform.Windows, nameof(AvatarTextureOptimizer.overrideWindows), nameof(AvatarTextureOptimizer.windowsProfile));
            DrawOnePlatformOverride(comp, ATOPlatform.Android, nameof(AvatarTextureOptimizer.overrideAndroid), nameof(AvatarTextureOptimizer.androidProfile));
            DrawOnePlatformOverride(comp, ATOPlatform.iOS, nameof(AvatarTextureOptimizer.overrideiOS), nameof(AvatarTextureOptimizer.iosProfile));
        }

        private void DrawOnePlatformOverride(AvatarTextureOptimizer comp, ATOPlatform platform, string overridePropName, string profilePropName)
        {
            var overrideProp = serializedObject.FindProperty(overridePropName);
            EditorGUILayout.PropertyField(overrideProp, new GUIContent(T("ato.ui.platform.override") + " " + platform));
            if (!overrideProp.boolValue) return; // override settings hidden until checked / 勾选才显示
            var profile = serializedObject.FindProperty(profilePropName);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.thresholds)), new GUIContent(T("ato.ui.thresholds")), true);
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.minDensity)), new GUIContent(T("ato.ui.density.min")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.maxDensity)), new GUIContent(T("ato.ui.density.max")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.generateAtlas)), new GUIContent(T("ato.ui.generateAtlas")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.padding)), new GUIContent(T("ato.ui.padding")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.experimentalNpotAtlas)), new GUIContent(T("ato.ui.npot")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.opaqueFormat)), new GUIContent(T("ato.ui.fmt.opaque")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.alphaFormat)), new GUIContent(T("ato.ui.fmt.alpha")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.normalFormat)), new GUIContent(T("ato.ui.fmt.normal")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.grayFormat)), new GUIContent(T("ato.ui.fmt.gray")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.mipStreamingOpaque)), new GUIContent(T("ato.ui.streaming.opaque")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.mipStreamingAlpha)), new GUIContent(T("ato.ui.streaming.alpha")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.mipStreamingNormal)), new GUIContent(T("ato.ui.streaming.normal")));
            EditorGUILayout.PropertyField(profile.FindPropertyRelative(nameof(PlatformProfile.mipStreamingGray)), new GUIContent(T("ato.ui.streaming.gray")));
            EditorGUI.indentLevel--;
        }

        private static string T(string key) => ATOLocalization.Tr(key);
    }
}

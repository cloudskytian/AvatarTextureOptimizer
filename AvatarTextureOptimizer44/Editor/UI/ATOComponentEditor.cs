// ATOComponentEditor.cs - i18n inspector for the component, with platform overrides & advanced foldouts.
// 组件的i18n检视器：平台Override与高级折叠。
using System;
using System.Linq;
using Fosa.ATO.Editor.Atlas;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Editor.Localization;
using Fosa.ATO.Runtime;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor.UI
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class ATOComponentEditor : UnityEditor.Editor
    {
        private bool _advQuality, _advAtlas, _advComp, _advLog;
        private ATOPlatform _tab = ATOPlatform.PC;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var comp = (AvatarTextureOptimizer)target;

            DrawLanguageRow();
            EditorGUILayout.Space(4);

            // ---------------- main settings / 主设置 ----------------
            var st = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings));
            var presetProp = st.FindPropertyRelative(nameof(ATOSettings.qualityPreset));
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(presetProp, new GUIContent(Tr("ato.ui.qualityPreset")));
            var preset = (ATOQualityPreset)presetProp.enumValueIndex;
            if (EditorGUI.EndChangeCheck() && preset != ATOQualityPreset.Custom)
                ApplyPreset(st, preset); // preset change refreshes params / 切挡位自动刷新参数
            if (preset != ATOQualityPreset.Custom && GUILayout.Button(Tr("ato.ui.applyPreset")))
                ApplyPreset(st, preset);
            EditorGUILayout.PropertyField(st.FindPropertyRelative(nameof(ATOSettings.minDensity)), new GUIContent(Tr("ato.ui.minDensity")));
            EditorGUILayout.PropertyField(st.FindPropertyRelative(nameof(ATOSettings.maxDensity)), new GUIContent(Tr("ato.ui.maxDensity")));
            EditorGUILayout.PropertyField(st.FindPropertyRelative(nameof(ATOSettings.generateAtlas)), new GUIContent(Tr("ato.ui.generateAtlas")));

            // ---------------- whitelist / 白名单 ----------------
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Tr("ato.ui.whitelist"), EditorStyles.boldLabel);
            var wl = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.whitelist));
            for (int i = 0; i < wl.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(wl.GetArrayElementAtIndex(i), GUIContent.none);
                if (GUILayout.Button("X", GUILayout.Width(20))) { wl.DeleteArrayElementAtIndex(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button(Tr("ato.ui.whitelistAdd"))) wl.arraySize++;

            // ---------------- advanced / 高级 ----------------
            EditorGUILayout.Space(4);
            _advQuality = EditorGUILayout.Foldout(_advQuality, Tr("ato.ui.advQuality"), true);
            if (_advQuality)
            {
                EditorGUI.indentLevel++;
                var q = st.FindPropertyRelative(nameof(ATOSettings.quality));
                foreach (var f in new[] { "msSsimMin", "deltaEMeanMax", "deltaEP95Max", "alphaRmseMax", "alphaCutoutIouMin", "normalMeanDegMax", "normalP95DegMax", "grayRmseMax" })
                    EditorGUILayout.PropertyField(q.FindPropertyRelative(f), new GUIContent(Tr("ato.q." + f)));
                EditorGUI.indentLevel--;
            }

            _advAtlas = EditorGUILayout.Foldout(_advAtlas, Tr("ato.ui.advAtlas"), true);
            if (_advAtlas)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(st.FindPropertyRelative(nameof(ATOSettings.minPadding)), new GUIContent(Tr("ato.ui.minPadding")));
                EditorGUILayout.PropertyField(st.FindPropertyRelative(nameof(ATOSettings.experimentalNpot)), new GUIContent(Tr("ato.ui.npot")));
                EditorGUILayout.PropertyField(st.FindPropertyRelative(nameof(ATOSettings.materialDedup)), new GUIContent(Tr("ato.ui.matDedup")));
                EditorGUILayout.PropertyField(st.FindPropertyRelative(nameof(ATOSettings.textureDedup)), new GUIContent(Tr("ato.ui.texDedup")));
                EditorGUI.indentLevel--;
            }

            _advComp = EditorGUILayout.Foldout(_advComp, Tr("ato.ui.advComp"), true);
            if (_advComp)
            {
                EditorGUI.indentLevel++;
                DrawCategory(st, nameof(ATOSettings.opaque), "ato.cat.opaque", _tab);
                DrawCategory(st, nameof(ATOSettings.transparent), "ato.cat.transparent", _tab);
                DrawCategory(st, nameof(ATOSettings.normalMap), "ato.cat.normal", _tab);
                DrawCategory(st, nameof(ATOSettings.grayscale), "ato.cat.gray", _tab);
                EditorGUI.indentLevel--;
            }

            // ---------------- platform overrides / 平台Override ----------------
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Tr("ato.ui.platform"), EditorStyles.boldLabel);
            _tab = (ATOPlatform)GUILayout.Toolbar((int)_tab, new[] { "PC", "Android", "iOS" });
            var ovName = _tab switch
            {
                ATOPlatform.Android => nameof(AvatarTextureOptimizer.androidOverride),
                ATOPlatform.iOS => nameof(AvatarTextureOptimizer.iosOverride),
                _ => nameof(AvatarTextureOptimizer.pcOverride),
            };
            var ov = serializedObject.FindProperty(ovName);
            var ovEn = ov.FindPropertyRelative(nameof(ATOPlatformOverride.enabled));
            EditorGUILayout.PropertyField(ovEn, new GUIContent(Tr("ato.ui.override")));
            if (ovEn.boolValue)
            {
                EditorGUI.indentLevel++;
                var ovSt = ov.FindPropertyRelative(nameof(ATOPlatformOverride.settings));
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.qualityPreset)), new GUIContent(Tr("ato.ui.qualityPreset")));
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.generateAtlas)), new GUIContent(Tr("ato.ui.generateAtlas")));
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.minPadding)), new GUIContent(Tr("ato.ui.minPadding")));
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.experimentalNpot)), new GUIContent(Tr("ato.ui.npot")));
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.opaque)), true);
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.transparent)), true);
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.normalMap)), true);
                EditorGUILayout.PropertyField(ovSt.FindPropertyRelative(nameof(ATOSettings.grayscale)), true);
                EditorGUI.indentLevel--;
            }

            // ---------------- logging / 日志 ----------------
            _advLog = EditorGUILayout.Foldout(_advLog, Tr("ato.ui.advLog"), true);
            if (_advLog)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.verboseLog)), new GUIContent(Tr("ato.ui.verbose")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.logTimings)), new GUIContent(Tr("ato.ui.timings")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.logImportSettings)), new GUIContent(Tr("ato.ui.importLog")));
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static string Tr(string k) => ATOI18n.Tr(k);

        private void DrawLanguageRow()
        {
            var langs = ATOI18n.AvailableLanguages();
            var options = new[] { "Auto" }.Concat(langs).ToArray();
            int cur = Array.IndexOf(options, ATOI18n.Selected);
            if (cur < 0) cur = 0;
            var sel = EditorGUILayout.Popup(Tr("ato.ui.language"), cur, options);
            if (sel != cur || sel == 0) ATOI18n.Selected = options[sel];
        }

        private void ApplyPreset(SerializedProperty st, ATOQualityPreset preset)
        {
            var p = ATOQualityParams.ForPreset(preset);
            var q = st.FindPropertyRelative(nameof(ATOSettings.quality));
            foreach (var f in new[] { "msSsimMin", "deltaEMeanMax", "deltaEP95Max", "alphaRmseMax", "alphaCutoutIouMin", "normalMeanDegMax", "normalP95DegMax", "grayRmseMax" })
                q.FindPropertyRelative(f).floatValue = (float)typeof(ATOQualityParams).GetField(f).GetValue(p);
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawCategory(SerializedProperty st, string catField, string labelKey, ATOPlatform platform)
        {
            var cat = st.FindPropertyRelative(catField);
            EditorGUILayout.LabelField(Tr(labelKey), EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            var comp = cat.FindPropertyRelative(nameof(ATOCategoryOptions.compression));
            EditorGUILayout.PropertyField(comp, new GUIContent(Tr("ato.ui.compression")));
            EditorGUILayout.PropertyField(cat.FindPropertyRelative(nameof(ATOCategoryOptions.mipmapsAndStreaming)), new GUIContent(Tr("ato.ui.mips")));
            EditorGUI.indentLevel--;
        }
    }
}

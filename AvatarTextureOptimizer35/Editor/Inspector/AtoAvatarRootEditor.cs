using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Custom inspector for AtoAvatarRoot. / AtoAvatarRoot 的自定义 Inspector。
    /// Defaults are beginner-friendly (everything important is visible); advanced options are
    /// collapsed; platform overrides appear only when enabled; the whitelist accepts any object. /
    /// 默认面向小白（关键项全可见）；高级选项折叠；平台 override 勾选后才显示；白名单不限对象类型。
    /// </summary>
    [CustomEditor(typeof(AtoAvatarRoot))]
    internal sealed class AtoAvatarRootEditor : UnityEditor.Editor
    {
        private bool _advancedFoldout;
        private bool _compressionFoldout = true;

        public override void OnInspectorGUI()
        {
            var root = (AtoAvatarRoot)target;
            serializedObject.Update();

            // ---- validation ----
            if (!AtoVrcSdkIntegration.HasVrcAvatarDescriptor(root.gameObject))
            {
                EditorGUILayout.HelpBox(
                    AtoLoc.Tr("error.noVrcDescriptor", root.gameObject.name), MessageType.Error);
            }
            var siblings = root.GetComponentsInChildren<AtoAvatarRoot>(true);
            if (siblings.Count(component => component != null && component != root) > 0)
            {
                EditorGUILayout.HelpBox(
                    AtoLoc.Tr("error.multipleRoots", root.name), MessageType.Error);
            }

            var settings = root.settings;

            // ---- atlas ----
            EditorGUILayout.LabelField(AtoLoc.Tr("ui.atlasSection"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.generateAtlases"),
                new GUIContent(AtoLoc.Tr("ui.generateAtlases")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.minPadding"),
                new GUIContent(AtoLoc.Tr("ui.padding")));

            // ---- quality ----
            EditorGUILayout.LabelField(AtoLoc.Tr("ui.qualitySection"), EditorStyles.boldLabel);
            var presetProp = serializedObject.FindProperty("settings.preset");
            // preset display names. / 挡位显示名。
            var presetNames = new[] { "ui.presetUltra", "ui.presetHigh", "ui.presetMedium", "ui.presetLow", "ui.presetCustom" }
                .Select(AtoLoc.Tr).ToArray();
            presetProp.enumValueIndex = EditorGUILayout.Popup(
                AtoLoc.Tr("ui.qualitySection"), presetProp.enumValueIndex, presetNames);

            _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, AtoLoc.Tr("ui.advanced"));
            if (_advancedFoldout)
            {
                EditorGUI.indentLevel++;
                if ((AtoQualityPreset)presetProp.enumValueIndex == AtoQualityPreset.Custom)
                {
                    DrawThresholds(serializedObject.FindProperty("settings.customThresholds"));
                }
                DrawDensity(serializedObject);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.experimentalNpot"),
                    new GUIContent(AtoLoc.Tr("ui.npot")));
                EditorGUI.indentLevel--;
            }

            // ---- mipmaps & streaming ----
            EditorGUILayout.LabelField(AtoLoc.Tr("ui.mipSection"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.mipmapsAndStreaming"),
                new GUIContent(AtoLoc.Tr("ui.mipstreaming")));

            // ---- compression ----
            _compressionFoldout = EditorGUILayout.Foldout(_compressionFoldout, AtoLoc.Tr("ui.compression"));
            if (_compressionFoldout)
            {
                EditorGUI.indentLevel++;
                DrawCompressionConfig(serializedObject.FindProperty("settings.compression"),
                    "ui.compression");
                DrawPlatformOverride(serializedObject, "platforms.pc", "ui.enablePc", AtoTargetPlatform.PC);
                DrawPlatformOverride(serializedObject, "platforms.android", "ui.enableAndroid", AtoTargetPlatform.Android);
                DrawPlatformOverride(serializedObject, "platforms.ios", "ui.enableIos", AtoTargetPlatform.IOS);
                EditorGUI.indentLevel--;
            }

            // ---- whitelist ----
            EditorGUILayout.LabelField(AtoLoc.Tr("ui.whitelistSection"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.whitelist"),
                new GUIContent(AtoLoc.Tr("ui.whitelist")));

            // ---- localization ----
            EditorGUILayout.LabelField(AtoLoc.Tr("ui.languageSection"), EditorStyles.boldLabel);
            var languageProp = serializedObject.FindProperty("settings.language");
            var codes = new List<string> { "auto" };
            codes.AddRange(AtoLoc.AvailableCodes);
            var labels = new List<string> { AtoLoc.Tr("ui.languageAuto") };
            labels.AddRange(AtoLoc.AvailableCodes);
            var currentIndex = Mathf.Max(0, codes.IndexOf(languageProp.stringValue));
            languageProp.stringValue = codes[EditorGUILayout.Popup(
                AtoLoc.Tr("ui.language"), currentIndex, labels.ToArray())];

            // ---- logging ----
            EditorGUILayout.LabelField(AtoLoc.Tr("ui.logSection"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.logLevel"),
                new GUIContent(AtoLoc.Tr("ui.logLevel")));

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawThresholds(SerializedProperty thresholds)
        {
            DrawRange(thresholds.FindPropertyRelative("msSsim"), AtoLoc.Tr("ui.paramMsSsim"));
            DrawRange(thresholds.FindPropertyRelative("deltaE00Mean"), AtoLoc.Tr("ui.paramDeltaE"));
            DrawRange(thresholds.FindPropertyRelative("cutoutIou"), AtoLoc.Tr("ui.paramIou"));
            DrawRange(thresholds.FindPropertyRelative("blendAlphaRmse"), AtoLoc.Tr("ui.paramAlphaRmse"));
            DrawRange(thresholds.FindPropertyRelative("normalAngleMean"), AtoLoc.Tr("ui.paramNormalMean"));
            DrawRange(thresholds.FindPropertyRelative("normalAngleP95"), AtoLoc.Tr("ui.paramNormalP95"));
            DrawRange(thresholds.FindPropertyRelative("grayscaleRmse"), AtoLoc.Tr("ui.paramGrayRmse"));
        }

        private static void DrawRange(SerializedProperty property, string label)
        {
            EditorGUILayout.PropertyField(property, new GUIContent(label));
        }

        private void DrawDensity(SerializedObject serializedObject)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.minPixelDensity"),
                new GUIContent(AtoLoc.Tr("ui.densitySection") + " min"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings.maxPixelDensity"),
                new GUIContent(AtoLoc.Tr("ui.densitySection") + " max"));
        }

        private void DrawCompressionConfig(SerializedProperty config, string labelPrefix)
        {
            DrawEnum(config.FindPropertyRelative("opaque"), AtoLoc.Tr("ui.compressionOpaque"));
            DrawEnum(config.FindPropertyRelative("transparent"), AtoLoc.Tr("ui.compressionTransparent"));
            DrawEnum(config.FindPropertyRelative("normalMap"), AtoLoc.Tr("ui.compressionNormal"));
            DrawEnum(config.FindPropertyRelative("grayscale"), AtoLoc.Tr("ui.compressionGray"));
        }

        private static void DrawEnum(SerializedProperty property, string label)
        {
            EditorGUILayout.PropertyField(property, new GUIContent(label));
        }

        private void DrawPlatformOverride(SerializedObject serializedObject, string path, string enableKey,
            AtoTargetPlatform platform)
        {
            var overrideProp = serializedObject.FindProperty($"settings.{path}.enabled");
            EditorGUILayout.PropertyField(overrideProp, new GUIContent(AtoLoc.Tr(enableKey)));
            if (overrideProp.boolValue)
            {
                EditorGUI.indentLevel++;
                var compression = serializedObject.FindProperty($"settings.{path}.compression");
                DrawCompressionConfig(compression, enableKey);
                EditorGUILayout.PropertyField(serializedObject.FindProperty($"settings.{path}.npot"),
                    new GUIContent(AtoLoc.Tr("ui.npot")));
                EditorGUI.indentLevel--;
            }
        }
    }
}

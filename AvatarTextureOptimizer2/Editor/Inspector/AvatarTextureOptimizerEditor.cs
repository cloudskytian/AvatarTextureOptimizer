using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizerComponent))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        bool _adv;
        bool _fmt;
        SerializedProperty _quality;

        void OnEnable()
        {
            _quality = serializedObject.FindProperty("quality");
        }

        public override void OnInspectorGUI()
        {
            var c = (AvatarTextureOptimizerComponent)target;
            AtoI18n.SetMode(c.language);
            serializedObject.Update();

            EditorGUILayout.HelpBox(AtoI18n.T("comp.help"), MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("language"), new GUIContent(AtoI18n.T("comp.language")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("verboseLogging"), new GUIContent(AtoI18n.T("comp.verbose")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("generateAtlas"), new GUIContent(AtoI18n.T("comp.generateAtlas")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("experimentalNpot"), new GUIContent(AtoI18n.T("comp.npot")));

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("qualityPreset"), new GUIContent(AtoI18n.T("comp.quality")));
            if (EditorGUI.EndChangeCheck() && c.qualityPreset != AtoQualityPreset.Custom)
            {
                var p = AtoQualityParameters.ForPreset(c.qualityPreset);
                c.quality = p;
                EditorUtility.SetDirty(c);
            }

            _adv = EditorGUILayout.Foldout(_adv, AtoI18n.T("comp.advanced"));
            if (_adv && _quality != null)
            {
                EditorGUI.indentLevel++;
                if (c.qualityPreset != AtoQualityPreset.Custom)
                    EditorGUILayout.HelpBox("Switch to Custom to edit without being overwritten. / 切换到自定义以免被挡位覆盖。", MessageType.None);
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("targetQuality"));
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("msSsimMin"));
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("ciede2000Max"));
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("alphaRmseMax"));
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("cutoutIouMin"));
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("normalAngleDegMax"));
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("normalP95AngleDegMax"));
                EditorGUILayout.PropertyField(_quality.FindPropertyRelative("grayRmseMax"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("minPixelDensity"), new GUIContent(AtoI18n.T("comp.minDensity")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxPixelDensity"), new GUIContent(AtoI18n.T("comp.maxDensity")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("minPadding"), new GUIContent(AtoI18n.T("comp.padding")));

            _fmt = EditorGUILayout.Foldout(_fmt, AtoI18n.T("comp.formats"));
            if (_fmt)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("opaqueFormat"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("transparentFormat"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("normalFormat"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("grayFormat"));
                EditorGUILayout.LabelField(AtoI18n.T("comp.mips"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("mipStreamingAlbedo"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("mipStreamingNormal"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("mipStreamingMask"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("mipStreamingGray"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateMaterials"), new GUIContent(AtoI18n.T("comp.dedupMat")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateTextures"), new GUIContent(AtoI18n.T("comp.dedupTex")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), new GUIContent(AtoI18n.T("comp.whitelist")), true);

            DrawPlatform("PC", serializedObject.FindProperty("pc"));
            DrawPlatform("Android", serializedObject.FindProperty("android"));
            DrawPlatform("iOS", serializedObject.FindProperty("ios"));

            serializedObject.ApplyModifiedProperties();
        }

        static void DrawPlatform(string label, SerializedProperty p)
        {
            if (p == null) return;
            var en = p.FindPropertyRelative("enabled");
            en.boolValue = EditorGUILayout.ToggleLeft($"{AtoI18n.T("comp.platform")}: {label}", en.boolValue);
            if (!en.boolValue) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(p.FindPropertyRelative("qualityPreset"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("generateAtlas"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("experimentalNpot"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("minPadding"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("opaqueFormat"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("transparentFormat"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("normalFormat"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("grayFormat"));
            EditorGUI.indentLevel--;
        }
    }
}

using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(Net.Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        bool _adv;
        bool _plat;

        public override void OnInspectorGUI()
        {
            var t = (Net.Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer)target;
            EditorGUILayout.HelpBox(I18n.T(t.language, "component.title") +
                                    "\n挂在带 VRCAvatarDescriptor 的根上。每个 Avatar 只能有一个。", MessageType.Info);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), new GUIContent(I18n.T(t.language, "component.whitelist")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("optimizeTextures"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("optimizeMaterials"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("language"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("verboseLogs"));

            DrawSettings("Common", serializedObject.FindProperty("common"), t.common, true);

            _plat = EditorGUILayout.Foldout(_plat, I18n.T(t.language, "component.platform"));
            if (_plat)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("enablePcOverride"));
                if (t.enablePcOverride) DrawSettings("PC", serializedObject.FindProperty("pc"), t.pc, false);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("enableAndroidOverride"));
                if (t.enableAndroidOverride) DrawSettings("Android", serializedObject.FindProperty("android"), t.android, false);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("enableIosOverride"));
                if (t.enableIosOverride) DrawSettings("iOS", serializedObject.FindProperty("ios"), t.ios, false);
            }

            serializedObject.ApplyModifiedProperties();
        }

        void DrawSettings(string label, SerializedProperty p, AtoPlatformSettings live, bool showAll)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(p.FindPropertyRelative("GenerateAtlas"), new GUIContent(I18n.T("Auto", "component.generateAtlas")));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("QualityPreset"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("MinPadding"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("MinPixelDensity"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("MaxPixelDensity"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("ExperimentalNpot"));
            _adv = EditorGUILayout.Foldout(_adv, I18n.T("Auto", "component.advanced"));
            if (_adv)
            {
                var q = p.FindPropertyRelative("QualityParameters");
                EditorGUI.BeginDisabledGroup(live.QualityPreset != AtoQualityPreset.Custom && live.QualityPreset != AtoQualityPreset.Custom);
                EditorGUILayout.PropertyField(q, true);
                EditorGUI.EndDisabledGroup();
                if (live.QualityPreset != AtoQualityPreset.Custom)
                    EditorGUILayout.HelpBox("切换挡位会覆盖参数；Custom 挡位不会被覆盖。", MessageType.None);
            }
            if (showAll)
            {
                EditorGUILayout.PropertyField(p.FindPropertyRelative("OpaqueFormat"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("AlphaFormat"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("NormalFormat"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("GrayFormat"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("MipStreamingAlbedo"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("MipStreamingNormal"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("MipStreamingMask"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("MipStreamingGray"));
            }
            EditorGUI.indentLevel--;
        }
    }
}

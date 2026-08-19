// ============================================================================
// AvatarTextureOptimizerEditor.cs — 组件 Inspector / Component inspector
// (EN) Custom inspector for the AvatarTextureOptimizer component with
//      localized labels and foldouts for advanced options.
// (ZH) AvatarTextureOptimizer 组件的自定义 Inspector，带本地化标签与高级折叠项。
// ============================================================================

using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class AvatarTextureOptimizerEditor : Editor
    {
        private string _lang = "en";

        private void OnEnable()
        {
            _lang = ATOLocalization.ResolveLanguage((AvatarTextureOptimizer)target);
        }

        public override void OnInspectorGUI()
        {
            var comp = (AvatarTextureOptimizer)target;
            _lang = ATOLocalization.ResolveLanguage(comp);

            serializedObject.Update();

            EditorGUILayout.HelpBox(T("ato.desc"), MessageType.Info);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("enable"),
                new GUIContent(T("ato.enable")));

            DrawQuality(comp);
            DrawAtlas(comp);
            DrawCompression(comp);
            DrawDedup(comp);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("ato.section.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("ato.section.localization"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("language"), new GUIContent("Language"));

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawQuality(AvatarTextureOptimizer comp)
        {
            comp.foldQuality = EditorGUILayout.Foldout(comp.foldQuality, T("ato.section.quality"), true);
            if (!comp.foldQuality) return;

            var preset = serializedObject.FindProperty("quality.preset");
            EditorGUILayout.PropertyField(preset, new GUIContent(T("ato.quality.preset")));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.minPixelDensity"),
                new GUIContent("Min px/m (最小像素密度)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("quality.maxPixelDensity"),
                new GUIContent("Max px/m (最大像素密度)"));

            // 自定义阈值（折叠）/ custom thresholds (foldout)
            EditorGUILayout.Space();
            comp.foldAdvanced = EditorGUILayout.Foldout(comp.foldAdvanced, "Custom thresholds (自定义阈值)", true);
            if (comp.foldAdvanced)
            {
                var c = serializedObject.FindProperty("quality.custom");
                EditorGUILayout.PropertyField(c.FindPropertyRelative("msSsim"), new GUIContent("MS-SSIM"));
                EditorGUILayout.PropertyField(c.FindPropertyRelative("deltaE2000"), new GUIContent("ΔE2000"));
                EditorGUILayout.PropertyField(c.FindPropertyRelative("alphaIoU"), new GUIContent("Alpha IoU (Cutout)"));
                EditorGUILayout.PropertyField(c.FindPropertyRelative("alphaRmse"), new GUIContent("Alpha RMSE (Blend)"));
                EditorGUILayout.PropertyField(c.FindPropertyRelative("normalAngleErrorDeg"), new GUIContent("Normal angle (deg)"));
                EditorGUILayout.PropertyField(c.FindPropertyRelative("normalP95"), new GUIContent("Normal p95"));
                EditorGUILayout.PropertyField(c.FindPropertyRelative("grayRmse"), new GUIContent("Gray RMSE"));
            }
        }

        private void DrawAtlas(AvatarTextureOptimizer comp)
        {
            EditorGUILayout.LabelField(T("ato.section.atlas"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("atlas.enableAtlas"), new GUIContent(T("ato.atlas.enable")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("atlas.padding"), new GUIContent(T("ato.atlas.padding")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("atlas.allowNPot"), new GUIContent(T("ato.atlas.allowNPot")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("atlas.maxAtlasSizePC"), new GUIContent(T("ato.atlas.maxPC")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("atlas.maxAtlasSizeMobile"), new GUIContent(T("ato.atlas.maxMobile")));
        }

        private void DrawCompression(AvatarTextureOptimizer comp)
        {
            comp.foldCompression = EditorGUILayout.Foldout(comp.foldCompression, T("ato.section.compression"), true);
            if (!comp.foldCompression) return;
            DrawTextureClass("opaque", "Opaque (不透明)");
            DrawTextureClass("transparent", "Transparent (透明)");
            DrawTextureClass("normal", "Normal (法线)");
            DrawTextureClass("grayscale", "Grayscale (灰度)");
        }

        private void DrawTextureClass(string path, string label)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty($"compression.{path}.format"), new GUIContent("Format"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty($"compression.{path}.mipmaps"), new GUIContent("Mipmaps (+MipStreaming)"));
        }

        private void DrawDedup(AvatarTextureOptimizer comp)
        {
            EditorGUILayout.LabelField(T("ato.section.dedup"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("dedup.materials"), new GUIContent(T("ato.dedup.materials")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("dedup.textures"), new GUIContent(T("ato.dedup.textures")));
        }

        private string T(string key) => ATOLocalization.T(_lang, key);
    }
}

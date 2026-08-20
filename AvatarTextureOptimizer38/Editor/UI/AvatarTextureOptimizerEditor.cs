using System;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Inspector: beginner-friendly defaults, advanced folded. / 面向小白的检视面板，高级选项折叠。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private bool _advQuality;
        private bool _platPc, _platAndroid, _platIos;

        public override void OnInspectorGUI()
        {
            var t = (AvatarTextureOptimizer)target;
            var lang = t.language;
            AtoLoc.EnsureLoaded();

            EditorGUILayout.HelpBox(AtoLoc.T("ato.inspector.help", lang), MessageType.Info);

            if (!t.HasAvatarDescriptor)
                EditorGUILayout.HelpBox(AtoLoc.T("ato.inspector.missingDescriptor", lang), MessageType.Error);

            var others = t.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (t.transform.parent != null)
            {
                var rootOthers = t.transform.root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
                if (rootOthers.Length > 1)
                    EditorGUILayout.HelpBox(AtoLoc.T("ato.inspector.duplicate", lang), MessageType.Error);
            }
            else if (others.Length > 1)
                EditorGUILayout.HelpBox(AtoLoc.T("ato.inspector.duplicate", lang), MessageType.Error);

            serializedObject.Update();

            DrawLang(t);
            EditorGUILayout.Space();
            DrawAtlas(t, lang);
            EditorGUILayout.Space();
            DrawQuality(t, lang);
            EditorGUILayout.Space();
            DrawDedup(lang);
            EditorGUILayout.Space();
            DrawWhitelist(lang);
            EditorGUILayout.Space();
            DrawPlatform(t, lang);
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("verboseLog"),
                new GUIContent(AtoLoc.T("ato.inspector.verbose", lang)));

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLang(AvatarTextureOptimizer t)
        {
            var langs = AtoLoc.AvailableLanguages;
            EditorGUILayout.LabelField(AtoLoc.T("ato.inspector.language", t.language), EditorStyles.boldLabel);
            var mode = (AtoLanguageMode)EditorGUILayout.EnumPopup(t.language);
            if (mode != t.language)
            {
                Undo.RecordObject(t, "ATO language");
                t.language = mode;
            }
            EditorGUILayout.LabelField("i18n", string.Join(", ", langs));
        }

        private void DrawAtlas(AvatarTextureOptimizer t, AtoLanguageMode lang)
        {
            EditorGUILayout.LabelField(AtoLoc.T("ato.inspector.atlas", lang), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("generateAtlas"),
                new GUIContent(AtoLoc.T("ato.inspector.generateAtlas", lang), AtoLoc.T("ato.inspector.generateAtlas.tooltip", lang)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("experimentalNpot"),
                new GUIContent(AtoLoc.T("ato.inspector.npot", lang), AtoLoc.T("ato.inspector.npot.tooltip", lang)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("minPadding"),
                new GUIContent(AtoLoc.T("ato.inspector.padding", lang)));
        }

        private void DrawQuality(AvatarTextureOptimizer t, AtoLanguageMode lang)
        {
            EditorGUILayout.LabelField(AtoLoc.T("ato.inspector.quality", lang), EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("qualityPreset"),
                new GUIContent(AtoLoc.T("ato.inspector.preset", lang), AtoLoc.T("ato.inspector.preset.tooltip", lang)));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                if (t.qualityPreset != QualityPreset.Custom)
                    t.qualityParameters = QualityParameters.ForPreset(t.qualityPreset);
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("minPixelDensity"),
                new GUIContent(AtoLoc.T("ato.inspector.minDensity", lang)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxPixelDensity"),
                new GUIContent(AtoLoc.T("ato.inspector.maxDensity", lang)));

            _advQuality = EditorGUILayout.Foldout(_advQuality, AtoLoc.T("ato.inspector.advancedQuality", lang), true);
            if (_advQuality)
            {
                EditorGUI.indentLevel++;
                var p = serializedObject.FindProperty("qualityParameters");
                // View always; edit only in Custom. / 始终可看，仅自定义可改。
                EditorGUI.BeginDisabledGroup(t.qualityPreset != QualityPreset.Custom);
                Prop(p, "msSsimMin", "ato.inspector.msssim", lang);
                Prop(p, "ciede2000Max", "ato.inspector.de", lang);
                Prop(p, "cutoutIouMin", "ato.inspector.iou", lang);
                Prop(p, "blendAlphaRmseMax", "ato.inspector.rmse", lang);
                Prop(p, "normalMeanAngleDegMax", "ato.inspector.nmean", lang);
                Prop(p, "normalP95AngleDegMax", "ato.inspector.np95", lang);
                Prop(p, "grayRmseMax", "ato.inspector.gray", lang);
                EditorGUI.EndDisabledGroup();
                EditorGUI.indentLevel--;
            }
        }

        private void DrawDedup(AtoLanguageMode lang)
        {
            EditorGUILayout.LabelField(AtoLoc.T("ato.inspector.dedup", lang), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateMaterials"),
                new GUIContent(AtoLoc.T("ato.inspector.dedupMat", lang)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("deduplicateTextures"),
                new GUIContent(AtoLoc.T("ato.inspector.dedupTex", lang)));
        }

        private void DrawWhitelist(AtoLanguageMode lang)
        {
            EditorGUILayout.LabelField(AtoLoc.T("ato.inspector.whitelist", lang), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(AtoLoc.T("ato.inspector.whitelist.help", lang), MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), true);
        }

        private void DrawPlatform(AvatarTextureOptimizer t, AtoLanguageMode lang)
        {
            EditorGUILayout.LabelField(AtoLoc.T("ato.inspector.platform", lang), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(AtoLoc.T("ato.inspector.platform.help", lang), MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enablePlatformOverride"),
                new GUIContent(AtoLoc.T("ato.inspector.platformEnable", lang)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultPlatform"));
            if (!t.enablePlatformOverride) return;

            _platPc = FoldPlat(_platPc, "PC", serializedObject.FindProperty("pcSettings"), lang, t.experimentalNpot);
            _platAndroid = FoldPlat(_platAndroid, "Android", serializedObject.FindProperty("androidSettings"), lang, t.experimentalNpot);
            _platIos = FoldPlat(_platIos, "iOS", serializedObject.FindProperty("iosSettings"), lang, t.experimentalNpot);
        }

        private bool FoldPlat(bool open, string name, SerializedProperty p, AtoLanguageMode lang, bool npot)
        {
            open = EditorGUILayout.Foldout(open, name, true);
            if (!open) return false;
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(p.FindPropertyRelative("opaqueFormat"), new GUIContent(AtoLoc.T("ato.inspector.opaqueFormat", lang)));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("transparentFormat"), new GUIContent(AtoLoc.T("ato.inspector.transparentFormat", lang)));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("normalFormat"), new GUIContent(AtoLoc.T("ato.inspector.normalFormat", lang)));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("grayFormat"), new GUIContent(AtoLoc.T("ato.inspector.grayFormat", lang)));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("mipStreamingOpaque"), new GUIContent(AtoLoc.T("ato.inspector.mip", lang) + " (opaque)"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("mipStreamingTransparent"), new GUIContent(AtoLoc.T("ato.inspector.mip", lang) + " (alpha)"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("mipStreamingNormal"), new GUIContent(AtoLoc.T("ato.inspector.mip", lang) + " (normal)"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("mipStreamingGray"), new GUIContent(AtoLoc.T("ato.inspector.mip", lang) + " (gray)"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("useCrunch"), new GUIContent(AtoLoc.T("ato.inspector.crunch", lang)));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("crunchQuality"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("compressorQuality"));
            if (npot) EditorGUILayout.HelpBox(AtoLoc.T("ato.warn.pvrtcNpot", lang), MessageType.Warning);
            EditorGUI.indentLevel--;
            return true;
        }

        private static void Prop(SerializedProperty parent, string field, string key, AtoLanguageMode lang)
        {
            var p = parent.FindPropertyRelative(field);
            if (p != null) EditorGUILayout.PropertyField(p, new GUIContent(AtoLoc.T(key, lang)));
        }
    }
}

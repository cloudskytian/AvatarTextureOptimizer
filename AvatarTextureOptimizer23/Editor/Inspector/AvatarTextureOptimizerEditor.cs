using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Beginner-first inspector. Advanced / platform / debug stay folded.
    /// 面向小白的检视器。高级 / 平台 / 调试默认折叠。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private bool _adv;
        private bool _plat;
        private bool _dbg;
        private ATOQualityPreset _lastPreset;

        private void OnEnable()
        {
            var t = (AvatarTextureOptimizer)target;
            _lastPreset = t.qualityPreset;
            ATOLoc.SetMode(t.language);
        }

        public override void OnInspectorGUI()
        {
            var t = (AvatarTextureOptimizer)target;
            ATOLoc.SetMode(t.language);
            serializedObject.Update();

            EditorGUILayout.HelpBox(ATOLoc.T("ato.ui.help"), MessageType.Info);

#if ATO_VRCSDK3_AVATARS
            if (t.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
                EditorGUILayout.HelpBox(ATOLoc.T("ato.error.need_descriptor"), MessageType.Error);
#endif
            var others = t.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (others != null && others.Length > 1)
                EditorGUILayout.HelpBox(ATOLoc.T("ato.error.multiple_components"), MessageType.Error);

            EditorGUILayout.Space(4);
            t.generateAtlas = EditorGUILayout.Toggle(new GUIContent(ATOLoc.T("ato.ui.generate_atlas"), ATOLoc.T("ato.ui.generate_atlas.tip")), t.generateAtlas);

            EditorGUI.BeginChangeCheck();
            t.qualityPreset = (ATOQualityPreset)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.quality"), t.qualityPreset);
            if (EditorGUI.EndChangeCheck() || t.qualityPreset != _lastPreset)
            {
                if (t.qualityPreset != ATOQualityPreset.Custom)
                    t.qualityParameters = ATOQualityParameters.ForPreset(t.qualityPreset);
                _lastPreset = t.qualityPreset;
            }

            t.minPixelDensity = EditorGUILayout.FloatField(ATOLoc.T("ato.ui.min_density"), t.minPixelDensity);
            t.maxPixelDensity = EditorGUILayout.FloatField(ATOLoc.T("ato.ui.max_density"), t.maxPixelDensity);
            DrawDensityPresets(t);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), new GUIContent(ATOLoc.T("ato.ui.whitelist"), ATOLoc.T("ato.ui.whitelist.tip")), true);

            _adv = EditorGUILayout.Foldout(_adv, ATOLoc.T("ato.ui.advanced"), true);
            if (_adv)
            {
                EditorGUI.indentLevel++;
                DrawQualityFields(t);
                t.minPadding = (ATOMinPadding)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.min_padding"), t.minPadding);
                t.experimentalNpot = EditorGUILayout.Toggle(new GUIContent(ATOLoc.T("ato.ui.npot"), ATOLoc.T("ato.ui.npot.tip")), t.experimentalNpot);
                t.enableMaterialDedup = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.dedup_mat"), t.enableMaterialDedup);
                t.enableTextureDedup = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.dedup_tex"), t.enableTextureDedup);
                EditorGUILayout.LabelField(ATOLoc.T("ato.ui.mip"), EditorStyles.boldLabel);
                t.mipStreamingOpaque = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_opaque"), t.mipStreamingOpaque);
                t.mipStreamingTransparent = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_alpha"), t.mipStreamingTransparent);
                t.mipStreamingNormal = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_normal"), t.mipStreamingNormal);
                t.mipStreamingGray = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_gray"), t.mipStreamingGray);
                EditorGUILayout.LabelField(ATOLoc.T("ato.ui.compress"), EditorStyles.boldLabel);
                t.formatOpaque = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_opaque"), t.formatOpaque);
                t.formatTransparent = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_alpha"), t.formatTransparent);
                t.formatNormal = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_normal"), t.formatNormal);
                t.formatGray = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_gray"), t.formatGray);
                EditorGUI.indentLevel--;
            }

            _plat = EditorGUILayout.Foldout(_plat, ATOLoc.T("ato.ui.platform"), true);
            if (_plat)
            {
                EditorGUI.indentLevel++;
                DrawPlatform(t.pcOverride, ATOLoc.T("ato.ui.plat_pc"));
                DrawPlatform(t.androidOverride, ATOLoc.T("ato.ui.plat_android"));
                DrawPlatform(t.iosOverride, ATOLoc.T("ato.ui.plat_ios"));
                EditorGUI.indentLevel--;
            }

            _dbg = EditorGUILayout.Foldout(_dbg, ATOLoc.T("ato.ui.debug"), true);
            if (_dbg)
            {
                EditorGUI.indentLevel++;
                t.language = (ATOLanguageMode)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.language"), t.language);
                t.debugLog = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.debug_log"), t.debugLog);
                EditorGUI.indentLevel--;
            }

            if (GUI.changed) EditorUtility.SetDirty(t);
            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawDensityPresets(AvatarTextureOptimizer t)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(ATOLoc.T("ato.ui.density_presets"));
            foreach (ATOPixelDensityPreset p in System.Enum.GetValues(typeof(ATOPixelDensityPreset)))
            {
                if (GUILayout.Button(((int)p).ToString(), GUILayout.Width(48)))
                {
                    // Click min then max intuitively: first click sets min if below, else max.
                    // 点击挡位：若小于当前最小则抬最小，否则改最大。
                    if ((int)p < t.minPixelDensity) t.minPixelDensity = (int)p;
                    else t.maxPixelDensity = (int)p;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawQualityFields(AvatarTextureOptimizer t)
        {
            var custom = t.qualityPreset == ATOQualityPreset.Custom;
            EditorGUI.BeginDisabledGroup(!custom);
            var q = custom ? t.customQualityParameters : t.qualityParameters;
            q.msSsim = EditorGUILayout.Slider("MS-SSIM", q.msSsim, 0f, 1f);
            q.deltaE00 = EditorGUILayout.Slider("ΔE00", q.deltaE00, 0f, 20f);
            q.alphaRmse = EditorGUILayout.Slider("Alpha RMSE", q.alphaRmse, 0f, 1f);
            q.alphaIou = EditorGUILayout.Slider("Cutout IoU", q.alphaIou, 0f, 1f);
            q.normalAngleDeg = EditorGUILayout.Slider("Normal °", q.normalAngleDeg, 0f, 45f);
            q.normalP95Deg = EditorGUILayout.Slider("Normal p95 °", q.normalP95Deg, 0f, 90f);
            q.grayRmse = EditorGUILayout.Slider("Gray RMSE", q.grayRmse, 0f, 1f);
            if (custom) t.customQualityParameters = q;
            else t.qualityParameters = q;
            EditorGUI.EndDisabledGroup();
            if (!custom)
                EditorGUILayout.HelpBox(ATOLoc.T("ato.ui.quality_locked"), MessageType.None);
        }

        private static void DrawPlatform(ATOPlatformSettings s, string label)
        {
            s.enabled = EditorGUILayout.ToggleLeft(label, s.enabled);
            if (!s.enabled) return;
            EditorGUI.indentLevel++;
            s.generateAtlas = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.generate_atlas"), s.generateAtlas);
            s.qualityPreset = (ATOQualityPreset)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.quality"), s.qualityPreset);
            if (s.qualityPreset != ATOQualityPreset.Custom)
                s.qualityParameters = ATOQualityParameters.ForPreset(s.qualityPreset);
            s.minPixelDensity = EditorGUILayout.FloatField(ATOLoc.T("ato.ui.min_density"), s.minPixelDensity);
            s.maxPixelDensity = EditorGUILayout.FloatField(ATOLoc.T("ato.ui.max_density"), s.maxPixelDensity);
            s.minPadding = (ATOMinPadding)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.min_padding"), s.minPadding);
            s.experimentalNpot = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.npot"), s.experimentalNpot);
            s.formatOpaque = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_opaque"), s.formatOpaque);
            s.formatTransparent = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_alpha"), s.formatTransparent);
            s.formatNormal = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_normal"), s.formatNormal);
            s.formatGray = (ATOCompressionChoice)EditorGUILayout.EnumPopup(ATOLoc.T("ato.ui.fmt_gray"), s.formatGray);
            s.mipStreamingOpaque = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_opaque"), s.mipStreamingOpaque);
            s.mipStreamingTransparent = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_alpha"), s.mipStreamingTransparent);
            s.mipStreamingNormal = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_normal"), s.mipStreamingNormal);
            s.mipStreamingGray = EditorGUILayout.Toggle(ATOLoc.T("ato.ui.mip_gray"), s.mipStreamingGray);
            EditorGUI.indentLevel--;
        }
    }
}

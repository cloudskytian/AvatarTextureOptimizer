// English: Beginner-friendly inspector; advanced quality folded. Platform overrides hidden until enabled.
// 中文：面向小白的检视器；高级质量参数折叠。平台覆盖需勾选后才显示。
using net.fosa.ato;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AtoInspector : Editor
    {
        private bool _adv, _comp, _plat;

        public override void OnInspectorGUI()
        {
            var t = (AvatarTextureOptimizer)target;
            AtoI18n.SetMode(t.language);
            EditorGUILayout.HelpBox(AtoI18n.T("ui.help"), MessageType.Info);

            EditorGUI.BeginChangeCheck();
            t.language = (AtoLanguageMode)EditorGUILayout.EnumPopup(AtoI18n.T("ui.language"), t.language);
            t.generateAtlas = EditorGUILayout.Toggle(AtoI18n.T("ui.generate_atlas"), t.generateAtlas);
            t.experimentalNpot = EditorGUILayout.Toggle(AtoI18n.T("ui.npot"), t.experimentalNpot);
            t.qualityPreset = (AtoQualityPreset)EditorGUILayout.EnumPopup(AtoI18n.T("ui.quality"), t.qualityPreset);
            if (t.qualityPreset != AtoQualityPreset.Custom)
                t.quality = AtoQualityThresholds.ForPreset(t.qualityPreset);

            t.minPadding = (AtoMinPadding)EditorGUILayout.EnumPopup(AtoI18n.T("ui.padding"), t.minPadding);
            t.minPixelDensity = (AtoPixelDensity)EditorGUILayout.EnumPopup(AtoI18n.T("ui.min_density"), t.minPixelDensity);
            t.maxPixelDensity = (AtoPixelDensity)EditorGUILayout.EnumPopup(AtoI18n.T("ui.max_density"), t.maxPixelDensity);
            t.dedupeTextures = EditorGUILayout.Toggle(AtoI18n.T("ui.dedupe_tex"), t.dedupeTextures);
            t.dedupeMaterials = EditorGUILayout.Toggle(AtoI18n.T("ui.dedupe_mat"), t.dedupeMaterials);
            t.verboseLogs = EditorGUILayout.Toggle(AtoI18n.T("ui.verbose"), t.verboseLogs);

            _adv = EditorGUILayout.Foldout(_adv, AtoI18n.T("ui.advanced"));
            if (_adv)
                DrawThresholds(ref t.quality, t.qualityPreset == AtoQualityPreset.Custom);

            _comp = EditorGUILayout.Foldout(_comp, AtoI18n.T("ui.compression"));
            if (_comp) DrawCompression(t.compression);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), new GUIContent(AtoI18n.T("ui.whitelist")), true);
            serializedObject.ApplyModifiedProperties();

            _plat = EditorGUILayout.Foldout(_plat, AtoI18n.T("ui.platform"));
            if (_plat)
            {
                t.overridePC = EditorGUILayout.Toggle("PC", t.overridePC);
                if (t.overridePC) DrawPlatform(t.pc, "PC");
                t.overrideAndroid = EditorGUILayout.Toggle("Android", t.overrideAndroid);
                if (t.overrideAndroid) DrawPlatform(t.android, "Android");
                t.overrideIOS = EditorGUILayout.Toggle("iOS", t.overrideIOS);
                if (t.overrideIOS) DrawPlatform(t.ios, "iOS");
            }

            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(t);
        }

        private static void DrawThresholds(ref AtoQualityThresholds q, bool editable)
        {
            EditorGUI.BeginDisabledGroup(!editable);
            q.msSsim = EditorGUILayout.Slider("MS-SSIM ≥", q.msSsim, 0f, 1f);
            q.ciede2000 = EditorGUILayout.Slider("ΔE00 ≤", q.ciede2000, 0f, 20f);
            q.alphaRmse = EditorGUILayout.Slider("Alpha RMSE ≤", q.alphaRmse, 0f, 1f);
            q.cutoutIou = EditorGUILayout.Slider("Cutout IoU ≥", q.cutoutIou, 0f, 1f);
            q.normalAngleDeg = EditorGUILayout.Slider("Normal mean ° ≤", q.normalAngleDeg, 0f, 45f);
            q.normalP95Deg = EditorGUILayout.Slider("Normal p95 ° ≤", q.normalP95Deg, 0f, 45f);
            q.grayRmse = EditorGUILayout.Slider("Gray RMSE ≤", q.grayRmse, 0f, 1f);
            EditorGUI.EndDisabledGroup();
            if (!editable)
                EditorGUILayout.HelpBox(AtoI18n.T("ui.preset_locked"), MessageType.None);
        }

        private static void DrawCompression(AtoCompressionSet c)
        {
            if (c == null) return;
            c.opaque = (AtoSafeCompression)EditorGUILayout.EnumPopup(AtoI18n.T("ui.fmt_opaque"), c.opaque);
            c.transparent = (AtoSafeCompression)EditorGUILayout.EnumPopup(AtoI18n.T("ui.fmt_alpha"), c.transparent);
            c.normal = (AtoSafeCompression)EditorGUILayout.EnumPopup(AtoI18n.T("ui.fmt_normal"), c.normal);
            c.gray = (AtoSafeCompression)EditorGUILayout.EnumPopup(AtoI18n.T("ui.fmt_gray"), c.gray);
            c.mipStreamingOpaque = EditorGUILayout.Toggle("Mip+Streaming Opaque", c.mipStreamingOpaque);
            c.mipStreamingTransparent = EditorGUILayout.Toggle("Mip+Streaming Alpha", c.mipStreamingTransparent);
            c.mipStreamingNormal = EditorGUILayout.Toggle("Mip+Streaming Normal", c.mipStreamingNormal);
            c.mipStreamingGray = EditorGUILayout.Toggle("Mip+Streaming Gray", c.mipStreamingGray);
        }

        private static void DrawPlatform(AtoPlatformSettings p, string label)
        {
            EditorGUI.indentLevel++;
            p.qualityPreset = (AtoQualityPreset)EditorGUILayout.EnumPopup(label + " quality", p.qualityPreset);
            p.ApplyPresetIfNotCustom();
            p.generateAtlas = EditorGUILayout.Toggle("Atlas", p.generateAtlas);
            p.experimentalNpot = EditorGUILayout.Toggle("NPOT", p.experimentalNpot);
            p.minPadding = (AtoMinPadding)EditorGUILayout.EnumPopup("Padding", p.minPadding);
            DrawCompression(p.compression);
            EditorGUI.indentLevel--;
        }
    }
}

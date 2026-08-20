using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private bool _advQ, _advFmt, _advPlat;
        private static readonly AtoQualityPreset[] Presets =
        {
            AtoQualityPreset.NearLossless, AtoQualityPreset.Ultra, AtoQualityPreset.High,
            AtoQualityPreset.Medium, AtoQualityPreset.Low, AtoQualityPreset.Custom
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var t = (AvatarTextureOptimizer)target;

            EditorGUILayout.HelpBox(AtoI18n.T("component.help"), MessageType.Info);

            DrawLang(t);

            EditorGUI.BeginChangeCheck();
            t.generateAtlas = EditorGUILayout.Toggle(new GUIContent(AtoI18n.T("opt.generateAtlas"), AtoI18n.T("opt.generateAtlas.help")), t.generateAtlas);
            t.experimentalNpot = EditorGUILayout.Toggle(new GUIContent(AtoI18n.T("opt.npot"), AtoI18n.T("opt.npot.help")), t.experimentalNpot);

            DrawPreset(ref t.qualityPreset, ref t.quality, AtoI18n.T("opt.quality"));
            t.minPadding = (AtoMinPadding)EditorGUILayout.EnumPopup(AtoI18n.T("opt.minPadding"), t.minPadding);
            t.minDensity = (AtoPixelDensityStop)EditorGUILayout.EnumPopup(AtoI18n.T("opt.minDensity"), t.minDensity);
            t.maxDensity = (AtoPixelDensityStop)EditorGUILayout.EnumPopup(AtoI18n.T("opt.maxDensity"), t.maxDensity);
            t.dedupeMaterials = EditorGUILayout.Toggle(AtoI18n.T("opt.dedupeMat"), t.dedupeMaterials);
            t.dedupeTextures = EditorGUILayout.Toggle(AtoI18n.T("opt.dedupeTex"), t.dedupeTextures);
            t.verboseLog = EditorGUILayout.Toggle(AtoI18n.T("opt.verbose"), t.verboseLog);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"),
                new GUIContent(AtoI18n.T("opt.whitelist"), AtoI18n.T("opt.whitelist.help")), true);

            _advQ = EditorGUILayout.Foldout(_advQ, AtoI18n.T("adv.quality"), true);
            if (_advQ)
            {
                EditorGUI.indentLevel++;
                DrawQualityFields(t.quality, t.qualityPreset == AtoQualityPreset.Custom);
                EditorGUI.indentLevel--;
            }

            _advFmt = EditorGUILayout.Foldout(_advFmt, AtoI18n.T("adv.formats"), true);
            if (_advFmt)
            {
                EditorGUI.indentLevel++;
                DrawFormats(t.formats, AtoPlatformUtil.Current(), t.experimentalNpot);
                EditorGUI.indentLevel--;
            }

            _advPlat = EditorGUILayout.Foldout(_advPlat, AtoI18n.T("adv.platform"), true);
            if (_advPlat)
            {
                EditorGUI.indentLevel++;
                DrawPlatform(AtoI18n.T("plat.pc"), ref t.overridePC, t.pc);
                DrawPlatform(AtoI18n.T("plat.android"), ref t.overrideAndroid, t.android);
                DrawPlatform(AtoI18n.T("plat.ios"), ref t.overrideIOS, t.ios);
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                if (t.qualityPreset != AtoQualityPreset.Custom)
                    t.quality = AtoQualitySettings.ForPreset(t.qualityPreset);
                EditorUtility.SetDirty(t);
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLang(AvatarTextureOptimizer t)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(AtoI18n.T("opt.language"));
            var langs = new List<string> { "auto" };
            langs.AddRange(AtoI18n.AvailableLanguages);
            var labels = new List<string> { AtoI18n.T("opt.language.auto") };
            foreach (var l in AtoI18n.AvailableLanguages) labels.Add(l);
            var cur = t.languageMode == AtoLanguageMode.Auto ? 0 : Mathf.Max(0, langs.IndexOf(AtoI18n.Normalize(t.manualLanguage)));
            if (cur < 0) cur = 0;
            var next = EditorGUILayout.Popup(cur, labels.ToArray());
            if (next == 0)
            {
                t.languageMode = AtoLanguageMode.Auto;
                AtoI18n.SetForcedLanguage(null);
            }
            else
            {
                t.languageMode = AtoLanguageMode.Manual;
                t.manualLanguage = langs[next];
                AtoI18n.SetForcedLanguage(t.manualLanguage);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawPreset(ref AtoQualityPreset preset, ref AtoQualitySettings q, string label)
        {
            var names = new string[Presets.Length];
            var idx = 0;
            for (var i = 0; i < Presets.Length; i++)
            {
                names[i] = AtoI18n.T("preset." + Presets[i]);
                if (Presets[i] == preset) idx = i;
            }
            var n = EditorGUILayout.Popup(label, idx, names);
            var np = Presets[n];
            if (np != preset)
            {
                preset = np;
                if (preset != AtoQualityPreset.Custom)
                    q = AtoQualitySettings.ForPreset(preset);
                else if (q == null)
                    q = new AtoQualitySettings();
            }
        }

        private static void DrawQualityFields(AtoQualitySettings q, bool editable)
        {
            if (q == null) return;
            using (new EditorGUI.DisabledScope(!editable && q != null))
            {
                // Custom is editable; other presets show values as read-only so users understand them.
                // Custom 可编辑；其他挡位只读展示，方便理解。
            }
            using (new EditorGUI.DisabledScope(!editable))
            {
                q.msSsim = EditorGUILayout.Slider("MS-SSIM", q.msSsim, 0f, 1f);
                q.deltaE = EditorGUILayout.Slider("CIEDE2000 ΔE", q.deltaE, 0f, 20f);
                q.alphaRmse = EditorGUILayout.Slider("Alpha RMSE (Blend)", q.alphaRmse, 0f, 1f);
                q.cutoutIou = EditorGUILayout.Slider("Cutout IoU", q.cutoutIou, 0f, 1f);
                q.normalAngleDeg = EditorGUILayout.Slider("Normal mean °", q.normalAngleDeg, 0f, 45f);
                q.normalP95Deg = EditorGUILayout.Slider("Normal p95 °", q.normalP95Deg, 0f, 45f);
                q.grayRmse = EditorGUILayout.Slider("Gray RMSE", q.grayRmse, 0f, 1f);
            }
        }

        private static void DrawFormats(AtoFormatSettings f, AtoPlatform plat, bool npot)
        {
            if (f == null) return;
            f.opaqueFormat = FormatPopup(AtoI18n.T("fmt.opaque"), f.opaqueFormat, false, npot, plat);
            f.opaqueMipStreaming = EditorGUILayout.Toggle(AtoI18n.T("fmt.mip") + " / " + AtoI18n.T("fmt.opaque"), f.opaqueMipStreaming);
            f.transparentFormat = FormatPopup(AtoI18n.T("fmt.transparent"), f.transparentFormat, true, npot, plat);
            f.transparentMipStreaming = EditorGUILayout.Toggle(AtoI18n.T("fmt.mip") + " / " + AtoI18n.T("fmt.transparent"), f.transparentMipStreaming);
            f.normalFormat = FormatPopup(AtoI18n.T("fmt.normal"), f.normalFormat, false, npot, plat);
            f.normalMipStreaming = EditorGUILayout.Toggle(AtoI18n.T("fmt.mip") + " / " + AtoI18n.T("fmt.normal"), f.normalMipStreaming);
            f.grayFormat = FormatPopup(AtoI18n.T("fmt.gray"), f.grayFormat, false, npot, plat);
            f.grayMipStreaming = EditorGUILayout.Toggle(AtoI18n.T("fmt.mip") + " / " + AtoI18n.T("fmt.gray"), f.grayMipStreaming);
        }

        private static AtoSafeFormat FormatPopup(string label, AtoSafeFormat cur, bool needAlpha, bool npot, AtoPlatform plat)
        {
            var opts = new List<AtoSafeFormat> { AtoSafeFormat.Auto };
            void Add(AtoSafeFormat f)
            {
                if (needAlpha && (f == AtoSafeFormat.DXT1 || f == AtoSafeFormat.RGB24 || f == AtoSafeFormat.ETC2_RGB ||
                                  f == AtoSafeFormat.PVRTC_RGB4 || f == AtoSafeFormat.BC4)) return;
                if (npot && (f == AtoSafeFormat.PVRTC_RGB4 || f == AtoSafeFormat.PVRTC_RGBA4)) return;
                opts.Add(f);
            }
            Add(AtoSafeFormat.RGBA32);
            Add(AtoSafeFormat.DXT1);
            Add(AtoSafeFormat.DXT5);
            Add(AtoSafeFormat.BC4);
            Add(AtoSafeFormat.BC5);
            Add(AtoSafeFormat.BC7);
            Add(AtoSafeFormat.ETC2_RGB);
            Add(AtoSafeFormat.ETC2_RGBA8);
            Add(AtoSafeFormat.ASTC_4x4);
            Add(AtoSafeFormat.ASTC_5x5);
            Add(AtoSafeFormat.ASTC_6x6);
            Add(AtoSafeFormat.ASTC_8x8);
            if (plat == AtoPlatform.iOS)
            {
                Add(AtoSafeFormat.PVRTC_RGB4);
                Add(AtoSafeFormat.PVRTC_RGBA4);
            }
            var labels = opts.ConvertAll(o => o.ToString()).ToArray();
            var idx = Mathf.Max(0, opts.IndexOf(cur));
            var n = EditorGUILayout.Popup(label, idx, labels);
            return opts[n];
        }

        private void DrawPlatform(string name, ref bool enabled, AtoPlatformOverride ov)
        {
            enabled = EditorGUILayout.ToggleLeft(name, enabled);
            if (!enabled || ov == null) return;
            EditorGUI.indentLevel++;
            ov.enabled = EditorGUILayout.Toggle("Enable override / 启用覆盖", ov.enabled);
            if (ov.enabled)
            {
                DrawPreset(ref ov.qualityPreset, ref ov.quality, AtoI18n.T("opt.quality"));
                ov.generateAtlas = EditorGUILayout.Toggle(AtoI18n.T("opt.generateAtlas"), ov.generateAtlas);
                ov.experimentalNpot = EditorGUILayout.Toggle(AtoI18n.T("opt.npot"), ov.experimentalNpot);
                ov.minPadding = (AtoMinPadding)EditorGUILayout.EnumPopup(AtoI18n.T("opt.minPadding"), ov.minPadding);
                if (ov.qualityPreset == AtoQualityPreset.Custom)
                    DrawQualityFields(ov.quality, true);
                DrawFormats(ov.formats, AtoPlatform.PC, ov.experimentalNpot);
            }
            EditorGUI.indentLevel--;
        }
    }
}

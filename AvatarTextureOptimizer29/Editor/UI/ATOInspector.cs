// ATO inspector: i18n labels, quality presets (edit -> Custom), advanced foldouts,
// platform overrides (params visible only when overridden), whitelist list.
// ATO 检查器：i18n 标签、质量挡位（编辑即切自定义）、高级折叠区、平台覆写
// （勾选才显示参数）、白名单列表。

using System;
using System.Linq;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class ATOInspector : UnityEditor.Editor
    {
        private bool _advanced, _platformFold;
        private int _platformTab;
        private static readonly int[] DensitySteps = { 512, 1024, 2048, 4096, 8192 };
        private static readonly int[] PaddingSteps = { 4, 8, 16, 32, 64 };

        private AvatarTextureOptimizer C => (AvatarTextureOptimizer)target;
        private string Lang => ATOL10n.ResolveLanguage(C.languageOverride);
        private string L(string key) => ATOL10n.Get(key, Lang);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawLanguage();
            EditorGUILayout.Space(4);

            // ---------------- general / 常规 ----------------
            EditorGUILayout.LabelField(L("ui.section.general"), EditorStyles.boldLabel);
            C.generateAtlas = EditorGUILayout.Toggle(GetContent("ui.generateAtlas", "ui.generateAtlas.tip"), C.generateAtlas);
            C.dedupTextures = EditorGUILayout.Toggle(GetContent("ui.dedupTextures", "ui.dedupTextures.tip"), C.dedupTextures);
            C.dedupMaterials = EditorGUILayout.Toggle(GetContent("ui.dedupMaterials", "ui.dedupMaterials.tip"), C.dedupMaterials);
            C.logLevel = (AtoLogLevel)EditorGUILayout.EnumPopup(L("ui.logLevel"), C.logLevel);

            EditorGUILayout.Space(6);

            // ---------------- whitelist / 白名单 ----------------
            EditorGUILayout.LabelField(L("ui.section.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(L("ui.whitelist.tip"), MessageType.Info);
            var wl = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.whitelist));
            EditorGUILayout.PropertyField(wl, new GUIContent(L("ui.whitelist")), true);

            EditorGUILayout.Space(6);

            // ---------------- quality (common = PC) ----------------
            EditorGUILayout.LabelField(L("ui.section.quality"), EditorStyles.boldLabel);
            DrawPreset(C.pcSettings);
            _advanced = EditorGUILayout.Foldout(_advanced, L("ui.section.advanced"), true);
            if (_advanced) DrawQualityParams(C.pcSettings, AtoPlatform.PC);

            EditorGUILayout.Space(6);

            // ---------------- platform overrides / 平台覆写 ----------------
            _platformFold = EditorGUILayout.Foldout(_platformFold, L("ui.section.platform"), true);
            if (_platformFold)
            {
                EditorGUILayout.HelpBox(L("pl.title"), MessageType.Info);
                _platformTab = GUILayout.Toolbar(_platformTab, new[] { L("pl.pc"), L("pl.android"), L("pl.ios") });
                var (ps, plat) = _platformTab switch
                {
                    1 => (C.androidSettings, AtoPlatform.Android),
                    2 => (C.iosSettings, AtoPlatform.iOS),
                    _ => (C.pcSettings, AtoPlatform.PC),
                };
                if (plat == AtoPlatform.PC)
                    EditorGUILayout.HelpBox($"{L("pl.pc")}: {L("pl.current")} = {EditorUserBuildSettings.activeBuildTarget}", MessageType.None);

                ps.useOverride = EditorGUILayout.ToggleLeft(L("pl.override"), ps.useOverride);
                if (ps.useOverride)
                {
                    DrawPreset(ps);
                    DrawQualityParams(ps, plat);
                }
                else EditorGUILayout.LabelField(L("pl.follow"), EditorStyles.miniLabel);
            }

            // validation feedback / 校验反馈
            EditorGUILayout.Space(6);
            var root = ((Component)target).transform.root;
            foreach (var e in AvatarTextureOptimizer.ValidatePlacement(root))
                EditorGUILayout.HelpBox(e, MessageType.Warning);

            if (GUI.changed) EditorUtility.SetDirty(C);
            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------
        private GUIContent GetContent(string labelKey, string tipKey = null) =>
            tipKey == null ? new GUIContent(L(labelKey))
                : new GUIContent(L(labelKey), L(tipKey));

        private void DrawLanguage()
        {
            var langs = ATOL10n.Languages.ToList();
            var options = new string[langs.Count + 1];
            options[0] = L("ui.language.auto");
            for (int i = 0; i < langs.Count; i++) options[i + 1] = langs[i];
            int cur = string.IsNullOrEmpty(C.languageOverride) ? 0 : Mathf.Max(0, langs.IndexOf(C.languageOverride) + 1);
            int next = EditorGUILayout.Popup(L("ui.language"), cur, options);
            if (next != cur)
            {
                C.languageOverride = next == 0 ? "" : langs[next - 1];
                GUI.FocusControl(null); // repaint with new language / 以新语言重绘
            }
        }

        private void DrawPreset(AtoPlatformSettings ps)
        {
            // switching preset refreshes parameter values (spec) / 切挡位即刷新参数值
            var names = Enum.GetValues(typeof(AtoQualityPreset)).Cast<AtoQualityPreset>().ToArray();
            int idx = Array.IndexOf(names, ps.preset);
            int next = EditorGUILayout.Popup(L("ui.preset"), idx, names.Select(PresetLabel).ToArray());
            if (next != idx) ps.preset = names[Mathf.Clamp(next, 0, names.Length - 1)];
        }

        private string PresetLabel(AtoQualityPreset p)
        {
            string key = "preset." + p;
            var s = ATOL10n.Get(key, Lang);
            return s == key ? p.ToString() : s;
        }

        private void DrawQualityParams(AtoPlatformSettings ps, AtoPlatform plat)
        {
            EditorGUILayout.HelpBox(L("ui.preset.tip"), MessageType.None);
            var shown = ps.preset == AtoQualityPreset.Custom ? ps.custom : QualityPresets.For(ps.preset);

            EditorGUI.BeginChangeCheck();
            shown.msssimMin = EditorGUILayout.Slider(L("q.msssimMin"), shown.msssimMin, 0.90f, 1f);
            shown.deltaEMeanMax = EditorGUILayout.Slider(L("q.deltaEMeanMax"), shown.deltaEMeanMax, 0f, 5f);
            shown.deltaEP95Max = EditorGUILayout.Slider(L("q.deltaEP95Max"), shown.deltaEP95Max, 0f, 8f);
            shown.normalAngleMeanMax = EditorGUILayout.Slider(L("q.normalAngleMeanMax"), shown.normalAngleMeanMax, 0f, 8f);
            shown.normalAngleP95Max = EditorGUILayout.Slider(L("q.normalAngleP95Max"), shown.normalAngleP95Max, 0f, 16f);
            shown.alphaCutoutIoUMin = EditorGUILayout.Slider(L("q.alphaCutoutIoUMin"), shown.alphaCutoutIoUMin, 0.90f, 1f);
            shown.alphaBlendRmseMax = EditorGUILayout.Slider(L("q.alphaBlendRmseMax"), shown.alphaBlendRmseMax, 0f, 0.05f);
            shown.grayRmseMax = EditorGUILayout.Slider(L("q.grayRmseMax"), shown.grayRmseMax, 0f, 0.05f);
            if (EditorGUI.EndChangeCheck() && ps.preset != AtoQualityPreset.Custom)
            {
                // editing switches to Custom; edited values preserved (spec)
                // 编辑即切自定义挡位，值保留（需求书）
                ps.custom = shown;
                ps.preset = AtoQualityPreset.Custom;
            }
            else if (ps.preset == AtoQualityPreset.Custom) ps.custom = shown;

            if (ps.preset == AtoQualityPreset.Custom)
                EditorGUILayout.HelpBox(L("q.nearLosslessNote"), MessageType.None);

            EditorGUILayout.Space(4);
            ps.minDensity = PopupInt(ps.minDensity, "ui.minDensity", DensitySteps);
            ps.maxDensity = PopupInt(ps.maxDensity, "ui.maxDensity", DensitySteps);
            ps.minPadding = PopupInt(ps.minPadding, "ui.minPadding", PaddingSteps);
            ps.experimentalNpot = EditorGUILayout.Toggle(GetContent("ui.npot", "ui.npot.tip"), ps.experimentalNpot);

            EditorGUILayout.Space(4);
            DrawCategory(ps, AtoTexCategory.Opaque, "cat.opaque", plat);
            DrawCategory(ps, AtoTexCategory.Alpha, "cat.alpha", plat);
            DrawCategory(ps, AtoTexCategory.Normal, "cat.normal", plat);
            DrawCategory(ps, AtoTexCategory.Gray, "cat.gray", plat);
        }

        private int PopupInt(int value, string key, int[] steps)
        {
            int idx = Array.IndexOf(steps, value);
            if (idx < 0) idx = Mathf.Clamp(ClosestIdx(value, steps), 0, steps.Length - 1);
            int next = EditorGUILayout.Popup(L(key), idx, steps.Select(v => v.ToString()).ToArray());
            return steps[Mathf.Clamp(next, 0, steps.Length - 1)];
        }

        private static int ClosestIdx(int v, int[] steps)
        {
            int best = 0, dist = int.MaxValue;
            for (int i = 0; i < steps.Length; i++)
                if (Math.Abs(steps[i] - v) < dist) { dist = Math.Abs(steps[i] - v); best = i; }
            return best;
        }

        // ------------------------------------------------------------------
        private void DrawCategory(AtoPlatformSettings ps, AtoTexCategory cat, string labelKey,
            AtoPlatform plat)
        {
            var p = ps.GetCategory(cat);
            EditorGUILayout.LabelField(L(labelKey), EditorStyles.miniBoldLabel);
            p.mipsAndStreaming = EditorGUILayout.Toggle(GetContent("cat.mips", "cat.mips.tip"), p.mipsAndStreaming);

            var options = FormatOptions(cat, plat);
            int idx = Array.IndexOf(options, p.format);
            if (idx < 0) { p.format = AtoTexFormat.Auto; idx = 0; }
            int next = EditorGUILayout.Popup(L("cat.format"), idx, options.Select(f => f.ToString()).ToArray());
            p.format = options[Mathf.Clamp(next, 0, options.Length - 1)];
        }

        /// <summary>Safe format list per platform/category (alpha never offered without an
        /// alpha channel; mobile offers ASTC only - no PVRTC, NPOT safe per spec).
        /// 按（平台,类别）安全格式列表（含alpha项不提供无alpha通道格式；移动端仅ASTC，
        /// 无PVRTC，NPOT 安全）。</summary>
        internal static AtoTexFormat[] FormatOptions(AtoTexCategory cat, AtoPlatform plat)
        {
            if (plat == AtoPlatform.PC)
                return cat == AtoTexCategory.Alpha
                    ? new[] { AtoTexFormat.Auto, AtoTexFormat.DXT5, AtoTexFormat.BC7, AtoTexFormat.DXT5Crunched }
                    : new[] { AtoTexFormat.Auto, AtoTexFormat.DXT1, AtoTexFormat.DXT5, AtoTexFormat.BC7,
                        AtoTexFormat.DXT1Crunched, AtoTexFormat.DXT5Crunched };
            return new[] { AtoTexFormat.Auto, AtoTexFormat.ASTC_4x4, AtoTexFormat.ASTC_5x5,
                AtoTexFormat.ASTC_6x6, AtoTexFormat.ASTC_8x8 };
        }
    }
}

// ============================================================================
// ATO - component inspector
// ATO - 组件检查器
//
// i18n-aware UI with collapsible sections. Platform override sections are
// shown only when the user enables overrides; density is a fixed-choice
// dropdown (512/1024/2048/4096/8192 px/m); the i18n language selector lists
// every loaded i18n/*.json file.
// 支持 i18n 的分节折叠 UI。平台 override 节在用户启用后显示；密度为固定选项
// 下拉（512/1024/2048/4096/8192 px/m）；i18n 语言选择列出全部已加载
// i18n/*.json 语言。
// ============================================================================

#region

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.AvatarTextureOptimizer.Editor.I18n;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.UI
{
    [CustomEditor(typeof(ATOComponent))]
    public class ATOComponentEditor : Editor
    {
        private bool _advQuality;
        private bool _advImport;
        private bool _advPlatform;
        private bool _advLog;

        private static readonly int[] Densities = { 512, 1024, 2048, 4096, 8192 };
        private static readonly int[] Paddings = { 4, 8, 16, 32, 64 };

        private ATOComponent C => (ATOComponent) target;

        public override void OnInspectorGUI()
        {
            var so = serializedObject;
            so.UpdateIfRequiredOrDirty();

            Prop(so, "active");

            // ---- quality  质量 ----
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.S("ato.section.quality"), EditorStyles.boldLabel);
            Prop(so, "qualityTier");
            if ((ATOQualityTier) C.QualityTier == ATOQualityTier.Custom)
            {
                Prop(so, "customQuality");
            }
            _advQuality = EditorGUILayout.Foldout(_advQuality, ATOI18n.S("ato.section.advancedQuality"), true);
            if (_advQuality)
            {
                var p = C.CustomParams;
                EditorGUILayout.HelpBox(ATOI18n.S("ato.advancedQuality.help"), MessageType.Info);
                p.ssim = EditorGUILayout.Slider("SSIM / MS-SSIM", p.ssim, 0f, 1f);
                p.deltaE2000 = EditorGUILayout.Slider("\u0394E2000 max", p.deltaE2000, 0f, 20f);
                p.alphaRMSE = EditorGUILayout.Slider("alpha RMSE (linear)", p.alphaRMSE, 0f, 0.2f);
                p.cutoutIoU = EditorGUILayout.Slider("cutout IoU min", p.cutoutIoU, 0f, 1f);
                p.normalAngleP95 = EditorGUILayout.Slider("normal p95 angle (deg)", p.normalAngleP95, 0f, 30f);
                p.grayRMSE = EditorGUILayout.Slider("gray RMSE (linear)", p.grayRMSE, 0f, 0.2f);
                EditorGUILayout.HelpBox(ATOI18n.S("ato.advancedQuality.customNote"), MessageType.None);
            }

            // ---- density  密度 ----
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.S("ato.section.density"), EditorStyles.boldLabel);
            var minSo = so.FindProperty("minDensity");
            var maxSo = so.FindProperty("maxDensity");
            if (minSo != null && maxSo != null)
            {
                minSo.intValue = Densities[DensityOf(minSo.intValue)];
                maxSo.intValue = Densities[DensityOf(maxSo.intValue)];
                EditorGUILayout.IntPopup("min px/m 最小密度", minSo.intValue, Densities,
                    System.Array.ConvertAll(Densities, d => d.ToString()));
                EditorGUILayout.IntPopup("max px/m 最大密度", maxSo.intValue, Densities,
                    System.Array.ConvertAll(Densities, d => d.ToString()));
            }

            // ---- atlas  图集 ----
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.S("ato.section.atlas"), EditorStyles.boldLabel);
            Prop(so, "generateAtlas");
            if (C.GenerateAtlas)
            {
                var padSo = so.FindProperty("minPadding");
                if (padSo != null)
                {
                    padSo.intValue = Paddings[PaddingOf(padSo.intValue)];
                    EditorGUILayout.IntPopup(ATOI18n.S("ato.atlas.minPadding"), padSo.intValue, Paddings,
                        System.Array.ConvertAll(Paddings, d => d.ToString()));
                }
                Prop(so, "useNPOT");
            }

            // ---- mips / formats  mips / 格式 ----
            _advImport = EditorGUILayout.Foldout(_advImport, ATOI18n.S("ato.section.import"), true);
            if (_advImport)
            {
                Prop(so, "mipsOpaque");
                Prop(so, "mipsTransparent");
                Prop(so, "mipsNormal");
                Prop(so, "mipsGray");
                FormatPopup(so, "formatOpaque", ATOTextureCategory.Opaque, false);
                FormatPopup(so, "formatTransparent", ATOTextureCategory.Transparent, true);
                FormatPopup(so, "formatNormal", ATOTextureCategory.Normal, false);
                FormatPopup(so, "formatGray", ATOTextureCategory.Gray, false);
            }

            // ---- platform override  平台 override ----
            _advPlatform = EditorGUILayout.Foldout(_advPlatform, ATOI18n.S("ato.section.platform"), true);
            if (_advPlatform)
            {
                Prop(so, "platformOverride");
                if (C.PlatformOverride)
                {
                    PlatformSection(so, "pc", "PC (Windows)");
                    PlatformSection(so, "android", "Android");
                    PlatformSection(so, "ios", "iOS");
                }
                else
                {
                    EditorGUILayout.HelpBox(ATOI18n.S("ato.platform.hint"), MessageType.Info);
                }
            }

            // ---- dedup / whitelist  去重 / 白名单 ----
            EditorGUILayout.Space(6);
            Prop(so, "dedupMaterials");
            Prop(so, "dedupTextures");
            Prop(so, "whitelist");

            // ---- i18n  国际化 ----
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(ATOI18n.S("ato.section.i18n"), EditorStyles.boldLabel);
            var langs = new List<string> { "auto" };
            foreach (var l in ATOI18n.LoadedLanguages) langs.Add(l);
            var names = System.Array.ConvertAll(langs.ToArray(),
                l => l == "auto" ? "Auto (NDMF 语言)" : l);
            int li = Mathf.Max(0, langs.IndexOf(C.LanguageOverride));
            li = EditorGUILayout.Popup("Language 语言", li, names);
            C.LanguageOverride = langs[Mathf.Clamp(li, 0, langs.Count - 1)];

            // ---- logging  日志 ----
            _advLog = EditorGUILayout.Foldout(_advLog, ATOI18n.S("ato.section.log"), true);
            if (_advLog)
            {
                Prop(so, "verboseLogging");
                Prop(so, "logMask");
            }

            so.ApplyModifiedProperties();
        }

        private void PlatformSection(SerializedObject so, string field, string label)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            Indent();
            FormatPopup(so, field + ".formatOpaque", ATOTextureCategory.Opaque, false);
            FormatPopup(so, field + ".formatTransparent", ATOTextureCategory.Transparent, true);
            FormatPopup(so, field + ".formatNormal", ATOTextureCategory.Normal, false);
            FormatPopup(so, field + ".formatGray", ATOTextureCategory.Gray, false);
            Prop(so, field + ".mipsOpaque");
            Prop(so, field + ".mipsTransparent");
            Prop(so, field + ".mipsNormal");
            Prop(so, field + ".mipsGray");
            Prop(so, field + ".useNPOT");
            Unindent();
        }

        /// <summary>Format dropdown restricted to platform-safe options
        /// (Auto + the safe enumeration for the current build platform).
        /// 格式下拉：仅显示当前平台安全枚举（Auto + 安全格式）。</summary>
        private static void FormatPopup(SerializedObject so, string prop,
            ATOTextureCategory cat, bool hasAlpha)
        {
            var p = so.FindProperty(prop);
            if (p == null) return;
            var platform = net.fosa.AvatarTextureOptimizer.Editor.Import.ImportStageImpl.CurrentPlatform();
            var safe = net.fosa.AvatarTextureOptimizer.Editor.Import.ImportStageImpl.SafeFormats(
                platform, false, cat, hasAlpha);
            var values = new System.Collections.Generic.List<int> { (int) ATOFormatChoice.Auto };
            var labels = new System.Collections.Generic.List<string> { "Auto" };
            foreach (var f in safe)
            {
                values.Add((int) f);
                labels.Add(f.ToString());
            }
            int idx = values.IndexOf(p.intValue);
            if (idx < 0) idx = 0;
            idx = EditorGUILayout.Popup(cat + " format 格式", idx, labels.ToArray());
            p.intValue = values[idx];
        }

        /// <summary>Finds a serialized property and draws it (no-op when
        /// missing). 查找并绘制序列化属性（缺失时跳过）。</summary>
        private static void Prop(SerializedObject so, string name)
        {
            var p = so.FindProperty(name);
            if (p != null) EditorGUILayout.PropertyField(p);
        }

        private static int DensityOf(int v)
        {
            for (int i = 0; i < Densities.Length; i++)
            {
                if (System.Math.Abs(Densities[i] - v) <= 1) return i;
            }
            return 2; // default 2048  默认 2048
        }

        private static int PaddingOf(int v)
        {
            for (int i = 0; i < Paddings.Length; i++)
            {
                if (System.Math.Abs(Paddings[i] - v) <= 1) return i;
            }
            return 0;
        }
    }
}

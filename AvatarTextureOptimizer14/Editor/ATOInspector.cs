// ATOInspector — component editor UI (novice-first, advanced folded) / 组件检视面板（小白优先，高级折叠）
// All text via i18n; advanced quality parameters are folded; platform overrides are hidden until the
// per-platform checkbox is enabled (Unity platform-override style).<br>
// 全部文案走 i18n；质量高级参数折叠；平台 Override 需勾选对应平台才显示（参考 Unity 风格）。
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Fosa.ATO.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    internal sealed class ATOInspector : UnityEditor.Editor
    {
        private static bool _qualityFold, _advFold, _platformFold;

        private SerializedProperty _generateAtlas, _qualityPreset, _customThresholds, _minDensity, _maxDensity,
            _minPadding, _allowNPOT, _whitelist, _mipSettings, _dedupTextures, _dedupMaterials,
            _pc, _android, _ios, _verbose, _language;

        private void OnEnable()
        {
            _generateAtlas = serializedObject.FindProperty("generateAtlas");
            _qualityPreset = serializedObject.FindProperty("qualityPreset");
            _customThresholds = serializedObject.FindProperty("customThresholds");
            _minDensity = serializedObject.FindProperty("minPixelDensity");
            _maxDensity = serializedObject.FindProperty("maxPixelDensity");
            _minPadding = serializedObject.FindProperty("minPadding");
            _allowNPOT = serializedObject.FindProperty("allowNPOT");
            _whitelist = serializedObject.FindProperty("whitelist");
            _mipSettings = serializedObject.FindProperty("mipSettings");
            _dedupTextures = serializedObject.FindProperty("dedupTextures");
            _dedupMaterials = serializedObject.FindProperty("dedupMaterials");
            _pc = serializedObject.FindProperty("pcOverride");
            _android = serializedObject.FindProperty("androidOverride");
            _ios = serializedObject.FindProperty("iosOverride");
            _verbose = serializedObject.FindProperty("verboseLogging");
            _language = serializedObject.FindProperty("languageOverride");
        }

        public override void OnInspectorGUI()
        {
            // language override must apply immediately in the inspector / 语言覆盖立即生效
            var comp = (AvatarTextureOptimizer)target;
            ATOL10n.OverrideLanguage = comp.languageOverride;
            serializedObject.Update();

            DrawValidation(comp);

            EditorGUILayout.PropertyField(_generateAtlas, new GUIContent(L("ato.ui.atlas", "Generate Atlas / 生成图集")));
            DrawQuality();
            DrawDensity(_minDensity, "ato.ui.density_min", "Min Pixel Density / 最小像素密度");
            DrawDensity(_maxDensity, "ato.ui.density_max", "Max Pixel Density / 最大像素密度");
            DrawIntPopup(_minPadding, AvatarTextureOptimizer.PaddingOptions, "ato.ui.padding", "Min Padding / 最小Padding");
            EditorGUILayout.PropertyField(_allowNPOT, new GUIContent(L("ato.ui.npot", "Experimental NPOT / 实验性NPOT")));

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(L("ato.ui.mips", "Mips & Streaming / Mipmap与流送"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_mipSettings.FindPropertyRelative("albedo"), new GUIContent(L("ato.ui.mips.albedo", "Albedo / 主色")));
            EditorGUILayout.PropertyField(_mipSettings.FindPropertyRelative("normal"), new GUIContent(L("ato.ui.mips.normal", "Normal / 法线")));
            EditorGUILayout.PropertyField(_mipSettings.FindPropertyRelative("mask"), new GUIContent(L("ato.ui.mips.mask", "Mask / 蒙版")));

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(_dedupTextures, new GUIContent(L("ato.ui.dedup_tex", "Dedup Textures/Atlases / 贴图与图集去重")));
            EditorGUILayout.PropertyField(_dedupMaterials, new GUIContent(L("ato.ui.dedup_mat", "Dedup Materials / 材质去重")));

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(_whitelist, new GUIContent(L("ato.ui.whitelist", "Whitelist / 白名单")), true);

            DrawPlatformOverrides();

            EditorGUILayout.Space(6);
            _advFold = EditorGUILayout.Foldout(_advFold, L("ato.ui.advanced", "Advanced / 高级"), true);
            if (_advFold)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_verbose, new GUIContent(L("ato.ui.verbose", "Verbose [ATO] Logging / 详细日志")));
                DrawLanguage(comp);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static string L(string key, string fallback) { var s = ATOL10n.T(key); return s.StartsWith("<") ? fallback : s; }

        private void DrawValidation(AvatarTextureOptimizer comp)
        {
            if (comp.GetComponent<VRCAvatarDescriptor>() == null)
                EditorGUILayout.HelpBox(L("ato.ui.err_no_descriptor", "Must be attached to the object owning VRCAvatarDescriptor / 必须挂在带 VRCAvatarDescriptor 的对象上"), MessageType.Error);
            var root = comp.transform.root;
            if (root.GetComponentsInChildren<AvatarTextureOptimizer>(true).Length > 1)
                EditorGUILayout.HelpBox(L("ato.ui.err_multiple", "Only ONE AvatarTextureOptimizer per avatar / 每个 Avatar 只允许一个组件"), MessageType.Error);
        }

        private void DrawQuality()
        {
            var names = Enum.GetNames(typeof(ATOQualityPreset))
                .Select(n => L("ato.ui.quality." + n.ToLowerInvariant(), n)).ToArray();
            var rect = EditorGUILayout.GetControlRect();
            _qualityPreset.enumValueIndex = EditorGUI.Popup(rect, L("ato.ui.quality", "Quality / 质量"), _qualityPreset.enumValueIndex, names);

            _qualityFold = EditorGUILayout.Foldout(_qualityFold, L("ato.ui.quality_adv", "Quality Thresholds (Advanced) / 质量阈值（高级）"), true);
            if (!_qualityFold) return;
            EditorGUI.indentLevel++;
            if ((ATOQualityPreset)_qualityPreset.enumValueIndex == ATOQualityPreset.Custom)
            {
                // user-editable; never overwritten by other presets / 用户可编辑，不被其他挡位覆盖
                EditorGUILayout.HelpBox(L("ato.ui.custom_hint", "Custom tier: edit freely, defaults are near-lossless / 自定义挡位：自由编辑，默认近无损"), MessageType.None);
                foreach (var f in new[] { "msSsimMin", "deltaEMaxP95", "alphaRmseMax", "cutoutIouMin", "normalAngleMeanDeg", "normalAngleP95Deg", "maskRmseMax" })
                    EditorGUILayout.PropertyField(_customThresholds.FindPropertyRelative(f), new GUIContent(L("ato.ui.th." + f, f)));
            }
            else
            {
                var p = (ATOQualityPreset)_qualityPreset.enumValueIndex;
                if (p == ATOQualityPreset.Lossless)
                    EditorGUILayout.HelpBox(L("ato.ui.lossless_hint", "Lossless: islands are copied as-is (no rescaling) / 近无损：原样拷贝，不缩放"), MessageType.Info);
                var th = ATOQualityThresholds.ForPreset(p);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.FloatField("MS-SSIM ≥", th.msSsimMin);
                EditorGUILayout.FloatField("ΔE2000 P95 ≤", th.deltaEMaxP95);
                EditorGUILayout.FloatField(L("ato.ui.th.alphaRmseMax", "Alpha RMSE ≤"), th.alphaRmseMax);
                EditorGUILayout.FloatField(L("ato.ui.th.cutoutIouMin", "Cutout IoU ≥"), th.cutoutIouMin);
                EditorGUILayout.FloatField(L("ato.ui.th.normalAngleMeanDeg", "Normal ∠mean ≤"), th.normalAngleMeanDeg);
                EditorGUILayout.FloatField(L("ato.ui.th.normalAngleP95Deg", "Normal ∠P95 ≤"), th.normalAngleP95Deg);
                EditorGUILayout.FloatField(L("ato.ui.th.maskRmseMax", "Mask RMSE ≤"), th.maskRmseMax);
                EditorGUI.EndDisabledGroup();
            }
            EditorGUI.indentLevel--;
        }

        private void DrawDensity(SerializedProperty prop, string key, string fallback)
        {
            var tiers = AvatarTextureOptimizer.DensityTiers;
            var labels = tiers.Select(t => t + " px/m").ToArray();
            int cur = prop.intValue;
            int idx = Array.IndexOf(tiers, cur);
            if (idx < 0)
            {
                labels = labels.Append(cur + " px/m").ToArray();
                idx = labels.Length - 1;
            }
            var rect = EditorGUILayout.GetControlRect();
            int ni = EditorGUI.Popup(rect, L(key, fallback), idx, labels);
            prop.intValue = ni < tiers.Length ? tiers[ni] : cur;
        }

        private void DrawIntPopup(SerializedProperty prop, int[] options, string key, string fallback)
        {
            var labels = options.Select(o => o + " px").ToArray();
            int idx = Array.IndexOf(options, prop.intValue);
            if (idx < 0) idx = 0;
            var rect = EditorGUILayout.GetControlRect();
            int ni = EditorGUI.Popup(rect, L(key, fallback), idx, labels);
            prop.intValue = options[ni];
        }

        private void DrawPlatformOverrides()
        {
            EditorGUILayout.Space(6);
            _platformFold = EditorGUILayout.Foldout(_platformFold, L("ato.ui.platforms", "Platform Overrides (Advanced) / 平台 Override（高级）"), true);
            if (!_platformFold) return;
            EditorGUI.indentLevel++;
            DrawOnePlatform(_pc, "PC", Stage4_Packing.CurrentPlatform() == ATOPlatform.PC);
            DrawOnePlatform(_android, "Android", Stage4_Packing.CurrentPlatform() == ATOPlatform.Android);
            DrawOnePlatform(_ios, "iOS", Stage4_Packing.CurrentPlatform() == ATOPlatform.IOS);
            EditorGUI.indentLevel--;
        }

        private void DrawOnePlatform(SerializedProperty p, string name, bool current)
        {
            var enabled = p.FindPropertyRelative("enabled");
            string label = name + (current ? L("ato.ui.platform_current", " (current / 当前)") : "");
            enabled.boolValue = EditorGUILayout.ToggleLeft(label, enabled.boolValue, EditorStyles.boldLabel);
            if (!enabled.boolValue) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(p.FindPropertyRelative("maxAtlasSize"), new GUIContent(L("ato.ui.max_atlas", "Max Atlas Size / 图集最大边长")));
            foreach (var f in new[]
                     {
                         ("albedoAlpha", "ato.ui.fmt.albedoAlpha", "Albedo (alpha) / 主色（透明）"),
                         ("albedoOpaque", "ato.ui.fmt.albedoOpaque", "Albedo (opaque) / 主色（不透明）"),
                         ("normal", "ato.ui.fmt.normal", "Normal / 法线"),
                         ("mask", "ato.ui.fmt.mask", "Mask / 蒙版"),
                     })
                EditorGUILayout.PropertyField(p.FindPropertyRelative(f.Item1), new GUIContent(L(f.Item2, f.Item3)));
            EditorGUI.indentLevel--;
        }

        private void DrawLanguage(AvatarTextureOptimizer comp)
        {
            var langs = ATOL10n.Languages ?? Array.Empty<string>();
            var options = new[] { "Auto" }.Concat(langs).ToArray();
            int idx = Array.IndexOf(options, comp.languageOverride);
            if (idx < 0) idx = 0;
            var rect = EditorGUILayout.GetControlRect();
            int ni = EditorGUI.Popup(rect, L("ato.ui.language", "Language / 语言"), idx, options);
            _language.stringValue = options[ni];
        }
    }
}

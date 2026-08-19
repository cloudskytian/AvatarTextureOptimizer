using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.UI
{
    // ATOAvatar 组件 Inspector：分区折叠显示全部优化设置（基础/质量/贴图格式/高级/平台覆盖/语言）。
    // ATOAvatar inspector: all optimization settings in collapsible sections (basic/quality/format/advanced/platform/language).
    [CustomEditor(typeof(ATOAvatar))]
    public sealed class ATOAvatarEditor : UnityEditor.Editor
    {
        private SerializedProperty _settings;
        private bool _foldAdvanced = true;
        private bool _foldPlatform = false;
        private bool _foldQuality = true;
        private bool _foldFormat = true;

        private void OnEnable()
        {
            _settings = serializedObject.FindProperty("settings");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var comp = (ATOAvatar)target;
            var s = comp.settings;
            if (s == null) s = comp.settings = new ATOSettings();
            if (s.customMetrics == null) s.customMetrics = ATOMetricThresholds.Lossless();
            Undo.RecordObject(comp, "ATO Settings");

            EditorGUILayout.LabelField(ATOLocalization.Tr("ato.name") + " v" + ATOConstants.Version, EditorStyles.boldLabel);

            // ---- 基础 ----
            EditorGUILayout.LabelField(ATOLocalization.Tr("section.basic"), EditorStyles.boldLabel);
            s.generateAtlas = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.generateAtlas"), ATOLocalization.Tr("setting.generateAtlas.tip")), s.generateAtlas);
            s.qualityPreset = (ATOQualityPreset)EditorGUILayout.EnumPopup(new GUIContent(ATOLocalization.Tr("setting.qualityPreset"), ATOLocalization.Tr("setting.qualityPreset.tip")), s.qualityPreset);
            // 密度挡位：512/1024/2048/4096/8192（默认 2048/4096）。Density tiers.
            int minIdx = NearestDensityIndex(s.minDensityPxPerMeter);
            minIdx = EditorGUILayout.Popup(new GUIContent(ATOLocalization.Tr("setting.minDensity"), ATOLocalization.Tr("setting.minDensity.tip")), minIdx, DensityLabels());
            s.minDensityPxPerMeter = ATOConstants.DensityOptions[Mathf.Clamp(minIdx, 0, ATOConstants.DensityOptions.Length - 1)];
            int maxIdx = NearestDensityIndex(s.maxDensityPxPerMeter);
            maxIdx = EditorGUILayout.Popup(new GUIContent(ATOLocalization.Tr("setting.maxDensity"), ATOLocalization.Tr("setting.maxDensity.tip")), maxIdx, DensityLabels());
            s.maxDensityPxPerMeter = ATOConstants.DensityOptions[Mathf.Clamp(maxIdx, 0, ATOConstants.DensityOptions.Length - 1)];
            if (s.minDensityPxPerMeter > s.maxDensityPxPerMeter) s.maxDensityPxPerMeter = s.minDensityPxPerMeter;

            // ---- 质量（自定义挡位阈值折叠在高级选项里）。Quality (custom thresholds folded in advanced).
            if (s.qualityPreset == ATOQualityPreset.Custom)
            {
                _foldQuality = EditorGUILayout.Foldout(_foldQuality, ATOLocalization.Tr("setting.metrics"));
                if (_foldQuality)
                {
                    EditorGUI.indentLevel++;
                    DrawMetrics(s.customMetrics);
                    EditorGUI.indentLevel--;
                }
            }

            // ---- 贴图格式 ----
            _foldFormat = EditorGUILayout.Foldout(_foldFormat, ATOLocalization.Tr("section.format"));
            if (_foldFormat)
            {
                EditorGUI.indentLevel++;
                DrawCategory(s.formats.opaqueColor, ATOLocalization.Tr("category.opaqueColor"), ATOTextureCategory.OpaqueColor);
                DrawCategory(s.formats.alphaColor, ATOLocalization.Tr("category.alphaColor"), ATOTextureCategory.AlphaColor);
                DrawCategory(s.formats.normalMap, ATOLocalization.Tr("category.normalMap"), ATOTextureCategory.NormalMap);
                DrawCategory(s.formats.grayscale, ATOLocalization.Tr("category.grayscale"), ATOTextureCategory.Grayscale);
                EditorGUI.indentLevel--;
            }

            // ---- 高级 ----
            _foldAdvanced = EditorGUILayout.Foldout(_foldAdvanced, ATOLocalization.Tr("section.advanced"));
            if (_foldAdvanced)
            {
                EditorGUI.indentLevel++;
                s.atlasPaddingPx = EditorGUILayout.IntPopup(new GUIContent(ATOLocalization.Tr("setting.atlasPadding")),
                    s.atlasPaddingPx, PaddingLabels(), ATOConstants.PaddingOptions);
                s.npotAtlases = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.npot")), s.npotAtlases);
                s.maxAtlasSize = EditorGUILayout.IntField(new GUIContent(ATOLocalization.Tr("setting.maxAtlasSize")), s.maxAtlasSize);
                s.deduplicateTextures = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.dedupTex")), s.deduplicateTextures);
                s.deduplicateMaterials = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.dedupMat")), s.deduplicateMaterials);
                s.mergeMaterialSlots = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.mergeSlots")), s.mergeMaterialSlots);
                s.verboseLog = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.verboseLog")), s.verboseLog);
                EditorGUI.indentLevel--;
            }

            // ---- 平台覆盖（勾选对应平台才显示全部参数）。Platform overrides (shown only when enabled).
            _foldPlatform = EditorGUILayout.Foldout(_foldPlatform, ATOLocalization.Tr("section.platform"));
            if (_foldPlatform)
            {
                EditorGUI.indentLevel++;
                DrawPlatformOverride(s, ATOPlatform.PC);
                DrawPlatformOverride(s, ATOPlatform.Android);
                DrawPlatformOverride(s, ATOPlatform.iOS);
                EditorGUI.indentLevel--;
            }

            // ---- 语言 ----
            EditorGUILayout.LabelField(ATOLocalization.Tr("section.i18n"), EditorStyles.boldLabel);
            var langs = new System.Collections.Generic.List<string> { "Auto" };
            langs.AddRange(ATOLocalization.AvailableLanguages);
            int cur = langs.IndexOf(s.language == "Auto" ? "Auto" : s.language);
            if (cur < 0) cur = 0;
            int sel = EditorGUILayout.Popup(ATOLocalization.Tr("setting.language"), cur, langs.ToArray());
            s.language = langs[Mathf.Clamp(sel, 0, langs.Count - 1)];

            if (GUILayout.Button(ATOLocalization.Tr("button.normalize")))
            {
                s.Normalize();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawPlatformOverride(ATOSettings s, ATOPlatform platform)
        {
            var ov = s.FindOverride(platform);
            if (ov == null)
            {
                ov = new ATOPlatformOverride { platform = platform };
                s.platformOverrides.Add(ov);
            }
            string platformKey = "platform." + platform.ToString().ToLowerInvariant();
            ov.enabled = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.platformOverride.enable") + " - " + ATOLocalization.Tr(platformKey)), ov.enabled);
            if (!ov.enabled) return;
            EditorGUI.indentLevel++;
            ov.qualityPreset = (ATOQualityPreset)EditorGUILayout.EnumPopup(ATOLocalization.Tr("setting.qualityPreset"), ov.qualityPreset);
            if (ov.qualityPreset == ATOQualityPreset.Custom)
            {
                if (ov.customMetrics == null) ov.customMetrics = ATOMetricThresholds.Lossless();
                DrawMetrics(ov.customMetrics);
            }
            DrawCategory(ov.formats.opaqueColor, ATOLocalization.Tr("category.opaqueColor"), ATOTextureCategory.OpaqueColor);
            DrawCategory(ov.formats.alphaColor, ATOLocalization.Tr("category.alphaColor"), ATOTextureCategory.AlphaColor);
            DrawCategory(ov.formats.normalMap, ATOLocalization.Tr("category.normalMap"), ATOTextureCategory.NormalMap);
            DrawCategory(ov.formats.grayscale, ATOLocalization.Tr("category.grayscale"), ATOTextureCategory.Grayscale);
            ov.npotAtlases = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.npot")), ov.npotAtlases);
            ov.maxAtlasSize = EditorGUILayout.IntField(new GUIContent(ATOLocalization.Tr("setting.maxAtlasSize")), ov.maxAtlasSize);
            EditorGUI.indentLevel--;
        }

        private void DrawCategory(ATOCategorySettings cat, string label, ATOTextureCategory category)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            var options = AllowedFormats(category);
            int cur = System.Array.IndexOf(options, cat.format);
            if (cur < 0) cur = 0;
            int sel = EditorGUILayout.Popup(ATOLocalization.Tr("setting.format"), cur, FormatLabels(options));
            cat.format = options[Mathf.Clamp(sel, 0, options.Length - 1)];
            cat.mipmaps = EditorGUILayout.Toggle(new GUIContent(ATOLocalization.Tr("setting.mipmaps"), ATOLocalization.Tr("setting.mipmaps.tip")), cat.mipmaps);
            EditorGUI.indentLevel--;
        }

        private void DrawMetrics(ATOMetricThresholds m)
        {
            m.msSsim = EditorGUILayout.Slider(ATOLocalization.Tr("setting.msSsim"), m.msSsim, 0f, 1f);
            m.deltaE2000 = EditorGUILayout.FloatField(ATOLocalization.Tr("setting.deltaE"), m.deltaE2000);
            m.alphaIoU = EditorGUILayout.Slider(ATOLocalization.Tr("setting.alphaIoU"), m.alphaIoU, 0f, 1f);
            m.alphaRMSE = EditorGUILayout.FloatField(ATOLocalization.Tr("setting.alphaRMSE"), m.alphaRMSE);
            m.normalAngleDegP95 = EditorGUILayout.FloatField(ATOLocalization.Tr("setting.normalAngle"), m.normalAngleDegP95);
            m.grayRMSE = EditorGUILayout.FloatField(ATOLocalization.Tr("setting.grayRMSE"), m.grayRMSE);
        }

        // 各类别允许的格式枚举（剔除不安全的选项；构建期仍做兜底校验）。
        // Allowed formats per category (unsafe options removed; build-time validation remains as a safety net).
        private static ATOCompressionFormat[] AllowedFormats(ATOTextureCategory category)
        {
            var all = (ATOCompressionFormat[])System.Enum.GetValues(typeof(ATOCompressionFormat));
            var list = new System.Collections.Generic.List<ATOCompressionFormat>();
            foreach (var f in all)
            {
                switch (category)
                {
                    case ATOTextureCategory.AlphaColor:
                        // 含透明贴图不提供不带 alpha 的选项。Transparent textures never offer alpha-less options.
                        if (f == ATOCompressionFormat.RGB24 || f == ATOCompressionFormat.BC5
                            || f == ATOCompressionFormat.R8 || f == ATOCompressionFormat.R16
                            || f == ATOCompressionFormat.RG16 || f == ATOCompressionFormat.RHalf
                            || f == ATOCompressionFormat.RGHalf) continue;
                        break;
                    case ATOTextureCategory.NormalMap:
                        // 法线需要至少双通道。Normals need at least two channels.
                        if (f == ATOCompressionFormat.RGB24 || f == ATOCompressionFormat.R8
                            || f == ATOCompressionFormat.R16 || f == ATOCompressionFormat.RHalf) continue;
                        break;
                    case ATOTextureCategory.OpaqueColor:
                        if (f == ATOCompressionFormat.R8 || f == ATOCompressionFormat.R16
                            || f == ATOCompressionFormat.RHalf || f == ATOCompressionFormat.BC5) continue;
                        break;
                }
                list.Add(f);
            }
            return list.ToArray();
        }

        private static string[] FormatLabels(ATOCompressionFormat[] options)
        {
            var labels = new string[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                string formatKey = "format." + options[i];
                labels[i] = ATOLocalization.Tr(formatKey);
            }
            return labels;
        }

        private static string[] PaddingLabels()
        {
            var labels = new string[ATOConstants.PaddingOptions.Length];
            for (int i = 0; i < ATOConstants.PaddingOptions.Length; i++)
            {
                labels[i] = ATOConstants.PaddingOptions[i] + " px";
            }
            return labels;
        }

        private static int NearestDensityIndex(float value)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < ATOConstants.DensityOptions.Length; i++)
            {
                float d = Mathf.Abs(ATOConstants.DensityOptions[i] - value);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        private static string[] DensityLabels()
        {
            var labels = new string[ATOConstants.DensityOptions.Length];
            for (int i = 0; i < ATOConstants.DensityOptions.Length; i++)
            {
                labels[i] = ATOConstants.DensityOptions[i] + " px/m";
            }
            return labels;
        }
    }
}

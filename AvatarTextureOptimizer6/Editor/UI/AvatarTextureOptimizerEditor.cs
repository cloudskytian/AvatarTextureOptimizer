using System;
using System.Linq;
using NetFosa.AvatarTextureOptimizer.Editor.i18n;
using UnityEditor;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.UI
{
    /// <summary>
    /// AvatarTextureOptimizer 检查器。
    /// 质量挡位参数折叠在"高级"里；平台覆盖折叠、勾选对应平台才显示；语言选项随可用翻译文件变化。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private SerializedProperty _generateAtlases;
        private SerializedProperty _qualityPreset;
        private SerializedProperty _customQuality;
        private SerializedProperty _minPpm;
        private SerializedProperty _maxPpm;
        private SerializedProperty _npot;
        private SerializedProperty _minPadding;
        private SerializedProperty _compression;
        private SerializedProperty _mipmaps;
        private SerializedProperty _platformOverrides;
        private SerializedProperty _whitelist;
        private SerializedProperty _dedupTex;
        private SerializedProperty _dedupMat;
        private SerializedProperty _mergeSlots;
        private SerializedProperty _useGpu;
        private SerializedProperty _useBurst;
        private SerializedProperty _verbose;
        private SerializedProperty _language;
        private SerializedProperty _showAdvanced;

        private void OnEnable()
        {
            _generateAtlases = serializedObject.FindProperty("generateAtlases");
            _qualityPreset = serializedObject.FindProperty("qualityPreset");
            _customQuality = serializedObject.FindProperty("customQuality");
            _minPpm = serializedObject.FindProperty("minPixelsPerMeter");
            _maxPpm = serializedObject.FindProperty("maxPixelsPerMeter");
            _npot = serializedObject.FindProperty("npotEnabled");
            _minPadding = serializedObject.FindProperty("minPadding");
            _compression = serializedObject.FindProperty("compression");
            _mipmaps = serializedObject.FindProperty("mipmaps");
            _platformOverrides = serializedObject.FindProperty("platformOverrides");
            _whitelist = serializedObject.FindProperty("whitelist");
            _dedupTex = serializedObject.FindProperty("deduplicateTextures");
            _dedupMat = serializedObject.FindProperty("deduplicateMaterials");
            _mergeSlots = serializedObject.FindProperty("mergeIdenticalMaterialSlots");
            _useGpu = serializedObject.FindProperty("useGPUAcceleration");
            _useBurst = serializedObject.FindProperty("useBurstJobs");
            _verbose = serializedObject.FindProperty("verboseLogging");
            _language = serializedObject.FindProperty("language");
            _showAdvanced = serializedObject.FindProperty("showAdvancedOptions");
        }

        public override void OnInspectorGUI()
        {
            Localization.EnsureLoaded();
            // 跟随组件语言设置
            var comp = (AvatarTextureOptimizer)target;
            Localization.SetLanguage(comp.language);

            serializedObject.Update();

            EditorGUILayout.LabelField(Localization.L("ato.componentName"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Localization.L("ato.description"), MessageType.Info);

            // 校验提示
            var descriptor = comp.GetComponent("VRC.SDKBase.VRC_AvatarDescriptor");
            if (descriptor == null)
                EditorGUILayout.HelpBox(Localization.L("ato.missingDescriptor"), MessageType.Error);

            EditorGUILayout.Space();

            // ---------- 基础 ----------
            EditorGUILayout.PropertyField(_generateAtlases, new GUIContent(Localization.L("ato.generateAtlases"), Localization.L("ato.generateAtlasesTip")));

            EditorGUILayout.PropertyField(_qualityPreset, new GUIContent(Localization.L("ato.qualityPreset")));

            // 质量阈值（高级折叠）
            _showAdvanced.boolValue = EditorGUILayout.Foldout(_showAdvanced.boolValue, Localization.L("ato.advanced"), true);
            if (_showAdvanced.boolValue)
            {
                EditorGUI.indentLevel++;
                var preset = (ATOQualityPreset)_qualityPreset.enumValueIndex;
                EditorGUILayout.HelpBox(Localization.L("ato.qualityThresholdsTip"), MessageType.None);
                DrawQualityThresholds(_customQuality, preset);
                EditorGUILayout.Space();
                DrawDensity();
                EditorGUILayout.Space();
                DrawAtlasOptions();
                EditorGUILayout.Space();
                DrawCompression();
                EditorGUILayout.Space();
                DrawMipmaps();
                EditorGUILayout.Space();
                DrawPerformance();
                EditorGUI.indentLevel--;
            }
            else
            {
                // 密度简版
                DrawDensity();
            }

            EditorGUILayout.Space();
            DrawPlatformOverrides();
            EditorGUILayout.Space();
            DrawWhitelist();
            EditorGUILayout.Space();
            DrawDedup();
            EditorGUILayout.Space();
            DrawLanguage();

            EditorGUILayout.Space();
            if (GUILayout.Button(Localization.L("ato.bakeNow")))
            {
                BakeNow(comp);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawQualityThresholds(SerializedProperty custom, ATOQualityPreset preset)
        {
            bool showCustom = preset == ATOQualityPreset.Custom;
            if (!showCustom)
            {
                var t = QualityThresholds.ForPreset(preset);
                EditorGUILayout.LabelField(Localization.L("ato.threshold.quality") + ": " + t.quality.ToString("F2"));
                EditorGUILayout.LabelField("MS-SSIM: " + t.msSsim.ToString("F3"));
                EditorGUILayout.LabelField("SSIM: " + t.ssim.ToString("F3"));
                EditorGUILayout.LabelField("ΔE2000: " + t.deltaE2000.ToString("F2"));
                EditorGUILayout.LabelField("Alpha IoU: " + t.alphaCutoutIoU.ToString("F3"));
                EditorGUILayout.LabelField("Alpha RMSE: " + t.alphaBlendRmse.ToString("F4"));
                EditorGUILayout.LabelField("Normal p95°: " + t.normalAngleP95.ToString("F2"));
                EditorGUILayout.LabelField("Gray RMSE: " + t.grayRmse.ToString("F4"));
                return;
            }

            EditorGUILayout.PropertyField(custom.FindPropertyRelative("quality"), new GUIContent(Localization.L("ato.threshold.quality")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("msSsim"), new GUIContent(Localization.L("ato.threshold.msSsim")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("ssim"), new GUIContent(Localization.L("ato.threshold.ssim")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("deltaE2000"), new GUIContent(Localization.L("ato.threshold.deltaE2000")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("alphaCutoutIoU"), new GUIContent(Localization.L("ato.threshold.alphaIoU")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("alphaBlendRmse"), new GUIContent(Localization.L("ato.threshold.alphaRmse")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("normalAngleP95"), new GUIContent(Localization.L("ato.threshold.normalAngle")));
            EditorGUILayout.PropertyField(custom.FindPropertyRelative("grayRmse"), new GUIContent(Localization.L("ato.threshold.grayRmse")));
        }

        private static readonly int[] DensityOptions = { 512, 1024, 2048, 4096, 8192 };

        private void DrawDensity()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(Localization.L("ato.minPixelsPerMeter")));
            _minPpm.intValue = DensityOptions[EditorGUILayout.Popup(Array.IndexOf(DensityOptions, _minPpm.intValue), DensityOptions.Select(d => d.ToString()).ToArray())];
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(Localization.L("ato.maxPixelsPerMeter")));
            _maxPpm.intValue = DensityOptions[EditorGUILayout.Popup(Array.IndexOf(DensityOptions, _maxPpm.intValue), DensityOptions.Select(d => d.ToString()).ToArray())];
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(Localization.L("ato.pixelsPerMeterTip"), MessageType.None);
        }

        private void DrawAtlasOptions()
        {
            EditorGUILayout.LabelField(Localization.L("ato.componentName"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_npot, new GUIContent(Localization.L("ato.npotEnabled"), Localization.L("ato.npotTip")));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(Localization.L("ato.minPadding")));
            _minPadding.intValue = MinPaddingOptions[EditorGUILayout.Popup(Array.IndexOf(MinPaddingOptions, _minPadding.intValue), MinPaddingOptions.Select(d => d.ToString()).ToArray())];
            EditorGUILayout.EndHorizontal();
        }

        private static readonly int[] MinPaddingOptions = { 4, 8, 16, 32, 64 };

        private void DrawCompression()
        {
            EditorGUILayout.LabelField(Localization.L("ato.compression"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Localization.L("ato.compressionTip"), MessageType.None);
            DrawFormatField(_compression.FindPropertyRelative("mainOpaque"), "ato.compression.mainOpaque");
            DrawFormatField(_compression.FindPropertyRelative("mainTransparent"), "ato.compression.mainTransparent");
            DrawFormatField(_compression.FindPropertyRelative("normal"), "ato.compression.normal");
            DrawFormatField(_compression.FindPropertyRelative("grayMask"), "ato.compression.grayMask");
            DrawFormatField(_compression.FindPropertyRelative("other"), "ato.compression.other");
        }

        private void DrawFormatField(SerializedProperty prop, string labelKey)
        {
            var names = Enum.GetNames(typeof(ATOCompressionFormat));
            int idx = Array.IndexOf(names, prop.enumNames[prop.enumValueIndex]);
            if (idx < 0) idx = 0;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(Localization.L(labelKey)));
            int newIdx = EditorGUILayout.Popup(idx, names);
            if (newIdx != idx) prop.enumValueIndex = newIdx;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawMipmaps()
        {
            EditorGUILayout.LabelField(Localization.L("ato.mipmaps"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Localization.L("ato.mipmapsTip"), MessageType.None);
            EditorGUILayout.PropertyField(_mipmaps.FindPropertyRelative("main"), new GUIContent(Localization.L("ato.mipmaps.main")));
            EditorGUILayout.PropertyField(_mipmaps.FindPropertyRelative("normal"), new GUIContent(Localization.L("ato.mipmaps.normal")));
            EditorGUILayout.PropertyField(_mipmaps.FindPropertyRelative("grayMask"), new GUIContent(Localization.L("ato.mipmaps.grayMask")));
            EditorGUILayout.PropertyField(_mipmaps.FindPropertyRelative("other"), new GUIContent(Localization.L("ato.mipmaps.other")));
        }

        private void DrawPerformance()
        {
            EditorGUILayout.PropertyField(_useGpu, new GUIContent(Localization.L("ato.useGPU")));
            EditorGUILayout.PropertyField(_useBurst, new GUIContent(Localization.L("ato.useBurst")));
            EditorGUILayout.PropertyField(_verbose, new GUIContent(Localization.L("ato.verboseLogging")));
        }

        private void DrawPlatformOverrides()
        {
            EditorGUILayout.LabelField(Localization.L("ato.platformOverrides"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Localization.L("ato.platformOverridesTip"), MessageType.None);
            DrawPlatformOverride(_platformOverrides.FindPropertyRelative("pc"), ATOPlatform.PC);
            DrawPlatformOverride(_platformOverrides.FindPropertyRelative("android"), ATOPlatform.Android);
            DrawPlatformOverride(_platformOverrides.FindPropertyRelative("ios"), ATOPlatform.iOS);
        }

        private void DrawPlatformOverride(SerializedProperty ov, ATOPlatform platform)
        {
            var enabled = ov.FindPropertyRelative("enabled");
            bool show = enabled.boolValue;
            show = EditorGUILayout.Foldout(show, new GUIContent(Localization.L("ato.platform." + platform)), true);
            enabled.boolValue = show;
            if (!show) return;

            EditorGUI.indentLevel++;
            var oq = ov.FindPropertyRelative("overrideQuality");
            EditorGUILayout.PropertyField(oq, new GUIContent(Localization.L("ato.override.quality")));
            if (oq.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(ov.FindPropertyRelative("qualityPreset"), new GUIContent(Localization.L("ato.qualityPreset")));
                var pq = (ATOQualityPreset)ov.FindPropertyRelative("qualityPreset").enumValueIndex;
                if (pq == ATOQualityPreset.Custom)
                {
                    var cq = ov.FindPropertyRelative("customQuality");
                    if (cq != null)
                    {
                        EditorGUILayout.PropertyField(cq.FindPropertyRelative("quality"), new GUIContent(Localization.L("ato.threshold.quality")));
                    }
                }
                EditorGUI.indentLevel--;
            }

            var od = ov.FindPropertyRelative("overrideDensity");
            EditorGUILayout.PropertyField(od, new GUIContent(Localization.L("ato.override.density")));
            if (od.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(ov.FindPropertyRelative("minPixelsPerMeter"));
                EditorGUILayout.PropertyField(ov.FindPropertyRelative("maxPixelsPerMeter"));
                EditorGUI.indentLevel--;
            }

            var oc = ov.FindPropertyRelative("overrideCompression");
            EditorGUILayout.PropertyField(oc, new GUIContent(Localization.L("ato.override.compression")));
            if (oc.boolValue)
            {
                EditorGUI.indentLevel++;
                var c = ov.FindPropertyRelative("compression");
                DrawFormatField(c.FindPropertyRelative("mainOpaque"), "ato.compression.mainOpaque");
                DrawFormatField(c.FindPropertyRelative("mainTransparent"), "ato.compression.mainTransparent");
                DrawFormatField(c.FindPropertyRelative("normal"), "ato.compression.normal");
                DrawFormatField(c.FindPropertyRelative("grayMask"), "ato.compression.grayMask");
                DrawFormatField(c.FindPropertyRelative("other"), "ato.compression.other");
                EditorGUI.indentLevel--;
            }

            var om = ov.FindPropertyRelative("overrideMipmaps");
            EditorGUILayout.PropertyField(om, new GUIContent(Localization.L("ato.override.mipmaps")));
            if (om.boolValue)
            {
                EditorGUI.indentLevel++;
                var m = ov.FindPropertyRelative("mipmaps");
                EditorGUILayout.PropertyField(m.FindPropertyRelative("main"), new GUIContent(Localization.L("ato.mipmaps.main")));
                EditorGUILayout.PropertyField(m.FindPropertyRelative("normal"), new GUIContent(Localization.L("ato.mipmaps.normal")));
                EditorGUILayout.PropertyField(m.FindPropertyRelative("grayMask"), new GUIContent(Localization.L("ato.mipmaps.grayMask")));
                EditorGUILayout.PropertyField(m.FindPropertyRelative("other"), new GUIContent(Localization.L("ato.mipmaps.other")));
                EditorGUI.indentLevel--;
            }

            var oa = ov.FindPropertyRelative("overrideAtlas");
            EditorGUILayout.PropertyField(oa, new GUIContent(Localization.L("ato.override.atlas")));
            if (oa.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(ov.FindPropertyRelative("npotEnabled"), new GUIContent(Localization.L("ato.npotEnabled")));
                EditorGUILayout.PropertyField(ov.FindPropertyRelative("minPadding"), new GUIContent(Localization.L("ato.minPadding")));
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;
        }

        private void DrawWhitelist()
        {
            EditorGUILayout.LabelField(Localization.L("ato.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Localization.L("ato.whitelistTip"), MessageType.None);
            EditorGUILayout.PropertyField(_whitelist, true);
        }

        private void DrawDedup()
        {
            EditorGUILayout.PropertyField(_dedupTex, new GUIContent(Localization.L("ato.deduplicateTextures")));
            EditorGUILayout.PropertyField(_dedupMat, new GUIContent(Localization.L("ato.deduplicateMaterials")));
            EditorGUILayout.PropertyField(_mergeSlots, new GUIContent(Localization.L("ato.mergeSlots"), Localization.L("ato.mergeSlotsTip")));
        }

        private void DrawLanguage()
        {
            Localization.EnsureLoaded();
            var langs = Localization.AvailableLanguages;
            var codes = new System.Collections.Generic.List<string> { "" };
            var labels = new System.Collections.Generic.List<string> { Localization.L("ato.language.Auto") };
            foreach (var l in langs)
            {
                codes.Add(l.code);
                labels.Add(l.displayName);
            }

            var comp = (AvatarTextureOptimizer)target;
            int idx = codes.IndexOf(comp.language);
            if (idx < 0) { idx = 0; }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(Localization.L("ato.language")));
            int newIdx = EditorGUILayout.Popup(idx, labels.ToArray());
            if (newIdx != idx)
            {
                _language.stringValue = codes[newIdx];
                Localization.SetLanguage(codes[newIdx]);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void BakeNow(AvatarTextureOptimizer comp)
        {
            var go = comp.gameObject;
            try
            {
                nadena.dev.ndmf.AvatarProcessor.ManualProcessAvatar(go);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ATO] Bake failed: {e}");
            }
        }
    }
}

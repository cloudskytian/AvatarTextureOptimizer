// Avatar Texture Optimizer / 头像贴图优化器
// Component inspector (IMGUI for maximum 2022.3 / VRChat-SDK compatibility).
// All editing goes through SerializedProperty, so Undo, multi-selection and
// prefab overrides behave correctly. All text flows through ATOLoc.
// 组件检视面板（IMGUI，最大化 2022.3 / VRChat SDK 兼容性）。
// 全部编辑走 SerializedProperty（撤销、多选、Prefab 覆盖语义正确），文案走 ATOLoc。
//
// Layout per spec:
//   quality preset (threshold table read-only unless Custom), pixel density
//   step buttons, atlas toggles, padding, NPOT, dedup toggles, platform
//   overrides (fully folded when disabled), whitelist, language, verbose.
// 布局按需求：质量挡位（非 Custom 阈值只读展示）、像素密度步进按钮、图集开关、
// padding、NPOT、去重开关、平台覆盖（未勾选全折叠）、白名单、语言、调试日志。

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Inspector for the ATO component. / ATO 组件的检视面板。</summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class ATOComponentEditor : UnityEditor.Editor
    {
        private static readonly ATOQualityPreset[] PresetValues =
        {
            ATOQualityPreset.Performance,
            ATOQualityPreset.Low,
            ATOQualityPreset.Balanced,
            ATOQualityPreset.High,
            ATOQualityPreset.Maximum,
            ATOQualityPreset.Custom,
        };

        private static readonly string[] PresetKeys =
        {
            "ato:ui.preset.performance",
            "ato:ui.preset.low",
            "ato:ui.preset.balanced",
            "ato:ui.preset.high",
            "ato:ui.preset.maximum",
            "ato:ui.preset.custom",
        };

        // Foldout state (per editor session, not serialized). / 折叠状态（会话级）
        private bool _advFoldout;
        private readonly bool[,] _platformFoldouts = new bool[3, 4];

        // Serialized property handles cached in OnEnable. / OnEnable 缓存的序列化属性。
        private SerializedProperty _pPreset, _pCustomQuality;
        private SerializedProperty _pMinDensity, _pMaxDensity;
        private SerializedProperty _pGenerateAtlas, _pPadding, _pNpot;
        private SerializedProperty _pDedupTex, _pDedupMat;
        private SerializedProperty _pPc, _pAndroid, _pIos;
        private SerializedProperty _pWhitelist;
        private SerializedProperty _pLangMode, _pLangManual;
        private SerializedProperty _pVerbose;

        private void OnEnable()
        {
            _pPreset = serializedObject.FindProperty("qualityPreset");
            _pCustomQuality = serializedObject.FindProperty("customQuality");
            _pMinDensity = serializedObject.FindProperty("minPixelDensity");
            _pMaxDensity = serializedObject.FindProperty("maxPixelDensity");
            _pGenerateAtlas = serializedObject.FindProperty("generateAtlas");
            _pPadding = serializedObject.FindProperty("minAtlasPadding");
            _pNpot = serializedObject.FindProperty("experimentalNPOT");
            _pDedupTex = serializedObject.FindProperty("deduplicateTextures");
            _pDedupMat = serializedObject.FindProperty("deduplicateMaterials");
            _pPc = serializedObject.FindProperty("pcOverride");
            _pAndroid = serializedObject.FindProperty("androidOverride");
            _pIos = serializedObject.FindProperty("iosOverride");
            _pWhitelist = serializedObject.FindProperty("whitelist");
            _pLangMode = serializedObject.FindProperty("languageMode");
            _pLangManual = serializedObject.FindProperty("manualLanguage");
            _pVerbose = serializedObject.FindProperty("verboseLogging");
        }

        public override void OnInspectorGUI()
        {
            var comp = (AvatarTextureOptimizer)target;

            // Resolve language from the component every repaint (cheap when loaded).
            // 每次重绘按组件解析语言（已加载时开销极小）。
            ATOLoc.Configure(comp.languageMode, comp.manualLanguage);

            serializedObject.Update();

            EditorGUILayout.HelpBox(ATOLoc.T("ato:ui.info"), MessageType.Info);

            DrawQuality();
            EditorGUILayout.Space(6);
            DrawAtlas();
            EditorGUILayout.Space(6);
            DrawDedup();
            EditorGUILayout.Space(6);
            DrawPlatformOverrides();
            EditorGUILayout.Space(6);
            DrawWhitelist();
            EditorGUILayout.Space(6);
            DrawLanguage(comp);
            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(_pVerbose,
                new GUIContent(ATOLoc.T("ato:ui.verbose"), ATOLoc.T("ato:ui.verbose.tip")));

            serializedObject.ApplyModifiedProperties();
        }

        // ---------------------------------------------------------- quality

        private void DrawQuality()
        {
            EditorGUILayout.LabelField(ATOLoc.T("ato:ui.section.quality"), EditorStyles.boldLabel);

            // Preset popup / 挡位下拉
            var labels = new string[PresetKeys.Length];
            for (int i = 0; i < PresetKeys.Length; i++) labels[i] = ATOLoc.T(PresetKeys[i]);
            int curIdx = 0;
            for (int i = 0; i < PresetValues.Length; i++)
                if ((int)PresetValues[i] == _pPreset.intValue) { curIdx = i; break; }
            EditorGUI.BeginChangeCheck();
            int nextIdx = EditorGUILayout.Popup(new GUIContent(ATOLoc.T("ato:ui.preset")), curIdx, labels);
            if (EditorGUI.EndChangeCheck()) _pPreset.intValue = (int)PresetValues[nextIdx];

            bool custom = (ATOQualityPreset)_pPreset.intValue == ATOQualityPreset.Custom;

            // Threshold table: editable for Custom (serialized -> Undo works),
            // read-only display of the preset values otherwise.
            // 阈值表：Custom 可编辑（序列化，撤销可用），其余挡位只读展示。
            _advFoldout = EditorGUILayout.Foldout(_advFoldout, ATOLoc.T("ato:ui.advanced"), true);
            if (_advFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    if (custom)
                    {
                        DrawQualityProps(_pCustomQuality);
                    }
                    else
                    {
                        var q = ATOQualityPresets.For((ATOQualityPreset)_pPreset.intValue);
                        using (new EditorGUI.DisabledGroupScope(true))
                        {
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.target"), q.targetQuality, 0.1f, 1f);
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.msssim"), q.msSsimMin, 0.5f, 1f);
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.deltae"), q.deltaEMax, 0.1f, 12f);
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.normalmean"), q.normalMeanDegMax, 0.1f, 15f);
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.normalp95"), q.normalP95DegMax, 0.1f, 30f);
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.alpha"), q.alphaRmseMax, 0.001f, 0.3f);
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.cutoutiou"), q.cutoutIouMin, 0.5f, 1f);
                            EditorGUILayout.Slider(ATOLoc.T("ato:ui.q.gray"), q.grayRmseMax, 0.001f, 0.3f);
                        }
                        EditorGUILayout.HelpBox(ATOLoc.T("ato:ui.advanced.readonly"), MessageType.None);
                    }
                }
            }

            // Pixel density with step buttons / 像素密度（步进按钮）
            DrawDensityRow("ato:ui.density.min", _pMinDensity);
            DrawDensityRow("ato:ui.density.max", _pMaxDensity);
            if (_pMinDensity.intValue > _pMaxDensity.intValue)
            {
                // Keep the interval sane: lift max up to min.
                // 保持区间合法：将上限抬到下限。
                _pMaxDensity.intValue = _pMinDensity.intValue;
            }
        }

        private static void DrawQualityProps(SerializedProperty root)
        {
            Slider(root, "targetQuality", "ato:ui.q.target", 0.1f, 1f);
            Slider(root, "msSsimMin", "ato:ui.q.msssim", 0.5f, 1f);
            Slider(root, "deltaEMax", "ato:ui.q.deltae", 0.1f, 12f);
            Slider(root, "normalMeanDegMax", "ato:ui.q.normalmean", 0.1f, 15f);
            Slider(root, "normalP95DegMax", "ato:ui.q.normalp95", 0.1f, 30f);
            Slider(root, "alphaRmseMax", "ato:ui.q.alpha", 0.001f, 0.3f);
            Slider(root, "cutoutIouMin", "ato:ui.q.cutoutiou", 0.5f, 1f);
            Slider(root, "grayRmseMax", "ato:ui.q.gray", 0.001f, 0.3f);
        }

        private static void Slider(SerializedProperty root, string field, string labelKey, float min, float max)
        {
            var p = root.FindPropertyRelative(field);
            if (p == null) return;
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.Slider(ATOLoc.T(labelKey), p.floatValue, min, max);
            if (EditorGUI.EndChangeCheck()) p.floatValue = v;
        }

        private void DrawDensityRow(string labelKey, SerializedProperty prop)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(ATOLoc.T(labelKey));
            int v = prop.intValue;
            EditorGUI.BeginChangeCheck();
            v = EditorGUILayout.IntField(v, GUILayout.Width(60));
            foreach (var step in ATOQualityPresets.PixelDensitySteps)
            {
                bool isCur = step == v;
                if (GUILayout.Toggle(isCur, step.ToString(),
                        isCur ? EditorStyles.miniButtonMid : EditorStyles.miniButton,
                        GUILayout.Width(46)) && !isCur)
                {
                    v = step;
                }
            }
            GUILayout.FlexibleSpace();
            if (EditorGUI.EndChangeCheck()) prop.intValue = Mathf.Clamp(v, 128, 16384);
            EditorGUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------ atlas

        private void DrawAtlas()
        {
            EditorGUILayout.LabelField(ATOLoc.T("ato:ui.section.atlas"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_pGenerateAtlas,
                new GUIContent(ATOLoc.T("ato:ui.atlas.generate"), ATOLoc.T("ato:ui.atlas.generate.tip")));
            using (new EditorGUI.DisabledGroupScope(!_pGenerateAtlas.boolValue))
            {
                EditorGUILayout.PropertyField(_pPadding,
                    new GUIContent(ATOLoc.T("ato:ui.atlas.padding"), ATOLoc.T("ato:ui.atlas.padding.tip")));
                EditorGUILayout.PropertyField(_pNpot,
                    new GUIContent(ATOLoc.T("ato:ui.atlas.npot"), ATOLoc.T("ato:ui.atlas.npot.tip")));
            }
        }

        // ------------------------------------------------------------ dedup

        private void DrawDedup()
        {
            EditorGUILayout.LabelField(ATOLoc.T("ato:ui.section.dedup"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_pDedupTex,
                new GUIContent(ATOLoc.T("ato:ui.dedup.textures"), ATOLoc.T("ato:ui.dedup.textures.tip")));
            EditorGUILayout.PropertyField(_pDedupMat,
                new GUIContent(ATOLoc.T("ato:ui.dedup.materials"), ATOLoc.T("ato:ui.dedup.materials.tip")));
        }

        // ------------------------------------------------- platform overrides

        private void DrawPlatformOverrides()
        {
            EditorGUILayout.LabelField(ATOLoc.T("ato:ui.section.platform"), EditorStyles.boldLabel);
            DrawOneOverride(_pPc, "ato:ui.platform.pc", 0);
            DrawOneOverride(_pAndroid, "ato:ui.platform.android", 1);
            DrawOneOverride(_pIos, "ato:ui.platform.ios", 2);
        }

        private void DrawOneOverride(SerializedProperty root, string nameKey, int platformIndex)
        {
            var enabled = root.FindPropertyRelative("enabled");
            // When disabled everything stays folded (spec: 未勾选全折叠).
            // 未启用时全部折叠（需求要求）。
            EditorGUILayout.PropertyField(enabled,
                new GUIContent(ATOLoc.T(nameKey)), false);
            if (!enabled.boolValue) return;

            using (new EditorGUI.IndentLevelScope())
            {
                var pMax = root.FindPropertyRelative("maxAtlasSize");
                EditorGUI.BeginChangeCheck();
                int v = EditorGUILayout.IntField(
                    new GUIContent(ATOLoc.T("ato:ui.platform.maxatlas"), ATOLoc.T("ato:ui.platform.maxatlas.tip")),
                    pMax.intValue);
                if (EditorGUI.EndChangeCheck()) pMax.intValue = Mathf.Clamp(v, 0, 8192);

                var platform = (ATOPlatform)platformIndex;
                DrawRule(root, "transparent", ATOTextureCategory.Transparent, "ato:ui.cat.transparent", platformIndex, catIndex: 0, platform);
                DrawRule(root, "opaque", ATOTextureCategory.Opaque, "ato:ui.cat.opaque", platformIndex, catIndex: 1, platform);
                DrawRule(root, "normal", ATOTextureCategory.Normal, "ato:ui.cat.normal", platformIndex, catIndex: 2, platform);
                DrawRule(root, "grayscale", ATOTextureCategory.Grayscale, "ato:ui.cat.grayscale", platformIndex, catIndex: 3, platform);
            }
        }

        private void DrawRule(SerializedProperty root, string ruleField, ATOTextureCategory cat,
            string labelKey, int platformIndex, int catIndex, ATOPlatform platform)
        {
            bool open = _platformFoldouts[platformIndex, catIndex];
            open = EditorGUILayout.Foldout(open, ATOLoc.T(labelKey), true);
            _platformFoldouts[platformIndex, catIndex] = open;
            if (!open) return;

            var rule = root.FindPropertyRelative(ruleField);
            using (new EditorGUI.IndentLevelScope())
            {
                DrawFormatPopup(rule.FindPropertyRelative("format"), cat, platform);

                var crunch = rule.FindPropertyRelative("crunch");
                EditorGUILayout.PropertyField(crunch, new GUIContent(ATOLoc.T("ato:ui.rule.crunch")));

                var quality = rule.FindPropertyRelative("compressorQuality");
                using (new EditorGUI.DisabledGroupScope(!crunch.boolValue))
                {
                    EditorGUI.BeginChangeCheck();
                    int q = EditorGUILayout.IntSlider(ATOLoc.T("ato:ui.rule.quality"), quality.intValue, 0, 100);
                    if (EditorGUI.EndChangeCheck()) quality.intValue = q;
                }

                EditorGUILayout.PropertyField(rule.FindPropertyRelative("mipmapsAndStreaming"),
                    new GUIContent(ATOLoc.T("ato:ui.rule.mipstream"), ATOLoc.T("ato:ui.rule.mipstream.tip")));
            }
        }

        /// <summary>
        /// Format dropdown filtered per category + platform. Mirrors the hard filters
        /// of ATOFormatMapping.Sanitize; the writer still re-validates at build time.
        /// 按类别+平台过滤的格式下拉。与 ATOFormatMapping.Sanitize 的硬过滤一致；
        /// 写盘端构建时仍会二次校验。
        /// </summary>
        private static void DrawFormatPopup(SerializedProperty formatProp, ATOTextureCategory cat, ATOPlatform platform)
        {
            var options = FormatsFor(cat, platform);
            var labels = new string[options.Count];
            for (int i = 0; i < options.Count; i++)
                labels[i] = options[i] == ATOEncodingFormat.Auto
                    ? ATOLoc.T("ato:ui.fmt.auto")
                    : options[i].ToString();

            int cur = options.IndexOf((ATOEncodingFormat)formatProp.intValue);
            if (cur < 0) cur = 0;
            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.Popup(ATOLoc.T("ato:ui.rule.format"), cur, labels);
            if (EditorGUI.EndChangeCheck()) formatProp.intValue = (int)options[next];
        }

        private static List<ATOEncodingFormat> FormatsFor(ATOTextureCategory cat, ATOPlatform platform)
        {
            bool pc = platform == ATOPlatform.PC;
            bool ios = platform == ATOPlatform.iOS;
            var list = new List<ATOEncodingFormat> { ATOEncodingFormat.Auto };
            switch (cat)
            {
                case ATOTextureCategory.Transparent:
                    list.Add(ATOEncodingFormat.RGBA32); list.Add(ATOEncodingFormat.ARGB32);
                    if (pc) { list.Add(ATOEncodingFormat.DXT5); list.Add(ATOEncodingFormat.BC7); }
                    else
                    {
                        list.Add(ATOEncodingFormat.ASTC_4x4); list.Add(ATOEncodingFormat.ASTC_6x6);
                        list.Add(ATOEncodingFormat.ASTC_8x8); list.Add(ATOEncodingFormat.ETC2_RGBA8);
                    }
                    if (ios) list.Add(ATOEncodingFormat.PVRTC_RGBA4);
                    break;
                case ATOTextureCategory.Opaque:
                    list.Add(ATOEncodingFormat.RGBA32); list.Add(ATOEncodingFormat.ARGB32); list.Add(ATOEncodingFormat.RGB24);
                    if (pc) { list.Add(ATOEncodingFormat.DXT1); list.Add(ATOEncodingFormat.DXT5); list.Add(ATOEncodingFormat.BC7); }
                    else
                    {
                        list.Add(ATOEncodingFormat.ASTC_4x4); list.Add(ATOEncodingFormat.ASTC_6x6);
                        list.Add(ATOEncodingFormat.ASTC_8x8); list.Add(ATOEncodingFormat.ETC2_RGB4);
                        list.Add(ATOEncodingFormat.ETC2_RGBA8);
                    }
                    if (ios) { list.Add(ATOEncodingFormat.PVRTC_RGB4); list.Add(ATOEncodingFormat.PVRTC_RGBA4); }
                    break;
                case ATOTextureCategory.Normal:
                    list.Add(ATOEncodingFormat.RGBA32); list.Add(ATOEncodingFormat.ARGB32);
                    if (pc) { list.Add(ATOEncodingFormat.BC5); list.Add(ATOEncodingFormat.BC7); }
                    else list.Add(ATOEncodingFormat.ASTC_6x6);
                    break;
                default: // Grayscale / 灰度
                    list.Add(ATOEncodingFormat.R8); list.Add(ATOEncodingFormat.R16);
                    list.Add(ATOEncodingFormat.RGBA32); list.Add(ATOEncodingFormat.ARGB32);
                    if (pc) list.Add(ATOEncodingFormat.BC7);
                    else list.Add(ATOEncodingFormat.ASTC_6x6);
                    break;
            }
            return list;
        }

        // ---------------------------------------------------------- whitelist

        private void DrawWhitelist()
        {
            EditorGUILayout.LabelField(ATOLoc.T("ato:ui.section.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOLoc.T("ato:ui.whitelist.tip"), MessageType.None);
            EditorGUILayout.PropertyField(_pWhitelist,
                new GUIContent(ATOLoc.T("ato:ui.whitelist.entries")), true);
        }

        // ----------------------------------------------------------- language

        private void DrawLanguage(AvatarTextureOptimizer comp)
        {
            EditorGUILayout.LabelField(ATOLoc.T("ato:ui.section.language"), EditorStyles.boldLabel);
            // Localized Auto/Manual popup (enum PropertyField would show English).
            // 本地化的 自动/手动 下拉（enum 的 PropertyField 只会显示英文）。
            var modeLabels = new[] { ATOLoc.T("ato:ui.lang.auto"), ATOLoc.T("ato:ui.lang.manualopt") };
            int modeCur = _pLangMode.intValue == (int)ATOLanguageMode.Manual ? 1 : 0;
            EditorGUI.BeginChangeCheck();
            int modeNext = EditorGUILayout.Popup(new GUIContent(ATOLoc.T("ato:ui.lang.mode")), modeCur, modeLabels);
            if (EditorGUI.EndChangeCheck())
                _pLangMode.intValue = modeNext == 1 ? (int)ATOLanguageMode.Manual : (int)ATOLanguageMode.Auto;
            if ((ATOLanguageMode)_pLangMode.intValue == ATOLanguageMode.Manual)
            {
                var langs = ATOLoc.AvailableLanguages;
                int cur = -1;
                for (int i = 0; i < langs.Count; i++)
                    if (string.Equals(langs[i], _pLangManual.stringValue, StringComparison.OrdinalIgnoreCase))
                        cur = i;
                if (cur < 0)
                {
                    // Unknown manual language: keep it visible as a text field.
                    // 未知手动语言：以文本框展示保留。
                    EditorGUILayout.PropertyField(_pLangManual, new GUIContent(ATOLoc.T("ato:ui.lang.manual")));
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    int next = EditorGUILayout.Popup(ATOLoc.T("ato:ui.lang.manual"), cur, ToArray(langs));
                    if (EditorGUI.EndChangeCheck() && next >= 0 && next < langs.Count)
                        _pLangManual.stringValue = langs[next];
                }
            }
            else
            {
                EditorGUILayout.LabelField(ATOLoc.T("ato:ui.lang.current"), ATOLoc.ActiveLanguage);
            }
        }

        private static string[] ToArray(IReadOnlyList<string> list)
        {
            var arr = new string[list.Count];
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }
    }
}

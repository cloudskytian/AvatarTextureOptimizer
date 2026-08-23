using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Inspector for the ATO component. Beginner-friendly surface by default; every advanced
    /// block is folded; per-platform texture/atlas parameters only appear when that platform's
    /// override is enabled; UI language switchable (Auto follows NDMF).
    /// / ATO 组件 Inspector：默认面向小白；高级选项全部折叠；平台参数勾选覆盖后才显示；
    /// 界面语言可切换（Auto 跟随 NDMF）。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class ATOEditor : UnityEditor.Editor
    {
        private bool _foldQuality, _foldAtlas, _foldCompression, _foldPlatforms, _foldDebug;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var comp = (AvatarTextureOptimizer)target;

            DrawLanguageSelector(comp);
            EditorGUILayout.Space(4);

            // ---- basics / 基础 ----
            var genAtlas = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings))
                .FindPropertyRelative(nameof(AtoSettings.generateAtlas));
            EditorGUILayout.PropertyField(genAtlas, new GUIContent(L("ui.generateAtlas"), L("ui.generateAtlas.tip")));

            DrawPresetSelector();

            _foldQuality = EditorGUILayout.Foldout(_foldQuality, L("ui.quality"), true);
            if (_foldQuality) DrawQuality();

            _foldAtlas = EditorGUILayout.Foldout(_foldAtlas, L("ui.atlas"), true);
            if (_foldAtlas) DrawAtlas();

            _foldCompression = EditorGUILayout.Foldout(_foldCompression, L("ui.compression"), true);
            if (_foldCompression) DrawCompression();

            _foldPlatforms = EditorGUILayout.Foldout(_foldPlatforms, L("ui.platforms"), true);
            if (_foldPlatforms) DrawPlatforms();

            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.whitelist)),
                new GUIContent(L("ui.whitelist"), L("ui.whitelist.tip")), true);

            _foldDebug = EditorGUILayout.Foldout(_foldDebug, L("ui.debug"), true);
            if (_foldDebug)
            {
                var verbose = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings))
                    .FindPropertyRelative(nameof(AtoSettings.verboseLog));
                EditorGUILayout.PropertyField(verbose, new GUIContent(L("ui.verbose")));
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------ blocks
        private void DrawLanguageSelector(AvatarTextureOptimizer comp)
        {
            // UI language is a global editor preference (Auto follows NDMF's language).
            // / 界面语言是全局编辑器偏好（Auto 跟随 NDMF 语言）。
            var languages = ATOL10n.Languages.ToList();
            var current = ATOL10n.LanguageOverride;
            int idx = current == "auto" ? 0 : Mathf.Max(1, languages.IndexOf(current) + 1);
            var labels = new[] { L("ui.language.auto") }
                .Concat(languages.Select(ATOL10n.DisplayName)).ToArray();
            int next = EditorGUILayout.Popup(L("ui.language"), idx, labels);
            if (next != idx)
            {
                ATOL10n.LanguageOverride = next == 0 ? "auto" : languages[next - 1];
                comp.languageOverride = ATOL10n.LanguageOverride; // mirrored for portability / 同步到组件便于迁移
            }
        }

        private void DrawPresetSelector()
        {
            var settings = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings));
            var presetProp = settings.FindPropertyRelative(nameof(AtoSettings.preset));
            var preset = (QualityPreset)presetProp.intValue;

            var names = Enum.GetNames(typeof(QualityPreset));
            var next = (QualityPreset)EditorGUILayout.Popup(L("ui.preset"), (int)preset,
                names.Select(n => L("preset." + n)).ToArray());

            if (next != preset)
            {
                presetProp.intValue = (int)next;
                // changing a preset updates the parameter values / 挡位切换同步参数值
                var q = AtoPresets.For(next);
                var qp = settings.FindPropertyRelative(nameof(AtoSettings.quality));
                WriteQuality(qp, q);
                if (next != QualityPreset.Custom) EditorUtility.SetDirty(target);
            }
        }

        private void DrawQuality()
        {
            var settings = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings));
            var qp = settings.FindPropertyRelative(nameof(AtoSettings.quality));

            EditorGUI.indentLevel++;
            if (qp.isExpanded || true)
            {
                Field(qp, nameof(QualityParams.msSsim), "q.msssim");
                Field(qp, nameof(QualityParams.deltaE2000Mean), "q.deltaE");
                Field(qp, nameof(QualityParams.alphaCutoutIoU), "q.alphaIou");
                Field(qp, nameof(QualityParams.alphaBlendRmse), "q.alphaRmse");
                Field(qp, nameof(QualityParams.normalAngleMeanDeg), "q.normalMean");
                Field(qp, nameof(QualityParams.normalAngleP95Deg), "q.normalP95");
                Field(qp, nameof(QualityParams.grayRmse), "q.grayRmse");

                // editing a parameter switches to Custom / 手改参数自动切到 Custom
                if (GUI.changed && (QualityPreset)settings.FindPropertyRelative(nameof(AtoSettings.preset)).intValue != QualityPreset.Custom)
                {
                    settings.FindPropertyRelative(nameof(AtoSettings.preset)).intValue = (int)QualityPreset.Custom;
                }

                IntSlider(settings, nameof(AtoSettings.minPixelsPerMeter), 512, 8192, "q.densityMin");
                IntSlider(settings, nameof(AtoSettings.maxPixelsPerMeter), 512, 8192, "q.densityMax");
            }
            EditorGUI.indentLevel--;
        }

        private void DrawAtlas()
        {
            var settings = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings));
            EditorGUI.indentLevel++;
            Field(settings, nameof(AtoSettings.experimentalNpot), "a.npot");
            EditorGUILayout.HelpBox(L("a.npot.tip"), MessageType.Info, true);

            // padding tiers 4..64 / padding 挡位
            var padProp = settings.FindPropertyRelative(nameof(AtoSettings.minPadding));
            int[] pads = { 4, 8, 16, 32, 64 };
            int idx = Mathf.Max(0, Array.IndexOf(pads, padProp.intValue));
            int next = EditorGUILayout.Popup(L("a.padding"), idx, pads.Select(p => p + " px").ToArray());
            if (next != idx) padProp.intValue = pads[next];

            IntSlider(settings, nameof(AtoSettings.maxAtlasSize), 64, 8192, "a.maxSize");

            Field(settings, nameof(AtoSettings.dedupMaterials), "d.dedupMaterials");
            Field(settings, nameof(AtoSettings.dedupTextures), "d.dedupTextures");
            EditorGUI.indentLevel--;
        }

        private void DrawCompression()
        {
            var settings = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings));
            var platform = ATOProcessPass.DetectPlatform();
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(L("c.currentPlatform"), platform.ToString());

            CategoryFormat(settings, nameof(AtoSettings.opaqueFormat), nameof(AtoSettings.opaqueMip), "c.opaque", platform);
            CategoryFormat(settings, nameof(AtoSettings.transparentFormat), nameof(AtoSettings.transparentMip), "c.transparent", platform);
            CategoryFormat(settings, nameof(AtoSettings.normalFormat), nameof(AtoSettings.normalMip), "c.normal", platform);
            CategoryFormat(settings, nameof(AtoSettings.grayscaleFormat), nameof(AtoSettings.grayscaleMip), "c.grayscale", platform);

            EditorGUILayout.HelpBox(L("c.mip.tip"), MessageType.None, true);
            EditorGUI.indentLevel--;
        }

        private void CategoryFormat(SerializedProperty settings, string formatField, string mipField,
            string labelKey, AtoPlatform platform)
        {
            var fp = settings.FindPropertyRelative(formatField);
            var mp = settings.FindPropertyRelative(mipField);
            EditorGUILayout.BeginHorizontal();
            var values = FormatChoices(platform, (AtoFormat)fp.intValue, out var currentIndex);
            int next = EditorGUILayout.Popup(L(labelKey), currentIndex, values.Select(FormatLabel).ToArray());
            if (next != currentIndex && next < values.Count) fp.intValue = (int)values[next];
            var width = GUILayout.Width(150);
            var mipLabel = new GUIContent(L("c.mipAndStream"));
            mp.boolValue = EditorGUILayout.ToggleLeft(mipLabel, mp.boolValue, width);
            EditorGUILayout.EndHorizontal();
        }

        private System.Collections.Generic.List<AtoFormat> FormatChoices(AtoPlatform platform,
            AtoFormat current, out int index)
        {
            var list = new System.Collections.Generic.List<AtoFormat> { AtoFormat.Auto, AtoFormat.Uncompressed };
            if (platform == AtoPlatform.PC)
                list.AddRange(new[] { AtoFormat.BC7, AtoFormat.DXT1, AtoFormat.DXT5, AtoFormat.BC4, AtoFormat.CrunchDXT1, AtoFormat.CrunchDXT5 });
            else
                list.AddRange(new[] { AtoFormat.ASTC_4x4, AtoFormat.ASTC_5x5, AtoFormat.ASTC_6x6, AtoFormat.ASTC_8x8, AtoFormat.ASTC_10x10, AtoFormat.ASTC_12x12, AtoFormat.ETC2_RGBA8, AtoFormat.ETC2_RGB });

            if (!list.Contains(current)) list.Add(current); // keep invalid selections visible / 保留非法选择可见（回退在构建时）
            list.Sort((a, b) => a == AtoFormat.Auto ? -1 : b == AtoFormat.Auto ? 1 : a.CompareTo(b));
            index = list.IndexOf(current);
            return list;
        }

        private static string FormatLabel(AtoFormat f) => f == AtoFormat.Auto ? L("c.auto") : f.ToString();

        private void DrawPlatforms()
        {
            DrawOverride(nameof(AvatarTextureOptimizer.pcOverride), "PC", AtoPlatform.PC);
            DrawOverride(nameof(AvatarTextureOptimizer.androidOverride), "Android", AtoPlatform.Android);
            DrawOverride(nameof(AvatarTextureOptimizer.iosOverride), "iOS", AtoPlatform.iOS);
        }

        private void DrawOverride(string fieldName, string title, AtoPlatform platform)
        {
            var ov = serializedObject.FindProperty(fieldName);
            var enabled = ov.FindPropertyRelative(nameof(AtoPlatformOverride.enabled));
            EditorGUILayout.PropertyField(enabled, new GUIContent(string.Format(L("p.enable"), title)));
            if (!enabled.boolValue) return; // platform params visible only when enabled / 勾选后才显示

            EditorGUI.indentLevel++;
            var settings = ov.FindPropertyRelative(nameof(AtoPlatformOverride.settings));
            Field(settings, nameof(AtoSettings.generateAtlas), "ui.generateAtlas");
            IntSlider(settings, nameof(AtoSettings.maxAtlasSize), 64, platform == AtoPlatform.PC ? 8192 : 4096, "a.maxSize");
            var padProp = settings.FindPropertyRelative(nameof(AtoSettings.minPadding));
            int[] pads = { 4, 8, 16, 32, 64 };
            int idx = Mathf.Max(0, Array.IndexOf(pads, padProp.intValue));
            int next = EditorGUILayout.Popup(L("a.padding"), idx, pads.Select(p => p + " px").ToArray());
            if (next != idx) padProp.intValue = pads[next];

            CategoryFormat(settings, nameof(AtoSettings.opaqueFormat), nameof(AtoSettings.opaqueMip), "c.opaque", platform);
            CategoryFormat(settings, nameof(AtoSettings.transparentFormat), nameof(AtoSettings.transparentMip), "c.transparent", platform);
            CategoryFormat(settings, nameof(AtoSettings.normalFormat), nameof(AtoSettings.normalMip), "c.normal", platform);
            CategoryFormat(settings, nameof(AtoSettings.grayscaleFormat), nameof(AtoSettings.grayscaleMip), "c.grayscale", platform);

            var qp = settings.FindPropertyRelative(nameof(AtoSettings.quality));
            EditorGUILayout.LabelField(L("ui.quality"));
            Field(qp, nameof(QualityParams.msSsim), "q.msssim");
            Field(qp, nameof(QualityParams.deltaE2000Mean), "q.deltaE");
            IntSlider(settings, nameof(AtoSettings.minPixelsPerMeter), 512, 8192, "q.densityMin");
            IntSlider(settings, nameof(AtoSettings.maxPixelsPerMeter), 512, 8192, "q.densityMax");
            Field(settings, nameof(AtoSettings.experimentalNpot), "a.npot");
            EditorGUI.indentLevel--;
        }

        // ------------------------------------------------------------------ small helpers
        private static void Field(SerializedProperty parent, string field, string key)
        {
            EditorGUILayout.PropertyField(parent.FindPropertyRelative(field), new GUIContent(L(key)));
        }

        private static void IntSlider(SerializedProperty parent, string field, int min, int max, string key)
        {
            var p = parent.FindPropertyRelative(field);
            if (p == null) return;
            p.intValue = EditorGUILayout.IntSlider(new GUIContent(L(key)), p.intValue, min, max);
        }

        private static void WriteQuality(SerializedProperty qp, QualityParams q)
        {
            qp.FindPropertyRelative(nameof(QualityParams.msSsim)).floatValue = q.msSsim;
            qp.FindPropertyRelative(nameof(QualityParams.deltaE2000Mean)).floatValue = q.deltaE2000Mean;
            qp.FindPropertyRelative(nameof(QualityParams.alphaCutoutIoU)).floatValue = q.alphaCutoutIoU;
            qp.FindPropertyRelative(nameof(QualityParams.alphaBlendRmse)).floatValue = q.alphaBlendRmse;
            qp.FindPropertyRelative(nameof(QualityParams.normalAngleMeanDeg)).floatValue = q.normalAngleMeanDeg;
            qp.FindPropertyRelative(nameof(QualityParams.normalAngleP95Deg)).floatValue = q.normalAngleP95Deg;
            qp.FindPropertyRelative(nameof(QualityParams.grayRmse)).floatValue = q.grayRmse;
        }

        private static string L(string key) => ATOL10n.L(key);
    }
}

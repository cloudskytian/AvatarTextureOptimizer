// Component inspector: novice-friendly defaults on top, advanced sections folded.
// 组件检查器：小白友好，默认收起高级选项。
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class AtoEditor : Editor
    {
        private bool _foldQuality, _foldAtlas, _foldPlatform, _foldWhitelist, _foldMisc;
        private static readonly int[] DensityValues = { 512, 1024, 2048, 4096, 8192 };
        private static readonly int[] PaddingValues = { 4, 8, 16, 32, 64 };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var t = (AvatarTextureOptimizer)target;
            AtoL10n.LanguageOverride = t.languageOverride;

            EditorGUILayout.HelpBox(AtoL10n.Tr("ui.help.novice"), MessageType.Info);

            // language / 语言
            DrawLanguage(t);

            EditorGUILayout.Space();

            // quality tier / 质量挡位
            var tierProp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.qualityTier));
            var tierNames = new[]
            {
                AtoL10n.Tr("ui.tier.lossless"), AtoL10n.Tr("ui.tier.high"), AtoL10n.Tr("ui.tier.balanced"),
                AtoL10n.Tr("ui.tier.compact"), AtoL10n.Tr("ui.tier.custom")
            };
            tierProp.enumValueIndex = EditorGUILayout.Popup(AtoL10n.Tr("ui.quality.tier"),
                tierProp.enumValueIndex, tierNames);

            // advanced quality params, folded / 高级质量参数（折叠）
            _foldQuality = EditorGUILayout.Foldout(_foldQuality, AtoL10n.Tr("ui.quality.advanced"), true);
            if (_foldQuality)
            {
                EditorGUI.indentLevel++;
                bool custom = (AtoQualityTier)tierProp.enumValueIndex == AtoQualityTier.Custom;
                if (!custom)
                {
                    // tier switch shows tier's live values readonly / 非自定义只读展示当前挡位参数
                    var q = AtoQualityParams.ForTier((AtoQualityTier)tierProp.enumValueIndex);
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.FloatField(AtoL10n.Tr("ui.q.msssim"), q.minMsSsim);
                        EditorGUILayout.FloatField(AtoL10n.Tr("ui.q.deltae"), q.maxDeltaE00P95);
                        EditorGUILayout.FloatField(AtoL10n.Tr("ui.q.alphaiou"), q.minAlphaCutoutIoU);
                        EditorGUILayout.FloatField(AtoL10n.Tr("ui.q.alpharmse"), q.maxAlphaBlendRmse);
                        EditorGUILayout.FloatField(AtoL10n.Tr("ui.q.normal"), q.maxNormalAngleP95Deg);
                        EditorGUILayout.FloatField(AtoL10n.Tr("ui.q.gray"), q.maxGrayRmse);
                    }
                    EditorGUILayout.HelpBox(AtoL10n.Tr("ui.q.tierhint"), MessageType.None);
                }
                else
                {
                    var cq = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.customQuality));
                    EditorGUILayout.PropertyField(cq.FindPropertyRelative(nameof(AtoQualityParams.minMsSsim)),
                        new GUIContent(AtoL10n.Tr("ui.q.msssim")));
                    EditorGUILayout.PropertyField(cq.FindPropertyRelative(nameof(AtoQualityParams.maxDeltaE00P95)),
                        new GUIContent(AtoL10n.Tr("ui.q.deltae")));
                    EditorGUILayout.PropertyField(cq.FindPropertyRelative(nameof(AtoQualityParams.minAlphaCutoutIoU)),
                        new GUIContent(AtoL10n.Tr("ui.q.alphaiou")));
                    EditorGUILayout.PropertyField(cq.FindPropertyRelative(nameof(AtoQualityParams.maxAlphaBlendRmse)),
                        new GUIContent(AtoL10n.Tr("ui.q.alpharmse")));
                    EditorGUILayout.PropertyField(cq.FindPropertyRelative(nameof(AtoQualityParams.maxNormalAngleP95Deg)),
                        new GUIContent(AtoL10n.Tr("ui.q.normal")));
                    EditorGUILayout.PropertyField(cq.FindPropertyRelative(nameof(AtoQualityParams.maxGrayRmse)),
                        new GUIContent(AtoL10n.Tr("ui.q.gray")));
                }

                // density / 像素密度
                DrawDensity(nameof(AvatarTextureOptimizer.minDensity), "ui.density.min");
                DrawDensity(nameof(AvatarTextureOptimizer.maxDensity), "ui.density.max");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();

            // atlas / 图集
            _foldAtlas = EditorGUILayout.Foldout(_foldAtlas, AtoL10n.Tr("ui.atlas.section"), true);
            if (_foldAtlas)
            {
                EditorGUI.indentLevel++;
                Toggle(nameof(AvatarTextureOptimizer.generateAtlas), "ui.atlas.generate");
                Toggle(nameof(AvatarTextureOptimizer.experimentalNpot), "ui.atlas.npot");
                DrawIntPopup(nameof(AvatarTextureOptimizer.minPadding), "ui.atlas.padding", PaddingValues);
                Toggle(nameof(AvatarTextureOptimizer.dedupTextures), "ui.dedup.textures");
                Toggle(nameof(AvatarTextureOptimizer.dedupMaterials), "ui.dedup.materials");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();

            // platform overrides / 平台覆盖
            _foldPlatform = EditorGUILayout.Foldout(_foldPlatform, AtoL10n.Tr("ui.platform.section"), true);
            if (_foldPlatform)
            {
                EditorGUI.indentLevel++;
                DrawPlatform(nameof(AvatarTextureOptimizer.pcOverride), "ui.platform.pc");
                DrawPlatform(nameof(AvatarTextureOptimizer.androidOverride), "ui.platform.android");
                DrawPlatform(nameof(AvatarTextureOptimizer.iosOverride), "ui.platform.ios");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();

            // whitelist / 白名单
            _foldWhitelist = EditorGUILayout.Foldout(_foldWhitelist, AtoL10n.Tr("ui.whitelist"), true);
            if (_foldWhitelist)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(AtoL10n.Tr("ui.whitelist.tip"), MessageType.None);
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty(nameof(AvatarTextureOptimizer.whitelist)), true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();

            // misc / 其他
            _foldMisc = EditorGUILayout.Foldout(_foldMisc, AtoL10n.Tr("ui.misc"), true);
            if (_foldMisc)
            {
                EditorGUI.indentLevel++;
                Toggle(nameof(AvatarTextureOptimizer.verboseLog), "ui.verbose");
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLanguage(AvatarTextureOptimizer t)
        {
            var langs = AtoL10n.AvailableLanguages.ToList();
            var display = new[] { AtoL10n.Tr("ui.language.auto") }.Concat(langs).ToArray();
            int cur = string.IsNullOrEmpty(t.languageOverride) ? 0 : langs.IndexOf(t.languageOverride) + 1;
            if (cur < 0) cur = 0;
            int next = EditorGUILayout.Popup(AtoL10n.Tr("ui.language"), cur, display);
            var value = next == 0 ? "" : langs[next - 1];
            if (value != t.languageOverride)
            {
                serializedObject.FindProperty(nameof(AvatarTextureOptimizer.languageOverride)).stringValue = value;
            }
        }

        private void Toggle(string prop, string key) =>
            EditorGUILayout.PropertyField(serializedObject.FindProperty(prop), new GUIContent(AtoL10n.Tr(key)));

        private void DrawDensity(string prop, string key)
        {
            var p = serializedObject.FindProperty(prop);
            var labels = DensityValues.Select(v => new GUIContent(v.ToString())).ToArray();
            p.intValue = EditorGUILayout.IntPopup(new GUIContent(AtoL10n.Tr(key)), p.intValue, labels, DensityValues);
        }

        private void DrawIntPopup(string prop, string key, int[] values)
        {
            var p = serializedObject.FindProperty(prop);
            var labels = values.Select(v => new GUIContent(v.ToString())).ToArray();
            p.intValue = EditorGUILayout.IntPopup(new GUIContent(AtoL10n.Tr(key)), p.intValue, labels, values);
        }

        private void DrawPlatform(string prop, string labelKey)
        {
            var p = serializedObject.FindProperty(prop);
            var enabled = p.FindPropertyRelative(nameof(AtoPlatformOverride.overrideEnabled));
            EditorGUILayout.PropertyField(enabled, new GUIContent(AtoL10n.Tr(labelKey)));
            if (!enabled.boolValue) return; // params only when checked / 勾选才显示参数
            EditorGUI.indentLevel++;
            Field(p, nameof(AtoPlatformOverride.opaqueFormat), "ui.format.opaque");
            Field(p, nameof(AtoPlatformOverride.transparentFormat), "ui.format.transparent");
            Field(p, nameof(AtoPlatformOverride.normalFormat), "ui.format.normal");
            Field(p, nameof(AtoPlatformOverride.grayFormat), "ui.format.gray");
            Field(p, nameof(AtoPlatformOverride.mipOpaque), "ui.mip.opaque");
            Field(p, nameof(AtoPlatformOverride.mipTransparent), "ui.mip.transparent");
            Field(p, nameof(AtoPlatformOverride.mipNormal), "ui.mip.normal");
            Field(p, nameof(AtoPlatformOverride.mipGray), "ui.mip.gray");
            Field(p, nameof(AtoPlatformOverride.minDensity), "ui.density.min");
            Field(p, nameof(AtoPlatformOverride.maxDensity), "ui.density.max");
            EditorGUI.indentLevel--;
        }

        private void Field(SerializedProperty parent, string child, string key) =>
            EditorGUILayout.PropertyField(parent.FindPropertyRelative(child), new GUIContent(AtoL10n.Tr(key)));
    }
}

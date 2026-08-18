// Avatar Texture Optimizer (ATO)
// Custom inspector for the ATOAvatarOptimizer component. Beginner-friendly defaults,
// advanced options collapsed, platform overrides shown per enabled platform.
// ATOAvatarOptimizer 组件的自定义 Inspector。小白友好默认值，高级选项折叠，平台覆写按勾选显示。

using UnityEditor;
using UnityEngine;

namespace NetFosa.ATO
{
    [CustomEditor(typeof(ATOAvatarOptimizer))]
    public class ATOAvatarInspector : Editor
    {
        private bool _showGeneral = true;
        private bool _showCompression;
        private bool _showWhitelist;
        private bool _showAdvanced;

        private static readonly GUIContent[] QualityLevels =
        {
            new GUIContent("Ultra (近无损)"),
            new GUIContent("High (高质量, 默认)"),
            new GUIContent("Medium (中)"),
            new GUIContent("Low (低)"),
            new GUIContent("Custom (自定义)"),
        };

        public override void OnInspectorGUI()
        {
            ATOI18n.Initialize();
            var comp = (ATOAvatarOptimizer)target;

            EditorGUILayout.HelpBox(
                "ATO analyzes this avatar's textures/UVs and re-packs them into atlases at your chosen quality.\n" +
                "ATO 会分析该 Avatar 的贴图/UV，并按所选质量重打包为图集。",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();

            _showGeneral = EditorGUILayout.Foldout(_showGeneral, "General / 通用", true);
            if (_showGeneral)
            {
                EditorGUI.indentLevel++;
                var g = comp.general.profile;
                g.qualityLevel = (ATOQualityLevel)EditorGUILayout.Popup("Quality / 质量挡位", (int)g.qualityLevel, QualityLevels);
                if (g.qualityLevel == ATOQualityLevel.Custom)
                {
                    var t = g.customThresholds;
                    t.targetQuality = EditorGUILayout.Slider("Target quality / 目标质量", t.targetQuality, 0f, 1f);
                    t.msSsimMin = EditorGUILayout.Slider("MS-SSIM min / 下限", t.msSsimMin, 0f, 1f);
                    t.deltaEMax = EditorGUILayout.Slider("ΔE2000 max / 上限", t.deltaEMax, 0f, 100f);
                    t.alphaRmseMax = EditorGUILayout.Slider("α RMSE max / 上限", t.alphaRmseMax, 0f, 1f);
                    t.alphaIoUMin = EditorGUILayout.Slider("α IoU min / 下限", t.alphaIoUMin, 0f, 1f);
                    t.angleDegMax = EditorGUILayout.Slider("Normal angle max (deg) / 法线角度上限", t.angleDegMax, 0f, 90f);
                    t.grayRmseMax = EditorGUILayout.Slider("Gray RMSE max / 上限", t.grayRmseMax, 0f, 1f);
                    g.customThresholds = t;
                }
                g.generateAtlas = EditorGUILayout.Toggle("Generate atlas / 生成图集", g.generateAtlas);
                g.padding = EditorGUILayout.IntPopup("Padding / 岛间距 (px)", g.padding,
                    new[] { "4", "8", "16", "32", "64" }, new[] { 4, 8, 16, 32, 64 });
                g.pixelDensityMin = EditorGUILayout.IntPopup("Min density / 最小密度 (px/m)", g.pixelDensityMin,
                    new[] { "512", "1024", "2048", "4096", "8192" }, new[] { 512, 1024, 2048, 4096, 8192 });
                g.pixelDensityMax = EditorGUILayout.IntPopup("Max density / 最大密度 (px/m)", g.pixelDensityMax,
                    new[] { "512", "1024", "2048", "4096", "8192" }, new[] { 512, 1024, 2048, 4096, 8192 });
                g.dedupTextures = EditorGUILayout.Toggle("Dedup textures / 贴图去重", g.dedupTextures);
                g.dedupMaterials = EditorGUILayout.Toggle("Dedup materials / 材质去重", g.dedupMaterials);
                g.mergeOpaqueSlots = EditorGUILayout.Toggle("Merge opaque slots / 合并不透明槽", g.mergeOpaqueSlots);
                EditorGUI.indentLevel--;
            }

            DrawPlatform(comp, ATOPlatform.PC, "PC (Windows/Linux)");
            DrawPlatform(comp, ATOPlatform.Android, "Android");
            DrawPlatform(comp, ATOPlatform.iOS, "iOS");

            _showCompression = EditorGUILayout.Foldout(_showCompression, "Compression & Mip Streaming / 压缩与 Mip 流式", false);
            if (_showCompression)
            {
                EditorGUI.indentLevel++;
                DrawCompressionChoice(comp.compression.opaque, "Opaque / 不透明");
                DrawCompressionChoice(comp.compression.alpha, "Alpha / 透明");
                DrawCompressionChoice(comp.compression.normal, "Normal / 法线");
                DrawCompressionChoice(comp.compression.grayscale, "Grayscale / 灰度");
                EditorGUI.indentLevel--;
            }

            _showWhitelist = EditorGUILayout.Foldout(_showWhitelist, "Whitelist / 白名单", false);
            if (_showWhitelist)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist.whitelist"), true);
                EditorGUI.indentLevel--;
            }

            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced / 高级", false);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                var a = comp.advanced;
                a.debugLogging = EditorGUILayout.Toggle("Debug logging / 调试日志", a.debugLogging);
                a.verboseLogging = EditorGUILayout.Toggle("Verbose logging / 详细日志", a.verboseLogging);
                a.languageMode = (ATOLanguageMode)EditorGUILayout.Popup("Language / 语言", (int)a.languageMode,
                    new[] { "Auto (NDMF)", "English", "简体中文" });
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(comp);
            }
        }

        private void DrawPlatform(ATOAvatarOptimizer comp, ATOPlatform platform, string label)
        {
            var ov = platform == ATOPlatform.PC ? comp.platform.pc
                : platform == ATOPlatform.Android ? comp.platform.android : comp.platform.ios;
            ov.enabled = EditorGUILayout.Toggle($"Override {label} / 覆写 {label}", ov.enabled);
            if (ov.enabled)
            {
                EditorGUI.indentLevel++;
                var p = ov.profile;
                p.npotAtlas = EditorGUILayout.Toggle("Experimental NPOT atlas / 实验性 NPOT 图集", p.npotAtlas);
                p.maxAtlasSize = EditorGUILayout.IntField("Max atlas size / 最大图集尺寸", p.maxAtlasSize);
                if (platform == ATOPlatform.PC)
                    comp.platform.pc.profile = p;
                else if (platform == ATOPlatform.Android)
                    comp.platform.android.profile = p;
                else
                    comp.platform.ios.profile = p;
                EditorGUI.indentLevel--;
            }
        }

        private void DrawCompressionChoice(ATOCompressionChoice c, string label)
        {
            c.format = (ATOCompressionFormat)EditorGUILayout.EnumPopup(label + " format / 格式", c.format);
            // Single switch controls both mipmaps and Mip Streaming (VRChat binds them). / 单一开关同时控制 mipmap 与 Mip 流式（VRChat 绑定二者）。
            c.mipStreaming = EditorGUILayout.Toggle(label + " mipmaps + streaming / Mipmap+流式", c.mipStreaming);
        }
    }
}

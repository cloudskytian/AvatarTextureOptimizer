using Fosa.ATO;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public class ATOEditorGUI : Editor
    {
        private bool _showAdvanced = false;
        private bool _showCompression = false;
        private bool _showPlatform = false;

        public override void OnInspectorGUI()
        {
            var t = (AvatarTextureOptimizer)target;

            EditorGUILayout.HelpBox(
                "Avatar Texture Optimizer (ATO) — VRChat 贴图优化工具。\n" +
                "仅处理贴图与 UV，不改材质其他参数；优化前后保持视觉一致。",
                MessageType.Info);

            // 语言选择。Language selector.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Language / 语言", GUILayout.Width(140));
            var mode = (ATOLocalization.LanguageMode)EditorGUILayout.EnumPopup(ATOLocalization.Mode);
            ATOLocalization.Mode = mode;
            if (mode == ATOLocalization.LanguageMode.Manual)
            {
                var langs = new System.Collections.Generic.List<string>(ATOLocalization.AvailableLanguages);
                int idx = langs.IndexOf(ATOLocalization.ManualLanguage);
                if (idx < 0) idx = 0;
                int newIdx = EditorGUILayout.Popup(idx, langs.ToArray());
                ATOLocalization.ManualLanguage = langs[newIdx];
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("General", EditorStyles.boldLabel);
            t.generateAtlas = EditorGUILayout.Toggle("生成图集 (Generate Atlas)", t.generateAtlas);
            t.padding = (ATOPadding)EditorGUILayout.EnumPopup("Padding", t.padding);
            t.allowNPOT = EditorGUILayout.Toggle("实验性 NPOT (Experimental NPOT)", t.allowNPOT);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quality", EditorStyles.boldLabel);
            t.qualityPreset = (ATOQualityPreset)EditorGUILayout.EnumPopup("目标质量 (Target Quality)", t.qualityPreset);
            if (t.qualityPreset == ATOQualityPreset.Custom)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("customQuality"), true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            t.minPixelDensity = EditorGUILayout.FloatField("最小像素密度 (Min px/m)", t.minPixelDensity);
            t.maxPixelDensity = EditorGUILayout.FloatField("最大像素密度 (Max px/m)", t.maxPixelDensity);

            EditorGUILayout.Space();
            t.mipmapAndStreaming = EditorGUILayout.Toggle("Mipmap + MipStreaming", t.mipmapAndStreaming);

            EditorGUILayout.Space();
            t.dedupMaterials = EditorGUILayout.Toggle("材质去重 (Dedup Materials)", t.dedupMaterials);
            t.dedupTextures = EditorGUILayout.Toggle("贴图去重 (Dedup Textures)", t.dedupTextures);

            EditorGUILayout.Space();
            _showCompression = EditorGUILayout.Foldout(_showCompression, "压缩 (Compression)");
            if (_showCompression)
            {
                EditorGUI.indentLevel++;
                var c = t.compression;
                c.transparent = (ATOCompressionFormat)EditorGUILayout.EnumPopup("透明贴图 (Transparent)", c.transparent);
                c.opaque = (ATOCompressionFormat)EditorGUILayout.EnumPopup("不透明贴图 (Opaque)", c.opaque);
                c.normalMap = (ATOCompressionFormat)EditorGUILayout.EnumPopup("法线贴图 (Normal)", c.normalMap);
                c.grayscale = (ATOCompressionFormat)EditorGUILayout.EnumPopup("灰度贴图 (Grayscale)", c.grayscale);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            _showPlatform = EditorGUILayout.Foldout(_showPlatform, "平台覆盖 (Platform Override)");
            if (_showPlatform)
            {
                EditorGUI.indentLevel++;
                DrawPlatform("PC", t.platformPC);
                DrawPlatform("Android", t.platformAndroid);
                DrawPlatform("iOS", t.platformiOS);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), true);

            EditorGUILayout.Space();
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "高级 (Advanced)");
            if (_showAdvanced)
            {
                t.verboseLogging = EditorGUILayout.Toggle("详细日志 (Verbose Logging)", t.verboseLogging);
            }

            if (GUI.changed)
            {
                EditorUtility.SetDirty(t);
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void DrawPlatform(string label, ATOPlatformSettings s)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            s.overrideEnabled = EditorGUILayout.Toggle(label, s.overrideEnabled);
            if (s.overrideEnabled)
            {
                EditorGUI.indentLevel++;
                s.maxAtlasSize = EditorGUILayout.IntField("最大图集边长 (Max Atlas Size)", s.maxAtlasSize);
                var c = s.compression;
                c.transparent = (ATOCompressionFormat)EditorGUILayout.EnumPopup("透明", c.transparent);
                c.opaque = (ATOCompressionFormat)EditorGUILayout.EnumPopup("不透明", c.opaque);
                c.normalMap = (ATOCompressionFormat)EditorGUILayout.EnumPopup("法线", c.normalMap);
                c.grayscale = (ATOCompressionFormat)EditorGUILayout.EnumPopup("灰度", c.grayscale);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }
    }
}

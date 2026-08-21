using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// ATO 组件自定义 Inspector / Custom inspector for the ATO component.
    /// 面向小白: 默认选项即最优解, 全部参数默认折叠; 面向高级用户: 折叠区可展开修改.
    /// Beginner-friendly: defaults are optimal and everything is collapsed by default;
    /// power users can expand and tweak every section.
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    internal sealed class ATOInspector : UnityEditor.Editor
    {
        private static readonly int[] PaddingChoices = { 4, 8, 16, 32, 64 };
        private static readonly float[] DensityPresets = { 512f, 1024f, 2048f, 4096f, 8192f };

        private bool _showQuality;
        private bool _showCompression;
        private bool _showPlatform;
        private bool _showAdvanced;

        private static string T(string key, params object[] args) => ATOI18n.T(key, args);

        public override void OnInspectorGUI()
        {
            var t = (AvatarTextureOptimizer)target;
            ATOI18n.Resolve(t.language);

            EditorGUILayout.HelpBox(T("ui.info.mount"), MessageType.Info);

            serializedObject.Update();

            // ---------------------------------------------------------------
            // 图集 / Atlas
            // ---------------------------------------------------------------
            EditorGUILayout.LabelField(T("ui.section.atlas"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.enableAtlas)),
                new GUIContent(T("ui.atlas.enable")));

            var paddingProp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.minPadding));
            paddingProp.intValue = EditorGUILayout.IntPopup(T("ui.atlas.padding"), paddingProp.intValue,
                PaddingChoices.Select(p => p.ToString()).ToArray(), PaddingChoices);

            var npotProp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.enableNPOT));
            EditorGUILayout.PropertyField(npotProp, new GUIContent(T("ui.atlas.npot")));
            if (npotProp.boolValue)
            {
                EditorGUILayout.HelpBox(T("ui.atlas.npot.experimental"), MessageType.Warning);
            }

            // ---------------------------------------------------------------
            // 质量 / Quality
            // ---------------------------------------------------------------
            EditorGUILayout.Space();
            _showQuality = EditorGUILayout.Foldout(_showQuality, T("ui.section.quality"), true);
            if (_showQuality)
            {
                var presetProp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.qualityPreset));
                EditorGUILayout.PropertyField(presetProp, new GUIContent(T("ui.quality.preset")));

                if ((ATOQualityPreset)presetProp.enumValueIndex == ATOQualityPreset.Custom)
                {
                    var q = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.customQuality));
                    EditorGUILayout.PropertyField(q, new GUIContent(T("ui.quality.custom")), true);
                }

                // 像素密度 / texel density
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.minTexelDensity)),
                    new GUIContent(T("ui.density.min")));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.maxTexelDensity)),
                    new GUIContent(T("ui.density.max")));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(T("ui.density.presets"), GUILayout.Width(120));
                foreach (var d in DensityPresets)
                {
                    if (GUILayout.Button(d.ToString()))
                    {
                        serializedObject.FindProperty(nameof(AvatarTextureOptimizer.minTexelDensity)).floatValue = d;
                        serializedObject.FindProperty(nameof(AvatarTextureOptimizer.maxTexelDensity)).floatValue = d;
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            // ---------------------------------------------------------------
            // Mipmap / Mipmap & Streaming
            // ---------------------------------------------------------------
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.enableMipmaps)),
                new GUIContent(T("ui.mipmap.toggle")));
            EditorGUILayout.HelpBox(T("ui.mipmap.note"), MessageType.None);

            // ---------------------------------------------------------------
            // 压缩 / Compression
            // ---------------------------------------------------------------
            EditorGUILayout.Space();
            _showCompression = EditorGUILayout.Foldout(_showCompression, T("ui.section.compression"), true);
            if (_showCompression)
            {
                var target = UnityEditor.EditorUserBuildSettings.activeBuildTarget;
                DrawFormatPopup(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.opaqueFormat)),
                    T("ui.compression.opaque"), target);
                DrawFormatPopup(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.transparentFormat)),
                    T("ui.compression.transparent"), target);
                DrawFormatPopup(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.normalFormat)),
                    T("ui.compression.normal"), target);
                DrawFormatPopup(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.grayscaleFormat)),
                    T("ui.compression.grayscale"), target);
            }

            // ---------------------------------------------------------------
            // 平台 override / Platform overrides
            // ---------------------------------------------------------------
            EditorGUILayout.Space();
            _showPlatform = EditorGUILayout.Foldout(_showPlatform, T("ui.section.platform"), true);
            if (_showPlatform)
            {
                DrawPlatform(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.windows)), "Windows (PC)");
                DrawPlatform(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.android)), "Android");
                DrawPlatform(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.ios)), "iOS");
            }

            // ---------------------------------------------------------------
            // 白名单 / Whitelist
            // ---------------------------------------------------------------
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("ui.section.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(T("ui.whitelist.note"), MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.whitelist)), true);

            // ---------------------------------------------------------------
            // 去重合并 / Dedup & merging
            // ---------------------------------------------------------------
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.dedupMaterials)),
                new GUIContent(T("ui.dedup.materials")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.dedupTextures)),
                new GUIContent(T("ui.dedup.textures")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.mergeOpaqueSlots)),
                new GUIContent(T("ui.dedup.slots")));

            // ---------------------------------------------------------------
            // 高级 / Advanced
            // ---------------------------------------------------------------
            EditorGUILayout.Space();
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, T("ui.section.advanced"), true);
            if (_showAdvanced)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.debugLogging)),
                    new GUIContent(T("ui.advanced.debug")));

                var langProp = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.language));
                var langs = new[] { "Auto" }.Concat(ATOI18n.Languages).ToArray();
                int idx = System.Array.IndexOf(langs, langProp.stringValue);
                if (idx < 0) idx = 0;
                idx = EditorGUILayout.Popup(T("ui.advanced.language"), idx, langs);
                langProp.stringValue = langs[idx];
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawPlatform(SerializedProperty platformProp, string label)
        {
            var enabledProp = platformProp.FindPropertyRelative(nameof(ATOPlatformSettings.overrideEnabled));
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.PropertyField(enabledProp, new GUIContent(T("ui.platform.override", label)));
            if (!enabledProp.boolValue)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(platformProp, new GUIContent(label), true);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 按当前构建平台能力过滤压缩格式选项(剔除不支持的项, 构建期另有兜底校验).
        /// Filters compression format options by the current build target's capabilities
        /// (build-time validation still acts as the safety net).
        /// </summary>
        private void DrawFormatPopup(SerializedProperty prop, string label, UnityEditor.BuildTarget target)
        {
            var values = System.Enum.GetValues(typeof(ATOCompressionFormat));
            var names = System.Enum.GetNames(typeof(ATOCompressionFormat));
            var allowed = new System.Collections.Generic.List<int>();
            var allowedNames = new System.Collections.Generic.List<string>();

            for (int i = 0; i < values.Length; i++)
            {
                var v = (ATOCompressionFormat)values.GetValue(i);
                if (v == ATOCompressionFormat.Auto)
                {
                    allowed.Add(i);
                    allowedNames.Add(names[i]);
                    continue;
                }

                var fmt = ATOImportConfig.ResolveFormat(v);
                bool ok = fmt == null || UnityEditor.TextureImporter.IsPlatformTextureFormatValid(
                    UnityEditor.TextureImporterType.Default, target, fmt.Value);
                if (ok)
                {
                    allowed.Add(i);
                    allowedNames.Add(names[i]);
                }
            }

            int current = System.Array.IndexOf(allowed.ToArray(), prop.enumValueIndex);
            if (current < 0) current = 0;
            int sel = EditorGUILayout.Popup(label, current, allowedNames.ToArray());
            prop.enumValueIndex = allowed[sel];
        }
    }
}

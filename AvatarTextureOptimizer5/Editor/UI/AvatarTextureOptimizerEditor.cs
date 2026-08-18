// Copyright (c) fosa. Licensed under the MIT License.
// Inspector for the optimizer component. Defaults are safe and the common case needs no
// configuration at all; advanced controls stay collapsed so beginners are never overwhelmed.
// 优化器组件的 Inspector。默认配置安全，常见场景无需任何设置；
// 高级选项默认折叠，使新手不会感到不知所措。

using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="AvatarTextureOptimizer" />.
    /// <see cref="AvatarTextureOptimizer" /> 的自定义 Inspector。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private SerializedProperty _settings;

        private bool _showQuality = true;
        private bool _showAtlas;
        private bool _showOutput;
        private bool _showExclusions;
        private bool _showPlatform;
        private bool _showDebug;

        private void OnEnable()
        {
            _settings = serializedObject.FindProperty("settings");
        }

        /// <inheritdoc />
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Settings could not be loaded.", MessageType.Error);
                return;
            }

            DrawHeader(out var validationFailed);
            if (validationFailed) return;

            var shared = _settings.FindPropertyRelative("shared");
            if (shared == null)
            {
                EditorGUILayout.HelpBox("Malformed settings.", MessageType.Error);
                return;
            }

            EditorGUILayout.Space();
            DrawQualitySection(shared);
            DrawAtlasSection(shared);
            DrawOutputSection(shared);
            DrawExclusionsSection();
            DrawPlatformSection();
            DrawDebugSection();

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Draws the title and any setup problems the user must fix before building.
        /// 绘制标题以及用户在构建前必须修复的配置问题。
        /// </summary>
        private void DrawHeader(out bool validationFailed)
        {
            validationFailed = false;

            EditorGUILayout.LabelField(
                ATOLocalization.Tr("ato.component.title"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                ATOLocalization.Tr("ato.component.description"),
                EditorStyles.wordWrappedMiniLabel);

            var component = (AvatarTextureOptimizer)target;
            var root = component.gameObject;

            // Duplicate components produce conflicting atlas layouts; catch it in the editor
            // rather than failing the build.
            // 重复组件会产生互相冲突的图集布局；在编辑器中即时捕获，而不是等到构建失败。
            var all = root.GetComponentsInParent<AvatarTextureOptimizer>(true);
            if (all.Length > 1)
            {
                EditorGUILayout.HelpBox(
                    ATOLocalization.Tr("ato.error.multiple-components"), MessageType.Error);
                validationFailed = true;
            }

            if (!HasAvatarDescriptor(root))
            {
                EditorGUILayout.HelpBox(
                    ATOLocalization.Tr("ato.error.no-descriptor"), MessageType.Error);
                validationFailed = true;
            }
        }

        /// <summary>
        /// Checks for a VRCAvatarDescriptor without a hard SDK reference.
        /// 在不建立 SDK 硬引用的前提下检查 VRCAvatarDescriptor。
        /// </summary>
        private static bool HasAvatarDescriptor(GameObject go)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var name = c.GetType().Name;
                if (name == "VRCAvatarDescriptor") return true;
            }

            return false;
        }

        private void DrawQualitySection(SerializedProperty platform)
        {
            _showQuality = Section(_showQuality, "ato.section.quality");
            if (!_showQuality) return;

            using (new EditorGUI.IndentLevelScope())
            {
                var tier = platform.FindPropertyRelative("tier");
                EditorGUILayout.PropertyField(
                    tier, new GUIContent(
                        ATOLocalization.Tr("ato.quality.tier"),
                        ATOLocalization.Tr("ato.quality.tier.tooltip")));

                var tierValue = (QualityTier)tier.enumValueIndex;

                if (tierValue == QualityTier.Maximum)
                {
                    EditorGUILayout.HelpBox(
                        ATOLocalization.Tr("ato.quality.maximum.note"), MessageType.Info);
                }

                if (tierValue == QualityTier.Custom)
                {
                    var custom = platform.FindPropertyRelative("customQuality");
                    if (custom != null)
                    {
                        using (new EditorGUI.IndentLevelScope())
                        {
                            DrawCustomQuality(custom);
                        }
                    }
                }
            }
        }

        private static void DrawCustomQuality(SerializedProperty quality)
        {
            DrawField(quality, "msSsimMin", "ato.quality.msssim");
            DrawField(quality, "ssimMin", "ato.quality.ssim");
            DrawField(quality, "deltaE00Mean", "ato.quality.deltaE.mean");
            DrawField(quality, "deltaE00P95", "ato.quality.deltaE.p95");
            DrawField(quality, "normalAngleMeanDeg", "ato.quality.normal.mean");
            DrawField(quality, "normalAngleP95Deg", "ato.quality.normal.p95");
            DrawField(quality, "grayscaleRmse255", "ato.quality.grayscale.rmse");
            DrawField(quality, "cutoutIoUMin", "ato.quality.cutout.iou");
            DrawField(quality, "blendAlphaRmse255", "ato.quality.blend.rmse");
            DrawField(quality, "minPixelDensity", "ato.quality.density.min");
            DrawField(quality, "maxPixelDensity", "ato.quality.density.max");
        }

        private void DrawAtlasSection(SerializedProperty platform)
        {
            _showAtlas = Section(_showAtlas, "ato.section.atlas");
            if (!_showAtlas) return;

            using (new EditorGUI.IndentLevelScope())
            {
                DrawField(platform, "generateAtlas", "ato.atlas.generate", "ato.atlas.generate.tooltip");
                DrawField(platform, "minPadding", "ato.atlas.padding", "ato.atlas.padding.tooltip");
                DrawField(platform, "maxAtlasSize", "ato.atlas.maxsize");
                DrawField(platform, "allowNpot", "ato.atlas.npot", "ato.atlas.npot.tooltip");

                var npot = platform.FindPropertyRelative("allowNpot");
                if (npot != null && npot.boolValue)
                {
                    EditorGUILayout.HelpBox(
                        ATOLocalization.Tr("ato.atlas.npot.tooltip"), MessageType.Warning);
                }

                EditorGUILayout.Space(2);
                DrawField(platform, "deduplicateTextures", "ato.dedup.textures");
                DrawField(platform, "deduplicateMaterials", "ato.dedup.materials");
            }
        }

        private void DrawOutputSection(SerializedProperty platform)
        {
            _showOutput = Section(_showOutput, "ato.section.output");
            if (!_showOutput) return;

            using (new EditorGUI.IndentLevelScope())
            {
                DrawCategory(platform, "opaqueColor", "ato.output.opaque");
                DrawCategory(platform, "transparentColor", "ato.output.transparent");
                DrawCategory(platform, "normalMap", "ato.output.normal");
                DrawCategory(platform, "grayscale", "ato.output.grayscale");
            }
        }

        private static void DrawCategory(
            SerializedProperty platform, string field, string labelKey)
        {
            var prop = platform.FindPropertyRelative(field);
            if (prop == null) return;

            EditorGUILayout.LabelField(ATOLocalization.Tr(labelKey), EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                DrawField(prop, "format", "ato.output.format");
                DrawField(prop, "mipmapAndStreaming", "ato.output.mipmap");
                DrawField(prop, "compressionQuality", "ato.output.quality");
            }
        }

        private void DrawExclusionsSection()
        {
            _showExclusions = Section(_showExclusions, "ato.section.whitelist");
            if (!_showExclusions) return;

            var whitelist = _settings.FindPropertyRelative("whitelist");
            if (whitelist == null) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.HelpBox(
                    ATOLocalization.Tr("ato.whitelist.tooltip"), MessageType.None);
                EditorGUILayout.PropertyField(
                    whitelist,
                    new GUIContent(ATOLocalization.Tr("ato.whitelist.label")),
                    true);
            }
        }

        private void DrawPlatformSection()
        {
            _showPlatform = Section(_showPlatform, "ato.section.platform");
            if (!_showPlatform) return;

            var overrides = _settings.FindPropertyRelative("platformOverrides");
            if (overrides == null) return;

            using (new EditorGUI.IndentLevelScope())
            {
                for (var i = 0; i < overrides.arraySize; i++)
                {
                    var entry = overrides.GetArrayElementAtIndex(i);
                    var platformProp = entry.FindPropertyRelative("platform");
                    var enabledProp = entry.FindPropertyRelative("enabled");
                    if (platformProp == null || enabledProp == null) continue;

                    var label = ((ATOPlatform)platformProp.enumValueIndex).ToString();

                    EditorGUILayout.BeginHorizontal();
                    enabledProp.boolValue = EditorGUILayout.ToggleLeft(
                        label, enabledProp.boolValue);
                    EditorGUILayout.EndHorizontal();

                    if (!enabledProp.boolValue) continue;

                    using (new EditorGUI.IndentLevelScope())
                    {
                        DrawField(entry, "tier", "ato.quality.tier");

                        if ((QualityTier)entry.FindPropertyRelative("tier").enumValueIndex
                            == QualityTier.Custom)
                        {
                            var custom = entry.FindPropertyRelative("customQuality");
                            if (custom != null)
                            {
                                using (new EditorGUI.IndentLevelScope())
                                {
                                    DrawCustomQuality(custom);
                                }
                            }
                        }

                        DrawField(entry, "generateAtlas", "ato.atlas.generate");
                        DrawField(entry, "minPadding", "ato.atlas.padding");
                        DrawField(entry, "maxAtlasSize", "ato.atlas.maxsize");
                        DrawField(entry, "allowNpot", "ato.atlas.npot");
                        DrawField(entry, "deduplicateTextures", "ato.dedup.textures");
                        DrawField(entry, "deduplicateMaterials", "ato.dedup.materials");

                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField(
                            ATOLocalization.Tr("ato.section.output"), EditorStyles.boldLabel);
                        DrawCategory(entry, "opaqueColor", "ato.output.opaque");
                        DrawCategory(entry, "transparentColor", "ato.output.transparent");
                        DrawCategory(entry, "normalMap", "ato.output.normal");
                        DrawCategory(entry, "grayscale", "ato.output.grayscale");
                    }
                }
            }
        }

        private void DrawDebugSection()
        {
            _showDebug = Section(_showDebug, "ato.section.debug");
            if (!_showDebug) return;

            using (new EditorGUI.IndentLevelScope())
            {
                DrawField(_settings, "verboseLogging", "ato.debug.verbose", "ato.debug.verbose.tooltip");
                DrawField(_settings, "languageMode", "ato.language");
            }
        }

        private static bool Section(bool state, string labelKey)
        {
            EditorGUILayout.Space(2);
            return EditorGUILayout.Foldout(
                state, ATOLocalization.Tr(labelKey), true, EditorStyles.foldoutHeader);
        }

        private static void DrawField(
            SerializedProperty parent, string field, string labelKey, string tooltipKey = null)
        {
            var prop = parent.FindPropertyRelative(field);
            if (prop == null) return;

            var content = new GUIContent(
                ATOLocalization.Tr(labelKey),
                tooltipKey != null ? ATOLocalization.Tr(tooltipKey) : null);

            EditorGUILayout.PropertyField(prop, content, true);
        }
    }
}

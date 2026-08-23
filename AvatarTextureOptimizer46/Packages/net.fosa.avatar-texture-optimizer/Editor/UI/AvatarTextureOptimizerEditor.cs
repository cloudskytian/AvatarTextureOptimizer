// SPDX-License-Identifier: MIT
// EN: Inspector for the avatar component. Simple by default, everything else folded away.
// ZH: Avatar 组件的检视面板。默认简洁，其余内容全部折叠。

using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.UI
{
    /// <summary>
    /// EN: Custom inspector. The default view shows only the quality tier and the atlas toggle, which is
    ///     all a newcomer needs; advanced users open the foldouts.
    /// ZH: 自定义检视面板。默认视图只显示质量挡位与图集开关，这已是新手所需的全部；
    ///     高级用户可以展开折叠项。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private SerializedProperty _settings;
        private bool _advancedOpen;
        private bool _whitelistOpen;
        private bool _texturesOpen;
        private bool _platformOpen;
        private bool _debugOpen;
        private ReorderableWhitelist _whitelistUi;

        private void OnEnable()
        {
            _settings = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.settings));
            _whitelistUi = new ReorderableWhitelist();
        }

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (AvatarTextureOptimizer)target;
            var s = component.settings;

            DrawHeaderBox();
            DrawLanguageSelector(s);

            EditorGUILayout.Space(4);
            DrawTier(s.common);
            DrawProp(_settings, "common.generateAtlas", "ui.generateAtlas");

            EditorGUILayout.Space(6);
            _whitelistOpen = EditorGUILayout.Foldout(_whitelistOpen, AtoLocalizer.Tr("ui.whitelist"), true);
            if (_whitelistOpen)
            {
                EditorGUILayout.HelpBox(AtoLocalizer.Tr("ui.whitelist.help"), MessageType.Info);
                _whitelistUi.Draw(_settings.FindPropertyRelative("whitelist"));
            }

            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen, AtoLocalizer.Tr("ui.advanced"), true);
            if (_advancedOpen)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawQualityParameters(s.common);
                    DrawProp(_settings, "common.allowNpot", "ui.allowNpot");
                    if (s.common.allowNpot)
                        EditorGUILayout.HelpBox(AtoLocalizer.Tr("ui.allowNpot.help"), MessageType.Warning);
                    DrawProp(_settings, "common.minPadding", "ui.minPadding");
                    DrawProp(_settings, "common.dedupeMaterials", "ui.dedupeMaterials");
                    DrawProp(_settings, "common.dedupeTextures", "ui.dedupeTextures");
                }
            }

            _texturesOpen = EditorGUILayout.Foldout(_texturesOpen, AtoLocalizer.Tr("ui.textureSettings"), true);
            if (_texturesOpen)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.HelpBox(AtoLocalizer.Tr("ui.textureSettings.help"), MessageType.Info);
                    DrawTextureKindSettings(_settings.FindPropertyRelative("common.textures"));
                }
            }

            _platformOpen = EditorGUILayout.Foldout(_platformOpen, AtoLocalizer.Tr("ui.platformOverrides"), true);
            if (_platformOpen)
            {
                using (new EditorGUI.IndentLevelScope())
                    DrawPlatformOverrides(_settings.FindPropertyRelative("platformOverrides"));
            }

            _debugOpen = EditorGUILayout.Foldout(_debugOpen, AtoLocalizer.Tr("ui.debug"), true);
            if (_debugOpen)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProp(_settings, "verboseLogging", "ui.verboseLogging");
                    DrawProp(_settings, "traceLogging", "ui.traceLogging");
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawHeaderBox()
        {
            EditorGUILayout.HelpBox(AtoLocalizer.Tr("ui.intro"), MessageType.None);
        }

        private void DrawLanguageSelector(AtoSettings s)
        {
            var languages = new List<string> { "auto" };
            languages.AddRange(AtoLocalizer.AvailableLanguages);
            int index = Mathf.Max(0, languages.FindIndex(l => l.Equals(s.language, StringComparison.OrdinalIgnoreCase)));
            int newIndex = EditorGUILayout.Popup(AtoLocalizer.Tr("ui.language"), index, languages.ToArray());
            if (newIndex != index)
            {
                Undo.RecordObject(target, "Change ATO language");
                s.language = languages[newIndex];
                AtoLocalizer.LanguageOverride = s.language;
                EditorUtility.SetDirty(target);
            }
            else
            {
                AtoLocalizer.LanguageOverride = s.language;
            }
        }

        private void DrawTier(AtoProfile profile)
        {
            EditorGUI.BeginChangeCheck();
            var tier = (AtoQualityTier)EditorGUILayout.EnumPopup(AtoLocalizer.Tr("ui.tier"), profile.tier);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "Change ATO quality tier");
                profile.tier = tier;
                // EN: Switching to a built-in tier reloads its parameters. The custom tier is never
                //     overwritten, exactly as the specification requires.
                // ZH: 切换到内置挡位会重新加载其参数。自定义挡位永不被覆盖，与规格要求一致。
                if (tier != AtoQualityTier.Custom)
                    profile.quality.CopyFrom(AtoQualityPresets.Create(tier));
                EditorUtility.SetDirty(target);
            }
            EditorGUILayout.LabelField(" ", AtoLocalizer.Tr("ui.tier." + tier), EditorStyles.miniLabel);
        }

        private void DrawQualityParameters(AtoProfile profile)
        {
            var q = profile.EffectiveQuality;
            bool custom = profile.tier == AtoQualityTier.Custom;
            using (new EditorGUI.DisabledScope(!custom))
            {
                EditorGUI.BeginChangeCheck();
                q.minMsSsim = EditorGUILayout.Slider(AtoLocalizer.Tr("ui.q.msSsim"), q.minMsSsim, 0.5f, 1f);
                q.maxDeltaE2000 = EditorGUILayout.Slider(AtoLocalizer.Tr("ui.q.deltaE"), q.maxDeltaE2000, 0f, 20f);
                q.minAlphaIoU = EditorGUILayout.Slider(AtoLocalizer.Tr("ui.q.alphaIoU"), q.minAlphaIoU, 0.5f, 1f);
                q.maxAlphaRmse = EditorGUILayout.Slider(AtoLocalizer.Tr("ui.q.alphaRmse"), q.maxAlphaRmse, 0f, 0.5f);
                q.maxNormalAngleP95 = EditorGUILayout.Slider(AtoLocalizer.Tr("ui.q.normalAngle"), q.maxNormalAngleP95, 0f, 45f);
                q.maxGrayscaleRmse = EditorGUILayout.Slider(AtoLocalizer.Tr("ui.q.grayRmse"), q.maxGrayscaleRmse, 0f, 0.5f);
                q.minPixelDensity = (AtoPixelDensity)EditorGUILayout.EnumPopup(AtoLocalizer.Tr("ui.q.minDensity"), q.minPixelDensity);
                q.maxPixelDensity = (AtoPixelDensity)EditorGUILayout.EnumPopup(AtoLocalizer.Tr("ui.q.maxDensity"), q.maxPixelDensity);
                if (EditorGUI.EndChangeCheck()) EditorUtility.SetDirty(target);
            }
            if (!custom)
                EditorGUILayout.HelpBox(AtoLocalizer.Tr("ui.q.readonly"), MessageType.None);
        }

        private static void DrawTextureKindSettings(SerializedProperty textures)
        {
            if (textures == null) return;
            DrawRelative(textures, "mipmapAndStreaming", "ui.tex.mipmap");
            DrawRelative(textures, "colorOpaqueFormat", "ui.tex.colorOpaque");
            DrawRelative(textures, "colorAlphaFormat", "ui.tex.colorAlpha");
            DrawRelative(textures, "normalFormat", "ui.tex.normal");
            DrawRelative(textures, "grayscaleFormat", "ui.tex.grayscale");
        }

        private static void DrawPlatformOverrides(SerializedProperty overrides)
        {
            if (overrides == null) return;
            for (int i = 0; i < overrides.arraySize; i++)
            {
                var element = overrides.GetArrayElementAtIndex(i);
                var platform = element.FindPropertyRelative("platform");
                var enabled = element.FindPropertyRelative("enabled");

                EditorGUILayout.BeginHorizontal();
                enabled.boolValue = EditorGUILayout.ToggleLeft(
                    ((AtoPlatform)platform.enumValueIndex).ToString(), enabled.boolValue);
                EditorGUILayout.EndHorizontal();

                if (!enabled.boolValue) continue;
                using (new EditorGUI.IndentLevelScope())
                {
                    var profile = element.FindPropertyRelative("profile");
                    DrawRelative(profile, "tier", "ui.tier");
                    DrawRelative(profile, "generateAtlas", "ui.generateAtlas");
                    DrawRelative(profile, "allowNpot", "ui.allowNpot");
                    DrawRelative(profile, "minPadding", "ui.minPadding");
                    DrawTextureKindSettings(profile.FindPropertyRelative("textures"));
                }
            }
        }

        private static void DrawProp(SerializedProperty root, string path, string key)
        {
            var p = root.FindPropertyRelative(path.Replace('.', '/').Replace('/', '.'));
            if (p == null)
            {
                // EN: FindPropertyRelative does not walk dotted paths, so resolve manually.
                // ZH: FindPropertyRelative 不支持带点的路径，因此手动逐级解析。
                p = root;
                foreach (var part in path.Split('.'))
                {
                    p = p?.FindPropertyRelative(part);
                    if (p == null) return;
                }
            }
            EditorGUILayout.PropertyField(p, new GUIContent(AtoLocalizer.Tr(key)));
        }

        private static void DrawRelative(SerializedProperty parent, string name, string key)
        {
            var p = parent?.FindPropertyRelative(name);
            if (p == null) return;
            EditorGUILayout.PropertyField(p, new GUIContent(AtoLocalizer.Tr(key)));
        }
    }

    /// <summary>
    /// EN: A minimal list editor for the whitelist that accepts objects of any type.
    /// ZH: 白名单的极简列表编辑器，接受任意类型的对象。
    /// </summary>
    internal sealed class ReorderableWhitelist
    {
        /// <summary>EN: Draws the list. ZH: 绘制列表。</summary>
        public void Draw(SerializedProperty list)
        {
            if (list == null) return;

            for (int i = 0; i < list.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(list.GetArrayElementAtIndex(i), GUIContent.none);
                if (GUILayout.Button("-", GUILayout.Width(22)))
                {
                    list.DeleteArrayElementAtIndex(i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            var dropped = EditorGUILayout.ObjectField(AtoLocalizer.Tr("ui.whitelist.add"), null, typeof(UnityEngine.Object), true);
            if (dropped != null)
            {
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = dropped;
            }
        }
    }
}

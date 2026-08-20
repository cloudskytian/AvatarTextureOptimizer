using System;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Inspector for beginners with advanced controls kept folded. / 面向小白的检视面板，高级控制默认折叠。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private bool _advancedQuality;
        private bool _platforms;
        private bool _commonPlatform;
        private bool _pc;
        private bool _android;
        private bool _ios;
        private ATOLocalization _localization;

        private void OnEnable()
        {
            _localization = new ATOLocalization();
            _localization.Reload();
            AvatarTextureOptimizer component = (AvatarTextureOptimizer)target;
            component.EnsureQualityParameters();
        }

        public override void OnInspectorGUI()
        {
            AvatarTextureOptimizer component = (AvatarTextureOptimizer)target;
            serializedObject.Update();
            EditorGUILayout.LabelField(_localization.Get(component, "title", "Avatar Texture Optimizer"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_localization.Get(component, "help.safe",
                "Unsupported or animated texture transforms are skipped conservatively. / 无法确认或存在动画纹理变换时会保守跳过。"), MessageType.Info);

            ComponentValidation.Draw(component);
            DrawCore(component);
            DrawQuality(component);
            DrawAtlas(component);
            DrawPlatforms(component);
            DrawSafety(component);
            DrawTools(component);

            if (serializedObject.ApplyModifiedProperties())
            {
                component.EnsureQualityParameters();
                EditorUtility.SetDirty(component);
            }
        }

        private void DrawCore(AvatarTextureOptimizer component)
        {
            EditorGUILayout.LabelField(_localization.Get(component, "core", "Core / 核心"), EditorStyles.boldLabel);
            Property("generateAtlases", "Generate atlases / 生成图集");
            Property("optimizeTextures", "Optimize textures / 优化纹理");
            Property("optimizeMaterials", "Optimize materials / 优化材质");
            Property("scanAnimationReferences", "Scan animation references / 扫描动画引用");
            Property("enableSourceDeduplication", "Deduplicate source textures / 去重源纹理");
            Property("enableMaterialDeduplication", "Deduplicate materials / 去重材质");
        }

        private void DrawQuality(AvatarTextureOptimizer component)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(_localization.Get(component, "quality", "Quality / 质量"), EditorStyles.boldLabel);
            SerializedProperty preset = serializedObject.FindProperty("qualityPreset");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(preset, new GUIContent("Preset / 挡位"));
            bool presetChanged = EditorGUI.EndChangeCheck();
            if (presetChanged)
            {
                serializedObject.ApplyModifiedProperties();
                component.EnsureQualityParameters();
                EditorUtility.SetDirty(component);
                serializedObject.Update();
            }
            _advancedQuality = EditorGUILayout.Foldout(_advancedQuality,
                _localization.Get(component, "advanced", "Advanced quality parameters / 高级质量参数"), true);
            if (_advancedQuality)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("qualityParameters"),
                    new GUIContent("Metrics / 指标"), true);
                EditorGUILayout.HelpBox("Preset changes overwrite these values; Custom is not overwritten. / 切换挡位会覆盖这些值，自定义挡位不会被覆盖。", MessageType.None);
            }
            Property("pixelDensityPreset", "Pixel density preset / 像素密度挡位");
            Property("minimumPixelsPerMeter", "Minimum pixels per meter / 最小像素密度");
            Property("maximumPixelsPerMeter", "Maximum pixels per meter / 最大像素密度");
        }

        private void DrawAtlas(AvatarTextureOptimizer component)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Atlas / 图集", EditorStyles.boldLabel);
            Property("minimumPadding", "Minimum padding / 最小 padding");
            EditorGUILayout.LabelField("Raster granularity / 光栅粒度", "4 px (fixed / 固定)");
            Property("allowUVTranslationIntoUnitSquare", "Normalize safe out-of-range UV / 归一化可安全越界 UV");
            EditorGUILayout.HelpBox("UV seams, Repeat-dependent crossings, and conflicts fall back safely. / UV 跨缝、依赖 Repeat 的跨缝与冲突会安全回退。", MessageType.None);
        }

        private void DrawPlatforms(AvatarTextureOptimizer component)
        {
            EditorGUILayout.Space(4f);
            _platforms = EditorGUILayout.Foldout(_platforms, "Platform overrides / 平台覆盖", true);
            if (!_platforms) return;
            _commonPlatform = EditorGUILayout.Foldout(_commonPlatform, "Common defaults / 通用默认值", true);
            if (_commonPlatform) DrawPlatformOptions(serializedObject.FindProperty("commonOptions"));
            DrawOverride(serializedObject.FindProperty("pcOverride"), ref _pc, "PC");
            DrawOverride(serializedObject.FindProperty("androidOverride"), ref _android, "Android");
            DrawOverride(serializedObject.FindProperty("iosOverride"), ref _ios, "iOS");
        }

        private void DrawOverride(SerializedProperty property, ref bool foldout, string name)
        {
            if (property == null) return;
            SerializedProperty enabled = property.FindPropertyRelative("enabled");
            EditorGUILayout.PropertyField(enabled, new GUIContent(name + " override / " + name + " 覆盖"));
            if (!enabled.boolValue) return;
            foldout = EditorGUILayout.Foldout(foldout, name + " parameters / " + name + " 参数", true);
            if (foldout) DrawPlatformOptions(property.FindPropertyRelative("options"));
        }

        private void DrawPlatformOptions(SerializedProperty property)
        {
            if (property == null) return;
            EditorGUILayout.PropertyField(property.FindPropertyRelative("optimizeTextures"), new GUIContent("Optimize textures / 优化纹理"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("optimizeMaterials"), new GUIContent("Optimize materials / 优化材质"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("generateAtlases"), new GUIContent("Generate atlases / 生成图集"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("experimentalNpotAtlases"), new GUIContent("Experimental NPOT / 实验性 NPOT"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("enableMipStreaming"), new GUIContent("Mipmap + MipStreaming / Mipmap 与 MipStreaming"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("allowTextureFormatOverride"), new GUIContent("Allow format override / 允许格式覆盖"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("maxSourceTextureSize"), new GUIContent("Max source size / 源纹理最大尺寸"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("maxAtlasSize"), new GUIContent("Max atlas size / 图集最大尺寸"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("atlasMinimumSize"), new GUIContent("Atlas minimum size / 图集最小尺寸"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("transparentFormat"), new GUIContent("Transparent format / 透明格式"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("opaqueFormat"), new GUIContent("Opaque format / 不透明格式"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("normalFormat"), new GUIContent("Normal format / 法线格式"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("grayscaleFormat"), new GUIContent("Grayscale format / 灰度格式"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("fallbackFormat"), new GUIContent("Fallback format / 回退格式"));
        }

        private void DrawSafety(AvatarTextureOptimizer component)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Safety and diagnostics / 安全与诊断", EditorStyles.boldLabel);
            Property("whitelist", "Whitelist / 白名单", true);
            Property("localization", "Language / 语言");
            Property("showProgress", "Show build progress / 显示构建进度");
            Property("detailedLogging", "Detailed logs / 详细日志");
            Property("keepTemporaryAssetsOnCancel", "Keep temporary assets on cancel / 取消时保留临时资产");
        }

        private void DrawTools(AvatarTextureOptimizer component)
        {
            EditorGUILayout.Space(4f);
            if (GUILayout.Button("Apply current quality preset / 应用当前质量挡位"))
            {
                serializedObject.ApplyModifiedProperties();
                component.EnsureQualityParameters();
                EditorUtility.SetDirty(component);
                serializedObject.Update();
            }
            EditorGUILayout.HelpBox(_localization.Get(component, "warning.preview",
                "NDMF preview is intentionally not supported. / 暂不支持 NDMF 预览。"), MessageType.None);
        }

        private void Property(string name, string label, bool includeChildren = false)
        {
            SerializedProperty property = serializedObject.FindProperty(name);
            if (property != null) EditorGUILayout.PropertyField(property, new GUIContent(label), includeChildren);
        }

        private static class ComponentValidation
        {
            public static void Draw(AvatarTextureOptimizer component)
            {
                bool descriptor = component != null && component.gameObject.GetComponent("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") != null;
                if (!descriptor)
                {
                    EditorGUILayout.HelpBox("A VRCAvatarDescriptor must be on this object. / 此对象必须同时存在 VRCAvatarDescriptor。", MessageType.Error);
                }
                AvatarTextureOptimizer[] all = component == null ? new AvatarTextureOptimizer[0] :
                    component.transform.root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
                if (all.Length > 1)
                {
                    EditorGUILayout.HelpBox("Only one optimizer is allowed under an avatar. / 一个 Avatar 及其子级只能挂载一个优化器。", MessageType.Error);
                }
            }
        }

        [MenuItem("GameObject/Fosa/Add Avatar Texture Optimizer", false, 30)]
        private static void AddComponent()
        {
            if (Selection.activeGameObject == null) return;
            Undo.AddComponent<AvatarTextureOptimizer>(Selection.activeGameObject);
        }
    }
}

using Fosa.AvatarTextureOptimizer.Editor.Inspector;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    internal sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private bool _advanced;
        private ATOLanguage _language;
        private readonly bool[] _platformFoldouts = new bool[3];

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var languageProperty = serializedObject.FindProperty("language");
            EditorGUILayout.PropertyField(languageProperty, Label("field.language"));
            var language = (ATOLanguage)languageProperty.enumValueIndex; _language = language;
            EditorGUILayout.Space(); EditorGUILayout.LabelField(T(language, "component.title"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(T(language, "component.description"), MessageType.Info);
            DrawPlacementValidation(language);

            DrawSettings(serializedObject.FindProperty("common"), language, false);
            EditorGUILayout.Space(); EditorGUILayout.LabelField(T(language, "section.platform"), EditorStyles.boldLabel);
            DrawOverride(serializedObject.FindProperty("pc"), 0, language);
            DrawOverride(serializedObject.FindProperty("android"), 1, language);
            DrawOverride(serializedObject.FindProperty("ios"), 2, language);

            EditorGUILayout.Space(); EditorGUILayout.LabelField(T(language, "section.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("whitelist"), Label("field.whitelist"), true);
            _advanced = EditorGUILayout.Foldout(_advanced, T(language, "section.advanced"), true);
            if (_advanced)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("verboseLogging"), Label("field.verbose"));
                DrawDebugSettings(serializedObject.FindProperty("debug"));
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawSettings(SerializedProperty settings, ATOLanguage language, bool compact)
        {
            EditorGUILayout.LabelField(T(language, "section.quality"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("qualityPreset"), Label("field.qualityPreset"));
            var preset = (ATOQualityPreset)settings.FindPropertyRelative("qualityPreset").enumValueIndex;
            if (preset == ATOQualityPreset.Custom)
                DrawQualitySettings(settings.FindPropertyRelative("customQuality"), "field.customQuality", false);
            else if (_advanced)
                DrawQualitySettings(settings.FindPropertyRelative("quality"), "field.presetThresholds", true);

            EditorGUILayout.Space(); EditorGUILayout.LabelField(T(language, "section.atlas"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("generateAtlases"), Label("field.generateAtlases"));
            if (settings.FindPropertyRelative("generateAtlases").boolValue)
            {
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("maximumAtlasSize"), Label("field.maximumAtlasSize"));
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("minimumPadding"), Label("field.minimumPadding"));
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("experimentalNpot"), Label("field.experimentalNpot"));
            }
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("minimumPixelDensity"), Label("field.minimumDensity"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("maximumPixelDensity"), Label("field.maximumDensity"));

            EditorGUILayout.Space(); EditorGUILayout.LabelField(T(language, "section.output"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("deduplicateMaterials"), Label("field.deduplicateMaterials"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("deduplicateTexturesAndAtlases"), Label("field.deduplicateTextures"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("mergeSafeOpaqueMaterialSlots"), Label("field.mergeOpaqueSlots"));
            if (_advanced)
            {
                DrawTextureClassSettings(settings.FindPropertyRelative("opaque"), "field.opaque");
                DrawTextureClassSettings(settings.FindPropertyRelative("alpha"), "field.alpha");
                DrawTextureClassSettings(settings.FindPropertyRelative("normal"), "field.normal");
                DrawTextureClassSettings(settings.FindPropertyRelative("grayscale"), "field.grayscale");
            }
        }

        private void DrawQualitySettings(SerializedProperty property, string titleKey, bool readOnly)
        {
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, T(_language, titleKey), true);
            if (!property.isExpanded) return;
            using (new EditorGUI.DisabledScope(readOnly))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(property.FindPropertyRelative("targetQuality"), Label("field.targetQuality"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("minMsSsim"), Label("field.minMsSsim"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("minSsim"), Label("field.minSsim"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("maxDeltaE2000"), Label("field.maxDeltaE"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("minCutoutIoU"), Label("field.minCutoutIoU"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("maxBlendAlphaRmse"), Label("field.maxAlphaRmse"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("maxNormalMeanDegrees"), Label("field.maxNormalMean"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("maxNormalP95Degrees"), Label("field.maxNormalP95"));
                EditorGUILayout.PropertyField(property.FindPropertyRelative("maxGrayscaleRmse"), Label("field.maxGrayscaleRmse"));
                EditorGUI.indentLevel--;
            }
        }

        private void DrawTextureClassSettings(SerializedProperty property, string titleKey)
        {
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, T(_language, titleKey), true);
            if (!property.isExpanded) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(property.FindPropertyRelative("compression"), Label("field.compression"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("mipmapsAndStreaming"), Label("field.mipStreaming"));
            EditorGUI.indentLevel--;
        }

        private void DrawDebugSettings(SerializedProperty property)
        {
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, T(_language, "field.debugCategories"), true);
            if (!property.isExpanded) return;
            EditorGUI.indentLevel++;
            foreach (var name in new[] { "analysis", "uvIslands", "quality", "packing", "generatedAssets", "animationRewrite", "resourceLifetime" })
                EditorGUILayout.PropertyField(property.FindPropertyRelative(name), Label("debug." + name));
            EditorGUI.indentLevel--;
        }

        private void DrawOverride(SerializedProperty property, int index, ATOLanguage language)
        {
            var platform = property.FindPropertyRelative("platform");
            var enabled = property.FindPropertyRelative("enabled");
            EditorGUILayout.BeginHorizontal();
            _platformFoldouts[index] = EditorGUILayout.Foldout(_platformFoldouts[index], platform.enumDisplayNames[platform.enumValueIndex], true);
            EditorGUILayout.PropertyField(enabled, GUIContent.none, GUILayout.Width(20)); EditorGUILayout.EndHorizontal();
            if (_platformFoldouts[index] && enabled.boolValue)
            {
                EditorGUI.indentLevel++; DrawSettings(property.FindPropertyRelative("settings"), language, true); EditorGUI.indentLevel--;
            }
        }

        private void DrawPlacementValidation(ATOLanguage language)
        {
            var component = (AvatarTextureOptimizer)target;
            var descriptor = component.GetComponent<VRCAvatarDescriptor>();
            var descriptorInParent = component.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (descriptor == null || descriptorInParent == null || descriptorInParent.gameObject != component.gameObject)
                EditorGUILayout.HelpBox(T(language, "error.root"), MessageType.Error);
            else if (descriptor.GetComponentsInChildren<AvatarTextureOptimizer>(true).Length != 1)
                EditorGUILayout.HelpBox(T(language, "error.multiple"), MessageType.Error);
        }

        private GUIContent Label(string key) => new GUIContent(T(_language, key));
        private static string T(ATOLanguage language, string key) => ATOI18n.Get(language, key);
    }
}

using System;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Beginner-friendly inspector for the ATO component.
    /// 面向新手友好的 ATO 组件检视面板。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    internal sealed class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private SerializedProperty _enableOptimization;
        private SerializedProperty _generateAtlases;
        private SerializedProperty _deduplicateTextures;
        private SerializedProperty _deduplicateMaterials;
        private SerializedProperty _debugLogging;
        private SerializedProperty _language;
        private SerializedProperty _general;
        private SerializedProperty _quality;
        private SerializedProperty _textures;
        private SerializedProperty _platformOverrides;
        private SerializedProperty _whitelist;

        private void OnEnable()
        {
            _enableOptimization = serializedObject.FindProperty("_enableOptimization");
            _generateAtlases = serializedObject.FindProperty("_generateAtlases");
            _deduplicateTextures = serializedObject.FindProperty("_deduplicateTextures");
            _deduplicateMaterials = serializedObject.FindProperty("_deduplicateMaterials");
            _debugLogging = serializedObject.FindProperty("_debugLogging");
            _language = serializedObject.FindProperty("_language");
            _general = serializedObject.FindProperty("_general");
            _quality = serializedObject.FindProperty("_quality");
            _textures = serializedObject.FindProperty("_textures");
            _platformOverrides = serializedObject.FindProperty("_platformOverrides");
            _whitelist = serializedObject.FindProperty("_whitelist");
        }

        public override void OnInspectorGUI()
        {
            AtoLocalization.ApplyLanguageOverrideFromScene();
            serializedObject.Update();
            var component = (AvatarTextureOptimizer)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLocalization.Translate("Inspector:Title"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(AtoLocalization.Translate("Inspector:Subtitle"), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();

            var platformLabel = AtoReporting.GetCurrentBuildPlatformLabel();
            EditorGUILayout.HelpBox(AtoLocalization.TranslateFormat("Inspector:CurrentBuildPlatform", platformLabel), MessageType.Info);
            EditorGUILayout.HelpBox(AtoLocalization.Translate("Inspector:StatusReady"), MessageType.None);

            if (!AtoReflection.IsAvatarDescriptorRoot(component.gameObject))
            {
                EditorGUILayout.HelpBox(AtoLocalization.Translate("Inspector:NotOnRoot"), MessageType.Warning);
            }

            DrawLanguageSelector();
            EditorGUILayout.PropertyField(_enableOptimization, new GUIContent(AtoLocalization.Translate("Inspector:EnableOptimization")));
            EditorGUILayout.PropertyField(_generateAtlases, new GUIContent(AtoLocalization.Translate("Inspector:GenerateAtlases")));
            EditorGUILayout.PropertyField(_deduplicateTextures, new GUIContent(AtoLocalization.Translate("Inspector:DeduplicateTextures")));
            EditorGUILayout.PropertyField(_deduplicateMaterials, new GUIContent(AtoLocalization.Translate("Inspector:DeduplicateMaterials")));
            EditorGUILayout.PropertyField(_debugLogging, new GUIContent(AtoLocalization.Translate("Inspector:DebugLogging")));

            DrawGeneral();
            DrawQuality(component);
            DrawTexturePolicies();
            DrawPlatformOverrides();
            DrawWhitelist();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLanguageSelector()
        {
            var available = new[] { AtoLocalization.AutoToken }
                .Concat(AtoLocalization.GetAvailableLanguages())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var labels = available.Select(AtoLocalization.GetNativeLanguageName).ToArray();
            var currentIndex = Array.FindIndex(available, x => string.Equals(x, _language.stringValue, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            EditorGUI.BeginChangeCheck();
            var nextIndex = EditorGUILayout.Popup(AtoLocalization.Translate("Inspector:Language"), currentIndex, labels);
            if (EditorGUI.EndChangeCheck())
            {
                _language.stringValue = available[nextIndex];
                if (!string.Equals(available[nextIndex], AtoLocalization.AutoToken, StringComparison.OrdinalIgnoreCase))
                {
                    nadena.dev.ndmf.localization.LanguagePrefs.Language = available[nextIndex].ToLowerInvariant();
                }
            }
        }

        private void DrawGeneral()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLocalization.Translate("Inspector:General"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_minimumPixelDensity"), new GUIContent(AtoLocalization.Translate("Inspector:MinPixelDensity")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_maximumPixelDensity"), new GUIContent(AtoLocalization.Translate("Inspector:MaxPixelDensity")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_minimumPadding"), new GUIContent(AtoLocalization.Translate("Inspector:Padding")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_experimentalNpotAtlasSizes"), new GUIContent(AtoLocalization.Translate("Inspector:ExperimentalNpot")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_enableMipMapAndStreamingForColor"), new GUIContent(AtoLocalization.Translate("Inspector:MipColor")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_enableMipMapAndStreamingForNormal"), new GUIContent(AtoLocalization.Translate("Inspector:MipNormal")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_enableMipMapAndStreamingForMask"), new GUIContent(AtoLocalization.Translate("Inspector:MipMask")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_enableProgressBar"), new GUIContent(AtoLocalization.Translate("Inspector:ProgressBar")));
            EditorGUILayout.PropertyField(_general.FindPropertyRelative("_enableCancellation"), new GUIContent(AtoLocalization.Translate("Inspector:Cancelable")));
        }

        private void DrawQuality(AvatarTextureOptimizer component)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLocalization.Translate("Inspector:Quality"), EditorStyles.boldLabel);
            var preset = _quality.FindPropertyRelative("_preset");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(preset, new GUIContent(AtoLocalization.Translate("Inspector:Preset")));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                component.Quality.ApplyPresetIfNeeded();
                EditorUtility.SetDirty(component);
                serializedObject.Update();
            }

            EditorGUILayout.PropertyField(_quality.FindPropertyRelative("_showAdvanced"), new GUIContent(AtoLocalization.Translate("Inspector:ShowAdvanced")));
            if (_quality.FindPropertyRelative("_showAdvanced").boolValue)
            {
                var parameters = _quality.FindPropertyRelative("_parameters");
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_structuralSimilarity"), new GUIContent(AtoLocalization.Translate("Inspector:StructuralSimilarity")));
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_maxDeltaE2000"), new GUIContent(AtoLocalization.Translate("Inspector:DeltaE")));
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_alphaEdgeIou"), new GUIContent(AtoLocalization.Translate("Inspector:AlphaIou")));
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_alphaBlendRmse"), new GUIContent(AtoLocalization.Translate("Inspector:AlphaRmse")));
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_normalAngularErrorDegrees"), new GUIContent(AtoLocalization.Translate("Inspector:NormalAngular")));
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_normalP95AngularErrorDegrees"), new GUIContent(AtoLocalization.Translate("Inspector:NormalP95")));
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_grayscaleRmse"), new GUIContent(AtoLocalization.Translate("Inspector:GrayRmse")));
                EditorGUILayout.PropertyField(parameters.FindPropertyRelative("_globalTargetQuality"), new GUIContent(AtoLocalization.Translate("Inspector:GlobalQuality")));
                EditorGUI.indentLevel--;
            }
        }

        private void DrawTexturePolicies()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLocalization.Translate("Inspector:Textures"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_textures.FindPropertyRelative("_opaquePolicy"), new GUIContent(AtoLocalization.Translate("Inspector:OpaquePolicy")));
            EditorGUILayout.PropertyField(_textures.FindPropertyRelative("_transparentPolicy"), new GUIContent(AtoLocalization.Translate("Inspector:TransparentPolicy")));
            EditorGUILayout.PropertyField(_textures.FindPropertyRelative("_normalPolicy"), new GUIContent(AtoLocalization.Translate("Inspector:NormalPolicy")));
            EditorGUILayout.PropertyField(_textures.FindPropertyRelative("_grayscalePolicy"), new GUIContent(AtoLocalization.Translate("Inspector:GrayscalePolicy")));
        }

        private void DrawPlatformOverrides()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLocalization.Translate("Inspector:Platforms"), EditorStyles.boldLabel);
            DrawPlatformProfile(_platformOverrides.FindPropertyRelative("_common"), "Common");
            DrawPlatformProfile(_platformOverrides.FindPropertyRelative("_pc"), "PC");
            DrawPlatformProfile(_platformOverrides.FindPropertyRelative("_android"), "Android");
            DrawPlatformProfile(_platformOverrides.FindPropertyRelative("_ios"), "iOS");
        }

        private void DrawPlatformProfile(SerializedProperty profile, string label)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                var overrideEnabled = profile.FindPropertyRelative("_overrideEnabled");
                if (!string.Equals(label, "Common", StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.PropertyField(overrideEnabled, new GUIContent(AtoLocalization.Translate("Inspector:OverrideEnabled")));
                }
                else
                {
                    overrideEnabled.boolValue = false;
                }

                if (string.Equals(label, "Common", StringComparison.OrdinalIgnoreCase) || overrideEnabled.boolValue)
                {
                    EditorGUILayout.PropertyField(profile.FindPropertyRelative("_maxAtlasSize"), new GUIContent(AtoLocalization.Translate("Inspector:MaxAtlasSize")));
                    var settings = profile.FindPropertyRelative("_textureSettings");
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(settings.FindPropertyRelative("_opaquePolicy"), new GUIContent(AtoLocalization.Translate("Inspector:OpaquePolicy")));
                    EditorGUILayout.PropertyField(settings.FindPropertyRelative("_transparentPolicy"), new GUIContent(AtoLocalization.Translate("Inspector:TransparentPolicy")));
                    EditorGUILayout.PropertyField(settings.FindPropertyRelative("_normalPolicy"), new GUIContent(AtoLocalization.Translate("Inspector:NormalPolicy")));
                    EditorGUILayout.PropertyField(settings.FindPropertyRelative("_grayscalePolicy"), new GUIContent(AtoLocalization.Translate("Inspector:GrayscalePolicy")));
                    EditorGUI.indentLevel--;
                }
            }
        }

        private void DrawWhitelist()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLocalization.Translate("Inspector:Whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_whitelist, true);
        }
    }
}

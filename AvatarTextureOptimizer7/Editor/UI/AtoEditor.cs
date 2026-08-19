using Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AtoEditor : UnityEditor.Editor
    {
        SerializedProperty _generateAtlas, _npot, _minPadding;
        SerializedProperty _preset, _quality, _custom;
        SerializedProperty _minDens, _maxDens;
        SerializedProperty _dedupMat, _dedupTex, _whitelist;
        SerializedProperty _platform, _ovPc, _ovAnd, _ovIos;
        SerializedProperty _shared, _pc, _android, _ios;
        SerializedProperty _language, _verbose;

        AtoQualityPreset _lastPreset;
        bool _advQuality;
        bool _advFormats;

        void OnEnable()
        {
            _generateAtlas = serializedObject.FindProperty("generateAtlas");
            _npot = serializedObject.FindProperty("experimentalNpot");
            _minPadding = serializedObject.FindProperty("minPadding");
            _preset = serializedObject.FindProperty("qualityPreset");
            _quality = serializedObject.FindProperty("quality");
            _custom = serializedObject.FindProperty("customQuality");
            _minDens = serializedObject.FindProperty("minPixelDensity");
            _maxDens = serializedObject.FindProperty("maxPixelDensity");
            _dedupMat = serializedObject.FindProperty("deduplicateMaterials");
            _dedupTex = serializedObject.FindProperty("deduplicateTextures");
            _whitelist = serializedObject.FindProperty("whitelist");
            _platform = serializedObject.FindProperty("platform");
            _ovPc = serializedObject.FindProperty("overridePC");
            _ovAnd = serializedObject.FindProperty("overrideAndroid");
            _ovIos = serializedObject.FindProperty("overrideIOS");
            _shared = serializedObject.FindProperty("sharedPlatform");
            _pc = serializedObject.FindProperty("pcPlatform");
            _android = serializedObject.FindProperty("androidPlatform");
            _ios = serializedObject.FindProperty("iosPlatform");
            _language = serializedObject.FindProperty("language");
            _verbose = serializedObject.FindProperty("verboseLogging");
            var t = (AvatarTextureOptimizer)target;
            _lastPreset = t.qualityPreset;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var t = (AvatarTextureOptimizer)target;
            var lang = t.language;

            EditorGUILayout.HelpBox(AtoLoc.T(lang, "inspector.help"), MessageType.Info);

#if ATO_VRCSDK3_AVATARS
            if (t.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
            {
                EditorGUILayout.HelpBox(AtoLoc.T(lang, "error.noDescriptor:description"), MessageType.Error);
            }
#endif

            EditorGUILayout.PropertyField(_language, new GUIContent(AtoLoc.T(lang, "inspector.language")));
            EditorGUILayout.Space();

            EditorGUILayout.LabelField(AtoLoc.T(lang, "inspector.generateAtlas"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_generateAtlas, new GUIContent(AtoLoc.T(lang, "inspector.generateAtlas")));
            EditorGUILayout.HelpBox(AtoLoc.T(lang, "inspector.generateAtlas.help"), MessageType.None);
            EditorGUILayout.PropertyField(_npot, new GUIContent(AtoLoc.T(lang, "inspector.npot")));
            EditorGUILayout.HelpBox(AtoLoc.T(lang, "inspector.npot.help"), MessageType.None);
            EditorGUILayout.PropertyField(_minPadding, new GUIContent(AtoLoc.T(lang, "inspector.minPadding")));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLoc.T(lang, "inspector.qualityPreset"), EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_preset, new GUIContent(AtoLoc.T(lang, "inspector.qualityPreset")));
            if (EditorGUI.EndChangeCheck() || t.qualityPreset != _lastPreset)
            {
                _lastPreset = t.qualityPreset;
                serializedObject.ApplyModifiedProperties();
                t.ApplyPresetToQuality();
                serializedObject.Update();
            }

            _advQuality = EditorGUILayout.Foldout(_advQuality, AtoLoc.T(lang, "inspector.quality.advanced"), true);
            if (_advQuality)
            {
                EditorGUI.indentLevel++;
                var q = t.qualityPreset == AtoQualityPreset.Custom ? _custom : _quality;
                using (new EditorGUI.DisabledScope(t.qualityPreset != AtoQualityPreset.Custom))
                {
                    DrawQuality(q, lang);
                }

                if (t.qualityPreset != AtoQualityPreset.Custom)
                {
                    EditorGUILayout.HelpBox("Switch to Custom to edit these numbers. / 切到自定义才可改这些数值。", MessageType.None);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLoc.T(lang, "inspector.pixelDensity"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_minDens, new GUIContent(AtoLoc.T(lang, "inspector.minDensity")));
            EditorGUILayout.PropertyField(_maxDens, new GUIContent(AtoLoc.T(lang, "inspector.maxDensity")));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_dedupMat, new GUIContent(AtoLoc.T(lang, "inspector.dedupMat")));
            EditorGUILayout.PropertyField(_dedupTex, new GUIContent(AtoLoc.T(lang, "inspector.dedupTex")));

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_whitelist, new GUIContent(AtoLoc.T(lang, "inspector.whitelist")), true);
            EditorGUILayout.HelpBox(AtoLoc.T(lang, "inspector.whitelist.help"), MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(AtoLoc.T(lang, "inspector.platform"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_platform, new GUIContent(AtoLoc.T(lang, "inspector.platform")));
            EditorGUILayout.HelpBox(AtoLoc.T(lang, "inspector.platform.help"), MessageType.None);
            EditorGUILayout.PropertyField(_ovPc, new GUIContent(AtoLoc.T(lang, "inspector.overridePC")));
            EditorGUILayout.PropertyField(_ovAnd, new GUIContent(AtoLoc.T(lang, "inspector.overrideAndroid")));
            EditorGUILayout.PropertyField(_ovIos, new GUIContent(AtoLoc.T(lang, "inspector.overrideIOS")));

            _advFormats = EditorGUILayout.Foldout(_advFormats, AtoLoc.T(lang, "inspector.formats"), true);
            if (_advFormats)
            {
                EditorGUI.indentLevel++;
                DrawPlatform(_shared, lang, "Shared / 通用");
                if (_ovPc.boolValue) DrawPlatform(_pc, lang, "PC");
                if (_ovAnd.boolValue) DrawPlatform(_android, lang, "Android");
                if (_ovIos.boolValue) DrawPlatform(_ios, lang, "iOS");
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_verbose, new GUIContent(AtoLoc.T(lang, "inspector.verbose")));

            serializedObject.ApplyModifiedProperties();
        }

        static void DrawQuality(SerializedProperty q, AtoLanguageMode lang)
        {
            EditorGUILayout.PropertyField(q.FindPropertyRelative("msSsim"), new GUIContent(AtoLoc.T(lang, "inspector.quality.msSsim")));
            EditorGUILayout.PropertyField(q.FindPropertyRelative("deltaE"), new GUIContent(AtoLoc.T(lang, "inspector.quality.deltaE")));
            EditorGUILayout.PropertyField(q.FindPropertyRelative("alphaRmse"), new GUIContent(AtoLoc.T(lang, "inspector.quality.alphaRmse")));
            EditorGUILayout.PropertyField(q.FindPropertyRelative("cutoutIou"), new GUIContent(AtoLoc.T(lang, "inspector.quality.cutoutIou")));
            EditorGUILayout.PropertyField(q.FindPropertyRelative("normalMeanDegrees"), new GUIContent(AtoLoc.T(lang, "inspector.quality.normalMean")));
            EditorGUILayout.PropertyField(q.FindPropertyRelative("normalP95Degrees"), new GUIContent(AtoLoc.T(lang, "inspector.quality.normalP95")));
            EditorGUILayout.PropertyField(q.FindPropertyRelative("grayRmse"), new GUIContent(AtoLoc.T(lang, "inspector.quality.grayRmse")));
        }

        static void DrawPlatform(SerializedProperty p, AtoLanguageMode lang, string label)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            var f = p.FindPropertyRelative("formats");
            EditorGUILayout.PropertyField(f.FindPropertyRelative("opaqueFormat"), new GUIContent(AtoLoc.T(lang, "inspector.opaqueFormat")));
            EditorGUILayout.PropertyField(f.FindPropertyRelative("transparentFormat"), new GUIContent(AtoLoc.T(lang, "inspector.transparentFormat")));
            EditorGUILayout.PropertyField(f.FindPropertyRelative("normalFormat"), new GUIContent(AtoLoc.T(lang, "inspector.normalFormat")));
            EditorGUILayout.PropertyField(f.FindPropertyRelative("grayFormat"), new GUIContent(AtoLoc.T(lang, "inspector.grayFormat")));
            EditorGUILayout.PropertyField(f.FindPropertyRelative("enableMipStreaming"), new GUIContent(AtoLoc.T(lang, "inspector.mipStreaming")));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("experimentalNpot"), new GUIContent(AtoLoc.T(lang, "inspector.npot")));
        }
    }
}

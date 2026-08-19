// English: Beginner-friendly inspector. Advanced / platform blocks stay folded.
// 中文：面向小白的 Inspector。高级项与平台覆盖默认折叠。
using System.Linq;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class ATOInspector : UnityEditor.Editor
    {
        private SerializedProperty _preset;
        private SerializedProperty _quality;
        private SerializedProperty _dedupMat;
        private SerializedProperty _dedupTex;
        private SerializedProperty _whitelist;
        private SerializedProperty _shared;
        private SerializedProperty _hint;
        private SerializedProperty _ovPc, _pc;
        private SerializedProperty _ovAd, _ad;
        private SerializedProperty _ovIo, _io;
        private SerializedProperty _langMode, _lang;
        private SerializedProperty _verbose;
        private bool _advQuality = false;
        private bool _advShared = false;

        private void OnEnable()
        {
            _preset = serializedObject.FindProperty("qualityPreset");
            _quality = serializedObject.FindProperty("quality");
            _dedupMat = serializedObject.FindProperty("deduplicateMaterials");
            _dedupTex = serializedObject.FindProperty("deduplicateTextures");
            _whitelist = serializedObject.FindProperty("whitelist");
            _shared = serializedObject.FindProperty("shared");
            _hint = serializedObject.FindProperty("platformHint");
            _ovPc = serializedObject.FindProperty("overridePC");
            _pc = serializedObject.FindProperty("pc");
            _ovAd = serializedObject.FindProperty("overrideAndroid");
            _ad = serializedObject.FindProperty("android");
            _ovIo = serializedObject.FindProperty("overrideIOS");
            _io = serializedObject.FindProperty("ios");
            _langMode = serializedObject.FindProperty("languageMode");
            _lang = serializedObject.FindProperty("manualLanguage");
            _verbose = serializedObject.FindProperty("verboseLogging");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var comp = (AvatarTextureOptimizer)target;

            EditorGUILayout.HelpBox(ATOLoc.T("inspector.help"), MessageType.Info);

            if (!comp.HasAvatarDescriptor)
            {
                EditorGUILayout.HelpBox(ATOLoc.T("error.noDescriptor"), MessageType.Error);
            }

            var others = comp.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (others != null && others.Length > 1)
            {
                EditorGUILayout.HelpBox(ATOLoc.T("error.multiple"), MessageType.Error);
            }

            EditorGUILayout.LabelField(ATOLoc.T("inspector.quality"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOLoc.T("inspector.quality.hint"), MessageType.None);
            EditorGUILayout.PropertyField(_preset, new GUIContent("Preset"));

            _advQuality = EditorGUILayout.Foldout(_advQuality, ATOLoc.T("inspector.advancedQuality"), true);
            if (_advQuality)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_quality, true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLoc.T("inspector.dedup"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_dedupMat);
            EditorGUILayout.PropertyField(_dedupTex);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLoc.T("inspector.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOLoc.T("inspector.whitelist.hint"), MessageType.None);
            EditorGUILayout.PropertyField(_whitelist, true);

            EditorGUILayout.Space();
            _advShared = EditorGUILayout.Foldout(_advShared, ATOLoc.T("inspector.shared"), true);
            if (_advShared)
            {
                EditorGUI.indentLevel++;
                DrawPlatform(_shared);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLoc.T("inspector.platform"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ATOLoc.T("inspector.platform.hint"), MessageType.None);
            EditorGUILayout.PropertyField(_hint, new GUIContent("Platform"));
            EditorGUILayout.PropertyField(_ovPc, new GUIContent("Override PC"));
            if (_ovPc.boolValue) DrawPlatform(_pc);
            EditorGUILayout.PropertyField(_ovAd, new GUIContent("Override Android"));
            if (_ovAd.boolValue) DrawPlatform(_ad);
            EditorGUILayout.PropertyField(_ovIo, new GUIContent("Override iOS"));
            if (_ovIo.boolValue) DrawPlatform(_io);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLoc.T("inspector.language"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_langMode);
            if (comp.languageMode == ATOLanguageMode.Manual)
            {
                var langs = ATOLoc.AvailableLanguages.OrderBy(x => x).ToArray();
                if (langs.Length == 0) langs = new[] { "en-us", "zh-hans" };
                var idx = System.Array.IndexOf(langs, comp.manualLanguage);
                if (idx < 0) idx = 0;
                var ni = EditorGUILayout.Popup("Language", idx, langs);
                _lang.stringValue = langs[Mathf.Clamp(ni, 0, langs.Length - 1)];
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ATOLoc.T("inspector.debug"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_verbose);

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawPlatform(SerializedProperty p)
        {
            if (p == null) return;
            EditorGUILayout.PropertyField(p.FindPropertyRelative("generateAtlases"),
                new GUIContent(ATOLoc.T("inspector.generateAtlases")));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("experimentalNpot"),
                new GUIContent(ATOLoc.T("inspector.npot")));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("minPadding"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("minPixelDensity"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("maxPixelDensity"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("maxAtlasEdgeOverride"));
            EditorGUILayout.PropertyField(p.FindPropertyRelative("compression"), true);
            EditorGUILayout.PropertyField(p.FindPropertyRelative("mipStreaming"), true);
        }
    }
}

using System;
using UnityEditor;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Beginner-friendly inspector with an Advanced foldout for power users.
    /// 面向小白的检查器，高级选项默认折叠。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class AtoEditor : UnityEditor.Editor
    {
        SerializedProperty generateAtlas, qualityPreset, quality;
        SerializedProperty experimentalNpot, minPadding;
        SerializedProperty minPixelDensity, maxPixelDensity;
        SerializedProperty dedupMaterials, dedupTextures, whitelist;
        SerializedProperty formats, platform;
        SerializedProperty pcOverride, androidOverride, iosOverride;
        SerializedProperty languageMode, languageCode, verboseLog;

        bool _adv, _fmt, _pc, _and, _ios, _lang;
        static bool _help = true;

        void OnEnable()
        {
            generateAtlas = serializedObject.FindProperty("generateAtlas");
            qualityPreset = serializedObject.FindProperty("qualityPreset");
            quality = serializedObject.FindProperty("quality");
            experimentalNpot = serializedObject.FindProperty("experimentalNpot");
            minPadding = serializedObject.FindProperty("minPadding");
            minPixelDensity = serializedObject.FindProperty("minPixelDensity");
            maxPixelDensity = serializedObject.FindProperty("maxPixelDensity");
            dedupMaterials = serializedObject.FindProperty("dedupMaterials");
            dedupTextures = serializedObject.FindProperty("dedupTextures");
            whitelist = serializedObject.FindProperty("whitelist");
            formats = serializedObject.FindProperty("formats");
            platform = serializedObject.FindProperty("platform");
            pcOverride = serializedObject.FindProperty("pcOverride");
            androidOverride = serializedObject.FindProperty("androidOverride");
            iosOverride = serializedObject.FindProperty("iosOverride");
            languageMode = serializedObject.FindProperty("languageMode");
            languageCode = serializedObject.FindProperty("languageCode");
            verboseLog = serializedObject.FindProperty("verboseLog");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var t = (AvatarTextureOptimizer)target;

            if (t.languageMode == AtoLanguageMode.Manual)
                AtoLoc.SetOverride(t.languageCode);
            else
                AtoLoc.SetOverride(null);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                _help = EditorGUILayout.Foldout(_help, AtoLoc.T("ato.ui.what"), true);
                if (_help)
                {
                    EditorGUILayout.HelpBox(AtoLoc.T("ato.ui.help"), MessageType.Info);
                    if (!t.HasAvatarDescriptor())
                        EditorGUILayout.HelpBox(AtoLoc.T("ato.ui.needDescriptor"), MessageType.Error);
                    var others = t.GetComponentsInChildren<AvatarTextureOptimizer>(true);
                    if (others.Length > 1)
                        EditorGUILayout.HelpBox(AtoLoc.T("ato.ui.onlyOne"), MessageType.Error);
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(AtoLoc.T("ato.ui.basic"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(generateAtlas, new GUIContent(AtoLoc.T("ato.ui.generateAtlas"), AtoLoc.T("ato.ui.generateAtlas.tip")));
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(qualityPreset, new GUIContent(AtoLoc.T("ato.ui.preset"), AtoLoc.T("ato.ui.preset.tip")));
            if (EditorGUI.EndChangeCheck() && (AtoQualityPreset)qualityPreset.enumValueIndex != AtoQualityPreset.Custom)
            {
                ApplyPreset((AtoQualityPreset)qualityPreset.enumValueIndex, quality);
            }

            EditorGUILayout.PropertyField(whitelist, new GUIContent(AtoLoc.T("ato.ui.whitelist"), AtoLoc.T("ato.ui.whitelist.tip")), true);

            EditorGUILayout.Space(6);
            _adv = EditorGUILayout.Foldout(_adv, AtoLoc.T("ato.ui.advanced"), true);
            if (_adv)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawQuality(quality);
                    EditorGUILayout.PropertyField(experimentalNpot, new GUIContent(AtoLoc.T("ato.ui.npot"), AtoLoc.T("ato.ui.npot.tip")));
                    EditorGUILayout.PropertyField(minPadding, new GUIContent(AtoLoc.T("ato.ui.padding")));
                    EditorGUILayout.PropertyField(minPixelDensity, new GUIContent(AtoLoc.T("ato.ui.minDensity")));
                    EditorGUILayout.PropertyField(maxPixelDensity, new GUIContent(AtoLoc.T("ato.ui.maxDensity")));
                    EditorGUILayout.PropertyField(dedupMaterials, new GUIContent(AtoLoc.T("ato.ui.dedupMat")));
                    EditorGUILayout.PropertyField(dedupTextures, new GUIContent(AtoLoc.T("ato.ui.dedupTex")));
                    EditorGUILayout.PropertyField(verboseLog, new GUIContent(AtoLoc.T("ato.ui.verbose")));

                    EditorGUILayout.Space(4);
                    _fmt = EditorGUILayout.Foldout(_fmt, AtoLoc.T("ato.ui.formats"), true);
                    if (_fmt)
                    {
                        using (new EditorGUI.IndentLevelScope())
                            DrawFormats(formats, AtoFormats.CurrentEditorPlatform(t.platform));
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.PropertyField(platform, new GUIContent(AtoLoc.T("ato.ui.platform")));
                    DrawOverride(ref _pc, pcOverride, "PC");
                    DrawOverride(ref _and, androidOverride, "Android");
                    DrawOverride(ref _ios, iosOverride, "iOS");

                    EditorGUILayout.Space(4);
                    _lang = EditorGUILayout.Foldout(_lang, AtoLoc.T("ato.ui.language"), true);
                    if (_lang)
                    {
                        EditorGUILayout.PropertyField(languageMode, new GUIContent("Mode"));
                        if (t.languageMode == AtoLanguageMode.Manual)
                        {
                            var codes = AtoLoc.AvailableCodes;
                            var cur = t.languageCode ?? "en-US";
                            int idx = 0;
                            for (int i = 0; i < codes.Count; i++)
                                if (string.Equals(codes[i], cur, StringComparison.OrdinalIgnoreCase)) idx = i;
                            var labels = new string[codes.Count];
                            for (int i = 0; i < codes.Count; i++) labels[i] = codes[i];
                            var n = EditorGUILayout.Popup("Language", idx, labels);
                            if (n >= 0 && n < codes.Count)
                                languageCode.stringValue = codes[n];
                            EditorGUILayout.HelpBox(AtoLoc.T("ato.ui.language.tip"), MessageType.None);
                        }
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        void DrawOverride(ref bool fold, SerializedProperty p, string label)
        {
            var en = p.FindPropertyRelative("enabled");
            fold = EditorGUILayout.Foldout(fold, label + " override", true);
            if (!fold) return;
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(en, new GUIContent("Enable " + label + " override"));
                if (!en.boolValue) return;
                EditorGUILayout.PropertyField(p.FindPropertyRelative("qualityPreset"));
                DrawQuality(p.FindPropertyRelative("quality"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("generateAtlas"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("experimentalNpot"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("minPadding"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("minPixelDensity"));
                EditorGUILayout.PropertyField(p.FindPropertyRelative("maxPixelDensity"));
                var plat = label == "Android" ? AtoBuildPlatform.Android
                    : label == "iOS" ? AtoBuildPlatform.iOS
                    : AtoBuildPlatform.PC;
                DrawFormats(p.FindPropertyRelative("formats"), plat);
            }
        }

        void DrawQuality(SerializedProperty q)
        {
            if (q == null) return;
            EditorGUILayout.LabelField(AtoLoc.T("ato.ui.qualityParams"), EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(q.FindPropertyRelative("msSsim"), new GUIContent("MS-SSIM"));
                EditorGUILayout.PropertyField(q.FindPropertyRelative("deltaE00Mean"), new GUIContent("ΔE00 mean"));
                EditorGUILayout.PropertyField(q.FindPropertyRelative("deltaE00P95"), new GUIContent("ΔE00 p95"));
                EditorGUILayout.PropertyField(q.FindPropertyRelative("normalAngleMeanDeg"), new GUIContent("Normal ° mean"));
                EditorGUILayout.PropertyField(q.FindPropertyRelative("normalAngleP95Deg"), new GUIContent("Normal ° p95"));
                EditorGUILayout.PropertyField(q.FindPropertyRelative("alphaIou"), new GUIContent("Cutout IoU"));
                EditorGUILayout.PropertyField(q.FindPropertyRelative("alphaRmse"), new GUIContent("Blend α RMSE"));
                EditorGUILayout.PropertyField(q.FindPropertyRelative("grayRmse"), new GUIContent("Gray RMSE"));
            }
        }

        void DrawFormats(SerializedProperty f, AtoBuildPlatform plat)
        {
            if (f == null) return;
            DrawClass(f.FindPropertyRelative("opaque"), "Opaque", plat, AtoTextureClass.Opaque);
            DrawClass(f.FindPropertyRelative("transparent"), "Transparent", plat, AtoTextureClass.Transparent);
            DrawClass(f.FindPropertyRelative("normal"), "Normal", plat, AtoTextureClass.Normal);
            DrawClass(f.FindPropertyRelative("gray"), "Gray", plat, AtoTextureClass.Gray);
        }

        void DrawClass(SerializedProperty c, string label, AtoBuildPlatform plat, AtoTextureClass cls)
        {
            if (c == null) return;
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                var fmt = c.FindPropertyRelative("format");
                EditorGUILayout.PropertyField(fmt, new GUIContent("Format"));
                var v = (AtoSafeFormat)fmt.enumValueIndex;
                if (!AtoFormats.Allowed(v, plat, cls, false) && v != AtoSafeFormat.Auto)
                    EditorGUILayout.HelpBox(AtoLoc.T("ato.ui.formatIllegal"), MessageType.Warning);
                EditorGUILayout.PropertyField(c.FindPropertyRelative("mipAndStreaming"),
                    new GUIContent("Mip + MipStreaming"));
            }
        }

        static void ApplyPreset(AtoQualityPreset p, SerializedProperty q)
        {
            var s = AtoQualitySettings.ForPreset(p);
            q.FindPropertyRelative("msSsim").floatValue = s.msSsim;
            q.FindPropertyRelative("deltaE00Mean").floatValue = s.deltaE00Mean;
            q.FindPropertyRelative("deltaE00P95").floatValue = s.deltaE00P95;
            q.FindPropertyRelative("normalAngleMeanDeg").floatValue = s.normalAngleMeanDeg;
            q.FindPropertyRelative("normalAngleP95Deg").floatValue = s.normalAngleP95Deg;
            q.FindPropertyRelative("alphaIou").floatValue = s.alphaIou;
            q.FindPropertyRelative("alphaRmse").floatValue = s.alphaRmse;
            q.FindPropertyRelative("grayRmse").floatValue = s.grayRmse;
            q.FindPropertyRelative("_forceLossless").boolValue = s._forceLossless;
        }
    }

    public static class AtoMenu
    {
        [MenuItem("GameObject/FOSA/Avatar Texture Optimizer", false, 49)]
        static void Add(MenuCommand cmd)
        {
            var go = cmd.context as GameObject ?? Selection.activeGameObject;
            if (go == null) return;
            Undo.AddComponent<AvatarTextureOptimizer>(go);
        }

        [MenuItem("CONTEXT/VRCAvatarDescriptor/Add Avatar Texture Optimizer")]
        static void AddCtx(MenuCommand cmd)
        {
            var c = cmd.context as Component;
            if (c == null) return;
            if (c.GetComponent<AvatarTextureOptimizer>() == null)
                Undo.AddComponent<AvatarTextureOptimizer>(c.gameObject);
        }
    }
}

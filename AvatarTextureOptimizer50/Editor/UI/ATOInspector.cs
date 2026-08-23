// -----------------------------------------------------------------------------
// ATOInspector.cs — component inspector (i18n, preset linkage, platform overrides).
// ATOInspector.cs —— 组件检视面板（i18n、挡位联动、平台覆盖）。
// -----------------------------------------------------------------------------

using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    [CustomEditor(typeof(net.fosa.ato.AvatarTextureOptimizer))]
    internal sealed class ATOInspector : UnityEditor.Editor
    {
        private bool _advQuality;
        private bool _advAtlas;
        private bool _advPlatform;
        private bool _advDebug;
        private bool _whitelistOpen = true;

        private static net.fosa.ato.AvatarTextureOptimizer Target =>
            (net.fosa.ato.AvatarTextureOptimizer)serializedObject.targetObject;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var t = Target;

            // ---- language selector / 语言选择 ----
            EditorGUILayout.BeginHorizontal();
            var langs = ATOLocalization.AvailableLanguages();
            var options = new[] { "Auto" }.Concat(langs).ToArray();
            int sel = 0;
            for (int i = 0; i < langs.Count; i++)
                if (langs[i] == t.language) sel = i + 1;
            int next = EditorGUILayout.Popup(ATOLocalization.L("UI:Language"), sel, options);
            if (next != sel)
            {
                t.language = next == 0 ? "auto" : langs[next - 1];
                EditorUtility.SetDirty(t);
                ATOLocalization.LanguageOverride = t.language;
            }

            EditorGUILayout.EndHorizontal();

            // ---- mount hint / 挂载提示 ----
#if ATO_VRCSDK_AVATARS
            var descriptor = t.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                EditorGUILayout.HelpBox(ATOLocalization.L("UI:NoDescriptor"), MessageType.Error);
            else
            {
                var extra = t.GetComponentsInChildren<net.fosa.ato.AvatarTextureOptimizer>(true);
                if (extra.Length > 1)
                    EditorGUILayout.HelpBox(ATOLocalization.L("UI:MultipleComponents"), MessageType.Error);
            }
#endif

            // ---- basic / 基础 ----
            DrawToggle(nameof(t.generateAtlas), "UI:GenerateAtlas");
            DrawToggle(nameof(t.dedupMaterials), "UI:DedupMaterials");
            DrawToggle(nameof(t.dedupTextures), "UI:DedupTextures");

            // ---- quality preset / 质量挡位 ----
            EditorGUILayout.Space(4);
            var presetProp = serializedObject.FindProperty(nameof(t.qualityPreset));
            var presets = new[] { "NearLossless", "High", "Medium", "Aggressive", "Custom" };
            int p = EditorGUILayout.Popup(ATOLocalization.L("UI:QualityPreset"),
                presetProp.enumValueIndex, presets.Select(ATOLocalization.L).ToArray());
            if (p != presetProp.enumValueIndex)
            {
                presetProp.enumValueIndex = p;
                var presetVal = (net.fosa.ato.ATOQualityPreset)p;
                if (presetVal == net.fosa.ato.ATOQualityPreset.Custom && t.quality == null)
                    t.quality = ATOPresets.CustomDefaults();
                else
                    ATOPresets.Apply(presetVal, t.quality);
                EditorUtility.SetDirty(t);
            }

            // ---- advanced quality / 高级质量 ----
            _advQuality = EditorGUILayout.Foldout(_advQuality, ATOLocalization.L("UI:AdvancedQuality"), true);
            if (_advQuality)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawQualityParams(t.quality);
                    DrawIntSlider(nameof(t.minPixelDensity), "UI:MinDensity", 512, 8192,
                        ATOPresets.DensitySteps);
                    DrawIntSlider(nameof(t.maxPixelDensity), "UI:MaxDensity", 512, 8192,
                        ATOPresets.DensitySteps);
                }
            }

            // ---- advanced atlas / 高级图集 ----
            _advAtlas = EditorGUILayout.Foldout(_advAtlas, ATOLocalization.L("UI:AdvancedAtlas"), true);
            if (_advAtlas)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawPadding(nameof(t.minPadding));
                    DrawToggle(nameof(t.npotAtlases), "UI:Npot");
                    DrawMip("mips.albedo", "UI:MipAlbedo");
                    DrawMip("mips.normalMap", "UI:MipNormal");
                    DrawMip("mips.grayMask", "UI:MipGray");
                }
            }

            // ---- whitelist / 白名单 ----
            _whitelistOpen = EditorGUILayout.Foldout(_whitelistOpen, ATOLocalization.L("UI:Whitelist"), true);
            if (_whitelistOpen)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    var wl = serializedObject.FindProperty(nameof(t.whitelist));
                    EditorGUILayout.PropertyField(wl, new GUIContent(ATOLocalization.L("UI:Whitelist")), true);
                }
            }

            // ---- platform overrides / 平台覆盖 ----
            _advPlatform = EditorGUILayout.Foldout(_advPlatform, ATOLocalization.L("UI:PlatformOverrides"), true);
            if (_advPlatform)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawPlatform(nameof(t.pcOverride), "UI:PlatformPC");
                    DrawPlatform(nameof(t.androidOverride), "UI:PlatformAndroid");
                    DrawPlatform(nameof(t.iosOverride), "UI:PlatformIOS");
                }
            }

            // ---- debug / 调试 ----
            _advDebug = EditorGUILayout.Foldout(_advDebug, ATOLocalization.L("UI:Debug"), true);
            if (_advDebug)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawPopup(nameof(t.logLevel), "UI:LogLevel", new[] { "Error", "Warning", "Info", "Debug", "Trace" });
                    DrawToggle(nameof(t.logReportToConsole), "UI:LogReport");
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ================================================================= //

        private void DrawToggle(string prop, string key)
        {
            var p = serializedObject.FindProperty(prop);
            EditorGUILayout.PropertyField(p, new GUIContent(ATOLocalization.L(key)));
        }

        private void DrawPopup(string prop, string key, string[] options)
        {
            var p = serializedObject.FindProperty(prop);
            p.intValue = EditorGUILayout.Popup(ATOLocalization.L(key), p.intValue,
                options.Select(ATOLocalization.L).ToArray());
        }

        private void DrawIntSlider(string prop, string key, int min, int max, int[] snap)
        {
            var p = serializedObject.FindProperty(prop);
            int v = EditorGUILayout.IntSlider(ATOLocalization.L(key), p.intValue, min, max);
            if (snap != null && Event.current.type == EventType.Used)
            {
                // snap on release / 松开时吸附
            }

            p.intValue = v;
        }

        private void DrawPadding(string prop)
        {
            var p = serializedObject.FindProperty(prop);
            float[] opts = { 4, 8, 16, 32, 64 };
            int idx = System.Array.IndexOf(opts, p.intValue);
            if (idx < 0) idx = 0;
            idx = EditorGUILayout.Popup(ATOLocalization.L("UI:MinPadding"),
                idx, opts.Select(o => o + "px").ToArray());
            p.intValue = (int)opts[idx];
        }

        private void DrawQualityParams(net.fosa.ato.ATOQualityParams q)
        {
            if (q == null) return;
            q.msSsim = EditorGUILayout.Slider(ATOLocalization.L("Q:msSsim"), q.msSsim, 0.5f, 1f);
            q.deltaE = EditorGUILayout.Slider(ATOLocalization.L("Q:deltaE"), q.deltaE, 0f, 10f);
            q.alphaIou = EditorGUILayout.Slider(ATOLocalization.L("Q:alphaIou"), q.alphaIou, 0.9f, 1f);
            q.alphaRmse = EditorGUILayout.Slider(ATOLocalization.L("Q:alphaRmse"), q.alphaRmse, 0f, 32f);
            q.normalAngleMean = EditorGUILayout.Slider(ATOLocalization.L("Q:normalMean"), q.normalAngleMean, 0f, 10f);
            q.normalAngleP95 = EditorGUILayout.Slider(ATOLocalization.L("Q:normalP95"), q.normalAngleP95, 0f, 20f);
            q.grayRmse = EditorGUILayout.Slider(ATOLocalization.L("Q:grayRmse"), q.grayRmse, 0f, 32f);
            if (GUI.changed) EditorUtility.SetDirty(Target);
        }

        private void DrawMip(string propPath, string key)
        {
            var p = serializedObject.FindProperty(propPath);
            EditorGUILayout.PropertyField(p, new GUIContent(ATOLocalization.L(key)));
        }

        private void DrawPlatform(string prop, string key)
        {
            var ovProp = serializedObject.FindProperty(prop);
            var enabledProp = ovProp.FindPropertyRelative("enabled");

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(ATOLocalization.L(key), GUILayout.Width(140));
            enabledProp.boolValue = EditorGUILayout.Toggle(enabledProp.boolValue, GUILayout.Width(20));
            EditorGUILayout.EndHorizontal();

            if (enabledProp.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    var maxProp = ovProp.FindPropertyRelative("maxAtlasSize");
                    maxProp.intValue = EditorGUILayout.IntSlider(
                        ATOLocalization.L("UI:MaxAtlasSize"), maxProp.intValue, 64, 8192);

                    var fmt = ovProp.FindPropertyRelative("formats");
                    DrawFormat(fmt, "albedoOpaque", "UI:FmtOpaque");
                    DrawFormat(fmt, "albedoAlpha", "UI:FmtAlpha");
                    DrawFormat(fmt, "normalMap", "UI:FmtNormal");
                    DrawFormat(fmt, "grayMask", "UI:FmtGray");
                }
            }
        }

        private static readonly string[] FormatNames =
        {
            "Auto", "DXT1", "DXT5", "BC7", "DXT1 Crunched", "DXT5 Crunched", "BC5",
            "ASTC 4x4", "ASTC 5x5", "ASTC 6x6", "ASTC 8x8", "ETC2 RGB", "ETC2 RGBA8",
            "ETC2 RGBA8 Crunched", "PVRTC 4RGB", "PVRTC 4RGBA", "RGBA32",
        };

        private void DrawFormat(SerializedProperty fmtSet, string field, string key)
        {
            var p = fmtSet.FindPropertyRelative(field);
            p.intValue = EditorGUILayout.Popup(ATOLocalization.L(key), p.intValue, FormatNames);
        }
    }
}

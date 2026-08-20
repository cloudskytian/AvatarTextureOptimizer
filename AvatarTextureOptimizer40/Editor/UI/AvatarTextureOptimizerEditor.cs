using Fosa.Ato.Editor.i18n;
using Fosa.Ato.Runtime;
using UnityEditor;
using UnityEngine;

namespace Fosa.Ato.Editor.UI
{
    /// <summary>
    /// Custom inspector. Defaults assume the user is a novice (safe defaults, clear toggles); advanced
    /// parameters and platform overrides are folded away. All strings go through i18n.
    /// 自定义 Inspector。默认面向小白（安全默认、清晰开关）；高级参数与平台覆盖折叠。所有文案走 i18n。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    [CanEditMultipleObjects]
    public class AvatarTextureOptimizerEditor : UnityEditor.Editor
    {
        private static bool _adv;
        private static bool _pc, _android, _ios;
        private static Vector2 _scroll;

        public override void OnInspectorGUI()
        {
            var t = (AvatarTextureOptimizer)target;
            t.Settings ??= new AtoSettings();
            var s = t.Settings;

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(Localizer.T("ato.name"), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                LanguageDropdown();
            }

            if (!t.IsValidRoot)
                EditorGUILayout.HelpBox(Localizer.T("err.noDescriptor"), MessageType.Error);

            s.Enabled = EditorGUILayout.Toggle(Localizer.Gui("ato.enabled", "ato.enabled.tip"), s.Enabled);
            using (new EditorGUI.DisabledScope(!s.Enabled))
            {
                EditorGUILayout.Space(4);
                DrawQuality(s);
                DrawAtlas(s);
                DrawDedup(s);
                DrawWhitelist(t);
                DrawPlatforms(s);
                DrawAdvanced(s);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static void LanguageDropdown()
        {
            var langs = Localizer.AvailableLanguages;
            var current = Localizer.CurrentLanguage;
            int idx = 0;
            var options = new string[langs.Count + 1];
            options[0] = "Auto";
            for (int i = 0; i < langs.Count; i++)
            {
                options[i + 1] = langs[i];
                if (langs[i] == current) idx = i + 1;
            }
            int pick = EditorGUILayout.Popup(idx, options, GUILayout.Width(90));
            Localizer.CurrentLanguage = pick == 0 ? "auto" : langs[pick - 1];
        }

        private void DrawQuality(AtoSettings s)
        {
            EditorGUILayout.LabelField(Localizer.T("ato.quality"), EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var preset = (QualityPreset)EditorGUILayout.EnumPopup(
                Localizer.Gui("ato.preset", "ato.preset.tip"), s.Preset);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "ATO preset");
                if (preset != QualityPreset.Custom) s.ApplyPreset(preset);
                else s.Preset = QualityPreset.Custom;
            }

            EditorGUILayout.LabelField(Localizer.T("ato.density"), EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                s.MinPixelDensity = EditorGUILayout.IntField(Localizer.T("ato.minDensity"),
                    Mathf.Clamp(s.MinPixelDensity, 64, 16384));
                s.MaxPixelDensity = EditorGUILayout.IntField(Localizer.T("ato.maxDensity"),
                    Mathf.Clamp(s.MaxPixelDensity, 64, 16384));
            }
        }

        private void DrawAtlas(AtoSettings s)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localizer.T("ato.atlas"), EditorStyles.boldLabel);
            s.GenerateAtlas = EditorGUILayout.Toggle(
                Localizer.Gui("ato.generateAtlas", "ato.generateAtlas.tip"), s.GenerateAtlas);
            using (new EditorGUI.DisabledScope(!s.GenerateAtlas))
            {
                s.MaxAtlasSizePC = EditorGUILayout.IntPopup(Localizer.T("ato.maxAtlas") + " (PC)",
                    s.MaxAtlasSizePC, new[] { "2048", "4096", "8192" }, new[] { 2048, 4096, 8192 });
                s.MaxAtlasSizeMobile = EditorGUILayout.IntPopup(Localizer.T("ato.maxAtlas") + " (Mobile)",
                    s.MaxAtlasSizeMobile, new[] { "1024", "2048", "4096" }, new[] { 1024, 2048, 4096 });
                s.ExperimentalNpot = EditorGUILayout.Toggle(
                    Localizer.Gui("ato.npot", "ato.npot.tip"), s.ExperimentalNpot);
                s.MinPadding = (PaddingMode)EditorGUILayout.EnumPopup(Localizer.T("ato.padding"), s.MinPadding);
            }
        }

        private void DrawDedup(AtoSettings s)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localizer.T("ato.dedup"), EditorStyles.boldLabel);
            s.DeduplicateMaterials = EditorGUILayout.Toggle(Localizer.T("ato.dedupMaterials"), s.DeduplicateMaterials);
            s.DeduplicateTextures = EditorGUILayout.Toggle(Localizer.T("ato.dedupTextures"), s.DeduplicateTextures);
            s.MergeOpaqueSlots = EditorGUILayout.Toggle(Localizer.T("ato.mergeOpaque"), s.MergeOpaqueSlots);
            s.DefaultMipStreaming = EditorGUILayout.Toggle(Localizer.T("ato.mipstream"), s.DefaultMipStreaming);
        }

        private void DrawWhitelist(AvatarTextureOptimizer t)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localizer.T("ato.whitelist"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Localizer.T("ato.whitelist.tip"), MessageType.Info);
            var so = new SerializedObject(t);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AvatarTextureOptimizer.Whitelist)), true);
            so.ApplyModifiedProperties();
        }

        private void DrawPlatforms(AtoSettings s)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localizer.T("ato.platform"), EditorStyles.boldLabel);
            DrawPlatform(s.OverridePC, ref _pc, Localizer.T("ato.platform.pc"));
            DrawPlatform(s.OverrideAndroid, ref _android, Localizer.T("ato.platform.android"));
            DrawPlatform(s.OverrideIOS, ref _ios, Localizer.T("ato.platform.ios"));
        }

        private void DrawPlatform(PlatformOverride ov, ref bool fold, string label)
        {
            fold = EditorGUILayout.Foldout(fold, label, true);
            if (!fold) return;
            using (new EditorGUI.IndentLevelScope())
            {
                ov.Enabled = EditorGUILayout.Toggle("Enabled / 启用", ov.Enabled);
                using (new EditorGUI.DisabledScope(!ov.Enabled))
                {
                    ov.MaxAtlasSize = EditorGUILayout.IntPopup(Localizer.T("ato.maxAtlas"),
                        ov.MaxAtlasSize, new[] { "1024", "2048", "4096", "8192" }, new[] { 1024, 2048, 4096, 8192 });
                    ov.ExperimentalNpot = EditorGUILayout.Toggle(Localizer.T("ato.npot"), ov.ExperimentalNpot);
                }
            }
        }

        private void DrawAdvanced(AtoSettings s)
        {
            EditorGUILayout.Space(4);
            _adv = EditorGUILayout.Foldout(_adv, Localizer.T("ato.advanced"), true);
            if (!_adv) return;
            using (new EditorGUI.IndentLevelScope())
            {
                s.VerboseLogging = EditorGUILayout.Toggle(Localizer.T("ato.verbose"), s.VerboseLogging);
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(Localizer.T("ato.classParams"), EditorStyles.miniBoldLabel);
                DrawClass(s.Opaque, Localizer.T("ato.class.opaque"));
                DrawClass(s.Transparent, Localizer.T("ato.class.transparent"));
                DrawClass(s.Normal, Localizer.T("ato.class.normal"));
                DrawClass(s.Grayscale, Localizer.T("ato.class.grayscale"));
            }
        }

        private static void DrawClass(TextureClassSettings c, string label)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
                c.MsSsim = EditorGUILayout.Slider(Localizer.T("ato.metric.msssim"), c.MsSsim, 0.8f, 1f);
                c.DeltaE = EditorGUILayout.Slider(Localizer.T("ato.metric.deltae"), c.DeltaE, 0.2f, 10f);
                c.AlphaCutoutIou = EditorGUILayout.Slider(Localizer.T("ato.metric.cutoutiou"), c.AlphaCutoutIou, 0.9f, 1f);
                c.AlphaBlendRmse = EditorGUILayout.Slider(Localizer.T("ato.metric.blendrmse"), c.AlphaBlendRmse, 0.002f, 0.1f);
                c.NormalAngleDeg = EditorGUILayout.Slider(Localizer.T("ato.metric.normaldeg"), c.NormalAngleDeg, 0.5f, 15f);
                c.NormalP95Deg = EditorGUILayout.Slider(Localizer.T("ato.metric.normalp95"), c.NormalP95Deg, 1f, 25f);
                c.DataRmse = EditorGUILayout.Slider(Localizer.T("ato.metric.datarmse"), c.DataRmse, 0.005f, 0.1f);
                c.MipmapAndStreaming = EditorGUILayout.Toggle(Localizer.T("ato.mipstream"), c.MipmapAndStreaming);
                c.Crunch = EditorGUILayout.Toggle(Localizer.T("ato.crunch"), c.Crunch);
            }
        }
    }
}

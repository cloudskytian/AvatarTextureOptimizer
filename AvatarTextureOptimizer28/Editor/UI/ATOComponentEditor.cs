using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Inspector for the component.
    ///
    ///     Design intent: a first-time user should only ever have to look at one dropdown. Everything
    ///     else lives behind collapsed foldouts, platform overrides are hidden until their checkbox is
    ///     ticked, and every default is the value we would pick ourselves.
    ///
    /// ZH: 组件的 Inspector。
    ///
    ///     设计意图：新手用户应当只需要看一个下拉框。其余内容都收在折叠区里，
    ///     平台覆盖在勾选之前完全隐藏，所有默认值都是我们自己会选的值。
    /// </summary>
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    public sealed class ATOComponentEditor : Editor
    {
        private static readonly string[] DensityLabels = { "512", "1024", "2048", "4096", "8192" };
        private static readonly int[] DensityValues = { 512, 1024, 2048, 4096, 8192 };
        private static readonly string[] PaddingLabels = { "4", "8", "16", "32", "64" };
        private static readonly int[] PaddingValues = { 4, 8, 16, 32, 64 };

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            var c = (AvatarTextureOptimizer)target;
            ATOLocalizer.ApplyPreference(c.languageMode, c.manualLanguage);

            EditorGUILayout.LabelField(T("ato.component.title"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(T("ato.component.description"), MessageType.None);

            DrawValidation(c);
            EditorGUILayout.Space();

            Undo.RecordObject(c, "Avatar Texture Optimizer");
            EditorGUI.BeginChangeCheck();

            // ---- Basic ------------------------------------------------------------------------------
            var common = c.common;
            var tier = (QualityTier)EditorGUILayout.EnumPopup(
                new GUIContent(T("ato.field.qualityTier"), T("ato.field.qualityTier:tooltip")), common.qualityTier);
            if (tier != common.qualityTier)
            {
                common.qualityTier = tier;
                common.SyncQualityFromTier();
            }

            common.generateAtlas = EditorGUILayout.Toggle(
                new GUIContent(T("ato.field.generateAtlas"), T("ato.field.generateAtlas:tooltip")),
                common.generateAtlas);

            EditorGUILayout.Space();

            // ---- Whitelist ---------------------------------------------------------------------------
            EditorGUILayout.LabelField(T("ato.section.whitelist"), EditorStyles.boldLabel);
            DrawWhitelist(c);

            EditorGUILayout.Space();

            // ---- Advanced ----------------------------------------------------------------------------
            c.uiAdvancedExpanded = EditorGUILayout.Foldout(c.uiAdvancedExpanded, T("ato.section.advanced"), true);
            if (c.uiAdvancedExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawProfile(c, common, isOverride: false);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(T("ato.section.platformOverride"), EditorStyles.boldLabel);
                    DrawOverride(c, c.pcOverride, ATOPlatform.PC);
                    DrawOverride(c, c.androidOverride, ATOPlatform.Android);
                    DrawOverride(c, c.iosOverride, ATOPlatform.iOS);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(T("ato.section.diagnostics"), EditorStyles.boldLabel);
                    c.verboseLogging = EditorGUILayout.Toggle(T("ato.field.verbose"), c.verboseLogging);
                    using (new EditorGUI.DisabledScope(!c.verboseLogging))
                        c.traceLogging = EditorGUILayout.Toggle(T("ato.field.trace"), c.traceLogging);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(T("ato.section.localization"), EditorStyles.boldLabel);
                    c.languageMode = (ATOLanguageMode)EditorGUILayout.EnumPopup(T("ato.field.languageMode"), c.languageMode);
                    if (c.languageMode == ATOLanguageMode.Manual)
                    {
                        var langs = ATOLocalizer.AvailableLanguages.ToArray();
                        if (langs.Length > 0)
                        {
                            int idx = Mathf.Max(0, Array.IndexOf(langs, c.manualLanguage));
                            idx = EditorGUILayout.Popup(T("ato.field.languageManual"), idx, langs);
                            c.manualLanguage = langs[idx];
                        }
                    }
                }
            }

            if (EditorGUI.EndChangeCheck()) EditorUtility.SetDirty(c);
        }

        private void DrawValidation(AvatarTextureOptimizer c)
        {
#if ATO_VRCSDK3_AVATARS
            if (c.GetComponent<VRCAvatarDescriptor>() == null)
                EditorGUILayout.HelpBox(T("ato.error.noDescriptor"), MessageType.Error);
#endif
            var root = c.transform;
            while (root.parent != null) root = root.parent;
            var all = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (all.Length > 1)
                EditorGUILayout.HelpBox(T("ato.error.multipleComponents"), MessageType.Error);
        }

        private void DrawWhitelist(AvatarTextureOptimizer c)
        {
            var list = c.whitelist;
            for (int i = 0; i < list.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    list[i] = EditorGUILayout.ObjectField(list[i], typeof(UnityEngine.Object), true);
                    if (GUILayout.Button("-", GUILayout.Width(22)))
                    {
                        list.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            if (GUILayout.Button("+", GUILayout.Width(40))) list.Add(null);
        }

        private void DrawOverride(AvatarTextureOptimizer c, PlatformProfile p, ATOPlatform platform)
        {
            bool wasEnabled = p.enabled;
            p.enabled = EditorGUILayout.ToggleLeft(
                string.Format(T("ato.field.overrideEnabled"), platform), p.enabled);

            // EN: Seed a freshly enabled override from the common profile so the user starts from a
            //     working configuration instead of an empty one.
            // ZH: 新启用的覆盖从通用配置播种，使用户从一份可用配置开始，而不是空配置。
            if (p.enabled && !wasEnabled)
            {
                var seeded = c.common.Clone();
                seeded.enabled = true;
                seeded.platform = platform;
                switch (platform)
                {
                    case ATOPlatform.PC: c.pcOverride = seeded; break;
                    case ATOPlatform.Android: c.androidOverride = seeded; break;
                    case ATOPlatform.iOS: c.iosOverride = seeded; break;
                }
                return;
            }

            if (!p.enabled) return;
            using (new EditorGUI.IndentLevelScope()) DrawProfile(c, p, isOverride: true);
        }

        private void DrawProfile(AvatarTextureOptimizer c, PlatformProfile p, bool isOverride)
        {
            if (isOverride)
            {
                var tier = (QualityTier)EditorGUILayout.EnumPopup(T("ato.field.qualityTier"), p.qualityTier);
                if (tier != p.qualityTier) { p.qualityTier = tier; p.SyncQualityFromTier(); }
                p.generateAtlas = EditorGUILayout.Toggle(T("ato.field.generateAtlas"), p.generateAtlas);
            }

            // ---- Quality thresholds --------------------------------------------------------------------
            EditorGUILayout.LabelField(T("ato.section.quality"), EditorStyles.miniBoldLabel);
            bool custom = p.qualityTier == QualityTier.Custom;
            var q = custom ? p.customQuality : p.quality;

            using (new EditorGUI.DisabledScope(!custom))
            {
                q.targetQuality = EditorGUILayout.Slider(T("ato.field.targetQuality"), q.targetQuality, 0f, 1f);
                q.minMsSsim = EditorGUILayout.Slider(T("ato.field.minMsSsim"), q.minMsSsim, 0.8f, 1f);
                q.maxDeltaE2000Mean = EditorGUILayout.FloatField(T("ato.field.maxDeltaEMean"), q.maxDeltaE2000Mean);
                q.maxDeltaE2000P95 = EditorGUILayout.FloatField(T("ato.field.maxDeltaEP95"), q.maxDeltaE2000P95);
                q.minAlphaCutoutIoU = EditorGUILayout.Slider(T("ato.field.minAlphaIoU"), q.minAlphaCutoutIoU, 0.9f, 1f);
                q.maxAlphaBlendRmse = EditorGUILayout.FloatField(T("ato.field.maxAlphaRmse"), q.maxAlphaBlendRmse);
                q.maxNormalAngleMeanDeg = EditorGUILayout.FloatField(T("ato.field.maxNormalMean"), q.maxNormalAngleMeanDeg);
                q.maxNormalAngleP95Deg = EditorGUILayout.FloatField(T("ato.field.maxNormalP95"), q.maxNormalAngleP95Deg);
                q.maxGrayscaleRmse = EditorGUILayout.FloatField(T("ato.field.maxGrayRmse"), q.maxGrayscaleRmse);
            }
            if (custom) p.customQuality = q; else p.quality = q;

            // ---- Density -------------------------------------------------------------------------------
            p.minTexelDensity = (ATODensity)EditorGUILayout.IntPopup(
                T("ato.field.minDensity"), (int)p.minTexelDensity, DensityLabels, DensityValues);
            p.maxTexelDensity = (ATODensity)EditorGUILayout.IntPopup(
                T("ato.field.maxDensity"), (int)p.maxTexelDensity, DensityLabels, DensityValues);
            if ((int)p.maxTexelDensity < (int)p.minTexelDensity) p.maxTexelDensity = p.minTexelDensity;

            // ---- Atlas ----------------------------------------------------------------------------------
            EditorGUILayout.LabelField(T("ato.section.atlas"), EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(!p.generateAtlas))
            {
                p.minPadding = (ATOPadding)EditorGUILayout.IntPopup(
                    T("ato.field.minPadding"), (int)p.minPadding, PaddingLabels, PaddingValues);
                p.allowIslandRotation = EditorGUILayout.Toggle(T("ato.field.allowRotation"), p.allowIslandRotation);
                p.experimentalNPOT = EditorGUILayout.Toggle(
                    new GUIContent(T("ato.field.npot"), T("ato.field.npot:tooltip")), p.experimentalNPOT);
            }

            // ---- Texture parameters -----------------------------------------------------------------------
            c.uiTextureParamsExpanded = EditorGUILayout.Foldout(
                c.uiTextureParamsExpanded, T("ato.section.textureParams"), true);
            if (c.uiTextureParamsExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    var o = p.output;
                    o.mipmapAndStreaming = EditorGUILayout.Toggle(
                        new GUIContent(T("ato.field.mipmap"), T("ato.field.mipmap:tooltip")), o.mipmapAndStreaming);
                    o.opaqueColorFormat = (ATOColorFormat)EditorGUILayout.EnumPopup(
                        T("ato.field.opaqueFormat"), o.opaqueColorFormat);
                    o.transparentColorFormat = (ATOColorFormat)EditorGUILayout.EnumPopup(
                        T("ato.field.transparentFormat"), o.transparentColorFormat);
                    o.normalFormat = (ATONormalFormat)EditorGUILayout.EnumPopup(
                        T("ato.field.normalFormat"), o.normalFormat);
                    o.grayscaleFormat = (ATOGrayscaleFormat)EditorGUILayout.EnumPopup(
                        T("ato.field.grayscaleFormat"), o.grayscaleFormat);
                    o.compressorQuality = EditorGUILayout.IntSlider(
                        T("ato.field.compressorQuality"), o.compressorQuality, 0, 100);
                    p.output = o;
                }
            }

            // ---- Dedup ------------------------------------------------------------------------------------
            EditorGUILayout.LabelField(T("ato.section.dedup"), EditorStyles.miniBoldLabel);
            p.deduplicateMaterials = EditorGUILayout.Toggle(T("ato.field.dedupMaterials"), p.deduplicateMaterials);
            p.deduplicateTextures = EditorGUILayout.Toggle(T("ato.field.dedupTextures"), p.deduplicateTextures);
        }

        private static string T(string key) => ATOLocalizer.Tr(key);
    }
}

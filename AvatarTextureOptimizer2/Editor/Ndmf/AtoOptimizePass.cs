using System;
using System.Threading;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Main bake pass. / 主烘焙通道。
    /// </summary>
    public sealed class AtoOptimizePass : Pass<AtoOptimizePass>
    {
        public override string DisplayName => "Avatar Texture Optimizer";

        protected override void Execute(BuildContext context)
        {
            var root = context.AvatarRootObject;
            if (root == null) return;

            var comps = root.GetComponentsInChildren<AvatarTextureOptimizerComponent>(true);
            if (comps == null || comps.Length == 0) return;

            AtoI18n.SetMode(comps[0].language);

            if (comps.Length > 1)
            {
                ErrorReport.ReportError(AtoError.Localizer, ErrorSeverity.Error, "err.multiple");
                AtoLog.Error(AtoI18n.T("err.multiple"));
                throw new Exception("[ATO] multiple components");
            }

            var comp = comps[0];
            if (!HasAvatarDescriptor(comp.gameObject) || comp.transform != root.transform)
            {
                ErrorReport.ReportError(AtoError.Localizer, ErrorSeverity.Error, "err.noDescriptor");
                AtoLog.Error(AtoI18n.T("err.noDescriptor"));
                throw new Exception("[ATO] missing VRCAvatarDescriptor colocated component");
            }

            AtoLog.Verbose = comp.verboseLogging;
            AtoI18n.SetMode(comp.language);

            var platform = AtoPlatformUtil.Detect(context);
            var settings = comp.Resolve(platform);
            AtoLog.Info($"start bake platform={platform} atlas={settings.generateAtlas} preset={settings.qualityPreset} q={settings.quality.targetQuality}");

            using (var session = new AtoSession(context, comp, settings, platform))
            {
                try
                {
                    session.Run();
                }
                catch (OperationCanceledException)
                {
                    AtoLog.Warn("bake cancelled by user; temp assets kept, CPU/GPU released");
                    throw;
                }
                finally
                {
                    // Component is editor-only; NDMF removes IEditorOnly, but strip ourselves too.
                    // 组件为编辑器专用；再主动移除成品上的自身。
                    if (comp != null)
                        UnityEngine.Object.DestroyImmediate(comp);
                }
            }
        }

        static bool HasAvatarDescriptor(GameObject go)
        {
#if ATO_VRCSDK3
            return go.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() != null;
#else
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                if (c.GetType().Name == "VRCAvatarDescriptor") return true;
            }
            return false;
#endif
        }
    }

    static class AtoError
    {
        public static readonly LocalizerHolder Localizer = new LocalizerHolder();

        public sealed class LocalizerHolder : nadena.dev.ndmf.localization.Localizer
        {
            public LocalizerHolder() : base("en", () =>
            {
                return new System.Collections.Generic.List<(string, Func<string, string>)>
                {
                    ("en", k => AtoI18nFallback("en", k)),
                    ("zh-Hans", k => AtoI18nFallback("zh-Hans", k)),
                    ("zh-Hant", k => AtoI18nFallback("zh-Hans", k))
                };
            })
            {
            }

            static string AtoI18nFallback(string lang, string key)
            {
                // Localizer already uses AtoI18n tables via T(); we map key directly.
                var prev = AtoI18n.CurrentLang;
                return AtoI18n.T(key);
            }
        }
    }
}

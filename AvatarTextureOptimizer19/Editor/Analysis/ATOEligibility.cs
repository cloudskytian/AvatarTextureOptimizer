// English: Apply whitelist + ST/wrap/special-purpose rules. Same-UV siblings skip atlasing only.
// 中文：套用白名单与 ST/wrap/特殊用途规则。同 UV 的其它贴图只跳过图集化。
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOEligibility
    {
        public static void Apply(ATOState state, ATOAnimImpact anim)
        {
            var stAnimatedTextures = new HashSet<Texture2D>();
            if (anim != null && anim.StAnimatedProps.Count > 0)
            {
                foreach (var use in state.Uses)
                {
                    if (use.Texture == null) continue;
                    // Any ST animation on the renderer path disqualifies that renderer's textures.
                    if (use.Renderer != null && use.Renderer.Renderer != null)
                    {
                        var name = use.Renderer.Renderer.name;
                        foreach (var key in anim.StAnimatedProps)
                        {
                            if (key.IndexOf(name, System.StringComparison.Ordinal) >= 0)
                            {
                                stAnimatedTextures.Add(use.Texture);
                                break;
                            }
                        }
                    }
                }
            }

            foreach (var use in state.Uses)
            {
                if (use.Texture == null) continue;
                if (state.WhitelistTextures.Contains(use.Texture) ||
                    state.WhitelistObjects.Contains(use.Texture) ||
                    (use.Material != null && state.WhitelistObjects.Contains(use.Material)) ||
                    (use.Renderer != null && use.Renderer.Renderer != null &&
                     (state.WhitelistObjects.Contains(use.Renderer.Renderer) ||
                      state.WhitelistObjects.Contains(use.Renderer.Renderer.gameObject))))
                {
                    use.Eligible = false;
                    use.SkipReason = "whitelist";
                    continue;
                }

                if (stAnimatedTextures.Contains(use.Texture))
                {
                    use.Eligible = false;
                    use.SkipReason = "animated ST / transform";
                }
            }

            // Same-UV siblings of a skipped texture: skip atlasing only.
            var skipAtlasKeys = new HashSet<string>();
            foreach (var use in state.Uses)
            {
                if (use.Eligible) continue;
                skipAtlasKeys.Add(UvKey(use));
            }

            foreach (var use in state.Uses)
            {
                if (!use.Eligible) continue;
                if (skipAtlasKeys.Contains(UvKey(use)))
                {
                    state.SkipAtlasTextures.Add(use.Texture);
                    state.Log.VerboseInfo("skip atlas (shared UV with ineligible) " + use.Texture.name);
                }
            }

            var skipped = 0;
            foreach (var use in state.Uses)
            {
                if (use.Eligible) continue;
                skipped++;
                if (use.SkipReason != "whitelist")
                {
                    state.Report.Warnings.Add(use.Texture != null
                        ? use.Texture.name + ": " + use.SkipReason
                        : use.SkipReason);
                    ErrorReport.ReportError(ATOLoc.L, ErrorSeverity.NonFatal, "warn.skippedTransform",
                        use.Texture != null ? use.Texture.name : "?");
                }
            }

            state.Log.Info("eligibility skipped uses=" + skipped + " skip-atlas textures=" + state.SkipAtlasTextures.Count);
        }

        private static string UvKey(ATOTextureUse use)
        {
            var id = use.Renderer != null && use.Renderer.Renderer != null
                ? use.Renderer.Renderer.GetInstanceID()
                : 0;
            return id + "|" + use.UvChannel;
        }
    }
}

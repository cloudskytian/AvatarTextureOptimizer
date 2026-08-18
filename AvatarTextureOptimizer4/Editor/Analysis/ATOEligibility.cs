// Avatar Texture Optimizer (ATO)
// Whitelist & eligibility resolution. Any texture that fails the strict processing
// rules is treated as whitelisted (skipped), with a warning.
// 白名单与资格解析。任何不满足严格处理规则的贴图都按白名单处理（跳过），并告警。

using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 2: resolve whitelists, animated enable/disable, ST disqualification, and
    /// mark per-texture skipAllOptimization.
    /// 阶段 2：解析白名单、动画启停、ST 取消资格，并标记逐贴图的 skipAllOptimization。
    /// </summary>
    public static class ATOEligibility
    {
        public static void Resolve(ATOBuildContext build, ATOProgress progress)
        {
            // 1. Animated enable/disable. / 动画启停。
            foreach (var rr in build.renderers)
            {
                if (build.anim.rendererEnablePaths.Contains(rr.path)) rr.animatedEnabled = true;
                // A parent active curve may enable/disable this renderer. / 父级 active 曲线可能启停该渲染器。
                foreach (var p in build.anim.activePaths)
                {
                    if (rr.path == p || rr.path.StartsWith(p + "/"))
                    {
                        rr.animatedEnabled = true;
                        break;
                    }
                }
            }

            // 2. Whitelist objects -> textures. / 白名单对象 -> 贴图。
            foreach (var tr in build.textures)
            {
                if (build.IsWhitelisted(tr.texture) || build.IsWhitelisted(tr.sourceAsset))
                {
                    tr.isWhitelisted = true;
                    tr.skipAllOptimization = true;
                    continue;
                }
                foreach (var u in tr.usages)
                {
                    if (build.IsWhitelisted(u.material) || (u.renderer != null && build.IsWhitelisted(u.renderer.renderer)))
                    {
                        tr.isWhitelisted = true;
                        tr.skipAllOptimization = true;
                        break;
                    }
                    if (u.renderer != null && build.IsWhitelisted(u.renderer.sourceMesh))
                    {
                        tr.isWhitelisted = true;
                        tr.skipAllOptimization = true;
                        break;
                    }
                }
            }

            // 3. ST transform disqualification. / ST 变换取消资格。
            foreach (var tr in build.textures)
            {
                if (tr.skipAllOptimization) continue;
                foreach (var u in tr.usages)
                {
                    if (u.material == null) continue;
                    // Animated ST? / ST 被动画修改？
                    if (build.anim.animatedSt.Contains((u.material, u.propertyName)))
                    {
                        u.stDisqualified = true;
                        continue;
                    }
                    // Static ST not identity? / 静态 ST 非单位？
                    var stName = u.propertyName + "_ST";
                    if (u.material.HasProperty(stName))
                    {
                        var v = u.material.GetVector(stName);
                        if (Mathf.Abs(v.x - 1f) > 1e-4f || Mathf.Abs(v.y - 1f) > 1e-4f || Mathf.Abs(v.z) > 1e-4f || Mathf.Abs(v.w) > 1e-4f)
                        {
                            u.stDisqualified = true;
                        }
                    }
                }

                if (AllUsagesStDisqualified(tr))
                {
                    tr.skipAllOptimization = true;
                    build.report.warnings.Add($"Texture '{tr.texture.name}' skipped: non-identity or animated UV transform. / 贴图 '{tr.texture.name}' 跳过：存在非单位或被动画修改的 UV 变换。");
                    ATOLogger.Warn(build.report.warnings[build.report.warnings.Count - 1]);
                }
            }

            // 4. Renderer inactivity: textures only on never-enabled renderers are skipped. / 渲染器不活动：仅出现在永不启用的渲染器上的贴图跳过。
            foreach (var tr in build.textures)
            {
                if (tr.skipAllOptimization) continue;
                bool anyActive = false;
                foreach (var u in tr.usages)
                {
                    if (u.renderer == null || u.renderer.EffectiveEnabled) { anyActive = true; break; }
                }
                if (!anyActive)
                {
                    tr.skipAllOptimization = true;
                    ATOLogger.Info($"Texture '{tr.texture.name}' skipped: only referenced by disabled renderers. / 贴图 '{tr.texture.name}' 跳过：仅被禁用的渲染器引用。");
                }
            }

            int skipped = 0;
            foreach (var tr in build.textures) if (tr.skipAllOptimization) skipped++;
            build.report.whitelistedTextureCount = skipped;
            ATOLogger.Info($"Eligibility: {skipped} of {build.textures.Count} texture refs skipped (whitelist/ineligible).");
        }

        private static bool AllUsagesStDisqualified(ATOTextureRef tr)
        {
            if (tr.usages.Count == 0) return false;
            foreach (var u in tr.usages)
                if (!u.stDisqualified) return false;
            return true;
        }
    }
}

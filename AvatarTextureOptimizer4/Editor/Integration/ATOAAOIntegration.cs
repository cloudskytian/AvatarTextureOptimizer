// Avatar Texture Optimizer (ATO)
// Avatar Optimizer (AAO) compatibility via the UVUsageCompabilityAPI (AAO's own spelling).
// Compiles only when AAO's API assembly is present (version define ATO_AAO).
// 通过 UVUsageCompabilityAPI（AAO 原文拼写）兼容 Avatar Optimizer。
// 仅在存在 AAO API 程序集时编译（version define ATO_AAO）。
//
// Verified against AAO 1.9.17 source: the API must only be called at build time, supports
// SkinnedMeshRenderer only, and RegisterTexCoordEvacuation throws if the saved channel is used by AAO.
// 已对照 AAO 1.9.17 源码验证：该 API 只能在构建时调用、仅支持 SkinnedMeshRenderer，
// 且当 saved 通道被 AAO 使用时 RegisterTexCoordEvacuation 会抛异常。

using System.Collections.Generic;
using UnityEngine;
using nadena.dev.ndmf;

namespace NetFosa.ATO
{
    /// <summary>
    /// Evacuates AAO-used UV channels before ATO rewrites them, so AAO can still read the
    /// original UVs from the evacuated channel.
    /// 在 ATO 改写 UV 之前疏散 AAO 使用的 UV 通道，让 AAO 仍能从疏散通道读取原始 UV。
    /// </summary>
    public static class ATOAAOIntegration
    {
        /// <summary>
        /// For every channel ATO will rewrite that AAO also uses, copy the original UVs to a
        /// free channel and register the evacuation.
        /// 对 ATO 将要改写且 AAO 也在使用的每个通道，把原始 UV 复制到空闲通道并注册疏散。
        /// </summary>
        public static void Evacuate(ATOBuildContext build, ATORendererRef rr)
        {
#if ATO_AAO
            var smr = rr.renderer as SkinnedMeshRenderer;
            if (smr == null) return; // API supports SMR only / API 仅支持 SMR
            var mesh = rr.sourceMesh;

            for (int ch = 0; ch < ATOConstants.MaxUvChannels; ch++)
            {
                if (!rr.usedUvChannels.Contains(ch)) continue;
                bool usedByAao;
                try { usedByAao = Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.IsTexCoordUsed(smr, ch); }
                catch (System.Exception) { continue; }
                if (!usedByAao) continue;

                // Find a free channel: not rewritten by ATO and not used by AAO. / 找空闲通道：ATO 不写且 AAO 不用。
                int saved = -1;
                for (int c = 0; c < ATOConstants.MaxUvChannels; c++)
                {
                    if (c == ch || rr.usedUvChannels.Contains(c)) continue;
                    bool used;
                    try { used = Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.IsTexCoordUsed(smr, c); }
                    catch (System.Exception) { continue; }
                    if (!used) { saved = c; break; }
                }
                if (saved < 0)
                {
                    ATOLogger.Warn($"No free UV channel to evacuate ch{ch} on '{rr.renderer.name}'; AAO may misbehave. / 无空闲通道疏散 '{rr.renderer.name}' 的通道{ch}，AAO 可能异常。");
                    continue;
                }

                if (ATOMeshUvAccessor.CopyChannel(mesh, ch, saved))
                {
                    Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.RegisterTexCoordEvacuation(smr, ch, saved);
                    ATOLogger.Debug($"Evacuated UV{ch} -> UV{saved} on '{rr.renderer.name}' for AAO.");
                }
            }
#endif
        }
    }
}

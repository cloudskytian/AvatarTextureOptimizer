using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// AAO evacuation planning: for every UV channel we will rewrite, if AAO also uses that
    /// channel (RemoveMeshByMask → uv0, RemoveMeshByUVTile → configured channels), we must save
    /// the original UV to a free channel and register the evacuation (verified against AAO
    /// source: EvacuateProcessor swaps channels while AAO's UV-dependent passes run; the
    /// RevertEvacuateProcessor restores our new UV and deletes the saved channel afterwards). /
    /// AAO 疏散规划：对我方将改写的每个 UV 通道，若 AAO 也使用该通道（RemoveMeshByMask → uv0、
    /// RemoveMeshByUVTile → 配置通道），必须把原始 UV 存到空闲通道并注册疏散（已对照 AAO 源码：
    /// EvacuateProcessor 在其依赖 UV 的 pass 期间交换通道；RevertEvacuateProcessor 之后恢复我方新 UV
    /// 并删除 saved 通道）。
    /// </summary>
    internal static class AtoAaoEvacuation
    {
        /// <summary>
        /// Plan evacuations for all renderers. Whitelists a UV group when no free channel exists. /
        /// 为全部渲染器规划疏散。无空闲通道时把该 UV 组白名单化。
        /// </summary>
        public static void Plan(AtoContext ctx)
        {
            if (!AtoAaoIntegration.IsAaoInstalled) return;

            foreach (var data in ctx.Renderers)
            {
                if (data.Renderer is not SkinnedMeshRenderer smr) continue;

                var sampledChannels = new HashSet<int>(data.UvGroups.Keys);

                foreach (var kv in data.UvGroups)
                {
                    var channel = kv.Key;
                    var uvGroup = kv.Value;
                    if (uvGroup.Whitelisted) continue;
                    if (!AtoAaoIntegration.IsTexCoordUsed(smr, channel)) continue;

                    var saved = -1;
                    for (var s = 0; s < 8; s++)
                    {
                        if (s == channel) continue;
                        if (sampledChannels.Contains(s)) continue;
                        if (data.AaoEvacuations.ContainsKey(s) || data.AaoEvacuations.ContainsValue(s)) continue;
                        if (AtoAaoIntegration.IsTexCoordUsed(smr, s)) continue;
                        saved = s;
                        break;
                    }

                    if (saved < 0)
                    {
                        uvGroup.Whitelisted = true;
                        uvGroup.WhitelistReason = "no free UV channel for AAO evacuation";
                        ctx.Warn(ctx.State.Tr("warn.aaoEvacuationFailed", uvGroup.DisplayName));
                        continue;
                    }
                    data.AaoEvacuations[channel] = saved;
                    AtoLog.Verbose($"[ATO] AAO evacuation planned: {data.Renderer.name} uv{channel} -> uv{saved}");
                }
            }
        }
    }
}

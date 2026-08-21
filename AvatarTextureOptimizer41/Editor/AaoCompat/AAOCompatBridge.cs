#if ATO_AAO_API_AVAILABLE
using System.Collections.Generic;
using Anatawa12.AvatarOptimizer.API;
using UnityEngine;

// Bridge to Avatar Optimizer's UVUsageCompatibilityAPI (note the original AAO spelling "Compability").
// When AAO might use a UV channel (e.g. RemoveMeshByUVTile), we evacuate the ORIGINAL UVs to a spare
// channel and register the evacuation so AAO keeps using the correct coordinates after our remap.
// AAO 的 UVUsageCompatibilityAPI 桥接（注意 AAO 原文拼写 "Compability"）。
// 当 AAO 可能使用某 UV 通道时（如 RemoveMeshByUVTile），我们把原始 UV 转移到备用通道并注册转移，
// 使 AAO 在重映射后仍使用正确的坐标。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AAOCompatBridge
    {
        /// <summary>
        /// Called after mesh remap: for every modified channel of every SkinnedMeshRenderer, if AAO uses
        /// the channel we evacuate the original UVs into a free channel and register the evacuation.
        /// 网格重映射后调用：对每个被修改通道的 SkinnedMeshRenderer，若 AAO 使用该通道，
        /// 则将原始 UV 转移到空闲通道并注册转移。
        /// </summary>
        public static void EvacuateModifiedChannels(ATOBuildContext ctx)
        {
            foreach (var kv in ctx.NewMeshes)
            {
                var renderer = kv.Key as SkinnedMeshRenderer;
                if (renderer == null) continue;
                // Channels modified on this renderer. 该渲染器被修改的通道。
                var modified = new HashSet<int>();
                foreach (var group in ctx.UVGroups)
                {
                    if (group.Renderer != renderer) continue;
                    bool atlased = false;
                    foreach (var use in group.Uses)
                        if (!use.Skip && ctx.UseAtlas.ContainsKey(use)) { atlased = true; break; }
                    if (atlased) modified.Add(group.Channel);
                }

                foreach (int channel in modified)
                {
                    if (!UVUsageCompabilityAPI.IsTexCoordUsed(renderer, channel)) continue;
                    // Find a spare channel (4..7) AAO does not use. 找一个 AAO 未使用的备用通道（4..7）。
                    int spare = -1;
                    for (int c = 4; c < 8; c++)
                    {
                        if (!UVUsageCompabilityAPI.IsTexCoordUsed(renderer, c)) { spare = c; break; }
                    }
                    if (spare < 0)
                    {
                        ATOLog.Warn($"cannot evacuate UV channel {channel} of {renderer.name}: no free channel for AAO compatibility");
                        continue;
                    }
                    // Copy the pre-remap UVs from the original mesh into the spare channel of the new mesh.
                    // 将原网格重映射前的 UV 复制到新网格的备用通道。
                    var srcMesh = renderer.sharedMesh;
                    if (ctx.OriginalMeshes.TryGetValue(renderer, out var original))
                    {
                        var uvList = new List<Vector2>(original.vertexCount);
                        original.GetUVs(channel, uvList);
                        if (uvList.Count > 0) srcMesh.SetUVs(spare, uvList);
                    }
                    try
                    {
                        UVUsageCompabilityAPI.RegisterTexCoordEvacuation(renderer, channel, spare);
                        ATOLog.Info($"AAO UV evacuation: {renderer.name} ch{channel} -> ch{spare}");
                    }
                    catch (System.InvalidOperationException e)
                    {
                        ATOLog.Warn($"AAO evacuation failed for {renderer.name} ch{channel}: {e.Message}");
                    }
                }
            }
        }
    }
}
#else
// Stub compiled when AAO is not installed: no-op (the API is optional).
// AAO 未安装时的桩：空实现（该 API 为可选项）。
namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AAOCompatBridge
    {
        public static void EvacuateModifiedChannels(ATOBuildContext ctx) { }
    }
}
#endif

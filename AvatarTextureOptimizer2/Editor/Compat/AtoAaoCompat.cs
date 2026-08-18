using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Optional AAO UVUsageCompabilityAPI (spelling as in AAO). Safe if AAO is absent.
    /// 可选 AAO UV 兼容 API；未安装 AAO 时为空操作。
    /// </summary>
    public static class AtoAaoCompat
    {
        public static void RegisterUvEvacuation(AtoGraph graph)
        {
#if ATO_AAO
            try
            {
                foreach (var r in graph.Renderers)
                {
                    var smr = r as SkinnedMeshRenderer;
                    if (smr == null) continue;
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if (!Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.IsTexCoordUsed(smr, ch))
                            continue;
                        // Find a free channel to save original UV.
                        int dest = -1;
                        for (int c = 7; c >= 0; c--)
                        {
                            if (!Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.IsTexCoordUsed(smr, c))
                            { dest = c; break; }
                        }
                        if (dest < 0)
                        {
                            AtoLog.Warn($"AAO uses UV{ch} on {smr.name} but no free channel to evacuate");
                            continue;
                        }
                        var mesh = smr.sharedMesh;
                        if (mesh == null) continue;
                        var uv = new System.Collections.Generic.List<Vector2>();
                        mesh.GetUVs(ch, uv);
                        if (uv.Count == 0) continue;
                        mesh.SetUVs(dest, uv);
                        Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.RegisterTexCoordEvacuation(smr, ch, dest);
                        AtoLog.Info($"AAO evacuate UV{ch} -> UV{dest} on {smr.name}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                AtoLog.Warn("AAO UVUsageCompabilityAPI failed: " + ex.Message);
            }
#else
            AtoLog.VerboseInfo("AAO not present; skip UVUsageCompabilityAPI");
#endif
        }
    }
}

using System;
using System.Reflection;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Optional AAO UVUsageCompabilityAPI via reflection so the package compiles without AAO.
    /// 通过反射调用 AAO UVUsageCompabilityAPI，未安装 AAO 时仍可编译。
    /// API read from AAO 1.9.17: IsTexCoordUsed(SkinnedMeshRenderer, int), RegisterTexCoordEvacuation(...).
    /// 源码：aao/API-Editor/UVUsageCompabilityAPI.cs（注意原文拼写 Compability）。
    /// </summary>
    public static class AaoBridge
    {
        public static void EvacuateIfNeeded(GameObject root)
        {
            var api = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor")
                      ?? Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
            if (api == null)
            {
                AtoLog.VerboseLog("AAO not present; skip UVUsageCompabilityAPI.");
                return;
            }

            var isUsed = api.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            var evacuate = api.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            if (isUsed == null || evacuate == null)
            {
                AtoLog.Warn("AAO UVUsageCompabilityAPI methods not found.");
                return;
            }

            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                try
                {
                    for (int ch = 0; ch < 8; ch++)
                    {
                        bool used = (bool)isUsed.Invoke(null, new object[] { smr, ch });
                        if (!used) continue;
                        // Save original UV to first unused channel. / 把原 UV 存到第一个未占用通道。
                        int saved = -1;
                        for (int s = 0; s < 8; s++)
                        {
                            bool u = (bool)isUsed.Invoke(null, new object[] { smr, s });
                            if (!u) { saved = s; break; }
                        }
                        if (saved < 0)
                        {
                            AtoLog.Warn($"AAO uses UV{ch} on {smr.name} but no free channel to evacuate.");
                            continue;
                        }
                        var mesh = smr.sharedMesh;
                        var list = new System.Collections.Generic.List<Vector2>();
                        mesh.GetUVs(ch, list);
                        if (list.Count > 0)
                        {
                            mesh.SetUVs(saved, list);
                            evacuate.Invoke(null, new object[] { smr, ch, saved });
                            AtoLog.Info($"AAO UV evacuate {smr.name} uv{ch} -> uv{saved}");
                        }
                    }
                }
                catch (Exception e)
                {
                    AtoLog.Warn($"AAO UV evacuate failed on {smr.name}: {e.Message}");
                }
            }
        }
    }
}

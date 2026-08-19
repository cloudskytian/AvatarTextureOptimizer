using System;
using System.Reflection;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Optional AAO UVUsageCompabilityAPI via reflection (no compile-time AAO reference).
    /// If AAO will consume a UV channel we rewrote, evacuate the original into a free channel.
    /// 通过反射可选调用 AAO 的 UVUsageCompabilityAPI（编译期不引用 AAO）。
    /// 若 AAO 会消费我们改写过的 UV 通道，就把原始 UV 疏散到空闲通道。
    /// </summary>
    internal static class ATOAaoCompat
    {
        public static void EvacuateIfNeeded(ATOContext ctx)
        {
            var api = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor")
                      ?? Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, Anatawa12.AvatarOptimizer.API.Editor")
                      ?? FindType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
            if (api == null)
            {
                ctx.Log.Detail("AAO not present, skip UV evacuation.");
                return;
            }

            var isUsed = api.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            var evacuate = api.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            if (isUsed == null || evacuate == null)
            {
                ctx.Log.Warn("AAO UVUsageCompabilityAPI methods not found.");
                return;
            }

            foreach (var ri in ctx.Renderers)
            {
                if (!ri.IsSkinned) continue;
                var smr = ri.Renderer as SkinnedMeshRenderer;
                if (smr == null || smr.sharedMesh == null) continue;

                var usedByUs = new bool[8];
                foreach (var island in ri.Islands)
                    if (island.Packed && island.UvChannel >= 0 && island.UvChannel < 8)
                        usedByUs[island.UvChannel] = true;

                for (int ch = 0; ch < 8; ch++)
                {
                    if (!usedByUs[ch]) continue;
                    bool aaoUses = false;
                    try { aaoUses = (bool)isUsed.Invoke(null, new object[] { smr, ch }); }
                    catch (Exception e)
                    {
                        ctx.Log.Detail($"AAO IsTexCoordUsed threw: {e.InnerException?.Message ?? e.Message}");
                        continue;
                    }
                    if (!aaoUses) continue;

                    var dest = FindFreeChannel(smr, usedByUs, isUsed);
                    if (dest < 0)
                    {
                        ctx.Log.Warn($"No free UV channel to evacuate UV{ch} on '{smr.name}' for AAO.");
                        continue;
                    }

                    CopyUv(smr.sharedMesh, ch, dest);
                    usedByUs[dest] = true;
                    try
                    {
                        evacuate.Invoke(null, new object[] { smr, ch, dest });
                        ctx.Log.Info($"AAO evacuate UV{ch} → UV{dest} on '{smr.name}'");
                    }
                    catch (Exception e)
                    {
                        ctx.Log.Warn($"AAO RegisterTexCoordEvacuation failed: {e.InnerException?.Message ?? e.Message}");
                    }
                }
            }
        }

        private static int FindFreeChannel(SkinnedMeshRenderer smr, bool[] usedByUs, MethodInfo isUsed)
        {
            for (int ch = 7; ch >= 0; ch--)
            {
                if (usedByUs[ch]) continue;
                var list = new System.Collections.Generic.List<Vector2>();
                smr.sharedMesh.GetUVs(ch, list);
                if (list.Count > 0) continue;
                try
                {
                    if ((bool)isUsed.Invoke(null, new object[] { smr, ch })) continue;
                }
                catch { continue; }
                return ch;
            }
            return -1;
        }

        private static void CopyUv(Mesh mesh, int from, int to)
        {
            var list = new System.Collections.Generic.List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(from, list);
            mesh.SetUVs(to, list);
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}

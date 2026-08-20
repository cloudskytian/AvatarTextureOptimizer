using System;
using System.Reflection;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline
{
    /// <summary>
    /// Soft bridge to Avatar Optimizer's UVUsageCompabilityAPI. Uses reflection so ATO compiles and
    /// runs whether or not AAO is installed. When AAO is present and uses a UV channel we modify, we
    /// evacuate the original UVs to the first free channel (0..7) and register the evacuation so AAO
    /// uses the saved copy and cleans it up.
    /// 到 AAO UVUsageCompabilityAPI 的软桥接，使用反射，未安装 AAO 也可运行。AAO 使用我们要修改的
    /// UV 通道时，把原 UV 疏散到第一个空闲通道并注册，AAO 会使用副本并自行清理。
    /// </summary>
    internal static class AaoBridge
    {
        private static readonly Type ApiType;
        private static readonly MethodInfo IsUsed;
        private static readonly MethodInfo Register;
        private static readonly bool Available;

        static AaoBridge()
        {
            ApiType = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor");
            if (ApiType == null)
            {
                // Try searching loaded assemblies by simple name.
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false);
                    if (t != null) { ApiType = t; break; }
                }
            }
            if (ApiType != null)
            {
                IsUsed = ApiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                Register = ApiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                Available = IsUsed != null && Register != null;
                if (Available) AtoLog.VIf(true, "[ATO] AAO UVUsageCompabilityAPI detected; AAO compatibility enabled.");
            }
        }

        /// <summary>Evacuate the original UVs of channel on this renderer if AAO uses that channel. / 若 AAO 使用该通道，疏散原 UV。</summary>
        public static void EvacuateIfNeeded(SkinnedMeshRenderer smr, int channel)
        {
            if (!Available || smr == null) return;
            try
            {
                bool used = (bool)IsUsed.Invoke(null, new object[] { smr, channel });
                if (!used) return;
                // Find a free channel to evacuate into (prefer higher channels). / 找空闲通道（优先高通道）
                var mesh = smr.sharedMesh;
                if (mesh == null) return;
                for (int saved = 7; saved >= 0; saved--)
                {
                    if (saved == channel) continue;
                    if (ChannelHasData(mesh, saved)) continue;
                    bool savedUsed = (bool)IsUsed.Invoke(null, new object[] { smr, saved });
                    if (savedUsed) continue;
                    CopyChannel(mesh, channel, saved);
                    Register.Invoke(null, new object[] { smr, channel, saved });
                    AtoLog.VIf(true, $"[ATO] Evacuated UV{channel} -> UV{saved} on '{smr.name}' for AAO compatibility.");
                    return;
                }
                AtoLog.Warn($"Could not find a free UV channel to evacuate UV{channel} on '{smr.name}'; AAO features may conflict.");
            }
            catch (Exception e)
            {
                AtoLog.Warn($"AAO evacuation failed for '{smr.name}': {e.Message}");
            }
        }

        private static bool ChannelHasData(Mesh m, int ch) => ch switch
        {
            0 => m.uv != null && m.uv.Length > 0,
            1 => m.uv2 != null && m.uv2.Length > 0,
            2 => m.uv3 != null && m.uv3.Length > 0,
            3 => m.uv4 != null && m.uv4.Length > 0,
            4 => m.uv5 != null && m.uv5.Length > 0,
            5 => m.uv6 != null && m.uv6.Length > 0,
            6 => m.uv7 != null && m.uv7.Length > 0,
            7 => m.uv8 != null && m.uv8.Length > 0,
            _ => false,
        };

        private static void CopyChannel(Mesh m, int src, int dst)
        {
            var data = src switch
            {
                0 => m.uv, 1 => m.uv2, 2 => m.uv3, 3 => m.uv4,
                4 => m.uv5, 5 => m.uv6, 6 => m.uv7, 7 => m.uv8, _ => null,
            };
            switch (dst)
            {
                case 0: m.uv = data; break; case 1: m.uv2 = data; break;
                case 2: m.uv3 = data; break; case 3: m.uv4 = data; break;
                case 4: m.uv5 = data; break; case 5: m.uv6 = data; break;
                case 6: m.uv7 = data; break; case 7: m.uv8 = data; break;
            }
        }
    }
}

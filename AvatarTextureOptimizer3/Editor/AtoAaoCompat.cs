// English: Optional AAO UVUsageCompabilityAPI (spelling as in AAO 1.9.17). Safe if AAO is absent.
// 中文：可选兼容 AAO 的 UVUsageCompabilityAPI（拼写以 AAO 1.9.17 为准）。未安装 AAO 时安全跳过。
using System;
using System.Reflection;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoAaoCompat
    {
        public static bool IsTexCoordUsed(SkinnedMeshRenderer smr, int channel)
        {
#if ATO_AAO
            try
            {
                return Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.IsTexCoordUsed(smr, channel);
            }
            catch (Exception e)
            {
                AtoLog.Warn("AAO UVUsageCompabilityAPI.IsTexCoordUsed failed: " + e.Message);
                return false;
            }
#else
            return Invoke("IsTexCoordUsed", smr, channel);
#endif
        }

        public static void RegisterEvacuation(SkinnedMeshRenderer smr, int original, int saved)
        {
#if ATO_AAO
            try
            {
                Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.RegisterTexCoordEvacuation(smr, original, saved);
            }
            catch (Exception e)
            {
                AtoLog.Warn("AAO RegisterTexCoordEvacuation failed: " + e.Message);
            }
#else
            Invoke("RegisterTexCoordEvacuation", smr, original, saved);
#endif
        }

        private static bool Invoke(string method, SkinnedMeshRenderer smr, int a, int b = -1)
        {
            try
            {
                var t = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor")
                         ?? Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                if (t == null) return false;
                if (b < 0)
                {
                    var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                    if (m == null) return false;
                    return (bool)m.Invoke(null, new object[] { smr, a });
                }
                else
                {
                    var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                    m?.Invoke(null, new object[] { smr, a, b });
                    return false;
                }
            }
            catch (Exception e)
            {
                AtoLog.VerboseInfo("AAO reflect " + method + ": " + e.Message);
                return false;
            }
        }
    }
}

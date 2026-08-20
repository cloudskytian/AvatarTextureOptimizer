using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Optional AAO UVUsageCompabilityAPI (spelling as in AAO).
    /// Resolved by reflection so missing AAO still compiles.
    /// 通过反射对接 AAO，未安装时跳过。
    /// </summary>
    public static class AaoCompat
    {
        public static void RegisterUvUsage(object buildContext, List<Renderer> renderers)
        {
            var t = FindType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI")
                    ?? FindType("Anatawa12.AvatarOptimizer.UVUsageCompabilityAPI");
            if (t == null)
            {
                AtoLog.Info("UVUsageCompabilityAPI type not found; skip.");
                return;
            }
            var method = t.GetMethod("RegisterUVUsage", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                AtoLog.Warn("UVUsageCompabilityAPI.RegisterUVUsage not found after reading type.");
                return;
            }
            foreach (var r in renderers)
            {
                try
                {
                    // Do not guess extra args: try (Renderer) then (BuildContext, Renderer).
                    var ps = method.GetParameters();
                    if (ps.Length == 1) method.Invoke(null, new object[] { r });
                    else if (ps.Length == 2) method.Invoke(null, new[] { buildContext, (object)r });
                    else AtoLog.Warn("Unexpected UVUsageCompabilityAPI signature; skip invoke.");
                }
                catch (Exception e)
                {
                    AtoLog.Warn("AAO UV register failed: " + e.Message);
                }
            }
        }

        static Type FindType(string full)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(full);
                if (t != null) return t;
            }
            return null;
        }
    }
}

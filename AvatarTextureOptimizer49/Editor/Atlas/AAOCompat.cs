using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Reflection adapter for AAO's UVUsageCompabilityAPI (spelling confirmed from AAO source).
    /// Pure reflection so AAO is an OPTIONAL dependency — missing AAO simply disables evacuation.
    /// / AAO UV 疏散 API 的反射适配器（拼写与 AAO 源码一致）。纯反射实现，未安装 AAO 时自动禁用。
    /// </summary>
    internal static class AAOCompat
    {
        private static MethodInfo _isUsed;
        private static MethodInfo _register;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI"))
                    .FirstOrDefault(t => t != null);
                if (type == null)
                {
                    ATOLog.Verbose("AAO not installed; UV evacuation disabled / 未安装AAO，跳过UV疏散");
                    return;
                }
                _isUsed = type.GetMethod("IsTexCoordUsed",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(SkinnedMeshRenderer), typeof(int) }, null);
                _register = type.GetMethod("RegisterTexCoordEvacuation",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) }, null);
                if (_isUsed != null && _register != null)
                    ATOLog.Info("AAO UVUsageCompabilityAPI detected / 已检测到AAO兼容API");
            }
            catch (Exception e)
            {
                ATOLog.Warning("AAO compat resolve failed: " + e.Message);
            }
        }

        internal static bool IsTexCoordUsed(SkinnedMeshRenderer smr, int channel)
        {
            Resolve();
            if (_isUsed == null) return false;
            try
            {
                return (bool)_isUsed.Invoke(null, new object[] { smr, channel });
            }
            catch
            {
                return false;
            }
        }

        internal static bool RegisterEvacuation(SkinnedMeshRenderer smr, int originalChannel, int savedChannel)
        {
            Resolve();
            if (_register == null) return false;
            try
            {
                _register.Invoke(null, new object[] { smr, originalChannel, savedChannel });
                return true;
            }
            catch (Exception e)
            {
                ATOLog.Warning($"AAO UV evacuation failed for {smr.name}: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }
    }
}

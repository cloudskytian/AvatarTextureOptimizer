// Avatar Optimizer compatibility via reflection (works whether or not AAO is installed).
// / 通过反射兼容 Avatar Optimizer（无论是否安装 AAO 均可工作）。
// Uses AAO's UVUsageCompabilityAPI: we evacuate original UVs to a spare channel before repacking,
// so AAO's UV-based features (e.g. Remove Mesh by UV Tile) keep working.
// / 使用 AAO 的 UVUsageCompatibilityAPI：重排 UV 前把原始 UV 疏散到备用通道，使 AAO 基于 UV 的功能（如按 UV 图块删网格）继续可用。

using System;
using System.Reflection;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.editor.pipeline;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>
    /// Reflection-based wrapper of Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.
    /// / Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI 的反射封装。
    /// </summary>
    public static class AaoCompat
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _register;

        private static bool Resolve()
        {
            if (_apiType != null) return true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false);
                if (t != null)
                {
                    _apiType = t;
                    _isUsed = t.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                    _register = t.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if AAO uses the given UV channel. / AAO 是否使用该 UV 通道。</summary>
        public static bool IsTexCoordUsed(Renderer renderer, int channel)
        {
            if (!Resolve()) return false;
            try
            {
                return (bool)_isUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                AtoLog.VerboseLog("AAO IsTexCoordUsed failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Tell AAO the original UVs of `originalChannel` are now stored in `savedChannel`. / 告知 AAO 原始 UV 已存入备用通道。</summary>
        public static void RegisterTexCoordEvacuation(Renderer renderer, int originalChannel, int savedChannel)
        {
            if (!Resolve()) return;
            try
            {
                _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                AtoLog.VerboseLog("AAO UV evacuation registered: ch" + originalChannel + " -> ch" + savedChannel);
            }
            catch (Exception e)
            {
                AtoLog.VerboseLog("AAO RegisterTexCoordEvacuation failed: " + e.Message);
            }
        }
    }
}

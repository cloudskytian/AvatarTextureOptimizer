// ATO — Avatar Texture Optimizer
// Optional Avatar Optimizer (AAO) integration via reflection: AAO's UVUsageCompabilityAPI
// (note the original spelling) lets ATO evacuate original UVs to a spare channel so AAO's
// UV-based optimizations keep working after ATO rewrites UVs. AAO absence is handled.
// 通过反射的可选 AAO 集成：AAO 的 UVUsageCompabilityAPI（注意原文拼写）允许 ATO 把原始 UV
// 疏散到备用通道，使 AAO 基于 UV 的优化在 ATO 重写 UV 后依然有效。未安装 AAO 时正常降级。

using System;
using System.Reflection;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Reflection wrapper over AAO's UVUsageCompabilityAPI. AAO UVUsageCompabilityAPI 的反射封装。
    /// </summary>
    public static class AAOIntegration
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _register;
        private static bool _resolved;

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _apiType = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor");
                if (_apiType == null)
                {
                    ATOLog.Verbose("[AAO] not found; UV evacuation skipped.");
                    return;
                }
                _isUsed = _apiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                _register = _apiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[AAO] reflection failed: {e.Message}");
                _apiType = null;
            }
        }

        /// <summary>True when AAO is installed and initialized. AAO 是否已安装并初始化。</summary>
        public static bool IsAvailable
        {
            get { EnsureResolved(); return _apiType != null; }
        }

        /// <summary>Whether AAO uses the given UV channel of the renderer. AAO 是否使用该渲染器的某 UV 通道。</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            EnsureResolved();
            if (_isUsed == null || renderer == null) return false;
            try { return (bool)_isUsed.Invoke(null, new object[] { renderer, channel }); }
            catch (Exception e)
            {
                ATOLog.Verbose($"[AAO] IsTexCoordUsed failed: {e.Message}");
                return false;
            }
        }

        /// <summary>Register a UV evacuation (original → saved channel). 注册 UV 疏散（原始 → 备用通道）。</summary>
        public static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            EnsureResolved();
            if (_register == null || renderer == null) return;
            try { _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel }); }
            catch (Exception e)
            {
                ATOLog.Warn($"[AAO] RegisterTexCoordEvacuation failed: {e.Message}");
            }
        }
    }
}

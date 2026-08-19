// AAOCompat — optional Avatar Optimizer integration via reflection / 可选 AAO 兼容层（反射）
// Verified: aao API-Editor asmdef is autoReferenced=false so we cannot hard-reference it;
// UVUsageCompabilityAPI (sic) lives in Anatawa12.AvatarOptimizer.API. Missing AAO ⇒ no-op.<br>
// 已验证：AAO 的 API 程序集 autoReferenced=false，必须反射；UVUsageCompabilityAPI（拼写如此）
// 位于 Anatawa12.AvatarOptimizer.API；未安装 AAO 时全部空操作。
using System;
using System.Reflection;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class AAOCompat
    {
        private static bool _init;
        private static MethodInfo _isTexCoordUsed;
        private static MethodInfo _registerEvacuation;

        internal static bool Available
        {
            get
            {
                EnsureInit();
                return _isTexCoordUsed != null && _registerEvacuation != null;
            }
        }

        private static void EnsureInit()
        {
            if (_init) return;
            _init = true;
            try
            {
                var type = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor");
                if (type == null) return; // AAO not installed / 未安装
                _isTexCoordUsed = type.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                _registerEvacuation = type.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                if (Available) ATOLog.V("AAO UVUsageCompabilityAPI detected");
            }
            catch (Exception e) { ATOLog.V($"AAO detection failed: {e.Message}"); }
        }

        /// <summary>Does AAO use this UV channel on this renderer? / AAO 是否使用该UV通道。</summary>
        internal static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!Available || renderer == null) return false;
            try { return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel }); }
            catch (Exception e) { ATOLog.Warn($"AAO IsTexCoordUsed failed: {e.Message}"); return false; }
        }

        /// <summary>Register UV evacuation so AAO reads original UVs from the saved channel. / 注册UV疏散，使 AAO 从保存通道读取原UV。</summary>
        internal static bool RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!Available || renderer == null) return false;
            try
            {
                _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                return true;
            }
            catch (Exception e)
            {
                ATOLog.Warn($"AAO RegisterTexCoordEvacuation({renderer.name}, ch{originalChannel}→ch{savedChannel}) failed: {e.Message}");
                return false;
            }
        }
    }
}

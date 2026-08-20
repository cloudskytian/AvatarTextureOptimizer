// ATOAAOCompat.cs — Avatar Optimizer 兼容桥（反射调用，AAO 未安装时安全跳过）/ AAO compatibility bridge (reflection; safe when AAO is absent).
// 说明：兼容 AAO 的 UVUsageCompabilityAPI（AAO 原文拼写如此，已读 AAO 1.9.17 源码 API-Editor/UVUsageCompabilityAPI.cs 验证）：
//  - IsTexCoordUsed(renderer, channel)：AAO 是否可能使用该 UV 通道（如 Remove Mesh by Mask）
//  - RegisterTexCoordEvacuation(renderer, originalChannel, savedChannel)：告知 AAO 原 UV 已保存到另一通道
// 使用反射避免编译期依赖（用户可能未安装 AAO）。
// Note: bridges AAO's UVUsageCompabilityAPI (spelling verified against AAO 1.9.17 source API-Editor/UVUsageCompabilityAPI.cs):
// IsTexCoordUsed / RegisterTexCoordEvacuation. Reflection avoids a compile-time dependency (AAO may be absent).

using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>AAO 兼容桥。/ AAO compatibility bridge.</summary>
    internal static class ATOAAOCompat
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _registerEvac;
        private static bool _resolved;

        /// <summary>AAO（API-Editor 程序集）是否可用。/ Whether AAO's API-Editor assembly is available.</summary>
        public static bool Available
        {
            get
            {
                Resolve();
                return _apiType != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "com.anatawa12.avatar-optimizer.api.editor");
                if (assembly == null) return;
                _apiType = assembly.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                if (_apiType == null) return;
                _isUsed = _apiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                _registerEvac = _apiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                if (_isUsed == null || _registerEvac == null) _apiType = null;
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"AAO compat resolution failed: {e.Message}");
                _apiType = null;
            }
        }

        /// <summary>AAO 是否可能使用某渲染器的某 UV 通道。/ Whether AAO may use a UV channel of a renderer.</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!Available) return false;
            try
            {
                return (bool)_isUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                ATOLog.Warning($"AAO IsTexCoordUsed failed: {e.Message}");
                return true; // 保守：失败按"使用"处理 / conservative: treat as used on failure
            }
        }

        /// <summary>注册 UV 迁移（原通道已保存到目标通道）。/ Register UV evacuation (original channel saved to the target channel).</summary>
        public static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!Available) return;
            try
            {
                _registerEvac.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
            }
            catch (Exception e)
            {
                ATOLog.Warning($"AAO RegisterTexCoordEvacuation failed: {e.Message} (AAO UV 迁移注册失败)");
            }
        }
    }
}

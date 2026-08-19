using System;
using System.Reflection;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.NDMF
{
    // AAO UVUsageCompatibilityAPI 反射适配器（AAO 为可选依赖，未安装时自动降级）。
    // Reflection adapter for AAO's UVUsageCompabilityAPI (AAO is optional; degrades gracefully when absent).
    //
    // API 语义（AAO 1.8+；类名拼写 "Compability" 为 AAO 原文）：
    // - IsTexCoordUsed(renderer, channel)：AAO 是否会在其优化中使用该 UV 通道（0~7）。
    // - RegisterTexCoordEvacuation(renderer, original, saved)：告知 AAO 原始 UV 已备份到 saved 通道；
    //   AAO 处理时改用备份通道，并在其流程结束后删除备份通道。若 saved 通道本身被 AAO 使用则抛异常。
    // 该 API 设计允许假阴性（返回 false 时不使用；返回 true 时可能不使用），因此异常时按“未使用”处理是安全的。
    internal static class AAOUVUsageAdapter
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _register;
        private static bool _initialized;

        public static bool Available
        {
            get
            {
                Init();
                return _apiType != null;
            }
        }

        private static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "com.anatawa12.avatar-optimizer.api.editor") continue;
                    var t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                    if (t == null) continue;
                    var m1 = t.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                    var m2 = t.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                    if (m1 == null || m2 == null) continue;
                    _apiType = t;
                    _isUsed = m1;
                    _register = m2;
                    break;
                }
            }
            catch (Exception e)
            {
                ATOLog.Warn("AAO UVUsageCompabilityAPI 初始化失败 / init failed: " + e.Message);
                _apiType = null;
            }
        }

        // 返回 null 表示不可用/异常；调用方按“AAO 未使用该通道”处理（API 设计上允许假阴性，安全）。
        public static bool? IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!Available) return null;
            try
            {
                return (bool)_isUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                ATOLog.Warn("AAO IsTexCoordUsed 调用失败 / failed: " + e.Message);
                return null;
            }
        }

        // 注册 UV 备份通道；失败时返回原因（如 saved 通道被 AAO 使用）。
        public static bool TryRegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel, out string error)
        {
            error = null;
            if (!Available) return false;
            try
            {
                _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                return true;
            }
            catch (Exception e)
            {
                error = e.InnerException != null ? e.InnerException.Message : e.Message;
                return false;
            }
        }
    }
}

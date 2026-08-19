// Avatar Texture Optimizer / 头像贴图优化器
// Optional AAO (AvatarOptimizer) compatibility via reflection, so ATO works
// whether or not AAO is installed.
// 通过反射实现可选的 AAO 兼容（用户未安装 AAO 时也能正常工作）。
//
// Verified against AAO 1.9.17:
//   Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI
//     static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
//     static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
// The implementation registers an InternalEvacuateUVChannel component on the
// renderer GameObject; AAO then reads the evacuated UV layer and restores it
// after its own processing. Registration is safe on NDMF build copies.
// 已对照 AAO 1.9.17 核实上述 API；其内部实现是给渲染器挂
// InternalEvacuateUVChannel 组件，AAO 处理后恢复。在 NDMF 构建副本上调用安全。

using System;
using System.Reflection;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Reflection bridge to AAO's UVUsageCompabilityAPI. / AAO UVUsageCompabilityAPI 的反射桥。</summary>
    public static class ATOAAOCompat
    {
        private static readonly Type _apiType;
        private static readonly MethodInfo _isUsed;
        private static readonly MethodInfo _register;

        static ATOAAOCompat()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try
                {
                    t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                }
                catch
                {
                    // ignore assemblies that fail type lookup / 忽略类型查找失败的程序集
                }
                if (t == null) continue;
                _apiType = t;
                _isUsed = t.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                _register = t.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                break;
            }
        }

        /// <summary>True when AAO is installed and both API methods exist. / AAO 已安装且两个 API 都存在。</summary>
        public static bool IsInstalled => _apiType != null && _isUsed != null && _register != null;

        /// <summary>Whether AAO uses the given UV channel on this renderer. / AAO 是否使用该渲染器的指定 UV 通道。</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!IsInstalled || renderer == null) return false;
            try
            {
                return (bool)_isUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                ATOLog.Warn($"AAO IsTexCoordUsed failed on {renderer.name}: {e.Message}");
                // Fail closed: assume used (=> fall back from atlas for this channel).
                // 保守失败：视为被占用（该通道退回非图集路径）。
                return true;
            }
        }

        /// <summary>Register UV evacuation for AAO. / 向 AAO 登记 UV 通道转移。</summary>
        public static bool RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel, out string error)
        {
            error = null;
            if (!IsInstalled || renderer == null)
            {
                error = "aao-not-installed";
                return false;
            }
            try
            {
                _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                return true;
            }
            catch (TargetInvocationException tie)
            {
                error = tie.InnerException?.Message ?? tie.Message;
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// Choose a safe evacuation channel: not used by AAO, not used by any of
        /// this mesh's usages in our model, and not equal to the original channel.
        /// 选择安全的转移通道：未被 AAO 占用、在模型中该网格的其他用途未占用、且不等于原通道。
        /// </summary>
        public static bool TryPickEvacuationChannel(
            SkinnedMeshRenderer renderer, int originalChannel, Func<int, bool> channelUsedByModel, out int channel)
        {
            for (int c = 7; c >= 1; c--) // 1 that high channels are usually free / 高序号通道通常空闲
            {
                if (c == originalChannel) continue;
                if (IsTexCoordUsed(renderer, c)) continue;
                if (channelUsedByModel != null && channelUsedByModel(c)) continue;
                channel = c;
                return true;
            }
            channel = -1;
            return false;
        }
    }
}

using System;
using System.Reflection;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.AAO
{
    /// <summary>
    /// AAO UVUsageCompabilityAPI 兼容层（反射实现，AAO 未安装时安全跳过）。
    /// 流程：对 AAO 使用中的 UV 通道，先把原 UV 拷贝到空闲通道并注册撤离，
    /// 再让本工具重排原通道；AAO 构建时会用撤离的原始 UV 做 RemoveMeshByMask/UVTile 等。
    /// </summary>
    public static class UVUsageCompat
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _register;

        private static bool _resolved;
        private static bool _available;

        public static bool Available
        {
            get
            {
                Resolve();
                return _available;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _apiType = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, Anatawa12.AvatarOptimizer.API");
                if (_apiType == null)
                {
                    // 按程序集名再试一次
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name.Contains("AvatarOptimizer"))
                        {
                            _apiType = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                            if (_apiType != null) break;
                        }
                    }
                }
                if (_apiType == null) return;
                _isUsed = _apiType.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                _register = _apiType.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                _available = _isUsed != null && _register != null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] UVUsageCompat resolve failed (AAO likely absent): {e.Message}");
                _available = false;
            }
        }

        /// <summary>AAO 是否使用指定 UV 通道。</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            Resolve();
            if (!_available || renderer == null) return false;
            try
            {
                return (bool)_isUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] IsTexCoordUsed failed: {e.Message}");
                return false;
            }
        }

        /// <summary>注册 UV 撤离（原通道 → 保存通道）。返回是否成功。</summary>
        public static bool RegisterEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            Resolve();
            if (!_available || renderer == null) return false;
            try
            {
                _register.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] RegisterTexCoordEvacuation failed (channel {savedChannel} may be used by AAO): {e.Message}");
                return false;
            }
        }

        /// <summary>寻找一个 AAO 未使用的空闲通道（0..7），找不到返回 -1。</summary>
        public static int FindFreeChannel(SkinnedMeshRenderer renderer, int exclude0, int exclude1 = -1)
        {
            for (int c = 0; c < 8; c++)
            {
                if (c == exclude0 || c == exclude1) continue;
                if (!IsTexCoordUsed(renderer, c)) return c;
            }
            return -1;
        }
    }
}

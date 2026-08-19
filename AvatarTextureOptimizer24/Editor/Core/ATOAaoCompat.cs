// ============================================================================
// ATOAaoCompat.cs — AAO UV 疏散兼容 / AAO UV evacuation compatibility
// (EN) Uses AAO's UVUsageCompabilityAPI (note the intentional spelling) via
//      reflection so ATO has no hard dependency on AAO. Before remapping a UV
//      channel, if AAO uses that channel (RemoveMeshByMask/ByUVTile), the
//      original UVs are evacuated to a spare channel and registered with AAO.
// (ZH) 通过反射调用 AAO 的 UVUsageCompabilityAPI（注意原拼写），使 ATO 不硬依赖
//      AAO。重映射 UV 通道前，若 AAO 使用该通道（RemoveMeshByMask/ByUVTile），
//      将原始 UV 疏散到空闲通道并注册给 AAO。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOAaoCompat
    {
        private static Type _apiType;
        private static MethodInfo _isUsed;
        private static MethodInfo _registerEvacuation;
        private static bool _resolved;

        private static bool Resolve()
        {
            if (_resolved) return _apiType != null;
            _resolved = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                if (t == null) continue;
                _apiType = t;
                _isUsed = t.GetMethod("IsTexCoordUsed", new[] { typeof(SkinnedMeshRenderer), typeof(int) });
                _registerEvacuation = t.GetMethod("RegisterTexCoordEvacuation", new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) });
                ATOLog.VerboseLog("[aao] UVUsageCompabilityAPI resolved via reflection");
                return true;
            }

            ATOLog.VerboseLog("[aao] AAO not present; UV evacuation skipped");
            return false;
        }

        public static bool Available => Resolve();

        /// <summary>(EN) True if AAO uses the given UV channel on the renderer. (ZH) AAO 是否使用该渲染器的某 UV 通道。</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!Resolve()) return false;
            try { return (bool)_isUsed.Invoke(null, new object[] { renderer, channel }); }
            catch (Exception e) { ATOLog.Warn("[aao] IsTexCoordUsed failed: " + e.Message); return false; }
        }

        /// <summary>(EN) Find a spare UV channel (unused by AAO and mesh) for evacuation. (ZH) 找一个空闲 UV 通道（AAO 与网格都未用）用于疏散。</summary>
        public static int FindSpareChannel(SkinnedMeshRenderer renderer, int originalChannel, bool[] meshChannels)
        {
            for (int c = 7; c >= 0; c--)
            {
                if (c == originalChannel) continue;
                if (IsTexCoordUsed(renderer, c)) continue;
                if (meshChannels[c]) continue; // 网格已用该通道 / mesh already uses this channel
                return c;
            }
            return -1;
        }

        /// <summary>(EN) Copy a mesh UV channel to another channel. (ZH) 拷贝网格某 UV 通道到另一通道。</summary>
        public static void CopyUvChannel(Mesh mesh, int from, int to)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(from, uvs);
            mesh.SetUVs(to, uvs);
        }

        /// <summary>(EN) Register evacuation with AAO for a renderer. (ZH) 为某渲染器向 AAO 注册疏散。</summary>
        public static bool RegisterEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!Resolve()) return false;
            try
            {
                _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
                return true;
            }
            catch (Exception e)
            {
                ATOLog.Warn("[aao] RegisterTexCoordEvacuation failed: " + e.Message);
                return false;
            }
        }
    }
}

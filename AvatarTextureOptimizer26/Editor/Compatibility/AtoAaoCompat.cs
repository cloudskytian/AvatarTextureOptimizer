using System;
using System.Reflection;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Optional AAO UVUsageCompabilityAPI via reflection. Safe when AAO is not installed.
    /// 通过反射可选调用 AAO UVUsageCompabilityAPI。未安装 AAO 时安全。
    /// API is SkinnedMeshRenderer-only (read from AAO 1.9.17 source).
    /// </summary>
    public static class AtoAaoCompat
    {
        private static Type _api;
        private static MethodInfo _isUsed;
        private static MethodInfo _evac;
        private static bool _init;

        private static void Ensure()
        {
            if (_init) return;
            _init = true;
            _api = Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor")
                   ?? Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, Anatawa12.AvatarOptimizer.API.Editor")
                   ?? FindType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
            if (_api == null)
            {
                AtoLog.VerboseInfo("AAO UVUsageCompabilityAPI not present — skipping.");
                return;
            }
            _isUsed = _api.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
            _evac = _api.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            AtoLog.Info("AAO UVUsageCompabilityAPI hooked / 已挂钩 AAO UV 兼容 API");
        }

        private static Type FindType(string full)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType(full);
                    if (t != null) return t;
                }
                catch { /* skip */ }
            }
            return null;
        }

        public static bool IsUsed(SkinnedMeshRenderer smr, int channel)
        {
            Ensure();
            if (_isUsed == null || smr == null) return false;
            try { return (bool)_isUsed.Invoke(null, new object[] { smr, channel }); }
            catch (Exception e)
            {
                AtoLog.Warn("AAO IsTexCoordUsed failed: " + e.Message);
                return false;
            }
        }

        public static bool TryEvacuate(SkinnedMeshRenderer smr, int original, int saved)
        {
            Ensure();
            if (_evac == null || smr == null) return false;
            try
            {
                _evac.Invoke(null, new object[] { smr, original, saved });
                AtoLog.Info($"AAO evacuate UV{original} -> UV{saved} on {smr.name}");
                return true;
            }
            catch (Exception e)
            {
                AtoLog.Warn($"AAO evacuate failed UV{original}->{saved} on {smr.name}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// If AAO uses this channel, copy original UVs to a free channel and register evacuation before remap.
        /// 若 AAO 使用该通道，则在重映射前把原 UV 拷到空闲通道并登记疏散。
        /// </summary>
        public static void EvacuateIfNeeded(SkinnedMeshRenderer smr, Mesh mesh, int channel)
        {
            if (smr == null || mesh == null) return;
            if (!IsUsed(smr, channel)) return;
            for (var saved = 7; saved >= 0; saved--)
            {
                if (saved == channel) continue;
                if (IsUsed(smr, saved)) continue;
                var uvs = new System.Collections.Generic.List<Vector2>();
                mesh.GetUVs(channel, uvs);
                if (uvs.Count == 0) return;
                mesh.SetUVs(saved, uvs);
                if (TryEvacuate(smr, channel, saved)) return;
            }
            AtoLog.Warn($"No free UV channel to evacuate UV{channel} on {smr.name}");
        }
    }
}

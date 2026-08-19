using System;
using System.Reflection;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Reflection bridge to Avatar Optimizer's public API (optional dependency).
    /// 到 Avatar Optimizer 公开 API 的反射桥（可选依赖）。
    /// </summary>
    public static class AAOCompatibility
    {
        private static Type _uvApi;
        private static MethodInfo _isTexCoordUsed;
        private static MethodInfo _registerEvacuation;
        private static bool _resolved = false;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var asm = Assembly.Load("com.anatawa12.avatar-optimizer.api.editor");
                if (asm == null) return;
                _uvApi = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                if (_uvApi == null) return;
                _isTexCoordUsed = _uvApi.GetMethod("IsTexCoordUsed",
                    BindingFlags.Public | BindingFlags.Static);
                _registerEvacuation = _uvApi.GetMethod("RegisterTexCoordEvacuation",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception e)
            {
                ATOLogger.InfoDetail("AAO not available (reflection): " + e.Message);
            }
        }

        public static bool Available
        {
            get { Resolve(); return _uvApi != null; }
        }

        /// <summary>Is the UV channel used by AAO (e.g. RemoveMeshByMask/ByUVTile)? / AAO 是否使用该 UV 通道？</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            Resolve();
            if (_isTexCoordUsed == null) return false;
            try { return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel }); }
            catch { return false; }
        }

        /// <summary>
        /// Evacuate original UV of a channel to a spare channel before rewriting, so AAO's
        /// RemoveMeshByMask/ByUVTile keep working. / 改写前将通道原始 UV 疏散到空闲通道，
        /// 使 AAO 的 RemoveMeshByMask/ByUVTile 继续可用。
        /// </summary>
        public static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int original, int saved)
        {
            Resolve();
            if (_registerEvacuation == null) return;
            try { _registerEvacuation.Invoke(null, new object[] { renderer, original, saved }); }
            catch (Exception e) { ATOLogger.Warn("AAO UV evacuation failed: " + e.Message, renderer); }
        }
    }
}

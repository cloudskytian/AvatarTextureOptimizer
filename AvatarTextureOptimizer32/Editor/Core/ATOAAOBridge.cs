using System;
using System.Reflection;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 通过反射调用 AAO 的 UVUsageCompabilityAPI（支持未安装 AAO 时优雅降级）。
    /// 注意：该 API 仅接受 SkinnedMeshRenderer，且仅限构建期使用。
    ///
    /// Reflection bridge to AAO's UVUsageCompabilityAPI (graceful when AAO is not installed).
    /// </summary>
    public static class ATOAAOBridge
    {
        private const string TypeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor";

        private static Type _type;
        private static MethodInfo _isTexCoordUsed;
        private static MethodInfo _registerEvacuation;
        private static bool _resolved = false;

        public static bool Available
        {
            get { Resolve(); return _type != null; }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _type = Type.GetType(TypeName);
                if (_type == null) return;
                _isTexCoordUsed = _type.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                _registerEvacuation = _type.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
            }
            catch
            {
                _type = null;
            }
        }

        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!Available) return false;
            try { return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel }); }
            catch { return false; }
        }

        public static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!Available) return;
            try { _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, savedChannel }); }
            catch (Exception e) { ATOLogger.Warn($"AAO evacuation failed: {e.Message}"); }
        }
    }
}

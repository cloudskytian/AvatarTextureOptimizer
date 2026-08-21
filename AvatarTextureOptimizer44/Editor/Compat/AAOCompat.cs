// AAOCompat.cs - Optional AvatarOptimizer (AAO) integration via reflection so users without AAO keep working.
// 经反射可选集成 AAO，未安装AAO的用户不受影响。
// Referenced API (read from com.anatawa12.avatar-optimizer 1.9.7 sources, exact name verified):
//   Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI  (note: "Compability" is AAO's own spelling)
//     static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
//     static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
// 引用的API（读自AAO 1.9.17源码，名称已核实）：UVUsageCompabilityAPI.IsTexCoordUsed / RegisterTexCoordEvacuation
using System;
using System.Reflection;
using Fosa.ATO.Editor.Core;
using UnityEngine;

namespace Fosa.ATO.Editor.Compat
{
    public static class AAOCompat
    {
        private const string TypeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor";
        private static Type _type;
        private static bool _searched;

        private static Type FindType()
        {
            if (_searched) return _type;
            _searched = true;
            try
            {
                _type = Type.GetType(TypeName, false);
                if (_type == null)
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _type = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false);
                        if (_type != null) break;
                    }
            }
            catch (Exception e) { ATOLog.Detail("AAO type lookup failed / AAO类型查找失败: " + e.Message); }
            if (_type != null) ATOLog.Info("AAO detected; UV usage API available / 检测到AAO，UV用量API可用");
            return _type;
        }

        /// <summary>Is AAO installed? / 是否安装了AAO？</summary>
        public static bool Available => FindType() != null;

        /// <summary>AAO uses this UV channel (RemoveMeshByUVTile etc.)? / AAO是否使用该UV通道？</summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer smr, int channel)
        {
            var t = FindType();
            if (t == null) return false;
            try
            {
                var mi = t.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                return mi != null && (bool)mi.Invoke(null, new object[] { smr, channel });
            }
            catch (Exception e) { ATOLog.Detail("IsTexCoordUsed failed / 调用失败: " + e.Message); return false; }
        }

        /// <summary>Register UV evacuation (reserved for future use; we currently rewrite values in place). / 登记UV迁移（预留；当前只就地改写数值）。</summary>
        public static void RegisterTexCoordEvacuation(SkinnedMeshRenderer smr, int original, int saved)
        {
            var t = FindType();
            if (t == null) return;
            try
            {
                var mi = t.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                mi?.Invoke(null, new object[] { smr, original, saved });
            }
            catch (Exception e) { ATOLog.Detail("RegisterTexCoordEvacuation failed / 调用失败: " + e.Message); }
        }
    }
}

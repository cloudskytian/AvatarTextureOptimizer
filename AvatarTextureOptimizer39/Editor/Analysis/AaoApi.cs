// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Reflection;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// Reflection-based wrapper for AAO's optional UVUsageCompabilityAPI
    /// (namespace Anatawa12.AvatarOptimizer.API). Used so ATO compiles and runs whether
    /// or not Avatar Optimizer is installed. Note: AAO spells it "Compability" (not a typo).
    ///
    /// 基于反射的 AAO 可选 UVUsageCompabilityAPI 封装（命名空间
    /// Anatawa12.AvatarOptimizer.API）。用于 ATO 在是否安装 AAO 的情况下都能编译运行。
    /// 注意：AAO 原文拼写为 "Compability"（非拼写错误）。
    /// </summary>
    public sealed class AaoApi
    {
        private readonly MethodInfo _isUsed;
        private readonly MethodInfo _registerEvac;

        private AaoApi(Type apiType)
        {
            _isUsed = apiType.GetMethod("IsTexCoordUsed",
                BindingFlags.Public | BindingFlags.Static);
            _registerEvac = apiType.GetMethod("RegisterTexCoordEvacuation",
                BindingFlags.Public | BindingFlags.Static);
        }

        /// <summary>Load the API via reflection, or null if AAO is not installed. 反射加载，未安装则 null。</summary>
        public static AaoApi TryLoad()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
                    if (t != null) return new AaoApi(t);
                }
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"AAO API load failed: {e.Message}");
            }
            return null;
        }

        public bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (_isUsed == null) return false;
            try { return (bool)_isUsed.Invoke(null, new object[] { renderer, channel }); }
            catch { return false; }
        }

        public void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (_registerEvac == null) return;
            try { _registerEvac.Invoke(null, new object[] { renderer, originalChannel, savedChannel }); }
            catch (Exception e) { ATOLog.Verbose($"AAO evacuation failed: {e.Message}"); }
        }
    }
}

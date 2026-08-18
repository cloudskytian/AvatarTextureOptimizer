// AAOCompat.cs / AAOCompat.cs
// Avatar Optimizer UVUsageCompabilityAPI integration.
// Registers UV channel evacuation so AAO's RemoveMeshByUVTile/Mask features don't break our repacked UVs.
// Avatar Optimizer UVUsageCompabilityAPI集成。注册UV通道疏散，让AAO的RemoveMeshByUVTile/Mask功能不会破坏我们重打包的UV。

using System;
using System.Collections.Generic;
using System.Reflection;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Util
{
    /// <summary>
    /// Best-effort integration with AAO's UVUsageCompabilityAPI.
    /// Uses reflection so the project compiles even without AAO installed.
    /// 与AAO的UVUsageCompabilityAPI尽力集成。使用反射，使未安装AAO时项目仍能编译。
    /// </summary>
    public static class AAOCompat
    {
        private static Type _apiType;
        private static MethodInfo _isUsedMethod;
        private static MethodInfo _registerEvacMethod;
        private static bool _tried = false;
        private static bool _available = false;

        public static bool Available
        {
            get
            {
                TryInit();
                return _available;
            }
        }

        private static void TryInit()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                // Try multiple possible assembly names / 尝试多个可能的程序集名
                Assembly aaoAsm = null;
                foreach (var name in new[] {
                    "com.anatawa12.avatar-optimizer.api.editor",
                    "com.anatawa12.avatar-optimizer.editor",
                    "Anatawa12.AvatarOptimizer.Editor",
                    "com.anatawa12.avatar-optimizer.runtime",
                    "Anatawa12.AvatarOptimizer.Runtime"
                })
                {
                    try { aaoAsm = Assembly.Load(name); if (aaoAsm != null) break; } catch { continue; }
                }
                if (aaoAsm == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.FullName.IndexOf("AvatarOptimizer", StringComparison.OrdinalIgnoreCase) >= 0
                            && asm.FullName.IndexOf("API", StringComparison.OrdinalIgnoreCase) < 0)
                        { aaoAsm = asm; break; }
                    }
                }
                if (aaoAsm == null) return;
                _apiType = aaoAsm.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI", false);
                if (_apiType == null) return;
                _isUsedMethod = _apiType.GetMethod("IsTexCoordUsed", new[] { typeof(SkinnedMeshRenderer), typeof(int) });
                _registerEvacMethod = _apiType.GetMethod("RegisterTexCoordEvacuation", new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) });
                _available = _isUsedMethod != null && _registerEvacMethod != null;
            }
            catch
            {
                _available = false;
            }
        }

        /// <summary>
        /// If AAO is present and original UV channel is used by AAO features, evacuate it to a free channel
        /// before we repack UV0. Returns the saved channel index, or -1 if no evacuation needed.
        /// 若AAO存在且原UV通道被AAO功能使用，在我们重打包UV0前将其疏散到空闲通道。
        /// </summary>
        public static int TryEvacuateChannelIfNeeded(SkinnedMeshRenderer smr, int originalChannel)
        {
            if (!Available) return -1;
            try
            {
                bool used = (bool)_isUsedMethod.Invoke(null, new object[] { smr, originalChannel });
                if (!used) return -1;
                for (int ch = 7; ch >= 0; ch--)
                {
                    if (ch == originalChannel) continue;
                    bool chUsed = (bool)_isUsedMethod.Invoke(null, new object[] { smr, ch });
                    if (!chUsed)
                    {
                        _registerEvacMethod.Invoke(null, new object[] { smr, originalChannel, ch });
                        return ch;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] AAO compat failed: {e.Message}");
            }
            return -1;
        }

        /// <summary>
        /// Register UV evacuation for all renderers in the analysis.
        /// 为分析结果中的所有渲染器注册UV疏散。
        /// </summary>
        public static void RegisterEvacuation(AvatarAnalysisResult analysis)
        {
            if (!Available) return;
            var registeredChannels = new HashSet<(SkinnedMeshRenderer, int)>();
            foreach (var re in analysis.Renderers)
            {
                if (re.Skinned == null) continue;
                // Determine which UV channels we rewrote / 确定我们重写了哪些UV通道
                var channels = new HashSet<int>();
                foreach (var isl in analysis.Islands)
                {
                    if (isl.RendererEntry == re && isl.AssignedAtlas != null && !isl.IsWhitelisted)
                        channels.Add(isl.UVChannel);
                }
                foreach (var ch in channels)
                {
                    var key = (re.Skinned, ch);
                    if (registeredChannels.Contains(key)) continue;
                    int savedTo = TryEvacuateChannelIfNeeded(re.Skinned, ch);
                    if (savedTo >= 0) registeredChannels.Add(key);
                }
            }
        }
    }
}

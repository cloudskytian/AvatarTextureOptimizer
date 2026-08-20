using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Isolated reflection-based integration with Avatar Optimizer's UVUsageCompabilityAPI. /
    /// 与 Avatar Optimizer 的 UVUsageCompabilityAPI 的反射隔离集成。
    ///
    /// AAO is an OPTIONAL dependency. API verified against AAO 1.9.17 source
    /// (API-Editor/UVUsageCompabilityAPI.cs): static class
    /// Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI in assembly
    /// "com.anatawa12.avatar-optimizer.api.editor" with:
    ///   bool IsTexCoordUsed(SkinnedMeshRenderer, int channel)  (channel 0..7)
    ///   void RegisterTexCoordEvacuation(SkinnedMeshRenderer, int originalChannel, int savedChannel)
    /// The evacuation contract (from AAO's EvacuateProcessors.cs): WE must copy the original UV
    /// data to the saved channel on the mesh, then register; AAO swaps the channels while its
    /// UV-dependent passes run and restores our new UV + deletes the saved channel afterwards. /
    /// AAO 是可选依赖。API 已对照 AAO 1.9.17 源码核实。疏散契约（来自 AAO EvacuateProcessors.cs）：
    /// 我方把原始 UV 拷到 saved 通道并注册；AAO 在其依赖 UV 的 pass 期间交换通道，最后恢复我方新 UV
    /// 并删除 saved 通道。
    /// </summary>
    internal static class AtoAaoIntegration
    {
        private const string AssemblyName = "com.anatawa12.avatar-optimizer.api.editor";
        private const string TypeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI";

        private static Type _apiType;
        private static MethodInfo _isTexCoordUsed;
        private static MethodInfo _registerEvacuation;
        private static bool _resolved;

        private static Type ApiType
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    try
                    {
                        _apiType = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => a.GetName().Name == AssemblyName)
                            .Select(a => a.GetType(TypeName))
                            .FirstOrDefault(t => t != null);
                        if (_apiType != null)
                        {
                            _isTexCoordUsed = _apiType.GetMethod("IsTexCoordUsed",
                                BindingFlags.Public | BindingFlags.Static);
                            _registerEvacuation = _apiType.GetMethod("RegisterTexCoordEvacuation",
                                BindingFlags.Public | BindingFlags.Static);
                        }
                    }
                    catch (Exception)
                    {
                        _apiType = null;
                    }
                }
                return _apiType;
            }
        }

        /// <summary>Whether Avatar Optimizer (with the API assembly) is installed. / AAO（含 API 程序集）是否已安装。</summary>
        public static bool IsAaoInstalled => ApiType != null && _isTexCoordUsed != null && _registerEvacuation != null;

        /// <summary>
        /// Whether AAO will use the given UV channel of the renderer (must evacuate if we rewrite it). /
        /// AAO 是否会使用渲染器的指定 UV 通道（若我方改写该通道则必须疏散）。
        /// </summary>
        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            if (!IsAaoInstalled || renderer == null || channel is < 0 or > 7) return false;
            try
            {
                return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                AtoLog.Warn($"AAO IsTexCoordUsed failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Register UV evacuation for a channel we rewrote. Throws InvalidOperationException if the
        /// saved channel is itself used by AAO. Call AFTER copying the original UV to savedChannel. /
        /// 为被改写的通道注册疏散。若 savedChannel 被 AAO 使用会抛 InvalidOperationException。
        /// 须在把原始 UV 拷入 savedChannel 之后调用。
        /// </summary>
        public static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)
        {
            if (!IsAaoInstalled || renderer == null) return;
            _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, savedChannel });
        }
    }
}

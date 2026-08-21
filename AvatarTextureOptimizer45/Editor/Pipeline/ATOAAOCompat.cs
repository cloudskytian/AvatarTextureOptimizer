using System;
using System.Reflection;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// AAO UVUsageCompabilityAPI 兼容(通过反射, 不硬依赖AAO) / AAO UVUsageCompabilityAPI compatibility
    /// (via reflection — no hard dependency on AAO).
    ///
    /// AAO 可能使用网格的UV通道(如 Remove Mesh by Mask)。当我们改写某通道的UV前, 若 AAO 需要该通道,
    /// 应先把原始UV疏散(复制)到一个空闲通道, 并调用 RegisterTexCoordEvacuation 告知 AAO;
    /// AAO 处理后会自动移除被疏散的通道。未安装AAO或API未初始化时直接跳过。
    /// AAO may use mesh UV channels (e.g. Remove Mesh by Mask). Before rewriting a channel, if AAO needs it,
    /// we evacuate (copy) the original UVs to a free channel and call RegisterTexCoordEvacuation; AAO removes
    /// the evacuated channel afterwards. Skipped when AAO is absent or its API is not initialized.
    /// </summary>
    internal static class ATOAAOCompat
    {
        private const string TypeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor";
        private static Type _apiType;
        private static bool _checked;
        private static bool _available;

        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _apiType = Type.GetType(TypeName);
                    _available = _apiType != null;
                }

                return _available;
            }
        }

        /// <summary>
        /// 若AAO需要该渲染器的该UV通道, 将原始UV疏散到空闲通道并注册。
        /// If AAO needs this UV channel on this renderer, evacuate the original UVs to a free channel and register.
        /// </summary>
        public static void EvacuateIfNeeded(SkinnedMeshRenderer renderer, Mesh originalMesh, Mesh workingMesh, int channel)
        {
            if (!Available || renderer == null || workingMesh == null) return;

            try
            {
                bool used = (bool)CallStatic("IsTexCoordUsed", new object[] { renderer, channel });
                if (!used) return;

                // 找空闲通道 / find a free channel
                int free = -1;
                for (int c = 0; c < 8; c++)
                {
                    if (c == channel) continue;
                    if (!workingMesh.HasVertexAttribute(VertexAttribute.TexCoord0 + c))
                    {
                        free = c;
                        break;
                    }
                }

                if (free < 0)
                {
                    ATOLog.Warn($"AAO 需要UV通道{channel}但无空闲通道可疏散, 跳过疏散(AAO相关功能可能受影响) / AAO needs UV channel {channel} but no free channel exists; skipping evacuation");
                    return;
                }

                var uvs = new System.Collections.Generic.List<Vector2>();
                originalMesh.GetUVs(channel, uvs);
                workingMesh.SetUVs(free, uvs);

                CallStatic("RegisterTexCoordEvacuation", new object[] { renderer, channel, free });
                ATOLog.InfoVerbose($"AAO UV疏散 / AAO UV evacuation: {renderer.name} ch{channel} -> ch{free}");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is InvalidOperationException)
            {
                // API 未初始化 = AAO 本次构建未启用 / API not initialized = AAO not active this build
            }
            catch (Exception e)
            {
                ATOLog.Warn($"AAO UV疏散失败 / AAO evacuation failed for {renderer.name}: {e.Message}");
            }
        }

        private static object CallStatic(string method, object[] args)
        {
            var m = _apiType.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            return m.Invoke(null, args);
        }
    }
}

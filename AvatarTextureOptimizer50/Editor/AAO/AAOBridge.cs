// -----------------------------------------------------------------------------
// AAOBridge.cs — main-assembly side of the AAO bridge (no hard dependency).
// AAOBridge.cs —— AAO 桥接的主程序集侧（无硬依赖）。
//
// The optional bridge assembly (net.fosa.avatar-texture-optimizer.aao-bridge) sets the
// hooks below when AAO is installed; without AAO everything degrades to no-ops.
// 可选桥接程序集在安装了 AAO 时设置以下钩子；未安装时全部安全退化为空操作。
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Hooks set by the optional AAO bridge assembly (public for cross-assembly
    /// access; see Editor/AssemblyInfo.cs InternalsVisibleTo as well).
    /// 由可选 AAO 桥接程序集设的钩子（跨程序集需 public）。</summary>
    public static class AAOBridgeHooks
    {
        public static Func<SkinnedMeshRenderer, int, bool> ChannelUsed;
        public static Action<SkinnedMeshRenderer, int, int> Evacuate;

        public static bool Available => ChannelUsed != null && Evacuate != null;
    }

    internal static class ATOAaoBridge
    {
        /// <summary>Copy original UVs of AAO-used channels into free channels and register
        /// the evacuation. Called AFTER mesh clone, BEFORE channel rewrite.
        /// 把 AAO 占用通道的原始 UV 复制到空闲通道并登记搬移。在克隆之后、通道改写之前调用。</summary>
        public static void EvacuateRenderer(RendererInfo r, Mesh mesh, ATOBuildState st)
        {
            if (!AAOBridgeHooks.Available) return;
            if (!(r.renderer is SkinnedMeshRenderer smr)) return;

            foreach (var g in st.uvGroups)
            {
                if (g.owner != r || !g.atlasified) continue;

                try
                {
                    if (!AAOBridgeHooks.ChannelUsed(smr, g.channel)) continue;

                    // find free channel: no UVs & unused by AAO / 找空闲通道：无UV且AAO未占用
                    int freeCh = -1;
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if (ch == g.channel) continue;
                        if (mesh.HasUV(ch)) continue;
                        if (AAOBridgeHooks.ChannelUsed(smr, ch)) continue;
                        freeCh = ch;
                        break;
                    }

                    if (freeCh < 0)
                    {
                        st.report.AddWarning(
                            $"No free UV channel to evacuate AAO usage on '{r.path}' UV{g.channel}");
                        continue;
                    }

                    var uv = new System.Collections.Generic.List<Vector2>();
                    mesh.GetUVs(g.channel, uv);
                    mesh.SetUVs(freeCh, uv);
                    AAOBridgeHooks.Evacuate(smr, g.channel, freeCh);
                    st.uvEvacuations.Add((smr, g.channel, freeCh));
                    ATOLog.Info($"AAO UV evacuation: '{r.path}' UV{g.channel} → UV{freeCh}");
                }
                catch (Exception e)
                {
                    st.report.AddWarning($"AAO evacuation failed on '{r.path}': {e.Message}");
                }
            }
        }
    }
}

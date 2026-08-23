// -----------------------------------------------------------------------------
// AAOBridgeImpl.cs — compiled ONLY when Avatar Optimizer ≥ 1.8.0 is installed
// (see asmdef versionDefines/defineConstraints). Wires AAO's
// UVUsageCompabilityAPI into ATO's bridge hooks.
// 仅在安装 Avatar Optimizer ≥ 1.8.0 时编译（见 asmdef versionDefines/defineConstraints）。
// 将 AAO 的 UVUsageCompabilityAPI 接入 ATO 桥接钩子。
// -----------------------------------------------------------------------------

using Anatawa12.AvatarOptimizer.API;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor.aao
{
    [InitializeOnLoad]
    internal static class AAOBridgeImpl
    {
        static AAOBridgeImpl()
        {
            AAOBridgeHooks.ChannelUsed = (smr, ch) =>
            {
                try
                {
                    return UVUsageCompabilityAPI.IsTexCoordUsed(smr, ch);
                }
                catch (System.Exception e)
                {
                    // API not initialized (AAO not in this build) → treat as unused
                    // API 未初始化（本次构建无 AAO）→ 视为未占用
                    ATOLog.Debug($"AAO IsTexCoordUsed threw: {e.Message}");
                    return false;
                }
            };

            AAOBridgeHooks.Evacuate = (smr, origCh, savedCh) =>
                UVUsageCompabilityAPI.RegisterTexCoordEvacuation(smr, origCh, savedCh);

            ATOLog.Info("AAO bridge active (UVUsageCompabilityAPI) / AAO 桥接已激活");
        }
    }
}

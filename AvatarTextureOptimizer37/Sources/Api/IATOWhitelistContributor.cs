// ============================================================================
// ATO public API - whitelist contribution
// ATO 公开 API - 白名单贡献
//
// Third parties can contribute extra whitelist objects during a build. Every
// texture referenced (directly or via materials/animations) by a whitelisted
// object skips ALL optimization; textures sharing a UV with a whitelisted
// texture skip atlasing only.
// 第三方可在构建期间贡献额外白名单对象。白名单对象引用的全部贴图跳过所有优化
// ；与其同 UV 的贴图仅跳过图集化。
// ============================================================================

#region

using System.Collections.Generic;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Api
{
    /// <summary>Contributes whitelist objects for one avatar build.
    /// 为单次 Avatar 构建贡献白名单对象。</summary>
    public interface IATOWhitelistContributor
    {
        /// <summary>Called once per build, before analysis. Objects may be any
        /// UnityEngine.Object (mesh, material, texture, animator, renderer,
        /// game object, component).
        /// 每次构建开始前调用一次。对象可为任意 UnityEngine.Object。</summary>
        void ContributeWhitelist(GameObject avatarRoot, List<UnityEngine.Object> outObjects);
    }
}

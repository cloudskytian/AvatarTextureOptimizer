// ATOApi.cs
// Public extension surface for advanced users and third-party developers.
// 面向高级用户与第三方开发者的公开扩展接口。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.api
{
    /// <summary>Quality context handed to extension callbacks. / 传给扩展回调的质量上下文。</summary>
    public sealed class IslandScaleContext
    {
        /// <summary>Renderer that owns the island. / 岛所属渲染器。</summary>
        public Renderer Renderer;
        /// <summary>Source texture of the layer being scaled. / 正在缩放的层源贴图。</summary>
        public Texture2D Texture;
        /// <summary>Current minimum passing scale (read-write). / 当前最小通过缩放(可读写)。</summary>
        public float MinScale;
        /// <summary>Pixel density of the island (px/m). / 岛像素密度(px/米)。</summary>
        public float Density;
    }

    /// <summary>Whitelist decision context. / 白名单决策上下文。</summary>
    public sealed class WhitelistContext
    {
        public Renderer Renderer;
        public Material Material;
        public Texture2D Texture;
        /// <summary>Set true to whitelist the texture. / 置 true 将贴图加入白名单。</summary>
        public bool Whitelist;
        /// <summary>Reason shown in the report. / 报告中显示的原因。</summary>
        public string Reason;
    }

    /// <summary>
    /// Entry points for third-party extensions. Register before the NDMF build runs
    /// (e.g. from an [InitializeOnLoadMethod]). / 第三方扩展入口。请在 NDMF 构建开始前注册
    /// (例如 [InitializeOnLoadMethod])。
    /// </summary>
    public static class ATOExtensions
    {
        /// <summary>Called per island before binary search; modify MinScale to clamp results. / 二分前逐岛调用;修改 MinScale 以钳制结果。</summary>
        public static event Action<IslandScaleContext> IslandScaleModifier;

        /// <summary>Called for every analyzed texture; set Whitelist=true to exclude it. / 对每个分析到的贴图调用;Whitelist=true 将其排除。</summary>
        public static event Action<WhitelistContext> WhitelistProvider;

        /// <summary>Arbitrary whitelist objects contributed by third parties. / 第三方贡献的白名单对象。</summary>
        public static event Func<IEnumerable<UnityEngine.Object>> WhitelistObjectsProvider;

        /// <summary>Called after atlases are baked (read-only inspection). / 图集烘焙完成后调用(只读检视)。</summary>
        public static event Action<GameObject> AtlasesBaked;

        internal static void RaiseIslandScale(IslandScaleContext ctx) => IslandScaleModifier?.Invoke(ctx);
        internal static bool RaiseWhitelist(WhitelistContext ctx)
        {
            WhitelistProvider?.Invoke(ctx);
            return ctx.Whitelist;
        }
        internal static IEnumerable<UnityEngine.Object> RaiseWhitelistObjects() => WhitelistObjectsProvider?.Invoke() ?? null;
        internal static void RaiseAtlasesBaked(GameObject root) => AtlasesBaked?.Invoke(root);
    }
}

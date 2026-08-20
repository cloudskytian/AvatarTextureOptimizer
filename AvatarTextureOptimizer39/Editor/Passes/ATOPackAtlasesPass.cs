// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.Linq;
using AvatarTextureOptimizer.Editor.Atlas;
using AvatarTextureOptimizer.Editor.Core;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 7 — pack islands into atlases per type group (category + color space +
    /// filterMode). Skipped when "generate atlas" is off (then whole-texture scaling is
    /// used instead).
    ///
    /// Pass 7 —— 按类型组（类别+色彩空间+filterMode）将岛装箱为图集。
    /// 关闭"生成图集"时跳过（改用整图缩放）。
    /// </summary>
    public sealed class ATOPackAtlasesPass : Pass<ATOPackAtlasesPass>
    {
        public override string DisplayName => "ATO: Pack atlases / 图集装箱";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ATOBuildState>();
            if (state.Component == null) return;
            state.BeginStage("Pack atlases / 图集装箱");

            if (!state.Component.generateAtlas)
            {
                ATOLog.Info("Atlas generation disabled; using whole-texture scaling. / 未生成图集，改用整图缩放。");
                return;
            }

            using var _ = ATOLog.Time("Pack atlases");

            // Dynamic padding: ceil(maxEdge/128) clamped to ≥4, then max with the user's
            // minimum padding choice. 动态 padding：ceil(maxEdge/128) 钳制 ≥4，再与用户最小
            // padding 取大者。
            int autoPadding = Mathf.Max(4, Mathf.CeilToInt(state.MaxAtlasEdge / 128f));
            int padding = Mathf.Max(state.MinPadding, autoPadding);

            // Group islands by type group key. 按类型组键分组。
            var groups = new Dictionary<string, List<ATOUVIslandEntry>>();
            foreach (var entry in state.Islands)
            {
                // Type group = category of the first (canonical) non-skipped texture.
                // 类型组 = 首个（规范）未跳过贴图的类别 + 色彩空间 + filterMode。
                var key = TypeGroupKeyOf(entry, state);
                if (key == null) continue;

                if (!groups.TryGetValue(key, out var list)) { list = new List<ATOUVIslandEntry>(); groups[key] = list; }
                list.Add(entry);
            }

            foreach (var kv in groups)
            {
                _ = ATOLog.Time($"Pack group {kv.Key}");
                var result = ATOAtlasPacker.Pack(kv.Value, kv.Key, state.MaxAtlasEdge, state.AllowNPOT,
                    padding, state);
                state.AtlasGroups.Add(result);
                int placements = 0;
                foreach (var a in result.Atlases) placements += a.Placements.Count;
                ATOLog.Info($"Group {kv.Key}: {result.Atlases.Count} atlas(es), " +
                            $"{placements} placements, {result.Dropped.Count} dropped, padding {padding}px. / " +
                            $"组 {kv.Key}：{result.Atlases.Count} 个图集、{placements} 个摆放、" +
                            $"{result.Dropped.Count} 个放弃、padding {padding}px。");
            }
        }

        private static string TypeGroupKeyOf(ATOUVIslandEntry entry, ATOBuildState state)
        {
            // Islands sharing UV with a whitelisted texture skip atlas-ization.
            // 与白名单贴图共享 UV 的岛跳过图集化。
            if (entry.SkipAtlas) return null;

            foreach (var t in entry.Textures)
            {
                if (t == null || t.SkipAll) continue;
                return t.TypeGroupKey;
            }
            return null;
        }
    }
}

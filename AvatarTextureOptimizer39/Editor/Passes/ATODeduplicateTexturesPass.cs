// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.Linq;
using AvatarTextureOptimizer.Editor.Core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 4 — deduplicate textures by actual pixels + import settings and update all
    /// references (materials and animation curves). If a dedup group contains a
    /// whitelisted texture, the dedup result is also treated as whitelisted.
    ///
    /// Pass 4 —— 按实际像素 + 导入设置去重贴图并更新所有引用（材质与动画曲线）。
    /// 若去重组含白名单贴图，则去重结果也视为白名单。
    /// </summary>
    public sealed class ATODeduplicateTexturesPass : Pass<ATODeduplicateTexturesPass>
    {
        public override string DisplayName => "ATO: Deduplicate textures / 贴图去重";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ATOBuildState>();
            if (state.Component == null) return;
            state.BeginStage("Deduplicate textures / 贴图去重");

            using var _ = ATOLog.Time("Deduplicate textures");

            // Group by import signature. 按导入签名分组。
            var groups = new Dictionary<string, List<Texture2D>>();
            foreach (var tex in state.Textures.Keys.ToList())
            {
                var rec = state.Textures[tex];
                if (rec.ImportSignature == null) continue;
                if (!groups.TryGetValue(rec.ImportSignature, out var list))
                {
                    list = new List<Texture2D>();
                    groups[rec.ImportSignature] = list;
                }
                list.Add(tex);
            }

            var remap = new Dictionary<Texture2D, Texture2D>();

            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count <= 1) continue;

                var canonical = list[0];
                bool whitelisted = list.Any(t => state.SkippedTextures.Contains(t));

                for (int i = 1; i < list.Count; i++)
                    remap[list[i]] = canonical;

                if (whitelisted) state.SkippedTextures.Add(canonical);

                ATOLog.Verbose($"Dedup: {list.Count} textures → {canonical.name}. / " +
                               $"{list.Count} 张贴图合并为 {canonical.name}。");
            }

            if (remap.Count == 0)
            {
                ATOLog.Info("No duplicate textures found. / 未发现重复贴图。");
                return;
            }

            // Update material references. 更新材质引用。
            foreach (var matRec in state.Materials.Values)
            {
                var mat = matRec.Material;
                foreach (var name in mat.GetTexturePropertyNames())
                {
                    var t = mat.GetTexture(name) as Texture2D;
                    if (t != null && remap.TryGetValue(t, out var c))
                        mat.SetTexture(name, c);
                }
            }

            // Update animation object curves. 更新动画对象曲线。
            var animCtx = context.ActivateExtensionContext<AnimatorServicesContext>();
            animCtx.AnimationIndex.RewriteObjectCurves(o =>
                o is Texture2D t && remap.TryGetValue(t, out var c) ? c : o);

            // Update build state records. 更新构建状态记录。
            foreach (var kv in remap)
            {
                if (state.Textures.TryGetValue(kv.Key, out var rec))
                {
                    state.Textures.Remove(kv.Key);
                    // Point any remaining references at the canonical record.
                    if (!state.Textures.ContainsKey(kv.Value))
                        state.Textures[kv.Value] = rec;
                }
            }

            // Re-point binding textures. 重新指向绑定贴图。
            foreach (var matRec in state.Materials.Values)
            {
                foreach (var b in matRec.Bindings)
                    if (remap.TryGetValue(b.Texture, out var c))
                        b.Texture = c;
            }
            foreach (var sb in state.SubmeshBindings.Values)
                foreach (var b in sb)
                    if (remap.TryGetValue(b.Texture, out var c))
                        b.Texture = c;

            ATOLog.Info($"Deduplicated {remap.Count} texture references. / 去重了 {remap.Count} 个贴图引用。");

            // Raw pixels are no longer needed (dedup + alpha detection done).
            // 原始像素不再需要（去重 + alpha 检测已完成）。
            ATOMemory.ReleaseRawPixels(state);
        }
    }
}

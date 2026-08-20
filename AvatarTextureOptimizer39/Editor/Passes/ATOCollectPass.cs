// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.Linq;
using AvatarTextureOptimizer.Editor.Analysis;
using AvatarTextureOptimizer.Editor.Core;
using AvatarTextureOptimizer.Editor.Texture;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 2 — collect all materials, textures, and animation references; build the
    /// UV ↔ texture-set correspondence and the whitelist.
    ///
    /// Pass 2 —— 收集所有材质、贴图与动画引用；建立 UV↔贴图集对应关系与白名单。
    /// </summary>
    public sealed class ATOCollectPass : Pass<ATOCollectPass>
    {
        public override string DisplayName => "ATO: Collect materials & textures / 收集材质与贴图";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ATOBuildState>();
            if (state.Component == null) return;

            state.InitProgress(context.AvatarRootObject.name, 9);
            state.BeginStage("Collect materials & textures / 收集材质与贴图");

            using var _ = ATOLog.Time("Collect");

            // Resolve effective settings. 解析有效设置。
            ResolveSettings(context, state);

            // Activate animator services for animation analysis. 激活动画服务用于动画分析。
            var animCtx = context.ActivateExtensionContext<AnimatorServicesContext>();
            var queries = new ATOAnimationQueries(animCtx.AnimationIndex);

            // Build whitelist. 构建白名单。
            var whitelist = new ATOWhitelist();
            whitelist.Build(state.Component.whitelist);
            state.Whitelist = new HashSet<Object>(state.Component.whitelist.Where(o => o != null));

            var root = context.AvatarRootObject;
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            var skipped = state.SkippedTextures;

            foreach (var renderer in renderers)
            {
                if (!IsEligibleRenderer(renderer, root, queries)) continue;

                state.EligibleRenderers.Add(renderer);

                string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
                var analyzer = new ATOMaterialAnalyzer(queries, whitelist, path);

                var mats = renderer.sharedMaterials;
                for (int sub = 0; sub < mats.Length; sub++)
                {
                    var mat = mats[sub];
                    if (mat == null) continue;

                    if (!state.Materials.TryGetValue(mat, out var matRec))
                    {
                        matRec = new ATOMaterialRecord { Material = mat, Renderer = renderer, SubMeshIndex = sub };
                        state.Materials[mat] = matRec;
                    }

                    var bindings = analyzer.Analyze(mat, skipped);
                    matRec.Bindings.AddRange(bindings);

                    // Store per (renderer, submesh) bindings for UV-set building.
                    // 存储按 (渲染器, 子网格) 的绑定，供 UV 组构建。
                    var key = (renderer, sub);
                    if (!state.SubmeshBindings.TryGetValue(key, out var sb))
                    {
                        sb = new List<ATOTextureBinding>();
                        state.SubmeshBindings[key] = sb;
                    }
                    sb.AddRange(bindings);

                    // Ensure texture records exist for every binding. 为每个绑定确保贴图记录存在。
                    foreach (var b in bindings)
                    {
                        if (!state.Textures.ContainsKey(b.Texture))
                        {
                            var rec = ATOTextureReader.Read(b.Texture);
                            if (rec == null)
                            {
                                skipped.Add(b.Texture);
                                continue;
                            }
                            rec.Category = b.Category;
                            rec.SkipAll = skipped.Contains(b.Texture);
                            state.Textures[b.Texture] = rec;
                        }
                        else if (state.Textures[b.Texture].SkipAll == false && skipped.Contains(b.Texture))
                        {
                            state.Textures[b.Texture].SkipAll = true;
                        }
                    }
                }
            }

            // Collect animation material/texture switches and merge into UV sets.
            // 收集动画中的材质/贴图切换并并入 UV 组。
            CollectAnimationReferences(state, queries, root);

            ATOLog.Info($"Collected {state.Textures.Count} textures, {state.Materials.Count} materials. / " +
                        $"收集到 {state.Textures.Count} 张贴图、{state.Materials.Count} 个材质。");
        }

        private static void ResolveSettings(BuildContext context, ATOBuildState state)
        {
            var comp = state.Component;
            state.Quality = comp.quality.Resolve();

            // Platform: read current build target. 平台：读取当前构建目标。
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: state.Platform = ATOPlatform.Android; break;
                case BuildTarget.iOS: state.Platform = ATOPlatform.iOS; break;
                default: state.Platform = ATOPlatform.PC; break;
            }

            var ps = comp.platformOverride.Get(state.Platform);
            state.MaxAtlasEdge = ps.overrideEnabled ? ps.maxAtlasEdge : 8192;
            if (state.Platform != ATOPlatform.PC) state.MaxAtlasEdge = Mathf.Min(state.MaxAtlasEdge, 4096);
            state.AllowNPOT = ps.overrideEnabled ? ps.allowNPOT : comp.allowNPOT;
            state.MinPadding = (int)comp.atlasPadding;
        }

        private static bool IsEligibleRenderer(Renderer r, GameObject root, ATOAnimationQueries queries)
        {
            if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) return false;
            if (r.CompareTag("EditorOnly")) return false;

            bool active = r.gameObject.activeInHierarchy && r.enabled;
            if (active) return true;

            // Check if animated on. 检查是否被动画启用。
            string path = AnimationUtility.CalculateTransformPath(r.transform, root.transform);
            return queries.IsGameObjectActiveAnimated(path) || queries.IsEnabledAnimated(path, r.GetType());
        }

        private static void CollectAnimationReferences(ATOBuildState state, ATOAnimationQueries queries,
            GameObject root)
        {
            int added = 0;
            foreach (var (binding, obj) in queries.ObjectReferences)
            {
                // Material/texture switches add new textures to the correspondence.
                // 材质/贴图切换会向对应关系添加新贴图。
                if (obj is Material switchedMat)
                {
                    foreach (var name in switchedMat.GetTexturePropertyNames())
                    {
                        var t = switchedMat.GetTexture(name) as Texture2D;
                        if (t != null)
                        {
                            ATOTextureReaderCache.Ensure(state, t);
                            added++;
                        }
                    }
                }
                else if (obj is Texture2D switchedTex)
                {
                    ATOTextureReaderCache.Ensure(state, switchedTex);
                    added++;
                }
            }

            if (added > 0)
                ATOLog.Verbose($"Animation adds {added} material/texture references. / 动画新增 {added} 个材质/贴图引用。");
        }
    }
}

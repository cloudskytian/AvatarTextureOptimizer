// -----------------------------------------------------------------------------
// ATOMaterialRebuild.cs — clone materials, rebind atlas layers, update animations.
// ATOMaterialRebuild.cs —— 克隆材质、重绑图集层、更新动画引用。
//
// Texture resolution is per (renderer, slot, material, property): the slot's islands
// decide which atlas & layer the property must point to. The SAME source material on
// two slots with different layouts gets separate clones. Only texture references are
// modified — every other shader property stays untouched (spec).
// 贴图解析按（渲染器, 槽位, 材质, 属性）进行：由该槽位的岛决定属性应指向哪个图集与层。
// 同一源材质在布局不同的两个槽上会得到各自的克隆。仅修改贴图引用——其余着色器
// 参数一律不动（规格）。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOMaterialRebuild
    {
        /// <summary>clone cache: (material, renderer, slot) → clone / 克隆缓存。</summary>
        private static readonly Dictionary<(Material, RendererInfo, int), Material> CloneCache =
            new Dictionary<(Material, RendererInfo, int), Material>();

        public static void Run(BuildContext ctx, ATOBuildState st)
        {
            CloneCache.Clear();
            var asc = ctx.Extension<AnimatorServicesContext>();

            foreach (var r in st.renderers)
            {
                var mats = r.renderer.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    var rebound = RebindForSlot(m, r, i, st);
                    if (!ReferenceEquals(rebound, m)) { mats[i] = rebound; changed = true; }
                }

                if (changed)
                {
                    r.renderer.sharedMaterials = mats;

                    // slot bookkeeping → clones (keep set ordering stable)
                    // 槽位记录同步为克隆（保持集合稳定）
                    for (int slot = 0; slot < r.slotMaterials.Count && slot < mats.Length; slot++)
                    {
                        var orig = r.slotMaterials[slot].ToList();
                        r.slotMaterials[slot].Clear();
                        foreach (var m2 in orig)
                        {
                            var mapped = m2;
                            if (m2 != null && CloneCache.TryGetValue((m2, r, slot), out var cl)) mapped = cl;
                            else if (m2 != null && st.materialClones.TryGetValue(m2, out var cl2)) mapped = cl2;
                            r.slotMaterials[slot].Add(mapped);
                        }
                    }
                }
            }

            // ---- rewrite animation material references / 重写动画材质引用 ----
            asc.AnimationIndex.RewriteObjectCurves(obj =>
            {
                if (obj is Material m && st.materialClones.TryGetValue(m, out var clone))
                    return clone;
                return obj;
            });

            ATOLog.Info($"Material rebuild: {st.materialClones.Count} cloned materials");
        }

        /// <summary>Master material→clone map consumed by the animation rewrite.
        /// 动画重写使用的总映射。</summary>
        internal static void RegisterClone(Material original, Material clone, ATOBuildState st)
        {
            if (!st.materialClones.ContainsKey(original)) st.materialClones[original] = clone;
        }

        private static Material RebindForSlot(Material m, RendererInfo r, int slot, ATOBuildState st)
        {
            var key = (m, r, slot);
            if (CloneCache.TryGetValue(key, out var cached)) return cached;

            var shader = m.shader;
            if (shader == null || !st.materialAnalysis.TryGetValue(m, out var analysis))
                return m;

            bool isRest = slot < r.initialMaterial.Count && r.initialMaterial[slot] == m;
            var changes = new List<(string prop, Texture newTex)>();

            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                string prop = shader.GetPropertyName(i);
                var tex = m.GetTexture(prop) as Texture2D;
                if (tex == null) continue;
                if (!st.texBySource.TryGetValue(tex, out var info)) continue;

                if (info.whitelisted) continue; // keep original / 保留原贴图

                var use = analysis.uses.FirstOrDefault(u => u.property == prop);
                var resolved = ResolveTexture(info, use, r, slot, isRest, st);
                if (resolved != null && resolved != tex) changes.Add((prop, resolved));
            }

            if (changes.Count == 0) return m;

            var clone = UnityEngine.Object.Instantiate(m);
            clone.name = m.name + "(ATO)";
            st.assetSaver.SaveAsset(clone);
            foreach (var (prop, newTex) in changes) clone.SetTexture(prop, newTex);

            CloneCache[key] = clone;
            RegisterClone(m, clone, st);
            return clone;
        }

        /// <summary>Resolve the optimized texture for (tex, use) in a slot context.
        /// 在槽位上下文中解析（贴图,用途）对应的优化贴图。</summary>
        private static Texture ResolveTexture(TexInfo info, TextureUse use, RendererInfo r, int slot,
            bool isRest, ATOBuildState st)
        {
            var role = use?.role ?? TexRole.Main;
            int channel = use?.uvChannel ?? 0;

            // The atlased group of this renderer on the property's channel.
            // 该属性通道上属于此渲染器的图集化组。
            var group = info.usages.Select(u => u.group)
                .FirstOrDefault(g => g.owner == r && g.channel == channel && g.atlasified);

            if (group == null)
            {
                // non-atlas path / 非图集路径
                return st.textureToOptimized.TryGetValue(info, out var t) ? t : null;
            }

            // island of this slot inside that group / 该组内属于此槽的岛
            var island = group.islands.FirstOrDefault(i =>
                i.atlasId >= 0 && i.triangles.Any(t => t.subMesh == slot));
            if (island == null)
                return st.textureToOptimized.TryGetValue(info, out var t) ? t : null;

            var atlas = ATOMeshRebuild.FindAtlas(st, island);
            if (atlas == null) return null;

            if (role == TexRole.Main)
            {
                if (isRest) return atlas.baseLayer?.texture;
                var variantLayer = atlas.layers.FirstOrDefault(l =>
                    l.kind == LayerKind.Variant && l.sourceTex == info);
                return variantLayer?.texture ?? atlas.baseLayer?.texture;
            }

            var layer = atlas.layers.FirstOrDefault(l =>
                l.kind == ATOAtlasBuilder.RoleToKind(role) && l.sourceTex == info);
            return layer?.texture;
        }
    }
}

// AvatarTextureOptimizer - GroupBuilder
// EN: Assembles type groups (usage signature + colorspace + filterMode) and UV groups (mesh+channel), applies
// whitelist/skipAtlas propagation, and joins animated textures into their base groups.
// CN: 装配类型组（用途签名 + 色彩空间 + filterMode）与 UV 组（网格+通道），应用白名单/跳图集传播，
//     并将动画切换贴图并入基础组。
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class GroupBuilder
    {
        /// <summary>EN: Builds UV groups and type groups from the scanned data. / CN: 从扫描数据构建 UV 组与类型组。</summary>
        public static void Build(AtoBuildState state)
        {
            state.UvGroups.Clear();
            state.TypeGroups.Clear();

            // ------------------------------------------------------------ UV 组
            var byMeshChannel = new Dictionary<(Mesh, int), UvGroup>();
            foreach (var mud in state.MeshUvData)
            {
                var g = new UvGroup
                {
                    mesh = mud.mesh,
                    renderer = mud.renderer,
                    channel = mud.channel,
                    whitelisted = mud.whitelisted,
                    uvShift = Vector2.zero
                };
                g.islands.AddRange(mud.islands);
                byMeshChannel[(mud.mesh, mud.channel)] = g;
                state.UvGroups.Add(g);
            }

            foreach (var tref in state.Textures)
            {
                foreach (var mu2 in tref.meshUsages)
                {
                    if (mu2.mesh == null) continue;
                    if (!byMeshChannel.TryGetValue((mu2.mesh, tref.uvChannel), out var g))
                    {
                        // EN: Mesh/channel not analyzed (channel absent on mesh) → cannot optimize safely.
                        // CN: 网格/通道未被分析（网格无此通道）→ 不能安全优化。
                        AtoLog.Warn(string.Format(I18n.T("warn.whitelisted.unknown"),
                            tref.texture != null ? tref.texture.name : tref.propertyName));
                        tref.whitelisted = true;
                        continue;
                    }
                    // EN: Track every renderer that uses this mesh (shared meshes!).
                    // CN: 记录使用该网格的全部渲染器（网格可能被共用）。
                    if (mu2.renderer != null && !g.renderers.Contains(mu2.renderer))
                        g.renderers.Add(mu2.renderer);
                    if (!tref.uvGroups.Contains(g)) tref.uvGroups.Add(g);
                    if (!g.textures.Contains(tref)) g.textures.Add(tref);
                }
            }

            // EN: A UV group containing any fully-whitelisted texture cannot have its UVs remapped: the other
            // textures in the group skip atlas (but still get whole-texture scaling + import optimization).
            // CN: 含完全白名单贴图的 UV 组不能重映射 UV：组内其他贴图跳过图集化（但仍做整图缩放与导入优化）。
            foreach (var g in state.UvGroups)
            {
                bool hasWhitelisted = g.whitelisted;
                foreach (var t in g.textures) if (t.whitelisted || t.specialUv) { hasWhitelisted = true; break; }
                if (!hasWhitelisted) continue;
                foreach (var t in g.textures)
                {
                    if (!t.whitelisted && !t.specialUv)
                    {
                        t.skipAtlas = true;
                        AtoLog.Detail($"UV group ({g.mesh.name}, ch{g.channel}) has whitelisted partner -> " +
                                      $"{t.texture.name} skips atlas");
                    }
                }
            }

            // ------------------------------------------------------------ 类型组
            var groupByKey = new Dictionary<string, TypeGroup>();
            foreach (var tref in state.Textures)
            {
                if (tref.whitelisted) continue; // 白名单不参与图集/缩放分组
                string key = KeyFor(tref);
                if (!groupByKey.TryGetValue(key, out var tg))
                {
                    tg = new TypeGroup
                    {
                        hasNormalMember = tref.usage == TextureUsage.Normal || HasNormalMaterial(tref),
                        hasMaskMember = tref.usage == TextureUsage.GrayMask || HasMaskMaterial(tref),
                        sRGB = tref.sRGB,
                        filterMode = tref.filterMode
                    };
                    groupByKey[key] = tg;
                    state.TypeGroups.Add(tg);
                }
                tref.typeGroup = tg;
                tg.textures.Add(tref);
            }

            // EN: Animated textures join the base texture's type group & UV groups (spec: 并入原贴图所在组).
            // CN: 动画贴图并入基础贴图的类型组与 UV 组（按需求）。
            foreach (var tref in state.Textures)
            {
                if (!tref.animated || tref.whitelisted) continue;
                var baseTref = FindBase(state, tref);
                if (baseTref == null || baseTref.typeGroup == null) continue;
                tref.typeGroup = baseTref.typeGroup;
                if (!baseTref.typeGroup.textures.Contains(tref)) baseTref.typeGroup.textures.Add(tref);
                foreach (var g in baseTref.uvGroups)
                {
                    if (!tref.uvGroups.Contains(g)) tref.uvGroups.Add(g);
                    if (!g.textures.Contains(tref)) g.textures.Add(tref);
                }
            }

            // EN: Totals for queue ordering.
            // CN: 队列排序用的总面积。
            foreach (var tg in state.TypeGroups)
            {
                long total = 0;
                foreach (var t in tg.textures)
                {
                    foreach (var g in t.uvGroups)
                        foreach (var island in g.islands)
                            total += (long)(island.fracRect.width * island.fracRect.height * t.width * t.height);
                }
                tg.totalAreaPx = (int)System.Math.Min(int.MaxValue, total);
            }
            state.TypeGroups.Sort((a, b) => b.totalAreaPx.CompareTo(a.totalAreaPx));
        }

        private static string KeyFor(TextureRef tref)
        {
            bool hasN = tref.usage == TextureUsage.Normal || HasNormalMaterial(tref);
            bool hasM = tref.usage == TextureUsage.GrayMask || HasMaskMaterial(tref);
            return $"{(hasN ? "N" : "")}{(hasM ? "M" : "")}|{(tref.sRGB ? "s" : "l")}|{(int)tref.filterMode}";
        }

        private static bool HasNormalMaterial(TextureRef tref)
        {
            foreach (var m in tref.materials) if (m.hasNormalRef) return true;
            return false;
        }

        private static bool HasMaskMaterial(TextureRef tref)
        {
            foreach (var m in tref.materials) if (m.hasMaskRef) return true;
            return false;
        }

        private static TextureRef FindBase(AtoBuildState state, TextureRef animated)
        {
            // EN: Base = non-animated texture with the same property name & mesh usage.
            // CN: 基础贴图 = 属性名与网格使用点相同的非动画贴图。
            foreach (var t in state.Textures)
            {
                if (t == animated || t.animated) continue;
                if (t.propertyName != animated.propertyName) continue;
                foreach (var mu2 in animated.meshUsages)
                    foreach (var bmu in t.meshUsages)
                        if (bmu.mesh == mu2.mesh && bmu.renderer == mu2.renderer && bmu.slot == mu2.slot)
                            return t;
            }
            return null;
        }
    }
}

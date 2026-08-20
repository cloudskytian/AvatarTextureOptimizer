// ============================================================================
// ATO - final texture resolution
// ATO - 最终贴图解析
//
// Computes the final texture for every (material, property):
//   1. whitelisted  -> original;
//   2. atlased      -> main page (albedo/utility) or mirror page (role);
//   3. whole-scaled -> scaled copy;
//   4. otherwise    -> original.
// 计算每个 (材质, 属性) 的最终贴图：1. 白名单 -> 原贴图；2. 已图集化 -> 主图
// 页（主色/工具）或镜像页（角色）；3. 整图缩放 -> 缩放副本；4. 其他 -> 原贴
// 图。
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using net.fosa.AvatarTextureOptimizer.Editor.Packing;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Atlas
{
    public static class FinalTextureResolver
    {
        public static void Resolve(ATOContext ctx)
        {
            var an = ctx.Analysis;
            var log = ctx.Log;
            an.FinalTextures.Clear();

            // atlas placement per (island, tid) -> main page index
            // (岛, 贴图) -> 主图页索引
            var islandPage = new Dictionary<(int, int), int>();
            foreach (var island in an.Islands)
            {
                if (island.AtlasPage < 0) continue;
                foreach (var tid in island.SampledTextureIds)
                {
                    islandPage[(island.Id, tid)] = island.AtlasPage;
                }
            }

            foreach (var (mat, info) in an.Materials)
            {
                foreach (var (prop, tex) in info.Textures)
                {
                    if (!(tex is Texture2D t2d))
                    {
                        an.FinalTextures[(mat, prop)] = null;
                        continue;
                    }
                    if (!an.TextureDedupMap.TryGetValue(t2d, out var tid))
                    {
                        an.FinalTextures[(mat, prop)] = null;
                        continue;
                    }
                    var tref = an.Textures[tid];

                    // 1. whitelist  白名单
                    if (tref.Whitelisted)
                    {
                        an.FinalTextures[(mat, prop)] = null;
                        continue;
                    }

                    // 1b. atlas-disabled (shares UV with whitelist): whole
                    //     image scaled or original  图集禁用：整图缩放或原图
                    if (tref.AtlasDisabled)
                    {
                        if (an.ScaledTextures.TryGetValue(tid, out var sTex) && sTex != null)
                        {
                            an.FinalTextures[(mat, prop)] = sTex;
                        }
                        else
                        {
                            an.FinalTextures[(mat, prop)] = null;
                        }
                        continue;
                    }

                    // 2. atlased  已图集化
                    int? page = null;
                    foreach (var island in an.Islands)
                    {
                        if (island.UVSet.Material != mat) continue;
                        if (!island.SampledTextureIds.Contains(tid)) continue;
                        if (island.AtlasPage >= 0)
                        {
                            page = island.AtlasPage;
                            break;
                        }
                    }
                    if (page != null)
                    {
                        var mainPage = an.PackedResult.Pages[page.Value];
                        var role = RoleOf(an, mat, prop, tid);
                        var final = role == Api.ATOTextureRole.Normal ||
                                    role == Api.ATOTextureRole.Mask ||
                                    role == Api.ATOTextureRole.Emission
                            ? (mainPage.MirrorRoles.TryGetValue(role, out var m) ? m.Texture : mainPage.Texture)
                            : mainPage.Texture;
                        if (final != null)
                        {
                            an.FinalTextures[(mat, prop)] = final;
                            continue;
                        }
                    }

                    // 3. whole scaled  整图缩放
                    if (an.ScaledTextures.TryGetValue(tid, out var scaled) && scaled != null)
                    {
                        an.FinalTextures[(mat, prop)] = scaled;
                        continue;
                    }

                    // 4. original  原贴图
                    an.FinalTextures[(mat, prop)] = null;
                }
            }
            log.V(ATOLogMask.Atlas,
                $"final textures resolved: {an.FinalTextures.Count} (material, property) entries. " +
                "最终贴图解析完成。");
        }

        private static Api.ATOTextureRole RoleOf(ATOAnalysis an, Material mat, string prop, int tid)
        {
            if (!an.Materials.TryGetValue(mat, out var info)) return Api.ATOTextureRole.Albedo;
            if (!info.PropertyRefs.TryGetValue(prop, out var pref)) return Api.ATOTextureRole.Albedo;
            return pref.Role;
        }
    }
}

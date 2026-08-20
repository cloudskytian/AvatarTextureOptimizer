// ATOPackItemBuilder.cs — 装箱项构建器 / Pack-item builder.
// 说明：为每张贴图构建刚性装箱项：以岛求解后的尺寸（4px 对齐）为矩形、按 UV 空间相对位置布局，
// 将岛形状（三角形）光栅化进基础位掩码（不含 padding，padding 在装箱时按箱尺寸膨胀）。
// Note: builds a rigid pack item per texture: island rects use solved sizes (4px aligned) laid out at their
// UV-space relative positions, with island shapes (triangles) rasterized into the base bitmask
// (padding is dilated per-bin at packing time).

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>装箱项构建器。/ Pack-item builder.</summary>
    internal static class ATOPackItemBuilder
    {
        /// <summary>
        /// 构建装箱项。需要岛基础尺寸（island.baseSizeU/V）已聚合完成。
        /// Build a pack item. Requires island base sizes (island.baseSizeU/V) to be aggregated.
        /// </summary>
        public static ATOPackItem Build(ATOItem item)
        {
            var packItem = new ATOPackItem { item = item };

            // 1. 岛矩形（4px 对齐）与项包围盒 / island rects (4px aligned) & item bounds
            var meshUvs = new Dictionary<(Mesh, int), List<Vector2>>();
            foreach (var r in item.refs)
            {
                var island = FindIslandOfRef(r);
                if (island == null) continue;
                var w = Mathf.Max(4, Mathf.CeilToInt(Mathf.Max(1f, island.baseSizeU) / 4f) * 4);
                var h = Mathf.Max(4, Mathf.CeilToInt(Mathf.Max(1f, island.baseSizeV) / 4f) * 4);
                // UV 空间相对位置（按归一化后的包围盒最小值）/ UV-space relative position (by normalized bbox min)
                var min = island.uvMin + island.translation;
                var x = Mathf.RoundToInt(min.x * island.baseSizeU / 4f) * 4;
                var y = Mathf.RoundToInt(min.y * island.baseSizeV / 4f) * 4;
                packItem.localRects[island] = new RectInt(x, y, w, h);
            }

            int maxX = 0, maxY = 0;
            foreach (var rect in packItem.localRects.Values)
            {
                maxX = Mathf.Max(maxX, rect.xMax);
                maxY = Mathf.Max(maxY, rect.yMax);
            }
            packItem.cellW = Mathf.Max(1, (maxX + 3) / 4);
            packItem.cellH = Mathf.Max(1, (maxY + 3) / 4);
            var mask = new ATOBitmask(packItem.cellW, packItem.cellH, Allocator.TempJob);

            // 2. 光栅化岛形状 / rasterize island shapes
            var tris = new List<ATOBitmaskOps.Tri>();
            foreach (var kv in packItem.localRects)
            {
                var island = kv.Key;
                var rect = kv.Value;
                var uvs = GetUvs(island.mesh, island.channel, meshUvs);
                var meshTris = island.mesh.triangles;

                // 归一化区间（合并岛使用合并包围盒）/ normalization span (merged bbox for merged islands)
                var bmin = island.uvMin + island.translation;
                var bmax = island.uvMax + island.translation;
                var span = bmax - bmin;
                var invSpan = new Vector2(span.x > 1e-6f ? 1f / span.x : 0f, span.y > 1e-6f ? 1f / span.y : 0f);

                foreach (var t in island.triangles)
                {
                    var p0 = Map(uvs[meshTris[t * 3 + 0]], bmin, invSpan, rect);
                    var p1 = Map(uvs[meshTris[t * 3 + 1]], bmin, invSpan, rect);
                    var p2 = Map(uvs[meshTris[t * 3 + 2]], bmin, invSpan, rect);
                    tris.Add(new ATOBitmaskOps.Tri { a = p0, b = p1, c = p2 });
                }
            }
            ATOBitmaskOps.Rasterize(mask, tris);

            // bits 所有权移交给 packItem（wrapper 仅由 GC 回收，不再 Dispose 缓冲）/
            // bits ownership transfers to packItem (the wrapper is GC'd; its buffer is not disposed)
            packItem.baseMask = mask.bits;
            packItem.areaCells = mask.CountBits();

            return packItem;
        }

        private static Unity.Mathematics.float2 Map(Vector2 uv, Vector2 bmin, Vector2 invSpan, RectInt rect)
        {
            var n = new Unity.Mathematics.float2((uv.x - bmin.x) * invSpan.x, (uv.y - bmin.y) * invSpan.y);
            return new Unity.Mathematics.float2(rect.x + n.x * rect.width, rect.y + n.y * rect.height);
        }

        private static ATOIsland FindIslandOfRef(ATOIslandRef r)
        {
            // 由构建会话提供映射（通过静态寄存器，装箱期间有效）/ provided by the session via a static registry (valid during packing)
            return PackIslandRegistry.TryGet(r);
        }

        private static List<Vector2> GetUvs(Mesh mesh, int channel, Dictionary<(Mesh, int), List<Vector2>> cache)
        {
            var key = (mesh, channel);
            if (!cache.TryGetValue(key, out var uvs))
            {
                uvs = new List<Vector2>();
                mesh.GetUVs(channel, uvs);
                cache[key] = uvs;
            }
            return uvs;
        }
    }

    /// <summary>装箱期间的 ref → island 寄存器。/ Ref → island registry during packing.</summary>
    internal static class PackIslandRegistry
    {
        private static Dictionary<ATOIslandRef, ATOIsland> _map;

        public static void Build(Dictionary<ATOIslandRef, ATOIsland> map)
        {
            _map = map;
        }

        public static ATOIsland TryGet(ATOIslandRef r)
        {
            return _map != null && _map.TryGetValue(r, out var island) ? island : null;
        }

        public static void Clear()
        {
            _map = null;
        }
    }
}

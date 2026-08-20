using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Drives type grouping, packing and compositing.
    ///
    ///     Texture type groups exist to stop a normal-map atlas being generated for UV groups that have
    ///     no normal map. The group key is (set of occupied slots, colour space, filter mode, wrap mode);
    ///     UV groups with different keys are packed separately, so ten colour textures of which only one
    ///     has a normal map never produce a normal atlas that is 90% empty.
    ///
    /// ZH: 驱动类型分组、装箱与合成。
    ///
    ///     贴图类型组的存在是为了避免给没有法线贴图的 UV 组生成法线图集。
    ///     分组键为（占据的槽位集合, 色彩空间, 过滤模式, 循环模式）；
    ///     键不同的 UV 组分开装箱，这样"十张彩色贴图中只有一张有法线"就绝不会产出 90% 空白的法线图集。
    /// </summary>
    public static class AtlasPipeline
    {
        /// <summary>EN: Execute the atlas path. ZH: 执行图集路径。</summary>
        public static void Run(BuildContext ctx, List<UVGroup> groups, PlatformProfile profile,
            ATOPlatform platform, GPUTextureIO io, ATOLog log, ATOProgress progress, ATOReport report,
            Dictionary<Texture2D, Texture2D> remap, Dictionary<UVGroup, Vector2Int> atlasSizeOf,
            List<Texture2D> generated)
        {
            var eligible = groups.Where(g => !g.SkipAtlas && !g.FullyWhitelisted && g.Islands.Count > 0).ToList();
            if (eligible.Count == 0) { log.Info("No UV groups are eligible for atlasing."); return; }

            var pool = AtlasCandidatePool.Build(platform, profile.experimentalNPOT);
            // EN: One padding value for the whole run, derived from the largest candidate. Using a
            //     per-candidate value would invalidate the masks whenever the packer changes atlas size.
            // ZH: 整轮使用同一个 padding 值，依据最大候选图集推导。
            //     若按候选图集分别取值，装箱器一换图集尺寸掩码就失效了。
            int padPx = AtlasCandidatePool.PaddingFor(pool[pool.Count - 1], profile.minPadding);
            var packer = new ShapePacker(log, progress, profile.allowIslandRotation, padPx);
            var compositor = new AtlasCompositor(io, log, progress);
            var avatarName = ctx.AvatarRootObject.name;
            int atlasIndex = 0;

            foreach (var typeGroup in eligible.GroupBy(TypeKey))
            {
                progress.ThrowIfCancelled();
                var units = new List<PackUnit>();

                foreach (var group in typeGroup)
                {
                    var masks = BuildMasks(group, padPx, log);
                    if (masks == null)
                    {
                        group.SkipAtlas = true;
                        group.SkipReason = "island rasterisation produced no coverage";
                        continue;
                    }
                    units.Add(new PackUnit
                    {
                        Group = group,
                        Masks = masks,
                        Coverage = masks.Sum(m => m.Coverage),
                        LongestSide = masks.Max(m => Mathf.Max(m.CellsX, m.CellsY)),
                    });
                }

                if (units.Count == 0) continue;

                var layouts = packer.Pack(units, pool);

                foreach (var layout in layouts)
                {
                    progress.ThrowIfCancelled();
                    foreach (var g in layout.Groups)
                    {
                        atlasSizeOf[g] = new Vector2Int(layout.Size.Width, layout.Size.Height);
                        foreach (var island in g.Islands) island.AtlasIndex = atlasIndex;
                    }

                    var baked = compositor.Bake(layout, atlasIndex, avatarName);
                    foreach (var kv in baked)
                    {
                        var atlas = kv.Value;
                        bool hasAlpha = atlas.Class == TextureClass.TransparentColor;
                        var channels = atlas.Sources
                            .Select(s => s.UsedChannels)
                            .Aggregate(new bool4Mask(), (a, b) => a | b);

                        TextureOutput.Apply(atlas.Texture, atlas.Class, hasAlpha, channels,
                            profile, platform, profile.experimentalNPOT, log);

                        ctx.AssetSaver.SaveAsset(atlas.Texture);
                        generated.Add(atlas.Texture);
                        report.Atlases.Add(atlas);
                        report.PackedIslands += atlas.IslandCount;

                        foreach (var src in atlas.Sources) remap[src.Source] = atlas.Texture;
                    }

                    atlasIndex++;
                }
            }

            // EN: Free decoded copies as soon as their atlas is done; a 40-texture avatar at 4K would
            //     otherwise hold several gigabytes of float pixels.
            // ZH: 图集完成后立刻释放解码副本；否则一个有 40 张 4K 贴图的 Avatar
            //     会一直占用数 GB 的浮点像素。
            foreach (var g in eligible)
            foreach (var t in g.Textures.SelectMany(kv => kv.Value))
                io.Evict(t.Representative.Source);
        }

        /// <summary>
        /// EN: Type group key. Textures that differ in colour space, filtering or wrapping cannot share
        ///     an atlas because those are per-texture import settings, not per-island.
        /// ZH: 类型组键。色彩空间、过滤方式或循环方式不同的贴图无法共享图集，
        ///     因为这些是逐贴图的导入设置，而不是逐岛的。
        /// </summary>
        private static string TypeKey(UVGroup g)
        {
            var slots = string.Join("+", g.Slots.OrderBy(s => (int)s));
            var rep = g.Textures.SelectMany(kv => kv.Value).Select(t => t.Representative).FirstOrDefault();
            return rep == null ? slots : $"{slots}|srgb={rep.SRGB}|filter={rep.Filter}|wrap={rep.Wrap}";
        }

        private static RasterMask[] BuildMasks(UVGroup group, int padPx, ATOLog log)
        {
            if (group.LayoutSize.x <= 0 || group.LayoutSize.y <= 0) return null;
            var binding = group.Bindings[0];
            var mesh = binding.Renderer is SkinnedMeshRenderer smr
                ? smr.sharedMesh
                : (binding.Renderer.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
            if (mesh == null) return null;

            var uvList = new List<Vector2>();
            mesh.GetUVs(binding.UvChannel, uvList);
            if (uvList.Count == 0) return null;
            var uv = uvList.ToArray();
            var indices = mesh.GetTriangles(binding.SubMesh);

            int padCells = Mathf.CeilToInt(padPx / (float)ATOConstants.RasterGranularity);

            var masks = new RasterMask[group.Islands.Count];
            for (int i = 0; i < group.Islands.Count; i++)
            {
                var island = group.Islands[i];
                int w = Mathf.Max(1, Mathf.RoundToInt((island.UvMax.x - island.UvMin.x) * group.LayoutSize.x * island.ScaleU));
                int h = Mathf.Max(1, Mathf.RoundToInt((island.UvMax.y - island.UvMin.y) * group.LayoutSize.y * island.ScaleV));
                island.ScaledSize = new Vector2Int(w, h);

                var mask = RasterMask.Rasterize(island, indices, uv, island.ScaledSize);
                island.Mask = mask;
                masks[i] = mask.Dilated(padCells);
            }
            return masks;
        }
    }
}

// ============================================================================
// IslandStage.cs — 阶段4：UV 岛提取、UV 组与类型组构建 / Stage 4: UV island
//                  extraction, UV group & type group construction
// (EN) For each renderer & UV channel: extracts islands, normalizes
//      out-of-bounds UV (whitelisting cross-seam cases), merges overlapping
//      islands, computes each island's referencing textures, groups islands
//      into UV groups (same UV → same placement), and assigns type groups by
//      profile (normal/mask presence, color space, filter mode).
// (ZH) 对每个渲染器的每个 UV 通道：提取岛、归一化越界 UV（跨缝则白名单）、
//      合并重叠岛、计算每个岛引用的贴图、将岛分组为 UV 组（同 UV → 同位置），
//      并按档案（法线/蒙版存在、色彩空间、filterMode）分配类型组。
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class ATOIslandResult
    {
        public List<ATOUVGroup> UvGroups = new List<ATOUVGroup>();
        public List<ATOTextureTypeGroup> TypeGroups = new List<ATOTextureTypeGroup>();

        public void Clear() { UvGroups.Clear(); TypeGroups.Clear(); }
    }

    public class IslandStage
    {
        private readonly ATOBuildContext _ctx;
        private readonly ATOCollectResult _collect;
        private readonly ATOIslandResult _result = new ATOIslandResult();

        public ATOIslandResult Result => _result;

        public IslandStage(ATOBuildContext ctx, ATOCollectResult collect)
        {
            _ctx = ctx;
            _collect = collect;
        }

        public void Run()
        {
            _result.Clear();

            // 按网格分组（共享网格只提取一次）/ group renderers by mesh (extract islands once per mesh)
            var byMesh = new Dictionary<Mesh, List<ATORendererInfo>>();
            foreach (var info in _collect.Renderers)
            {
                if (!byMesh.TryGetValue(info.Mesh, out var list)) { list = new List<ATORendererInfo>(); byMesh[info.Mesh] = list; }
                list.Add(info);
            }

            foreach (var kv in byMesh)
            {
                var mesh = kv.Key;
                var renderers = kv.Value;
                var first = renderers[0];

                for (int channel = 0; channel < 8; channel++)
                {
                    if (!first.UvChannelPresent[channel]) continue;

                    var islands = ATOMeshUtils.ExtractIslands(mesh, channel);
                    if (islands.Count == 0) continue;

                    // 1) UV 归一化 / UV normalization
                    NormalizeIslands(islands);

                    // 2) 重叠岛合并 / merge overlapping islands (containment)
                    islands = MergeOverlapping(islands);

                    // 3) 计算引用贴图（跨所有使用该网格的渲染器）/ referencing textures across all renderers
                    foreach (var island in islands)
                        ComputeReferencing(island, renderers, channel);

                    // 4) 面积与像素尺寸 / area & pixel size
                    float maxAnimScale = 1f;
                    foreach (var r in renderers)
                        maxAnimScale = Mathf.Max(maxAnimScale, Mathf.Max(r.AnimScale.x, Mathf.Max(r.AnimScale.y, r.AnimScale.z)));

                    foreach (var island in islands.Where(i => i.ReferencingTextures.Count > 0))
                    {
                        island.UvChannel = channel;
                        island.MaxAreaScale = maxAnimScale;
                        island.MaxBlendArea = ATOMeshUtils.ComputeMaxBlendShapeArea(mesh, island);
                        SetPixelSize(island);
                        AddToUvGroup(mesh, renderers, channel, island);
                    }
                }
            }

            // 5) 计算档案并分配类型组 / compute profiles and assign type groups
            AssignTypeGroups();

            ATOLog.VerboseLog($"[islands] {_result.UvGroups.Count} UV groups, {_result.TypeGroups.Count} type groups");
        }

        // ---------------------------------------------------------------------
        // UV 归一化 / UV normalization
        // ---------------------------------------------------------------------
        private void NormalizeIslands(List<ATOUVIsland> islands)
        {
            foreach (var island in islands)
            {
                var b = island.Bounds;
                bool crossesX = b.width > 1f + 1e-4f || Mathf.Floor(b.xMin) != Mathf.Floor(b.xMax);
                bool crossesY = b.height > 1f + 1e-4f || Mathf.Floor(b.yMin) != Mathf.Floor(b.yMax);

                if (crossesX || crossesY)
                {
                    island.CrossesWrapSeam = true;
                }
                else
                {
                    island.Translation = new Vector2(-Mathf.Floor(b.xMin), -Mathf.Floor(b.yMin));
                    island.Bounds = new Rect(b.xMin + island.Translation.x, b.yMin + island.Translation.y, b.width, b.height);
                }
            }
        }

        // ---------------------------------------------------------------------
        // 重叠岛合并（包围盒包含）/ merge overlapping islands (bbox containment)
        // ---------------------------------------------------------------------
        private List<ATOUVIsland> MergeOverlapping(List<ATOUVIsland> islands)
        {
            var merged = new List<ATOUVIsland>();
            var used = new bool[islands.Count];

            for (int i = 0; i < islands.Count; i++)
            {
                if (used[i]) continue;
                var island = islands[i];
                for (int j = i + 1; j < islands.Count; j++)
                {
                    if (used[j]) continue;
                    if (Contains(island.Bounds, islands[j].Bounds))
                    {
                        // 合并 j 进 i / merge j into i
                        island.Triangles.AddRange(islands[j].Triangles);
                        island.TriangleVerts.AddRange(islands[j].TriangleVerts);
                        island.TriangleUVs.AddRange(islands[j].TriangleUVs);
                        island.Submeshes.UnionWith(islands[j].Submeshes);
                        island.WorldArea += islands[j].WorldArea;
                        used[j] = true;
                    }
                }
                merged.Add(island);
            }

            // 重新计算包含合并后的包围盒 / recompute bounds after merge (conservative)
            return merged;
        }

        private static bool Contains(Rect a, Rect b)
        {
            return a.Contains(new Vector2(b.xMin, b.yMin)) &&
                   a.Contains(new Vector2(b.xMax, b.yMax));
        }

        /// <summary>(EN) Set island pixel size from the first referencing texture's resolution. (ZH) 由首个引用贴图分辨率设置岛像素尺寸。</summary>
        private void SetPixelSize(ATOUVIsland island)
        {
            if (island.ReferencingTextures.Count == 0) return;
            var tex = island.ReferencingTextures[0].Texture;
            if (tex == null) return;
            ATOMeshUtils.SetIslandPixelSize(island, tex.width, tex.height);
        }

        // ---------------------------------------------------------------------
        // 引用贴图 / referencing textures
        // ---------------------------------------------------------------------
        private void ComputeReferencing(ATOUVIsland island, List<ATORendererInfo> renderers, int channel)
        {
            island.ReferencingTextures.Clear();
            island.HasUnsafeReference = false;
            foreach (var info in renderers)
            {
                foreach (var sub in island.Submeshes)
                {
                    ATOSlot slot = null;
                    foreach (var s in info.Slots) if (s.SlotIndex == sub) { slot = s; break; }
                    if (slot == null) continue;
                    foreach (var t in slot.Textures)
                    {
                        if (t.UvChannel != channel) continue;
                        if (!t.SafeToOptimize)
                        {
                            // 该 UV 被不安全贴图引用 → 整个岛跳过图集化（UV 不可重映射）
                            // unsafe texture references this UV → island skips atlas (UV must not be remapped)
                            island.HasUnsafeReference = true;
                            continue;
                        }
                        if (!island.ReferencingTextures.Contains(t.Ref))
                            island.ReferencingTextures.Add(t.Ref);
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // UV 组 / UV groups
        // ---------------------------------------------------------------------
        private void AddToUvGroup(Mesh mesh, List<ATORendererInfo> renderers, int channel, ATOUVIsland island)
        {
            // 按引用贴图集合分组 / group by referencing-texture set
            var key = BuildKey(mesh, channel, island);
            ATOUVGroup group = null;
            foreach (var g in _result.UvGroups)
            {
                if (g.Key == key) { group = g; break; }
            }

            if (group == null)
            {
                group = new ATOUVGroup
                {
                    Key = key,
                    Mesh = mesh,
                    UvChannel = channel,
                };
                group.Renderers.AddRange(renderers);
                group.Textures.AddRange(island.ReferencingTextures);
                _result.UvGroups.Add(group);
            }
            group.Islands.Add(island);
        }

        private static string BuildKey(Mesh mesh, int channel, ATOUVIsland island)
        {
            var ids = island.ReferencingTextures.Select(t => t.DedupIdentity).OrderBy(x => x);
            return mesh.GetInstanceID() + ":" + channel + ":" + string.Join(",", ids);
        }

        // ---------------------------------------------------------------------
        // 档案与类型组 / profiles & type groups
        // ---------------------------------------------------------------------
        private void AssignTypeGroups()
        {
            // 计算每个 UV 组的档案 / compute each UV group's profile
            foreach (var group in _result.UvGroups)
            {
                group.Profile = ComputeProfile(group);
            }

            // 严格性传播：贴图同时出现在有法线/无法线组 → 全归有法线组 / strictness propagation
            var hasNormalTextures = new HashSet<ATOTextureRef>();
            var hasMaskTextures = new HashSet<ATOTextureRef>();
            foreach (var g in _result.UvGroups)
            {
                if (g.Profile.HasNormalMap) foreach (var t in g.Textures) hasNormalTextures.Add(t);
                if (g.Profile.HasMaskMap) foreach (var t in g.Textures) hasMaskTextures.Add(t);
            }
            foreach (var g in _result.UvGroups)
            {
                foreach (var t in g.Textures)
                {
                    if (hasNormalTextures.Contains(t)) g.Profile.HasNormalMap = true;
                    if (hasMaskTextures.Contains(t)) g.Profile.HasMaskMap = true;
                }
            }

            // 按档案键分组 / group by profile key
            var byKey = new Dictionary<string, ATOTextureTypeGroup>();
            foreach (var g in _result.UvGroups)
            {
                var key = g.Profile.ToKey();
                if (!byKey.TryGetValue(key, out var tg))
                {
                    tg = new ATOTextureTypeGroup
                    {
                        Key = key,
                        PrimaryUsage = ATOTextureUsage.MainColor,
                        HasNormalMap = g.Profile.HasNormalMap,
                        HasMaskMap = g.Profile.HasMaskMap,
                        Srgb = g.Profile.Srgb,
                        FilterMode = g.Profile.FilterMode,
                    };
                    byKey[key] = tg;
                    _result.TypeGroups.Add(tg);
                }
                foreach (var t in g.Textures)
                    if (!tg.Textures.Contains(t)) tg.Textures.Add(t);
            }
        }

        private ATOTextureProfile ComputeProfile(ATOUVGroup group)
        {
            var profile = new ATOTextureProfile();
            ATOTextureRef mainColor = null;
            foreach (var t in group.Textures)
            {
                switch (t.Usage)
                {
                    case ATOTextureUsage.NormalMap: profile.HasNormalMap = true; break;
                    case ATOTextureUsage.Mask:
                    case ATOTextureUsage.Grayscale: profile.HasMaskMap = true; break;
                    case ATOTextureUsage.MainColor:
                        if (mainColor == null) mainColor = t;
                        break;
                }
            }
            if (mainColor != null)
            {
                profile.Srgb = IsSrgb(mainColor.Texture);
                profile.FilterMode = mainColor.Texture != null ? mainColor.Texture.filterMode : FilterMode.Bilinear;
            }
            else
            {
                // 无主色（只有法线/蒙版）→ 取第一个贴图的设置 / fallback
                var first = group.Textures.Count > 0 ? group.Textures[0] : null;
                if (first != null)
                {
                    profile.Srgb = IsSrgb(first.Texture);
                    profile.FilterMode = first.Texture != null ? first.Texture.filterMode : FilterMode.Bilinear;
                }
            }
            return profile;
        }

        private static bool IsSrgb(Texture2D tex)
        {
            if (tex == null) return true;
            var path = UnityEditor.AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return true;
            var importer = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
            return importer == null || importer.sRGBTexture;
        }
    }
}

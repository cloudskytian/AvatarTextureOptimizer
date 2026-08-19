using System;
using System.Collections.Generic;
using System.Text;
using Fosa.AvatarTextureOptimizer;
using Fosa.AvatarTextureOptimizer.API;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// One mesh UV channel as sampled by one or more textures.
    /// 一张网格的一个 UV 通道，被一张或多张贴图采样。
    /// </summary>
    public sealed class UvBinding
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public int Submesh;
        public int UvChannel;
        public Material Material;
        public int MaterialSlot;
        public AtoTextureSlot Slot;
        public bool FromAnimation;
    }

    /// <summary>
    /// All textures that must share atlas positions because they sample the same UV.
    /// 因采样同一 UV 而必须在各图集上位置相同的全部贴图。
    /// </summary>
    public sealed class UvGroup
    {
        public int Id;
        public Renderer Renderer;
        public Mesh Mesh;
        public int UvChannel;
        public readonly List<UvBinding> Bindings = new List<UvBinding>();
        public readonly List<UvIsland> Islands = new List<UvIsland>();
        public readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>();
        public bool SkipAtlas;
        public bool SkipAll;
        public AtoSkipReason SkipReason;
        public string SkipDetail;
        public TypeGroup TypeGroup;
        public bool ContainsNormal;
        public int MaxSourceEdge;
    }

    /// <summary>
    /// Textures that share the same companion-map / color-space / filter signature.
    /// 具有相同伴随贴图 / 色彩空间 / 过滤模式签名的贴图组。
    /// </summary>
    public sealed class TypeGroup
    {
        public string Key;
        public bool HasNormal;
        public bool HasMask;
        public FilterMode Filter;
        public bool Srgb;
        public readonly List<UvGroup> UvGroups = new List<UvGroup>();
        public readonly List<AtoTextureKind> Kinds = new List<AtoTextureKind>();
    }

    public sealed class AtoGraph
    {
        public readonly List<AtoRendererInfo> Renderers = new List<AtoRendererInfo>();
        public readonly List<UvBinding> Bindings = new List<UvBinding>();
        public readonly List<UvGroup> UvGroups = new List<UvGroup>();
        public readonly List<TypeGroup> TypeGroups = new List<TypeGroup>();
        public readonly Dictionary<Texture2D, Texture2D> TextureDedup = new Dictionary<Texture2D, Texture2D>();
        public readonly Dictionary<Material, Material> WorkingMaterials = new Dictionary<Material, Material>();

        public void DisposeNative()
        {
            foreach (var ug in UvGroups)
            {
                if (ug?.Islands == null) continue;
                foreach (var isl in ug.Islands)
                {
                    if (isl?.CachedMask == null) continue;
                    var m = isl.CachedMask.Value;
                    if (m.Bits.IsCreated) m.Dispose();
                    isl.CachedMask = null;
                }
            }
        }
    }

    public static class GraphBuilder
    {
        public static AtoGraph Build(AtoSession session, List<AtoRendererInfo> renderers, AnimationCollector anim)
        {
            var g = new AtoGraph();
            g.Renderers.AddRange(renderers);

            var allTextures = new HashSet<Texture2D>();
            var allMaterials = new HashSet<Material>();

            foreach (var ri in renderers)
            {
                anim.PerRenderer.TryGetValue(ri.Renderer, out var ra);
                for (int slot = 0; slot < ri.Materials.Length; slot++)
                {
                    var mats = new List<Material>();
                    if (ri.Materials[slot] != null) mats.Add(ri.Materials[slot]);
                    if (ra.SlotMaterials != null && ra.SlotMaterials.TryGetValue(slot, out var extra))
                        foreach (var m in extra)
                            if (m != null && !mats.Contains(m))
                                mats.Add(m);

                    foreach (var mat in mats)
                    {
                        allMaterials.Add(mat);
                        var fromAnim = !ReferenceEquals(mat, ri.Materials[slot]);
                        var ctx = new AtoShaderAnalyzeContext
                        {
                            Material = mat,
                            Renderer = ri.Renderer,
                            MaterialSlotIndex = slot,
                            AnimatedProperties = ra.AnimatedProperties,
                            HasAnimatedUvTransform = ra.HasUvTransform
                        };
                        var analysis = ShaderAnalyzeService.Analyze(ctx, session.Log);
                        if (!analysis.Success)
                        {
                            session.WarnNdmf("warn.shader", mat.name + " / " + analysis.SkipDetail);
                            MarkMaterialTexturesWhitelist(mat, session);
                            continue;
                        }

                        // Most stringent alpha among material + animated cutoff / mode.
                        // 材质与动画 Cutoff / 模式中取最严。
                        var alpha = analysis.AlphaMode;
                        var cutoff = analysis.Cutoff;
                        if (ra.Cutoffs != null)
                        {
                            foreach (var c in ra.Cutoffs) cutoff = Mathf.Min(cutoff, c);
                        }

                        if (ra.AlphaModeHints != null)
                        {
                            foreach (var h in ra.AlphaModeHints)
                            {
                                if (h == 1) alpha = MostStrict(alpha, AtoAlphaMode.Cutout);
                                if (h >= 2) alpha = MostStrict(alpha, AtoAlphaMode.Blend);
                            }
                        }

                        foreach (var s in analysis.Slots)
                        {
                            s.AlphaMode = MostStrict(s.AlphaMode, alpha);
                            if (s.AlphaMode == AtoAlphaMode.Cutout) s.Cutoff = Mathf.Min(s.Cutoff, cutoff);

                            if (s.Texture == null) continue;
                            allTextures.Add(s.Texture);

                            if (ra.PropertyTextures != null &&
                                ra.PropertyTextures.TryGetValue(s.PropertyName, out var swapped))
                            {
                                foreach (var t in swapped)
                                {
                                    if (t == null) continue;
                                    allTextures.Add(t);
                                    var clone = CloneSlot(s, t);
                                    g.Bindings.Add(MakeBinding(ri, slot, mat, clone, s.UvChannel, true));
                                }
                            }

                            g.Bindings.Add(MakeBinding(ri, slot, mat, s, s.UvChannel, fromAnim));
                        }
                    }
                }
            }

            // Dedup textures (importer + pixels). / 按导入设置 + 像素去重。
            using (session.Log.Stage("dedup-textures"))
            {
                g.TextureDedup = TextureDeduplicator.Dedup(allTextures, session.WhitelistTextures, session.Log);
                foreach (var b in g.Bindings)
                {
                    if (b.Slot?.Texture != null && g.TextureDedup.TryGetValue(b.Slot.Texture, out var nt) && nt != null)
                        b.Slot.Texture = nt;
                }

                // Clone materials we will mutate later. / 稍后会改引用的材质先克隆。
                foreach (var mat in allMaterials)
                {
                    if (mat == null) continue;
                    var clone = UnityEngine.Object.Instantiate(mat);
                    clone.name = mat.name;
                    clone.hideFlags = HideFlags.HideAndDontSave;
                    session.Track(clone);
                    g.WorkingMaterials[mat] = clone;
                    TextureDeduplicator.RemapMaterials(new[] { clone }, g.TextureDedup, session.Log);
                }
            }

            BuildUvGroups(g, session);
            ExtractIslands(g, session, anim);
            BuildTypeGroups(g, session);
            session.Log.Info("Graph: bindings=" + g.Bindings.Count + " uvGroups=" + g.UvGroups.Count +
                             " typeGroups=" + g.TypeGroups.Count);
            return g;
        }

        static UvBinding MakeBinding(AtoRendererInfo ri, int slot, Material mat, AtoTextureSlot s, int uv, bool fromAnim)
        {
            return new UvBinding
            {
                Renderer = ri.Renderer,
                Mesh = ri.Mesh,
                Submesh = Mathf.Clamp(slot, 0, Mathf.Max(0, ri.Mesh.subMeshCount - 1)),
                UvChannel = uv,
                Material = mat,
                MaterialSlot = slot,
                Slot = s,
                FromAnimation = fromAnim
            };
        }

        static AtoTextureSlot CloneSlot(AtoTextureSlot s, Texture2D tex)
        {
            return new AtoTextureSlot
            {
                Material = s.Material,
                PropertyName = s.PropertyName,
                Texture = tex,
                UvChannel = s.UvChannel,
                Kind = s.Kind,
                AlphaMode = s.AlphaMode,
                Cutoff = s.Cutoff,
                IsSrgb = tex.isDataSRGB,
                FilterMode = tex.filterMode,
                ColorSpace = tex.isDataSRGB ? ColorSpace.Gamma : ColorSpace.Linear,
                UsedChannels = s.UsedChannels,
                HasIdentitySt = s.HasIdentitySt,
                SkipReason = s.SkipReason,
                SkipDetail = s.SkipDetail
            };
        }

        static void MarkMaterialTexturesWhitelist(Material mat, AtoSession session)
        {
            if (mat == null) return;
            try
            {
                foreach (var n in mat.GetTexturePropertyNames())
                    if (mat.GetTexture(n) is Texture2D t)
                        session.WhitelistTextures.Add(t);
            }
            catch { /* ignore */ }
        }

        static AtoAlphaMode MostStrict(AtoAlphaMode a, AtoAlphaMode b)
        {
            // Blend evaluates RMSE, Cutout evaluates IoU; if both appear, keep both via Blend+Cutout by taking Blend
            // and still recording cutoff on the slot. Callers already min() cutoff.
            // 同时出现时保留更严：Blend 含 RMSE，Cutout 含 IoU。这里取数值更大者作为“更严的模式标签”，
            // 实际评估时会按每个引用材质分别跑。
            return (AtoAlphaMode)Mathf.Max((int)a, (int)b);
        }

        static void BuildUvGroups(AtoGraph g, AtoSession session)
        {
            var map = new Dictionary<(Renderer, Mesh, int), UvGroup>();
            int id = 0;
            foreach (var b in g.Bindings)
            {
                var key = (b.Renderer, b.Mesh, b.UvChannel);
                if (!map.TryGetValue(key, out var ug))
                {
                    ug = new UvGroup
                    {
                        Id = id++,
                        Renderer = b.Renderer,
                        Mesh = b.Mesh,
                        UvChannel = b.UvChannel
                    };
                    map[key] = ug;
                    g.UvGroups.Add(ug);
                }

                ug.Bindings.Add(b);
                if (b.Slot?.Texture != null) ug.Textures.Add(b.Slot.Texture);
                if (b.Slot != null && b.Slot.Kind == AtoTextureKind.Normal) ug.ContainsNormal = true;

                if (session.WhitelistTextures.Contains(b.Slot?.Texture))
                {
                    // Whitelist texture: skip ALL opt. Same-UV others skip atlas only.
                    // 白名单贴图跳过一切优化；同 UV 其他贴图只跳过图集化。
                    ug.SkipAtlas = true;
                }

                if (b.Slot != null && b.Slot.SkipReason != AtoSkipReason.None)
                {
                    session.WhitelistTextures.Add(b.Slot.Texture);
                    ug.SkipAtlas = true;
                    if (b.Slot.SkipReason == AtoSkipReason.HasSTTransform ||
                        b.Slot.SkipReason == AtoSkipReason.HasAnimatedST)
                    {
                        session.WarnNdmf("warn.st", b.Slot.Texture != null ? b.Slot.Texture.name : b.Slot.PropertyName);
                    }
                    else if (b.Slot.SkipReason == AtoSkipReason.SpecialUse ||
                             b.Slot.SkipReason == AtoSkipReason.UnsupportedShader)
                    {
                        session.WarnNdmf("warn.shader",
                            (b.Slot.Texture != null ? b.Slot.Texture.name : "?") + " (" + b.Slot.SkipDetail + ")");
                    }
                }
            }
        }

        static void ExtractIslands(AtoGraph g, AtoSession session, AnimationCollector anim)
        {
            foreach (var ug in g.UvGroups)
            {
                var all = new List<UvIsland>();
                var seenSub = new HashSet<int>();
                foreach (var b in ug.Bindings)
                {
                    if (!seenSub.Add(b.Submesh)) continue;
                    var islands = UvIslandExtractor.Extract(ug.Mesh, b.Submesh, ug.UvChannel, session.Log);
                    foreach (var isl in islands)
                    {
                        isl.SourceTexture = b.Slot?.Texture;
                        if (isl.CrossesSeam)
                        {
                            ug.SkipAll = false;
                            ug.SkipAtlas = true;
                            ug.SkipReason = AtoSkipReason.UvWrapOrCrossSeam;
                            session.WarnNdmf("warn.uvWrap",
                                ug.Renderer.name + " UV" + ug.UvChannel + " sub" + b.Submesh);
                        }
                    }

                    all.AddRange(islands);
                }

                // Assign each island a representative texture (first binding). / 每个岛先挂一个代表贴图。
                foreach (var isl in all)
                {
                    if (isl.SourceTexture == null && ug.Textures.Count > 0)
                    {
                        foreach (var t in ug.Textures) { isl.SourceTexture = t; break; }
                    }
                }

                all = UvIslandExtractor.MergeOverlapping(all);
                anim.PerRenderer.TryGetValue(ug.Renderer, out var ra);
                foreach (var isl in all)
                {
                    isl.WorldArea = WorldAreaEstimator.Estimate(ug.Renderer, ug.Mesh, isl, ra);
                    if (isl.SourceTexture != null)
                    {
                        isl.OrigPixelW = Mathf.Max(1, Mathf.CeilToInt(isl.UvWidth * isl.SourceTexture.width));
                        isl.OrigPixelH = Mathf.Max(1, Mathf.CeilToInt(isl.UvHeight * isl.SourceTexture.height));
                        ug.MaxSourceEdge = Mathf.Max(ug.MaxSourceEdge,
                            Mathf.Max(isl.SourceTexture.width, isl.SourceTexture.height));
                    }

                    isl.ScaledW = isl.OrigPixelW;
                    isl.ScaledH = isl.OrigPixelH;
                }

                ug.Islands.AddRange(all);
                session.Report.IslandCount += all.Count;
            }
        }

        static void BuildTypeGroups(AtoGraph g, AtoSession session)
        {
            // Promote UV groups: if any binding has a normal / mask, the whole group carries that flag.
            // 提升：任一 binding 有法线/蒙版，则整组带上该标志。
            foreach (var ug in g.UvGroups)
            {
                bool hasN = false, hasM = false;
                FilterMode filter = FilterMode.Bilinear;
                bool srgb = false;
                foreach (var b in ug.Bindings)
                {
                    if (b.Slot == null) continue;
                    if (b.Slot.Kind == AtoTextureKind.Normal) hasN = true;
                    if (b.Slot.Kind == AtoTextureKind.Mask || b.Slot.Kind == AtoTextureKind.Gray) hasM = true;
                    filter = MaxFilter(filter, b.Slot.FilterMode);
                    if (b.Slot.IsSrgb && b.Slot.Kind == AtoTextureKind.Albedo) srgb = true;
                }

                ug.ContainsNormal = hasN;
                var key = "n" + (hasN ? 1 : 0) + "_m" + (hasM ? 1 : 0) + "_f" + (int)filter + "_s" + (srgb ? 1 : 0);
                var tg = g.TypeGroups.Find(t => t.Key == key);
                if (tg == null)
                {
                    tg = new TypeGroup
                    {
                        Key = key,
                        HasNormal = hasN,
                        HasMask = hasM,
                        Filter = filter,
                        Srgb = srgb
                    };
                    if (true) tg.Kinds.Add(AtoTextureKind.Albedo);
                    if (hasN) tg.Kinds.Add(AtoTextureKind.Normal);
                    if (hasM) tg.Kinds.Add(AtoTextureKind.Mask);
                    g.TypeGroups.Add(tg);
                }

                tg.UvGroups.Add(ug);
                ug.TypeGroup = tg;
            }
        }

        static FilterMode MaxFilter(FilterMode a, FilterMode b)
        {
            if (a == FilterMode.Trilinear || b == FilterMode.Trilinear) return FilterMode.Trilinear;
            if (a == FilterMode.Bilinear || b == FilterMode.Bilinear) return FilterMode.Bilinear;
            return FilterMode.Point;
        }
    }
}

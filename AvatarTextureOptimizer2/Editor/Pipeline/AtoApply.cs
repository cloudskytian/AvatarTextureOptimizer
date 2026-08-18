using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoApply
    {
        public static readonly Dictionary<Texture2D, Texture2D> TextureRemap = new Dictionary<Texture2D, Texture2D>();
        public static readonly Dictionary<Material, Material> MaterialRemap = new Dictionary<Material, Material>();

        public static void Apply(BuildContext ctx, AtoGraph graph, List<AtoIsland> islands,
            List<AtoAtlasResult> atlases, AtoPlatformOverride settings, AtoTextureCache cache, AtoReport report,
            AtoPlatform platform)
        {
            CloneTouchedMeshes(graph, islands, ctx);
            CloneTouchedMaterials(graph);
            AtoAaoCompat.RegisterUvEvacuation(graph);

            var remappedUv = new HashSet<(Mesh, int)>();
            if (atlases != null)
            {
                // Albedo first so UV is written from the layout master.
                foreach (var a in atlases.OrderBy(x => x.Role == AtoTextureRole.Albedo ? 0 : 1))
                {
                    SaveAsset(ctx, a.Texture);
                    bool hasA = a.Islands.Any(i => i.Blend != AtoBlendMode.Opaque);
                    AtoImportApply.Configure(a.Texture, a.Role, hasA, settings, report, platform, cache);
                    RemapUvs(a, remappedUv);
                    RemapMaterials(graph, a);
                    foreach (var src in a.Sources)
                        TextureRemap[src] = a.Texture;
                }
            }

            foreach (var tex in graph.EligibleTextures)
            {
                if (tex == null) continue;
                if (atlases != null && atlases.Any(a => a.Sources.Contains(tex) || a.Texture == tex)) continue;
                AtoImportApply.Configure(tex, AtoTextureRole.Albedo, true, settings, report, platform, cache);
            }

            AtoAnimationRemapper.RemapTexturesAndMaterials(ctx.AvatarRootObject, TextureRemap, MaterialRemap, report);
        }

        static void CloneTouchedMeshes(AtoGraph graph, List<AtoIsland> islands, BuildContext ctx)
        {
            var map = new Dictionary<Mesh, Mesh>();
            Mesh Clone(Mesh src)
            {
                if (src == null) return null;
                if (map.TryGetValue(src, out var n)) return n;
                n = Object.Instantiate(src);
                n.name = src.name + "_ATO";
                map[src] = n;
                if (ctx.AssetContainer != null) AssetDatabase.AddObjectToAsset(n, ctx.AssetContainer);
                return n;
            }

            foreach (var isl in islands)
                if (isl.Mesh != null) isl.Mesh = Clone(isl.Mesh);
            foreach (var b in graph.Bindings)
                if (b.Mesh != null) b.Mesh = Clone(b.Mesh);
            foreach (var r in graph.Renderers)
            {
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    smr.sharedMesh = Clone(smr.sharedMesh);
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) mf.sharedMesh = Clone(mf.sharedMesh);
                }
            }
        }

        static void CloneTouchedMaterials(AtoGraph graph)
        {
            var map = new Dictionary<Material, Material>();
            foreach (var b in graph.Bindings)
            {
                if (b.Material == null || !b.Eligible) continue;
                if (!map.TryGetValue(b.Material, out var n))
                {
                    n = Object.Instantiate(b.Material);
                    n.name = b.Material.name + "_ATO";
                    map[b.Material] = n;
                    MaterialRemap[b.Material] = n;
                }
                b.Material = n;
            }
            foreach (var r in graph.Renderers)
            {
                var mats = r.sharedMaterials;
                bool ch = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && map.TryGetValue(mats[i], out var n))
                    { mats[i] = n; ch = true; }
                }
                if (ch) r.sharedMaterials = mats;
            }
        }

        static void RemapUvs(AtoAtlasResult atlas, HashSet<(Mesh, int)> done)
        {
            var byMesh = atlas.Islands.GroupBy(i => (i.Mesh, i.UvChannel));
            foreach (var g in byMesh)
            {
                var mesh = g.Key.Mesh;
                int ch = g.Key.UvChannel;
                if (mesh == null) continue;
                if (!done.Add((mesh, ch))) continue;
                var uv = AtoUvUtil.Normalize(AtoUvUtil.GetUv(mesh, ch), out _);
                var nuv = (Vector2[])uv.Clone();
                foreach (var isl in g)
                {
                    float u0 = isl.UvBounds.xMin, v0 = isl.UvBounds.yMin;
                    float uw = Mathf.Max(isl.UvBounds.width, 1e-6f);
                    float vh = Mathf.Max(isl.UvBounds.height, 1e-6f);
                    float dw = Mathf.Max(1, isl.PixelBounds.width * isl.ScaleU);
                    float dh = Mathf.Max(1, isl.PixelBounds.height * isl.ScaleV);
                    foreach (var vi in isl.Vertices)
                    {
                        float lu = (uv[vi].x - u0) / uw;
                        float lv = (uv[vi].y - v0) / vh;
                        float px, py;
                        if (isl.Rotated)
                        {
                            px = isl.AtlasX + lv * dh;
                            py = isl.AtlasY + lu * dw;
                        }
                        else
                        {
                            px = isl.AtlasX + lu * dw;
                            py = isl.AtlasY + lv * dh;
                        }
                        nuv[vi] = new Vector2(px / isl.AtlasSizeX, py / isl.AtlasSizeY);
                    }
                }
                mesh.SetUVs(ch, new List<Vector2>(nuv));
            }
        }

        static void RemapMaterials(AtoGraph graph, AtoAtlasResult atlas)
        {
            var srcSet = new HashSet<Texture2D>(atlas.Sources);
            foreach (var b in graph.Bindings)
            {
                if (b.Texture == null || b.Material == null) continue;
                if (!srcSet.Contains(b.Texture)) continue;
                if (b.Material.HasProperty(b.Property))
                    b.Material.SetTexture(b.Property, atlas.Texture);
                b.Texture = atlas.Texture;
            }
        }

        static void SaveAsset(BuildContext ctx, Object obj)
        {
            if (ctx.AssetContainer != null)
                AssetDatabase.AddObjectToAsset(obj, ctx.AssetContainer);
        }
    }

    public static class AtoImportApply
    {
        public static void Configure(Texture2D tex, AtoTextureRole role, bool hasAlpha,
            AtoPlatformOverride settings, AtoReport report, AtoPlatform platform, AtoTextureCache cache)
        {
            if (tex == null) return;
            tex.wrapMode = TextureWrapMode.Clamp;
            bool mips = role switch
            {
                AtoTextureRole.Normal => settings.mipStreamingNormal,
                AtoTextureRole.Mask => settings.mipStreamingMask,
                AtoTextureRole.Gray => settings.mipStreamingGray,
                _ => settings.mipStreamingAlbedo
            };
            bool multiGray = false;
            if (role == AtoTextureRole.Gray && cache != null)
                multiGray = AtoFormatUtil.IsMultiChannelGray(cache.GetPixels(tex));
            var fmt = AtoFormatUtil.Resolve(role, hasAlpha, multiGray, settings, platform, tex, report);
            try
            {
                if (fmt != tex.format && tex.isReadable)
                    EditorUtility.CompressTexture(tex, fmt, TextureCompressionQuality.Normal);
            }
            catch (System.Exception ex)
            {
                report.Warn("warn.formatFallback", $"{tex.name} {fmt} ({ex.Message})");
            }
            tex.Apply(mips, false);
            report.Detail($"import {tex.name} role={role} mips={mips} fmt={fmt}");
        }
    }
}

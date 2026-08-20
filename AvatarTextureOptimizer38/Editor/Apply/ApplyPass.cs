using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Rewrite mesh UVs and texture references only. Never other shader parameters.
    /// 只改网格 UV 与贴图引用，绝不改其它着色器参数。
    /// </summary>
    public static class ApplyPass
    {
        public static void Apply(BuildContext ctx, GameObject root, AvatarTextureOptimizer settings,
            List<TextureBinding> bindings, List<UvGroup> uvGroups, Dictionary<int, AtlasResult> atlases,
            AnimationFacts facts, string folder, PlatformTextureSettings plat, AtoBuildPlatform platform,
            AtoReportData report)
        {
            var texRemap = new Dictionary<Texture2D, Texture2D>();
            foreach (var ar in atlases.Values)
            {
                foreach (var src in ar.Sources)
                    if (src != null && ar.Texture != null) texRemap[src] = ar.Texture;
            }

            var meshCache = new Dictionary<Mesh, Mesh>();
            var processedRenderers = new HashSet<Renderer>();

            foreach (var g in uvGroups)
            {
                if (g.Whitelisted) continue;
                foreach (var isl in g.Islands)
                {
                    if (isl.Owner == null || isl.Mesh == null) continue;
                    var rr = isl.Owner;
                    if (!meshCache.TryGetValue(isl.Mesh, out var copy))
                    {
                        copy = Object.Instantiate(isl.Mesh);
                        copy.name = isl.Mesh.name + "_ATO";
                        ctx.AssetSaver.SaveAsset(copy);
                        ObjectRegistry.RegisterReplacedObject(isl.Mesh, copy);
                        meshCache[isl.Mesh] = copy;
                    }
                    RewriteIslandUv(copy, isl, atlases);
                    if (processedRenderers.Add(rr.Renderer))
                    {
                        if (rr.IsSkinned && rr.Renderer is SkinnedMeshRenderer smr) smr.sharedMesh = copy;
                        else
                        {
                            var mf = rr.Renderer.GetComponent<MeshFilter>();
                            if (mf != null) mf.sharedMesh = copy;
                        }
                    }
                }
            }

            // Assign atlas/scaled textures on cloned materials. / 在克隆材质上赋贴图。
            var matRemap = new Dictionary<Material, Material>();
            foreach (var b in bindings)
            {
                if (b.Material == null || b.Texture == null) continue;
                if (b.IsWhitelisted) continue;
                if (!texRemap.TryGetValue(b.Texture, out var neu)) continue;
                if (!matRemap.TryGetValue(b.Material, out var mc))
                {
                    mc = Object.Instantiate(b.Material);
                    mc.name = b.Material.name + "_ATO";
                    ctx.AssetSaver.SaveAsset(mc);
                    ObjectRegistry.RegisterReplacedObject(b.Material, mc);
                    matRemap[b.Material] = mc;
                }
                mc.SetTexture(b.PropertyName, neu);
            }

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool ch = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && matRemap.TryGetValue(mats[i], out var n))
                    {
                        mats[i] = n;
                        ch = true;
                    }
                }
                if (ch) r.sharedMaterials = mats;
            }

            RewriteAnimation(ctx, texRemap, matRemap);

            if (settings.deduplicateTextures)
                DedupTextures(root, texRemap, ctx);
            if (settings.deduplicateMaterials)
                DedupMaterials(root, facts, ctx, settings);

            ApplyImporterSettings(atlases.Values.Select(a => a.Texture).Where(t => t != null), plat, platform, folder, settings, report);

            report.OutputTextures = texRemap.Values.Distinct().Count();
            long before = 0, after = 0;
            foreach (var b in bindings)
                if (b.Texture != null) before += GpuUtil.EstVram(b.Texture.width, b.Texture.height, true, true);
            foreach (var t in texRemap.Values.Distinct())
                if (t != null) after += GpuUtil.EstVram(t.width, t.height, true, true);
            report.VramBefore = before;
            report.VramAfter = after;
        }

        private static void RewriteIslandUv(Mesh mesh, UvIsland isl, Dictionary<int, AtlasResult> atlases)
        {
            if (isl.AtlasId < 0 || !atlases.TryGetValue(isl.AtlasId, out var ar)) return;
            var uvs = MeshUvUtil.GetUv(mesh, isl.UvChannel);
            if (uvs == null) return;
            float u0 = isl.UvMin.x, v0 = isl.UvMin.y;
            float uw = Mathf.Max(1e-8f, isl.UvMax.x - isl.UvMin.x);
            float vh = Mathf.Max(1e-8f, isl.UvMax.y - isl.UvMin.y);
            float ax = isl.PackedX / (float)ar.Width;
            float ay = isl.PackedY / (float)ar.Height;
            float aw = isl.PackedW / (float)ar.Width;
            float ah = isl.PackedH / (float)ar.Height;
            var vset = new HashSet<int>(isl.VertexIndices);
            for (int i = 0; i < uvs.Length; i++)
            {
                if (!vset.Contains(i)) continue;
                var uv = uvs[i] + isl.UvTranslate;
                float lx = (uv.x - u0) / uw;
                float ly = (uv.y - v0) / vh;
                if (isl.Rotated90)
                {
                    // Match 90° CW packing: (x,y) -> (1-y, x) in local island. / 与装箱顺时针 90° 一致。
                    float nx = 1f - ly;
                    float ny = lx;
                    lx = nx; ly = ny;
                }
                uvs[i] = new Vector2(ax + lx * aw, ay + ly * ah);
            }
            MeshUvUtil.SetUv(mesh, isl.UvChannel, uvs);
        }

        private static void RewriteAnimation(BuildContext ctx, Dictionary<Texture2D, Texture2D> texRemap,
            Dictionary<Material, Material> matRemap)
        {
            AnimatorServicesContext anim;
            try { anim = ctx.Extension<AnimatorServicesContext>(); }
            catch { return; }
            if (anim == null) return;

            anim.AnimationIndex.RewriteObjectCurves(obj =>
            {
                if (obj is Texture2D t && texRemap.TryGetValue(t, out var nt)) return nt;
                if (obj is Material m && matRemap.TryGetValue(m, out var nm)) return nm;
                return obj;
            });
        }

        private static void DedupTextures(GameObject root, Dictionary<Texture2D, Texture2D> texRemap, BuildContext ctx)
        {
            var byHash = new Dictionary<string, Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var t in texRemap.Values.Distinct())
            {
                if (t == null) continue;
                var h = TextureDecodeCache.PixelHash(t) + "|" + t.width + "x" + t.height + "|" + t.filterMode + "|" + t.wrapMode;
                if (byHash.TryGetValue(h, out var s) && s != t) remap[t] = s;
                else byHash[h] = t;
            }
            if (remap.Count == 0) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    var sh = mat.shader;
                    for (int i = 0; i < sh.GetPropertyCount(); i++)
                    {
                        if (sh.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                        var n = sh.GetPropertyName(i);
                        if (mat.GetTexture(n) is Texture2D t && remap.TryGetValue(t, out var neu))
                            mat.SetTexture(n, neu);
                    }
                }
            }
            AtoLog.Info($"Post-atlas texture dedup merged {remap.Count}");
        }

        private static void DedupMaterials(GameObject root, AnimationFacts facts, BuildContext ctx, AvatarTextureOptimizer settings)
        {
            var mats = new List<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && !mats.Contains(m)) mats.Add(m);

            var remap = new Dictionary<Material, Material>();
            for (int i = 0; i < mats.Count; i++)
            for (int j = i + 1; j < mats.Count; j++)
            {
                if (remap.ContainsKey(mats[j])) continue;
                if (MaterialsEqual(mats[i], mats[j])) remap[mats[j]] = mats[i];
            }
            if (remap.Count == 0) return;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var arr = r.sharedMaterials;
                bool ch = false;
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && remap.TryGetValue(arr[i], out var n)) { arr[i] = n; ch = true; }
                if (ch) r.sharedMaterials = arr;
                TryMergeOpaqueSlots(r, facts);
            }

            try
            {
                var anim = ctx.Extension<AnimatorServicesContext>();
                anim.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    if (obj is Material m && remap.TryGetValue(m, out var n)) return n;
                    return obj;
                });
            }
            catch { /* no animator context */ }

            AtoLog.Info($"Material dedup merged {remap.Count}");
        }

        private static bool MaterialsEqual(Material a, Material b)
        {
            if (a.shader != b.shader) return false;
            if (a.renderQueue != b.renderQueue) return false;
            var sh = a.shader;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                var n = sh.GetPropertyName(i);
                var t = sh.GetPropertyType(i);
                switch (t)
                {
                    case ShaderPropertyType.Texture:
                        if (a.GetTexture(n) != b.GetTexture(n)) return false;
                        if (a.GetTextureScale(n) != b.GetTextureScale(n)) return false;
                        if (a.GetTextureOffset(n) != b.GetTextureOffset(n)) return false;
                        break;
                    case ShaderPropertyType.Color:
                        if (a.GetColor(n) != b.GetColor(n)) return false;
                        break;
                    case ShaderPropertyType.Vector:
                        if (a.GetVector(n) != b.GetVector(n)) return false;
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (!Mathf.Approximately(a.GetFloat(n), b.GetFloat(n))) return false;
                        break;
                    case ShaderPropertyType.Int:
                        if (a.GetInt(n) != b.GetInt(n)) return false;
                        break;
                }
            }
            var ka = a.shaderKeywords;
            var kb = b.shaderKeywords;
            if (ka.Length != kb.Length) return false;
            Array.Sort(ka); Array.Sort(kb);
            for (int i = 0; i < ka.Length; i++) if (ka[i] != kb[i]) return false;
            return true;
        }

        /// <summary>
        /// Merge identical consecutive opaque slots when animation does not switch them independently.
        /// 动画未单独切换时，合并连续的相同不透明材质槽。
        /// </summary>
        private static void TryMergeOpaqueSlots(Renderer r, AnimationFacts facts)
        {
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length < 2) return;
            Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh :
                r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null;
            if (mesh == null || mesh.subMeshCount != mats.Length) return;

            var path = PathUtil.RelativePath(r.transform.root, r.transform);
            if (facts.IndependentSlotSwitch.Any(k => k.StartsWith(path))) return;

            bool allOpaqueSame = true;
            for (int i = 1; i < mats.Length; i++)
            {
                if (mats[i] != mats[0] || mats[i] == null) { allOpaqueSame = false; break; }
                if (mats[i].GetTag("RenderType", false, "") == "Transparent") { allOpaqueSame = false; break; }
            }
            if (!allOpaqueSame) return;

            // Combine all submeshes into 0. / 合并全部 submesh 到 0。
            var combined = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) combined.AddRange(mesh.GetTriangles(s));
            mesh.subMeshCount = 1;
            mesh.SetTriangles(combined, 0);
            r.sharedMaterials = new[] { mats[0] };
            AtoLog.Info($"Merged opaque material slots on {r.name}");
        }

        private static void ApplyImporterSettings(IEnumerable<Texture2D> textures, PlatformTextureSettings plat,
            AtoBuildPlatform platform, string folder, AvatarTextureOptimizer settings, AtoReportData report)
        {
            AssetDatabase.Refresh();
            foreach (var tex in textures)
            {
                if (tex == null) continue;
                var path = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback: CompressTexture on the in-memory asset. / 回退：内存资产压缩。
                    tex.wrapMode = TextureWrapMode.Clamp;
                    continue;
                }
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.isReadable = false;
                ti.npotScale = TextureImporterNPOTScale.None;
                bool isNormal = tex.name.ToLowerInvariant().Contains("norm") || tex.name.ToLowerInvariant().Contains("bump");
                bool hasAlpha = ti.DoesSourceTextureHaveAlpha();
                bool gray = false;
                var fmt = ChooseFormat(plat, isNormal, hasAlpha, gray, settings.experimentalNpot, platform, report);
                bool mip = MipFor(plat, isNormal, hasAlpha, gray);
                ti.mipmapEnabled = mip;
                ti.streamingMipmaps = mip; // VRChat: mip on => streaming on. / VRC：开 mip 必须开 streaming。
                ti.sRGBTexture = !isNormal && !gray;
                if (isNormal) ti.textureType = TextureImporterType.NormalMap;
                var platName = platform == AtoBuildPlatform.Android ? "Android" :
                    platform == AtoBuildPlatform.iOS ? "iPhone" : "Standalone";
                var ps = ti.GetPlatformTextureSettings(platName);
                ps.overridden = true;
                ps.format = ToImporterFormat(fmt, hasAlpha, isNormal, gray, report, tex);
                ps.textureCompression = TextureImporterCompression.Compressed;
                ps.compressionQuality = plat.useCrunch ? plat.crunchQuality : plat.compressorQuality;
                if (plat.useCrunch && SupportsCrunch(fmt)) ps.crunchedCompression = true;
                ti.SetPlatformTextureSettings(ps);
                ti.SaveAndReimport();
            }
        }

        private static bool MipFor(PlatformTextureSettings p, bool n, bool a, bool g)
        {
            if (n) return p.mipStreamingNormal;
            if (g) return p.mipStreamingGray;
            if (a) return p.mipStreamingTransparent;
            return p.mipStreamingOpaque;
        }

        private static AtoCompressionFormat ChooseFormat(PlatformTextureSettings p, bool n, bool a, bool g,
            bool npot, AtoBuildPlatform plat, AtoReportData report)
        {
            var f = n ? p.normalFormat : g ? p.grayFormat : a ? p.transparentFormat : p.opaqueFormat;
            if (f == AtoCompressionFormat.Auto)
            {
                if (n) return plat == AtoBuildPlatform.PC ? AtoCompressionFormat.BC5 : AtoCompressionFormat.ASTC_4x4;
                if (g) return plat == AtoBuildPlatform.PC ? AtoCompressionFormat.BC4 : AtoCompressionFormat.ASTC_6x6;
                if (a) return plat == AtoBuildPlatform.PC ? AtoCompressionFormat.BC7 : AtoCompressionFormat.ASTC_6x6;
                return plat == AtoBuildPlatform.PC ? AtoCompressionFormat.BC7 : AtoCompressionFormat.ASTC_6x6;
            }
            if (npot && (f == AtoCompressionFormat.PVRTC_RGB4 || f == AtoCompressionFormat.PVRTC_RGBA4))
            {
                report.Warnings.Add(AtoLoc.T("ato.warn.pvrtcNpot"));
                return AtoCompressionFormat.ASTC_6x6;
            }
            if (a && (f == AtoCompressionFormat.DXT1_BC1 || f == AtoCompressionFormat.ETC2_RGB || f == AtoCompressionFormat.PVRTC_RGB4))
            {
                report.Warnings.Add(AtoLoc.T("ato.warn.alphaFallback"));
                return plat == AtoBuildPlatform.PC ? AtoCompressionFormat.BC7 : AtoCompressionFormat.ASTC_6x6;
            }
            return f;
        }

        private static TextureImporterFormat ToImporterFormat(AtoCompressionFormat f, bool alpha, bool normal, bool gray,
            AtoReportData report, Texture2D tex)
        {
            switch (f)
            {
                case AtoCompressionFormat.Uncompressed: return alpha ? TextureImporterFormat.RGBA32 : TextureImporterFormat.RGB24;
                case AtoCompressionFormat.DXT1_BC1: return TextureImporterFormat.DXT1;
                case AtoCompressionFormat.DXT5_BC3: return TextureImporterFormat.DXT5;
                case AtoCompressionFormat.BC4:
                    if (!gray) { report.Warnings.Add(AtoLoc.T("ato.warn.grayFallback")); return TextureImporterFormat.Automatic; }
                    return TextureImporterFormat.BC4;
                case AtoCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case AtoCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case AtoCompressionFormat.ETC2_RGB: return TextureImporterFormat.ETC2_RGB4;
                case AtoCompressionFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case AtoCompressionFormat.EAC_R: return TextureImporterFormat.EAC_R;
                case AtoCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case AtoCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case AtoCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case AtoCompressionFormat.PVRTC_RGB4: return TextureImporterFormat.PVRTC_RGB4;
                case AtoCompressionFormat.PVRTC_RGBA4: return TextureImporterFormat.PVRTC_RGBA4;
                default: return TextureImporterFormat.Automatic;
            }
        }

        private static bool SupportsCrunch(AtoCompressionFormat f) =>
            f == AtoCompressionFormat.DXT1_BC1 || f == AtoCompressionFormat.DXT5_BC3 ||
            f == AtoCompressionFormat.ETC2_RGB || f == AtoCompressionFormat.ETC2_RGBA8;
    }
}

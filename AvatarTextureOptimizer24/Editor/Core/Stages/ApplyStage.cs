// ============================================================================
// ApplyStage.cs — 阶段7：应用优化结果 / Stage 7: apply results
// (EN) Persists atlas textures (with import settings: compression, MipStreaming,
//      clamp, sRGB, filter), remaps mesh UVs to atlas positions (clone mesh),
//      clones materials pointing their texture properties at the atlases, and
//      registers all replacements via ObjectRegistry.
// (ZH) 持久化图集贴图（含导入设置：压缩、MipStreaming、clamp、sRGB、filter），
//      将网格 UV 重映射到图集位置（克隆网格），克隆材质并让贴图属性指向图集，
//      并通过 ObjectRegistry 登记全部替换。
// ============================================================================

using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class ApplyStage
    {
        private readonly ATOBuildContext _ctx;

        // 贴图引用 → 图集贴图 / texture ref -> atlas texture
        private readonly Dictionary<ATOTextureRef, (ATOAtlas atlas, ATOTextureClass kind)> _texToAtlas =
            new Dictionary<ATOTextureRef, (ATOAtlas, ATOTextureClass)>();

        // 图集贴图持久化缓存 / persisted atlas texture cache
        private readonly Dictionary<Texture2D, Texture2D> _persisted = new Dictionary<Texture2D, Texture2D>();

        // 材质克隆缓存（共享材质只克隆一次）/ material clone cache (clone shared material once)
        private readonly Dictionary<Material, Material> _matClone = new Dictionary<Material, Material>();

        // old -> new 映射（供动画引用重写）/ old -> new mapping (for animation rewriting)
        private readonly Dictionary<Object, Object> _mapping = new Dictionary<Object, Object>();

        private string _genDir;

        public ApplyStage(ATOBuildContext ctx) => _ctx = ctx;

        public void Run()
        {
            if (!_ctx.Atlas.enableAtlas)
            {
                ApplyNoAtlas();
                Finalize();
                return;
            }

            BuildAtlasLookup();

            _genDir = "Assets/ATO_Generated/" + Sanitize(_ctx.AvatarRoot.name);
            if (Directory.Exists(_genDir)) AssetDatabase.DeleteAsset(_genDir);

            // 1) 持久化图集贴图 / persist atlas textures
            PersistAtlasTextures();

            // 2) 重映射网格 UV / remap mesh UVs
            RemapMeshes();

            // 3) 克隆材质并指向图集 / clone materials pointing at atlases
            RemapMaterials();

            // 4) 整图缩放贴图（被不安全引用岛引用的安全贴图）/ whole-scaled textures
            ApplyWholeScaledTextures();

            Finalize();
        }

        private void Finalize()
        {
            // 4) 材质/贴图去重 + 材质槽合并 / material/texture dedup + slot merge
            if (_ctx.Dedup.materials || _ctx.Dedup.textures)
                new ATODedup(_ctx, _mapping).Run();

            // 5) 动画引用重写 / animation reference rewriting
            ATOAnimationRewriter.Rewrite(_ctx, _mapping);
        }

        // ---------------------------------------------------------------------
        // 贴图→图集 查找表 / texture -> atlas lookup
        // ---------------------------------------------------------------------
        private void BuildAtlasLookup()
        {
            _texToAtlas.Clear();
            foreach (var atlas in _ctx.Pack.Atlases)
            {
                foreach (var kv in atlas.KindTextures)
                {
                    var kind = kv.Key;
                    foreach (var g in atlas.Groups)
                        foreach (var t in g.Textures)
                        {
                            if (KindOf(t) == kind && !_texToAtlas.ContainsKey(t))
                                _texToAtlas[t] = (atlas, kind);
                        }
                }
            }
        }

        private static ATOTextureClass KindOf(ATOTextureRef t)
        {
            switch (t.Usage)
            {
                case ATOTextureUsage.NormalMap: return ATOTextureClass.Normal;
                case ATOTextureUsage.Mask:
                case ATOTextureUsage.Grayscale: return ATOTextureClass.Grayscale;
                default: return ATOTextureClass.Opaque;
            }
        }

        // ---------------------------------------------------------------------
        // 持久化图集贴图 / persist atlas textures
        // ---------------------------------------------------------------------
        private void PersistAtlasTextures()
        {
            foreach (var atlas in _ctx.Pack.Atlases)
            {
                foreach (var kv in atlas.KindTextures)
                {
                    var kind = kv.Key;
                    var tex = kv.Value;
                    var persisted = PersistTexture(tex, atlas, kind);
                    atlas.KindTextures[kind] = persisted;
                }
            }
        }

        private Texture2D PersistTexture(Texture2D tex, ATOAtlas atlas, ATOTextureClass kind)
        {
            if (!Directory.Exists(_genDir)) Directory.CreateDirectory(_genDir);
            var path = _genDir + "/" + tex.name + ".png";

            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool isNormal = kind == ATOTextureClass.Normal;
                bool isGray = kind == ATOTextureClass.Grayscale;
                importer.textureType = isNormal ? TextureImporterType.NormalMap
                    : isGray ? TextureImporterType.SingleChannel : TextureImporterType.Default;
                importer.sRGBTexture = !isNormal && !isGray && atlas.TypeGroup.Srgb;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = atlas.TypeGroup.FilterMode;
                importer.mipmapEnabled = _ctx.Compression.Get(kind).mipmaps;
                importer.streamingMipmaps = _ctx.Compression.Get(kind).mipmaps; // 绑定 / bound together
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.crunchedCompression = false;
                importer.maxTextureSize = Mathf.Max(atlas.Width, atlas.Height);
                ApplyCompressionFormat(importer, _ctx.Compression.Get(kind).format);
                importer.SaveAndReimport();
            }

            var result = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            _persisted[tex] = result;
            return result;
        }

        private void ApplyCompressionFormat(TextureImporter importer, ATOCompressionFormat format)
        {
            if (format == ATOCompressionFormat.Auto) return;

            var standalone = new TextureImporterPlatformSettings { name = "Standalone" };
            var android = new TextureImporterPlatformSettings { name = "Android" };
            var ios = new TextureImporterPlatformSettings { name = "iPhone" };
            bool setStandalone = false, setAndroid = false, setIos = false;

            switch (format)
            {
                case ATOCompressionFormat.ASTC:
                    android.format = TextureImporterFormat.ASTC_6x6; ios.format = TextureImporterFormat.ASTC_6x6;
                    setAndroid = setIos = true; break;
                case ATOCompressionFormat.BC7: standalone.format = TextureImporterFormat.BC7; setStandalone = true; break;
                case ATOCompressionFormat.BC5: standalone.format = TextureImporterFormat.BC5; setStandalone = true; break;
                case ATOCompressionFormat.BC3: standalone.format = TextureImporterFormat.DXT5; setStandalone = true; break;
                case ATOCompressionFormat.BC1: standalone.format = TextureImporterFormat.DXT1; setStandalone = true; break;
                case ATOCompressionFormat.ETC2_RGB: android.format = TextureImporterFormat.ETC2_RGB4; setAndroid = true; break;
                case ATOCompressionFormat.ETC2_RGBA: android.format = TextureImporterFormat.ETC2_RGBA8; setAndroid = true; break;
                case ATOCompressionFormat.R8: standalone.format = TextureImporterFormat.R8; setStandalone = true; break;
                case ATOCompressionFormat.RGBA32: standalone.format = TextureImporterFormat.RGBA32; setStandalone = true; break;
                case ATOCompressionFormat.RGB24: standalone.format = TextureImporterFormat.RGB24; setStandalone = true; break;
            }

            if (setStandalone) { standalone.overridden = true; importer.SetPlatformTextureSettings(standalone); }
            if (setAndroid) { android.overridden = true; importer.SetPlatformTextureSettings(android); }
            if (setIos) { ios.overridden = true; importer.SetPlatformTextureSettings(ios); }
        }

        // ---------------------------------------------------------------------
        // 重映射网格 UV / remap mesh UVs
        // ---------------------------------------------------------------------
        private void RemapMeshes()
        {
            // 网格 → 克隆（共享网格只克隆一次）/ mesh -> clone (clone shared mesh once)
            var meshClone = new Dictionary<Mesh, Mesh>();
            // 网格 → 渲染器 / mesh -> renderers
            var meshRenderers = new Dictionary<Mesh, List<ATORendererInfo>>();
            foreach (var renderer in _ctx.Collect.Renderers)
            {
                if (!meshRenderers.TryGetValue(renderer.Mesh, out var list)) { list = new List<ATORendererInfo>(); meshRenderers[renderer.Mesh] = list; }
                list.Add(renderer);
            }

            foreach (var kv in meshRenderers)
            {
                var mesh = kv.Key;
                var renderers = kv.Value;
                var newMesh = Object.Instantiate(mesh);
                newMesh.name = mesh.name + "_ATO";
                meshClone[mesh] = newMesh;

                RemapMeshIslands(mesh, newMesh);

                foreach (var renderer in renderers)
                    AssignMesh(renderer, newMesh);
            }
        }

        private void RemapMeshIslands(Mesh mesh, Mesh newMesh)
        {
            // AAO UV 疏散：对每个 SkinnedMeshRenderer，若 AAO 使用某通道，先疏散
            // AAO UV evacuation for SkinnedMeshRenderers (before remapping)
            EvacuateForAao(mesh, newMesh);

            // 收集该网格所有已装箱岛 / collect placed islands for this mesh
            // 仅光栅化过的岛（RasterizedMask != null）才是真正装箱的
            var placedIslands = new List<(ATOUVIsland island, ATOAtlas atlas)>();
            foreach (var atlas in _ctx.Pack.Atlases)
                foreach (var g in atlas.Groups)
                    if (g.Mesh == mesh)
                        foreach (var island in g.Islands)
                            if (island.RasterizedMask != null && island.RasterX >= 0)
                                placedIslands.Add((island, atlas));

            for (int channel = 0; channel < 8; channel++)
            {
                var uvs = new List<Vector2>();
                newMesh.GetUVs(channel, uvs);
                if (uvs.Count == 0) continue;

                bool changed = false;
                // 顶点 → 新 UV / vertex -> new UV
                var vertexNew = new Dictionary<int, Vector2>();

                foreach (var (island, atlas) in placedIslands)
                {
                    if (island.UvChannel != channel) continue;

                    float su = island.ScaledPixelW;
                    float sh = island.ScaledPixelH;
                    for (int t = 0; t < island.TriangleUVs.Count; t += 3)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            int vert = island.TriangleVerts[t + k];
                            if (vertexNew.ContainsKey(vert)) continue;
                            var raw = island.TriangleUVs[t + k];
                            float localU = (raw.x + island.Translation.x - island.Bounds.xMin) / Mathf.Max(1e-6f, island.Bounds.width);
                            float localV = (raw.y + island.Translation.y - island.Bounds.yMin) / Mathf.Max(1e-6f, island.Bounds.height);
                            float ix = localU * su;
                            float iy = localV * sh;

                            float ax, ay;
                            if (island.Rotated90) { ax = island.RasterX * ATOPacker.Granularity + (sh - iy); ay = island.RasterY * ATOPacker.Granularity + ix; }
                            else { ax = island.RasterX * ATOPacker.Granularity + ix; ay = island.RasterY * ATOPacker.Granularity + iy; }

                            vertexNew[vert] = new Vector2(ax / atlas.Width, ay / atlas.Height);
                        }
                    }
                }

                foreach (var kv in vertexNew)
                {
                    uvs[kv.Key] = kv.Value;
                    changed = true;
                }

                if (changed) newMesh.SetUVs(channel, uvs);
            }
        }

        private void EvacuateForAao(Mesh originalMesh, Mesh newMesh)
        {
            if (!ATOAaoCompat.Available) return;

            // 收集使用该网格的 SkinnedMeshRenderer / collect SkinnedMeshRenderers using this mesh
            var smrs = new List<(SkinnedMeshRenderer smr, bool[] channels)>();
            foreach (var renderer in _ctx.Collect.Renderers)
            {
                if (renderer.Mesh != originalMesh) continue;
                if (renderer.Renderer is SkinnedMeshRenderer smr)
                    smrs.Add((smr, renderer.UvChannelPresent));
            }
            if (smrs.Count == 0) return;

            for (int c = 0; c < 8; c++)
            {
                // 该通道是否被任何渲染器的 AAO 使用 / is this channel used by AAO on any renderer
                bool anyUsed = false;
                foreach (var (smr, channels) in smrs)
                    if (channels[c] && ATOAaoCompat.IsTexCoordUsed(smr, c)) { anyUsed = true; break; }
                if (!anyUsed) continue;

                // 找空闲通道（基于第一个渲染器的网格通道占用）/ find spare channel
                var first = smrs[0];
                int saved = ATOAaoCompat.FindSpareChannel(first.smr, c, first.channels);
                if (saved < 0)
                {
                    ATOLog.Warn($"[aao] no spare UV channel for evacuation of UV{c} on {first.smr.name}");
                    continue;
                }

                // 疏散克隆网格的原始 UV（一次）/ evacuate the clone's original UVs (once)
                ATOAaoCompat.CopyUvChannel(newMesh, c, saved);

                // 注册每个渲染器 / register each renderer
                foreach (var (smr, channels) in smrs)
                    if (channels[c] && ATOAaoCompat.IsTexCoordUsed(smr, c))
                        ATOAaoCompat.RegisterEvacuation(smr, c, saved);

                ATOLog.Info($"[aao] evacuated UV{c} -> UV{saved} on {first.smr.name}");
            }
        }

        private void AssignMesh(ATORendererInfo renderer, Mesh newMesh)
        {
            var old = ATOMeshUtils.GetMesh(renderer.Renderer);
            if (renderer.Renderer is SkinnedMeshRenderer smr) smr.sharedMesh = newMesh;
            else if (renderer.Renderer is MeshRenderer mr) mr.GetComponent<MeshFilter>().sharedMesh = newMesh;
            ObjectRegistry.RegisterReplacedObject(old, newMesh);
        }

        // ---------------------------------------------------------------------
        // 克隆材质并指向图集 / clone materials pointing at atlases
        // ---------------------------------------------------------------------
        private void RemapMaterials()
        {
            foreach (var renderer in _ctx.Collect.Renderers)
            {
                var materials = renderer.Renderer.sharedMaterials;
                bool anyChanged = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;

                    var slot = FindSlot(renderer, i);
                    if (slot == null) continue;

                    var newMat = CloneMaterialIfNeeded(mat, slot);
                    if (newMat != mat)
                    {
                        materials[i] = newMat;
                        anyChanged = true;
                    }
                }

                if (anyChanged) renderer.Renderer.sharedMaterials = materials;
            }
        }

        private ATOSlot FindSlot(ATORendererInfo renderer, int index)
        {
            foreach (var s in renderer.Slots) if (s.SlotIndex == index) return s;
            return null;
        }

        private Material CloneMaterialIfNeeded(Material mat, ATOSlot slot)
        {
            bool needsClone = false;
            foreach (var t in slot.Textures)
            {
                if (!t.SafeToOptimize) continue;
                if (_texToAtlas.TryGetValue(t.Ref, out _)) { needsClone = true; break; }
            }
            if (!needsClone) return mat;

            if (!_matClone.TryGetValue(mat, out var newMat))
            {
                newMat = new Material(mat) { name = mat.name + "_ATO", parent = null };
                ObjectRegistry.RegisterReplacedObject(mat, newMat);
                _matClone[mat] = newMat;
                _mapping[mat] = newMat;
            }

            foreach (var t in slot.Textures)
            {
                if (!t.SafeToOptimize) continue;
                if (_texToAtlas.TryGetValue(t.Ref, out var target))
                {
                    newMat.SetTexture(t.PropertyName, target.atlas.KindTextures[target.kind]);
                    // 贴图映射（动画切换贴图时使用）/ texture mapping (for animation texture switches)
                    if (!_mapping.ContainsKey(t.Ref.Texture))
                        _mapping[t.Ref.Texture] = target.atlas.KindTextures[target.kind];
                }
            }

            return newMat;
        }

        // ---------------------------------------------------------------------
        // 无图集模式 / no-atlas mode (whole-texture scaling)
        // ---------------------------------------------------------------------
        private void ApplyNoAtlas()
        {
            _genDir = "Assets/ATO_Generated/" + Sanitize(_ctx.AvatarRoot.name);
            if (Directory.Exists(_genDir)) AssetDatabase.DeleteAsset(_genDir);
            ApplyWholeScaledTextures();
        }

        /// <summary>(EN) Apply whole-texture scaling for textures whose islands were skipped from atlasing. (ZH) 对跳过图集化的贴图应用整图缩放。</summary>
        private void ApplyWholeScaledTextures()
        {
            // 每张唯一贴图：若需缩放则生成缩放版 / scale each unique texture if needed
            var scaledMap = new Dictionary<ATOTextureRef, Texture2D>();
            foreach (var tex in _ctx.Collect.Canonical.Values)
            {
                if (tex.Whitelisted || tex.Texture == null) continue;
                if (Mathf.Approximately(tex.WholeScaleX, 1f) && Mathf.Approximately(tex.WholeScaleY, 1f)) continue;

                var scaled = ScaleTexture(tex);
                scaledMap[tex] = scaled;
            }

            if (scaledMap.Count == 0) return;

            // 替换材质引用 / replace material references
            foreach (var renderer in _ctx.Collect.Renderers)
            {
                var materials = renderer.Renderer.sharedMaterials;
                bool anyChanged = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;
                    var slot = FindSlot(renderer, i);
                    if (slot == null) continue;

                    bool changed = false;
                    var newMat = _matClone.TryGetValue(mat, out var clone) ? clone : null;
                    foreach (var t in slot.Textures)
                    {
                        if (!t.SafeToOptimize) continue;
                        if (!scaledMap.TryGetValue(t.Ref, out var scaledTex)) continue;
                        if (newMat == null)
                        {
                            newMat = new Material(mat) { name = mat.name + "_ATO", parent = null };
                            ObjectRegistry.RegisterReplacedObject(mat, newMat);
                            _matClone[mat] = newMat;
                            _mapping[mat] = newMat;
                        }
                        newMat.SetTexture(t.PropertyName, scaledTex);
                        if (!_mapping.ContainsKey(t.Ref.Texture)) _mapping[t.Ref.Texture] = scaledTex;
                        changed = true;
                    }
                    if (changed) { materials[i] = newMat; anyChanged = true; }
                }
                if (anyChanged) renderer.Renderer.sharedMaterials = materials;
            }
        }

        private Texture2D ScaleTexture(ATOTextureRef tex)
        {
            int tw = tex.Texture.width, th = tex.Texture.height;
            var src = ATOTextureIO.ReadRegion(tex.Texture, 0, 0, tw, th);
            int dw = Mathf.Max(1, Mathf.RoundToInt(tw * tex.WholeScaleX));
            int dh = Mathf.Max(1, Mathf.RoundToInt(th * tex.WholeScaleY));

            var scaled = new Color[dw * dh];
            ATOQuality.ResampleRegion(src, tw, th, 0, 0, tw, th, dw, dh,
                linearSpace: true, premultiplyAlpha: tex.Classification == ATOTextureClass.Transparent, scaled);

            var result = new Texture2D(dw, dh, TextureFormat.RGBA32, true, false);
            result.SetPixels(scaled);
            result.Apply(true, false);
            result.name = tex.Texture.name + "_ATO_scaled";

            return PersistScaled(result, tex);
        }

        private Texture2D PersistScaled(Texture2D tex, ATOTextureRef source)
        {
            if (!Directory.Exists(_genDir)) Directory.CreateDirectory(_genDir);
            var path = _genDir + "/" + tex.name + ".png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                var kind = source.Classification;
                importer.textureType = kind == ATOTextureClass.Normal ? TextureImporterType.NormalMap
                    : kind == ATOTextureClass.Grayscale ? TextureImporterType.SingleChannel : TextureImporterType.Default;
                importer.sRGBTexture = kind == ATOTextureClass.Opaque || kind == ATOTextureClass.Transparent;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = _ctx.Compression.Get(kind).mipmaps;
                importer.streamingMipmaps = _ctx.Compression.Get(kind).mipmaps;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }
    }
}

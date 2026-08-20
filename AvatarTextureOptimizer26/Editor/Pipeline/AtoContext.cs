using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class AtoContext : IDisposable
    {
        public BuildContext Ndmf;
        public GameObject Avatar;
        public AvatarTextureOptimizer Component;
        public AtoPlatform Platform;
        public AtoPlatformOverride Settings;
        public AnimatorServicesContext Anim;
        public AtoProgress Progress;
        public AtoReportData Report = new AtoReportData();
        public AtoAnimFacts AnimFacts;

        public readonly HashSet<Texture2D> WhitelistTextures = new HashSet<Texture2D>();
        public readonly HashSet<Material> WhitelistMaterials = new HashSet<Material>();
        public readonly HashSet<Renderer> WhitelistRenderers = new HashSet<Renderer>();
        public readonly HashSet<Object> WhitelistObjects = new HashSet<Object>();

        public readonly List<Renderer> Renderers = new List<Renderer>();
        public readonly Dictionary<Texture2D, Texture2D> TextureRemap = new Dictionary<Texture2D, Texture2D>();
        public readonly List<AtoTextureUse> Uses = new List<AtoTextureUse>();
        public readonly Dictionary<AtoUvKey, AtoUvGroup> UvGroups = new Dictionary<AtoUvKey, AtoUvGroup>();
        public readonly List<AtoTypeGroup> TypeGroups = new List<AtoTypeGroup>();
        public readonly List<AtoAtlas> Atlases = new List<AtoAtlas>();
        public readonly Dictionary<Texture2D, Color32[]> PixelCache = new Dictionary<Texture2D, Color32[]>();
        public readonly HashSet<Object> OwnedTemps = new HashSet<Object>();

        public bool Canceled;
        public int UvGroupSerial;
        public int TypeGroupSerial;
        public int AtlasSerial;
        public int IslandSerial;

        public Texture2D GetReadable(Texture2D src)
        {
            if (src == null) return null;
            if (PixelCache.ContainsKey(src) && src.isReadable) return src;
            return AtoTextureIO.EnsureReadable(this, src);
        }

        public Color32[] GetPixels(Texture2D src)
        {
            src = GetReadable(src);
            if (src == null) return Array.Empty<Color32>();
            if (PixelCache.TryGetValue(src, out var px)) return px;
            px = src.GetPixels32();
            PixelCache[src] = px;
            return px;
        }

        public void ReleasePixels(Texture2D src)
        {
            if (src != null) PixelCache.Remove(src);
        }

        public void RegisterTemp(Object o)
        {
            if (o == null) return;
            OwnedTemps.Add(o);
            try
            {
                if (Ndmf != null && Ndmf.AssetContainer != null)
                    Ndmf.AssetSaver.SaveAsset(o);
            }
            catch
            {
                // AssetSaver API variance — fallback is fine; NDMF will serialize referenced assets.
                // AssetSaver API 可能有差异，回退即可；NDMF 会序列化被引用资产。
            }
        }

        public void Dispose()
        {
            foreach (var rt in _rts)
            {
                if (rt != null) rt.Release();
            }
            _rts.Clear();
            PixelCache.Clear();
            // Do not DestroyImmediate generated textures — they are baked assets.
            // 不要 DestroyImmediate 成品贴图——它们是烘焙资产。
        }

        private readonly List<RenderTexture> _rts = new List<RenderTexture>();

        public RenderTexture GetRT(int w, int h, GraphicsFormat fmt)
        {
            var rt = new RenderTexture(w, h, 0, fmt)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
            _rts.Add(rt);
            return rt;
        }

        public void ReleaseRT(RenderTexture rt)
        {
            if (rt == null) return;
            _rts.Remove(rt);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}

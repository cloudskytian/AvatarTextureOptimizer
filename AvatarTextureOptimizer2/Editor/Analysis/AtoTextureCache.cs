using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Decode / import-settings cache. Avoids repeated GetPixels.
    /// 解码与导入设置缓存，避免重复 GetPixels。
    /// </summary>
    public sealed class AtoTextureCache : IDisposable
    {
        readonly Dictionary<int, Color32[]> _pixels = new Dictionary<int, Color32[]>();
        readonly Dictionary<int, AtoImportKey> _import = new Dictionary<int, AtoImportKey>();
        readonly List<RenderTexture> _rts = new List<RenderTexture>();

        public Color32[] GetPixels(Texture2D tex)
        {
            if (tex == null) return Array.Empty<Color32>();
            var id = tex.GetInstanceID();
            if (_pixels.TryGetValue(id, out var p)) return p;
            p = AtoTextureUtil.SafeGetPixels32(tex);
            _pixels[id] = p;
            return p;
        }

        public AtoImportKey GetImport(Texture2D tex)
        {
            var id = tex.GetInstanceID();
            if (_import.TryGetValue(id, out var k)) return k;
            k = AtoImportKey.From(tex);
            _import[id] = k;
            return k;
        }

        public RenderTexture BorrowRt(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGB32)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, fmt, RenderTextureReadWrite.Linear);
            _rts.Add(rt);
            return rt;
        }

        public void ReleaseGpu()
        {
            foreach (var rt in _rts)
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            _rts.Clear();
        }

        public void Dispose()
        {
            ReleaseGpu();
            _pixels.Clear();
        }
    }

    public struct AtoImportKey : IEquatable<AtoImportKey>
    {
        public TextureImporterType type;
        public TextureImporterCompression compression;
        public TextureImporterNPOTScale npot;
        public FilterMode filter;
        public TextureWrapMode wrap;
        public bool sRGB;
        public bool mipmap;
        public int aniso;
        public int maxSize;
        public string assetPath;

        public static AtoImportKey From(Texture2D tex)
        {
            var k = new AtoImportKey
            {
                filter = tex.filterMode,
                wrap = tex.wrapMode,
                sRGB = tex.isDataSRGB,
                mipmap = tex.mipmapCount > 1,
                aniso = tex.anisoLevel,
                maxSize = Mathf.Max(tex.width, tex.height),
                assetPath = UnityEditor.AssetDatabase.GetAssetPath(tex)
            };
            if (!string.IsNullOrEmpty(k.assetPath))
            {
                var imp = UnityEditor.AssetImporter.GetAtPath(k.assetPath) as UnityEditor.TextureImporter;
                if (imp != null)
                {
                    k.type = imp.textureType;
                    k.compression = imp.textureCompression;
                    k.npot = imp.npotScale;
                    k.sRGB = imp.sRGBTexture;
                    k.mipmap = imp.mipmapEnabled;
                    k.aniso = imp.anisoLevel;
                    k.maxSize = imp.maxTextureSize;
                    k.filter = imp.filterMode;
                    k.wrap = imp.wrapMode;
                }
            }
            return k;
        }

        public bool Equals(AtoImportKey o) =>
            type == o.type && compression == o.compression && npot == o.npot &&
            filter == o.filter && wrap == o.wrap && sRGB == o.sRGB &&
            mipmap == o.mipmap && aniso == o.aniso && maxSize == o.maxSize &&
            assetPath == o.assetPath;

        public override bool Equals(object obj) => obj is AtoImportKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine((int)type, (int)filter, sRGB, mipmap, maxSize, assetPath);
    }

    public static class AtoTextureUtil
    {
        public static Color32[] SafeGetPixels32(Texture2D tex)
        {
            try
            {
                if (tex.isReadable) return tex.GetPixels32();
            }
            catch { /* fall through */ }

            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                tmp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                tmp.Apply();
                var px = tmp.GetPixels32();
                Object.DestroyImmediate(tmp);
                return px;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        public static ulong ContentHash(Color32[] px)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < px.Length; i++)
                {
                    h ^= px[i].r; h *= 1099511628211UL;
                    h ^= px[i].g; h *= 1099511628211UL;
                    h ^= px[i].b; h *= 1099511628211UL;
                    h ^= px[i].a; h *= 1099511628211UL;
                }
                return h;
            }
        }
    }
}

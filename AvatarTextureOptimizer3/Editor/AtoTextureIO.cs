// English: Decode textures once, fingerprint importers, classify linear/sRGB, cache pixels.
// 中文：贴图只解码一次，导入设置指纹，线性/sRGB 分类，缓存像素。
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public sealed class AtoDecoded
    {
        public Texture2D Source;
        public Color32[] Pixels;
        public int W, H;
        public bool Linear;
        public bool HasAlpha;
        public FilterMode Filter;
        public TextureWrapMode Wrap;
        public string Fingerprint;
        public AtoTextureClass ClassHint;
        public bool SolidColor;
        public Color32 Solid;
    }

    public sealed class AtoTextureCache : IDisposable
    {
        private readonly Dictionary<int, AtoDecoded> _map = new Dictionary<int, AtoDecoded>();

        public AtoDecoded Get(Texture2D tex)
        {
            if (tex == null) return null;
            var id = tex.GetInstanceID();
            if (_map.TryGetValue(id, out var d)) return d;
            d = Decode(tex);
            _map[id] = d;
            return d;
        }

        public static string ImporterFingerprint(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            var imp = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null)
                return $"runtime|{tex.width}x{tex.height}|{tex.format}|{tex.filterMode}|{tex.wrapMode}|{tex.mipmapCount}";
            var sb = new StringBuilder();
            sb.Append(imp.sRGBTexture).Append('|')
              .Append(imp.textureType).Append('|')
              .Append(imp.filterMode).Append('|')
              .Append(imp.wrapMode).Append('|')
              .Append(imp.mipmapEnabled).Append('|')
              .Append(imp.maxTextureSize).Append('|')
              .Append(imp.textureCompression).Append('|')
              .Append(imp.alphaSource).Append('|')
              .Append(imp.npotScale).Append('|')
              .Append(imp.isReadable);
            return sb.ToString();
        }

        public static string ContentHash(AtoDecoded d)
        {
            if (d == null || d.Pixels == null) return "";
            using (var md5 = MD5.Create())
            {
                var bytes = new byte[d.Pixels.Length * 4];
                for (int i = 0; i < d.Pixels.Length; i++)
                {
                    bytes[i * 4] = d.Pixels[i].r;
                    bytes[i * 4 + 1] = d.Pixels[i].g;
                    bytes[i * 4 + 2] = d.Pixels[i].b;
                    bytes[i * 4 + 3] = d.Pixels[i].a;
                }
                return BitConverter.ToString(md5.ComputeHash(bytes));
            }
        }

        private static AtoDecoded Decode(Texture2D tex)
        {
            var d = new AtoDecoded
            {
                Source = tex,
                W = tex.width,
                H = tex.height,
                Filter = tex.filterMode,
                Wrap = tex.wrapMode,
                Fingerprint = ImporterFingerprint(tex)
            };
            var path = AssetDatabase.GetAssetPath(tex);
            var imp = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            d.Linear = imp != null && !imp.sRGBTexture;
            if (imp != null && imp.textureType == TextureImporterType.NormalMap)
                d.ClassHint = AtoTextureClass.Normal;

            d.Pixels = ReadPixels(tex);
            d.HasAlpha = false;
            bool solid = true;
            var s0 = d.Pixels.Length > 0 ? d.Pixels[0] : new Color32(0, 0, 0, 255);
            for (int i = 0; i < d.Pixels.Length; i++)
            {
                var p = d.Pixels[i];
                if (p.a < 250) d.HasAlpha = true;
                if (p.r != s0.r || p.g != s0.g || p.b != s0.b || p.a != s0.a) solid = false;
            }
            d.SolidColor = solid;
            d.Solid = s0;
            return d;
        }

        public static Color32[] ReadPixels(Texture2D tex)
        {
            if (tex.isReadable)
            {
                try { return tex.GetPixels32(); }
                catch { /* fall through */ }
            }
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var tmp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
            tmp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            tmp.Apply();
            var px = tmp.GetPixels32();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(tmp);
            return px;
        }

        public void Dispose() => _map.Clear();
    }
}

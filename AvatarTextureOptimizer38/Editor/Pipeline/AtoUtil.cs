using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoProgress
    {
        public static void Update(AtoLanguageMode lang, string key, float t)
        {
            var title = AtoLoc.T("ato.progress.title", lang);
            var msg = AtoLoc.T(key, lang);
            if (EditorUtility.DisplayCancelableProgressBar(title, msg, Mathf.Clamp01(t)))
                throw new AtoCanceledException();
        }

        public static void Clear() => EditorUtility.ClearProgressBar();
    }

    /// <summary>
    /// Decode Texture2D via blit when not readable. LRU-cached. / 不可读贴图用 blit 解码，LRU 缓存。
    /// </summary>
    public static class TextureDecodeCache
    {
        private const int MaxEntries = 24;
        private static readonly LinkedList<Texture2D> Order = new LinkedList<Texture2D>();
        private static readonly Dictionary<Texture2D, Color32[]> Pixels = new Dictionary<Texture2D, Color32[]>();
        private static readonly Dictionary<Texture2D, int> Widths = new Dictionary<Texture2D, int>();
        private static readonly Dictionary<Texture2D, int> Heights = new Dictionary<Texture2D, int>();

        public static Color32[] GetPixels(Texture2D tex, out int w, out int h)
        {
            if (tex == null) { w = h = 0; return Array.Empty<Color32>(); }
            if (Pixels.TryGetValue(tex, out var cached))
            {
                Touch(tex);
                w = Widths[tex];
                h = Heights[tex];
                return cached;
            }

            w = tex.width;
            h = tex.height;
            Color32[] px;
            try
            {
                if (tex.isReadable)
                {
                    px = tex.GetPixels32();
                }
                else
                {
                    px = BlitRead(tex, w, h);
                }
            }
            catch (Exception e)
            {
                AtoLog.Warn($"Failed to decode {tex.name}: {e.Message}", tex);
                px = new Color32[Math.Max(1, w * h)];
            }

            Evict();
            Pixels[tex] = px;
            Widths[tex] = w;
            Heights[tex] = h;
            Order.AddFirst(tex);
            return px;
        }

        private static void Touch(Texture2D tex)
        {
            var node = Order.Find(tex);
            if (node != null)
            {
                Order.Remove(node);
                Order.AddFirst(node);
            }
        }

        private static void Evict()
        {
            while (Order.Count >= MaxEntries)
            {
                var last = Order.Last.Value;
                Order.RemoveLast();
                Pixels.Remove(last);
                Widths.Remove(last);
                Heights.Remove(last);
            }
        }

        public static void DisposeAll()
        {
            Pixels.Clear();
            Widths.Clear();
            Heights.Clear();
            Order.Clear();
        }

        public static Color32[] BlitRead(Texture tex, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tmp.Apply(false, false);
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

        public static string PixelHash(Texture2D tex)
        {
            var px = GetPixels(tex, out var w, out var h);
            using (var md5 = MD5.Create())
            {
                var buf = new byte[16];
                // Hash a subsample + size for speed, then full if small. / 大图抽样，小图全量。
                md5.TransformBlock(BitConverter.GetBytes(w), 0, 4, null, 0);
                md5.TransformBlock(BitConverter.GetBytes(h), 0, 4, null, 0);
                int step = px.Length > 1024 * 1024 ? 16 : 1;
                for (int i = 0; i < px.Length; i += step)
                {
                    unchecked
                    {
                        buf[0] = px[i].r; buf[1] = px[i].g; buf[2] = px[i].b; buf[3] = px[i].a;
                    }
                    md5.TransformBlock(buf, 0, 4, null, 0);
                }
                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(md5.Hash).Replace("-", "");
            }
        }
    }

    public static class GpuUtil
    {
        private static ComputeShader _qualityCs;
        private static Material _pullPushMat;

        public static ComputeShader QualityShader
        {
            get
            {
                if (_qualityCs == null)
                {
                    _qualityCs = Load<ComputeShader>("Editor/Shaders/AtoQuality.compute");
                }
                return _qualityCs;
            }
        }

        public static Material PullPushMaterial
        {
            get
            {
                if (_pullPushMat == null)
                {
                    var sh = Load<Shader>("Editor/Shaders/AtoPullPush.shader");
                    if (sh != null) _pullPushMat = new Material(sh);
                }
                return _pullPushMat;
            }
        }

        public static T Load<T>(string relative) where T : Object
        {
            var path = AtoLoc.PackageRoot + "/" + relative;
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            var name = Path.GetFileNameWithoutExtension(relative);
            var guids = AssetDatabase.FindAssets($"{name} t:{typeof(T).Name}");
            if (guids != null && guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0]));
            AtoLog.Warn($"Missing shader/compute {relative}");
            return null;
        }

        public static void ReleaseScratch()
        {
            if (_pullPushMat != null)
            {
                Object.DestroyImmediate(_pullPushMat);
                _pullPushMat = null;
            }
        }

        public static long EstVram(int w, int h, bool hasAlpha, bool mips)
        {
            long bpp = hasAlpha ? 4 : 3;
            long bytes = (long)w * h * bpp;
            if (mips) bytes = (long)(bytes * 1.333);
            return bytes;
        }
    }

    public static class MeshUvUtil
    {
        public static Vector2[] GetUv(Mesh mesh, int channel)
        {
            var list = new List<Vector2>();
            mesh.GetUVs(channel, list);
            return list.Count == 0 ? null : list.ToArray();
        }

        public static void SetUv(Mesh mesh, int channel, Vector2[] uvs)
        {
            mesh.SetUVs(channel, uvs);
        }

        public static int ChannelCount(Mesh mesh)
        {
            int n = 0;
            for (int i = 0; i < 8; i++)
            {
                var u = GetUv(mesh, i);
                if (u != null && u.Length > 0) n = i + 1;
            }
            return n;
        }
    }

    public static class PathUtil
    {
        public static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var stack = new Stack<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                stack.Push(cur.name);
                cur = cur.parent;
            }
            return string.Join("/", stack.ToArray());
        }
    }
}

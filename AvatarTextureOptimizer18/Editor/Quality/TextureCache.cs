using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    // 贴图缓存：把源贴图一次性读入 GPU（线性空间转换 + 预乘 alpha）→ 回读为半精度 CPU 池，供 Burst 指标作业读取。
    // Texture cache: loads source textures via GPU (linear conversion + alpha premultiply) → reads back into a half-float CPU pool for Burst jobs.
    // 内存纪律：所有贴图合并进一个 NativeArray<half4> 池（半精度 = 源 RGBA32 的 2 倍内存），处理完统一释放。
    // Memory discipline: all textures share one half-float pool (2x the RGBA32 source size); freed together when done.
    internal sealed class TextureCache
    {
        public struct EntryInfo
        {
            public int offset;   // 池内偏移（half4 个数）。Offset in the pool (in half4 units).
            public int width, height;
            public bool dxt5nm;  // 法线解码模式（DXT5nm swizzle 或 xyz）。Normal decode mode.
            public int usedChannels; // 灰度贴图被使用的通道位掩码（r=1,g=2,b=4）。Grayscale used-channel bitmask.
        }

        private NativeArray<half4> _pool;
        private readonly Dictionary<Analysis.TextureEntry, EntryInfo> _infos = new Dictionary<Analysis.TextureEntry, EntryInfo>();
        private int _nextOffset;
        private bool _disposed;

        public bool Has(Analysis.TextureEntry entry) { return _infos.ContainsKey(entry); }

        public EntryInfo Get(Analysis.TextureEntry entry) { return _infos[entry]; }

        public NativeArray<half4> Pool => _pool;

        // 加载一张贴图（GPU 转换 → CPU 半精度）。Loads one texture (GPU conversion → CPU half floats).
        public void Load(Analysis.TextureEntry entry, bool premultiply)
        {
            if (_infos.ContainsKey(entry)) return;
            if (_disposed) throw new System.ObjectDisposedException("TextureCache");

            var tex = entry.source;
            int w = tex.width, h = tex.height;

            // GPU：线性空间读回（sRGB 自动转换）。GPU: linear-space readback (sRGB converted automatically).
            var colors = GpuReadback(tex, w, h);

            int offset = _nextOffset;
            _nextOffset += w * h;

            // 增长池。Grow the pool.
            var newPool = new NativeArray<half4>(_nextOffset, Allocator.Persistent);
            if (_pool.IsCreated)
            {
                NativeArray<half4>.Copy(_pool, 0, newPool, 0, _pool.Length);
                _pool.Dispose();
            }
            _pool = newPool;

            // 填充：预乘 + 半精度。Fill: premultiply + half precision.
            for (int i = 0; i < w * h; i++)
            {
                var c = colors[i];
                if (premultiply)
                {
                    c.r *= c.a; c.g *= c.a; c.b *= c.a;
                }
                _pool[offset + i] = new half4((half)c.r, (half)c.g, (half)c.b, (half)c.a);
            }

            var info = new EntryInfo { offset = offset, width = w, height = h, dxt5nm = false, usedChannels = 0b111 };
            if (entry.kind == Analysis.ATOTextureKind.NormalMap)
            {
                info.dxt5nm = DetectNormalMode(entry, colors, w, h);
                info.usedChannels = 0;
                entry.dxt5nm = info.dxt5nm;
            }
            else if (entry.kind == Analysis.ATOTextureKind.Grayscale || entry.kind == Analysis.ATOTextureKind.Mask)
            {
                info.usedChannels = DetectUsedChannels(colors, w, h);
                entry.usedChannels = info.usedChannels;
            }
            _infos[entry] = info;
        }

        // GPU 读回（RenderTexture 线性 + RGBAHalf）。GPU readback (linear RenderTexture + RGBAHalf).
        private static Color[] GpuReadback(Texture2D tex, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            try
            {
                var prev = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
                try
                {
                    tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tmp.Apply(false, false);
                    return tmp.GetPixels();
                }
                finally
                {
                    Object.DestroyImmediate(tmp);
                    RenderTexture.active = prev;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // 法线解码模式检测：采样若干像素，比较两种解码后 |n|≈1 的程度。
        // Normal decode-mode detection: compare how close sampled vectors are to unit length under both decodes.
        private static bool DetectNormalMode(Analysis.TextureEntry entry, Color[] colors, int w, int h)
        {
            int step = Mathf.Max(1, (w * h) / 4096);
            double scoreXyz = 0, scoreSwizzle = 0;
            int count = 0;
            for (int i = 0; i < w * h; i += step)
            {
                var c = colors[i];
                var n1 = QualityMath.DecodeNormalByte((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(c.a * 255f), false);
                var n2 = QualityMath.DecodeNormalByte((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(c.a * 255f), true);
                scoreXyz += math.abs(math.length(n1) - 1f);
                scoreSwizzle += math.abs(math.length(n2) - 1f);
                count++;
            }
            if (count == 0) return false;
            return scoreSwizzle < scoreXyz;
        }

        // 灰度贴图被使用的通道：任何非白（≠1）像素即视为使用该通道。Grayscale used channels: any non-white pixel uses the channel.
        private static int DetectUsedChannels(Color[] colors, int w, int h)
        {
            int step = Mathf.Max(1, (w * h) / 8192);
            int mask = 0;
            for (int i = 0; i < w * h; i += step)
            {
                var c = colors[i];
                if (c.r < 0.996f) mask |= 1;
                if (c.g < 0.996f) mask |= 2;
                if (c.b < 0.996f) mask |= 4;
                if (mask == 0b111) break;
            }
            return mask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_pool.IsCreated) _pool.Dispose();
            _infos.Clear();
        }
    }
}

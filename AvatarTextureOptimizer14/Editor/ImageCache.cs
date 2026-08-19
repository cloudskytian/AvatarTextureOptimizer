// ImageCache — safe GPU readback + bounded pixel cache / 安全的 GPU 回读与有界像素缓存
// Readback pattern verified against avatar-compressor sources: Blit into a private RT (not the RT pool),
// ReadPixels, strict destroy in finally; restore RenderTexture.active & GL.sRGBWrite.<br>
// 回读方案依据 avatar-compressor 源码并验证：自建 RT(非RT池)→ReadPixels→finally 严格销毁；恢复 RT.active/GL.sRGBWrite。
// Color-space policy: we always recover the *stored* bytes (identity copy), sRGB→linear happens later in Burst.<br>
// 色彩空间策略：始终还原"存储字节"（恒等拷贝），sRGB→linear 转换之后在 Burst 内完成。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class ImageCache
    {
        private sealed class Entry
        {
            internal Texture2D tex;
            internal Color32[] raw;      // stored bytes (mip0, effective size) / 存储字节（有效尺寸）
            internal float[] linear;     // lazily built linear RGBA floats / 惰性构建的线性 RGBA
            internal int w, h;
            internal long tick;
        }

        private static readonly Dictionary<Texture2D, Entry> _map = new Dictionary<Texture2D, Entry>();
        private static long _tick;
        private const long BudgetBytes = 768L * 1024 * 1024; // stay comfortable on user machines / 控制内存占用
        private static long _usedBytes;

        /// <summary>Effective (import-clamped) dimensions of a texture. / 贴图经导入钳制后的有效尺寸。</summary>
        internal static Vector2Int EffectiveSize(Texture2D tex, TextureImporter imp)
        {
            int w = tex.width, h = tex.height;
            if (imp != null && imp.maxTextureSize > 0)
            {
                float k = Mathf.Min(1f, imp.maxTextureSize / (float)Mathf.Max(w, h));
                w = Mathf.Max(1, Mathf.RoundToInt(w * k));
                h = Mathf.Max(1, Mathf.RoundToInt(h * k));
            }
            return new Vector2Int(w, h);
        }

        /// <summary>Raw stored bytes of the effective mip0 (works for unreadable/Crunch textures). / 有效 mip0 存储字节（支持不可读/Crunch）。</summary>
        internal static Color32[] GetRaw(Texture2D tex, bool srgbStored, out int w, out int h)
        {
            var e = Get(tex, srgbStored);
            w = e.w; h = e.h;
            return e.raw;
        }

        /// <summary>Linear RGBA float view (sRGB decoded when the texture stores sRGB). / 线性 RGBA 视图（sRGB 存储时解码）。</summary>
        internal static float[] GetLinear(Texture2D tex, bool srgbStored, out int w, out int h)
        {
            var e = Get(tex, srgbStored);
            w = e.w; h = e.h;
            if (e.linear == null)
            {
                e.linear = new float[e.w * e.h * 4];
                for (int i = 0, j = 0; i < e.raw.Length; i++, j += 4)
                {
                    var p = e.raw[i];
                    if (srgbStored)
                    {
                        e.linear[j] = SrgbToLinear(p.r / 255f);
                        e.linear[j + 1] = SrgbToLinear(p.g / 255f);
                        e.linear[j + 2] = SrgbToLinear(p.b / 255f);
                    }
                    else // data textures (normal/mask) are linear already / 数据贴图本身即线性
                    {
                        e.linear[j] = p.r / 255f;
                        e.linear[j + 1] = p.g / 255f;
                        e.linear[j + 2] = p.b / 255f;
                    }
                    e.linear[j + 3] = p.a / 255f;
                }
                _usedBytes += e.linear.Length * 4;
            }
            e.tick = ++_tick;
            return e.linear;
        }

        internal static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        private static Entry Get(Texture2D tex, bool srgbStored)
        {
            if (_map.TryGetValue(tex, out var e) && e.raw != null) { e.tick = ++_tick; return e; }
            e = Readback(tex, srgbStored);
            if (e == null) return null;
            _map[tex] = e;
            _usedBytes += (long)e.w * e.h * 4;
            EvictIfNeeded();
            return e;
        }

        private static Entry Readback(Texture2D tex, bool srgbStored)
        {
            var imp = AssetDatabase.GetAssetPath(tex)?.Length > 0 ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter : null;
            var sz = EffectiveSize(tex, imp);
            int w = sz.x, h = sz.y;

            bool linearProject = PlayerSettings.colorSpace == ColorSpace.Linear;
            var rw = (linearProject && srgbStored) ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;

            var prevActive = RenderTexture.active;
            var prevSRGBWrite = GL.sRGBWrite;
            RenderTexture rt = null;
            Texture2D readable = null;
            try
            {
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, rw);
                rt.Create();
                GL.sRGBWrite = rw == RenderTextureReadWrite.sRGB; // identity copy of stored bytes / 恒等拷贝存储字节
                Graphics.Blit(tex, rt);

                RenderTexture.active = rt;
                readable = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply(false);

                return new Entry { tex = tex, raw = readable.GetPixels32(), w = w, h = h };
            }
            catch (Exception ex)
            {
                ATOLog.Warn($"readback failed '{tex?.name}': {ex.Message}");
                return null;
            }
            finally
            {
                GL.sRGBWrite = prevSRGBWrite;
                RenderTexture.active = prevActive;
                if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (readable != null) Object.DestroyImmediate(readable);
            }
        }

        private static void EvictIfNeeded()
        {
            if (_usedBytes <= BudgetBytes) return;
            // Evict least-recently-used entries until under budget / LRU 淘汰
            var ordered = new List<Entry>(_map.Values);
            ordered.Sort((a, b) => a.tick.CompareTo(b.tick));
            foreach (var e in ordered)
            {
                if (_usedBytes <= BudgetBytes) break;
                if (e.linear != null) { _usedBytes -= (long)e.linear.Length * 4; e.linear = null; }
                else if (e.raw != null)
                {
                    _usedBytes -= (long)e.w * e.h * 4;
                    _map.Remove(e.tex);
                }
            }
            ATOLog.V($"image cache evicted; used ≈ {_usedBytes / (1024 * 1024)} MB");
        }

        internal static void ReleaseAll()
        {
            _map.Clear();
            _usedBytes = 0;
        }
    }
}

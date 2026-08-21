using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace net.fosa.ato
{
    // ============================================================================
    // 灰度/蒙版贴图的被使用通道分析 / Used-channel analysis for grayscale/mask textures.
    //
    // 灰度贴图只在被使用的通道上评估线性RMSE. 被使用通道的判定:
    // Grayscale textures are evaluated on used channels only. Detection:
    //  1. 关键字分析: 检查引用该贴图的材质启用的着色器关键字, 匹配已知通道语义
    //     (如 _METALLICGLOSSMAP 表示 R=金属度 A=光滑度); 全部引用均可确认时才收窄通道.
    //     Keyword analysis: shader keywords enabled on referencing materials with known channel semantics
    //     (e.g. _METALLICGLOSSMAP: R=metallic, A=smoothness); channels are narrowed only when every
    //     reference is confidently analyzable.
    //  2. 像素兜底: 某通道在贴图像素中完全恒定时视为未使用.
    //     Pixel fallback: a channel that is constant across the pixels is considered unused.
    // ============================================================================
    internal static class ATOUsedChannels
    {
        // 关键字 -> 使用通道位掩码 / keyword -> used-channel bitmask
        private static readonly Dictionary<string, int> KnownKeywords = new Dictionary<string, int>
        {
            { "_METALLICGLOSSMAP", 0b1001 },          // R=metallic, A=smoothness
            { "_SPECGLOSSMAP", 0b1001 },              // R=specular, A=gloss
            { "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", 0b1000 },
            { "_PARALLAXMAP", 0b0010 },               // G=height (parallax)
            { "_DETAIL_MULX2", 0b1111 },              // 细节法线: RG / detail normal: RG
        };

        /// <summary>分析贴图的被使用通道(写入 tex.usedChannels) / Analyzes and writes tex.usedChannels.</summary>
        public static void Analyze(ATOBuildState state)
        {
            Profiler.BeginSample("ATO.UsedChannels");
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full) continue;
                if (tex.category != ATOTextureCategory.Grayscale && tex.category != ATOTextureCategory.Mask) continue;

                // 1. 关键字分析 / keyword analysis
                int mask = 0b1111;
                bool anyRef = false, allKnown = true;
                foreach (var r in tex.refs)
                {
                    if (r.material == null) continue;
                    anyRef = true;
                    int refMask = 0;
                    bool refKnown = false;
                    foreach (var kw in r.material.shaderKeywords)
                    {
                        if (kw == null) continue;
                        string upper = kw.ToUpperInvariant();
                        if (KnownKeywords.TryGetValue(upper, out var m))
                        {
                            refMask |= m;
                            refKnown = true;
                        }
                    }

                    if (!refKnown)
                    {
                        allKnown = false;
                        break;
                    }

                    mask &= refMask;
                }

                if (!anyRef)
                {
                    allKnown = false;
                }

                if (allKnown && mask != 0)
                {
                    tex.usedChannels = mask;
                    ATOLog.InfoVerbose($"灰度贴图被使用通道(关键字) / used channels (keywords): {tex.source.name} = {ChannelName(mask)}");
                    continue;
                }

                // 2. 像素兜底 / pixel fallback
                tex.usedChannels = DetectConstantChannels(tex);
                ATOLog.InfoVerbose($"灰度贴图被使用通道(像素) / used channels (pixels): {tex.source.name} = {ChannelName(tex.usedChannels)}");
            }

            Profiler.EndSample();
        }

        /// <summary>检测恒定为未使用的通道(抽样) / Marks constant channels as unused (sampled).</summary>
        private static int DetectConstantChannels(ATOTextureInfo tex)
        {
            var readable = ATOTextureIO.EnsureReadable(tex);
            if (readable == null) return 0b1111;

            int mask = 0;
            try
            {
                // 抽样4个角+中心 / sample 4 corners + center
                int w = readable.width, h = readable.height;
                var samples = new[]
                {
                    readable.GetPixel(0, 0),
                    readable.GetPixel(w - 1, 0),
                    readable.GetPixel(0, h - 1),
                    readable.GetPixel(w - 1, h - 1),
                    readable.GetPixel(w / 2, h / 2)
                };

                var varies = new bool[4];
                for (int ch = 0; ch < 4; ch++)
                {
                    float first = ChannelOf(samples[0], ch);
                    foreach (var s in samples)
                    {
                        if (Mathf.Abs(ChannelOf(s, ch) - first) > 0.004f)
                        {
                            varies[ch] = true;
                            break;
                        }
                    }
                }

                mask = (varies[0] ? 1 : 0) | (varies[1] ? 2 : 0) | (varies[2] ? 4 : 0) | (varies[3] ? 8 : 0);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"通道检测失败 / channel detection failed for {tex.source.name}: {e.Message}");
                return 0b1111;
            }
            finally
            {
                ATOTextureIO.ReleaseReadable(tex); // 检测完释放 / release after detection
            }

            if (mask == 0) mask = 0b1111; // 全恒定 -> 保守全通道 / all constant -> conservative
            return mask;
        }

        private static float ChannelOf(Color c, int ch)
        {
            switch (ch)
            {
                case 0: return c.r;
                case 1: return c.g;
                case 2: return c.b;
                default: return c.a;
            }
        }

        private static string ChannelName(int mask)
        {
            var s = "";
            if ((mask & 1) != 0) s += "R";
            if ((mask & 2) != 0) s += "G";
            if ((mask & 4) != 0) s += "B";
            if ((mask & 8) != 0) s += "A";
            return s.Length > 0 ? s : "?";
        }
    }
}

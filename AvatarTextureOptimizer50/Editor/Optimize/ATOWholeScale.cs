// -----------------------------------------------------------------------------
// ATOWholeScale.cs — whole-texture scaling path (no-atlas mode & fallback groups).
// ATOWholeScale.cs —— 整图缩放路径（不生成图集模式与回退组）。
//
// Treats the full texture as a single island and runs the same quality binary search.
// Density clamps use the texture's total footprint vs the group's world area.
// 将整张贴图视为单一岛运行相同的质量二分；密度钳制以整图覆盖对组世界面积。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOWholeScale
    {
        /// <summary>Scale one whole texture per quality settings; record in
        /// st.textureToOptimized. Whitelisted textures are skipped (spec).
        /// 按质量设置整图缩放并记录到 textureToOptimized；白名单贴图跳过（规格）。</summary>
        public static void Process(TexInfo t, ATOBuildState st)
        {
            if (t == null || t.whitelisted || t.source == null) return;
            if (t.wholeScaled != null || st.textureToOptimized.ContainsKey(t)) return;

            var buf = ATOQuality.GetBuffer(t, st);
            if (buf == null)
            {
                st.report.AddWarning($"Cannot read '{t.source.name}' — left untouched / 无法读取，保持原样");
                return;
            }

            // synthetic island over the whole texture / 覆盖整图的合成岛
            var fake = new IslandInfo
            {
                group = t.usages.FirstOrDefault().group,
                uvBounds = new Rect(0, 0, 1, 1),
                origSize = new Vector2Int(t.Width, t.Height),
                sampledTextures = new List<(TexInfo, TexRole)> { (t, TexRole.Main) },
            };
            // world area across all groups referencing t (max density reference)
            // 以引用 t 的所有组取最小世界面积（最保守密度）
            float world = 0f;
            foreach (var (g, _) in t.usages)
                world += g.islands.Count > 0 ? g.islands.Sum(i => i.worldArea) : 0f;
            fake.worldArea = world;

            var q = st.settings.quality;
            if (q.IsLossless)
            {
                fake.losslessCopy = true;
                fake.scaledSize = fake.origSize;
            }
            else
            {
                ATOQuality.DecideIslandScale(fake, st);
            }

            int w = fake.scaledSize.x, h = fake.scaledSize.y;
            if (w == t.Width && h == t.Height && !q.IsLossless && !fake.pureColor)
            {
                // unchanged content → still apply import params via a copy
                // 内容未变 → 仍通过副本应用导入参数
            }

            var px = ResampleWhole(t, st, w, h);
            bool srgb = t.IsSRGB;
            if (srgb) px = ATOGpu.LinearToSrgb(px);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, !srgb)
            {
                name = t.source.name + "(ATO)",
            };
            tex.SetPixels32(px);
            // whole textures keep their own wrap mode? Atlas path forces Clamp;
            // whole-scaled textures keep the ORIGINAL wrap for repeat-safety.
            // 整图缩放保留原 wrap（repeat 语义安全），图集路径才强制 Clamp。
            tex.wrapMode = t.source.wrapMode;
            tex.filterMode = t.source.filterMode;
            tex.anisoLevel = t.source.anisoLevel;
            ATOGpu.PullPushBleed(tex, st.gpu); // harmless for full coverage / 全覆盖时无副作用
            ATOTextureParams.Apply(tex, t.texClass, st, tex.name);
            st.assetSaver.SaveAsset(tex);

            t.wholeScaled = tex;
            st.textureToOptimized[t] = tex;
            st.report.optimizedPixels += (long)w * h;
            st.report.originalPixels = Math.Max(st.report.originalPixels, (long)t.Width * t.Height);
            ATOLog.Info($"whole-scale '{t.source.name}': {t.Width}x{t.Height} → {w}x{h}");
        }

        private static Color32[] ResampleWhole(TexInfo t, ATOBuildState st, int w, int h)
        {
            var buf = ATOQuality.GetBuffer(t, st);
            if (w == buf.width && h == buf.height) return (Color32[])buf.pixels.Clone();

            var src = buf.pixels;
            var dst = new Color32[w * h];
            // Reuse the Burst downsample job via a tiny wrapper loop over rows.
            // 复用 Burst 降采样 job。
            var srcHandle = new Unity.Collections.NativeArray<Color32>(src, Unity.Collections.Allocator.TempJob);
            var dstHandle = new Unity.Collections.NativeArray<Color32>(dst, Unity.Collections.Allocator.TempJob);
            try
            {
                new ATOQualityJobs.DownsampleJob
                {
                    src = srcHandle, srcW = buf.width, srcH = buf.height,
                    srcX = 0, srcY = 0, srcWd = buf.width, srcHt = buf.height,
                    dstW = w, dstH = h, premultiply = t.texClass == TexClass.AlbedoAlpha,
                    dst = dstHandle,
                }.Schedule(dstHandle.Length, 64).Complete();
                dstHandle.CopyTo(dst);
            }
            finally
            {
                srcHandle.Dispose();
                dstHandle.Dispose();
            }

            return dst;
        }
    }
}

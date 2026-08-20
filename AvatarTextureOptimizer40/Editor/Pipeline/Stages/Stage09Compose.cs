using System.Collections.Generic;
using Fosa.Ato.Editor.i18n;
using Fosa.Ato.Editor.Util;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 09: Compose atlas textures (or scaled standalone fallbacks). Blits each island's
    /// resampled region into the atlas at its pixel rect, then runs GPU pull-push dilation to bleed
    /// island colors into the padding/empty area (transparent islands keep alpha 0). Import settings:
    /// Read/Write off, Clamp forced; other settings take the strictest (highest quality) across
    /// sources. Names start with ATO_.
    /// 阶段 09：合成图集（或缩放后的独立贴图）。把每个岛的重采样区域 blit 到图集对应像素矩形，再用
    /// GPU pull-push 渗色填充 padding/空白（透明岛 alpha 保持 0）。导入设置：关闭 Read/Write、强制
    /// Clamp；其余取所有源贴图中最严格（最高质量）的。名称以 ATO_ 开头。
    /// </summary>
    internal sealed class Stage09Compose : IStage
    {
        public string Name => "ATO/09 Composing atlases";
        public float Weight => 5f;

        private Material _pullPushMat;

        public void Run(AtoPipeline p)
        {
            // First compose atlases that were planned in Stage08.
            // 先合成阶段08规划好的图集
            foreach (var atlas in p.Atlases)
            {
                p.Progress.ThrowIfCancelled();
                if (atlas.Texture != null) continue;
                if (!atlas.FallbackStandalone) ComposeAtlas(p, atlas);
            }

            // Then produce scaled standalone textures for UV groups not yet placed in any atlas.
            // 然后为还未装入图集的 UV 组生成缩放后的独立贴图
            var placed = new HashSet<Island>();
            foreach (var a in p.Atlases)
                foreach (var pl in a.Placements)
                    placed.Add(pl.Island);

            foreach (var g in p.UvGroups)
            {
                p.Progress.ThrowIfCancelled();
                if (g.Islands.TrueForAll(i => placed.Contains(i))) continue;
                ComposeStandalone(p, g, placed);
            }

            p.RegisterCleanup(() => { if (_pullPushMat != null) Object.DestroyImmediate(_pullPushMat); });
        }

        private void ComposeAtlas(AtoPipeline p, AtlasResult atlas)
        {
            var kind = atlas.Kind;
            bool linear = kind != TextureKind.Color && kind != TextureKind.Emission;
            // Always compose in uncompressed RGBA32; Stage12 reimport compresses to final format.
            // 始终以未压缩 RGBA32 合成，阶段12 reimport 时压缩为最终格式
            var tex = new Texture2D(atlas.Width, atlas.Height, TextureFormat.RGBA32, true, linear)
            {
                name = atlas.Name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            long srcBytes = 0;
            var done = new HashSet<Island>();

            using (var atlasRt = new RenderTexture(atlas.Width, atlas.Height, 0,
                       RenderTextureFormat.ARGB32,
                       linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
                   { wrapMode = TextureWrapMode.Clamp, useMipMap = false })
            {
                RenderTexture.active = atlasRt;
                GL.Clear(false, true, Color.clear);
                var prev = RenderTexture.active;

                foreach (var pl in atlas.Placements)
                {
                    if (done.Contains(pl.Island)) continue;
                    done.Add(pl.Island);
                    var isl = pl.Island;
                    var src = isl.SourceTexture;
                    if (src == null) continue;
                    srcBytes += TextureIO.EstimateBytes(src.width, src.height, src.format, src.mipmapCount > 1);
                    int tw = Mathf.Max(1, Mathf.RoundToInt(isl.TargetSizePx.x));
                    int th = Mathf.Max(1, Mathf.RoundToInt(isl.TargetSizePx.y));
                    BlitRegionIntoAtlas(src, isl, atlasRt, pl.PixelRect, tw, th, pl.Rotated, linear);
                }

                // Read back composed atlas / 读回合成后的图集
                RenderTexture.active = atlasRt;
                tex.ReadPixels(new Rect(0, 0, atlas.Width, atlas.Height), 0, 0);
                tex.Apply(true, false);
                RenderTexture.active = prev;
            }

            // GPU pull-push bleed dilation / GPU 渗色
            tex = DilationBleed(tex, atlas.Kind);

            p.Ctx.AssetSaver.SaveAsset(tex);
            tex.wrapMode = TextureWrapMode.Clamp;

            atlas.Texture = tex;
            atlas.SourceBytes = srcBytes;
            atlas.OutputBytes = TextureIO.EstimateBytes(atlas.Width, atlas.Height, TextureFormat.RGBA32, true);
        }

        private void ComposeStandalone(AtoPipeline p, UvGroup g, HashSet<Island> placed)
        {
            foreach (var isl in g.Islands)
            {
                p.Progress.ThrowIfCancelled();
                if (placed.Contains(isl)) continue;
                var src = isl.SourceTexture;
                if (src == null) continue;
                bool linear = isl.SourceUsage.Kind != TextureKind.Color && isl.SourceUsage.Kind != TextureKind.Emission;
                int tw = Mathf.Max(1, Mathf.RoundToInt(isl.TargetSizePx.x));
                int th = Mathf.Max(1, Mathf.RoundToInt(isl.TargetSizePx.y));
                string name = $"ATO_standalone_{src.name}_{isl.Id}";

                var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32,
                    linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                var tex = new Texture2D(tw, th, TextureFormat.RGBA32, src.mipmapCount > 1, linear)
                {
                    name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                };
                try
                {
                    // Normalized source sub-rect (0..1 in source UV space). / 归一化源子矩形
                    var srcRect = new Rect(isl.UvBox.xMin, isl.UvBox.yMin, isl.UvBox.width, isl.UvBox.height);
                    Graphics.Blit(src, rt, srcRect, new Rect(0, 0, 1, 1));
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                    tex.Apply(true, false);
                    RenderTexture.active = prev;
                }
                finally { RenderTexture.ReleaseTemporary(rt); }

                p.Ctx.AssetSaver.SaveAsset(tex);
                tex.wrapMode = TextureWrapMode.Clamp;

                var result = new AtlasResult
                {
                    Name = tex.name, Width = tw, Height = th, Texture = tex, Group = null,
                    Kind = isl.SourceUsage.Kind, Utilization = 1f, FallbackStandalone = true,
                    SourceBytes = TextureIO.EstimateBytes(src.width, src.height, src.format, src.mipmapCount > 1),
                    OutputBytes = TextureIO.EstimateBytes(tw, th, TextureFormat.RGBA32, tex.mipmapCount > 1),
                };
                // A placement with a 0..1 rect lets Stage10 remap UVs into the new standalone.
                // 添加 0..1 的放置，供 Stage10 重映射 UV。
                result.Placements.Add(new PlacedIsland
                {
                    Island = isl, Group = g,
                    PixelRect = new RectInt(0, 0, tw, th), Rotated = false,
                });
                p.Atlases.Add(result);
                placed.Add(isl);
                isl.TargetSizePx = new Vector2(tw, th);
            }
        }

        /// <summary>
        /// Blit an island's source sub-region into an atlas RenderTexture at the target placement.
        /// Uses a temporary RT of exactly the island target size to get GPU bilinear resampling,
        /// then a second blit (with an offset/scale material or CopyTexture) into the atlas.
        /// 将岛的源子区域 blit 到目标位置。先在临时 RT 上以目标尺寸得到 GPU 双线性重采样，再复制进图集。
        /// </summary>
        private void BlitRegionIntoAtlas(Texture2D src, Island isl, RenderTexture atlasRt,
            RectInt dstRect, int tw, int th, bool rotated, bool linear)
        {
            if (dstRect.width <= 0 || dstRect.height <= 0) return;
            if (rotated) (tw, th) = (th, tw);

            var regionRt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            try
            {
                // Resample the island's source sub-rect to exactly (tw,th) on the GPU (bilinear).
                // 在 GPU 上把岛的源子矩形精确重采样到 (tw,th)（双线性）
                var srcRect = new Rect(isl.UvBox.xMin, isl.UvBox.yMin, isl.UvBox.width, isl.UvBox.height);
                Graphics.Blit(src, regionRt, srcRect, new Rect(0, 0, 1, 1));

                // Paste resampled region into the atlas at the desired pixel rectangle using our
                // vertex-transform shader. / 用顶点变换 shader 把重采样区域贴到图集目标像素矩形
                var mat = GetBlitIntoRectMaterial();
                if (mat == null)
                {
                    // Fallback: Graphics.Blit with dest scale/offset (0..1). / 回退：按 0..1 缩放偏移
                    float sx = (float)dstRect.width / atlasRt.width;
                    float sy = (float)dstRect.height / atlasRt.height;
                    float ox = (float)dstRect.xMin / atlasRt.width;
                    float oy = (float)dstRect.yMin / atlasRt.height;
                    Graphics.Blit(regionRt, atlasRt, new Vector2(sx, sy), new Vector2(ox, oy));
                    return;
                }
                mat.SetTexture("_MainTex", regionRt);
                mat.SetVector("_DstRect", new Vector4(dstRect.xMin, dstRect.yMin, dstRect.xMax, dstRect.yMax));
                mat.SetInt("_Rotated", rotated ? 1 : 0);
                var prev = RenderTexture.active;
                RenderTexture.active = atlasRt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, atlasRt.width, 0, atlasRt.height);
                mat.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0, 0); GL.Vertex3(dstRect.xMin, dstRect.yMin, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(dstRect.xMax, dstRect.yMin, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(dstRect.xMax, dstRect.yMax, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(dstRect.xMin, dstRect.yMax, 0);
                GL.End();
                GL.PopMatrix();
                RenderTexture.active = prev;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(regionRt);
            }
        }

        private Material _blitIntoRectMat;
        private Material GetBlitIntoRectMaterial()
        {
            if (_blitIntoRectMat != null) return _blitIntoRectMat;
            var shader = Shader.Find("Hidden/Fosa/ATO/BlitIntoRect");
            // Fallback to hidden internal blit if custom shader not found.
            // 找不到自定义 shader 时回退
            _blitIntoRectMat = shader != null ? new Material(shader) : null;
            return _blitIntoRectMat;
        }

        private Texture2D DilationBleed(Texture2D tex, TextureKind kind)
        {
            if (_pullPushMat == null)
            {
                var shader = Shader.Find("Hidden/Fosa/ATO/PullPush");
                if (shader == null) return tex; // missing shader -> keep raw composition
                _pullPushMat = new Material(shader);
            }
            var rtA = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            var rtB = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            bool linear = kind != TextureKind.Color && kind != TextureKind.Emission;
            try
            {
                Graphics.Blit(tex, rtA);
                for (int i = 0; i < 6; i++)
                {
                    Graphics.Blit(rtA, rtB, _pullPushMat, 0);
                    Graphics.Blit(rtB, rtA, _pullPushMat, 1);
                }
                var prev = RenderTexture.active; RenderTexture.active = rtA;
                var dst = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, true, linear)
                { wrapMode = TextureWrapMode.Clamp };
                dst.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                dst.Apply(true, false);
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
                return dst;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rtA);
                RenderTexture.ReleaseTemporary(rtB);
            }
        }
    }
}

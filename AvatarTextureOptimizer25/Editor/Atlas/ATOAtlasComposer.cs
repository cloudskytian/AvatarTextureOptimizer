// Avatar Texture Optimizer / 头像贴图优化器
// Renders each atlas plan into per-role layer textures (GPU), tracks per-layer
// coverage masks, performs pull-push hole filling (island-edge color spread
// with alpha preserved at 0 outside coverage) and encodes PNG bytes.
// 将每张图集规划渲染为按角色分层的贴图（GPU），跟踪逐层覆盖掩码，执行
// pull-push 空洞填充（岛边缘颜色外扩、覆盖区外 alpha 保持 0），并编码 PNG。
//
// Layers of the same plan share ONE UV layout (placements); role layers whose
// quality demand is lower than the main layer are rendered at a proportionally
// reduced resolution (same layout, fewer bytes).
// 同一规划内的各层共享同一 UV 布局；质量需求低于主色的角色层按比例降分辨率
// 渲染（布局不变，体积更小）。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>One generated layer (bytes + metadata). / 一张生成层（字节+元数据）。</summary>
    public sealed class ATOGeneratedLayer
    {
        public ATORole role;
        public int width, height;
        public byte[] pngBytes;
        public bool sRGB;
        public bool hasAlpha;
        public bool isNormal;
        /// <summary>
        /// True when content is single-channel (g,b within ±2 of r everywhere),
        /// i.e. safe for R8/R16-style grayscale formats. Multi-channel mask
        /// content must NOT be classified Grayscale (spec: safe fallback).
        /// 内容为单通道（各像素 g、b 与 r 差 ≤2）时为 true，可安全用
        /// R8/R16 类灰度格式。多通道蒙版内容禁止分类为灰度（需求：安全兜底）。
        /// </summary>
        public bool isEffectivelyGray = true;
    }

    /// <summary>Output for one atlas plan (= one atlas set with role layers). / 一张图集规划的输出（含多个角色层）。</summary>
    public sealed class ATOAtlasSetResult
    {
        public ATOAtlasPlan plan;
        public readonly Dictionary<ATORole, ATOGeneratedLayer> layers = new Dictionary<ATORole, ATOGeneratedLayer>();
    }

    /// <summary>
    /// Composes atlases on the GPU from island source sessions.
    /// 基于岛源会话在 GPU 上合成图集。
    /// </summary>
    public sealed class ATOAtlasComposer
    {
        private readonly ATOGpuPipeline _gpu;
        private readonly AvatarTextureOptimizer _settings;
        private Texture2D _white;

        public ATOAtlasComposer(ATOGpuPipeline gpu, AvatarTextureOptimizer settings)
        {
            _gpu = gpu;
            _settings = settings;
        }

        private Texture2D WhiteTex()
        {
            if (_white == null)
            {
                _white = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
                var px = new Color32[16];
                for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
                _white.SetPixels32(px);
                _white.Apply(false, false);
            }
            return _white;
        }

        /// <summary>
        /// Compose all layers of one plan. Sessions are opened lazily per texture
        /// and closed at the end of the call.
        /// 合成一张规划的全部角色层。按贴图惰性打开会话并在结束时关闭。
        /// </summary>
        public ATOAtlasSetResult Compose(
            ATOAtlasPlan plan,
            Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> qualityRatios,
            ATOProgress progress)
        {
            using (new ATOLog.Step($"compose:{plan.typeGroupKey}#{plan.setIndex}"))
            {
                var result = new ATOAtlasSetResult { plan = plan };

                // ---- determine roles + layer scales / 确定角色与层缩放 ----
                var roleTextures = new Dictionary<ATORole, HashSet<ATOTextureEntry>>();
                foreach (var p in plan.islands)
                {
                    foreach (var u in p.unit.group.OptimizableTextures())
                    {
                        var role = RoleOfTexture(p.unit.group, u);
                        if (!roleTextures.TryGetValue(role, out var set))
                        {
                            set = new HashSet<ATOTextureEntry>();
                            roleTextures[role] = set;
                        }
                        set.Add(u);
                    }
                }

                float mainDemand = 0f;
                var demandByRole = new Dictionary<ATORole, float>();
                foreach (var kv in roleTextures)
                {
                    float demand = MaxRatioOfTextures(kv.Value, plan, qualityRatios);
                    demandByRole[kv.Key] = demand;
                    if (kv.Key == ATORole.Main || kv.Key == ATORole.MainLayer || kv.Key == ATORole.Emission)
                        mainDemand = Mathf.Max(mainDemand, demand);
                }
                float overallMain = Mathf.Max(mainDemand, 0.0625f);

                foreach (var kv in roleTextures)
                {
                    var role = kv.Key;
                    float scale = 1f;
                    float demand = demandByRole[role];
                    // Layers with strictly lower demand than main may be reduced.
                    // 需求严格低于主色的层可缩减。
                    if (role != ATORole.Main && role != ATORole.MainLayer && overallMain > 0f)
                    {
                        scale = Mathf.Clamp01(demand / overallMain);
                        // keep the smallest island above min padding / 保证最小岛不低于最小 padding
                        scale = Mathf.Max(scale, MinScaleForPadding(plan, role));
                        scale = Mathf.Clamp(scale, 0.0625f, 1f);
                    }
                    int lw = Mathf.Max(4, Mathf.RoundToInt(plan.width * scale));
                    int lh = Mathf.Max(4, Mathf.RoundToInt(plan.height * scale));
                    var layer = RenderLayer(plan, role, kv.Value, qualityRatios, lw, lh, progress);
                    result.layers[role] = layer;
                }
                return result;
            }
        }

        private ATORole RoleOfTexture(ATOUVGroup group, ATOTextureEntry tex)
        {
            foreach (var u in group.usages)
                if (u.texture == tex && u.Optimizable) return u.role;
            return ATORole.Main;
        }

        private float MaxRatioOfTextures(
            HashSet<ATOTextureEntry> textures, ATOAtlasPlan plan,
            Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> ratios)
        {
            float max = 0f;
            foreach (var p in plan.islands)
            {
                var g = p.unit.group;
                bool usedHere = false;
                foreach (var t in textures) if (g.OptimizableTextures().Contains(t)) usedHere = true;
                if (!usedHere) continue;
                if (ratios != null && ratios.TryGetValue(g, out var rmap) && rmap.TryGetValue(p.island, out var r))
                {
                    max = Mathf.Max(max, Mathf.Max(r.x, r.y));
                }
            }
            return Mathf.Max(max, 1f / 16f);
        }

        private float MinScaleForPadding(ATOAtlasPlan plan, ATORole role)
        {
            int pad = Mathf.Max(4, Mathf.CeilToInt(Mathf.Max(plan.width, plan.height) / 128f));
            int minIsland = int.MaxValue;
            foreach (var p in plan.islands)
            {
                minIsland = Mathf.Min(minIsland, Mathf.Min(p.w, p.h));
            }
            if (minIsland == int.MaxValue) return 0.0625f;
            return Mathf.Clamp01((float)(pad * 2 + 2) / Mathf.Max(1, minIsland));
        }

        private ATOGeneratedLayer RenderLayer(
            ATOAtlasPlan plan, ATORole role, HashSet<ATOTextureEntry> textures,
            Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> qualityRatios,
            int layerW, int layerH, ATOProgress progress)
        {
            bool isNormal = role == ATORole.Normal;
            bool srgbLayer = role == ATORole.Main || role == ATORole.MainLayer || role == ATORole.Emission;

            var layerRT = NewFloatRT(layerW, layerH);
            var coverageRT = NewMaskRT(layerW, layerH);
            try
            {
                ClearRT(layerRT, new Color(0, 0, 0, 0));
                ClearRT(coverageRT, Color.black);
                // layerRT: content rgb (premultiplied for sRGB, straight otherwise)
                // + the texture's OWN alpha. coverageRT: binary island coverage used
                // only as the hole mask for pull-push.
                // layerRT：内容 rgb（sRGB 走预乘，其余直通）+ 贴图自身 alpha。
                // coverageRT：二值岛覆盖，仅作 pull-push 的空洞掩码。

                // Track which islands each group has (they share placement per plan).
                // 跟踪各组的岛（其在规划内共享摆放）。
                float scaleX = (float)layerW / plan.width;
                float scaleY = (float)layerH / plan.height;

                foreach (var unitGroup in GroupIslandsByUnit(plan))
                {
                    var unit = unitGroup.Key;
                    foreach (var tex in unit.group.OptimizableTextures())
                    {
                        if (!textures.Contains(tex)) continue;
                        progress.ThrowIfCancelled();
                        ATOTextureSession session = null;
                        try
                        {
                            session = _gpu.OpenSession(tex, isNormal);
                            foreach (var p in unitGroup.Value)
                            {
                                var dstRect = new RectInt(
                                    Mathf.RoundToInt(p.x * scaleX),
                                    Mathf.RoundToInt(p.y * scaleY),
                                    Mathf.Max(1, Mathf.RoundToInt((p.rotated90 ? p.h : p.w) * scaleX)),
                                    Mathf.Max(1, Mathf.RoundToInt((p.rotated90 ? p.w : p.h) * scaleY)));
                                DrawIsland(session, p, dstRect, layerRT, coverageRT, isNormal);
                            }
                        }
                        finally
                        {
                            session?.Dispose();
                        }
                    }
                }

                // ---- pull-push fill + final encode ----
                // ---- pull-push 填充 + 最终编码 ----
                var filled = PullPushFill(layerRT, coverageRT, layerW, layerH);
                // The texture's own alpha must be extrapolated with the SAME
                // coverage geometry; unpremultiplying by the packed coverage alpha
                // would double-darken semi-transparent pixels (QA-1 finding).
                // 贴图自身的 alpha 必须以同一覆盖几何外推；若用打包覆盖 alpha 做
                // 反预乘，半透明像素会被二次变暗（QA-1 发现）。
                RenderTexture alphaSrc = null;
                RenderTexture filledAlpha = null;
                ATOGeneratedLayer layer;
                try
                {
                    if (!isNormal)
                    {
                        alphaSrc = _gpu.RunPass(layerRT, ATOGpuPipeline.PassAlphaToRgb, layerW, layerH);
                        filledAlpha = PullPushFill(alphaSrc, coverageRT, layerW, layerH);
                    }
                    layer = EncodeLayer(filled, filledAlpha, role, srgbLayer, isNormal, layerW, layerH);
                }
                finally
                {
                    _gpu.Pool.Return(filled);
                    if (alphaSrc != null) _gpu.Pool.Return(alphaSrc);
                    if (filledAlpha != null) _gpu.Pool.Return(filledAlpha);
                }
                return layer;
            }
            finally
            {
                _gpu.Pool.Return(layerRT);
                _gpu.Pool.Return(coverageRT);
            }
        }

        private Dictionary<ATOPackUnit, List<ATOPlacedIsland>> GroupIslandsByUnit(ATOAtlasPlan plan)
        {
            var dict = new Dictionary<ATOPackUnit, List<ATOPlacedIsland>>();
            foreach (var p in plan.islands)
            {
                if (!dict.TryGetValue(p.unit, out var list))
                {
                    list = new List<ATOPlacedIsland>();
                    dict[p.unit] = list;
                }
                list.Add(p);
            }
            return dict;
        }

        private void DrawIsland(
            ATOTextureSession session, ATOPlacedIsland p, RectInt dstRect,
            RenderTexture layerRT, RenderTexture coverageRT, bool isNormal)
        {
            var tex = session.entry.texture;
            var crop = new RectInt(
                Mathf.Clamp(Mathf.FloorToInt(p.island.uvMin.x * tex.width), 0, tex.width - 1),
                Mathf.Clamp(Mathf.FloorToInt(p.island.uvMin.y * tex.height), 0, tex.height - 1),
                0, 0);
            crop.width = Mathf.Clamp(
                Mathf.CeilToInt(p.island.uvMax.x * tex.width) - crop.x, 1, tex.width - crop.x);
            crop.height = Mathf.Clamp(
                Mathf.CeilToInt(p.island.uvMax.y * tex.height) - crop.y, 1, tex.height - crop.y);

            // scaled content at pre-rotation dst orientation / 预旋转方向的目标尺寸内容
            int contentW = p.rotated90 ? dstRect.height : dstRect.width;
            int contentH = p.rotated90 ? dstRect.width : dstRect.height;

            var chain = _gpu.DownsampleCrop(session.fullLinearFloat, crop, contentW, contentH);
            RenderTexture content = chain[chain.Count - 1];
            RenderTexture finalContent = content;
            RenderTexture rotated = null;
            RenderTexture renorm = null;
            try
            {
                if (p.rotated90)
                {
                    rotated = _gpu.Pool.Rent(contentH, contentW, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    var mat = _gpuPass8Material();
                    mat.SetVector("_SrcPixelSize", new Vector4(contentW, contentH, 0, 0));
                    mat.SetVector("_DstPixelSize", new Vector4(contentH, contentW, 0, 0));
                    _gpuBlitWithMaterial(content, rotated, mat, ATOGpuPipeline.PassRotate90CW);
                    finalContent = rotated;
                }
                if (isNormal)
                {
                    renorm = _gpu.RunPass(finalContent, ATOGpuPipeline.PassRenormalize, finalContent.width, finalContent.height);
                    finalContent = renorm;
                }
                ViewportBlit(finalContent, layerRT, dstRect);
                ViewportBlit(WhiteTex(), coverageRT, dstRect);
            }
            finally
            {
                foreach (var rt in chain) _gpu.Pool.Return(rt);
                if (rotated != null) _gpu.Pool.Return(rotated);
                if (renorm != null) _gpu.Pool.Return(renorm);
            }
        }

        private Material _gpuPass8Material() => _gpu.SharedMaterial;

        private void _gpuBlitWithMaterial(RenderTexture src, RenderTexture dst, Material mat, int pass)
        {
            Graphics.Blit(src, dst, mat, pass);
        }

        /// <summary>Blit src into a sub-rect of dst using GL viewport. / 用 GL 视口把 src 画进 dst 的子矩形。</summary>
        private static void ViewportBlit(RenderTexture src, RenderTexture dst, RectInt rect)
        {
            src.filterMode = FilterMode.Point;
            GL.PushMatrix();
            try
            {
                GL.Viewport(new Rect(rect.x, rect.y, rect.width, rect.height));
                Graphics.Blit(src, dst);
            }
            finally
            {
                GL.Viewport(new Rect(0, 0, dst.width, dst.height));
                GL.PopMatrix();
            }
        }

        private RenderTexture NewFloatRT(int w, int h)
        {
            return _gpu.Pool.Rent(w, h, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        }

        private RenderTexture NewMaskRT(int w, int h)
        {
            return _gpu.Pool.Rent(w, h, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
        }

        private static void ClearRT(RenderTexture rt, Color c)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, c);
            RenderTexture.active = prev;
        }

        /// <summary>
        /// Pull-push fill: build a mip-like chain of (color, coverage in alpha?),
        /// fill DOWN until all pixels covered, then PUSH back up compositing fill
        /// beneath original color. Coverage outside keeps alpha=0 of the color.
        /// pull-push 填充：构建 (颜色, 覆盖) 的逐级链，向下填到全覆盖，再向上回推，
        /// 在原色下方合成填充色。覆盖区外保持原色 alpha=0。
        /// </summary>
        private RenderTexture PullPushFill(RenderTexture color, RenderTexture coverage, int w, int h)
        {
            // Pack rgb + coverage-as-alpha into one RT for the fill pipeline.
            // 将 rgb + 覆盖（作 alpha）打包到一个 RT 进入填充管线。
            var pack = _gpu.Pool.Rent(w, h, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _gpu.CombineColorCoverageInto(pack, color, coverage);

            var pyramid = new List<RenderTexture> { pack };
            int cw = w, ch = h;
            RenderTexture cur = pack;
            // PULL chain / 下拉链
            while (cw > 2 && ch > 2)
            {
                cw = Mathf.Max(1, cw / 2);
                ch = Mathf.Max(1, ch / 2);
                var next = _gpu.Pool.Rent(cw, ch, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Graphics.Blit(cur, next);
                // a couple of dilate dilations per level / 每级少量膨胀
                _gpu.RunPassSelf(ref next, ATOGpuPipeline.PassDilate, cw, ch);
                pyramid.Add(next);
                cur = next;
            }
            // Coarsest: dilate until full coverage / 最粗层：膨胀到全覆盖
            for (int i = 0; i < 32; i++)
            {
                _gpu.RunPassSelf(ref cur, ATOGpuPipeline.PassDilate, cw, ch);
            }

            // PUSH back up / 回推
            for (int level = pyramid.Count - 2; level >= 0; level--)
            {
                var target = pyramid[level];
                int tw = target.width, th = target.height;
                var up = _gpu.Pool.Rent(tw, th, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Graphics.Blit(cur, up);
                _gpu.RunPassSelf(ref up, ATOGpuPipeline.PassDilate, tw, th);
                _gpu.RunPassSelf(ref up, ATOGpuPipeline.PassDilate, tw, th);
                var combined = _gpu.Pool.Rent(tw, th, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                _gpu.CombineFillInto(combined, target, up);
                _gpu.Pool.Return(up);
                cur = combined;
                if (level != 0) _gpu.Pool.Return(target);
            }

            // cur now = full-res filled pack (rgb + coverage-as-alpha)
            // cur 现为全尺寸填充包（rgb + 覆盖作 alpha）
            var finalOut = _gpu.Pool.Rent(w, h, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _gpu.CombineFillInto(finalOut, pack, cur);
            _gpu.Pool.Return(pack);
            _gpu.Pool.Return(cur);
            return finalOut; // rgb=fill-where-empty, a = coverage (unused later) / alpha 为覆盖（后续不用）
        }

        private ATOGeneratedLayer EncodeLayer(
            RenderTexture filled, RenderTexture filledAlpha, ATORole role, bool srgbLayer, bool isNormal,
            int w, int h)
        {
            var layer = new ATOGeneratedLayer { role = role, width = w, height = h, sRGB = srgbLayer, isNormal = isNormal };

            RenderTexture bytesRT;
            if (isNormal)
            {
                // Re-normalize: pull-push spreads un-normalized vectors into the
                // padding, and mip sampling near island edges would pick them up.
                // 重归一化：pull-push 会把未归一向量扩散进 padding，岛边缘的 mip
                // 采样会采到它们。
                var normalized = _gpu.RunPass(filled, ATOGpuPipeline.PassRenormalize, w, h);
                // Vectors -> classic [0,1] RGB storage. / 向量 -> 经典 [0,1] RGB 存储。
                var enc = _gpu.Pool.Rent(w, h, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                _gpu.EncodeNormalToBytes(enc, normalized);
                _gpu.Pool.Return(normalized);
                bytesRT = _gpu.Pool.Rent(w, h, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(enc, bytesRT);
                _gpu.Pool.Return(enc);
            }
            else if (srgbLayer)
            {
                // Straight-alpha PNG from premultiplied fill + extrapolated alpha.
                // 预乘填充 + 外推 alpha -> 直通 alpha PNG。
                bytesRT = _gpu.RunPassWithSecond(filled, filledAlpha,
                    ATOGpuPipeline.PassResolveUnpremultiplySRGB, w, h);
            }
            else
            {
                bytesRT = _gpu.RunPassWithSecond(filled, filledAlpha,
                    ATOGpuPipeline.PassResolveLinearAlpha, w, h);
            }

            var pixels = _gpu.ReadbackRegion32(bytesRT, new RectInt(0, 0, w, h));
            _gpu.Pool.Return(bytesRT);

            var readable = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            try
            {
                readable.SetPixels32(pixels, 0);
                readable.Apply(false, false);
                bool hasAlpha = false;
                bool isGray = true;
                foreach (var p in pixels)
                {
                    if (p.a < 250) hasAlpha = true;
                    if (isGray && (Mathf.Abs(p.g - p.r) > 2 || Mathf.Abs(p.b - p.r) > 2)) isGray = false;
                    if (hasAlpha && !isGray) break;
                }
                layer.hasAlpha = hasAlpha;
                layer.isEffectivelyGray = isGray;
                layer.pngBytes = ImageConversion.EncodeToPNG(readable);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(readable);
            }
            return layer;
        }
    }
}

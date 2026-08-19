// Stage 7: atlas baking (island pixel composition, rotation, role-downscaled atlases,
// GPU pull-push dilation) and the non-atlas whole-texture scaling path.
// 阶段7：图集烘焙（岛像素合成、旋转、类型图集整体缩放、GPU pull-push 外扩）与整图缩放路径。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class BakeStage
    {
        public static void Run(AtoContext ctx)
        {
            using (AtoLog.Time("BakeStage", (l, ms) => ctx.Stats.StageTimes.Add((l, ms))))
            {
                AtoProgress.BeginStage(AtoL10n.Tr("stage.bake"));
                BakeAtlases(ctx);
                BakeWholeScaled(ctx);
            }
        }

        // Shared mapping used by BakeStage and RewriteStage: island-local px -> atlas px.
        // 烘焙与重写共用的坐标变换：岛内局部像素 → 图集像素。
        public static Vector2 IslandToAtlasPx(Island isl, Vector2 scaledLocal)
        {
            Vector2 d = isl.Rotated
                ? new Vector2(scaledLocal.y, isl.RasterSize.x - scaledLocal.x)
                : scaledLocal;
            return new Vector2(isl.PlacePos.x, isl.PlacePos.y) + d;
        }

        private static void BakeAtlases(AtoContext ctx)
        {
            if (!ctx.Settings.generateAtlas) return;

            // physical atlas assignment: textures sharing mappings must live on separate layers.
            // 物理图集分配：共享映射（动画切换变体）的贴图需分层。
            var atlasGroups = ctx.Textures.Values
                .Where(t => !t.Whitelisted && t.AtlasIndex >= 0)
                .GroupBy(t => t.AtlasIndex);

            int atlasCounter = 0;
            foreach (var group in atlasGroups)
            {
                var byRole = group.GroupBy(t => (t.Role, t.SRGB && t.Role == TexRole.Color));
                foreach (var roleGroup in byRole)
                {
                    var layers = AssignLayers(roleGroup.ToList());
                    foreach (var layer in layers)
                    {
                        AtoProgress.Step(0.1f + 0.8f * atlasCounter / 16f % 0.8f, $"atlas {atlasCounter}");
                        BakePhysicalAtlas(ctx, group.Key, roleGroup.Key.Item1, layer, atlasCounter++);
                    }
                }
            }
        }

        /// <summary>Textures sharing a mapping conflict -> separate layers. / 共享映射即冲突 → 分层。</summary>
        private static List<List<TexInfo>> AssignLayers(List<TexInfo> textures)
        {
            var layers = new List<List<TexInfo>>();
            foreach (var t in textures)
            {
                var layer = layers.FirstOrDefault(l =>
                    !l.Any(o => o.Mappings.Overlaps(t.Mappings)));
                if (layer == null) { layer = new List<TexInfo>(); layers.Add(layer); }
                layer.Add(t);
            }
            return layers;
        }

        private static void BakePhysicalAtlas(AtoContext ctx, int layoutIndex, TexRole role,
            List<TexInfo> textures, int atlasCounter)
        {
            var unit = ctx.PackUnits.FirstOrDefault(u => u.Textures.Any(t => t.AtlasIndex == layoutIndex));
            var size = unit?.AtlasSize ?? new Vector2Int(ctx.MaxAtlasSize, ctx.MaxAtlasSize);
            if (size.x <= 0 || size.y <= 0) size = new Vector2Int(ctx.MaxAtlasSize, ctx.MaxAtlasSize);

            // role atlas downscale when quality demand is uniformly lower / 类型需求整体更低时整体缩放
            float roleScale = RoleScale(ctx, textures, layoutIndex);
            var texSize = new Vector2Int(
                SnapSize(Mathf.CeilToInt(size.x * roleScale), ctx),
                SnapSize(Mathf.CeilToInt(size.y * roleScale), ctx));
            float fx = texSize.x / (float)size.x, fy = texSize.y / (float)size.y;

            bool srgb = role == TexRole.Color && textures.Any(t => t.SRGB);
            var rt = RenderTexture.GetTemporary(texSize.x, texSize.y, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0, 0, 0, 0));

            long usedPixels = 0;
            foreach (var ti in textures)
            {
                foreach (var key in ti.Mappings)
                {
                    if (!ctx.Islands.TryGetValue(key, out var islands)) continue;
                    ulong texMask = ti.SubmeshMask.TryGetValue(key, out var m) ? m : ulong.MaxValue;
                    foreach (var isl in islands)
                    {
                        if (isl.PlacedAtlas != layoutIndex) continue;
                        if ((isl.SubmeshMask & texMask) == 0) continue; // island not used by this texture
                        DrawIsland(ctx, rt, ti, isl, fx, fy);
                        usedPixels += (long)(isl.RasterSize.x * fx) * (long)(isl.RasterSize.y * fy);
                    }
                }
            }

            RenderTexture.active = prev;

            // pull-push dilation / 外扩填充
            var filled = PullPush.Fill(rt);
            RenderTexture.ReleaseTemporary(rt);

            var tex = ReadbackAtlas(filled, texSize, srgb, role);
            RenderTexture.ReleaseTemporary(filled);
            tex.name = $"ATO_{atlasCounter}_{role}";
            tex.wrapMode = TextureWrapMode.Clamp; // forced / 强制Clamp
            tex.anisoLevel = textures.Max(t => t.Tex.anisoLevel);
            tex.filterMode = textures.Select(t => t.Filter).OrderByDescending(f => (int)f).First();

            var result = new AtlasResult
            {
                Name = tex.name, Role = role, SRGB = srgb,
                HasAlpha = textures.Any(t => t.HasAlphaContent),
                Width = texSize.x, Height = texSize.y, Texture = tex, UsedPixels = usedPixels
            };
            result.Sources.AddRange(textures);
            ctx.Atlases.Add(result);
            ctx.Stats.Atlases.Add(result);
            foreach (var ti in textures)
            {
                ti.Output = tex;
                ctx.Stats.TexturesAtlased++;
            }
            AtoLog.Info($"atlas '{tex.name}' {texSize.x}x{texSize.y} " +
                        $"({textures.Count} sources, util {result.Utilization:P1})");
        }

        private static float RoleScale(AtoContext ctx, List<TexInfo> textures, int layoutIndex)
        {
            if (textures.All(t => t.Role == TexRole.Color)) return 1f;
            float need = 0f;
            foreach (var ti in textures)
                foreach (var key in ti.Mappings)
                {
                    if (!ctx.Islands.TryGetValue(key, out var islands)) continue;
                    foreach (var isl in islands)
                    {
                        if (isl.PlacedAtlas != layoutIndex) continue;
                        float g = Mathf.Max(isl.GroupScale.x, isl.GroupScale.y);
                        float own = isl.Scale.TryGetValue(ti, out var s) ? Mathf.Max(s.x, s.y) : g;
                        need = Mathf.Max(need, g > 1e-6f ? own / g : 1f);
                    }
                }
            if (need <= 0f || need >= 0.999f) return 1f;
            // keep min padding honored / 保证最小 padding
            float minScale = (int)ctx.Settings.minPadding / (float)Mathf.Max(4, (int)ctx.Settings.minPadding);
            return Mathf.Clamp(Mathf.Max(need, 0.25f) * minScale, 0.25f, 1f);
        }

        private static int SnapSize(int v, AtoContext ctx)
        {
            v = Mathf.Clamp(v, 64, ctx.MaxAtlasSize);
            if (ctx.Settings.experimentalNpot) return ((v + 63) / 64) * 64;
            int p = 64;
            while (p < v) p <<= 1;
            return Mathf.Min(p, ctx.MaxAtlasSize);
        }

        /// <summary>Draw one island into the atlas RT (resample + optional rotate). / 绘制单岛。</summary>
        private static void DrawIsland(AtoContext ctx, RenderTexture atlasRt, TexInfo ti, Island isl,
            float fx, float fy)
        {
            // 1) resample island content to RasterSize / 重采样岛内容
            var srcRect = QualityStage.IslandPixelRect(isl, ti.Tex.width, ti.Tex.height);
            int dw = Mathf.Max(1, Mathf.RoundToInt(isl.RasterSize.x * fx));
            int dh = Mathf.Max(1, Mathf.RoundToInt(isl.RasterSize.y * fy));
            bool premult = ti.HasAlphaContent && ti.Role == TexRole.Color;
            bool asNormal = ti.Role == TexRole.Normal;
            bool lossless1to1 = ctx.Quality.IsLossless && dw == srcRect.width && dh == srcRect.height;

            var content = ResampleToRt(ti.Tex, srcRect, new Vector2Int(dw, dh), premult, asNormal, lossless1to1);

            // 2) draw at position with optional 90° rotation / 旋转绘制
            // Pixel matrix is y-UP to match UV/ReadPixels conventions everywhere.
            // 像素矩阵取 y 向上，与 UV/ReadPixels 约定全程一致。
            var prev = RenderTexture.active;
            RenderTexture.active = atlasRt;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, atlasRt.width, 0, atlasRt.height);

            float px = isl.PlacePos.x * fx, py = isl.PlacePos.y * fy;
            float w = dw, h = dh;
            var mat = BlitMat;
            mat.mainTexture = content;
            mat.SetPass(1); // pass 1 = straight copy / 直拷贝
            GL.Begin(GL.QUADS);
            if (!isl.Rotated)
            {
                // local (x,y) -> atlas (px+x, py+y) / 与 IslandToAtlasPx 一致
                GL.TexCoord2(0, 0); GL.Vertex3(px, py, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(px + w, py, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(px + w, py + h, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(px, py + h, 0);
            }
            else
            {
                // local (x,y) -> atlas (px + y, py + W - x); footprint (h, w)
                // 与 IslandToAtlasPx 的旋转映射严格一致；占位 (h, w)
                GL.TexCoord2(1, 0); GL.Vertex3(px, py, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(px + h, py, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(px + h, py + w, 0);
                GL.TexCoord2(0, 0); GL.Vertex3(px, py + w, 0);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(content);
        }

        private static Material _blitMat;
        private static Material BlitMat
        {
            get
            {
                if (_blitMat == null)
                    _blitMat = new Material(Shader.Find("Hidden/ATO/Resample")) { hideFlags = HideFlags.HideAndDontSave };
                return _blitMat;
            }
        }

        private static RenderTexture ResampleToRt(Texture2D tex, RectInt rect, Vector2Int target,
            bool premult, bool asNormal, bool straightCopy)
        {
            var decodeMat = new Material(Shader.Find("Hidden/ATO/Decode")) { hideFlags = HideFlags.HideAndDontSave };
            var resampleMat = BlitMat;
            RenderTexture crop = null, work = null;
            var outRt = RenderTexture.GetTemporary(target.x, target.y, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            try
            {
                crop = RenderTexture.GetTemporary(rect.width, rect.height, 0,
                    RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                var scale = new Vector2(rect.width / (float)tex.width, rect.height / (float)tex.height);
                var offset = new Vector2(rect.x / (float)tex.width, rect.y / (float)tex.height);
                Graphics.Blit(tex, crop, scale, offset);
                if (asNormal)
                {
                    // decode -> renormalized encode / 解码重归一化编码
                    var tmp = RenderTexture.GetTemporary(rect.width, rect.height, 0,
                        RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    decodeMat.SetFloat("_AsNormal", 1f);
                    Graphics.Blit(crop, tmp, decodeMat, 0);
                    RenderTexture.ReleaseTemporary(crop);
                    crop = tmp;
                }

                var src = crop;
                if (premult && !straightCopy)
                {
                    work = RenderTexture.GetTemporary(rect.width, rect.height, 0,
                        RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    Graphics.Blit(crop, work, resampleMat, 0);
                    src = work;
                }
                Graphics.Blit(src, outRt, resampleMat, 1);
                if (premult && !straightCopy)
                {
                    var un = RenderTexture.GetTemporary(target.x, target.y, 0,
                        RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    Graphics.Blit(outRt, un, resampleMat, 2);
                    RenderTexture.ReleaseTemporary(outRt);
                    outRt = un;
                }
                return outRt;
            }
            finally
            {
                if (crop) RenderTexture.ReleaseTemporary(crop);
                if (work) RenderTexture.ReleaseTemporary(work);
                UnityEngine.Object.DestroyImmediate(decodeMat);
            }
        }

        private static Texture2D ReadbackAtlas(RenderTexture rt, Vector2Int size, bool srgb, TexRole role)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var readFloat = new Texture2D(size.x, size.y, TextureFormat.RGBAFloat, false, true);
            readFloat.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0, false);
            readFloat.Apply(false);
            RenderTexture.active = prev;

            var pixels = readFloat.GetPixels();
            UnityEngine.Object.DestroyImmediate(readFloat);

            var tex = new Texture2D(size.x, size.y, TextureFormat.RGBA32, true, !srgb);
            var final = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                float r = c.r, g = c.g, b = c.b, a = Mathf.Clamp01(c.a);
                if (srgb)
                {
                    r = Mathf.LinearToGammaSpace(r);
                    g = Mathf.LinearToGammaSpace(g);
                    b = Mathf.LinearToGammaSpace(b);
                }
                if (role == TexRole.Normal)
                {
                    // RGorAG-safe encode: A=X keeps both unpack paths correct / A=X 双路径安全编码
                    a = r;
                }
                final[i] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(r) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(b) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
            }
            tex.SetPixels32(final);
            tex.Apply(true, false);
            return tex;
        }

        // ---- non-atlas whole scaling / 非图集整图缩放 ----
        private static void BakeWholeScaled(AtoContext ctx)
        {
            var candidates = ctx.Textures.Values
                .Where(t => !t.Whitelisted && t.AtlasIndex < 0 && t.Output == null)
                .ToList();
            foreach (var ti in candidates)
            {
                float scale = 1f;
                bool any = false;
                foreach (var key in ti.Mappings)
                {
                    if (!ctx.Islands.TryGetValue(key, out var islands)) continue;
                    foreach (var isl in islands)
                        if (isl.Scale.TryGetValue(ti, out var s))
                        {
                            scale = Mathf.Max(any ? scale : 0f, Mathf.Max(s.x, s.y));
                            any = true;
                        }
                }
                if (!any || scale >= 0.999f) { ctx.Stats.FinalPixels += (long)ti.Tex.width * ti.Tex.height; continue; }

                var target = new Vector2Int(
                    SnapSize(Mathf.Max(4, Mathf.CeilToInt(ti.Tex.width * scale)), ctx),
                    SnapSize(Mathf.Max(4, Mathf.CeilToInt(ti.Tex.height * scale)), ctx));
                if (target.x >= ti.Tex.width && target.y >= ti.Tex.height) continue;

                bool premult = ti.HasAlphaContent && ti.Role == TexRole.Color;
                var pixels = Resampler.ResizeFull(ti.Tex, target, premult, ti.Role == TexRole.Normal);
                var tex = new Texture2D(target.x, target.y, TextureFormat.RGBA32, true, !ti.SRGB);
                var final = new Color32[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    float r = c.x, g = c.y, b = c.z, a = Mathf.Clamp01(c.w);
                    if (ti.SRGB)
                    {
                        r = Mathf.LinearToGammaSpace(r);
                        g = Mathf.LinearToGammaSpace(g);
                        b = Mathf.LinearToGammaSpace(b);
                    }
                    if (ti.Role == TexRole.Normal) a = r;
                    final[i] = new Color32(
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(r) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(b) * 255f),
                        (byte)Mathf.RoundToInt(a * 255f));
                }
                pixels.Dispose();
                tex.SetPixels32(final);
                tex.Apply(true, false);
                tex.name = $"ATO_scaled_{ti.Tex.name}";
                tex.wrapMode = ti.Tex.wrapMode;
                tex.filterMode = ti.Filter;
                tex.anisoLevel = ti.Tex.anisoLevel;
                ti.Output = tex;
                ti.WholeScale = scale;
                ctx.Stats.TexturesScaled++;
                AtoLog.Info($"whole-scaled '{ti.Tex.name}' {ti.Tex.width}x{ti.Tex.height} -> {target.x}x{target.y}");
            }
        }
    }

    /// <summary>GPU pull-push infinite dilation. / GPU pull-push 无限外扩。</summary>
    public static class PullPush
    {
        public static RenderTexture Fill(RenderTexture src)
        {
            var mat = new Material(Shader.Find("Hidden/ATO/PullPush")) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var levels = new List<RenderTexture>();
                var cur = src;
                int w = src.width, h = src.height;
                // pull chain / 下采样链
                while (w > 1 || h > 1)
                {
                    w = Mathf.Max(1, w >> 1);
                    h = Mathf.Max(1, h >> 1);
                    var next = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat,
                        RenderTextureReadWrite.Linear);
                    Graphics.Blit(cur, next, mat, 0);
                    levels.Add(next);
                    cur = next;
                }
                // push chain / 回推链
                RenderTexture coarse = levels.Count > 0 ? levels[levels.Count - 1] : src;
                for (int i = levels.Count - 2; i >= -1; i--)
                {
                    var fine = i >= 0 ? levels[i] : src;
                    var filledFine = RenderTexture.GetTemporary(fine.width, fine.height, 0,
                        RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    mat.SetTexture("_CoarseTex", coarse);
                    Graphics.Blit(fine, filledFine, mat, 1);
                    if (i >= 0 && coarse != levels[levels.Count - 1]) RenderTexture.ReleaseTemporary(coarse);
                    else if (coarse != levels[levels.Count - 1] && coarse != src) RenderTexture.ReleaseTemporary(coarse);
                    coarse = filledFine;
                }
                // release pull levels / 释放
                foreach (var l in levels) RenderTexture.ReleaseTemporary(l);

                // final: rgb from filled, alpha from src / RGB取填充结果，alpha取原始（岛外0）
                var final = RenderTexture.GetTemporary(src.width, src.height, 0,
                    RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                var combine = new Material(Shader.Find("Hidden/ATO/PullPush")) { hideFlags = HideFlags.HideAndDontSave };
                combine.SetTexture("_CoarseTex", coarse);
                Graphics.Blit(src, final, combine, 2);
                UnityEngine.Object.DestroyImmediate(combine);
                RenderTexture.ReleaseTemporary(coarse);
                return final;
            }
            finally { UnityEngine.Object.DestroyImmediate(mat); }
        }
    }
}

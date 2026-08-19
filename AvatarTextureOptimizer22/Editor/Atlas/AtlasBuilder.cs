// AvatarTextureOptimizer
// File: Editor/Atlas/AtlasBuilder.cs
//
// Creates the actual atlas textures:
//   - GPU-resamples each island (from its source texture region, with the
//     island's final quality scale and 90° rotation when placed rotated) into
//     the atlas RenderTexture
//   - fills empty regions with GPU pull-push (infinite extrapolation); alpha
//     stays 0 for transparent atlases
//   - names start with ATO_
//   - applies per-category import settings (compression, mipmap + streaming
//     binding, Clamp wrap, no Read/Write) and saves into NDMF's asset
//     container so the build pipeline persists them
//   - deduplicates identical atlases/textures
//
// 创建实际图集贴图：
//   - 将每个岛从源贴图区域 GPU 重采样（应用岛的最终质量缩放与放置旋转）到
//     图集 RenderTexture
//   - 用 GPU pull-push（无限外扩）填充空白区域；透明图集 alpha 保持 0
//   - 名称以 ATO_ 开头
//   - 应用按类别的导入参数（压缩、mipmap 与 streaming 绑定、Clamp 包裹、
//     关闭 Read/Write）并保存进 NDMF 资产容器，使构建流水线持久化它们
//   - 对相同图集/贴图去重

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.import;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.atlas
{
    public static class AtlasBuilder
    {
        private static ComputeShader _pullPush;
        private static int _kPull, _kPush;

        private static ComputeShader PullPushShader
        {
            get
            {
                if (_pullPush != null) return _pullPush;
                _pullPush = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/net.fosa.avatar-texture-optimizer/Editor/Atlas/Shaders/ATO_PullPush.compute");
                if (_pullPush != null)
                {
                    _kPull = _pullPush.FindKernel("PullDown");
                    _kPush = _pullPush.FindKernel("PushUp");
                }
                return _pullPush;
            }
        }

        public static void Build(BuildContext context, ATOBuildState state)
        {
            if (state.Atlases.Count == 0) return;
            var component = state.Component;
            var stopwatch = new ATOStopwatch("AtlasBuilder.Build");
            bool npot = component.Atlas.EnableNPOT;

            foreach (var atlas in state.Atlases)
            {
                if (atlas.Texture != null) continue; // already built / 已构建
                stopwatch.Begin($"build {atlas.Name}");
                BuildAtlas(context, state, atlas, npot);
                stopwatch.End($"build {atlas.Name}");
            }

            ATOLog.Info($"[ATO] Built {state.Atlases.Count} atlas textures. / 构建了 {state.Atlases.Count} 张图集贴图。");
        }

        private static void BuildAtlas(BuildContext context, ATOBuildState state, AtlasEntry atlas, bool npot)
        {
            bool hasAlpha = atlas.TypeGroup != null && atlas.TypeGroup.HasAlpha;
            var category = CategoryFor(atlas.TypeGroup);

            // Collect the islands that belong to this atlas: only groups of the
            // atlas's canonical layout that reference THIS type group's texture.
            // 收集属于该图集的岛：仅该图集规范布局中引用本类型组贴图的组。
            var islands = new List<(UVGroup Group, UVIsland Island)>();
            foreach (var group in state.UVGroups)
            {
                if (group.Whitelisted || group.SkippedAtlas) continue;
                if (group.AtlasIndex != atlas.LayoutIndex) continue;
                if (atlas.TypeGroup != null && !group.Textures.Any(u =>
                        u.Texture != null && atlas.TypeGroup.Textures.Contains(u.Texture)))
                    continue;
                foreach (var island in group.Islands)
                    if (island.Raster != null && island.Raster.WidthCells > 0)
                        islands.Add((group, island));
            }
            if (islands.Count == 0) return;

            // 1. Draw all islands into the atlas RT (linear space).
            //    将所有岛绘制进图集 RT（线性空间）。
            var rt = GPUImageOps.CreateRT(atlas.Width, atlas.Height);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0, 0, 0, hasAlpha ? 0 : 1));
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, atlas.Width, 0, atlas.Height);

            foreach (var (group, island) in islands)
            {
                // The source texture of this island: the texture of THIS atlas's
                // type group referenced by the group (main/normal/mask each
                // draw from their own texture). / 该岛的源贴图：组引用的、属于
                // 本图集类型组的贴图（主色/法线/蒙版各自从自己的贴图绘制）。
                var source = atlas.TypeGroup != null
                    ? group.Textures.FirstOrDefault(u => atlas.TypeGroup.Textures.Contains(u.Texture))?.Texture
                    : (group.Textures.Count > 0 ? group.Textures[0].Texture : null);
                if (source == null) continue;

                DrawIsland(source, group, island, rt, hasAlpha);
            }

            GL.PopMatrix();
            RenderTexture.active = prev;

            // 2. Pull-push fill of empty regions. / 空白区域 pull-push 填充。
            if (component.Atlas.PullPushFill)
                PullPushFill(rt, hasAlpha);

            // 3. Read back into a Texture2D. / 读回 Texture2D。
            RenderTexture.active = rt;
            bool linear = atlas.TypeGroup != null && !atlas.TypeGroup.IsSRGB;
            var tex = new Texture2D(atlas.Width, atlas.Height, TextureFormat.RGBA32, false, linear);
            tex.ReadPixels(new Rect(0, 0, atlas.Width, atlas.Height), 0, 0);
            RenderTexture.active = prev;
            rt.Release();

            tex.name = atlas.Name;
            tex.wrapMode = TextureWrapMode.Clamp;

            // 4. Per-category import settings. / 按类别导入参数。
            TextureImportConfig.ApplyGeneratedSettings(state, tex, category, hasAlpha, npot, readableForDedup: true);

            // 5. Deduplicate identical atlas textures. / 对相同图集去重。
            var existing = state.NewTextures.FirstOrDefault(t => t != null &&
                t.width == tex.width && t.height == tex.height &&
                FormatsEqual(t.format, tex.format) && ContentEquals(t, tex));
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(tex);
                tex = existing;
            }
            else
            {
                state.NewTextures.Add(tex);
                // Persist into NDMF's asset container. / 持久化进 NDMF 资产容器。
                PersistTexture(context, tex);
            }
            atlas.Texture = tex;

            // 6. Report accounting. / 报告记账。
            var entry = new logging.AtlasReportEntry
            {
                Name = atlas.Name,
                Width = atlas.Width,
                Height = atlas.Height,
                SourceCount = atlas.Sources.Count,
                Utilization = atlas.Utilization,
                IslandCount = islands.Count,
            };
            foreach (var kv in atlas.Sources)
            {
                entry.Sources.Add(kv.Key.name);
                entry.OriginalBytes += TextureImportConfig.EstimateBytes(kv.Key);
            }
            entry.AtlasBytes = TextureImportConfig.EstimateBytes(tex);
            state.Report.AddAtlas(entry);
            state.Report.AddBytes(entry.OriginalBytes, entry.AtlasBytes);
            ATOLog.Info($"[ATO] Atlas {atlas.Name}: {atlas.Width}x{atlas.Height}, {islands.Count} islands, utilization {atlas.Utilization:P1}. / 图集 {atlas.Name}：{atlas.Width}x{atlas.Height}，{islands.Count} 个岛，利用率 {atlas.Utilization:P1}。");
        }

        /// <summary>
        /// Draw one island into the atlas RT with bilinear sampling of its
        /// source region. Handles rotation (transposed placement).
        /// 将单个岛绘制进图集 RT，对源区域双线性采样。处理旋转（转置放置）。
        /// </summary>
        private static void DrawIsland(Texture2D source, UVGroup group, UVIsland island, RenderTexture rt, bool hasAlpha)
        {
            var rect = island.ScaledRect;
            if (rect.width < 1 || rect.height < 1) return;

            // Island pixel bounds in the source texture (clamped to the
            // texture extent so out-of-bounds-but-normalizable islands sample
            // correctly). / 源贴图中的岛像素包围盒（钳制到贴图范围，使越界
            // 但可归一的岛正确采样）。
            int pbw = Mathf.Clamp(island.PixelBounds.width, 1, source.width - Mathf.Clamp(island.PixelBounds.x, 0, source.width - 1));
            int pbh = Mathf.Clamp(island.PixelBounds.height, 1, source.height - Mathf.Clamp(island.PixelBounds.y, 0, source.height - 1));
            int pbx = Mathf.Clamp(island.PixelBounds.x, 0, source.width - pbw);
            int pby = Mathf.Clamp(island.PixelBounds.y, 0, source.height - pbh);
            float sx = pbx / (float)source.width;
            float sy = pby / (float)source.height;
            float sw = pbw / (float)source.width;
            float sh = pbh / (float)source.height;

            if (island.RotatedInAtlas)
            {
                // Rotated 90° CW in the atlas: draw the source with a rotated
                // UV mapping into a rect of (sh, sw) dimensions.
                // 在图集中顺时针旋转 90 度：以旋转 UV 映射绘制到 (sh, sw) 矩形。
                float angle = -90f * Mathf.Deg2Rad;
                var dest = new Rect(rect.x, rect.y, rect.height, rect.width);
                GL.PushMatrix();
                GL.MultMatrix(Matrix4x4.TRS(new Vector3(rect.x + rect.height * 0.5f, rect.y + rect.width * 0.5f, 0),
                    Quaternion.Euler(0, 0, angle), Vector3.one));
                Graphics.DrawTexture(new Rect(-rect.height * 0.5f, -rect.width * 0.5f, rect.height, rect.width),
                    source, new Rect(sx, sy, sh, sw), 0, 0, 0, 0);
                GL.PopMatrix();
            }
            else
            {
                Graphics.DrawTexture(rect, source, new Rect(sx, sy, sw, sh), 0, 0, 0, 0);
            }
        }

        /// <summary>
        /// GPU pull-push infinite extrapolation over the atlas RT.
        /// GPU pull-push 无限外扩。
        /// </summary>
        private static void PullPushFill(RenderTexture rt, bool hasAlpha)
        {
            var shader = PullPushShader;
            if (shader == null) return;

            int levels = 1;
            int w = rt.width, h = rt.height;
            while (w > 1 || h > 1) { w = Mathf.Max(1, w >> 1); h = Mathf.Max(1, h >> 1); levels++; }

            // Build the mip chain. / 构建 mip 链。
            var colors = new RenderTexture[levels];
            var covers = new RenderTexture[levels];
            colors[0] = rt;
            covers[0] = MakeCoverRT(rt.width, rt.height);
            // Coverage from alpha? We need a coverage mask; approximate: any
            // pixel that is NOT fully transparent/black counts as covered.
            // Actually the coverage is derived from where islands were drawn:
            // for opaque atlases the clear color is black -> treat alpha==1 as
            // empty; for transparent atlases alpha>0 is covered.
            // 从 alpha 推导覆盖率：不透明图集黑色 alpha==1 视为空；透明图集
            // alpha>0 视为已覆盖。
            InitCover(rt, covers[0], hasAlpha);

            for (int l = 1; l < levels; l++)
            {
                int cw = Mathf.Max(1, colors[l - 1].width >> 1);
                int ch = Mathf.Max(1, colors[l - 1].height >> 1);
                colors[l] = GPUImageOps.CreateRT(cw, ch);
                covers[l] = MakeCoverRT(cw, ch);
                SetInputs(shader, _kPull, colors[l - 1], covers[l - 1], colors[l], covers[l]);
                shader.Dispatch(_kPull, Mathf.Max(1, Mathf.CeilToInt(cw / 8f)), Mathf.Max(1, Mathf.CeilToInt(ch / 8f)), 1);
            }

            // Push from coarse to fine. / 从粗到细 push。
            for (int l = levels - 1; l >= 1; l--)
            {
                SetInputs(shader, _kPush, colors[l], covers[l], colors[l - 1], covers[l - 1]);
                shader.SetInt("KeepAlphaZero", hasAlpha ? 1 : 0);
                shader.Dispatch(_kPush,
                    Mathf.Max(1, Mathf.CeilToInt(colors[l - 1].width / 8f)),
                    Mathf.Max(1, Mathf.CeilToInt(colors[l - 1].height / 8f)), 1);
            }

            // Free chain except level 0 (the atlas). / 释放除 0 级（图集）外的链。
            for (int l = 1; l < levels; l++)
            {
                colors[l].Release();
                covers[l].Release();
            }
            covers[0].Release();
        }

        private static RenderTexture MakeCoverRT(int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.R8)
            {
                enableRandomWrite = true,
                name = "ATO_Cover",
            };
            rt.Create();
            return rt;
        }

        private static void InitCover(RenderTexture color, RenderTexture cover, bool hasAlpha)
        {
            // CPU coverage init is expensive; we instead clear the atlas RT to
            // transparent black and draw islands over it, so coverage = (alpha
            // > 0) for transparent atlases and (any channel > 0) for opaque.
            // 覆盖率初始化：图集 RT 已被清为透明黑并绘制了岛；因此覆盖率 =
            // 透明图集 (alpha>0)、不透明图集 (任意通道>0)。
            var prev = RenderTexture.active;
            RenderTexture.active = cover;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = prev;

            // Readback-free approach: run a small compute that writes 1 where
            // the color differs from the empty sentinel. We approximate with
            // the alpha channel only (documented limitation).
            // 免读回方案：alpha 通道近似（已注明的局限）。
            // (Implemented via the DrawCover kernel below.)
            DrawCover(color, cover, hasAlpha);
        }

        private static void DrawCover(RenderTexture color, RenderTexture cover, bool hasAlpha)
        {
            // Simplest robust coverage: copy alpha>0 (or color!=0) into cover
            // using a tiny graphics blit with a shader. To avoid an extra
            // shader, we use the PullPush shader's PullDown at level 0? Not
            // applicable. We use a dedicated kernel-less trick: read back once
            // (acceptable for a single atlas) and write the cover texture.
            // 最简稳健方案：用一次读回初始化 cover（单张图集可接受）。
            var prev = RenderTexture.active;
            RenderTexture.active = color;
            var tex = new Texture2D(color.width, color.height, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, color.width, color.height), 0, 0);
            RenderTexture.active = prev;

            RenderTexture.active = cover;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = prev;

            var write = new Texture2D(cover.width, cover.height, TextureFormat.R8, false, true);
            var px = tex.GetPixels32();
            var data = new byte[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                bool covered = hasAlpha ? p.a > 8 : (p.r | p.g | p.b) > 8;
                data[i] = covered ? (byte)255 : (byte)0;
            }
            write.SetPixelData(data, 0);
            write.Apply();
            Graphics.Blit(write, cover);
            UnityEngine.Object.DestroyImmediate(write);
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void SetInputs(ComputeShader shader, int kernel, RenderTexture inColor, RenderTexture inCover,
            RenderTexture outColor, RenderTexture outCover)
        {
            shader.SetTexture(kernel, "InColor", inColor);
            shader.SetTexture(kernel, "InCover", inCover);
            shader.SetTexture(kernel, "OutColor", outColor);
            shader.SetTexture(kernel, "OutCover", outCover);
            shader.SetVector("InSize", new Vector4(inColor.width, inColor.height, 1f / inColor.width, 1f / inColor.height));
            shader.SetVector("OutSize", new Vector4(outColor.width, outColor.height, 1f / outColor.width, 1f / outColor.height));
        }

        private static ATOImportCategory CategoryFor(TextureTypeGroup tg)
        {
            if (tg == null) return ATOImportCategory.Opaque;
            if ((tg.Companions & CompanionFlags.Normal) != 0) return ATOImportCategory.NormalMap;
            if ((tg.Companions & CompanionFlags.Mask) != 0) return ATOImportCategory.Grayscale;
            return tg.HasAlpha ? ATOImportCategory.Transparent : ATOImportCategory.Opaque;
        }

        private static void PersistTexture(BuildContext context, Texture2D tex)
        {
            try
            {
                var container = context.AssetContainer;
                if (container != null)
                {
                    AssetDatabase.AddObjectToAsset(tex, container);
                    tex.hideFlags = HideFlags.HideInHierarchy;
                }
            }
            catch (Exception e)
            {
                ATOLog.Warn($"[ATO] Failed to persist atlas {tex.name}: {e.Message}. / 无法持久化图集。");
            }
        }

        private static bool FormatsEqual(TextureFormat a, TextureFormat b) => a == b;

        private static bool ContentEquals(Texture2D a, Texture2D b)
        {
            if (a == b) return true;
            if (a.width != b.width || a.height != b.height) return false;
            if (!a.isReadable || !b.isReadable) return false;
            try
            {
                var pa = a.GetPixels32();
                var pb = b.GetPixels32();
                if (pa.Length != pb.Length) return false;
                for (int i = 0; i < pa.Length; i++)
                    if (!pa[i].Equals(pb[i])) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

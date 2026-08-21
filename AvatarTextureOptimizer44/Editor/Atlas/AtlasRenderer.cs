// AtlasRenderer.cs - Render packed islands into atlas images (per texture-type signature), GPU pull-push
// dilation, readback and handoff to TextureWriter. / 将装箱后的岛渲染为图集映像（按类型签名），GPU外扩，回读并交给写入器。
// One LAYOUT can produce several IMAGES (main sRGB bilinear / normal linear / mask ...) sharing island rects,
// so the same UV points at the same place in every atlas image. / 同一布局可产出多张映像（主色/法线/蒙版…），
// 岛位置在所有映像中一致，同一UV处处同位。
using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.ATO.Editor.Analysis;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Runtime;
using UnityEngine;
using Fosa.ATO.Editor.Quality;

namespace Fosa.ATO.Editor.Atlas
{
    /// <summary>One rendered atlas image. / 单张渲染出的图集映像。</summary>
    public sealed class AtlasImage
    {
        public AtlasPlan plan;
        public bool isNormal, srgb;
        public FilterMode filter;
        public ATOTextureCategory category;
        public Texture2D output;                  // final / 成品
        public readonly HashSet<TexEntry> sources = new HashSet<TexEntry>();
    }

    public static class AtlasRenderer
    {
        /// <summary>Render every atlas image. / 渲染全部图集映像。</summary>
        public static List<AtlasImage> Render(UsageGraph g, PackResult pack, GPUContext gpu, GPUTexOps ops, ATOSettings st, ATOPlatform platform, ATOProgress progress)
        {
            using (ATOLog.Scope("RenderAtlases"))
            {
                var images = new List<AtlasImage>();
                int pi = 0;
                foreach (var plan in pack.atlases)
                {
                    progress?.Report(pi++ / (float)Mathf.Max(1, pack.atlases.Count), "Render atlases");
                    // image signatures needed by this layout / 本布局需要的映像签名
                    var keys = new Dictionary<(bool, bool, FilterMode, ATOTextureCategory), AtlasImage>();
                    foreach (var isl in plan.islands)
                        foreach (var e in isl.group.textures)
                        {
                            if (e.whitelisted) continue;
                            var cat = e.Category();
                            var k = (cat == ATOTextureCategory.NormalMap, e.import.sRGB, e.texture.filterMode, cat);
                            if (!keys.TryGetValue(k, out var img))
                            {
                                img = new AtlasImage { plan = plan, isNormal = k.Item1, srgb = k.Item2, filter = k.Item3, category = k.Item4 };
                                keys[k] = img; images.Add(img);
                            }
                            img.sources.Add(e);
                        }

                    foreach (var img in keys.Values)
                        RenderImage(g, plan, img, gpu, ops, st, platform);
                }
                foreach (var img in images)
                    ATOLog.Info($"image: {(img.output != null ? img.output.name : "?")} {img.plan.width}x{img.plan.height} {img.category} src={img.sources.Count}");
                return images;
            }
        }

        private static void RenderImage(UsageGraph g, AtlasPlan plan, AtlasImage img, GPUContext gpu, GPUTexOps ops, ATOSettings st, ATOPlatform platform)
        {
            int W = plan.width, H = plan.height;
            bool transparent = img.category == ATOTextureCategory.Transparent;
            var rt = gpu.Owned(W, H, RenderTextureFormat.ARGBFloat, uav: true, name: "ATO_atlas");
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, new Color(0, 0, 0, 0));
            RenderTexture.active = prev;

            var mat = gpu.Mat("IslandCopy", "Hidden/ATO/IslandCopy");
            foreach (var e in img.sources)
            {
                var srcRt = ops.ToLinearRT(e.texture); // bounded cache / 有界缓存
                bool isNormal = (e.StrictestRole & ATOTextureRole.Normal) != 0;
                foreach (var grp in g.Coverage(e))
                {
                    if (!grp.Processable) continue;
                    foreach (var isl in grp.islands.Where(i => i.placed && i.atlasId == plan.id))
                        DrawIsland(mat, rt, srcRt, e, isl, isNormal, transparent, ops);
                }
            }

            PullPushBleed(gpu, rt, transparent);
            img.output = TextureWriter.FinalizeAtlas(rt, img, st, platform);
        }

        /// <summary>Draw one island: downsample source region to target, then 1:1 (rotated) copy into the atlas rect. / 绘制单岛：源区域降采样到目标尺寸，再1:1（可旋转）拷入图集矩形。</summary>
        private static void DrawIsland(Material mat, RenderTexture atlasRt, RenderTexture srcRt, TexEntry e, Island isl, bool isNormal, bool transparent, GPUTexOps ops)
        {
            var region = RegionOf(isl, e.texture);
            var down = ops.Downsample(srcRt, region, isl.targetW, isl.targetH, isNormal, transparent);
            mat.SetTexture("_MainTex", down);
            mat.SetFloat("_Unpremult", transparent ? 1f : 0f);

            var rect = isl.atlasRect;
            var prev = RenderTexture.active;
            RenderTexture.active = atlasRt;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, atlasRt.width, atlasRt.height, 0);
            mat.SetPass(0);
            GL.Begin(GL.QUADS);
            if (!isl.rotated)
            {
                GL.TexCoord2(0, 0); GL.Vertex3(rect.xMin, rect.yMin, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(rect.xMax, rect.yMin, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(rect.xMax, rect.yMax, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(rect.xMin, rect.yMax, 0);
            }
            else
            {
                // 90deg CCW: atlas-local A = (1-Ly, Lx); inverse L = (Ay, 1-Ax) / 逆时针90度映射
                GL.TexCoord2(0, 1); GL.Vertex3(rect.xMin, rect.yMin, 0);
                GL.TexCoord2(0, 0); GL.Vertex3(rect.xMax, rect.yMin, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(rect.xMax, rect.yMax, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(rect.xMin, rect.yMax, 0);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(down); // temp discipline / 临时RT即用即还
        }

        private static RectInt RegionOf(Island isl, Texture2D tex)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(isl.uvMin.x * tex.width), 0, tex.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(isl.uvMin.y * tex.height), 0, tex.height - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt((isl.uvMax.x - isl.uvMin.x) * tex.width), 1, tex.width - x);
            int h = Mathf.Clamp(Mathf.CeilToInt((isl.uvMax.y - isl.uvMin.y) * tex.height), 1, tex.height - y);
            return new RectInt(x, y, w, h);
        }

        /// <summary>Pull-push pyramid bleed; transparent atlases restore alpha=0 outside original coverage. / 金字塔外扩；透明图集在原覆盖外恢复alpha=0。</summary>
        private static void PullPushBleed(GPUContext gpu, RenderTexture rt, bool transparent)
        {
            var cs = gpu.Compute("ATOPullPush");
            RenderTexture coverage = null;
            if (transparent)
            {
                coverage = gpu.Owned(rt.width, rt.height, RenderTextureFormat.ARGBFloat, name: "ATO_cover");
                Graphics.CopyTexture(rt, coverage);
            }
            // pull down / 下拉
            var levels = new List<RenderTexture> { rt };
            int w = rt.width / 2, h = rt.height / 2;
            while (w >= 1 && h >= 1 && levels.Count < 14)
            {
                var lv = gpu.Owned(w, h, RenderTextureFormat.ARGBFloat, uav: true, name: "ATO_pp");
                levels.Add(lv);
                if (w == 1 || h == 1) break;
                w /= 2; h /= 2;
            }
            for (int i = 1; i < levels.Count; i++)
            {
                var dst = levels[i];
                cs.SetTexture(0, "_Src", levels[i - 1]);
                cs.SetTexture(0, "_Dst", dst);
                cs.SetInts("_DstSize", dst.width, dst.height);
                cs.Dispatch(0, (dst.width + 7) / 8, (dst.height + 7) / 8, 1);
            }
            // push up into every finer level incl. the atlas / 上推回填（含图集层）
            for (int i = levels.Count - 2; i >= 0; i--)
            {
                var fine = levels[i];
                cs.SetTexture(1, "_Coarse", levels[i + 1]);
                cs.SetTexture(1, "_Fine", fine);
                cs.SetInts("_FineSize", fine.width, fine.height);
                cs.Dispatch(1, (fine.width + 7) / 8, (fine.height + 7) / 8, 1);
            }
            // restore alpha=0 outside coverage / 覆盖外alpha归零
            if (transparent && coverage != null)
            {
                cs.SetTexture(2, "_Orig", coverage);
                cs.SetTexture(2, "_Target", rt);
                cs.Dispatch(2, (rt.width + 7) / 8, (rt.height + 7) / 8, 1);
            }
        }
    }
}

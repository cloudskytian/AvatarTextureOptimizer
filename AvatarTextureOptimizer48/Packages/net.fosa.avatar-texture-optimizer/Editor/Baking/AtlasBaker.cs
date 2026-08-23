// Atlas baking: draws islands into canvases on the GPU (RenderTexture + GL quads), applies pull-push
// bleeding, reads back, saves PNG/EXR, and configures the importer.
// / 图集烘焙：在 GPU 上把岛绘制进画布（RenderTexture + GL 四边形），执行 pull-push 外扩，
// 回读后保存 PNG/EXR 并配置导入器。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.packing;
using net.fosa.avatar_texture_optimizer.editor.pipeline;
using net.fosa.avatar_texture_optimizer.editor.quality;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.baking
{
    /// <summary>
    /// Bakes all atlases and whole-scaled textures. / 烘焙全部图集与整图缩放贴图。
    /// </summary>
    public static class AtlasBaker
    {
        private static Material _islandMat;
        private static Material _bleedMat;
        private static Texture2D _white;

        private static Material IslandMat => _islandMat != null
            ? _islandMat
            : (_islandMat = new Material(Shader.Find("ATO/IslandDraw")));

        private static Material BleedMat => _bleedMat != null
            ? _bleedMat
            : (_bleedMat = new Material(Shader.Find("ATO/Bleed")));

        private static Texture2D WhiteTex
        {
            get
            {
                if (_white == null)
                {
                    _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply();
                }
                return _white;
            }
        }

        /// <summary>Bake everything and record stats. / 烘焙全部并记录统计。</summary>
        public static void BakeAll(BuildContext ctx, PackingResult packing,
            AvatarTextureOptimizer component, BuildTargetHint hint, bool mobile, ProgressScope progress,
            BuildReport report)
        {
            string root = "Assets/ATO_Generated";
            EnsureFolder(root);
            string dir = root + "/" + Sanitize(ctx.AvatarRootObject.name);
            EnsureFolder(dir);

            int idx = 0;
            foreach (var plan in packing.Atlases)
            {
                progress.Report("Baking atlases / 烘焙图集", plan.TypeGroupKey, 0.68f + 0.2f * idx / (float)Mathf.Max(1, packing.Atlases.Count));
                var tex = BakeAtlas(ctx, plan, dir, idx, component, hint, mobile);
                if (tex == null) continue;

                // Assign result to records / 把结果分配给记录
                var seen = new HashSet<TexRecord>();
                foreach (var e in plan.Entries)
                {
                    if (seen.Add(e.Texture.Record))
                    {
                        e.Texture.Record.ResultTexture = tex;
                        e.Texture.Record.ResultName = tex.name;
                    }
                }

                // Stats / 统计
                var stat = new AtlasStat
                {
                    Name = tex.name,
                    Width = tex.width,
                    Height = tex.height,
                    IslandCount = plan.Entries.Count,
                    SourceTextures = seen.Count + " tex / 张贴图",
                };
                long used = 0;
                foreach (var e in plan.Entries) used += (long)e.W * e.H;
                stat.Utilization = tex.width * tex.height > 0 ? used / (double)(tex.width * tex.height) : 0;
                long src = 0;
                foreach (var r in seen) src += (long)r.Width * r.Height;
                stat.OriginalTexelCount = src;
                stat.AtlasTexelCount = (long)tex.width * tex.height;
                stat.SavingsRatio = src > 0 ? 1.0 - (double)stat.AtlasTexelCount / src : 0;
                report.Atlases.Add(stat);

                idx++;
            }

            // Whole-texture scaling (fallback / no-atlas) / 整图缩放（回退/无图集）
            var bar = QualityBar.FromSettings(component.quality);
            int wIdx = 0;
            foreach (var record in packing.WholeScaleRecords)
            {
                if (record.Whitelisted) continue;
                progress.Report("Scaling textures / 缩放贴图", record.Texture.name, 0.9f + 0.08f * wIdx / (float)Mathf.Max(1, packing.WholeScaleRecords.Count));
                wIdx++;
                var tex = BakeWholeScaled(ctx, record, dir, bar, component, hint, mobile);
                if (tex != null)
                {
                    record.ResultTexture = tex;
                    record.ResultName = tex.name;
                }
            }
        }

        /// <summary>Bake one atlas plan. / 烘焙一张图集。</summary>
        private static Texture2D BakeAtlas(BuildContext ctx, AtlasPlan plan, string dir, int index,
            AvatarTextureOptimizer component, BuildTargetHint hint, bool mobile)
        {
            int canvas = plan.CanvasSize;
            bool srgb = plan.TypeGroupKey.IndexOf("srgb", StringComparison.Ordinal) >= 0;
            bool isNormal = plan.TypeGroupKey.IndexOf("normal", StringComparison.Ordinal) >= 0;
            bool hasAlpha = false;
            var seen = new HashSet<TexRecord>();
            foreach (var e in plan.Entries)
            {
                if (seen.Add(e.Texture.Record) && e.Texture.Record.HasAlpha) hasAlpha = true;
            }

            var fmt = srgb ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGBHalf;
            var rt = RenderTexture.GetTemporary(canvas, canvas, 0, fmt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0, 0, 0, hasAlpha ? 0f : 1f));

            foreach (var e in plan.Entries)
            {
                DrawIsland(e.Texture.SourceTexture, e.Island.ScaledRect, e.X, e.Y, e.W, e.H, canvas, e.Rotated90);
            }

            // Pull-push bleeding / pull-push 外扩
            if (component.packing.pullPush && plan.Entries.Count > 0)
            {
                int pad = PackingPlanner.PaddingFor(canvas, component.packing.minPadding);
                int iterations = Mathf.Clamp(pad, 2, 8);
                var maskRt = RenderTexture.GetTemporary(canvas, canvas, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = maskRt;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                foreach (var e in plan.Entries)
                {
                    DrawIsland(WhiteTex, e.Island.ScaledRect, e.X, e.Y, e.W, e.H, canvas, e.Rotated90);
                }
                var srcRt = rt;
                var dstRt = RenderTexture.GetTemporary(canvas, canvas, 0, fmt);
                var mat = BleedMat;
                mat.SetTexture("_MaskTex", maskRt);
                for (int i = 0; i < iterations; i++)
                {
                    mat.SetTexture("_MainTex", srcRt);
                    Graphics.Blit(srcRt, dstRt, mat);
                    var tmp = srcRt; srcRt = dstRt; dstRt = tmp;
                }
                // srcRt holds the final result / srcRt 为最终结果
                if (srcRt != rt)
                {
                    Graphics.Blit(srcRt, rt);
                }
                RenderTexture.ReleaseTemporary(maskRt);
                RenderTexture.ReleaseTemporary(dstRt);
            }

            RenderTexture.active = rt;
            var tex = new Texture2D(canvas, canvas, srgb ? TextureFormat.RGBA32 : TextureFormat.RGBAHalf, false);
            tex.ReadPixels(new Rect(0, 0, canvas, canvas), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            string name = "ATO_" + Sanitize(plan.TypeGroupKey) + "_" + index;
            string ext = srgb ? "png" : "exr";
            string path = dir + "/" + name + "." + ext;
            byte[] bytes = srgb ? tex.EncodeToPNG() : tex.EncodeToEXR();
            File.WriteAllBytes(path, bytes);
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            var category = GetCategory(plan.TypeGroupKey, hasAlpha);
            TextureImporterSetup.Apply(path, isNormal, srgb, hasAlpha, component.output.mipmap, canvas,
                GetCompression(component, category), component.packing.allowNPOT, hint, component.platform,
                component.output.compression, category);

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Bake a whole-texture scaled version. / 烘焙整图缩放版本。</summary>
        private static Texture2D BakeWholeScaled(BuildContext ctx, TexRecord record, string dir,
            QualityBar bar, AvatarTextureOptimizer component, BuildTargetHint hint, bool mobile)
        {
            var src = record.Texture;

            // Evaluate scale on a proxy (max 512) for speed / 在代理（最大 512）上评估缩放以提速
            int proxy = Mathf.Min(512, Mathf.Max(1, Mathf.Max(src.width, src.height)));
            float scale = 1f;
            {
                var rt = RenderTexture.GetTemporary(proxy, proxy, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tmp = new Texture2D(proxy, proxy, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, proxy, proxy), 0, 0);
                tmp.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                var bytes = tmp.GetRawTextureData<byte>();
                var rgba = new float[bytes.Length / 4 * 4];
                for (int i = 0; i < bytes.Length / 4; i++)
                {
                    float a = MetricMath.SrgbByteToLinear(bytes[i * 4 + 3]);
                    rgba[i * 4] = MetricMath.SrgbByteToLinear(bytes[i * 4]) * a;
                    rgba[i * 4 + 1] = MetricMath.SrgbByteToLinear(bytes[i * 4 + 1]) * a;
                    rgba[i * 4 + 2] = MetricMath.SrgbByteToLinear(bytes[i * 4 + 2]) * a;
                    rgba[i * 4 + 3] = a;
                }
                UnityEngine.Object.DestroyImmediate(tmp);

                var role = record.Bindings.Count > 0 ? record.Bindings[0].Role : TextureRole.MainColor;
                scale = QualityEvaluator.FindWholeScale(rgba, proxy, proxy, record, role, bar);
            }

            if (scale >= 0.999f)
            {
                record.WholeScale = 1f;
                return src; // no change: keep original reference / 无变化：保留原引用
            }

            record.WholeScale = scale;
            int w = Mathf.Max(1, Mathf.RoundToInt(src.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(src.height * scale));

            bool srgb = record.IsSrgb;
            var rt2 = RenderTexture.GetTemporary(w, h, 0, srgb ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGBHalf);
            var prev2 = RenderTexture.active;
            RenderTexture.active = rt2;
            Graphics.Blit(src, rt2);
            var tex = new Texture2D(w, h, srgb ? TextureFormat.RGBA32 : TextureFormat.RGBAHalf, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev2;
            RenderTexture.ReleaseTemporary(rt2);

            var role2 = record.Bindings.Count > 0 ? record.Bindings[0].Role : TextureRole.MainColor;
            bool isNormal = role2 == TextureRole.Normal || record.IsNormalMap;
            string ext = srgb ? "png" : "exr";
            string name = "ATO_scale_" + Sanitize(src.name) + "_" + (w) + "x" + h;
            string path = dir + "/" + name + "." + ext;
            File.WriteAllBytes(path, srgb ? tex.EncodeToPNG() : tex.EncodeToEXR());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            var category = isNormal ? "normal" : record.HasAlpha ? "transparent" : "opaque";
            TextureImporterSetup.Apply(path, isNormal, srgb, record.HasAlpha, component.output.mipmap,
                Mathf.Max(w, h), GetCompression(component, category), component.packing.allowNPOT,
                hint, component.platform, component.output.compression, category);

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Draw one island quad (GPU). / 在 GPU 上绘制一个岛四边形。</summary>
        private static void DrawIsland(Texture2D src, Rect srcUv, int x, int y, int w, int h, int canvas, bool rotated)
        {
            var mat = IslandMat;
            mat.mainTexture = src;
            mat.SetPass(0);

            float x0 = x / (float)canvas;
            float x1 = (x + w) / (float)canvas;
            float y0 = 1f - (y + h) / (float)canvas;   // flip: texture row 0 = top / 翻转：纹理行 0 在顶部
            float y1 = 1f - y / (float)canvas;

            // target locals (tx,ty) for corners / 角点目标局部坐标
            var corners = new[]
            {
                new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1)
            };
            var locals = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)
            };

            GL.PushMatrix();
            GL.LoadOrtho();
            GL.Begin(GL.QUADS);
            for (int i = 0; i < 4; i++)
            {
                var l = locals[i];
                // rotated 90°: source local = (1-ty, tx) / 旋转 90°：源局部坐标 = (1-ty, tx)
                var s = rotated ? new Vector2(1f - l.y, l.x) : l;
                float u = srcUv.xMin + s.x * srcUv.width;
                float v = srcUv.yMin + s.y * srcUv.height;
                GL.TexCoord2(u, v);
                GL.Vertex3(corners[i].x, corners[i].y, 0);
            }
            GL.End();
            GL.PopMatrix();
        }

        private static string GetCategory(string key, bool hasAlpha)
        {
            if (key.IndexOf("normal", StringComparison.Ordinal) >= 0) return "normal";
            if (key.IndexOf("mask", StringComparison.Ordinal) >= 0) return "grayscale";
            return hasAlpha ? "transparent" : "opaque";
        }

        private static AvatarTextureOptimizer.CompressionFormat GetCompression(
            AvatarTextureOptimizer component, string category)
        {
            var c = component.output.compression;
            switch (category)
            {
                case "normal": return c.normal;
                case "grayscale": return c.grayscale;
                case "transparent": return c.transparent;
                default: return c.opaque;
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int idx = path.LastIndexOf('/');
            string parent = idx > 0 ? path.Substring(0, idx) : "";
            string name = idx >= 0 ? path.Substring(idx + 1) : path;
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }
    }
}

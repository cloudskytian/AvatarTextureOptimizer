using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 应用阶段：生成图集贴图、重写网格 UV、重指向材质、压缩/MipStreaming 设置、
    /// 材质/贴图去重、AAO 兼容、移除组件、输出报告。
    ///
    /// Apply: build atlas textures, rewrite mesh UVs, re-point materials, compression/streaming,
    /// dedup, AAO compat, remove component, report.
    /// </summary>
    public class ATOApplier
    {
        private readonly nadena.dev.ndmf.BuildContext _ctx;
        private readonly ATOBuildData _data;
        private readonly AvatarTextureOptimizer _comp;

        // 原贴图 → 新贴图（缩放后/图集）引用替换表。Original texture -> replacement.
        private readonly Dictionary<Texture2D, Texture2D> _replacement = new Dictionary<Texture2D, Texture2D>();

        public ATOApplier(nadena.dev.ndmf.BuildContext ctx, ATOBuildData data)
        {
            _ctx = ctx;
            _data = data;
            _comp = data.component;
        }

        public void Run()
        {
            using var step = ATOLogger.Step("Write meshes, textures & materials");
            ATOLogger.Begin("stage.apply");

            // 1) 生成图集。Build atlases.
            BuildAtlases();
            ATOLogger.Report(0.3f);

            // 2) 处理非图集贴图（整贴图缩放 / 白名单 fallback）。Whole-texture / whitelist fallback.
            BuildStandaloneTextures();
            ATOLogger.Report(0.5f);

            // 3) 重写网格 UV。Rewrite mesh UVs.
            RewriteMeshes();
            ATOLogger.Report(0.7f);

            // 4) 重指向材质。Re-point materials.
            RepointMaterials();
            ATOLogger.Report(0.85f);

            // 5) 去重材质/贴图。Dedup materials & textures.
            DedupMaterials();

            // 6) AAO 兼容。AAO compatibility.
            ApplyAAOCompat();

            // 7) 移除组件。Remove self component.
            Object.DestroyImmediate(_comp);

            // 8) 报告。Report.
            WriteReport();

            ATOLogger.Report(1f);
            ATOLogger.EndProgress();
            ATOLogger.FlushReport();
            ATOLogger.Info(ATOLocalization.Tr("done"));
        }

        private void BuildAtlases()
        {
            foreach (var atlas in _data.atlases)
            {
                ATOLogger.ThrowIfCancelled();
                var tex = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, true, !atlas.group.sRGB);
                tex.name = atlas.name;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = atlas.group.filterMode;
                var colors = new Color[tex.width * tex.height];
                for (int i = 0; i < colors.Length; i++) colors[i] = new Color(0, 0, 0, 0);
                tex.SetPixels(colors);

                foreach (var island in atlas.islands)
                {
                    BlitIslandIntoTex(tex, island, atlas);
                }

                // pull-push 边缘外扩（无限外扩填充空白，透明 alpha 保持 0）。TODO: GPU pull-push.
                ApplyPullPushPadding(tex);

                tex.Apply(false, false);
                atlas.texture = tex;

                // 关联替换表。
                foreach (var island in atlas.islands)
                    _replacement[island.texture.texture] = tex;

                // 设置压缩/MipStreaming（best-effort）。
                ATOCompression.Apply(tex, atlas, _comp);

                // 注册为临时资产，随构建保存。
                _ctx.AssetSaver.SaveAsset(tex);

                ATOLogger.ReportDetail($"Atlas {tex.name}: {tex.width}x{tex.height}, islands={atlas.islands.Count}, " +
                                       $"utilization={Utilization(atlas):P1}");
            }
        }

        private float Utilization(ATOAtlas atlas)
        {
            long used = 0;
            foreach (var island in atlas.islands)
            {
                used += (long)(island.bounds.width * island.texture.width * island.packedScale.x) *
                        (long)(island.bounds.height * island.texture.height * island.packedScale.y);
            }
            return (float)used / (atlas.width * atlas.height);
        }

        private void BlitIslandIntoTex(Texture2D atlas, ATOIsland island, ATOAtlas a)
        {
            var src = ATOProcessor.ReadTextureLinear(island.texture.texture);
            var bounds = island.bounds;
            int iw = Mathf.Max(1, Mathf.RoundToInt(bounds.width * src.w * island.packedScale.x));
            int ih = Mathf.Max(1, Mathf.RoundToInt(bounds.height * src.h * island.packedScale.y));

            // 裁剪 + 缩放（线性 → 回 sRGB）。
            var crop = CropRegion(src, bounds);
            var scaled = ATOProcessor.Resample(crop.px, crop.w, crop.h, iw, ih, island.isNormalMap ? ATOProcessor.ResampleMode.Normal : ATOProcessor.ResampleMode.Color);

            int ox = Mathf.RoundToInt(island.packedUv.x);
            int oy = Mathf.RoundToInt(island.packedUv.y);
            bool sRGB = island.texture.sRGB;
            for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                {
                    int si = (y * iw + x) * 4;
                    float r = scaled.px[si], g = scaled.px[si + 1], b = scaled.px[si + 2], al = scaled.px[si + 3];
                    // 反预乘 + 反线性。
                    if (al > 1e-6f) { r /= al; g /= al; b /= al; }
                    if (sRGB) { r = ATOQualityMetrics.LinearToSRGB(r); g = ATOQualityMetrics.LinearToSRGB(g); b = ATOQualityMetrics.LinearToSRGB(b); }
                    atlas.SetPixel(ox + x, atlas.height - 1 - (oy + y), new Color(r, g, b, al));
                }
        }

        private void ApplyPullPushPadding(Texture2D tex)
        {
            // pull-push 边缘外扩：迭代到收敛（无限外扩近似），透明 alpha 保持 0。
            // 已知渗色问题（需求已确认"够用了"）；生产路径可换 GPU pull-push。
            // Pull-push padding: iterate to convergence (infinite dilation approx), transparent alpha stays 0.
            int w = tex.width, h = tex.height;
            var src = tex.GetPixels();
            var dst = new Color[src.Length];

            int maxIter = Mathf.Max(w, h) / 2 + 2; // 收敛上界（最远传播距离）
            for (int iter = 0; iter < maxIter; iter++)
            {
                bool changed = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (src[i].a > 0.01f) { dst[i] = src[i]; continue; }
                        // 8 邻域非空像素颜色均值（RGB 取均值，alpha 归零）。
                        float r = 0, g = 0, b = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                var c = src[ny * w + nx];
                                if (c.a > 0.01f) { r += c.r; g += c.g; b += c.b; cnt++; }
                            }
                        if (cnt > 0)
                        {
                            dst[i] = new Color(r / cnt, g / cnt, b / cnt, 0f);
                            changed = true;
                        }
                        else
                        {
                            dst[i] = src[i];
                        }
                    }

                var tmp = src; src = dst; dst = tmp;
                if (!changed) break;
            }

            tex.SetPixels(src);
            tex.Apply(false, false);
        }

        private void BuildStandaloneTextures()
        {
            // 白名单贴图：原样保留（跳过所有优化）。
            // 不生成图集时：整贴图缩放。Whole-texture scaling when atlas off.
            if (_comp.generateAtlas)
            {
                // 图集模式下：白名单/装不下的贴图原样保留，但更新引用（去重）。
                foreach (var e in _data.entries)
                {
                    if (e.IsDuplicate) _replacement[e.texture] = e.Canonical.texture;
                    else if (e.whitelisted) _replacement[e.texture] = e.texture; // 原样
                }
                return;
            }

            // 非图集模式：对每张贴图按最保守缩放整贴图缩放。
            foreach (var e in _data.entries)
            {
                if (e.IsDuplicate) { _replacement[e.texture] = e.Canonical.texture; continue; }
                if (e.whitelisted) { _replacement[e.texture] = e.texture; continue; }

                float maxScale = 1f;
                foreach (var island in _data.allIslands)
                    if (island.texture.Canonical == e)
                        maxScale = Mathf.Max(maxScale, island.packedScale.x, island.packedScale.y);

                if (maxScale >= 1f - 1e-4f) { _replacement[e.texture] = e.texture; continue; }

                var scaled = ScaleWholeTexture(e, maxScale);
                _replacement[e.texture] = scaled;
                ATOLogger.ReportDetail($"Scaled {e.texture.name} to {maxScale:P0} ({e.width}x{e.height} -> {scaled.width}x{scaled.height})");
            }
        }

        private Texture2D ScaleWholeTexture(ATOTextureEntry e, float scale)
        {
            int nw = Mathf.Max(1, Mathf.RoundToInt(e.width * scale));
            int nh = Mathf.Max(1, Mathf.RoundToInt(e.height * scale));
            var src = ATOProcessor.ReadTextureLinear(e.texture);
            var res = ATOProcessor.Resample(src.px, src.w, src.h, nw, nh, e.slots.Count > 0 && e.slots[0].isNormalMap ? ATOProcessor.ResampleMode.Normal : ATOProcessor.ResampleMode.Color);

            var tex = new Texture2D(nw, nh, TextureFormat.RGBA32, true, !e.sRGB);
            tex.name = "ATO_" + e.texture.name;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = e.filterMode;
            var cols = new Color[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int si = (y * nw + x) * 4;
                    float r = res.px[si], g = res.px[si + 1], b = res.px[si + 2], al = res.px[si + 3];
                    if (al > 1e-6f) { r /= al; g /= al; b /= al; }
                    if (e.sRGB) { r = ATOQualityMetrics.LinearToSRGB(r); g = ATOQualityMetrics.LinearToSRGB(g); b = ATOQualityMetrics.LinearToSRGB(b); }
                    cols[y * nw + x] = new Color(r, g, b, al);
                }
            tex.SetPixels(cols);
            tex.Apply(false, false);
            _ctx.AssetSaver.SaveAsset(tex);
            return tex;
        }

        private void RewriteMeshes()
        {
            if (_comp.generateAtlas == false) return; // 非图集模式不重写 UV

            // 按网格分组岛屿。Group islands by mesh+channel.
            var byMesh = new Dictionary<(Mesh, int), List<ATOIsland>>();
            foreach (var island in _data.allIslands)
            {
                if (island.atlas == null) continue;
                var key = (island.mesh, island.uvGroup.uvChannel);
                if (!byMesh.TryGetValue(key, out var list)) byMesh[key] = list = new List<ATOIsland>();
                list.Add(island);
            }

            foreach (var kv in byMesh)
            {
                var (mesh, channel) = kv.Key;
                var islands = kv.Value;

                // 克隆网格，避免修改共享资产。Clone mesh.
                var newMesh = Object.Instantiate(mesh);
                newMesh.name = mesh.name;

                var uv = new List<Vector2>();
                newMesh.GetUVs(channel, uv);
                var tris = newMesh.triangles;

                foreach (var island in islands)
                {
                    var atlas = island.atlas;
                    var bounds = island.bounds;
                    int iw = Mathf.Max(1, Mathf.RoundToInt(bounds.width * island.texture.width * island.packedScale.x));
                    int ih = Mathf.Max(1, Mathf.RoundToInt(bounds.height * island.texture.height * island.packedScale.y));

                    foreach (var t in island.triangles)
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = tris[t * 3 + k];
                            if (vi >= uv.Count) continue;
                            var p = uv[vi];
                            float u = (p.x - bounds.x) / bounds.width;
                            float v = (p.y - bounds.y) / bounds.height;
                            float nu = (island.packedUv.x + u * iw) / atlas.width;
                            float nv = (island.packedUv.y + v * ih) / atlas.height;
                            uv[vi] = new Vector2(nu, nv);
                        }
                }

                newMesh.SetUVs(channel, uv);
                _ctx.AssetSaver.SaveAsset(newMesh);
                // 更新渲染器引用。Assign to renderer.
                var renderer = islands[0].uvGroup.renderer;
                if (renderer is SkinnedMeshRenderer smr) smr.sharedMesh = newMesh;
                else if (renderer is MeshRenderer mr) mr.GetComponent<MeshFilter>().sharedMesh = newMesh;

                // 让 NDMF 不重算 UV 分布度量（我们已精确设置）。
                _ctx.SetEnableUVDistributionRecalculation(newMesh, false);
            }
        }

        private void RepointMaterials()
        {
            foreach (var slot in _data.allSlots)
            {
                var tex = slot.texture;
                if (!_replacement.TryGetValue(tex, out var newTex)) continue;
                if (newTex == tex) continue;
                var mat = slot.material;
                if (mat == null) continue;
                if (mat.GetTexture(slot.propertyName) == tex)
                    mat.SetTexture(slot.propertyName, newTex);
            }
        }

        private void DedupMaterials()
        {
            if (!_comp.dedupMaterials && !_comp.dedupTextures) return;
            new ATODedup(_ctx, _data).Run();
        }

        private void ApplyAAOCompat()
        {
            if (!ATOAAOBridge.Available)
            {
                ATOLogger.VerboseLog(ATOLocalization.Tr("warning.noAAO"));
                return;
            }
            // 对每个被重写 UV 的 SkinnedMeshRenderer：把原 UV 疏散到另一个通道并注册。
            foreach (var smr in _ctx.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                for (int ch = 0; ch < 8; ch++)
                {
                    if (!ATOAAOBridge.IsTexCoordUsed(smr, ch)) continue;
                    int saved = FindFreeChannel(smr, ch);
                    if (saved < 0) continue;
                    var mesh = smr.sharedMesh;
                    var uv = new List<Vector2>();
                    mesh.GetUVs(ch, uv);
                    mesh.SetUVs(saved, uv);
                    ATOAAOBridge.RegisterTexCoordEvacuation(smr, ch, saved);
                }
            }
        }

        private int FindFreeChannel(SkinnedMeshRenderer smr, int used)
        {
            var mesh = smr.sharedMesh;
            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == used) continue;
                var uv = new List<Vector2>();
                mesh.GetUVs(ch, uv);
                if (uv.Count == 0) return ch;
            }
            return -1;
        }

        private void WriteReport()
        {
            ATOLogger.ReportLine($"{ATOLocalization.Tr("report.atlases")}: {_data.atlases.Count}");
            ATOLogger.ReportLine($"{ATOLocalization.Tr("report.islands")}: {_data.allIslands.Count}");
            long origPx = 0, newPx = 0;
            foreach (var e in _data.entries)
            {
                if (e.IsDuplicate || e.whitelisted) continue;
                origPx += (long)e.width * e.height;
                if (_replacement.TryGetValue(e.texture, out var nt) && nt != null && nt != e.texture)
                    newPx += (long)nt.width * nt.height;
                else newPx += (long)e.width * e.height;
            }
            if (origPx > 0)
                ATOLogger.ReportLine($"{ATOLocalization.Tr("report.saved")}: {(1f - (float)newPx / origPx):P1}");
        }

        private static (float[] px, int w, int h) CropRegion((float[] px, int w, int h) src, Rect bounds)
        {
            int w = Mathf.Max(1, Mathf.RoundToInt(bounds.width * src.w));
            int h = Mathf.Max(1, Mathf.RoundToInt(bounds.height * src.h));
            int x0 = Mathf.Clamp(Mathf.RoundToInt(bounds.x * src.w), 0, src.w - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(bounds.y * src.h), 0, src.h - 1);
            var outPx = new float[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int si = ((Mathf.Clamp(y0 + y, 0, src.h - 1)) * src.w + Mathf.Clamp(x0 + x, 0, src.w - 1)) * 4;
                    int di = (y * w + x) * 4;
                    outPx[di] = src.px[si]; outPx[di + 1] = src.px[si + 1];
                    outPx[di + 2] = src.px[si + 2]; outPx[di + 3] = src.px[si + 3];
                }
            return (outPx, w, h);
        }
    }
}

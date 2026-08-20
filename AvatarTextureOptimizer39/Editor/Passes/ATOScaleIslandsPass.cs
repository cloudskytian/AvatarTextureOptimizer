// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Collections.Generic;
using AvatarTextureOptimizer.Editor.Analysis;
using AvatarTextureOptimizer.Editor.Core;
using AvatarTextureOptimizer.Editor.Quality;
using AvatarTextureOptimizer.Editor.UVIsland;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 6 — scale each UV island using the target-quality algorithm:
    /// binary-search the minimum uniform scale, then anisotropic per-axis refinement,
    /// with pixel-density clamping, pure-color short-circuit, and near-lossless skip.
    ///
    /// Pass 6 —— 用目标质量算法缩放每个 UV 岛：二分搜索最小均匀缩放，再各向异性双轴细化，
    /// 含像素密度钳制、纯色短路、近无损跳过。
    /// </summary>
    public sealed class ATOScaleIslandsPass : Pass<ATOScaleIslandsPass>
    {
        public override string DisplayName => "ATO: Scale UV islands / 缩放 UV 岛";

        private ATOBuildState _state;
        private ATOAnimationQueries _anim;
        private GameObject _root;

        protected override void Execute(BuildContext context)
        {
            _state = context.GetState<ATOBuildState>();
            if (_state.Component == null) return;
            _state.BeginStage("Scale UV islands / 缩放 UV 岛");

            var animCtx = context.ActivateExtensionContext<AnimatorServicesContext>();
            _anim = new ATOAnimationQueries(animCtx.AnimationIndex);
            _root = context.AvatarRootObject;

            using var _ = ATOLog.Time("Scale islands");

            bool nearLossless = _state.Quality.msSsim >= 0.999f;

            int processed = 0;
            foreach (var entry in _state.Islands)
            {
                _state.ThrowIfCancelled();
                ScaleIsland(entry, nearLossless);
                processed++;
            }

            ATOLog.Info($"Scaled {processed} islands. / 缩放了 {processed} 个岛。");
        }

        private void ScaleIsland(ATOUVIslandEntry entry, bool nearLossless)
        {
            // Skip if all textures whitelisted. 若全部贴图白名单则跳过。
            bool anyActive = false;
            bool anySkipped = false;
            foreach (var t in entry.Textures)
            {
                if (t == null) continue;
                if (t.SkipAll) anySkipped = true;
                else anyActive = true;
            }
            if (!anyActive) return;

            // Shares UV with a whitelisted texture → skip atlas-ization for this UV set.
            // 与白名单贴图共享 UV → 该 UV 组跳过图集化。
            if (anySkipped) entry.SkipAtlas = true;

            // Determine max texture resolution. 取最大贴图分辨率。
            int maxRes = 1;
            foreach (var t in entry.Textures)
                if (t != null) maxRes = Mathf.Max(maxRes, Mathf.Max(t.Width, t.Height));

            int pw = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.width * maxRes));
            int ph = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.height * maxRes));

            // Pixel density. 像素密度。
            // Include animated object-scale area factor. 计入动画缩放面积因子。
            string path = AnimationUtility.CalculateTransformPath(entry.Renderer.transform, _root.transform);
            float animFactor = _anim.GetMaxAnimatedAreaFactor(path);
            float worldArea = entry.Island.MaxArea * Mathf.Max(0.0001f, ScaleFactorSq(entry.Renderer)) * animFactor;
            float currentDensity = Mathf.Sqrt((pw * ph) / worldArea);
            float sDensityFloor = Mathf.Min(1f, (int)_state.Component.minPixelDensity / Mathf.Max(1f, currentDensity));

            // Near-lossless: skip scaling. 近无损：跳过缩放。
            if (nearLossless)
            {
                entry.UniformScale = 1f;
                entry.AnisoScale = Vector2.one;
                return;
            }

            // Pure-color short-circuit. 纯色短路。
            if (AllPureColor(entry, pw, ph))
            {
                float shortEdge = Mathf.Min(pw, ph);
                float target = Mathf.Min(4f, shortEdge);
                float s = target / shortEdge;
                s = Mathf.Max(s, sDensityFloor);
                entry.UniformScale = s;
                entry.AnisoScale = new Vector2(s, s);
                ATOLog.Verbose($"Pure-color island short-circuit scale={s:F3}. / 纯色岛短路 scale={s:F3}");
                return;
            }

            // Uniform binary search. 均匀二分搜索。
            float sUniform = BinarySearchScale(entry, pw, ph, sDensityFloor);

            // Anisotropic refinement. 各向异性细化。
            float sx = BinarySearchScale(entry, pw, ph, sDensityFloor, fixedY: sUniform, axisX: true);
            float sy = BinarySearchScale(entry, pw, ph, sDensityFloor, fixedX: sx, axisX: false);

            entry.UniformScale = sUniform;
            entry.AnisoScale = new Vector2(sx, sy);

            ATOLog.Verbose($"Island scale: uniform={sUniform:F3} aniso=({sx:F3},{sy:F3}). / " +
                           $"岛缩放 uniform={sUniform:F3} aniso=({sx:F3},{sy:F3})");
        }

        /// <summary>
        /// Binary search the minimum scale that passes all quality metrics.
        /// 二分搜索通过所有质量指标的最小缩放。
        /// </summary>
        private float BinarySearchScale(ATOUVIslandEntry entry, int pw, int ph, float floor,
            float fixedY = -1f, float fixedX = -1f, bool axisX = true)
        {
            float lo = floor, hi = 1f;

            for (int iter = 0; iter < 12; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                float sx = axisX ? mid : (fixedX >= 0 ? fixedX : mid);
                float sy = axisX ? (fixedY >= 0 ? fixedY : mid) : mid;

                if (Passes(entry, pw, ph, sx, sy)) hi = mid;
                else lo = mid;
            }

            return hi;
        }

        /// <summary>Evaluate whether a scale candidate passes for ALL textures. 评估候选是否全部通过。</summary>
        private bool Passes(ATOUVIslandEntry entry, int pw, int ph, float sx, float sy)
        {
            foreach (var tex in entry.Textures)
            {
                if (tex == null || tex.SkipAll) continue;

                float tw = tex.Width, th = tex.Height;
                int tpw = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.width * tw));
                int tph = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.height * th));

                int scaledW = Mathf.Max(1, Mathf.RoundToInt(tpw * sx));
                int scaledH = Mathf.Max(1, Mathf.RoundToInt(tph * sy));

                var (original, mask) = CropIsland(entry, tex, tpw, tph);
                if (original == null) continue;

                var usages = ResolveUsages(tex);

                var result = ATOQualityEvaluator.Evaluate(
                    _state.Quality, original, tpw, tph, scaledW, scaledH, usages);

                if (!result.Passed) return false;
            }

            return true;
        }

        /// <summary>
        /// Crop the island region from the texture (linear pixels) plus its coverage mask.
        /// 从贴图裁剪岛区域（线性像素）及其覆盖掩码。
        /// </summary>
        private (Color[], bool[]) CropIsland(ATOUVIslandEntry entry, ATOTextureRecord tex, int pw, int ph)
        {
            if (tex.Pixels == null) return (null, null);

            int x0 = Mathf.Clamp(Mathf.FloorToInt(entry.NormalizedBounds.xMin * tex.Width), 0, tex.Width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(entry.NormalizedBounds.yMin * tex.Height), 0, tex.Height - 1);
            int w = Mathf.Clamp(pw, 1, tex.Width - x0);
            int h = Mathf.Clamp(ph, 1, tex.Height - y0);

            var cropped = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    cropped[y * w + x] = tex.Pixels[(y0 + y) * tex.Width + (x0 + x)];

            // Coverage mask. 覆盖掩码。
            var (uvs, tris) = GetUvData(entry);
            bool[] mask = null;
            if (uvs != null)
                mask = ATOTriangleRasterizer.Rasterize(uvs, tris, entry.Island.Triangles,
                    entry.NormalizedBounds, w, h);

            return (cropped, mask);
        }

        private (Vector2[], int[]) GetUvData(ATOUVIslandEntry entry)
        {
            var mesh = entry.Renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                : entry.Renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh : null;
            if (mesh == null) return (null, null);

            var uvs = new List<Vector2>();
            if (entry.UVChannel == 0) return (mesh.uv, mesh.GetTriangles(entry.SubMeshIndex));
            if (entry.UVChannel == 1) return (mesh.uv2, mesh.GetTriangles(entry.SubMeshIndex));
            if (!mesh.GetUVs(entry.UVChannel, uvs)) return (null, null);
            return (uvs.ToArray(), mesh.GetTriangles(entry.SubMeshIndex));
        }

        /// <summary>Resolve all usages (opaque/cutout/blend, cutoff) of a texture across materials.</summary>
        private List<ATOTextureUsage> ResolveUsages(ATOTextureRecord tex)
        {
            var usages = new List<ATOTextureUsage>();

            foreach (var matRec in _state.Materials.Values)
            {
                bool references = false;
                foreach (var b in matRec.Bindings)
                    if (b.Texture == tex.Texture) { references = true; break; }
                if (!references) continue;

                string path = matRec.Renderer != null
                    ? AnimationUtility.CalculateTransformPath(matRec.Renderer.transform, _root.transform)
                    : "";
                usages.Add(ClassifyUsage(matRec.Material, tex, path));
            }

            if (usages.Count == 0)
                usages.Add(new ATOTextureUsage { opaque = !tex.HasAlpha });

            return usages;
        }

        private ATOTextureUsage ClassifyUsage(Material mat, ATOTextureRecord tex, string path)
        {
            var usage = new ATOTextureUsage();

            switch (tex.Category)
            {
                case ATOTextureCategory.Normal:
                    usage.isNormal = true;
                    usage.opaque = true;
                    return usage;
                case ATOTextureCategory.Mask:
                    usage.isGrayscale = true;
                    usage.grayChannels = 0xF; // assume RGBA used. 假设 RGBA 全用。
                    usage.opaque = true;
                    return usage;
            }

            bool alphaTest = mat.IsKeywordEnabled("_ALPHATEST_ON") ||
                             mat.renderQueue >= (int)RenderQueue.AlphaTest && mat.renderQueue < (int)RenderQueue.Transparent;
            bool alphaBlend = mat.IsKeywordEnabled("_ALPHABLEND_ON") ||
                              mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                              mat.renderQueue >= (int)RenderQueue.Transparent;

            // Animation may modify render mode / cutoff → take the strictest.
            // 动画可能修改渲染模式/Cutoff → 取最严苛。
            if (!string.IsNullOrEmpty(path) && _anim != null && _anim.IsRenderModeAnimated(path))
            {
                // Conservatively treat as both cutout and blend. 保守地视为 cutout 与 blend 并存。
                if (tex.HasAlpha)
                {
                    usage.cutout = true;
                    usage.blend = true;
                }
            }

            if (tex.HasAlpha && (alphaTest || usage.cutout))
            {
                usage.cutout = true;
                float baseCutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff")
                    : mat.HasProperty("_AlphaCutoff") ? mat.GetFloat("_AlphaCutoff") : 0.5f;
                usage.cutoff = string.IsNullOrEmpty(path) || _anim == null
                    ? baseCutoff
                    : Mathf.Max(baseCutoff, _anim.GetMaxMaterialFloat(path, "_Cutoff", baseCutoff));
            }

            if (tex.HasAlpha && (alphaBlend || usage.blend))
            {
                usage.blend = true;
            }

            if (!usage.cutout && !usage.blend)
                usage.opaque = true;

            return usage;
        }

        /// <summary>
        /// True if the island region is a single uniform color in every non-skipped texture.
        /// 每个未跳过贴图的岛区域是否都是单一纯色。
        /// </summary>
        private bool AllPureColor(ATOUVIslandEntry entry, int pw, int ph)
        {
            bool any = false;
            foreach (var tex in entry.Textures)
            {
                if (tex == null || tex.SkipAll || tex.Pixels == null) continue;
                any = true;

                int tpw = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.width * tex.Width));
                int tph = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.height * tex.Height));
                var (cropped, _) = CropIsland(entry, tex, tpw, tph);
                if (cropped == null || cropped.Length == 0) continue;

                var first = cropped[0];
                foreach (var c in cropped)
                {
                    if (!Approx(c, first)) return false;
                }
            }
            return any;
        }

        private static bool Approx(Color a, Color b)
        {
            return Mathf.Approximately(a.r, b.r) && Mathf.Approximately(a.g, b.g) &&
                   Mathf.Approximately(a.b, b.b) && Mathf.Approximately(a.a, b.a);
        }

        private static float ScaleFactorSq(Renderer r)
        {
            var s = r.transform.lossyScale;
            float m = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            return m * m;
        }
    }
}

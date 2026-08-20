// ATOQualityEvaluator.cs — 质量评估编排器 / Quality evaluation orchestrator.
// 说明：为每个（岛引用 × 候选尺寸）构建评估输入并调度 GPU/Burst 评估；缓存源裁剪缓冲与
// 纯色检测结果；按贴图释放像素缓存控制内存。评估覆盖引用该贴图的所有材质用途（取最严苛）。
// Note: builds evaluation inputs per (island ref × candidate size) and dispatches GPU/Burst evaluation;
// caches source crop buffers and solid-color results; releases per-texture pixel caches to bound memory.
// All material usages referencing the texture are evaluated (strictest wins).

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>质量评估编排器。/ Quality evaluation orchestrator.</summary>
    internal sealed class ATOQualityEvaluator : IDisposable
    {
        private readonly ATOGpuMetrics _gpu;
        private readonly ATOSourceCache _cache = new ATOSourceCache();
        private readonly Dictionary<ATOIslandRef, NativeArray<float4>> _crops = new Dictionary<ATOIslandRef, NativeArray<float4>>();
        private readonly Dictionary<ATOIslandRef, bool> _solid = new Dictionary<ATOIslandRef, bool>();
        private readonly Dictionary<Texture2D, ATOIslandCrop.NormalEncoding> _normalEnc = new Dictionary<Texture2D, ATOIslandCrop.NormalEncoding>();
        private readonly Dictionary<Texture2D, bool> _isSrgb = new Dictionary<Texture2D, bool>();
        private long _totalEvaluations;

        public long TotalEvaluations => _totalEvaluations;

        /// <summary>GPU 度量器（供合成器 pull-push 使用）。/ GPU metrics (for compositor pull-push).</summary>
        public ATOGpuMetrics Gpu => _gpu;

        public ATOQualityEvaluator()
        {
            _gpu = new ATOGpuMetrics();
            if (_gpu.Available) ATOLog.Info("GPU quality evaluation enabled. (GPU 质量评估已启用)");
            else ATOLog.Info("GPU unavailable; using Burst CPU evaluation. (GPU 不可用，使用 Burst CPU 评估)");
        }

        /// <summary>获取引用的源裁剪缓冲（懒加载 + 缓存）。/ Get a ref's source crop buffer (lazy + cached).</summary>
        public NativeArray<float4> GetSourceCrop(ATOIslandRef r)
        {
            if (_crops.TryGetValue(r, out var crop)) return crop;
            var premult = (r.usages.Count > 0 && (r.AlphaFlags() & ATOAlphaUsage.Blend) != 0);
            var enc = GetNormalEncoding(r.texture);
            var srgb = GetIsSrgb(r.texture, r.usages);
            crop = ATOIslandCrop.LoadCrop(_cache, r.texture, r.cropRect, srgb, premult, enc, Allocator.Persistent);
            _crops[r] = crop;
            _solid[r] = ATOIslandCrop.TryGetSolidColor(crop, out _);
            return crop;
        }

        /// <summary>引用是否纯色。/ Whether the ref's crop is solid.</summary>
        public bool IsSolid(ATOIslandRef r)
        {
            GetSourceCrop(r);
            return _solid[r];
        }

        /// <summary>获取纯色颜色。/ Get the solid color.</summary>
        public float4 GetSolidColor(ATOIslandRef r)
        {
            var crop = GetSourceCrop(r);
            ATOIslandCrop.TryGetSolidColor(crop, out var c);
            return c;
        }

        private ATOIslandCrop.NormalEncoding GetNormalEncoding(Texture2D texture)
        {
            if (_normalEnc.TryGetValue(texture, out var enc)) return enc;
            enc = ATOIslandCrop.NormalEncoding.RGB;
            var path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path))
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null && importer.textureType == TextureImporterType.NormalMap)
                    enc = ATOIslandCrop.NormalEncoding.DXT5nm;
            }
            _normalEnc[texture] = enc;
            return enc;
        }

        private bool GetIsSrgb(Texture2D texture, List<ATOTextureUsage> usages)
        {
            if (_isSrgb.TryGetValue(texture, out var v)) return v;
            v = ATOAvatarScanner.GetIsSRGB(texture);
            _isSrgb[texture] = v;
            return v;
        }

        /// <summary>
        /// 评估（引用 × 候选尺寸）。候选尺寸 = 从源裁剪缩放到 (w,h) 再双线性上采样回原尺寸比较。
        /// Evaluate (ref × candidate size): resize the source crop to (w,h), bilinear-upsample back, compare.
        /// </summary>
        public ATOEvalResult Evaluate(ATOIslandRef r, int w, int h, ATOQualityParams thresholds)
        {
            _totalEvaluations++;
            var source = GetSourceCrop(r);

            // Cutoff 采样（各用途逐一评估，取最严苛）/ cutoff samples (all usages evaluated, strictest wins)
            var samples = new List<float>();
            foreach (var u in r.usages)
            {
                if ((u.alphaUsage & ATOAlphaUsage.Cutout) == 0) continue;
                if (u.cutoffSamples != null)
                    foreach (var c in u.cutoffSamples) samples.Add(c);
            }
            if (samples.Count == 0) samples.Add(0.5f);
            var cutoffs = new NativeArray<float>(samples.ToArray(), Allocator.Temp);

            var input = new ATOEvalInput
            {
                source = source,
                srcW = r.cropRect.width,
                srcH = r.cropRect.height,
                dstW = Math.Max(1, w),
                dstH = Math.Max(1, h),
                premultiplied = (r.AlphaFlags() & ATOAlphaUsage.Blend) != 0,
                normalMap = r.category == ATOScaleCategory.Normal,
                grayEval = r.category == ATOScaleCategory.Mask,
                alphaFlags = r.AlphaFlags(),
                cutoffs = cutoffs,
                thresholds = thresholds,
            };
            try
            {
                return _gpu.Evaluate(input, Allocator.Temp);
            }
            finally
            {
                cutoffs.Dispose();
            }
        }

        /// <summary>释放贴图的像素缓存与相关裁剪缓冲（贴图处理完毕后调用）。/ Release a texture's pixel cache & crops (after the texture is done).</summary>
        public void ReleaseTexture(Texture2D texture)
        {
            var toRemove = new List<ATOIslandRef>();
            foreach (var kv in _crops)
            {
                if (kv.Key.texture == texture)
                {
                    if (kv.Value.IsCreated) kv.Value.Dispose();
                    toRemove.Add(kv.Key);
                }
            }
            foreach (var key in toRemove)
            {
                _crops.Remove(key);
                _solid.Remove(key);
            }
            _cache.Release(texture);
        }

        public void Dispose()
        {
            foreach (var kv in _crops)
                if (kv.Value.IsCreated) kv.Value.Dispose();
            _crops.Clear();
            _solid.Clear();
            _cache.Dispose();
            _gpu.Dispose();
        }
    }

    /// <summary>ATOIslandRef 的扩展辅助。/ Helpers for ATOIslandRef.</summary>
    internal static class ATOIslandRefExtensions
    {
        /// <summary>聚合的透明度模式位标志。/ Aggregated alpha mode flags.</summary>
        public static ATOAlphaUsage AlphaFlags(this ATOIslandRef r)
        {
            ATOAlphaUsage flags = ATOAlphaUsage.Opaque;
            foreach (var u in r.usages) flags |= u.alphaUsage;
            return flags;
        }


    }
}

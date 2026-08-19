using System.Collections.Generic;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer
{
    /// <summary>
    /// Public extension surface for advanced users and third-party developers.
    /// 给高级用户和第三方开发者的扩展入口。
    /// </summary>
    public static class ATOApi
    {
        internal static readonly List<IATOShaderAnalyzer> ShaderAnalyzers = new List<IATOShaderAnalyzer>();
        internal static readonly List<IATOTextureClassifier> TextureClassifiers = new List<IATOTextureClassifier>();
        internal static readonly List<IATOQualityMetric> QualityMetrics = new List<IATOQualityMetric>();
        internal static readonly List<IATOPacker> Packers = new List<IATOPacker>();

        public static void RegisterShaderAnalyzer(IATOShaderAnalyzer analyzer)
        {
            if (analyzer != null && !ShaderAnalyzers.Contains(analyzer))
                ShaderAnalyzers.Add(analyzer);
        }

        public static void UnregisterShaderAnalyzer(IATOShaderAnalyzer analyzer)
        {
            ShaderAnalyzers.Remove(analyzer);
        }

        public static void RegisterTextureClassifier(IATOTextureClassifier classifier)
        {
            if (classifier != null && !TextureClassifiers.Contains(classifier))
                TextureClassifiers.Add(classifier);
        }

        public static void UnregisterTextureClassifier(IATOTextureClassifier classifier)
        {
            TextureClassifiers.Remove(classifier);
        }

        public static void RegisterQualityMetric(IATOQualityMetric metric)
        {
            if (metric != null && !QualityMetrics.Contains(metric))
                QualityMetrics.Add(metric);
        }

        public static void UnregisterQualityMetric(IATOQualityMetric metric)
        {
            QualityMetrics.Remove(metric);
        }

        public static void RegisterPacker(IATOPacker packer)
        {
            if (packer != null && !Packers.Contains(packer))
                Packers.Add(packer);
        }

        public static void UnregisterPacker(IATOPacker packer)
        {
            Packers.Remove(packer);
        }
    }

    /// <summary>
    /// Analyzes a shader/material and reports how a texture property is sampled.
    /// 分析着色器/材质，报告贴图属性如何被采样。
    /// </summary>
    public interface IATOShaderAnalyzer
    {
        /// <summary>Lower runs first. / 越小越先跑。</summary>
        int Priority { get; }

        bool TryAnalyze(Material material, string textureProperty, out ATOTextureSlotInfo info);
    }

    /// <summary>
    /// Classifies a texture into a semantic category after looking at pixels and usage.
    /// 结合像素和用途把贴图分到语义类别。
    /// </summary>
    public interface IATOTextureClassifier
    {
        int Priority { get; }
        bool TryClassify(Texture2D texture, IReadOnlyList<ATOTextureSlotInfo> usages, out ATOTextureCategory category);
    }

    /// <summary>
    /// Extra quality metric. Return false to reject the candidate scale.
    /// 额外质量指标。返回 false 则否决该缩放候选。
    /// </summary>
    public interface IATOQualityMetric
    {
        string Id { get; }
        bool Evaluate(
            Color[] original,
            Color[] upsampled,
            int width,
            int height,
            ATOTextureCategory category,
            ATOQualityParameters thresholds,
            out float score);
    }

    /// <summary>
    /// Replacement island packer. Returning false falls back to the built-in BLF packer.
    /// 可替换的岛装箱器。返回 false 则回退到内置 BLF。
    /// </summary>
    public interface IATOPacker
    {
        string Id { get; }
        bool TryPack(ATOPackRequest request, out ATOPackResult result);
    }

    /// <summary>
    /// One texture property on one material, bound to a mesh UV channel.
    /// 某个材质上的一个贴图属性，绑定到网格的某一路 UV。
    /// </summary>
    public sealed class ATOTextureSlotInfo
    {
        public Material material;
        public Renderer renderer;
        public int submeshIndex;
        public string propertyName;
        public Texture2D texture;
        public int uvChannel;
        public ATOTextureCategory category = ATOTextureCategory.Unknown;
        public ATOAlphaMode alphaMode = ATOAlphaMode.Opaque;
        public float cutoff = 0.5f;
        public ColorSpace colorSpace = ColorSpace.Gamma;
        public FilterMode filterMode = FilterMode.Bilinear;
        public bool hasNormalCompanion;
        public bool hasMaskCompanion;
        public bool eligible = true;
        public string ineligibleReason;
        public bool isSpecialPurpose;
        public bool hasTransform;
    }

    public sealed class ATOPackRequest
    {
        public int atlasWidth;
        public int atlasHeight;
        public int padding;
        public IReadOnlyList<ATOPackIsland> islands;
        public bool allowRotation = true;
    }

    public sealed class ATOPackIsland
    {
        public int id;
        public int width;
        public int height;
        public ulong[] bitmask;
        public int maskWidth;
        public int maskHeight;
    }

    public sealed class ATOPackResult
    {
        public bool success;
        public ATOPackedIsland[] placements;
        public float utilization;
    }

    public struct ATOPackedIsland
    {
        public int id;
        public int x;
        public int y;
        public bool rotated;
    }
}

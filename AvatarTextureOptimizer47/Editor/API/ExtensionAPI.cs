using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.API
{
    /// <summary>EN: Stable extension stages exposed to third-party NDMF tools. ZH: 向第三方 NDMF 工具公开的稳定扩展阶段。</summary>
    public enum AtoExtensionStage { BeforeAnalysis, AfterAnalysis, BeforeAtlas, AfterBake }

    /// <summary>EN: Result supplied by a custom shader texture-property analyzer. ZH: 自定义 Shader 贴图属性分析器返回的结果。</summary>
    public struct AtoTextureUsageDescriptor
    {
        public TextureSemantic semantic;
        public int uvChannel;
        public int usedChannelMask;
        public bool safe;
        public string unsafeReason;
    }

    /// <summary>EN: Adds shader-specific UV semantics without replacing ATO core code. ZH: 无需替换 ATO 核心代码即可添加 Shader 特定 UV 语义。</summary>
    public interface IAtoTexturePropertyAnalyzer
    {
        bool TryAnalyze(Material material, string propertyName, Texture2D texture, out AtoTextureUsageDescriptor descriptor);
    }

    /// <summary>EN: Adds a conservative quality veto to every candidate size. ZH: 为每个候选尺寸添加保守质量否决条件。</summary>
    public interface IAtoIslandQualityConstraint
    {
        bool Accept(Texture2D source, Rect sourceUv, Vector2Int candidateSize, TextureSemantic semantic,
            QualityThresholds thresholds);
    }

    /// <summary>EN: Observes build stages; implementations must not retain cloned avatar objects after the call. ZH: 观察构建阶段；实现不得在调用后持有克隆 Avatar 对象。</summary>
    public interface IAtoBuildStageExtension
    {
        void OnStage(AtoExtensionStage stage, GameObject avatarRoot);
    }

    /// <summary>EN: Post-processes a generated texture before compression. ZH: 在压缩前后处理生成贴图。</summary>
    public interface IAtoGeneratedTexturePostprocessor
    {
        void Process(Texture2D generatedTexture, TextureSemantic semantic);
    }

    /// <summary>EN: Thread-safe process-local registry for optional third-party extensions. ZH: 线程安全、进程内的可选第三方扩展注册表。</summary>
    public static class AtoExtensionRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<object> Extensions = new List<object>();

        public static IDisposable Register(object extension)
        {
            if (extension == null) throw new ArgumentNullException(nameof(extension));
            lock (Gate) Extensions.Add(extension);
            return new Registration(extension);
        }

        internal static T[] Get<T>()
        {
            lock (Gate) return Extensions.OfType<T>().ToArray();
        }

        internal static void Stage(AtoExtensionStage stage, GameObject root)
        {
            foreach (var extension in Get<IAtoBuildStageExtension>())
            {
                try { extension.OnStage(stage, root); }
                catch (Exception ex) { Debug.LogWarning($"[ATO] Build extension {extension.GetType().FullName} failed at {stage}: {ex.Message}"); }
            }
        }

        internal static void Postprocess(Texture2D texture, TextureSemantic semantic)
        {
            foreach (var extension in Get<IAtoGeneratedTexturePostprocessor>())
            {
                try { extension.Process(texture, semantic); }
                catch (Exception ex) { Debug.LogWarning($"[ATO] Texture postprocessor {extension.GetType().FullName} failed: {ex.Message}", texture); }
            }
        }

        private sealed class Registration : IDisposable
        {
            private object _extension;
            public Registration(object extension) { _extension = extension; }
            public void Dispose()
            {
                lock (Gate) { if (_extension != null) Extensions.Remove(_extension); _extension = null; }
            }
        }
    }
}

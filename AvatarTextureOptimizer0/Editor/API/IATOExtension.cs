using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.API
{
    // Extensions must be deterministic and may veto unsafe processing. BeforeAnalysis may tune Settings; the
    // pipeline sanitizes them afterwards. BeforeCommit receives a detached read snapshot, so edits there are ignored.
    // 扩展必须确定性执行并可否决不安全处理；BeforeAnalysis 可调整设置，BeforeCommit 的设置是只读语义快照。
    public interface IATOExtension
    {
        int Priority { get; }
        void BeforeAnalysis(ATOExtensionContext context);
        void ClassifyTexture(ATOTextureClassificationContext context);
        void BeforeCommit(ATOExtensionContext context);
    }

    public sealed class ATOExtensionContext
    {
        public GameObject AvatarRoot { get; internal set; }
        public AvatarTextureOptimizer Component { get; internal set; }
        public ATOOptimizationSettings Settings { get; internal set; }
        public IList<string> Warnings { get; internal set; }
    }

    /// <summary>
    /// Mutable texture classification proposal. Built-in unsafe decisions and earlier extension vetoes are monotonic.
    /// UnsupportedComposite forces fallback whenever the material uses surface alpha, and TextureAlpha always produces
    /// an alpha-preserving color output even if Kind is proposed as ColorOpaque.
    /// / 可变贴图分类提案；内建不安全结论与扩展 veto 不可清除，表面 Alpha 始终使用保 Alpha 输出。
    /// </summary>
    public sealed class ATOTextureClassificationContext
    {
        public Material Material { get; internal set; }
        public string PropertyName { get; internal set; }
        public Texture2D Texture { get; internal set; }
        public ATOTextureKind Kind { get; set; }
        public ATOSurfaceAlphaUsage SurfaceAlphaUsage { get; set; }
        public int UvChannel { get; set; }
        public bool RejectAsUnsafe { get; set; }
        public string RejectionReason { get; set; }
    }
}

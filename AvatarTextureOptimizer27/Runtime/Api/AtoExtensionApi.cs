using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Hooks for advanced users / third-party tools.
    /// 高级用户与第三方扩展钩子。
    /// </summary>
    public static class AtoExtensionApi
    {
        public static event Action<IAtoContext> BeforeAnalyze;
        public static event Action<IAtoContext> AfterAnalyze;
        public static event Action<IAtoContext> BeforePack;
        public static event Action<IAtoContext> AfterPack;
        public static event Action<IAtoContext> AfterApply;

        public static void RaiseBeforeAnalyze(IAtoContext ctx) => BeforeAnalyze?.Invoke(ctx);
        public static void RaiseAfterAnalyze(IAtoContext ctx) => AfterAnalyze?.Invoke(ctx);
        public static void RaiseBeforePack(IAtoContext ctx) => BeforePack?.Invoke(ctx);
        public static void RaiseAfterPack(IAtoContext ctx) => AfterPack?.Invoke(ctx);
        public static void RaiseAfterApply(IAtoContext ctx) => AfterApply?.Invoke(ctx);
    }

    public interface IAtoContext
    {
        GameObject AvatarRoot { get; }
        IReadOnlyList<UvGroup> Groups { get; }
        AtoPlatformSettings Settings { get; }
    }

    public sealed class AtoContext : IAtoContext
    {
        public GameObject AvatarRoot { get; set; }
        public IReadOnlyList<UvGroup> Groups { get; set; }
        public AtoPlatformSettings Settings { get; set; }
    }
}

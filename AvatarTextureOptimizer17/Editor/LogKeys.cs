// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// LogKeys.cs — 日志/警告 i18n 键 / Log & warning i18n keys
//
// 需求: 必要信息输出到 ndmf 控制台；日志 [ATO] 前缀。
// 说明: 警告文案走 i18n（en/zh-CN），键见 i18n/*.json。
// ============================================================================
using System;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>i18n 键常量 / i18n key constants</summary>
    public static class LogKeys
    {
        // 警告 / warnings
        public const string UnpackableIsland = "warnings.unpackableIsland";
        public const string OobRepeat = "warnings.oobRepeat";
        public const string AnimatedSt = "warnings.animatedSt";
        public const string UnknownShader = "warnings.unknownShader";
        public const string GrayscaleFallback = "warnings.grayscaleFallback";
        public const string AlphaFallback = "warnings.alphaFallback";
        public const string NpotFormat = "warnings.npotFormat";
        public const string Decal = "warnings.decal";
        public const string UvTransform = "warnings.uvTransform";
        public const string NoAao = "warnings.noAao";
        public const string NormalRetarget = "warnings.normalRetarget";
    }

    /// <summary>日志格式化助手 / Log formatting helpers</summary>
    public static class LogFmt
    {
        /// <summary>
        /// 带 [Avatar] 上下文的 i18n 警告 / i18n warning with avatar context prefix.
        /// </summary>
        public static string Warn(string key, params object[] args)
        {
            var avatar = Log.Stage.Length > 0 ? Log.Stage : "";
            var msg = I18n.T(key, args);
            return avatar.Length > 0 ? $"[{avatar}] {msg}" : msg;
        }
    }
}

// SPDX-License-Identifier: MIT
// EN: NDMF error report integration.
// ZH: NDMF 错误报告集成。

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using Net.Fosa.AvatarTextureOptimizer.Editor.Localization;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// EN: A localized error/warning surfaced in the NDMF error report window.
    /// ZH: 在 NDMF 错误报告窗口中显示的本地化错误/警告。
    /// </summary>
    public sealed class AtoError : SimpleError
    {
        private readonly ErrorSeverity _severity;
        private readonly string _titleKey;
        private readonly string[] _subst;

        /// <summary>EN: Creates an error with the given key and substitutions. ZH: 用给定的键与替换参数创建错误。</summary>
        public AtoError(ErrorSeverity severity, string titleKey, params string[] subst)
        {
            _severity = severity;
            _titleKey = titleKey;
            _subst = subst ?? Array.Empty<string>();
        }

        /// <inheritdoc/>
        public override Localizer Localizer => AtoLocalizer.Localizer;
        /// <inheritdoc/>
        public override string TitleKey => _titleKey;
        /// <inheritdoc/>
        public override string[] TitleSubst => _subst;
        /// <inheritdoc/>
        public override string[] DetailsSubst => _subst;
        /// <inheritdoc/>
        public override ErrorSeverity Severity => _severity;
    }

    /// <summary>
    /// EN: Convenience helpers that log to the console and to the NDMF report at the same time, so a
    ///     user never has to look in two places.
    /// ZH: 同时写入控制台与 NDMF 报告的便捷方法，用户无需在两个地方查找信息。
    /// </summary>
    public static class AtoReporting
    {
        /// <summary>EN: Reports a non fatal problem. ZH: 报告一个非致命问题。</summary>
        public static void Warn(string stage, string key, UnityObject context, params string[] subst)
        {
            var err = new AtoError(ErrorSeverity.NonFatal, key, subst);
            if (context != null) err.AddReference(ObjectRegistry.GetReference(context));
            ErrorReport.ReportError(err);
            AtoLog.Warning(stage, $"{key} {string.Join(" | ", subst)}{(context != null ? $" @ {context.name}" : "")}");
        }

        /// <summary>EN: Reports a fatal problem that stops the build. ZH: 报告一个会中止构建的致命问题。</summary>
        public static void Fatal(string stage, string key, UnityObject context, params string[] subst)
        {
            var err = new AtoError(ErrorSeverity.Error, key, subst);
            if (context != null) err.AddReference(ObjectRegistry.GetReference(context));
            ErrorReport.ReportError(err);
            AtoLog.Error(stage, $"{key} {string.Join(" | ", subst)}");
        }

        /// <summary>EN: Reports an informational entry. ZH: 报告一条信息。</summary>
        public static void Info(string stage, string key, UnityObject context, params string[] subst)
        {
            var err = new AtoError(ErrorSeverity.Information, key, subst);
            if (context != null) err.AddReference(ObjectRegistry.GetReference(context));
            ErrorReport.ReportError(err);
            AtoLog.Info(stage, $"{key} {string.Join(" | ", subst)}");
        }
    }
}

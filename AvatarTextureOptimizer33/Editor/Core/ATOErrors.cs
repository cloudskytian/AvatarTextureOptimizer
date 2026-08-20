// SPDX-License-Identifier: MIT
// EN: NDMF error report integration.
// ZH: 与 NDMF 报告系统的集成。

using System;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: A localised error/warning shown in the NDMF console.
    /// ZH: 在 NDMF 控制台中显示的本地化错误/警告。
    /// </summary>
    public sealed class ATOError : SimpleError
    {
        private readonly ErrorSeverity _severity;
        private readonly string _titleKey;
        private readonly string[] _subst;

        public ATOError(ErrorSeverity severity, string titleKey, params string[] subst)
        {
            _severity = severity;
            _titleKey = titleKey;
            _subst = subst ?? Array.Empty<string>();
        }

        public override Localizer Localizer => ATOL10n.Localizer;
        public override string TitleKey => _titleKey;
        public override string[] TitleSubst => _subst;
        public override string DetailsKey => _titleKey + ":description";
        public override string[] DetailsSubst => _subst;
        public override ErrorSeverity Severity => _severity;

        /// <summary>EN: Fluent reference attachment. ZH: 链式添加引用对象。</summary>
        public ATOError With(UnityEngine.Object obj)
        {
            if (obj != null) AddReference(ObjectRegistry.GetReference(obj));
            return this;
        }
    }

    /// <summary>
    /// EN: Helper to report warnings/errors both to the NDMF console and the ATO log.
    /// ZH: 同时向 NDMF 控制台与 ATO 日志报告警告/错误的辅助类。
    /// </summary>
    public sealed class ATOReporter
    {
        private readonly ATOLog _log;

        public ATOReporter(ATOLog log)
        {
            _log = log;
        }

        public void Warn(string key, UnityEngine.Object context, params string[] subst)
        {
            _log.Warning("report", ATOL10n.Tr(key, subst) + (context != null ? $" ({context.name})" : ""));
            try
            {
                ErrorReport.ReportError(new ATOError(ErrorSeverity.NonFatal, key, subst).With(context));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{ATOLog.Prefix}[report] could not queue warning: {e.Message}");
            }
        }

        public void Error(string key, UnityEngine.Object context, params string[] subst)
        {
            _log.Error("report", ATOL10n.Tr(key, subst));
            try
            {
                ErrorReport.ReportError(new ATOError(ErrorSeverity.Error, key, subst).With(context));
            }
            catch (Exception e)
            {
                Debug.LogError($"{ATOLog.Prefix}[report] could not queue error: {e.Message}");
            }
        }
    }
}

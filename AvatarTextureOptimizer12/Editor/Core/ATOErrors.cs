// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - NDMF error/warning objects.
// AvatarTextureOptimizer (ATO) - NDMF 错误/警告对象。

using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using Net.Fosa.AvatarTextureOptimizer.Editor.Localization;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// EN: All ATO diagnostics funnel through here so that they are localized and grouped consistently
    ///     inside the NDMF console.
    /// ZH: 所有 ATO 诊断信息都经由此处发出，保证在 NDMF 控制台里本地化与分组一致。
    /// </summary>
    internal sealed class ATOError : SimpleError
    {
        private readonly string[] _subst;

        public ATOError(ErrorSeverity severity, string key, params object[] args)
        {
            Severity = severity;
            TitleKey = key;

            var subst = new string[args?.Length ?? 0];
            for (int i = 0; i < subst.Length; i++)
            {
                var a = args[i];
                if (a == null) { subst[i] = "<null>"; continue; }
                if (a is UnityEngine.Object uo)
                {
                    var reference = ObjectRegistry.GetReference(uo);
                    AddReference(reference);
                    subst[i] = uo.name;
                }
                else
                {
                    subst[i] = a.ToString();
                }
            }
            _subst = subst;
        }

        public override Localizer Localizer => ATOL.Localizer;
        public override ErrorSeverity Severity { get; }
        public override string TitleKey { get; }
        public override string[] TitleSubst => _subst;
        public override string[] DetailsSubst => _subst;
        public override string[] HintSubst => _subst;
    }

    internal static class ATOReportUtil
    {
        public static void Info(string key, params object[] args)
        {
            ErrorReport.ReportError(new ATOError(ErrorSeverity.Information, key, args));
            ATOLog.Debug_($"info: {key} [{string.Join(", ", System.Array.ConvertAll(args ?? new object[0], a => a?.ToString() ?? "null"))}]");
        }

        public static void Warn(string key, params object[] args)
        {
            ErrorReport.ReportError(new ATOError(ErrorSeverity.NonFatal, key, args));
            ATOLog.Warn($"{key} [{string.Join(", ", System.Array.ConvertAll(args ?? new object[0], a => a?.ToString() ?? "null"))}]");
        }

        public static void Fatal(string key, params object[] args)
        {
            ErrorReport.ReportError(new ATOError(ErrorSeverity.Error, key, args));
            ATOLog.Error($"{key} [{string.Join(", ", System.Array.ConvertAll(args ?? new object[0], a => a?.ToString() ?? "null"))}]");
        }
    }
}

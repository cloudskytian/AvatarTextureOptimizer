// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Errors.cs — NDMF 错误报告 / NDMF error reporting
//
// 需求: 不合规挂载（无 VRCAvatarDescriptor / 多个组件）→ 报错中止烘焙或构建。
// 实现: SimpleError 子类 + 复用本包 I18n 的 Localizer（回退英文）。
// ============================================================================
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO 错误 / ATO error.
    /// </summary>
    public sealed class ATOError : SimpleError
    {
        private readonly Localizer _localizer;
        private readonly string _titleKey;
        private readonly ErrorSeverity _severity;
        private readonly string[] _subst;

        public ATOError(string titleKey, ErrorSeverity severity, params object[] args)
        {
            _localizer = ATOLocalizer.Instance;
            _titleKey = titleKey;
            _severity = severity;
            var list = new List<string>();
            foreach (var a in args)
            {
                list.Add(a == null ? "<null>" : a.ToString());
            }
            _subst = list.ToArray();
        }

        public override Localizer Localizer => _localizer;
        public override string TitleKey => _titleKey;
        public override ErrorSeverity Severity => _severity;
        public override string[] TitleSubst => _subst;
    }

    /// <summary>
    /// ATO i18n → NDMF Localizer 适配 / NDMF Localizer adapter backed by ATO I18n.
    /// </summary>
    public static class ATOLocalizer
    {
        private static Localizer _instance;

        public static Localizer Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new Localizer("en", () =>
                    {
                        var list = new List<(string, Func<string, string>)>();
                        foreach (var code in I18n.AvailableLocales)
                        {
                            list.Add((code, key => I18n.T(key)));
                        }
                        return list;
                    });
                }
                return _instance;
            }
        }
    }
}

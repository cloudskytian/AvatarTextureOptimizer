// Avatar Texture Optimizer / 头像贴图优化器
// JSON-based, user-extensible localization.
// 基于 JSON、可由用户扩展的本地化系统。
//
// Sources (later files override earlier ones for the same language):
//   1. Packages/net.fosa.avatar-texture-optimizer/Editor/i18n/*.json (built-in)
//   2. Assets/AvatarTextureOptimizer/I18n/*.json (user drop-in)
// Language resolution: Manual -> component.manualLanguage; Auto -> NDMF's
// LanguagePrefs.Language. Keys missing in the resolved language fall back to
// English, then to the key itself. The language list shown in the UI is
// derived purely from the JSON files that exist (有几个语言的配置文件就显示几个语言).
//
// 加载源（后加载的覆盖先加载的同语言键）：
//   1. 包内置 Packages/.../Editor/i18n/*.json
//   2. 用户投放 Assets/AvatarTextureOptimizer/I18n/*.json
// 语言解析：Manual -> 组件手动语言；Auto -> NDMF LanguagePrefs.Language。
// 所选语言缺失的键回退英文，再回退键名。UI 中的语言列表完全由存在的
// JSON 文件决定。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// JSON i18n service. JSON file format:
    /// { "language": "en-US", "strings": { "key": "text", ... } }
    /// JSON 国际化服务。文件格式：{ "language": "en-US", "strings": { "key": "text", ... } }
    /// </summary>
    public static class ATOLoc
    {
        private static readonly Dictionary<string, Dictionary<string, string>> _langs =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        private static string _activeLanguage = "en-US";
        private static ATOLanguageMode _mode = ATOLanguageMode.Auto;
        private static string _manual = "en-US";

        /// <summary>Active resolved language code. / 当前生效的语言代码。</summary>
        public static string ActiveLanguage => _activeLanguage;

        /// <summary>All language codes discovered from JSON files. / 从 JSON 文件发现的所有语言代码。</summary>
        public static IReadOnlyList<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                return _langs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        /// <summary>Force a reload of the JSON files (e.g. when the user edits them). / 强制重载 JSON 文件。</summary>
        public static void Reload()
        {
            _loaded = false;
            EnsureLoaded();
            ResolveActiveLanguage();
        }

        /// <summary>Configure from component settings. / 按组件设置配置。</summary>
        public static void Configure(ATOLanguageMode mode, string manualLanguage)
        {
            _mode = mode;
            _manual = string.IsNullOrEmpty(manualLanguage) ? "en-US" : manualLanguage;
            EnsureLoaded();
            ResolveActiveLanguage();
        }

        /// <summary>Translate a key. Never returns null. / 翻译键，永不返回 null。</summary>
        public static string T(string key)
        {
            EnsureLoaded();
            if (_langs.TryGetValue(_activeLanguage, out var dict) && dict.TryGetValue(key, out var v))
                return v;
            if (_langs.TryGetValue("en-US", out var en) && en.TryGetValue(key, out var ven))
                return ven;
            return key;
        }

        /// <summary>Translate with string.Format-style args (exceptions are swallowed). / 带参翻译（吞掉格式化异常）。</summary>
        public static string T(string key, params object[] args)
        {
            var s = T(key);
            try
            {
                return args == null || args.Length == 0 ? s : string.Format(s, args);
            }
            catch (FormatException)
            {
                return s;
            }
        }

        /// <summary>Adapter exposing ATO strings as an NDMF Localizer for the error UI. / 供报错 UI 复用的 NDMF Localizer 适配器。</summary>
        public static Localizer AsNdmfLocalizer()
        {
            EnsureLoaded();
            // NDMF language ids are lowercase (e.g. "zh-hans"); normalize ours similarly.
            // NDMF 的语言 ID 是小写形式（如 "zh-hans"），我们同样归一化。
            var pairs = new List<(string, Func<string, string>)>();
            foreach (var kv in _langs)
            {
                var dict = kv.Value; // capture / 捕获
                pairs.Add((kv.Key.ToLowerInvariant(), key => dict.TryGetValue(key, out var v) ? v : null));
            }
            return new Localizer("en-us", () => pairs);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _langs.Clear();
            LoadDir(AbsPathOf(ATOConsts.BuiltinI18nDir));
            // User drop-in directory; ignored when absent. / 用户自定义目录，不存在则忽略。
            LoadDir(AbsPathOf(ATOConsts.UserI18nDir));
            if (!_langs.ContainsKey("en-US"))
            {
                _langs["en-US"] = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private static string AbsPathOf(string assetRelative)
        {
            try
            {
                var full = Path.GetFullPath(assetRelative);
                return full;
            }
            catch
            {
                return assetRelative;
            }
        }

        private static void LoadDir(string absDir)
        {
            if (string.IsNullOrEmpty(absDir) || !Directory.Exists(absDir)) return;
            foreach (var file in Directory.GetFiles(absDir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var root = ATOJson.ParseObjectOrDefault(json);
                    if (root == null)
                    {
                        ATOLog.Warn($"i18n file unreadable, skipped: {file}");
                        continue;
                    }
                    var lang = ATOJson.GetString(root, "language");
                    var strings = ATOJson.GetObject(root, "strings");
                    if (string.IsNullOrEmpty(lang) || strings == null)
                    {
                        ATOLog.Warn($"i18n file missing 'language'/'strings', skipped: {file}");
                        continue;
                    }
                    if (!_langs.TryGetValue(lang, out var dict))
                    {
                        dict = new Dictionary<string, string>(StringComparer.Ordinal);
                        _langs[lang] = dict;
                    }
                    foreach (var kv in strings)
                    {
                        if (kv.Value is string s) dict[kv.Key] = s;
                    }
                    ATOLog.Verbose($"i18n loaded: {lang} ({strings.Count} keys) from {file}");
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"i18n load failed for {file}: {e.Message}");
                }
            }
        }

        private static void ResolveActiveLanguage()
        {
            string want;
            if (_mode == ATOLanguageMode.Manual)
            {
                want = _manual;
            }
            else
            {
                // Auto: follow NDMF. NDMF ids are lowercase ("zh-hans"); map case-insensitively.
                // Auto：跟随 NDMF。NDMF 语言 ID 为小写，大小写不敏感地匹配。
                want = "en-US";
                try
                {
                    var ndmf = LanguagePrefs.Language;
                    if (!string.IsNullOrEmpty(ndmf)) want = ndmf;
                }
                catch (Exception e)
                {
                    ATOLog.Verbose("NDMF LanguagePrefs unavailable, defaulting to en-US: " + e.Message);
                }
            }

            var hit = _langs.Keys.FirstOrDefault(k => string.Equals(k, want, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                // Try base language match, e.g. zh-Hant-TW -> zh-Hant, pt-BR unavailable -> en-US.
                // 尝试基础语言匹配，如 zh-Hant-TW -> zh-Hant，均无则回退 en-US。
                var baseName = want.Split('-')[0];
                hit = _langs.Keys.FirstOrDefault(k =>
                    string.Equals(k.Split('-')[0], baseName, StringComparison.OrdinalIgnoreCase));
            }
            _activeLanguage = hit ?? "en-US";
        }

        /// <summary>Editor utility: NDMF language id for our resolved language. / 取当前语言对应的 NDMF 语言 ID。</summary>
        public static string ActiveNdmfLanguageId => _activeLanguage.ToLowerInvariant();

        [MenuItem("Tools/Avatar Texture Optimizer/Reload i18n", false, 100)]
        private static void MenuReload()
        {
            Reload();
            ATOLog.Info("i18n reloaded / i18n 已重载");
        }
    }
}

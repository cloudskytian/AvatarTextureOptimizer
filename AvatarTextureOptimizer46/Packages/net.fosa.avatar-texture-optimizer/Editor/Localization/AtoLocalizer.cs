// SPDX-License-Identifier: MIT
// EN: JSON based, user extensible localization bridged into NDMF's Localizer.
// ZH: 基于 JSON、可由用户扩展的本地化，桥接到 NDMF 的 Localizer。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Localization
{
    /// <summary>
    /// EN: Loads every <c>*.json</c> under any <c>Editor/Resources/i18n</c> style folder that belongs to
    ///     ATO, plus any folder registered through <see cref="AddSearchPath"/>. A language exists as soon
    ///     as a file for it exists; nothing else has to be edited. Missing keys fall back to English.
    /// ZH: 加载所有属于 ATO 的 <c>Editor/Resources/i18n</c> 风格目录下的 <c>*.json</c>，
    ///     以及通过 <see cref="AddSearchPath"/> 注册的任意目录。只要存在对应文件，该语言就会出现，
    ///     无需修改其他任何内容。缺失的键回退到英文。
    /// </summary>
    public static class AtoLocalizer
    {
        /// <summary>EN: Default (fallback) language code. ZH: 默认（回退）语言代码。</summary>
        public const string DefaultLanguage = "en-US";

        private const string BuiltinFolderGuidHint = "Packages/net.fosa.avatar-texture-optimizer/Editor/Resources/i18n";

        private static readonly List<string> _searchPaths = new List<string> { BuiltinFolderGuidHint };
        private static Localizer _localizer;
        private static Dictionary<string, Dictionary<string, string>> _tables;

        /// <summary>
        /// EN: The language explicitly selected by the user, or <c>null</c>/"auto" to follow NDMF.
        /// ZH: 用户显式选择的语言，<c>null</c> 或 "auto" 表示跟随 NDMF。
        /// </summary>
        public static string LanguageOverride { get; set; }

        /// <summary>
        /// EN: Registers an additional folder to scan for translation JSON files. Third party packages can
        ///     call this from <c>[InitializeOnLoadMethod]</c> to ship extra languages.
        /// ZH: 注册额外的翻译 JSON 扫描目录。第三方包可在 <c>[InitializeOnLoadMethod]</c> 中调用以提供额外语言。
        /// </summary>
        public static void AddSearchPath(string assetsRelativeFolder)
        {
            if (string.IsNullOrEmpty(assetsRelativeFolder)) return;
            if (_searchPaths.Contains(assetsRelativeFolder)) return;
            _searchPaths.Add(assetsRelativeFolder);
            Reload();
        }

        /// <summary>EN: The NDMF localizer instance, used for error reports and UI Elements. ZH: NDMF 的 localizer 实例，用于错误报告与 UI Elements。</summary>
        public static Localizer Localizer
        {
            get
            {
                if (_localizer == null) Reload();
                return _localizer;
            }
        }

        /// <summary>EN: All language codes that have a translation file. ZH: 所有拥有翻译文件的语言代码。</summary>
        public static IEnumerable<string> AvailableLanguages
        {
            get
            {
                if (_tables == null) Reload();
                return _tables.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>EN: Rescans every search path and rebuilds the tables. ZH: 重新扫描所有搜索路径并重建表。</summary>
        public static void Reload()
        {
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in _searchPaths)
            {
                string absolute = ToAbsolute(folder);
                if (absolute == null || !Directory.Exists(absolute)) continue;

                foreach (var file in Directory.GetFiles(absolute, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var code = Path.GetFileNameWithoutExtension(file);
                        var json = File.ReadAllText(file);
                        var parsed = AtoMiniJson.ParseFlatStringMap(json);
                        if (parsed == null || parsed.Count == 0) continue;

                        // EN: Normalize e.g. "zh-hans" to the culture canonical form used by NDMF.
                        // ZH: 将 "zh-hans" 之类的代码规范化为 NDMF 使用的规范形式。
                        string normalized;
                        try { normalized = CultureInfo.GetCultureInfo(code).Name; }
                        catch (CultureNotFoundException) { normalized = code; }

                        if (!_tables.TryGetValue(normalized, out var table))
                            _tables[normalized] = table = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var kv in parsed) table[kv.Key] = kv.Value;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ATO][i18n] Failed to load '{file}': {e.Message}");
                    }
                }
            }

            if (!_tables.ContainsKey(DefaultLanguage))
                _tables[DefaultLanguage] = new Dictionary<string, string>(StringComparer.Ordinal);

            _localizer = new Localizer(DefaultLanguage, () =>
                _tables.Select(kv => (kv.Key, (Func<string, string>)(k => kv.Value.TryGetValue(k, out var v) ? v : null))).ToList());
        }

        private static string ToAbsolute(string unityPath)
        {
            if (Path.IsPathRooted(unityPath)) return unityPath;
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (projectRoot == null) return null;
            return Path.Combine(projectRoot, unityPath);
        }

        /// <summary>
        /// EN: Looks up a key, honouring <see cref="LanguageOverride"/> then NDMF's language then English.
        ///     Returns <c>&lt;key&gt;</c> if nothing matches, which makes missing strings obvious.
        /// ZH: 查询一个键，依次按 <see cref="LanguageOverride"/>、NDMF 当前语言、英文回退。
        ///     全部未命中时返回 <c>&lt;key&gt;</c>，让缺失的字符串一目了然。
        /// </summary>
        public static string Tr(string key)
        {
            if (_tables == null) Reload();

            var candidates = new List<string>(4);
            if (!string.IsNullOrEmpty(LanguageOverride) && !LanguageOverride.Equals("auto", StringComparison.OrdinalIgnoreCase))
                candidates.Add(LanguageOverride);
            candidates.Add(LanguagePrefs.Language);
            var baseLang = (LanguagePrefs.Language ?? "en").Split('-')[0];
            foreach (var k in _tables.Keys)
                if (k.Equals(baseLang, StringComparison.OrdinalIgnoreCase) || k.StartsWith(baseLang + "-", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(k);
            candidates.Add(DefaultLanguage);

            foreach (var c in candidates)
            {
                if (c != null && _tables.TryGetValue(c, out var table) && table.TryGetValue(key, out var value))
                    return value;
            }
            return $"<{key}>";
        }

        /// <summary>EN: <see cref="Tr(string)"/> with <see cref="string.Format(string,object[])"/> applied. ZH: 在 <see cref="Tr(string)"/> 基础上应用 <see cref="string.Format(string,object[])"/>。</summary>
        public static string Tr(string key, params object[] args)
        {
            var s = Tr(key);
            try { return string.Format(s, args); }
            catch (FormatException) { return s; }
        }

        [InitializeOnLoadMethod]
        private static void Init() => Reload();
    }
}

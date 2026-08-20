using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using nadena.dev.ndmf.ui;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// ATO i18n: user-extensible JSON localization. / ATO 本地化：用户可扩展的 JSON 语言配置。
    ///
    /// - Shipped languages live in Editor/Resources/ATO/i18n/*.json (currently en, zh-cn). /
    ///   自带语言位于 Editor/Resources/ATO/i18n/*.json（当前 en、zh-cn）。
    /// - Users can add their own JSON files under any folder named "ATO/i18n" inside Assets/. /
    ///   用户可在 Assets 下任意 "ATO/i18n" 文件夹添加自己的 JSON 文件。
    /// - Auto mode follows NDMF's current language; missing keys fall back to English. /
    ///   Auto 模式跟随 NDMF 当前语言；缺失翻译回退英文。
    /// </summary>
    internal static class AtoLoc
    {
        /// <summary>Default/fallback language. / 默认/回退语言。</summary>
        public const string FallbackCode = "en";

        private static Dictionary<string, Dictionary<string, string>> _tables =
            new Dictionary<string, Dictionary<string, string>>();

        /// <summary>NDMF Localizer used by inspector UI (follows NDMF's global language). / 供 Inspector UI 使用的 NDMF Localizer（跟随 NDMF 全局语言）。</summary>
        public static Localizer L { get; private set; }

        static AtoLoc()
        {
            ReloadTables();
            L = new Localizer(FallbackCode, () =>
            {
                var list = new List<(string, Func<string, string>)>();
                foreach (var kv in _tables.OrderBy(k => k.Key))
                {
                    var table = kv.Value;
                    list.Add((kv.Key, key => table.TryGetValue(key, out var v) ? v : null));
                }
                return list;
            });

            // Register our languages with NDMF's language switcher. / 向 NDMF 语言切换器注册我们的语言。
            foreach (var code in AvailableCodes)
            {
                try { LanguagePrefs.RegisterLanguage(code); } catch (Exception) { /* ignore duplicates */ }
            }
        }

        /// <summary>All discovered language codes (sorted). / 已发现的语言代码（有序）。</summary>
        public static IReadOnlyList<string> AvailableCodes => _tables.Keys.OrderBy(k => k).ToList();

        /// <summary>
        /// Reload all JSON tables (shipped + user-provided). / 重新加载全部 JSON 表（自带 + 用户提供）。
        /// </summary>
        public static void ReloadTables()
        {
            _tables = new Dictionary<string, Dictionary<string, string>>();
            LoadShipped();
            LoadUser();
        }

        /// <summary>Load tables shipped in the package Resources folder. / 加载包内 Resources 文件夹的语言表。</summary>
        private static void LoadShipped()
        {
            var assets = Resources.LoadAll<TextAsset>("ATO/i18n");
            foreach (var asset in assets)
            {
                if (asset == null) continue;
                ParseTable(asset.text, _tables, "<package>");
            }
        }

        /// <summary>
        /// Load user-provided tables: any *.json under an "ATO/i18n" folder in Assets. /
        /// 加载用户提供的表：Assets 下 "ATO/i18n" 文件夹中的任意 *.json。
        /// </summary>
        private static void LoadUser()
        {
            var guids = AssetDatabase.FindAssets("t:TextAsset");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith("Assets/")) continue;
                var dir = Path.GetDirectoryName(path);
                if (dir == null || Path.GetFileName(dir) != "i18n") continue;
                if (Path.GetFileName(Path.GetDirectoryName(dir)) != "ATO") continue;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var text = File.ReadAllText(path);
                ParseTable(text, _tables, path);
            }
        }

        /// <summary>
        /// Parse one JSON localization file into the tables. / 解析一个 JSON 语言文件并入表。
        /// Format (JsonUtility-compatible): / 格式（兼容 JsonUtility）：
        /// { "code": "zh-cn", "strings": [ { "key": "stage.scan", "value": "扫描" }, ... ] }
        /// </summary>
        private static void ParseTable(string json, Dictionary<string, Dictionary<string, string>> tables, string source)
        {
            try
            {
                var root = JsonUtility.FromJson<AtoLanguageFile>(json);
                if (root == null || string.IsNullOrEmpty(root.code) || root.strings == null)
                {
                    Debug.LogWarning($"[ATO] i18n: malformed localization file ignored: {source}");
                    return;
                }
                if (!tables.TryGetValue(root.code, out var table))
                {
                    table = new Dictionary<string, string>();
                    tables[root.code] = table;
                }
                // User files may override existing keys (later files win by merge order). / 用户文件可覆盖已有键。
                foreach (var entry in root.strings)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    table[entry.Key] = entry.Value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] i18n: failed to parse {source}: {e.Message}");
            }
        }

        /// <summary>
        /// Resolve the effective language code for a settings value. / 解析设置值对应的有效语言代码。
        /// </summary>
        /// <param name="setting">The "language" setting value ("auto" or a code). / 设置值（"auto" 或语言代码）。</param>
        public static string ResolveCode(string setting)
        {
            if (!string.IsNullOrEmpty(setting) && setting != "auto" && _tables.ContainsKey(setting))
                return setting;
            return NormalizeNdmfCode(LanguagePrefs.Language);
        }

        /// <summary>
        /// Map an NDMF language code (e.g. "zh-Hans", "en-US") to one of our table codes. / 将 NDMF 语言代码映射到我们的表代码。
        /// </summary>
        public static string NormalizeNdmfCode(string ndmfCode)
        {
            if (string.IsNullOrEmpty(ndmfCode)) return FallbackCode;
            var lower = ndmfCode.ToLowerInvariant();
            if (lower.StartsWith("zh")) return _tables.ContainsKey("zh-cn") ? "zh-cn" : FallbackCode;
            if (lower.StartsWith("en")) return FallbackCode;
            return _tables.ContainsKey(ndmfCode) ? ndmfCode : FallbackCode;
        }

        /// <summary>
        /// Translate with the GLOBAL language (NDMF's current language; inspector UI use). / 按全局语言翻译（跟随 NDMF；Inspector UI 用）。
        /// Missing keys fall back to English, then the key itself. / 缺失回退英文，再回退键名。
        /// </summary>
        public static string Tr(string key, params object[] args)
        {
            var text = L.GetLocalizedString(key);
            if (string.IsNullOrEmpty(text)) text = key;
            return args != null && args.Length > 0 ? string.Format(text, args) : text;
        }

        /// <summary>
        /// Translate with an explicit language code (build-time reporting use). / 按指定语言代码翻译（烘焙期报告用）。
        /// </summary>
        public static string Tr(string code, string key, params object[] args)
        {
            var text = Lookup(code, key);
            return args != null && args.Length > 0 ? string.Format(text, args) : text;
        }

        /// <summary>Look up a key in a specific code (fallback en → key). / 按代码查键（回退 en → 键名）。</summary>
        public static string Lookup(string code, string key)
        {
            if (!string.IsNullOrEmpty(code) && _tables.TryGetValue(code, out var table) &&
                table.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;
            if (_tables.TryGetValue(FallbackCode, out var en) && en.TryGetValue(key, out var ev) &&
                !string.IsNullOrEmpty(ev))
                return ev;
            return key;
        }

        [Serializable]
        private class AtoLanguageFile
        {
            public string code;
            public List<AtoStringEntry> strings;
        }

        [Serializable]
        private class AtoStringEntry
        {
            public string key;
            public string value;
        }
    }

}

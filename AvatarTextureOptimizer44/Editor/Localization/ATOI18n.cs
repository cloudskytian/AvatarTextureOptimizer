// ATOI18n.cs - User-extensible JSON i18n. Scans Localization/*.json (one language per file).
// 可由用户扩展的JSON i18n。扫描 Localization/*.json（每个文件一种语言）。
// - Language = filename without extension (e.g. "en-us", "zh-hans", "ja-jp").
// - "Auto" (default) follows NDMF's current language; falls back to English.
// - Users can add more json files; they appear automatically.
// 语言 = 文件名（如 en-us / zh-hans）。默认 Auto 跟随 NDMF 当前语言，缺失回退英文。用户添加新json即自动出现新语言。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor.Localization
{
    public static class ATOI18n
    {
        public const string AutoLanguage = "auto";
        private const string PrefsKey = "net.fosa.ato.language";
        private static Dictionary<string, Dictionary<string, string>> _tables;

        /// <summary>User selection: "auto" or a language code. / 用户选择：auto 或语言码。</summary>
        public static string Selected
        {
            get => SessionState.GetString(PrefsKey, AutoLanguage);
            set { SessionState.SetString(PrefsKey, value); _tables = null; }
        }

        /// <summary>All discovered languages ("auto" excluded). / 全部发现的语言（不含auto）。</summary>
        public static List<string> AvailableLanguages()
        {
            EnsureLoaded();
            return _tables.Keys.OrderBy(k => k).ToList();
        }

        /// <summary>Translate a key. / 翻译一个键。</summary>
        public static string Tr(string key)
        {
            EnsureLoaded();
            string lang = EffectiveLanguage();
            if (_tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (lang != "en-us" && _tables.TryGetValue("en-us", out var e) && e.TryGetValue(key, out var v2) && !string.IsNullOrEmpty(v2)) return v2;
            return key; // key itself as last resort / 最终回退为键名
        }

        /// <summary>Tr with args, {0} style. / 带参数翻译。</summary>
        public static string F(string key, params object[] args)
        {
            try { return string.Format(Tr(key), args); } catch { return Tr(key); }
        }

        /// <summary>Effective language: user selection, or NDMF current. / 有效语言：用户选择或NDMF当前语言。</summary>
        public static string EffectiveLanguage()
        {
            string sel = Selected;
            if (sel != AutoLanguage) return sel;
            string nd = LanguagePrefs.Language;
            if (string.IsNullOrEmpty(nd)) return "en-us";
            // exact match first, then language-part match (zh-hans vs zh) / 先精确匹配，再语言部分匹配
            EnsureLoaded();
            if (_tables.ContainsKey(nd)) return nd;
            string lp = nd.Split('-')[0];
            foreach (var k in _tables.Keys) if (k.StartsWith(lp, StringComparison.OrdinalIgnoreCase)) return k;
            return "en-us";
        }

        /// <summary>NDMF Localizer bridge so NDMF console errors localize too.
        /// Uses the tuple-loader constructor (pure BCL, no LocalizationAsset dependency).
        /// NDMF Localizer 桥接，使 NDMF 控制台错误同样本地化。使用元组加载器构造（纯BCL，无LocalizationAsset依赖）。</summary>
        public static Localizer BuildNdmfLocalizer()
        {
            EnsureLoaded();
            return new Localizer("en-us", () =>
            {
                var list = new List<(string, Func<string, string>)>();
                foreach (var kv in _tables)
                {
                    var table = kv.Value;
                    list.Add((kv.Key, k => table.TryGetValue(k, out var v) ? v : null));
                }
                return list;
            });
        }

        private static void EnsureLoaded()
        {
            if (_tables != null) return;
            _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string dir = PackageRoot.LocalizationFolder;
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                string lang = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                try
                {
                    var table = ParseJsonFlat(File.ReadAllText(file));
                    _tables[lang] = table;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ATO] i18n file load failed / i18n文件加载失败: {file}: {e.Message}");
                }
            }
        }

        /// <summary>Minimal flat "key": "value" JSON parser (no nested objects needed). / 极简扁平键值JSON解析（无需嵌套）。</summary>
        internal static Dictionary<string, string> ParseJsonFlat(string json)
        {
            var r = new Dictionary<string, string>();
            // crude but robust tokenizer for flat string->string maps / 对扁平字符串映射的简易解析器
            int i = 0; int n = json.Length;
            while (i < n)
            {
                int ks = json.IndexOf('"', i); if (ks < 0) break;
                int ke = FindStringEnd(json, ks); if (ke < 0) break;
                string key = Unescape(json.Substring(ks + 1, ke - ks - 1));
                int colon = json.IndexOf(':', ke); if (colon < 0) break;
                int vs = json.IndexOf('"', colon); if (vs < 0) break;
                int ve = FindStringEnd(json, vs); if (ve < 0) break;
                string val = Unescape(json.Substring(vs + 1, ve - vs - 1));
                r[key] = val;
                i = ve + 1;
            }
            return r;
        }

        private static int FindStringEnd(string s, int open)
        {
            for (int i = open + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i;
            }
            return -1;
        }

        private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\t", "\t");
    }

    /// <summary>Locates this package's root folder (works for any install path). / 定位本包根目录（任意安装路径可用）。</summary>
    public static class PackageRoot
    {
        private static string _folder;
        /// <summary>Package root. "/ 包根目录。</summary>
        public static string Folder => _folder ??= Find();
        /// <summary>Localization folder. / 本地化目录。</summary>
        public static string LocalizationFolder => Path.Combine(Folder, "Localization");

        private static string Find()
        {
            // Find via an asset inside this package; asset paths are PROJECT-relative so anchor
            // them to the project root before GetFullPath. / 通过包内资产定位；资产路径是工程相对路径，
            // 必须先锚定到工程根再做 GetFullPath。
            var me = AssetDatabase.FindAssets("t:Script ATOI18n");
            string projRoot = Path.GetDirectoryName(Application.dataPath);
            foreach (var g in me)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith("ATOI18n.cs", StringComparison.OrdinalIgnoreCase))
                {
                    string full = Path.GetFullPath(Path.Combine(projRoot, p)); // .../Editor/Localization/ATOI18n.cs
                    return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(full))); // package root / 包根
                }
            }
            return Path.Combine(Application.dataPath, "net.fosa.avatar-texture-optimizer"); // fallback / 兜底
        }
    }
}

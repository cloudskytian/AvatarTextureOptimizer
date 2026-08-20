// ATOI18n.cs — i18n 本地化系统 / i18n localization system.
// 说明：读取包内 Localization/ato.i18n.*.json 配置文件（有几个文件就提供几个语言）；
// 语言可选 Auto（读取 NDMF 当前语言配置，缺失翻译回退英文）或手动指定；支持用户扩展：新增 json 文件即新增语言。
// Note: reads Localization/ato.i18n.*.json from the package (one language per file); Auto follows NDMF's current
// language with fallback to English; users can extend languages by adding more json files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>i18n 管理器。/ i18n manager.</summary>
    public static class ATOI18n
    {
        private static Dictionary<string, Dictionary<string, string>> _tables; // 语言码 → (key → 文本) / lang code → (key → text)
        private static List<string> _languages;                                 // 可用语言码 / available language codes
        private static string _forcedLanguage;                                  // 手动指定语言 / manually selected language

        private const string JsonPattern = "ato.i18n.";
        private const string JsonExt = ".json";

        /// <summary>可用语言码列表（供 UI 下拉）。/ Available language codes (for UI dropdown).</summary>
        public static IReadOnlyList<string> Languages
        {
            get
            {
                EnsureLoaded();
                return _languages;
            }
        }

        /// <summary>手动指定语言（null = Auto）。/ Manually selected language (null = Auto).</summary>
        public static void SetForcedLanguage(string code)
        {
            _forcedLanguage = string.IsNullOrEmpty(code) ? null : code;
        }

        /// <summary>当前生效语言码。/ Current effective language code.</summary>
        public static string CurrentLanguage
        {
            get
            {
                EnsureLoaded();
                if (_forcedLanguage != null && _tables.ContainsKey(_forcedLanguage)) return _forcedLanguage;
                // Auto：跟随 NDMF 当前语言 / Auto: follow NDMF's current language
                try
                {
                    var ndmfLang = nadena.dev.ndmf.localization.LanguagePrefs.Language; // 例: "en-us" / e.g. "en-us"
                    foreach (var l in _languages)
                    {
                        if (l.Equals(ndmfLang, StringComparison.OrdinalIgnoreCase)) return l;
                        // 前缀匹配（zh-Hans 匹配 zh-hans / zh / zh-cn）/ prefix match
                        if (ndmfLang.StartsWith(l.Substring(0, 2), StringComparison.OrdinalIgnoreCase) &&
                            l.StartsWith(ndmfLang.Substring(0, 2), StringComparison.OrdinalIgnoreCase))
                            return l;
                    }
                }
                catch (Exception)
                {
                    // NDMF 语言读取失败 → 回退英文 / failed to read NDMF language → fall back to English
                }
                foreach (var l in _languages)
                    if (l.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return l;
                return _languages.Count > 0 ? _languages[0] : "en";
            }
        }

        /// <summary>
        /// 翻译：key 在当前语言缺失时回退英文，再缺失返回 key 本身。
        /// Translate: falls back to English when missing in the current language, then to the key itself.
        /// </summary>
        public static string Tr(string key)
        {
            EnsureLoaded();
            var lang = CurrentLanguage;
            if (_tables.TryGetValue(lang, out var table) && table.TryGetValue(key, out var v)) return v;
            if (_tables.TryGetValue("en", out var enTable) && enTable.TryGetValue(key, out var v2)) return v2;
            return key;
        }

        /// <summary>翻译并格式化。/ Translate and format.</summary>
        public static string Tr(string key, params object[] args)
        {
            var s = Tr(key);
            try { return args != null && args.Length > 0 ? string.Format(s, args) : s; }
            catch (FormatException) { return s; }
        }

        /// <summary>
        /// 确保已加载全部 json（懒加载 + 缓存）。语言变化时调用 Reload() 重载。
        /// Ensure all json files are loaded (lazy + cached). Call Reload() when files change.
        /// </summary>
        private static void EnsureLoaded()
        {
            if (_tables != null) return;
            Reload();
        }

        /// <summary>重新扫描并加载全部 i18n 配置。/ Rescan and reload all i18n files.</summary>
        public static void Reload()
        {
            _tables = new Dictionary<string, Dictionary<string, string>>();
            _languages = new List<string>();

            var packagePath = FindPackagePath();
            if (string.IsNullOrEmpty(packagePath)) return;

            var dir = Path.Combine(packagePath, "Localization");
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.GetFiles(dir, JsonPattern + "*" + JsonExt))
            {
                var fileName = Path.GetFileName(file); // ato.i18n.en.json
                var code = fileName.Substring(JsonPattern.Length, fileName.Length - JsonPattern.Length - JsonExt.Length);
                try
                {
                    var table = new Dictionary<string, string>();
                    var json = File.ReadAllText(file);
                    // 使用 Unity 的 JsonUtility 需要包装类；这里用轻量解析（单层对象）/
                    // use a lightweight parser (single-level object) instead of JsonUtility wrappers
                    ParseJsonObject(json, table);
                    if (table.Count > 0)
                    {
                        _tables[code] = table;
                        _languages.Add(code);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ATO] Failed to load i18n file {file}: {e.Message} (i18n 文件加载失败)");
                }
            }
            _languages.Sort();
        }

        /// <summary>查找包根路径（通过程序集定位）。/ Find the package root path (via assembly location).</summary>
        public static string FindPackagePath()
        {
            // 通过本 asmdef 路径定位包根 / locate the package root via this asmdef path
            var asmdefGuids = AssetDatabase.FindAssets("Fosa.AvatarTextureOptimizer.Editor t:AssemblyDefinitionAsset");
            foreach (var guid in asmdefGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("/Editor/Fosa.AvatarTextureOptimizer.Editor.asmdef"))
                    return path.Substring(0, path.Length - "/Editor/Fosa.AvatarTextureOptimizer.Editor.asmdef".Length);
            }
            // 兜底：扫描 Packages / fallback: scan Packages
            var guids2 = AssetDatabase.FindAssets("Fosa.AvatarTextureOptimizer t:AssemblyDefinitionAsset");
            foreach (var guid in guids2)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("AvatarTextureOptimizer"))
                {
                    var idx = path.LastIndexOf("AvatarTextureOptimizer", StringComparison.Ordinal);
                    if (idx >= 0 && path.Substring(0, idx).TrimEnd('/').EndsWith("Packages"))
                        return path.Substring(0, idx + "AvatarTextureOptimizer".Length);
                }
            }
            return null;
        }

        /// <summary>轻量 JSON 单层对象解析（key: "value" 与嵌套单层数组跳过）。/ Lightweight single-level JSON object parser.</summary>
        internal static void ParseJsonObject(string json, Dictionary<string, string> into)
        {
            var idx = json.IndexOf('{');
            if (idx < 0) return;
            var end = json.LastIndexOf('}');
            if (end < 0) return;
            var body = json.Substring(idx + 1, end - idx - 1);

            int i = 0;
            var len = body.Length;
            while (i < len)
            {
                // 跳过空白与逗号 / skip whitespace and commas
                while (i < len && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
                if (i >= len) break;
                var keyStart = body.IndexOf('"', i);
                if (keyStart < 0) break;
                var keyEnd = body.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                var key = body.Substring(keyStart + 1, keyEnd - keyStart - 1);
                i = keyEnd + 1;
                while (i < len && body[i] != ':') i++;
                i++;
                while (i < len && char.IsWhiteSpace(body[i])) i++;
                if (i >= len) break;
                if (body[i] == '"')
                {
                    var valStart = i;
                    var valEnd = i + 1;
                    // 处理转义：跳到下一个未转义引号 / handle escapes: jump to next unescaped quote
                    while (valEnd < len)
                    {
                        if (body[valEnd] == '"' && body[valEnd - 1] != '\\') break;
                        valEnd++;
                    }
                    var val = body.Substring(valStart + 1, valEnd - valStart - 1);
                    into[key] = val.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
                    i = valEnd + 1;
                }
                else if (body[i] == '{' || body[i] == '[')
                {
                    // 嵌套对象/数组：跳到匹配括号结束 / nested object/array: skip to matching close
                    var open = body[i];
                    var close = open == '{' ? '}' : ']';
                    var depth = 0;
                    while (i < len)
                    {
                        if (body[i] == open) depth++;
                        else if (body[i] == close)
                        {
                            depth--;
                            if (depth == 0) break;
                        }
                        i++;
                    }
                    i++;
                }
                else
                {
                    // 其他标量：跳到逗号或括号 / other scalar: jump to comma or brace
                    while (i < len && body[i] != ',' && body[i] != '}') i++;
                }
            }
        }
    }
}

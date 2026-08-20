using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 可扩展的 JSON i18n 本地化。
    /// 从包内 Editor/I18n/*.json 读取（有几个语言文件就显示几个语言）。
    /// 默认 Auto：优先 NDMF 当前语言，再回退系统语言，最终回退英文；无对应翻译回退英文。
    ///
    /// Extensible JSON i18n. Loads Editor/I18n/*.json (one language per file).
    /// Auto mode: NDMF language -> system language -> English fallback.
    /// </summary>
    public static class ATOLocalization
    {
        public enum LanguageMode { Auto, Manual }

        private static LanguageMode _mode = LanguageMode.Auto;
        private static string _manualLang = "en";
        private static readonly Dictionary<string, Dictionary<string, string>> _tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded = false;

        public static LanguageMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        public static string ManualLanguage
        {
            get => _manualLang;
            set => _manualLang = value;
        }

        public static IReadOnlyCollection<string> AvailableLanguages
        {
            get { EnsureLoaded(); return _tables.Keys; }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            // 定位包内 I18n 目录：通过本包 Editor asmdef 定位。Locate I18n dir via the editor asmdef.
            var asmdefGuids = AssetDatabase.FindAssets("t:asmdef net.fosa.avatar-texture-optimizer.editor");
            foreach (var guid in asmdefGuids)
            {
                var asmdefPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!asmdefPath.EndsWith("net.fosa.avatar-texture-optimizer.editor.asmdef")) continue;
                var dir = Path.Combine(Path.GetDirectoryName(asmdefPath) ?? "", "I18n");
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    var lang = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        _tables[lang] = ParseFlatJson(File.ReadAllText(file));
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ATO] Failed to parse locale '{file}': {e.Message}");
                    }
                }
                break;
            }

            if (!_tables.ContainsKey("en"))
            {
                // 内置英文兜底。Built-in English fallback.
                _tables["en"] = BuiltinEnglish();
            }
        }

        /// <summary>当前生效语言代码。Effective language code.</summary>
        public static string EffectiveLanguage
        {
            get
            {
                EnsureLoaded();
                if (_mode == LanguageMode.Manual && _tables.ContainsKey(_manualLang))
                    return _manualLang;

                // 尝试 NDMF 语言（反射，避免强依赖其内部 API）。Try NDMF language via reflection.
                var ndmfLang = TryGetNdmfLanguage();
                if (ndmfLang != null && _tables.ContainsKey(ndmfLang)) return ndmfLang;

                var sys = SystemLanguageToCode(Application.systemLanguage);
                if (sys != null && _tables.ContainsKey(sys)) return sys;

                return "en";
            }
        }

        public static string Tr(string key)
        {
            EnsureLoaded();
            var lang = EffectiveLanguage;
            if (_tables.TryGetValue(lang, out var table) && table.TryGetValue(key, out var v))
                return v;
            if (_tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var v2))
                return v2;
            return key; // 兜底：返回 key 本身 / fallback: return the key
        }

        public static string Tr(string key, params object[] args)
        {
            var s = Tr(key);
            try { return string.Format(s, args); } catch { return s; }
        }

        private static string TryGetNdmfLanguage()
        {
            try
            {
                var t = Type.GetType("nadena.dev.ndmf.localization.LanguagePrefs, nadena.dev.ndmf");
                var prop = t?.GetProperty("Language", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var val = prop?.GetValue(null)?.ToString();
                return string.IsNullOrEmpty(val) ? null : val;
            }
            catch { return null; }
        }

        private static string SystemLanguageToCode(SystemLanguage lang)
        {
            switch (lang)
            {
                case SystemLanguage.Chinese: case SystemLanguage.ChineseSimplified: return "zh-CN";
                case SystemLanguage.ChineseTraditional: return "zh-TW";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.English: return "en";
                default: return null;
            }
        }

        /// <summary>
        /// 极简扁平 JSON 对象解析器（仅处理 {"key":"value",...}），供 i18n 使用。
        /// Minimal flat JSON object parser for i18n files.
        /// </summary>
        private static Dictionary<string, string> ParseFlatJson(string json)
        {
            var result = new Dictionary<string, string>();
            int i = 0, n = json.Length;

            void SkipWs() { while (i < n && (char.IsWhiteSpace(json[i]))) i++; }

            SkipWs();
            if (i >= n || json[i] != '{') throw new Exception("not a JSON object");
            i++;
            SkipWs();
            if (i < n && json[i] == '}') { return result; }

            while (i < n)
            {
                SkipWs();
                if (i >= n || json[i] != '"') throw new Exception("expected string key");
                var key = ReadString(json, ref i);
                SkipWs();
                if (i >= n || json[i] != ':') throw new Exception("expected ':'");
                i++;
                SkipWs();
                if (i >= n || json[i] != '"') throw new Exception("expected string value");
                var value = ReadString(json, ref i);
                result[key] = value;
                SkipWs();
                if (i < n && json[i] == ',') { i++; continue; }
                if (i < n && json[i] == '}') { i++; break; }
                throw new Exception("unexpected char in object");
            }
            return result;
        }

        private static string ReadString(string s, ref int i)
        {
            i++; // skip opening quote
            var sb = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i < s.Length)
                {
                    var e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                var hex = s.Substring(i, 4); i += 4;
                                sb.Append((char)Convert.ToInt32(hex, 16));
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static Dictionary<string, string> BuiltinEnglish()
        {
            return new Dictionary<string, string>
            {
                ["stage.collect"] = "Collecting textures & materials",
                ["stage.analyze"] = "Analyzing animation & UV mapping",
                ["stage.process"] = "Scaling UV islands by target quality",
                ["stage.pack"] = "Packing islands into atlases",
                ["stage.apply"] = "Writing meshes, textures & materials",
                ["done"] = "Avatar Texture Optimizer finished",
                ["cancel"] = "Cancelled by user",
            };
        }
    }
}

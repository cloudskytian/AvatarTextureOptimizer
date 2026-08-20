// ============================================================================
// ATO - i18n (JSON based, user extensible)
// ATO - 国际化（基于 JSON，用户可扩展）
//
// Every *.json file in the package's i18n/ folder defines one language
// (flat "key": "string" map). Adding a file = adding a language. The UI
// language is:
//   1. the component's manual override, if set and present;
//   2. otherwise the NDMF language selection (nadena.dev.ndmf.language-selection);
//   3. falling back to English per missing key, and to the key itself as a
//      last resort.
// 包内 i18n/ 目录下的每个 *.json 文件定义一种语言（扁平 "key":"string" 映射）
// 。新增文件 = 新增语言。界面语言为：1) 组件手动指定（若存在）；2) 否则 NDMF
// 语言选择；3) 缺失键回退英文，最终回退键名。
// ============================================================================

#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using Newtonsoft.Json;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.I18n
{
    [InitializeOnLoad] // register available languages with NDMF on domain load
    // 域加载时向 NDMF 注册可用语言
    public static class ATOI18n
    {
        // GUID of the i18n folder .meta (stable, shipped with the package).
        // i18n 文件夹 .meta 的 GUID（稳定，随包发布）。
        private const string I18nFolderGuid = "a7c1f0e2b3d44f6a8e9c0d1e2f3a4b5c";

        private static readonly Dictionary<string, Dictionary<string, string>> _languages = new();
        private static string _languageRoot;
        private static string _activeLanguage = "en";
        private static bool _loaded;

        /// <summary>Loaded language ids (file stems), e.g. ["en","zh-Hans"].
        /// 已加载语言 id（文件名主体）。</summary>
        public static IReadOnlyList<string> LoadedLanguages => _languages.Keys.ToList();

        /// <summary>Currently active language id. 当前生效语言 id。</summary>
        public static string ActiveLanguage => _activeLanguage;

        /// <summary>Applies the language selection for one build and returns
        /// the effective language.
        /// 为一次构建应用语言选择，返回生效语言。</summary>
        public static string Apply(ATOComponent component)
        {
            EnsureLoaded();
            var requested = component != null ? component.LanguageOverride : "auto";
            string effective;
            if (string.IsNullOrEmpty(requested) || requested == "auto")
            {
                effective = LanguagePrefs.Language; // "en-us", "zh-hans", ...
            }
            else
            {
                effective = requested;
            }
            _activeLanguage = MatchLanguage(effective);
            return _activeLanguage;
        }

        /// <summary>Translate a key. Missing keys fall back to English, then
        /// to the key. 翻译一个键；缺失时回退英文，再回退键名。</summary>
        public static string S(string key)
        {
            if (_languages.TryGetValue(_activeLanguage, out var map) &&
                map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            {
                return v;
            }

            if (_activeLanguage != "en" && _languages.TryGetValue("en", out var en) &&
                en.TryGetValue(key, out var ev) && !string.IsNullOrEmpty(ev))
            {
                return ev;
            }

            return key;
        }

        /// <summary>Translate + format. 翻译 + 格式化。</summary>
        public static string Sf(string key, params object[] args)
        {
            try
            {
                return string.Format(S(key), args);
            }
            catch (FormatException)
            {
                return S(key) + "(" + string.Join(", ", args) + ")";
            }
        }

        private static string MatchLanguage(string requested)
        {
            var norm = requested.ToLowerInvariant().Replace("_", "-");
            // exact file match  精确匹配
            if (_languages.ContainsKey(norm)) return norm;
            // base language match ("zh-hans" -> "zh")  基础语言匹配
            var baseLang = norm.Split('-')[0];
            foreach (var lang in _languages.Keys)
            {
                if (lang.ToLowerInvariant().StartsWith(baseLang)) return lang.ToLowerInvariant();
            }
            return _languages.ContainsKey("en") ? "en" : "en";
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            _languageRoot = ResolveI18nRoot();
            if (_languageRoot == null)
            {
                Debug.LogWarning("[ATO] i18n folder not found - using English-only fallback. " +
                                 "未找到 i18n 文件夹 - 使用纯英文回退。");
                return;
            }

            foreach (var file in Directory.GetFiles(_languageRoot, "*.json"))
            {
                var lang = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                try
                {
                    var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                        File.ReadAllText(file));
                    if (map == null) continue;
                    _languages[lang] = map;
                    try
                    {
                        // Expose the language to NDMF's language switchers.
                        // 将该语言暴露给 NDMF 的语言切换器。
                        LanguagePrefs.RegisterLanguage(lang);
                    }
                    catch (Exception)
                    {
                        // NDMF localization API may be unavailable in tests.
                        // 测试环境中 NDMF 本地化 API 可能不可用。
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ATO] Failed to load i18n file " + file + ": " + e.Message);
                }
            }
        }

        private static string ResolveI18nRoot()
        {
            // 1) stable GUID (shipped .meta)  稳定 GUID（随包发布的 .meta）
            try
            {
                var p = AssetDatabase.GUIDToAssetPath(I18nFolderGuid);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
            }
            catch (Exception)
            {
            }

            // 2) package folder by name under Packages/ or Assets/
            //    按包名在 Packages/ 或 Assets/ 下查找
            var candidates = new List<string>();
            var dataPath = Application.dataPath;
            var projectRoot = Path.GetDirectoryName(dataPath);
            candidates.Add(Path.Combine(projectRoot, "Packages", "net.fosa.avatar-texture-optimizer", "i18n"));

            foreach (var baseDir in new[] { Path.Combine(projectRoot, "Packages"), dataPath })
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var dir in Directory.GetDirectories(baseDir))
                {
                    if (Path.GetFileName(dir) != "net.fosa.avatar-texture-optimizer") continue;
                    candidates.Add(Path.Combine(dir, "i18n"));
                }
            }

            return candidates.FirstOrDefault(Directory.Exists);
        }
    }
}

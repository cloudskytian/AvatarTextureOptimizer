using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// User-extensible i18n: every *.json under the package's Localization folder becomes a
    /// selectable language ("Auto" follows NDMF's language setting; missing keys fall back to
    /// English, then to the key itself).
    /// / 可由用户扩展的本地化：Localization 目录下每个 json 即一个可选语言；
    /// 默认 Auto 跟随 NDMF 语言；缺失键回退英文，再回退键名本身。
    /// </summary>
    [InitializeOnLoad]
    internal static class ATOL10n
    {
        private const string PrefsKey = "net.fosa.avatar-texture-optimizer.language";
        private const string FallbackLanguage = "en";

        /// <summary>"auto" or a language code. / "auto" 或语言代码。</summary>
        internal static string LanguageOverride
        {
            get => EditorPrefs.GetString(PrefsKey, "auto");
            set => EditorPrefs.SetString(PrefsKey, value);
        }

        // lang code (normalized display) -> key->value / 语言 -> 键值表
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static List<string> _languages;      // file-order languages / 文件名语言
        private static string _markerPath;           // Localization folder path / 目录路径

        /// <summary>Discovered languages (from json file names). / 发现的语言列表。</summary>
        internal static IReadOnlyList<string> Languages
        {
            get { EnsureLoaded(); return _languages; }
        }

        /// <summary>NDMF Localizer bridging our json tables for console reports. / 供 NDMF 控制台报告使用的 Localizer。</summary>
        internal static Localizer NdmfLocalizer
        {
            get
            {
                EnsureLoaded();
                if (_ndfmLocalizer == null)
                {
                    _ndfmLocalizer = new Localizer(FallbackLanguage, () =>
                    {
                        EnsureLoaded();
                        var list = new List<(string, Func<string, string>)>();
                        foreach (var lang in _languages)
                        {
                            var lang0 = lang;
                            list.Add((lang0, key => LookupRaw(lang0, key)));
                        }
                        return list;
                    });
                }
                return _ndfmLocalizer;
            }
        }

        private static Localizer _ndfmLocalizer;

        static ATOL10n()
        {
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_languages != null && _languages.Count > 0) return;
            try
            {
                var dir = FindLocalizationFolder();
                if (dir == null)
                {
                    Debug.LogWarning("[ATO] Localization folder not found; UI falls back to keys. / 未找到 Localization 目录，界面回退显示键名。");
                    _languages = new List<string> { FallbackLanguage };
                    return;
                }

                _markerPath = dir;
                Tables.Clear();
                var langs = new List<string>();
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var lang = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        Tables[lang] = AtoMiniJson.Parse(File.ReadAllText(file));
                        langs.Add(lang);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ATO] Failed to parse i18n file {file}: {e.Message} / 解析失败");
                    }
                }

                if (langs.Count == 0) langs.Add(FallbackLanguage);
                _languages = langs;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ATO] i18n load failed: " + e.Message);
                _languages = new List<string> { FallbackLanguage };
            }
        }

        /// <summary>
        /// Locate the package's Localization folder: marker asset GUID first (metas shipped with the
        /// package), then a path-based fallback scan so it also works without meta files.
        /// / 定位 Localization 目录：优先用随包 .meta 的 GUID 定位；失败则按路径特征回退扫描。
        /// </summary>
        private static string FindLocalizationFolder()
        {
            // Primary: marker file GUID (written into en.json.meta at packaging time).
            // 主路径：随包发布 en.json.meta 的 GUID。
            foreach (var markerGuid in MarkerGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(markerGuid);
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return Path.GetDirectoryName(p)?.Replace('\\', '/');
            }

            // Fallback: any */Localization/en.json whose path mentions this package name.
            // 回退：路径包含本包名的 */Localization/en.json。
            foreach (var guid in AssetDatabase.FindAssets("en t:TextAsset"))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (p != null && p.EndsWith("/Localization/en.json", StringComparison.OrdinalIgnoreCase)
                             && p.IndexOf("avatar-texture-optimizer", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Path.GetDirectoryName(p)?.Replace('\\', '/');
            }

            return null;
        }

        // Filled at packaging from Localization/en.json.meta. / 打包时由 en.json.meta 写入。
        private static readonly string[] MarkerGuids = { "9f4f86d6fc8b45cfa22d3930a304ebca" };

        /// <summary>Translate a key with the current language selection. / 按当前语言选择翻译。</summary>
        internal static string L(string key)
        {
            EnsureLoaded();
            var lang = EffectiveLanguage;
            return LookupRaw(lang, key) ?? LookupRaw(FallbackLanguage, key) ?? key;
        }

        /// <summary>Effective language: explicit override, else NDMF's language mapped to a known file. / 生效语言：手动选择，否则把 NDMF 语言映射到已知文件。</summary>
        internal static string EffectiveLanguage
        {
            get
            {
                var o = LanguageOverride;
                if (!string.IsNullOrEmpty(o) && o != "auto") return MatchLanguage(o);
                return MatchLanguage(LanguagePrefs.Language);
            }
        }

        /// <summary>Map a language tag to the closest discovered file ("en-US"→"en", "zh-Hans"/"zh-CN"→"zh-hans"...). / 将语言标签映射到最接近的语言文件。</summary>
        private static string MatchLanguage(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return FallbackLanguage;
            foreach (var lang in _languages)
                if (string.Equals(lang, tag, StringComparison.OrdinalIgnoreCase)) return lang;

            var baseTag = tag.Split('-')[0];
            foreach (var lang in _languages)
                if (lang.Split('-')[0].Equals(baseTag, StringComparison.OrdinalIgnoreCase)) return lang;

            return FallbackLanguage;
        }

        private static string LookupRaw(string lang, string key)
        {
            if (Tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v)) return v;
            return null;
        }

        /// <summary>Display name for the language dropdown. / 语言下拉框显示名。</summary>
        internal static string DisplayName(string lang)
        {
            try
            {
                var c = CultureInfo.GetCultureInfo(lang);
                return string.IsNullOrEmpty(c.NativeName) ? lang : $"{c.NativeName} ({lang})";
            }
            catch
            {
                return lang;
            }
        }
    }
}

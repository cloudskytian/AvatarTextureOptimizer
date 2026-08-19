// English: i18n loader. Discovers every JSON next to this script and feeds NDMF Localizer.
// 中文：i18n 加载器。读取本脚本同目录下全部 JSON，交给 NDMF Localizer。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOLoc
    {
        private static Localizer _localizer;
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static Localizer L
        {
            get
            {
                if (_localizer == null) Build();
                return _localizer;
            }
        }

        public static IReadOnlyCollection<string> AvailableLanguages
        {
            get
            {
                if (_localizer == null) Build();
                return Tables.Keys;
            }
        }

        public static string CurrentLanguage
        {
            get { return LanguagePrefs.Language; }
        }

        [InitializeOnLoadMethod]
        private static void Init()
        {
            Build();
        }

        internal static void Build()
        {
            Tables.Clear();
            var folder = LocateFolder();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    try
                    {
                        var lang = Path.GetFileNameWithoutExtension(file);
                        var json = File.ReadAllText(file);
                        var table = ParseFlatJson(json);
                        Tables[lang] = table;
                        LanguagePrefs.RegisterLanguage(lang);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(AvatarTextureOptimizer.LogPrefix + " Failed to load i18n " + file + ": " + e.Message);
                    }
                }
            }

            if (!Tables.ContainsKey("en-us"))
            {
                Tables["en-us"] = new Dictionary<string, string>();
            }

            _localizer = new Localizer("en-us", () =>
            {
                var list = new List<(string, Func<string, string>)>();
                foreach (var kv in Tables)
                {
                    var table = kv.Value;
                    list.Add((kv.Key, key =>
                    {
                        string value;
                        return table.TryGetValue(key, out value) ? value : null;
                    }));
                }

                return list;
            });
        }

        public static string T(string key)
        {
            return L.GetLocalizedString(key);
        }

        public static string Tf(string key, params object[] args)
        {
            var s = T(key);
            try
            {
                return args == null || args.Length == 0 ? s : string.Format(s, args);
            }
            catch
            {
                return s;
            }
        }

        /// <summary>
        /// Apply the component language setting. Auto reads NDMF LanguagePrefs.
        /// 应用组件语言。Auto 读取 NDMF 当前语言。
        /// </summary>
        public static void ApplyComponentLanguage(AvatarTextureOptimizer comp)
        {
            if (comp == null || comp.languageMode != ATOLanguageMode.Manual) return;
            if (string.IsNullOrEmpty(comp.manualLanguage)) return;
            if (!Tables.ContainsKey(comp.manualLanguage)) return;
            LanguagePrefs.Language = comp.manualLanguage;
        }

        private static string LocateFolder()
        {
            var guids = AssetDatabase.FindAssets("t:Script ATOLoc");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Replace('\\', '/').EndsWith("/Editor/Localization/ATOLoc.cs", StringComparison.Ordinal))
                {
                    return Path.GetDirectoryName(path);
                }
            }

            // Fallback when the script is not imported yet (unit / first import).
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == "ATOLoc.cs")
                    return Path.GetDirectoryName(path);
            }

            return null;
        }
    }
}

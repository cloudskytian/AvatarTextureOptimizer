// SPDX-License-Identifier: MIT
// EN: User extensible JSON based localisation, bridged into NDMF's Localizer.
// ZH: 可由用户扩展的 JSON 本地化，并桥接到 NDMF 的 Localizer。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Loads every <c>*.json</c> localisation file it can find and exposes them through one NDMF Localizer.
    ///     File name (without extension) is the BCP-47 language code, e.g. <c>en.json</c>, <c>zh-Hans.json</c>.
    ///     Users can add their own languages by dropping a json file into any folder named
    ///     <c>ATO-Localization</c> anywhere inside <c>Assets/</c>.
    /// ZH: 读取所有能找到的 <c>*.json</c> 本地化文件，并通过一个 NDMF Localizer 暴露出去。
    ///     文件名（不含扩展名）即为 BCP-47 语言代码，例如 <c>en.json</c>、<c>zh-Hans.json</c>。
    ///     用户只要在 <c>Assets/</c> 下任意名为 <c>ATO-Localization</c> 的文件夹里放入 json 即可扩展语言。
    /// </summary>
    public static class ATOL10n
    {
        private const string UserFolderName = "ATO-Localization";

        private static Localizer _localizer;

        /// <summary>EN: The shared localizer instance. ZH: 共享的 Localizer 实例。</summary>
        public static Localizer Localizer => _localizer ??= new Localizer("en", LoadAll);

        /// <summary>
        /// EN: Language codes that actually have a configuration file.
        /// ZH: 真正存在配置文件的语言代码。
        /// </summary>
        public static IReadOnlyList<string> AvailableLanguages
        {
            get
            {
                var list = LoadAll().Select(x => x.Item1).Distinct().ToList();
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            }
        }

        /// <summary>
        /// EN: Translate a key using the currently selected language, falling back to English and then the key.
        /// ZH: 使用当前语言翻译 key，找不到时回退英文，再回退 key 本身。
        /// </summary>
        public static string Tr(string key)
        {
            return Localizer.GetLocalizedString(key);
        }

        /// <summary>EN: Translate + string.Format. ZH: 翻译并做 string.Format。</summary>
        public static string Tr(string key, params object[] args)
        {
            var s = Tr(key);
            try
            {
                return string.Format(s, args);
            }
            catch (FormatException)
            {
                return s;
            }
        }

        /// <summary>EN: GUIContent helper. ZH: GUIContent 辅助方法。</summary>
        public static GUIContent G(string key, string tooltipKey = null)
        {
            return tooltipKey == null ? new GUIContent(Tr(key)) : new GUIContent(Tr(key), Tr(tooltipKey));
        }

        private static List<(string, Func<string, string>)> LoadAll()
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in EnumerateLocalizationFiles())
            {
                try
                {
                    var lang = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(lang)) continue;

                    var dict = ATOJson.ParseFlatStringMap(File.ReadAllText(path));
                    if (dict == null) continue;

                    if (!result.TryGetValue(lang, out var target))
                    {
                        target = new Dictionary<string, string>();
                        result[lang] = target;
                    }

                    foreach (var kv in dict) target[kv.Key] = kv.Value;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"{ATOLog.Prefix}[i18n] Failed to load '{path}': {e.Message}");
                }
            }

            if (result.Count == 0)
            {
                // EN: Never return an empty set, NDMF expects at least the default language.
                // ZH: 绝不返回空集合，NDMF 至少需要默认语言。
                result["en"] = new Dictionary<string, string>();
            }

            return result.Select(kv =>
            {
                var map = kv.Value;
                Func<string, string> lookup = k => map.TryGetValue(k, out var v) ? v : null;
                return (kv.Key, lookup);
            }).ToList();
        }

        private static IEnumerable<string> EnumerateLocalizationFiles()
        {
            // EN: 1) Files shipped with the package. ZH: 1) 包内自带的文件。
            var packageDir = ATOPackagePaths.LocalizationDirectory;
            if (!string.IsNullOrEmpty(packageDir) && Directory.Exists(packageDir))
            {
                foreach (var f in Directory.GetFiles(packageDir, "*.json", SearchOption.AllDirectories))
                    yield return f;
            }

            // EN: 2) User supplied folders inside the project. ZH: 2) 工程内用户提供的文件夹。
            if (Directory.Exists("Assets"))
            {
                foreach (var dir in Directory.GetDirectories("Assets", UserFolderName, SearchOption.AllDirectories))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                        yield return f;
                }
            }
        }

        /// <summary>
        /// EN: Force a reload; call after the user edits translation files.
        /// ZH: 强制重新加载；用户编辑翻译文件后调用。
        /// </summary>
        public static void Reload()
        {
            _localizer = null;
            Localizer.ReloadLocalizations();
        }
    }

    /// <summary>
    /// EN: Locates the on-disk directories of this package (works for both Packages/ and Assets/ installs).
    /// ZH: 定位本包在磁盘上的目录（Packages/ 与 Assets/ 两种安装方式都支持）。
    /// </summary>
    public static class ATOPackagePaths
    {
        private static string _root;

        /// <summary>EN: Package root directory. ZH: 包根目录。</summary>
        public static string Root
        {
            get
            {
                if (!string.IsNullOrEmpty(_root)) return _root;

                // EN: Resolve from this script's asset path, which is stable in both layouts.
                // ZH: 通过本脚本的资产路径解析，两种布局都稳定。
                var guids = AssetDatabase.FindAssets("ATOL10n t:MonoScript");
                foreach (var guid in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(p) || !p.EndsWith("ATOL10n.cs", StringComparison.Ordinal)) continue;
                    // .../Editor/Localization/ATOL10n.cs -> package root is two levels up from Editor
                    var editorDir = Path.GetDirectoryName(Path.GetDirectoryName(p));
                    if (editorDir == null) continue;
                    _root = Path.GetDirectoryName(editorDir)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(_root)) return _root;
                }

                _root = "Packages/net.fosa.avatar-texture-optimizer";
                return _root;
            }
        }

        /// <summary>EN: Directory containing the shipped translation files. ZH: 自带翻译文件所在目录。</summary>
        public static string LocalizationDirectory => Root + "/Editor/Localization";

        /// <summary>EN: Directory containing the compute shaders. ZH: 计算着色器所在目录。</summary>
        public static string ShaderDirectory => Root + "/Editor/Shaders";
    }
}

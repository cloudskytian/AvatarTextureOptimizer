// AvatarTextureOptimizer - AtoLocalization
// EN: Bootstraps the i18n tables: loads every Localization/*.json shipped with the package, then picks the
// language: manual selection → NDMF current language (LanguagePrefs.Language) → Unity editor language → English.
// CN: 启动 i18n 表：加载包内全部 Localization/*.json，再选择语言：
//     手动选择 → NDMF 当前语言（LanguagePrefs.Language）→ Unity 编辑器语言 → 英文。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace net.fosa.avatar_texture_optimizer
{
    [InitializeOnLoad]
    public static class AtoLocalization
    {
        static AtoLocalization()
        {
            Reload();
        }

        public static void Reload()
        {
            try
            {
                // EN: Locate the package folder (works for Packages/ and Assets/ installs).
                // CN: 定位包目录（兼容 Packages/ 与 Assets/ 安装）。
                string packageRoot = ResolvePackageRoot();
                string locDir = Path.Combine(packageRoot, "Runtime", "Localization");
                if (!Directory.Exists(locDir)) locDir = Path.Combine(packageRoot, "Localization");
                if (!Directory.Exists(locDir)) return;

                foreach (var file in Directory.GetFiles(locDir, "*.json"))
                {
                    string code = Path.GetFileNameWithoutExtension(file);
                    string text = File.ReadAllText(file);
                    I18n.AddLanguage(code, text);
                }
                ApplyLanguage();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[ATO] i18n load failed: {e.Message}");
            }
        }

        private static string ResolvePackageRoot()
        {
            // EN: Try the script's asset path first.
            // CN: 优先经脚本资产路径定位。
            var locator = ScriptableObject.CreateInstance<Locator>();
            try
            {
                var script = MonoScript.FromScriptableObject(locator);
                string path = AssetDatabase.GetAssetPath(script);
                if (!string.IsNullOrEmpty(path))
                {
                    string dir = Path.GetDirectoryName(path);
                    // Editor/Util → 包根
                    return Path.GetFullPath(Path.Combine(dir, "..", ".."));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(locator);
            }
            return "Packages/net.fosa.avatar-texture-optimizer";
        }

        private sealed class Locator : ScriptableObject { }

        /// <summary>EN: Applies the effective language (manual > NDMF > system). / CN: 应用有效语言（手动 > NDMF > 系统）。</summary>
        public static void ApplyLanguage()
        {
            string code = null;
            if (!string.IsNullOrEmpty(I18n.ManualLanguage))
            {
                code = I18n.ManualLanguage;
            }
            else
            {
                try
                {
                    // EN: NDMF's current language (verified against NDMF 1.14.4 source).
                    // CN: NDMF 当前语言（已对照 NDMF 1.14.4 源码核实）。
                    string ndmfLang = nadena.dev.ndmf.localization.LanguagePrefs.Language;
                    code = MapNdmfToAto(ndmfLang);
                }
                catch (Exception) { code = null; }
                if (code == null)
                {
                    code = UnityEditor.EditorPrefs.GetString("ato.lang", "");
                }
                if (string.IsNullOrEmpty(code))
                {
                    switch (UnityEngine.Application.systemLanguage)
                    {
                        case UnityEngine.SystemLanguage.ChineseSimplified: code = "zh-CN"; break;
                        case UnityEngine.SystemLanguage.ChineseTraditional: code = "zh-CN"; break;
                        default: code = "en"; break;
                    }
                }
            }
            if (I18n.AvailableLanguages.Count > 0)
            {
                // EN: Fall back to the closest available language.
                // CN: 回退到最接近的可用语言。
                bool found = false;
                foreach (var l in I18n.AvailableLanguages)
                {
                    if (l.Equals(code, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                    if (l.StartsWith(code.Split('-')[0], StringComparison.OrdinalIgnoreCase)) { found = true; code = l; break; }
                }
                if (!found)
                {
                    // EN: No match — fall back to English (spec: 若不存在对应翻译则回退到英文).
                    // CN: 无匹配 —— 回退英文。
                    code = "en";
                    if (!I18n.AvailableLanguages.Contains("en") && I18n.AvailableLanguages.Count > 0)
                        code = I18n.AvailableLanguages[0];
                }
            }
            I18n.SetLanguage(code);
        }

        private static string MapNdmfToAto(string ndmfLang)
        {
            if (string.IsNullOrEmpty(ndmfLang)) return null;
            string lower = ndmfLang.ToLowerInvariant();
            if (lower.StartsWith("zh")) return "zh-CN";
            if (lower.StartsWith("ja")) return "en"; // 无日语文件，回退英文
            if (lower.StartsWith("ko")) return "en";
            return "en";
        }
    }
}

// English: NDMF ErrorReport localizer wrapping ATO i18n JSON.
// 中文：把 ATO 的 i18n JSON 接到 NDMF ErrorReport。
using System.Collections.Generic;
using System.IO;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoErrors
    {
        public static readonly Localizer Localizer = new Localizer("en-US", Load);

        private static List<(string, System.Func<string, string>)> Load()
        {
            var list = new List<(string, System.Func<string, string>)>();
            string dir = null;
            try
            {
                var guids = AssetDatabase.FindAssets("t:TextAsset en-US");
                foreach (var g in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.Replace("\\", "/").Contains("avatar-texture-optimizer") && p.EndsWith("en-US.json"))
                    {
                        dir = Path.GetDirectoryName(p);
                        break;
                    }
                }
            }
            catch { /* import time */ }

            if (dir == null || !Directory.Exists(dir))
            {
                list.Add(("en-US", k => null));
                return list;
            }

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var locale = Path.GetFileNameWithoutExtension(file);
                Dictionary<string, string> map;
                try { map = MiniJson.ParseObject(File.ReadAllText(file)); }
                catch { continue; }
                list.Add((locale, k => map.TryGetValue(k, out var v) ? v : null));
            }
            return list;
        }
    }
}

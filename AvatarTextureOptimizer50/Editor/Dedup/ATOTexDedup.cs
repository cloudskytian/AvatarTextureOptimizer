// -----------------------------------------------------------------------------
// ATOTexDedup.cs — pre-optimization texture dedup (content + import settings).
// ATOTexDedup.cs —— 优化前的贴图去重（内容 + 导入设置）。
//
// Duplicate textures collapse into ONE TexInfo; all later stages automatically use
// the canonical texture (islands, atlases, rebinding). Original materials keep their
// references until cloning — nothing on disk is ever modified.
// 重复贴图合并为单个 TexInfo；后续所有阶段自动使用规范贴图。原始材质的引用保持到
// 克隆时才替换——绝不修改磁盘资产。
// Spec: different import settings ⇒ different texture; if any duplicate is
// whitelisted, the merged result is whitelisted too.
// 规格：导入设置不同即不同贴图；任一重复项在白名单内，合并结果也视为白名单。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOTexDedup
    {
        public static void Run(ATOBuildState st)
        {
            // ---- capture import snapshots & hashes / 导入设置快照与哈希 ----
            foreach (var t in st.textures.ToList())
            {
                t.assetPath = AssetDatabase.GetAssetPath(t.source);
                t.importSnap = CaptureImport(t.source, t.assetPath);
                t.contentHash = Hash(t, st);
            }

            // ---- group / 分组 ----
            var groups = st.textures
                .GroupBy(t => t.contentHash)
                .Where(g => g.Count() > 1)
                .ToList();

            if (groups.Count == 0)
            {
                ATOLog.Info("texture dedup: no duplicates");
                return;
            }

            int mergedCount = 0;
            foreach (var g in groups)
            {
                var list = g.OrderByDescending(t => t.usages.Count).ToList();
                var canonical = list[0];

                foreach (var dup in list.Skip(1))
                {
                    // whitelist contagion / 白名单传染
                    if (dup.whitelisted) canonical.MarkWhitelist("dedup with whitelisted twin / 与白名单项重复");

                    // merge usages / 合并用途
                    foreach (var u in dup.usages)
                        if (!canonical.usages.Contains(u))
                            canonical.usages.Add(u);

                    foreach (var kv in dup.usedByMaterials)
                    {
                        if (canonical.usedByMaterials.TryGetValue(kv.Key, out var set))
                            set.UnionWith(kv.Value);
                        else
                            canonical.usedByMaterials[kv.Key] = new HashSet<string>(kv.Value);
                    }

                    foreach (var kv in dup.alphaUsage)
                    {
                        if (canonical.alphaUsage.TryGetValue(kv.Key, out var au))
                        {
                            var mode = (AlphaMode)Mathf.Max((int)au.mode, (int)kv.Value.mode);
                            var cutoffs = new List<float>(au.cutoffs);
                            cutoffs.AddRange(kv.Value.cutoffs);
                            canonical.alphaUsage[kv.Key] = (mode, cutoffs);
                        }
                        else
                        {
                            canonical.alphaUsage[kv.Key] = kv.Value;
                        }
                    }

                    // remap lookups: duplicate source → canonical info / 重定向查找
                    st.texBySource[dup.source] = canonical;
                    foreach (var g2 in st.uvGroups)
                    {
                        var idx = g2.textures.IndexOf(dup);
                        if (idx >= 0) g2.textures[idx] = canonical;
                        for (int i = g2.textures.Count - 1; i >= 1; i--)
                            if (g2.textures[i] == canonical && g2.textures.IndexOf(canonical) != i)
                                g2.textures.RemoveAt(i); // dedupe the list itself / 列表自身去重
                    }

                    st.textures.Remove(dup);
                    mergedCount++;
                }
            }

            st.report.dedupedTextureCount = mergedCount;
            ATOLog.Info($"texture dedup: merged {mergedCount} duplicates into canonical textures");
        }

        // ================================================================= //

        private static ImportSnapshot CaptureImport(Texture2D tex, string path)
        {
            var snap = new ImportSnapshot
            {
                width = tex.width,
                height = tex.height,
                wrapMode = tex.wrapMode,
                filterMode = tex.filterMode,
                aniso = tex.anisoLevel,
            };

            var ti = !string.IsNullOrEmpty(path) ? AssetImporter.GetAtPath(path) as TextureImporter : null;
            if (ti != null)
            {
                var s = new TextureImporterSettings();
                ti.ReadTextureSettings(s);
                snap.sRGB = s.sRGBTexture;
                snap.mipmaps = s.mipmapEnabled;
                snap.compression = ti.textureCompression;
                snap.rawJson = EditorJsonUtility.ToJson(s) + "|q=" + (int)ti.compressionQuality
                               + "|ctype=" + (int)ti.textureType;
            }
            else
            {
                snap.sRGB = tex.graphicsFormat.ToString().Contains("SRGB");
                snap.mipmaps = tex.mipmapCount > 1;
                snap.compression = TextureImporterCompression.Compressed;
                snap.rawJson = $"runtime:{tex.format}/{tex.graphicsFormat}/{tex.mipmapCount}";
            }

            return snap;
        }

        private static string Hash(TexInfo t, ATOBuildState st)
        {
            var raw = ATOGpu.ReadPixelsRaw(t.source, st.gpu);
            uint h = 2166136261u;
            void Mix(byte b) { h = (h ^ b) * 16777619u; }

            int step = Mathf.Max(1, raw.Length / 32768);
            for (int i = 0; i < raw.Length; i += step)
            {
                Mix(raw[i].r);
                Mix(raw[i].g);
                Mix(raw[i].b);
                Mix(raw[i].a);
            }

            return $"{t.importSnap.rawJson}|{t.importSnap.width}x{t.importSnap.height}|{h}";
        }
    }
}

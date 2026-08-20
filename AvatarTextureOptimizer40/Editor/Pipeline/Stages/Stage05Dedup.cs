using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 05: Deduplicate textures by actual pixels AND import settings (different import settings
    /// => distinct). Update all material/animation references. If a duplicate is whitelisted, the
    /// merged result is treated as whitelisted too.
    /// 阶段 05：按实际像素与导入设置去重（导入设置不同即不同），更新材质/动画引用；若去重结果涉及
    /// 白名单，则整体视为白名单。
    /// </summary>
    internal sealed class Stage05Dedup : IStage
    {
        public string Name => "ATO/05 Deduplicating source textures";
        public float Weight => 1f;

        public void Run(AtoPipeline p)
        {
            // Hash by readable pixels. / 按可读像素哈希
            var hashToTex = new Dictionary<(long, int), Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();

            foreach (var u in p.Usages.Values)
            {
                var tex = u.Texture;
                if (tex == null) continue;
                if (u.Whitelisted) continue;

                long pixelHash = PixelHash(tex);
                var key = (pixelHash, u.ImportHash);
                if (hashToTex.TryGetValue(key, out var canonical))
                {
                    if (canonical != tex) remap[tex] = canonical;
                }
                else hashToTex[key] = tex;
            }

            if (remap.Count == 0) { AtoLog.VIf(p.Settings.VerboseLogging, "No duplicate textures found."); return; }

            // Apply remap to materials + animation clips / 更新材质与动画引用
            int updated = 0;
            foreach (var slot in p.SlotTextures)
            {
                var r = slot.Key.Renderer;
                if (r == null) continue;
                var mats = r.sharedMaterials;
                int idx = slot.Key.SlotIndex;
                if (idx < 0 || idx >= mats.Length || mats[idx] == null) continue;
                var mat = new Material(mats[idx]);
                var props = mat.GetTexturePropertyNames();
                bool changed = false;
                foreach (var pn in props)
                {
                    if (mat.GetTexture(pn) is Texture2D t && remap.TryGetValue(t, out var to))
                    { mat.SetTexture(pn, to); changed = true; }
                }
                if (changed) { mats[idx] = mat; r.sharedMaterials = mats; updated++; }
            }

            // If a whitelisted texture is among the duplicates, treat result as whitelist.
            // 若白名单贴图参与去重，结果视为白名单
            foreach (var u in p.Usages.Values)
            {
                if (u.Texture != null && remap.TryGetValue(u.Texture, out var to) && u.Whitelisted)
                {
                    if (p.Usages.TryGetValue(to, out var targetU)) targetU.Whitelisted = true;
                }
            }

            AtoLog.Info($"Deduplicated {remap.Count} texture(s); updated {updated} material(s). / 去重 {remap.Count} 张，更新 {updated} 个材质。");
        }

        private static long PixelHash(Texture2D t)
        {
            // Fast content hash via raw texture data (does not require isReadable for compressed source
            // if we go through GetRawTextureData). 快速内容哈希。
            unchecked
            {
                long h = 17;
                try
                {
                    var path = AssetDatabase.GetAssetPath(t);
                    if (!string.IsNullOrEmpty(path))
                    {
                        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                        byte[] buf = new byte[64 * 1024]; int read;
                        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                            for (int i = 0; i < read; i++) h = h * 31 + buf[i];
                        return h;
                    }
                }
                catch { }
                h = h * 31 + t.width; h = h * 31 + t.height; h = h * 31 + (int)t.format;
                return h;
            }
        }
    }
}

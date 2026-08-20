using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Dedup textures by actual pixels AND importer settings (different importer => different).
    /// 按实际像素与导入设置去重（导入设置不同则视为不同）。
    /// If any duplicate member is whitelist, the survivor is whitelist too.
    /// 去重成员含白名单则结果也视为白名单。
    /// </summary>
    public static class TextureContentDedup
    {
        public static void Apply(List<TextureBinding> bindings, HashSet<Texture2D> whitelist, BuildContext ctx)
        {
            var groups = new Dictionary<string, List<Texture2D>>();
            var seen = new HashSet<Texture2D>();
            foreach (var b in bindings)
            {
                if (b.Texture == null || !seen.Add(b.Texture)) continue;
                var key = MakeKey(b.Texture);
                if (!groups.TryGetValue(key, out var list)) { list = new List<Texture2D>(); groups[key] = list; }
                list.Add(b.Texture);
            }

            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in groups)
            {
                if (kv.Value.Count < 2) continue;
                var survivor = kv.Value[0];
                bool wl = false;
                foreach (var t in kv.Value) if (whitelist.Contains(t)) { wl = true; survivor = t; break; }
                if (wl) whitelist.Add(survivor);
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (kv.Value[i] == survivor) continue;
                    remap[kv.Value[i]] = survivor;
                    ObjectRegistry.RegisterReplacedObject(kv.Value[i], survivor);
                    if (wl) whitelist.Add(kv.Value[i]);
                }
                AtoLog.Info($"Dedup textures {kv.Value.Count} -> {survivor.name} key={kv.Key.Substring(0, Mathf.Min(12, kv.Key.Length))}…");
            }

            foreach (var b in bindings)
            {
                if (b.Texture != null && remap.TryGetValue(b.Texture, out var n))
                {
                    b.Texture = n;
                    if (whitelist.Contains(n)) { b.IsWhitelisted = true; b.SkipAtlas = true; }
                }
            }

            // Update material references on the clone. / 更新克隆上的材质引用。
            if (ctx?.AvatarRootObject != null)
            {
                foreach (var r in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var mat = mats[i];
                        if (mat == null) continue;
                        var sh = mat.shader;
                        if (sh == null) continue;
                        for (int p = 0; p < sh.GetPropertyCount(); p++)
                        {
                            if (sh.GetPropertyType(p) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                            var name = sh.GetPropertyName(p);
                            if (mat.GetTexture(name) is Texture2D t && remap.TryGetValue(t, out var n))
                            {
                                mat.SetTexture(name, n);
                                changed = true;
                            }
                        }
                    }
                    if (changed) r.sharedMaterials = mats;
                }
            }
        }

        private static string MakeKey(Texture2D tex)
        {
            var hash = TextureDecodeCache.PixelHash(tex);
            var path = AssetDatabase.GetAssetPath(tex);
            string imp = "";
            if (!string.IsNullOrEmpty(path))
            {
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti != null)
                {
                    imp = $"{ti.sRGBTexture}|{ti.textureType}|{ti.filterMode}|{ti.wrapMode}|{ti.mipmapEnabled}|{ti.textureCompression}|{ti.GetDefaultPlatformTextureSettings().format}|{ti.npotScale}";
                }
            }
            return hash + "|" + tex.width + "x" + tex.height + "|" + tex.filterMode + "|" + tex.wrapMode + "|" + imp;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Dedup by pixels + import settings; whitelist result stays whitelist.
    /// 按像素与导入设置去重；若源在白名单则结果也白名单。
    /// </summary>
    public static class AtoTextureDedup
    {
        public static void Apply(GameObject root, HashSet<Texture2D> whitelist, AtoTextureCache cache)
        {
            var map = new Dictionary<(ulong hash, int w, int h, int importHash), Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();

            var seen = new HashSet<Texture2D>();
            CollectAll(root, seen);

            foreach (var tex in seen)
            {
                if (tex == null) continue;
                var px = cache.GetPixels(tex);
                var imp = cache.GetImport(tex);
                var key = (AtoTextureUtil.ContentHash(px), tex.width, tex.height, imp.GetHashCode());
                if (map.TryGetValue(key, out var canon))
                {
                    if (!ReferenceEquals(canon, tex))
                    {
                        remap[tex] = canon;
                        if (whitelist.Contains(tex)) whitelist.Add(canon);
                        if (whitelist.Contains(canon)) whitelist.Add(tex);
                    }
                }
                else map[key] = tex;
            }

            if (remap.Count == 0) return;
            AtoLog.Info($"dedup remap count={remap.Count}");
            RemapRefs(root, remap);
        }

        static void CollectAll(GameObject root, HashSet<Texture2D> set)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                AtoWhitelist.CollectFrom(m, set);
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                AtoWhitelist.CollectFrom(a.runtimeAnimatorController, set);
        }

        static void RemapRefs(GameObject root, Dictionary<Texture2D, Texture2D> remap)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                foreach (var m in mats)
                {
                    if (m == null || m.shader == null) continue;
                    int n = m.shader.GetPropertyCount();
                    for (int i = 0; i < n; i++)
                    {
                        if (m.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                        var name = m.shader.GetPropertyName(i);
                        if (m.GetTexture(name) is Texture2D t && remap.TryGetValue(t, out var ntex))
                            m.SetTexture(name, ntex);
                    }
                }
            }
        }
    }
}

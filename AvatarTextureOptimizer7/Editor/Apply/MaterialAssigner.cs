using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer;
using UnityEngine;
using nadena.dev.ndmf;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Assigns atlases / scaled textures onto cloned materials. Never writes other shader parameters.
    /// 把图集 / 缩放后的贴图赋给克隆材质。绝不改其他着色器参数。
    /// </summary>
    public static class MaterialAssigner
    {
        public static void Apply(AtoSession session, AtoGraph graph, AtlasPlan plan)
        {
            // Map (texture, kind, uvGroup) → replacement. / 贴图替换表。
            var texReplace = new Dictionary<Texture2D, Texture2D>(session.TextureRemap);

            foreach (var kv in plan.Layouts)
            {
                var ug = kv.Key;
                foreach (var atlas in plan.Atlases)
                {
                    if (atlas.TypeGroup != ug.TypeGroup) continue;
                    foreach (var b in ug.Bindings)
                    {
                        if (b.Slot == null || b.Slot.Texture == null) continue;
                        if (b.Slot.Kind != atlas.Kind) continue;
                        if (session.WhitelistTextures.Contains(b.Slot.Texture)) continue;
                        texReplace[b.Slot.Texture] = atlas.Texture;
                    }
                }
            }

            foreach (var kv in graph.WorkingMaterials)
            {
                var clone = kv.Value;
                if (clone == null) continue;
                string[] names;
                try { names = clone.GetTexturePropertyNames(); }
                catch { continue; }

                foreach (var prop in names)
                {
                    if (clone.GetTexture(prop) is Texture2D t && texReplace.TryGetValue(t, out var nt) && nt != null)
                    {
                        clone.SetTexture(prop, nt);
                    }
                }

                session.Save(clone);
                session.MaterialRemap[kv.Key] = clone;
                ObjectRegistry.RegisterReplacedObject(kv.Key, clone);
            }

            foreach (var ri in graph.Renderers)
            {
                var mats = ri.Renderer.sharedMaterials;
                var changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && session.MaterialRemap.TryGetValue(mats[i], out var nm) && nm != null)
                    {
                        mats[i] = nm;
                        changed = true;
                    }
                }

                if (changed) ri.Renderer.sharedMaterials = mats;
            }

            session.Log.Info("Assigned materials: " + session.MaterialRemap.Count + " texture remaps=" + texReplace.Count);
        }
    }
}

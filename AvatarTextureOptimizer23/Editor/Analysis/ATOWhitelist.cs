using System.Collections.Generic;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Builds the whitelist. User objects of any type + ineligible slots.
    /// Same-UV siblings of a whitelisted texture skip atlas but still scale / import-optimize.
    /// 建立白名单。用户任意类型对象 + 不合格槽。
    /// 与白名单贴图同 UV 的其它贴图跳过图集化，但仍做整图缩放和导入参数优化。
    /// </summary>
    internal static class ATOWhitelist
    {
        public static void Run(ATOContext ctx)
        {
            var user = ctx.Settings.whitelist;
            if (user != null)
            {
                foreach (var obj in user)
                {
                    if (obj == null) continue;
                    CollectTexturesFromObject(obj, ctx.WhitelistedTextures);
                }
            }

            foreach (var use in ctx.Uses)
            {
                if (use.Slot.texture == null) continue;
                if (!use.Slot.eligible)
                {
                    ctx.WhitelistedTextures.Add(use.Slot.texture);
                }
                // Disabled renderers that animation never enables. / 从未被启用的 Renderer。
                if (!use.Renderer.EnabledNow && !use.Renderer.EnabledByAnimation)
                {
                    ctx.WhitelistedTextures.Add(use.Slot.texture);
                    ctx.Log.Detail($"Skip unused renderer texture {use.Slot.texture.name} on {use.Renderer.Renderer.name}");
                }
            }

            // Same-UV siblings skip atlas only. / 同 UV 兄弟只跳过图集。
            foreach (var use in ctx.Uses)
            {
                if (use.Slot.texture == null) continue;
                if (!ctx.WhitelistedTextures.Contains(use.Slot.texture)) continue;
                foreach (var other in ctx.Uses)
                {
                    if (other == use || other.Slot.texture == null) continue;
                    if (other.Renderer == use.Renderer &&
                        other.Slot.submeshIndex == use.Slot.submeshIndex &&
                        other.Slot.uvChannel == use.Slot.uvChannel)
                    {
                        ctx.SkipAtlasTextures.Add(other.Slot.texture);
                    }
                }
            }

            ctx.Report.WhitelistCount = ctx.WhitelistedTextures.Count;
            ctx.Log.Info($"Whitelist textures: {ctx.WhitelistedTextures.Count}, skip-atlas siblings: {ctx.SkipAtlasTextures.Count}");
        }

        public static void CollectTexturesFromObject(Object obj, HashSet<Texture2D> dst)
        {
            if (obj is Texture2D t)
            {
                dst.Add(t);
                return;
            }
            if (obj is Material mat)
            {
                CollectFromMaterial(mat, dst);
                return;
            }
            if (obj is Renderer r)
            {
                if (r.sharedMaterials == null) return;
                foreach (var m in r.sharedMaterials) CollectFromMaterial(m, dst);
                return;
            }
            if (obj is GameObject go)
            {
                foreach (var rr in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (rr.sharedMaterials == null) continue;
                    foreach (var m in rr.sharedMaterials) CollectFromMaterial(m, dst);
                }
                return;
            }
            if (obj is AnimationClip clip)
            {
                foreach (var b in UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys == null) continue;
                    foreach (var k in keys)
                    {
                        if (k.value is Texture2D tt) dst.Add(tt);
                        if (k.value is Material mm) CollectFromMaterial(mm, dst);
                    }
                }
                return;
            }
            if (obj is Mesh)
            {
                // Mesh itself has no textures; ignore. / 网格本身没有贴图。
                return;
            }
        }

        private static void CollectFromMaterial(Material mat, HashSet<Texture2D> dst)
        {
            if (mat == null || mat.shader == null) return;
            var n = mat.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (mat.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var tex = mat.GetTexture(mat.shader.GetPropertyName(i)) as Texture2D;
                if (tex != null) dst.Add(tex);
            }
        }
    }
}

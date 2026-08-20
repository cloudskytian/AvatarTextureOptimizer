using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Whitelist resolution: the whitelist accepts objects of ANY type (meshes, materials,
    /// textures, animations...). All textures referenced by whitelisted objects skip ALL
    /// optimization (including import parameters); co-UV textures skip atlasing but still get
    /// whole-texture scaling and import optimization. /
    /// 白名单解析：白名单不限对象类型（网格、材质、贴图、动画…）。白名单对象引用的全部贴图跳过所有优化
    /// （含导入参数）；同 UV 的其他贴图跳过图集化、参与整图缩放与导入参数优化。
    /// </summary>
    internal static class WhitelistResolver
    {
        /// <summary>
        /// Resolve the user whitelist into ctx.WhitelistObjects and ctx.WhitelistedTextures. /
        /// 把用户白名单解析为 ctx.WhitelistObjects 与 ctx.WhitelistedTextures。
        /// </summary>
        public static void Resolve(AtoContext ctx, List<UnityEngine.Object> whitelist)
        {
            if (whitelist == null) return;

            foreach (var entry in whitelist)
            {
                if (entry == null) continue;
                ResolveEntry(ctx, entry, $"user whitelist ({entry.name})");
            }
        }

        private static void ResolveEntry(AtoContext ctx, UnityEngine.Object entry, string reason)
        {
            switch (entry)
            {
                case GameObject go:
                    // The whole subtree is whitelisted. / 整个子树白名单。
                    ctx.WhitelistObjects.Add(go);
                    foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    {
                        ctx.WhitelistObjects.Add(t.gameObject);
                    }
                    foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer != null) WhitelistRenderer(ctx, renderer, reason);
                    }
                    break;

                case Renderer renderer:
                    ctx.WhitelistObjects.Add(renderer.gameObject);
                    WhitelistRenderer(ctx, renderer, reason);
                    break;

                case Material material:
                    WhitelistMaterial(ctx, material, reason);
                    break;

                case Texture2D texture:
                    ctx.WhitelistTexture(texture, reason);
                    break;

                case AnimationClip clip:
                    WhitelistClip(ctx, clip, reason);
                    break;

                case RuntimeAnimatorController controller:
                    WhitelistController(ctx, controller, reason);
                    break;

                case Mesh mesh:
                    // Whitelisting a mesh whitelists renderers using it. / 白名单网格 → 使用它的渲染器白名单。
                    ctx.WhitelistObjects.Add(mesh);
                    foreach (var renderer in ctx.AvatarRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh == mesh)
                        {
                            WhitelistRenderer(ctx, renderer, reason);
                        }
                        else if (renderer is MeshRenderer mr && mr.GetComponent<MeshFilter>()?.sharedMesh == mesh)
                        {
                            WhitelistRenderer(ctx, renderer, reason);
                        }
                    }
                    break;

                default:
                    ctx.WhitelistObjects.Add(entry);
                    break;
            }
        }

        private static void WhitelistRenderer(AtoContext ctx, Renderer renderer, string reason)
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (material != null) WhitelistMaterial(ctx, material, reason);
            }
        }

        private static void WhitelistMaterial(AtoContext ctx, Material material, string reason)
        {
            ctx.WhitelistObjects.Add(material);
            var shader = material.shader;
            if (shader == null) return;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var name = shader.GetPropertyName(i);
                if (material.GetTexture(name) is Texture2D texture)
                {
                    ctx.WhitelistTexture(texture, reason);
                }
            }
        }

        private static void WhitelistClip(AtoContext ctx, AnimationClip clip, string reason)
        {
            ctx.WhitelistObjects.Add(clip);
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!binding.type.IsSubclassOf(typeof(Material)) && binding.type != typeof(Material)) continue;
                foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                {
                    if (key.value is Texture2D texture) ctx.WhitelistTexture(texture, reason);
                }
            }
        }

        private static void WhitelistController(AtoContext ctx, RuntimeAnimatorController controller, string reason)
        {
            ctx.WhitelistObjects.Add(controller);
            foreach (var clip in controller.animationClips)
            {
                if (clip != null) WhitelistClip(ctx, clip, reason);
            }
        }
    }
}

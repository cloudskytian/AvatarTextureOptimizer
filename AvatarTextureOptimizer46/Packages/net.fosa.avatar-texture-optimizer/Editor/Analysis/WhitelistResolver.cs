// SPDX-License-Identifier: MIT
// EN: Resolves the user whitelist into concrete sets of protected objects.
// ZH: 将用户白名单解析为具体的受保护对象集合。

using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: The whitelist accepts objects of any type. Every texture reachable from a whitelisted object
    ///     is protected, and so are the renderers/materials that live under a whitelisted GameObject.
    /// ZH: 白名单接受任意类型的对象。凡是能从白名单对象到达的贴图都受保护，
    ///     白名单 GameObject 之下的渲染器与材质同样受保护。
    /// </summary>
    public sealed class WhitelistResolver
    {
        /// <summary>EN: Protected textures. ZH: 受保护的贴图。</summary>
        public readonly HashSet<Texture> Textures = new HashSet<Texture>();
        /// <summary>EN: Protected materials. ZH: 受保护的材质。</summary>
        public readonly HashSet<Material> Materials = new HashSet<Material>();
        /// <summary>EN: Protected renderers. ZH: 受保护的渲染器。</summary>
        public readonly HashSet<Renderer> Renderers = new HashSet<Renderer>();
        /// <summary>EN: Protected meshes. ZH: 受保护的网格。</summary>
        public readonly HashSet<Mesh> Meshes = new HashSet<Mesh>();

        /// <summary>
        /// EN: Builds the protected sets from the user's list. Dependencies are collected with
        ///     <see cref="EditorUtility.CollectDependencies"/>, which walks serialized references of any
        ///     asset type, including AnimationClips and AnimatorControllers.
        /// ZH: 从用户列表构建受保护集合。依赖通过 <see cref="EditorUtility.CollectDependencies"/> 收集，
        ///     它会遍历任意资产类型的序列化引用，包括 AnimationClip 与 AnimatorController。
        /// </summary>
        public void Resolve(IEnumerable<UnityObject> whitelist)
        {
            var roots = new List<UnityObject>();
            foreach (var o in whitelist)
            {
                if (o == null) continue;
                roots.Add(o);

                if (o is GameObject go)
                {
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        Renderers.Add(r);
                        roots.Add(r);
                    }
                }
                else if (o is Renderer rend)
                {
                    Renderers.Add(rend);
                }
                else if (o is Component c)
                {
                    roots.Add(c.gameObject);
                }
            }

            if (roots.Count == 0) return;

            foreach (var dep in EditorUtility.CollectDependencies(roots.ToArray()))
            {
                switch (dep)
                {
                    case Texture t: Textures.Add(t); break;
                    case Material m: Materials.Add(m); break;
                    case Mesh mesh: Meshes.Add(mesh); break;
                }
            }

            AtoLog.Info("Whitelist",
                $"resolved: {Textures.Count} textures, {Materials.Count} materials, {Meshes.Count} meshes, {Renderers.Count} renderers");
        }

        /// <summary>EN: True when the texture must be left untouched. ZH: 该贴图必须保持原样时为 true。</summary>
        public bool IsProtected(Texture t) => t != null && Textures.Contains(t);
        /// <summary>EN: True when the material must be left untouched. ZH: 该材质必须保持原样时为 true。</summary>
        public bool IsProtected(Material m) => m != null && Materials.Contains(m);
        /// <summary>EN: True when the renderer must be left untouched. ZH: 该渲染器必须保持原样时为 true。</summary>
        public bool IsProtected(Renderer r) => r != null && Renderers.Contains(r);
    }
}

// Copyright (c) fosa. Licensed under the MIT License.
// Expands the user's whitelist into the concrete set of textures to leave untouched.
// 将用户白名单展开为需要保持原样的具体贴图集合。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Resolves whitelist entries of any type into the textures they reference. The whitelist
    /// deliberately accepts renderers, materials, textures, animation clips and GameObjects,
    /// so users never have to know which object actually owns a texture.
    /// 将任意类型的白名单条目解析为其引用的贴图。
    /// 白名单有意接受渲染器、材质、贴图、动画与游戏对象，
    /// 使用户无需知道究竟是哪个对象真正持有贴图。
    /// </summary>
    public sealed class WhitelistResolver
    {
        private readonly ATOLogger _log;

        /// <summary>Creates a resolver. / 创建解析器。</summary>
        public WhitelistResolver(ATOLogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Expands every whitelist entry into the set of textures that must be skipped.
        /// 将每个白名单条目展开为必须跳过的贴图集合。
        /// </summary>
        public HashSet<Texture2D> Resolve(IEnumerable<Object> entries)
        {
            var result = new HashSet<Texture2D>();
            if (entries == null) return result;

            foreach (var entry in entries)
            {
                if (entry == null) continue;
                CollectFrom(entry, result);
            }

            _log?.Detail($"Whitelist resolved to {result.Count} textures");
            return result;
        }

        private void CollectFrom(Object entry, HashSet<Texture2D> sink)
        {
            switch (entry)
            {
                case Texture2D tex:
                    sink.Add(tex);
                    break;

                case Material mat:
                    CollectFromMaterial(mat, sink);
                    break;

                case Renderer renderer:
                    foreach (var m in renderer.sharedMaterials) CollectFromMaterial(m, sink);
                    break;

                case AnimationClip clip:
                    CollectFromClip(clip, sink);
                    break;

                case GameObject go:
                    // A GameObject stands in for everything renderable beneath it, which is the
                    // most intuitive behaviour when a user drags in an outfit or a body part.
                    // 游戏对象代表其下所有可渲染内容，
                    // 这是用户拖入一套服装或身体部件时最符合直觉的行为。
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var m in r.sharedMaterials) CollectFromMaterial(m, sink);
                    }

                    break;

                case Mesh mesh:
                    // A mesh alone carries no textures, but users may whitelist one meaning
                    // "do not touch anything drawn with this mesh". Handled by the caller,
                    // which matches renderers against whitelisted meshes.
                    // 网格本身不携带贴图，但用户列入白名单通常意为
                    // “不要动用这个网格绘制的任何东西”。由调用方将渲染器与白名单网格做匹配处理。
                    break;
            }
        }

        private static void CollectFromMaterial(Material mat, HashSet<Texture2D> sink)
        {
            if (mat == null || mat.shader == null) return;

            foreach (var propName in mat.GetTexturePropertyNames())
            {
                if (mat.GetTexture(propName) is Texture2D tex && tex != null)
                {
                    sink.Add(tex);
                }
            }
        }

        private static void CollectFromClip(AnimationClip clip, HashSet<Texture2D> sink)
        {
            if (clip == null) return;

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null) continue;

                foreach (var key in keys)
                {
                    switch (key.value)
                    {
                        case Texture2D tex:
                            sink.Add(tex);
                            break;
                        case Material mat:
                            CollectFromMaterial(mat, sink);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Returns true when a whitelist contains a mesh matching the renderer's mesh.
        /// 当白名单包含与渲染器网格匹配的网格时返回 true。
        /// </summary>
        public static bool IsRendererWhitelistedByMesh(
            Renderer renderer, IEnumerable<Object> entries)
        {
            if (renderer == null || entries == null) return false;

            Mesh rendererMesh = null;
            if (renderer is SkinnedMeshRenderer smr) rendererMesh = smr.sharedMesh;
            else if (renderer.TryGetComponent<MeshFilter>(out var mf)) rendererMesh = mf.sharedMesh;

            if (rendererMesh == null) return false;

            foreach (var e in entries)
            {
                if (e is Mesh m && m == rendererMesh) return true;
                if (e is Renderer r && r == renderer) return true;
                if (e is GameObject go && go == renderer.gameObject) return true;
            }

            return false;
        }
    }
}

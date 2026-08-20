// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// Expands the user whitelist into concrete "skip everything" sets.
    /// Whitelist objects may be meshes, materials, textures, animation clips,
    /// game objects, etc. Any texture referenced by a whitelisted object skips all
    /// optimization (including later parameter optimization); textures sharing a UV
    /// set with a skipped texture skip atlas-ization but still get whole-texture scaling
    /// and import-parameter optimization.
    ///
    /// 将用户白名单展开为具体的"全部跳过"集合。白名单对象可为网格/材质/贴图/动画/物体等。
    /// 白名单对象引用的贴图跳过所有优化（含后续参数优化）；与被跳过贴图同 UV 的贴图跳过
    /// 图集化，但仍参与整图缩放与导入参数优化。
    /// </summary>
    public sealed class ATOWhitelist
    {
        public HashSet<Texture2D> Textures = new HashSet<Texture2D>();
        public HashSet<Material> Materials = new HashSet<Material>();
        public HashSet<Mesh> Meshes = new HashSet<Mesh>();
        public HashSet<Renderer> Renderers = new HashSet<Renderer>();
        public HashSet<GameObject> GameObjects = new HashSet<GameObject>();

        public bool IsEmpty =>
            Textures.Count == 0 && Materials.Count == 0 && Meshes.Count == 0 &&
            Renderers.Count == 0 && GameObjects.Count == 0;

        public void Build(IEnumerable<Object> whitelist)
        {
            var queue = new Queue<Object>();
            foreach (var o in whitelist)
                if (o != null) queue.Enqueue(o);

            var visited = new HashSet<Object>();

            while (queue.Count > 0)
            {
                var obj = queue.Dequeue();
                if (obj == null || !visited.Add(obj)) continue;

                switch (obj)
                {
                    case Texture2D tex:
                        Textures.Add(tex);
                        break;

                    case Material mat:
                        Materials.Add(mat);
                        foreach (var t in GetMaterialTextures(mat))
                            if (t != null) { Textures.Add(t); }
                        break;

                    case Mesh mesh:
                        Meshes.Add(mesh);
                        break;

                    case Renderer r:
                        Renderers.Add(r);
                        GameObjects.Add(r.gameObject);
                        foreach (var t in GetRendererTextures(r))
                            if (t != null) Textures.Add(t);
                        break;

                    case GameObject go:
                        GameObjects.Add(go);
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            Renderers.Add(r);
                            foreach (var t in GetRendererTextures(r))
                                if (t != null) Textures.Add(t);
                        }
                        break;

                    case AnimationClip clip:
                        foreach (var t in GetAnimationClipTextures(clip))
                            if (t != null) Textures.Add(t);
                        break;

                    case RuntimeAnimatorController controller:
                        foreach (var c in controller.animationClips)
                            foreach (var t in GetAnimationClipTextures(c))
                                if (t != null) Textures.Add(t);
                        break;
                }
            }
        }

        /// <summary>Is this texture referenced by any whitelisted object? 该贴图是否被白名单对象引用？</summary>
        public bool ContainsTexture(Texture2D tex) => tex != null && Textures.Contains(tex);
        public bool ContainsMaterial(Material mat) => mat != null && Materials.Contains(mat);
        public bool ContainsMesh(Mesh mesh) => mesh != null && Meshes.Contains(mesh);
        public bool ContainsRenderer(Renderer r) => r != null && (Renderers.Contains(r) || GameObjects.Contains(r.gameObject));

        private static IEnumerable<Texture2D> GetMaterialTextures(Material mat)
        {
            foreach (var name in mat.GetTexturePropertyNames())
            {
                var t = mat.GetTexture(name) as Texture2D;
                if (t != null) yield return t;
            }
        }

        private static IEnumerable<Texture2D> GetRendererTextures(Renderer r)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null)
                    foreach (var t in GetMaterialTextures(m))
                        yield return t;
        }

        private static IEnumerable<Texture2D> GetAnimationClipTextures(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (binding.type != typeof(Material) && binding.type != typeof(Texture) &&
                    binding.type != typeof(Texture2D)) continue;

                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null) continue;
                foreach (var kf in curve)
                {
                    if (kf.value is Texture2D t) yield return t;
                    if (kf.value is Material m)
                        foreach (var mt in GetMaterialTextures(m))
                            yield return mt;
                }
            }
        }
    }
}

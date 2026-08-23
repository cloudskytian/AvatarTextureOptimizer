using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal static class WhitelistResolver
    {
        public static HashSet<Texture2D> Resolve(IEnumerable<Object> roots)
        {
            var result = new HashSet<Texture2D>();
            // Public serialized fields and BeforeAnalysis extensions can assign null after OnValidate has run.
            // Treat that state as an empty whitelist at the actual build boundary. / 公开字段或扩展可在
            // OnValidate 后写入 null；真正的构建边界必须把它安全解释为空白名单。
            if (roots == null) return result;

            var seeds = new List<Object>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                seeds.Add(root);
                if (root is GameObject gameObject)
                {
                    foreach (var component in gameObject.GetComponentsInChildren<Component>(true))
                        if (component != null) seeds.Add(component);
                }
            }

            if (seeds.Count == 0) return result;
            foreach (var dependency in EditorUtility.CollectDependencies(seeds.ToArray()))
            {
                if (dependency is Texture2D texture) result.Add(texture);
                if (!(dependency is Material material) || material.shader == null) continue;
                var count = ShaderUtil.GetPropertyCount(material.shader);
                for (var i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(material.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    if (material.GetTexture(ShaderUtil.GetPropertyName(material.shader, i)) is Texture2D materialTexture)
                        result.Add(materialTexture);
                }
            }
            return result;
        }
    }
}

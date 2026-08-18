// Copyright (c) fosa. Licensed under the MIT License.
// Clones materials and swaps ONLY texture references. No other shader parameter is ever
// touched -- this is a hard project rule, because any other change alters authored appearance.
// 克隆材质并**仅**替换贴图引用。绝不修改任何其他着色器参数——
// 这是项目硬性规则，因为任何其他改动都会改变作者设定的外观。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Produces optimized material variants and deduplicates identical results.
    /// 生成优化后的材质变体并对完全相同的结果去重。
    /// </summary>
    public sealed class MaterialRemapper
    {
        private readonly ATOLogger _log;
        private readonly Dictionary<Material, Material> _clones =
            new Dictionary<Material, Material>();

        /// <summary>Materials created by this remapper, for asset saving. / 该重映射器创建的材质，用于资产保存。</summary>
        public IReadOnlyCollection<Material> CreatedMaterials => _clones.Values;

        /// <summary>Creates a remapper. / 创建重映射器。</summary>
        public MaterialRemapper(ATOLogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Returns a clone of <paramref name="source" /> with its textures replaced according to
        /// <paramref name="textureMap" />. Materials with no replaced texture are returned as-is
        /// so untouched materials are never needlessly duplicated.
        /// 返回 <paramref name="source" /> 的克隆，其贴图按 <paramref name="textureMap" /> 替换。
        /// 没有任何贴图被替换的材质原样返回，从而不会无谓地复制未改动的材质。
        /// </summary>
        public Material Remap(
            Material source, IReadOnlyDictionary<Texture2D, Texture2D> textureMap)
        {
            if (source == null || source.shader == null) return source;
            if (_clones.TryGetValue(source, out var existing)) return existing;

            var propertyNames = source.GetTexturePropertyNames();
            var replacements = new List<(string prop, Texture2D tex)>();

            foreach (var prop in propertyNames)
            {
                if (!(source.GetTexture(prop) is Texture2D tex) || tex == null) continue;
                if (!textureMap.TryGetValue(tex, out var replacement)) continue;
                if (replacement == null || replacement == tex) continue;

                replacements.Add((prop, replacement));
            }

            if (replacements.Count == 0)
            {
                // Nothing to change: reuse the original material.
                // 无需改动：复用原材质。
                _clones[source] = source;
                return source;
            }

            var clone = new Material(source)
            {
                name = TextureOutput.NamePrefix + source.name,
            };

            foreach (var (prop, tex) in replacements)
            {
                // ONLY the texture reference changes. Scale/offset, colours, floats, keywords,
                // render queue and every other property are inherited untouched from the clone.
                // **只有**贴图引用发生变化。缩放/偏移、颜色、浮点、关键字、
                // 渲染队列及所有其他属性都从克隆中原样继承。
                clone.SetTexture(prop, tex);
            }

            _clones[source] = clone;
            _log?.Detail($"Remapped material {source.name}: {replacements.Count} textures");
            return clone;
        }

        /// <summary>
        /// Merges materials that ended up byte-for-byte equivalent, reducing draw calls.
        /// Two materials merge only when their shader, all texture references and all property
        /// values match, so merging can never change how anything renders.
        /// 合并最终完全等价的材质以减少 draw call。
        /// 只有当着色器、所有贴图引用与所有属性值都一致时才合并，
        /// 因此合并绝不会改变任何渲染结果。
        /// </summary>
        public Dictionary<Material, Material> BuildDeduplication(IEnumerable<Material> materials)
        {
            var mapping = new Dictionary<Material, Material>();
            var byKey = new Dictionary<string, Material>(StringComparer.Ordinal);

            foreach (var mat in materials)
            {
                if (mat == null) continue;
                if (mapping.ContainsKey(mat)) continue;

                var key = BuildMaterialKey(mat);
                if (byKey.TryGetValue(key, out var rep))
                {
                    mapping[mat] = rep;
                }
                else
                {
                    byKey[key] = mat;
                    mapping[mat] = mat;
                }
            }

            return mapping;
        }

        /// <summary>
        /// Builds an identity key covering every property that affects rendering.
        /// 构建覆盖所有影响渲染的属性的身份键。
        /// </summary>
        private static string BuildMaterialKey(Material mat)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append(mat.shader != null ? mat.shader.name : "<none>");
            sb.Append('|').Append(mat.renderQueue);

            var keywords = mat.shaderKeywords;
            if (keywords != null && keywords.Length > 0)
            {
                // Copy before sorting: never mutate an array handed to us by the engine.
                // 排序前先复制：绝不修改引擎交给我们的数组。
                var sorted = new string[keywords.Length];
                Array.Copy(keywords, sorted, keywords.Length);
                Array.Sort(sorted, StringComparer.Ordinal);
                foreach (var k in sorted) sb.Append('|').Append(k);
            }

            var shader = mat.shader;
            if (shader == null) return sb.ToString();

            // Use the modern Shader reflection API, matching ShaderAnalyzer.
            // 使用现代 Shader 反射 API，与 ShaderAnalyzer 保持一致。
            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                sb.Append('|').Append(name).Append('=');

                switch (type)
                {
                    case ShaderPropertyType.Color:
                        sb.Append(mat.GetColor(name));
                        break;
                    case ShaderPropertyType.Vector:
                        sb.Append(mat.GetVector(name));
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        sb.Append(mat.GetFloat(name).ToString("R"));
                        break;
                    case ShaderPropertyType.Int:
                        sb.Append(mat.GetFloat(name).ToString("R"));
                        break;
                    case ShaderPropertyType.Texture:
                        var tex = mat.GetTexture(name);
                        sb.Append(tex != null ? tex.GetInstanceID().ToString() : "0");
                        sb.Append(',').Append(mat.GetTextureScale(name));
                        sb.Append(',').Append(mat.GetTextureOffset(name));
                        break;
                }
            }

            return sb.ToString();
        }
    }
}

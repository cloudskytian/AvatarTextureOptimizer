// SPDX-License-Identifier: MIT
// EN: Material rewriting (texture references only), material/texture deduplication and material slot
//     merging. No shader parameter other than the texture references is ever touched.
// ZH: 材质重写（只改贴图引用）、材质/贴图去重以及材质槽合并。
//     除贴图引用外，绝不修改任何其他着色器参数。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Rewrites material texture references and performs the optional deduplication passes.
    /// ZH: 重写材质的贴图引用，并执行可选的去重流程。
    /// </summary>
    public sealed class ATOMaterialRewriter
    {
        private readonly ATOLog _log;
        private readonly Dictionary<Material, Material> _clones = new Dictionary<Material, Material>();

        public ATOMaterialRewriter(ATOLog log)
        {
            _log = log;
        }

        /// <summary>EN: original material -&gt; rewritten material. ZH: 原材质 -&gt; 重写后的材质。</summary>
        public IReadOnlyDictionary<Material, Material> Clones => _clones;

        /// <summary>
        /// EN: Creates (or returns) the rewritten variant of a material with the new texture references.
        /// ZH: 创建（或返回）替换过贴图引用的材质副本。
        /// </summary>
        public Material Rewrite(Material material, Func<Texture2D, Texture2D> textureMapping)
        {
            if (material == null) return null;
            if (_clones.TryGetValue(material, out var existing)) return existing;

            var shader = material.shader;
            if (shader == null) return material;

            Material clone = null;
            var count = shader.GetPropertyCount();

            for (var i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var name = shader.GetPropertyName(i);
                var tex = material.GetTexture(name) as Texture2D;
                if (tex == null) continue;

                var replacement = textureMapping(tex);
                if (replacement == null || ReferenceEquals(replacement, tex)) continue;

                if (clone == null)
                {
                    clone = new Material(material) { name = material.name + "_ATO" };
                }

                // EN: Only the texture reference changes; scale/offset stay exactly as authored.
                // ZH: 只替换贴图引用；scale/offset 完全保持原样。
                var scale = material.GetTextureScale(name);
                var offset = material.GetTextureOffset(name);
                clone.SetTexture(name, replacement);
                clone.SetTextureScale(name, scale);
                clone.SetTextureOffset(name, offset);
            }

            var result = clone ?? material;
            _clones[material] = result;
            if (clone != null) _log.Trace("material", $"'{material.name}' rewritten");
            return result;
        }

        // ------------------------------------------------------------------ deduplication

        /// <summary>
        /// EN: Deduplicates materials whose full property set is identical.
        /// ZH: 对属性完全一致的材质做去重。
        /// </summary>
        public Dictionary<Material, Material> DeduplicateMaterials(IEnumerable<Material> materials)
        {
            var bySignature = new Dictionary<string, Material>();
            var mapping = new Dictionary<Material, Material>();

            foreach (var material in materials)
            {
                if (material == null) continue;
                var signature = ComputeMaterialSignature(material);
                if (bySignature.TryGetValue(signature, out var canonical))
                {
                    if (!ReferenceEquals(canonical, material))
                    {
                        mapping[material] = canonical;
                        _log.Trace("dedup", $"material '{material.name}' -> '{canonical.name}'");
                    }
                }
                else
                {
                    bySignature[signature] = material;
                }
            }

            if (mapping.Count > 0) _log.Info("dedup", $"merged {mapping.Count} materials");
            return mapping;
        }

        /// <summary>
        /// EN: Builds a stable signature covering the shader, keywords, render state and every property.
        /// ZH: 构建覆盖着色器、关键字、渲染状态与所有属性的稳定签名。
        /// </summary>
        public static string ComputeMaterialSignature(Material material)
        {
            var sb = new StringBuilder();
            sb.Append(material.shader != null ? material.shader.name : "<null>");
            sb.Append('|').Append(material.renderQueue);
            sb.Append('|').Append((int)material.globalIlluminationFlags);
            sb.Append('|').Append(material.doubleSidedGI);
            sb.Append('|').Append(material.enableInstancing);

            var keywords = new List<string>(material.shaderKeywords);
            keywords.Sort(StringComparer.Ordinal);
            foreach (var k in keywords) sb.Append('|').Append(k);

            var shader = material.shader;
            if (shader == null) return sb.ToString();

            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                sb.Append('|').Append(name).Append('=');
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        sb.Append(material.GetColor(name).ToString("F6"));
                        break;
                    case ShaderPropertyType.Vector:
                        sb.Append(material.GetVector(name).ToString("F6"));
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        sb.Append(material.GetFloat(name).ToString("F6"));
                        break;
                    case ShaderPropertyType.Texture:
                        var tex = material.GetTexture(name);
                        sb.Append(tex != null ? tex.GetInstanceID().ToString() : "null");
                        sb.Append('@').Append(material.GetTextureScale(name).ToString("F6"));
                        sb.Append('+').Append(material.GetTextureOffset(name).ToString("F6"));
                        break;
                    case ShaderPropertyType.Int:
                        sb.Append(material.GetInt(name));
                        break;
                }
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------ slot merging

        /// <summary>
        /// EN: Merges neighbouring material slots that ended up with the same material, provided no
        ///     animation switches those slots individually. Sub meshes are concatenated accordingly.
        /// ZH: 合并最终使用同一材质的材质槽（前提是没有动画单独切换这些槽），并相应地合并子网格。
        /// </summary>
        public bool TryMergeSlots(Renderer renderer, Mesh mesh, HashSet<int> animatedSlots, out Mesh mergedMesh,
            out Material[] mergedMaterials, out int[] slotRemap)
        {
            mergedMesh = mesh;
            mergedMaterials = renderer.sharedMaterials;
            slotRemap = null;

            if (mesh == null) return false;
            var materials = renderer.sharedMaterials;
            if (materials.Length < 2 || mesh.subMeshCount != materials.Length) return false;
            if (animatedSlots != null && animatedSlots.Count > 0) return false;

            var groups = new List<(Material material, List<int> slots)>();
            foreach (var (material, index) in EnumerateWithIndex(materials))
            {
                var found = false;
                foreach (var g in groups)
                {
                    if (!ReferenceEquals(g.material, material)) continue;
                    g.slots.Add(index);
                    found = true;
                    break;
                }

                if (!found) groups.Add((material, new List<int> { index }));
            }

            if (groups.Count == materials.Length) return false; // EN: nothing to merge. ZH: 无需合并。

            var newMesh = UnityEngine.Object.Instantiate(mesh);
            newMesh.name = mesh.name + "_ATOMerged";
            newMesh.subMeshCount = groups.Count;

            slotRemap = new int[materials.Length];
            var newMaterials = new Material[groups.Count];

            for (var g = 0; g < groups.Count; g++)
            {
                var triangles = new List<int>();
                foreach (var slot in groups[g].slots)
                {
                    triangles.AddRange(mesh.GetTriangles(slot));
                    slotRemap[slot] = g;
                }

                newMesh.SetTriangles(triangles, g, true);
                newMaterials[g] = groups[g].material;
            }

            newMesh.RecalculateBounds();
            mergedMesh = newMesh;
            mergedMaterials = newMaterials;

            _log.Info("dedup",
                $"'{renderer.name}': merged material slots {materials.Length} -> {groups.Count}");
            return true;
        }

        private static IEnumerable<(T value, int index)> EnumerateWithIndex<T>(IReadOnlyList<T> list)
        {
            for (var i = 0; i < list.Count; i++) yield return (list[i], i);
        }
    }
}

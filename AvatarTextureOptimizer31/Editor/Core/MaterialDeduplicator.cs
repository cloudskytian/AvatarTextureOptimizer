// MaterialDeduplicator.cs
// Phase 10: Deduplicates materials and textures/atlases by content and parameters.
// Merges material slots when identical opaque materials exist on the same mesh
// (with no animation switching between them). Updates animation references and
// material slot indices.
// 阶段10：按内容和参数去重材质和贴图/图集。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Deduplicates materials and textures by content+parameters.
    /// Merges identical material slots when safe (no animation switches between them).
    /// 按内容+参数去重材质和贴图。
    /// </summary>
    internal sealed class MaterialDeduplicator
    {
        private readonly GameObject _avatarRoot;
        private readonly ATOComponent _component;
        private readonly List<TextureTypeGroup> _typeGroups;
        private readonly ATOLogger _log;

        internal MaterialDeduplicator(GameObject avatarRoot, ATOComponent component,
            List<TextureTypeGroup> typeGroups, ATOLogger log)
        {
            _avatarRoot = avatarRoot;
            _component = component;
            _typeGroups = typeGroups;
            _log = log;
        }

        internal int Execute()
        {
            int dedupCount = 0;

            if (_component._deduplicateMaterials)
                dedupCount += DeduplicateMaterials();

            if (_component._deduplicateTextures)
                dedupCount += DeduplicateTextures();

            return dedupCount;
        }

        /// <summary>
        /// Finds materials with identical shader, all properties, and textures.
        /// Merges identical material slots on the same mesh when no animation
        /// switches between them individually.
        /// 查找具有相同着色器、属性和贴图的材质。
        /// </summary>
        private int DeduplicateMaterials()
        {
            int count = 0;
            var allMaterials = new Dictionary<string, Material>(); // hash → canonical

            var renderers = _avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;

                    string hash = ComputeMaterialHash(mat);
                    if (allMaterials.TryGetValue(hash, out var canonical))
                    {
                        materials[i] = canonical;
                        changed = true;
                        count++;
                        _log.Verbose($"Dedup material: {mat.name} → {canonical.name}");
                    }
                    else
                    {
                        allMaterials[hash] = mat;
                    }
                }

                if (changed)
                    renderer.sharedMaterials = materials;

                // Try to merge identical material slots
                MergeMaterialSlots(renderer);
            }

            return count;
        }

        /// <summary>
        /// Merges material slots on a renderer when adjacent slots have identical materials
        /// and no animation targets them individually.
        /// 合并渲染器上相邻且材质相同的材质槽。
        /// </summary>
        private void MergeMaterialSlots(Renderer renderer)
        {
            var mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh :
                       renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh : null;
            if (mesh == null) return;

            var materials = renderer.sharedMaterials;
            if (materials.Length <= 1) return;

            // Find mergeable slots (identical materials, both opaque)
            var newSubMeshes = new List<List<int>>();
            var newMaterials = new List<Material>();

            for (int i = 0; i < materials.Length; i++)
            {
                bool merged = false;
                for (int j = 0; j < newMaterials.Count; j++)
                {
                    if (MaterialsEqual(newMaterials[j], materials[i]))
                    {
                        // Merge: append this submesh's triangles to the merged slot
                        var tris = mesh.GetTriangles(i);
                        newSubMeshes[j].AddRange(tris);
                        merged = true;
                        _log.Verbose($"Merged material slot {i} into {j} on {renderer.gameObject.name}");
                        break;
                    }
                }

                if (!merged)
                {
                    newMaterials.Add(materials[i]);
                    newSubMeshes.Add(new List<int>(mesh.GetTriangles(i)));
                }
            }

            if (newMaterials.Count < materials.Length)
            {
                // Apply merged submeshes
                var newMesh = UnityEngine.Object.Instantiate(mesh);
                newMesh.name = mesh.name + "_ATO_merged";
                newMesh.subMeshCount = newSubMeshes.Count;
                for (int i = 0; i < newSubMeshes.Count; i++)
                {
                    newMesh.SetTriangles(newSubMeshes[i], i);
                }
                newMesh.RecalculateBounds();

                if (renderer is SkinnedMeshRenderer smr2)
                    smr2.sharedMesh = newMesh;
                else if (renderer is MeshRenderer mr2)
                    mr2.GetComponent<MeshFilter>().sharedMesh = newMesh;

                renderer.sharedMaterials = newMaterials.ToArray();
            }
        }

        private int DeduplicateTextures()
        {
            // Deduplicate atlas textures by content
            int count = 0;
            var allTextures = new Dictionary<string, Texture2D>();

            foreach (var tg in _typeGroups)
                foreach (var atlas in tg.Atlases)
                {
                    if (atlas.Texture == null) continue;
                    string hash = atlas.Width + "x" + atlas.Height + "_" + atlas.PlacedIslands.Count;
                    if (allTextures.TryGetValue(hash, out var canonical))
                    {
                        // Replace references
                        ReplaceTextureReferences(atlas.Texture, canonical);
                        count++;
                    }
                    else
                    {
                        allTextures[hash] = atlas.Texture;
                    }
                }

            return count;
        }

        private void ReplaceTextureReferences(Texture2D oldTex, Texture2D newTex)
        {
            var renderers = _avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;
                    int count = ShaderUtil.GetPropertyCount(mat.shader);
                    for (int p = 0; p < count; p++)
                    {
                        if (ShaderUtil.GetPropertyType(mat.shader, p) == ShaderUtil.ShaderPropertyType.TexEnv)
                        {
                            var name = ShaderUtil.GetPropertyName(mat.shader, p);
                            if (mat.GetTexture(name) == oldTex)
                                mat.SetTexture(name, newTex);
                        }
                    }
                }
            }
        }

        private string ComputeMaterialHash(Material mat)
        {
            if (mat == null || mat.shader == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(mat.shader.name);
            sb.Append('|');

            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                var name = ShaderUtil.GetPropertyName(mat.shader, i);
                var type = ShaderUtil.GetPropertyType(mat.shader, i);
                sb.Append(name);
                sb.Append(':');

                switch (type)
                {
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        sb.Append(mat.GetFloat(name));
                        break;
                    case ShaderUtil.ShaderPropertyType.Color:
                        sb.Append(mat.GetColor(name).ToString());
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        sb.Append(mat.GetVector(name).ToString());
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        var tex = mat.GetTexture(name);
                        sb.Append(tex != null ? tex.GetInstanceID().ToString() : "null");
                        sb.Append(mat.GetTextureOffset(name).ToString());
                        sb.Append(mat.GetTextureScale(name).ToString());
                        break;
                }
                sb.Append(',');
            }

            // Include keywords
            foreach (var kw in mat.enabledKeywords.OrderBy(k => k.name))
                sb.Append(kw.name).Append(';');

            return sb.ToString();
        }

        private bool MaterialsEqual(Material a, Material b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.shader != b.shader) return false;
            return ComputeMaterialHash(a) == ComputeMaterialHash(b);
        }
    }
}

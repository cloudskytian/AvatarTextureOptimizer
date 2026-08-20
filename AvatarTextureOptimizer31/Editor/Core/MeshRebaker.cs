// MeshRebaker.cs
// Phase 9: Reassigns UV coordinates on meshes to point to the atlas placements,
// and updates material texture references. Creates new mesh assets.
// Only modifies mesh UVs and material texture references — never modifies
// any other shader parameters.
// 阶段9：重新分配网格 UV 坐标指向图集放置位置，更新材质贴图引用。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Rebakes mesh UVs to reference atlas placements and updates material texture references.
    /// 复烘网格 UV 以引用图集放置位置。
    /// </summary>
    internal sealed class MeshRebaker
    {
        private readonly List<UVGroup> _uvGroups;
        private readonly List<TextureTypeGroup> _typeGroups;
        private readonly BuildContext _context;
        private readonly ATOLogger _log;

        internal MeshRebaker(List<UVGroup> uvGroups, List<TextureTypeGroup> typeGroups,
            BuildContext context, ATOLogger log)
        {
            _uvGroups = uvGroups;
            _typeGroups = typeGroups;
            _context = context;
            _log = log;
        }

        internal void Execute()
        {
            // Group islands by source mesh
            var meshIslands = new Dictionary<Mesh, List<UVIsland>>();
            foreach (var ug in _uvGroups)
                foreach (var island in ug.Islands)
                {
                    if (island.SourceMesh == null) continue;
                    if (!meshIslands.TryGetValue(island.SourceMesh, out var list))
                    {
                        list = new List<UVIsland>();
                        meshIslands[island.SourceMesh] = list;
                    }
                    list.Add(island);
                }

            // Reassign UVs on each mesh
            var meshReplacements = new Dictionary<Mesh, Mesh>();
            foreach (var kvp in meshIslands)
            {
                var newMesh = ReassignUVs(kvp.Key, kvp.Value);
                if (newMesh != null)
                    meshReplacements[kvp.Key] = newMesh;
            }

            // Apply mesh replacements to renderers
            ApplyMeshReplacements(meshReplacements);

            // Update material texture references to use atlas textures
            UpdateMaterialReferences();

            _log.Info($"Rebaked {meshReplacements.Count} meshes.");
        }

        /// <summary>
        /// Creates a new mesh with UVs remapped to atlas coordinates.
        /// For UV groups where atlas generation was skipped, keeps original UVs.
        /// 创建新网格，UV 重映射到图集坐标。
        /// </summary>
        private Mesh ReassignUVs(Mesh sourceMesh, List<UVIsland> islands)
        {
            // Clone the mesh (Instantiate creates a deep copy)
            var newMesh = UnityEngine.Object.Instantiate(sourceMesh);
            newMesh.name = sourceMesh.name + "_ATO";
            newMesh.hideFlags = HideFlags.HideAndDontSave;

            // For each UV channel used by islands, remap UVs
            var channelsUsed = islands.Select(i => i.UVChannel).Distinct().ToList();

            foreach (var channel in channelsUsed)
            {
                var channelIslands = islands.Where(i => i.UVChannel == channel).ToList();
                RemapUVChannel(newMesh, sourceMesh, channel, channelIslands);
            }

            // Recalculate bounds
            newMesh.RecalculateBounds();

            return newMesh;
        }

        private void RemapUVChannel(Mesh newMesh, Mesh sourceMesh, int channel, List<UVIsland> islands)
        {
            var uvs = new List<Vector2>();
            sourceMesh.GetUVs(channel, uvs);
            if (uvs.Count == 0) return;

            var newUVs = new Vector2[uvs.Count];
            uvs.CopyTo(newUVs);

            // For each island, find the atlas it belongs to and remap UVs
            foreach (var island in islands)
            {
                if (island.AtlasPlacement.width <= 0 || island.AtlasPlacement.height <= 0) continue;

                // Find the atlas this island is placed in
                var atlas = FindAtlasForIsland(island);
                if (atlas == null) continue;

                // For each vertex in this island's triangles, remap UV from source to atlas
                foreach (var triIdx in island.TriangleIndices)
                {
                    if (triIdx >= newUVs.Length) continue;

                    var origUV = uvs[triIdx];

                    // Compute position within the original texture's pixel bounds
                    float relX = (origUV.x - island.PixelBounds.x) / Mathf.Max(1, island.PixelBounds.width);
                    float relY = (origUV.y - island.PixelBounds.y) / Mathf.Max(1, island.PixelBounds.height);

                    // Handle rotation
                    if (island.Rotation == 90)
                    {
                        float tmp = relX;
                        relX = relY;
                        relY = 1 - tmp;
                    }

                    // Map to atlas UV space
                    float atlasU = (island.AtlasPlacement.x + relX * island.AtlasPlacement.width) / atlas.Width;
                    float atlasV = (island.AtlasPlacement.y + relY * island.AtlasPlacement.height) / atlas.Height;

                    newUVs[triIdx] = new Vector2(atlasU, atlasV);
                }
            }

            newMesh.SetUVs(channel, newUVs);
        }

        private GeneratedAtlas FindAtlasForIsland(UVIsland island)
        {
            if (island.TypeGroup == null) return null;
            foreach (var atlas in island.TypeGroup.Atlases)
            {
                if (atlas.PlacedIslands.Contains(island))
                    return atlas;
            }
            return null;
        }

        private void ApplyMeshReplacements(Dictionary<Mesh, Mesh> meshReplacements)
        {
            var renderers = _context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    if (meshReplacements.TryGetValue(smr.sharedMesh, out var newMesh))
                    {
                        smr.sharedMesh = newMesh;
                        try { _context.AssetSaver.SaveAsset(newMesh); } catch { }
                    }
                }
                else if (renderer is MeshRenderer mr)
                {
                    var filter = mr.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null &&
                        meshReplacements.TryGetValue(filter.sharedMesh, out var newMesh))
                    {
                        filter.sharedMesh = newMesh;
                        try { _context.AssetSaver.SaveAsset(newMesh); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Updates material texture references to point to atlas textures instead of originals.
        /// Only changes texture references — no other shader parameters.
        /// 更新材质贴图引用指向图集纹理。
        /// </summary>
        private void UpdateMaterialReferences()
        {
            var renderers = _context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            var processedMaterials = new HashSet<Material>();

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;

                    // Clone material if persistent
                    if (AssetDatabase.Contains(mat))
                    {
                        mat = new Material(mat);
                        materials[i] = mat;
                        changed = true;
                    }

                    if (processedMaterials.Contains(mat)) continue;
                    processedMaterials.Add(mat);

                    // Replace textures with atlas references
                    ReplaceTexturesWithAtlases(mat);

                    try { _context.AssetSaver.SaveAsset(mat); } catch { }
                }

                if (changed)
                    renderer.sharedMaterials = materials;
            }
        }

        private void ReplaceTexturesWithAtlases(Material mat)
        {
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var name = ShaderUtil.GetPropertyName(mat.shader, i);
                var tex = mat.GetTexture(name);
                if (!(tex is Texture2D t2d)) continue;

                // Find the atlas that contains this texture's islands
                var atlas = FindAtlasForTexture(t2d);
                if (atlas != null && atlas.Texture != null)
                {
                    mat.SetTexture(name, atlas.Texture);
                    _log.Verbose($"Material {mat.name}: replaced {name} → {atlas.Texture.name}");
                }
            }
        }

        private GeneratedAtlas FindAtlasForTexture(Texture2D tex)
        {
            foreach (var tg in _typeGroups)
                foreach (var atlas in tg.Atlases)
                    if (atlas.PlacedIslands.Any(i => i.SourceTexture == tex))
                        return atlas;
            return null;
        }
    }
}

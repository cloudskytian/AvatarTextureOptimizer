// UVMappingBuilder.cs
// Phase 4: Builds the UV-to-texture mapping relationships, UV groups, and
// texture type groups. Handles multi-channel UV, blendshape area, animation
// scale, and UV normalization.
// 阶段4：建立 UV-贴图映射关系、UV 组和贴图类型组。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Util;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Builds the complete mapping of UV islands to textures across the avatar.
    /// Groups textures by companion-map signature (texture type groups) and
    /// groups UVs by shared coordinates (UV groups).
    /// 建立完整的 UV 岛到贴图的映射。
    /// </summary>
    internal sealed class UVMappingBuilder
    {
        private readonly AvatarScanResult _scan;
        private readonly GameObject _avatarRoot;
        private readonly ATOComponent _component;
        private readonly AdvancedSettings _settings;
        private readonly ATOLogger _log;

        private int _nextIslandId = 0;
        private int _nextUVGroupId = 0;
        private int _nextTypeGroupId = 0;

        internal UVMappingBuilder(AvatarScanResult scan, GameObject avatarRoot,
            ATOComponent component, AdvancedSettings settings, ATOLogger log)
        {
            _scan = scan;
            _avatarRoot = avatarRoot;
            _component = component;
            _settings = settings;
            _log = log;
        }

        internal (List<UVGroup> uvGroups, List<TextureTypeGroup> typeGroups) Build()
        {
            // Step 1: Extract islands from each renderer's mesh
            var allIslands = new List<UVIsland>();
            var islandToUVGroupKey = new Dictionary<UVIsland, string>();

            foreach (var slotInfo in _scan.MaterialSlots)
            {
                if (slotInfo.Renderer == null) continue;
                var mesh = GetMesh(slotInfo.Renderer);
                if (mesh == null) continue;

                foreach (var mat in slotInfo.AllMaterials)
                {
                    if (mat == null) continue;

                    // Get all color-type textures from this material
                    var colorTextures = GetColorTextures(mat, slotInfo.Renderer, slotInfo.SlotIndex);
                    if (colorTextures.Count == 0) continue;

                    foreach (var (tex, propName, uvChannel) in colorTextures)
                    {
                        if (_scan.WhitelistedTextures.Contains(tex)) continue;

                        var islands = UVIslandExtractor.Extract(mesh, uvChannel, slotInfo.SlotIndex,
                            new Vector2(tex.width, tex.height));

                        // Get companion maps
                        var (normalMap, maskMap) = ShaderTextureAnalyzer.GetCompanionMaps(mat, propName);

                        foreach (var extracted in islands)
                        {
                            // Check UV out-of-bounds
                            if (extracted.CrossesWrapSeam)
                            {
                                _log.Warning($"UV island on {tex.name} crosses wrap seam (channel {uvChannel}). " +
                                    "Whitelisting for safety. / UV 岛越界且跨缝，已加入白名单。");
                                _scan.WhitelistedTextures.Add(tex);
                                continue;
                            }

                            var island = new UVIsland
                            {
                                Id = _nextIslandId++,
                                SourceMesh = mesh,
                                SourceRenderer = slotInfo.Renderer,
                                UVChannel = uvChannel,
                                MaterialSlot = slotInfo.SlotIndex,
                                UVBounds = extracted.UVBounds,
                                PixelBounds = extracted.PixelBounds,
                                TriangleIndices = extracted.TriangleIndices,
                                CrossesWrapSeam = extracted.CrossesWrapSeam,
                                ScaledPixelBounds = extracted.PixelBounds,
                                SourceTexture = tex,
                                RasterGranularity = _settings.rasterGranularity,
                            };

                            // Compute pixel density considering blendshapes and animation scale
                            island.PixelDensity = ComputePixelDensity(mesh, tex, island, slotInfo.Renderer);

                            // Determine the type group signature for this texture
                            var typeGroupKey = GetTypeGroupSignature(tex, normalMap, maskMap, mat);

                            allIslands.Add(island);
                            islandToUVGroupKey[island] = typeGroupKey;
                        }
                    }
                }
            }

            // Step 2: Build UV groups (same UV region across textures)
            var uvGroups = BuildUVGroups(allIslands);

            // Step 3: Build texture type groups
            var typeGroups = BuildTypeGroups(allIslands, islandToUVGroupKey);

            // Link islands to their UV groups and type groups
            foreach (var island in allIslands)
            {
                // Find the UV group containing this island
                foreach (var ug in uvGroups)
                {
                    if (ug.Islands.Contains(island))
                    {
                        island.UVGroup = ug;
                        break;
                    }
                }

                // Find the type group
                foreach (var tg in typeGroups)
                {
                    if (tg.AllIslands.Contains(island))
                    {
                        island.TypeGroup = tg;
                        break;
                    }
                }
            }

            return (uvGroups, typeGroups);
        }

        /// <summary>
        /// Groups islands that share the same UV position across different textures
        /// (same mesh, same UV channel, overlapping UV bounds).
        /// 将不同贴图中 UV 位置相同的岛归为同一 UV 组。
        /// </summary>
        private List<UVGroup> BuildUVGroups(List<UVIsland> islands)
        {
            var groups = new List<UVGroup>();

            // Group by (mesh, uvChannel, materialSlot) - islands on the same mesh region
            var keyMap = new Dictionary<string, UVGroup>();

            foreach (var island in islands)
            {
                // UV group key: identifies a unique UV region on a specific mesh
                var key = $"{island.SourceMesh.GetInstanceID()}_{island.UVChannel}_{island.MaterialSlot}";

                if (!keyMap.TryGetValue(key, out var group))
                {
                    group = new UVGroup
                    {
                        Id = _nextUVGroupId++,
                        MaxOriginalDimension = 0
                    };
                    keyMap[key] = group;
                    groups.Add(group);
                }

                group.Islands.Add(island);
                group.AllTextures.Add(island.SourceTexture);

                // Track max original dimension (wood-barrel cap)
                int dim = Mathf.Max(island.SourceTexture.width, island.SourceTexture.height);
                if (dim > group.MaxOriginalDimension)
                    group.MaxOriginalDimension = dim;
            }

            _log.Verbose($"Built {groups.Count} UV groups from {islands.Count} islands.");
            return groups;
        }

        /// <summary>
        /// Groups textures by their companion-map signature:
        /// (hasNormal, hasMask, colorSpace, filterMode).
        /// Textures referenced by both normal and non-normal materials go to the normal group.
        /// 按配套贴图签名分组贴图。
        /// </summary>
        private List<TextureTypeGroup> BuildTypeGroups(List<UVIsland> islands,
            Dictionary<UVIsland, string> signatures)
        {
            var groupMap = new Dictionary<string, TextureTypeGroup>();

            foreach (var island in islands)
            {
                string sig = signatures[island];

                if (!groupMap.TryGetValue(sig, out var tg))
                {
                    tg = new TextureTypeGroup
                    {
                        Id = _nextTypeGroupId++
                    };
                    // Parse signature
                    ParseTypeGroupSignature(sig, tg);
                    groupMap[sig] = tg;
                }

                tg.AllIslands.Add(island);
                if (!tg.PrimaryTextures.Contains(island.SourceTexture))
                    tg.PrimaryTextures.Add(island.SourceTexture);

                // Track companion maps
                // (these are populated during analysis)
            }

            // Link UV groups to type groups
            foreach (var tg in groupMap.Values)
            {
                var uvGroupsInTG = tg.AllIslands.Select(i => i.UVGroup).Distinct().ToList();
                tg.UVGroups = uvGroupsInTG;
            }

            _log.Verbose($"Built {groupMap.Count} texture type groups.");
            return groupMap.Values.ToList();
        }

        private string GetTypeGroupSignature(Texture2D tex, Texture2D normalMap, Texture2D maskMap, Material mat)
        {
            bool hasNormal = normalMap != null;
            bool hasMask = maskMap != null;
            var colorSpace = TextureImporterFormatCheck(tex);
            var filterMode = tex != null ? tex.filterMode : FilterMode.Bilinear;

            return $"N{(hasNormal ? 1 : 0)}M{(hasMask ? 1 : 0)}C{(int)colorSpace}F{(int)filterMode}";
        }

        private void ParseTypeGroupSignature(string sig, TextureTypeGroup tg)
        {
            // Format: N{0|1}M{0|1}C{n}F{n}
            tg.HasNormal = sig.Contains("N1");
            tg.HasMask = sig.Contains("M1");
            // Parse color space and filter mode from signature
            int cIdx = sig.IndexOf('C');
            int fIdx = sig.IndexOf('F');
            if (cIdx >= 0 && cIdx + 1 < sig.Length)
            {
                if (int.TryParse(sig[cIdx + 1].ToString(), out int cs))
                    tg.ColorSpace = (ColorSpace)cs;
            }
            if (fIdx >= 0 && fIdx + 1 < sig.Length)
            {
                if (int.TryParse(sig[fIdx + 1].ToString(), out int fm))
                    tg.FilterMode = (FilterMode)fm;
            }
        }

        private ColorSpace TextureImporterFormatCheck(Texture2D tex)
        {
            if (tex == null) return ColorSpace.sRGB;
            // sRGB = 0, Linear = 1
            return tex.isDataSRGB ? ColorSpace.sRGB : ColorSpace.Linear;
        }

        private List<(Texture2D tex, string propName, int uvChannel)> GetColorTextures(Material mat,
            Renderer renderer, int slotIndex)
        {
            var result = new List<(Texture2D, string, int)>();
            if (mat == null || mat.shader == null) return result;

            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var name = ShaderUtil.GetPropertyName(mat.shader, i);
                var tex = mat.GetTexture(name) as Texture2D;
                if (tex == null) continue;
                if (_scan.WhitelistedTextures.Contains(tex)) continue;

                // Only process color-type textures for atlas mapping
                if (_scan.TextureReferences.TryGetValue(tex, out var refr))
                {
                    if (refr.Category == TextureCategory.Color ||
                        refr.Category == TextureCategory.ColorOpaque ||
                        refr.Category == TextureCategory.Emission)
                    {
                        int uvChannel = ShaderTextureAnalyzer.GetUVChannel(mat, name);
                        result.Add((tex, name, uvChannel));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Computes the effective pixel density for an island, considering:
        /// - Blendshape area (max of 0% and 100% values)
        /// - Animation scale (max scale)
        /// 考虑形态键和动画缩放计算有效像素密度。
        /// </summary>
        private float ComputePixelDensity(Mesh mesh, Texture2D tex, UVIsland island, Renderer renderer)
        {
            if (tex == null || mesh == null) return 1.0f;

            // Base pixel density: island pixel area / mesh world area
            float islandPixelWidth = island.PixelBounds.width;
            float islandPixelHeight = island.PixelBounds.height;

            // Estimate mesh surface area for this island
            float meshArea = EstimateMeshArea(mesh, island.TriangleIndices, renderer);

            if (meshArea <= 0) return _component._maxPixelDensity;

            float texWorldWidth = (tex.width > 0 ? islandPixelWidth / tex.width : 1f);
            float texWorldHeight = (tex.height > 0 ? islandPixelHeight / tex.height : 1f);

            // Factor in blendshapes (take max of weight 0 and 100)
            float blendshapeScale = GetBlendshapeScale(mesh, renderer);
            float animScale = GetAnimationScale(renderer);

            float density = (islandPixelWidth * islandPixelHeight) / (meshArea * blendshapeScale * animScale + 1e-6f);
            return Mathf.Sqrt(density);
        }

        private float EstimateMeshArea(Mesh mesh, List<int> triangleIndices, Renderer renderer)
        {
            var vertices = mesh.vertices;
            float area = 0;
            var transform = renderer.transform;

            for (int i = 0; i < triangleIndices.Count; i += 3)
            {
                int i0 = triangleIndices[i];
                int i1 = triangleIndices[i + 1];
                int i2 = triangleIndices[i + 2];

                if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) continue;

                var v0 = transform.TransformPoint(vertices[i0]);
                var v1 = transform.TransformPoint(vertices[i1]);
                var v2 = transform.TransformPoint(vertices[i2]);

                area += Vector3.Cross(v1 - v0, v2 - v0).magnitude * 0.5f;
            }

            return area;
        }

        /// <summary>
        /// Gets the scale factor from blendshapes. For each blendshape, takes the max
        /// area change between weight 0 and weight 100. Only considers weight 0 and 100
        /// (not intermediate values or negative, per spec, to avoid combinatorial explosion).
        /// 获取形态键的缩放因子。仅取 0 和 100 时二者的最大值。
        /// </summary>
        private float GetBlendshapeScale(Mesh mesh, Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer smr) || mesh == null) return 1f;

            int shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0) return 1f;

            var baseVertices = mesh.vertices;
            if (baseVertices.Length == 0) return 1f;

            // Cache base area for the island's triangles
            var transform = smr.transform;
            float baseArea = 0f;
            // Precompute which vertex indices are in this island's triangles
            var islandVertexSet = new HashSet<int>();
            foreach (var idx in island.TriangleIndices)
            {
                if (idx >= 0 && idx < baseVertices.Length)
                    islandVertexSet.Add(idx);
            }

            // Compute base (weight 0) triangle area for the island
            float areaWeight0 = ComputeTriangleArea(mesh, island.TriangleIndices, baseVertices, transform);

            if (areaWeight0 <= 0f) return 1f;

            float maxScale = 1f;

            for (int s = 0; s < shapeCount; s++)
            {
                int frameCount = mesh.GetBlendShapeFrameCount(s);
                if (frameCount == 0) continue;

                // Find the frame closest to weight 100
                // BlendShape frames have specific weights; we look for the last frame (typically 100)
                int targetFrame = -1;
                float bestWeight = -1f;
                for (int f = 0; f < frameCount; f++)
                {
                    float frameWeight = mesh.GetBlendShapeFrameWeight(s, f);
                    if (frameWeight > bestWeight && frameWeight <= 100f)
                    {
                        bestWeight = frameWeight;
                        targetFrame = f;
                    }
                }

                if (targetFrame < 0) continue;

                // Get delta vertices and normals at weight 100
                var deltaVertices = new Vector3[baseVertices.Length];
                var deltaNormals = new Vector3[baseVertices.Length];
                var deltaTangents = new Vector3[baseVertices.Length];
                mesh.GetBlendShapeFrameVertices(s, targetFrame, deltaVertices, deltaNormals, deltaTangents);

                // Compute modified vertex positions (base + delta)
                var modifiedVertices = new Vector3[baseVertices.Length];
                for (int v = 0; v < baseVertices.Length; v++)
                    modifiedVertices[v] = baseVertices[v] + deltaVertices[v];

                // Compute area at weight 100
                float areaWeight100 = ComputeTriangleArea(mesh, island.TriangleIndices, modifiedVertices, transform);

                // The scale factor is the ratio of max(base, weight100) / base
                // This accounts for blendshapes that stretch the mesh
                float ratio = areaWeight100 / areaWeight0;
                if (ratio > maxScale)
                    maxScale = ratio;
            }

            _log.Verbose($"  Blendshape area scale: {maxScale:F3} (shapes={shapeCount})");
            return maxScale;
        }

        /// <summary>
        /// Computes the world-space area of a set of triangles given vertex positions.
        /// 计算给定顶点位置的三角形集合的世界空间面积。
        /// </summary>
        private float ComputeTriangleArea(Mesh mesh, List<int> triangleIndices, Vector3[] vertices, Transform transform)
        {
            float area = 0f;
            for (int i = 0; i < triangleIndices.Count; i += 3)
            {
                int i0 = triangleIndices[i];
                int i1 = triangleIndices[i + 1];
                int i2 = triangleIndices[i + 2];

                if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) continue;
                if (i0 < 0 || i1 < 0 || i2 < 0) continue;

                var v0 = transform.TransformPoint(vertices[i0]);
                var v1 = transform.TransformPoint(vertices[i1]);
                var v2 = transform.TransformPoint(vertices[i2]);

                area += Vector3.Cross(v1 - v0, v2 - v0).magnitude * 0.5f;
            }
            return area;
        }

        /// <summary>
        /// Gets the maximum animation scale for the renderer by scanning all
        /// animation clips referenced by the avatar's animator controllers.
        /// Returns the maximum axis scale found across all keyframes.
        /// 通过扫描所有动画剪辑获取渲染器的最大动画缩放。
        /// </summary>
        private float GetAnimationScale(Renderer renderer)
        {
            float maxScale = 1f;

            // Start with current local scale as a baseline
            var currentScale = renderer.transform.localScale;
            maxScale = Mathf.Max(currentScale.x, currentScale.y, currentScale.z);

            // Build the animation path relative to avatar root
            string animPath = GetAnimationPath(renderer.transform, _avatarRoot.transform);
            if (string.IsNullOrEmpty(animPath))
            {
                // Renderer is the avatar root itself
                animPath = "";
            }

            // Scan all animation controllers on the avatar
            var animators = _avatarRoot.GetComponentsInChildren<Animator>(true);
            var processedClips = new HashSet<AnimationClip>();

            foreach (var animator in animators)
            {
                if (animator.runtimeAnimatorController == null) continue;
                foreach (var clip in animator.runtimeAnimatorController.animationClips)
                {
                    if (processedClips.Contains(clip)) continue;
                    processedClips.Add(clip);

                    float clipScale = GetMaxScaleFromClip(clip, animPath);
                    if (clipScale > maxScale)
                        maxScale = clipScale;
                }
            }

#if ATO_VRCSDK_PRESENT
            // Also check VRC avatar descriptor layers
            var descriptor = _avatarRoot.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var layer in descriptor.baseAnimationLayers)
                {
                    if (layer.animatorController != null)
                    {
                        foreach (var clip in layer.animatorController.animationClips)
                        {
                            if (processedClips.Contains(clip)) continue;
                            processedClips.Add(clip);
                            float clipScale = GetMaxScaleFromClip(clip, animPath);
                            if (clipScale > maxScale) maxScale = clipScale;
                        }
                    }
                }
            }
#endif

            _log.Verbose($"  Animation scale: {maxScale:F3} (path={animPath})");
            return maxScale;
        }

        /// <summary>
        /// Scans an animation clip for localScale keyframes on the given path
        /// and returns the maximum axis scale across all keyframes.
        /// 扫描动画剪辑中的缩放关键帧，返回最大轴缩放。
        /// </summary>
        private float GetMaxScaleFromClip(AnimationClip clip, string path)
        {
            float maxScale = 1f;
            var bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);

            foreach (var binding in bindings)
            {
                // Match path and check for localScale curves
                if (binding.path != path) continue;
                if (binding.propertyName != "m_LocalScale.x" &&
                    binding.propertyName != "m_LocalScale.y" &&
                    binding.propertyName != "m_LocalScale.z") continue;

                var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.keys.Length == 0) continue;

                foreach (var key in curve.keys)
                {
                    if (key.value > maxScale)
                        maxScale = key.value;
                }
            }

            return maxScale;
        }

        /// <summary>
        /// Gets the animation path (relative to root) for a transform.
        /// 获取 Transform 相对于根的动画路径。
        /// </summary>
        private string GetAnimationPath(Transform target, Transform root)
        {
            if (target == root) return "";
            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }

        private Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (renderer is MeshRenderer mr)
            {
                var filter = mr.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }
            return null;
        }
    }
}

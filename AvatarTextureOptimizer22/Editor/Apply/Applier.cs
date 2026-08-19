// AvatarTextureOptimizer
// File: Editor/Apply/Applier.cs
//
// Applies the optimization result to the avatar:
//   1. For every atlasized UV group, compute the new per-vertex UVs mapping
//      into the canonical atlas layout.
//   2. Clone each affected mesh and split vertices per submesh (a vertex
//      shared by several material slots gets a separate copy per slot, each
//      with its own slot's atlas UVs). Bone weights and blend shapes are
//      rebuilt for the new vertex order.
//   3. AAO UV evacuation: when AAO depends on a rewritten channel, the ORIGINAL
//      UVs are saved to a spare channel and registered via UVUsageCompabilityAPI
//      before the channel is rewritten.
//   4. Reassign textures on materials (base state) and update animation
//      references (texture switches, material-slot switches).
// Only mesh UVs and texture references change; no other material property is
// ever touched.
//
// 将优化结果应用到 Avatar：
//   1. 为每个图集化 UV 组计算映射进规范图集布局的新逐顶点 UV。
//   2. 克隆每个受影响网格并按子网格拆分顶点（被多个材质槽共享的顶点按槽
//      复制，各自携带该槽的图集 UV）。为新的顶点顺序重建骨骼权重与形态键。
//   3. AAO UV 疏散：当 AAO 依赖被重写的通道时，将【原始】UV 保存到备用
//      通道，并在重写前通过 UVUsageCompabilityAPI 注册。
//   4. 在材质（基础状态）上重新赋贴图，并更新动画引用（贴图切换、材质槽
//      切换）。
// 只修改网格 UV 与贴图引用；绝不修改材质的其他任何属性。

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.compat;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.apply
{
    public static class Applier
    {
        public static void Apply(BuildContext context, ATOBuildState state)
        {
            if (state.Atlases.Count == 0 && state.WholeTextureScale.Count == 0) return;

            var stopwatch = new ATOStopwatch("Applier.Apply");

            // 1. Remap meshes. / 重映射网格。
            stopwatch.Begin("remap meshes");
            RemapMeshes(state);
            stopwatch.End("remap meshes");

            // 2. Reassign textures on materials. / 在材质上重新赋贴图。
            stopwatch.Begin("reassign materials");
            ReassignMaterials(state);
            stopwatch.End("reassign materials");

            // 3. Update animation references. / 更新动画引用。
            stopwatch.Begin("update animations");
            AnimationUpdater.Update(state);
            stopwatch.End("update animations");
        }

        // ====================================================================
        // Mesh remapping / 网格重映射
        // ====================================================================

        private static void RemapMeshes(ATOBuildState state)
        {
            // Collect groups per renderer+mesh. / 按渲染器+网格收集组。
            var byRenderer = state.UVGroups
                .Where(g => !g.Whitelisted && !g.SkippedAtlas && g.AtlasIndex >= 0 && g.Islands.Count > 0)
                .GroupBy(g => g.Space.Renderer)
                .ToList();

            foreach (var rendererGroup in byRenderer)
            {
                var renderer = rendererGroup.Key;
                if (renderer == null || state.WhitelistedRenderers.Contains(renderer)) continue;

                var mesh = GetMesh(renderer);
                if (mesh == null) continue;

                var groups = rendererGroup.ToList();
                if (!groups.Any(g => g.Islands.Any(i => i.Raster != null && i.ScaledRect.width > 0)))
                    continue;

                var stopwatch = new ATOStopwatch($"RemapMeshes:{renderer.name}");
                var remapper = new MeshRemapper(mesh, renderer, state);
                stopwatch.Begin("map");
                bool changed = remapper.Remap(groups);
                stopwatch.End("map");
                if (!changed) continue;

                stopwatch.Begin("build");
                var newMesh = remapper.Build();
                stopwatch.End("build");

                if (newMesh == null) continue;
                AssignMesh(renderer, newMesh);
                ATOLog.Info($"[ATO] Remapped mesh {mesh.name} ({renderer.name}): {mesh.vertexCount} -> {newMesh.vertexCount} vertices. / 重映射网格：{mesh.vertexCount} -> {newMesh.vertexCount} 顶点。");
            }
        }

        /// <summary>
        /// Computes per-island, per-vertex atlas UVs and rewrites mesh channels
        /// with vertex splitting. All bookkeeping lives here.
        /// 计算逐岛、逐顶点的图集 UV 并以顶点拆分重写网格通道。全部记账都在
        /// 这里。
        /// </summary>
        private sealed class MeshRemapper
        {
            private readonly Mesh _original;
            private readonly Renderer _renderer;
            private readonly ATOBuildState _state;
            private readonly List<Vector3> _positions = new List<Vector3>();
            private readonly List<Vector3> _normals = new List<Vector3>();
            private readonly List<Vector4> _tangents = new List<Vector4>();
            private readonly List<Color32> _colors = new List<Color32>();
            private readonly List<Vector2>[] _uvs = new List<Vector2>[8];
            private readonly List<BoneWeight> _boneWeights = new List<BoneWeight>();
            // New UV data per (submesh, channel), indexed by ORIGINAL vertex.
            // 每 (子网格, 通道) 的新 UV 数据，按原始顶点索引。
            private readonly Dictionary<(int Submesh, int Channel), Vector2[]> _newChannelUVs =
                new Dictionary<(int, int), Vector2[]>();

            // Per-submesh: original vertex index -> new vertex index. / 每子网格：原始顶点索引 -> 新顶点索引。
            private readonly Dictionary<int, Dictionary<int, int>> _remap = new Dictionary<int, Dictionary<int, int>>();
            private readonly List<Vector3> _newPositions = new List<Vector3>();
            private readonly List<Vector3> _newNormals = new List<Vector3>();
            private readonly List<Vector4> _newTangents = new List<Vector4>();
            private readonly List<Color32> _newColors = new List<Color32>();
            private readonly List<Vector2>[] _newUVs = new List<Vector2>[8];
            private readonly List<BoneWeight> _newBoneWeights = new List<BoneWeight>();
            private readonly List<int[]> _newIndices = new List<int[]>();

            // AAO evacuation plans / AAO 疏散计划
            private readonly List<(int Channel, int Spare)> _evacuations = new List<(int, int)>();

            public MeshRemapper(Mesh mesh, Renderer renderer, ATOBuildState state)
            {
                _original = mesh;
                _renderer = renderer;
                _state = state;
                _positions.AddRange(mesh.vertices);
                _normals.AddRange(mesh.normals);
                _tangents.AddRange(mesh.tangents);
                _colors.AddRange(mesh.colors32);
                for (int c = 0; c < 8; c++)
                {
                    _uvs[c] = new List<Vector2>();
                    try { mesh.GetUVs(c, _uvs[c]); } catch { }
                    _newUVs[c] = new List<Vector2>();
                }
                _boneWeights.AddRange(mesh.boneWeights);
            }

            /// <summary>
            /// Compute new UVs per channel (indexed by ORIGINAL vertex) and plan
            /// AAO evacuation. Returns true when anything will change.
            /// 计算每通道的新 UV（按原始顶点索引）并规划 AAO 疏散。有任何
            /// 变化时返回 true。
            /// </summary>
            public bool Remap(List<UVGroup> groups)
            {
                bool any = false;
                // Groups are unique per (renderer, slot, channel). Each group
                // writes its own (submesh, channel) UV array, so vertices
                // shared across material slots get per-slot UVs (vertex split).
                // 组按 (渲染器, 槽, 通道) 唯一。每个组写入自己的 (子网格, 通道)
                // UV 数组，使跨材质槽共享的顶点获得按槽的 UV（顶点拆分）。
                foreach (var group in groups)
                {
                    int channel = group.Space.UVChannel;
                    if (channel < 0 || channel >= 8) continue;
                    int submesh = Mathf.Clamp(group.Space.MaterialSlot, 0, _original.subMeshCount - 1);

                    // AAO evacuation: save originals before rewriting.
                    // AAO 疏散：重写前保存原始 UV。
                    PlanEvacuation(submesh, channel);

                    var newUVs = new Vector2[_original.vertexCount];
                    bool hasData = false;
                    foreach (var island in group.Islands)
                    {
                        if (island.Raster == null || island.ScaledRect.width <= 0) continue;
                        var rect = island.ScaledRect;
                        float atlasW = LayoutWidth(group);
                        float atlasH = LayoutHeight(group);
                        if (atlasW <= 0 || atlasH <= 0) continue;

                        var b = island.BoundsUV;
                        float bw = b.width > 1e-6f ? b.width : 1f;
                        float bh = b.height > 1e-6f ? b.height : 1f;

                        foreach (var v in island.Vertices)
                        {
                            if (v < 0 || v >= _uvs[channel].Count) continue;
                            var uv = _uvs[channel][v];
                            float nx = (uv.x - b.xMin) / bw;
                            float ny = (uv.y - b.yMin) / bh;
                            nx = Mathf.Clamp01(nx);
                            ny = Mathf.Clamp01(ny);

                            float u, vv;
                            if (island.RotatedInAtlas)
                            {
                                // Transposed placement: (nx, ny) -> (ny, 1-nx).
                                // 转置放置：(nx, ny) -> (ny, 1-nx)。
                                u = (rect.x + ny * rect.width) / atlasW;
                                vv = (rect.y + (1f - nx) * rect.height) / atlasH;
                            }
                            else
                            {
                                u = (rect.x + nx * rect.width) / atlasW;
                                vv = (rect.y + ny * rect.height) / atlasH;
                            }
                            newUVs[v] = new Vector2(u, vv);
                            hasData = true;
                        }
                    }
                    if (hasData)
                    {
                        _newChannelUVs[(submesh, channel)] = newUVs;
                        any = true;
                    }
                }
                return any;
            }

            private float LayoutWidth(UVGroup group)
            {
                var layout = _state.Layouts.FirstOrDefault(l =>
                    l.Groups.Any(g => g.AtlasIndex == group.AtlasIndex));
                return layout != null ? layout.Width : 0;
            }

            private float LayoutHeight(UVGroup group)
            {
                var layout = _state.Layouts.FirstOrDefault(l =>
                    l.Groups.Any(g => g.AtlasIndex == group.AtlasIndex));
                return layout != null ? layout.Height : 0;
            }

            private void PlanEvacuation(int submesh, int channel)
            {
#if ATO_AAO
                if (_renderer is SkinnedMeshRenderer smr && AAOUVUsage.IsTexCoordUsed(smr, channel))
                {
                    int spare = AAOUVUsage.FindSpareChannel(_original, smr, channel);
                    if (spare >= 0 && spare != channel)
                    {
                        _evacuations.Add((channel, spare));
                        // The spare channel gets the ORIGINAL UVs of this channel
                        // for this submesh's vertices (per-submesh copy).
                        // 备用通道获得该子网格顶点在该通道的【原始】UV（按子网格复制）。
                        var spareUVs = new Vector2[_original.vertexCount];
                        for (int i = 0; i < _uvs[channel].Count && i < _original.vertexCount; i++)
                            spareUVs[i] = _uvs[channel][i];
                        _newChannelUVs[(submesh, spare)] = spareUVs;
                        ATOLog.Info($"[ATO] AAO UV evacuation: {_renderer.name} uv{channel} -> uv{spare}. / AAO UV 疏散：{_renderer.name} uv{channel} -> uv{spare}。");
                    }
                }
#endif
            }

            /// <summary>
            /// Build the new mesh with per-submesh vertex splitting.
            /// 构建带逐子网格顶点拆分的新网格。
            /// </summary>
            public Mesh Build()
            {
                int submeshCount = _original.subMeshCount;
                for (int s = 0; s < submeshCount; s++)
                {
                    var indices = _original.GetIndices(s);
                    var slotMap = new Dictionary<int, int>();
                    var newIndices = new int[indices.Length];
                    for (int i = 0; i < indices.Length; i++)
                    {
                        int ov = indices[i];
                        if (!slotMap.TryGetValue(ov, out int nv))
                        {
                            nv = AddVertex(ov, s);
                            slotMap[ov] = nv;
                        }
                        newIndices[i] = nv;
                    }
                    _remap[s] = slotMap;
                    _newIndices.Add(newIndices);
                }

                // Build the mesh. / 构建网格。
                var mesh = new Mesh
                {
                    name = _original.name + " (ATO)",
                };
                mesh.SetVertices(_newPositions);
                if (_newNormals.Count == _newPositions.Count) mesh.SetNormals(_newNormals);
                if (_newTangents.Count == _newPositions.Count) mesh.SetTangents(_newTangents);
                if (_newColors.Count == _newPositions.Count) mesh.SetColors(_newColors);
                for (int c = 0; c < 8; c++)
                    if (_newUVs[c].Count == _newPositions.Count)
                        mesh.SetUVs(c, _newUVs[c]);
                mesh.boneWeights = _newBoneWeights.ToArray();
                mesh.subMeshCount = _newIndices.Count;
                for (int s = 0; s < _newIndices.Count; s++)
                    mesh.SetIndices(_newIndices[s], MeshTopology.Triangles, s);
                mesh.RecalculateBounds();

                RebuildBlendShapes(mesh);
                CommitEvacuations();

                return mesh;
            }

            private int AddVertex(int originalVertex, int submesh)
            {
                int nv = _newPositions.Count;
                _newPositions.Add(originalVertex < _positions.Count ? _positions[originalVertex] : Vector3.zero);
                _newNormals.Add(originalVertex < _normals.Count ? _normals[originalVertex] : Vector3.up);
                _newTangents.Add(originalVertex < _tangents.Count ? _tangents[originalVertex] : new Vector4(1, 0, 0, 1));
                _newColors.Add(originalVertex < _colors.Count ? _colors[originalVertex] : new Color32(255, 255, 255, 255));
                _newBoneWeights.Add(originalVertex < _boneWeights.Count ? _boneWeights[originalVertex] : new BoneWeight());

                for (int c = 0; c < 8; c++)
                {
                    Vector2 uv = originalVertex < _uvs[c].Count ? _uvs[c][originalVertex] : Vector2.zero;
                    // Overwrite remapped/evacuated channels with the new value.
                    // 用新值覆盖被重写/疏散的通道（按子网格查表）。
                    if (_newChannelUVs.TryGetValue((submesh, c), out var newData) &&
                        originalVertex < newData.Length)
                        uv = newData[originalVertex];
                    _newUVs[c].Add(uv);
                }
                return nv;
            }

            private void RebuildBlendShapes(Mesh mesh)
            {
                int shapeCount = _original.blendShapeCount;
                if (shapeCount == 0) return;
                var dv = new Vector3[mesh.vertexCount];
                var dn = new Vector3[mesh.vertexCount];
                var dt = new Vector3[mesh.vertexCount];

                for (int s = 0; s < shapeCount; s++)
                {
                    string name = _original.GetBlendShapeName(s);
                    int frameCount = _original.GetBlendShapeFrameCount(s);
                    for (int f = 0; f < frameCount; f++)
                    {
                        float weight = _original.GetBlendShapeFrameWeight(s, f);
                        var origDv = new Vector3[_original.vertexCount];
                        var origDn = new Vector3[_original.vertexCount];
                        var origDt = new Vector3[_original.vertexCount];
                        _original.GetBlendShapeFrameVertices(s, f, origDv, origDn, origDt);

                        Array.Clear(dv, 0, dv.Length);
                        Array.Clear(dn, 0, dn.Length);
                        Array.Clear(dt, 0, dt.Length);

                        foreach (var kv in _remap)
                        {
                            foreach (var pair in kv.Value)
                            {
                                int orig = pair.Key;
                                int neu = pair.Value;
                                if (orig < origDv.Length)
                                {
                                    dv[neu] = origDv[orig];
                                    dn[neu] = origDn[orig];
                                    dt[neu] = origDt[orig];
                                }
                            }
                        }
                        mesh.AddBlendShapeFrame(name, weight, dv, dn, dt);
                    }
                }
            }

            private void CommitEvacuations()
            {
#if ATO_AAO
                foreach (var (channel, spare) in _evacuations)
                {
                    if (_renderer is SkinnedMeshRenderer smr)
                        AAOUVUsage.RegisterEvacuation(smr, channel, spare);
                }
#endif
            }
        }

        // ====================================================================
        // Helpers / 辅助
        // ====================================================================

        private static Mesh GetMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf != null ? mf.sharedMesh : null;
                default: return null;
            }
        }

        private static void AssignMesh(Renderer renderer, Mesh mesh)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr:
                    smr.sharedMesh = mesh;
                    break;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        mf.sharedMesh = mesh;
                        // Reattach via sharedMesh assignment to refresh the SRP
                        // mesh. / 通过 sharedMesh 赋值刷新 SRP 网格。
                    }
                    break;
            }
            EditorUtility.SetDirty(renderer);
        }

        // ====================================================================
        // Material reassignment / 材质贴图重赋
        // ====================================================================

        private static void ReassignMaterials(ATOBuildState state)
        {
            foreach (var group in state.UVGroups)
            {
                if (group.Whitelisted) continue;

                foreach (var usage in group.Textures)
                {
                    if (usage.Material == null || usage.Texture == null) continue;

                    Texture2D newTexture = ResolveNewTexture(state, group, usage);
                    if (newTexture == null || newTexture == usage.Texture) continue;

                    if (usage.Material.HasProperty(usage.PropertyName))
                    {
                        usage.Material.SetTexture(usage.PropertyName, newTexture);
                        EditorUtility.SetDirty(usage.Material);
                        ATOLog.Trace($"{usage}: texture -> {newTexture.name}");
                    }
                }
            }
        }

        /// <summary>
        /// Resolve the texture that replaces `usage.Texture`:
        ///  - atlasized: the type-group atlas texture that contains this UV group
        ///  - whole-texture scaled: the scaled copy
        ///  - otherwise: the original texture
        /// 解析替换 usage.Texture 的贴图：
        ///  - 图集化：包含该 UV 组的类型组图集贴图
        ///  - 整图缩放：缩放后的副本
        ///  - 否则：原贴图
        /// </summary>
        public static Texture2D ResolveNewTexture(ATOBuildState state, UVGroup group, TextureUsage usage)
        {
            var tex = usage.Texture;
            if (tex == null) return null;

            // Whitelisted textures keep their reference. / 白名单贴图保持原引用。
            if (state.WhitelistedTextures.Contains(tex)) return tex;

            if (group.SkippedAtlas || !state.Component.GenerateAtlas)
            {
                // Whole-texture scaling: create (or reuse) the scaled copy.
                // 整图缩放：创建（或复用）缩放副本。
                return GetOrCreateWholeTextureCopy(state, tex);
            }

            // Atlas path: find the type-group atlas containing this group.
            // 图集路径：找到包含该组的类型组图集。
            var typeGroup = state.TypeGroups.FirstOrDefault(t => t.Textures.Contains(tex));
            if (typeGroup == null) return tex;

            var atlas = state.Atlases.FirstOrDefault(a =>
                a.TypeGroup == typeGroup && a.LayoutIndex == group.AtlasIndex && a.Texture != null);
            return atlas != null ? atlas.Texture : tex;
        }

        /// <summary>
        /// Create (or reuse) a whole-texture scaled copy per WholeTextureScale.
        /// The copy is persisted into NDMF's container and applied with the
        /// texture's own import category settings.
        /// 按 WholeTextureScale 创建（或复用）整图缩放副本。副本持久化进
        /// NDMF 容器并应用贴图自身类别的导入参数。
        /// </summary>
        public static Texture2D GetOrCreateWholeTextureCopy(ATOBuildState state, Texture2D tex)
        {
            if (state.WholeTextureCopies.TryGetValue(tex, out var cached)) return cached;
            if (!state.WholeTextureScale.TryGetValue(tex, out var target))
                return tex;

            int w = Mathf.Max(1, target.x);
            int h = Mathf.Max(1, target.y);
            bool linear = !TextureCollector.IsSRGBTexture(tex);

            // Bilinear downsample via RenderTexture (works for any source
            // layout, including non-readable textures). / 通过 RenderTexture
            // 双线性下采样（适用于任意源布局，包括不可读贴图）。
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            var prev = RenderTexture.active;
            var src = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, src);
            var dst = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, dst);
            RenderTexture.active = dst;
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(src);
            RenderTexture.ReleaseTemporary(dst);

            copy.name = tex.name + " (ATO scaled)";
            copy.wrapMode = tex.wrapMode;
            copy.filterMode = tex.filterMode;

            var category = ClassifyCategory(state, tex);
            import.TextureImportConfig.ApplyGeneratedSettings(state, copy, category, HasAlpha(tex), state.Component.Atlas.EnableNPOT);

            state.WholeTextureCopies[tex] = copy;
            state.NewTextures.Add(copy);
            PersistWholeTextureCopy(state, copy);
            ATOLog.Info($"[ATO] Whole-texture copy {tex.name}: {tex.width}x{tex.height} -> {w}x{h}. / 整图副本 {tex.name}：{tex.width}x{tex.height} -> {w}x{h}。");
            return copy;
        }

        private static bool HasAlpha(Texture2D tex)
        {
            var fmt = tex.format;
            switch (fmt)
            {
                case TextureFormat.RGBA32: case TextureFormat.RGBA4444: case TextureFormat.ARGB32:
                case TextureFormat.BGRA32: case TextureFormat.DXT5: case TextureFormat.BC7:
                case TextureFormat.ASTC_4x4: case TextureFormat.ASTC_6x6: case TextureFormat.ASTC_8x8:
                case TextureFormat.ETC2_RGBA8: case TextureFormat.RGBAFloat: case TextureFormat.RGBAHalf:
                    return true;
                default: return false;
            }
        }

        private static ATOImportCategory ClassifyCategory(ATOBuildState state, Texture2D tex)
        {
            foreach (var u in state.AllUsages)
            {
                if (u.Texture == tex)
                {
                    switch (u.Type)
                    {
                        case TextureUsageType.NormalMap: return ATOImportCategory.NormalMap;
                        case TextureUsageType.Mask: return ATOImportCategory.Grayscale;
                    }
                }
            }
            return HasAlpha(tex) ? ATOImportCategory.Transparent : ATOImportCategory.Opaque;
        }

        private static void PersistWholeTextureCopy(ATOBuildState state, Texture2D copy)
        {
            // The Applier has no BuildContext; the finalizer persists all new
            // textures into the container instead. / Applier 没有 BuildContext；
            // 收尾阶段会将所有新贴图持久化进容器。
        }
    }
}

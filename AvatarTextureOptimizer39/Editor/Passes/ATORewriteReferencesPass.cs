// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.Linq;
using AvatarTextureOptimizer.Editor.Analysis;
using AvatarTextureOptimizer.Editor.Atlas;
using AvatarTextureOptimizer.Editor.Core;
using AvatarTextureOptimizer.Editor.Packing;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 9 — rewrite references: remap mesh UVs to atlas coordinates, assign atlas
    /// textures to materials, update animation references, deduplicate materials, merge
    /// material slots, integrate with AAO's UVUsageCompabilityAPI, and remove the ATO
    /// component from the final avatar.
    ///
    /// Pass 9 —— 重写引用：网格 UV 重映射、材质赋图集、动画引用更新、材质去重、
    /// 材质槽合并、兼容 AAO UVUsageCompabilityAPI、移除成品上的自身组件。
    /// </summary>
    public sealed class ATORewriteReferencesPass : Pass<ATORewriteReferencesPass>
    {
        public override string DisplayName => "ATO: Rewrite references / 重写引用";

        private ATOBuildState _state;
        private GameObject _root;

        protected override void Execute(BuildContext context)
        {
            _state = context.GetState<ATOBuildState>();
            if (_state.Component == null) return;
            _state.BeginStage("Rewrite references / 重写引用");

            _root = context.AvatarRootObject;

            using var _ = ATOLog.Time("Rewrite references");

            if (_state.Component.generateAtlas)
            {
                BuildTextureToAtlasMap();
                RemapMeshUVs(context);
                AssignAtlasToMaterials(context);
            }

            AssignReplacementTextures(context);
            UpdateAnimationReferences(context);
            DeduplicateMaterialsAndTextures(context);
            MergeMaterialSlots(context);
            AaoCompatibility(context);
            RemoveComponent(context);

            // Linear pixels no longer needed. 线性像素不再需要。
            ATOMemory.ReleaseLinearPixels(_state);
        }

        private void BuildTextureToAtlasMap()
        {
            _state.TextureToAtlas.Clear();
            int atlasIdx = 0;
            foreach (var group in _state.AtlasGroups)
            {
                foreach (var atlas in group.Atlases)
                {
                    if (atlasIdx >= _state.GeneratedAtlases.Count) break;
                    var atlasTex = _state.GeneratedAtlases[atlasIdx++];
                    foreach (var p in atlas.Placements)
                        foreach (var t in p.Entry.Textures)
                            if (t != null)
                                _state.TextureToAtlas[t.Texture] = atlasTex;
                }
            }
        }

        private void RemapMeshUVs(BuildContext context)
        {
            var meshClones = new Dictionary<Mesh, Mesh>();

            foreach (var group in _state.AtlasGroups)
            foreach (var atlas in group.Atlases)
            foreach (var p in atlas.Placements)
            {
                _state.ThrowIfCancelled();

                var entry = p.Entry;
                var renderer = entry.Renderer;
                var srcMesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (srcMesh == null) continue;

                if (!meshClones.TryGetValue(srcMesh, out var clone))
                {
                    clone = Object.Instantiate(srcMesh);
                    clone.name = srcMesh.name + "_ATO";
                    meshClones[srcMesh] = clone;

                    if (renderer is SkinnedMeshRenderer s)
                    {
                        s.sharedMesh = clone;
                        context.ObjectRegistry.RegisterReplacedObject(srcMesh, clone);
                    }
                    else if (renderer is MeshRenderer m)
                    {
                        m.GetComponent<MeshFilter>().sharedMesh = clone;
                        context.ObjectRegistry.RegisterReplacedObject(srcMesh, clone);
                    }
                }

                RemapIslandUVs(entry, clone, p, srcMesh);
            }
        }

        private static void RemapIslandUVs(ATOUVIslandEntry entry, Mesh clone, ATOPlacement p, Mesh srcMesh)
        {
            int channel = entry.UVChannel;
            var uvs = new List<Vector2>();
            if (channel == 0) uvs = new List<Vector2>(srcMesh.uv);
            else if (channel == 1) uvs = new List<Vector2>(srcMesh.uv2);
            else if (!srcMesh.GetUVs(channel, uvs)) return;

            var bounds = entry.NormalizedBounds;
            int pw = p.PixelW, ph = p.PixelH;

            var tris = srcMesh.GetTriangles(entry.SubMeshIndex);
            var verts = new HashSet<int>();
            foreach (var t in entry.Island.Triangles)
            {
                verts.Add(tris[t * 3]);
                verts.Add(tris[t * 3 + 1]);
                verts.Add(tris[t * 3 + 2]);
            }

            float invW = 1f / Mathf.Max(1e-6f, bounds.width);
            float invH = 1f / Mathf.Max(1e-6f, bounds.height);

            foreach (var vi in verts)
            {
                if (vi >= uvs.Count) continue;
                var uv = uvs[vi];

                float lx = (uv.x - bounds.xMin) * invW * pw;
                float ly = (uv.y - bounds.yMin) * invH * ph;

                float rx, ry;
                switch (p.Rotation)
                {
                    case 90: rx = ph - ly; ry = lx; break;
                    case 180: rx = pw - lx; ry = ph - ly; break;
                    case 270: rx = ly; ry = pw - lx; break;
                    default: rx = lx; ry = ly; break;
                }

                float atlasSize = p.AtlasSize;
                uvs[vi] = new Vector2((p.PixelX + rx) / atlasSize, (p.PixelY + ry) / atlasSize);
            }

            clone.SetUVs(channel, uvs);
        }

        private void AssignAtlasToMaterials(BuildContext context)
        {
            foreach (var matRec in _state.Materials.Values)
            {
                var mat = matRec.Material;
                foreach (var b in matRec.Bindings)
                {
                    if (b.Texture == null) continue;
                    if (_state.TextureToAtlas.TryGetValue(b.Texture, out var atlas))
                        mat.SetTexture(b.PropertyName, atlas);
                }
            }
            _ = context;
        }

        /// <summary>
        /// Assign whole-texture-scaled replacements (non-atlas mode / skip-atlas textures)
        /// to materials. 将整图缩放的替换贴图赋给材质。
        /// </summary>
        private void AssignReplacementTextures(BuildContext context)
        {
            if (_state.TextureRemap.Count == 0) return;

            foreach (var matRec in _state.Materials.Values)
            {
                var mat = matRec.Material;
                foreach (var b in matRec.Bindings)
                {
                    if (b.Texture == null) continue;
                    if (_state.TextureRemap.TryGetValue(b.Texture, out var replacement))
                        mat.SetTexture(b.PropertyName, replacement);
                }
            }
            _ = context;
        }

        private void UpdateAnimationReferences(BuildContext context)
        {
            if (_state.TextureToAtlas.Count == 0 && _state.TextureRemap.Count == 0) return;

            var animCtx = context.ActivateExtensionContext<AnimatorServicesContext>();
            animCtx.AnimationIndex.RewriteObjectCurves(o =>
            {
                if (o is Texture2D t)
                {
                    if (_state.TextureToAtlas.TryGetValue(t, out var atlas)) return atlas;
                    if (_state.TextureRemap.TryGetValue(t, out var remapped)) return remapped;
                }
                return o;
            });
        }

        private void DeduplicateMaterialsAndTextures(BuildContext context)
        {
            if (!_state.Component.deduplicateMaterials && !_state.Component.deduplicateTextures) return;

            var remap = new Dictionary<Material, Material>();
            var bySignature = new Dictionary<string, Material>();

            foreach (var matRec in _state.Materials.Values)
            {
                var sig = MaterialSignature(matRec.Material);
                if (bySignature.TryGetValue(sig, out var canonical))
                    remap[matRec.Material] = canonical;
                else
                    bySignature[sig] = matRec.Material;
            }

            if (remap.Count == 0) return;

            foreach (var renderer in _state.EligibleRenderers)
            {
                var mats = renderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && remap.TryGetValue(mats[i], out var c))
                        mats[i] = c;
                renderer.sharedMaterials = mats;
            }

            var animCtx = context.ActivateExtensionContext<AnimatorServicesContext>();
            animCtx.AnimationIndex.RewriteObjectCurves(o =>
                o is Material m && remap.TryGetValue(m, out var c) ? c : o);

            ATOLog.Info($"Deduplicated {remap.Count} material(s). / 去重了 {remap.Count} 个材质。");
        }

        private static string MaterialSignature(Material m)
        {
            var sb = new System.Text.StringBuilder(m.shader.name);
            sb.Append('#').Append(m.renderQueue);
            if (m.IsKeywordEnabled("_ALPHATEST_ON")) sb.Append("|cutout");
            if (m.IsKeywordEnabled("_ALPHABLEND_ON")) sb.Append("|blend");

            foreach (var name in m.GetTexturePropertyNames())
            {
                var t = m.GetTexture(name);
                sb.Append('|').Append(name).Append('=').Append(t != null ? t.GetInstanceID().ToString() : "null");
            }

            // Colors & floats via serialized props. 颜色与浮点。
            foreach (var name in m.GetPropertyNames(MaterialPropertyType.Color))
                sb.Append('|').Append(name).Append('=').Append(m.GetColor(name));
            foreach (var name in m.GetPropertyNames(MaterialPropertyType.Float))
                sb.Append('|').Append(name).Append('=').Append(m.GetFloat(name));

            return sb.ToString();
        }

        /// <summary>
        /// Merge duplicate opaque material slots and update animation slot indices.
        /// 合并重复的不透明材质槽并更新动画槽索引。
        /// </summary>
        private void MergeMaterialSlots(BuildContext context)
        {
            var animCtx = context.ActivateExtensionContext<AnimatorServicesContext>();
            var index = animCtx.AnimationIndex;

            foreach (var renderer in _state.EligibleRenderers)
            {
                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length <= 1) continue;

                string path = AnimationUtility.CalculateTransformPath(renderer.transform, _root.transform);
                var type = renderer.GetType();

                var slotAnimated = new bool[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var binding = new EditorCurveBinding
                    {
                        path = path,
                        type = type,
                        propertyName = $"m_Materials.Array.data[{i}]",
                    };
                    if (index.GetClipsForBinding(binding).Any()) slotAnimated[i] = true;
                }

                var oldMesh = GetMesh(renderer);
                var merged = ATOMaterialSlotMerger.Merge(renderer, slotAnimated);
                if (merged == null) continue;

                context.ObjectRegistry.RegisterReplacedObject(oldMesh, merged);
                ATOLog.Verbose($"Merged duplicate opaque material slots on {renderer.name}. / " +
                               $"合并了 {renderer.name} 上重复的不透明材质槽。");
            }
        }

        private static Mesh GetMesh(Renderer r)
        {
            return r is SkinnedMeshRenderer smr ? smr.sharedMesh
                : r is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh : null;
        }

        private void AaoCompatibility(BuildContext context)
        {
            var aao = AaoApi.TryLoad();
            if (aao == null) return;

            foreach (var renderer in _state.EligibleRenderers)
            {
                if (!(renderer is SkinnedMeshRenderer smr)) continue;

                for (int ch = 0; ch < 8; ch++)
                {
                    if (!aao.IsTexCoordUsed(smr, ch)) continue;
                    for (int dest = 0; dest < 8; dest++)
                    {
                        if (dest == ch || aao.IsTexCoordUsed(smr, dest)) continue;
                        aao.RegisterTexCoordEvacuation(smr, ch, dest);
                        break;
                    }
                }
            }

            _ = context;
        }

        private void RemoveComponent(BuildContext context)
        {
            if (_state.Component != null)
                Object.DestroyImmediate(_state.Component);
            ATOLog.Info("Removed ATO component from the avatar. / 已从成品移除 ATO 组件。");
            _ = context;
        }
    }
}

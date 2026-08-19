// ReferenceRewriter.cs
// Rewrites meshes (UV remap), materials (texture refs) and animation object curves to
// point at generated atlases/scaled textures. Only texture references change — no other
// shader parameters are touched. / 重写网格UV、材质贴图引用与动画对象曲线,指向生成的
// 图集/缩放贴图。只改贴图引用,不改任何其他着色器参数。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace net.fosa.ato
{
    internal sealed partial class ATOProcessor
    {
        /// <summary>island → normalized placement rect. / 岛→归一化放置矩形。</summary>
        private Dictionary<long, PlacedIsland> _placementByIsland;

        private void RewriteReferences()
        {
            BuildPlacementIndex();
            RewriteMeshes();
            RewriteMaterials();
            RewriteAnimations();
        }

        private void BuildPlacementIndex()
        {
            _placementByIsland = new Dictionary<long, PlacedIsland>();
            foreach (var plan in _d.AtlasPlans)
                foreach (var pi in plan.Placed)
                {
                    long key = ATOBuildData.Key(pi.SetId, pi.IslandId);
                    if (!_placementByIsland.ContainsKey(key)) _placementByIsland[key] = pi;
                }
        }

        // ------------------------------------------------------------------ //
        // Meshes / 网格
        // ------------------------------------------------------------------ //
        private void RewriteMeshes()
        {
            int rewritten = 0;
            // mesh → channels/islands to remap / 网格→需重映射的通道与岛
            var meshEdits = new Dictionary<Mesh, List<IslandSetData>>();
            foreach (var set in _d.IslandSets)
            {
                bool any = false;
                foreach (var isl in set.Islands)
                    if (_placementByIsland.ContainsKey(ATOBuildData.Key(_d.IslandSets.IndexOf(set), isl.Id)))
                    { any = true; break; }
                if (!any) continue;
                List<IslandSetData> list;
                if (!meshEdits.TryGetValue(set.Mesh, out list)) meshEdits[set.Mesh] = list = new List<IslandSetData>();
                list.Add(set);
            }

            foreach (var kv in meshEdits)
            {
                var srcMesh = kv.Key;
                Mesh clone;
                if (!_d.MeshClones.TryGetValue(srcMesh, out clone))
                {
                    clone = UnityEngine.Object.Instantiate(srcMesh);
                    clone.name = srcMesh.name + "(ATO)";
                    _d.Ctx.AssetSaver.SaveAsset(clone);
                    _d.MeshClones[srcMesh] = clone;
                }

                foreach (var set in kv.Value)
                {
                    int setId = _d.IslandSets.IndexOf(set);
                    var uvList = new List<Vector2>();
                    clone.GetUVs(set.Channel, uvList);
                    if (uvList.Count == 0) continue;

                    foreach (var isl in set.Islands)
                    {
                        PlacedIsland pi;
                        if (!_placementByIsland.TryGetValue(ATOBuildData.Key(setId, isl.Id), out pi)) continue;
                        foreach (var v in isl.Vertices)
                        {
                            var uv = set.NormalizedUvs[v];
                            float nx = (uv.x - pi.SourceUvBounds.xMin) / Mathf.Max(1e-9f, pi.SourceUvBounds.width);
                            float ny = (uv.y - pi.SourceUvBounds.yMin) / Mathf.Max(1e-9f, pi.SourceUvBounds.height);
                            float au, av;
                            if (pi.Rotated)
                            {
                                au = pi.RectN.x + ny * pi.RectN.width;
                                av = pi.RectN.y + nx * pi.RectN.height;
                            }
                            else
                            {
                                au = pi.RectN.x + nx * pi.RectN.width;
                                av = pi.RectN.y + ny * pi.RectN.height;
                            }
                            uvList[v] = new Vector2(au, av);
                        }
                    }
                    clone.SetUVs(set.Channel, uvList);
                }

                // assign to renderers / 赋回渲染器
                foreach (var rec in _d.Renderers)
                {
                    if (rec.Mesh != srcMesh) continue;
                    if (rec.Renderer is SkinnedMeshRenderer smr) smr.sharedMesh = clone;
                    else
                    {
                        var mf = rec.Renderer.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = clone;
                    }
                    rewritten++;
                }
            }
            ATOLog.V($"mesh rewrite: {_d.MeshClones.Count} meshes cloned, {rewritten} renderers updated");
        }

        // ------------------------------------------------------------------ //
        // Materials / 材质
        // ------------------------------------------------------------------ //
        private void RewriteMaterials()
        {
            // gather all materials in play (slots + animation swaps) / 收集全部相关材质
            var allMaterials = new HashSet<Material>();
            foreach (var rec in _d.Renderers)
                foreach (var kv in rec.SlotMaterials)
                    foreach (var m in kv.Value)
                        if (m != null) allMaterials.Add(m);

            int cloned = 0;
            foreach (var mat in allMaterials)
            {
                var analysis = ShaderAnalyzer.Analyze(mat, _d.Animations, "", 0);
                bool changed = false;
                foreach (var u in analysis.Usages)
                    if (ResolveReplacement(u.Texture) != null) { changed = true; break; }
                if (!changed) continue;

                // clone (avoid touching shared assets) / 克隆(避免改动共享资产)
                Material target;
                if (_d.Ctx.IsTemporaryAsset(mat))
                {
                    target = mat; // temporary per-build instance, safe / 构建期临时实例,安全
                }
                else if (!_d.MaterialClones.TryGetValue(mat, out target))
                {
                    target = UnityEngine.Object.Instantiate(mat);
                    target.name = mat.name + "(ATO)";
                    _d.Ctx.AssetSaver.SaveAsset(target);
                    _d.MaterialClones[mat] = target;
                    cloned++;
                }

                foreach (var u in analysis.Usages)
                {
                    var rep = ResolveReplacement(u.Texture);
                    if (rep == null) continue;
                    if (target.HasProperty(u.PropertyName))
                        target.SetTexture(u.PropertyName, rep);
                }
            }

            // reassign renderer slots / 重赋材质槽
            foreach (var rec in _d.Renderers)
            {
                var mats = rec.Renderer.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material clone;
                    if (mats[i] != null && _d.MaterialClones.TryGetValue(mats[i], out clone))
                    {
                        mats[i] = clone;
                        dirty = true;
                    }
                }
                if (dirty) rec.Renderer.sharedMaterials = mats;
            }

            // also fix slot bookkeeping / 同步槽位记录
            foreach (var rec in _d.Renderers)
            {
                var keys = rec.SlotMaterials.Keys.ToList();
                foreach (var k in keys)
                {
                    var list = rec.SlotMaterials[k];
                    for (int i = 0; i < list.Count; i++)
                    {
                        Material clone;
                        if (list[i] != null && _d.MaterialClones.TryGetValue(list[i], out clone)) list[i] = clone;
                    }
                }
            }

            ATOLog.V($"material rewrite: {cloned} cloned, {_d.MaterialClones.Count} mapped");
        }

        /// <summary>Replacement texture for a source texture (atlas → scaled → dedup). / 源贴图的替换结果(图集→缩放→去重)。</summary>
        private Texture2D ResolveReplacement(Texture2D src)
        {
            if (src == null) return null;
            AtlasPlan plan;
            if (_d.AtlasByTexture.TryGetValue(src, out plan) && plan.Baked != null) return plan.Baked;
            Texture2D scaled;
            if (_d.StandaloneBaked.TryGetValue(src, out scaled)) return scaled;
            Texture2D dedup;
            if (_d.TextureDedupMap.TryGetValue(src, out dedup))
            {
                var recursive = ResolveReplacement(dedup);
                return recursive ?? dedup;
            }
            return null;
        }

        // ------------------------------------------------------------------ //
        // Animations / 动画
        // ------------------------------------------------------------------ //
        private void RewriteAnimations()
        {
            if (_d.MaterialClones.Count == 0 && _d.TextureReplacements.Count == 0 && _d.AtlasByTexture.Count == 0)
                return;

            var asc = _d.Ctx.Extension<AnimatorServicesContext>();
            asc.AnimationIndex.RewriteObjectCurves(obj => MapObjectReference(obj));
            ATOLog.V("animation object curves rewritten");
        }

        private UnityEngine.Object MapObjectReference(UnityEngine.Object obj)
        {
            var mat = obj as Material;
            if (mat != null)
            {
                Material clone;
                return _d.MaterialClones.TryGetValue(mat, out clone) ? clone : mat;
            }
            var tex = obj as Texture2D;
            if (tex != null)
            {
                var rep = ResolveReplacement(tex);
                return rep != null ? (UnityEngine.Object)rep : obj;
            }
            return obj;
        }
    }
}

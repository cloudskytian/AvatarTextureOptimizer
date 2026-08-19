using System;
using System.Collections.Generic;
using System.Linq;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>
    /// 材质槽合并：同一网格上内容与参数完全相同的"不透明"材质（且动画没有单独切换其中任一个槽）
    /// 合并材质槽——合并子网格三角形、更新 sharedMaterials、注册动画槽索引重映射。
    /// </summary>
    public sealed class SlotMerger
    {
        private readonly ATOLogger _logger;
        private readonly AnimationAnalysis _animation;
        private readonly AnimationPatcher _patcher;

        public SlotMerger(ATOLogger logger, AnimationAnalysis animation, AnimationPatcher patcher)
        {
            _logger = logger;
            _animation = animation;
            _patcher = patcher;
        }

        public int MergeAll(IEnumerable<Renderer> renderers)
        {
            int merged = 0;
            foreach (var r in renderers)
            {
                merged += MergeRenderer(r);
            }
            if (merged > 0) _logger.Info($"Slot merger: merged {merged} material slot(s).");
            return merged;
        }

        private int MergeRenderer(Renderer r)
        {
            var mesh = GetMesh(r);
            if (mesh == null) return 0;
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length < 2) return 0;

            // 分组：相同材质实例
            var groups = new Dictionary<Material, List<int>>();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (groups.TryGetValue(m, out var list)) list.Add(i);
                else groups[m] = new List<int> { i };
            }

            int mergedCount = 0;
            var newMats = new List<Material>();
            var remap = new Dictionary<int, int>();

            for (int slot = 0; slot < mats.Length; slot++)
            {
                var m = mats[slot];
                if (m == null)
                {
                    // 空材质槽：保留原索引，不合并
                    remap[slot] = newMats.Count;
                    newMats.Add(null);
                    continue;
                }

                var group = groups[m];
                bool opaque = !IsTransparentMaterial(m);
                bool canMerge = opaque && group.Count > 1;

                if (canMerge)
                {
                    // 动画单独切换组内任一槽 → 不合并
                    foreach (var s in group)
                    {
                        if (_animation.SlotMaterialSwaps.ContainsKey((r, s)))
                        {
                            canMerge = false;
                            break;
                        }
                    }
                }

                if (!canMerge)
                {
                    // 该材质作为单槽处理（组内后续槽会走这里并各自保留）
                    if (!remap.ContainsKey(slot))
                    {
                        remap[slot] = newMats.Count;
                        newMats.Add(m);
                    }
                    continue;
                }

                // 合并组：第一个出现时建新槽，其余合并
                if (!remap.ContainsKey(slot))
                {
                    int target = newMats.Count;
                    remap[slot] = target;
                    newMats.Add(m);
                }
            }

            // 若没有真正合并则返回
            var oldCount = mats.Length;
            if (newMats.Count >= oldCount) return 0;
            mergedCount = oldCount - newMats.Count;

            // 合并子网格三角形
            int subMeshCount = mesh.subMeshCount;
            var finalTris = new int[newMats.Count][];
            for (int old = 0; old < subMeshCount; old++)
            {
                int ns;
                if (!remap.TryGetValue(old, out ns) || ns < 0 || ns >= finalTris.Length) continue;
                var tri = mesh.GetTriangles(old);
                if (finalTris[ns] == null) finalTris[ns] = tri;
                else
                {
                    var mergedArr = new int[finalTris[ns].Length + tri.Length];
                    Array.Copy(finalTris[ns], mergedArr, finalTris[ns].Length);
                    Array.Copy(tri, 0, mergedArr, finalTris[ns].Length, tri.Length);
                    finalTris[ns] = mergedArr;
                }
            }

            // 回填：把 null 槽补空（正常情况下不会有）
            for (int i = 0; i < finalTris.Length; i++) if (finalTris[i] == null) finalTris[i] = Array.Empty<int>();

            mesh.subMeshCount = finalTris.Length;
            for (int i = 0; i < finalTris.Length; i++)
            {
                mesh.SetTriangles(finalTris[i], i);
            }

            r.sharedMaterials = newMats.ToArray();

            // 动画槽索引重映射
            foreach (var kv in remap)
            {
                if (kv.Key != kv.Value) _patcher.AddSlotRemap(r, kv.Key, kv.Value);
            }

            _logger.VerboseLog($"Merged {mergedCount} slot(s) on '{r.name}' ({oldCount} -> {newMats.Count}).");
            return mergedCount;
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer mr) return mr.GetComponent<MeshFilter>()?.sharedMesh;
            return null;
        }

        private static bool IsTransparentMaterial(Material m)
        {
            if (m == null) return true;
            var mode = Analysis.RenderModeResolver.Resolve(m).mode;
            return mode == RenderMode.Blend || mode == RenderMode.Cutout;
        }
    }
}

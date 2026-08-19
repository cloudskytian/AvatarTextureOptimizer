// AvatarTextureOptimizer
// File: Editor/Apply/Deduplicator.cs
//
// Final cleanup:
//   - deduplicate materials with identical content+parameters, updating all
//     renderer slots and animation references
//   - merge material slots (consecutive identical opaque materials, not
//     individually animated): combine submeshes, compact the material array,
//     and record the slot-index remap for animations
//   - deduplicate identical textures/atlases
//   - persist generated textures into NDMF's asset container
//   - remove the ATO component from the baked avatar
//   - print the build report to the NDMF console
//
// 收尾：
//   - 对内容和参数完全相同的材质去重，更新所有渲染器槽位与动画引用
//   - 合并材质槽（连续相同的不透明材质、未被单独动画的槽）：合并子网格、
//     压缩材质数组，并记录供动画使用的槽索引重映射
//   - 对相同贴图/图集去重
//   - 将生成的贴图持久化进 NDMF 资产容器
//   - 从烘焙成品上移除 ATO 组件
//   - 将烘焙报告输出到 NDMF 控制台

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.apply
{
    public static class Deduplicator
    {
        public static void Finalize(BuildContext context, ATOBuildState state)
        {
            var stopwatch = new ATOStopwatch("Deduplicator.Finalize");

            if (state.Component.OptimizeTextures)
            {
                stopwatch.Begin("dedup textures");
                DeduplicateTextures(state);
                stopwatch.End("dedup textures");
            }

            if (state.Component.OptimizeMaterials)
            {
                stopwatch.Begin("dedup materials");
                DeduplicateMaterials(state);
                stopwatch.End("dedup materials");

                stopwatch.Begin("merge material slots");
                MergeMaterialSlots(context, state);
                stopwatch.End("merge material slots");
            }

            stopwatch.Begin("persist + report");
            PersistGeneratedTextures(context, state);
            RemoveComponent(state);
            PrintReport(state);
            stopwatch.End("persist + report");
        }

        // ====================================================================
        // Texture dedup / 贴图去重
        // ====================================================================

        private static void DeduplicateTextures(ATOBuildState state)
        {
            // Dedup generated textures by content (readable at this stage).
            // 按内容对生成的贴图去重（此阶段仍可读）。
            var seen = new Dictionary<string, List<Texture2D>>();
            foreach (var tex in state.NewTextures.ToList())
            {
                if (tex == null) continue;
                if (!tex.isReadable) continue;
                var key = $"{tex.width}x{tex.height}:{tex.format}";
                if (!seen.TryGetValue(key, out var list))
                    seen[key] = list = new List<Texture2D>();
                list.Add(tex);
            }

                foreach (var list in seen.Values)
                {
                    if (list.Count < 2) continue;
                    var rep = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var other = list[i];
                    if (ContentEquals(rep, other))
                    {
                        // Re-point any usage that references `other`.
                        // 将引用 other 的引用重指向 rep。
                        foreach (var group in state.UVGroups)
                        {
                            if (group.Whitelisted) continue;
                            foreach (var usage in group.Textures)
                            {
                                if (usage.Texture == other)
                                {
                                    if (usage.Material != null && usage.Material.HasProperty(usage.PropertyName))
                                        usage.Material.SetTexture(usage.PropertyName, rep);
                                    usage.Texture = rep;
                                }
                            }
                        }
                        foreach (var atlas in state.Atlases)
                            if (atlas.Texture == other) atlas.Texture = rep;
                        UnityEngine.Object.DestroyImmediate(other);
                        state.NewTextures.Remove(other);
                        ATOLog.Info($"[ATO] Deduplicated generated texture {other.name} -> {rep.name}. / 生成的贴图去重：{other.name} -> {rep.name}。");
                    }
                }
            }

            // Read/Write OFF for generated textures (spec: atlases default to
            // no Read/Write). / 生成的贴图关闭 Read/Write（规格：图集默认关闭）。
            foreach (var tex in state.NewTextures)
            {
                if (tex == null) continue;
                if (tex.isReadable)
                {
                    try { tex.Apply(false, true); } catch { }
                }
            }
        }

        // ====================================================================
        // Material dedup / 材质去重
        // ====================================================================

        private static void DeduplicateMaterials(ATOBuildState state)
        {
            var allMaterials = new HashSet<Material>();
            foreach (var renderer in state.UVGroups.Select(g => g.Space.Renderer).Distinct())
            {
                if (renderer == null) continue;
                foreach (var m in renderer.sharedMaterials)
                    if (m != null) allMaterials.Add(m);
            }
            // Also materials referenced in animations via material slots.
            // 也包含动画材质槽引用的材质。
            foreach (var usage in state.AllUsages)
                if (usage.Material != null) allMaterials.Add(usage.Material);

            var groups = allMaterials.GroupBy(MaterialKey).Where(g => g.Count() > 1).ToList();
            foreach (var group in groups)
            {
                var list = group.ToList();
                var rep = list[0];
                foreach (var other in list.Skip(1))
                {
                    if (!MaterialsEqual(rep, other)) continue;
                    state.MaterialRemap[other] = rep;
                    ATOLog.Trace($"material dedup: {other.name} -> {rep.name}");
                }
            }

            // Apply to renderer slots. / 应用到渲染器槽位。
            foreach (var renderer in state.UVGroups.Select(g => g.Space.Renderer).Distinct())
            {
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && state.MaterialRemap.TryGetValue(mats[i], out var rep))
                    {
                        mats[i] = rep;
                        changed = true;
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = mats;
                    EditorUtility.SetDirty(renderer);
                }
            }
        }

        private static string MaterialKey(Material m)
        {
            if (m == null) return "<null>";
            var sb = new System.Text.StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "<null>");
            var keywords = m.shaderKeywords ?? Array.Empty<string>();
            Array.Sort(keywords, StringComparer.Ordinal);
            foreach (var k in keywords) sb.Append('|').Append(k);
            return sb.ToString();
        }

        private static bool MaterialsEqual(Material a, Material b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.shader != b.shader) return false;
            if (!SameKeywords(a, b)) return false;

            var shader = a.shader;
            try
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    string name = shader.GetPropertyName(i);
                    if (!a.HasProperty(name) || !b.HasProperty(name)) continue;
                    var type = shader.GetPropertyType(i);
                    switch (type)
                    {
                        case ShaderPropertyType.Texture:
                            if (a.GetTexture(name) != b.GetTexture(name)) return false;
                            break;
                        case ShaderPropertyType.Color:
                            if (a.GetColor(name) != b.GetColor(name)) return false;
                            break;
                        case ShaderPropertyType.Vector:
                            if (a.GetVector(name) != b.GetVector(name)) return false;
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            if (!Mathf.Approximately(a.GetFloat(name), b.GetFloat(name))) return false;
                            break;
                        case ShaderPropertyType.Int:
                            if (a.GetInt(name) != b.GetInt(name)) return false;
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                ATOLog.Trace($"material compare failed: {e.Message}");
                return false;
            }
            return true;
        }

        private static bool SameKeywords(Material a, Material b)
        {
            var ka = a.shaderKeywords ?? Array.Empty<string>();
            var kb = b.shaderKeywords ?? Array.Empty<string>();
            if (ka.Length != kb.Length) return false;
            var sa = new HashSet<string>(ka);
            var sb = new HashSet<string>(kb);
            return sa.SetEquals(sb);
        }

        // ====================================================================
        // Material slot merge / 材质槽合并
        // ====================================================================

        private static void MergeMaterialSlots(BuildContext context, ATOBuildState state)
        {
            var animatedRenderers = state.AnimatedMaterialSlotRenderers;
            var root = state.Component != null ? state.Component.gameObject : null;
            if (root == null) return;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (animatedRenderers.Contains(renderer)) continue; // 动画单独切换槽位时不合并
                var mats = renderer.sharedMaterials;
                if (mats.Length < 2) continue;

                // Consecutive runs of identical materials. / 连续相同材质段。
                int runStart = 0;
                for (int i = 1; i <= mats.Length; i++)
                {
                    bool same = i < mats.Length && mats[i] != null && mats[i] == mats[runStart];
                    if (!same)
                    {
                        int runLen = i - runStart;
                        if (runLen >= 2)
                        {
                            TryMergeRun(renderer, mats, runStart, runLen, state);
                        }
                        runStart = i;
                    }
                }
            }
        }

        private static void TryMergeRun(Renderer renderer, Material[] mats, int start, int len, ATOBuildState state)
        {
            var mesh = GetMesh(renderer);
            if (mesh == null) return;

            // Only merge opaque materials (transparent sorting must stay).
            // 只合并不透明材质（透明排序必须保留）。
            var mat = mats[start];
            if (mat.HasProperty("_Mode"))
            {
                int mode = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (mode != 0 && mode != 1) return; // Fade/Transparent -> skip / 跳过
            }

            // Combine the submesh index buffers of the run into one submesh.
            // 将段的子网格索引缓冲合并为一个子网格。
            if (mesh.subMeshCount < start + len) return;
            var combined = new List<int>();
            for (int s = start; s < start + len; s++)
                combined.AddRange(mesh.GetIndices(s));

            // New mesh: same vertices, combined submesh. / 新网格：同顶点，合并子网格。
            var newMesh = UnityEngine.Object.Instantiate(mesh);
            newMesh.name = mesh.name + " (merged)";
            int newCount = mesh.subMeshCount - (len - 1);
            newMesh.subMeshCount = newCount;
            newMesh.SetIndices(combined.ToArray(), MeshTopology.Triangles, start);
            for (int s = start + len; s < mesh.subMeshCount; s++)
                newMesh.SetIndices(mesh.GetIndices(s), MeshTopology.Triangles, s - (len - 1));

            AssignMesh(renderer, newMesh);

            // Compact the material array. / 压缩材质数组。
            var newMats = new Material[newCount];
            for (int i = 0; i < mats.Length; i++)
            {
                if (i < start) newMats[i] = mats[i];
                else if (i == start) newMats[start] = mats[start];
                else if (i >= start + len) newMats[i - (len - 1)] = mats[i];
            }
            renderer.sharedMaterials = newMats;
            EditorUtility.SetDirty(renderer);

            // Record slot-index remap for animations. / 为动画记录槽索引重映射。
            for (int s = start + 1; s < start + len; s++)
                state.MaterialSlotMerge[(renderer, s)] = start;

            ATOLog.Info($"[ATO] Merged {len} material slots of {renderer.name} into slot {start}. / 将 {renderer.name} 的 {len} 个材质槽合并为槽 {start}。");
        }

        // ====================================================================
        // Persist / 持久化
        // ====================================================================

        private static void PersistGeneratedTextures(BuildContext context, ATOBuildState state)
        {
            var container = context.AssetContainer;
            if (container == null) return;
            foreach (var tex in state.NewTextures)
            {
                if (tex == null || EditorUtility.IsPersistent(tex)) continue;
                try
                {
                    AssetDatabase.AddObjectToAsset(tex, container);
                    tex.hideFlags = HideFlags.HideInHierarchy;
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"[ATO] Failed to persist {tex.name}: {e.Message}. / 无法持久化 {tex.name}。");
                }
            }
        }

        // ====================================================================
        // Cleanup + report / 清理 + 报告
        // ====================================================================

        private static void RemoveComponent(ATOBuildState state)
        {
            // ndmf 烘焙后应正确移除成品上的自身
            if (state.Component != null)
                UnityEngine.Object.DestroyImmediate(state.Component);
        }

        private static void PrintReport(ATOBuildState state)
        {
            // Memory accounting for textures that are NOT atlases (whole-texture
            // copies + untouched). / 非图集贴图（整图副本 + 未改动）的内存记账。
            long original = 0, result = 0;
            var unique = new HashSet<Texture2D>();
            foreach (var usage in state.AllUsages)
                if (usage.Texture != null) unique.Add(usage.Texture);

            foreach (var tex in unique)
            {
                long o = TextureImportEstimate(tex);
                original += o;
                long r = o;
                if (state.WholeTextureCopies.TryGetValue(tex, out var copy))
                    r = TextureImportEstimate(copy);
                else
                {
                    var group = state.UVGroups.FirstOrDefault(g => !g.Whitelisted && g.Textures.Any(u => u.Texture == tex));
                    if (group != null && group.AtlasIndex >= 0)
                        r = 0; // replaced by an atlas (already counted) / 已被图集替代（已计入）
                }
                result += r;
            }
            state.Report.AddBytes(original, result);
            state.Report.Print();
        }

        private static long TextureImportEstimate(Texture2D tex)
        {
            return net.fosa.avatar_texture_optimizer.editor.import.TextureImportConfig.EstimateBytes(tex);
        }

        private static bool ContentEquals(Texture2D a, Texture2D b)
        {
            if (a == b) return true;
            if (a.width != b.width || a.height != b.height) return false;
            if (!a.isReadable || !b.isReadable) return false;
            try
            {
                var pa = a.GetPixels32();
                var pb = b.GetPixels32();
                if (pa.Length != pb.Length) return false;
                for (int i = 0; i < pa.Length; i++)
                    if (!pa[i].Equals(pb[i])) return false;
                return true;
            }
            catch { return false; }
        }

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
                case SkinnedMeshRenderer smr: smr.sharedMesh = mesh; break;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = mesh;
                    break;
            }
            EditorUtility.SetDirty(renderer);
        }
    }
}

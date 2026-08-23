// Write-back: remap mesh UVs, replace materials, rewrite animations, deduplicate materials,
// remove the component, and register AAO UV evacuation.
// / 回写：重映射网格 UV、替换材质、重写动画、材质去重、移除组件、登记 AAO UV 疏散。

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.packing;
using net.fosa.avatar_texture_optimizer.editor.pipeline;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.writeback
{
    /// <summary>
    /// Writes the optimized data back onto the avatar. / 把优化结果写回 Avatar。
    /// </summary>
    public static class WriteBackProcessor
    {
        /// <summary>Process write-back. / 执行回写。</summary>
        public static void Process(BuildContext ctx, AnalysisResult analysis, PackingResult packing,
            AvatarTextureOptimizer component, ProgressScope progress, BuildReport report)
        {
            // 1) Material texture replacement / 材质贴图替换
            progress.Report("Write back / 回写", "Materials / 材质", 0.98f);
            ReplaceMaterialTextures(analysis, ctx);

            // 2) Mesh UV remapping (once per mesh) / 网格 UV 重映射（每网格一次）
            progress.Report("Write back / 回写", "Mesh UVs / 网格 UV", 0.99f);
            RemapMeshUvs(ctx, analysis, packing);

            // 3) Material deduplication / 材质去重
            Dictionary<Material, Material> materialMap = new Dictionary<Material, Material>();
            if (component.deduplicateMaterials)
            {
                materialMap = DeduplicateMaterials(analysis);
            }

            // 4) Animation rewrite / 动画重写
            progress.Report("Write back / 回写", "Animations / 动画", 0.995f);
            AnimationRewriter.Rewrite(ctx.AvatarRootObject.transform, analysis, materialMap, report.WarningMessages);

            // 5) Remove our component from the baked avatar / 从烘焙成品上移除自身组件
            UnityEngine.Object.DestroyImmediate(component);

            progress.Report("Write back / 回写", "Done / 完成", 1f);
        }

        private static void ReplaceMaterialTextures(AnalysisResult analysis, BuildContext ctx)
        {
            // Collect materials to modify / 收集需要修改的材质
            var matsToClone = new HashSet<Material>();
            foreach (var record in analysis.Textures)
            {
                if (record.Whitelisted || record.ResultTexture == null) continue;
                foreach (var b in record.Bindings)
                {
                    if (b.Material != null) matsToClone.Add(b.Material);
                }
            }

            var cloneMap = new Dictionary<Material, Material>();
            foreach (var m in matsToClone)
            {
                if (cloneMap.ContainsKey(m)) continue;
                var clone = new Material(m);
                clone.name = m.name + " (ATO)";
                cloneMap[m] = clone;
                ctx.ObjectRegistry.RegisterReplacedObject(m, clone);
            }

            // Assign clones to slots / 把克隆材质赋给材质槽
            foreach (var mesh in analysis.Meshes)
            {
                foreach (var slot in mesh.Slots)
                {
                    if (slot.Material != null && cloneMap.TryGetValue(slot.Material, out var clone))
                    {
                        slot.Material = clone;
                    }
                }
                // write materials back to the renderer / 写回渲染器
                var mats = new Material[mesh.Slots.Count];
                for (int i = 0; i < mesh.Slots.Count; i++) mats[i] = mesh.Slots[i].Material;
                ApplyMaterials(mesh, mats);
            }

            // Set new textures on clones / 在克隆材质上设置新贴图
            foreach (var record in analysis.Textures)
            {
                if (record.Whitelisted || record.ResultTexture == null) continue;
                foreach (var b in record.Bindings)
                {
                    if (b.Material == null) continue;
                    if (!cloneMap.TryGetValue(b.Material, out var clone)) continue;
                    if (clone.HasProperty(b.PropertyName))
                    {
                        clone.SetTexture(b.PropertyName, record.ResultTexture);
                    }
                }
            }
        }

        private static void ApplyMaterials(MeshUsage mesh, Material[] mats)
        {
            if (mesh.Renderer is SkinnedMeshRenderer smr) smr.sharedMaterials = mats;
            else if (mesh.Renderer is MeshRenderer mr) mr.sharedMaterials = mats;
        }

        private static void RemapMeshUvs(BuildContext ctx, AnalysisResult analysis, PackingResult packing)
        {
            // Group islands by mesh / 按网格归类岛
            var byMesh = new Dictionary<Mesh, List<UVGroup>>();
            foreach (var g in analysis.UvGroups)
            {
                if (g.Whitelisted) continue;
                if (!byMesh.TryGetValue(g.Mesh.Mesh, out var list))
                {
                    list = new List<UVGroup>();
                    byMesh[g.Mesh.Mesh] = list;
                }
                list.Add(g);
            }

            foreach (var kv in byMesh)
            {
                var mesh = kv.Key;
                var groups = kv.Value;
                var renderers = groups.Select(g => g.Mesh.Renderer).Distinct().ToList();

                // Clone the mesh once / 克隆网格一次
                var newMesh = UnityEngine.Object.Instantiate(mesh);
                newMesh.name = mesh.name + " (ATO)";

                // Merge per-channel islands with max-rect dedup (shared meshes) / 每通道合并岛并取最大矩形
                foreach (var channelGroup in groups.GroupBy(g => g.UvChannel))
                {
                    int ch = channelGroup.Key;
                    var byBBox = new Dictionary<long, Island>();
                    foreach (var g in channelGroup)
                    {
                        foreach (var iso in g.Islands)
                        {
                            if (iso.AtlasW <= 0) continue;
                            long key = BBoxKey(iso);
                            if (byBBox.TryGetValue(key, out var existing))
                            {
                                if ((long)iso.AtlasW * iso.AtlasH > (long)existing.AtlasW * existing.AtlasH)
                                {
                                    CopyPlacement(iso, existing);
                                }
                            }
                            else
                            {
                                byBBox[key] = iso;
                            }
                        }
                    }
                    if (byBBox.Count == 0) continue;

                    RemapChannel(mesh, newMesh, ch, byBBox.Values.ToList(), renderers);
                }

                // Register replacement & save / 登记替换并保存
                ctx.ObjectRegistry.RegisterReplacedObject(mesh, newMesh);
                ctx.AssetSaver.SaveAsset(newMesh);

                foreach (var r in renderers)
                {
                    if (r is SkinnedMeshRenderer smr) smr.sharedMesh = newMesh;
                    else if (r is MeshRenderer mr)
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = newMesh;
                    }
                }
            }
        }

        private static long BBoxKey(Island iso)
        {
            int x = Mathf.RoundToInt(iso.Min.x * 10000f);
            int y = Mathf.RoundToInt(iso.Min.y * 10000f);
            return ((long)x << 32) | (uint)y;
        }

        private static void CopyPlacement(Island from, Island to)
        {
            to.AtlasX = from.AtlasX; to.AtlasY = from.AtlasY;
            to.AtlasW = from.AtlasW; to.AtlasH = from.AtlasH;
            to.Rotated90 = from.Rotated90;
        }

        private static void RemapChannel(Mesh oldMesh, Mesh newMesh, int ch,
            List<Island> islands, List<Renderer> renderers)
        {
            var srcUv = GetUv(oldMesh, ch);
            if (srcUv == null || srcUv.Length == 0) return;

            var dstUv = new Vector2[srcUv.Length];

            // vertex -> island lookup via triangles / 通过三角形建立顶点到岛的映射
            var vertIsland = new int[srcUv.Length];
            for (int i = 0; i < srcUv.Length; i++) vertIsland[i] = -1;

            var tris = oldMesh.triangles;
            // Precompute island bounds lookup per island / 预计算岛的包围盒
            for (int t = 0; t < tris.Length / 3; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                int isoIdx = FindIslandForUv(islands, srcUv[i0]);
                if (isoIdx >= 0)
                {
                    if (vertIsland[i0] < 0) vertIsland[i0] = isoIdx;
                    if (vertIsland[i1] < 0) vertIsland[i1] = isoIdx;
                    if (vertIsland[i2] < 0) vertIsland[i2] = isoIdx;
                }
            }

            // layout size: unified canvas from packing layout / 布局尺寸：打包的统一画布
            int layoutSize = _layoutSize > 0 ? _layoutSize : 1;

            for (int v = 0; v < srcUv.Length; v++)
            {
                int isoIdx = vertIsland[v];
                if (isoIdx < 0) { dstUv[v] = srcUv[v]; continue; }
                var iso = islands[isoIdx];
                if (iso.AtlasW <= 0) { dstUv[v] = srcUv[v]; continue; }

                float bboxW = Mathf.Max(1e-6f, iso.Max.x - iso.Min.x);
                float bboxH = Mathf.Max(1e-6f, iso.Max.y - iso.Min.y);
                float lx = (srcUv[v].x - iso.Min.x) / bboxW;
                float ly = (srcUv[v].y - iso.Min.y) / bboxH;

                // rotation transform (matches drawing) / 旋转变换（与绘制一致）
                float tx = iso.Rotated90 ? ly : lx;
                float ty = iso.Rotated90 ? 1f - lx : ly;

                float u0 = iso.AtlasX / (float)layoutSize;
                float u1 = (iso.AtlasX + iso.AtlasW) / (float)layoutSize;
                float v0 = 1f - (iso.AtlasY + iso.AtlasH) / (float)layoutSize;
                float v1 = 1f - iso.AtlasY / (float)layoutSize;

                dstUv[v] = new Vector2(u0 + tx * (u1 - u0), v0 + ty * (v1 - v0));
            }

            SetUv(newMesh, ch, dstUv);

            // AAO compatibility: evacuate original UVs if AAO uses this channel / AAO 兼容：若 AAO 使用该通道则疏散原始 UV
            foreach (var r in renderers)
            {
                if (AaoCompat.IsTexCoordUsed(r, ch))
                {
                    int spare = ch == 7 ? 6 : 7;
                    if (GetUv(oldMesh, spare) == null || GetUv(oldMesh, spare).Length == 0)
                    {
                        SetUv(newMesh, spare, srcUv);
                        AaoCompat.RegisterTexCoordEvacuation(r, ch, spare);
                    }
                }
            }
        }

        private static int FindIslandForUv(List<Island> islands, Vector2 uv)
        {
            for (int i = 0; i < islands.Count; i++)
            {
                var iso = islands[i];
                if (uv.x >= iso.Min.x - 1e-5f && uv.x <= iso.Max.x + 1e-5f &&
                    uv.y >= iso.Min.y - 1e-5f && uv.y <= iso.Max.y + 1e-5f)
                {
                    return i;
                }
            }
            return -1;
        }

        private static Vector2[] GetUv(Mesh mesh, int ch)
        {
            switch (ch)
            {
                case 0: return mesh.uv;
                case 1: return mesh.uv2;
                case 2: return mesh.uv3;
                case 3: return mesh.uv4;
                case 4: return mesh.uv5;
                case 5: return mesh.uv6;
                case 6: return mesh.uv7;
                case 7: return mesh.uv8;
                default: return null;
            }
        }

        private static void SetUv(Mesh mesh, int ch, Vector2[] uv)
        {
            switch (ch)
            {
                case 0: mesh.uv = uv; break;
                case 1: mesh.uv2 = uv; break;
                case 2: mesh.uv3 = uv; break;
                case 3: mesh.uv4 = uv; break;
                case 4: mesh.uv5 = uv; break;
                case 5: mesh.uv6 = uv; break;
                case 6: mesh.uv7 = uv; break;
                case 7: mesh.uv8 = uv; break;
            }
        }

        private static int _layoutSize = 1;
        private static int _layoutSizeSet;

        /// <summary>Set the unified layout canvas size before remapping. / 重映射前设置统一布局画布尺寸。</summary>
        public static void SetLayoutSize(int size)
        {
            _layoutSize = size;
        }

        private static Dictionary<Material, Material> DeduplicateMaterials(AnalysisResult analysis)
        {
            var all = new List<Material>();
            foreach (var m in analysis.Meshes)
            {
                foreach (var slot in m.Slots)
                {
                    if (slot.Material != null) all.Add(slot.Material);
                }
            }

            // Do not deduplicate materials that are individually animated (safety) / 不合并被单独动画切换的材质
            var animated = new HashSet<Material>();
            if (analysis.Facts != null)
            {
                foreach (var s in analysis.Facts.AnimatedMaterialSlots)
                {
                    // resolve material at (path, slot) is complex here; conservatively skip dedup of ALL
                    // materials when any material-slot animation exists on the avatar / 简化：存在任何材质槽动画时保守跳过全部
                    animated.Add(null);
                }
            }
            if (animated.Count > 0)
            {
                AtoLog.VerboseLog("Material dedup skipped: material-slot animations detected. / 检测到材质槽动画，跳过材质去重。");
                return new Dictionary<Material, Material>();
            }

            var map = MaterialDeduper.Deduplicate(all);
            var changed = map.Where(kv => kv.Value != kv.Key).ToList();
            if (changed.Count > 0)
            {
                // Rewrite slot references / 重写材质槽引用
                foreach (var m in analysis.Meshes)
                {
                    bool dirty = false;
                    for (int i = 0; i < m.Slots.Count; i++)
                    {
                        var slot = m.Slots[i];
                        if (slot.Material != null && map.TryGetValue(slot.Material, out var rep) && rep != slot.Material)
                        {
                            slot.Material = rep;
                            dirty = true;
                        }
                    }
                    if (dirty)
                    {
                        var mats = new Material[m.Slots.Count];
                        for (int i = 0; i < m.Slots.Count; i++) mats[i] = m.Slots[i].Material;
                        ApplyMaterials(m, mats);
                    }
                }
                AtoLog.Info("Material dedup: " + changed.Count + " materials merged. / 材质去重：合并 " + changed.Count + " 个材质。");
            }
            return map;
        }
    }
}

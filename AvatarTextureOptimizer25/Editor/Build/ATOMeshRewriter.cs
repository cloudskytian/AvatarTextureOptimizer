// Avatar Texture Optimizer / 头像贴图优化器
// Mesh UV rewriting: clones meshes (deep copy incl. blendshapes/bindposes
// via Object.Instantiate), rewrites UV positions from atlas placements,
// splits vertices shared between conflicting submeshes, and coordinates AAO
// UV-channel evacuation when AvatarOptimizer occupies a channel.
// 网格 UV 重写：克隆网格（经 Object.Instantiate 深拷贝含形态键/绑定姿势）、
// 按图集摆放重写 UV、对跨子网格共享且冲突的顶点拆分，并在 AAO 占用通道时
// 协调 UV 通道转移登记。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Placement info needed by the UV rewrite. / UV 重写所需的摆放信息。</summary>
    public sealed class ATOAtlasPlacementLookup
    {
        public ATOAtlasPlan plan;
        public int atlasW, atlasH;
    }

    /// <summary>Rewrites mesh UVs to atlas placements. / 将网格 UV 重写到图集摆放。</summary>
    public sealed class ATOMeshRewriter
    {
        private readonly BuildContext _ctx;
        private readonly ATOBuildReport _report;
        private readonly Dictionary<Mesh, Mesh> _cloneCache = new Dictionary<Mesh, Mesh>();
        private readonly Dictionary<(Mesh, string), Mesh> _evacCloneCache = new Dictionary<(Mesh, string), Mesh>();

        public ATOMeshRewriter(BuildContext ctx, ATOBuildReport report)
        {
            _ctx = ctx;
            _report = report;
        }

        /// <summary>
        /// Compute the new UV for an island's baked vertex in atlas space.
        /// 计算岛某烘焙顶点在图集空间中的新 UV。
        /// </summary>
        public static Vector2 TransformUvToAtlas(Vector2 baked, ATOIsland isl, ATOPlacedIsland p, int atlasW, int atlasH)
        {
            float spanX = Mathf.Max(1e-8f, isl.uvMax.x - isl.uvMin.x);
            float spanY = Mathf.Max(1e-8f, isl.uvMax.y - isl.uvMin.y);
            float u01 = (baked.x - isl.uvMin.x) / spanX;
            float v01 = (baked.y - isl.uvMin.y) / spanY;
            float px, py;
            if (p.rotated90)
            {
                // Content rotated CW: (u,v) -> (1-v, u), rect axes swapped.
                // 内容顺时针旋转：(u,v) -> (1-v, u)，矩形轴互换。
                px = p.x + (1f - v01) * p.w;
                py = p.y + u01 * p.h;
            }
            else
            {
                px = p.x + u01 * p.w;
                py = p.y + v01 * p.h;
            }
            return new Vector2(px / atlasW, py / atlasH);
        }

        /// <summary>
        /// Rewrite a renderer's mesh UVs for all group placements that it uses.
        /// Vertices shared between submeshes are SPLIT first, so rewritten UVs never
        /// leak into other submeshes/usages. The returned mesh replaces the renderer's.
        /// 按该渲染器使用到的全部组摆放重写其网格 UV。跨子网格共享的顶点会先拆分，
        /// 重写绝不泄漏到其它子网格/用途。返回值替换渲染器的网格。
        /// </summary>
        public Mesh RewriteRendererMesh(
            Renderer renderer, Mesh originalMesh,
            List<(ATOUVGroup group, Dictionary<ATOIsland, ATOPlacedIsland> placements, ATOAtlasPlacementLookup lookup)> groupPlacements)
        {
            if (originalMesh == null || groupPlacements == null || groupPlacements.Count == 0) return originalMesh;

            var rewrittenSubmeshes = new HashSet<int>();
            foreach (var (group, _, _) in groupPlacements) rewrittenSubmeshes.Add(group.submesh);

            var clone = GetOrClone(originalMesh);
            EnsureSplit(clone, rewrittenSubmeshes);

            foreach (var (group, placements, lookup) in groupPlacements)
            {
                int ch = group.uvChannel;
                var uvs = GetUvList(clone, ch);
                if (uvs == null) continue;

                // Target by ORIGINAL-UV weld key, not by vertex id: weld mates
                // (identical UV, different vertex id — e.g. normal-split seams)
                // must ALL receive the rewrite, otherwise unwritten mates keep
                // sampling the old layout and crack the surface (QA-1 finding).
                // 以原始 UV 焊接键而非顶点 ID 寻址：焊接伙伴（UV 相同、顶点不同，
                // 例如法线分裂缝）必须全部被重写，否则未写伙伴仍采旧布局，
                // 表面开缝（QA-1 发现）。
                var targetByKey = new Dictionary<(int, int), Vector2>();
                foreach (var kv in placements)
                {
                    var isl = kv.Key;
                    var p = kv.Value;
                    for (int i = 0; i < isl.bakedUVs.Length; i++)
                    {
                        int vertexId = isl.origVertexIds[i];
                        if (vertexId < 0 || vertexId >= uvs.Count) continue;
                        var key = (QuantUv(uvs[vertexId].x), QuantUv(uvs[vertexId].y));
                        // Same original UV maps to the same target; first writer wins.
                        // 相同原始 UV 映射到同一目标；首写生效。
                        if (!targetByKey.ContainsKey(key))
                            targetByKey[key] = TransformUvToAtlas(isl.bakedUVs[i], isl, p, lookup.atlasW, lookup.atlasH);
                    }
                }

                // Rewrite every vertex feeding this group's submesh triangles.
                // 重写喂给该组子网格三角形的每一个顶点。
                var tris = clone.GetTriangles(group.submesh);
                var seen = new HashSet<int>();
                foreach (var v in tris)
                {
                    if (!seen.Add(v)) continue;
                    if (v < 0 || v >= uvs.Count) continue;
                    var key = (QuantUv(uvs[v].x), QuantUv(uvs[v].y));
                    if (targetByKey.TryGetValue(key, out var target))
                        uvs[v] = target;
                }
                SetUvList(clone, ch, uvs);
            }

            return clone;
        }

        private static int QuantUv(float f) => Mathf.RoundToInt(f * 1e6f);

        private Mesh GetOrClone(Mesh original)
        {
            if (_cloneCache.TryGetValue(original, out var clone) && clone != null) return clone;
            clone = Object.Instantiate(original);
            clone.name = original.name + "_ATO";
            _ctx.ObjectRegistry.RegisterReplacedObject(original, clone);
            _cloneCache[original] = clone;
            return clone;
        }

        private readonly HashSet<Mesh> _splitDone = new HashSet<Mesh>();

        /// <summary>
        /// Ensure every vertex that is shared across submesh boundaries has a
        /// submesh-local duplicate for each REWRITTEN submesh using it, remapping
        /// that submesh's triangles to the duplicates.
        /// 确保跨子网格共享的顶点，对每个使用它的"被重写子网格"都有独立副本，
        /// 并把该子网格的三角形重映射到副本上。
        /// </summary>
        private void EnsureSplit(Mesh mesh, HashSet<int> rewrittenSubmeshes)
        {
            if (rewrittenSubmeshes == null || rewrittenSubmeshes.Count == 0) return;
            if (_splitDone.Contains(mesh)) return;

            // 1) vertex ownership map / 顶点归属表
            int vCount = mesh.vertexCount;
            var owners = new List<int>[vCount];
            int smCount = mesh.subMeshCount;
            var trisBySm = new int[smCount][];
            for (int sm = 0; sm < smCount; sm++)
            {
                var tris = mesh.GetTriangles(sm);
                trisBySm[sm] = tris;
                foreach (var v in tris)
                {
                    (owners[v] ?? (owners[v] = new List<int>())).Add(sm);
                }
            }

            // 2) which duplicates are needed / 需要哪些副本
            var dupNeeded = new Dictionary<int, List<int>>(); // vertex -> list of submeshes needing a dup / 顶点 -> 需副本的子网格
            foreach (var sm in rewrittenSubmeshes)
            {
                if (sm < 0 || sm >= smCount) continue;
                foreach (var v in trisBySm[sm])
                {
                    var o = owners[v];
                    if (o == null || o.Count <= 1) continue; // unshared / 未共享
                    if (!dupNeeded.TryGetValue(v, out var list))
                    {
                        list = new List<int>();
                        dupNeeded[v] = list;
                    }
                    if (!list.Contains(sm)) list.Add(sm);
                }
            }
            if (dupNeeded.Count == 0)
            {
                _splitDone.Add(mesh);
                return;
            }

            // 3) rebuild mesh with appended duplicates / 重建网格并追加副本
            var splitter = new ATOMeshSplitter();
            splitter.LoadFrom(mesh);
            var dupMapOut = new Dictionary<(int, int), int>();
            foreach (var kv in dupNeeded)
            {
                int v = kv.Key;
                foreach (var sm in kv.Value)
                {
                    int dup = splitter.DuplicateVertex(v);
                    dupMapOut[(sm, v)] = dup;
                }
            }
            // Remap triangle indices of rewritten submeshes to their local dups. / 被重写子网格的三角形索引重映射到其副本。
            for (int sm = 0; sm < smCount; sm++)
            {
                var tris = mesh.GetTriangles(sm);
                if (rewrittenSubmeshes.Contains(sm))
                {
                    for (int i = 0; i < tris.Length; i++)
                    {
                        if (dupMapOut.TryGetValue((sm, tris[i]), out int dup)) tris[i] = dup;
                    }
                }
                splitter.SetSubmeshTriangles(sm, tris);
            }
            splitter.WriteBack(mesh);
            _splitDone.Add(mesh);
        }

        private static List<Vector2> GetUvList(Mesh mesh, int channel)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            return uvs.Count > 0 ? uvs : null;
        }

        private static void SetUvList(Mesh mesh, int channel, List<Vector2> uvs)
        {
            mesh.SetUVs(channel, uvs);
        }

        // ------------------------------------------------------------------
        // AAO evacuation handling / AAO 通道转移处理
        // ------------------------------------------------------------------

        /// <summary>
        /// If AAO (when installed) uses the given UV channel on this SMR, copy the
        /// ORIGINAL UV data of that channel into a free channel of the (cloned or
        /// original) mesh and register the evacuation. Returns the mesh to use
        /// (possibly a different evac-variant clone), or null when impossible.
        /// Pass a pre-picked <paramref name="evacChannel"/> (e.g. reserved during the
        /// planning stage); -1 picks one automatically.
        /// 若 AAO（已安装时）占用了该 SMR 的指定 UV 通道，则将原通道数据复制到空闲
        /// 通道并登记转移。返回应使用的网格（可能是另一转移变体克隆），失败返回 null。
        /// 可传入规划阶段预选的 <paramref name="evacChannel"/>；-1 时自动挑选。
        /// </summary>
        public Mesh EnsureAaoEvacuation(
            SkinnedMeshRenderer smr, Mesh mesh, int originalChannel,
            Func<int, bool> channelUsedByModel, int evacChannel = -1)
        {
            if (!ATOAAOCompat.IsInstalled) return mesh;
            if (!ATOAAOCompat.IsTexCoordUsed(smr, originalChannel)) return mesh;

            if (evacChannel < 0 || evacChannel == originalChannel)
            {
                if (!ATOAAOCompat.TryPickEvacuationChannel(smr, originalChannel, channelUsedByModel, out evacChannel))
                {
                    _report.warnings.Add(ATOLoc.T("ato:aao.nochannel", smr.name, originalChannel));
                    return null;
                }
            }
            else if (ATOAAOCompat.IsTexCoordUsed(smr, evacChannel) ||
                     (channelUsedByModel != null && channelUsedByModel(evacChannel)))
            {
                // Defensive: a pre-picked channel that became invalid is a hard failure.
                // 防御：预选通道已失效属于硬失败。
                _report.warnings.Add(ATOLoc.T("ato:aao.nochannel", smr.name, originalChannel));
                return null;
            }

            string sig = $"{originalChannel}->{evacChannel}";
            if (!_evacCloneCache.TryGetValue((mesh, sig), out var evacClone) || evacClone == null)
            {
                evacClone = Object.Instantiate(mesh);
                evacClone.name = mesh.name + $"_ATO_evac{sig}";
                // Copy ORIGINAL channel uv into evac channel BEFORE atlas rewrite.
                // 在图集重写前把原通道 UV 拷贝进转移通道。
                var origUvs = new List<Vector2>();
                mesh.GetUVs(originalChannel, origUvs);
                if (origUvs.Count == mesh.vertexCount)
                {
                    evacClone.SetUVs(evacChannel, origUvs);
                }
                else
                {
                    ATOLog.Warn($"AAO evacuation: mesh {mesh.name} has incomplete UV{originalChannel}");
                }
                _ctx.ObjectRegistry.RegisterReplacedObject(mesh, evacClone);
                _evacCloneCache[(mesh, sig)] = evacClone;
            }

            if (!ATOAAOCompat.RegisterTexCoordEvacuation(smr, originalChannel, evacChannel, out var error))
            {
                _report.warnings.Add(ATOLoc.T("ato:aao.evac_failed", smr.name, error));
                return null;
            }
            ATOLog.Verbose($"AAO evacuation: {smr.name} uv{originalChannel}->uv{evacChannel}");
            return evacClone;
        }
    }
}

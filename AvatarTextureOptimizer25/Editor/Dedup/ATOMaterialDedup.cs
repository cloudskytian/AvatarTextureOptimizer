// Avatar Texture Optimizer / 头像贴图优化器
// Post-processing material dedup: identical materials (content+params) merge;
// texture/atlas dedup by content; guarded opaque submesh/slot merges with
// animation-slot index remapping.
// 后处理去重：内容与参数完全一致的材质合并；图集/贴图按内容去重；带守卫的
// 不透明子网格/槽位合并（含动画槽位索引重映射）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Material + texture/atlas deduplication. / 材质与贴图/图集去重。</summary>
    public sealed class ATOMaterialDedup
    {
        private readonly BuildContext _ctx;
        private readonly ATOBuildReport _report;
        private readonly ATOAnimationData _anim;

        public ATOMaterialDedup(BuildContext ctx, ATOBuildReport report, ATOAnimationData anim)
        {
            _ctx = ctx;
            _report = report;
            _anim = anim;
        }

        /// <summary>Full fingerprint of a material's content & parameters. / 材质内容与参数的完整指纹。</summary>
        public static string Fingerprint(Material m)
        {
            var sb = new StringBuilder(512);
            sb.Append(m.shader != null ? m.shader.GetInstanceID() : 0).Append('|');
            sb.Append(m.renderQueue).Append('|');
            sb.Append((int)m.globalIlluminationFlags).Append('|');
            sb.Append(m.doubleSidedGI).Append('|');
            var kw = m.shaderKeywords;
            Array.Sort(kw, StringComparer.Ordinal);
            foreach (var k in kw) sb.Append(k).Append(',');

            var shader = m.shader;
            int n = shader != null ? shader.GetPropertyCount() : 0;
            for (int i = 0; i < n; i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                sb.Append(name).Append('=');
                switch (type)
                {
                    case ShaderPropertyType.Color:
                        var c = m.GetColor(name);
                        sb.Append('c').Append(c.r).Append(',').Append(c.g).Append(',').Append(c.b).Append(',').Append(c.a);
                        break;
                    case ShaderPropertyType.Vector:
                        var v = m.GetVector(name);
                        sb.Append('v').Append(v.x).Append(',').Append(v.y).Append(',').Append(v.z).Append(',').Append(v.w);
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        sb.Append('f').Append(m.GetFloat(name));
                        break;
                    case ShaderPropertyType.Int:
                        sb.Append('i').Append(m.GetInt(name));
                        break;
                    case ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        sb.Append('t').Append(t != null ? t.GetInstanceID() : 0);
                        // Texture import settings matter (sRGB etc.) / 贴图导入设置也算
                        if (t is Texture2D t2)
                        {
                            sb.Append(':').Append(t2.format).Append(':').Append(m.GetTextureScale(name))
                                .Append(':').Append(m.GetTextureOffset(name));
                        }
                        break;
                    default:
                        sb.Append('?');
                        break;
                }
                sb.Append(';');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Deduplicate cloned materials by fingerprint. Returns remap clone->representative.
        /// 按指纹对克隆材质去重，返回 克隆->代表 的映射。
        /// </summary>
        public Dictionary<Material, Material> DeduplicateMaterials(IEnumerable<Material> cloned)
        {
            using (new ATOLog.Step("material-dedup"))
            {
                var rep = new Dictionary<Material, Material>();
                var byPrint = new Dictionary<string, Material>();
                foreach (var m in cloned)
                {
                    if (m == null) continue;
                    var fp = Fingerprint(m);
                    if (byPrint.TryGetValue(fp, out var existing) && existing != null)
                    {
                        rep[m] = existing;
                        _report.materialsDeduplicatedInto++;
                    }
                    else
                    {
                        byPrint[fp] = m;
                    }
                }
                // Apply to renderers and animations / 应用到渲染器与动画
                if (rep.Count > 0)
                {
                    foreach (var r in _ctx.AvatarRootTransform.GetComponentsInChildren<Renderer>(true))
                    {
                        var mats = r.sharedMaterials;
                        bool dirty = false;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            if (mats[i] != null && rep.TryGetValue(mats[i], out var target))
                            {
                                mats[i] = target;
                                dirty = true;
                            }
                        }
                        if (dirty) r.sharedMaterials = mats;
                    }
                    var asStringWise = rep.ToDictionary(kv => kv.Key, kv => kv.Value);
                    ATOAnimationScanner.RemapMaterialReferences(_anim, asStringWise);
                    // destroy redundant clones / 销毁多余克隆
                    foreach (var kv in rep)
                    {
                        if (kv.Key != null) Object.DestroyImmediate(kv.Key);
                    }
                }
                return rep;
            }
        }

        // ------------------------------------------------------------------
        // Slot merging (guarded) / 槽位合并（带守卫）
        // ------------------------------------------------------------------

        /// <summary>
        /// Merge identical opaque material slots/submeshes of the same renderer
        /// when animation provably doesn't distinguish them.
        /// 当动画可证无区分时，合并同一渲染器上相同的不透明材质槽/子网格。
        /// </summary>
        public void MergeDuplicateOpaqueSlots(
            Dictionary<Material, ATORenderMode> finalModes)
        {
            using (new ATOLog.Step("slot-merge"))
            {
                foreach (var renderer in _ctx.AvatarRootTransform.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                    var path = PathOf(renderer.transform);
                    if (_anim.materialSwapsByPath.ContainsKey(path)) continue; // animated materials: skip / 有材质动画：跳过
                    if (_anim.animatedMatProps.ContainsKey(path)) continue;    // animated material props: skip
                    if (_anim.materialFloatsByPath.ContainsKey(path)) continue;

                    var mats = renderer.sharedMaterials;
                    Mesh mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                        : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null) continue;
                    int submeshCount = mesh.subMeshCount;
                    if (mats.Length != submeshCount) continue; // conservative / 保守跳过

                    // Find merge groups of identical opaque slots / 找出相同不透明槽位组
                    var groups = new Dictionary<int, List<int>>();
                    for (int i = 0; i < submeshCount; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        if (!finalModes.TryGetValue(m, out var mode) || mode != ATORenderMode.Opaque) continue;
                        int id = m.GetInstanceID();
                        if (!groups.TryGetValue(id, out var list))
                        {
                            list = new List<int>();
                            groups[id] = list;
                        }
                        list.Add(i);
                    }
                    var merges = groups.Where(kv => kv.Value.Count > 1).ToList();
                    if (merges.Count == 0) continue;

                    // Build new submesh triangle arrays / 构建新子网格三角形数组
                    try
                    {
                        ApplySlotMerge(renderer, mesh, mats, merges);
                    }
                    catch (Exception e)
                    {
                        _report.warnings.Add(ATOLoc.T("ato:slotmerge.failed", renderer.name, e.Message));
                    }
                }
            }
        }

        private void ApplySlotMerge(Renderer renderer, Mesh mesh, Material[] mats,
            List<KeyValuePair<int, List<int>>> merges)
        {
            int submeshCount = mesh.subMeshCount;
            var tris = new int[submeshCount][];
            for (int i = 0; i < submeshCount; i++) tris[i] = mesh.GetTriangles(i);

            // map old slot -> new slot / 旧槽位 -> 新槽位
            var slotMap = new int[submeshCount];
            var removed = new bool[submeshCount];
            var newTris = new List<int[]>();
            var newMats = new List<Material>();
            for (int i = 0; i < submeshCount; i++)
            {
                var merge = merges.FirstOrDefault(kv => kv.Value.Contains(i));
                if (merge.Value != null && merge.Value[0] != i)
                {
                    removed[i] = true;
                    continue; // destination slot appends later / 目标槽稍后追加
                }
                slotMap[i] = newMats.Count;
                newMats.Add(mats[i]);
                newTris.Add(tris[i]);
            }
            // Append removed triangles into their destination slots / 将被移除的三角形追加到目标槽
            for (int i = 0; i < submeshCount; i++)
            {
                if (!removed[i]) continue;
                var merge = merges.First(kv => kv.Value.Contains(i));
                int dstOld = merge.Value[0];
                int dstNew = slotMap[dstOld];
                var combined = newTris[dstNew].Concat(tris[i]).ToArray();
                newTris[dstNew] = combined;
            }

            // Commit: clone the mesh, shrink submesh count, overwrite triangle lists.
            // 提交：克隆网格，收缩子网格数，覆写三角形列表。
            var target = Object.Instantiate(mesh);
            target.name = mesh.name + "_ATO_merge";
            target.subMeshCount = newTris.Count;
            for (int i = 0; i < newTris.Count; i++)
                target.SetTriangles(newTris[i], i, false);
            target.RecalculateBounds();

            _ctx.ObjectRegistry.RegisterReplacedObject(mesh, target);
            if (renderer is SkinnedMeshRenderer smr) smr.sharedMesh = target;
            else
            {
                var mf = renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = target;
            }
            renderer.sharedMaterials = newMats.ToArray();
            _report.whitelistNotes.Add(ATOLoc.T("ato:slotmerge.done", renderer.name, merges.Count));
        }

        private string PathOf(Transform t)
        {
            var root = _ctx.AvatarRootTransform;
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}

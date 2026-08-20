// ============================================================================
// ATO - material dedup + opaque sub-mesh slot merge (PLAN only, stage 6)
// ATO - 材质去重 + 不透明子网格槽合并（仅计划，阶段 6）
//
// Rules 规则：
//  - two materials are identical when shader + render queue + all numeric
//    properties + final texture references + known-keyword set match;
//  - a material that is individually switched by animation (object
//    reference curves on m_Materials.Array.data[i]) is NEVER merged/deduped;
//  - sub-mesh slot merge happens only for OPAQUE (renderQueue < 3000)
//    materials, on a copied mesh, and records slot index remaps for the
//    animation rewriter.
//  两材质相同 = 着色器+渲染队列+全部数值属性+最终贴图引用+已知关键字集合一
//  致；被动画单独切换的材质绝不合并/去重；子网格槽合并仅针对不透明
//  （renderQueue<3000）材质，在拷贝网格上进行，并记录槽索引重映射供动画
//  改写使用。
// ============================================================================

#region

using System.Collections.Generic;
using System.Linq;
using System.Text;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Dedup
{
    public sealed class ATORendererPlan
    {
        public Renderer Renderer;
        public Mesh Mesh;          // possibly a copy  可能为拷贝
        public Material[] Materials;
        public Dictionary<int, int> SlotRemap = new();
        public bool Modified;
    }

    public static class MaterialDedup
    {
        // Known keywords probed for material comparison (standard + lilToon
        // family). 用于材质比较的已知关键字集合（标准 + lilToon 族）。
        private static readonly string[] ProbeKeywords =
        {
            "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON", "_NORMALMAP",
            "_EMISSION", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_GLOSSYREFLECTIONS_OFF",
            "_SPECULARHIGHLIGHTS_OFF", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A",
            "UNITY_UI_ALPHACLIP", "UNITY_UI_CLIP_RECT",
            "LIL_FEATURE_NORMAL_1ST", "LIL_FEATURE_EMISSION_1ST", "LIL_FEATURE_MAIN2ND",
        };

        public static void Plan(ATOContext ctx)
        {
            var an = ctx.Analysis;
            var log = ctx.Log;
            if (!ctx.Component.DedupMaterials) return;

            // 1. individually-switched materials  1. 被动画单独切换的材质
            var switched = CollectSwitchedMaterials(ctx);

            // 2. hash materials  2. 材质哈希
            var byHash = new Dictionary<string, List<Material>>();
            var hashes = new Dictionary<Material, string>();
            foreach (var (mat, info) in an.Materials)
            {
                var h = HashMaterial(an, mat);
                hashes[mat] = h;
                if (!byHash.TryGetValue(h, out var list))
                {
                    list = new List<Material>();
                    byHash[h] = list;
                }
                list.Add(mat);
            }

            // 3. dedup: keep first, map others  3. 去重：保留首个，映射其余
            foreach (var list in byHash.Values)
            {
                if (list.Count < 2) continue;
                var keep = list[0];
                if (switched.Contains(keep)) continue; // switched material kept as-is
                // 被切换的材质保持原样
                for (int i = 1; i < list.Count; i++)
                {
                    var other = list[i];
                    if (ReferenceEquals(other, keep)) continue;
                    if (switched.Contains(other))
                    {
                        log.V(ATOLogMask.Dedup,
                            $"material \"{other.name}\" skipped dedup (animation-switched). " +
                            "材质跳过（被动画切换）。");
                        continue;
                    }
                    if (!an.MaterialDedupMap.ContainsKey(other))
                    {
                        an.MaterialDedupMap[other] = keep;
                    }
                }
            }

            // 4. slot merge plans  4. 槽合并计划
            var renderers = new HashSet<Renderer>();
            foreach (var r in ctx.Renderers) renderers.Add(r);
            foreach (var sm in ctx.Anim.SwappedMaterials)
            {
                if (sm.Renderer != null) renderers.Add(sm.Renderer);
            }

            foreach (var r in renderers)
            {
                var smr = r as SkinnedMeshRenderer;
                var mr = r as MeshRenderer;
                Mesh mesh = smr != null ? smr.sharedMesh : mr != null ? mr.sharedMesh : null;
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                // final material per slot (after dedup)  每槽最终材质（去重后）
                var finalMats = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    finalMats[i] = mats[i] != null && an.MaterialDedupMap.TryGetValue(mats[i], out var rep) ? rep : mats[i];
                }

                // merge groups (opaque only, animation-safe)
                // 合并组（仅不透明、动画安全）
                var slotNew = new Dictionary<int, int>();
                var newMatList = new List<Material>();
                var mergedTriangles = new List<List<int>>();
                bool anyMerge = false;
                for (int i = 0; i < finalMats.Length; i++)
                {
                    var m = finalMats[i];
                    int target = -1;
                    if (m != null && m.renderQueue < 3000 && !switched.Contains(m))
                    {
                        for (int j = 0; j < newMatList.Count; j++)
                        {
                            if (ReferenceEquals(newMatList[j], m))
                            {
                                target = j;
                                break;
                            }
                        }
                    }
                    if (target >= 0)
                    {
                        anyMerge = true;
                        mergedTriangles[target].AddRange(mesh.GetTriangles(i));
                        slotNew[i] = target;
                    }
                    else
                    {
                        int idx = newMatList.Count;
                        newMatList.Add(m);
                        mergedTriangles.Add(new List<int>(mesh.GetTriangles(i)));
                        slotNew[i] = idx;
                    }
                }

                var plan = new ATORendererPlan { Renderer = r, Mesh = mesh, Materials = newMatList.ToArray() };
                foreach (var (old, neu) in slotNew) plan.SlotRemap[old] = neu;
                if (!anyMerge)
                {
                    // still apply dedup replacement  仍应用去重替换
                    plan.Materials = newMatList.ToArray();
                    foreach (var (old, neu) in slotNew)
                    {
                        if (old != neu) plan.SlotRemap[old] = neu;
                    }
                }
                else
                {
                    plan.Mesh = Object.Instantiate(mesh);
                    plan.Mesh.name = mesh.name + "_ATO";
                    for (int s = 0; s < mergedTriangles.Count; s++)
                    {
                        plan.Mesh.SetTriangles(mergedTriangles[s], s);
                    }
                    plan.Modified = true;
                    an.SlotRemap[r] = plan.SlotRemap;
                }
                an.RendererPlans[r] = plan;
            }

            int merged = 0;
            foreach (var p in an.RendererPlans.Values)
            {
                if (p.Modified) merged++;
            }
            log.Info(ATOLogMask.Dedup,
                $"dedup plan: {an.MaterialDedupMap.Count} material merges, {merged} meshes with merged slots. " +
                "去重计划完成。");

            // 5. post-optimization texture/atlas dedup (switch-gated)
            //    优化后贴图/图集去重（开关控制）
            if (ctx.Component.DedupTextures)
            {
                DedupGeneratedTextures(ctx, log);
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Dedups identical generated textures/atlas pages
        /// (sampled content + size + category). 去重内容+尺寸+类别完全相同的
        /// 生成贴图/图集页（采样哈希）。</summary>
        private static void DedupGeneratedTextures(ATOContext ctx, ATOLog log)
        {
            var an = ctx.Analysis;
            var candidates = new List<Texture2D>();
            if (an.PackedResult != null)
            {
                foreach (var page in an.PackedResult.Pages)
                {
                    if (page.Texture != null && !candidates.Contains(page.Texture))
                    {
                        candidates.Add(page.Texture);
                    }
                }
            }
            foreach (var scaled in an.ScaledTextures.Values)
            {
                if (scaled != null && !candidates.Contains(scaled))
                {
                    candidates.Add(scaled);
                }
            }
            if (candidates.Count < 2) return;

            var byKey = new Dictionary<string, Texture2D>();
            int deduped = 0;
            foreach (var tex in candidates)
            {
                var key = SampledKey(ctx, tex);
                if (byKey.TryGetValue(key, out var keep))
                {
                    if (keep == tex) continue;
                    ObjectRegistry.RegisterReplacedObject(tex, keep);
                    // point final textures to the keeper  最终贴图指向保留者
                    foreach (var k in an.FinalTextures.Keys.ToList())
                    {
                        if (an.FinalTextures[k] == tex) an.FinalTextures[k] = keep;
                    }
                    if (an.PackedResult != null)
                    {
                        foreach (var page in an.PackedResult.Pages)
                        {
                            if (page.Texture == tex) page.Texture = keep;
                        }
                    }
                    // drop the redundant import plan  丢弃冗余导入计划
                    if (an.ImportPlans.ContainsKey(tex)) an.ImportPlans.Remove(tex);
                    UnityEngine.Object.DestroyImmediate(tex);
                    deduped++;
                    log.V(ATOLogMask.Dedup, $"texture dedup: {tex.name} -> {keep.name}");
                }
                else
                {
                    byKey[key] = tex;
                }
            }
            if (deduped > 0)
            {
                log.Info(ATOLogMask.Dedup, $"texture dedup: {deduped} generated textures merged. 贴图去重。");
            }
        }

        private static string SampledKey(ATOContext ctx, Texture2D tex)
        {
            int cat = 0;
            if (ctx.Analysis.ImportPlans.TryGetValue(tex, out var plan))
            {
                cat = (int) plan.Category;
            }
            long hash = 0xcbf29ce484222325UL;
            try
            {
                // strip-based sampling (memory friendly)  按条带采样（省内存）
                int w = tex.width, h = tex.height;
                int stripH = Mathf.Max(1, h / 4);
                for (int y = 0; y < h; y += stripH)
                {
                    int ch = Mathf.Min(stripH, h - y);
                    var colors = tex.GetPixels(0, y, w, ch);
                    int step = Mathf.Max(1, colors.Length / 512);
                    for (int i = 0; i < colors.Length; i += step)
                    {
                        byte b = colors[i].r;
                        hash ^= b;
                        hash *= 1099511628211UL;
                        b = colors[i].g;
                        hash ^= b;
                        hash *= 1099511628211UL;
                        b = colors[i].b;
                        hash ^= b;
                        hash *= 1099511628211UL;
                        b = colors[i].a;
                        hash ^= b;
                        hash *= 1099511628211UL;
                    }
                }
            }
            catch (System.Exception)
            {
                return "err";
            }
            return $"{cat}|{tex.width}|{tex.height}|{hash:x16}";
        }

        private static HashSet<Material> CollectSwitchedMaterials(ATOContext ctx)
        {
            var set = new HashSet<Material>(new ObjectIdentityEqualityComparer());
            if (ctx.Anim == null) return set;
            foreach (var clip in ctx.Anim.Clips)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!(b.target is Renderer r)) continue;
                    if (!b.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                    var frames = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (frames == null) continue;
                    foreach (var f in frames)
                    {
                        if (f.value is Material m) set.Add(m);
                    }
                }
            }
            return set;
        }

        private static string HashMaterial(ATOAnalysis an, Material mat)
        {
            var sb = new StringBuilder();
            sb.Append(mat.shader.name).Append('|');
            sb.Append(mat.renderQueue).Append('|');
            sb.Append((int) mat.renderType).Append('|');
            int count = ShaderUtil.GetPropertyCount(mat.shader);
            for (int i = 0; i < count; i++)
            {
                var name = ShaderUtil.GetPropertyName(mat.shader, i);
                var type = ShaderUtil.GetPropertyType(mat.shader, i);
                switch (type)
                {
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        sb.Append(name).Append('=').Append(mat.GetFloat(name).ToString("R")).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.Color:
                        var c = mat.GetColor(name);
                        sb.Append(name).Append('=').Append(c.r).Append(',').Append(c.g)
                          .Append(',').Append(c.b).Append(',').Append(c.a).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        var v = mat.GetVector(name);
                        sb.Append(name).Append('=').Append(v.x).Append(',').Append(v.y)
                          .Append(',').Append(v.z).Append(',').Append(v.w).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.Texture:
                        var final = an.FinalTextures.TryGetValue((mat, name), out var ft) ? ft : null;
                        sb.Append(name).Append('=').Append(final != null ? final.GetInstanceID() : "null").Append(';');
                        break;
                }
            }
            foreach (var kw in ProbeKeywords)
            {
                try
                {
                    if (mat.IsKeywordEnabled(kw)) sb.Append(kw).Append(' ');
                }
                catch (System.Exception)
                {
                    // keyword not in shader - ignore  着色器无此关键字 - 忽略
                }
            }
            return sb.ToString();
        }
    }
}

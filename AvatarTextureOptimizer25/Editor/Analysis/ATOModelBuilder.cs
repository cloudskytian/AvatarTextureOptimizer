// Avatar Texture Optimizer / 头像贴图优化器
// Builds the usage model: renderers, materials, animations, whitelist, UV
// groups. This is the single most safety-critical stage: anything we cannot
// PROVE safe is excluded (acts as whitelist) here.
// 构建使用模型：渲染器、材质、动画、白名单、UV 组。这是安全级别最高的阶段：
// 任何无法证明安全的对象都在这里被排除（按白名单处理）。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Builds <see cref="ATOUsageModel"/> from the build context. / 由编译上下文构建 <see cref="ATOUsageModel"/>。</summary>
    public sealed class ATOModelBuilder
    {
        private readonly BuildContext _ctx;
        private readonly AvatarTextureOptimizer _settings;
        private readonly ATOAnimationData _anim;
        private readonly Dictionary<Material, ATOMaterialAnalysis> _matAnalysis = new Dictionary<Material, ATOMaterialAnalysis>();

        public ATOModelBuilder(BuildContext ctx, AvatarTextureOptimizer settings, ATOAnimationData anim)
        {
            _ctx = ctx;
            _settings = settings;
            _anim = anim;
        }

        /// <summary>Full build of the model. / 完整构建模型。</summary>
        public ATOUsageModel Build()
        {
            var model = new ATOUsageModel { animation = _anim };
            ExpandWhitelist(model);
            CollectRenderers(model);
            BuildUsages(model);
            BuildUVGroups(model);
            return model;
        }

        // ------------------------------------------------------------------
        // Whitelist / 白名单
        // ------------------------------------------------------------------

        private void ExpandWhitelist(ATOUsageModel model)
        {
            var wl = _settings.whitelist;
            if (wl == null) return;
            foreach (var obj in wl)
            {
                if (obj == null) continue;
                try
                {
                    ExpandWhitelistObject(obj, model);
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"whitelist expansion failed for {obj.name}: {e.Message}");
                }
            }
        }

        private void MarkTexture(Texture t, ATOUsageModel model)
        {
            if (t is Texture2D t2 && t2 != null)
            {
                model.whitelistedTextures.Add(t2);
                model.EntryFor(t2).exclusion |= ATOExcludeReason.UserWhitelist;
            }
        }

        private void MarkMaterialTextures(Material m, ATOUsageModel model)
        {
            if (m == null) return;
            var shader = m.shader;
            if (shader == null) return;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var tex = m.GetTexture(shader.GetPropertyName(i));
                if (tex != null) MarkTexture(tex, model);
            }
        }

        private void MarkRendererMaterials(Renderer r, ATOUsageModel model)
        {
            if (r == null) return;
            foreach (var m in r.sharedMaterials) MarkMaterialTextures(m, model);
        }

        private void ExpandWhitelistObject(Object obj, ATOUsageModel model)
        {
            switch (obj)
            {
                case Texture tex:
                    MarkTexture(tex, model);
                    model.notes.Add($"whitelist: texture {tex.name}");
                    return;
                case Material mat:
                    MarkMaterialTextures(mat, model);
                    model.notes.Add($"whitelist: material {mat.name}");
                    return;
                case Renderer rend:
                    MarkRendererMaterials(rend, model);
                    return;
                case AnimationClip clip:
                    // Materials referenced by the clip's PPtr curves contribute their textures.
                    // 动画 clip 的 PPtr 曲线引用的材质贡献其贴图。
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        foreach (var k in AnimationUtility.GetObjectReferenceCurve(clip, b))
                        {
                            if (k.value is Material m) MarkMaterialTextures(m, model);
                        }
                    }
                    model.notes.Add($"whitelist: clip {clip.name}");
                    return;
                case RuntimeAnimatorController ctrl:
                    foreach (var clip in ctrl.animationClips) ExpandWhitelistObject(clip, model);
                    return;
                case Mesh mesh:
                    // Mesh-level whitelist: mark usages in BuildUsages via mesh set.
                    // 网格级白名单：在 BuildUsages 阶段通过网格集合排除用途。
                    _whitelistedMeshes.Add(mesh);
                    model.notes.Add($"whitelist: mesh {mesh.name}");
                    return;
                case Component comp:
                    ExpandGameObject(comp.gameObject, model);
                    return;
                case GameObject go:
                    ExpandGameObject(go, model);
                    return;
                default:
                    ATOLog.Verbose($"whitelist object of unsupported type {obj.GetType().Name}: {obj.name}");
                    return;
            }
        }

        private void ExpandGameObject(GameObject go, ATOUsageModel model)
        {
            if (go == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) MarkRendererMaterials(r, model);
            foreach (var a in go.GetComponentsInChildren<Animation>(true))
                foreach (AnimationState st in a) if (st.clip != null) ExpandWhitelistObject(st.clip, model);
            foreach (var a in go.GetComponentsInChildren<Animator>(true))
                if (a.runtimeAnimatorController != null) ExpandWhitelistObject(a.runtimeAnimatorController, model);
            model.notes.Add($"whitelist: gameobject {go.name}");
        }

        private readonly HashSet<Mesh> _whitelistedMeshes = new HashSet<Mesh>();

        // ------------------------------------------------------------------
        // Renderers / 渲染器
        // ------------------------------------------------------------------

        private string RelPath(Transform t)
        {
            if (t == null) return "";
            var root = _ctx.AvatarRootTransform;
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private void CollectRenderers(ATOUsageModel model)
        {
            var root = _ctx.AvatarRootTransform;
            var all = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in all)
            {
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                if (r.CompareTag("EditorOnly")) continue; // double safety; NDMF removes them earlier / 双保险

                var path = RelPath(r.transform);
                bool activeNow = r.gameObject.activeInHierarchy && r.enabled;
                bool animatedActive = _anim.enableAnimatedPaths.Contains(path);
                if (!activeNow && !animatedActive) continue; // never enabled / 永不启用

                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null) continue;

                var rec = new ATORendererRecord
                {
                    renderer = r,
                    path = path,
                    mesh = mesh,
                    isSkinned = r is SkinnedMeshRenderer,
                    staticScale = AbsVec(r.transform.lossyScale),
                    activeAnimated = animatedActive,
                };
                rec.staticMaterials.AddRange(r.sharedMaterials.Where(m => m != null));

                // Animated scale (self + ancestors) / 动画缩放（自身+祖先）
                var asMax = Vector3.one;
                var t = r.transform;
                while (t != null && t != root)
                {
                    var p = RelPath(t);
                    if (_anim.maxAnimatedScale.TryGetValue(p, out var v))
                        asMax = new Vector3(asMax.x * Mathf.Max(1f, v.x), asMax.y * Mathf.Max(1f, v.y), asMax.z * Mathf.Max(1f, v.z));
                    t = t.parent;
                }
                rec.animatedScaleMax = asMax;
                rec.stAnimated = _anim.stAnimatedPaths.Contains(path);

                if (_anim.materialSwapsByPath.TryGetValue(path, out var swaps))
                {
                    foreach (var kv in swaps)
                    {
                        if (!rec.animatedSlotMaterials.TryGetValue(kv.Key, out var set))
                        {
                            set = new HashSet<Material>();
                            rec.animatedSlotMaterials[kv.Key] = set;
                        }
                        set.UnionWith(kv.Value);
                    }
                }
                if (_anim.materialFloatsByPath.TryGetValue(path, out var floats))
                {
                    foreach (var kv in floats) rec.animatedFloats[kv.Key] = kv.Value;
                }
                if (_anim.animatedMatProps.TryGetValue(path, out var props)) rec.animatedPropNames = props;

                rec.blendshapeFactor = ComputeBlendshapeFactor(rec);

                model.renderers.Add(rec);
            }
            model.report.renderersScanned = model.renderers.Count;
        }

        /// <summary>
        /// Max area multiplier from blendshapes: every shape is sampled at
        /// weight 0 and 100 only; larger area wins. No cross-shape combinations.
        /// 形态键最大面积系数：每个形态键只取 0 与 100 两种状态，取面积更大者；不做组合。
        /// </summary>
        private float ComputeBlendshapeFactor(ATORendererRecord rec)
        {
            if (!(rec.renderer is SkinnedMeshRenderer smr)) return 1f;
            var mesh = smr.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0) return 1f;

            try
            {
                var verts = mesh.vertices;
                var tris = mesh.triangles;
                if (verts.Length == 0 || tris.Length == 0) return 1f;

                float baseArea = 0f;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                    baseArea += TriArea(verts[tris[i]], verts[tris[i + 1]], verts[tris[i + 2]]);
                if (baseArea <= 1e-12f) return 1f;

                float best = 1f;
                var displaced = new Vector3[verts.Length];
                var dv = new Vector3[verts.Length];
                var dn = new Vector3[verts.Length];
                var dt = new Vector3[verts.Length];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(s);
                    if (frames <= 0) continue;
                    float w = mesh.GetBlendShapeFrameWeight(s, frames - 1);
                    mesh.GetBlendShapeFrameVertices(s, frames - 1, dv, dn, dt);
                    float scale = w > 1e-5f ? 100f / w : 0f;
                    for (int i = 0; i < verts.Length; i++)
                        displaced[i] = verts[i] + dv[i] * scale;
                    float area = 0f;
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                        area += TriArea(displaced[tris[i]], displaced[tris[i + 1]], displaced[tris[i + 2]]);
                    best = Mathf.Max(best, area / baseArea);
                }
                return Mathf.Clamp(best, 1f, 64f);
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"blendshape factor failed on {rec.path}: {e.Message}");
                return 1f;
            }
        }

        private static float TriArea(Vector3 a, Vector3 b, Vector3 c)
            => Vector3.Cross(b - a, c - a).magnitude * 0.5f;

        private static Vector3 AbsVec(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // ------------------------------------------------------------------
        // Usages / 用途
        // ------------------------------------------------------------------

        private ATOMaterialAnalysis AnalyzeCached(Material m)
        {
            if (m == null) return null;
            if (!_matAnalysis.TryGetValue(m, out var a))
            {
                a = ATOShaderAnalyzer.Analyze(m);
                _matAnalysis[m] = a;
                model_materials_count++;
            }
            return a;
        }

        private int model_materials_count;

        private void BuildUsages(ATOUsageModel model)
        {
            foreach (var rec in model.renderers)
            {
                int submeshCount = rec.mesh.subMeshCount;
                int slotCount = Mathf.Min(rec.renderer.sharedMaterials.Length, submeshCount);
                // materials per slot / 每槽位材质集合
                for (int slot = 0; slot < slotCount; slot++)
                {
                    var mats = new HashSet<Material>();
                    var sm = rec.renderer.sharedMaterials;
                    if (slot < sm.Length && sm[slot] != null) mats.Add(sm[slot]);
                    foreach (var kv in rec.animatedSlotMaterials)
                    {
                        if (kv.Key == slot || kv.Key == -1)
                            foreach (var m in kv.Value) if (m != null) mats.Add(m);
                    }

                    foreach (var m in mats)
                    {
                        var analysis = AnalyzeCached(m);
                        if (analysis == null) continue;
                        bool fromAnimation = !(slot < sm.Length && sm[slot] == m);

                        foreach (var slotInfo in analysis.slots)
                        {
                            var usage = new ATOUsage
                            {
                                role = slotInfo.role,
                                propertyName = slotInfo.propertyName,
                                usedChannels = slotInfo.usedChannelsMask,
                                renderer = rec.renderer,
                                rendererPath = rec.path,
                                submeshIndex = slot,
                                materialSlot = slot,
                                uvChannel = slotInfo.uvChannel,
                                material = m,
                                renderMode = analysis.renderMode,
                                cutoff = analysis.cutoff,
                                exclusion = slotInfo.exclusion,
                                note = slotInfo.note,
                                fromAnimation = fromAnimation,
                            };

                            var tex2d = slotInfo.texture as Texture2D;
                            if (tex2d != null)
                            {
                                var entry = model.EntryFor(tex2d);
                                usage.texture = entry;
                                // Propagate exclusions from analysis slot. / 槽分析得出的排除原因传递。
                                if (slotInfo.exclusion != ATOExcludeReason.None)
                                {
                                    usage.exclusion = slotInfo.exclusion;
                                }
                                // ST animation on this renderer -> everything on it is unsafe.
                                // 该渲染器存在 ST 动画 -> 其上贴图全部按不安全处理。
                                if (rec.stAnimated)
                                {
                                    usage.exclusion |= ATOExcludeReason.AnimatedUnsafe;
                                }
                                // EditorOnly-touched mesh / 白名单网格
                                if (_whitelistedMeshes.Contains(rec.mesh))
                                {
                                    usage.exclusion |= ATOExcludeReason.WhitelistedGraph;
                                }
                                model.usages.Add(usage);
                            }
                            else if (slotInfo.texture != null)
                            {
                                // RenderTexture etc: never optimized; leave silent record. / 渲染贴图等：不优化。
                                var ghost = new ATOUsage
                                {
                                    renderer = rec.renderer, rendererPath = rec.path, submeshIndex = slot,
                                    materialSlot = slot, material = m, propertyName = slotInfo.propertyName,
                                    exclusion = ATOExcludeReason.NotTexture2D,
                                };
                                model.usages.Add(ghost);
                            }
                        }

                        // Cutoff animation: strictest wins; store range on the analysis view.
                        // Cutoff 动画：取最严格；范围记录到分析视图。
                        if (rec.animatedFloats.TryGetValue("_Cutoff", out var range))
                        {
                            foreach (var usage in model.usages)
                            {
                                if (usage.material == m && usage.renderer == rec.renderer)
                                {
                                    usage.cutoff = StrictestCutoff(analysis.cutoff, range);
                                }
                            }
                        }
                    }
                }
            }
            model.report.materialsScanned = model_materials_count;
            model.report.texturesScanned = model.textures.Count;
        }

        private static float StrictestCutoff(float baseCutoff, ATOFloatRange range)
        {
            // For clipped silhouette comparisons the most demanding value is the
            // HIGHEST cutoff within [0,1] (more texels must survive).
            // 对 clip 轮廓比较而言最严格的是 [0,1] 区间内最大的 cutoff（更多纹素须存活）。
            float c = baseCutoff;
            if (range.min <= range.max)
            {
                c = Mathf.Max(baseCutoff, Mathf.Clamp01(range.max));
            }
            return Mathf.Clamp01(c);
        }

        // ------------------------------------------------------------------
        // UV groups / UV 组
        // ------------------------------------------------------------------

        private void BuildUVGroups(ATOUsageModel model)
        {
            var map = new Dictionary<(Mesh, int, int), ATOUVGroup>();
            var recsByRenderer = model.renderers.ToDictionary(r => r.renderer, r => r);
            foreach (var u in model.usages)
            {
                if (u.texture == null) continue;
                var key = (recsByRenderer[u.renderer].mesh, u.submeshIndex, u.uvChannel);
                if (!map.TryGetValue(key, out var g))
                {
                    g = new ATOUVGroup { mesh = key.Item1, submesh = key.Item2, uvChannel = key.Item3 };
                    map[key] = g;
                    model.uvGroups.Add(g);
                }
                g.usages.Add(u);
                u.group = g; // back-reference for GroupOf / GroupOf 的反向引用
                var rec = recsByRenderer[u.renderer];
                g.areaFactor = Mathf.Max(g.areaFactor, rec.MaxAreaFactor());
            }
            model.report.uvGroupsTotal = model.uvGroups.Count;
        }
    }
}

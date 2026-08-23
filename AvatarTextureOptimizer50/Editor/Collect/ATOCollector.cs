// -----------------------------------------------------------------------------
// ATOCollector.cs — collect renderers, materials, animations; build UV groups.
// ATOCollector.cs — 采集渲染器、材质、动画，并建立 UV 组。
//
// Animation scanning runs through NDMF AnimatorServicesContext (all VRC layers +
// Animator components are virtualized & reconciled automatically).
// 动画扫描经由 NDMF AnimatorServicesContext（所有 VRC 层与 Animator 组件均被
// 虚拟化并自动写回）。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    internal static class ATOCollector
    {
        /// <summary>Run the collection stage. / 执行采集阶段。</summary>
        public static void Run(BuildContext ctx, ATOBuildState st)
        {
            var root = ctx.AvatarRootObject;

            // ---- path cache / 路径缓存 ----
            var pathToTransform = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                pathToTransform[RelativePath(root, t)] = t;

            // ---- 1. renderers at rest / 静态渲染器 ----
            CollectRenderers(root, st);

            // ---- 2. animations / 动画 ----
            var asc = ctx.Extension<AnimatorServicesContext>();
            CollectAnimations(ctx, asc, st, pathToTransform);

            // ---- 3. drop renderers that stay disabled / 丢弃始终禁用的 ----
            st.renderers.RemoveAll(r => !r.IsRelevant);

            // ---- 4. material analysis (initial + swapped) / 材质分析（初始+切换） ----
            AnalyzeAllMaterials(st);

            // ---- 5. whitelist resolution / 白名单解析 ----
            ResolveWhitelist(st);

            // ---- 6. build texture usage & UV groups / 建立贴图使用与UV组 ----
            BuildUvGroups(st);

            // ---- 7. animated ST / cutoff strictness / 动画ST与cutoff从严 ----
            ApplyAnimationStrictness(st);

            ATOLog.Info($"Collected: {st.renderers.Count} renderers, " +
                        $"{st.materialAnalysis.Count} materials, {st.textures.Count} textures, " +
                        $"{st.uvGroups.Count} UV groups");
        }

        // ================================================================= //
        // 1. renderers
        // ================================================================= //

        private static void CollectRenderers(GameObject root, ATOBuildState st)
        {
            foreach (var go in root.GetComponentsInChildren<Transform>(true))
            {
                var g = go.gameObject;
                // EditorOnly objects are stripped by VRC at upload / VRC 上传时会剔除 EditorOnly
                if (IsEditorOnlyBranch(root, g.transform)) continue;

                foreach (var r in g.GetComponents<Renderer>())
                {
                    if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;

                    var info = new RendererInfo
                    {
                        renderer = r,
                        path = RelativePath(root, r.transform),
                        isSkinned = r is SkinnedMeshRenderer,
                        mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh,
                        activeAtRest = r.gameObject.activeInHierarchy && r.enabled,
                    };

                    if (info.mesh == null) continue;

                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        info.slotMaterials.Add(new HashSet<Material>());
                        info.initialMaterial.Add(mats[i]);
                        if (mats[i] != null) info.slotMaterials[i].Add(mats[i]);
                    }

                    // blendshape rest weights / 形态键静态权重
                    if (r is SkinnedMeshRenderer smr2)
                    {
                        var mesh = smr2.sharedMesh;
                        for (int b = 0; b < mesh.blendShapeCount; b++)
                        {
                            float w = smr2.GetBlendShapeWeight(b);
                            if (w > 0.01f) info.blendshapeMax[mesh.GetBlendShapeName(b)] = Mathf.Min(w, 100f);
                        }
                    }

                    // rest scale factor (animated factor multiplied later)
                    // 静态缩放系数（动画系数稍后相乘）
                    st.renderers.Add(info);
                }
            }
        }

        private static bool IsEditorOnlyBranch(GameObject root, Transform t)
        {
            while (t != null && t.gameObject != root)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }

            return false;
        }

        // ================================================================= //
        // 2. animations
        // ================================================================= //

        private static void CollectAnimations(BuildContext ctx, AnimatorServicesContext asc,
            ATOBuildState st, Dictionary<string, Transform> pathToTransform)
        {
            // (rendererPath, materialProp) → has ST animation
            // （渲染器路径, 材质属性）→ 是否存在 ST 动画
            var stAnimatedProps = new HashSet<(string, string)>();
            // (rendererPath, slot) → swapped materials / 换入材质
            var swappedMaterials = new Dictionary<(string, int), HashSet<Material>>();
            // rendererPath → animated enable / 动画启用
            var animatedEnabled = new HashSet<string>();
            // transformPath → per-axis max |scale| seen / 变换路径→各轴最大|缩放|
            var scaleMax = new Dictionary<string, Vector3>();
            // (rendererPath, blendshape) → animated / 形态键被动画
            var animatedBlendshapes = new HashSet<(string, string)>();
            // (rendererPath, alphaProp) → cutoff values / cutoff 键值
            var animatedCutoffs = new Dictionary<(string, string), SortedSet<float>>();
            // (rendererPath, anyAlphaStructProp) → ambiguity / alpha 结构属性动画
            var animatedAlphaStruct = new HashSet<string>();

            var clips = asc.ControllerContext.Controllers.Values
                .SelectMany(c => c.AllReachableNodes())
                .OfType<VirtualClip>()
                .Distinct()
                .ToList();

            ATOLog.Info($"Animation scan: {asc.ControllerContext.Controllers.Count} controllers, " +
                        $"{clips.Count} clips");

            foreach (var clip in clips)
            {
                // ---- object (PPtr) curves: material swaps / 对象曲线：换材质 ----
                foreach (var binding in clip.GetObjectCurveBindings())
                {
                    if (binding.propertyName == null ||
                        !binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                        continue;
                    int idx = ParseSlotIndex(binding.propertyName);
                    if (idx < 0) continue;
                    var kfs = clip.GetObjectCurve(binding);
                    if (kfs == null) continue;
                    var key = (binding.path, idx);
                    if (!swappedMaterials.TryGetValue(key, out var set))
                        swappedMaterials[key] = set = new HashSet<Material>();
                    foreach (var kf in kfs)
                        if (kf.value is Material mat) set.Add(mat);
                }

                // ---- float curves / 浮点曲线 ----
                foreach (var b in clip.GetFloatCurveBindings())
                {
                    var prop = b.propertyName;
                    if (string.IsNullOrEmpty(prop)) continue;

                    if (b.type == typeof(Transform) && prop.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                    {
                        var curve = clip.GetFloatCurve(b);
                        if (curve == null) continue;
                        Vector3 v = scaleMax.TryGetValue(b.path, out var cur) ? cur : Vector3.one;
                        float m = 0f;
                        foreach (var k in curve.keys) m = Mathf.Max(m, Mathf.Abs(k.value));
                        // Keep rest=1 as baseline too / 静态1也作为基线
                        m = Mathf.Max(m, 1f);
                        if (prop.EndsWith(".x")) v.x = Mathf.Max(v.x, m);
                        else if (prop.EndsWith(".y")) v.y = Mathf.Max(v.y, m);
                        else if (prop.EndsWith(".z")) v.z = Mathf.Max(v.z, m);
                        scaleMax[b.path] = v;
                        continue;
                    }

                    if (b.type == typeof(GameObject) && prop == "m_IsActive")
                    {
                        var curve = clip.GetFloatCurve(b);
                        if (curve != null && curve.keys.Any(k => k.value > 0.5f))
                            animatedEnabled.Add(b.path);
                        continue;
                    }

                    if (typeof(Behaviour).IsAssignableFrom(b.type) && prop == "m_Enabled")
                    {
                        var curve = clip.GetFloatCurve(b);
                        if (curve != null && curve.keys.Any(k => k.value > 0.5f))
                            animatedEnabled.Add(b.path);
                        continue;
                    }

                    if (b.type == typeof(SkinnedMeshRenderer) &&
                        prop.StartsWith("blendShape.", StringComparison.Ordinal))
                    {
                        animatedBlendshapes.Add((b.path, prop.Substring("blendShape.".Length)));
                        continue;
                    }

                    if (prop.StartsWith("material.", StringComparison.Ordinal) &&
                        typeof(Renderer).IsAssignableFrom(b.type))
                    {
                        var matProp = prop.Substring("material.".Length);

                        // ST component animation → transform / ST 分量动画→变换
                        int stIdx = matProp.IndexOf("_ST.", StringComparison.Ordinal);
                        if (stIdx > 0)
                        {
                            var texProp = matProp.Substring(0, stIdx);
                            stAnimatedProps.Add((b.path, texProp));
                            continue;
                        }

                        var curve = clip.GetFloatCurve(b);

                        // cutoff-like props / cutoff 类属性
                        if (matProp == "_Cutoff" || matProp == "_SubpassCutoff" || matProp == "_AlphaMaskValue")
                        {
                            var key = (b.path, matProp);
                            if (!animatedCutoffs.TryGetValue(key, out var set))
                                animatedCutoffs[key] = set = new SortedSet<float>();
                            if (curve != null)
                                foreach (var k in curve.keys)
                                    if (k.value > 0f && k.value < 1f) set.Add(k.value);
                            continue;
                        }

                        // alpha structure / UV structure changes → strictest / 结构变化→从严
                        if (matProp.Contains("_ScrollRotate") || matProp.EndsWith("Angle") ||
                            matProp.EndsWith("_UVMode") || matProp == "_ParallaxScale" ||
                            matProp == "_ShiftBackfaceUV" || matProp == "_AlphaMaskMode" ||
                            matProp == "_Surface" || matProp == "_Blend" || matProp == "_Mode" ||
                            matProp == "_TransparentMode" || matProp == "_CullMode" ||
                            matProp.EndsWith("BlendMode") || matProp.EndsWith("RenderType"))
                        {
                            animatedAlphaStruct.Add(b.path);
                            continue;
                        }

                        // other material floats (emission color etc.) are harmless
                        // 其余材质浮点（发光颜色等）无影响
                    }
                }
            }

            // ---- apply to renderers / 应用到渲染器 ----
            foreach (var r in st.renderers)
            {
                // enabled animations on self or ancestors / 自身或祖先被动画启用
                if (animatedEnabled.Contains(r.path)) r.animatedActive = true;
                var t = r.renderer.transform.parent;
                while (t != null)
                {
                    var p = RelativePath(ctx.AvatarRootObject, t);
                    if (animatedEnabled.Contains(p)) { r.animatedActive = true; break; }
                    t = t.parent;
                }

                // scale factor = Π over ancestor transforms of max pairwise product
                // 缩放系数 = 各级祖先最大两轴积之积
                float factor = 1f;
                var cur = r.renderer.transform;
                while (cur != null)
                {
                    var p = RelativePath(ctx.AvatarRootObject, cur);
                    if (scaleMax.TryGetValue(p, out var s))
                    {
                        float rest = cur.localScale.sqrMagnitude > 0f ? 1f : 1f; // rest applied via lossyScale later
                        float pair = Mathf.Max(Mathf.Abs(s.x * s.y), Mathf.Max(Mathf.Abs(s.x * s.z), Mathf.Abs(s.y * s.z)));
                        factor *= Mathf.Max(1f, pair * rest);
                    }

                    cur = cur.parent == ctx.AvatarRootTransform ? null : cur.parent;
                }

                r.scaleAreaFactor = factor;

                foreach (var (key, mats) in swappedMaterials)
                {
                    if (key.Item1 != r.path) continue;
                    if (key.Item2 < r.slotMaterials.Count)
                    {
                        foreach (var m in mats) r.slotMaterials[key.Item2].Add(m);
                        r.slotsWithSoloSwapAnimation.Add(key.Item2);
                    }
                }

                foreach (var (path, bs) in animatedBlendshapes)
                {
                    if (path == r.path)
                        r.blendshapeMax[bs] = 100f; // consider the 100 state / 考虑100状态
                }
            }

            // stash for stage 7 / 暂存给第7步
            st.stash.StAnimatedProps = stAnimatedProps;
            st.stash.AnimatedCutoffs = animatedCutoffs;
            st.stash.AnimatedAlphaStruct = animatedAlphaStruct;
        }

        private static int ParseSlotIndex(string propName)
        {
            // "m_Materials.Array.data[N]" → N
            int s = propName.IndexOf('[') + 1;
            int e = propName.IndexOf(']');
            if (s <= 0 || e <= s) return -1;
            return int.TryParse(propName.Substring(s, e - s), out var n) ? n : -1;
        }

        // ================================================================= //
        // 4. material analysis
        // ================================================================= //

        private static void AnalyzeAllMaterials(ATOBuildState st)
        {
            foreach (var r in st.renderers)
            foreach (var slot in r.slotMaterials)
            foreach (var m in slot)
            {
                if (m == null) continue;
                if (!st.materialAnalysis.ContainsKey(m))
                    st.materialAnalysis[m] = ATOShaderAnalyzer.Analyze(m);
            }
        }

        // ================================================================= //
        // 5. whitelist
        // ================================================================= //

        private static void ResolveWhitelist(ATOBuildState st)
        {
            var wl = st.settings.component?.whitelist;
            if (wl == null) return;

            var textures = new HashSet<Texture2D>();
            foreach (var entry in wl)
            {
                if (entry == null) continue;
                CollectWhitelistedTextures(entry, st, textures);
            }

            foreach (var tex in textures)
            {
                var info = st.GetOrCreateTex(tex);
                info.MarkWhitelist("user whitelist / 用户白名单");
            }

            ATOLog.Info($"Whitelist resolved: {textures.Count} textures");
        }

        private static void CollectWhitelistedTextures(Object obj, ATOBuildState st, HashSet<Texture2D> into)
        {
            switch (obj)
            {
                case Texture2D t:
                    into.Add(t);
                    return;
                case Material m:
                    foreach (var u in ATOShaderAnalyzer.Analyze(m).uses)
                        if (u.texture != null) into.Add(u.texture);
                    return;
                case Mesh mesh:
                    foreach (var r in st.renderers.Where(r => r.mesh == mesh))
                        foreach (var slot in r.slotMaterials)
                        foreach (var m in slot)
                            if (m != null)
                                foreach (var u in st.materialAnalysis[m].uses)
                                    if (u.texture != null) into.Add(u.texture);
                    return;
                case GameObject go:
                    foreach (var r in st.renderers.Where(r => r.transform.IsChildOf(go.transform)))
                        foreach (var slot in r.slotMaterials)
                        foreach (var m in slot)
                            if (m != null)
                                foreach (var u in st.materialAnalysis[m].uses)
                                    if (u.texture != null) into.Add(u.texture);
                    return;
                case AnimationClip clip:
                    foreach (var ed in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, ed))
                            if (kf.value is Texture2D t2) into.Add(t2);
                    return;
                case Renderer rr:
                    foreach (var m in rr.sharedMaterials)
                        if (m != null)
                            foreach (var u in ATOShaderAnalyzer.Analyze(m).uses)
                                if (u.texture != null) into.Add(u.texture);
                    return;
                default:
                    // Generic serialized traversal (any component/asset), depth-limited.
                    // 通用序列化遍历（任意组件/资产），限制深度。
                    TraverseSerialized(obj, into, 0);
                    return;
            }
        }

        private static void TraverseSerialized(Object obj, HashSet<Texture2D> into, int depth)
        {
            if (obj == null || depth > 6) return;
            if (obj is Texture2D t) { into.Add(t); return; }

            try
            {
                var so = new SerializedObject(obj);
                var p = so.GetIterator();
                bool enterChildren = true;
                while (p.Next(enterChildren))
                {
                    enterChildren = p.propertyType != SerializedPropertyType.ObjectReference;
                    if (p.propertyType == SerializedPropertyType.ObjectReference &&
                        p.objectReferenceValue is Object o && !(o is Mesh) && !(o is GameObject))
                        TraverseSerialized(o, into, depth + 1);
                }
            }
            catch (Exception) { }
        }

        // ================================================================= //
        // 6. UV groups
        // ================================================================= //

        private static void BuildUvGroups(ATOBuildState st)
        {
            var groupMap = new Dictionary<(RendererInfo, int), UvGroupInfo>();

            foreach (var r in st.renderers)
            {
                for (int slot = 0; slot < r.slotMaterials.Count; slot++)
                foreach (var m in r.slotMaterials[slot])
                {
                    if (m == null) continue;
                    var analysis = st.materialAnalysis[m];
                    if (!analysis.supported)
                    {
                        // unsupported material → all its textures whitelisted
                        // 不受支持的材质 → 其全部贴图进白名单
                        foreach (var u in analysis.uses)
                        {
                            if (u.texture == null) continue;
                            var info = st.GetOrCreateTex(u.texture);
                            info.MarkWhitelist($"unsupported shader '{m.shader.name}' / 不支持的着色器");
                        }

                        continue;
                    }

                    foreach (var u in analysis.uses)
                    {
                        if (u.texture == null) continue;
                        var info = st.GetOrCreateTex(u.texture);
                        info.usedByMaterials.TryGetValue(m, out var set);
                        if (set == null) info.usedByMaterials[m] = set = new HashSet<string>();
                        set.Add(u.property);

                        if (u.transformed || u.uvChannel < 0)
                        {
                            info.MarkWhitelist(u.note ?? "UV transformed / UV被变换");
                            continue;
                        }

                        var key = (r, u.uvChannel);
                        if (!groupMap.TryGetValue(key, out var g))
                        {
                            groupMap[key] = g = new UvGroupInfo { owner = r, channel = u.uvChannel };
                            st.uvGroups.Add(g);
                        }

                        g.materials.Add(m);
                        if (!g.textures.Contains(info)) g.textures.Add(info);
                        if (!info.usages.Any(x => x.group == g && x.role == u.role))
                            info.usages.Add((g, u.role));

                        // alpha usage bookkeeping / alpha 用途登记
                        if (u.role == TexRole.Main && analysis.alphaMode != AlphaMode.Opaque)
                        {
                            info.alphaUsage.TryGetValue(m, out var au);
                            var mode = StrictestMode(au.mode, analysis.alphaMode);
                            var cutoffs = au.cutoffs ?? new List<float>();
                            if (analysis.alphaMode == AlphaMode.Cutout)
                            {
                                cutoffs.AddRange(analysis.cutoffs);
                                if (cutoffs.Count == 0) cutoffs.Add(0.5f);
                            }

                            info.alphaUsage[m] = (mode, cutoffs);
                        }
                    }
                }
            }
        }

        private static AlphaMode StrictestMode(AlphaMode a, AlphaMode b)
        {
            // Both matter → Blend is the strictest for RMSE; Cutout for IoU.
            // Both are evaluated when ambiguous (see ATOQuality). Order: Opaque < Cutout < Blend.
            // 两者都评估（见 ATOQuality）。此处保留更严的：Blend 优先（RMSE 全图敏感）。
            return (AlphaMode)Mathf.Max((int)a, (int)b);
        }

        // ================================================================= //
        // 7. animated strictness
        // ================================================================= //

        private static void ApplyAnimationStrictness(ATOBuildState st)
        {
            var stash = st.stash;

            foreach (var r in st.renderers)
            {
                for (int slot = 0; slot < r.slotMaterials.Count; slot++)
                foreach (var m in r.slotMaterials[slot])
                {
                    if (m == null || !st.materialAnalysis.TryGetValue(m, out var analysis)) continue;

                    foreach (var u in analysis.uses)
                    {
                        if (u.texture == null) continue;
                        var info = st.texBySource.TryGetValue(u.texture, out var ti) ? ti : null;
                        if (ti == null) continue;

                        // animated ST on this renderer + prop → whitelist
                        // 该渲染器上该属性存在 ST 动画 → 白名单
                        if (stash.StAnimatedProps.Contains((r.path, u.property)))
                        {
                            // Only if this texture is actually bound via that prop on this renderer
                            // 仅当该贴图确实经此属性绑定在该渲染器上
                            if (m.GetTexture(u.property) == u.texture)
                                ti.MarkWhitelist($"animated ST on '{u.property}' / 属性存在ST动画");
                        }

                        // animated alpha-structure props → strictest alpha evaluation
                        // alpha 结构属性动画 → 最严苛 alpha 评估
                        if (stash.AnimatedAlphaStruct.Contains(r.path) &&
                            info.alphaUsage.TryGetValue(m, out var au))
                        {
                            info.alphaUsage[m] = (AlphaMode.Blend, au.cutoffs);
                            analysis.alphaAmbiguous = true;
                        }

                        // animated cutoffs → collect all keys / 收集动画 cutoff 全部键值
                        foreach (var set in stash.AnimatedCutoffs.Values)
                        {
                            if (set.Count > 0 && info.alphaUsage.ContainsKey(m))
                            {
                                var cur = info.alphaUsage[m];
                                foreach (var c in set) if (!cur.cutoffs.Contains(c)) cur.cutoffs.Add(c);
                            }
                        }
                    }
                }
            }
        }

        // ================================================================= //

        internal static string RelativePath(GameObject root, Transform t)
        {
            if (t == root.transform) return "";
            var parts = new List<string>();
            while (t != null && t != root.transform)
            {
                parts.Add(t.name);
                t = t.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }

    /// <summary>Temporary cross-stage stash stored inside ATOBuildState.
    /// 存于 ATOBuildState 内的跨阶段暂存。</summary>
    internal sealed class ATOCollectorStash
    {
        public HashSet<(string, string)> StAnimatedProps = new HashSet<(string, string)>();
        public Dictionary<(string, string), SortedSet<float>> AnimatedCutoffs =
            new Dictionary<(string, string), SortedSet<float>>();
        public HashSet<string> AnimatedAlphaStruct = new HashSet<string>();
    }
}

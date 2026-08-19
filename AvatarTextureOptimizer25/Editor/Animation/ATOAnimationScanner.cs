// Avatar Texture Optimizer / 头像贴图优化器
// Animation scan: material swaps (PPtr), animated material floats (cutoff etc.),
// object enable/disable, transform scale. All reads go through NDMF's
// VirtualControllerContext so MA/LLC-virtualized controllers are included;
// writes (material reference remaps) go through VirtualClip which is
// copy-on-write safe (marker clips / platform proxies refuse writes).
// 动画扫描：材质切换（PPtr）、材质浮点动画（Cutoff 等）、物体启停、缩放。
// 所有读取经由 NDMF VirtualControllerContext（覆盖 MA/LLC 虚拟化后的控制器）；
// 写入经 VirtualClip（写时复制，marker clip/平台代理动画拒绝写入，天然安全）。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>One sampled range of an animated float property. / 一条动画浮点属性的取值范围。</summary>
    public struct ATOFloatRange
    {
        public float min, max;
        public void Add(float v)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
    }

    /// <summary>
    /// Everything the pipeline needs to know about animations.
    /// 管线所需的全部动画信息。
    /// </summary>
    public sealed class ATOAnimationData
    {
        /// <summary>Per-renderer path: materials injected by animation per material slot. -1 = "all slots / unknown slot". / 每渲染器路径：动画注入的逐槽位材质集合；-1 表示全部/未知槽位。</summary>
        public readonly Dictionary<string, Dictionary<int, HashSet<Material>>> materialSwapsByPath =
            new Dictionary<string, Dictionary<int, HashSet<Material>>>();

        /// <summary>Per-renderer path: animated material float ranges (property name -> range). / 每渲染器路径：材质浮点动画范围。</summary>
        public readonly Dictionary<string, Dictionary<string, ATOFloatRange>> materialFloatsByPath =
            new Dictionary<string, Dictionary<string, ATOFloatRange>>();

        /// <summary>Paths animated by enable/disable curves (m_IsActive or m_Enabled). / 受启停动画影响的路径。</summary>
        public readonly HashSet<string> enableAnimatedPaths = new HashSet<string>();

        /// <summary>Per-transform path: maximum absolute scale seen (component-wise). / 每变换路径：动画中出现的最大绝对缩放（分量取大）。</summary>
        public readonly Dictionary<string, Vector3> maxAnimatedScale = new Dictionary<string, Vector3>();

        /// <summary>Paths that animate any ST-like material property (those textures become whitelist). / 动画修改 ST 类属性的路径。</summary>
        public readonly HashSet<string> stAnimatedPaths = new HashSet<string>();

        /// <summary>Properties animated per renderer path (for material-dedup safety checks). / 每渲染器路径上被动画的属性集合（材质去重安全检查用）。</summary>
        public readonly Dictionary<string, HashSet<string>> animatedMatProps = new Dictionary<string, HashSet<string>>();

        /// <summary>Virtual clips referencing each material (for later reference remap). / 引用每个材质的 VirtualClip（后续引用重映射用）。</summary>
        public readonly Dictionary<Material, List<(VirtualClip clip, EditorCurveBinding binding)>> materialPPtrOwners =
            new Dictionary<Material, List<(VirtualClip, EditorCurveBinding)>>();

        /// <summary>All scanned clips (statistics). / 扫描过的 clip 数量（统计）。</summary>
        public int clipCount;
    }

    /// <summary>
    /// Scans all animation sources. Read API only produces data; remaps are
    /// applied later through <see cref="RemapMaterialReferences"/>.
    /// 扫描所有动画来源。读取只产生数据；重映射经 <see cref="RemapMaterialReferences"/> 应用。
    /// </summary>
    public static class ATOAnimationScanner
    {
        /// <summary>
        /// Scan the whole avatar using NDMF's virtual controllers plus
        /// (for material source discovery only) legacy Animation components.
        /// 使用 NDMF 虚拟控制器扫描整个 Avatar（另以旧式 Animation 组件补充贴图来源发现）。
        /// </summary>
        public static ATOAnimationData Scan(BuildContext ctx)
        {
            var data = new ATOAnimationData();
            var root = ctx.AvatarRootTransform;
            var scannedVirtual = new HashSet<VirtualClip>();

            // ---- 1) Virtual controllers (covers MA/LLC virtualized controllers) ----
            // ---- 1) 虚拟控制器（覆盖 MA/LLC 虚拟化后的控制器）----
            VirtualControllerContext vcc = null;
            try
            {
                vcc = ctx.Extension<VirtualControllerContext>();
            }
            catch (Exception e)
            {
                ATOLog.Verbose("VirtualControllerContext unavailable: " + e.Message);
                // Activate on demand (our pass declares WithRequiredExtension).
                // 按需激活（本 pass 已声明 WithRequiredExtension）。
                try { vcc = (VirtualControllerContext)ctx.ActivateExtensionContext(typeof(VirtualControllerContext)); }
                catch (Exception e2)
                {
                    ATOLog.Warn("virtual controller context not available, falling back to direct scan: " + e2.Message);
                }
            }

            if (vcc != null)
            {
                foreach (var vac in SafeEnumerate(vcc))
                {
                    ScanVirtualController(vac, root, data, scannedVirtual);
                }
            }

            // ---- 2) Direct scan fallback (any controller not tracked by the virtualization layer) ----
            // ---- 2) 直接扫描兜底（未被虚拟化层跟踪的控制器）----
            foreach (var clip in CollectDirectClips(ctx))
            {
                ScanClipReadOnly(clip, root, data);
            }

            data.clipCount = scannedVirtual.Count;
            return data;
        }

        private static IEnumerable<VirtualAnimatorController> SafeEnumerate(VirtualControllerContext vcc)
        {
            VirtualAnimatorController[] all;
            try
            {
                all = vcc.GetAllControllers().ToArray();
            }
            catch (Exception e)
            {
                ATOLog.Warn("GetAllControllers failed: " + e.Message);
                yield break;
            }
            foreach (var c in all) yield return c;
        }

        private static void ScanVirtualController(
            VirtualAnimatorController vac, Transform root, ATOAnimationData data, HashSet<VirtualClip> scanned)
        {
            foreach (var layer in vac.Layers)
            {
                ScanStateMachine(layer.StateMachine, root, data, scanned);
            }
        }

        private static void ScanStateMachine(
            VirtualStateMachine sm, Transform root, ATOAnimationData data, HashSet<VirtualClip> scanned)
        {
            if (sm == null) return;
            foreach (var child in sm.States)
            {
                ScanMotion(child.State?.Motion, root, data, scanned);
            }
            foreach (var child in sm.StateMachines)
            {
                ScanStateMachine(child.StateMachine, root, data, scanned);
            }
        }

        private static void ScanMotion(
            VirtualMotion motion, Transform root, ATOAnimationData data, HashSet<VirtualClip> scanned)
        {
            switch (motion)
            {
                case null:
                    return;
                case VirtualClip vc:
                    ScanVirtualClip(vc, root, data, scanned);
                    break;
                case VirtualBlendTree bt:
                    foreach (var child in bt.Children) ScanMotion(child.Motion, root, data, scanned);
                    break;
            }
        }

        private static void ScanVirtualClip(
            VirtualClip clip, Transform root, ATOAnimationData data, HashSet<VirtualClip> scanned)
        {
            if (clip == null || !scanned.Add(clip)) return;

            // Float curves / 浮点曲线
            IEnumerable<EditorCurveBinding> floatBindings;
            try { floatBindings = clip.GetFloatCurveBindings()?.ToArray(); }
            catch { floatBindings = null; }

            if (floatBindings != null)
            {
                foreach (var b in floatBindings)
                {
                    try
                    {
                        var curve = clip.GetFloatCurve(b);
                        if (curve == null) continue;
                        ProcessFloatBinding(b, curve, root, data);
                    }
                    catch (Exception e)
                    {
                        ATOLog.Verbose($"curve read failed {b.path}:{b.propertyName}: {e.Message}");
                    }
                }
            }

            // Object reference curves (material swaps) / 对象引用曲线（材质切换）
            IEnumerable<EditorCurveBinding> objBindings;
            try { objBindings = clip.GetObjectCurveBindings()?.ToArray(); }
            catch { objBindings = null; }

            if (objBindings != null)
            {
                foreach (var b in objBindings)
                {
                    try
                    {
                        var keys = clip.GetObjectCurve(b);
                        if (keys == null) continue;
                        ProcessPPtrBinding(clip, b, keys, data);
                    }
                    catch (Exception e)
                    {
                        ATOLog.Verbose($"pptr read failed {b.path}:{b.propertyName}: {e.Message}");
                    }
                }
            }
        }

        private static void ProcessFloatBinding(
            EditorCurveBinding b, AnimationCurve curve, Transform root, ATOAnimationData data)
        {
            var prop = b.propertyName ?? "";

            // GameObject enable / 物体启停
            if (prop == "m_IsActive")
            {
                data.enableAnimatedPaths.Add(b.path);
                return;
            }

            // Renderer enable / 渲染器启停
            if (prop == "m_Enabled")
            {
                if (typeof(Renderer).IsAssignableFrom(b.type) || b.type == typeof(MeshRenderer) ||
                    b.type == typeof(SkinnedMeshRenderer))
                {
                    data.enableAnimatedPaths.Add(b.path);
                }
                return;
            }

            // Transform scale / 缩放
            if (prop.StartsWith("m_LocalScale", StringComparison.Ordinal))
            {
                float maxAbs = curve.keys.Length == 0 ? 1f : curve.keys.Max(k => Mathf.Abs(k.value));
                if (maxAbs < 1e-6f) maxAbs = 1e-6f;
                if (!data.maxAnimatedScale.TryGetValue(b.path, out var v)) v = Vector3.one;
                if (prop.EndsWith(".x")) v.x = Mathf.Max(v.x, maxAbs);
                else if (prop.EndsWith(".y")) v.y = Mathf.Max(v.y, maxAbs);
                else if (prop.EndsWith(".z")) v.z = Mathf.Max(v.z, maxAbs);
                else v = new Vector3(Mathf.Max(v.x, maxAbs), Mathf.Max(v.y, maxAbs), Mathf.Max(v.z, maxAbs));
                data.maxAnimatedScale[b.path] = v;
                return;
            }

            // Material float properties / 材质浮点属性
            if (prop.StartsWith("material.", StringComparison.Ordinal))
            {
                var name = prop.Substring("material.".Length);
                if (!data.materialFloatsByPath.TryGetValue(b.path, out var dict))
                {
                    dict = new Dictionary<string, ATOFloatRange>();
                    data.materialFloatsByPath[b.path] = dict;
                }
                if (!dict.TryGetValue(name, out var r)) r = new ATOFloatRange { min = float.MaxValue, max = float.MinValue };
                foreach (var k in curve.keys) r.Add(k.value);
                if (curve.keys.Length == 0) { r.min = 0; r.max = 0; }
                dict[name] = r;

                if (!data.animatedMatProps.TryGetValue(b.path, out var set))
                {
                    set = new HashSet<string>();
                    data.animatedMatProps[b.path] = set;
                }
                set.Add(name);

                if (name.EndsWith("_ST", StringComparison.Ordinal) ||
                    name.EndsWith("_ScrollRotate", StringComparison.Ordinal) ||
                    name.EndsWith("_UVMode", StringComparison.Ordinal))
                {
                    data.stAnimatedPaths.Add(b.path);
                }
            }
        }

        private static void ProcessPPtrBinding(
            VirtualClip clip, EditorCurveBinding b, ObjectReferenceKeyframe[] keys, ATOAnimationData data)
        {
            if (keys == null) return;
            var prop = b.propertyName ?? "";
            bool isMaterialSlot = prop.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal);
            int slot = -1;
            if (isMaterialSlot)
            {
                var start = "m_Materials.Array.data[".Length;
                var end = prop.IndexOf(']', start);
                if (end > start && int.TryParse(prop.Substring(start, end - start), out var parsed)) slot = parsed;
            }

            foreach (var k in keys)
            {
                if (!(k.value is Material m) || m == null) continue;
                if (!data.materialSwapsByPath.TryGetValue(b.path, out var dict))
                {
                    dict = new Dictionary<int, HashSet<Material>>();
                    data.materialSwapsByPath[b.path] = dict;
                }
                var key = isMaterialSlot && slot >= 0 ? slot : -1;
                if (!dict.TryGetValue(key, out var set))
                {
                    set = new HashSet<Material>();
                    dict[key] = set;
                }
                set.Add(m);

                // Legacy/direct clips are read-only by policy (clip == null here):
                // only virtual clips can be remapped later (COW-safe).
                // 旧式/直接 clip 按策略只读（此处 clip == null）：只有虚拟 clip
                // 后续可重映射（写时复制安全）。
                if (clip == null) continue;
                if (!data.materialPPtrOwners.TryGetValue(m, out var owners))
                {
                    owners = new List<(VirtualClip, EditorCurveBinding)>();
                    data.materialPPtrOwners[m] = owners;
                }
                owners.Add((clip, b));
            }
        }

        /// <summary>
        /// After materials are finalized, rewrite PPtr material references in the
        /// virtual clips. Safe: VirtualClip refuses writes on immutable marker clips.
        /// 材质定稿后重写 VirtualClip 中的 PPtr 材质引用。安全：不可变 marker clip 会被拒绝写入。
        /// </summary>
        public static int RemapMaterialReferences(ATOAnimationData data, Dictionary<Material, Material> remap)
        {
            if (remap == null || remap.Count == 0) return 0;
            int changed = 0;
            foreach (var kv in data.materialPPtrOwners)
            {
                var oldMat = kv.Key;
                if (!remap.TryGetValue(oldMat, out var newMat) || newMat == null || ReferenceEquals(oldMat, newMat))
                    continue;
                foreach (var (clip, binding) in kv.Value)
                {
                    try
                    {
                        var keys = clip.GetObjectCurve(binding);
                        if (keys == null) continue;
                        bool any = false;
                        for (int i = 0; i < keys.Length; i++)
                        {
                            if (ReferenceEquals(keys[i].value, oldMat))
                            {
                                keys[i].value = newMat;
                                any = true;
                            }
                        }
                        if (any)
                        {
                            clip.SetObjectCurve(binding, keys);
                            changed++;
                        }
                    }
                    catch (Exception e)
                    {
                        ATOLog.Warn($"failed to remap material reference in {clip.Name}: {e.Message}");
                    }
                }
            }
            return changed;
        }

        // -------- Direct (read-only) fallback collection / 直接（只读）兜底收集 --------

        private static IEnumerable<AnimationClip> CollectDirectClips(BuildContext ctx)
        {
            var result = new HashSet<AnimationClip>();
            var root = ctx.AvatarRootObject;

            // Legacy Animation components (read-only discovery) / 旧式 Animation 组件（只读发现）
            foreach (var anim in root.GetComponentsInChildren<Animation>(true))
            {
                try
                {
                    foreach (AnimationState st in anim)
                        if (st.clip != null) result.Add(st.clip);
                    if (anim.clip != null) result.Add(anim.clip);
                }
                catch { /* best effort */ }
            }

            // Animator components not covered by virtualization / 未被虚拟化覆盖的 Animator
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var ctrl = animator.runtimeAnimatorController;
                if (ctrl == null) continue;
                foreach (var clip in ctrl.animationClips) if (clip != null) result.Add(clip);
            }

            // VRC descriptor layers / VRC descriptor 层
            try
            {
                var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    foreach (var layer in descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers))
                    {
                        var ctrl = layer.animatorController;
                        if (ctrl == null) continue;
                        foreach (var clip in ctrl.animationClips) if (clip != null) result.Add(clip);
                    }
                }
            }
            catch (Exception e)
            {
                ATOLog.Verbose("descriptor layer scan failed: " + e.Message);
            }
            return result;
        }

        /// <summary>
        /// Read-only scan of a raw clip (used for discovery fallback; curves are never edited here).
        /// 对原始 clip 的只读扫描（仅用于来源发现；永不编辑曲线）。
        /// </summary>
        private static void ScanClipReadOnly(AnimationClip clip, Transform root, ATOAnimationData data)
        {
            if (clip == null) return;
            try
            {
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve != null) ProcessFloatBinding(b, curve, root, data);
                }
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys != null) ProcessPPtrBinding(null, b, keys, data);
                }
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"direct clip scan failed {clip.name}: {e.Message}");
            }
        }
    }
}

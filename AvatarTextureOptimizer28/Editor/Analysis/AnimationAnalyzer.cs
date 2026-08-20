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
    /// <summary>
    /// EN: Everything the rest of the pipeline needs to know about what animation can do at runtime.
    /// ZH: 流水线其余部分需要了解的"动画在运行时能做什么"的全部信息。
    /// </summary>
    public sealed class AnimationFacts
    {
        /// <summary>EN: Material property names written by any clip, per object path. ZH: 按对象路径记录、被任意片段写入的材质属性名。</summary>
        public readonly Dictionary<string, HashSet<string>> AnimatedMaterialProps =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>EN: Union of all animated material property names, used as a conservative fallback.
        /// ZH: 所有被动画写入的材质属性名的并集，作为保守回退使用。</summary>
        public readonly HashSet<string> AllAnimatedMaterialProps = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>EN: Materials that animation can assign, keyed by "path#slotIndex". ZH: 动画可能赋值的材质，键为 "路径#槽索引"。</summary>
        public readonly Dictionary<string, HashSet<Material>> AnimatedMaterials =
            new Dictionary<string, HashSet<Material>>(StringComparer.Ordinal);

        /// <summary>EN: Textures that animation can assign directly to a material property. ZH: 动画可直接赋给材质属性的贴图。</summary>
        public readonly HashSet<Texture2D> AnimatedTextures = new HashSet<Texture2D>();

        /// <summary>EN: Object paths that any clip can enable. ZH: 任意片段可以启用的对象路径。</summary>
        public readonly HashSet<string> PathsAnimationCanEnable = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>EN: Maximum absolute local scale animation can apply, per object path. ZH: 按对象路径记录、动画可施加的最大绝对局部缩放。</summary>
        public readonly Dictionary<string, Vector3> MaxAnimatedScale = new Dictionary<string, Vector3>(StringComparer.Ordinal);

        /// <summary>EN: Object paths whose material slot array is driven individually by animation.
        /// ZH: 材质槽数组被动画单独驱动的对象路径。</summary>
        public readonly Dictionary<string, HashSet<int>> IndividuallyAnimatedSlots =
            new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        /// <summary>EN: Lookup animated property names for a path, falling back to the global union.
        /// ZH: 按路径查询被动画写入的属性名，回退到全局并集。</summary>
        public IReadOnlyCollection<string> PropsFor(string path) =>
            AnimatedMaterialProps.TryGetValue(path, out var s) ? (IReadOnlyCollection<string>)s : Array.Empty<string>();
    }

    /// <summary>
    /// EN: Reads every animator controller reachable from the avatar through NDMF's AnimationIndex and
    ///     derives the facts above. Object curves are the source of animated material / texture swaps;
    ///     float curves give us animated ST, cutoff, render mode, enable state and scale.
    /// ZH: 通过 NDMF 的 AnimationIndex 读取 Avatar 上可达的所有动画控制器并推导出上述事实。
    ///     对象曲线是动画切换材质/贴图的来源；浮点曲线给出被动画驱动的 ST、Cutoff、渲染模式、启用状态与缩放。
    /// </summary>
    public static class AnimationAnalyzer
    {
        private const string MaterialsPrefix = "m_Materials.Array.data[";

        /// <summary>EN: Analyse all clips in the build context. ZH: 分析构建上下文中的所有片段。</summary>
        public static AnimationFacts Analyze(BuildContext ctx, ATOLog log)
        {
            var facts = new AnimationFacts();
            var asc = ctx.Extension<AnimatorServicesContext>();
            int clipCount = 0;

            foreach (var controller in asc.ControllerContext.GetAllControllers())
            foreach (var clip in EnumerateClips(controller))
            {
                clipCount++;

                foreach (var b in clip.GetObjectCurveBindings())
                {
                    var curve = clip.GetObjectCurve(b);
                    if (curve == null) continue;

                    if (b.propertyName.StartsWith(MaterialsPrefix, StringComparison.Ordinal))
                    {
                        var idx = ParseSlotIndex(b.propertyName);
                        var key = b.path + "#" + idx;
                        if (!facts.AnimatedMaterials.TryGetValue(key, out var set))
                            facts.AnimatedMaterials[key] = set = new HashSet<Material>();
                        foreach (var kf in curve) if (kf.value is Material m) set.Add(m);

                        if (!facts.IndividuallyAnimatedSlots.TryGetValue(b.path, out var slots))
                            facts.IndividuallyAnimatedSlots[b.path] = slots = new HashSet<int>();
                        slots.Add(idx);
                    }
                    else
                    {
                        foreach (var kf in curve) if (kf.value is Texture2D t) facts.AnimatedTextures.Add(t);
                    }
                }

                foreach (var b in clip.GetFloatCurveBindings())
                {
                    var prop = b.propertyName;

                    if (prop.StartsWith("material.", StringComparison.Ordinal))
                    {
                        var name = prop.Substring("material.".Length);
                        Add(facts.AnimatedMaterialProps, b.path, name);
                        facts.AllAnimatedMaterialProps.Add(name);
                        var dot = name.LastIndexOf('.');
                        if (dot > 0)
                        {
                            var baseName = name.Substring(0, dot);
                            Add(facts.AnimatedMaterialProps, b.path, baseName);
                            facts.AllAnimatedMaterialProps.Add(baseName);
                        }
                        continue;
                    }

                    if (prop == "m_IsActive" || prop == "m_Enabled")
                    {
                        var curve = clip.GetFloatCurve(b);
                        if (curve != null && curve.keys.Any(k => k.value > 0.5f))
                            facts.PathsAnimationCanEnable.Add(b.path);
                        continue;
                    }

                    if (prop.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                    {
                        var curve = clip.GetFloatCurve(b);
                        if (curve == null || curve.length == 0) continue;
                        var max = curve.keys.Max(k => Mathf.Abs(k.value));
                        facts.MaxAnimatedScale.TryGetValue(b.path, out var v);
                        switch (prop[prop.Length - 1])
                        {
                            case 'x': v.x = Mathf.Max(v.x, max); break;
                            case 'y': v.y = Mathf.Max(v.y, max); break;
                            case 'z': v.z = Mathf.Max(v.z, max); break;
                        }
                        facts.MaxAnimatedScale[b.path] = v;
                    }
                }
            }

            log.Verbose($"Animation analysis: {clipCount} clips, {facts.AnimatedTextures.Count} animated textures, " +
                        $"{facts.AnimatedMaterials.Count} animated material slots, " +
                        $"{facts.AllAnimatedMaterialProps.Count} animated material properties");
            return facts;
        }

        private static IEnumerable<VirtualClip> EnumerateClips(VirtualAnimatorController controller)
        {
            // EN: AllReachableNodes walks the whole virtual graph (layers, state machines, blend trees).
            // ZH: AllReachableNodes 会遍历整个虚拟图（层、状态机、混合树）。
            return controller.AllReachableNodes().OfType<VirtualClip>();
        }

        private static int ParseSlotIndex(string prop)
        {
            var open = prop.IndexOf('[');
            var close = prop.IndexOf(']', open + 1);
            if (open < 0 || close < 0) return 0;
            return int.TryParse(prop.Substring(open + 1, close - open - 1), out var i) ? i : 0;
        }

        private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(value);
        }
    }
}

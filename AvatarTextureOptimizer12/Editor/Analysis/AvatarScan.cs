// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Avatar-wide scan: renderers, animations, and the UV<->texture graph.
// AvatarTextureOptimizer (ATO) - 全 Avatar 扫描：渲染器、动画，以及 UV<->贴图 关系图。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.runtime;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Identifies one mesh UV stream: a renderer's sub-mesh sampled through one UV channel.
    ///     Multi-channel UVs are split out and treated as fully independent UVs, per spec.
    /// ZH: 标识一条网格 UV 流：某个渲染器的某个子网格通过某个 UV 通道采样。
    ///     按需求，多通道 UV 会被拆出来当作完全独立的 UV 使用。
    /// </summary>
    public readonly struct UVSlotKey : IEquatable<UVSlotKey>
    {
        public readonly Renderer Renderer;
        public readonly int SubMesh;
        public readonly int UvChannel;

        public UVSlotKey(Renderer renderer, int subMesh, int uvChannel)
        {
            Renderer = renderer;
            SubMesh = subMesh;
            UvChannel = uvChannel;
        }

        public bool Equals(UVSlotKey other) =>
            ReferenceEquals(Renderer, other.Renderer) && SubMesh == other.SubMesh && UvChannel == other.UvChannel;

        public override bool Equals(object obj) => obj is UVSlotKey k && Equals(k);

        public override int GetHashCode() =>
            (Renderer != null ? Renderer.GetInstanceID() : 0) * 397 ^ (SubMesh * 31 + UvChannel);

        public override string ToString() =>
            $"{(Renderer != null ? Renderer.name : "<null>")}#{SubMesh}.uv{UvChannel}";
    }

    /// <summary>
    /// EN: Everything ATO knows about one source texture.
    /// ZH: ATO 关于某张源贴图所知的全部信息。
    /// </summary>
    public sealed class TextureUsage
    {
        public Texture2D Texture;

        /// <summary>EN: Whitelisted or rejected: skip every optimisation. ZH: 白名单或被拒绝：跳过全部优化。</summary>
        public bool Excluded;

        public SlotRejectReason Reject = SlotRejectReason.None;

        /// <summary>EN: Referenced as a normal map by at least one material. ZH: 至少被一个材质当作法线贴图引用。</summary>
        public bool IsNormalMap;

        /// <summary>EN: Referenced as the shader's main texture by at least one material. ZH: 至少被一个材质当作主贴图引用。</summary>
        public bool IsMainTexture;

        /// <summary>EN: sRGB sampling. ZH: 是否 sRGB 采样。</summary>
        public bool SRGB;

        /// <summary>EN: UV streams that sample this texture. ZH: 采样该贴图的 UV 流集合。</summary>
        public readonly HashSet<UVSlotKey> UvSlots = new HashSet<UVSlotKey>();

        /// <summary>EN: Materials referencing it, with the property name used. ZH: 引用它的材质及所用属性名。</summary>
        public readonly List<MaterialTextureSlot> Slots = new List<MaterialTextureSlot>();

        /// <summary>
        /// EN: Strictest alpha requirement across every referencing material (Cutout beats Blend beats Opaque
        ///     because a clipped silhouette is the least forgiving).
        /// ZH: 所有引用材质中最严苛的 alpha 要求（Cutout &gt; Blend &gt; Opaque，因为裁剪轮廓最不容错）。
        /// </summary>
        public ATOAlphaMode AlphaMode = ATOAlphaMode.Opaque;

        /// <summary>EN: All cutoff values the texture is ever clipped against. ZH: 该贴图会被裁剪时用到的全部 Cutoff 值。</summary>
        public readonly HashSet<float> Cutoffs = new HashSet<float>();

        public TextureContentInfo Content;

        public ATOTextureClass Class = ATOTextureClass.OpaqueColor;

        /// <summary>EN: Assigned during atlas planning. ZH: 图集规划阶段分配。</summary>
        public int UvGroupId = -1;
        public string TypeGroupKey;

        public override string ToString() => $"{Texture?.name} [{Class}{(Excluded ? ", excluded:" + Reject : "")}]";
    }

    /// <summary>
    /// EN: One renderer that participates in the optimisation.
    /// ZH: 参与优化的一个渲染器。
    /// </summary>
    public sealed class RendererEntry
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public string Path;
        public int UvChannelCount;
        public float MaxAnimatedScale = 1f;

        /// <summary>EN: Every material that can ever be bound to each slot (static + animated).
        ///     ZH: 每个材质槽上可能出现的全部材质（静态 + 动画切换）。</summary>
        public List<HashSet<Material>> SlotMaterials = new List<HashSet<Material>>();
    }

    /// <summary>
    /// EN: Facts extracted from the avatar's animators.
    /// ZH: 从 Avatar 的动画控制器中提取的事实。
    /// </summary>
    public sealed class AnimationFacts
    {
        /// <summary>EN: path -&gt; material slot index -&gt; possible materials. ZH: 路径 -&gt; 材质槽索引 -&gt; 可能的材质。</summary>
        public readonly Dictionary<string, Dictionary<int, HashSet<Material>>> MaterialSwaps =
            new Dictionary<string, Dictionary<int, HashSet<Material>>>();

        /// <summary>EN: path -&gt; shader texture property -&gt; possible textures. ZH: 路径 -&gt; 着色器贴图属性 -&gt; 可能的贴图。</summary>
        public readonly Dictionary<string, Dictionary<string, HashSet<Texture>>> TextureSwaps =
            new Dictionary<string, Dictionary<string, HashSet<Texture>>>();

        /// <summary>EN: path -&gt; set of animated material float properties. ZH: 路径 -&gt; 被动画修改的材质 float 属性集合。</summary>
        public readonly Dictionary<string, HashSet<string>> AnimatedMaterialFloats =
            new Dictionary<string, HashSet<string>>();

        /// <summary>EN: path -&gt; all values an animated _Cutoff takes. ZH: 路径 -&gt; 动画中 _Cutoff 取到的全部值。</summary>
        public readonly Dictionary<string, HashSet<float>> AnimatedCutoffs =
            new Dictionary<string, HashSet<float>>();

        /// <summary>EN: paths that some animation turns on. ZH: 会被某个动画启用的路径。</summary>
        public readonly HashSet<string> PathsEnabledByAnimation = new HashSet<string>();

        /// <summary>EN: paths whose renderer component is enabled by animation. ZH: 渲染器组件被动画启用的路径。</summary>
        public readonly HashSet<string> RenderersEnabledByAnimation = new HashSet<string>();

        /// <summary>EN: path -&gt; maximum absolute animated local scale. ZH: 路径 -&gt; 动画中的最大绝对局部缩放。</summary>
        public readonly Dictionary<string, float> MaxAnimatedScale = new Dictionary<string, float>();

        /// <summary>EN: Every material slot whose material list is animated at all. ZH: 材质列表被动画修改过的全部材质槽。</summary>
        public readonly HashSet<(string path, int slot)> AnimatedMaterialSlots = new HashSet<(string, int)>();
    }

    public static class AvatarScan
    {
        // ---- Animation scanning / 动画扫描 -------------------------------------------------------

        /// <summary>
        /// EN: Walk every clip reachable from the avatar's animators (through NDMF's virtual animator layer,
        ///     which already accounts for Modular Avatar's merges because we run after MA).
        /// ZH: 遍历 Avatar 动画控制器中可达的全部动画剪辑（经由 NDMF 的虚拟动画层；
        ///     因为我们在 MA 之后运行，所以 MA 的合并结果已经包含在内）。
        /// </summary>
        public static AnimationFacts ScanAnimations(BuildContext ctx)
        {
            var facts = new AnimationFacts();
            AnimatorServicesContext asc;
            try
            {
                asc = ctx.Extension<AnimatorServicesContext>();
            }
            catch (Exception)
            {
                ATOLog.Warn("AnimatorServicesContext unavailable; animation analysis skipped");
                return facts;
            }

            int clipCount = 0;
            var visited = new HashSet<VirtualClip>();
            foreach (var controller in asc.ControllerContext.GetAllControllers())
            {
                foreach (var node in controller.AllReachableNodes())
                {
                    if (!(node is VirtualClip clip)) continue;
                    if (!visited.Add(clip)) continue;
                    clipCount++;
                    ScanClip(clip, facts);
                }
            }

            ATOLog.Debug_($"animation scan: {clipCount} clip(s), " +
                          $"{facts.MaterialSwaps.Count} path(s) with material swaps, " +
                          $"{facts.TextureSwaps.Count} path(s) with texture swaps");
            return facts;
        }

        private static void ScanClip(VirtualClip clip, AnimationFacts facts)
        {
            foreach (var binding in clip.GetObjectCurveBindings())
            {
                var curve = clip.GetObjectCurve(binding);
                if (curve == null) continue;

                // ---- Material slot swaps / 材质槽切换 ----
                if (binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                {
                    int slot = ParseArrayIndex(binding.propertyName);
                    if (slot < 0) continue;
                    facts.AnimatedMaterialSlots.Add((binding.path, slot));

                    if (!facts.MaterialSwaps.TryGetValue(binding.path, out var bySlot))
                        facts.MaterialSwaps[binding.path] = bySlot = new Dictionary<int, HashSet<Material>>();
                    if (!bySlot.TryGetValue(slot, out var mats))
                        bySlot[slot] = mats = new HashSet<Material>();

                    foreach (var kf in curve)
                    {
                        if (kf.value is Material m && m != null) mats.Add(m);
                    }
                    continue;
                }

                // ---- Texture property swaps / 贴图属性切换 ----
                if (binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                {
                    var prop = binding.propertyName.Substring("material.".Length);
                    if (!facts.TextureSwaps.TryGetValue(binding.path, out var byProp))
                        facts.TextureSwaps[binding.path] = byProp = new Dictionary<string, HashSet<Texture>>();
                    if (!byProp.TryGetValue(prop, out var texes))
                        byProp[prop] = texes = new HashSet<Texture>();

                    foreach (var kf in curve)
                    {
                        if (kf.value is Texture t && t != null) texes.Add(t);
                    }
                }
            }

            foreach (var binding in clip.GetFloatCurveBindings())
            {
                var curve = clip.GetFloatCurve(binding);
                if (curve == null) continue;
                var prop = binding.propertyName;

                // ---- Material float properties / 材质 float 属性 ----
                if (prop.StartsWith("material.", StringComparison.Ordinal))
                {
                    var name = prop.Substring("material.".Length);
                    if (!facts.AnimatedMaterialFloats.TryGetValue(binding.path, out var set))
                        facts.AnimatedMaterialFloats[binding.path] = set = new HashSet<string>();

                    // EN: "material._MainTex_ST.x" -> record both the dotted and the base name.
                    // ZH: "material._MainTex_ST.x" -> 同时记录带点与去点的名字。
                    set.Add(name);
                    int dot = name.LastIndexOf('.');
                    if (dot > 0) set.Add(name.Substring(0, dot));

                    if (name.StartsWith("_Cutoff", StringComparison.Ordinal) ||
                        name.Contains("Cutoff") || name.Contains("AlphaClip"))
                    {
                        if (!facts.AnimatedCutoffs.TryGetValue(binding.path, out var cuts))
                            facts.AnimatedCutoffs[binding.path] = cuts = new HashSet<float>();
                        foreach (var kf in curve.keys) cuts.Add(kf.value);
                    }
                    continue;
                }

                // ---- GameObject / renderer enable state / 物体与渲染器启用状态 ----
                if (prop == "m_IsActive")
                {
                    foreach (var kf in curve.keys)
                    {
                        if (kf.value > 0.5f) { facts.PathsEnabledByAnimation.Add(binding.path); break; }
                    }
                    continue;
                }
                if (prop == "m_Enabled")
                {
                    foreach (var kf in curve.keys)
                    {
                        if (kf.value > 0.5f) { facts.RenderersEnabledByAnimation.Add(binding.path); break; }
                    }
                    continue;
                }

                // ---- Animated scale / 动画缩放 ----
                if (prop.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    float max = 0f;
                    foreach (var kf in curve.keys) max = Mathf.Max(max, Mathf.Abs(kf.value));
                    if (max <= 0f) continue;
                    facts.MaxAnimatedScale.TryGetValue(binding.path, out var prev);
                    facts.MaxAnimatedScale[binding.path] = Mathf.Max(prev, max);
                }
            }
        }

        private static int ParseArrayIndex(string propertyName)
        {
            int open = propertyName.IndexOf('[');
            int close = propertyName.IndexOf(']', open + 1);
            if (open < 0 || close < 0) return -1;
            return int.TryParse(propertyName.Substring(open + 1, close - open - 1), out var i) ? i : -1;
        }

        // ---- Renderer collection / 渲染器收集 ----------------------------------------------------

        /// <summary>
        /// EN: Collect the renderers we are allowed to touch: enabled (or animated-enabled) Skinned/Mesh
        ///     renderers that are not under an EditorOnly object.
        /// ZH: 收集允许处理的渲染器：已启用（或被动画启用）且不在 EditorOnly 物体之下的
        ///     SkinnedMeshRenderer / MeshRenderer。
        /// </summary>
        public static List<RendererEntry> CollectRenderers(BuildContext ctx, AnimationFacts facts)
        {
            var result = new List<RendererEntry>();
            var root = ctx.AvatarRootObject;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsUnderEditorOnly(renderer.transform, root.transform)) continue;

                var path = RuntimeUtil.RelativePath(root, renderer.gameObject) ?? "";

                bool activeNow = renderer.gameObject.activeInHierarchy && renderer.enabled;
                bool activeByAnim = facts.PathsEnabledByAnimation.Contains(path) ||
                                    facts.RenderersEnabledByAnimation.Contains(path) ||
                                    AnyAncestorEnabledByAnimation(renderer.transform, root.transform, facts);
                if (!activeNow && !activeByAnim)
                {
                    ATOLog.Trace($"skipping never-enabled renderer '{path}'");
                    continue;
                }

                var mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : GetFilterMesh(renderer);
                if (mesh == null) continue;

                var entry = new RendererEntry
                {
                    Renderer = renderer,
                    Mesh = mesh,
                    Path = path,
                    UvChannelCount = CountUvChannels(mesh),
                    MaxAnimatedScale = MaxScaleForChain(renderer.transform, root.transform, facts),
                };

                var shared = renderer.sharedMaterials;
                for (int slot = 0; slot < shared.Length; slot++)
                {
                    var set = new HashSet<Material>();
                    if (shared[slot] != null) set.Add(shared[slot]);
                    if (facts.MaterialSwaps.TryGetValue(path, out var bySlot) &&
                        bySlot.TryGetValue(slot, out var animated))
                    {
                        foreach (var m in animated) set.Add(m);
                    }
                    entry.SlotMaterials.Add(set);
                }

                result.Add(entry);
            }

            ATOLog.Debug_($"collected {result.Count} renderer(s)");
            return result;
        }

        private static Mesh GetFilterMesh(Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static bool AnyAncestorEnabledByAnimation(Transform t, Transform root, AnimationFacts facts)
        {
            var cur = t;
            while (cur != null)
            {
                var p = RuntimeUtil.RelativePath(root, cur);
                if (p != null && facts.PathsEnabledByAnimation.Contains(p)) return true;
                if (cur == root) break;
                cur = cur.parent;
            }
            return false;
        }

        private static float MaxScaleForChain(Transform t, Transform root, AnimationFacts facts)
        {
            float result = 1f;
            var cur = t;
            while (cur != null)
            {
                var p = RuntimeUtil.RelativePath(root, cur);
                if (p != null && facts.MaxAnimatedScale.TryGetValue(p, out var s))
                {
                    var rest = cur.localScale;
                    float restMax = Mathf.Max(Mathf.Abs(rest.x), Mathf.Max(Mathf.Abs(rest.y), Mathf.Abs(rest.z)));
                    if (restMax > 1e-6f) result *= Mathf.Max(1f, s / restMax);
                }
                if (cur == root) break;
                cur = cur.parent;
            }
            return result;
        }

        private static bool IsUnderEditorOnly(Transform t, Transform root)
        {
            var cur = t;
            while (cur != null)
            {
                if (cur.CompareTag("EditorOnly")) return true;
                if (cur == root) break;
                cur = cur.parent;
            }
            return false;
        }

        private static int CountUvChannels(Mesh mesh)
        {
            int count = 0;
            var tmp = new List<Vector2>();
            for (int i = 0; i < 8; i++)
            {
                mesh.GetUVs(i, tmp);
                if (tmp.Count > 0) count = i + 1;
            }
            return Mathf.Max(1, count);
        }

        // ---- Whitelist / 白名单 -------------------------------------------------------------------

        /// <summary>
        /// EN: Expand the user's whitelist into the concrete set of textures it protects. The whitelist
        ///     accepts any object type; we walk the dependency graph so that whitelisting a mesh, renderer,
        ///     GameObject, material or animation clip protects every texture reachable from it.
        /// ZH: 把用户白名单展开为它所保护的具体贴图集合。白名单接受任意对象类型；
        ///     我们会遍历依赖关系图，使白名单中的网格、渲染器、物体、材质或动画所引用的全部贴图都被保护。
        /// </summary>
        public static HashSet<Texture2D> ExpandWhitelist(IEnumerable<UnityEngine.Object> whitelist,
            List<RendererEntry> renderers)
        {
            var textures = new HashSet<Texture2D>();
            if (whitelist == null) return textures;

            foreach (var obj in whitelist)
            {
                if (obj == null) continue;
                CollectTexturesFrom(obj, textures, renderers, 0);
            }

            ATOLog.Debug_($"whitelist protects {textures.Count} texture(s)");
            return textures;
        }

        private static void CollectTexturesFrom(UnityEngine.Object obj, HashSet<Texture2D> into,
            List<RendererEntry> renderers, int depth)
        {
            if (obj == null || depth > 6) return;

            switch (obj)
            {
                case Texture2D tex:
                    into.Add(tex);
                    return;

                case Material mat:
                    AddMaterialTextures(mat, into);
                    return;

                case Mesh mesh:
                    foreach (var r in renderers)
                    {
                        if (r.Mesh != mesh) continue;
                        foreach (var set in r.SlotMaterials)
                        foreach (var m in set)
                            AddMaterialTextures(m, into);
                    }
                    return;

                case Renderer renderer:
                    foreach (var m in renderer.sharedMaterials) AddMaterialTextures(m, into);
                    return;

                case GameObject go:
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        AddMaterialTextures(m, into);
                    return;

                case Component comp:
                    CollectTexturesFrom(comp.gameObject, into, renderers, depth + 1);
                    return;

                case AnimationClip clip:
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                        {
                            if (kf.value is Texture2D t) into.Add(t);
                            else if (kf.value is Material m) AddMaterialTextures(m, into);
                        }
                    }
                    return;

                default:
                {
                    // EN: Generic fallback - walk serialized object references one level deep.
                    // ZH: 通用兜底——向下遍历一层序列化对象引用。
                    try
                    {
                        var so = new SerializedObject(obj);
                        var it = so.GetIterator();
                        while (it.NextVisible(true))
                        {
                            if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                            var v = it.objectReferenceValue;
                            if (v != null && v != obj) CollectTexturesFrom(v, into, renderers, depth + 1);
                        }
                    }
                    catch (Exception)
                    {
                        // EN: Not all objects can be inspected; ignore. ZH: 并非所有对象都能被内省，忽略。
                    }
                    return;
                }
            }
        }

        private static void AddMaterialTextures(Material mat, HashSet<Texture2D> into)
        {
            if (mat == null || mat.shader == null) return;
            int count = mat.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (mat.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                if (mat.GetTexture(mat.shader.GetPropertyName(i)) is Texture2D t) into.Add(t);
            }
        }
    }
}

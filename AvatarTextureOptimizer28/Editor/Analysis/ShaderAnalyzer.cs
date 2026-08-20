using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: The verdict for one texture property of one material.
    /// ZH: 对某个材质的某个贴图属性的判定结果。
    /// </summary>
    public sealed class TexturePropertyVerdict
    {
        /// <summary>EN: Shader property name. ZH: 着色器属性名。</summary>
        public string Property;
        /// <summary>EN: Assigned texture, may be null. ZH: 已赋值的贴图，可能为 null。</summary>
        public Texture2D Texture;
        /// <summary>EN: True when the property is safe to optimise. ZH: 该属性是否可安全优化。</summary>
        public bool Safe;
        /// <summary>EN: Reason the property is unsafe, for the warning log. ZH: 不安全的原因，用于警告日志。</summary>
        public string UnsafeReason;
        /// <summary>EN: UV channel the shader samples with. ZH: 着色器采样所用的 UV 通道。</summary>
        public int UvChannel;
        /// <summary>EN: Normalised slot. ZH: 归一化槽位。</summary>
        public TextureSlot Slot;
        /// <summary>EN: True when Unity marks the property [NoScaleOffset], meaning no _ST exists at all.
        /// ZH: Unity 是否将该属性标记为 [NoScaleOffset]，即完全不存在 _ST。</summary>
        public bool NoScaleOffset;
    }

    /// <summary>
    /// EN: The verdict for a whole material.
    /// ZH: 对整个材质的判定结果。
    /// </summary>
    public sealed class MaterialVerdict
    {
        /// <summary>EN: The analysed material. ZH: 被分析的材质。</summary>
        public Material Material;
        /// <summary>EN: Per-property verdicts, keyed by property name. ZH: 按属性名索引的逐属性判定。</summary>
        public readonly Dictionary<string, TexturePropertyVerdict> Properties =
            new Dictionary<string, TexturePropertyVerdict>(StringComparer.Ordinal);
        /// <summary>EN: How the material treats alpha. ZH: 材质对 alpha 的处理方式。</summary>
        public AlphaMode AlphaMode;
        /// <summary>EN: Alpha cutoff, meaningful for <see cref="AlphaMode.Cutout"/>. ZH: alpha 阈值，Cutout 时有意义。</summary>
        public float Cutoff = 0.5f;
        /// <summary>EN: True when the shader itself could not be analysed and everything must be skipped.
        /// ZH: 着色器本身无法被分析、必须整体跳过时为 true。</summary>
        public bool ShaderUnanalysable;
    }

    /// <summary>
    /// EN: Static analysis of shaders and materials. The analyser is intentionally conservative:
    ///     anything it cannot prove safe is reported as unsafe, and the caller treats it as whitelisted.
    ///
    ///     It understands three families of "this texture is not a plain UV lookup" signals:
    ///       1. A non-identity <c>_ST</c> (scale / offset), including values written by animation.
    ///       2. lilToon's <c>&lt;Tex&gt;_ScrollRotate</c> vector, which animates UVs inside the shader.
    ///       3. lilToon's <c>&lt;Tex&gt;_UVMode</c> integer, which reroutes the property to UV1..UV3 or to a
    ///          procedural coordinate (MatCap / Rim) that has no mesh UV at all.
    ///     Properties flagged <c>[NoScaleOffset]</c> have no _ST by construction and are always safe on
    ///     that axis. All three signals are read from the real shader property table via
    ///     <see cref="ShaderUtil"/>, never guessed from a name list, so future lilToon versions and any
    ///     third-party shader using the standard keywords keep working.
    ///
    /// ZH: 对着色器与材质的静态分析。分析器刻意保守：任何无法证明安全的情况都会被报为不安全，
    ///     调用方按白名单处理。
    ///
    ///     它识别三类"该贴图不是普通 UV 查表"的信号：
    ///       1. 非单位的 <c>_ST</c>（缩放 / 平移），包括由动画写入的值。
    ///       2. lilToon 的 <c>&lt;Tex&gt;_ScrollRotate</c> 向量，会在着色器内部对 UV 做动画。
    ///       3. lilToon 的 <c>&lt;Tex&gt;_UVMode</c> 整数，会把该属性改接到 UV1..UV3，
    ///          或改接到根本没有网格 UV 的程序化坐标（MatCap / Rim）。
    ///     被标记 <c>[NoScaleOffset]</c> 的属性从构造上就没有 _ST，在该维度上恒为安全。
    ///     以上三类信号全部通过 <see cref="ShaderUtil"/> 从真实的着色器属性表读取，
    ///     绝不靠名字列表猜测，因此未来的 lilToon 版本以及任何使用标准关键字的第三方着色器都能继续工作。
    /// </summary>
    public sealed class ShaderAnalyzer
    {
        private readonly ATOLog _log;
        private readonly Dictionary<Shader, ShaderInfo> _cache = new Dictionary<Shader, ShaderInfo>();

        /// <summary>EN: Construct with a logger. ZH: 使用日志器构造。</summary>
        public ShaderAnalyzer(ATOLog log) { _log = log; }

        /// <summary>
        /// EN: Cached, per-shader static facts. Built once per shader asset.
        /// ZH: 按着色器缓存的静态事实，每个着色器资产只构建一次。
        /// </summary>
        private sealed class ShaderInfo
        {
            public readonly Dictionary<string, ShaderPropertyFlags> TextureProps =
                new Dictionary<string, ShaderPropertyFlags>(StringComparer.Ordinal);
            public readonly HashSet<string> VectorProps = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> FloatProps = new HashSet<string>(StringComparer.Ordinal);
            public bool HasCutoff;
            public string CutoffProperty;
            public bool IsLilToon;
            public string ShaderName;
            public bool Unanalysable;
        }

        private ShaderInfo GetInfo(Shader shader)
        {
            if (shader == null) return new ShaderInfo { Unanalysable = true, ShaderName = "<null>" };
            if (_cache.TryGetValue(shader, out var cached)) return cached;

            var info = new ShaderInfo { ShaderName = shader.name };
            try
            {
                var n = shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    var name = shader.GetPropertyName(i);
                    var type = shader.GetPropertyType(i);
                    var flags = shader.GetPropertyFlags(i);
                    switch (type)
                    {
                        case ShaderPropertyType.Texture:
                            info.TextureProps[name] = flags;
                            break;
                        case ShaderPropertyType.Vector:
                            info.VectorProps.Add(name);
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                        case ShaderPropertyType.Int:
                            info.FloatProps.Add(name);
                            break;
                    }
                }

                // EN: Standard keyword used by Unity Standard, URP Lit, lilToon and virtually every
                //     toon shader for the alpha test threshold.
                // ZH: Unity Standard、URP Lit、lilToon 以及几乎所有卡通着色器都使用的标准 alpha 测试阈值关键字。
                foreach (var candidate in new[] { "_Cutoff", "_AlphaClipThreshold", "_Cutout", "_AlphaCutoff" })
                {
                    if (info.FloatProps.Contains(candidate))
                    {
                        info.HasCutoff = true;
                        info.CutoffProperty = candidate;
                        break;
                    }
                }

                info.IsLilToon = shader.name.StartsWith("lilToon", StringComparison.OrdinalIgnoreCase)
                                 || shader.name.Contains("/lilToon")
                                 || info.FloatProps.Contains("_lilToonVersion")
                                 || info.VectorProps.Contains("_MainTex_ScrollRotate");
            }
            catch (Exception e)
            {
                _log.Warn($"Shader property table of '{shader.name}' could not be read: {e.Message}");
                info.Unanalysable = true;
            }

            _cache[shader] = info;
            return info;
        }

        /// <summary>
        /// EN: Analyse one material. <paramref name="animatedProperties"/> holds every material property
        ///     name that any animation clip writes to on any renderer using this material; a property in
        ///     that set is treated as if it held its worst possible value.
        /// ZH: 分析单个材质。<paramref name="animatedProperties"/> 包含任何动画片段在任何使用该材质的
        ///     渲染器上写入过的所有材质属性名；集合中的属性按其最坏可能取值处理。
        /// </summary>
        public MaterialVerdict Analyze(Material mat, IReadOnlyCollection<string> animatedProperties)
        {
            var verdict = new MaterialVerdict { Material = mat };
            if (mat == null || mat.shader == null)
            {
                verdict.ShaderUnanalysable = true;
                return verdict;
            }

            var info = GetInfo(mat.shader);
            if (info.Unanalysable)
            {
                verdict.ShaderUnanalysable = true;
                return verdict;
            }

            verdict.AlphaMode = ResolveAlphaMode(mat, info, animatedProperties, out var cutoff);
            verdict.Cutoff = cutoff;

            foreach (var kv in info.TextureProps)
            {
                var prop = kv.Key;
                var flags = kv.Value;
                var v = new TexturePropertyVerdict
                {
                    Property = prop,
                    Safe = true,
                    UvChannel = 0,
                    NoScaleOffset = (flags & ShaderPropertyFlags.NoScaleOffset) != 0,
                };

                if (!mat.HasProperty(prop)) { v.Safe = false; v.UnsafeReason = "property missing"; verdict.Properties[prop] = v; continue; }

                var tex = mat.GetTexture(prop);
                v.Texture = tex as Texture2D;

                // EN: A third-party shader author knows their own sampler better than any heuristic, so
                //     a registered provider is consulted first and its verdict is final.
                // ZH: 第三方着色器作者比任何启发式都更了解自己的采样方式，
                //     因此优先咨询已注册的提供者，其判定为最终结果。
                if (api.ATOExtensionRegistry.TryDescribe(mat, prop, out var extSafe, out var extUv, out var extSlot))
                {
                    v.Safe = extSafe;
                    v.UvChannel = Mathf.Clamp(extUv, 0, 7);
                    v.Slot = extSlot;
                    if (!extSafe) v.UnsafeReason = "declared unsafe by a third-party shader support provider";
                    verdict.Properties[prop] = v;
                    continue;
                }

                // EN: Non Texture2D (cubemap, render texture, texture array) is never atlasable.
                // ZH: 非 Texture2D（立方体贴图、RenderTexture、贴图数组）永远不可图集化。
                if (tex != null && v.Texture == null)
                {
                    v.Safe = false;
                    v.UnsafeReason = $"'{prop}' is not a Texture2D ({tex.GetType().Name})";
                    verdict.Properties[prop] = v;
                    continue;
                }

                // ---- 1. Scale / offset -------------------------------------------------------------
                if (!v.NoScaleOffset)
                {
                    var stName = prop + "_ST";
                    var st = mat.GetTextureScale(prop);
                    var off = mat.GetTextureOffset(prop);
                    if (Mathf.Abs(st.x - 1f) > 1e-5f || Mathf.Abs(st.y - 1f) > 1e-5f ||
                        Mathf.Abs(off.x) > 1e-5f || Mathf.Abs(off.y) > 1e-5f)
                    {
                        v.Safe = false;
                        v.UnsafeReason = $"non-identity {stName} = ({st.x},{st.y},{off.x},{off.y})";
                    }
                    else if (animatedProperties.Contains(stName) ||
                             animatedProperties.Contains(stName + ".x") ||
                             animatedProperties.Contains(stName + ".y") ||
                             animatedProperties.Contains(stName + ".z") ||
                             animatedProperties.Contains(stName + ".w"))
                    {
                        v.Safe = false;
                        v.UnsafeReason = $"{stName} is written by an animation";
                    }
                }

                // ---- 2. lilToon style UV scroll / rotate --------------------------------------------
                var scrollName = prop + "_ScrollRotate";
                if (v.Safe && info.VectorProps.Contains(scrollName))
                {
                    var sr = mat.GetVector(scrollName);
                    if (sr.sqrMagnitude > 1e-10f)
                    {
                        v.Safe = false;
                        v.UnsafeReason = $"{scrollName} = {sr} animates UVs inside the shader";
                    }
                    else if (IsAnimated(animatedProperties, scrollName))
                    {
                        v.Safe = false;
                        v.UnsafeReason = $"{scrollName} is written by an animation";
                    }
                }

                // ---- 3. lilToon style UV channel selector -------------------------------------------
                var uvModeName = prop + "_UVMode";
                if (v.Safe && info.FloatProps.Contains(uvModeName))
                {
                    var mode = Mathf.RoundToInt(mat.GetFloat(uvModeName));
                    if (IsAnimated(animatedProperties, uvModeName))
                    {
                        v.Safe = false;
                        v.UnsafeReason = $"{uvModeName} is written by an animation";
                    }
                    else if (mode >= 0 && mode <= 3)
                    {
                        // EN: UV0..UV3 map straight onto mesh UV channels, which we fully support.
                        // ZH: UV0..UV3 直接对应网格 UV 通道，我们完全支持。
                        v.UvChannel = mode;
                    }
                    else
                    {
                        // EN: MatCap / Rim / any other procedural coordinate has no mesh UV to remap.
                        // ZH: MatCap / Rim 或其他程序化坐标没有可重映射的网格 UV。
                        v.Safe = false;
                        v.UnsafeReason = $"{uvModeName} = {mode} selects a procedural coordinate";
                    }
                }

                // ---- 4. Unity Standard's secondary UV selector ---------------------------------------
                if (v.Safe && info.FloatProps.Contains("_UVSec") && IsDetailProperty(prop))
                {
                    var sec = Mathf.RoundToInt(mat.GetFloat("_UVSec"));
                    if (IsAnimated(animatedProperties, "_UVSec")) { v.Safe = false; v.UnsafeReason = "_UVSec is animated"; }
                    else v.UvChannel = Mathf.Clamp(sec, 0, 1);
                }

                // ---- 5. Decal / parallax style deformation -------------------------------------------
                if (v.Safe && IsDeformingProperty(prop))
                {
                    v.Safe = false;
                    v.UnsafeReason = $"'{prop}' is a deforming / procedural sampler";
                }

                v.Slot = ClassifySlot(prop, flags);
                verdict.Properties[prop] = v;
            }

            return verdict;
        }

        private static bool IsAnimated(IReadOnlyCollection<string> animated, string prop)
        {
            if (animated.Contains(prop)) return true;
            foreach (var suffix in new[] { ".x", ".y", ".z", ".w", ".r", ".g", ".b", ".a" })
                if (animated.Contains(prop + suffix)) return true;
            return false;
        }

        private static bool IsDetailProperty(string prop) =>
            prop.IndexOf("Detail", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// EN: Properties whose sampler is intrinsically not a plain mesh-UV lookup. These are matched by
        ///     substring on purpose: it is a deny list, so a false positive only costs an optimisation
        ///     opportunity, never correctness.
        /// ZH: 采样方式天然不是普通网格 UV 查表的属性。这里刻意用子串匹配：
        ///     这是一个拒绝列表，误判只会损失一次优化机会，绝不会损害正确性。
        /// </summary>
        private static bool IsDeformingProperty(string prop)
        {
            string[] deny =
            {
                "MatCap", "Parallax", "Height", "Refraction", "Distort", "Screen", "Grab",
                "Cubemap", "Panorama", "Dither", "GradationTex", "GradTex", "Ramp",
                "AudioLink", "Glitter", "Fur", "Outline", "Rim", "Noise",
            };
            foreach (var d in deny)
                if (prop.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// EN: Map a shader property onto a normalised slot. The <c>[Normal]</c> flag is authoritative
        ///     for normal maps; the rest falls back to widely used naming conventions.
        /// ZH: 把着色器属性映射到归一化槽位。<c>[Normal]</c> 标记对法线贴图是权威依据；
        ///     其余情况回退到广泛使用的命名约定。
        /// </summary>
        private static TextureSlot ClassifySlot(string prop, ShaderPropertyFlags flags)
        {
            if ((flags & ShaderPropertyFlags.Normal) != 0) return TextureSlot.Normal;
            if ((flags & ShaderPropertyFlags.MainTexture) != 0) return TextureSlot.Color;

            if (prop.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                prop.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0) return TextureSlot.Normal;
            if (prop.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0) return TextureSlot.Emission;
            if (prop.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                prop.IndexOf("Metallic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                prop.IndexOf("Smoothness", StringComparison.OrdinalIgnoreCase) >= 0 ||
                prop.IndexOf("Occlusion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                prop.IndexOf("Gloss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                prop.IndexOf("Specular", StringComparison.OrdinalIgnoreCase) >= 0) return TextureSlot.Mask;
            if (prop == "_MainTex" || prop == "_BaseMap" || prop == "_BaseColorMap" ||
                prop == "_Color" || prop == "_Albedo") return TextureSlot.Color;

            return TextureSlot.Other;
        }

        /// <summary>
        /// EN: Determine how a material treats alpha. Because animations can flip the render mode or the
        ///     cutoff at runtime we take the strictest interpretation: if any animation writes the mode
        ///     we assume Cutout AND Blend must both hold, and if any animation writes the cutoff we take
        ///     the tightest cutoff in the animated range.
        /// ZH: 判定材质如何处理 alpha。由于动画可能在运行时修改渲染模式或 Cutoff，
        ///     我们取最严苛的解释：若有动画写入模式，则同时按 Cutout 与 Blend 都必须成立处理；
        ///     若有动画写入 Cutoff，则在动画取值范围内取最严苛的 Cutoff。
        /// </summary>
        private static AlphaMode ResolveAlphaMode(Material mat, ShaderInfo info,
            IReadOnlyCollection<string> animated, out float cutoff)
        {
            cutoff = 0.5f;
            if (info.HasCutoff && mat.HasProperty(info.CutoffProperty))
                cutoff = mat.GetFloat(info.CutoffProperty);

            var queue = mat.renderQueue;
            var name = info.ShaderName;

            bool nameSaysCutout = name.IndexOf("cutout", StringComparison.OrdinalIgnoreCase) >= 0;
            bool nameSaysTrans = name.IndexOf("trans", StringComparison.OrdinalIgnoreCase) >= 0
                                 || name.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0
                                 || name.IndexOf("overlay", StringComparison.OrdinalIgnoreCase) >= 0
                                 || name.IndexOf("gem", StringComparison.OrdinalIgnoreCase) >= 0;

            // EN: Unity Standard exposes _Mode: 0 Opaque, 1 Cutout, 2 Fade, 3 Transparent.
            // ZH: Unity Standard 暴露 _Mode：0 不透明、1 Cutout、2 Fade、3 Transparent。
            if (info.FloatProps.Contains("_Mode") && mat.HasProperty("_Mode"))
            {
                if (IsAnimated(animated, "_Mode")) return AlphaMode.Blend;
                var mode = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (mode == 1) return AlphaMode.Cutout;
                if (mode >= 2) return AlphaMode.Blend;
                if (mode == 0 && !nameSaysCutout && !nameSaysTrans) return AlphaMode.Opaque;
            }

            if (info.HasCutoff && IsAnimated(animated, info.CutoffProperty))
            {
                // EN: An animated cutoff means we cannot rely on a single threshold, so the alpha ramp
                //     itself must survive: Blend is the stricter of the two metrics for that.
                // ZH: Cutoff 被动画驱动意味着不能依赖单一阈值，因此必须保住整条 alpha 渐变：
                //     此时 Blend 是两种度量中更严苛的一个。
                return AlphaMode.Blend;
            }

            if (nameSaysTrans || queue >= (int)RenderQueue.Transparent) return AlphaMode.Blend;
            if (nameSaysCutout || queue >= (int)RenderQueue.AlphaTest) return AlphaMode.Cutout;
            return AlphaMode.Opaque;
        }

        /// <summary>
        /// EN: Combine two alpha treatments into the strictest one. Blend &gt; Cutout &gt; Opaque, because
        ///     Blend requires the whole ramp, Cutout only the thresholded silhouette.
        /// ZH: 把两种 alpha 处理方式合并为最严苛者。Blend &gt; Cutout &gt; Opaque，
        ///     因为 Blend 需要保住整条渐变，Cutout 只需保住阈值化后的轮廓。
        /// </summary>
        public static AlphaMode Strictest(AlphaMode a, AlphaMode b) => (AlphaMode)Mathf.Max((int)a, (int)b);
    }
}

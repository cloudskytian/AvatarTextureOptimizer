// Avatar Texture Optimizer / 头像贴图优化器
// Per-material shader analysis: family classification, texture role table,
// transform/special-use checks.
// 逐材质着色器分析：家族判定、贴图角色表、变换/特殊用途检查。
//
// Extension point: IATOShaderAnalyzer can be registered to add support for
// more shaders; see API/ATOExtensionApi.cs.
// 扩展点：可通过 IATOShaderAnalyzer 注册更多着色器支持（见 API/ATOExtensionApi.cs）。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Material transparency mode relevant to alpha quality evaluation. / 与 alpha 质量评估相关的透明模式。</summary>
    public enum ATORenderMode
    {
        Opaque = 0,
        Cutout = 1,
        Transparent = 2,
    }

    /// <summary>Result of analyzing one material. / 单材质分析结果。</summary>
    public sealed class ATOMaterialAnalysis
    {
        public Material material;
        public ATOShaderFamily family = ATOShaderFamily.Unknown;
        public ATORenderMode renderMode = ATORenderMode.Opaque;
        public float cutoff = 0.5f;
        public readonly List<ATOTextureSlot> slots = new List<ATOTextureSlot>();
        /// <summary>True when the whole material is unproven-safe (all textures whitelisted). / 整体无法证明安全。</summary>
        public bool fullyUnsupported;

        /// <summary>Iterate only optimizable slots. / 仅遍历可优化的槽。</summary>
        public IEnumerable<ATOTextureSlot> OptimizableSlots()
        {
            foreach (var s in slots)
                if (s.exclusion == ATOExcludeReason.None && s.texture != null)
                    yield return s;
        }
    }

    /// <summary>
    /// Analyzes materials based on the baked knowledge tables.
    /// 基于烘焙知识表分析材质。
    /// </summary>
    public static class ATOShaderAnalyzer
    {
        /// <summary>Analyze a material; never throws per-slot (defensive). / 分析材质（逐槽防御性，不抛异常）。</summary>
        public static ATOMaterialAnalysis Analyze(Material mat)
        {
            // Third-party analyzers first (registration order, failure-isolated).
            // 第三方分析器优先（按注册顺序，异常隔离）。
            if (mat != null && ATOExtensionApi.TryCustomAnalyze(mat, out var custom))
                return custom;

            var result = new ATOMaterialAnalysis { material = mat };
            if (mat == null || mat.shader == null)
            {
                result.fullyUnsupported = true;
                return result;
            }

            var shader = mat.shader;
            result.family = ClassifyFamily(shader, mat);
            if (result.family == ATOShaderFamily.Unknown)
            {
                // Unknown shader: cannot prove any slot safe -> full whitelist.
                // 未知着色器：无法证明任何槽安全 -> 整体白名单。
                result.fullyUnsupported = true;
            }

            result.renderMode = ResolveRenderMode(mat);
            result.cutoff = mat.HasProperty("_Cutoff") ? Mathf.Clamp01(mat.GetFloat("_Cutoff")) : 0.5f;

            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                string name = shader.GetPropertyName(i);
                var slot = new ATOTextureSlot { propertyName = name, uvChannel = 0 };
                try
                {
                    slot.texture = mat.GetTexture(name);
                }
                catch (Exception e)
                {
                    slot.exclusion = ATOExcludeReason.UnknownShader;
                    slot.note = "GetTexture failed: " + e.Message;
                }

                ApplyRules(mat, result, slot);
                result.slots.Add(slot);
            }

            if (result.fullyUnsupported)
            {
                foreach (var s in result.slots)
                {
                    s.exclusion |= ATOExcludeReason.UnknownShader;
                    s.note ??= "unknown shader / 未识别着色器";
                }
            }
            return result;
        }

        /// <summary>Classify material/shader family. / 判定材质/着色器家族。</summary>
        public static ATOShaderFamily ClassifyFamily(Shader shader, Material mat)
        {
            string sn = shader != null ? shader.name : "";
            if (!string.IsNullOrEmpty(sn))
            {
                // lilToon family: "lilToon", "Hidden/liltoon.../..." variants / lilToon 家族判定
                if (sn.StartsWith("lilToon", StringComparison.OrdinalIgnoreCase) ||
                    sn.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (mat.HasProperty("_MainTex") || mat.HasProperty("_BaseMap")) // sanity / 健全性校验
                        return ATOShaderFamily.LilToon;
                }
            }
            // Standard-keyword compatible: at least _MainTex or _BaseMap / 标准关键字兼容判定
            if (mat.HasProperty("_MainTex") || mat.HasProperty("_BaseMap") || mat.HasProperty("_BaseColorMap"))
            {
                return ATOShaderFamily.StandardLike;
            }
            return ATOShaderFamily.Unknown;
        }

        private static void ApplyRules(Material mat, ATOMaterialAnalysis result, ATOTextureSlot slot)
        {
            if (slot.texture == null) return;

            // Non Texture2D sources are never optimized. / 非 Texture2D 不优化。
            if (!(slot.texture is Texture2D))
            {
                slot.exclusion |= ATOExcludeReason.NotTexture2D;
                return;
            }

            ATOPropRule rule;
            bool known;
            if (result.family == ATOShaderFamily.LilToon) known = ATOLilToonTable.TryGet(slot.propertyName, out rule);
            else known = TryStandard(slot.propertyName, out rule);

            if (!known)
            {
                // Unknown texture property: cannot prove how it is sampled -> whitelist THIS texture only.
                // 未知贴图属性：无法证明采样方式 -> 仅白名单化该贴图。
                slot.exclusion |= ATOExcludeReason.UnknownShader;
                slot.note = "unknown texture property / 未知贴图属性";
                return;
            }

            if (!rule.meshUv)
            {
                slot.exclusion |= ATOExcludeReason.SpecialPurpose;
                return;
            }

            slot.role = rule.role;
            slot.usedChannelsMask = rule.usedChannels;
            slot.uvChannel = ResolveUvChannel(mat, slot.propertyName);

            // ST identity check (lilToon + standard both honor _XXX_ST). / ST 恒等检查。
            if (!CheckIdentityST(mat, slot.propertyName))
            {
                slot.exclusion |= ATOExcludeReason.UvTransform;
                slot.note = "non-identity ST / ST 非恒等";
            }

            if (result.family == ATOShaderFamily.LilToon)
            {
                // lilToon-specific float checks (scroll/rotate/uv mode/decal/msdf/parallax/backface shift).
                // lilToon 专属浮点检查（平移旋转/UV 模式/Decal/MSDF/视差/背面偏移）。
                foreach (var zc in ATOLilToonTable.ZeroChecks)
                {
                    if (!string.Equals(zc.affectsSlot, slot.propertyName, StringComparison.Ordinal)) continue;
                    if (!mat.HasProperty(zc.prop)) continue; // future versions may drop props / 未来版本可能删属性
                    if (zc.safeValue <= -999f)
                    {
                        // Vector zero-check. / 向量全零检查。
                        var v = mat.GetVector(zc.prop);
                        if (v != Vector4.zero)
                        {
                            slot.exclusion |= ATOExcludeReason.UvTransform;
                            slot.note = zc.prop + " != zero";
                        }
                    }
                    else
                    {
                        float f = mat.GetFloat(zc.prop);
                        if (Mathf.Abs(f - zc.safeValue) > 1e-5f)
                        {
                            slot.exclusion |= ATOExcludeReason.UvTransform;
                            slot.note = $"{zc.prop} = {f}";
                        }
                    }
                }
            }
            else
            {
                // Standard-like: reject crafted per-texture UV modifiers we know about.
                // 标准系：拒绝已知的逐贴图 UV 修改属性。
                string[] suffixes = { "_UVMode", "_UV", "_ScrollRotate", "_Pan", "_Scroll", "_Rotate" };
                foreach (var suf in suffixes)
                {
                    var p = slot.propertyName + suf;
                    if (!mat.HasProperty(p)) continue;
                    var v = mat.GetVector(p);
                    if (v != Vector4.zero)
                    {
                        slot.exclusion |= ATOExcludeReason.UvTransform;
                        slot.note = p + " != zero";
                    }
                }
            }
        }

        private static bool TryStandard(string prop, out ATOPropRule rule)
        {
            foreach (var r in ATOStandardTable.Rules)
            {
                if (string.Equals(r.name, prop, StringComparison.Ordinal))
                {
                    rule = r;
                    return true;
                }
            }
            rule = default;
            return false;
        }

        /// <summary>Determine which UV channel feeds the sampler (standard rules: UV0). / 判断采样器使用的 UV 通道（标准规则：UV0）。</summary>
        private static int ResolveUvChannel(Material mat, string prop)
        {
            // lilToon per-texture UV select properties; guarded to 0 by ZeroChecks already but rediscover here safely.
            // lilToon 逐贴图 UV 选择属性；ZeroChecks 已强制为 0，此处再安全读取一次。
            var modeProp = prop + "_UVMode";
            if (mat.HasProperty(modeProp))
            {
                int v = Mathf.RoundToInt(mat.GetFloat(modeProp));
                if (v >= 0 && v < 8) return v;
                return 0;
            }
            return 0;
        }

        /// <summary>Check XYZW ST identity for a texture slot. / 检查贴图槽 ST 是否恒等。</summary>
        private static bool CheckIdentityST(Material mat, string prop)
        {
            var st = prop + "_ST";
            if (!mat.HasProperty(st)) return true;
            var v = mat.GetVector(st);
            var ident = new Vector4(1, 1, 0, 0);
            return (v - ident).sqrMagnitude < 1e-8f;
        }

        /// <summary>Render mode from render queue (matches lilToon & standard conventions). / 由渲染队列判断透明模式。</summary>
        public static ATORenderMode ResolveRenderMode(Material mat)
        {
            int q = mat.renderQueue;
            if (q < 0) q = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            if (q >= 3000) return ATORenderMode.Transparent;
            if (q >= 2450) return ATORenderMode.Cutout;
            return ATORenderMode.Opaque;
        }
    }
}

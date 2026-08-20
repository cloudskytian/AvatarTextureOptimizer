// ============================================================================
// ATO public API - shader analysis
// ATO 公开 API - 着色器分析
//
// ATO needs to know, for a material's shader, which texture properties exist,
// which UV channel each one samples, and which alpha mode/cutoff the material
// uses. Built-in analyzers cover:
//   - "standard keyword" shaders (Unity Standard / Toon / Unlit and
//     URP/HDRP-style keyword conventions)
//   - lilToon 2.x (lilToon / lilToon Lite / ... variant shader names)
// Third parties can register custom analyzers for other shader families;
// analyzers are queried in registration order and the first successful result
// wins. Shaders no analyzer understands are treated as WHITELISTED (skipped)
// with a console warning - never optimized blindly.
// ATO 需要知道材质着色器中有哪些贴图属性、各自采样哪个 UV 通道、以及材质使用
// 的透明模式/Cutoff。内置分析器覆盖：
//   - 标准关键字着色器（Unity Standard/Toon/Unlit 与 URP/HDRP 风格关键字约定）
//   - lilToon 2.x（lilToon / lilToon Lite 等变体着色器名）
// 第三方可为其他着色器族注册自定义分析器；按注册顺序查询，第一个成功结果生效
// 。没有任何分析器能理解的着色器按白名单处理（跳过 + 控制台警告），绝不盲目
// 优化。
// ============================================================================

#region

using System;
using System.Collections.Generic;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Api
{
    /// <summary>Semantic role of a texture slot.
    /// 贴图槽语义角色。</summary>
    public enum ATOTextureRole
    {
        /// <summary>Albedo / base color. 主色。</summary>
        Albedo = 0,
        /// <summary>Normal map (tangent space, RGB expected).
        /// 法线贴图（切线空间，期望 RGB）。</summary>
        Normal = 1,
        /// <summary>Mask / detail mask / single-channel utility map.
        /// 蒙版/单通道工具贴图。</summary>
        Mask = 2,
        /// <summary>Emission / self-illumination map. 自发光。</summary>
        Emission = 3,
        /// <summary>Recognized but not safely optimizable (kept as data
        /// dependency only). 可识别但不安全优化。</summary>
        Utility = 4,
    }

    /// <summary>One texture property of a shader, as understood by an analyzer.
    /// 着色器中的一个贴图属性（分析器视角）。</summary>
    [Serializable]
    public struct ATOShaderTextureRef
    {
        /// <summary>Material property name, e.g. "_MainTex". 材质属性名。</summary>
        public string Property;
        /// <summary>Semantic role. 语义角色。</summary>
        public ATOTextureRole Role;
        /// <summary>UV channel sampled by the shader (0..7). 采样 UV 通道。</summary>
        public int UVChannel;
        /// <summary>Optional property name that selects the UV channel at
        /// runtime (int, e.g. lilToon "_Main2ndTex_UVMode"). A value that does
        /// not map to a plain UV0..3 channel marks the texture as special-use.
        /// 可选：运行时选择 UV 通道的属性名。取值无法映射到普通 UV0..3 时该
        /// 贴图视为特殊用途。</summary>
        public string UVModeProperty;
        /// <summary>Optional UV scroll/rotate animation vector property (if
        /// non-zero at any sampled time the texture is whitelisted).
        /// 可选：UV 滚动/旋转动画向量属性（任意采样时刻非零则白名单化）。</summary>
        public string ScrollRotateProperty;
        /// <summary>Optional feature-enable float property; the texture is
        /// only active when it is &gt; 0.5 (e.g. lilToon "_UseMain2ndTex").
        /// 可选：功能开关浮点属性，&gt;0.5 时贴图才生效。</summary>
        public string EnableProperty;
        /// <summary>True when the property has [NoScaleOffset] or otherwise
        /// cannot carry ST. 属性是否无 ST。</summary>
        public bool NoScaleOffset;
        /// <summary>True for special-purpose textures (gradient/dither/matcap
        /// ...). They are always whitelisted. 特殊用途贴图（渐变/抖动/MatCap
        /// 等），始终白名单。</summary>
        public bool SpecialUse;
    }

    /// <summary>Result of analyzing one shader.
    /// 单个着色器的分析结果。</summary>
    [Serializable]
    public class ATOShaderAnalysis
    {
        /// <summary>Texture property table. 贴图属性表。</summary>
        public List<ATOShaderTextureRef> Textures = new();
        /// <summary>0=opaque 1=cutout 2=blend 3=premultiply.
        /// 0=不透明 1=裁剪 2=混合 3=预乘。</summary>
        public int AlphaMode;
        /// <summary>Property name of the main alpha cutoff (e.g. "_Cutoff").
        /// 主 alpha 裁剪阈值属性名。</summary>
        public string CutoffProperty;
        /// <summary>Property name of a secondary/subpass cutoff (lilToon
        /// "_SubpassCutoff"). 次级/子通道裁剪阈值属性名。</summary>
        public string SubpassCutoffProperty;
        /// <summary>Short human-readable analyzer tag, for logs.
        /// 分析器短标签（日志用）。</summary>
        public string AnalyzerTag = "unknown";
    }

    /// <summary>Custom shader analyzer. Implementations must be deterministic
    /// and side-effect free. Return false (result unmodified) when the shader
    /// is not understood.
    /// 自定义着色器分析器。实现必须确定且无副作用。无法理解时返回 false。</summary>
    public interface IATOShaderAnalyzer
    {
        /// <summary>Short tag shown in [ATO] logs. 日志中的短标签。</summary>
        string Tag { get; }

        /// <summary>Tries to analyze the shader.
        /// 尝试分析着色器。</summary>
        /// <param name="shader">The shader instance. 着色器实例。</param>
        /// <param name="material">The material to analyze against (may be used
        /// to read runtime state like UVMode int values). 用于读取运行时状态。</param>
        /// <param name="result">Filled on success. 成功时填充。</param>
        bool TryAnalyze(Shader shader, Material material, out ATOShaderAnalysis result);
    }
}

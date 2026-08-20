// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Collections.Generic;

namespace AvatarTextureOptimizer.Editor.ShaderAnalysis
{
    /// <summary>
    /// Semantic role of a texture property. Determines which category/atlas group it
    /// belongs to and which quality metric applies.
    ///
    /// 贴图属性的语义角色。决定其所属类别/图集类型组，以及适用的质量指标。
    /// </summary>
    [Flags]
    public enum ATOTextureSemantic
    {
        None = 0,

        /// <summary>Main color / albedo (sRGB). 主色/albedo（sRGB）。</summary>
        Albedo = 1 << 0,

        /// <summary>Normal map (linear). 法线贴图（线性）。</summary>
        Normal = 1 << 1,

        /// <summary>Mask / data map (linear). 蒙版/数据贴图（线性）。</summary>
        Mask = 1 << 2,

        /// <summary>Emission / other sRGB color. 自发光或其他 sRGB 彩色贴图。</summary>
        Emission = 1 << 3,

        /// <summary>Metallic-smoothness / PBR packed (linear, treat as mask). 金属度-光滑度打包（按蒙版处理）。</summary>
        MetallicGloss = 1 << 4,

        /// <summary>MatCap (lilToon). MatCap（lilToon）。</summary>
        MatCap = 1 << 5,

        /// <summary>Other / unknown. 其他/未知。</summary>
        Other = 1 << 6,
    }

    /// <summary>
    /// Result of analyzing a single texture property on a shader.
    /// 分析着色器上单个贴图属性的结果。
    /// </summary>
    public sealed class ATOShaderTextureInfo
    {
        public string PropertyName;
        public string Description;
        public ATOTextureSemantic Semantic = ATOTextureSemantic.Other;

        /// <summary>True if the shader declares [NoScaleOffset] (no ST transform). 是否声明 [NoScaleOffset]。</summary>
        public bool NoScaleOffset;

        /// <summary>
        /// Names of sibling properties that may introduce a scale/offset/rotation transform
        /// (e.g. "_MainTex_ST", lilToon "_MainTex_ScrollRotate"). Empty ⇒ no transform possible.
        /// 可能引入平移/缩放/旋转的兄弟属性名（如 "_MainTex_ST"、lilToon "_MainTex_ScrollRotate"）。
        /// 为空 ⇒ 不存在变换可能。
        /// </summary>
        public List<string> TransformProperties = new List<string>();

        /// <summary>
        /// True if this property could not be reliably analyzed (unsafe → whitelist).
        /// 是否无法可靠分析（不安全 → 白名单）。
        /// </summary>
        public bool Unsupported;

        public string UnsupportedReason;
    }

    /// <summary>
    /// Full analysis result for one shader.
    /// 一个着色器的完整分析结果。
    /// </summary>
    public sealed class ATOShaderInfo
    {
        public UnityEngine.Shader Shader;
        public List<ATOShaderTextureInfo> Textures = new List<ATOShaderTextureInfo>();

        /// <summary>True when the shader as a whole is unsupported (→ all its textures whitelisted). 整体不支持。</summary>
        public bool Unsupported;
        public string UnsupportedReason;
    }

    /// <summary>
    /// Extension point: custom shader analyzers. Register an implementation to add support
    /// for more shaders. Analyzers run in registration order; the first that returns true wins.
    ///
    /// 扩展点：自定义着色器分析器。注册实现以支持更多着色器。按注册顺序运行，首个返回
    /// true 的生效。
    /// </summary>
    public interface IATOShaderAnalyzer
    {
        /// <summary>
        /// Attempt to analyze a shader. Return false to let the next analyzer try.
        /// 尝试分析一个着色器。返回 false 则交给下一个分析器。
        /// </summary>
        bool TryAnalyze(UnityEngine.Shader shader, ATOShaderInfo result);
    }
}

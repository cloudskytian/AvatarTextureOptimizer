// ATO — Avatar Texture Optimizer
// Extension point for custom shader analysis. Third-party tools can teach ATO how to
// classify the texture properties of a shader ATO does not recognize, by implementing
// this interface; implementations are discovered via Unity's TypeCache.
// 自定义着色器分析的扩展点。第三方工具可通过实现本接口，教会 ATO 如何分类其不识别的
// 着色器的贴图属性；实现经由 Unity 的 TypeCache 发现。

using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// A texture property descriptor returned by a custom analyzer.
    /// 自定义分析器返回的贴图属性描述。
    /// </summary>
    public struct ATOShaderTextureProperty
    {
        /// <summary>Shader property name (e.g. _MainTex). 着色器属性名。</summary>
        public string propertyName;
        /// <summary>Texture kind. 贴图类别。</summary>
        public int kind;        // net.fosa.ato.editor.ATOTextureKind value (kept as int to stay runtime-safe)
        /// <summary>UV channel 0..7; -1 = non-mesh/special. UV 通道；-1 表示非网格/特殊。</summary>
        public int uvChannel;
        /// <summary>Decal / parallax / data usage. 贴花/视差/数据用途。</summary>
        public bool specialUsage;
        /// <summary>Whether the property may carry scroll-rotate. 该属性是否可能带滚动旋转。</summary>
        public bool mayScrollRotate;
    }

    /// <summary>
    /// Custom shader analysis provider. 自定义着色器分析提供者。
    /// </summary>
    public interface IATOTextureKindProvider
    {
        /// <summary>Display name used in logs. 日志中显示的名称。</summary>
        string DisplayName { get; }

        /// <summary>
        /// Whether this provider understands the given shader. 该提供者是否理解给定的着色器。
        /// </summary>
        bool Supports(Shader shader);

        /// <summary>
        /// Enumerate the texture properties ATO should consider for this shader.
        /// 枚举 ATO 应为此着色器考虑的贴图属性。
        /// </summary>
        ATOShaderTextureProperty[] GetTextureProperties(Shader shader);
    }
}

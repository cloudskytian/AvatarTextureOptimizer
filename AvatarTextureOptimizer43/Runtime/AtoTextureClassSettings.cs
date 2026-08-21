using System;
using UnityEngine;

namespace Fosa.ATO
{
    /// <summary>
    /// Per-semantic-class output options (format, mip/streaming).
    /// 按贴图语义分类的输出选项（格式、Mip/Streaming）。
    /// </summary>
    [Serializable]
    public class AtoTextureClassSettings
    {
        [Tooltip("Safe compressed format. Invalid combos fall back at bake. 安全压缩格式，非法组合烘焙时回退。")]
        public AtoSafeFormat format = AtoSafeFormat.Auto;

        [Tooltip("Enable mipmaps AND streaming mipmaps together (VRChat requires them bound). 同时开关 Mip 与 MipStreaming（VRChat 绑定要求）。")]
        public bool mipAndStreaming = true;

        public AtoTextureClassSettings Clone()
        {
            return (AtoTextureClassSettings)MemberwiseClone();
        }
    }

    /// <summary>
    /// Four-class texture output block. 四类贴图输出参数块。
    /// </summary>
    [Serializable]
    public class AtoFormatSettings
    {
        public AtoTextureClassSettings opaque = new AtoTextureClassSettings { format = AtoSafeFormat.Auto, mipAndStreaming = true };
        public AtoTextureClassSettings transparent = new AtoTextureClassSettings { format = AtoSafeFormat.Auto, mipAndStreaming = true };
        public AtoTextureClassSettings normal = new AtoTextureClassSettings { format = AtoSafeFormat.Auto, mipAndStreaming = true };
        public AtoTextureClassSettings gray = new AtoTextureClassSettings { format = AtoSafeFormat.Auto, mipAndStreaming = true };

        public AtoTextureClassSettings ForClass(AtoTextureClass c)
        {
            switch (c)
            {
                case AtoTextureClass.Transparent: return transparent;
                case AtoTextureClass.Normal: return normal;
                case AtoTextureClass.Gray: return gray;
                default: return opaque;
            }
        }

        public AtoFormatSettings Clone()
        {
            return new AtoFormatSettings
            {
                opaque = opaque.Clone(),
                transparent = transparent.Clone(),
                normal = normal.Clone(),
                gray = gray.Clone()
            };
        }
    }
}

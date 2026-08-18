// Avatar Texture Optimizer (ATO)
// Small shared utilities. / 小型共享工具。

using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using nadena.dev.ndmf;

namespace NetFosa.ATO
{
    /// <summary>
    /// Shared utilities. / 共享工具方法。
    /// </summary>
    public static class ATOUtil
    {
        /// <summary>Map the active build target to an ATO platform. / 将当前构建目标映射为 ATO 平台。</summary>
        public static ATOPlatform GetActivePlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    return ATOPlatform.Android;
                case BuildTarget.iOS:
                    return ATOPlatform.iOS;
                default:
                    return ATOPlatform.PC; // desktop / 桌面端
            }
        }

        /// <summary>True for mobile platforms. / 是否移动平台。</summary>
        public static bool IsMobile(ATOPlatform p) => p != ATOPlatform.PC;

        /// <summary>
        /// Compute an import-settings fingerprint for a texture. Two textures are considered
        /// identical only when content AND import settings match.
        /// 计算贴图导入设置指纹。内容与导入设置都一致才视为相同。
        /// </summary>
        public static string ImportFingerprint(Texture2D t)
        {
            var path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return t.name + "|runtime";
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return t.name + "|" + t.imageContentsHash;
            var sb = new StringBuilder();
            sb.Append(t.imageContentsHash.ToString());
            sb.Append('|').Append(imp.sRGBTexture);
            sb.Append('|').Append(imp.mipmapEnabled);
            sb.Append('|').Append(imp.streamingMipmaps);
            sb.Append('|').Append(imp.wrapMode);
            sb.Append('|').Append(imp.filterMode);
            sb.Append('|').Append(imp.maxTextureSize);
            sb.Append('|').Append(imp.textureCompression);
            sb.Append('|').Append(imp.crunchedCompression);
            sb.Append('|').Append(imp.alphaIsTransparency);
            sb.Append('|').Append(imp.npotScale);
            var plat = imp.GetPlatformTextureSettings("DefaultTexturePlatform");
            sb.Append('|').Append(plat.format).Append('|').Append(plat.overridden);
            return sb.ToString();
        }

        /// <summary>
        /// Create an editable clone of a texture asset for build-time modification.
        /// The clone is always RGBA32 (SetPixels-compatible even when the source is a
        /// compressed format), stored as sRGB, so GetPixels/SetPixels and the GPU path stay
        /// consistent with the CPU reference implementation.
        /// 创建可编辑的贴图资产克隆用于构建期修改。克隆恒为 RGBA32（即使源为压缩格式也可
        /// SetPixels），按 sRGB 存储，使 GetPixels/SetPixels 与 GPU 路径和 CPU 参考实现一致。
        /// </summary>
        public static Texture2D CloneTexture(Texture2D src, bool readable = true)
        {
            var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, src.mipmapCount > 1, false);
            // Straight GPU copy preserves raw texels (decompressing if needed). / 直接 GPU 拷贝保留原始纹素（必要时解压）。
            Graphics.CopyTexture(src, copy);
            copy.name = src.name + "_ato";
            copy.wrapMode = src.wrapMode;
            copy.filterMode = src.filterMode;
            copy.anisoLevel = src.anisoLevel;
            return copy;
        }

        /// <summary>
        /// Return a readable copy of a texture (GPU readback), or the source itself if it is
        /// already readable. Used before any GetPixels/SetPixels on user textures.
        /// 返回贴图的可读副本（GPU 读回）；若已可读则直接返回源。用于对用户贴图
        /// 做 GetPixels/SetPixels 之前。
        /// </summary>
        public static Texture2D EnsureReadable(Texture2D src)
        {
            if (src == null || src.isReadable) return src;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            copy.name = src.name + "_readable";
            copy.wrapMode = src.wrapMode;
            copy.filterMode = src.filterMode;
            return copy;
        }

        /// <summary>Get pixel colors converted from sRGB to linear (alpha untouched). / 获取 sRGB 转线性后的像素（alpha 不变）。</summary>
        public static Color[] GetPixelsLinear(Texture2D t)
        {
            var c = t.GetPixels();
            for (int i = 0; i < c.Length; i++)
                c[i] = SrgbToLinear(c[i]);
            return c;
        }

        /// <summary>Convert an sRGB value to linear. / sRGB 转线性。</summary>
        public static float SrgbToLinear(float v) => Mathf.GammaToLinearSpace(v);

        public static Color SrgbToLinear(Color c) =>
            new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a);

        public static Color LinearToSrgb(Color c) =>
            new Color(Mathf.LinearToGammaSpace(c.r), Mathf.LinearToGammaSpace(c.g), Mathf.LinearToGammaSpace(c.b), c.a);

        /// <summary>Compute the transform path relative to the given root. / 计算相对给定根的变换路径。</summary>
        public static string GetRelativePath(Transform root, Transform t)
        {
            if (t == null || t == root) return t != null ? t.name : "";
            var path = t.name;
            var p = t.parent;
            while (p != null && p != root)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }

    /// <summary>
    /// A simple NDMF inline error using a localized title key. / 使用本地化标题键的简单 NDMF 错误。
    /// </summary>
    public sealed class ATOInlineError : SimpleError
    {
        private readonly nadena.dev.ndmf.localization.Localizer _localizer;
        private readonly ErrorSeverity _severity;
        private readonly string _key;

        public ATOInlineError(ErrorSeverity severity, string key)
        {
            _severity = severity;
            _key = key;
            _localizer = ATOI18n.NdmfLocalizer;
        }

        public override nadena.dev.ndmf.localization.Localizer Localizer => _localizer;
        public override string TitleKey => _key;
        public override ErrorSeverity Severity => _severity;
    }
}

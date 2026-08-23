using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>Public texture category for extension code. / 面向扩展代码的公开贴图类别。</summary>
    public enum AtoTextureCategory
    {
        Color = 0,
        Normal = 1,
        Mask = 2,
        Grayscale = 3,
        LinearColor = 4,
        /// <summary>Cannot handle — whitelist it. / 无法处理——白名单。</summary>
        Unsupported = 99,
    }

    /// <summary>Result of an external classification. / 外部分类结果。</summary>
    public struct TextureClassification
    {
        public AtoTextureCategory Category;
        /// <summary>Mesh UV channel 0..3, -1 for non-mesh UV. / 网格UV通道；-1为非网格UV。</summary>
        public int UvChannel;
        /// <summary>False ⇒ whitelist with Reason. / false 则白名单并给出原因。</summary>
        public bool Safe;
        public string Reason;
    }

    /// <summary>
    /// Extension point: classify a material's texture slot before ATO's built-in analyzer runs.
    /// Return true when your classifier owns this slot. / 扩展点：在内置分析器之前对材质贴图槽位分类。
    /// </summary>
    public interface ITextureClassifier
    {
        bool TryClassify(Material material, string property, Texture2D texture, out TextureClassification result);
    }

    /// <summary>
    /// Extension point: provide additional whitelist textures at build time (e.g. from your own
    /// components). / 扩展点：构建期提供额外的白名单贴图。
    /// </summary>
    public interface IExtraWhitelistProvider
    {
        IEnumerable<Texture2D> GetWhitelistTextures(GameObject avatarRoot);
    }

    /// <summary>
    /// Public API for advanced users and third-party developers. Register extensions from
    /// [InitializeOnLoad] code; all registrations are optional and additive.
    /// / 面向高级用户与第三方开发者的公开 API。可从 [InitializeOnLoad] 注册，全部为可选叠加项。
    /// </summary>
    public static class ATOApi
    {
        private static readonly List<ITextureClassifier> Classifiers = new List<ITextureClassifier>();
        private static readonly List<IExtraWhitelistProvider> WhitelistProviders = new List<IExtraWhitelistProvider>();

        /// <summary>Register a texture classifier (called before built-in analysis). / 注册贴图分类器。</summary>
        public static void RegisterClassifier(ITextureClassifier classifier)
        {
            if (classifier != null && !Classifiers.Contains(classifier)) Classifiers.Add(classifier);
        }

        /// <summary>Register an extra whitelist provider. / 注册额外白名单提供者。</summary>
        public static void RegisterWhitelistProvider(IExtraWhitelistProvider provider)
        {
            if (provider != null && !WhitelistProviders.Contains(provider)) WhitelistProviders.Add(provider);
        }

        /// <summary>Remove all registrations (tests/tooling). / 清空注册（测试用）。</summary>
        public static void ClearRegistrations()
        {
            Classifiers.Clear();
            WhitelistProviders.Clear();
        }

        // ------------------------------------------------------------------ internal hooks
        internal static bool TryClassifyExternal(Material mat, string prop, Texture2D tex,
            out TexCategory category, out int uvChannel, out bool safe, out string reason)
        {
            category = default;
            uvChannel = 0;
            safe = true;
            reason = null;
            foreach (var c in Classifiers)
            {
                try
                {
                    if (c.TryClassify(mat, prop, tex, out var r))
                    {
                        if (r.Category == AtoTextureCategory.Unsupported)
                        {
                            safe = false;
                            reason = string.IsNullOrEmpty(r.Reason) ? "external classifier" : r.Reason;
                            return true;
                        }
                        category = (TexCategory)(int)r.Category;
                        uvChannel = r.UvChannel;
                        safe = r.Safe && r.UvChannel >= 0;
                        reason = r.Reason;
                        return true;
                    }
                }
                catch (Exception e)
                {
                    ATOLog.Warning($"external classifier threw: {e.Message} / 外部分类器异常");
                }
            }
            return false;
        }

        internal static HashSet<Texture2D> CollectExternalWhitelist(GameObject root)
        {
            var set = new HashSet<Texture2D>();
            foreach (var p in WhitelistProviders)
            {
                try
                {
                    if (p.GetWhitelistTextures(root) == null) continue;
                    foreach (var t in p.GetWhitelistTextures(root))
                        if (t != null) set.Add(t);
                }
                catch (Exception e)
                {
                    ATOLog.Warning($"whitelist provider threw: {e.Message} / 白名单提供者异常");
                }
            }
            return set;
        }
    }
}

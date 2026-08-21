using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Public extension surface for advanced users and third-party tools.
    /// 给高级用户和第三方开发者的扩展接口。
    /// </summary>
    public static class AtoApi
    {
        public static event Action<AtoBakeContext> BeforeAnalyze;
        public static event Action<AtoBakeContext> AfterAnalyze;
        public static event Action<AtoBakeContext> BeforeApply;
        public static event Action<AtoBakeContext> AfterApply;
        /// <summary>Fired once per finished atlas. 每张成品图集回调一次。</summary>
        public static event Action<AtoBakeContext, Texture2D> AtlasCreated;

        static readonly List<IAtoShaderAnalyzer> ExtraAnalyzers = new List<IAtoShaderAnalyzer>();

        public static void RegisterShaderAnalyzer(IAtoShaderAnalyzer analyzer)
        {
            if (analyzer != null && !ExtraAnalyzers.Contains(analyzer))
                ExtraAnalyzers.Add(analyzer);
        }

        public static void UnregisterShaderAnalyzer(IAtoShaderAnalyzer analyzer)
        {
            ExtraAnalyzers.Remove(analyzer);
        }

        internal static IReadOnlyList<IAtoShaderAnalyzer> ShaderAnalyzers => ExtraAnalyzers;

        internal static void RaiseBeforeAnalyze(AtoBakeContext c) => BeforeAnalyze?.Invoke(c);
        internal static void RaiseAfterAnalyze(AtoBakeContext c) => AfterAnalyze?.Invoke(c);
        internal static void RaiseBeforeApply(AtoBakeContext c) => BeforeApply?.Invoke(c);
        internal static void RaiseAfterApply(AtoBakeContext c) => AfterApply?.Invoke(c);
    }

    /// <summary>
    /// Custom shader analyzer. Return null to decline; first non-null wins after built-in lilToon/standard.
    /// 自定义着色器分析器。返回 null 表示不处理；内置 lilToon/标准关键字之后，第一个非 null 生效。
    /// </summary>
    public interface IAtoShaderAnalyzer
    {
        AtoShaderInfo Analyze(Material material);
    }

    /// <summary>Bake-time context passed to extension events. 扩展事件可访问的烘焙上下文。</summary>
    public sealed class AtoBakeContext
    {
        public GameObject AvatarRoot;
        public AvatarTextureOptimizer Component;
        public AtoResolvedSettings Settings;
        public AtoReport Report;
        public IReadOnlyList<AtoTextureRef> TextureRefs;
        public IReadOnlyList<AtoUvGroup> UvGroups;
    }

    /// <summary>One texture slot sampled by a mesh UV channel. 一条“网格 UV → 贴图”的引用。</summary>
    public sealed class AtoTextureRef
    {
        public Texture2D Texture;
        public Material Material;
        public Renderer Renderer;
        public Mesh Mesh;
        public int MaterialSlot;
        public int UvChannel;
        public string PropertyName;
        public AtoTextureClass Class;
        public AtoAlphaMode AlphaMode;
        public float Cutoff;
        public bool Linear;
        public FilterMode Filter;
        public TextureWrapMode WrapU;
        public TextureWrapMode WrapV;
        public bool Eligible;
        public bool Whitelisted;
        public string SkipReason;
    }

    /// <summary>Textures that must share UV layout. 必须共享 UV 布局的贴图集合。</summary>
    public sealed class AtoUvGroup
    {
        public string Id;
        public readonly List<AtoTextureRef> Refs = new List<AtoTextureRef>();
    }

    /// <summary>Parsed shader slot. 解析出的着色器贴图槽。</summary>
    public sealed class AtoShaderSlot
    {
        public string PropertyName;
        public int UvChannel;          // 0-7, or -1 if not a mesh UV
        public AtoTextureClass Class;
        public bool HasST;
        public bool HasScrollRotate;
        public bool SpecialPurpose;    // decal / matcap / screen / rim ...
        public string CompanionOf;     // main property this is a companion of (normal/mask of)
        public TextureWrapMode? ForcedWrap;
    }

    public sealed class AtoShaderInfo
    {
        public bool Compatible = true;
        public string Warning;
        public AtoAlphaMode AlphaMode = AtoAlphaMode.Opaque;
        public float Cutoff = 0.5f;
        public readonly List<AtoShaderSlot> Slots = new List<AtoShaderSlot>();
        public readonly List<string> Keywords = new List<string>();
    }
}

// ATOTypes.cs - Central data model shared by every pipeline stage. / 各管线阶段共享的核心数据模型。
using System;
using System.Collections.Generic;
using Fosa.ATO.Editor.Atlas;
using UnityEngine;
using Fosa.ATO.Runtime;

namespace Fosa.ATO.Editor.Analysis
{
    /// <summary>Alpha handling of a material reference. / 材质引用的透明处理方式。</summary>
    public enum ATOAlphaMode { Opaque, Cutout, Blend }

    /// <summary>How a texture is sampled in one usage context. / 单个使用上下文中贴图的采样方式。</summary>
    public sealed class UsageContext
    {
        public Material material;          // material that references the texture / 引用该贴图的材质
        public string prop;                // shader property name / 着色器属性名
        public Renderer renderer;          // renderer (avatar space, post-MA) / 渲染器（MA处理后）
        public int slot;                   // material slot index / 材质槽索引
        public int submesh;                // submesh index (== slot normally) / 子网格索引
        public ATOTextureRole role;        // detected role / 检测到的角色
        public bool srgb;                  // expected color space / 期望色彩空间
        public ATOAlphaMode alphaMode = ATOAlphaMode.Opaque;
        public float cutoff = 0.5f;        // strictest cutoff across refs & anims / 全部引用与动画中最严的cutoff
        public bool stTransformed;         // ST/scroll/rotate/decal detected -> ineligible / 检测到ST变换->不合格
        public int uvChannel;              // mesh uv channel used / 使用的网格UV通道
    }

    /// <summary>Immutable texture identity: content hash + import settings hash. / 不可变贴图身份：内容哈希+导入设置哈希。</summary>
    public sealed class TexKey : IEquatable<TexKey>
    {
        public readonly Hash128 Content; public readonly Hash128 Import;
        public TexKey(Hash128 c, Hash128 i) { Content = c; Import = i; }
        public bool Equals(TexKey o) => o != null && Content.Equals(o.Content) && Import.Equals(o.Import);
        public override bool Equals(object o) => Equals(o as TexKey);
        public override int GetHashCode() => Content.GetHashCode() * 397 ^ Import.GetHashCode();
    }

    /// <summary>One unique texture after dedup. / 去重后的唯一贴图。</summary>
    public sealed class TexEntry
    {
        public Texture2D texture;
        public TexKey key;
        public string assetPath = "";
        public ImportSettingsUtil.Snapshot import = ImportSettingsUtil.Snapshot.Default;
        public bool whitelisted;                 // skips ALL optimization / 跳过所有优化
        public bool hasAlphaChannel;             // format-level alpha / 格式层面含alpha
        public bool usesAlpha;                   // pixel scan: any a > threshold / 像素扫描：存在有效alpha
        public readonly List<UsageContext> usages = new List<UsageContext>();
        public readonly List<Texture2D> dedupGroup = new List<Texture2D>(); // all originals mapped here / 映射到这里的全部原始贴图
        public int atlasImageId = -1;            // assigned atlas image / 被分配的图集映像
        public float wholeScale = 1f;             // whole-texture mode scale / 整图模式缩放比
        public bool Processable => !whitelisted;
        public ATOTextureRole StrictestRole;     // union of roles across usages / 全部用途的并集
        public ATOAlphaMode StrictestAlpha = ATOAlphaMode.Opaque;
        public float StrictestCutoff = 0.5f;
        public bool IsNormal => (StrictestRole & ATOTextureRole.Normal) != 0;
        /// <summary>Colors the entry by the strictest usage (worst case). / 以最严用途归类。</summary>
        public ATOTextureCategory Category()
        {
            if (IsNormal) return ATOTextureCategory.NormalMap;
            if (StrictestAlpha != ATOAlphaMode.Opaque && usesAlpha) return ATOTextureCategory.Transparent;
            if ((StrictestRole & (ATOTextureRole.Mask | ATOTextureRole.Data)) != 0 && !usesColor_) return ATOTextureCategory.Grayscale;
            return ATOTextureCategory.Opaque;
        }
        internal bool usesColor_ = true;         // set by grayscale pixel analysis / 由灰度像素分析填充
    }

    /// <summary>A (mesh asset, uv channel) pair. / （网格资产, UV通道）对。</summary>
    public struct UvKey : IEquatable<UvKey>
    {
        public Mesh mesh; public int channel;
        public UvKey(Mesh m, int ch) { mesh = m; channel = ch; }
        public bool Equals(UvKey o) => mesh == o.mesh && channel == o.channel;
        public override bool Equals(object o) => o is UvKey k && Equals(k);
        public override int GetHashCode() => (mesh != null ? mesh.GetInstanceID() : 0) * 397 ^ channel;
        public override string ToString() => $"{(mesh != null ? mesh.name : "null")}#uv{channel}";
    }

    /// <summary>All islands of one (mesh, channel); every covering texture shares these islands so island positions
    /// stay identical across every atlas image. / 一个(网格,通道)的全部岛；覆盖它的所有贴图共享这些岛，保证岛在不同图集映像上位置一致。</summary>
    public sealed class UvGroup
    {
        public UvKey key;
        public readonly List<Island> islands = new List<Island>();
        public readonly HashSet<TexEntry> textures = new HashSet<TexEntry>();
        public readonly HashSet<Renderer> renderers = new HashSet<Renderer>();
        public bool skipAtlas;                   // whitelisted texture in group -> others whole-scale only / 组内存在白名单贴图->其余仅整图缩放
        public bool Processable => !skipAtlas;
    }

    /// <summary>One connected UV island. / 一个连通UV岛。</summary>
    public sealed class Island
    {
        public int id; public UvGroup group;
        public int[] vertices;                  // unique vertex indices / 去重后的顶点索引
        public int[] triangles;                 // island triangle vertex indices / 岛内三角形顶点索引
        public Vector2 uvMin, uvMax;            // source bbox (normalized after shift) / 源包围盒（平移归一后）
        public Vector2 uvShift;                 // integer shift applied to bring into [0,1] / 归一化平移量
        public bool wrapped;                    // crosses wrap seam -> group whitelisted / 跨wrap缝->组白名单
        public float worldAreaM2;               // max over blendshape 0/100 & anim scale / 形态键与动画缩放的最大值
        public float sourceTexelDensity;        // px per meter in source texture / 源贴图中的像素密度
        // --- quality results / 质量结果 ---
        public bool pureColor;                  // constant color shortcut / 纯色短路
        public bool pureColorChecked;           // scan done / 已扫描
        public Vector2 targetScale = Vector2.one; // uniform then anisotropic / 均匀+各向异性
        public int targetW, targetH;            // px in atlas reference space / 图集参考空间像素
        // --- packing results / 装箱结果 ---
        public IslandRasterizer.Mask mask;        // undilated raster mask / 未膨胀光栅掩码
        public int maskCells;                     // set cells / 置位单元数
        public bool placed; public int atlasId = -1;
        public Rect atlasRect;                  // normalized [0,1] rect / 归一化矩形
        public bool rotated;
        public int RasterW => Mathf.Max(1, (int)(targetW * (rotated ? 0 : 1)));
    }

    /// <summary>Signature deciding which atlas queue a texture belongs to. / 决定贴图归入哪个装箱队列的签名。</summary>
    public sealed class TypeGroupKey : IEquatable<TypeGroupKey>
    {
        public ATOTextureRole auxRoles;         // union of AUX roles (Normal|Mask|Emission...) covering same UVs / 覆盖同一UV的辅助角色并集
        public bool srgb; public FilterMode filter;
        public TypeGroupKey(ATOTextureRole r, bool s, FilterMode f) { auxRoles = r; srgb = s; filter = f; }
        public bool Equals(TypeGroupKey o) => o != null && o.auxRoles == auxRoles && o.srgb == srgb && o.filter == filter;
        public override bool Equals(object o) => Equals(o as TypeGroupKey);
        public override int GetHashCode() => (int)auxRoles * 31 ^ (srgb ? 2 : 0) ^ ((int)filter << 3);
        public override string ToString() => $"{auxRoles}/{(srgb ? "sRGB" : "Linear")}/{filter}";
    }
}

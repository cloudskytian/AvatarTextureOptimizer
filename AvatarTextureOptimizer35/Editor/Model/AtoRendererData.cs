using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Per-renderer processing record. / 每个渲染器的处理记录。
    /// </summary>
    public sealed class AtoRendererData
    {
        /// <summary>The renderer (SkinnedMeshRenderer or MeshRenderer). / 渲染器（SMR 或 MR）。</summary>
        public Renderer Renderer;

        /// <summary>The original mesh. / 原始网格。</summary>
        public Mesh Mesh;

        /// <summary>Material slots. / 材质槽。</summary>
        public List<AtoMaterialSlot> Slots = new List<AtoMaterialSlot>();

        /// <summary>UV groups by channel. / 按通道索引的 UV 组。</summary>
        public Dictionary<int, AtoUvGroup> UvGroups = new Dictionary<int, AtoUvGroup>();

        /// <summary>Whether this renderer is whitelisted (skip everything). / 是否白名单（跳过一切）。</summary>
        public bool Whitelisted;

        public string WhitelistReason;

        /// <summary>The rewritten mesh (null until the mesh stage). / 重写后的网格（网格阶段前为 null）。</summary>
        public Mesh ResultMesh;

        /// <summary>Channels that will be rewritten (and may need AAO evacuation). / 将被改写的通道（可能需要 AAO 疏散）。</summary>
        public HashSet<int> RewrittenChannels = new HashSet<int>();

        /// <summary>Whether the renderer is enabled at least once (initial or via animation). / 渲染器是否至少启用一次（初始或动画）。</summary>
        public bool EffectivelyEnabled = true;

        /// <summary>Max animated world scale on this object (and its animated ancestors). / 该物体（及动画祖先）的最大动画世界缩放。</summary>
        public Vector3 MaxAnimatedScale = Vector3.one;

        /// <summary>Whether any animated ancestor scales the object. / 是否有动画祖先缩放该物体。</summary>
        public bool HasAnimatedAncestorScale;

        /// <summary>Planned AAO evacuations: original channel → saved channel (filled by the planner). /
        /// 已规划的 AAO 疏散：原通道 → saved 通道（由规划器填充）。</summary>
        public Dictionary<int, int> AaoEvacuations = new Dictionary<int, int>();

        /// <summary>Material slot index remap after slot merging: old index → kept index. /
        /// 材质槽合并后的索引重映射：旧索引 → 保留索引。</summary>
        public Dictionary<int, int> SlotMap = new Dictionary<int, int>();
    }

    /// <summary>
    /// One material slot of a renderer. / 渲染器的一个材质槽。
    /// </summary>
    public sealed class AtoMaterialSlot
    {
        /// <summary>Slot index. / 槽索引。</summary>
        public int Index;

        /// <summary>Owning renderer data. / 所属渲染器数据。</summary>
        public AtoRendererData RendererData;

        /// <summary>Initial material. / 初始材质。</summary>
        public Material Initial;

        /// <summary>All materials that can be assigned by animations (deduplicated, includes Initial). / 动画可能切换到的全部材质（去重，含初始材质）。</summary>
        public List<Material> AnimatedOptions = new List<Material>();

        /// <summary>Whether this slot is individually animated (materials or properties). / 该槽是否被动画单独控制（材质或属性）。</summary>
        public bool IndividuallyAnimated;

        /// <summary>Whether the slot participates in slot merging. / 该槽是否参与槽合并。</summary>
        public bool SlotMergeCandidate;
    }
}

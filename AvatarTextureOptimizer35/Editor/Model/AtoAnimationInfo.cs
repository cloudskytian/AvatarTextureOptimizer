using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Animation analysis results (filled by the animations stage). / 动画分析结果（由动画阶段填充）。
    /// </summary>
    internal sealed class AtoAnimationInfo
    {
        /// <summary>All animation clips reachable from animators / animation components / the VRC descriptor. /
        /// 从 Animator / Animation 组件 / VRC 描述符可达的全部动画剪辑。</summary>
        public List<AnimationClip> Clips = new List<AnimationClip>();

        /// <summary>Per (renderer, slot): all materials the animation can assign. / 每个（渲染器, 槽）：动画可能赋值的全部材质。</summary>
        public Dictionary<(Renderer renderer, int slot), List<Material>> SlotMaterialOptions =
            new Dictionary<(Renderer renderer, int slot), List<Material>>();

        /// <summary>Per (material, property): textures swapped by animation object-reference curves. / 每个（材质, 属性）：动画对象引用曲线切换的贴图。</summary>
        public Dictionary<(Material material, string property), List<Texture2D>> TextureSwaps =
            new Dictionary<(Material material, string property), List<Texture2D>>();

        /// <summary>(material, property) pairs whose ST (scale/offset) is animated. / ST（缩放/平移）被动画的（材质, 属性）对。</summary>
        public HashSet<(Material, string)> AnimatedSt = new HashSet<(Material, string)>();

        /// <summary>(material, property) pairs with ANY animated property curves (used for merge safety). / 有任意属性动画的（材质, 属性）对（用于合并安全判定）。</summary>
        public HashSet<(Material, string)> AnimatedProperties = new HashSet<(Material, string)>();

        /// <summary>(material, keyword) pairs animated by keyword curves. / 被关键字曲线动画的（材质, 关键字）对。</summary>
        public HashSet<(Material, string)> AnimatedKeywords = new HashSet<(Material, string)>();

        /// <summary>Renderers whose enabled state is animated. / 启用状态被动画的渲染器。</summary>
        public HashSet<Renderer> AnimatedEnabled = new HashSet<Renderer>();

        /// <summary>GameObjects whose active state is animated. / 激活状态被动画的游戏对象。</summary>
        public HashSet<GameObject> AnimatedActive = new HashSet<GameObject>();

        /// <summary>Per renderer: max animated local scale from direct animation curves (per axis). / 每个渲染器：直接动画曲线的最大局部缩放（逐轴）。</summary>
        public Dictionary<Renderer, Vector3> MaxDirectScale = new Dictionary<Renderer, Vector3>();

        /// <summary>GameObjects whose local scale is animated. / 局部缩放被动画的游戏对象。</summary>
        public HashSet<GameObject> AnimatedScaleObjects = new HashSet<GameObject>();

        /// <summary>Per GameObject: max animated local scale (per axis, abs). / 每个 GameObject：最大动画局部缩放（逐轴，绝对值）。</summary>
        public Dictionary<GameObject, Vector3> MaxLocalScale = new Dictionary<GameObject, Vector3>();

        /// <summary>Whether any animation animates the avatar's cutout/rendering-mode properties. / 是否有动画修改 Cutout/渲染模式属性。</summary>
        public bool AnimatesRenderingMode;

        /// <summary>(renderer, slot) pairs with animated material properties (no slot merging). /
        /// 有材质属性动画的（渲染器, 槽）对（禁止槽合并）。</summary>
        public HashSet<(Renderer, int)> AnimatedSlotProperties = new HashSet<(Renderer, int)>();

        /// <summary>Animated _Cutoff values per material (all distinct values; worst wins). / 每个材质被动画的 _Cutoff 值（全部去重值；取最严）。</summary>
        public Dictionary<Material, List<float>> AnimatedCutoffs = new Dictionary<Material, List<float>>();

        /// <summary>Whether any material-typed binding could not be resolved to a material (conservative). /
        /// 是否有无法解析到具体材质的 Material 类型绑定（保守）。</summary>
        public bool HasUnresolvedMaterialBinding;

        /// <summary>
        /// Materials that are direct animation targets (material-asset bindings). These must NOT be
        /// deduplicated: the clip stores the material reference internally with no public API to
        /// re-target it. / 作为直接动画目标的材质（材质资产绑定）。这些材质不得去重：剪辑内部存储材质引用，
        /// 没有公开 API 可改指向。
        /// </summary>
        public HashSet<Material> DirectAnimatedMaterials = new HashSet<Material>();
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>Per-renderer facts gathered from the scene + animations. / 来自场景与动画的渲染器事实。</summary>
    internal class RendererInfo
    {
        internal Renderer renderer;
        internal Mesh mesh;
        internal SkinnedMeshRenderer smr;
        /// <summary>Renderer enabled now or animated on at some point. / 当前启用或被动画启用过。</summary>
        internal bool include;
        internal Material[] slots;
        /// <summary>Slot has material-swap animations. / 材质槽存在材质切换动画。</summary>
        internal bool[] slotAnimated;
        /// <summary>Swap materials seen in animations per slot. / 各槽位动画中出现过的材质。</summary>
        internal readonly Dictionary<int, List<Material>> slotSwapMaterials = new Dictionary<int, List<Material>>();
        /// <summary>Extra textures swapped via material property animation (applies to renderer). / 经材质属性动画切换的贴图。</summary>
        internal readonly List<(string prop, List<Texture2D> textures)> textureSwaps = new List<(string, List<Texture2D>)>();
        /// <summary>Animated properties that make texture transforms unsafe ("_ST","_ScrollRotate",...). / 使贴图变换不安全的动画属性。</summary>
        internal readonly HashSet<string> unsafeAnimatedProps = new HashSet<string>();
        /// <summary>Renderer-level area factor from max animation scale. / 最大动画缩放的面积因子。</summary>
        internal float animAreaFactor = 1f;
        /// <summary>Alpha (mode,cutoff) keyframes seen in animation on this renderer. / 动画中出现过的透明参数。</summary>
        internal readonly List<(AlphaMode mode, float cutoff)> animatedAlpha = new List<(AlphaMode, float)>();
    }
}

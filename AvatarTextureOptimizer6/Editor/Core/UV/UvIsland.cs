using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.UV
{
    /// <summary>
    /// UV 岛：同一 UV 组内一个连通的 UV 区域。
    /// </summary>
    public sealed class UvIsland
    {
        public int id;
        public UvGroup group;

        /// <summary>三角形索引（绝对索引，指向 mesh.triangles，每三角形 3 个）。</summary>
        public readonly List<int> triangleIndices = new List<int>();

        /// <summary>原始 UV 空间包围盒（未归一化）。</summary>
        public Rect uvBounds;

        /// <summary>越界但可平移归一时的整数平移量。</summary>
        public Vector2 normalizedOffset;

        public bool needsNormalize;

        /// <summary>质量缩放后的组级缩放（相对原 UV 尺寸，逐轴）。</summary>
        public float scaleU = 1f, scaleV = 1f;

        /// <summary>最终图集 rect（UV 空间 0..1，组内各贴图图集共用同值）。</summary>
        public Rect atlasRect;

        /// <summary>打包时是否旋转 90°。</summary>
        public bool rotated90;

        /// <summary>图集中位置（UV 空间 0..1，组内各图集共用）。</summary>
        public Vector2 atlasPosUV;

        /// <summary>位置是否已由（本组或跨类型组）装箱分配。</summary>
        public bool layoutAssigned;

        /// <summary>纯色短路标记（质量阶段填充）。</summary>
        public bool pureColor;

        /// <summary>世界面积（m²，含形态键与动画缩放，密度计算用）。</summary>
        public float worldAreaM2 = -1f;

        /// <summary>该岛是否失败（应回退整图缩放）。</summary>
        public bool failed;
        public string failReason;
    }
}

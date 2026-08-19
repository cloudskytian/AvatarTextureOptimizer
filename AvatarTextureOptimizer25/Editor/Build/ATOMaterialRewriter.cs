// Avatar Texture Optimizer / 头像贴图优化器
// Material rewriting: clone materials (deep copy), retarget ONLY texture slots
// (never any other shader parameter), reassign renderer slots, remap PPtr
// animation references.
// 材质重写：克隆材质（深拷贝），只重定向贴图槽（绝不修改其他任何着色器参数），
// 重新分配渲染器槽位，重映射 PPtr 动画引用。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// What a texture should be replaced with for a given slot.
    /// 给定槽位应替换为的贴图资产。
    /// </summary>
    public sealed class ATOTextureAssignment
    {
        public Texture2D replacement;   // atlas layer or scaled standalone / 图集层或缩放独立图
        public RectInt atlasRectPx;     // placement (atlas generation only) / 摆放矩形（仅图集生成时）
        public int atlasW, atlasH;
        public bool rotated90;
        public bool usesAtlas;
    }

    /// <summary>
    /// Clones materials, retargets texture slots, updates renderer slots and animations.
    /// 克隆材质、重定向贴图槽、更新渲染器槽与动画。
    /// </summary>
    public sealed class ATOMaterialRewriter
    {
        private readonly BuildContext _ctx;
        private readonly ATOBuildReport _report;
        private readonly ATOAnimationData _anim;
        public readonly Dictionary<Material, Material> Cloned = new Dictionary<Material, Material>();
        public readonly Dictionary<Material, Material> Identity = new Dictionary<Material, Material>(); // clone -> original

        public ATOMaterialRewriter(BuildContext ctx, ATOBuildReport report, ATOAnimationData anim)
        {
            _ctx = ctx;
            _report = report;
            _anim = anim;
        }

        /// <summary>Get (or clone) the writable clone of a material. / 取（或克隆）某材质的可写副本。</summary>
        public Material GetWritableClone(Material original)
        {
            if (original == null) return null;
            if (Cloned.TryGetValue(original, out var clone) && clone != null) return clone;
            clone = Object.Instantiate(original);
            clone.name = original.name + "_ATO";
            _ctx.ObjectRegistry.RegisterReplacedObject(original, clone);
            Cloned[original] = clone;
            Identity[clone] = original;
            return clone;
        }

        /// <summary>
        /// Retarget texture slots on all cloned materials according to the
        /// assignment map. ONLY SetTexture calls are made.
        /// 按分配表重定向所有克隆材质的贴图槽。只调用 SetTexture。
        /// </summary>
        public void ApplyAssignments(
            Dictionary<(Material, string), ATOTextureAssignment> assignments)
        {
            using (new ATOLog.Step("material-assign"))
            {
                // Group assignments by material / 按材质分组
                var perMat = new Dictionary<Material, List<(string prop, ATOTextureAssignment a)>>();
                foreach (var kv in assignments)
                {
                    var (mat, prop) = kv.Key;
                    if (!perMat.TryGetValue(mat, out var list))
                    {
                        list = new List<(string, ATOTextureAssignment)>();
                        perMat[mat] = list;
                    }
                    list.Add((prop, kv.Value));
                }

                foreach (var kv in perMat)
                {
                    var clone = GetWritableClone(kv.Key);
                    if (clone == null) continue;
                    foreach (var (prop, a) in kv.Value)
                    {
                        if (a.replacement == null) continue;
                        try
                        {
                            clone.SetTexture(prop, a.replacement);
                        }
                        catch (Exception e)
                        {
                            _report.warnings.Add(ATOLoc.T("ato:mat.settex_failed", clone.name, prop, e.Message));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Replace renderer slots with clones where they differ; remap PPtr animation references.
        /// 将有差异的渲染器槽替换为克隆版；重映射 PPtr 动画引用。
        /// </summary>
        public void ApplyToRenderers(IEnumerable<Renderer> renderers)
        {
            int slotChanged = 0;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (Cloned.TryGetValue(m, out var clone) && clone != null)
                    {
                        mats[i] = clone;
                        changed = true;
                        slotChanged++;
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }
            ATOLog.Verbose($"material clone slots applied: {slotChanged}");

            // PPtr animation references old materials -> remap to clones.
            // PPtr 动画引用旧材质 -> 重映射到克隆。
            if (Cloned.Count > 0)
            {
                int remapped = ATOAnimationScanner.RemapMaterialReferences(_anim, Cloned);
                ATOLog.Verbose($"animation material refs remapped: {remapped}");
            }
        }
    }
}

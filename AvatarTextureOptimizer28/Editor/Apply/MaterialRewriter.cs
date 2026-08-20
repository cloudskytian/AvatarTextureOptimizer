using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Swaps texture references on materials and inside animation clips.
    ///
    ///     Strict invariant: only texture *references* are written. No colour, float, keyword, render
    ///     queue or ST value is ever modified. Materials are cloned before being touched, so the user's
    ///     source assets are never mutated.
    ///
    /// ZH: 替换材质与动画片段中的贴图引用。
    ///
    ///     严格不变量：只写入贴图**引用**。绝不修改任何颜色、浮点、关键字、渲染队列或 ST 值。
    ///     材质在被修改前会先克隆，因此用户的源资产永不被改动。
    /// </summary>
    public sealed class MaterialRewriter
    {
        private readonly ATOLog _log;
        private readonly Dictionary<Material, Material> _clones = new Dictionary<Material, Material>();

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public MaterialRewriter(ATOLog log) { _log = log; }

        /// <summary>EN: Materials we cloned, original to clone. ZH: 我们克隆过的材质，原件 -> 克隆件。</summary>
        public IReadOnlyDictionary<Material, Material> Clones => _clones;

        /// <summary>
        /// EN: Apply a texture remap to every renderer slot and every animation object curve.
        /// ZH: 把贴图重映射应用到所有渲染器材质槽与所有动画对象曲线。
        /// </summary>
        public void Apply(BuildContext ctx, IEnumerable<SlotRecord> slots,
            IReadOnlyDictionary<Texture2D, Texture2D> remap)
        {
            if (remap.Count == 0) return;

            foreach (var slot in slots)
            {
                var mats = slot.Renderer.sharedMaterials;
                if (slot.Index >= mats.Length) continue;
                var original = mats[slot.Index];
                if (original == null) continue;

                var updated = RewriteMaterial(original, remap);
                if (updated == original) continue;

                mats[slot.Index] = updated;
                slot.Renderer.sharedMaterials = mats;
            }

            // EN: Animation can assign materials that no renderer currently holds; rewrite those too, or
            //     switching an outfit at runtime would restore the un-atlased textures.
            // ZH: 动画可能赋予当前没有任何渲染器持有的材质；这些也必须重写，
            //     否则运行时切换服装会把未图集化的贴图换回来。
            var asc = ctx.Extension<AnimatorServicesContext>();
            asc.AnimationIndex.RewriteObjectCurves(obj =>
            {
                switch (obj)
                {
                    case Material m: return RewriteMaterial(m, remap);
                    case Texture2D t: return remap.TryGetValue(t, out var nt) ? nt : t;
                    default: return obj;
                }
            });

            _log.Verbose($"Material rewrite: {_clones.Count} materials cloned, {remap.Count} texture references remapped");
        }

        private Material RewriteMaterial(Material original, IReadOnlyDictionary<Texture2D, Texture2D> remap)
        {
            if (original == null || original.shader == null) return original;
            if (_clones.TryGetValue(original, out var existing)) return existing;

            var shader = original.shader;
            var count = shader.GetPropertyCount();
            var changes = new List<(string prop, Texture2D tex)>();

            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var name = shader.GetPropertyName(i);
                if (!(original.GetTexture(name) is Texture2D t)) continue;
                if (remap.TryGetValue(t, out var nt) && nt != t) changes.Add((name, nt));
            }

            if (changes.Count == 0)
            {
                _clones[original] = original;
                return original;
            }

            var clone = new Material(original) { name = original.name + " (ATO)" };
            foreach (var (prop, tex) in changes) clone.SetTexture(prop, tex);

            _clones[original] = clone;
            _log.Trace($"Material '{original.name}': remapped {changes.Count} texture references");
            return clone;
        }
    }
}

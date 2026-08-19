using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Apply
{
    // 动画绑定重写器：重写临时剪辑中贴图属性动画（m_Materials.Array.data[i].PROP 的对象引用曲线）的贴图值，
    // 使其指向图集/替换贴图（按属性与槽位解析，支持同一贴图被多个图集替换的情况）。
    // Animation binding remapper: rewrites texture-property animation values (object-reference curves of
    // m_Materials.Array.data[i].PROP) on temporary clips to point at atlases/replacements (resolved per property & slot;
    // supports one texture replaced by multiple atlases).
    internal static class AnimationBindingRemapper
    {
        private static readonly Regex SlotPropPattern = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]\.(.*)$", RegexOptions.Compiled);

        public static void RemapTextureProperties(ATOContext ctx, ATOReport.Stage stage)
        {
            int rewrites = 0;
            foreach (var kv in ctx.animations.clipRefs)
            {
                ctx.CheckCancelled();
                var clip = kv.Key;
                if (!ctx.ndmf.IsTemporaryAsset(clip)) continue;
                Transform baseT;
                if (!ctx.animations.clipBase.TryGetValue(clip, out baseT)) continue;

                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (binding.type != typeof(Material)) continue;
                    var m = SlotPropPattern.Match(binding.propertyName);
                    if (!m.Success) continue;
                    int idx;
                    if (!int.TryParse(m.Groups[1].Value, out idx)) continue;
                    string prop = m.Groups[2].Value;
                    var target = ResolvePath(baseT, binding.path);
                    if (target == null) continue;
                    var renderer = target.GetComponent<Renderer>();
                    if (renderer == null) continue;

                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    bool changed = false;
                    for (int k = 0; k < curve.Length; k++)
                    {
                        var tex = curve[k].value as Texture2D;
                        if (tex == null) continue;
                        var replacement = ResolveReplacement(ctx, renderer, idx, prop, tex);
                        if (replacement != null && replacement != tex)
                        {
                            curve[k].value = replacement;
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, curve);
                        rewrites++;
                    }
                }
            }
            if (rewrites > 0) stage.AddLine(string.Format(ATOLocalization.Tr("log.animRemap"), rewrites));
        }

        // 按（渲染器, 槽位, 属性, 旧贴图）解析替换。Resolves the replacement per (renderer, slot, property, old texture).
        private static Texture2D ResolveReplacement(ATOContext ctx, Renderer renderer, int slotIndex, string propertyName, Texture2D oldTex)
        {
            Analysis.TextureEntry entry;
            ctx.textureMap.TryGetValue(oldTex, out entry);
            var canon = ResolveCanonical(entry);
            if (canon == null) return null;

            // 该槽位上同名属性的使用 → 岛级替换。The slot's use with this property → island-level replacement.
            foreach (var slot in ctx.slots)
            {
                if (slot.renderer != renderer || slot.slotIndex != slotIndex) continue;
                foreach (var use in slot.uses)
                {
                    if (use.propertyName != propertyName || use.texture == null) continue;
                    if (ResolveCanonical(use.texture) != canon) continue;
                    var replacement = MaterialApplier.GetReplacement(ctx, use);
                    if (replacement != null) return replacement;
                }
            }
            if (canon.replacementTexture != null) return canon.replacementTexture;
            return null;
        }

        private static Analysis.TextureEntry ResolveCanonical(Analysis.TextureEntry entry)
        {
            var cur = entry;
            int guard = 0;
            while (cur != null && cur.dedupTarget != null && guard++ < 32) cur = cur.dedupTarget;
            return cur;
        }

        private static Transform ResolvePath(Transform baseT, string path)
        {
            if (string.IsNullOrEmpty(path)) return baseT;
            return baseT.Find(path);
        }
    }
}

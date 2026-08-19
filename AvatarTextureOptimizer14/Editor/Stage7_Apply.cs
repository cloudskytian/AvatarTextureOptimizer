// Stage7_Apply — material clone & texture retarget / 材质克隆与贴图重指向
// Only texture references change; every other material parameter is preserved by cloning (spec).
// A (texture, class) latched into pipe.blockedTex is never retargeted (safe fallback — its UV slots
// were left unchanged). Replaced materials are written back to renderer slots; originals are kept
// untouched on disk and registered for clip retargeting via pipe.materialReplacements.<br>
// 仅修改贴图引用，其余材质参数经克隆完整保留（需求）。进入 blockedTex 的 (贴图,类型) 绝不重指向
// （安全回退——对应UV槽保持原样）。替换后的材质回写渲染器槽位；磁盘原资产不动，
// 动画重定向经 pipe.materialReplacements 查表。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class Stage7_Apply
    {
        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            // ---- per-material retarget plan: orig -> (prop -> newTex) / 材质级重指向计划 ----
            var plan = new Dictionary<Material, Dictionary<string, Texture2D>>();
            foreach (var kv in pipe.slotRefs)
            {
                var slot = kv.Key;
                if (pipe.skipSlots.Contains(slot)) continue;                 // latched unsafe slot / 已锁存的不安全槽
                foreach (var r in kv.Value)
                {
                    if (r.material == null || string.IsNullOrEmpty(r.property)) continue;
                    foreach (var t in r.textures)
                    {
                        if (!pipe.infoOf.TryGetValue(t, out var info)) continue;
                        var target = ResolveTexture(pipe, info, r.cls);
                        if (target == null || target == t) continue;
                        if (!plan.TryGetValue(r.material, out var props)) plan[r.material] = props = new Dictionary<string, Texture2D>();
                        // first writer wins; variants (animation) share the same prop → same class target / 首个写入生效
                        if (!props.ContainsKey(r.property)) props[r.property] = target;
                        break; // one replacement per (material, property) is enough / 每(材质,属性)一个替换即可
                    }
                }
            }

            // whole-texture replacements also cover materials not present in any UV group / 整图替换同样覆盖材质
            foreach (var rep in pipe.wholeTexReplacement)
            {
                var info = rep.Key; var newTex = rep.Value;
                if (newTex == null) continue;
                foreach (var kv in pipe.slotRefs)
                {
                    foreach (var r in kv.Value)
                    {
                        if (r.material == null || !r.textures.Contains(info.source)) continue;
                        // whole replacement only when no atlas plane exists for this class / 该类无图集平面时才用整图替换
                        if (pipe.atlasPlaneOf.ContainsKey((info, r.cls))) continue;
                        if (pipe.blockedTex.Contains((info, r.cls))) continue;
                        if (!plan.TryGetValue(r.material, out var props)) plan[r.material] = props = new Dictionary<string, Texture2D>();
                        if (!props.ContainsKey(r.property)) props[r.property] = newTex;
                    }
                }
            }

            // ---- clone materials & apply / 克隆材质并应用 ----
            var toSave = new List<Object>();
            int pi = 0;
            foreach (var kv in plan)
            {
                pi++;
                if ((pi & 7) == 0) pipe.CancelCheck(progress, ATOL10n.T("ato.stage.materials"), (float)pi / Mathf.Max(1, plan.Count));
                var orig = kv.Key;
                var clone = Object.Instantiate(orig);
                clone.name = orig.name + "_ATO";
                foreach (var p in kv.Value) clone.SetTexture(p.Key, p.Value);
                pipe.materialReplacements[orig] = clone;
                toSave.Add(clone);
            }
            if (toSave.Count > 0) ctx.AssetSaver?.SaveAssets(toSave);

            // ---- write back renderer slots / 回写渲染器槽位 ----
            foreach (var r in pipe.rendererStates.Keys)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m != null && pipe.materialReplacements.TryGetValue(m, out var rep) && rep != null)
                    {
                        mats[i] = rep; changed = true;
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }

            ATOLog.Info(ATOL10n.T("ato.log.materials_done", pipe.materialReplacements.Count));
            ATOEvents.Raise("materials", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("materials", pipe);
        }

        /// <summary>
        /// Resolve the optimized replacement for (texture, class): atlas plane texture, whole-texture
        /// replacement, or null (keep original). Blocked units always resolve to null (safe fallback).<br/>
        /// 解析 (贴图,类型) 的优化产物：图集平面贴图 → 整图替换 → null（保持原图）。被阻断单元恒为 null。
        /// </summary>
        internal static Texture2D ResolveTexture(ATOPipeContext pipe, TextureInfo info, TexClass cls)
        {
            if (info == null || info.whitelisted) return null;                 // whitelist: untouched / 白名单不动
            if (pipe.blockedTex.Contains((info, cls))) return null;             // latched unsafe / 安全锁存
            if (pipe.atlasPlaneOf.TryGetValue((info, cls), out var plane) && plane.texture != null) return plane.texture;
            if (pipe.wholeTexReplacement.TryGetValue(info, out var whole) && whole != null) return whole;
            return null;
        }
    }
}

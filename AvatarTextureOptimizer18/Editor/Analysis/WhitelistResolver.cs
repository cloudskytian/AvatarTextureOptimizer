using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    // 白名单解析：直接白名单 + 派生传播 + 使用级条件白名单。
    // Whitelist resolution: direct whitelist + derived propagation + per-use conditional whitelisting.
    //
    // 规则摘要（依据需求）：
    // - 白名单不限制对象类型（网格、材质、贴图、动画、GameObject 等）；
    // - 白名单对象中引用的全部贴图跳过所有优化（含导入参数）→ Full；
    // - ST 平移/缩放/旋转（含动画修改）、特殊用途 UV、UVMode 动画、不支持着色器 → 视作白名单处理（Full + warning）；
    // - 去重组内任一成员白名单 → 去重结果也视为白名单；
    // - 同 UV 的其他贴图跳过图集化（NoAtlas）→ 待 UV 组建立后处理（后续阶段）。
    internal static class WhitelistResolver
    {
        public static void Resolve(ATOContext ctx, ATOReport.Stage stage)
        {
            // 1) 直接白名单集合（GameObject 递归展开整个子树）。Direct whitelist set (GameObjects expand to their whole subtree).
            var direct = new HashSet<Object>();
            foreach (var w in ATOWhitelist.Collect(ctx.avatarRoot))
            {
                if (w.objects == null) continue;
                foreach (var obj in w.objects)
                {
                    if (obj == null) continue;
                    direct.Add(obj);
                    if (obj is GameObject go)
                    {
                        foreach (var t in go.GetComponentsInChildren<Transform>(true)) direct.Add(t.gameObject);
                        foreach (var c in go.GetComponentsInChildren<Component>(true)) direct.Add(c);
                    }
                }
            }

            // 2) 槽位级白名单传播。Slot-level propagation.
            foreach (var slot in ctx.slots)
            {
                ctx.CheckCancelled();
                if (direct.Contains(slot.mesh)) { WhitelistSlot(slot, "warn.whitelist.viaMesh", stage); continue; }
                if (direct.Contains(slot.renderer) || direct.Contains(slot.renderer.gameObject)) { WhitelistSlot(slot, "warn.whitelist.viaObject", stage); continue; }
                if (direct.Contains(slot.material)) { WhitelistSlot(slot, "warn.whitelist.viaMaterial", stage); continue; }
                if (!ShaderTextureTable.IsShaderSupported(slot.material)) { WhitelistSlot(slot, "warn.whitelist.unsupportedShader", stage); continue; }
            }

            // 3) 动画白名单：该动画引用的贴图/材质全白名单。Whitelisted clips: their referenced textures/materials are whitelisted.
            if (direct.Count > 0)
            {
                foreach (var kv in ctx.animations.clipRefs)
                {
                    if (!direct.Contains(kv.Key)) continue;
                    foreach (var mat in kv.Value.materials) WhitelistMaterialUses(ctx, mat, "warn.whitelist.viaClip");
                    foreach (var tex in kv.Value.textures) WhitelistTexture(ctx, tex, "warn.whitelist.viaClip");
                }
            }

            // 4) 使用级条件白名单：ST 变换 / 特殊用途 UV / UVMode 动画。
            // Per-use conditional whitelisting: ST transforms / special-purpose UV / animated UVMode.
            foreach (var e in ctx.textures)
            {
                foreach (var u in e.uses)
                {
                    if (u.stTransform) SetUseLevel(u, ATOWhitelistLevel.Full, "warn.whitelist.stTransform");
                    else if (u.specialPurposeUV) SetUseLevel(u, ATOWhitelistLevel.Full, "warn.whitelist.specialUV");
                    else if (u.uvModeAnimated) SetUseLevel(u, ATOWhitelistLevel.Full, "warn.whitelist.uvModeAnimated");
                }
            }

            // 5) 条目级汇总：任一使用 Full → 条目 Full。Entry-level summary: any Full use → entry Full.
            foreach (var e in ctx.textures)
            {
                SumEntryLevel(e);
            }

            // 6) 去重组传播：任一成员白名单 → 去重结果（canonical）也白名单。
            // Dedup-group propagation: any member whitelisted → the dedup result (canonical) is whitelisted too.
            foreach (var e in ctx.textures)
            {
                var canon = e.dedupTarget;
                while (canon != null && canon.dedupTarget != null) canon = canon.dedupTarget;
                if (canon == null) continue;
                if (e.whitelistLevel == ATOWhitelistLevel.Full && canon.whitelistLevel != ATOWhitelistLevel.Full)
                {
                    canon.whitelistLevel = ATOWhitelistLevel.Full;
                    canon.whitelistReason = e.whitelistReason;
                    stage.AddLine(string.Format(ATOLocalization.Tr("log.whitelistDedup"), canon.ToString(), ATOLocalization.Tr(e.whitelistReason)));
                }
            }

            // 统计。Statistics.
            int full = 0, noAtlas = 0;
            foreach (var e in ctx.textures)
            {
                if (e.whitelistLevel == ATOWhitelistLevel.Full) full++;
                else if (e.whitelistLevel == ATOWhitelistLevel.NoAtlas) noAtlas++;
            }
            ctx.report.whitelistedTextureCount = full;
            stage.AddLine(string.Format(ATOLocalization.Tr("log.whitelistSummary"), ctx.textures.Count, full, noAtlas));

            // 注：同 UV 的其他贴图 → NoAtlas 级别传播在 UvGroupBuilder.Build 中处理（已实现）。
            // Note: NoAtlas propagation to other textures sharing the same UV is handled in UvGroupBuilder.Build (implemented).
        }

        // 整个槽位的贴图使用 → Full（含日志）。Whitelists all texture uses of a slot (with log).
        private static void WhitelistSlot(SlotEntry slot, string reasonKey, ATOReport.Stage stage)
        {
            foreach (var use in slot.uses)
            {
                SetUseLevel(use, ATOWhitelistLevel.Full, reasonKey);
            }
            stage.AddLine(string.Format(ATOLocalization.Tr("log.whitelistSlot"), slot.ToString(), ATOLocalization.Tr(reasonKey)));
        }

        // 某材质的所有贴图使用 → Full。Whitelists all texture uses of a material.
        private static void WhitelistMaterialUses(ATOContext ctx, Material mat, string reasonKey)
        {
            if (mat == null) return;
            foreach (var e in ctx.textures)
            {
                foreach (var u in e.uses)
                {
                    if (u.slot != null && u.slot.material == mat) SetUseLevel(u, ATOWhitelistLevel.Full, reasonKey);
                }
            }
        }

        // 某贴图的全部使用 → Full。Whitelists all uses of a texture.
        private static void WhitelistTexture(ATOContext ctx, Texture2D tex, string reasonKey)
        {
            if (tex == null) return;
            TextureEntry e;
            if (!ctx.textureMap.TryGetValue(tex, out e)) return;
            foreach (var u in e.uses) SetUseLevel(u, ATOWhitelistLevel.Full, reasonKey);
        }

        // 设置使用级别（只升不降）。Sets the use level (only escalates).
        private static void SetUseLevel(TextureUse use, ATOWhitelistLevel level, string reasonKey)
        {
            if ((int)level <= (int)use.whitelistLevel) return;
            use.whitelistLevel = level;
            use.whitelistReason = reasonKey;
        }

        // 条目级汇总。Entry-level summary.
        private static void SumEntryLevel(TextureEntry e)
        {
            foreach (var u in e.uses)
            {
                if (u.whitelistLevel == ATOWhitelistLevel.Full)
                {
                    e.whitelistLevel = ATOWhitelistLevel.Full;
                    e.whitelistReason = u.whitelistReason;
                    return;
                }
                if (u.whitelistLevel == ATOWhitelistLevel.NoAtlas && e.whitelistLevel == ATOWhitelistLevel.None)
                {
                    e.whitelistLevel = ATOWhitelistLevel.NoAtlas;
                    e.whitelistReason = u.whitelistReason;
                }
            }
        }
    }
}

// ============================================================================
// CollectStage.cs — 阶段1：收集渲染器/材质/贴图 / Stage 1: collect renderers,
//                    materials, and textures
// (EN) Walks all SkinnedMeshRenderer/MeshRenderer under the avatar (skipping
//      EditorOnly), analyzes each material slot, filters textures by the safety
//      rules (no ST transform, not special-purpose, not whitelisted), registers
//      them in a dedup registry, and extracts per-renderer UV channel presence.
// (ZH) 遍历 Avatar 下所有 SkinnedMeshRenderer/MeshRenderer（跳过 EditorOnly），
//      分析每个材质槽，按安全规则过滤贴图（无 ST 变换、非特殊用途、非白名单），
//      注册到去重表，并提取每个渲染器的 UV 通道信息。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>(EN) Result of the collect stage, shared with later stages. (ZH) 收集阶段结果，供后续阶段共享。</summary>
    public class ATOCollectResult
    {
        public List<ATORendererInfo> Renderers = new List<ATORendererInfo>();
        /// <summary>(EN) Canonical texture ref per asset instance. (ZH) 每个贴图资产的规范引用。</summary>
        public Dictionary<Texture2D, ATOTextureRef> Canonical = new Dictionary<Texture2D, ATOTextureRef>();
        /// <summary>(EN) Dedup: duplicate -> canonical. (ZH) 去重：重复贴图 → 规范贴图。</summary>
        public List<KeyValuePair<Texture2D, Texture2D>> DedupPairs = new List<KeyValuePair<Texture2D, Texture2D>>();

        public void Clear() { Renderers.Clear(); Canonical.Clear(); DedupPairs.Clear(); }
    }

    public class CollectStage
    {
        private readonly ATOBuildContext _ctx;
        private readonly ATOCollectResult _result = new ATOCollectResult();

        public ATOCollectResult Result => _result;

        public CollectStage(ATOBuildContext ctx) => _ctx = ctx;

        public void Run()
        {
            _result.Clear();
            ATOTextureIO.ClearCache();

            // 白名单初始化 / initialize whitelist
            ATOWhitelist.Set.Clear();
            foreach (var obj in _ctx.Whitelist)
            {
                if (obj != null) ATOWhitelist.Set.Add(obj);
            }

            // 遍历所有渲染器 / iterate all renderers
            var renderers = _ctx.AvatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                if (IsEditorOnly(r)) continue;

                var mesh = ATOMeshUtils.GetMesh(r);
                if (mesh == null) continue;

                var info = new ATORendererInfo
                {
                    Renderer = r,
                    Mesh = mesh,
                    IsSkinned = r is SkinnedMeshRenderer,
                    EnabledByDefault = r.enabled,
                    EnabledByAnimation = false, // 动画启用状态在阶段2填充 / filled in stage 2
                    UvChannelPresent = ATOMeshUtils.GetUvChannelPresence(mesh),
                };

                CollectSlots(r, info);
                _result.Renderers.Add(info);
            }

            ATOLog.VerboseLog($"[collect] {_result.Renderers.Count} renderers collected, {_result.Canonical.Count} unique textures");
        }

        private void CollectSlots(Renderer r, ATORendererInfo info)
        {
            var materials = r.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                var mat = materials[i];
                if (mat == null) continue;

                var slot = new ATOSlot { SlotIndex = i, Material = mat };

                foreach (var entry in ATOShaderAnalysis.AnalyzeMaterial(mat))
                {
                    RegisterTexture(entry, info);
                    slot.Textures.Add(entry);
                }

                info.Slots.Add(slot);
            }
        }

        /// <summary>(EN) Register a texture ref in the dedup registry; mark whitelisted if needed. (ZH) 注册贴图引用到去重表；必要时标记白名单。</summary>
        private void RegisterTexture(ATOSlotTexture entry, ATORendererInfo info)
        {
            var tex = entry.Ref.Texture;
            if (tex == null) { entry.Ref.Whitelisted = true; return; }

            // 白名单判定 / whitelist decision
            if (ATOWhitelist.TextureWhitelisted(tex, info))
            {
                entry.Ref.Whitelisted = true;
            }

            entry.Ref.ImportSignature = ATOTextureIO.GetImportSignature(tex);
            entry.Ref.PixelSignature = ATOTextureIO.GetPixelSignature(tex);
            ATOTextureIO.Classify(entry.Ref);

            // 去重登记 / dedup registration
            if (_result.Canonical.TryGetValue(tex, out var canonical))
            {
                // 用途冲突检测：同一贴图被不同材质以不同用途引用（如既是主色又是法线）
                // usage conflict: same texture referenced with different usages
                if (canonical.Usage != entry.Ref.Usage && !canonical.Whitelisted)
                {
                    canonical.Whitelisted = true;
                    ATOLog.Warn($"[collect] texture {tex.name} referenced with multiple usages ({canonical.Usage} vs {entry.Ref.Usage}); whitelisted (skipped)");
                }
                // 同一资产只注册一次 / register once per asset
                entry.Ref = canonical;
                return;
            }

            // 检查是否与已有贴图内容+设置完全相同 / check for content+settings duplicates
            var identity = entry.Ref.DedupIdentity;
            foreach (var kv in _result.Canonical)
            {
                var existing = kv.Value;
                if (existing.DedupIdentity == identity)
                {
                    _result.DedupPairs.Add(new KeyValuePair<Texture2D, Texture2D>(tex, kv.Key));
                    entry.Ref = existing;
                    _result.Canonical[tex] = existing;
                    ATOLog.VerboseLog($"[dedup] {tex.name} == {kv.Key.name} (identical content & settings)");
                    return;
                }
            }

            _result.Canonical[tex] = entry.Ref;
        }

        private static bool IsEditorOnly(Renderer r)
        {
            var t = r.transform;
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }
    }
}

// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Analysis/AvatarAnalyzer.cs — Avatar 材质槽/贴图/UV 收集 / Avatar material-slot & texture collection
//
// 逻辑（对应需求"大体逻辑"第 1、2 步）:
//  1) 遍历所有材质槽（跳过 EditorOnly），获取满足限制条件的主色/法线等贴图；
//     多通道 UV 拆成独立 UV 使用。
//  2) 结合动画分析：动画切换的贴图/材质并入 UV 映射（去重）。
//  3) 建立 (网格, UV通道) → UVGroup（同一 UV 的全部贴图）。
//  4) 不满足限制条件（ST 变换/贴花/未知属性/白名单等）→ 视作白名单。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Avatar 分析结果 / Avatar analysis result.
    /// </summary>
    public sealed class AvatarAnalysis
    {
        public List<MaterialSlotRef> slots = new List<MaterialSlotRef>();
        public Dictionary<Mesh, Dictionary<int, UVGroup>> groupsByMesh = new Dictionary<Mesh, Dictionary<int, UVGroup>>();
        public List<UVGroup> allGroups = new List<UVGroup>();
        public List<TextureRef> allTextures = new List<TextureRef>();
        public int processedSlotCount;
        public int whitelistedSlotCount;
    }

    /// <summary>
    /// Avatar 分析器 / Avatar analyzer.
    /// </summary>
    public static class AvatarAnalyzer
    {
        /// <summary>
        /// 分析 Avatar / Analyze the avatar.
        /// </summary>
        public static AvatarAnalysis Analyze(GameObject root, ATOComponent cfg, AnimationData anim, Whitelist wl)
        {
            var result = new AvatarAnalysis();
            var seenTextures = new HashSet<Texture2D>();

            // 1. 遍历渲染器 / Iterate renderers
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int ri = 0; ri < renderers.Length; ri++)
            {
                Cancel.Checkpoint();

                var r = renderers[ri];
                var go = r.gameObject;

                // 跳过 EditorOnly / Skip EditorOnly
                if (go.CompareTag("EditorOnly")) continue;

                // 只在被启用或有动画启用的渲染器上处理 / Only enabled or animation-enabled renderers
                bool animatedEnabled = anim.everEnabledByAnimation.Contains(r);
                if (!r.enabled && !animatedEnabled)
                {
                    Log.VerboseLog($"skip renderer '{go.name}': disabled and not animation-enabled");
                    continue;
                }

                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else if (r is MeshRenderer mr)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null)
                {
                    Log.VerboseLog($"skip renderer '{go.name}': no mesh");
                    continue;
                }

                bool rendererWhitelisted = wl.IsWhitelisted(go) || wl.IsWhitelisted(r) || wl.IsWhitelisted(mesh);
                bool meshSwapAnimated = anim.animatedMeshSwap.Contains(r);

                // 2. 材质槽 / Material slots
                var materials = r.sharedMaterials;
                int subMeshCount = mesh.subMeshCount;
                for (int slot = 0; slot < subMeshCount; slot++)
                {
                    if (slot >= materials.Length || materials[slot] == null) continue;
                    var mat = materials[slot];
                    result.processedSlotCount++;

                    var slotRef = new MaterialSlotRef
                    {
                        renderer = r,
                        slotIndex = slot,
                        material = mat,
                        mesh = mesh,
                    };

                    bool slotWhitelisted = rendererWhitelisted || wl.IsWhitelisted(mat) || wl.IsWhitelisted(mat.shader);
                    if (meshSwapAnimated)
                    {
                        slotRef.whitelisted = true;
                        slotWhitelisted = true;
                    }
                    if (slotWhitelisted) result.whitelistedSlotCount++;

                    // 材质静态贴图属性 / Static material texture properties
                    var analysis = ShaderAnalyzer.AnalyzeMaterial(mat, new ShaderAnalyzer.LogContext { avatarName = root.name });
                    slotRef.alphaMode = analysis.alphaMode;
                    slotRef.cutoff = analysis.cutoff;
                    slotRef.textures = new List<TextureRef>();

                    foreach (var at in analysis.textures)
                    {
                        var tref = BuildTextureRef(root, r, mesh, slot, mat, at, anim, wl, slotWhitelisted);
                        if (tref == null) continue;
                        slotRef.textures.Add(tref);
                        if (seenTextures.Add(tref.source))
                        {
                            result.allTextures.Add(tref);
                        }
                    }

                    // 动画切换的材质 → 分析并加入 / Animated material swaps → analyze and add
                    if (anim.slotAnims.TryGetValue(r, out var slotMap) && slotMap.TryGetValue(slot, out var sinfo))
                    {
                        foreach (var swappedMat in sinfo.materialSwaps)
                        {
                            if (swappedMat == null) continue;
                            var sa = ShaderAnalyzer.AnalyzeMaterial(swappedMat, new ShaderAnalyzer.LogContext { avatarName = root.name });
                            slotRef.extraAlphaModes.Add((sa.alphaMode, sa.cutoff));
                            foreach (var at in sa.textures)
                            {
                                var tref = BuildTextureRef(root, r, mesh, slot, swappedMat, at, anim, wl, slotWhitelisted);
                                if (tref == null) continue;
                                slotRef.textures.Add(tref);
                                if (seenTextures.Add(tref.source)) result.allTextures.Add(tref);
                            }
                        }

                        // 动画切换的贴图属性 → 加入 / Animated texture swaps → add
                        foreach (var kv in sinfo.textureSwaps)
                        {
                            foreach (var tex in kv.Value)
                            {
                                if (tex == null) continue;
                                // 该贴图通过属性 kv.Key 采样同一 UV；角色用材质属性名推断 /
                                // The texture samples the same UV via kv.Key; classify by property name
                                var at = ClassifySwappedProperty(mat, kv.Key, tex);
                                if (at == null) continue;
                                var tref = BuildTextureRef(root, r, mesh, slot, mat, at.Value, anim, wl, slotWhitelisted);
                                if (tref == null) continue;
                                tref.animated = true;
                                slotRef.textures.Add(tref);
                                if (seenTextures.Add(tref.source)) result.allTextures.Add(tref);
                            }
                        }
                    }

                    if (slotRef.textures.Count == 0)
                    {
                        // 无优化贴图；仍记录槽位供后续统计 / No optimizable textures
                        result.slots.Add(slotRef);
                        continue;
                    }

                    result.slots.Add(slotRef);
                }
            }

            // 3. 建立 UV 组 / Build UV groups
            foreach (var slot in result.slots)
            {
                foreach (var tref in slot.textures)
                {
                    if (tref.uvChannel < 0) continue; // 非网格UV → 不参与 UV 组
                    var group = GetOrCreateGroup(result, slot.mesh, tref.uvChannel);
                    AddTextureToGroup(group, tref, slot);
                }
            }

            // 4. 组级白名单传播 / Group-level whitelist propagation
            foreach (var g in result.allGroups)
            {
                foreach (var t in g.textures)
                {
                    if (t.whitelisted)
                    {
                        g.whitelisted = true;
                        g.whitelistReason = t.whitelistReason;
                        break;
                    }
                }
                // 组内原尺寸上限 / Max original short side in group
                int maxSide = 0;
                foreach (var t in g.textures)
                {
                    if (t.source != null) maxSide = Mathf.Max(maxSide, Mathf.Min(t.source.width, t.source.height));
                }
                g.maxOriginalShortSide = maxSide;
            }

            Log.Info($"analyzed {result.processedSlotCount} slots ({result.whitelistedSlotCount} whitelisted), " +
                     $"{result.allTextures.Count} unique textures, {result.allGroups.Count} UV groups");
            return result;
        }

        /// <summary>
        /// 构建单个 TextureRef / Build a single TextureRef.
        /// </summary>
        private static TextureRef BuildTextureRef(GameObject root, Renderer r, Mesh mesh, int slot, Material mat,
            AnalyzedTexture at, AnimationData anim, Whitelist wl, bool slotWhitelisted)
        {
            if (at.texture == null) return null;

            var tref = new TextureRef
            {
                source = at.texture,
                property = at.property,
                role = at.role,
                uvChannel = -1,
            };

            // 材质静态 ST 已在 ShaderAnalyzer 标记 / Static ST already flagged in ShaderAnalyzer
            if (at.incompatible)
            {
                tref.whitelisted = true;
                tref.whitelistReason = "unknown-or-transform";
                return tref;
            }

            // 非网格UV/贴花/特殊 → 白名单，不参与 UV 组 / Non-mesh/decal/special → whitelisted, no UV group
            if (at.nonMesh)
            {
                tref.whitelisted = true;
                tref.whitelistReason = "non-mesh-uv";
                return tref;
            }
            if (at.decalOrSpecial)
            {
                tref.whitelisted = true;
                tref.whitelistReason = "decal-or-special";
                return tref;
            }

            // 网格必须存在该 UV 通道 / Mesh must have this UV channel
            int channel = at.uvChannel;
            if (!MeshHasChannel(mesh, channel))
            {
                tref.whitelisted = true;
                tref.whitelistReason = "no-uv-channel";
                return tref;
            }
            tref.uvChannel = channel;

            // 用户白名单 / User whitelist
            if (slotWhitelisted || wl.IsWhitelisted(at.texture))
            {
                tref.whitelisted = true;
                tref.whitelistReason = "user-whitelist";
            }

            // 动画 ST 变换 → 白名单 / Animated ST transform → whitelist
            if (anim.slotAnims.TryGetValue(r, out var slotMap) && slotMap.TryGetValue(slot, out var sinfo))
            {
                if (sinfo.floatProps.Contains(at.property + "_ST"))
                {
                    tref.whitelisted = true;
                    tref.whitelistReason = "animated-st";
                }
            }

            // 元数据 / Metadata
            var importer = GetTextureImporter(at.texture);
            tref.sRGB = importer != null ? importer.sRGBTexture : !IsNormal(at.role);
            tref.filterMode = at.texture != null ? at.texture.filterMode : FilterMode.Bilinear;
            if (tref.filterMode == FilterMode.Point) tref.filterMode = FilterMode.Bilinear; // Point 不缩放纹理, 仅记录
            tref.category = CategoryFor(at.role);
            tref.hasAlpha = false; // 惰性计算 / lazy
            tref.sourceBytes = at.texture != null ? (long)at.texture.width * at.texture.height * 4 : 0;

            return tref;
        }

        private static bool MeshHasChannel(Mesh mesh, int channel)
        {
            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            return uvs.Count > 0;
        }

        /// <summary>
        /// 动画切换贴图的属性分类（无法确定时用通用规则兜底）/
        /// Classify an animation-swapped texture property.
        /// </summary>
        private static AnalyzedTexture? ClassifySwappedProperty(Material mat, string property, Texture2D tex)
        {
            var a = ShaderAnalyzer.Classify(mat, property, tex, new ShaderAnalyzer.LogContext());
            if (a.nonMesh || a.decalOrSpecial || a.incompatible || a.disabled) return null;
            return a;
        }

        private static void AddTextureToGroup(UVGroup group, TextureRef tref, MaterialSlotRef slot)
        {
            // 同贴图去重（组内唯一） / Dedup within group
            foreach (var existing in group.textures)
            {
                if (existing.source == tref.source)
                {
                    existing.referencingSlots.Add(slot);
                    return;
                }
            }
            group.textures.Add(tref);
            tref.referencingSlots.Add(slot);
        }

        private static UVGroup GetOrCreateGroup(AvatarAnalysis result, Mesh mesh, int channel)
        {
            if (!result.groupsByMesh.TryGetValue(mesh, out var channelMap))
            {
                channelMap = new Dictionary<int, UVGroup>();
                result.groupsByMesh[mesh] = channelMap;
            }
            if (!channelMap.TryGetValue(channel, out var group))
            {
                group = new UVGroup { mesh = mesh, uvChannel = channel };
                channelMap[channel] = group;
                result.allGroups.Add(group);
            }
            return group;
        }

        /// <summary>
        /// 读取 TextureImporter（可能为 null）/ Get the texture importer (may be null).
        /// </summary>
        internal static UnityEditor.TextureImporter GetTextureImporter(Texture2D tex)
        {
            if (tex == null) return null;
            var path = UnityEditor.AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;
            return UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
        }

        /// <summary>角色 → 压缩分类 / Role → compression category</summary>
        internal static TextureCategory CategoryFor(TextureRole role)
        {
            switch (role)
            {
                case TextureRole.Normal: return TextureCategory.Normal;
                case TextureRole.Mask:
                case TextureRole.Emission:
                case TextureRole.Other: return TextureCategory.Grayscale;
                default: return TextureCategory.Opaque;
            }
        }

        private static bool IsNormal(TextureRole role) => role == TextureRole.Normal;
    }
}

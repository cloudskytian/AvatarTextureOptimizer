using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Texture-use collection: walks renderers/materials/shader properties and animations to build
// the UV<->texture mapping (UVGroups) with whitelist/transform safety flags.
// 贴图引用收集：遍历渲染器/材质/着色器属性与动画，构建 UV↔贴图映射（UV 组）及白名单/变换安全标记。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class TextureUseCollector
    {
        private readonly GameObject _root;
        private readonly ATOSettingsData _data;
        private readonly AnimationAnalysis _anim;
        private readonly WhiteListEvaluator _white;
        private readonly TextureDecodeCache _decode;
        private readonly ATOBuildContext _ctx;
        private readonly List<string> _warnings = new List<string>();

        public TextureUseCollector(GameObject root, ATOSettingsData data, AnimationAnalysis anim, WhiteListEvaluator white, TextureDecodeCache decode, ATOBuildContext ctx)
        {
            _root = root; _data = data; _anim = anim; _white = white; _decode = decode; _ctx = ctx;
        }

        public IReadOnlyList<string> Warnings => _warnings;

        public void Collect()
        {
            var renderers = _root.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (IsEditorOnly(renderer.gameObject)) continue;
                // Skip renderers that are never enabled (static off, no enable animation). 跳过永不启用的渲染器。
                if (!_anim.IsEverEnabled(renderer.transform, _root))
                {
                    ATOLog.VerboseLog($"skip never-enabled renderer {renderer.name}");
                    continue;
                }
                if (_white.IsWhitelisted(renderer))
                {
                    ATOLog.VerboseLog($"renderer {renderer.name} whitelisted");
                    continue;
                }

                Mesh mesh = GetMesh(renderer);
                if (mesh == null) continue;
                if (_white.IsWhitelisted(mesh))
                {
                    ATOLog.VerboseLog($"mesh {mesh.name} whitelisted");
                    continue;
                }

                Material[] mats = renderer.sharedMaterials;
                int submeshCount = mesh.subMeshCount;
                for (int slot = 0; slot < mats.Length && slot < submeshCount; slot++)
                {
                    CollectSlot(renderer, mesh, slot, mats[slot]);
                }
            }
        }

        private void CollectSlot(Renderer renderer, Mesh mesh, int slot, Material staticMat)
        {
            // Material candidates: static + animation-switched materials on this slot. 材质候选：静态 + 动画切换。
            var candidates = new List<Material>();
            if (staticMat != null) candidates.Add(staticMat);
            foreach (var o in _anim.SlotReferenceCandidates(renderer.transform, _root, slot, ""))
            {
                // The "m_Materials.Array.data[i]" object-ref curve targets a Material. 该对象引用曲线的目标是材质。
                if (o is Material m && !candidates.Contains(m)) candidates.Add(m);
            }

            bool slotMaterialWhitelisted = _white.IsWhitelisted(staticMat);

            // Per-channel uses gathered first, then islands per channel.
            // 先按通道收集引用，再按通道提取岛。
            var usesByChannel = new Dictionary<int, List<TextureUse>>();
            var channelMatCap = new HashSet<int>();
            var channelSTBad = new HashSet<int>();

            foreach (var mat in candidates)
            {
                if (mat == null || mat.shader == null) continue;
                var shaderInfo = ShaderPropertyTable.Get(mat.shader);
                if (shaderInfo == null)
                {
                    _warnings.Add($"shader {mat.shader.name} could not be analyzed on {renderer.name} slot {slot}; treated as whitelist");
                    continue;
                }
                bool matWhitelisted = slotMaterialWhitelisted || _white.IsWhitelisted(mat);
                foreach (var prop in shaderInfo.TextureProperties)
                {
                    if (ShaderPropertyTable.IsDefinitivelyDisabled(mat, prop)) continue;
                    Texture2D tex = mat.GetTexture(prop.PropertyName) as Texture2D;
                    if (tex == null) continue;
                    if (tex.width == 0 || tex.height == 0) continue;

                    int channel = ShaderPropertyTable.ResolveUVChannel(mat, prop, out bool matCap);
                    if (matCap) channelMatCap.Add(channel);

                    bool stOk = ShaderPropertyTable.HasIdentityST(mat, prop);
                    if (!string.IsNullOrEmpty(prop.STProperty))
                    {
                        if (_anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.STProperty + ".x") ||
                            _anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.STProperty + ".y") ||
                            _anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.STProperty + ".z") ||
                            _anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.STProperty + ".w") ||
                            _anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.PropertyName + "_ST.x") ||
                            _anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.PropertyName + "_ST.y") ||
                            _anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.PropertyName + "_ST.z") ||
                            _anim.SlotPropertyAnimated(renderer.transform, _root, slot, prop.PropertyName + "_ST.w"))
                            stOk = false;
                    }
                    if (!stOk) channelSTBad.Add(channel);

                    // Animated texture candidates on this property. 该属性的动画贴图候选。
                    var animTexCandidates = _anim.SlotReferenceCandidates(renderer.transform, _root, slot, prop.PropertyName);
                    var allTextures = new List<Texture2D> { tex };
                    foreach (var o in animTexCandidates)
                        if (o is Texture2D t && t != tex) allTextures.Add(t);

                    foreach (var t in allTextures)
                    {
                        var use = BuildUse(renderer, slot, mat, prop, t, channel, matWhitelisted, matCap, !stOk);
                        if (!usesByChannel.TryGetValue(channel, out var list)) { list = new List<TextureUse>(); usesByChannel[channel] = list; }
                        list.Add(use);
                    }
                }
            }

            if (usesByChannel.Count == 0) return;

            // Islands per used channel. 每个被使用的通道提取岛。
            foreach (var kv in usesByChannel)
            {
                int channel = kv.Key;
                if (channelMatCap.Contains(channel))
                {
                    foreach (var u in kv.Value) MarkSkip(u, "MatCap/view-space UV cannot be repacked (UVMode=4)");
                    continue;
                }
                if (channelSTBad.Contains(channel))
                {
                    foreach (var u in kv.Value) MarkSkip(u, "ST transform (static or animated) present");
                    continue;
                }
                if (channel >= 8 || MeshUVAnalyzer.GetUVs(mesh, channel) == null)
                {
                    foreach (var u in kv.Value) MarkSkip(u, $"mesh has no UV channel {channel}");
                    continue;
                }

                // Islands are shared by all uses on this (slot, channel); SourceTexture = first use's texture.
                // 该（槽位,通道）的岛为所有引用共享；SourceTexture 取首个引用的贴图。
                var wrapList = new List<UVIsland>();
                var islands = MeshUVAnalyzer.ExtractIslands(mesh, slot, channel, kv.Value[0].Texture, _anim, renderer, _root, wrapList);
                foreach (var w in wrapList)
                {
                    _warnings.Add($"UV island on {renderer.name} slot {slot} ch{channel} crosses a wrap seam or exceeds [0,1]; treated as whitelist");
                }

                if (islands.Count == 0 && kv.Value.Count > 0)
                {
                    foreach (var u in kv.Value) MarkSkip(u, "no usable UV islands on this channel");
                    continue;
                }

                var group = new UVGroup { Renderer = renderer, SlotIndex = slot, Channel = channel, Islands = islands, Uses = kv.Value };
                group.AnyOptimizable = kv.Value.Any(u => !u.Skip);
                _ctx.UVGroups.Add(group);
            }
        }

        private TextureUse BuildUse(Renderer renderer, int slot, Material mat, TexturePropertyInfo prop, Texture2D tex,
            int channel, bool matWhitelisted, bool matCap, bool stBad)
        {
            var use = new TextureUse
            {
                Texture = tex,
                Kind = prop.Kind,
                Class = TextureClassifier.Classify(tex, prop.Kind, _decode),
                PropertyName = prop.PropertyName,
                UVChannel = channel,
                HasIdentityST = !stBad,
                Material = mat,
                SlotIndex = slot,
                AlphaMode = ResolveAlphaMode(mat),
            };
            if (mat.HasProperty("_Cutoff")) use.Cutoff = mat.GetFloat("_Cutoff");
            else if (mat.HasProperty("_AlphaCutoff")) use.Cutoff = mat.GetFloat("_AlphaCutoff");

            // Animation may change render mode / cutoff: take the strictest. 动画可能改渲染模式/Cutoff：取最严苛。
            if (_anim.TryGetSlotFloatRange(renderer.transform, _root, slot, "_Cutoff", out float cMin, out float cMax))
            {
                use.Cutoff = Mathf.Min(use.Cutoff, cMin);
            }

            if (matWhitelisted)
            {
                MarkSkip(use, $"material {mat.name} is whitelisted");
            }
            else if (_white.IsTextureWhitelisted(tex, null))
            {
                MarkSkip(use, $"texture {tex.name} is whitelisted");
            }
            else if (matCap)
            {
                MarkSkip(use, "MatCap/view-space UV (UVMode=4)");
            }
            else if (stBad)
            {
                MarkSkip(use, "ST transform present (static or animated)");
            }
            else if (prop.Kind == TextureKind.Unknown)
            {
                MarkSkip(use, "unknown texture property kind (unsupported shader usage)");
            }
            return use;
        }

        private static void MarkSkip(TextureUse use, string reason)
        {
            use.Skip = true;
            if (string.IsNullOrEmpty(use.SkipReason)) use.SkipReason = reason;
        }

        /// <summary>
        /// Best-effort alpha mode detection: lilToon _AlphaMaskMode/_UseAlphaMask & standard _Mode/_SrcBlend,
        /// plus import alpha presence. 尽力检测 alpha 模式。
        /// </summary>
        public static AlphaMode ResolveAlphaMode(Material mat)
        {
            if (mat == null) return AlphaMode.Opaque;
            if (mat.HasProperty("_AlphaMaskMode"))
            {
                int m = mat.GetInt("_AlphaMaskMode");
                if (m == 1 || m == 2) return AlphaMode.Cutout; // lilToon alpha mask modes. lilToon alpha 蒙版模式。
            }
            if (mat.HasProperty("_Cutoff")) return AlphaMode.Cutout;
            if (mat.HasProperty("_Mode"))
            {
                int mode = mat.GetInt("_Mode");
                // Standard shader: 1=cutout, 2=fade, 3=transparent. 标准着色器：1=Cutout，2/3=半透明。
                if (mode == 1) return AlphaMode.Cutout;
                if (mode == 2 || mode == 3) return AlphaMode.Blend;
            }
            if (mat.HasProperty("_SrcBlend") && mat.HasProperty("_DstBlend"))
            {
                int src = mat.GetInt("_SrcBlend"), dst = mat.GetInt("_DstBlend");
                // Transparent blend modes use dst != 0 with src alpha. 半透明混合模式 dst!=0。
                if (dst != 0 && src == 5) return AlphaMode.Blend; // SrcAlpha. 
            }
            if (mat.HasProperty("_Surface"))
            {
                int surface = mat.GetInt("_Surface"); // URP: 1 = transparent. URP：1=透明。
                if (surface == 1) return AlphaMode.Blend;
            }
            return AlphaMode.Opaque;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = renderer.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static bool IsEditorOnly(GameObject go)
        {
            var cur = go.transform;
            while (cur != null)
            {
                if (cur.CompareTag("EditorOnly")) return true;
                cur = cur.parent;
            }
            return false;
        }
    }
}

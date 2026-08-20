using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Turns material slots into the UV group graph: which geometry shares which UV layout, and
    ///     which textures are bound to it. Multi-channel UVs are split into independent groups, and any
    ///     reference the shader analyser cannot prove safe pushes its texture onto the whitelist.
    /// ZH: 把材质槽转换成 UV 组图：哪些几何体共享哪套 UV 布局，以及绑定到它的贴图有哪些。
    ///     多通道 UV 会被拆成独立的组；任何着色器分析器无法证明安全的引用都会把其贴图推入白名单。
    /// </summary>
    public sealed class UVGroupBuilder
    {
        private readonly ATOLog _log;
        private readonly WhitelistResolver _whitelist;
        private readonly AnimationFacts _anim;
        private readonly Transform _root;
        private readonly ShaderAnalyzer _shaders;

        /// <summary>EN: Every texture we saw, keyed by asset. ZH: 我们见到的所有贴图，按资产索引。</summary>
        public readonly Dictionary<Texture2D, AtoTexture> AllTextures = new Dictionary<Texture2D, AtoTexture>();

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public UVGroupBuilder(ATOLog log, WhitelistResolver whitelist, AnimationFacts anim, Transform root)
        {
            _log = log;
            _whitelist = whitelist;
            _anim = anim;
            _root = root;
            _shaders = new ShaderAnalyzer(log);
        }

        /// <summary>EN: Build the groups. ZH: 构建 UV 组。</summary>
        public List<UVGroup> Build(List<SlotRecord> slots)
        {
            var byKey = new Dictionary<(Mesh, int, int), UVGroup>();
            int nextId = 0;

            foreach (var slot in slots)
            {
                var animatedProps = new HashSet<string>(_anim.PropsFor(slot.Path), StringComparer.Ordinal);

                foreach (var mat in slot.Materials)
                {
                    var verdict = _shaders.Analyze(mat, animatedProps);
                    if (verdict.ShaderUnanalysable)
                    {
                        _log.Warn(ATOLocalizer.Tr("ato.warn.shaderUnknown",
                            mat != null && mat.shader != null ? mat.shader.name : "<null>"));
                        MarkMaterialWhitelisted(mat);
                        continue;
                    }

                    foreach (var pv in verdict.Properties.Values)
                    {
                        if (pv.Texture == null) continue;

                        var tex = GetOrCreate(pv.Texture, pv.Slot, verdict);
                        if (!pv.Safe)
                        {
                            // EN: Anything we cannot prove safe is treated exactly like a whitelisted
                            //     texture, which is the fail-safe direction.
                            // ZH: 任何无法证明安全的情况都完全按白名单贴图处理，这是安全的失效方向。
                            tex.Whitelisted = true;
                            _log.Warn(ATOLocalizer.Tr("ato.warn.textureTransform",
                                pv.Texture.name, mat.name) + $" ({pv.UnsafeReason})");
                            continue;
                        }

                        var key = (slot.Mesh, slot.Index, pv.UvChannel);
                        if (!byKey.TryGetValue(key, out var group))
                        {
                            byKey[key] = group = new UVGroup { Id = nextId++ };
                        }

                        if (!group.Bindings.Any(b => b.Renderer == slot.Renderer && b.SubMesh == slot.Index))
                            group.Bindings.Add(new MeshBinding(slot.Renderer, slot.Index, pv.UvChannel));

                        group.AddTexture(pv.Slot, tex);
                        group.Usages.Add(new TextureUsage
                        {
                            Material = mat,
                            PropertyName = pv.Property,
                            Texture = tex,
                            Slot = pv.Slot,
                            UvChannel = pv.UvChannel,
                            AlphaMode = verdict.AlphaMode,
                            Cutoff = verdict.Cutoff,
                        });
                    }
                }
            }

            // EN: Textures that animation can assign directly to a property join the group of the UV
            //     they will be sampled with, so their islands share the same layout.
            // ZH: 动画可直接赋给某属性的贴图会并入其将被采样的 UV 所在的组，
            //     使它们的岛共享同一布局。
            foreach (var group in byKey.Values)
            {
                foreach (var animTex in _anim.AnimatedTextures)
                {
                    if (animTex == null) continue;
                    foreach (var slot in group.Textures.Keys.ToList())
                    {
                        var reference = group.Textures[slot].FirstOrDefault();
                        if (reference == null) continue;
                        if (animTex.width != reference.Width || animTex.height != reference.Height) continue;

                        var t = GetOrCreate(animTex, slot, null);
                        group.AddTexture(slot, t);
                    }
                }
            }

            var groups = byKey.Values.ToList();
            foreach (var g in groups)
            {
                g.FullyWhitelisted = g.Textures.SelectMany(kv => kv.Value).All(t => t.Representative.Whitelisted);
                if (g.FullyWhitelisted)
                {
                    g.SkipAtlas = true;
                    g.SkipReason = "all bound textures are whitelisted";
                }
                else if (g.Textures.SelectMany(kv => kv.Value).Any(t => t.Representative.Whitelisted))
                {
                    // EN: A whitelisted texture sharing a UV forces the whole group out of atlasing, but
                    //     the other textures may still be rescaled and re-imported.
                    // ZH: 共享同一 UV 的白名单贴图会让整组退出图集化，
                    //     但其他贴图仍可参与整图缩放与导入参数优化。
                    g.SkipAtlas = true;
                    g.SkipReason = "shares a UV with a whitelisted texture";
                }
            }

            return groups;
        }

        /// <summary>EN: Build islands for every group and derive the shared layout size. ZH: 为所有组构建岛并推导共享布局尺寸。</summary>
        public void BuildIslands(List<UVGroup> groups, ATOProgress progress)
        {
            int i = 0;
            foreach (var group in groups)
            {
                progress.ThrowIfCancelled();
                progress.Report(0.28f + 0.07f * (++i / (float)groups.Count));
                if (group.Bindings.Count == 0) continue;

                // EN: Bucket effect: the shared layout is authored against the largest source size in
                //     the group and can never exceed it. Computed first, because the whole-texture
                //     fallback path needs it even for groups that fail to atlas.
                // ZH: 木桶效应：共享布局以组内最大的源尺寸为基准，且绝不超过它。
                //     先算它，因为即使某组无法图集化，整图缩放回退路径也需要这个值。
                int lw = 1, lh = 1;
                foreach (var t in group.Textures.SelectMany(kv => kv.Value))
                {
                    var rep = t.Representative;
                    lw = Mathf.Max(lw, rep.Width);
                    lh = Mathf.Max(lh, rep.Height);
                }
                group.LayoutSize = new Vector2Int(lw, lh);

                var binding = group.Bindings[0];
                var mesh = MeshOf(binding.Renderer);
                if (mesh == null) { group.SkipAtlas = true; group.SkipReason = "no mesh"; continue; }

                float scale = group.Bindings
                    .Select(b => UVIslandBuilder.WorstCaseWorldScale(b.Renderer, _root, _anim))
                    .DefaultIfEmpty(1f).Max();

                var set = UVIslandBuilder.Build(mesh, binding.SubMesh, binding.UvChannel, scale, _log);
                if (set.Unsafe)
                {
                    group.SkipAtlas = true;
                    group.SkipReason = set.UnsafeReason;
                    _log.Warn(ATOLocalizer.Tr("ato.warn.uvOutOfRange", mesh.name, binding.SubMesh));
                    continue;
                }

                group.Islands.AddRange(set.Islands);
                foreach (var island in group.Islands) island.Group = group;
            }
        }

        private static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            return r != null && r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
        }

        private void MarkMaterialWhitelisted(Material mat)
        {
            if (mat == null || mat.shader == null) return;
            int n = mat.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (mat.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                if (mat.GetTexture(mat.shader.GetPropertyName(i)) is Texture2D t)
                    GetOrCreate(t, TextureSlot.Other, null).Whitelisted = true;
            }
        }

        private AtoTexture GetOrCreate(Texture2D tex, TextureSlot slot, MaterialVerdict verdict)
        {
            if (AllTextures.TryGetValue(tex, out var existing)) return existing;

            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            bool srgb = importer?.sRGBTexture ?? (slot == TextureSlot.Color || slot == TextureSlot.Emission);
            if (slot == TextureSlot.Normal) srgb = false;

            var t = new AtoTexture
            {
                Source = tex,
                Width = tex.width,
                Height = tex.height,
                SRGB = srgb,
                Filter = tex.filterMode,
                Wrap = tex.wrapMode,
                AnisoLevel = tex.anisoLevel,
                Class = slot == TextureSlot.Normal ? TextureClass.Normal
                      : (slot == TextureSlot.Mask || slot == TextureSlot.Other) ? TextureClass.Grayscale
                      : TextureClass.OpaqueColor,
                Whitelisted = _whitelist.Textures.Contains(tex),
            };

            AllTextures[tex] = t;
            return t;
        }
    }
}

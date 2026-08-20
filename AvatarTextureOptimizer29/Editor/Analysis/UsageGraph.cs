// Usage graph construction: whitelist expansion -> texture dedup -> material cloning
// -> TexUse analysis (static + animated) -> per-texture classification.
// 使用图构建：白名单扩展 → 贴图去重 → 材质克隆 → 引用分析（静态+动画）→ 贴图分类。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    internal static class UsageGraph
    {
        internal static void Build(AtoSession s)
        {
            using var _ = ATOLog.Scope("BuildUsageGraph");

            // 0) per-path animated material props (for ShaderAnalyzer) / 每路径动画材质属性
            foreach (var kv in s.anim.renderers)
            {
                var props = new AnimatedMatProps();
                foreach (var f in kv.Value.floatProps)
                    props.floatValues[f.Key] = f.Value;
                foreach (var o in kv.Value.textureProps)
                    props.objectValues[o.Key] = o.Value;
                s.matAnim[kv.Key] = props;
            }

            var whitelistTextures = ExpandWhitelist(s);

            // 1) raw texture enumeration for dedup / 原始贴图枚举供去重
            var rawTextures = EnumerateRawTextures(s);

            // 2) texInfo shells / 建立贴图信息壳
            foreach (var t in rawTextures)
                if (t != null && !s.texInfos.ContainsKey(t))
                    s.texInfos[t] = new TexInfo { texture = t };

            // 3) dedup by content+import / 去重
            TextureDedup.Run(s, whitelistTextures);

            // 4) clone materials & apply dedup map to references / 克隆材质并对引用应用去重
            if (s.component.dedupTextures && s.textureDedupMap.Count > 0)
                ApplyTextureDedupToReferences(s);

            // 5) full usage analysis / 完整引用分析
            foreach (var ri in s.renderers)
            {
                s.matAnim.TryGetValue(ri.path, out var matProps);
                for (int slot = 0; slot < ri.slotMaterials.Count; slot++)
                {
                    foreach (var mat in ri.slotMaterials[slot])
                    {
                        var m = ResolveClone(s, mat);
                        foreach (var use in ShaderAnalyzer.Analyze(ri.renderer, slot, m, matProps))
                            AddUse(s, use);
                    }
                }

                // animated texture-property swaps add textures on this renderer's slots
                // 动画贴图属性切换：该渲染器所有槽位（保守处理，去重由 AddUse 保证）
                if (s.anim.renderers.TryGetValue(ri.path, out var rAnim))
                    foreach (var kv in rAnim.textureProps)
                        AddAnimatedTextureUses(s, ri, kv.Key, kv.Value);
            }

            // 6) classification / 分类
            Classify(s, whitelistTextures);

            int wl = s.texInfos.Values.Count(t => t.whitelisted);
            ATOLog.Info($"usage graph: {s.texInfos.Count} textures, {wl} whitelisted, " +
                        $"{s.texInfos.Values.Count(t => t.eligibleForAtlas)} atlas-eligible");
        }

        // ------------------------------------------------------------------
        private static void AddUse(AtoSession s, TexUse use)
        {
            // dedup resolution / 去重解析
            if (s.textureDedupMap.TryGetValue(use.texture, out var canonical)) use.texture = canonical;

            if (!s.texInfos.TryGetValue(use.texture, out var info))
                s.texInfos[use.texture] = info = new TexInfo { texture = use.texture };
            info.uses.Add(use);
        }

        private static void AddAnimatedTextureUses(AtoSession s, RendererInfo ri, string binding, List<Object> values)
        {
            string prop = binding.StartsWith("material.") ? binding.Substring(9) : binding;
            foreach (var v in values)
            {
                if (!(v is Texture2D tex)) continue;
                foreach (var mat in ri.slotMaterials.SelectMany(x => x))
                {
                    var m = ResolveClone(s, mat);
                    if (m == null) continue;
                    var rule = m.shader != null ? ShaderCatalog.Resolve(m.shader, prop) : null;
                    var alpha = ShaderAnalyzer.DetectAlphaMode(m, out float cutoff);
                    AddUse(s, new TexUse
                    {
                        renderer = ri.renderer, slot = -1, material = m, prop = prop, texture = tex,
                        kind = rule?.kind ?? TexKind.Special,
                        uvChannel = rule != null && rule.uv == UvMode.UvSelector ? 0 : 0,
                        alpha = alpha, cutoff = cutoff,
                        specialUse = rule == null || rule.uv == UvMode.NonMeshUv,
                    });
                }
            }
        }

        private static HashSet<Texture2D> ExpandWhitelist(AtoSession s)
        {
            var set = new HashSet<Texture2D>();
            foreach (var obj in s.component.whitelist)
            {
                if (obj == null) continue;
                CollectWhitelistTextures(s, obj, set);
            }

            return set;
        }

        private static void CollectWhitelistTextures(AtoSession s, Object obj, HashSet<Texture2D> set)
        {
            try
            {
                // CollectDependencies handles every object type (material/clip/SO/renderer...).
                // CollectDependencies 覆盖任意对象类型的引用闭包。
                var deps = UnityEditor.EditorUtility.CollectDependencies(new[] { obj });
                foreach (var d in deps)
                    if (d is Texture2D t && !ShaderAnalyzer.IsBuiltinTex(t))
                        set.Add(t);

                // GameObject: include children. / GameObject 含子级。
                if (obj is GameObject go)
                    foreach (var r in go.GetComponentsInChildren<Transform>(true))
                        foreach (var d in UnityEditor.EditorUtility.CollectDependencies(new Object[] { r }))
                            if (d is Texture2D t && !ShaderAnalyzer.IsBuiltinTex(t))
                                set.Add(t);

                // Mesh: whitelist textures of renderers using it. / 网格：使用它的渲染器的贴图。
                if (obj is Mesh mesh)
                    foreach (var ri in s.renderers)
                        if (ri.mesh == mesh)
                            foreach (var mats in ri.slotMaterials)
                                foreach (var m in mats)
                                    foreach (var d in UnityEditor.EditorUtility.CollectDependencies(new Object[] { m }))
                                        if (d is Texture2D t && !ShaderAnalyzer.IsBuiltinTex(t))
                                            set.Add(t);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"whitelist expansion failed for {obj.name}: {e.Message}");
            }
        }

        private static HashSet<Texture2D> EnumerateRawTextures(AtoSession s)
        {
            var set = new HashSet<Texture2D>();
            foreach (var ri in s.renderers)
            {
                foreach (var mat in ri.slotMaterials.SelectMany(x => x))
                {
                    if (mat == null || mat.shader == null) continue;
                    for (int i = 0; i < mat.shader.GetPropertyCount(); i++)
                    {
                        if (mat.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                        if (mat.GetTexture(mat.shader.GetPropertyName(i)) is Texture2D t)
                            set.Add(t);
                    }
                }

                if (s.anim.renderers.TryGetValue(ri.path, out var rAnim))
                {
                    foreach (var list in rAnim.slotMaterials.Values)
                        foreach (var o in list)
                            if (o is Material m && m.shader != null)
                                for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                                {
                                    if (m.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                                    if (m.GetTexture(m.shader.GetPropertyName(i)) is Texture2D t)
                                        set.Add(t);
                                }

                    foreach (var o in rAnim.textureProps.Values.SelectMany(x => x))
                        if (o is Texture2D t) set.Add(t);
                }
            }

            set.RemoveWhere(ShaderAnalyzer.IsBuiltinTex);
            return set;
        }

        private static void ApplyTextureDedupToReferences(AtoSession s)
        {
            // materials on renderers (clone or temp -> safe to edit) / 渲染器上的材质（克隆或临时资产，可安全修改）
            foreach (var ri in s.renderers)
            {
                bool changed = false;
                var arr = ri.renderer.sharedMaterials;
                for (int i = 0; i < arr.Length; i++)
                {
                    var m = ResolveClone(s, arr[i]);
                    if (m != arr[i]) { arr[i] = m; changed = true; }
                    if (m == null) continue;
                    changed |= ReplaceMaterialTextures(m, s.textureDedupMap);
                }

                if (changed) ri.renderer.sharedMaterials = arr;
            }

            // animation texture curves / 动画贴图曲线
            AnimationAnalyzer.ReplaceTextures(s, s.textureDedupMap);
        }

        private static bool ReplaceMaterialTextures(Material m, Dictionary<Texture2D, Texture2D> map)
        {
            if (m == null || m.shader == null) return false;
            bool changed = false;
            for (int i = 0; i < m.shader.GetPropertyCount(); i++)
            {
                if (m.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                string prop = m.shader.GetPropertyName(i);
                if (m.GetTexture(prop) is Texture2D t && map.TryGetValue(t, out var nt) && nt != t)
                {
                    m.SetTexture(prop, nt);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>Get (or create) the avatar-local clone of a material. NDMF temporaries stay.
        /// 取（或创建）材质的 Avatar 本地克隆；NDMF 临时资产直接复用。</summary>
        internal static Material ResolveClone(AtoSession s, Material mat)
        {
            if (mat == null) return null;
            if (s.materialCloneMap.TryGetValue(mat, out var c)) return c;
            if (s.ctx.IsTemporaryAsset(mat))
            {
                s.materialCloneMap[mat] = mat;
                return mat;
            }

            c = UnityEngine.Object.Instantiate(mat);
            c.name = mat.name + "(ATO)";
            s.ctx.AssetSaver.SaveAsset(c);
            s.materialCloneMap[mat] = c;
            ATOLog.DebugL($"cloned material {mat.name} -> {c.name}");
            return c;
        }

        /// <summary>Push material clone map into renderers & animations. / 将克隆映射写回渲染器与动画。</summary>
        internal static void ApplyMaterialMap(AtoSession s)
        {
            if (s.materialCloneMap.Count == 0) return;
            var effective = s.materialCloneMap.Where(kv => kv.Value != kv.Key)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (effective.Count == 0) return;

            foreach (var ri in s.renderers)
            {
                var arr = ri.renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && effective.TryGetValue(arr[i], out var nm) && nm != arr[i])
                    {
                        arr[i] = nm;
                        changed = true;
                    }

                if (changed) ri.renderer.sharedMaterials = arr;
            }

            AnimationAnalyzer.ReplaceMaterials(s, effective);
        }

        // ------------------------------------------------------------------
        private static void Classify(AtoSession s, HashSet<Texture2D> whitelistTextures)
        {
            var warnedShaders = new HashSet<Shader>();

            foreach (var kv in s.texInfos)
            {
                var info = kv.Value;
                var tex = kv.Key;

                if (whitelistTextures.Contains(tex))
                {
                    info.whitelisted = true;
                    info.whiteReason = "user whitelist";
                    continue;
                }

                // unknown shaders -> whitelist + warn (once per shader) / 未知着色器→白名单+警告
                bool violated = false;
                string reason = null;
                foreach (var u in info.uses)
                {
                    if (u.specialUse) { violated = true; reason = $"special use via {u.prop}"; break; }
                    if (u.stTransformed) { violated = true; reason = $"UV transform via {u.prop}"; break; }
                    var sh = u.material.shader;
                    if (sh != null && !ShaderCatalog.IsLilToon(sh) && !IsKnownStandardShader(sh))
                    {
                        violated = true;
                        reason = $"unknown shader {sh.name}";
                        if (warnedShaders.Add(sh))
                            s.warnings.Add(string.Format(ATOL10n.Get("warn.unknownShader"), sh.name));
                        break;
                    }
                }

                if (violated)
                {
                    info.whitelisted = true;
                    info.whiteReason = reason ?? "ineligible";
                    if (reason != null && reason.StartsWith("UV transform"))
                        s.warnings.Add(string.Format(ATOL10n.Get("warn.stAnimated"), tex.name));
                    continue;
                }

                // kind strictness: Normal > GrayMask > Color / 类别取最严苛
                info.category = ClassifyCategory(info);

                // content flags / 内容标志
                var cp = TexturePixels.Get(tex, info.category == AtoTexCategory.Normal);
                if (cp == null)
                {
                    info.whitelisted = true;
                    info.whiteReason = "pixel readback failed";
                    continue;
                }

                info.hasAlphaContent = TexturePixels.HasAlpha(cp);
                if (info.category == AtoTexCategory.Opaque && info.hasAlphaContent)
                    info.category = AtoTexCategory.Alpha;

                if (info.category == AtoTexCategory.Gray)
                {
                    info.isGrayscaleContent = TexturePixels.IsGrayscale(cp);
                    foreach (var u in info.uses.Where(u => u.kind == TexKind.GrayMask))
                    foreach (var ch in GrayChannelsOf(u))
                        info.usedChannels.Add(ch);
                }

                // content channels (format-safety fallback needs this) / 实际有变化的通道（格式安全兜底）
                var significance = TexturePixels.ChannelSignificance(cp);
                for (byte i = 0; i < 4; i++)
                    if (significance[i])
                        info.contentChannels.Add(i);

                info.eligibleForAtlas = info.uses.Count > 0;
            }
        }

        internal static bool IsKnownStandardShader(Shader shader)
        {
            // shaders whose texture props we fully trust via flags/names / 可被通用分析完全信任的着色器
            string n = shader.name;
            return n.StartsWith("Standard") || n.StartsWith("Universal Render Pipeline/Lit")
                || n.StartsWith("Universal Render Pipeline/Simple Lit") || n.StartsWith("Unlit")
                || n.StartsWith("Universal Render Pipeline/Unlit") || n.Contains("lilToon");
        }

        private static AtoTexCategory ClassifyCategory(TexInfo info)
        {
            bool normal = false, gray = false, color = false;
            foreach (var u in info.uses)
            {
                switch (u.kind)
                {
                    case TexKind.Normal: normal = true; break;
                    case TexKind.GrayMask: gray = true; break;
                    case TexKind.Color: color = true; break;
                }
            }

            if (normal) return AtoTexCategory.Normal;
            if (!color && gray) return AtoTexCategory.Gray;
            return color && gray ? AtoTexCategory.Gray : AtoTexCategory.Opaque; // mixed color+gray: gray metrics are stricter / 混用按灰度（更严）
        }

        private static IEnumerable<byte> GrayChannelsOf(TexUse u)
        {
            var rule = ShaderCatalog.Resolve(u.material.shader, u.prop);
            if (rule?.grayChannels == null) yield break;
            foreach (var ch in rule.grayChannels)
                yield return ch == "r" ? (byte)0 : ch == "g" ? (byte)1 : ch == "b" ? (byte)2 : (byte)3;
        }
    }
}

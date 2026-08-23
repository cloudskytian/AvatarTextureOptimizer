// Avatar analysis: gathers renderers, material slots, textures (deduped), animation facts,
// applies the whitelist, and builds UV groups + islands.
// / Avatar 分析：收集渲染器、材质槽、贴图（去重）、动画要素，应用白名单，构建 UV 组与岛。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.runtime;
using net.fosa.avatar_texture_optimizer.editor.pipeline;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>
    /// Main analysis pass. / 主分析阶段。
    /// </summary>
    public static class AvatarAnalyzer
    {
        /// <summary>Run the analysis. / 执行分析。</summary>
        public static AnalysisResult Analyze(Transform avatarRoot, AvatarTextureOptimizer component)
        {
            var result = new AnalysisResult();
            var facts = AnimationScanner.Scan(avatarRoot);
            result.Facts = facts;
            var deduper = new TextureDeduper();

            // Resolve whitelist into a set of textures / 把白名单解析为贴图集合
            var whitelistTextures = ResolveWhitelist(component, avatarRoot, result.Warnings);
            var whitelistMeshes = new HashSet<Mesh>();
            var whitelistRenderers = new HashSet<Renderer>();
            foreach (var entry in component.whitelist)
            {
                if (entry.target == null) continue;
                if (entry.target is Mesh m) whitelistMeshes.Add(m);
                else if (entry.target is Renderer r) whitelistRenderers.Add(r);
                else if (entry.target is GameObject go)
                {
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true)) whitelistRenderers.Add(r);
                }
            }

            // Collect renderers / 收集渲染器
            var renderers = new List<Renderer>();
            renderers.AddRange(avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            renderers.AddRange(avatarRoot.GetComponentsInChildren<MeshRenderer>(true));

            // Group cache for meshes (blend shapes etc. computed once) / 网格数据缓存
            var meshDataCache = new Dictionary<(Mesh, int), MeshData>();

            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.gameObject == null) continue;
                if (renderer.CompareTag("EditorOnly")) continue;
                if (whitelistRenderers.Contains(renderer)) continue;

                // Only process enabled or animation-enabled renderers / 只处理被启用或动画启用的渲染器
                bool animatedActive = IsAnimatedActive(renderer, avatarRoot, facts);
                if (!renderer.enabled && !animatedActive) continue;

                Mesh mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : renderer is MeshRenderer mr ? (mr.GetComponent<MeshFilter>() != null ? mr.GetComponent<MeshFilter>().sharedMesh : null)
                    : null;
                if (mesh == null) continue;
                if (whitelistMeshes.Contains(mesh)) continue;

                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0) continue;

                // Renderer path facts / 渲染器路径要素
                string pathFromAvatar = RelativePath(avatarRoot, renderer.transform);
                float maxScale = facts.MaxScaleFor(pathFromAvatar);
                foreach (var animRoot in avatarRoot.GetComponentsInChildren<Animator>(true))
                {
                    maxScale = Mathf.Max(maxScale, facts.MaxScaleFor(RelativePath(animRoot.transform, renderer.transform)));
                }

                var usage = new MeshUsage
                {
                    Renderer = renderer,
                    Mesh = mesh,
                    Skinned = renderer is SkinnedMeshRenderer,
                    Transform = renderer.transform,
                    MaxAnimatedScale = maxScale,
                    AnimatedActive = animatedActive,
                };
                result.Meshes.Add(usage);

                int submeshCount = Mathf.Min(materials.Length, mesh.subMeshCount > 0 ? mesh.subMeshCount : materials.Length);
                for (int sub = 0; sub < submeshCount; sub++)
                {
                    var mat = materials[sub];
                    if (mat == null) continue;

                    var slot = new MeshSlot { SubMeshIndex = sub, Material = mat };
                    usage.Slots.Add(slot);

                    var props = ShaderAnalyzer.Analyze(mat);
                    foreach (var prop in props)
                    {
                        bool unsafeForSampling = prop.HasSTTransform || prop.SpecialPurpose != null;
                        bool animatedSt = IsAnimatedSt(facts, renderer, sub, prop.Name);
                        bool whitelisted = unsafeForSampling || animatedSt ||
                                           whitelistTextures.Contains(prop.Texture) ||
                                           whitelistRenderers.Contains(renderer);

                        var binding = new TexBinding
                        {
                            Material = mat,
                            PropertyName = prop.Name,
                            Role = prop.Role,
                            Texture = prop.Texture,
                            UvChannel = 0,
                            Animated = false,
                        };
                        ApplyTransparency(mat, prop.Name, facts, renderer, sub, binding);

                        var record = deduper.GetOrCreate(prop.Texture);
                        record.Bindings.Add(binding);
                        if (whitelisted) record.Whitelisted = true;

                        slot.Bindings.Add(binding);
                    }

                    // Animation-switched textures on this slot / 该材质槽的动画切换贴图
                    foreach (var texRef in facts.TextureRefs)
                    {
                        if (texRef.SlotIndex != sub) continue;
                        if (!PathMatches(renderer, texRef.Path, avatarRoot)) continue;
                        if (whitelistTextures.Contains(texRef.Texture)) continue;

                        var binding = new TexBinding
                        {
                            Material = mat,
                            PropertyName = texRef.PropertyName,
                            Role = ShaderAnalyzer.ClassifyRole(texRef.PropertyName),
                            Texture = texRef.Texture,
                            UvChannel = 0,
                            Animated = true,
                        };
                        ApplyTransparency(mat, texRef.PropertyName, facts, renderer, sub, binding);
                        var record = deduper.GetOrCreate(texRef.Texture);
                        record.Bindings.Add(binding);
                        slot.Bindings.Add(binding);
                    }
                }

                // Build UV groups / 构建 UV 组
                int channelCount = component.processAllUvChannels ? 8 : 1;
                for (int ch = 0; ch < channelCount; ch++)
                {
                    if (!meshDataCache.TryGetValue((mesh, ch), out var md))
                    {
                        md = MeshData.Load(mesh, ch, renderer.transform);
                        if (md != null) meshDataCache[(mesh, ch)] = md;
                    }
                    if (md == null) continue;

                    var group = new UVGroup { Id = result.UvGroups.Count, Mesh = usage, UvChannel = ch };
                    result.UvGroups.Add(group);

                    var islands = IslandExtractor.Extract(md, group.Id);
                    // Animated scale affects world area / 动画缩放影响世界面积
                    if (maxScale > 1f)
                    {
                        foreach (var iso in islands)
                        {
                            iso.WorldArea *= maxScale * maxScale;
                            iso.WorldSize = Mathf.Sqrt(iso.WorldArea);
                        }
                    }
                    IslandExtractor.MergeOverlaps(islands);
                    group.Islands.AddRange(islands);

                    // Bind textures to this group / 把贴图绑定到该组
                    var seen = new HashSet<TexRecord>();
                    foreach (var slot in usage.Slots)
                    {
                        foreach (var b in slot.Bindings)
                        {
                            if (b.UvChannel != ch) continue;
                            if (!seen.Add(deduper.FindRecord(b.Texture))) continue;
                            AddGroupTexture(group, deduper, b.Texture, b.Role, b.Animated);
                        }
                    }

                    if (group.Textures.Count == 0)
                    {
                        // no textures on this channel -> nothing to optimize / 该通道无贴图
                        result.UvGroups.RemoveAt(result.UvGroups.Count - 1);
                        continue;
                    }
                }
            }

            // Finalize records / 收尾记录
            foreach (var record in deduper.AllRecords)
            {
                result.Textures.Add(record);
                if (record.Whitelisted) result.WhitelistedTextureCount++;
            }

            // Whitelisted involvement -> mark groups / 涉及白名单 → 标记组
            foreach (var group in result.UvGroups)
            {
                foreach (var gt in group.Textures)
                {
                    if (gt.Record.Whitelisted)
                    {
                        group.Whitelisted = true;
                        break;
                    }
                }
            }

            return result;
        }

        private static bool IsAnimatedActive(Renderer renderer, Transform avatarRoot, AnimationFacts facts)
        {
            if (facts.AnimatedActiveObjects.Count == 0) return false;
            string path = RelativePath(avatarRoot, renderer.transform);
            if (facts.AnimatedActiveObjects.Contains(path)) return true;
            foreach (var animRoot in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (facts.AnimatedActiveObjects.Contains(RelativePath(animRoot.transform, renderer.transform))) return true;
            }
            return false;
        }

        private static bool IsAnimatedSt(AnimationFacts facts, Renderer renderer, int slot, string propName)
        {
            foreach (var s in facts.StRefs)
            {
                if (s.SlotIndex == slot &&
                    string.Equals(s.PropertyName, propName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Apply render-mode / cutoff info (incl. animation extremes) to a binding. / 把渲染模式/Cutoff（含动画极值）应用到绑定。</summary>
        private static void ApplyTransparency(Material mat, string propName, AnimationFacts facts,
            Renderer renderer, int slot, TexBinding binding)
        {
            bool cutout = false, blend = false;
            float cutoff = 0.5f;

            if (mat.HasProperty("_Mode"))
            {
                int mode = (int)mat.GetFloat("_Mode");
                cutout = mode == 1;      // Opaque=0, Cutout=1, Fade=2, Transparent=3 / 常见枚举
                blend = mode == 2 || mode == 3;
            }
            if (mat.HasProperty("_Cutoff")) cutoff = mat.GetFloat("_Cutoff");
            // lilToon render mode / lilToon 渲染模式
            if (mat.HasProperty("_TransparentMode")) blend = mat.GetFloat("_TransparentMode") > 0;

            // Animation extremes / 动画极值（取最严苛）
            foreach (var p in facts.MaterialProps)
            {
                if (p.SlotIndex != slot) continue;
                if (p.PropertyName == "_Cutoff") cutoff = Mathf.Min(cutoff, p.MinValue);
                if (p.PropertyName == "_Mode" && p.MaxValue >= 2f) blend = true;
                if (p.PropertyName == "_Mode" && p.MinValue == 1f) cutout = true;
            }

            binding.TransparentCutout = cutout;
            binding.TransparentBlend = blend;
            binding.Cutoff = cutoff;
        }

        private static bool PathMatches(Renderer renderer, string animPath, Transform avatarRoot)
        {
            if (string.IsNullOrEmpty(animPath)) return false;
            if (RelativePath(avatarRoot, renderer.transform) == animPath) return true;
            foreach (var animRoot in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (RelativePath(animRoot.transform, renderer.transform) == animPath) return true;
            }
            return false;
        }

        /// <summary>Relative path from root to target (Unity animation path style). / root 到目标的相对路径（Unity 动画路径风格）。</summary>
        public static string RelativePath(Transform root, Transform target)
        {
            if (root == target) return "";
            var parts = new List<string>();
            var t = target;
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            if (t == null) return "";
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void AddGroupTexture(UVGroup group, TextureDeduper deduper, Texture2D tex,
            TextureRole role, bool animated)
        {
            var record = deduper.FindRecord(tex);
            foreach (var gt in group.Textures)
            {
                if (gt.Record == record)
                {
                    if (!gt.Roles.Contains(role)) gt.Roles.Add(role);
                    return;
                }
            }
            var g = new GroupTexture
            {
                Record = record,
                Role = role,
                SourceTexture = tex,
                Roles = { role },
            };
            group.Textures.Add(g);
        }

        /// <summary>Resolve whitelist entries into a set of textures. / 把白名单条目解析为贴图集合。</summary>
        private static HashSet<Texture2D> ResolveWhitelist(AvatarTextureOptimizer component, Transform avatarRoot,
            List<string> warnings)
        {
            var set = new HashSet<Texture2D>();
            foreach (var entry in component.whitelist)
            {
                if (entry.target == null) continue;
                if (entry.target is Texture2D t2d)
                {
                    set.Add(t2d);
                }
                else if (entry.target is Material mat)
                {
                    var props = ShaderAnalyzer.Analyze(mat);
                    foreach (var p in props) set.Add(p.Texture);
                }
                else if (entry.target is Mesh mesh)
                {
                    foreach (var r in avatarRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        var m = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                            : r is MeshRenderer mr && mr.GetComponent<MeshFilter>() != null ? mr.GetComponent<MeshFilter>().sharedMesh : null;
                        if (m == mesh)
                        {
                            foreach (var sm in r.sharedMaterials)
                            {
                                if (sm == null) continue;
                                foreach (var p in ShaderAnalyzer.Analyze(sm)) set.Add(p.Texture);
                            }
                        }
                    }
                }
                else if (entry.target is Renderer r2)
                {
                    foreach (var sm in r2.sharedMaterials)
                    {
                        if (sm == null) continue;
                        foreach (var p in ShaderAnalyzer.Analyze(sm)) set.Add(p.Texture);
                    }
                }
                else if (entry.target is AnimationClip clip)
                {
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                        if (curve == null) continue;
                        foreach (var f in curve)
                        {
                            if (f.value is Texture2D t) set.Add(t);
                            if (f.value is Material mm)
                            {
                                foreach (var p in ShaderAnalyzer.Analyze(mm)) set.Add(p.Texture);
                            }
                        }
                    }
                }
                else
                {
                    warnings.Add("Whitelist entry type '" + entry.target.GetType().Name +
                                 "' is not handled; referenced textures may still be optimized. / 白名单条目类型未处理。");
                }
            }
            return set;
        }
    }
}

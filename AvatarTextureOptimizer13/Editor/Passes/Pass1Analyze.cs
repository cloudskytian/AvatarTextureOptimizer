// ATO — Avatar Texture Optimizer
// Pass 1 — analysis: collects renderers / materials / textures, incorporates animation
// swaps, applies the whitelist, dedups textures, and builds UV groups, texture type
// groups and UV islands.
// Pass 1——分析：收集渲染器/材质/贴图，纳入动画切换，应用白名单，贴图去重，
// 构建 UV 组、贴图类型组与 UV 岛。

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pass 1 — analysis. Pass 1——分析。
    /// </summary>
    public class Pass1Analyze : ATOBasePass<Pass1Analyze>
    {
        protected override void Process(ATOBuildContext bc, nadena.dev.ndmf.BuildContext context)
        {
            var result = bc.Result;
            if (result == null) return; // no component

            RunStage(bc, ATOI18nKeys.StageAnalyze, 4, () =>
            {
                var avatarRoot = context.AvatarRootObject;

                // 1. Collect renderers and material usages. 1. 收集渲染器与材质用途。
                CollectRenderersAndUsages(avatarRoot, result);

                // 2. Animation analysis + animation-introduced textures. 2. 动画分析 + 动画引入贴图。
                AnimationAnalyzer.Analyze(avatarRoot, result);
                CollectAnimationIntroduced(avatarRoot, result);

                // 3. Whitelist. 3. 白名单。
                WhitelistResolver.Resolve(result.component, result);

                // 4. Build texture refs (group usages by texture) then dedup.
                // 4. 构建贴图引用（按贴图分组合并用途）再去重。
                result.textures = BuildTextureRefs(result);
                result.textures = Deduplicator.DedupTextures(result.textures);

                // 5. Build UV groups + type groups. 5. 构建 UV 组与类型组。
                BuildGroups(result);

                // 6. Extract islands. 6. 提取 UV 岛。
                ExtractIslands(result);

                // 7. Strictest alpha modes / cutoffs. 7. 最严苛透明模式 / Cutoff。
                ResolveAlphaModes(result);

                result.didAnything = result.textures.Any(t => !t.whitelisted);
                ATOLog.Info($"[Analysis] renderers used, usages={result.allUsages.Count}, " +
                            $"textures={result.textures.Count}, uvGroups={result.uvGroups.Count}, " +
                            $"typeGroups={result.typeGroups.Count}.");
            });
        }

        // ------------------------------------------------------------------

        private void CollectRenderersAndUsages(GameObject avatarRoot, ATOAnalysisResult result)
        {
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is not (SkinnedMeshRenderer or MeshRenderer)) continue;
                if (renderer.gameObject.CompareTag("EditorOnly")) continue;

                bool inUse = renderer.enabled || renderer.gameObject.activeInHierarchy;
                if (!inUse) continue; // animated-enable handled after animation analysis

                var mats = renderer.sharedMaterials;
                for (int slot = 0; slot < mats.Length; slot++)
                {
                    var mat = mats[slot];
                    if (mat == null) continue;
                    AddUsagesForSlot(result, renderer, mat, slot, fromAnimation: false);
                }
            }
        }

        private void AddUsagesForSlot(ATOAnalysisResult result, Renderer renderer, Material mat, int slot, bool fromAnimation)
        {
            var infos = ShaderPropertyAnalyzer.Analyze(mat);
            if (infos == null)
            {
                ATOLog.Warn(ATOI18n.T(ATOI18nKeys.WarnUnsupportedShader, mat.name, mat.shader != null ? mat.shader.name : "null"));
                // Unsupported shader → whitelist this material's textures. 不支持的着色器 → 白名单该材质贴图。
                WhitelistMaterialTextures(result, mat);
                return;
            }

            foreach (var info in infos)
            {
                if (info.isSpecialUsage) continue; // handled via whitelist below
                var usage = new ATOTextureUsage
                {
                    texture = mat.GetTexture(info.propertyName) as Texture2D,
                    kind = info.kind,
                    propertyName = info.propertyName,
                    uvChannel = info.uvChannel,
                    isMainColor = info.kind == ATOTextureKind.Color && IsMainColorProperty(mat, info.propertyName),
                    material = mat,
                    renderer = renderer,
                    slotIndex = slot,
                    hasScrollRotate = info.hasScrollRotate,
                    isSpecialUsage = false,
                };
                if (usage.texture == null) continue;
                result.allUsages.Add(usage);
            }

            // Special usages → whitelist their textures. 特殊用途 → 白名单其贴图。
            foreach (var info in infos)
            {
                if (!info.isSpecialUsage) continue;
                var tex = mat.GetTexture(info.propertyName) as Texture2D;
                if (tex == null) continue;
                var usage = new ATOTextureUsage
                {
                    texture = tex,
                    kind = info.kind,
                    propertyName = info.propertyName,
                    uvChannel = info.uvChannel,
                    material = mat,
                    renderer = renderer,
                    slotIndex = slot,
                    isSpecialUsage = true,
                    whitelisted = true,
                };
                result.allUsages.Add(usage);
                ATOLog.Warn(ATOI18n.T(ATOI18nKeys.WarnDecal, tex.name, mat.name));
            }
        }

        private static bool IsMainColorProperty(Material mat, string prop)
        {
            // lilToon main texture is _MainTex (or _BaseMap/_BaseColorMap dummies). Standard is _MainTex.
            // lilToon 主色为 _MainTex（或 _BaseMap/_BaseColorMap 假属性）。Standard 为 _MainTex。
            return prop == "_MainTex" || prop == "_BaseMap" || prop == "_BaseColorMap";
        }

        private static void WhitelistMaterialTextures(ATOAnalysisResult result, Material mat)
        {
            var shader = mat.shader;
            if (shader == null) return;
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var tex = mat.GetTexture(ShaderUtil.GetPropertyName(shader, i)) as Texture2D;
                if (tex == null) continue;
                result.allUsages.Add(new ATOTextureUsage
                {
                    texture = tex,
                    kind = ATOTextureKind.Other,
                    propertyName = ShaderUtil.GetPropertyName(shader, i),
                    uvChannel = 0,
                    material = mat,
                    whitelisted = true,
                });
            }
        }

        private void CollectAnimationIntroduced(GameObject avatarRoot, ATOAnalysisResult result)
        {
            var clips = CollectClips(avatarRoot);
            var byName = BuildNameIndex(avatarRoot);
            var byPath = BuildPathIndex(avatarRoot);

            var anim = result.animation;

            // For each animated material slot, gather the swapped-in materials and analyze them.
            // 对每个被动画的材质槽，收集换入的材质并分析。
            foreach (var (renderer, slot) in anim.animatedMaterialSlots)
            {
                foreach (var clip in clips)
                {
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        if (!binding.propertyName.Contains("m_Materials.Array.data")) continue;
                        int idx = SlotIndexOf(binding.propertyName);
                        if (idx != slot) continue;
                        var targets = Resolve(binding.path, byPath, byName);
                        if (!targets.Contains(renderer.gameObject) && !targets.Any(g => g.GetComponent<Renderer>() == renderer)) continue;

                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        foreach (var k in curve)
                        {
                            if (k.value is Material swapped && swapped != null)
                                AddUsagesForSlot(result, renderer, swapped, slot, fromAnimation: true);
                        }
                    }
                }
            }

            // Texture property swaps: bindings like "..._MainTex" referencing a Texture2D.
            // 贴图属性切换：类似 "..._MainTex" 且引用 Texture2D 的绑定。
            var knownProps = new HashSet<string>();
            foreach (var u in result.allUsages) knownProps.Add(u.propertyName);
            foreach (var clip in clips)
            {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    string prop = binding.propertyName;
                    string texProp = prop;
                    int lastDot = prop.LastIndexOf('.');
                    if (lastDot > 0) texProp = prop.Substring(lastDot + 1);
                    if (!knownProps.Contains(texProp)) continue;

                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    foreach (var k in curve)
                    {
                        if (!(k.value is Texture2D tex) || tex == null) continue;
                        foreach (var go in Resolve(binding.path, byPath, byName))
                        {
                            var r = go.GetComponent<Renderer>();
                            if (r == null) continue;
                            int slot = SlotIndexOf(prop);
                            foreach (var u in result.allUsages)
                            {
                                if (u.renderer == r && u.propertyName == texProp && u.slotIndex == (slot >= 0 ? slot : u.slotIndex))
                                {
                                    if (u.texture == tex) break; // already known
                                    result.allUsages.Add(new ATOTextureUsage
                                    {
                                        texture = tex,
                                        kind = u.kind,
                                        propertyName = texProp,
                                        uvChannel = u.uvChannel,
                                        material = u.material,
                                        renderer = r,
                                        slotIndex = u.slotIndex,
                                        isMainColor = u.isMainColor,
                                    });
                                    ATOLog.Verbose($"[Animation] texture swap '{tex.name}' added to UV group (prop={texProp}).");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Renderers animated ON: add their usages even if currently disabled. 被动画启用的渲染器：即使当前禁用也加入。
            foreach (var renderer in anim.animatedEnableRenderers)
            {
                var mats = renderer.sharedMaterials;
                for (int slot = 0; slot < mats.Length; slot++)
                {
                    if (mats[slot] == null) continue;
                    AddUsagesForSlot(result, renderer, mats[slot], slot, fromAnimation: true);
                }
            }

            // Drop duplicate usages (same texture+renderer+slot+prop). 去除重复用途。
            var seen = new HashSet<(Texture2D, Renderer, int, string)>();
            result.allUsages = result.allUsages.Where(u =>
            {
                if (u.texture == null) return false;
                return seen.Add((u.texture, u.renderer, u.slotIndex, u.propertyName));
            }).ToList();
        }

        // ------------------------------------------------------------------

        private static List<ATOTextureRef> BuildTextureRefs(ATOAnalysisResult result)
        {
            var byTexture = new Dictionary<Texture2D, ATOTextureRef>();
            foreach (var usage in result.allUsages)
            {
                if (usage.texture == null) continue;
                if (!byTexture.TryGetValue(usage.texture, out var texRef))
                {
                    texRef = new ATOTextureRef { texture = usage.texture };
                    byTexture[usage.texture] = texRef;
                }
                texRef.usages.Add(usage);
                if (usage.whitelisted) texRef.whitelisted = true;
            }
            return new List<ATOTextureRef>(byTexture.Values);
        }

        private void BuildGroups(ATOAnalysisResult result)
        {
            // UV groups keyed by (renderer, slot, channel). UV 组按（渲染器, 槽, 通道）分组。
            var uvGroups = new Dictionary<(Renderer, int, int), ATOUVGroup>();
            foreach (var usage in result.allUsages)
            {
                if (usage.uvChannel < 0) continue;
                var key = (usage.renderer, usage.slotIndex, usage.uvChannel);
                if (!uvGroups.TryGetValue(key, out var group))
                {
                    group = new ATOUVGroup { renderer = usage.renderer, uvChannel = usage.uvChannel, slotIndex = usage.slotIndex, id = uvGroups.Count };
                    uvGroups[key] = group;
                }
                group.usages.Add(usage);
                if (usage.whitelisted) group.hasWhitelistMember = true;
            }
            result.uvGroups = uvGroups.Values.ToList();

            // A group is fully whitelisted only when every member is. 仅当全部成员白名单时组整体白名单。
            foreach (var g in result.uvGroups)
            {
                g.whitelisted = g.usages.Count > 0 && g.usages.TrueForAll(u => u.whitelisted);
            }

            // Animated scale factor per renderer → apply to groups. 动画缩放因子 → 应用到组。
            foreach (var g in result.uvGroups)
            {
                if (g.renderer != null && result.animation.animatedScaleFactors.TryGetValue(g.renderer, out float f))
                    g.areaScaleFactor = f * f;
            }

            // Texture type groups: signature over the main-color usages. 贴图类型组：按主色用途签名。
            var typeGroups = new Dictionary<string, ATOTextureTypeGroup>();
            var mainUsages = result.allUsages.Where(u => u.isMainColor && u.texture != null).ToList();
            foreach (var usage in mainUsages)
            {
                var group = result.uvGroups.FirstOrDefault(g =>
                    g.renderer == usage.renderer && g.uvChannel == usage.uvChannel &&
                    g.slotIndex == usage.slotIndex);
                if (group == null) continue;
                // Groups with a whitelisted member skip atlas generation entirely. 含白名单成员的组完全跳过图集化。
                if (group.hasWhitelistMember) continue;

                bool hasNormal = group.usages.Any(x => x.kind == ATOTextureKind.NormalMap);
                bool hasMask = group.usages.Any(x => x.kind == ATOTextureKind.Mask || x.kind == ATOTextureKind.Grayscale);
                bool linear = !ATOTextureIO.IsSRGB(usage.texture);
                var filter = usage.texture.filterMode;

                string key = ATOTextureTypeGroup.BuildKey(hasNormal, hasMask, linear, filter);
                if (!typeGroups.TryGetValue(key, out var tg))
                {
                    tg = new ATOTextureTypeGroup
                    {
                        key = key,
                        hasNormalMap = hasNormal,
                        hasMask = hasMask,
                        linearColorSpace = linear,
                        filterMode = filter,
                    };
                    typeGroups[key] = tg;
                }
                tg.colorUsages.Add(usage);
            }
            result.typeGroups = typeGroups.Values.ToList();
        }

        private void ExtractIslands(ATOAnalysisResult result)
        {
            // Cache per (renderer, slot, channel) so shared slots don't re-extract.
            // 按（渲染器, 槽, 通道）缓存，避免重复提取。
            var cache = new Dictionary<(Renderer, int, int), List<ATOIsland>>();
            var blendFactorCache = new Dictionary<Renderer, float>();
            foreach (var g in result.uvGroups)
            {
                if (g.whitelisted) continue;
                if (g.renderer == null) continue;
                var mesh = GetSharedMesh(g.renderer);
                if (mesh == null) continue;

                var key = (g.renderer, g.slotIndex, g.uvChannel);
                if (!cache.TryGetValue(key, out var islands))
                {
                    // Fold in blend-shape area (0/100 max) for animated shapes. 计入动画形态键（0/100 最大）面积。
                    if (!blendFactorCache.TryGetValue(g.renderer, out var blendFactor))
                    {
                        blendFactor = 1f;
                        if (g.renderer is SkinnedMeshRenderer smr &&
                            result.animation.animatedBlendShapes.TryGetValue(smr, out var bsSet) && bsSet.Count > 0)
                        {
                            blendFactor = BlendShapeAnalyzer.ComputeFactor(smr, bsSet);
                        }
                        blendFactorCache[g.renderer] = blendFactor;
                    }

                    var extract = UVIslandExtractor.Extract(mesh, g.uvChannel, g.areaScaleFactor * blendFactor, g.slotIndex);
                    if (extract.cannotNormalize)
                    {
                        g.whitelisted = true;
                        ATOLog.Warn(ATOI18n.T(ATOI18nKeys.WarnUvOutOfBounds, mesh.name));
                        continue;
                    }
                    islands = extract.islands;
                    cache[key] = islands;
                }
                g.islands = islands;
            }
        }

        private void ResolveAlphaModes(ATOAnalysisResult result)
        {
            foreach (var texRef in result.textures)
            {
                texRef.alphaMode = ATOAlphaMode.Opaque;
                texRef.cutoff = 0.5f;
                foreach (var u in texRef.usages)
                {
                    if (u.material == null) continue;
                    var mode = AlphaModeDetector.Detect(u.material);
                    if (result.animation.animatedRenderMode.Contains(u.material))
                        mode = (ATOAlphaMode)Mathf.Max((int)mode, (int)ATOAlphaMode.Blend);
                    float cutoff = AlphaModeDetector.DetectCutoff(u.material);
                    if ((int)mode > (int)texRef.alphaMode)
                    {
                        texRef.alphaMode = mode;
                        texRef.cutoff = cutoff;
                    }
                    else if (mode == texRef.alphaMode && cutoff < texRef.cutoff)
                    {
                        texRef.cutoff = cutoff;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ shared helpers

        private static Mesh GetSharedMesh(Renderer r)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf != null ? mf.sharedMesh : null;
                default: return null;
            }
        }

        private static List<AnimationClip> CollectClips(GameObject avatarRoot)
        {
            var clips = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
#if ATO_VRCSDK3
            var descriptor = avatarRoot.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var l in descriptor.baseAnimationLayers) Collect(l.animatorController, clips, seen);
                foreach (var l in descriptor.specialAnimationLayers) Collect(l.animatorController, clips, seen);
            }
#endif
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
                Collect(animator.runtimeAnimatorController, clips, seen);
            return clips;
        }

        private static void Collect(RuntimeAnimatorController c, List<AnimationClip> clips, HashSet<AnimationClip> seen)
        {
            if (c == null) return;
            foreach (var clip in c.animationClips)
                if (clip != null && seen.Add(clip)) clips.Add(clip);
        }

        private static Dictionary<string, List<GameObject>> BuildNameIndex(GameObject root)
        {
            var index = new Dictionary<string, List<GameObject>>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string k = t.name.ToLowerInvariant();
                if (!index.TryGetValue(k, out var list)) { list = new List<GameObject>(); index[k] = list; }
                list.Add(t.gameObject);
            }
            return index;
        }

        private static Dictionary<string, GameObject> BuildPathIndex(GameObject root)
        {
            var index = new Dictionary<string, GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string p = RelativePath(root.transform, t);
                if (!index.ContainsKey(p)) index[p] = t.gameObject;
            }
            return index;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static List<GameObject> Resolve(string path, Dictionary<string, GameObject> byPath, Dictionary<string, List<GameObject>> byName)
        {
            var found = new List<GameObject>();
            if (byPath.TryGetValue(path, out var exact)) found.Add(exact);
            if (found.Count == 0)
                foreach (var kv in byPath)
                    if (kv.Key.EndsWith("/" + path)) found.Add(kv.Value);
            if (found.Count == 0)
            {
                string name = path;
                int slash = path.LastIndexOf('/');
                if (slash >= 0) name = path.Substring(slash + 1);
                if (byName.TryGetValue(name.ToLowerInvariant(), out var list)) found.AddRange(list);
            }
            return found;
        }

        private static int SlotIndexOf(string prop)
        {
            int start = prop.IndexOf('[');
            int end = prop.IndexOf(']', start + 1);
            if (start >= 0 && end > start && int.TryParse(prop.Substring(start + 1, end - start - 1), out int idx)) return idx;
            return -1;
        }
    }
}

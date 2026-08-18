// Avatar Texture Optimizer (ATO)
// Animation analysis: material swaps, texture swaps, enable/disable, scale,
// render-mode/cutoff changes, and UV ST transforms.
// 动画分析：材质切换、贴图切换、启用/禁用、缩放、渲染模式/Cutoff 修改以及 UV ST 变换。

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Data gathered from all animations on the avatar. / 从 Avatar 全部动画中收集的数据。
    /// </summary>
    public sealed class ATOAnimationData
    {
        /// <summary>GameObject paths whose active state is animated. / 被动画控制 active 状态的对象路径。</summary>
        public readonly HashSet<string> activePaths = new HashSet<string>();

        /// <summary>Renderer paths whose enabled state is animated. / 被动画控制 enabled 的渲染器路径。</summary>
        public readonly HashSet<string> rendererEnablePaths = new HashSet<string>();

        /// <summary>Transform path -> max area scale factor from animated local scale. / 路径 -> 动画局部缩放的最大面积缩放系数。</summary>
        public readonly Dictionary<string, float> maxAreaScale = new Dictionary<string, float>();

        /// <summary>(rendererPath, slotIndex) -> swapped-in materials. / (渲染器路径, 槽) -> 切换进来的材质。</summary>
        public readonly Dictionary<(string, int), List<Material>> materialSwaps = new Dictionary<(string, int), List<Material>>();

        /// <summary>(material, property) -> swapped-in textures. / (材质, 属性) -> 切换进来的贴图。</summary>
        public readonly Dictionary<(Material, string), List<Texture2D>> textureSwaps = new Dictionary<(Material, string), List<Texture2D>>();

        /// <summary>Materials whose render mode/cutoff is animated (strictest wins). / 渲染模式/Cutoff 被动画修改的材质（取最严）。</summary>
        public readonly Dictionary<Material, (ATOAlphaMode mode, float minCutoff)> animatedAlpha = new Dictionary<Material, (ATOAlphaMode, float)>();

        /// <summary>(material, property) whose ST vector is animated to non-identity -> disqualify. / ST 向量被动画改成非单位值 -> 取消资格。</summary>
        public readonly HashSet<(Material, string)> animatedSt = new HashSet<(Material, string)>();
    }

    /// <summary>
    /// Stage 1: walk every animation clip and record material/texture/transform effects.
    /// 阶段 1：遍历所有动画片段，记录材质/贴图/变换影响。
    /// </summary>
    public static class ATOAnimationAnalyzer
    {
        public static void Analyze(ATOBuildContext build, ATOProgress progress)
        {
            var clips = CollectClips(build.avatarRoot);
            progress.Begin(clips.Count);

            var rendererByPath = new Dictionary<string, ATORendererRef>();
            foreach (var rr in build.renderers)
                rendererByPath[rr.path] = rr;

            foreach (var clip in clips)
            {
                if (clip == null) { progress.Advance(1, "null clip"); continue; }
                AnalyzeClip(build, clip, rendererByPath);
                progress.Advance(1, clip.name);
            }

            // Apply material-swap results: collect their textures into the build state. / 应用材质切换结果：把贴图并入构建状态。
            ApplyMaterialSwaps(build, rendererByPath);

            // Apply texture-swap results. / 应用贴图切换结果。
            ApplyTextureSwaps(build);

            // Apply animated render modes & cutoffs (strictest). / 应用动画渲染模式与 cutoff（取最严）。
            ApplyAnimatedAlpha(build);

            ATOLogger.Info($"Analyzed {clips.Count} animation clips; {build.anim.materialSwaps.Count} material swaps, {build.anim.textureSwaps.Count} texture swaps.");
        }

        // ---------------- clip collection / 收集动画片段 ----------------

        private static List<AnimationClip> CollectClips(GameObject root)
        {
            var result = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();

            void Add(AnimationClip c)
            {
                if (c != null && seen.Add(c)) result.Add(c);
            }

            foreach (var anim in root.GetComponentsInChildren<Animator>(true))
            {
                var rc = anim.runtimeAnimatorController;
                if (rc == null) continue;
                if (rc is AnimatorOverrideController aoc)
                {
                    // Include base + overrides. / 包含基础层与覆写。
                    CollectFromController(aoc.runtimeAnimatorController, Add);
                    foreach (var op in aoc.overrides) { Add(op.Key); Add(op.Value); }
                }
                else
                {
                    CollectFromController(rc, Add);
                }
            }

            foreach (var legacy in root.GetComponentsInChildren<Animation>(true))
            {
                foreach (var c in AnimationUtility.GetAnimationClips(legacy.gameObject)) Add(c);
                if (legacy.clip != null) Add(legacy.clip);
            }

            return result;
        }

        private static void CollectFromController(RuntimeAnimatorController rc, System.Action<AnimationClip> add)
        {
            if (rc is not AnimatorController ac) return;
            foreach (var layer in ac.layers)
                CollectFromStateMachine(layer.stateMachine, add);
        }

        private static void CollectFromStateMachine(AnimatorStateMachine sm, System.Action<AnimationClip> add)
        {
            foreach (var state in sm.states)
                CollectFromMotion(state.state.motion, add);
            foreach (var child in sm.stateMachines)
                CollectFromStateMachine(child.stateMachine, add);
        }

        private static void CollectFromMotion(Motion motion, System.Action<AnimationClip> add)
        {
            if (motion == null) return;
            if (motion is AnimationClip c) { add(c); return; }
            if (motion is BlendTree bt)
            {
                foreach (var child in bt.children) CollectFromMotion(child.motion, add);
            }
        }

        // ---------------- per-clip analysis / 逐片段分析 ----------------

        private static void AnalyzeClip(ATOBuildContext build, AnimationClip clip, Dictionary<string, ATORendererRef> rendererByPath)
        {
            var curveBindings = AnimationUtility.GetCurveBindings(clip);
            var objRefBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);

            foreach (var b in curveBindings)
            {
                var prop = b.propertyName;
                // Game object active toggling. / 游戏对象 active 切换。
                if (prop == "m_IsActive") build.anim.activePaths.Add(b.path);
                // Renderer enabled toggling. / 渲染器 enabled 切换。
                if (prop == "m_Enabled" && (b.type == typeof(SkinnedMeshRenderer) || b.type == typeof(MeshRenderer)))
                    build.anim.rendererEnablePaths.Add(b.path);

                // Transform scale. / 变换缩放。
                if (prop == "m_LocalScale.x" || prop == "m_LocalScale.y" || prop == "m_LocalScale.z")
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null) continue;
                    float maxAbs = 0f;
                    foreach (var k in curve.keys) maxAbs = Mathf.Max(maxAbs, Mathf.Abs(k.value));
                    if (maxAbs > 0f)
                    {
                        build.anim.maxAreaScale.TryGetValue(b.path, out var prev);
                        build.anim.maxAreaScale[b.path] = Mathf.Max(prev, maxAbs * maxAbs);
                    }
                }

                // Material float properties: render mode & cutoff. / 材质浮点属性：渲染模式与 cutoff。
                if (b.type == typeof(Material))
                {
                    var mat = FindMaterialByPath(build, b.path);
                    if (mat == null) continue;
                    if (prop == "_Cutoff" || prop == "_AlphaClipThreshold")
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, b);
                        float min = 0.5f;
                        if (curve != null) foreach (var k in curve.keys) min = Mathf.Min(min, k.value);
                        UpdateAnimatedAlpha(build, mat, ATOAlphaMode.Cutout, min);
                    }
                    else if (prop == "_Mode" || prop == "_SrcBlend" || prop == "_DstBlend" || prop == "_ZWrite")
                    {
                        UpdateAnimatedAlpha(build, mat, ATOAlphaMode.Blend, 0f);
                    }
                    else if (prop.EndsWith("_ST.x") || prop.EndsWith("_ST.y") || prop.EndsWith("_ST.z") || prop.EndsWith("_ST.w"))
                    {
                        var baseName = prop.Substring(0, prop.Length - 3); // strip .x/.y/.z/.w / 去掉 .x/.y/.z/.w
                        build.anim.animatedSt.Add((mat, baseName));
                    }
                }
            }

            foreach (var b in objRefBindings)
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (curve == null) continue;
                foreach (var k in curve)
                {
                    var obj = k.value;
                    if (obj == null) continue;

                    // Material slot swap: propertyName "m_Materials.Array.data[i]" / 材质槽切换。
                    if (b.propertyName.StartsWith("m_Materials.Array.data["))
                    {
                        var idxStr = b.propertyName.Substring("m_Materials.Array.data[".Length).TrimEnd(']');
                        if (int.TryParse(idxStr, out var slot) && obj is Material m)
                        {
                            var key = (b.path, slot);
                            if (!build.anim.materialSwaps.TryGetValue(key, out var list))
                                build.anim.materialSwaps[key] = list = new List<Material>();
                            if (!list.Contains(m)) list.Add(m);
                        }
                    }
                    // Texture property swap on a material. / 材质贴图属性切换。
                    else if (b.type == typeof(Material) && obj is Texture2D t)
                    {
                        var mat = FindMaterialByPath(build, b.path);
                        if (mat == null) continue;
                        var key = (mat, b.propertyName);
                        if (!build.anim.textureSwaps.TryGetValue(key, out var list))
                            build.anim.textureSwaps[key] = list = new List<Texture2D>();
                        if (!list.Contains(t)) list.Add(t);
                    }
                }
            }
        }

        private static void UpdateAnimatedAlpha(ATOBuildContext build, Material mat, ATOAlphaMode mode, float minCutoff)
        {
            build.anim.animatedAlpha.TryGetValue(mat, out var cur);
            var nextMode = cur.mode > mode ? cur.mode : mode; // Opaque<Cutout<Blend / 取更严者
            var nextCutoff = cur.minCutoff == 0 ? minCutoff : Mathf.Min(cur.minCutoff, minCutoff);
            build.anim.animatedAlpha[mat] = (nextMode, nextCutoff);
        }

        private static Material FindMaterialByPath(ATOBuildContext build, string path)
        {
            // Path-based: binding path is the original material asset path; resolve to its clone.
            // 路径匹配：绑定路径是原始材质资产路径；解析到其克隆。
            if (build.materialPathRemap.TryGetValue(path, out var clonePath))
                foreach (var clone in build.baseMaterialClone.Values)
                    if (AssetDatabase.GetAssetPath(clone) == clonePath) return clone;
            // Fallback: leaf-name match (stripping our "_ato" suffix). / 兜底：叶节点名匹配（去掉 "_ato" 后缀）。
            var leaf = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;
            foreach (var tr in build.textures)
                foreach (var u in tr.usages)
                {
                    if (u.material == null) continue;
                    if (u.material.name == leaf || u.material.name == leaf + "_ato") return u.material;
                }
            return null;
        }

        // ---------------- applying collected data / 应用收集的数据 ----------------

        private static void ApplyMaterialSwaps(ATOBuildContext build, Dictionary<string, ATORendererRef> rendererByPath)
        {
            foreach (var kvp in build.anim.materialSwaps)
            {
                if (!rendererByPath.TryGetValue(kvp.Key.Item1, out var rr)) continue;
                foreach (var m in kvp.Value)
                {
                    // Resolve the swapped-in material to its clone (never mutate the user's asset).
                    // 把切换进来的材质解析到其克隆（绝不修改用户资产）。
                    if (!build.baseMaterialClone.TryGetValue(m, out var mm))
                    {
                        mm = new Material(m) { name = m.name + "_ato" };
                        build.baseMaterialClone[m] = mm;
                        try { build.ndmf.AssetSaver.SaveAsset(mm); } catch (System.Exception) { }
                    }
                    ATOAvatarScanner.CollectMaterialTextures(build, rr, kvp.Key.Item2, mm, fromAnimation: true);
                }
                // Mark the slot as animation-swapped. / 标记该槽被动画切换。
                foreach (var tr in build.textures)
                    foreach (var u in tr.usages)
                        if (u.material != null && rr.slots.Length > kvp.Key.Item2 && rr.slots[kvp.Key.Item2] == u.material)
                            u.materialSwappedViaAnimation = true;
            }
        }

        private static void ApplyTextureSwaps(ATOBuildContext build)
        {
            foreach (var kvp in build.anim.textureSwaps)
            {
                var mat = kvp.Key.Item1;
                var prop = kvp.Key.Item2;
                foreach (var t in kvp.Value)
                {
                    // Find the texture ref for the swapped-in texture and add a usage. / 找到切换贴图的 ref 并添加使用。
                    var tr = FindOrCreate(build, t);
                    var category = ATOShaderPropertyAnalyzer.Analyze(mat).TryGetValue(prop, out var c) ? c : ATOTextureCategory.Other;
                    if (category == ATOTextureCategory.Other) continue;
                    tr.usages.Add(new ATOTextureUsage
                    {
                        material = mat,
                        propertyName = prop,
                        category = category,
                        uvChannel = 0,
                        alphaMode = ATOAvatarScanner.ResolveAlphaMode(mat),
                        cutoff = 0.5f,
                        fromAnimation = true,
                    });
                }
            }
        }

        private static void ApplyAnimatedAlpha(ATOBuildContext build)
        {
            foreach (var kvp in build.anim.animatedAlpha)
            {
                foreach (var tr in build.textures)
                    foreach (var u in tr.usages)
                        if (u.material == kvp.Key)
                        {
                            if (kvp.Value.mode > u.alphaMode) u.alphaMode = kvp.Value.mode;
                            u.cutoff = Mathf.Min(u.cutoff, kvp.Value.minCutoff == 0 ? u.cutoff : kvp.Value.minCutoff);
                        }
            }
        }

        private static ATOTextureRef FindOrCreate(ATOBuildContext build, Texture2D tex)
        {
            foreach (var t in build.textures)
                if (t.texture == tex) return t;
            var tr = new ATOTextureRef
            {
                texture = tex, sourceAsset = tex,
                assetPath = AssetDatabase.GetAssetPath(tex),
                width = tex.width, height = tex.height,
                isSRGB = true, filterMode = tex.filterMode, wrapMode = tex.wrapMode,
                importFingerprint = ATOUtil.ImportFingerprint(tex),
                hasAlpha = true,
            };
            build.textures.Add(tr);
            return tr;
        }
    }
}

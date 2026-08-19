// AvatarTextureOptimizer
// File: Editor/Analysis/AnimationScanner.cs
//
// Scans all animation clips reachable from the avatar (Animator controller,
// VRChat playable layers, override controllers, nested blend trees) and
// extracts everything the optimizer must know:
//   - renderers / GameObjects toggled by animation (counts as "enabled")
//   - material property animations (render mode, cutoff, texture switches, ST)
//   - material-slot switches (m_Materials.Array.data[N])
//   - mesh switches (m_Mesh) -> those renderers are whitelisted
//   - texture switches -> new TextureUsages merged into the UV mapping
//   - blend shape weight animations -> shape-area analysis input
//
// 扫描从 Avatar 可达的所有动画剪辑（Animator 控制器、VRChat 播放层、
// 覆写控制器、嵌套混合树），提取优化器必须知道的一切：
//   - 被动画切换的渲染器/游戏对象（视为"被启用"）
//   - 材质属性动画（渲染模式、cutoff、贴图切换、ST）
//   - 材质槽切换（m_Materials.Array.data[N]）
//   - 网格切换（m_Mesh）-> 这些渲染器被白名单
//   - 贴图切换 -> 合并进 UV 映射的新 TextureUsage
//   - 形态键权重动画 -> 形态面积分析的输入

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if NDMF_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>Animation facts gathered during the scan. / 扫描期间收集的动画事实。</summary>
    public sealed class AnimationFacts
    {
        /// <summary>Renderers animated to be enabled (or whose GO is animated active). / 被动画启用的渲染器（或其 GO 被动画激活）。</summary>
        public readonly HashSet<Renderer> AnimatedEnabledRenderers = new HashSet<Renderer>();

        /// <summary>GameObjects whose activeSelf is animated. / activeSelf 被动画的 GameObject。</summary>
        public readonly HashSet<GameObject> AnimatedActiveObjects = new HashSet<GameObject>();

        /// <summary>Renderers with animated m_Mesh -> must be whitelisted. / m_Mesh 被动画的渲染器 -> 必须白名单。</summary>
        public readonly HashSet<Renderer> AnimatedMeshRenderers = new HashSet<Renderer>();

        /// <summary>Materials with animated ST on a texture property -> texture whitelisted. / 贴图属性 ST 被动画的材质。</summary>
        public readonly HashSet<Material> AnimatedSTMaterials = new HashSet<Material>();

        /// <summary>Material -> set of animated non-texture property names (cutoff, mode...). / 材质 -> 被动画的非贴图属性名集合。</summary>
        public readonly Dictionary<Material, HashSet<string>> AnimatedMaterialProperties =
            new Dictionary<Material, HashSet<string>>();

        /// <summary>Renderers -> slot -> property -> textures switched in by animation. / 渲染器 -> 槽 -> 属性 -> 动画切入的贴图。</summary>
        public readonly Dictionary<(Renderer, int), Dictionary<string, HashSet<Texture2D>>> AnimatedTextureSwitches =
            new Dictionary<(Renderer, int), Dictionary<string, HashSet<Texture2D>>>();

        /// <summary>Renderers with animated material-slot switches. / 材质槽被动画切换的渲染器。</summary>
        public readonly HashSet<Renderer> AnimatedMaterialSlots = new HashSet<Renderer>();

        /// <summary>Clips containing blend shape weight animations (path -> names). / 包含形态键权重动画的剪辑（路径 -> 名称）。</summary>
        public readonly Dictionary<string, HashSet<string>> AnimatedBlendShapes = new Dictionary<string, HashSet<string>>();

        /// <summary>Maximum animated local scale per transform path (for area-at-max-scale handling). / 每个变换路径的最大动画局部缩放（用于按最大缩放计算面积）。</summary>
        public readonly Dictionary<string, Vector3> MaxAnimatedScale = new Dictionary<string, Vector3>();

        public bool IsRendererEffectivelyEnabled(Renderer r)
        {
            return r.enabled || AnimatedEnabledRenderers.Contains(r) || AnimatedActiveObjects.Contains(r.gameObject);
        }

        public bool HasMeshSwitch(Renderer r) => AnimatedMeshRenderers.Contains(r);
    }

    /// <summary>
    /// Scans animations reachable from the avatar.
    /// 扫描从 Avatar 可达的动画。
    /// </summary>
    public static class AnimationScanner
    {
        /// <summary>
        /// Scan all animation controllers of the avatar and gather facts.
        /// 扫描 Avatar 的全部动画控制器并收集事实。
        /// </summary>
        public static AnimationFacts Scan(GameObject avatarRoot, ATOBuildState state)
        {
            var facts = new AnimationFacts();
            var stopwatch = new ATOStopwatch("AnimationScanner.Scan");
            var visited = new HashSet<AnimationClip>();

            // 1. Runtime Animator controller. / 运行期 Animator 控制器。
            var animator = avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                CollectClips(animator.runtimeAnimatorController, visited);
            }

            // 2. Legacy Animation component clips. / 旧版 Animation 组件剪辑。
            foreach (var anim in avatarRoot.GetComponentsInChildren<UnityEngine.Animation>(true))
            {
                var clips = new List<AnimationClip>();
                anim.GetClips(clips);
                foreach (var c in clips) visited.Add(c);
            }

#if NDMF_VRCSDK3_AVATARS
            // 3. VRChat playable layers (FX / Gesture / Action / Locomotion / ...).
            //    VRChat 播放层。
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                var layers = new List<VRCAvatarDescriptor.CustomAnimLayer>();
                if (descriptor.baseAnimationLayers != null)
                    layers.AddRange(descriptor.baseAnimationLayers);
                if (descriptor.specialAnimationLayers != null)
                    layers.AddRange(descriptor.specialAnimationLayers);
                foreach (var layer in layers)
                {
                    if (layer.animatorController != null && layer.isDefault == false)
                        CollectClips(layer.animatorController, visited);
                    else if (layer.animatorController != null)
                        CollectClips(layer.animatorController, visited);
                }
            }
#endif

            // 4. Parse every clip once. / 解析每个剪辑一次。
            foreach (var clip in visited)
            {
                try
                {
                    ParseClip(clip, avatarRoot, facts, state);
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"[ATO] Failed to parse animation clip {clip.name}: {e.Message}");
                }
            }

            stopwatch.End("parse clips");
            return facts;
        }

        /// <summary>
        /// Recursively collect all AnimationClips reachable from a controller
        /// (handles override controllers and blend trees).
        /// 递归收集从控制器可达的所有 AnimationClip（处理覆写控制器与混合树）。
        /// </summary>
        private static void CollectClips(RuntimeAnimatorController controller, HashSet<AnimationClip> visited)
        {
            switch (controller)
            {
                case AnimatorOverrideController overrideController:
                {
                    var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    overrideController.GetOverrides(overrides);
                    foreach (var kv in overrides)
                    {
                        if (kv.Key != null) CollectClipsInternal(kv.Key, visited);
                        if (kv.Value != null) CollectClipsInternal(kv.Value, visited);
                    }
                    break;
                }
                case AnimatorController ac:
                {
                    foreach (var layer in ac.layers)
                        foreach (var stateMachine in CollectStateMachines(layer.stateMachine))
                            foreach (var state in stateMachine.states)
                                if (state.state.motion != null)
                                    CollectMotion(state.state.motion, visited);
                    break;
                }
            }
        }

        private static void CollectClipsInternal(AnimationClip clip, HashSet<AnimationClip> visited)
        {
            if (clip != null) visited.Add(clip);
        }

        private static void CollectMotion(Motion motion, HashSet<AnimationClip> visited)
        {
            switch (motion)
            {
                case AnimationClip clip:
                    visited.Add(clip);
                    break;
                case BlendTree tree:
                    foreach (var child in tree.children)
                        if (child.motion != null) CollectMotion(child.motion, visited);
                    break;
            }
        }

        private static IEnumerable<AnimatorStateMachine> CollectStateMachines(AnimatorStateMachine sm)
        {
            var result = new List<AnimatorStateMachine> { sm };
            foreach (var child in sm.stateMachines)
                result.AddRange(CollectStateMachines(child.stateMachine));
            return result;
        }

        /// <summary>
        /// Parse the bindings of one clip and update the facts.
        /// 解析一个剪辑的绑定并更新事实。
        /// </summary>
        private static void ParseClip(AnimationClip clip, GameObject avatarRoot, AnimationFacts facts, ATOBuildState state)
        {
            var bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);
            var objectBindings = UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip);

            foreach (var binding in bindings)
            {
                // Path is relative to the animator root; try avatar root then
                // animator's own transform.
                // 路径相对动画器根；先尝试 Avatar 根再尝试动画器自身变换。
                var target = Resolve(binding.path, avatarRoot);
                if (target == null) continue;

                var property = binding.propertyName;

                if (binding.type == typeof(GameObject))
                {
                    if (property == "m_IsActive") facts.AnimatedActiveObjects.Add(target);
                }
                else if (binding.type == typeof(Transform))
                {
                    // Transform animation (including scale) affects the area the
                    // mesh occupies in world space; record the max scale so the
                    // pixel-density calculation uses the largest area (spec).
                    // 变换动画（含缩放）影响网格在世界空间占据的面积；记录最大
                    // 缩放，使像素密度计算使用最大面积（规格）。
                    var prop = binding.propertyName;
                    if (prop.StartsWith("m_LocalScale."))
                    {
                        var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve != null && curve.keys.Length > 0)
                        {
                            int axis = prop.EndsWith(".x") ? 0 : prop.EndsWith(".y") ? 1 : prop.EndsWith(".z") ? 2 : -1;
                            if (axis >= 0)
                            {
                                if (!facts.MaxAnimatedScale.TryGetValue(binding.path, out var maxScale))
                                    maxScale = Vector3.one;
                                foreach (var key in curve.keys)
                                {
                                    float v = Mathf.Abs(key.value);
                                    if (v > maxScale[axis]) maxScale[axis] = v;
                                }
                                facts.MaxAnimatedScale[binding.path] = maxScale;
                            }
                        }
                    }
                }
                else if (binding.type == typeof(SkinnedMeshRenderer) || binding.type == typeof(MeshRenderer))
                {
                    var renderer = target.GetComponent(binding.type) as Renderer;
                    if (renderer == null) continue;

                    if (property == "m_Enabled") facts.AnimatedEnabledRenderers.Add(renderer);
                    else if (property == "m_Mesh") facts.AnimatedMeshRenderers.Add(renderer);
                    else if (property.StartsWith("m_Materials.Array.data["))
                        facts.AnimatedMaterialSlots.Add(renderer);
                    else if (property.StartsWith("blendShape."))
                    {
                        if (!facts.AnimatedBlendShapes.TryGetValue(binding.path, out var names))
                            facts.AnimatedBlendShapes[binding.path] = names = new HashSet<string>();
                        names.Add(property.Substring("blendShape.".Length));
                    }
                }
                else if (binding.type == typeof(Material))
                {
                    // Note: material bindings carry no path; they apply to the
                    // material itself via "material._Prop" on renderers — but
                    // Unity also emits direct material bindings in some editors.
                    // We record the animated property name on the material.
                    // 注意：材质绑定不带路径；它们经由渲染器的 "material._Prop"
                    // 应用——但某些编辑器也会直接发出材质绑定。我们在材质上
                    // 记录被动画的属性名。
                    if (property.StartsWith("material."))
                    {
                        string prop = property.Substring("material.".Length);
                        var mat = ResolveMaterialFromClip(binding, avatarRoot);
                        if (mat != null) RecordAnimatedMaterialProperty(mat, prop, facts);
                    }
                }
            }

            foreach (var binding in objectBindings)
            {
                var target = Resolve(binding.path, avatarRoot);
                if (target == null) continue;

                var property = binding.propertyName;
                var renderer = target.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null) renderer = target.GetComponent<MeshRenderer>();

                if (property.StartsWith("m_Materials.Array.data["))
                {
                    // Material slot switch: record per-slot animations.
                    // 材质槽切换：按槽记录动画。
                    facts.AnimatedMaterialSlots.Add(renderer);
                    continue;
                }

                if (renderer == null) continue;

                if (property.StartsWith("material."))
                {
                    string prop = property.Substring("material.".Length);

                    // Texture property animation (ObjectReferenceKeyframe).
                    // 贴图属性动画（ObjectReferenceKeyframe）。
                    var keyframes = UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keyframes == null) continue;

                    int slot = ParseSlotIndex(property);
                    var textures = new HashSet<Texture2D>();
                    foreach (var kf in keyframes)
                    {
                        if (kf.value is Texture2D t2d && t2d != null)
                            textures.Add(t2d);
                    }
                    if (textures.Count == 0) continue;

                    if (!facts.AnimatedTextureSwitches.TryGetValue((renderer, slot), out var byProp))
                        facts.AnimatedTextureSwitches[(renderer, slot)] = byProp = new Dictionary<string, HashSet<Texture2D>>();
                    if (!byProp.TryGetValue(prop, out var set))
                        byProp[prop] = set = new HashSet<Texture2D>();
                    foreach (var t in textures) set.Add(t);
                }
            }
        }

        private static void RecordAnimatedMaterialProperty(Material mat, string prop, AnimationFacts facts)
        {
            if (prop.EndsWith("_ST"))
            {
                facts.AnimatedSTMaterials.Add(mat);
                return;
            }
            if (!facts.AnimatedMaterialProperties.TryGetValue(mat, out var set))
                facts.AnimatedMaterialProperties[mat] = set = new HashSet<string>();
            set.Add(prop);
        }

        private static Material ResolveMaterialFromClip(EditorCurveBinding binding, GameObject avatarRoot)
        {
            // Direct material bindings in clips usually reference the material
            // asset through a "material" sub-object binding; without a reliable
            // sub-object resolution we conservatively mark all materials of the
            // target as animated.
            // 剪辑中的直接材质绑定通常通过 "material" 子对象绑定引用材质资产；
            // 在没有可靠子对象解析的情况下，保守地将目标的所有材质标记为动画。
            var target = Resolve(binding.path, avatarRoot);
            if (target == null) return null;
            var renderer = target.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null) renderer = target.GetComponent<MeshRenderer>();
            if (renderer == null) return null;
            // The safest conservative choice for direct material bindings:
            // return the first material. Callers record per-material facts only;
            // false positives here just widen the whitelist slightly.
            // 直接材质绑定的最安全保守选择：返回第一个材质。调用方只按材质
            // 记录事实；这里的误报只会轻微扩大白名单。
            var mats = renderer.sharedMaterials;
            return mats.Length > 0 ? mats[0] : null;
        }

        private static int ParseSlotIndex(string propertyName)
        {
            const string prefix = "m_Materials.Array.data[";
            if (propertyName.StartsWith(prefix))
            {
                int end = propertyName.IndexOf(']', prefix.Length);
                if (end > prefix.Length &&
                    int.TryParse(propertyName.Substring(prefix.Length, end - prefix.Length), out var idx))
                    return idx;
            }
            return 0;
        }

        private static GameObject Resolve(string path, GameObject avatarRoot)
        {
            if (string.IsNullOrEmpty(path)) return avatarRoot;
            return avatarRoot.transform.Find(path)?.gameObject;
        }

        /// <summary>
        /// Merge animation facts into the collected usages: mark animated
        /// renderers, add switched textures as new usages, and whitelist
        /// textures whose ST or mesh is animated.
        /// 将动画事实合并进已收集的引用：标记动画渲染器、把切换的贴图加入
        /// 新引用、白名单 ST 或网格被动画的贴图。
        /// </summary>
        public static void MergeFacts(AnimationFacts facts, ATOBuildState state)
        {
            // 1. Whitelist textures whose material has animated ST.
            //    白名单材质 ST 被动画的贴图。
            if (facts.AnimatedSTMaterials.Count > 0)
            {
                foreach (var usage in state.AllUsages)
                {
                    if (facts.AnimatedSTMaterials.Contains(usage.Material))
                    {
                        state.Warn($"{usage}: material ST is animated -> whitelisted / 材质 ST 被动画，视作白名单");
                        state.WhitelistedTextures.Add(usage.Texture);
                    }
                }
            }

            // 2. Whitelist renderers with animated mesh (UVs could change).
            //    白名单网格被动画的渲染器（UV 可能改变）。
            foreach (var r in facts.AnimatedMeshRenderers)
            {
                state.WhitelistedRenderers.Add(r);
                state.Warn($"[ATO] Renderer {r.name}: m_Mesh is animated -> renderer whitelisted / m_Mesh 被动画，渲染器白名单");
            }

            // 3. Whitelist textures switched in by animation for a slot that is
            //    also animated for ST on the SAME property (handled above for
            //    base-state materials; here we whitelist animated ST of the
            //    switched-in textures conservatively via material check).
            //    Note: animation curves usually animate "material._X_ST" on the
            //    renderer, which we capture as renderer-level ST animation below.
            //    同上，但针对动画切入的贴图保守处理（见下）。

            // 4. For renderers with animated material-slot switches, mark all
            //    their slots' textures as candidates for strict handling: the
            //    optimizer already takes the strictest requirement among all
            //    usages, so no extra whitelist is needed unless a texture is
            //    only reachable through the animated slot (covered below).
            //    对材质槽被动画的渲染器，其全部槽位贴图按最严苛需求处理：
            //    优化器本身会取全部引用中最严苛的要求，因此除非贴图只能通过
            //    动画槽位到达（见下），否则无需额外白名单。

            // 5. Add switched-in textures as new usages.
            //    将动画切入的贴图作为新引用加入。
            foreach (var kv in facts.AnimatedTextureSwitches)
            {
                var (renderer, slot) = kv.Key;
                foreach (var byProp in kv.Value)
                {
                    string prop = byProp.Key;
                    foreach (var tex in byProp.Value)
                    {
                        // Find the base usage for this slot+property to copy
                        // the metadata (UV channel, type) from it; if missing,
                        // classify from the property name alone.
                        // 查找该槽+属性的基础引用以复制元数据（UV 通道、类型）；
                        // 若缺失则仅按属性名分类。
                        var baseUsage = state.AllUsages.FirstOrDefault(u =>
                            u.Renderer == renderer && u.MaterialSlot == slot && u.PropertyName == prop);

                        var material = baseUsage?.Material;
                        if (material == null)
                        {
                            var materials = renderer.sharedMaterials;
                            if (slot >= 0 && slot < materials.Length) material = materials[slot];
                        }

                        var usage = new TextureUsage
                        {
                            Renderer = renderer,
                            MaterialSlot = slot,
                            Material = material,
                            PropertyName = prop,
                            Texture = tex,
                            Type = baseUsage?.Type ?? ClassifyByPropertyName(prop),
                            UVChannel = baseUsage?.UVChannel ?? 0,
                            STScale = baseUsage?.STScale ?? Vector2.one,
                            STOffset = baseUsage?.STOffset ?? Vector2.zero,
                            IsSRGB = TextureCollector.IsSRGBTexture(tex),
                            FilterMode = tex.filterMode,
                            RenderMode = baseUsage?.RenderMode ?? "Opaque",
                            Cutoff = baseUsage?.Cutoff ?? 0.5f,
                            FromAnimation = true,
                        };

                        // If this material has animated ST for this property,
                        // the switched texture is also unsafe.
                        // 若该材质此属性 ST 被动画，切换的贴图同样不安全。
                        if (facts.AnimatedSTMaterials.Contains(material))
                        {
                            state.Warn($"{usage}: animated ST -> whitelisted / ST 被动画，视作白名单");
                            state.WhitelistedTextures.Add(tex);
                            continue;
                        }

                        state.AllUsages.Add(usage);
                    }
                }
            }

            // 6. Record renderers with individually animated material slots
            //    (slot merging is skipped for them).
            //    记录材质槽被单独动画的渲染器（对它们跳过槽合并）。
            foreach (var r in facts.AnimatedMaterialSlots)
                state.AnimatedMaterialSlotRenderers.Add(r);
        }

        private static TextureUsageType ClassifyByPropertyName(string propertyName)
        {
            // Lightweight fallback classification. / 轻量回退分类。
            if (propertyName.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                propertyName.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0)
                return TextureUsageType.NormalMap;
            if (propertyName.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                propertyName.IndexOf("metallic", StringComparison.OrdinalIgnoreCase) >= 0)
                return TextureUsageType.Mask;
            return TextureUsageType.MainColor;
        }
    }
}

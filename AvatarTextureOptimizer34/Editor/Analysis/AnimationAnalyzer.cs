// AvatarTextureOptimizer - AnimationAnalyzer
// EN: Scans avatar animators/clips for anything that affects texture/mesh/material state.
// CN: 扫描 Avatar 的动画器/片段，找出影响贴图/网格/材质状态的一切因素。
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Collects animation effects: material/texture switches, ST transforms, render mode & cutoff changes,
    /// renderer enablement, and max object scale. Results feed the UV group builder and the evaluator.
    /// CN: 收集动画影响：材质/贴图切换、ST 变换、渲染模式与 Cutoff 变更、渲染器启用、最大缩放。
    /// </summary>
    public static class AnimationAnalyzer
    {
        private const string MaterialsArray = "m_Materials.Array.data[";

        /// <summary>EN: Analyses all animators under the avatar root. / CN: 分析 Avatar 下所有 Animator。</summary>
        public static AnimationData Analyze(GameObject root, HashSet<UnityEngine.Object> whitelist,
            System.Action<float, string> progress)
        {
            var data = new AnimationData();
            var animators = root.GetComponentsInChildren<Animator>(true);
            var allClips = new List<AnimationClip>();
            int idx = 0;

            foreach (var animator in animators)
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                data.controllers.Add(new AnimatorControllerRef { controller = controller });
                CollectClips(controller, data.controllers[data.controllers.Count - 1].clips, allClips);
            }

            // EN: Also scan clips directly referenced anywhere under the avatar (e.g. VRC menu expressions use clips
            // via VRCPlayableLayerControl or custom components) — NDMF object scanning would be needed for full
            // coverage; we additionally pick up clips referenced by the descriptor's layers.
            // CN: 同时扫描 Avatar 下直接引用的片段（如 VRC 表达式菜单）；额外收录描述符动画层的片段。
            CollectDescriptorClips(root, allClips);

            foreach (var clip in allClips)
            {
                if (whitelist.Contains(clip)) { AtoLog.Detail($"Clip {clip.name} whitelisted"); continue; }
                if (data.clips.Contains(clip)) continue;
                data.clips.Add(clip);
                progress?.Invoke(0.3f + 0.5f * idx / Mathf.Max(1, allClips.Count), $"Animation: {clip.name}");
                idx++;
                ScanClip(root, clip, data);
            }
            return data;
        }

        private static void CollectClips(RuntimeAnimatorController controller, List<AnimationClip> outClips,
            List<AnimationClip> allClips)
        {
            var seen = new HashSet<AnimationClip>();
            foreach (var clip in controller.animationClips)
            {
                if (clip == null || seen.Contains(clip)) continue;
                seen.Add(clip);
                outClips.Add(clip);
                allClips.Add(clip);
            }
        }

        private static void CollectDescriptorClips(GameObject root, List<AnimationClip> allClips)
        {
#if ATO_VRCSDK3_AVATARS
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null) return;
            var layers = descriptor.baseAnimationLayers;
            foreach (var layer in layers)
            {
                if (layer == null || layer.animatorController == null) continue;
                foreach (var clip in layer.animatorController.animationClips)
                {
                    if (clip != null && !allClips.Contains(clip)) allClips.Add(clip);
                }
            }
#endif
        }

        private static void ScanClip(GameObject root, AnimationClip clip, AnimationData data)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (var binding in bindings)
            {
                var go = ResolvePath(root, binding.path);
                if (go == null) continue;

                // EN: Transform scale — max per axis across keyframes.
                // CN: 变换缩放 —— 逐轴取关键帧最大值。
                if (binding.propertyName == "m_LocalScale")
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null) continue;
                    // EN: Vector3 scale is split into .x/.y/.z bindings; take the max absolute value per component
                    // (area scale uses the max axis — conservative, prevents blur).
                    // CN: Vector3 缩放按 .x/.y/.z 分量绑定；逐分量取最大绝对值（面积按最大轴，保守防糊）。
                    float compMax = 0f;
                    foreach (var k in curve.keys) compMax = Mathf.Max(compMax, Mathf.Abs(k.value));
                    if (compMax > 0)
                    {
                        // EN: Record on the GameObject so the analyzer can walk the ancestor chain.
                        // CN: 记录在 GameObject 上，便于分析器沿祖先链查找。
                        data.maxScale.TryGetValue(go, out float cur);
                        if (compMax > cur) data.maxScale[go] = compMax;
                    }
                    continue;
                }

                if (binding.type == typeof(Transform) || binding.type == typeof(RectTransform)) continue;

                // EN: Renderer enablement.
                // CN: 渲染器启用。
                if (binding.propertyName == "m_Enabled" && typeof(Renderer).IsAssignableFrom(binding.type))
                {
                    var r = go.GetComponent(binding.type) as Renderer;
                    if (r != null) data.animatedEnabled.Add(r);
                    continue;
                }

                // EN: Renderer material-slot bindings: m_Materials.Array.data[i].<prop>
                // CN: 渲染器材质槽绑定：m_Materials.Array.data[i].<prop>
                if (binding.propertyName.StartsWith(MaterialsArray))
                {
                    int slot = ParseSlot(binding.propertyName);
                    string prop = ParseProp(binding.propertyName);
                    var renderer = go.GetComponent(binding.type) as Renderer;
                    if (renderer == null || slot < 0) continue;
                    var mats = renderer.sharedMaterials;
                    if (slot >= mats.Length || mats[slot] == null) continue;
                    var mat = mats[slot];

                    if (IsStProperty(prop))
                    {
                        data.stAnimated.Add((renderer, slot, prop));
                        AtoLog.Detail($"ST animated on {renderer.name} slot {slot} prop {prop} (clip {clip.name})");
                        continue;
                    }
                    if (IsRenderModeProperty(prop))
                    {
                        var mu = GetUsage(data, mat);
                        mu.animated = true;
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve != null)
                        {
                            if (prop == "_Cutoff" || prop == "_PreCutoff" || prop == "_AlphaCutoff")
                                foreach (var k in curve.keys) mu.AddCutoff(k.value);
                        }
                        // EN: Value-based mode for _Mode property (standard shader).
                        // CN: _Mode 属性的取值判定（标准着色器）。
                        if (prop == "_Mode")
                        {
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (curve != null)
                            {
                                foreach (var k in curve.keys)
                                    mu.AddMode(ModeFromFloat(k.value));
                            }
                        }
                        continue;
                    }
                    // EN: Any other animated material float/color property that could affect sampling is recorded so
                    // the classifier can decide; texture-affecting ones (_ST etc.) are handled above.
                    // CN: 其他可能影响采样的动画化材质标量/颜色属性均被记录，交由分类器决策。
                    mu = GetUsage(data, mat);
                    mu.animatedProperties.Add(prop);
                }
                else if (binding.propertyName.StartsWith("material."))
                {
                    // EN: Legacy "material._XXX_ST" style bindings (no m_Materials array) also imply ST animation.
                    // CN: 旧式 "material._XXX_ST" 绑定（无 m_Materials 数组）同样意味着 ST 动画。
                    string prop = binding.propertyName.Substring("material.".Length);
                    var renderer = go.GetComponent(binding.type) as Renderer;
                    if (renderer == null || renderer.sharedMaterials.Length == 0) continue;
                    var mat = renderer.sharedMaterials[0];
                    if (mat == null) continue;
                    if (IsStProperty(prop))
                    {
                        data.stAnimated.Add((renderer, 0, prop));
                        AtoLog.Detail($"ST animated (legacy) on {renderer.name} prop {prop}");
                        continue;
                    }
                    if (IsRenderModeProperty(prop))
                    {
                        var mu = GetUsage(data, mat);
                        mu.animated = true;
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve != null && (prop == "_Cutoff" || prop == "_PreCutoff" || prop == "_AlphaCutoff"))
                            foreach (var k in curve.keys) mu.AddCutoff(k.value);
                        continue;
                    }
                    GetUsage(data, mat).animatedProperties.Add(prop);
                }
            }

            // EN: Object reference curves: material slot swaps & texture property swaps.
            // CN: 对象引用曲线：材质槽切换与贴图属性切换。
            var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            foreach (var binding in objBindings)
            {
                var go = ResolvePath(root, binding.path);
                if (go == null) continue;
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null) continue;
                var values = keyframes.Where(k => k.value != null).Select(k => k.value).Distinct().ToList();
                if (values.Count == 0) continue;

                if (binding.propertyName.StartsWith(MaterialsArray))
                {
                    int slot = ParseSlot(binding.propertyName);
                    string prop = ParseProp(binding.propertyName);
                    var renderer = go.GetComponent(binding.type) as Renderer;
                    if (renderer == null || slot < 0) continue;

                    if (string.IsNullOrEmpty(prop))
                    {
                        // EN: Slot material swap.
                        // CN: 槽位材质切换。
                        var mats = values.OfType<Material>().ToList();
                        if (mats.Count > 0)
                        {
                            if (!data.animatedMaterials.TryGetValue((renderer, slot), out var set))
                                data.animatedMaterials[(renderer, slot)] = set = new HashSet<Material>();
                            foreach (var m in mats) set.Add(m);
                            data.individuallyAnimatedSlots.Add((renderer, slot));
                        }
                    }
                    else
                    {
                        // EN: Texture property swap on a slot material (e.g. _MainTex).
                        // CN: 槽位材质上的贴图属性切换（如 _MainTex）。
                        var texs = values.OfType<Texture2D>().ToList();
                        if (texs.Count > 0)
                        {
                            if (!data.animatedTextureProps.TryGetValue((renderer, slot, prop), out var set))
                                data.animatedTextureProps[(renderer, slot, prop)] = set = new HashSet<Texture2D>();
                            foreach (var t in texs) set.Add(t);
                        }
                    }
                }
                else if (binding.type == typeof(Material) || binding.type == typeof(UnityEngine.Object))
                {
                    // EN: Material asset property swaps (rare; e.g. clip animating a material asset texture).
                    // CN: 材质资产属性切换（少见；如片段直接动画化材质资产的贴图）。
                    var mat = go.GetComponent<Material>();
                    if (mat == null) continue;
                    var texs = values.OfType<Texture2D>().ToList();
                    if (texs.Count > 0)
                    {
                        if (!data.animatedMaterialAssetTextures.TryGetValue(mat, out var map))
                            data.animatedMaterialAssetTextures[mat] = map = new Dictionary<string, HashSet<Texture2D>>();
                        if (!map.TryGetValue(binding.propertyName, out var set))
                            map[binding.propertyName] = set = new HashSet<Texture2D>();
                        foreach (var t in texs) set.Add(t);
                    }
                }
            }
        }

        private static GameObject ResolvePath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var t = root.transform.Find(path);
            return t != null ? t.gameObject : null;
        }

        private static int ParseSlot(string propertyName)
        {
            int start = propertyName.IndexOf('[');
            int end = propertyName.IndexOf(']');
            if (start < 0 || end <= start) return -1;
            return int.TryParse(propertyName.Substring(start + 1, end - start - 1), out int s) ? s : -1;
        }

        private static string ParseProp(string propertyName)
        {
            int end = propertyName.IndexOf(']');
            if (end < 0 || end + 1 >= propertyName.Length) return "";
            return propertyName.Substring(end + 1).TrimStart('.');
        }

        private static bool IsStProperty(string prop)
        {
            return prop.EndsWith("_ST") || prop == "_MainTex_ST" || prop.EndsWith("_ScrollRotate") ||
                   prop.Contains("_ST.");
        }

        private static bool IsRenderModeProperty(string prop)
        {
            switch (prop)
            {
                case "_Mode": case "_Cutoff": case "_PreCutoff": case "_AlphaCutoff":
                case "_SrcBlend": case "_DstBlend": case "_AlphaToMask":
                    return true;
            }
            return false;
        }

        private static RenderMode ModeFromFloat(float v)
        {
            // EN: Standard shader _Mode: 0=Opaque 1=Cutout 2=Fade 3=Transparent.
            // CN: 标准着色器 _Mode：0=Opaque 1=Cutout 2=Fade 3=Transparent。
            if (v <= 0.5f) return RenderMode.Opaque;
            if (v <= 1.5f) return RenderMode.Cutout;
            return RenderMode.Blend;
        }

        private static MaterialUsage GetUsage(AnimationData data, Material mat)
        {
            if (!data.materialUsage.TryGetValue(mat, out var mu))
                data.materialUsage[mat] = mu = new MaterialUsage { material = mat };
            return mu;
        }
    }
}

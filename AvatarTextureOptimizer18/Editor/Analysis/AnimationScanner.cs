using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    // 动画扫描器：收集所有动画对材质/贴图/物体/形态键/缩放的影响。
    // Animation scanner: collects every animation-driven effect on materials, textures, objects, blend shapes and scale.
    //
    // 关注的影响（对后续优化至关重要）：
    // - 材质槽切换（m_Materials.Array.data[i] 对象引用动画）→ 该 UV 可能对应多份贴图；
    // - 材质 float/vector 属性动画（_Cutoff、_MainTex_ST、liltoon _MainTex_ScrollRotate、_UVMode 等）→ ST 变换/渲染模式可能被动画修改；
    // - 贴图属性动画（m_Materials.Array.data[i]._MainTex）→ 新增贴图引用；
    // - GameObject 启停 / Renderer 启停 → 决定是否处理该渲染器；
    // - Transform 缩放动画 → 面积估算按最大缩放；
    // - 形态键动画 → 面积估算仅取 0 与 100 两个状态的最大值。
    internal static class AnimationScanner
    {
        // m_Materials.Array.data[i] 或 m_Materials.Array.data[i].prop 匹配。Matches slot / slot-property bindings.
        private static readonly Regex SlotPropPattern = new Regex(@"^m_Materials\.Array\.data\[(\d+)\](?:\.(.*))?$", RegexOptions.Compiled);
        // blendShape.NAME 匹配。Matches blend shape bindings.
        private static readonly Regex BlendShapePattern = new Regex(@"^blendShape\.(.*)$", RegexOptions.Compiled);

        public static void Scan(ATOContext ctx, ATOReport.Stage stage)
        {
            var a = ctx.animations;

            // 1) 收集动画控制器来源：描述符自定义层（跳过默认层/空/禁用）+ 子级 Animator。
            // Collect controller sources: descriptor custom layers (skip default/null/disabled) + child Animators.
            var sources = new List<KeyValuePair<Transform, RuntimeAnimatorController>>();
            if (ctx.descriptor != null)
            {
                CollectFromLayers(ctx.descriptor.baseAnimationLayers, ctx.avatarRoot.transform, sources);
                CollectFromLayers(ctx.descriptor.specialAnimationLayers, ctx.avatarRoot.transform, sources);
            }
            foreach (var animator in ctx.avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                var c = animator.runtimeAnimatorController;
                if (c == null) continue;
                sources.Add(new KeyValuePair<Transform, RuntimeAnimatorController>(animator.transform, c));
            }

            // 2) 逐剪辑扫描。Scan every clip.
            var slotLookup = BuildSlotLookup(ctx.slots);
            int clipCount = 0;
            foreach (var src in sources)
            {
                foreach (var clip in CollectClips(src.Value))
                {
                    ctx.CheckCancelled();
                    clipCount++;
                    ScanClip(ctx, src.Key, clip, slotLookup, a, stage);
                }
            }

            stage.AddLine(string.Format(ATOLocalization.Tr("log.animScanSummary"), sources.Count, clipCount));
        }

        private static void CollectFromLayers(CustomAnimLayer[] layers, Transform root, List<KeyValuePair<Transform, RuntimeAnimatorController>> outList)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                // 默认层为 VRChat 内置控制器（不含材质动画），跳过。Default layers are VRChat built-ins (no material animation); skip.
                if (layer.isDefault || layer.animatorController == null) continue;
                if (!layer.isEnabled) continue;
                outList.Add(new KeyValuePair<Transform, RuntimeAnimatorController>(root, layer.animatorController));
            }
        }

        // 收集控制器全部剪辑（含 Override 控制器）。Collects all clips of a controller (incl. override controllers).
        private static List<AnimationClip> CollectClips(RuntimeAnimatorController controller)
        {
            var set = new HashSet<AnimationClip>();
            if (controller is AnimatorOverrideController aoc)
            {
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(overrides);
                foreach (var kv in overrides)
                {
                    if (kv.Value != null) set.Add(kv.Value);
                }
                if (aoc.runtimeAnimatorController is AnimatorController baseCtl)
                {
                    foreach (var c in baseCtl.animationClips)
                    {
                        if (c != null) set.Add(c);
                    }
                }
            }
            else if (controller is AnimatorController ac)
            {
                foreach (var c in ac.animationClips)
                {
                    if (c != null) set.Add(c);
                }
            }
            return new List<AnimationClip>(set);
        }

        private static Dictionary<Renderer, Dictionary<int, SlotEntry>> BuildSlotLookup(List<SlotEntry> slots)
        {
            var lookup = new Dictionary<Renderer, Dictionary<int, SlotEntry>>();
            foreach (var s in slots)
            {
                if (!lookup.TryGetValue(s.renderer, out var inner))
                {
                    inner = new Dictionary<int, SlotEntry>();
                    lookup[s.renderer] = inner;
                }
                if (!inner.ContainsKey(s.slotIndex)) inner[s.slotIndex] = s;
            }
            return lookup;
        }

        private static void ScanClip(ATOContext ctx, Transform baseT, AnimationClip clip,
            Dictionary<Renderer, Dictionary<int, SlotEntry>> slotLookup, AnimationAnalysis a, ATOReport.Stage stage)
        {
            if (!a.clipBase.ContainsKey(clip)) a.clipBase[clip] = baseT;
            if (!a.clipRefs.TryGetValue(clip, out var refs))
            {
                refs = new ClipRefs();
                a.clipRefs[clip] = refs;
            }

            // ---- float 曲线 ----
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.keys == null || curve.keys.Length == 0) continue;
                float min = float.MaxValue, max = float.MinValue;
                foreach (var k in curve.keys)
                {
                    if (k.value < min) min = k.value;
                    if (k.value > max) max = k.value;
                }
                ScanFloatBinding(baseT, binding, min, max, slotLookup, a, refs);
            }

            // ---- 对象引用曲线 ----
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null || curve.Length == 0) continue;
                ScanObjectBinding(baseT, binding, curve, slotLookup, a, refs);
            }
        }

        private static Transform ResolvePath(Transform baseT, string path)
        {
            if (string.IsNullOrEmpty(path)) return baseT;
            return baseT.Find(path);
        }

        private static void ScanFloatBinding(Transform baseT, EditorCurveBinding binding, float min, float max,
            Dictionary<Renderer, Dictionary<int, SlotEntry>> slotLookup, AnimationAnalysis a, ClipRefs refs)
        {
            var target = ResolvePath(baseT, binding.path);
            var type = binding.type;

            if (type == typeof(GameObject))
            {
                if (binding.propertyName == "m_IsActive" && target != null) a.objectToggled.Add(target.gameObject);
                return;
            }

            if (type == typeof(Renderer) || type == typeof(SkinnedMeshRenderer) || type == typeof(MeshRenderer))
            {
                if (target == null) return;
                if (binding.propertyName == "m_Enabled")
                {
                    var renderer = target.GetComponent<Renderer>();
                    if (renderer != null) a.rendererToggled.Add(renderer);
                }
                var bsMatch = BlendShapePattern.Match(binding.propertyName);
                if (bsMatch.Success)
                {
                    var sr = target.GetComponent<SkinnedMeshRenderer>();
                    if (sr != null)
                    {
                        // 每个形态键仅取 0 与 100 两个状态的最大值；不考虑负数/超过 100/排列组合（避免组合爆炸）。
                        // Only the max of the 0 and 100 states is used per blend shape; negatives, >100 and combinations are ignored.
                        float w = Mathf.Clamp(Mathf.Max(Mathf.Abs(min), Mathf.Abs(max)), 0f, 100f);
                        if (!a.blendShapeWeights.TryGetValue(sr, out var dict))
                        {
                            dict = new Dictionary<string, float>();
                            a.blendShapeWeights[sr] = dict;
                        }
                        string name = bsMatch.Groups[1].Value;
                        float old;
                        dict.TryGetValue(name, out old);
                        dict[name] = Mathf.Max(old, w);
                    }
                }
                return;
            }

            if (type == typeof(Transform))
            {
                if (target == null) return;
                float abs = Mathf.Max(Mathf.Abs(min), Mathf.Abs(max));
                Vector3 cur;
                if (!a.maxLocalScale.TryGetValue(target, out cur)) cur = Vector3.one;
                switch (binding.propertyName)
                {
                    case "m_LocalScale.x": cur.x = Mathf.Max(cur.x, abs); break;
                    case "m_LocalScale.y": cur.y = Mathf.Max(cur.y, abs); break;
                    case "m_LocalScale.z": cur.z = Mathf.Max(cur.z, abs); break;
                }
                a.maxLocalScale[target] = cur;
                return;
            }

            if (type == typeof(Material))
            {
                HandleMaterialFloatBinding(target, binding, min, max, slotLookup, a, refs);
                return;
            }

            ATOLog.Debug(string.Format("忽略未识别的动画曲线 / ignoring unknown curve binding: path={0} type={1} prop={2}", binding.path, type, binding.propertyName));
        }

        private static void HandleMaterialFloatBinding(Transform target, EditorCurveBinding binding, float min, float max,
            Dictionary<Renderer, Dictionary<int, SlotEntry>> slotLookup, AnimationAnalysis a, ClipRefs refs)
        {
            // 形式1：path 指向 Renderer，属性 m_Materials.Array.data[i].prop[.comp]
            // Form 1: path points at a renderer, property is m_Materials.Array.data[i].prop[.comp]
            var m = SlotPropPattern.Match(binding.propertyName);
            if (m.Success && target != null)
            {
                var renderer = target.GetComponent<Renderer>();
                if (renderer == null) return;
                int idx;
                if (!int.TryParse(m.Groups[1].Value, out idx)) return;
                string rest = m.Groups[2].Value;
                if (string.IsNullOrEmpty(rest)) return; // 纯材质槽切换由对象引用曲线处理。Pure slot swaps are handled by object-reference curves.
                rest = StripComponent(rest);

                var slot = FindSlot(slotLookup, renderer, idx);
                if (slot == null) return;
                RecordSlotRange(a, slot, rest, min, max);
                if (slot.material != null) refs.materials.Add(slot.material);
                return;
            }

            // 形式2：材质资产本身（path 为空）→ 无法定位到具体材质实例，仅记录 debug（实际动画均通过槽位绑定）。
            // Form 2: the material asset itself (empty path) → cannot resolve the instance; debug log only.
            ATOLog.Debug(string.Format("忽略无法定位的材质属性动画 / ignoring unresolvable material property animation: path={0} prop={1}", binding.path, binding.propertyName));
        }

        private static void ScanObjectBinding(Transform baseT, EditorCurveBinding binding, ObjectReferenceKeyframe[] curve,
            Dictionary<Renderer, Dictionary<int, SlotEntry>> slotLookup, AnimationAnalysis a, ClipRefs refs)
        {
            var target = ResolvePath(baseT, binding.path);
            var m = SlotPropPattern.Match(binding.propertyName);

            if (m.Success && target != null)
            {
                var renderer = target.GetComponent<Renderer>();
                if (renderer == null) return;
                int idx;
                if (!int.TryParse(m.Groups[1].Value, out idx)) return;
                string rest = m.Groups[2].Value;
                var slot = FindSlot(slotLookup, renderer, idx);

                foreach (var kf in curve)
                {
                    if (kf.value is Material mat)
                    {
                        // 材质槽切换：该槽可能使用多个材质（动画切换）。
                        // Material slot swap: this slot may use multiple materials over time.
                        a.materialSwapTargets.Add(mat);
                        a.materialsReferenced.Add(mat);
                        refs.materials.Add(mat);
                        if (slot != null)
                        {
                            slot.slotSwappedByAnimation = true;
                            if (!a.slotSwapMaterials.TryGetValue(slot, out var mats))
                            {
                                mats = new HashSet<Material>();
                                a.slotSwapMaterials[slot] = mats;
                            }
                            mats.Add(mat);
                        }
                    }
                    else if (kf.value is Texture2D tex)
                    {
                        // 贴图属性动画：动画直接切换贴图。
                        // Texture property animation: the animation swaps the texture directly.
                        a.animatedTextureTargets.Add(tex);
                        refs.textures.Add(tex);
                        if (slot != null && !string.IsNullOrEmpty(rest))
                        {
                            RecordSlotTextureProp(a, slot, rest);
                            if (!a.slotSwapTextures.TryGetValue(slot, out var texs))
                            {
                                texs = new HashSet<Texture2D>();
                                a.slotSwapTextures[slot] = texs;
                            }
                            texs.Add(tex);
                        }
                    }
                }
                return;
            }

            if (string.IsNullOrEmpty(binding.path) && binding.type == typeof(Material))
            {
                // 材质资产级对象引用动画（少见）。Material-asset-level object-reference animation (rare).
                foreach (var kf in curve)
                {
                    if (kf.value is Material mat)
                    {
                        a.materialsReferenced.Add(mat);
                        refs.materials.Add(mat);
                    }
                    else if (kf.value is Texture2D tex)
                    {
                        a.animatedTextureTargets.Add(tex);
                        refs.textures.Add(tex);
                    }
                }
            }
        }

        // 记录槽位 float 属性动画范围（属性名已去除分量后缀）。Records a slot's animated float-property range.
        private static void RecordSlotRange(AnimationAnalysis a, SlotEntry slot, string prop, float min, float max)
        {
            if (!a.slotFloatRanges.TryGetValue(slot, out var dict))
            {
                dict = new Dictionary<string, Vector2>();
                a.slotFloatRanges[slot] = dict;
            }
            Vector2 old;
            if (dict.TryGetValue(prop, out old))
            {
                dict[prop] = new Vector2(Mathf.Min(old.x, min), Mathf.Max(old.y, max));
            }
            else
            {
                dict[prop] = new Vector2(min, max);
            }
        }

        // 记录槽位贴图属性动画。Records a slot's animated texture property.
        private static void RecordSlotTextureProp(AnimationAnalysis a, SlotEntry slot, string prop)
        {
            if (!a.slotTexturePropsAnimated.TryGetValue(slot, out var set))
            {
                set = new HashSet<string>();
                a.slotTexturePropsAnimated[slot] = set;
            }
            set.Add(prop);
        }

        // 去除向量属性的 .x/.y/.z/.w 分量后缀。Strips the .x/.y/.z/.w component suffix of vector properties.
        private static string StripComponent(string prop)
        {
            if (prop.EndsWith(".x") || prop.EndsWith(".y") || prop.EndsWith(".z") || prop.EndsWith(".w"))
            {
                return prop.Substring(0, prop.Length - 2);
            }
            return prop;
        }

        private static SlotEntry FindSlot(Dictionary<Renderer, Dictionary<int, SlotEntry>> lookup, Renderer renderer, int index)
        {
            Dictionary<int, SlotEntry> inner;
            if (!lookup.TryGetValue(renderer, out inner)) return null;
            SlotEntry slot;
            return inner.TryGetValue(index, out slot) ? slot : null;
        }
    }
}

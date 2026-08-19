// ============================================================================
// AnimationStage.cs — 阶段2：动画分析 / Stage 2: animation analysis
// (EN) Walks all animation clips referenced by Animators and legacy Animation
//      components, and discovers:
//        - material slot switches (m_Materials.Array.data[i])
//        - texture switches (material.<prop> object curves)
//        - ST/scroll/rotate animations (→ whitelist the affected texture)
//        - render mode / cutoff animations (→ strictest quality)
//        - GameObject enable toggles (→ EnabledByAnimation)
//        - transform scale animations (→ max scale for area)
//      Textures switched in by animation are merged into the same slot, so they
//      naturally join the original UV group.
// (ZH) 遍历 Animator 与旧版 Animation 引用的所有动画片段，发现：
//        材质槽切换、贴图切换、ST/滚动/旋转变换（→白名单）、渲染模式/Cutoff 动画
//        （→最严苛质量）、GameObject 启停（→动画启用）、缩放动画（→最大面积）。
//      动画切换进来的贴图并入原槽位，自然加入原 UV 组。
// ============================================================================

using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class AnimationStage
    {
        private readonly ATOBuildContext _ctx;
        private readonly ATOCollectResult _collect;

        private static readonly Regex MatSlotRe = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);
        private static readonly Regex MatPropRe = new Regex(@"^material\.(.+)$", RegexOptions.Compiled);

        public AnimationStage(ATOBuildContext ctx, ATOCollectResult collect)
        {
            _ctx = ctx;
            _collect = collect;
        }

        public void Run()
        {
            var seen = new HashSet<AnimationClip>();

            // Animator 控制器 / animator controllers
            foreach (var animator in _ctx.AvatarRoot.GetComponentsInChildren<Animator>(true))
            {
                var controller = animator.runtimeAnimatorController;
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                {
                    if (clip == null || !seen.Add(clip)) continue;
                    AnalyzeClip(clip, animator.transform);
                }
            }

            // 旧版 Animation 组件 / legacy Animation components
            foreach (var anim in _ctx.AvatarRoot.GetComponentsInChildren<Animation>(true))
            {
                foreach (var clip in AnimationUtility.GetAnimationClips(anim.gameObject))
                {
                    if (clip == null || !seen.Add(clip)) continue;
                    AnalyzeClip(clip, anim.transform);
                }
            }

            ATOLog.VerboseLog($"[animations] {seen.Count} unique clips analyzed");
        }

        private void AnalyzeClip(AnimationClip clip, Transform root)
        {
            // 对象引用曲线（材质槽切换、贴图切换）/ object reference curves
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                HandleObjectBinding(clip, binding, root);
            }

            // 浮点曲线（启停、ST、滚动旋转、渲染模式、缩放）/ float curves
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                HandleFloatBinding(clip, binding, root);
            }
        }

        // ---------------------------------------------------------------------
        // 对象引用曲线 / object reference curves
        // ---------------------------------------------------------------------
        private void HandleObjectBinding(AnimationClip clip, EditorCurveBinding binding, Transform root)
        {
            var go = ResolvePath(root, binding.path);
            if (go == null) return;

            // 材质槽切换 / material slot switch
            var slotMatch = MatSlotRe.Match(binding.propertyName);
            if (slotMatch.Success)
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) return;
                int slotIndex = int.Parse(slotMatch.Groups[1].Value);
                var info = FindRenderer(renderer);
                if (info == null) return;

                foreach (var frame in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                {
                    var mat = frame.value as Material;
                    if (mat == null) continue;
                    if (!info.Slots.Exists(s => s.SlotIndex == slotIndex)) continue;
                    var slot = info.Slots.Find(s => s.SlotIndex == slotIndex);
                    if (!slot.SwitchedMaterials.Contains(mat))
                    {
                        slot.SwitchedMaterials.Add(mat);
                        MergeMaterialTextures(info, slot, mat);
                    }
                }
                return;
            }

            // 贴图切换 / texture switch within a material property
            var propMatch = MatPropRe.Match(binding.propertyName);
            if (propMatch.Success)
            {
                var propName = propMatch.Groups[1].Value;
                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) return;
                var info = FindRenderer(renderer);
                if (info == null) return;

                foreach (var frame in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                {
                    var tex = frame.value as Texture2D;
                    if (tex == null) continue;
                    AddAnimatedTexture(info, propName, tex);
                }
            }
        }

        // ---------------------------------------------------------------------
        // 浮点曲线 / float curves
        // ---------------------------------------------------------------------
        private void HandleFloatBinding(AnimationClip clip, EditorCurveBinding binding, Transform root)
        {
            var go = ResolvePath(root, binding.path);
            if (go == null) return;

            var prop = binding.propertyName;

            // GameObject 启用开关 / enable toggle
            if (prop == "m_IsActive")
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var info = FindRenderer(renderer);
                    if (info != null) info.EnabledByAnimation = true;
                }
                return;
            }

            // 缩放动画 / scale animation (area)
            if (prop == "m_LocalScale.x" || prop == "m_LocalScale.y" || prop == "m_LocalScale.z")
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var info = FindRenderer(renderer);
                    if (info != null)
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve != null)
                        {
                            float maxVal = 0f;
                            foreach (var k in curve.keys) maxVal = Mathf.Max(maxVal, Mathf.Abs(k.value));
                            if (prop == "m_LocalScale.x") info.AnimScale.x = Mathf.Max(info.AnimScale.x, maxVal);
                            if (prop == "m_LocalScale.y") info.AnimScale.y = Mathf.Max(info.AnimScale.y, maxVal);
                            if (prop == "m_LocalScale.z") info.AnimScale.z = Mathf.Max(info.AnimScale.z, maxVal);
                        }
                    }
                }
                return;
            }

            // 材质属性动画 / material property animation
            var propMatch = MatPropRe.Match(prop);
            if (propMatch.Success)
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) return;
                var info = FindRenderer(renderer);
                if (info == null) return;

                var matProp = propMatch.Groups[1].Value;

                // ST 变换 / ST scale-offset
                if (matProp.EndsWith("_ST.x") || matProp.EndsWith("_ST.y") || matProp.EndsWith("_ST.z") || matProp.EndsWith("_ST.w"))
                {
                    MarkTransform(info, matProp.Substring(0, matProp.Length - 2)); // strip ".x"
                    return;
                }

                // 滚动/旋转 / scroll-rotate
                if (matProp.Contains("_ScrollRotate") || matProp.Contains("Pivot") || matProp.EndsWith("Angle"))
                {
                    var baseProp = matProp.Contains("_ScrollRotate")
                        ? matProp.Substring(0, matProp.IndexOf("_ScrollRotate") + "_ScrollRotate".Length)
                        : matProp;
                    MarkTransform(info, baseProp);
                    return;
                }

                // 渲染模式 / render mode
                if (matProp == "_RenderMode" || matProp == "_TransparentMode" || matProp == "_Cutoff")
                {
                    foreach (var slot in info.Slots)
                    {
                        slot.AnimatedRenderMode = true;
                        if (matProp == "_Cutoff")
                        {
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (curve != null)
                            {
                                float minVal = float.MaxValue;
                                foreach (var k in curve.keys) minVal = Mathf.Min(minVal, k.value);
                                slot.MinCutoff = Mathf.Min(slot.MinCutoff, minVal);
                            }
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // 辅助 / helpers
        // ---------------------------------------------------------------------

        /// <summary>(EN) Mark the slot-texture (by base property) as having an animated transform. (ZH) 将对应贴图标记为存在动画变换。</summary>
        private void MarkTransform(ATORendererInfo info, string baseProp)
        {
            foreach (var slot in info.Slots)
            {
                foreach (var t in slot.Textures)
                {
                    // 匹配属性名（忽略 _ST 后缀差异）/ match by property name (ignoring _ST suffix)
                    if (t.PropertyName == baseProp || baseProp.StartsWith(t.PropertyName))
                    {
                        t.HasTransform = true;
                    }
                }
            }
        }

        /// <summary>(EN) Add a texture switched in by animation to the matching slot-textures. (ZH) 将动画切换的贴图加入对应槽位。</summary>
        private void AddAnimatedTexture(ATORendererInfo info, string propName, Texture2D tex)
        {
            // 找到该属性归属的槽位（其材质声明了该属性）/ find slots whose material declares the property
            foreach (var slot in info.Slots)
            {
                if (slot.Material == null) continue;
                if (!slot.Material.HasProperty(propName)) continue;

                // 去重：若该属性已存在且贴图不同，则并入（同一 UV 组）/ dedup & merge
                var existing = slot.Find(propName, 0);
                if (existing != null && existing.Ref.Texture == tex) continue;

                var entry = new ATOSlotTexture
                {
                    Ref = CreateOrGetRef(tex, info),
                    PropertyName = propName,
                    UvChannel = GuessUvChannel(propName),
                };
                slot.Textures.Add(entry);
                ATOLog.VerboseLog($"[animations] texture switch {tex.name} -> {propName} on {info}");
            }
        }

        /// <summary>(EN) Analyze a switched-in material and merge its textures into the slot. (ZH) 分析切换进来的材质并将其贴图并入槽位。</summary>
        private void MergeMaterialTextures(ATORendererInfo info, ATOSlot slot, Material mat)
        {
            foreach (var entry in ATOShaderAnalysis.AnalyzeMaterial(mat))
            {
                var existing = slot.Find(entry.PropertyName, entry.UvChannel);
                if (existing != null && existing.Ref.Texture == entry.Ref.Texture) continue;

                entry.Ref = CreateOrGetRef(entry.Ref.Texture, info);
                slot.Textures.Add(entry);
                ATOLog.VerboseLog($"[animations] material switch adds {entry.Ref.Texture.name} -> {entry.PropertyName} on {info}");
            }
        }

        /// <summary>(EN) Get-or-create a canonical texture ref and register it. (ZH) 获取或创建规范贴图引用并注册。</summary>
        private ATOTextureRef CreateOrGetRef(Texture2D tex, ATORendererInfo info)
        {
            if (_collect.Canonical.TryGetValue(tex, out var existing)) return existing;

            var ref_ = new ATOTextureRef
            {
                Texture = tex,
                Whitelisted = ATOWhitelist.TextureWhitelisted(tex, info),
            };
            ref_.ImportSignature = ATOTextureIO.GetImportSignature(tex);
            ref_.PixelSignature = ATOTextureIO.GetPixelSignature(tex);
            ATOTextureIO.Classify(ref_);
            _collect.Canonical[tex] = ref_;
            return ref_;
        }

        private int GuessUvChannel(string propName)
        {
            var n = propName.ToLowerInvariant();
            if (n.Contains("3rd")) return 2;
            if (n.Contains("2nd")) return 1;
            return 0;
        }

        private ATORendererInfo FindRenderer(Renderer renderer)
        {
            foreach (var r in _collect.Renderers)
                if (r.Renderer == renderer) return r;
            return null;
        }

        private static Transform ResolvePath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            return root.Find(path);
        }
    }
}

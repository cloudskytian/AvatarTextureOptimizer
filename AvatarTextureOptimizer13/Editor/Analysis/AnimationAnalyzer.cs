// ATO — Avatar Texture Optimizer
// Animation analysis: discovers which renderers / material slots / texture properties /
// blend shapes / scales / render-modes are animated, so that the optimizer can account
// for material swaps, animated ST transforms and animated scale (area).
// 动画分析：发现哪些渲染器/材质槽/贴图属性/形态键/缩放/渲染模式被动画修改，
// 使优化器能处理材质切换、动画 ST 变换与动画缩放（面积）。
//
// Note: animation paths are resolved best-effort (exact relative path → suffix → name),
// since MA/AAO may later merge or rename objects. When a target cannot be resolved,
// ATO treats the affected textures conservatively (whitelist + warning).
// 注意：动画路径按"精确相对路径 → 后缀 → 名称"尽力解析（因为 MA/AAO 之后可能合并或改名对象）。
// 无法解析目标时，ATO 保守处理相关贴图（白名单 + 警告）。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;
#if ATO_VRCSDK3
using VRC.SDK3.Avatars.Components;
#endif

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Collects and classifies animation curves affecting textures/UV. 收集并分类影响贴图/UV 的动画曲线。
    /// </summary>
    public static class AnimationAnalyzer
    {
        private const float StEps = 1e-3f;

        /// <summary>
        /// Run animation analysis for the avatar and populate <paramref name="result"/>.
        /// 对 Avatar 执行动画分析并填充 <paramref name="result"/>。
        /// </summary>
        public static void Analyze(GameObject avatarRoot, ATOAnalysisResult result)
        {
            var clips = CollectClips(avatarRoot);
            var byName = BuildNameIndex(avatarRoot);
            var byPath = BuildPathIndex(avatarRoot);
            var anim = result.animation;

            foreach (var clip in clips)
            {
                if (clip == null) continue;
                AnalyzeClip(clip, byPath, byName, anim);
            }
        }

        private static List<AnimationClip> CollectClips(GameObject avatarRoot)
        {
            var clips = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
#if ATO_VRCSDK3
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                foreach (var layer in AllLayers(descriptor))
                {
                    CollectFromController(layer.animatorController, clips, seen);
                }
            }
#endif
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                CollectFromController(animator.runtimeAnimatorController, clips, seen);
            }
            // Fallback: any Animation component in the hierarchy. 兜底：层级中的 Animation 组件。
            foreach (var animation in avatarRoot.GetComponentsInChildren<Animation>(true))
            {
                if (animation.clip != null && seen.Add(animation.clip)) clips.Add(animation.clip);
            }
            return clips;
        }

#if ATO_VRCSDK3
        private static IEnumerable<VRCAvatarDescriptor.CustomAnimLayer> AllLayers(VRCAvatarDescriptor d)
        {
            foreach (var l in d.baseAnimationLayers) yield return l;
            foreach (var l in d.specialAnimationLayers) yield return l;
        }
#endif

        private static void CollectFromController(RuntimeAnimatorController controller, List<AnimationClip> clips, HashSet<AnimationClip> seen)
        {
            if (controller == null) return;
            foreach (var c in controller.animationClips)
            {
                if (c != null && seen.Add(c)) clips.Add(c);
            }
        }

        private static Dictionary<string, List<GameObject>> BuildNameIndex(GameObject root)
        {
            var index = new Dictionary<string, List<GameObject>>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string key = t.name.ToLowerInvariant();
                if (!index.TryGetValue(key, out var list)) { list = new List<GameObject>(); index[key] = list; }
                list.Add(t.gameObject);
            }
            return index;
        }

        private static Dictionary<string, GameObject> BuildPathIndex(GameObject root)
        {
            var index = new Dictionary<string, GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string path = RelativePath(root.transform, t);
                if (!index.ContainsKey(path)) index[path] = t.gameObject;
            }
            return index;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Resolve a binding path to GameObjects. 将绑定路径解析为 GameObject 列表。</summary>
        private static List<GameObject> Resolve(string path, Dictionary<string, GameObject> byPath, Dictionary<string, List<GameObject>> byName)
        {
            var found = new List<GameObject>();
            if (byPath.TryGetValue(path, out var exact)) found.Add(exact);
            if (found.Count == 0)
            {
                // Suffix match. 后缀匹配。
                foreach (var kv in byPath)
                {
                    if (kv.Key.EndsWith("/" + path) || kv.Key == path) found.Add(kv.Value);
                }
            }
            if (found.Count == 0)
            {
                string name = path;
                int slash = path.LastIndexOf('/');
                if (slash >= 0) name = path.Substring(slash + 1);
                if (byName.TryGetValue(name.ToLowerInvariant(), out var list)) found.AddRange(list);
            }
            return found;
        }

        private static void AnalyzeClip(AnimationClip clip, Dictionary<string, GameObject> byPath, Dictionary<string, List<GameObject>> byName, ATOAnimationState anim)
        {
            // Float curves. 浮点曲线。
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                string prop = binding.propertyName;
                string lower = prop.ToLowerInvariant();

                if (lower == "m_enabled")
                {
                    foreach (var go in Resolve(binding.path, byPath, byName))
                    {
                        var r = go.GetComponent<Renderer>();
                        if (r != null) anim.animatedEnableRenderers.Add(r);
                    }
                    continue;
                }

                if (lower.StartsWith("m_localscale"))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    float maxAbs = MaxAbs(curve);
                    float factor = Mathf.Max(1f, maxAbs);
                    foreach (var go in Resolve(binding.path, byPath, byName))
                    {
                        var r = go.GetComponent<Renderer>();
                        if (r != null)
                        {
                            if (!anim.animatedScaleFactors.TryGetValue(r, out var cur) || factor > cur)
                                anim.animatedScaleFactors[r] = factor;
                        }
                    }
                    continue;
                }

                if (lower.StartsWith("blendshape."))
                {
                    string bsName = prop.Substring("blendShape.".Length);
                    foreach (var go in Resolve(binding.path, byPath, byName))
                    {
                        var smr = go.GetComponent<SkinnedMeshRenderer>();
                        if (smr == null || smr.sharedMesh == null) continue;
                        int idx = smr.sharedMesh.GetBlendShapeIndex(bsName);
                        if (idx < 0) continue;
                        if (!anim.animatedBlendShapes.TryGetValue(smr, out var set)) { set = new HashSet<int>(); anim.animatedBlendShapes[smr] = set; }
                        set.Add(idx);
                    }
                    continue;
                }

                // Material property curves (render mode / cutoff / ST). 材质属性曲线。
                if (lower.Contains("material") || prop.Contains("m_Materials"))
                {
                    if (lower.Contains("_cutoff") || lower.Contains("_srcblend") || lower.Contains("_dstblend") ||
                        lower.Contains("_alphatomask") || lower.Contains("_renderqueue") || lower.Contains("_zwrite") ||
                        lower.Contains("_cull"))
                    {
                        // Animated render-mode-ish property; mark affected materials.
                        // 动画修改渲染模式类属性；标记受影响材质。
                        foreach (var go in Resolve(binding.path, byPath, byName))
                        {
                            var r = go.GetComponent<Renderer>();
                            if (r != null)
                                foreach (var mat in r.sharedMaterials)
                                    if (mat != null) anim.animatedRenderMode.Add(mat);
                        }
                    }

                    if (lower.Contains("_st.") || lower.Contains("_scrollrotate"))
                    {
                        // Animated ST / scroll-rotate → will be treated as whitelist in Pass 1.
                        // 动画 ST / 滚动旋转 → 在 Pass 1 中按白名单处理。
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve != null)
                        {
                            bool identity = IsIdentityST(prop, curve);
                            if (!identity)
                            {
                                string texProp = ExtractTextureProperty(prop);
                                foreach (var go in Resolve(binding.path, byPath, byName))
                                {
                                    var r = go.GetComponent<Renderer>();
                                    if (r != null)
                                        foreach (var mat in r.sharedMaterials)
                                            if (mat != null) anim.animatedTextureProps.Add((mat, texProp));
                                }
                            }
                        }
                    }
                }
            }

            // Object reference curves: material slot swaps + texture swaps. 对象引用曲线：材质槽切换与贴图切换。
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                string prop = binding.propertyName;
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null || curve.Length == 0) continue;

                var resolved = Resolve(binding.path, byPath, byName);
                if (resolved.Count == 0) continue;

                if (prop.Contains("m_Materials.Array.data"))
                {
                    int slot = ExtractSlotIndex(prop);
                    foreach (var go in resolved)
                    {
                        var r = go.GetComponent<Renderer>();
                        if (r == null) continue;
                        anim.animatedMaterialSlots.Add((r, slot));
                    }
                }
            }
        }

        private static float MaxAbs(AnimationCurve curve)
        {
            if (curve == null || curve.keys.Length == 0) return 1f;
            float m = 0f;
            foreach (var k in curve.keys) m = Mathf.Max(m, Mathf.Abs(k.value));
            return m;
        }

        private static bool IsIdentityST(string prop, AnimationCurve curve)
        {
            bool isX = prop.EndsWith(".x"), isY = prop.EndsWith(".y");
            float target = (isX || isY) ? 1f : 0f;
            foreach (var k in curve.keys)
            {
                if (Mathf.Abs(k.value - target) > StEps) return false;
            }
            return true;
        }

        private static string ExtractTextureProperty(string prop)
        {
            // e.g. "m_Materials.Array.data[0]._MainTex_ST.x" → "_MainTex"
            // 例如 "m_Materials.Array.data[0]._MainTex_ST.x" → "_MainTex"
            int dot = prop.LastIndexOf('.');
            string before = dot > 0 ? prop.Substring(0, dot) : prop;
            int underscore = before.LastIndexOf('_');
            if (underscore < 0) return prop;
            string candidate = before.Substring(underscore);
            if (candidate.EndsWith("_ST") || candidate.EndsWith("_ScrollRotate"))
                return candidate.Substring(0, candidate.Length - (candidate.EndsWith("_ST") ? 3 : 12));
            return prop;
        }

        private static int ExtractSlotIndex(string prop)
        {
            int start = prop.IndexOf('[');
            int end = prop.IndexOf(']', start + 1);
            if (start >= 0 && end > start)
            {
                if (int.TryParse(prop.Substring(start + 1, end - start - 1), out int idx)) return idx;
            }
            return -1;
        }
    }
}

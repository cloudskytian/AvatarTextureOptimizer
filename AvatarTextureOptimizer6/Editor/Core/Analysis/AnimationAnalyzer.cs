using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>动画分析结果。</summary>
    public sealed class AnimationAnalysis
    {
        /// <summary>被动画（可能）启用的 Renderer。</summary>
        public readonly HashSet<Renderer> RenderersMaybeEnabled = new HashSet<Renderer>();

        /// <summary>每个材质槽可被动画赋值的材质集合。(renderer, slotIndex) → materials</summary>
        public readonly Dictionary<(Renderer, int), HashSet<Material>> SlotMaterialSwaps =
            new Dictionary<(Renderer, int), HashSet<Material>>();

        /// <summary>动画中直接切换的贴图。(renderer, slotIndex, propName) → textures</summary>
        public readonly Dictionary<(Renderer, int, string), HashSet<Texture>> TextureSwaps =
            new Dictionary<(Renderer, int, string), HashSet<Texture>>();

        /// <summary>被动画修改的材质属性（含 ST/渲染模式/Cutoff 等）。键 (renderer, slotIndex, propName)</summary>
        public readonly HashSet<(Renderer, int, string)> AnimatedMaterialProperties =
            new HashSet<(Renderer, int, string)>();

        /// <summary>被动画修改的浮点属性的值范围（如 _Cutoff）。键 (renderer, slotIndex, propName)</summary>
        public readonly Dictionary<(Renderer, int, string), (float min, float max)> AnimatedFloatRanges =
            new Dictionary<(Renderer, int, string), (float min, float max)>();

        /// <summary>渲染器路径上动画缩放造成的最大面积系数（即 maxScaleFactor² 的因子）。</summary>
        public readonly Dictionary<Renderer, float> AreaScaleFactor = new Dictionary<Renderer, float>();

        /// <summary>全部收集到的动画剪辑。</summary>
        public readonly HashSet<AnimationClip> Clips = new HashSet<AnimationClip>();

        public bool TryGetAreaScaleFactor(Renderer r, out float f) => AreaScaleFactor.TryGetValue(r, out f);
    }

    /// <summary>
    /// 动画分析器：收集 Avatar 上全部动画（Animator/Animation/Descriptor 上的控制器），
    /// 提取材质槽切换、贴图切换、材质属性（含 ST/Cutoff/混合模式）动画、物体启用、缩放动画。
    /// 依据：Unity 动画绑定属性命名（m_Materials.Array.data[i]._Prop / m_IsActive / m_LocalScale.x）。
    /// </summary>
    public sealed class AnimationAnalyzer
    {
        private readonly GameObject _root;
        private readonly AnimationAnalysis _result = new AnimationAnalysis();
        private readonly Dictionary<string, Transform> _pathCache = new Dictionary<string, Transform>();
        private readonly Dictionary<Renderer, Dictionary<int, Transform>> _rendererCache =
            new Dictionary<Renderer, Dictionary<int, Transform>>();

        public AnimationAnalyzer(GameObject root)
        {
            _root = root;
        }

        public AnimationAnalysis Analyze()
        {
            CollectControllers();
            return _result;
        }

        // ------------------------------------------------------------------
        // 控制器收集
        // ------------------------------------------------------------------

        private void CollectControllers()
        {
            var controllers = new HashSet<RuntimeAnimatorController>();

            // Animator 组件
            foreach (var animator in _root.GetComponentsInChildren<Animator>(true))
            {
                var c = animator.runtimeAnimatorController;
                if (c != null) controllers.Add(c);
            }

            // Animation 组件
            foreach (var anim in _root.GetComponentsInChildren<Animation>(true))
            {
                foreach (AnimationState state in anim)
                {
                    if (state != null && state.clip != null) _result.Clips.Add(state.clip);
                }
            }

            // VRC_AvatarDescriptor 上的控制器（反射，字段类型 AnimatorController）
            var descriptor = _root.GetComponentInChildren<Component>();
            var desc = _root.GetComponent("VRC.SDKBase.VRC_AvatarDescriptor");
            if (desc == null) desc = _root.GetComponentInChildren<MonoBehaviour>(true)?.GetComponent("VRC.SDKBase.VRC_AvatarDescriptor");
            if (desc != null)
            {
                foreach (var field in desc.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(AnimatorController))
                    {
                        var ctrl = field.GetValue(desc) as AnimatorController;
                        if (ctrl != null) controllers.Add(ctrl);
                    }
                }
            }

            foreach (var ctrl in controllers)
            {
                CollectClipsFromController(ctrl);
            }
        }

        private void CollectClipsFromController(RuntimeAnimatorController controller)
        {
            if (controller == null) return;
            var visited = new HashSet<RuntimeAnimatorController>();

            // AnimatorOverrideController 解包
            var stack = new Stack<RuntimeAnimatorController>();
            stack.Push(controller);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (c == null || !visited.Add(c)) continue;

                if (c is AnimatorOverrideController overrideController)
                {
                    foreach (var clip in overrideController.clips)
                    {
                        if (clip.originalClip != null) ProcessClip(clip.originalClip);
                        if (clip.overrideClip != null) ProcessClip(clip.overrideClip);
                    }
                    var inner = overrideController.runtimeAnimatorController;
                    if (inner != null) stack.Push(inner);
                    continue;
                }

                if (c is AnimatorController ac)
                {
                    foreach (var layer in ac.layers)
                    {
                        foreach (var state in layer.stateMachine.states)
                        {
                            CollectMotion(state.state.motion);
                        }
                        CollectStateMachine(layer.stateMachine);
                    }
                    foreach (var clip in ac.animationClips) ProcessClip(clip);
                }
            }
        }

        private void CollectStateMachine(AnimatorStateMachine sm)
        {
            if (sm == null) return;
            foreach (var sub in sm.stateMachines) CollectStateMachine(sub.stateMachine);
            foreach (var st in sm.states)
            {
                if (st.state == null) continue;
                CollectMotion(st.state.motion);
                // 过渡上的 motion（极少见，仍收集）
                if (st.state.transitions != null)
                {
                    foreach (var t in st.state.transitions)
                    {
                        if (t != null) CollectMotion(t.destinationState != null ? t.destinationState.motion : null);
                    }
                }
            }
        }

        private void CollectMotion(Motion motion)
        {
            if (motion == null) return;
            if (motion is AnimationClip clip)
            {
                ProcessClip(clip);
            }
            else if (motion is BlendTree tree)
            {
                foreach (var child in tree.children) CollectMotion(child.motion);
            }
        }

        // ------------------------------------------------------------------
        // 剪辑曲线解析
        // ------------------------------------------------------------------

        private void ProcessClip(AnimationClip clip)
        {
            if (clip == null) return;
            if (!_result.Clips.Add(clip)) return;

            // 浮点曲线
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                ProcessFloatBinding(clip, binding);
            }
            // 对象引用曲线（材质/贴图切换）
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                ProcessObjectBinding(clip, binding);
            }
        }

        private void ProcessFloatBinding(AnimationClip clip, EditorCurveBinding binding)
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null) return;
            var t = FindPath(binding.path);
            if (t == null) return;

            var prop = binding.propertyName;

            if (prop == "m_IsActive")
            {
                // 该对象可能被启用
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    _result.RenderersMaybeEnabled.Add(r);
                }
                return;
            }

            if (prop.StartsWith("m_LocalScale"))
            {
                RecordScaleAnimation(t, prop, curve);
                return;
            }

            if (IsMaterialProperty(prop, out int slotIndex))
            {
                var renderer = t.GetComponent<Renderer>();
                if (renderer == null) return;
                var propName = ExtractMaterialPropName(prop);
                _result.AnimatedMaterialProperties.Add((renderer, slotIndex, propName));

                float minV = float.MaxValue, maxV = float.MinValue;
                foreach (var key in curve.keys)
                {
                    minV = Mathf.Min(minV, key.value);
                    maxV = Mathf.Max(maxV, key.value);
                }
                if (minV <= maxV)
                {
                    _result.AnimatedFloatRanges[(renderer, slotIndex, propName)] = (minV, maxV);
                }
                // 渲染模式/透明度相关属性动画 → 视为需要 alpha 指标
                _result.RenderersMaybeEnabled.Add(renderer);
            }
        }

        private void ProcessObjectBinding(AnimationClip clip, EditorCurveBinding binding)
        {
            var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (frames == null || frames.Length == 0) return;
            var t = FindPath(binding.path);
            if (t == null) return;
            var prop = binding.propertyName;

            if (IsMaterialProperty(prop, out int slotIndex))
            {
                var renderer = t.GetComponent<Renderer>();
                if (renderer == null) return;
                var propName = ExtractMaterialPropName(prop);
                foreach (var frame in frames)
                {
                    if (frame.value is Material mat)
                    {
                        AddSlotSwap(renderer, slotIndex, mat);
                    }
                    else if (frame.value is Texture tex)
                    {
                        AddTextureSwap(renderer, slotIndex, propName, tex);
                    }
                }
            }
        }

        private void AddSlotSwap(Renderer renderer, int slot, Material mat)
        {
            if (!_result.SlotMaterialSwaps.TryGetValue((renderer, slot), out var set))
            {
                set = new HashSet<Material>();
                _result.SlotMaterialSwaps[(renderer, slot)] = set;
            }
            set.Add(mat);
        }

        private void AddTextureSwap(Renderer renderer, int slot, string propName, Texture tex)
        {
            var key = (renderer, slot, propName);
            if (!_result.TextureSwaps.TryGetValue(key, out var set))
            {
                set = new HashSet<Texture>();
                _result.TextureSwaps[key] = set;
            }
            set.Add(tex);
        }

        private void RecordScaleAnimation(Transform t, string prop, AnimationCurve curve)
        {
            // 记录该变换动画缩放的最大值（各轴）
            float maxAbs = 0f;
            foreach (var key in curve.keys) maxAbs = Mathf.Max(maxAbs, Mathf.Abs(key.value));
            // 也采样曲线极值（折线最大可能出现在关键帧之间，但保守取端点即可）
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (!_result.AreaScaleFactor.TryGetValue(r, out float cur))
                {
                    cur = 1f;
                }
                // 链式相乘（沿路径方向，用最大轴幅度近似）
                _result.AreaScaleFactor[r] = cur * Mathf.Max(1f, maxAbs);
            }
        }

        private static bool IsMaterialProperty(string propertyName, out int slotIndex)
        {
            slotIndex = -1;
            // m_Materials.Array.data[N]._Prop 或 m_Materials.Array.data[N]
            const string prefix = "m_Materials.Array.data[";
            if (propertyName.StartsWith(prefix, StringComparison.Ordinal))
            {
                int idxStart = prefix.Length;
                int idxEnd = propertyName.IndexOf(']', idxStart);
                if (idxEnd > idxStart && int.TryParse(propertyName.Substring(idxStart, idxEnd - idxStart), out slotIndex))
                {
                    return true;
                }
            }
            return false;
        }

        private static string ExtractMaterialPropName(string propertyName)
        {
            int dot = propertyName.IndexOf("._", StringComparison.Ordinal);
            if (dot >= 0) return propertyName.Substring(dot + 1);
            return propertyName;
        }

        private Transform FindPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return _root.transform;
            if (_pathCache.TryGetValue(path, out var cached)) return cached;
            var t = _root.transform.Find(path);
            _pathCache[path] = t;
            return t;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal sealed class AnimationAnalyzer
    {
        private readonly AnimationIndex _index;
        private readonly Transform _root;
        private readonly Func<IEnumerable<VirtualClip>> _additiveClips;

        // Main, pre, outline and fur are the four non-ForwardAdd Blend commands present in audited lilToon 2.3.4.
        // ForwardAdd is deliberately excluded: its One/One additive lighting pass does not make opaque main alpha
        // a surface-transparency input. Every source factor must remain One and every destination factor Zero.
        // 主、预处理、描边与毛发是已审计 lilToon 2.3.4 中四组非 ForwardAdd Blend 状态。
        private static readonly Dictionary<string, float> OpaqueBlendFactors =
            new Dictionary<string, float>(StringComparer.Ordinal)
            {
                { "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One },
                { "_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One },
                { "_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero },
                { "_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero },
                { "_PreSrcBlend", (float)UnityEngine.Rendering.BlendMode.One },
                { "_PreSrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One },
                { "_PreDstBlend", (float)UnityEngine.Rendering.BlendMode.Zero },
                { "_PreDstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero },
                { "_OutlineSrcBlend", (float)UnityEngine.Rendering.BlendMode.One },
                { "_OutlineSrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One },
                { "_OutlineDstBlend", (float)UnityEngine.Rendering.BlendMode.Zero },
                { "_OutlineDstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero },
                { "_FurSrcBlend", (float)UnityEngine.Rendering.BlendMode.One },
                { "_FurSrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One },
                { "_FurDstBlend", (float)UnityEngine.Rendering.BlendMode.Zero },
                { "_FurDstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero }
            };

        public AnimationAnalyzer(AnimationIndex index, Transform root,
            Func<IEnumerable<VirtualClip>> additiveClips = null)
        {
            _index = index;
            _root = root;
            _additiveClips = additiveClips;
        }

        private IEnumerable<VirtualClip> Clips(string path)
        {
            foreach (var clip in _index.GetClipsForObjectPath(path))
            {
                ATOProgress.Checkpoint("Analyzing animation clip " + (clip == null ? "<null>" : clip.Name));
                yield return clip;
            }
        }

        private static IEnumerable<EditorCurveBinding> FloatBindings(VirtualClip clip)
        {
            var index = 0;
            foreach (var binding in clip.GetFloatCurveBindings())
            {
                if ((index++ & 255) == 0) ATOProgress.Checkpoint("Scanning animation float bindings");
                yield return binding;
            }
        }

        private static IEnumerable<EditorCurveBinding> ObjectBindings(VirtualClip clip)
        {
            var index = 0;
            foreach (var binding in clip.GetObjectCurveBindings())
            {
                if ((index++ & 255) == 0) ATOProgress.Checkpoint("Scanning animation object bindings");
                yield return binding;
            }
        }

        private static IEnumerable<ObjectReferenceKeyframe> Frames(ObjectReferenceKeyframe[] curve)
        {
            for (var index = 0; index < curve.Length; index++)
            {
                if ((index & 255) == 0) ATOProgress.Checkpoint("Scanning animation object keyframes");
                yield return curve[index];
            }
        }

        public string Path(Component component) => AnimationUtility.CalculateTransformPath(component.transform, _root);

        public bool CanBecomeEnabled(Renderer renderer)
        {
            var hierarchyCanBeActive = true;
            var cursor = renderer.transform;
            while (cursor != null && cursor.IsChildOf(_root))
            {
                if (!cursor.gameObject.activeSelf)
                {
                    var path = AnimationUtility.CalculateTransformPath(cursor, _root);
                    var canActivate = false;
                    foreach (var clip in Clips(path))
                    foreach (var binding in FloatBindings(clip))
                        if (binding.path == path && binding.propertyName == "m_IsActive" &&
                            ReferenceEquals(ResolveBindingTarget(_root, binding), cursor.gameObject) &&
                            CurveCanBePositive(clip.GetFloatCurve(binding))) canActivate = true;
                    if (!canActivate) { hierarchyCanBeActive = false; break; }
                }
                if (cursor == _root) break;
                cursor = cursor.parent;
            }
            if (!hierarchyCanBeActive) return false;
            if (renderer.enabled) return true;

            var rendererPath = Path(renderer);
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in FloatBindings(clip))
                if (binding.propertyName == "m_Enabled" &&
                    BindingTargetsRenderer(_root, binding, rendererPath, renderer) &&
                    CurveCanBePositive(clip.GetFloatCurve(binding))) return true;
            return false;
        }

        public IEnumerable<Material> AnimatedMaterials(Renderer renderer, string rendererPath, int slot)
        {
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in ObjectBindings(clip))
            {
                if (!BindingTargetsRenderer(_root, binding, rendererPath, renderer) ||
                    !TryGetMaterialSlot(binding.propertyName, out var animatedSlot) || animatedSlot != slot) continue;
                var curve = clip.GetObjectCurve(binding);
                if (curve == null) continue;
                foreach (var frame in Frames(curve)) if (frame.value is Material material) yield return material;
            }
        }

        public bool HasUnsupportedAnimatedMaterial(Renderer renderer, string rendererPath, int slot)
        {
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in ObjectBindings(clip))
            {
                if (!BindingTargetsRenderer(_root, binding, rendererPath, renderer) ||
                    !TryGetMaterialSlot(binding.propertyName, out var animatedSlot) || animatedSlot != slot) continue;
                var curve = clip.GetObjectCurve(binding);
                if (curve == null) continue;
                foreach (var frame in Frames(curve))
                    if (frame.value != null && !(frame.value is Material)) return true;
            }
            return false;
        }

        public IEnumerable<Texture2D> AnimatedTextures(Renderer renderer, string rendererPath, int slot, string property)
        {
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in ObjectBindings(clip))
            {
                if (!IsMaterialPropertyBinding(binding, renderer, rendererPath, slot, property)) continue;
                var curve = clip.GetObjectCurve(binding);
                if (curve == null) continue;
                foreach (var frame in Frames(curve)) if (frame.value is Texture2D texture) yield return texture;
            }
        }

        public bool IsTextureAnimated(Renderer renderer, string rendererPath, int slot, string property)
        {
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in ObjectBindings(clip))
                if (IsMaterialPropertyBinding(binding, renderer, rendererPath, slot, property)) return true;
            return false;
        }

        public bool HasUnsupportedAnimatedTexture(Renderer renderer, string rendererPath, int slot, string property)
        {
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in ObjectBindings(clip))
            {
                if (!IsMaterialPropertyBinding(binding, renderer, rendererPath, slot, property)) continue;
                var curve = clip.GetObjectCurve(binding);
                if (curve == null) continue;
                foreach (var frame in Frames(curve))
                    if (frame.value != null && !(frame.value is Texture2D)) return true;
            }
            return false;
        }

        public bool IsTextureTransformAnimated(Renderer renderer, string rendererPath, int slot, string property)
        {
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in FloatBindings(clip))
            {
                if (!BindingTargetsRenderer(_root, binding, rendererPath, renderer) ||
                    !TryGetMaterialProperty(binding.propertyName, out var animatedSlot, out var animatedProperty) ||
                    animatedSlot != slot) continue;
                if (IsTextureTransformProperty(animatedProperty, property)) return true;
            }
            return false;
        }

        public bool HasMaterialParameterAnimation(Renderer renderer, string rendererPath, int slot, string property)
        {
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in FloatBindings(clip))
            {
                if (!BindingTargetsRenderer(_root, binding, rendererPath, renderer) ||
                    !TryGetMaterialProperty(binding.propertyName, out var animatedSlot, out var animatedProperty) ||
                    animatedSlot != slot) continue;
                if (animatedProperty == property || animatedProperty.StartsWith(property + ".", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public void AccumulateAlphaStates(Renderer renderer, string rendererPath, int slot, Material material,
            ref ATOAlphaMode mode, ref bool evaluateCutout, ref bool evaluateBlend, ref float cutoff,
            ICollection<float> cutoffs)
        {
            DetectAlphaStates(material, out evaluateCutout, out evaluateBlend);
            mode = evaluateBlend ? ATOAlphaMode.Blend :
                evaluateCutout ? ATOAlphaMode.Cutout : ATOAlphaMode.Opaque;
            cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;
            cutoffs?.Add(cutoff);

            foreach (var clip in Clips(rendererPath))
            foreach (var binding in FloatBindings(clip))
            {
                if (!BindingTargetsRenderer(_root, binding, rendererPath, renderer) ||
                    !TryGetMaterialProperty(binding.propertyName, out var animatedSlot, out var property) ||
                    animatedSlot != slot) continue;
                var curve = clip.GetFloatCurve(binding);
                if (curve == null) continue;

                if (property == "_Cutoff")
                {
                    // Cutoff is a scalar threshold, so every continuously animated state is represented by the
                    // finite range of the curve. The quality gate later sweeps all covered alpha breakpoints inside
                    // that range; requiring stepped curves here would reject a case we can prove conservatively.
                    // Cutoff 是标量阈值；质量门禁会扫描有限曲线范围内全部 Alpha 断点，因此可安全支持连续动画。
                    if (!TryCurveBounds(curve, out var minimumCutoff, out var maximumCutoff))
                    {
                        cutoffs?.Add(float.NaN);
                        continue;
                    }
                    minimumCutoff = Mathf.Clamp01(minimumCutoff);
                    maximumCutoff = Mathf.Clamp01(maximumCutoff);
                    cutoff = Mathf.Max(cutoff, maximumCutoff);
                    cutoffs?.Add(minimumCutoff);
                    cutoffs?.Add(maximumCutoff);
                    continue;
                }

                if (!IsTransparencyStateProperty(property)) continue;
                if (!TryCurveBounds(curve, out var minimum, out var maximum))
                {
                    evaluateCutout = true; evaluateBlend = true; mode = ATOAlphaMode.Blend;
                    continue;
                }
                if (property == "_AlphaClip")
                {
                    if (maximum > 0.5f) { evaluateCutout = true; mode = Strictest(mode, ATOAlphaMode.Cutout); }
                    continue;
                }
                if (OpaqueBlendFactors.TryGetValue(property, out var opaqueBlendFactor))
                {
                    if (minimum != opaqueBlendFactor || maximum != opaqueBlendFactor)
                    { evaluateBlend = true; mode = ATOAlphaMode.Blend; }
                    continue;
                }
                if (property == "_Surface")
                {
                    if (maximum > 0.5f) { evaluateBlend = true; mode = ATOAlphaMode.Blend; }
                    continue;
                }
                if (maximum >= 1f) { evaluateCutout = true; mode = Strictest(mode, ATOAlphaMode.Cutout); }
                if (maximum >= 2f)
                {
                    evaluateBlend = true; mode = ATOAlphaMode.Blend;
                }
                // A range spanning both sides of a state threshold can enter every intervening mode.
                if (minimum < 1f && maximum > 1f) evaluateCutout = true;
            }
        }

        public bool HasAdditiveScaleAnimation(Transform target)
        {
            if (target == null || _root == null || _additiveClips == null) return false;
            var hierarchy = new Dictionary<string, Transform>(StringComparer.Ordinal);
            for (var current = target; current != null && current.IsChildOf(_root); current = current.parent)
            {
                hierarchy[AnimationUtility.CalculateTransformPath(current, _root)] = current;
                if (current == _root) break;
            }

            var clips = _additiveClips() ?? Enumerable.Empty<VirtualClip>();
            foreach (var clip in clips.Where(value => value != null).Distinct())
            {
                ATOProgress.Checkpoint("Checking additive transform-scale animation " + clip.Name);
                foreach (var binding in FloatBindings(clip))
                {
                    if (!hierarchy.TryGetValue(binding.path, out var transform) ||
                        !IsLocalScaleAxis(binding.propertyName) ||
                        !ReferenceEquals(ResolveBindingTarget(_root, binding), transform)) continue;
                    return true;
                }
            }
            return false;
        }

        public bool HasUnmodeledBlendShapeDriver(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            if (renderer == null || mesh == null || mesh.blendShapeCount == 0) return false;
            if (!ReferenceEquals(renderer.sharedMesh, mesh)) return true;

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                if ((shape & 63) == 0) ATOProgress.Checkpoint("Checking blend-shape weight domain");
                var name = mesh.GetBlendShapeName(shape);
                if (string.IsNullOrEmpty(name) || !names.Add(name)) return true;
                var currentWeight = renderer.GetBlendShapeWeight(shape);
                if (!Finite(currentWeight) || currentWeight < 0f || currentWeight > 100f) return true;

                var previousFrameWeight = float.NegativeInfinity;
                var hasHundredPercentFrame = false;
                var frameCount = mesh.GetBlendShapeFrameCount(shape);
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var frameWeight = mesh.GetBlendShapeFrameWeight(shape, frame);
                    if (!Finite(frameWeight) || frameWeight <= previousFrameWeight ||
                        frameWeight < 0f || frameWeight > 100f) return true;
                    if (frameWeight == 100f) hasHundredPercentFrame = true;
                    previousFrameWeight = frameWeight;
                }
                // The documented 0..100 interval is sufficient for density reduction only when its upper endpoint
                // is authored explicitly. Otherwise native extrapolation semantics would become part of the proof.
                // 只有显式 100% frame 才允许缩小；否则原生外推语义未经实机证明。
                if (frameCount > 0 && !hasHundredPercentFrame) return true;
            }

            var rendererPath = Path(renderer);
            foreach (var clip in Clips(rendererPath))
            foreach (var binding in FloatBindings(clip))
            {
                if (!BindingTargetsRenderer(_root, binding, rendererPath, renderer) ||
                    !TryGetBlendShapeName(binding.propertyName, out var shapeName) || !names.Contains(shapeName)) continue;
                if (!TryCurveBounds(clip.GetFloatCurve(binding), out var minimum, out var maximum) ||
                    minimum < 0f || maximum > 100f) return true;
            }

            if (_additiveClips == null) return false;
            var additive = _additiveClips() ?? Enumerable.Empty<VirtualClip>();
            foreach (var clip in additive.Where(value => value != null).Distinct())
            {
                ATOProgress.Checkpoint("Checking additive blend-shape animation " + clip.Name);
                foreach (var binding in FloatBindings(clip))
                    if (BindingTargetsRenderer(_root, binding, rendererPath, renderer) &&
                        TryGetBlendShapeName(binding.propertyName, out var shapeName) && names.Contains(shapeName))
                        return true;
            }
            return false;
        }

        public float MaximumAreaScale(Transform target)
        {
            var areaMaximum = 1f;
            var current = target;
            while (current != null && current.IsChildOf(_root))
            {
                var localMaximum = Abs(current.localScale);
                var path = AnimationUtility.CalculateTransformPath(current, _root);
                foreach (var clip in Clips(path))
                foreach (var binding in FloatBindings(clip))
                {
                    if (binding.path != path || !IsLocalScaleAxis(binding.propertyName) ||
                        !ReferenceEquals(ResolveBindingTarget(_root, binding), current)) continue;
                    var axis = binding.propertyName.EndsWith(".x", StringComparison.Ordinal) ? 0 :
                        binding.propertyName.EndsWith(".y", StringComparison.Ordinal) ? 1 :
                        binding.propertyName.EndsWith(".z", StringComparison.Ordinal) ? 2 : -1;
                    if (axis < 0) continue;
                    if (!TryCurveBounds(clip.GetFloatCurve(binding), out var minimum, out var maximum)) return float.PositiveInfinity;
                    var absolute = Mathf.Max(Mathf.Abs(minimum), Mathf.Abs(maximum));
                    if (axis == 0) localMaximum.x = Mathf.Max(localMaximum.x, absolute);
                    if (axis == 1) localMaximum.y = Mathf.Max(localMaximum.y, absolute);
                    if (axis == 2) localMaximum.z = Mathf.Max(localMaximum.z, absolute);
                }
                var localValues = new[] { localMaximum.x, localMaximum.y, localMaximum.z };
                Array.Sort(localValues);
                areaMaximum *= localValues[1] * localValues[2];
                if (!Finite(areaMaximum)) return float.PositiveInfinity;
                if (current == _root) break;
                current = current.parent;
            }
            return Mathf.Max(1e-8f, areaMaximum);
        }

        internal static UnityEngine.Object ResolveBindingTarget(Transform avatarRoot, EditorCurveBinding binding)
        {
            if (avatarRoot == null || binding.type == null) return null;
            var transform = string.IsNullOrEmpty(binding.path) ? avatarRoot : avatarRoot.Find(binding.path);
            if (transform == null) return null;
            if (binding.type == typeof(GameObject)) return transform.gameObject;
            if (!typeof(Component).IsAssignableFrom(binding.type)) return null;
            return transform.gameObject.GetComponent(binding.type);
        }

        internal static bool BindingTargetsRenderer(Transform avatarRoot, EditorCurveBinding binding,
            string rendererPath, Renderer renderer)
        {
            return renderer != null && binding.path == rendererPath &&
                   ReferenceEquals(ResolveBindingTarget(avatarRoot, binding), renderer);
        }

        internal static bool BindingTargetsRenderer(EditorCurveBinding binding, string rendererPath, Renderer renderer)
        {
            if (renderer == null || binding.path != rendererPath || binding.type == null ||
                !typeof(Component).IsAssignableFrom(binding.type)) return false;
            return ReferenceEquals(renderer.gameObject.GetComponent(binding.type), renderer);
        }

        internal static HashSet<string> FindAmbiguousTransformPaths(Transform avatarRoot)
        {
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            if (avatarRoot == null) return ambiguous;
            var firstByPath = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var transforms = avatarRoot.GetComponentsInChildren<Transform>(true);
            for (var index = 0; index < transforms.Length; index++)
            {
                if ((index & 255) == 0) ATOProgress.Checkpoint("Checking animation hierarchy paths");
                var path = AnimationUtility.CalculateTransformPath(transforms[index], avatarRoot);
                if (!firstByPath.TryGetValue(path, out var first)) firstByPath.Add(path, transforms[index]);
                else if (!ReferenceEquals(first, transforms[index])) ambiguous.Add(path);
            }
            return ambiguous;
        }

        internal static RendererRecord ResolveRendererRecord(IEnumerable<RendererRecord> records,
            EditorCurveBinding binding, out bool ambiguous)
        {
            ambiguous = false;
            var candidates = (records ?? Enumerable.Empty<RendererRecord>())
                .Where(record => record?.Renderer != null && record.Path == binding.path).ToArray();
            var transforms = candidates.Select(record => record.Renderer.transform).Distinct().ToArray();
            if (transforms.Length > 1) { ambiguous = true; return null; }
            if (transforms.Length == 0 || binding.type == null ||
                !typeof(Component).IsAssignableFrom(binding.type)) return null;
            var target = transforms[0].gameObject.GetComponent(binding.type) as Renderer;
            if (target == null) return null;
            var matches = candidates.Where(record => ReferenceEquals(record.Renderer, target)).ToArray();
            if (matches.Length > 1) { ambiguous = true; return null; }
            return matches.Length == 1 ? matches[0] : null;
        }

        internal static bool TryGetMaterialSlot(string property, out int slot)
        {
            slot = -1; const string prefix = "m_Materials.Array.data[";
            if (!property.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var end = property.IndexOf(']', prefix.Length);
            return end == property.Length - 1 && end > prefix.Length &&
                   int.TryParse(property.Substring(prefix.Length, end - prefix.Length), out slot);
        }

        internal static bool TryGetMaterialProperty(string property, out int slot, out string materialProperty)
        {
            slot = 0; materialProperty = null;
            const string prefix = "material."; const string arrayPrefix = "materials.Array.data[";
            if (property.StartsWith(prefix, StringComparison.Ordinal))
            {
                materialProperty = property.Substring(prefix.Length); return materialProperty.Length > 0;
            }
            if (!property.StartsWith(arrayPrefix, StringComparison.Ordinal)) return false;
            var end = property.IndexOf(']', arrayPrefix.Length);
            if (end < 0 || !int.TryParse(property.Substring(arrayPrefix.Length, end - arrayPrefix.Length), out slot)) return false;
            var dot = property.IndexOf('.', end);
            if (dot != end + 1 || dot + 1 >= property.Length) return false;
            materialProperty = property.Substring(dot + 1); return true;
        }

        private bool IsMaterialPropertyBinding(EditorCurveBinding binding, Renderer renderer, string rendererPath,
            int slot, string property)
        {
            return BindingTargetsRenderer(_root, binding, rendererPath, renderer) &&
                   TryGetMaterialProperty(binding.propertyName, out var animatedSlot, out var animatedProperty) &&
                   animatedSlot == slot && animatedProperty == property;
        }

        private static bool IsLocalScaleAxis(string property) =>
            property == "m_LocalScale.x" || property == "m_LocalScale.y" || property == "m_LocalScale.z";

        internal static bool TryGetBlendShapeName(string property, out string shapeName)
        {
            const string prefix = "blendShape.";
            shapeName = property != null && property.StartsWith(prefix, StringComparison.Ordinal)
                ? property.Substring(prefix.Length) : null;
            return !string.IsNullOrEmpty(shapeName);
        }

        private static bool IsTextureTransformProperty(string animatedProperty, string textureProperty)
        {
            return animatedProperty == textureProperty + "_ST" ||
                   animatedProperty.StartsWith(textureProperty + "_ST.", StringComparison.Ordinal) ||
                   animatedProperty == textureProperty + "_ScrollRotate" ||
                   animatedProperty.StartsWith(textureProperty + "_ScrollRotate.", StringComparison.Ordinal) ||
                   animatedProperty == textureProperty + ".scale" ||
                   animatedProperty.StartsWith(textureProperty + ".scale.", StringComparison.Ordinal) ||
                   animatedProperty == textureProperty + ".offset" ||
                   animatedProperty.StartsWith(textureProperty + ".offset.", StringComparison.Ordinal);
        }

        private static bool IsTransparencyStateProperty(string property)
        {
            return property == "_Mode" || property == "_Surface" || property == "_AlphaClip" ||
                   property == "_TransparentMode" || OpaqueBlendFactors.ContainsKey(property);
        }

        internal static bool TryCurveValueBounds(AnimationCurve curve, out float minimum, out float maximum) =>
            TryCurveBounds(curve, out minimum, out maximum);

        private static bool TryCurveBounds(AnimationCurve curve, out float minimum, out float maximum)
        {
            minimum = float.PositiveInfinity; maximum = float.NegativeInfinity;
            if (curve == null) return false;
            var keys = curve.keys;
            if (keys.Length == 0) return false;
            for (var index = 0; index < keys.Length; index++)
            {
                if ((index & 255) == 0) ATOProgress.Checkpoint("Bounding animation keyframes");
                var key = keys[index];
                if (!Finite(key.time) || !Finite(key.value)) return false;
                minimum = Mathf.Min(minimum, key.value); maximum = Mathf.Max(maximum, key.value);
            }
            for (var index = 0; index + 1 < keys.Length; index++)
            {
                if ((index & 255) == 0) ATOProgress.Checkpoint("Bounding animation curve segments");
                var left = keys[index]; var right = keys[index + 1]; var duration = right.time - left.time;
                if (!Finite(duration) || duration <= 0f) return false;
                // Unity's unweighted cubic Hermite segment is a Bezier curve whose Y values stay inside the
                // convex hull of these two controls and the endpoint values. Weighted handles need a separately
                // audited rational/time reparameterization proof, so reject them rather than guessing.
                // Unity 非加权 Hermite 段可按 Bezier 控制点凸包保守求界；加权手柄需独立证明，暂时安全回退。
                if ((left.weightedMode & WeightedMode.Out) != 0 ||
                    (right.weightedMode & WeightedMode.In) != 0) return false;
                if (!float.IsInfinity(left.outTangent))
                {
                    var control = left.value + left.outTangent * duration / 3f;
                    if (!Finite(control)) return false;
                    minimum = Mathf.Min(minimum, control); maximum = Mathf.Max(maximum, control);
                }
                if (!float.IsInfinity(right.inTangent))
                {
                    var control = right.value - right.inTangent * duration / 3f;
                    if (!Finite(control)) return false;
                    minimum = Mathf.Min(minimum, control); maximum = Mathf.Max(maximum, control);
                }
            }
            return Finite(minimum) && Finite(maximum);
        }

        private static bool CurveCanBePositive(AnimationCurve curve)
        {
            return TryCurveBounds(curve, out _, out var maximum) && maximum > 0.5f;
        }

        private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static ATOAlphaMode Strictest(ATOAlphaMode a, ATOAlphaMode b) => (ATOAlphaMode)Mathf.Max((int)a, (int)b);

        public static ATOAlphaMode DetectAlphaMode(Material material)
        {
            DetectAlphaStates(material, out var cutout, out var blend);
            return blend ? ATOAlphaMode.Blend : cutout ? ATOAlphaMode.Cutout : ATOAlphaMode.Opaque;
        }

        private static void DetectAlphaStates(Material material, out bool cutout, out bool blend)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            var canCutout = false; var canBlend = false;
            var renderType = material.GetTag("RenderType", false, "") ?? string.Empty;
            var taggedCutout = renderType.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0;
            canCutout |= taggedCutout;
            canBlend |= !taggedCutout && renderType.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0;
            canCutout |= material.IsKeywordEnabled("_ALPHATEST_ON") ||
                          material.IsKeywordEnabled("UNITY_UI_ALPHACLIP");
            canBlend |= material.IsKeywordEnabled("_ALPHABLEND_ON") ||
                        material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                        material.IsKeywordEnabled("UNITY_UI_CLIP_RECT");
            canBlend |= material.renderQueue >= 3000;
            ShaderTextureAnalyzer.AccumulateVerifiedLilPassAlphaFlags(material, ref canCutout, ref canBlend);

            if (TryGetFiniteState(material, "_AlphaClip", out var alphaClip, ref canCutout, ref canBlend) &&
                alphaClip > 0.5f) canCutout = true;
            if (TryGetFiniteState(material, "_Mode", out var mode, ref canCutout, ref canBlend))
            {
                if (mode >= 1f) canCutout = true;
                if (mode >= 2f) canBlend = true;
            }
            if (TryGetFiniteState(material, "_Surface", out var surface, ref canCutout, ref canBlend) &&
                surface > 0.5f) canBlend = true;
            if (TryGetFiniteState(material, "_TransparentMode", out var transparentMode,
                    ref canCutout, ref canBlend))
            {
                // lilToon modes above zero include cutout, transparent, refraction, fur and gem variants.
                // Checking both metrics for modes >= 2 is conservative for variants whose exact pass is shader-defined.
                // lilToon 非零模式覆盖裁剪、透明、折射、毛发及宝石；>=2 时同时检查两类 Alpha 指标。
                if (transparentMode >= 1f) canCutout = true;
                if (transparentMode >= 2f) canBlend = true;
            }

            foreach (var factor in OpaqueBlendFactors)
                AccumulateBlendFactor(material, factor.Key, factor.Value, ref canBlend);
            cutout = canCutout; blend = canBlend;
        }

        private static bool TryGetFiniteState(Material material, string property, out float value,
            ref bool cutout, ref bool blend)
        {
            value = 0f;
            if (!material.HasProperty(property)) return false;
            value = material.GetFloat(property);
            if (Finite(value)) return true;
            cutout = true; blend = true; return false;
        }

        private static void AccumulateBlendFactor(Material material, string property,
            float opaqueValue, ref bool blend)
        {
            if (!material.HasProperty(property)) return;
            var value = material.GetFloat(property);
            if (!Finite(value) || value != opaqueValue) blend = true;
        }
    }
}

using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class AnimationBindingTargetTests
    {
        [Test]
        public void DuplicateHierarchyPathResolvesOnlyToFirstTransform()
        {
            var root = new GameObject("Root");
            try
            {
                var first = NewMeshRenderer(root.transform, "Twin");
                var second = NewMeshRenderer(root.transform, "Twin");
                var binding = Binding("Twin", typeof(MeshRenderer), "material._MainTex");

                Assert.That(AnimationAnalyzer.BindingTargetsRenderer(root.transform, binding, "Twin", first), Is.True);
                Assert.That(AnimationAnalyzer.BindingTargetsRenderer(root.transform, binding, "Twin", second), Is.False);

                var records = new[]
                {
                    new RendererRecord { Renderer = first, Path = "Twin" },
                    new RendererRecord { Renderer = second, Path = "Twin" }
                };
                Assert.That(AnimationAnalyzer.ResolveRendererRecord(records, binding, out var ambiguous), Is.Null);
                Assert.That(ambiguous, Is.True, "rewriters must fail closed instead of guessing between duplicate paths");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void DuplicatePathIsDetectedEvenWhenCompetitorHasNoRenderer()
        {
            var root = new GameObject("Root");
            try
            {
                NewMeshRenderer(root.transform, "Twin");
                var competitor = new GameObject("Twin"); competitor.transform.SetParent(root.transform, false);

                Assert.That(AnimationAnalyzer.FindAmbiguousTransformPaths(root.transform), Does.Contain("Twin"),
                    "path ambiguity is decided before component lookup");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void BroadRendererBindingSelectsOnlyUnityGetComponentTarget()
        {
            var root = new GameObject("Root");
            try
            {
                var target = new GameObject("Target"); target.transform.SetParent(root.transform, false);
                target.AddComponent<MeshFilter>();
                var meshRenderer = target.AddComponent<MeshRenderer>();
                var lineRenderer = target.AddComponent<LineRenderer>();
                var binding = Binding("Target", typeof(Renderer), "material._MainTex");
                var selected = target.GetComponent(typeof(Renderer)) as Renderer;
                var other = ReferenceEquals(selected, meshRenderer) ? (Renderer)lineRenderer : meshRenderer;

                Assert.That(AnimationAnalyzer.BindingTargetsRenderer(root.transform, binding, "Target", selected), Is.True);
                Assert.That(AnimationAnalyzer.BindingTargetsRenderer(root.transform, binding, "Target", other), Is.False,
                    "assignability alone would incorrectly attribute one binding to both Renderer components");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void AnalyzerDoesNotAttributeCurveToSiblingRendererComponent()
        {
            var root = new GameObject("Root");
            var texture = new Texture2D(1, 1);
            try
            {
                var target = new GameObject("Target"); target.transform.SetParent(root.transform, false);
                target.AddComponent<MeshFilter>();
                var meshRenderer = target.AddComponent<MeshRenderer>();
                var lineRenderer = target.AddComponent<LineRenderer>();
                var binding = ObjectBinding("Target", typeof(LineRenderer), "material._MainTex");
                var clip = VirtualClip.Create("component identity");
                clip.SetObjectCurve(binding, new[] { new ObjectReferenceKeyframe { time = 0f, value = texture } });
                var analyzer = new AnimationAnalyzer(new AnimationIndex(new[] { clip }), root.transform);

                Assert.That(analyzer.AnimatedTextures(meshRenderer, "Target", 0, "_MainTex"), Is.Empty);
                Assert.That(analyzer.AnimatedTextures(lineRenderer, "Target", 0, "_MainTex"),
                    Is.EquivalentTo(new[] { texture }));
            }
            finally
            {
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AnimatedMaterialSlotAllowsNullAndMaterialButRejectsOtherObjectTypes()
        {
            var root = new GameObject("Root");
            Material material = null;
            var texture = new Texture2D(1, 1);
            try
            {
                var renderer = NewMeshRenderer(root.transform, "Target");
                var binding = ObjectBinding("Target", typeof(MeshRenderer), "m_Materials.Array.data[0]");
                var shader = Shader.Find("Hidden/InternalErrorShader");
                Assert.That(shader, Is.Not.Null);
                material = new Material(shader);

                var safeClip = VirtualClip.Create("safe material values");
                safeClip.SetObjectCurve(binding, new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = null },
                    new ObjectReferenceKeyframe { time = 1f, value = material }
                });
                var safe = new AnimationAnalyzer(new AnimationIndex(new[] { safeClip }), root.transform);
                Assert.That(safe.HasUnsupportedAnimatedMaterial(renderer, "Target", 0), Is.False);

                var invalidClip = VirtualClip.Create("invalid material value");
                invalidClip.SetObjectCurve(binding, new[]
                    { new ObjectReferenceKeyframe { time = 0f, value = texture } });
                var invalid = new AnimationAnalyzer(new AnimationIndex(new[] { invalidClip }), root.transform);
                Assert.That(invalid.HasUnsupportedAnimatedMaterial(renderer, "Target", 0), Is.True);
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void VirtualClipObjectCurveContractUsesPPtrDeletionAndRejectsMarkerMutation()
        {
            var texture = new Texture2D(1, 1);
            var markerSource = new AnimationClip { name = "marker source" };
            try
            {
                var binding = ObjectBinding("Target", typeof(MeshRenderer), "material._MainTex");
                Assert.That(binding.isPPtrCurve, Is.True,
                    "NDMF SetObjectCurve rejects a field-initialized binding without the native PPtr flag");
                var clip = VirtualClip.Create("mutable object curve");
                clip.SetObjectCurve(binding,
                    new[] { new ObjectReferenceKeyframe { time = 0f, value = texture } });
                Assert.That(clip.GetObjectCurve(binding)[0].value, Is.SameAs(texture));
                Assert.That(clip.GetObjectCurveBindings(), Is.EquivalentTo(new[] { binding }));

                clip.SetObjectCurve(binding, null);
                Assert.That(clip.GetObjectCurve(binding), Is.Null);
                Assert.That(clip.GetObjectCurveBindings(), Is.Empty,
                    "NDMF null SetObjectCurve removes the binding from the virtual commit cache");

                var marker = VirtualClip.FromMarker(markerSource);
                Assert.That(marker.IsMarkerClip, Is.True);
                Assert.Throws<System.InvalidOperationException>(() =>
                    MaterialAnimationRewriter.EnsureMutableClipForRewrite(marker));
                Assert.DoesNotThrow(() => MaterialAnimationRewriter.EnsureMutableClipForRewrite(clip));
            }
            finally
            {
                Object.DestroyImmediate(markerSource);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ActivationAndRendererEnabledCurvesAreBothRequired()
        {
            var root = new GameObject("Root");
            try
            {
                var renderer = NewMeshRenderer(root.transform, "Target");
                renderer.gameObject.SetActive(false); renderer.enabled = false;
                var activeBinding = Binding("Target", typeof(GameObject), "m_IsActive");
                var enabledBinding = Binding("Target", typeof(MeshRenderer), "m_Enabled");
                var activeOnly = VirtualClip.Create("active only");
                activeOnly.SetFloatCurve(activeBinding, AnimationCurve.Constant(0f, 1f, 1f));
                var incomplete = new AnimationAnalyzer(new AnimationIndex(new[] { activeOnly }), root.transform);
                Assert.That(incomplete.CanBecomeEnabled(renderer), Is.False,
                    "an active GameObject curve must not imply that a disabled Renderer can render");

                var completeClip = VirtualClip.Create("active and enabled");
                completeClip.SetFloatCurve(activeBinding, AnimationCurve.Constant(0f, 1f, 1f));
                completeClip.SetFloatCurve(enabledBinding, AnimationCurve.Constant(0f, 1f, 1f));
                var complete = new AnimationAnalyzer(new AnimationIndex(new[] { completeClip }), root.transform);
                Assert.That(complete.CanBecomeEnabled(renderer), Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ActualBlendFactorsOverrideStaleOpaqueQueueTagAndMode()
        {
            var shader = Shader.Find("Standard");
            if (shader == null) Assert.Ignore("Unity Standard shader is unavailable.");
            var material = new Material(shader);
            try
            {
                Assert.That(material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend"), Is.True);
                material.SetOverrideTag("RenderType", "Opaque");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 0f);
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

                Assert.That(AnimationAnalyzer.DetectAlphaMode(material), Is.EqualTo(ATOAlphaMode.Blend),
                    "actual ShaderLab blend factors must not be hidden by stale inspector metadata");
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test]
        public void StaticCutoutAndBlendStateEnablesBothQualitySemantics()
        {
            var root = new GameObject("Root"); Material material = null;
            try
            {
                var renderer = NewMeshRenderer(root.transform, "Target");
                var shader = Shader.Find("Standard");
                if (shader == null) Assert.Ignore("Unity Standard shader is unavailable.");
                material = new Material(shader);
                material.SetOverrideTag("RenderType", "Opaque");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 0f);
                material.EnableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                var analyzer = new AnimationAnalyzer(new AnimationIndex(new VirtualClip[0]), root.transform);
                var mode = ATOAlphaMode.Opaque; var cutout = false; var blend = false; var cutoff = 0.5f;
                var cutoffs = new System.Collections.Generic.List<float>();

                analyzer.AccumulateAlphaStates(renderer, "Target", 0, material,
                    ref mode, ref cutout, ref blend, ref cutoff, cutoffs);

                Assert.That(cutout, Is.True);
                Assert.That(blend, Is.True);
                Assert.That(mode, Is.EqualTo(ATOAlphaMode.Blend));
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AnimatedBlendFactorEnablesBlendWithoutModeAnimation()
        {
            var root = new GameObject("Root"); Material material = null;
            try
            {
                var renderer = NewMeshRenderer(root.transform, "Target");
                var shader = Shader.Find("Standard");
                if (shader == null) Assert.Ignore("Unity Standard shader is unavailable.");
                material = new Material(shader);
                material.SetOverrideTag("RenderType", "Opaque");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 0f);
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                var clip = VirtualClip.Create("animated source blend");
                clip.SetFloatCurve(Binding("Target", typeof(MeshRenderer), "material._SrcBlend"),
                    AnimationCurve.Linear(0f, (float)UnityEngine.Rendering.BlendMode.One, 1f,
                        (float)UnityEngine.Rendering.BlendMode.SrcAlpha));
                var analyzer = new AnimationAnalyzer(new AnimationIndex(new[] { clip }), root.transform);
                var mode = ATOAlphaMode.Opaque; var cutout = false; var blend = false; var cutoff = 0.5f;
                var cutoffs = new System.Collections.Generic.List<float>();

                analyzer.AccumulateAlphaStates(renderer, "Target", 0, material,
                    ref mode, ref cutout, ref blend, ref cutoff, cutoffs);

                Assert.That(blend, Is.True);
                Assert.That(mode, Is.EqualTo(ATOAlphaMode.Blend));
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(root);
            }
        }

        [TestCase("_PreSrcBlend", (int)UnityEngine.Rendering.BlendMode.One,
            (int)UnityEngine.Rendering.BlendMode.SrcAlpha)]
        [TestCase("_OutlineDstBlend", (int)UnityEngine.Rendering.BlendMode.Zero,
            (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha)]
        [TestCase("_FurSrcBlendAlpha", (int)UnityEngine.Rendering.BlendMode.One,
            (int)UnityEngine.Rendering.BlendMode.SrcAlpha)]
        public void AnimatedLilAuxiliaryPassBlendFactorEnablesBlend(string property, int opaque, int animated)
        {
            var root = new GameObject("Root"); Material material = null;
            try
            {
                var renderer = NewMeshRenderer(root.transform, "Target");
                var shader = Shader.Find("Standard");
                if (shader == null) Assert.Ignore("Unity Standard shader is unavailable.");
                material = new Material(shader);
                material.SetOverrideTag("RenderType", "Opaque");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 0f);
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);

                var clip = VirtualClip.Create("animated lil auxiliary blend");
                clip.SetFloatCurve(Binding("Target", typeof(MeshRenderer), "material." + property),
                    AnimationCurve.Linear(0f, opaque, 1f, animated));
                var analyzer = new AnimationAnalyzer(new AnimationIndex(new[] { clip }), root.transform);
                var mode = ATOAlphaMode.Opaque; var cutout = false; var blend = false; var cutoff = 0.5f;
                var cutoffs = new System.Collections.Generic.List<float>();

                analyzer.AccumulateAlphaStates(renderer, "Target", 0, material,
                    ref mode, ref cutout, ref blend, ref cutoff, cutoffs);

                Assert.That(blend, Is.True, property + " is an actual lilToon ShaderLab Blend controller");
                Assert.That(mode, Is.EqualTo(ATOAlphaMode.Blend));
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ContinuousAndSteppedCutoffCurvesProduceFiniteConservativeRanges()
        {
            var root = new GameObject("Root");
            Material material = null;
            try
            {
                var renderer = NewMeshRenderer(root.transform, "Target");
                var binding = Binding("Target", typeof(MeshRenderer), "material._Cutoff");
                var shader = Shader.Find("Hidden/InternalErrorShader");
                Assert.That(shader, Is.Not.Null);
                material = new Material(shader);

                var continuousClip = VirtualClip.Create("continuous cutoff");
                continuousClip.SetFloatCurve(binding, AnimationCurve.Linear(0f, 0.2f, 1f, 0.8f));
                var continuous = new AnimationAnalyzer(new AnimationIndex(new[] { continuousClip }), root.transform);
                var mode = ATOAlphaMode.Opaque; var cutout = false; var blend = false; var cutoff = 0.5f;
                var values = new System.Collections.Generic.List<float>();
                continuous.AccumulateAlphaStates(renderer, "Target", 0, material,
                    ref mode, ref cutout, ref blend, ref cutoff, values);
                Assert.That(values.Exists(float.IsNaN), Is.False);
                CollectionAssert.Contains(values, 0.2f);
                CollectionAssert.Contains(values, 0.8f);
                Assert.That(cutoff, Is.EqualTo(0.8f).Within(1e-6f));

                var steppedClip = VirtualClip.Create("stepped cutoff");
                var stepped = new AnimationCurve(
                    new Keyframe(0f, 0.2f, float.PositiveInfinity, float.PositiveInfinity),
                    new Keyframe(1f, 0.8f, float.PositiveInfinity, float.PositiveInfinity));
                steppedClip.SetFloatCurve(binding, stepped);
                var steppedAnalyzer = new AnimationAnalyzer(new AnimationIndex(new[] { steppedClip }), root.transform);
                values.Clear(); cutoff = 0.5f;
                steppedAnalyzer.AccumulateAlphaStates(renderer, "Target", 0, material,
                    ref mode, ref cutout, ref blend, ref cutoff, values);
                Assert.That(values.Exists(float.IsNaN), Is.False);
                CollectionAssert.Contains(values, 0.2f);
                CollectionAssert.Contains(values, 0.8f);
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CutoffHermiteOvershootIsClampedAndUnsafeCurvesFailClosed()
        {
            var root = new GameObject("Root");
            Material material = null;
            try
            {
                var renderer = NewMeshRenderer(root.transform, "Target");
                var binding = Binding("Target", typeof(MeshRenderer), "material._Cutoff");
                var shader = Shader.Find("Hidden/InternalErrorShader");
                Assert.That(shader, Is.Not.Null);
                material = new Material(shader);

                var overshootClip = VirtualClip.Create("cutoff Hermite overshoot");
                overshootClip.SetFloatCurve(binding, new AnimationCurve(
                    new Keyframe(0f, 0.2f, 0f, 6f), new Keyframe(1f, 0.2f, -6f, 0f)));
                var values = AccumulateCutoffs(root, renderer, material, overshootClip);
                Assert.That(values.Exists(float.IsNaN), Is.False);
                Assert.That(values, Does.Contain(0.2f));
                Assert.That(values, Does.Contain(1f),
                    "a finite Hermite overshoot above one must conservatively clamp to cutoff one");

                var emptyClip = VirtualClip.Create("empty cutoff");
                emptyClip.SetFloatCurve(binding, new AnimationCurve());
                Assert.That(AccumulateCutoffs(root, renderer, material, emptyClip).Exists(float.IsNaN), Is.True,
                    "an empty animated cutoff cannot establish a safe continuous interval");

                var nonFiniteClip = VirtualClip.Create("non-finite cutoff");
                nonFiniteClip.SetFloatCurve(binding,
                    new AnimationCurve(new Keyframe(0f, float.PositiveInfinity)));
                Assert.That(AccumulateCutoffs(root, renderer, material, nonFiniteClip).Exists(float.IsNaN), Is.True);

                var first = new Keyframe(0f, 0.2f, 0f, 3f)
                    { outWeight = 0.75f, weightedMode = WeightedMode.Out };
                var weightedClip = VirtualClip.Create("weighted cutoff");
                weightedClip.SetFloatCurve(binding, new AnimationCurve(first, new Keyframe(1f, 0.8f)));
                Assert.That(AccumulateCutoffs(root, renderer, material, weightedClip).Exists(float.IsNaN), Is.True,
                    "weighted Bezier cutoff handles are unsupported and must fail closed");
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MaximumAreaScaleIncludesAnimatedAncestorScaleAndCurveOvershoot()
        {
            var root = new GameObject("Root");
            try
            {
                root.transform.localScale = new Vector3(2f, 3f, 4f);
                var ancestor = new GameObject("Ancestor"); ancestor.transform.SetParent(root.transform, false);
                var target = new GameObject("Target"); target.transform.SetParent(ancestor.transform, false);
                var binding = Binding("Ancestor", typeof(Transform), "m_LocalScale.x");
                var clip = VirtualClip.Create("ancestor scale overshoot");
                clip.SetFloatCurve(binding, new AnimationCurve(
                    new Keyframe(0f, 1f, 0f, 6f), new Keyframe(1f, 1f, -6f, 0f)));
                var analyzer = new AnimationAnalyzer(new AnimationIndex(new[] { clip }), root.transform);

                Assert.That(analyzer.MaximumAreaScale(target.transform), Is.EqualTo(36f).Within(1e-4f));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void AdditiveAncestorScaleAnimationIsDetectedByExactTransformIdentity()
        {
            var root = new GameObject("Root");
            try
            {
                var ancestor = new GameObject("Ancestor"); ancestor.transform.SetParent(root.transform, false);
                var target = new GameObject("Target"); target.transform.SetParent(ancestor.transform, false);
                var clip = VirtualClip.Create("additive ancestor scale");
                clip.SetFloatCurve(Binding("Ancestor", typeof(Transform), "m_LocalScale.x"),
                    AnimationCurve.Constant(0f, 1f, 2f));
                var analyzer = new AnimationAnalyzer(new AnimationIndex(new[] { clip }), root.transform,
                    () => new[] { clip });

                Assert.That(analyzer.HasAdditiveScaleAnimation(target.transform), Is.True);

                var wrongType = VirtualClip.Create("same path wrong component type");
                wrongType.SetFloatCurve(Binding("Ancestor", typeof(GameObject), "m_LocalScale.x"),
                    AnimationCurve.Constant(0f, 1f, 2f));
                var wrongTypeAnalyzer = new AnimationAnalyzer(new AnimationIndex(new[] { wrongType }), root.transform,
                    () => new[] { wrongType });
                Assert.That(wrongTypeAnalyzer.HasAdditiveScaleAnimation(target.transform), Is.False);

                var unrelated = VirtualClip.Create("unrelated scale");
                unrelated.SetFloatCurve(Binding("Other", typeof(Transform), "m_LocalScale.x"),
                    AnimationCurve.Constant(0f, 1f, 2f));
                var unrelatedAnalyzer = new AnimationAnalyzer(new AnimationIndex(new[] { unrelated }), root.transform,
                    () => new[] { unrelated });
                Assert.That(unrelatedAnalyzer.HasAdditiveScaleAnimation(target.transform), Is.False);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ScaleConstraintOnAncestorIsAnExternalAreaDriver()
        {
            var root = new GameObject("Root");
            try
            {
                var ancestor = new GameObject("Ancestor"); ancestor.transform.SetParent(root.transform, false);
                ancestor.AddComponent<ScaleConstraint>();
                var target = new GameObject("Target"); target.transform.SetParent(ancestor.transform, false);

                Assert.That(AvatarAnalyzer.HasExternalScaleDriver(target.transform, root.transform), Is.True);
                Assert.That(AvatarAnalyzer.HasExternalScaleDriver(root.transform, target.transform), Is.False,
                    "the scan must not leave the declared Avatar hierarchy");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void BlendShapeWeightDomainRejectsUnboundedCompositionAndAcceptsFiniteOverrideRange()
        {
            var root = new GameObject("Root");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            var extendedMesh = new Mesh { vertices = mesh.vertices };
            var missingEndpointMesh = new Mesh { vertices = mesh.vertices };
            var zeros = new Vector3[3]; var smile = new Vector3[3]; smile[1] = Vector3.right;
            mesh.AddBlendShapeFrame("Smile", 100f, smile, zeros, zeros);
            extendedMesh.AddBlendShapeFrame("Smile", 120f, smile, zeros, zeros);
            missingEndpointMesh.AddBlendShapeFrame("Smile", 50f, smile, zeros, zeros);
            try
            {
                var target = new GameObject("Target"); target.transform.SetParent(root.transform, false);
                var renderer = target.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                var binding = Binding("Target", typeof(SkinnedMeshRenderer), "blendShape.Smile");
                var safeClip = VirtualClip.Create("safe blend shape");
                safeClip.SetFloatCurve(binding, AnimationCurve.Linear(0f, 0f, 1f, 100f));
                var safe = new AnimationAnalyzer(new AnimationIndex(new[] { safeClip }), root.transform,
                    () => new VirtualClip[0]);
                Assert.That(safe.HasUnmodeledBlendShapeDriver(renderer, mesh), Is.False);

                renderer.SetBlendShapeWeight(0, 101f);
                Assert.That(safe.HasUnmodeledBlendShapeDriver(renderer, mesh), Is.True,
                    "the current renderer state is part of the reachable weight domain");
                renderer.SetBlendShapeWeight(0, 0f);

                var overshootClip = VirtualClip.Create("blend shape overshoot");
                overshootClip.SetFloatCurve(binding, new AnimationCurve(
                    new Keyframe(0f, 50f, 0f, 300f), new Keyframe(1f, 50f, -300f, 0f)));
                var overshoot = new AnimationAnalyzer(new AnimationIndex(new[] { overshootClip }), root.transform,
                    () => new VirtualClip[0]);
                Assert.That(overshoot.HasUnmodeledBlendShapeDriver(renderer, mesh), Is.True);

                var additive = new AnimationAnalyzer(new AnimationIndex(new[] { safeClip }), root.transform,
                    () => new[] { safeClip });
                Assert.That(additive.HasUnmodeledBlendShapeDriver(renderer, mesh), Is.True,
                    "an additive layer is not bounded by the override-layer convex range");

                var wrongType = VirtualClip.Create("wrong blend-shape component identity");
                wrongType.SetFloatCurve(Binding("Target", typeof(Transform), "blendShape.Smile"),
                    AnimationCurve.Linear(0f, 0f, 1f, 100f));
                var wrongTypeAnalyzer = new AnimationAnalyzer(new AnimationIndex(new[] { wrongType }), root.transform,
                    () => new[] { wrongType });
                Assert.That(wrongTypeAnalyzer.HasUnmodeledBlendShapeDriver(renderer, mesh), Is.False);

                renderer.sharedMesh = extendedMesh;
                var extended = new AnimationAnalyzer(new AnimationIndex(new VirtualClip[0]), root.transform,
                    () => new VirtualClip[0]);
                Assert.That(extended.HasUnmodeledBlendShapeDriver(renderer, extendedMesh), Is.True,
                    "model frame weights outside 0..100 declare a wider runtime domain");

                renderer.sharedMesh = missingEndpointMesh;
                Assert.That(extended.HasUnmodeledBlendShapeDriver(renderer, missingEndpointMesh), Is.True,
                    "density reduction must not rely on unaudited native extrapolation to weight 100");
            }
            finally
            {
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(extendedMesh);
                Object.DestroyImmediate(missingEndpointMesh); Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WeightedScaleCurveFailsClosed()
        {
            var first = new Keyframe(0f, 1f, 0f, 3f) { outWeight = 0.75f, weightedMode = WeightedMode.Out };
            var second = new Keyframe(1f, 2f, 0f, 0f);
            Assert.That(AnimationAnalyzer.TryCurveValueBounds(new AnimationCurve(first, second), out _, out _),
                Is.False, "weighted Bezier handles require a separate conservative extrema proof");
        }

        [Test]
        public void NonFiniteScaleCurveReturnsInfiniteSafetyBound()
        {
            var root = new GameObject("Root");
            try
            {
                var target = new GameObject("Target"); target.transform.SetParent(root.transform, false);
                var binding = Binding("Target", typeof(Transform), "m_LocalScale.x");
                var nanClip = VirtualClip.Create("NaN scale");
                nanClip.SetFloatCurve(binding, new AnimationCurve(new Keyframe(0f, float.NaN)));
                var nanAnalyzer = new AnimationAnalyzer(new AnimationIndex(new[] { nanClip }), root.transform);
                Assert.That(float.IsPositiveInfinity(nanAnalyzer.MaximumAreaScale(target.transform)), Is.True);

                var infinityClip = VirtualClip.Create("infinite scale");
                infinityClip.SetFloatCurve(binding,
                    new AnimationCurve(new Keyframe(0f, float.PositiveInfinity)));
                var infinityAnalyzer = new AnimationAnalyzer(new AnimationIndex(new[] { infinityClip }), root.transform);
                Assert.That(float.IsPositiveInfinity(infinityAnalyzer.MaximumAreaScale(target.transform)), Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void NonFiniteScaleSafetyPreservesResolutionWithoutPoisoningIslandArea()
        {
            foreach (var unsafeScale in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
            {
                var renderer = new RendererRecord();
                Assert.That(AvatarAnalyzer.ConfigureAreaScaleSafety(renderer, unsafeScale, false), Is.True);
                Assert.That(renderer.PreserveOriginalIslandResolution, Is.True);
                Assert.That(renderer.MaximumAreaScale, Is.EqualTo(1f));
                Assert.That(float.IsNaN(renderer.MaximumAreaScale) || float.IsInfinity(renderer.MaximumAreaScale),
                    Is.False);
            }

            var skinned = new RendererRecord();
            Assert.That(AvatarAnalyzer.ConfigureAreaScaleSafety(skinned, 9f, true), Is.False);
            Assert.That(skinned.PreserveOriginalIslandResolution, Is.True);
            Assert.That(skinned.MaximumAreaScale, Is.EqualTo(9f));

            var finite = new RendererRecord();
            Assert.That(AvatarAnalyzer.ConfigureAreaScaleSafety(finite, 4f, false), Is.False);
            Assert.That(finite.PreserveOriginalIslandResolution, Is.False);
            Assert.That(finite.MaximumAreaScale, Is.EqualTo(4f));

            var external = new RendererRecord();
            Assert.That(AvatarAnalyzer.ConfigureAreaScaleSafety(external, 4f, false, true), Is.False);
            Assert.That(external.PreserveOriginalIslandResolution, Is.True,
                "additive or constraint-driven scale uncertainty must combine with bone/unbounded safety using OR");
            Assert.That(external.MaximumAreaScale, Is.EqualTo(4f));
        }

        [Test]
        public void GameObjectAndTransformBindingsRemainTypeClosed()
        {
            var root = new GameObject("Root");
            try
            {
                var child = new GameObject("Child"); child.transform.SetParent(root.transform, false);
                var gameObjectBinding = Binding("Child", typeof(GameObject), "m_IsActive");
                var transformBinding = Binding("Child", typeof(Transform), "m_LocalScale.x");

                Assert.That(AnimationAnalyzer.ResolveBindingTarget(root.transform, gameObjectBinding), Is.SameAs(child));
                Assert.That(AnimationAnalyzer.ResolveBindingTarget(root.transform, transformBinding), Is.SameAs(child.transform));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void MaterialBindingParsersRequireExactSlotAndPropertySyntax()
        {
            Assert.That(AnimationAnalyzer.TryGetMaterialSlot("m_Materials.Array.data[3]", out var materialSlot), Is.True);
            Assert.That(materialSlot, Is.EqualTo(3));
            Assert.That(AnimationAnalyzer.TryGetMaterialSlot("m_Materials.Array.data[3].extra", out _), Is.False);

            Assert.That(AnimationAnalyzer.TryGetMaterialProperty("material._MainTex_ST.x", out var firstSlot,
                out var firstProperty), Is.True);
            Assert.That(firstSlot, Is.Zero); Assert.That(firstProperty, Is.EqualTo("_MainTex_ST.x"));
            Assert.That(AnimationAnalyzer.TryGetMaterialProperty("materials.Array.data[2]._Cutoff", out var secondSlot,
                out var secondProperty), Is.True);
            Assert.That(secondSlot, Is.EqualTo(2)); Assert.That(secondProperty, Is.EqualTo("_Cutoff"));
            Assert.That(AnimationAnalyzer.TryGetMaterialProperty("materials.Array.data[2]junk._Cutoff", out _, out _),
                Is.False);
        }

        private static System.Collections.Generic.List<float> AccumulateCutoffs(GameObject root,
            MeshRenderer renderer, Material material, VirtualClip clip)
        {
            var analyzer = new AnimationAnalyzer(new AnimationIndex(new[] { clip }), root.transform);
            var mode = ATOAlphaMode.Opaque; var cutout = false; var blend = false; var cutoff = 0.5f;
            var values = new System.Collections.Generic.List<float>();
            analyzer.AccumulateAlphaStates(renderer, "Target", 0, material,
                ref mode, ref cutout, ref blend, ref cutoff, values);
            return values;
        }

        private static MeshRenderer NewMeshRenderer(Transform parent, string name)
        {
            var value = new GameObject(name); value.transform.SetParent(parent, false);
            value.AddComponent<MeshFilter>(); return value.AddComponent<MeshRenderer>();
        }

        private static EditorCurveBinding Binding(string path, System.Type type, string property) =>
            EditorCurveBinding.FloatCurve(path, type, property);

        private static EditorCurveBinding ObjectBinding(string path, System.Type type, string property) =>
            EditorCurveBinding.PPtrCurve(path, type, property);
    }
}

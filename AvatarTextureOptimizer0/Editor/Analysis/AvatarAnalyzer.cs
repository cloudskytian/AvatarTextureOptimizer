using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal sealed class AvatarAnalyzer
    {
        private readonly BuildContext _context;
        private readonly AvatarTextureOptimizer _component;
        private readonly ATOOptimizationSettings _settings;
        private readonly AnimationAnalyzer _animations;
        private readonly ShaderTextureAnalyzer _shaderAnalyzer = new ShaderTextureAnalyzer();
        private readonly IATOExtension[] _extensions;
        private readonly string[] _preAnalysisWarnings;

        public AvatarAnalyzer(BuildContext context, AvatarTextureOptimizer component, ATOOptimizationSettings settings,
            IATOExtension[] extensions, IEnumerable<string> preAnalysisWarnings)
        {
            _context = context; _component = component; _settings = settings;
            var animatorServices = context.Extension<AnimatorServicesContext>();
            _animations = new AnimationAnalyzer(animatorServices.AnimationIndex, context.AvatarRootTransform,
                () => animatorServices.ControllerContext.GetAllControllers()
                    .SelectMany(controller => controller.Layers)
                    .Where(layer => layer.BlendingMode == UnityEditor.Animations.AnimatorLayerBlendingMode.Additive)
                    .SelectMany(layer => layer.AllReachableNodes().OfType<VirtualClip>()));
            _extensions = extensions ?? Array.Empty<IATOExtension>();
            _preAnalysisWarnings = preAnalysisWarnings == null ? Array.Empty<string>() : preAnalysisWarnings.ToArray();
        }

        public AvatarAnalysis Analyze()
        {
            var analysis = new AvatarAnalysis();
            analysis.WhitelistedTextures.UnionWith(WhitelistResolver.Resolve(_component.whitelist));

            var renderers = _context.AvatarRootObject.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer is SkinnedMeshRenderer || renderer is MeshRenderer)
                .Where(renderer => !IsEditorOnly(renderer.transform)).ToArray();
            // A duplicate path is unsafe even when the competing Transform has no supported Renderer: Unity resolves
            // the Transform first, so a later component-only search would otherwise attribute its curve to the wrong object.
            var ambiguousPaths = AnimationAnalyzer.FindAmbiguousTransformPaths(_context.AvatarRootTransform);
            foreach (var renderer in renderers)
            {
                ATOProgress.Checkpoint("Scanning renderer " + (renderer == null ? "<null>" : renderer.name));
                if (!_animations.CanBecomeEnabled(renderer)) continue;
                var mesh = GetMesh(renderer);
                if (mesh == null) { analysis.Fallbacks.Add(new FallbackRecord(renderer, "renderer has no mesh")); continue; }
                AnalyzeRenderer(analysis, renderer, mesh, ambiguousPaths.Contains(_animations.Path(renderer)));
            }

            BuildDeduplicationMap(analysis, _settings.deduplicateTexturesAndAtlases);
            PromoteWhitelistAcrossDuplicates(analysis);
            BuildUvGroups(analysis);
            foreach (var warning in _preAnalysisWarnings.Where(value => !string.IsNullOrWhiteSpace(value)))
                analysis.Fallbacks.Add(new FallbackRecord(_component, warning));
            return analysis;
        }

        private void AnalyzeRenderer(AvatarAnalysis analysis, Renderer renderer, Mesh mesh, bool ambiguousAnimationPath)
        {
            var skinned = renderer as SkinnedMeshRenderer;
            var hasBoneSkinning = skinned != null && skinned.bones != null &&
                                  skinned.bones.Length > 0 && mesh.bindposes.Length > 0;
            var maximumAreaScale = _animations.MaximumAreaScale(renderer.transform);
            var hasAdditiveScale = _animations.HasAdditiveScaleAnimation(renderer.transform);
            var hasExternalScaleDriver = HasExternalScaleDriver(renderer.transform, _context.AvatarRootTransform);
            var hasUnmodeledBlendShapeDriver = skinned != null &&
                                               _animations.HasUnmodeledBlendShapeDriver(skinned, mesh);
            var rendererRecord = new RendererRecord
            {
                Renderer = renderer, Mesh = mesh, Path = _animations.Path(renderer)
            };
            var hasUnboundedScale = ConfigureAreaScaleSafety(rendererRecord, maximumAreaScale, hasBoneSkinning,
                hasAdditiveScale || hasExternalScaleDriver || hasUnmodeledBlendShapeDriver);
            analysis.Renderers.Add(rendererRecord);
            if (hasBoneSkinning)
                analysis.Fallbacks.Add(new FallbackRecord(renderer,
                    "bone-relative deformation cannot be finitely bounded across animation, constraints, and physics; " +
                    "original island resolution is preserved"));
            if (hasAdditiveScale)
                analysis.Fallbacks.Add(new FallbackRecord(renderer,
                    "additive transform-scale animation has no proven finite composition bound; " +
                    "original island resolution is preserved"));
            if (hasExternalScaleDriver)
                analysis.Fallbacks.Add(new FallbackRecord(renderer,
                    "a scale constraint can drive transform area outside analyzed clips; " +
                    "original island resolution is preserved"));
            if (hasUnmodeledBlendShapeDriver)
                analysis.Fallbacks.Add(new FallbackRecord(renderer,
                    "blend-shape weights are not proven to remain in the analyzed 0..100 range; " +
                    "original island resolution is preserved"));
            if (hasUnboundedScale)
                analysis.Fallbacks.Add(new FallbackRecord(renderer,
                    "animated transform scale could not be finitely bounded; original island resolution is preserved"));
            var materials = renderer.sharedMaterials;
            for (var slotIndex = 0; slotIndex < materials.Length; slotIndex++)
            {
                if (slotIndex >= mesh.subMeshCount)
                {
                    analysis.Fallbacks.Add(new FallbackRecord(renderer, "material slot has no matching submesh"));
                    continue;
                }
                var slot = new MaterialSlotRecord { Slot = slotIndex };
                if (materials[slotIndex] != null) slot.Materials.Add(materials[slotIndex]);
                foreach (var animated in _animations.AnimatedMaterials(rendererRecord.Renderer, rendererRecord.Path, slotIndex))
                    slot.Materials.Add(animated);
                if (_animations.HasUnsupportedAnimatedMaterial(rendererRecord.Renderer, rendererRecord.Path, slotIndex))
                {
                    slot.AtlasUnsafe = true;
                    analysis.Fallbacks.Add(new FallbackRecord(renderer,
                        "animated material slot contains a non-Material object value"));
                }
                rendererRecord.Slots.Add(slot);
                foreach (var material in slot.Materials) AnalyzeMaterial(analysis, rendererRecord, slot, material);
                if (!ambiguousAnimationPath) continue;
                slot.AtlasUnsafe = true;
                foreach (var binding in slot.Bindings)
                {
                    binding.AtlasSafe = false;
                    binding.UnsafeReason = "duplicate hierarchy path cannot identify one animation target";
                }
            }
            if (ambiguousAnimationPath)
                analysis.Fallbacks.Add(new FallbackRecord(renderer,
                    "duplicate hierarchy path cannot identify one animation target"));
        }

        private void AnalyzeMaterial(AvatarAnalysis analysis, RendererRecord renderer, MaterialSlotRecord slot, Material material)
        {
            if (material == null || material.shader == null) return;
            Func<string, bool> animatedTransform = property => _animations.IsTextureTransformAnimated(
                renderer.Renderer, renderer.Path, slot.Slot, property);
            Func<string, IEnumerable<Texture2D>> animatedTextures = property =>
                _animations.AnimatedTextures(renderer.Renderer, renderer.Path, slot.Slot, property);
            Func<string, bool> textureAnimated = property => _animations.IsTextureAnimated(
                renderer.Renderer, renderer.Path, slot.Slot, property);
            foreach (var propertyInfo in _shaderAnalyzer.Analyze(material, animatedTransform, animatedTextures, textureAnimated))
            {
                var assignedTexture = material.GetTexture(propertyInfo.PropertyName);
                if ((assignedTexture != null && !(assignedTexture is Texture2D)) ||
                    _animations.HasUnsupportedAnimatedTexture(renderer.Renderer, renderer.Path, slot.Slot,
                        propertyInfo.PropertyName))
                {
                    if (!slot.AtlasUnsafe)
                        analysis.Fallbacks.Add(new FallbackRecord(renderer.Renderer,
                            "material texture state contains a non-Texture2D value"));
                    slot.AtlasUnsafe = true;
                }
                var source = assignedTexture as Texture2D;
                if (source != null) AddBinding(analysis, renderer, slot, material, propertyInfo, source, true, false);
                foreach (var animatedTexture in _animations.AnimatedTextures(renderer.Renderer, renderer.Path,
                             slot.Slot, propertyInfo.PropertyName).Distinct())
                    AddBinding(analysis, renderer, slot, material, propertyInfo, animatedTexture, false, true);
            }
        }

        private void AddBinding(AvatarAnalysis analysis, RendererRecord renderer, MaterialSlotRecord slot, Material material,
            ShaderTextureInfo propertyInfo, Texture2D texture, bool initialValue, bool animatedValue)
        {
            var alphaMode = ATOAlphaMode.Opaque;
            var evaluateCutout = false;
            var evaluateBlend = false;
            var cutoff = 0.5f; var cutoffs = new List<float>();
            _animations.AccumulateAlphaStates(renderer.Renderer, renderer.Path, slot.Slot, material,
                ref alphaMode, ref evaluateCutout, ref evaluateBlend, ref cutoff, cutoffs);
            var unsupportedCutoffCurve = cutoffs.Any(value => float.IsNaN(value) || float.IsInfinity(value));
            cutoffs.RemoveAll(value => float.IsNaN(value) || float.IsInfinity(value));

            var materialUsesSurfaceAlpha = evaluateCutout || evaluateBlend;
            var alphaSemanticsUnsupported = materialUsesSurfaceAlpha &&
                                            propertyInfo.SurfaceAlphaUsage == ATOSurfaceAlphaUsage.UnsupportedComposite;
            var drivesSurfaceAlpha = false;
            var kind = ShaderTextureAnalyzer.Classify(material, propertyInfo.PropertyName, texture);
            var uvChannel = propertyInfo.UvChannel;
            var builtInRejected = !propertyInfo.Safe || unsupportedCutoffCurve || alphaSemanticsUnsupported;
            var rejected = builtInRejected;
            var rejectionReason = unsupportedCutoffCurve
                ? "animated render mode or weighted/invalid cutoff cannot be bounded with complete pass state"
                : alphaSemanticsUnsupported
                    ? "surface alpha combines channels or properties that cannot be evaluated independently"
                    : propertyInfo.Reason;
            var classification = new ATOTextureClassificationContext
            {
                Material = material, PropertyName = propertyInfo.PropertyName, Texture = texture,
                Kind = kind, SurfaceAlphaUsage = propertyInfo.SurfaceAlphaUsage,
                UvChannel = uvChannel, RejectAsUnsafe = rejected, RejectionReason = rejectionReason
            };
            var classifierReason = ApplyExtensionClassifiers(classification, _extensions,
                out var classifierRejected);
            if (!Enum.IsDefined(typeof(ATOTextureKind), classification.Kind) ||
                !Enum.IsDefined(typeof(ATOSurfaceAlphaUsage), classification.SurfaceAlphaUsage))
            {
                // An extension is untrusted input at this boundary. Invalid semantic enum values must not silently
                // select an arbitrary compression or alpha path; retain analyzable defaults and fail this binding closed.
                // 扩展返回非法语义枚举时恢复可分析默认值，并对该绑定执行安全跳过。
                classification.Kind = kind;
                classification.SurfaceAlphaUsage = propertyInfo.SurfaceAlphaUsage;
                classifierRejected = true;
                classifierReason = "an extension returned an invalid texture kind or surface-alpha usage";
            }
            kind = classification.Kind; uvChannel = classification.UvChannel;
            var effectiveAlphaSemanticsUnsupported = RequiresSurfaceAlphaFallback(materialUsesSurfaceAlpha,
                propertyInfo.SurfaceAlphaUsage, classification.SurfaceAlphaUsage);
            rejected = builtInRejected || effectiveAlphaSemanticsUnsupported || classifierRejected;
            rejectionReason = builtInRejected
                ? rejectionReason
                : effectiveAlphaSemanticsUnsupported
                    ? "surface alpha combines channels or properties that cannot be evaluated independently"
                    : classifierReason;
            if (effectiveAlphaSemanticsUnsupported) slot.AtlasUnsafe = true;

            // RGBA that does not directly drive surface compositing is straight channel data. It must retain A,
            // but must never enter the premultiplied color path or use its A as a silhouette threshold. An extension
            // cannot clear an earlier unsupported-composite result or introduce one without forcing the whole slot to
            // fallback. / 非表面 Alpha 的 RGBA 保持逐通道；扩展不能清除或绕过复合 Alpha 安全结论。
            var effectiveAlphaUsage = effectiveAlphaSemanticsUnsupported
                ? ATOSurfaceAlphaUsage.UnsupportedComposite : classification.SurfaceAlphaUsage;
            kind = ResolveTextureKindForAlpha(kind, materialUsesSurfaceAlpha,
                effectiveAlphaUsage, out drivesSurfaceAlpha, out var evaluatePackedChannels);
            if (drivesSurfaceAlpha && kind != ATOTextureKind.ColorAlpha)
            {
                rejected = true;
                rejectionReason = "surface alpha property was reclassified to an incompatible texture type";
                slot.AtlasUnsafe = true;
            }
            evaluateCutout &= drivesSurfaceAlpha;
            evaluateBlend &= drivesSurfaceAlpha;
            if (!drivesSurfaceAlpha) alphaMode = ATOAlphaMode.Opaque;

            if (uvChannel < 0 || uvChannel > 7 || !MeshHasUv(renderer.Mesh, uvChannel))
            {
                rejected = true; rejectionReason = "selected UV channel is absent";
            }
            if (_settings.generateAtlases && renderer.Renderer.GetComponent<Cloth>() != null)
            {
                rejected = true;
                rejectionReason = "renderer has a Cloth component whose per-vertex data cannot be remapped safely";
            }
            if (_settings.generateAtlases && renderer.Renderer is MeshRenderer meshRenderer &&
                meshRenderer.additionalVertexStreams != null)
            {
                rejected = true;
                rejectionReason = "MeshRenderer additional vertex streams cannot be reindexed safely";
            }
            var whitelisted = analysis.WhitelistedTextures.Contains(texture) || rejected;
            var existing = slot.Bindings.FirstOrDefault(value => value.Material == material &&
                value.PropertyName == propertyInfo.PropertyName && value.Texture == texture);
            if (existing != null)
            {
                existing.IsInitialValue |= initialValue; existing.IsAnimatedValue |= animatedValue;
                existing.AtlasSafe &= !whitelisted; existing.Whitelisted |= whitelisted;
                return;
            }
            var binding = new TextureBindingRecord
            {
                Renderer = renderer, Slot = slot, Material = material, PropertyName = propertyInfo.PropertyName,
                Texture = texture, OriginalTexture = texture, Kind = kind, UvChannel = uvChannel, AlphaMode = alphaMode,
                EvaluateCutout = evaluateCutout, EvaluateBlend = evaluateBlend,
                EvaluatePackedChannels = evaluatePackedChannels, UsedChannels = propertyInfo.UsedChannels,
                Cutoff = drivesSurfaceAlpha ? cutoff : 0.5f,
                Cutoffs = drivesSurfaceAlpha ? cutoffs.Distinct().OrderBy(value => value).ToArray() : Array.Empty<float>(),
                Whitelisted = whitelisted, AtlasSafe = !whitelisted, UnsafeReason = rejectionReason,
                ImportSignature = TextureFingerprint.ImportSettings(texture),
                IsInitialValue = initialValue, IsAnimatedValue = animatedValue
            };
            slot.Bindings.Add(binding);
            if (rejected) analysis.Fallbacks.Add(new FallbackRecord(texture, rejectionReason ?? "shader usage cannot be proven safe"));
        }

        internal static bool RequiresSurfaceAlphaFallback(bool materialUsesSurfaceAlpha,
            ATOSurfaceAlphaUsage builtInUsage, ATOSurfaceAlphaUsage classifiedUsage)
        {
            // Extension classifiers may add conservative knowledge, but cannot erase a built-in unsafe conclusion.
            // Conversely, an extension that introduces UnsupportedComposite must not rely on also setting a separate
            // veto flag: this semantic value is itself a fail-closed declaration.
            // 扩展可增加保守信息但不能清除内建结论；UnsupportedComposite 本身就是必须回退的声明。
            return materialUsesSurfaceAlpha &&
                   (builtInUsage == ATOSurfaceAlphaUsage.UnsupportedComposite ||
                    classifiedUsage == ATOSurfaceAlphaUsage.UnsupportedComposite);
        }

        internal static ATOTextureKind ResolveTextureKindForAlpha(ATOTextureKind classified,
            bool materialUsesSurfaceAlpha, ATOSurfaceAlphaUsage usage, out bool drivesSurfaceAlpha,
            out bool evaluatePackedChannels)
        {
            drivesSurfaceAlpha = materialUsesSurfaceAlpha && usage == ATOSurfaceAlphaUsage.TextureAlpha;
            // Even an importer that reports no source alpha must store the runtime constant-one channel in an
            // alpha-capable output once the shader's surface semantics depend on it. This also prevents an extension
            // from routing a real alpha texture through RGB24/DXT1/ETC2RGB opaque compression.
            // 只要表面语义依赖 Alpha，就统一进入保 Alpha 类型，禁止扩展把真实 Alpha 路由到无 Alpha 压缩。
            if (drivesSurfaceAlpha && classified == ATOTextureKind.ColorOpaque)
                classified = ATOTextureKind.ColorAlpha;
            evaluatePackedChannels = classified == ATOTextureKind.ColorAlpha && !drivesSurfaceAlpha;
            return evaluatePackedChannels ? ATOTextureKind.ColorRgbaData : classified;
        }

        internal static string ApplyExtensionClassifiers(ATOTextureClassificationContext classification,
            IEnumerable<IATOExtension> extensions, out bool rejected)
        {
            if (classification == null) throw new ArgumentNullException(nameof(classification));
            rejected = classification.RejectAsUnsafe;
            var reason = rejected ? classification.RejectionReason : null;
            var unsupportedComposite = classification.SurfaceAlphaUsage == ATOSurfaceAlphaUsage.UnsupportedComposite;
            foreach (var extension in extensions ?? Enumerable.Empty<IATOExtension>())
            {
                if (extension == null) continue;
                extension.ClassifyTexture(classification);
                unsupportedComposite |= classification.SurfaceAlphaUsage == ATOSurfaceAlphaUsage.UnsupportedComposite;
                if (classification.RejectAsUnsafe)
                {
                    rejected = true;
                    if (string.IsNullOrWhiteSpace(reason)) reason = classification.RejectionReason;
                }
                // Safety declarations are monotonic. Later classifiers may refine Kind/UV, but cannot silently clear
                // an earlier veto or UnsupportedComposite semantic. / 安全声明单向生效，后续扩展不能静默撤销。
                if (unsupportedComposite)
                    classification.SurfaceAlphaUsage = ATOSurfaceAlphaUsage.UnsupportedComposite;
                if (!rejected) continue;
                classification.RejectAsUnsafe = true;
                classification.RejectionReason = reason;
            }
            if (rejected && string.IsNullOrWhiteSpace(reason))
                reason = "an extension rejected this texture binding without a reason";
            classification.RejectionReason = reason;
            return reason;
        }

        internal static bool HasExternalScaleDriver(Transform target, Transform avatarRoot)
        {
            if (target == null || avatarRoot == null) return false;
            for (var current = target; current != null && current.IsChildOf(avatarRoot); current = current.parent)
            {
                foreach (var component in current.GetComponents<Component>())
                {
                    if (component == null) continue;
                    var fullName = component.GetType().FullName;
                    if (fullName == "UnityEngine.Animations.ScaleConstraint" ||
                        fullName == "VRC.SDK3.Dynamics.Constraint.Components.VRCScaleConstraint") return true;
                }
                if (current == avatarRoot) break;
            }
            return false;
        }

        internal static bool ConfigureAreaScaleSafety(RendererRecord renderer, float maximumAreaScale,
            bool hasBoneSkinning, bool hasUnmodeledScaleDriver = false)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            var unbounded = float.IsNaN(maximumAreaScale) || float.IsInfinity(maximumAreaScale) ||
                            maximumAreaScale < 0f;
            // Keep downstream area arithmetic finite once the uncertainty has already selected preserve mode.
            // 一旦不确定缩放进入保留模式，就不再让 NaN/Infinity 污染后续面积计算。
            renderer.MaximumAreaScale = unbounded ? 1f : maximumAreaScale;
            renderer.PreserveOriginalIslandResolution = hasBoneSkinning || hasUnmodeledScaleDriver || unbounded;
            return unbounded;
        }

        internal static void BuildDeduplicationMap(AvatarAnalysis analysis, bool deduplicate)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            var groups = deduplicate ? new Dictionary<string, Texture2D>(StringComparer.Ordinal) : null;
            foreach (var texture in analysis.Renderers.SelectMany(r => r.Slots).SelectMany(s => s.Bindings)
                         .Select(b => b.Texture).Where(t => t != null).Distinct())
            {
                Texture2D canonical;
                if (deduplicate)
                {
                    ATOProgress.Checkpoint("Fingerprinting texture " + texture.name);
                    var key = TextureFingerprint.Build(texture);
                    if (!groups.TryGetValue(key, out canonical)) groups[key] = canonical = texture;
                }
                else canonical = texture;
                analysis.CanonicalTextures[texture] = canonical;
                analysis.InputTexturePixels += (long)texture.width * texture.height;
            }
        }

        internal static void PromoteWhitelistAcrossDuplicates(AvatarAnalysis analysis)
        {
            var whiteCanonical = new HashSet<Texture2D>(analysis.WhitelistedTextures
                .Where(analysis.CanonicalTextures.ContainsKey).Select(t => analysis.CanonicalTextures[t]));
            foreach (var binding in analysis.Renderers.SelectMany(r => r.Slots).SelectMany(s => s.Bindings))
            {
                binding.Texture = analysis.CanonicalTextures[binding.Texture];
                if (!whiteCanonical.Contains(binding.Texture)) continue;
                binding.Whitelisted = true; binding.AtlasSafe = false;
                analysis.WhitelistedTextures.Add(binding.Texture);
            }
        }

        private static void BuildUvGroups(AvatarAnalysis analysis)
        {
            var nextId = 0;
            foreach (var renderer in analysis.Renderers)
            foreach (var slot in renderer.Slots)
            foreach (var channelGroup in slot.Bindings.GroupBy(binding => binding.UvChannel))
            {
                var group = new UvGroupRecord
                {
                    Id = nextId++, Renderer = renderer, Slot = slot, UvChannel = channelGroup.Key,
                    AtlasSafe = !slot.AtlasUnsafe && channelGroup.All(binding => binding.AtlasSafe)
                };
                group.Bindings.AddRange(channelGroup);
                group.TypeGroupKey = BuildTypeGroupKey(group.Bindings);
                analysis.UvGroups.Add(group);
            }

            var typeGroups = analysis.TextureBindings.GroupBy(binding => new TextureTypeKey(binding.Kind,
                TextureFingerprint.IsSrgb(binding.Texture), binding.Texture.filterMode,
                binding.Texture.anisoLevel, binding.Texture.mipMapBias)).ToArray();
            for (var i = 0; i < typeGroups.Length; i++)
            {
                var record = new TextureTypeGroupRecord { Id = i, Key = typeGroups[i].Key };
                record.Bindings.AddRange(typeGroups[i]);
                analysis.TextureTypeGroups.Add(record);
            }
        }

        private static string BuildTypeGroupKey(IEnumerable<TextureBindingRecord> bindings)
        {
            var list = bindings.ToList();
            var kinds = string.Join("+", list.Select(binding => binding.Kind).Distinct().OrderBy(kind => kind));
            var sampling = string.Join("+", list.Select(binding => binding.Texture)
                .Select(texture => TextureFingerprint.IsSrgb(texture) + ":" + texture.filterMode +
                                   ":aniso=" + texture.anisoLevel + ":bias=" +
                                   texture.mipMapBias.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Distinct().OrderBy(x => x));
            return kinds + "|" + sampling;
        }

        private static bool MeshHasUv(Mesh mesh, int channel)
        {
            var values = new List<Vector2>();
            mesh.GetUVs(channel, values);
            return values.Count == mesh.vertexCount;
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        private static bool IsEditorOnly(Transform value)
        {
            for (var current = value; current != null; current = current.parent)
                if (current.CompareTag("EditorOnly")) return true;
            return false;
        }
    }
}

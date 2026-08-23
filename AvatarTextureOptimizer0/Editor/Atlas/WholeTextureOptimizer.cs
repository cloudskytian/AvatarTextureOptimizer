using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>Scales complete textures without cropping or changing UVs. / 不裁剪、不改 UV 的整图缩放路径。</summary>
    internal sealed class WholeTextureOptimizer : IDisposable
    {
        internal sealed class Result
        {
            public readonly Dictionary<TextureBindingRecord, Texture2D> Replacements = new Dictionary<TextureBindingRecord, Texture2D>();
            // Only textures created by this optimizer belong to its failure-cleanup domain. Identity-only replacements
            // may point at pre-existing non-persistent Avatar textures and must never be reclaimed here.
            // 仅优化器创建的贴图属于失败清理域；identity-only 替换可能指向 Avatar 原有瞬态贴图，不得在此回收。
            public readonly HashSet<Texture2D> GeneratedTextures = new HashSet<Texture2D>();
            public long OutputPixels;
            public IATOCommitTransaction CommitTransaction;
        }

        private readonly ATOOptimizationSettings _settings;
        private readonly IAssetSaver _assetSaver;
        private readonly AnimationIndex _animationIndex;
        private readonly GpuLinearResampler _resampler = new GpuLinearResampler();
        private readonly Dictionary<string, Texture2D> _dedup = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        public WholeTextureOptimizer(ATOOptimizationSettings settings, IAssetSaver assetSaver, AnimationIndex animationIndex)
        {
            _settings = settings; _assetSaver = assetSaver; _animationIndex = animationIndex;
        }

        public Result BuildAndCommit(AvatarAnalysis analysis)
        {
            Result result = null;
            try
            {
                result = Generate(analysis);
                var rewriter = new WholeTextureRewriter(_assetSaver, _animationIndex, _settings.deduplicateMaterials);
                result.CommitTransaction = rewriter.Apply(analysis, result.Replacements, result.GeneratedTextures);
                return result;
            }
            catch (Exception exception)
            {
                // Once Apply has failed to restore every Avatar/curve reference, generated materials may still point
                // at these textures. Preserve them rather than manufacturing dangling Unity references.
                // Apply 回滚不完整时生成材质仍可能引用这些贴图；宁可保留，也不能制造悬空引用。
                if (CanDestroyGeneratedTexturesAfterBuildFailure(exception))
                    DestroyTransientTextures((result == null ? Enumerable.Empty<Texture2D>() : result.GeneratedTextures)
                        .Concat(_dedup.Values));
                _dedup.Clear();
                throw;
            }
        }

        internal static bool CanDestroyGeneratedTexturesAfterBuildFailure(Exception exception) =>
            !(exception is ATORollbackIncompleteException);

        private Result Generate(AvatarAnalysis analysis)
        {
            var result = new Result();
            try
            {
            foreach (var textureGroup in analysis.TextureBindings.Where(value => value.Texture != null).GroupBy(value => value.Texture))
            {
                ATOProgress.Checkpoint("Optimizing whole texture " + textureGroup.Key.name);
                var bindings = textureGroup.ToArray();
                if (bindings.Any(value => !value.AtlasSafe)) continue;
                var uvGroups = analysis.UvGroups.Where(group => group.Bindings.Any(bindings.Contains)).ToArray();
                if (uvGroups.Length == 0 || uvGroups.Any(group => !group.AtlasSafe || group.Islands.Count == 0)) continue;
                var keys = bindings.Select(value => new TextureTypeKey(value.Kind,
                    TextureFingerprint.IsSrgb(value.Texture), value.Texture.filterMode,
                    value.Texture.anisoLevel, value.Texture.mipMapBias)).Distinct().ToArray();
                if (keys.Length != 1)
                {
                    analysis.Fallbacks.Add(new FallbackRecord(bindings[0].OriginalTexture,
                        "whole-texture mode found incompatible color-space, type, or filtering uses"));
                    continue;
                }
                var source = textureGroup.Key; var requiredWidth = 1; var requiredHeight = 1;
                foreach (var island in uvGroups.SelectMany(value => value.Islands))
                {
                    requiredWidth = Mathf.Max(requiredWidth, Mathf.CeilToInt(island.TargetPixelSize.x /
                        Mathf.Max(1e-7f, island.UvBounds.width)));
                    requiredHeight = Mathf.Max(requiredHeight, Mathf.CeilToInt(island.TargetPixelSize.y /
                        Mathf.Max(1e-7f, island.UvBounds.height)));
                }
                var target = new Vector2Int(Mathf.Clamp(requiredWidth, 1, source.width),
                    Mathf.Clamp(requiredHeight, 1, source.height));
                var sourceMipReduction = SelectSourceMipReduction(source, target);
                if (source.mipmapCount > 1)
                    target = new Vector2Int(Mathf.Max(1, source.width >> sourceMipReduction),
                        Mathf.Max(1, source.height >> sourceMipReduction));
                if (target.x == source.width && target.y == source.height)
                {
                    // No resampling is needed, but exact pixel+import-setting duplicates may still be redirected to
                    // the already-persistent canonical Texture2D. This is an identity-only change proven by the
                    // fingerprint map and keeps the no-atlas mode's deduplication switch effective.
                    // 无需重采样时，仍可把像素与导入参数完全一致的副本引用改为已存在的规范贴图。
                    AddIdentityCanonicalReplacements(result, bindings, source,
                        _settings.deduplicateTexturesAndAtlases);
                    continue;
                }
                // WholeLevelPasses limits each later quality readback, but the complete RGBA16F/RGBA32 output is
                // allocated first. Reject the complete target before any allocation using the same conservative page
                // budget as Atlas plus the device's hard axis limit. / 逐岛质量门禁晚于整图分配，故须先检查整图预算。
                if (!FitsOutputBudget(target, SystemInfo.maxTextureSize))
                {
                    analysis.Fallbacks.Add(new FallbackRecord(bindings[0].OriginalTexture,
                        "whole-texture safety fallback: target exceeds the device texture-size limit or conservative GPU memory budget"));
                    continue;
                }
                var classSettings = TextureFormatResolver.ClassSettings(keys[0].Kind, _settings);
                if (classSettings.mipmapsAndStreaming && (target.x > 1 || target.y > 1) &&
                    TextureLodSafety.RequiresFractionalLodFallback(keys[0].FilterMode, 2))
                {
                    analysis.Fallbacks.Add(new FallbackRecord(bindings[0].OriginalTexture,
                        "whole-texture safety fallback: Trilinear mip filtering requires an unproven fractional-LOD quality gate"));
                    continue;
                }
                var generated = GenerateTexture(source, target, keys[0], bindings, uvGroups,
                    sourceMipReduction, result.GeneratedTextures);
                if (generated == null)
                {
                    analysis.Fallbacks.Add(new FallbackRecord(bindings[0].OriginalTexture,
                        "final whole-texture output or one of its persisted mip levels did not meet quality"));
                    continue;
                }
                foreach (var binding in bindings) result.Replacements[binding] = generated;
            }
            result.OutputPixels = result.Replacements.Values.Distinct().Sum(value => (long)value.width * value.height);
            return result;
            }
            catch
            {
                DestroyTransientTextures(result.GeneratedTextures.Concat(_dedup.Values));
                _dedup.Clear();
                throw;
            }
        }

        internal static void AddIdentityCanonicalReplacements(Result result,
            IEnumerable<TextureBindingRecord> bindings, Texture2D canonical, bool enabled)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (!enabled || canonical == null || bindings == null) return;
            foreach (var binding in bindings)
                if (binding != null && binding.OriginalTexture != null && binding.OriginalTexture != canonical)
                    result.Replacements[binding] = canonical;
        }

        internal static bool FitsOutputBudget(Vector2Int size, int deviceMaximum)
        {
            return size.x > 0 && size.y > 0 && deviceMaximum > 0 &&
                   size.x <= deviceMaximum && size.y <= deviceMaximum &&
                   (long)size.x * size.y <= ShapeAtlasPacker.MaximumAtlasPixels;
        }

        internal static int SelectSourceMipReduction(Texture2D source, Vector2Int required)
        {
            if (source == null || source.mipmapCount <= 1) return 0;
            // A whole-texture replacement keeps the same mip bias. Restrict reduction to an integer source mip
            // offset so candidate LOD c corresponds exactly to source LOD c+offset at runtime.
            var reduction = 0;
            while (reduction + 1 < source.mipmapCount)
            {
                var nextReduction = reduction + 1;
                var next = new Vector2Int(Mathf.Max(1, source.width >> nextReduction),
                    Mathf.Max(1, source.height >> nextReduction));
                if (next.x < required.x || next.y < required.y) break;
                // Runtime LOD is derived from the replacement base dimensions. For an odd/NPOT source, right-shifted
                // mip dimensions are not necessarily an exact 2^n base reduction (for example 3 -> 1). In that case
                // an unchanged mip bias cannot map candidate LOD c to source LOD c+n, so stop at the last exact level.
                // 奇数/NPOT 尺寸右移不一定等于精确 2^n 缩放，此时不能声称 LOD 偏移完全一致。
                var scale = 1L << nextReduction;
                if ((long)next.x * scale != source.width || (long)next.y * scale != source.height) break;
                reduction = nextReduction;
            }
            return reduction;
        }

        private Texture2D GenerateTexture(Texture2D source, Vector2Int size, TextureTypeKey key,
            IReadOnlyCollection<TextureBindingRecord> bindings, IReadOnlyCollection<UvGroupRecord> uvGroups,
            int sourceMipReduction, ISet<Texture2D> generatedTextures)
        {
            RenderTexture rendered = null; Texture2D output = null;
            try
            {
                var preserveSourceMips = source.mipmapCount > 1;
                rendered = _resampler.Resample(source, new Rect(0f, 0f, 1f, 1f), size,
                    preserveSourceMips || source.filterMode == FilterMode.Point, false, true, key.Kind,
                    ATONormalInputEncoding.Imported, false, sourceMipReduction);
                var format = TextureFormatResolver.Resolve(key, _settings);
                var normalEncoding = key.Kind == ATOTextureKind.Normal
                    ? TextureFormatResolver.NormalStorageEncoding(format) : ATONormalInputEncoding.Imported;
                output = ReadRgba(rendered, source, key, "ATO_Whole_" + source.name, normalEncoding,
                    sourceMipReduction);
                if (!WholeOutputPasses(source, output, key, bindings, uvGroups, normalEncoding,
                        sourceMipReduction))
                {
                    UnityEngine.Object.DestroyImmediate(output); output = null; return null;
                }
                if (format != TextureFormat.RGBA32)
                {
                    try
                    {
                        EditorUtility.CompressTexture(output, format, TextureCompressionQuality.Best);
                        if (!WholeOutputPasses(source, output, key, bindings, uvGroups, normalEncoding,
                                sourceMipReduction))
                            throw new InvalidOperationException("compressed whole texture did not meet the configured island and mip quality thresholds");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[ATO] Whole-texture compression fallback to RGBA32: " + exception.Message);
                        UnityEngine.Object.DestroyImmediate(output);
                        output = ReadRgba(rendered, source, key, "ATO_Whole_" + source.name, normalEncoding,
                            sourceMipReduction);
                        if (!WholeOutputPasses(source, output, key, bindings, uvGroups, normalEncoding,
                                sourceMipReduction))
                        {
                            UnityEngine.Object.DestroyImmediate(output); output = null; return null;
                        }
                    }
                }
                var streaming = TextureFormatResolver.ClassSettings(key.Kind, _settings).mipmapsAndStreaming;
                SetStreaming(output, streaming);
                string identity = null;
                if (_settings.deduplicateTexturesAndAtlases)
                {
                    identity = output.width + "x" + output.height + ":" + output.format + ":" + output.mipmapCount +
                               ":" + key.Srgb + ":" + output.filterMode + ":" + output.wrapModeU + ":" +
                               output.wrapModeV + ":" + output.wrapModeW + ":" + output.anisoLevel + ":" +
                               output.mipMapBias.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                               ":streaming=" + streaming + ":priority=0:" + output.imageContentsHash;
                    if (_dedup.TryGetValue(identity, out var existing))
                    {
                        UnityEngine.Object.DestroyImmediate(output); output = null; return existing;
                    }
                }
                // Finalize before publishing to either ownership collection. Any exception still leaves output under
                // this method's finally block. / 完成只读化后再发布到缓存及所有权集合；异常时仍由本方法 finally 回收。
                output.Apply(false, true);
                if (identity != null) _dedup.Add(identity, output);
                generatedTextures.Add(output);
                var completed = output; output = null; return completed;
            }
            finally
            {
                if (output != null && !EditorUtility.IsPersistent(output)) UnityEngine.Object.DestroyImmediate(output);
                GpuLinearResampler.Release(rendered);
            }
        }

        private bool WholeOutputPasses(Texture2D sourceTexture, Texture2D candidateTexture,
            TextureTypeKey key, IReadOnlyCollection<TextureBindingRecord> bindings,
            IReadOnlyCollection<UvGroupRecord> uvGroups, ATONormalInputEncoding normalEncoding,
            int sourceMipReduction)
        {
            if (TextureLodSafety.RequiresFractionalLodFallback(key.FilterMode, candidateTexture.mipmapCount))
                return false;
            if (sourceMipReduction > 0 &&
                candidateTexture.mipmapCount != sourceTexture.mipmapCount - sourceMipReduction) return false;
            foreach (var group in uvGroups)
            {
                ATOProgress.Checkpoint("Validating whole-texture UV group " + group.Id);
                var relevant = group.Bindings.Where(bindings.Contains).ToArray();
                if (relevant.Length == 0) continue;
                foreach (var island in group.Islands)
                {
                    // First prove total close-view loss against source mip 0 at its original island footprint.
                    if (!WholeLevelPasses(sourceTexture, 0, candidateTexture, 0, group, island, relevant, key,
                            normalEncoding, sourceTexture.filterMode == FilterMode.Point)) return false;
                    if (sourceMipReduction > 0)
                    {
                        // With a power-of-two base reduction and unchanged mip bias, runtime candidate LOD c maps to
                        // source LOD c+reduction. Compare every persisted level directly, including the 1x1 tail.
                        for (var candidateMip = 0; candidateMip < candidateTexture.mipmapCount; candidateMip++)
                            if (!WholeLevelPasses(sourceTexture, sourceMipReduction + candidateMip,
                                    candidateTexture, candidateMip, group, island, relevant, key, normalEncoding, true))
                                return false;
                    }
                    else if (sourceTexture.mipmapCount == 1 && candidateTexture.mipmapCount > 1)
                    {
                        // The source had no mip chain. Validate each generated level against a source-base reduction at
                        // that level's physical footprint rather than silently trusting Unity's mip generation.
                        for (var candidateMip = 1; candidateMip < candidateTexture.mipmapCount; candidateMip++)
                        {
                            var evaluationSize = new Vector2Int(
                                Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width *
                                    Mathf.Max(1, candidateTexture.width >> candidateMip))),
                                Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height *
                                    Mathf.Max(1, candidateTexture.height >> candidateMip))));
                            if (!WholeLevelPasses(sourceTexture, 0, candidateTexture, candidateMip, group,
                                    island, relevant, key, normalEncoding,
                                    sourceTexture.filterMode == FilterMode.Point, evaluationSize)) return false;
                        }
                    }
                }
            }
            return true;
        }

        private bool WholeLevelPasses(Texture2D sourceTexture, int sourceMip, Texture2D candidateTexture,
            int candidateMip, UvGroupRecord group, UvIsland island,
            IReadOnlyCollection<TextureBindingRecord> bindings, TextureTypeKey key,
            ATONormalInputEncoding normalEncoding, bool point, Vector2Int? evaluationSize = null)
        {
            var sourceWidth = Mathf.Max(1, sourceTexture.width >> sourceMip);
            var sourceHeight = Mathf.Max(1, sourceTexture.height >> sourceMip);
            var size = evaluationSize ?? new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * sourceWidth)),
                Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * sourceHeight)));
            if ((long)size.x * size.y > IslandQualityEvaluator.MaximumResidentPixels) return false;
            RenderTexture reference = null, candidate = null;
            NativeArray<float4> referencePixels = default, candidatePixels = default;
            NativeArray<byte> mask = default;
            try
            {
                reference = _resampler.Resample(sourceTexture, island.UvBounds, size, point, false, false,
                    key.Kind, ATONormalInputEncoding.Imported, false, sourceMip);
                candidate = _resampler.Resample(candidateTexture, island.UvBounds, size, point, false, false,
                    key.Kind, normalEncoding, false, candidateMip);
                referencePixels = _resampler.Readback(reference, Allocator.TempJob);
                candidatePixels = _resampler.Readback(candidate, Allocator.TempJob);
                mask = IslandMaskRasterizer.Rasterize(group, island, size, Allocator.TempJob);
                var covered = false;
                for (var i = 0; i < mask.Length; i++)
                    if (mask[i] != 0) { covered = true; break; }
                if (!covered) return false;
                foreach (var binding in bindings)
                {
                    var metrics = QualityMetricEvaluator.EvaluateForBinding(referencePixels, candidatePixels, mask,
                        size.x, size.y, binding);
                    if (!metrics.Passes(_settings.EffectiveQuality, binding)) return false;
                }
                return true;
            }
            finally
            {
                if (referencePixels.IsCreated) referencePixels.Dispose();
                if (candidatePixels.IsCreated) candidatePixels.Dispose();
                if (mask.IsCreated) mask.Dispose();
                GpuLinearResampler.Release(reference); GpuLinearResampler.Release(candidate);
            }
        }

        private Texture2D ReadRgba(RenderTexture source, Texture2D template, TextureTypeKey key, string name,
            ATONormalInputEncoding normalEncoding, int sourceMipReduction)
        {
            var mipmaps = TextureFormatResolver.ClassSettings(key.Kind, _settings).mipmapsAndStreaming;
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, mipmaps, !key.Srgb);
                texture.name = SafeName(name);
                texture.filterMode = template.filterMode;
                texture.wrapModeU = template.wrapModeU;
                texture.wrapModeV = template.wrapModeV;
                texture.wrapModeW = template.wrapModeW;
                texture.anisoLevel = template.anisoLevel;
                texture.mipMapBias = template.mipMapBias;
                if (!mipmaps)
                {
                    GpuLinearResampler.CopyToRgba32(source, texture, 0, key.Srgb);
                    texture.Apply(false, false);
                }
                else
                {
                    if (template.mipmapCount > 1)
                    {
                        if (sourceMipReduction <= 0 ||
                            texture.mipmapCount != template.mipmapCount - sourceMipReduction)
                            throw new InvalidOperationException(
                                "whole-texture mip preservation requires an exact source mip offset");
                        for (var mip = 0; mip < texture.mipmapCount; mip++)
                        {
                            RenderTexture level = null;
                            try
                            {
                                level = mip == 0 ? source : _resampler.Resample(template,
                                    new Rect(0f, 0f, 1f, 1f),
                                    new Vector2Int(Mathf.Max(1, texture.width >> mip),
                                        Mathf.Max(1, texture.height >> mip)),
                                    true, false, true, key.Kind, ATONormalInputEncoding.Imported, false,
                                    sourceMipReduction + mip);
                                CopyRgba32Level(level, texture, mip, key.Srgb);
                            }
                            finally { if (mip != 0) GpuLinearResampler.Release(level); }
                        }
                        texture.Apply(false, false);
                    }
                    else
                    {
                        // There is no source chain to copy. Generate the requested chain from the verified base level;
                        // the final gate below compares every generated LOD back to a source-base reconstruction.
                        GpuLinearResampler.CopyToRgba32(source, texture, 0, key.Srgb);
                        if (key.Kind == ATOTextureKind.ColorAlpha)
                        {
                            texture.Apply(false, false);
                            TextureFormatResolver.BuildPremultipliedAlphaMipChain(texture, key.Srgb);
                        }
                        else texture.Apply(true, false);
                    }
                }
                if (key.Kind == ATOTextureKind.Normal)
                    TextureFormatResolver.EncodeNormalMipChain(texture, normalEncoding);
                return texture;
            }
            catch
            {
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        private static void CopyRgba32Level(RenderTexture source, Texture2D destination, int mip, bool srgb)
        {
            GpuLinearResampler.CopyToRgba32(source, destination, mip, srgb);
        }

        private static void SetStreaming(Texture2D texture, bool enabled)
        {
            using (var serialized = new SerializedObject(texture))
            {
                var streaming = serialized.FindProperty("m_StreamingMipmaps");
                var priority = serialized.FindProperty("m_StreamingMipmapsPriority");
                if (streaming != null) streaming.boolValue = enabled;
                if (priority != null) priority.intValue = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static string SafeName(string value) => string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character == '_' ? character : '_'));

        internal static void DestroyTransientTextures(IEnumerable<Texture2D> textures)
        {
            foreach (var texture in (textures ?? Enumerable.Empty<Texture2D>()).Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(texture)) UnityEngine.Object.DestroyImmediate(texture);
        }

        public void Dispose() => _resampler.Dispose();
    }

    internal sealed class WholeTextureRewriter
    {
        private readonly IAssetSaver _assetSaver;
        private readonly AnimationIndex _animationIndex;
        private readonly bool _deduplicateMaterials;
        public WholeTextureRewriter(IAssetSaver assetSaver, AnimationIndex animationIndex, bool deduplicateMaterials)
        { _assetSaver = assetSaver; _animationIndex = animationIndex; _deduplicateMaterials = deduplicateMaterials; }

        private readonly struct CloneKey : IEquatable<CloneKey>
        {
            public readonly MaterialSlotRecord Slot; public readonly Material Material;
            public CloneKey(MaterialSlotRecord slot, Material material) { Slot = slot; Material = material; }
            public bool Equals(CloneKey other) => ReferenceEquals(Slot, other.Slot) && Material == other.Material;
            public override bool Equals(object obj) => obj is CloneKey other && Equals(other);
            public override int GetHashCode() => (Slot.GetHashCode() * 397) ^ (Material == null ? 0 : Material.GetHashCode());
        }

        private sealed class RendererChange { public Renderer Renderer; public Material[] Before, After; }
        private sealed class CurveChange
        {
            public VirtualClip Clip; public EditorCurveBinding Binding;
            public ObjectReferenceKeyframe[] Before, After;
            public void Apply() => Clip.SetObjectCurve(Binding, After);
            public bool Rollback() => TryRollback("whole-texture object curve",
                () => Clip.SetObjectCurve(Binding, Before));
        }

        public IATOCommitTransaction Apply(AvatarAnalysis analysis,
            IReadOnlyDictionary<TextureBindingRecord, Texture2D> replacements)
        {
            return Apply(analysis, replacements, replacements.Values);
        }

        internal IATOCommitTransaction Apply(AvatarAnalysis analysis,
            IReadOnlyDictionary<TextureBindingRecord, Texture2D> replacements,
            IEnumerable<Texture2D> generatedTextures)
        {
            if (replacements.Count == 0) return null;
            Dictionary<CloneKey, Material> clones = null;
            WholeCommitTransaction transaction = null;
            try
            {
                clones = BuildClones(analysis, replacements);
                Deduplicate(clones);
                var curves = BuildCurves(analysis, replacements, clones);
                var renderers = BuildRenderers(analysis, clones);
                PersistCommitAssets(replacements.Values, clones.Values);
                transaction = new WholeCommitTransaction(curves, renderers,
                    clones.Values.Distinct().ToArray(),
                    (generatedTextures ?? Enumerable.Empty<Texture2D>()).Distinct().ToArray());
                transaction.Apply();
                return transaction;
            }
            catch (Exception exception)
            {
                if (transaction == null) DestroyTransient(clones == null ? null : clones.Values);
                else if (!transaction.ApplyRollbackRestored)
                    throw new ATORollbackIncompleteException(
                        "ATO whole-texture commit failed and at least one Avatar reference could not be restored; generated assets were retained.",
                        exception);
                throw;
            }
        }

        private static Dictionary<CloneKey, Material> BuildClones(AvatarAnalysis analysis,
            IReadOnlyDictionary<TextureBindingRecord, Texture2D> replacements)
        {
            var result = new Dictionary<CloneKey, Material>();
            try
            {
                foreach (var slot in analysis.Renderers.SelectMany(value => value.Slots))
                foreach (var material in slot.Materials.Where(value => value != null))
                {
                    var initial = slot.Bindings.Where(value => value.Material == material && value.IsInitialValue &&
                        replacements.ContainsKey(value)).ToArray();
                    if (initial.Length == 0 && !slot.Bindings.Any(value => value.Material == material &&
                            value.IsAnimatedValue && replacements.ContainsKey(value))) continue;
                    var clone = UnityEngine.Object.Instantiate(material);
                    try
                    {
                        clone.name = "ATO_" + material.name + "_Whole";
                        foreach (var binding in initial) clone.SetTexture(binding.PropertyName, replacements[binding]);
                        result.Add(new CloneKey(slot, material), clone); clone = null;
                    }
                    finally { if (clone != null) UnityEngine.Object.DestroyImmediate(clone); }
                }
                return result;
            }
            catch
            {
                DestroyTransient(result.Values); throw;
            }
        }

        private void Deduplicate(Dictionary<CloneKey, Material> clones)
        {
            if (!_deduplicateMaterials) return;
            var canonical = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (var key in clones.Keys.ToArray())
            {
                var material = clones[key];
                var identity = MaterialAnimationRewriter.MaterialIdentity(material);
                if (canonical.TryGetValue(identity, out var existing))
                { clones[key] = existing; UnityEngine.Object.DestroyImmediate(material); continue; }
                canonical.Add(identity, material);
            }
        }

        private void PersistCommitAssets(IEnumerable<Texture2D> textures, IEnumerable<Material> materials)
        {
            // Every curve and Renderer change is known before the first SaveAsset call. IAssetSaver itself has no
            // deletion/rollback API, so a later persistence exception remains fatal but cannot expose a partial Avatar.
            foreach (var texture in (textures ?? Enumerable.Empty<Texture2D>()).Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(texture)) _assetSaver.SaveAsset(texture);
            foreach (var material in (materials ?? Enumerable.Empty<Material>()).Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(material)) _assetSaver.SaveAsset(material);
        }

        private List<CurveChange> BuildCurves(AvatarAnalysis analysis,
            IReadOnlyDictionary<TextureBindingRecord, Texture2D> replacements, IReadOnlyDictionary<CloneKey, Material> clones)
        {
            var changes = new List<CurveChange>();
            var clips = analysis.Renderers.SelectMany(value => _animationIndex.GetClipsForObjectPath(value.Path)).Distinct();
            foreach (var clip in clips)
            foreach (var binding in clip.GetObjectCurveBindings().ToArray())
            {
                var renderer = AnimationAnalyzer.ResolveRendererRecord(analysis.Renderers, binding, out var ambiguous);
                // Whole mode does not alter UVs; an ambiguous target can safely retain its original curve.
                if (ambiguous || renderer == null ||
                    !TryParse(binding.propertyName, out var slotIndex, out var property) || slotIndex < 0) continue;
                var slot = renderer.Slots.FirstOrDefault(value => value.Slot == slotIndex); if (slot == null) continue;
                var before = clip.GetObjectCurve(binding); if (before == null) continue;
                var after = (ObjectReferenceKeyframe[])before.Clone(); var changed = false;
                for (var frame = 0; frame < after.Length; frame++)
                {
                    if (property == null && after[frame].value is Material material &&
                        clones.TryGetValue(new CloneKey(slot, material), out var clone))
                    { after[frame].value = clone; changed = true; continue; }
                    if (property == null || !(after[frame].value is Texture source)) continue;
                    var resolution = AnimatedTextureResolver.Resolve(slot, property, source, replacements,
                        out var replacementTexture);
                    // Whole mode does not change UVs. An incomplete or ambiguous keyframe can therefore safely keep
                    // its original texture instead of guessing or rewriting only some animated material states.
                    if (resolution != AnimatedTextureResolution.Resolved) continue;
                    after[frame].value = replacementTexture; changed = true;
                }
                if (changed)
                {
                    MaterialAnimationRewriter.EnsureMutableClipForRewrite(clip);
                    changes.Add(new CurveChange { Clip = clip, Binding = binding, Before = before, After = after });
                }
            }
            return changes;
        }

        private static List<RendererChange> BuildRenderers(AvatarAnalysis analysis, IReadOnlyDictionary<CloneKey, Material> clones)
        {
            var changes = new List<RendererChange>();
            foreach (var renderer in analysis.Renderers)
            {
                var before = renderer.Renderer.sharedMaterials; var after = (Material[])before.Clone();
                foreach (var slot in renderer.Slots)
                    if (slot.Slot < after.Length && after[slot.Slot] != null &&
                        clones.TryGetValue(new CloneKey(slot, after[slot.Slot]), out var clone)) after[slot.Slot] = clone;
                if (!before.SequenceEqual(after)) changes.Add(new RendererChange { Renderer = renderer.Renderer, Before = before, After = after });
            }
            return changes;
        }

        private sealed class WholeCommitTransaction : IATOCommitTransaction
        {
            private readonly IReadOnlyList<CurveChange> _curves;
            private readonly IReadOnlyList<RendererChange> _renderers;
            private readonly IReadOnlyList<Material> _materials;
            private readonly IReadOnlyList<Texture2D> _textures;
            private bool _applied;
            private bool _finished;
            internal bool ApplyRollbackRestored { get; private set; }

            public WholeCommitTransaction(IReadOnlyList<CurveChange> curves,
                IReadOnlyList<RendererChange> renderers, IReadOnlyList<Material> materials,
                IReadOnlyList<Texture2D> textures)
            {
                _curves = curves ?? Array.Empty<CurveChange>();
                _renderers = renderers ?? Array.Empty<RendererChange>();
                _materials = materials ?? Array.Empty<Material>();
                _textures = textures ?? Array.Empty<Texture2D>();
            }

            public void Apply()
            {
                if (_applied || _finished) throw new InvalidOperationException("ATO whole-texture transaction cannot be applied twice.");
                var curveCount = 0; var rendererCount = 0;
                var currentCurve = false; var currentRenderer = false;
                try
                {
                    for (; curveCount < _curves.Count; curveCount++)
                    {
                        ATOProgress.Checkpoint("Committing whole-texture animation curves");
                        currentCurve = true; _curves[curveCount].Apply(); currentCurve = false;
                    }
                    for (; rendererCount < _renderers.Count; rendererCount++)
                    {
                        ATOProgress.Checkpoint("Committing whole-texture materials");
                        currentRenderer = true;
                        _renderers[rendererCount].Renderer.sharedMaterials = _renderers[rendererCount].After;
                        currentRenderer = false;
                    }
                    _applied = true;
                }
                catch
                {
                    var restored = true;
                    if (currentRenderer) restored &= RollbackRenderer(_renderers[rendererCount], "current whole-texture renderer");
                    if (currentCurve) restored &= _curves[curveCount].Rollback();
                    restored &= RollbackCompleted(rendererCount, curveCount);
                    ApplyRollbackRestored = restored;
                    _finished = true;
                    if (restored) DestroyGeneratedObjects();
                    throw;
                }
            }

            public void Complete()
            {
                if (_finished) return;
                // A transaction is only returned after Apply succeeds. Keep this terminal edge non-throwing so the
                // pipeline may remove its build-only marker immediately before completion without losing rollback.
                // 事务仅在 Apply 成功后返回；此终止边界不得抛异常，组件删除后不会再出现需回滚的失败点。
                Debug.Assert(_applied, "ATO whole-texture transaction completed before Apply.");
                _finished = true;
            }

            public bool Rollback()
            {
                if (_finished) return ApplyRollbackRestored;
                if (!_applied) { ApplyRollbackRestored = true; _finished = true; return true; }
                var restored = RollbackCompleted(_renderers.Count, _curves.Count);
                ApplyRollbackRestored = restored;
                _finished = true;
                if (restored) DestroyGeneratedObjects();
                return restored;
            }

            public void Dispose()
            {
                if (!_finished) Rollback();
            }

            private bool RollbackCompleted(int rendererCount, int curveCount)
            {
                var restored = true;
                for (var index = rendererCount - 1; index >= 0; index--)
                    restored &= RollbackRenderer(_renderers[index], "whole-texture renderer");
                for (var index = curveCount - 1; index >= 0; index--)
                    restored &= _curves[index].Rollback();
                return restored;
            }

            private static bool RollbackRenderer(RendererChange change, string operation) =>
                TryRollback(operation, () => change.Renderer.sharedMaterials = change.Before);

            private void DestroyGeneratedObjects()
            {
                DestroyTransient(_materials);
                WholeTextureOptimizer.DestroyTransientTextures(_textures);
            }
        }

        private static bool TryRollback(string operation, Action rollback)
        {
            try { rollback(); return true; }
            catch (Exception exception)
            {
                Debug.LogError("[ATO] Transaction rollback failed for " + operation + ": " + exception);
                return false;
            }
        }

        private static void DestroyTransient(IEnumerable<Material> materials)
        {
            if (materials == null) return;
            foreach (var material in materials.Where(value => value != null).Distinct())
                if (!EditorUtility.IsPersistent(material)) UnityEngine.Object.DestroyImmediate(material);
        }

        private static bool TryParse(string name, out int slot, out string property)
        {
            const string array = "m_Materials.Array.data[";
            if (name.StartsWith(array, StringComparison.Ordinal))
            {
                var close = name.IndexOf(']', array.Length);
                if (close > array.Length && int.TryParse(name.Substring(array.Length, close - array.Length), out slot))
                { property = null; return close == name.Length - 1; }
            }
            return AnimationAnalyzer.TryGetMaterialProperty(name, out slot, out property);
        }
    }
}

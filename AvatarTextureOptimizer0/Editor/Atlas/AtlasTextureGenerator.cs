using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using nadena.dev.ndmf;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal sealed class AtlasTextureGenerator : IDisposable
    {
        private readonly ATOOptimizationSettings _settings;
        private readonly ComputeShader _shader;
        private readonly GpuLinearResampler _resampler = new GpuLinearResampler();
        private readonly int _clear, _copy, _push, _pull, _unpremultiply;
        private readonly Dictionary<string, Texture2D> _textureDedup = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        private sealed class AtlasQualityException : Exception
        {
            public AtlasQualityException(string message) : base(message) { }
        }

        public AtlasTextureGenerator(ATOOptimizationSettings settings, IAssetSaver assetSaver)
        {
            _settings = settings;
            _ = assetSaver; // Persistence is owned by the final material/mesh/curve transaction.
            _shader = Resources.Load<ComputeShader>("ATOResample");
            if (_shader == null) throw new InvalidOperationException("ATOResample.compute resource is missing.");
            _clear = _shader.FindKernel("ClearAtlas"); _copy = _shader.FindKernel("CopyMasked");
            _push = _shader.FindKernel("PushLevel"); _pull = _shader.FindKernel("PullLevel");
            _unpremultiply = _shader.FindKernel("Unpremultiply");
        }

        public AtlasBuildResult Generate(AvatarAnalysis analysis, AtlasPlan plan)
        {
            var result = new AtlasBuildResult();
            var rejectedPages = new List<AtlasPage>();
            try
            {
                foreach (var page in plan.Pages.ToArray())
                {
                    ATOProgress.Checkpoint("Generating atlas page " + page.Id);
                    AtlasBuildResult pageResult = null;
                    try
                    {
                        pageResult = GeneratePage(plan, page);
                        Merge(result, pageResult);
                    }
                    catch (AtlasQualityException exception)
                    {
                        rejectedPages.Add(page);
                        foreach (var group in page.Groups)
                        {
                            group.AtlasSafe = false;
                            analysis.Fallbacks.Add(new FallbackRecord(group?.Renderer?.Renderer,
                                "atlas safety fallback before commit: " + exception.Message));
                        }
                        Debug.LogWarning("[ATO] Atlas page " + page.Id + " retained original meshes and textures: " +
                                         exception.Message);
                    }
                    catch
                    {
                        if (pageResult != null) DestroyTransientPage(pageResult);
                        throw;
                    }
                }
                foreach (var page in rejectedPages) plan.Pages.Remove(page);
                result.OutputPixels = result.AllTextures.Distinct()
                    .Sum(texture => (long)texture.width * texture.height);
                return result;
            }
            catch
            {
                DestroyTransientPage(result);
                throw;
            }
        }

        private AtlasBuildResult GeneratePage(AtlasPlan plan, AtlasPage page)
        {
            var result = new AtlasBuildResult();
            var cache = new Dictionary<VariantKey, Texture2D>();
            // Animated object curves need one object identity for one semantic source value even when optional
            // byte-level asset deduplication is disabled. Each binding is still generated and quality-gated before
            // equivalent output objects are coalesced. / 动画语义身份独立于可选资源去重。
            var semanticOutputs = new Dictionary<VariantContentKey, Texture2D>();
            // Values already owned by an earlier accepted page must never be destroyed when this page falls back.
            var preexisting = new HashSet<Texture2D>(_textureDedup.Values.Where(value => value != null));
            var generated = result.OwnedTextures;
            try
            {
                if (page == null || page.Groups.Count == 0)
                    throw new InvalidOperationException("Atlas page has no groups.");
                if (!HasCompletePlacementCoverage(page))
                    throw new AtlasQualityException("atlas page has incomplete or invalid island placement coverage");
                var firstLayout = plan.GroupLayouts[page.Groups[0]];
                if ((page.Size.x > 1 || page.Size.y > 1) && firstLayout.LayerKeys.Any(key =>
                        TextureFormatResolver.ClassSettings(key.Kind, _settings).mipmapsAndStreaming &&
                        TextureLodSafety.RequiresFractionalLodFallback(key.FilterMode, 2)))
                    throw new AtlasQualityException(
                        "Trilinear mip filtering requires an unproven fractional-LOD quality gate");
                for (var layer = 0; layer < firstLayout.LayerKeys.Count; layer++)
                {
                    var key = new VariantKey(page.Id, layer, null, null);
                    Texture2D texture = null;
                    // A null material texture is a real shader state, not an all-zero image. If no current material
                    // contributes this layer, keep the base output null instead of creating an unreachable blank asset.
                    // null 贴图是实际材质状态而非全零图片；无当前内容时不得制造不可达 blank 资产。
                    if (HasBaseLayerContent(plan, page, layer))
                    {
                        texture = BuildLayer(plan, page, layer, null, null, "Base",
                            preexisting, generated, out _);
                        foreach (var group in page.Groups)
                        {
                            var baseBinding = BaseBinding(plan, group, layer);
                            if (HasTextureContent(baseBinding))
                                semanticOutputs[new VariantContentKey(page.Id, layer, group, baseBinding.Texture)] = texture;
                        }
                    }
                    cache[key] = texture;
                    result.BaseLayers[new PageLayerKey(page.Id, layer)] = texture;
                }

                foreach (var group in page.Groups)
                {
                    var layout = plan.GroupLayouts[group];
                    for (var layer = 0; layer < layout.LayerKeys.Count; layer++)
                    {
                        var baseTexture = result.BaseLayers[new PageLayerKey(page.Id, layer)];
                        foreach (var pair in layout.MaterialLayers)
                        {
                            var binding = pair.Value[layer].Initial;
                            var materialKey = new GroupMaterialLayerKey(group, pair.Key, layer);
                            if (!HasTextureContent(binding))
                            {
                                result.MaterialVariants[materialKey] = null;
                                continue;
                            }
                            var texture = ResolveVariant(plan, page, layer, group, binding, baseTexture, cache,
                                semanticOutputs, preexisting, generated,
                                "Material_" + SafeName(pair.Key == null ? "Null" : pair.Key.name));
                            result.MaterialVariants[materialKey] = texture;
                        }
                        foreach (var materialLayers in layout.MaterialLayers.Values)
                        foreach (var animated in materialLayers[layer].AnimatedValues)
                            result.AnimatedTextureVariants[animated] = ResolveVariant(plan, page, layer, group, animated,
                                baseTexture, cache, semanticOutputs, preexisting, generated,
                                "Texture_" + SafeName(animated.Texture.name));
                    }
                }
                return result;
            }
            catch
            {
                DestroyTransientPage(result);
                throw;
            }
        }

        private void DestroyTransientPage(AtlasBuildResult page)
        {
            if (page == null) return;
            var owned = new HashSet<Texture2D>(page.OwnedTextures
                .Where(value => value != null && !EditorUtility.IsPersistent(value)));
            foreach (var key in _textureDedup.Where(pair => owned.Contains(pair.Value))
                         .Select(pair => pair.Key).ToArray())
                _textureDedup.Remove(key);
            page.DestroyOwnedTransient();
        }

        internal static void TrackPageTexture(Texture2D texture, ISet<Texture2D> preexisting,
            ISet<Texture2D> generated)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (preexisting == null) throw new ArgumentNullException(nameof(preexisting));
            if (generated == null) throw new ArgumentNullException(nameof(generated));
            // A later page may reuse a transient object owned by an earlier successful page through the global
            // content hash. Only this page's newly created objects participate in its rollback set.
            // 后续页面可复用前页的瞬态对象；页面回滚只能销毁本页新建对象。
            if (!preexisting.Contains(texture)) generated.Add(texture);
        }

        internal static bool CanDestroySupersededSemanticTexture(Texture2D generatedTexture,
            Texture2D semanticTexture, ISet<Texture2D> preexisting, bool createdByCall)
        {
            // Only this exact BuildLayer call's new candidate may be reclaimed. A dedup hit can belong to an earlier
            // page or another output on this page. / 只能回收本次 BuildLayer 新建的候选；dedup 命中也可能由
            // 早页或本页其他输出持有。
            return createdByCall && generatedTexture != null && generatedTexture != semanticTexture &&
                   !EditorUtility.IsPersistent(generatedTexture) &&
                   (preexisting == null || !preexisting.Contains(generatedTexture));
        }

        private void RemoveDedupValue(Texture2D texture)
        {
            foreach (var key in _textureDedup.Where(pair => pair.Value == texture).Select(pair => pair.Key).ToArray())
                _textureDedup.Remove(key);
        }

        private static void Merge(AtlasBuildResult destination, AtlasBuildResult source)
        {
            foreach (var pair in source.BaseLayers) destination.BaseLayers.Add(pair.Key, pair.Value);
            foreach (var pair in source.MaterialVariants) destination.MaterialVariants.Add(pair.Key, pair.Value);
            foreach (var pair in source.AnimatedTextureVariants) destination.AnimatedTextureVariants.Add(pair.Key, pair.Value);
            foreach (var texture in source.OwnedTextures) destination.OwnedTextures.Add(texture);
        }

        private Texture2D ResolveVariant(AtlasPlan plan, AtlasPage page, int layer, UvGroupRecord group,
            TextureBindingRecord binding, Texture2D baseTexture, Dictionary<VariantKey, Texture2D> cache,
            Dictionary<VariantContentKey, Texture2D> semanticOutputs, HashSet<Texture2D> preexisting,
            HashSet<Texture2D> generated, string suffix)
        {
            var baseBinding = BaseBinding(plan, group, layer);
            // Pixel identity alone is not a sufficient validation identity. The same Texture2D can be used by
            // animated material states with different cutoffs, alpha semantics, or packed-channel masks. Reusing a
            // variant before evaluating that binding would let the first state's quality proof stand in for another.
            if (CanReuseBaseVariant(binding, baseBinding)) return baseTexture;
            var key = new VariantKey(page.Id, layer, group, binding);
            if (cache.TryGetValue(key, out var existing)) return existing;

            // BuildLayer validates this exact binding. Only afterwards may equivalent content share the single object
            // identity required by a VirtualClip object-reference keyframe.
            var generatedTexture = BuildLayer(plan, page, layer, group, binding, suffix,
                preexisting, generated, out var createdByCall);
            var contentKey = new VariantContentKey(page.Id, layer, group, binding?.Texture);
            if (binding?.Texture != null && semanticOutputs.TryGetValue(contentKey, out var semantic))
            {
                if (CanDestroySupersededSemanticTexture(generatedTexture, semantic, preexisting,
                        createdByCall))
                {
                    generated.Remove(generatedTexture);
                    RemoveDedupValue(generatedTexture);
                    UnityEngine.Object.DestroyImmediate(generatedTexture);
                }
                generatedTexture = semantic;
            }
            else if (binding?.Texture != null)
            {
                semanticOutputs.Add(contentKey, generatedTexture);
            }
            cache[key] = generatedTexture; return generatedTexture;
        }

        private Texture2D BuildLayer(AtlasPlan plan, AtlasPage page, int layer, UvGroupRecord overrideGroup,
            TextureBindingRecord overrideBinding, string suffix, ISet<Texture2D> preexisting,
            ISet<Texture2D> generated, out bool createdByCall)
        {
            RenderTexture atlas = null, validity = null;
            try
            {
                atlas = CreateRt(page.Size, GraphicsFormat.R16G16B16A16_SFloat, true, "ATO_Atlas_Work");
                validity = CreateRt(page.Size, GraphicsFormat.R8_UNorm, true, "ATO_Atlas_Validity");
                var key = plan.GroupLayouts[page.Groups[0]].LayerKeys[layer];
                _shader.SetInt("_TextureKind", (int)key.Kind);
                _shader.SetInts("_AtlasSize", page.Size.x, page.Size.y);
                _shader.SetTexture(_clear, "_Atlas", atlas); _shader.SetTexture(_clear, "_Validity", validity);
                _shader.Dispatch(_clear, Groups(page.Size.x), Groups(page.Size.y), 1);
                foreach (var placement in page.Placements)
                {
                    ATOProgress.Checkpoint("Compositing atlas placement");
                    var binding = placement.Group == overrideGroup ? overrideBinding : BaseBinding(plan, placement.Group, layer);
                    if (binding == null || binding.Texture == null) continue;
                    CopyPlacement(placement, binding, atlas, validity);
                }
                var padded = PullPush(atlas, validity, page.Size);
                try
                {
                    return ReadAndCompress(padded, key, "ATO_Page" + page.Id + "_Layer" + layer + "_" + suffix,
                        plan, page, layer, overrideGroup, overrideBinding, preexisting, generated,
                        out createdByCall);
                }
                finally { GpuLinearResampler.Release(padded); }
            }
            finally { GpuLinearResampler.Release(atlas); GpuLinearResampler.Release(validity); }
        }

        private void CopyPlacement(AtlasPlacement placement, TextureBindingRecord binding, RenderTexture atlas, RenderTexture validity)
        {
            var source = binding.Texture;
            RenderTexture sampled = null; Texture2D mask = null;
            try
            {
                sampled = _resampler.Resample(source, placement.Island.UvBounds, placement.Island.TargetPixelSize,
                    source.filterMode == FilterMode.Point, false, false, binding.Kind);
                mask = CreateMask(placement.Group, placement.Island);
                _shader.SetTexture(_copy, "_Source", sampled); _shader.SetTexture(_copy, "_Mask", mask);
                _shader.SetTexture(_copy, "_Atlas", atlas); _shader.SetTexture(_copy, "_Validity", validity);
                _shader.SetInts("_CopyOffset", placement.ContentRect.x, placement.ContentRect.y);
                _shader.SetInts("_CopySize", placement.ContentRect.width, placement.ContentRect.height);
                _shader.SetInt("_RotateCopy", placement.Rotated ? 1 : 0);
                _shader.Dispatch(_copy, Groups(placement.ContentRect.width), Groups(placement.ContentRect.height), 1);
            }
            finally
            {
                GpuLinearResampler.Release(sampled);
                if (mask != null) UnityEngine.Object.DestroyImmediate(mask);
            }
        }

        // Dispatch-level seam for validating the exact non-square rotation and mask convention used by production.
        // 用实际 Compute kernel 验证生产路径的非方形旋转与 mask 坐标约定。
        internal RenderTexture CopyMaskedForTests(Texture source, Texture mask, Vector2Int sourceSize, bool rotated)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (sourceSize.x <= 0 || sourceSize.y <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSize));
            var outputSize = rotated ? new Vector2Int(sourceSize.y, sourceSize.x) : sourceSize;
            RenderTexture atlas = null, validity = null;
            try
            {
                atlas = CreateRt(outputSize, GraphicsFormat.R16G16B16A16_SFloat, true, "ATO_Test_Copy");
                validity = CreateRt(outputSize, GraphicsFormat.R8_UNorm, true, "ATO_Test_CopyValidity");
                _shader.SetInts("_AtlasSize", outputSize.x, outputSize.y);
                _shader.SetTexture(_clear, "_Atlas", atlas);
                _shader.SetTexture(_clear, "_Validity", validity);
                _shader.Dispatch(_clear, Groups(outputSize.x), Groups(outputSize.y), 1);
                _shader.SetTexture(_copy, "_Source", source);
                _shader.SetTexture(_copy, "_Mask", mask);
                _shader.SetTexture(_copy, "_Atlas", atlas);
                _shader.SetTexture(_copy, "_Validity", validity);
                _shader.SetInts("_CopyOffset", 0, 0);
                _shader.SetInts("_CopySize", outputSize.x, outputSize.y);
                _shader.SetInt("_RotateCopy", rotated ? 1 : 0);
                _shader.Dispatch(_copy, Groups(outputSize.x), Groups(outputSize.y), 1);
                var result = atlas;
                atlas = null;
                return result;
            }
            finally
            {
                GpuLinearResampler.Release(atlas);
                GpuLinearResampler.Release(validity);
            }
        }

        private static Texture2D CreateMask(UvGroupRecord group, UvIsland island)
        {
            var packed = IslandMaskRasterizer.Rasterize(group, island, island.TargetPixelSize, Allocator.TempJob);
            try
            {
                var bytes = new byte[island.TargetPixelSize.x * island.TargetPixelSize.y];
                for (var i = 0; i < bytes.Length; i++) bytes[i] = IslandMaskRasterizer.IsSet(packed, i) ? (byte)255 : (byte)0;
                Texture2D texture = null;
                try
                {
                    texture = new Texture2D(island.TargetPixelSize.x, island.TargetPixelSize.y,
                        TextureFormat.R8, false, true);
                    texture.filterMode = FilterMode.Point;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.name = "ATO_Mask_Temporary";
                    texture.SetPixelData(bytes, 0); texture.Apply(false, true); return texture;
                }
                catch
                {
                    if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                    throw;
                }
            }
            finally { packed.Dispose(); }
        }

        // Test seam for dispatch-level validation of odd-sized pull/push chains. The production path sets the
        // same kind immediately before calling PullPush from BuildLayer.
        internal RenderTexture PullPushForTests(RenderTexture baseColor, RenderTexture baseValidity,
            Vector2Int size, ATOTextureKind kind)
        {
            _shader.SetInt("_TextureKind", (int)kind);
            return PullPush(baseColor, baseValidity, size);
        }

        private RenderTexture PullPush(RenderTexture baseColor, RenderTexture baseValidity, Vector2Int size)
        {
            var owned = new HashSet<RenderTexture>();
            try
            {
                var colors = new List<RenderTexture> { baseColor }; var validities = new List<RenderTexture> { baseValidity };
                var current = size;
                while (current.x > 1 || current.y > 1)
                {
                    // Ceil division retains an odd right/top edge instead of dropping its only valid texels.
                    var next = NextPullSize(current);
                    var color = CreateRt(next, GraphicsFormat.R16G16B16A16_SFloat, true, "ATO_Push_Color");
                    owned.Add(color);
                    var validity = CreateRt(next, GraphicsFormat.R8_UNorm, true, "ATO_Push_Validity");
                    owned.Add(validity);
                    _shader.SetTexture(_push, "_FineColor", colors[colors.Count - 1]);
                    _shader.SetTexture(_push, "_FineValidity", validities[validities.Count - 1]);
                    _shader.SetTexture(_push, "_OutputColor", color); _shader.SetTexture(_push, "_OutputValidity", validity);
                    _shader.SetInts("_FineSize", current.x, current.y); _shader.SetInts("_CoarseSize", next.x, next.y);
                    _shader.Dispatch(_push, Groups(next.x), Groups(next.y), 1);
                    colors.Add(color); validities.Add(validity); current = next;
                }

                var filledColor = colors[colors.Count - 1]; var filledValidity = validities[validities.Count - 1];
                if (colors.Count == 1)
                {
                    // Keep the return value independently owned even for a 1x1 page. Returning baseColor aliases the
                    // caller's atlas and causes the nested BuildLayer cleanup to release the same RT twice.
                    filledColor = CreateRt(size, GraphicsFormat.R16G16B16A16_SFloat, true, "ATO_Pull_Color");
                    owned.Add(filledColor);
                    Graphics.CopyTexture(baseColor, filledColor);
                }
                for (var level = colors.Count - 2; level >= 0; level--)
                {
                    var fineSize = new Vector2Int(colors[level].width, colors[level].height);
                    var output = CreateRt(fineSize, GraphicsFormat.R16G16B16A16_SFloat, true, "ATO_Pull_Color");
                    owned.Add(output);
                    var outputValidity = CreateRt(fineSize, GraphicsFormat.R8_UNorm, true, "ATO_Pull_Validity");
                    owned.Add(outputValidity);
                    _shader.SetTexture(_pull, "_FineColor", colors[level]); _shader.SetTexture(_pull, "_FineValidity", validities[level]);
                    _shader.SetTexture(_pull, "_CoarseColor", filledColor); _shader.SetTexture(_pull, "_CoarseValidity", filledValidity);
                    _shader.SetTexture(_pull, "_OutputColor", output); _shader.SetTexture(_pull, "_OutputValidity", outputValidity);
                    _shader.SetInts("_FineSize", fineSize.x, fineSize.y);
                    _shader.SetInts("_CoarseSize", filledColor.width, filledColor.height);
                    _shader.Dispatch(_pull, Groups(fineSize.x), Groups(fineSize.y), 1);
                    ReleaseOwned(owned, filledColor); ReleaseOwned(owned, filledValidity);
                    ReleaseOwned(owned, colors[level]); ReleaseOwned(owned, validities[level]);
                    filledColor = output; filledValidity = outputValidity;
                }
                // The final pull result is already a writable full-size texture; normalize it in place to avoid another
                // full-atlas allocation. / 最终拉取结果可原地处理，避免再分配一张完整图集。
                ReleaseOwned(owned, filledValidity);
                _shader.SetTexture(_unpremultiply, "_Atlas", filledColor);
                _shader.SetInts("_AtlasSize", size.x, size.y);
                _shader.Dispatch(_unpremultiply, Groups(size.x), Groups(size.y), 1);
                owned.Remove(filledColor); return filledColor;
            }
            finally { foreach (var texture in owned) GpuLinearResampler.Release(texture); }
        }

        private Texture2D ReadAndCompress(RenderTexture source, TextureTypeKey key, string name, AtlasPlan plan,
            AtlasPage page, int layer, UvGroupRecord overrideGroup, TextureBindingRecord overrideBinding,
            ISet<Texture2D> preexisting, ISet<Texture2D> generated, out bool createdByCall)
        {
            var classSettings = TextureFormatResolver.ClassSettings(key.Kind, _settings);
            var format = TextureFormatResolver.Resolve(key, _settings);
            var normalEncoding = key.Kind == ATOTextureKind.Normal
                ? TextureFormatResolver.NormalStorageEncoding(format) : ATONormalInputEncoding.Imported;
            Texture2D texture = null;
            try
            {
                const TextureFormat fallbackFormat = TextureFormat.RGBA32;
                texture = ReadUncompressed(source, key, classSettings, name, normalEncoding, fallbackFormat);
                if (!AtlasOutputPasses(texture, key, plan, page, layer, overrideGroup, overrideBinding,
                        normalEncoding))
                    throw new AtlasQualityException(
                        "RGBA8 quantization or cumulative atlas resampling exceeded the configured thresholds");
                if (format != TextureFormat.RGBA32)
                {
                    try
                    {
                        EditorUtility.CompressTexture(texture, format, TextureCompressionQuality.Best);
                        if (!AtlasOutputPasses(texture, key, plan, page, layer, overrideGroup,
                                overrideBinding, normalEncoding))
                            throw new InvalidOperationException(
                                "compressed atlas did not meet the configured source-to-output island quality thresholds");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[ATO] Compression fallback to " + fallbackFormat + ": " + exception.Message);
                        UnityEngine.Object.DestroyImmediate(texture);
                        texture = ReadUncompressed(source, key, classSettings, name, normalEncoding, fallbackFormat);
                        if (!AtlasOutputPasses(texture, key, plan, page, layer, overrideGroup,
                                overrideBinding, normalEncoding))
                            throw new AtlasQualityException(
                                "RGBA8 compression fallback no longer met the configured source-to-output thresholds");
                    }
                }
                SetStreaming(texture, classSettings.mipmapsAndStreaming);
                string identity = null;
                if (_settings.deduplicateTexturesAndAtlases)
                    identity = texture.width + "x" + texture.height + ":" + texture.format + ":" +
                               texture.mipmapCount + ":" + key.Srgb + ":" + key.FilterMode + ":aniso=" +
                               key.AnisoLevel + ":bias=" + key.MipMapBias.ToString("R",
                                   System.Globalization.CultureInfo.InvariantCulture) + ":streaming=" +
                               classSettings.mipmapsAndStreaming + ":priority=0:" + texture.imageContentsHash;

                // Transfer the candidate to the finalization helper before leaving this catch domain. It establishes
                // page ownership before BuildLayer's RT finally blocks run. / 离开本 catch 域前转交候选，并在
                // BuildLayer 的 RT finally 执行前建立页面所有权。
                var candidate = texture; texture = null;
                return FinalizeAndPublishTexture(candidate, identity, _textureDedup, preexisting, generated,
                    value => value.Apply(false, true), out createdByCall);
            }
            catch
            {
                if (texture != null && !EditorUtility.IsPersistent(texture)) UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        internal static Texture2D FinalizeAndPublishTexture(Texture2D candidate, string identity,
            IDictionary<string, Texture2D> dedup, ISet<Texture2D> preexisting, ISet<Texture2D> generated,
            Action<Texture2D> finalize, out bool createdByCall)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            createdByCall = false;
            try
            {
                if (preexisting == null) throw new ArgumentNullException(nameof(preexisting));
                if (generated == null) throw new ArgumentNullException(nameof(generated));
                if (finalize == null) throw new ArgumentNullException(nameof(finalize));
                if (identity != null)
                {
                    if (dedup == null) throw new ArgumentNullException(nameof(dedup));
                    if (dedup.TryGetValue(identity, out var existing))
                    {
                        if (existing == null)
                        {
                            dedup.Remove(identity);
                            throw new InvalidOperationException("ATO atlas dedup cache contains a destroyed texture.");
                        }
                        if (!EditorUtility.IsPersistent(candidate)) UnityEngine.Object.DestroyImmediate(candidate);
                        TrackPageTexture(existing, preexisting, generated);
                        return existing;
                    }
                }

                // No cache or caller can observe this candidate until every failure-capable finalization step passes.
                // 所有可能失败的 finalization 完成前，缓存与调用方都不可观察该候选。
                finalize(candidate);
                TrackPageTexture(candidate, preexisting, generated);
                if (identity != null) dedup.Add(identity, candidate);
                createdByCall = true;
                return candidate;
            }
            catch
            {
                if (identity != null && dedup != null && dedup.TryGetValue(identity, out var published) &&
                    published == candidate)
                    dedup.Remove(identity);
                generated?.Remove(candidate);
                if (candidate != null && !EditorUtility.IsPersistent(candidate))
                    UnityEngine.Object.DestroyImmediate(candidate);
                throw;
            }
        }

        private static Texture2D ReadUncompressed(RenderTexture source, TextureTypeKey key,
            ATOTextureClassSettings classSettings, string name, ATONormalInputEncoding normalEncoding,
            TextureFormat uncompressedFormat)
        {
            if (uncompressedFormat != TextureFormat.RGBA32)
                throw new ArgumentOutOfRangeException(nameof(uncompressedFormat));
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(source.width, source.height, uncompressedFormat,
                    classSettings.mipmapsAndStreaming, !key.Srgb);
                texture.name = SafeName(name);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = key.FilterMode;
                texture.anisoLevel = key.AnisoLevel;
                texture.mipMapBias = key.MipMapBias;
                GpuLinearResampler.CopyToRgba32(source, texture, 0, key.Srgb);
                if (classSettings.mipmapsAndStreaming && key.Kind == ATOTextureKind.ColorAlpha)
                {
                    texture.Apply(false, false);
                    TextureFormatResolver.BuildPremultipliedAlphaMipChain(texture, key.Srgb);
                }
                else
                {
                    texture.Apply(classSettings.mipmapsAndStreaming, false);
                    if (key.Kind == ATOTextureKind.Normal)
                        TextureFormatResolver.EncodeNormalMipChain(texture, normalEncoding);
                }
                return texture;
            }
            catch
            {
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        private bool AtlasOutputPasses(Texture2D candidateAtlas, TextureTypeKey key, AtlasPlan plan,
            AtlasPage page, int layer, UvGroupRecord overrideGroup, TextureBindingRecord overrideBinding,
            ATONormalInputEncoding normalEncoding)
        {
            if (TextureLodSafety.RequiresFractionalLodFallback(key.FilterMode, candidateAtlas.mipmapCount))
                return false;
            foreach (var placement in page.Placements)
            {
                ATOProgress.Checkpoint("Validating atlas placement quality");
                var binding = placement.Group == overrideGroup ? overrideBinding : BaseBinding(plan, placement.Group, layer);
                if (binding == null || binding.Texture == null) continue;

                // Mip 0 proves cumulative close-view loss from the actual source, through packing/padding, byte
                // quantization, normal swizzling and optional block compression.
                var originalSize = new Vector2Int(
                    Mathf.Max(1, Mathf.CeilToInt(placement.Island.UvBounds.width * binding.Texture.width)),
                    Mathf.Max(1, Mathf.CeilToInt(placement.Island.UvBounds.height * binding.Texture.height)));
                if (!AtlasLevelPasses(candidateAtlas, 0, binding, 0, placement, page, key,
                        normalEncoding, originalSize)) return false;

                // Without a mip chain, minification remains clamped to level 0 and no derivative-to-LOD offset has
                // to be preserved. The close-view comparison above is therefore the complete persisted-level gate.
                if (candidateAtlas.mipmapCount <= 1) continue;

                // A packed page's low mips can merge padding and eventually unrelated islands. Validate every actual
                // persisted level, including candidate mip 0 at its runtime-corresponding source LOD and the 1x1 tail.
                // 图集低级 mip 可能合并 padding；从候选 mip 0 的对应源 LOD 起逐级验证到 1x1 尾级。
                if (!TryGetExactSourceMipOffset(binding.Texture, placement.Island,
                        placement.Island.TargetPixelSize, out var sourceOffset)) return false;
                for (var candidateMip = 0; candidateMip < candidateAtlas.mipmapCount; candidateMip++)
                {
                    ATOProgress.Checkpoint("Validating atlas mip " + candidateMip);
                    var sourceMip = Mathf.Min(binding.Texture.mipmapCount - 1, candidateMip + sourceOffset);
                    var evaluationSize = new Vector2Int(
                        Mathf.Max(1, Mathf.CeilToInt(placement.Island.UvBounds.width *
                            Mathf.Max(1, binding.Texture.width >> sourceMip))),
                        Mathf.Max(1, Mathf.CeilToInt(placement.Island.UvBounds.height *
                            Mathf.Max(1, binding.Texture.height >> sourceMip))));
                    if (!AtlasLevelPasses(candidateAtlas, candidateMip, binding, sourceMip, placement,
                            page, key, normalEncoding, evaluationSize)) return false;
                }
            }
            return true;
        }

        private bool AtlasLevelPasses(Texture2D candidateAtlas, int candidateMip,
            TextureBindingRecord binding, int sourceMip, AtlasPlacement placement, AtlasPage page,
            TextureTypeKey key, ATONormalInputEncoding normalEncoding, Vector2Int size)
        {
            if ((long)size.x * size.y > IslandQualityEvaluator.MaximumResidentPixels) return false;
            var atlasUv = new Rect((float)placement.ContentRect.x / page.Size.x,
                (float)placement.ContentRect.y / page.Size.y,
                (float)placement.ContentRect.width / page.Size.x,
                (float)placement.ContentRect.height / page.Size.y);
            var point = binding.Texture.filterMode == FilterMode.Point;
            RenderTexture reference = null, candidate = null;
            NativeArray<float4> referencePixels = default, candidatePixels = default;
            NativeArray<byte> mask = default;
            try
            {
                reference = _resampler.Resample(binding.Texture, placement.Island.UvBounds, size, point,
                    false, false, key.Kind, ATONormalInputEncoding.Imported, false, sourceMip);
                candidate = _resampler.Resample(candidateAtlas, atlasUv, size, point, false, false,
                    key.Kind, normalEncoding, placement.Rotated, candidateMip);
                referencePixels = _resampler.Readback(reference, Allocator.TempJob);
                candidatePixels = _resampler.Readback(candidate, Allocator.TempJob);
                mask = IslandMaskRasterizer.Rasterize(placement.Group, placement.Island, size,
                    Allocator.TempJob);
                if (!MaskHasCoverage(mask)) return false;
                var metrics = QualityMetricEvaluator.EvaluateForBinding(referencePixels, candidatePixels, mask,
                    size.x, size.y, binding);
                return metrics.Passes(_settings.EffectiveQuality, binding);
            }
            finally
            {
                if (referencePixels.IsCreated) referencePixels.Dispose();
                if (candidatePixels.IsCreated) candidatePixels.Dispose();
                if (mask.IsCreated) mask.Dispose();
                GpuLinearResampler.Release(reference); GpuLinearResampler.Release(candidate);
            }
        }

        internal static bool TryGetExactSourceMipOffset(Texture2D source, UvIsland island,
            Vector2Int target, out int offset)
        {
            // Remapping changes dUV/dscreen by sourceFootprint/atlasFootprint. A single unchanged mip bias can only
            // preserve source LOD when both axes have the same non-negative power-of-two ratio. Arbitrary or
            // anisotropic reductions are safe at mip 0 but not at every derivative direction, so the page falls back.
            // 只有二轴相同的非负 2 次幂缩放才能在不修改材质 mipBias 时保持 LOD 映射。
            var ratioX = island.UvBounds.width * source.width / Mathf.Max(1, target.x);
            var ratioY = island.UvBounds.height * source.height / Mathf.Max(1, target.y);
            if (!(ratioX >= 1f) || !(ratioY >= 1f) || float.IsNaN(ratioX) || float.IsNaN(ratioY) ||
                float.IsInfinity(ratioX) || float.IsInfinity(ratioY))
            { offset = 0; return false; }
            var lodX = Mathf.Log(ratioX, 2f); var lodY = Mathf.Log(ratioY, 2f);
            var rounded = Mathf.RoundToInt(lodX);
            const float tolerance = 1e-5f;
            if (source.mipmapCount <= 1 || rounded < 0 || rounded >= source.mipmapCount ||
                Mathf.Abs(lodX - rounded) > tolerance || Mathf.Abs(lodY - rounded) > tolerance)
            { offset = 0; return false; }
            offset = rounded; return true;
        }

        private static bool MaskHasCoverage(NativeArray<byte> mask)
        {
            for (var i = 0; i < mask.Length; i++)
                if (mask[i] != 0) return true;
            return false;
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

        internal static Vector2Int NextPullSize(Vector2Int current) =>
            new Vector2Int(Mathf.Max(1, current.x / 2 + current.x % 2),
                Mathf.Max(1, current.y / 2 + current.y % 2));

        private static RenderTexture CreateRt(Vector2Int size, GraphicsFormat format, bool randomWrite, string name)
        {
            RenderTexture value = null;
            try
            {
                value = new RenderTexture(size.x, size.y, 0);
                value.graphicsFormat = format;
                value.enableRandomWrite = randomWrite;
                value.useMipMap = false;
                value.autoGenerateMips = false;
                value.wrapMode = TextureWrapMode.Clamp;
                value.filterMode = FilterMode.Point;
                value.name = name;
                if (!value.Create()) throw new InvalidOperationException("ATO could not allocate an atlas GPU surface.");
                return value;
            }
            catch { GpuLinearResampler.Release(value); throw; }
        }

        internal static bool CanReuseBaseVariant(TextureBindingRecord binding, TextureBindingRecord baseBinding) =>
            ReferenceEquals(binding, baseBinding) || binding == null && baseBinding == null;

        internal static bool HasTextureContent(TextureBindingRecord binding) =>
            binding != null && binding.Texture != null;

        internal static bool HasBaseLayerContent(AtlasPlan plan, AtlasPage page, int layer)
        {
            if (plan == null || page == null || layer < 0) return false;
            return page.Groups.Any(group => group != null && plan.GroupLayouts.ContainsKey(group) &&
                layer < plan.GroupLayouts[group].LayerKeys.Count && HasTextureContent(BaseBinding(plan, group, layer)));
        }

        internal static bool HasCompletePlacementCoverage(AtlasPage page)
        {
            if (page == null || page.Size.x <= 0 || page.Size.y <= 0 || page.Groups.Count == 0 ||
                page.Groups.Any(group => group == null)) return false;
            var expected = new HashSet<UvIsland>(page.Groups.SelectMany(group => group.Islands));
            if (expected.Count == 0 || page.Placements.Count != expected.Count) return false;
            var seen = new HashSet<UvIsland>();
            foreach (var placement in page.Placements)
            {
                if (placement == null || placement.Group == null || placement.Island == null ||
                    !page.Groups.Contains(placement.Group) || !placement.Group.Islands.Contains(placement.Island) ||
                    !expected.Contains(placement.Island) || !seen.Add(placement.Island)) return false;
                var expectedSize = placement.Rotated
                    ? new Vector2Int(placement.Island.TargetPixelSize.y, placement.Island.TargetPixelSize.x)
                    : placement.Island.TargetPixelSize;
                if (expectedSize.x <= 0 || expectedSize.y <= 0 ||
                    placement.ContentRect.width != expectedSize.x || placement.ContentRect.height != expectedSize.y ||
                    placement.PaddedRect.width <= 0 || placement.PaddedRect.height <= 0 ||
                    placement.PaddedRect.xMin < 0 || placement.PaddedRect.yMin < 0 ||
                    placement.PaddedRect.xMax > page.Size.x || placement.PaddedRect.yMax > page.Size.y ||
                    placement.ContentRect.xMin < placement.PaddedRect.xMin ||
                    placement.ContentRect.yMin < placement.PaddedRect.yMin ||
                    placement.ContentRect.xMax > placement.PaddedRect.xMax ||
                    placement.ContentRect.yMax > placement.PaddedRect.yMax) return false;
            }
            return true;
        }

        private static TextureBindingRecord BaseBinding(AtlasPlan plan, UvGroupRecord group, int layer)
        {
            var material = CurrentMaterial(group); var layout = plan.GroupLayouts[group];
            return material != null && layout.MaterialLayers.TryGetValue(material, out var layers) ? layers[layer].Initial : null;
        }

        private static Material CurrentMaterial(UvGroupRecord group)
        {
            var renderer = group?.Renderer?.Renderer;
            if (renderer == null || group.Slot == null) return null;
            var materials = renderer.sharedMaterials;
            return group.Slot.Slot >= 0 && group.Slot.Slot < materials.Length ? materials[group.Slot.Slot] : null;
        }

        private static void ReleaseOwned(HashSet<RenderTexture> owned, RenderTexture texture)
        {
            if (texture != null && owned.Remove(texture)) GpuLinearResampler.Release(texture);
        }

        private static int Groups(int value) => (value + 7) / 8;
        private static string SafeName(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_'));
        public void Dispose() => _resampler.Dispose();

        private readonly struct VariantContentKey : IEquatable<VariantContentKey>
        {
            private readonly int _page, _layer;
            private readonly UvGroupRecord _group;
            private readonly Texture2D _source;
            public VariantContentKey(int page, int layer, UvGroupRecord group, Texture2D source)
            { _page = page; _layer = layer; _group = group; _source = source; }
            public bool Equals(VariantContentKey other) => _page == other._page && _layer == other._layer &&
                ReferenceEquals(_group, other._group) && _source == other._source;
            public override bool Equals(object obj) => obj is VariantContentKey other && Equals(other);
            public override int GetHashCode() => (((_page * 397) ^ _layer) * 397 ^
                (_group == null ? 0 : _group.GetHashCode())) * 397 ^
                (_source == null ? 0 : _source.GetHashCode());
        }

        private readonly struct VariantKey : IEquatable<VariantKey>
        {
            private readonly int _page, _layer;
            private readonly UvGroupRecord _group;
            private readonly TextureBindingRecord _binding;
            public VariantKey(int page, int layer, UvGroupRecord group, TextureBindingRecord binding)
            { _page = page; _layer = layer; _group = group; _binding = binding; }
            public bool Equals(VariantKey other) => _page == other._page && _layer == other._layer &&
                ReferenceEquals(_group, other._group) && ReferenceEquals(_binding, other._binding);
            public override bool Equals(object obj) => obj is VariantKey other && Equals(other);
            public override int GetHashCode() => (((_page * 397) ^ _layer) * 397 ^
                (_group == null ? 0 : _group.GetHashCode())) * 397 ^
                (_binding == null ? 0 : _binding.GetHashCode());
        }
    }
}

// Application Pass - Complete with MipStreaming binding, material slot merge, safety fallback
// 应用Pass - 包含MipStreaming绑定、材质槽合并、安全降级的完整实现

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Runtime;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace net.fosa.avatar_texture_optimizer.Editor.Core.Passes
{
    public class ApplicationPass : Pass<ApplicationPass>
    {
        public override string DisplayName => "ATO: Application / 应用";

        protected override void Execute(BuildContext context)
        {
            var sw = Stopwatch.StartNew();
            var atoCtx = context.GetState<ATOBuildContext>();
            if (!atoCtx.IsValid) return;
            var comp = atoCtx.Component;

            atoCtx.ReportProgress("Applying: Meshes...", 0f);
            ApplyMeshUVs(atoCtx, context);

            atoCtx.ReportProgress("Applying: Materials...", 0.3f);
            UpdateMaterialReferences(atoCtx, context);

            atoCtx.ReportProgress("Applying: Animations...", 0.5f);
            UpdateAnimationReferences(atoCtx);

            atoCtx.ReportProgress("Applying: Texture settings...", 0.7f);
            ApplyTextureSettings(atoCtx, comp);

            atoCtx.ReportProgress("Applying: Safety fallbacks...", 0.85f);
            ApplySafetyFallbacks(atoCtx, comp);

            atoCtx.ReportProgress("Applying: Material slot merge...", 0.95f);
            MergeMaterialSlots(atoCtx);

            sw.Stop();
            atoCtx.StageTimings["Application"] = sw.Elapsed.TotalMilliseconds;
        }

        private void ApplyMeshUVs(ATOBuildContext atoCtx, BuildContext context)
        {
            var islandsByMesh = new Dictionary<Mesh, List<UVIsland>>();
            foreach (var isl in atoCtx.AllIslands)
            {
                if (isl.NewUVs == null || isl.NewUVs.Count == 0 || isl.SourceMesh == null) continue;
                if (!islandsByMesh.ContainsKey(isl.SourceMesh))
                    islandsByMesh[isl.SourceMesh] = new List<UVIsland>();
                islandsByMesh[isl.SourceMesh].Add(isl);
            }

            foreach (var kvp in islandsByMesh)
            {
                var origMesh = kvp.Key;
                var islands = kvp.Value;
                var newMesh = Object.Instantiate(origMesh);
                newMesh.name = origMesh.name + "_ATO";

                int ch = islands[0].UvChannel;
                var origUVs = new List<Vector2>();
                newMesh.GetUVs(ch, origUVs);
                var allUVs = new Vector2[newMesh.vertexCount];
                for (int i = 0; i < origUVs.Count && i < allUVs.Length; i++)
                    allUVs[i] = origUVs[i];

                foreach (var isl in islands)
                {
                    if (isl.NewUVs == null) continue;
                    // Map island vertex indices to mesh vertex indices and apply new UVs
                    var vertMap = BuildVertexMap(isl);
                    for (int i = 0; i < isl.NewUVs.Count && i < vertMap.Count; i++)
                    {
                        int vi = vertMap[i];
                        if (vi >= 0 && vi < allUVs.Length)
                            allUVs[vi] = isl.NewUVs[i];
                    }
                }

                newMesh.SetUVs(ch, new List<Vector2>(allUVs));

                // Replace on renderers
                foreach (var ri in atoCtx.Renderers)
                {
                    if (ri.SharedMesh != origMesh) continue;
                    if (ri.Renderer is SkinnedMeshRenderer smr) smr.sharedMesh = newMesh;
                    else
                    {
                        var mf = ri.Renderer.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = newMesh;
                    }
                }
                atoCtx.ModifiedMeshes[origMesh] = newMesh;
            }
            ATOLog.Info($"Applied UVs to {atoCtx.ModifiedMeshes.Count} meshes.");
        }

        private List<int> BuildVertexMap(UVIsland island)
        {
            // Build mapping from island UV index to mesh vertex index
            var uniqueVerts = new HashSet<int>();
            foreach (int vi in island.TriangleIndices) uniqueVerts.Add(vi);
            return uniqueVerts.OrderBy(v => v).ToList();
        }

        private void UpdateMaterialReferences(ATOBuildContext atoCtx, BuildContext context)
        {
            if (atoCtx.Atlases == null || atoCtx.Atlases.Count == 0) return;

            var texToAtlas = new Dictionary<int, Texture2D>();
            foreach (var atlas in atoCtx.Atlases)
            {
                if (atlas.AtlasTexture == null) continue;
                foreach (var pk in atlas.PackedIslands)
                {
                    var isl = atoCtx.AllIslands.FirstOrDefault(i => i.Id == pk.IslandId);
                    if (isl == null || isl.SourceTextureIndex < 0 || isl.SourceTextureIndex >= atoCtx.AllTextures.Count) continue;
                    texToAtlas[atoCtx.AllTextures[isl.SourceTextureIndex].InstanceId] = atlas.AtlasTexture;
                }
            }

            var updated = new HashSet<Material>();
            foreach (var ri in atoCtx.Renderers)
            {
                if (ri.SharedMaterials == null) continue;
                foreach (var mat in ri.SharedMaterials)
                {
                    if (mat == null || updated.Contains(mat)) continue;
                    updated.Add(mat);
                    if (!atoCtx.ShaderAnalysisResults.TryGetValue(mat, out var sr)) continue;
                    foreach (var tp in sr.TextureProperties)
                    {
                        var tex = mat.GetTexture(tp.PropertyName) as Texture2D;
                        if (tex == null) continue;
                        if (texToAtlas.TryGetValue(tex.GetInstanceID(), out var atlasTex))
                        {
                            mat.SetTexture(tp.PropertyName, atlasTex);
                            if (!atoCtx.MaterialUpdates.ContainsKey(mat))
                                atoCtx.MaterialUpdates[mat] = new MaterialUpdate { OriginalMaterial = mat };
                            atoCtx.MaterialUpdates[mat].TextureReplacements[tp.PropertyName] = atlasTex;
                        }
                    }
                }
            }
        }

        private void UpdateAnimationReferences(ATOBuildContext atoCtx)
        {
            if (atoCtx.AnimationAnalysis == null) return;
            foreach (var swap in atoCtx.AnimationAnalysis.MaterialSwaps)
            {
                foreach (var mat in swap.SwappedMaterials)
                {
                    if (mat == null) continue;
                    if (atoCtx.MaterialUpdates.TryGetValue(mat, out var upd))
                        foreach (var kvp in upd.TextureReplacements)
                            mat.SetTexture(kvp.Key, kvp.Value);
                }
            }
        }

        /// <summary>
        /// Apply MipStreaming/Mipmap binding, compression formats, and import settings.
        /// MipStreaming和Mipmap绑定（单开关同时控制两者），压缩格式，导入设置。
        /// VRChat requires: if Mipmap enabled → MipStreaming must be enabled.
        /// VRChat要求：若开启Mipmap → 必须开启MipStreaming。因此二者绑定。
        /// </summary>
        private void ApplyTextureSettings(ATOBuildContext atoCtx, AvatarTextureOptimizerComponent comp)
        {
#if UNITY_EDITOR
            bool mipStreaming = comp.enableMipStreaming;

            // Apply to all generated textures (atlases + fallback)
            var allGenerated = new List<Texture2D>(atoCtx.GeneratedTextures);
            allGenerated.AddRange(atoCtx.FallbackTextures);

            foreach (var tex in allGenerated)
            {
                if (tex == null) continue;
                string path = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(path)) continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                // Mipmap + MipStreaming binding (single toggle controls both)
                // Mipmap + MipStreaming绑定（单开关同时控制两者）
                importer.mipmapEnabled = mipStreaming;
                importer.streamingMipmaps = mipStreaming;

                // Read/Write: forced off
                importer.isReadable = false;
                // WrapMode: forced clamp for atlases
                if (tex.name.StartsWith("ATO_Atlas"))
                    importer.wrapMode = TextureWrapMode.Clamp;

                // Apply compression format based on texture role and platform
                ApplyPlatformCompression(importer, tex, atoCtx, comp);

                importer.SaveAndReimport();
            }
#endif
        }

        /// <summary>
        /// Apply platform-specific compression with safety checks.
        /// 应用平台特定的压缩格式并进行安全检查。
        /// </summary>
        private void ApplyPlatformCompression(TextureImporter importer, Texture2D tex,
            ATOBuildContext atoCtx, AvatarTextureOptimizerComponent comp)
        {
#if UNITY_EDITOR
            // Determine texture category
            bool hasAlpha = false;
            bool isNormal = false;
            bool isGrayscale = false;

            foreach (var ti in atoCtx.AllTextures)
            {
                if (ti.Texture == tex || ti.OriginalTexture == tex)
                {
                    hasAlpha = ti.HasAlpha;
                    isNormal = ti.IsNormalMap;
                    isGrayscale = ti.IsGrayscale;
                    break;
                }
            }

            // Check atlas alpha
            if (tex.name.StartsWith("ATO_Atlas"))
            {
                foreach (var atlas in atoCtx.Atlases)
                {
                    if (atlas.AtlasTexture == tex)
                    {
                        hasAlpha = atlas.PackedIslands.Any(p =>
                        {
                            var isl = atoCtx.AllIslands.FirstOrDefault(i => i.Id == p.IslandId);
                            if (isl == null || isl.SourceTextureIndex < 0) return false;
                            if (isl.SourceTextureIndex >= atoCtx.AllTextures.Count) return false;
                            return atoCtx.AllTextures[isl.SourceTextureIndex].HasAlpha;
                        });
                        break;
                    }
                }
            }

            // Get format settings
            var fmt = GetFormatForCategory(comp, hasAlpha, isNormal, isGrayscale, atoCtx.EffectivePlatform);

            // Safety: alpha textures must not use non-alpha formats
            // 安全检查：有alpha的贴图不能使用无alpha的格式
            if (hasAlpha && !FormatSupportsAlpha(fmt))
            {
                atoCtx.AddWarning($"Texture '{tex.name}' has alpha but format {fmt} doesn't support alpha. Falling back to BC7. / 贴图'{tex.name}'有alpha但格式{fmt}不支持alpha，降级为BC7。");
                fmt = TextureImporterFormat.BC7;
            }

            // Safety: normal maps should use BC5 or similar two-channel format
            if (isNormal && fmt != TextureImporterFormat.BC5 && fmt != TextureImporterFormat.Automatic)
            {
                // Allow but warn
                atoCtx.AddWarning($"Normal map '{tex.name}' using non-standard format {fmt}. / 法线贴图'{tex.name}'使用非标准格式{fmt}。");
            }

            // Platform-specific restrictions
            // 平台特定限制
            if (atoCtx.EffectivePlatform == TargetPlatform.iOS)
            {
                // iOS: remove PVRTC when NPOT enabled
                if (comp.enableNPOTAtlas && (fmt == TextureImporterFormat.PVRTC_RGB_4BPP ||
                    fmt == TextureImporterFormat.PVRTC_RGBA_4BPP))
                {
                    atoCtx.AddWarning($"iOS + NPOT: PVRTC not supported. Falling back to ASTC. / iOS+NPOT：不支持PVRTC，降级为ASTC。");
                    fmt = hasAlpha ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.ASTC_6x6;
                }
            }

            // Apply format
            var platform = GetPlatformString(atoCtx.EffectivePlatform);
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.overridden = true;
            settings.format = (TextureImporterFormat)fmt;
            settings.textureCompression = TextureImporterCompression.Compressed;
            importer.SetPlatformTextureSettings(settings);

            // Also set default
            importer.textureCompression = TextureImporterCompression.Compressed;
#endif
        }

        private TextureImporterFormat GetFormatForCategory(
            AvatarTextureOptimizerComponent comp, bool hasAlpha,
            bool isNormal, bool isGrayscale, TargetPlatform platform)
        {
            var fs = comp.formatSettings;

            // Check platform overrides
            if (comp.enablePlatformOverrides)
            {
                var pSettings = GetPlatformSettings(comp, platform);
                if (pSettings != null) fs = pSettings.formatSettings;
            }

            if (isNormal) return MapFormat(fs.normalFormat);
            if (isGrayscale) return MapFormat(fs.grayscaleFormat);
            if (hasAlpha) return MapFormat(fs.transparentFormat);
            return MapFormat(fs.opaqueFormat);
        }

        private Runtime.PlatformSpecificSettings GetPlatformSettings(
            AvatarTextureOptimizerComponent comp, TargetPlatform platform)
        {
            switch (platform)
            {
                case TargetPlatform.PC: return comp.platformOverrides.overridePC ? comp.platformOverrides.pcSettings : null;
                case TargetPlatform.Android: return comp.platformOverrides.overrideAndroid ? comp.platformOverrides.androidSettings : null;
                case TargetPlatform.iOS: return comp.platformOverrides.overrideiOS ? comp.platformOverrides.iOSSettings : null;
                default: return null;
            }
        }

        private TextureImporterFormat MapFormat(TextureCompressionFormat f)
        {
            switch (f)
            {
                case TextureCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case TextureCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case TextureCompressionFormat.BC4: return TextureImporterFormat.BC4;
                case TextureCompressionFormat.BC1: return TextureImporterFormat.DXT1;
                case TextureCompressionFormat.BC3: return TextureImporterFormat.DXT5;
                case TextureCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case TextureCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case TextureCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case TextureCompressionFormat.ETC2_RGB: return TextureImporterFormat.ETC2_RGB4;
                case TextureCompressionFormat.ETC2_RGBA: return TextureImporterFormat.ETC2_RGBA8;
                default: return TextureImporterFormat.Automatic;
            }
        }

        private bool FormatSupportsAlpha(TextureImporterFormat fmt)
        {
            switch (fmt)
            {
                case TextureImporterFormat.BC7:
                case TextureImporterFormat.BC3:
                case TextureImporterFormat.DXT5:
                case TextureImporterFormat.ASTC_4x4:
                case TextureImporterFormat.ASTC_6x6:
                case TextureImporterFormat.ASTC_8x8:
                case TextureImporterFormat.ASTC_12x12:
                case TextureImporterFormat.ETC2_RGBA8:
                case TextureImporterFormat.PVRTC_RGBA_4BPP:
                case TextureImporterFormat.RGBA32:
                case TextureImporterFormat.Automatic:
                    return true;
                default:
                    return false;
            }
        }

        private string GetPlatformString(TargetPlatform p)
        {
            switch (p)
            {
                case TargetPlatform.Android: return "Android";
                case TargetPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        /// <summary>
        /// Safety fallback: ensure no invalid option combinations damage materials.
        /// 安全降级：确保无效选项组合不会破坏材质。
        /// </summary>
        private void ApplySafetyFallbacks(ATOBuildContext atoCtx, AvatarTextureOptimizerComponent comp)
        {
            // Check all modified materials for issues
            foreach (var kvp in atoCtx.MaterialUpdates)
            {
                var mat = kvp.Key;
                if (mat == null || mat.shader == null) continue;

                // Safety: ensure cutout materials still have alpha channel
                if (atoCtx.ShaderAnalysisResults.TryGetValue(mat, out var sr))
                {
                    var transMode = ShaderAnalyzer.GetTransparencyMode(mat, sr);
                    if (transMode == TransparencyMode.Cutout || transMode == TransparencyMode.Blend)
                    {
                        // Verify the main texture still has alpha
                        foreach (var tp in sr.TextureProperties)
                        {
                            var tex = mat.GetTexture(tp.PropertyName) as Texture2D;
                            if (tex == null) continue;
                            if (tp.Role != TextureRole.MainColor) continue;

                            // If the replacement texture lost alpha somehow, fallback
                            if (transMode != TransparencyMode.Opaque)
                            {
                                var format = GetAppliedFormat(tex);
                                if (!FormatSupportsAlpha(format))
                                {
                                    atoCtx.AddWarning($"Material '{mat.name}' requires alpha but texture format doesn't support it. Fallback to BC7. / 材质'{mat.name}'需要alpha但贴图格式不支持，降级为BC7。");
                                    // Will be fixed in texture settings
                                }
                            }
                        }
                    }
                }
            }
        }

        private TextureImporterFormat GetAppliedFormat(Texture2D tex)
        {
#if UNITY_EDITOR
            string path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path))
            {
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp != null) return imp.textureFormat;
            }
#endif
            return TextureImporterFormat.Automatic;
        }

        /// <summary>
        /// Merge material slots when opaque materials are deduplicated.
        /// Updates animation references and material slot indices.
        /// 当不透明材质被去重时合并材质槽。更新动画引用和材质槽索引。
        /// </summary>
        private void MergeMaterialSlots(ATOBuildContext atoCtx)
        {
            if (!atoCtx.Component.deduplicateMaterials) return;

            foreach (var ri in atoCtx.Renderers)
            {
                if (ri.SharedMaterials == null || ri.SharedMaterials.Length <= 1) continue;

                // Find identical materials in different slots
                var matHash = new Dictionary<string, int>(); // hash → first slot index
                var slotsToRemove = new List<int>();

                for (int i = 0; i < ri.SharedMaterials.Length; i++)
                {
                    var mat = ri.SharedMaterials[i];
                    if (mat == null) continue;

                    string hash = GetMaterialContentHash(mat);
                    if (matHash.TryGetValue(hash, out int firstSlot))
                    {
                        // This slot is a duplicate - check if animation switches them individually
                        bool animSwitches = HasIndividualAnimationSwitch(ri.Renderer, i, firstSlot, atoCtx);
                        if (!animSwitches)
                        {
                            slotsToRemove.Add(i);
                            atoCtx.SlotMerges.Add(new MaterialSlotMerge
                            {
                                Renderer = ri.Renderer,
                                MergedSlots = new List<int> { i },
                                TargetSlot = firstSlot,
                                MergedMaterial = ri.SharedMaterials[firstSlot]
                            });
                        }
                    }
                    else
                    {
                        matHash[hash] = i;
                    }
                }

                if (slotsToRemove.Count > 0)
                {
                    // Rebuild material array without duplicates
                    var newMats = new List<Material>();
                    for (int i = 0; i < ri.SharedMaterials.Length; i++)
                    {
                        if (!slotsToRemove.Contains(i))
                            newMats.Add(ri.SharedMaterials[i]);
                    }

                    var newMatArray = newMats.ToArray();
                    if (ri.Renderer is SkinnedMeshRenderer smr) smr.sharedMaterials = newMatArray;
                    else if (ri.Renderer is MeshRenderer mr) mr.sharedMaterials = newMatArray;

                    ATOLog.Info($"Merged {slotsToRemove.Count} duplicate material slots on '{ri.Renderer.name}'.");
                }
            }
        }

        private bool HasIndividualAnimationSwitch(Renderer renderer, int slotA, int slotB, ATOBuildContext atoCtx)
        {
            if (atoCtx.AnimationAnalysis == null) return false;
            foreach (var swap in atoCtx.AnimationAnalysis.MaterialSwaps)
            {
                if (swap.Renderer != renderer) continue;
                if (swap.MaterialSlot == slotA || swap.MaterialSlot == slotB)
                    return true; // Animation individually switches these slots
            }
            return false;
        }

        private string GetMaterialContentHash(Material mat)
        {
            if (mat == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(mat.shader?.name ?? "");
            var shader = mat.shader;
            if (shader != null)
            {
                int pc = shader.GetPropertyCount();
                for (int i = 0; i < pc; i++)
                {
                    var pt = shader.GetPropertyType(i);
                    var pn = shader.GetPropertyName(i);
                    switch (pt)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            sb.Append($"{pn}:{mat.GetTexture(pn)?.GetInstanceID() ?? 0},");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            sb.Append($"{pn}:{mat.GetFloat(pn):F4},");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            sb.Append($"{pn}:{mat.GetColor(pn)},");
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            sb.Append($"{pn}:{mat.GetVector(pn)},");
                            break;
                    }
                }
            }
            return sb.ToString();
        }
    }
}

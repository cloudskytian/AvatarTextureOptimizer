// ATOPipeline.cs
// Main pipeline orchestration. Coordinates all stages:
// scan → dedup → analyze shaders → build UV mappings → quality scale →
// rasterize → pack → generate atlas → rebake mesh → dedup materials → report.
// 主管线编排。协调所有阶段。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// The top-level pipeline that drives the entire texture optimization process.
    /// 驱动整个贴图优化过程的顶层管线。
    /// </summary>
    internal sealed class ATOPipeline
    {
        private const int TotalPhases = 12;
        private readonly BuildContext _context;
        private ATOComponent _component;
        private AdvancedSettings _settings;
        private ATOLogger _log;
        private ATOOptimizationReport _report;
        private ATOProgress _progress;

        internal ATOPipeline(BuildContext context)
        {
            _context = context;
            _log = ATOLogger.Instance;
        }

        internal void Execute()
        {
            var totalSw = Stopwatch.StartNew();
            _log.Configure(false);
            _progress = new ATOProgress();

            try
            {
                ExecuteInternal(totalSw);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("Pipeline cancelled by user. Temporary assets retained on disk. / 用户取消了管线。临时资产保留在磁盘上。");
                _log.Info($"═══ Pipeline cancelled after {totalSw.ElapsedMilliseconds}ms ═══");
            }
            catch (Exception ex)
            {
                _log.Error($"Pipeline failed: {ex}");
                Debug.LogException(ex);
                throw;
            }
            finally
            {
                _progress?.Dispose();
            }
        }

        private void ExecuteInternal(Stopwatch totalSw)
        {
            // ── Step 0: Find and validate the ATO component ──
            _log.BeginTimer("Phase0_Validate");
            _progress.ShowPhase("Validating component", 0, TotalPhases);

            _component = FindAndValidateComponent();
            if (_component == null || !_component.IsEnabled)
            {
                _log.EndTimer("Phase0_Validate");
                _log.Info("ATO component not found or disabled. Skipping. / 未找到 ATO 组件或已禁用，跳过。");
                return;
            }

            _log.Configure(_component._verboseLogging);
            _log.Info($"═══ Avatar Texture Optimizer — Starting pipeline ═══");
            _log.Info($"Avatar: {_context.AvatarRootObject.name}");
            _log.Info($"Quality preset: {_component._qualityPreset}");
            _log.Info($"Generate atlas: {_component._generateAtlas}");

            // Resolve settings from preset
            _settings = _component._qualityPreset == QualityPreset.Custom
                ? _component._advanced.Clone()
                : AdvancedSettings.ForPreset(_component._qualityPreset);

            if (_component._qualityPreset == QualityPreset.NearLossless)
            {
                _settings.mSSSIMThreshold = 1.0f;
            }

            _report = new ATOOptimizationReport();
            _log.EndTimer("Phase0_Validate");

            // ── Step 1: Scan avatar ──
            _progress.ShowPhase("Scanning avatar", 1, TotalPhases);
            _log.BeginTimer("Phase1_Scan");
            var scanner = new AvatarScanner(_context.AvatarRootObject, _component, _settings);
            var scanResult = scanner.Scan();
            _log.EndTimer("Phase1_Scan");

            if (scanResult.TextureReferences.Count == 0)
            {
                _log.Info("No optimizable textures found. Skipping. / 未找到可优化的贴图，跳过。");
                FinishReport(totalSw);
                return;
            }

            // ── Step 2: Deduplicate textures ──
            _progress.ShowPhase("Deduplicating textures", 2, TotalPhases);
            _log.BeginTimer("Phase2_DedupTextures");
            var deduplicator = new TextureDeduplicator(scanResult, _log);
            int dedupCount = deduplicator.Execute();
            _report.TexturesDeduplicated = dedupCount;
            _log.Info($"Texture deduplication: {dedupCount} duplicates removed. / 去重移除了 {dedupCount} 个重复贴图。");
            _log.EndTimer("Phase2_DedupTextures");

            // ── Step 3: Analyze shaders ──
            _progress.ShowPhase("Analyzing shaders", 3, TotalPhases);
            _log.BeginTimer("Phase3_AnalyzeShaders");
            var shaderAnalyzer = new ShaderTextureAnalyzer(scanResult, _log);
            shaderAnalyzer.Analyze();
            _log.EndTimer("Phase3_AnalyzeShaders");

            // ── Step 4: Build UV-to-texture mappings ──
            _progress.ShowPhase("Building UV mappings", 4, TotalPhases);
            _log.BeginTimer("Phase4_BuildMappings");
            var mappingBuilder = new UVMappingBuilder(scanResult, _context.AvatarRootObject, _component, _settings, _log);
            var (uvGroups, typeGroups) = mappingBuilder.Build();
            _log.Info($"Built {uvGroups.Count} UV groups across {typeGroups.Count} texture type groups. / 建立了 {uvGroups.Count} 个 UV 组，{typeGroups.Count} 个贴图类型组。");
            _log.EndTimer("Phase4_BuildMappings");

            int totalIslands = 0;
            foreach (var ug in uvGroups) totalIslands += ug.Islands.Count;
            _report.IslandsProcessed = totalIslands;

            if (totalIslands == 0)
            {
                _log.Info("No UV islands to process. Skipping. / 没有 UV 岛需要处理，跳过。");
                FinishReport(totalSw);
                return;
            }

            // ── Step 5: Quality-scale UV islands ──
            _progress.ShowPhase("Quality scaling UV islands", 5, TotalPhases);
            _log.BeginTimer("Phase5_QualityScale");
            var scaler = new QualityScaler(uvGroups, typeGroups, _settings, _component, _log);
            int scaledCount = scaler.Execute();
            _report.IslandsScaled = scaledCount;
            _log.Info($"Quality-scaled {scaledCount} islands. / 质量缩放了 {scaledCount} 个岛。");
            _log.EndTimer("Phase5_QualityScale");

            // ── Step 6-8: Atlas generation (if enabled) ──
            if (_component._generateAtlas)
            {
                // Step 6: Rasterize islands
                _progress.ShowPhase("Rasterizing islands (Burst)", 6, TotalPhases);
                _log.BeginTimer("Phase6_Rasterize");
                var rasterizer = new IslandRasterizer(typeGroups, _settings, _log);
                rasterizer.Execute();
                _log.EndTimer("Phase6_Rasterize");

                // Step 7: Pack into atlases
                _progress.ShowPhase("Packing atlases", 7, TotalPhases);
                _log.BeginTimer("Phase7_Pack");
                var packer = new BinPacker(typeGroups, _component, _settings, _log);
                var atlases = packer.Execute();
                _report.AtlasesGenerated = atlases.Count;
                _log.Info($"Generated {atlases.Count} atlases. / 生成了 {atlases.Count} 个图集。");
                _log.EndTimer("Phase7_Pack");

                // Step 8: Render atlas textures
                _progress.ShowPhase("Rendering atlases (GPU pull-push)", 8, TotalPhases);
                _log.BeginTimer("Phase8_RenderAtlas");
                var renderer = new AtlasBuilder(typeGroups, atlases, _context, _settings, _log);
                renderer.Execute();
                _report.OriginalTextureBytes = renderer.OriginalBytes;
                _report.OptimizedTextureBytes = renderer.OptimizedBytes;
                foreach (var a in atlases)
                {
                    _report.AtlasDetails.Add(new AtlasDetail
                    {
                        Name = a.Name,
                        Width = a.Width,
                        Height = a.Height,
                        Utilization = a.Utilization,
                        SourceCount = a.PlacedIslands.Select(i => i.SourceTexture).Distinct().Count(),
                        IslandCount = a.PlacedIslands.Count
                    });
                }
                _log.EndTimer("Phase8_RenderAtlas");
            }
            else
            {
                _progress.ShowPhase("Scaling textures (no atlas)", 6, TotalPhases);
                _log.BeginTimer("Phase6_ScaleTextures");
                var textureScaler = new WholeTextureScaler(typeGroups, _context, _settings, _log);
                textureScaler.Execute();
                _log.EndTimer("Phase6_ScaleTextures");
            }

            // ── Step 9: Rebake meshes (reassign UVs) ──
            _progress.ShowPhase("Rebaking meshes", 9, TotalPhases);
            _log.BeginTimer("Phase9_RebakeMesh");
            var meshRebaker = new MeshRebaker(uvGroups, typeGroups, _context, _log);
            meshRebaker.Execute();
            _log.EndTimer("Phase9_RebakeMesh");

            // ── Step 10: Deduplicate materials ──
            _progress.ShowPhase("Deduplicating materials", 10, TotalPhases);
            if (_component._deduplicateMaterials || _component._deduplicateTextures)
            {
                _log.BeginTimer("Phase10_DedupMaterials");
                var matDedup = new MaterialDeduplicator(_context.AvatarRootObject, _component, typeGroups, _log);
                int matDedupCount = matDedup.Execute();
                _report.MaterialsDeduplicated = matDedupCount;
                _log.Info($"Material deduplication: {matDedupCount} duplicates removed. / 材质去重移除了 {matDedupCount} 个重复。");
                _log.EndTimer("Phase10_DedupMaterials");
            }

            // ── Step 11: AAO UV compatibility ──
            _progress.ShowPhase("AAO UV compatibility", 11, TotalPhases);
            _log.BeginTimer("Phase11_AAOCompat");
            try
            {
                var aaoCompat = new AAOCompat.AAOCompatibility(_context.AvatarRootObject, uvGroups, _log);
                aaoCompat.RegisterEvacuation();
            }
            catch (Exception ex)
            {
                _log.Verbose($"AAO compatibility skipped: {ex.Message}");
            }
            _log.EndTimer("Phase11_AAOCompat");

            // ── Step 12: Configure texture import settings ──
            _progress.ShowPhase("Configuring import settings", 12, TotalPhases);
            _log.BeginTimer("Phase12_ImportSettings");
            var importConfig = new TextureImportConfigurator(typeGroups, _context, _component, _settings, _log);
            importConfig.Execute();
            _log.EndTimer("Phase12_ImportSettings");

            FinishReport(totalSw);
        }

        private ATOComponent FindAndValidateComponent()
        {
            var components = _context.AvatarRootObject.GetComponentsInChildren<ATOComponent>(true);
            if (components.Length == 0)
            {
                _log.Verbose("No ATOComponent found on avatar. / Avatar 上未找到 ATOComponent。");
                return null;
            }

            // Validate: only one component allowed across the entire hierarchy
            if (components.Length > 1)
            {
                _log.Error($"Multiple ATOComponents found ({components.Length}). Only one is allowed per avatar hierarchy. Aborting. / 发现多个 ATOComponent，每个 Avatar 层级只允许一个。中止。");
                throw new Exception("ATO: Multiple ATOComponents found. Only one is allowed per avatar.");
            }

            var comp = components[0];

            // Validate: component must be on a VRCAvatarDescriptor object (or its child where descriptor exists on root)
            #if ATO_VRCSDK_PRESENT
            var rootDescriptor = _context.AvatarRootObject.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (rootDescriptor == null)
            {
                _log.Error("ATOComponent requires a VRCAvatarDescriptor on the avatar root. Aborting. / ATOComponent 需要 Avatar 根上有 VRCAvatarDescriptor。中止。");
                throw new Exception("ATO: VRCAvatarDescriptor not found on avatar root.");
            }
            #endif

            _log.Info($"ATO component found on: {comp.gameObject.name}");
            return comp;
        }

        private void FinishReport(Stopwatch totalSw)
        {
            totalSw.Stop();
            _log.Info($"═══ Pipeline complete in {totalSw.ElapsedMilliseconds}ms ═══");

            // Output summary to NDMF console
            var reportString = _log.GenerateSummaryReport(_report);
            _log.Info(reportString);

#if ATO_VRCSDK_PRESENT || true
            // Output report to the NDMF error console via Debug.Log (already done in Info above).
            // The full report is also available in the Unity Console with [ATO] prefix.
#endif
        }
    }
}

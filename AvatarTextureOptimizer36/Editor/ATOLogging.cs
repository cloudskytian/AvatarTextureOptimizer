using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Centralized logger with one stable prefix. / 带统一前缀的集中式日志器。
    /// </summary>
    internal sealed class ATOLogger : IDisposable
    {
        private readonly bool _detailed;
        private bool _disposed;

        public bool Detailed => _detailed;

        public ATOLogger(bool detailed)
        {
            _detailed = detailed;
        }

        public void Info(string message)
        {
            UnityEngine.Debug.Log("[ATO] " + message);
        }

        public void Warning(string message)
        {
            UnityEngine.Debug.LogWarning("[ATO] Warning: " + message);
        }

        public void Detail(string message)
        {
            if (_detailed) UnityEngine.Debug.Log("[ATO] Detail: " + message);
        }

        public IDisposable Measure(string stage)
        {
            return new ATOStageTimer(this, stage);
        }

        public static void Debug(string message)
        {
            UnityEngine.Debug.Log("[ATO] Debug: " + message);
        }

        public static void Error(string message)
        {
            UnityEngine.Debug.LogError("[ATO] Error: " + message);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private sealed class ATOStageTimer : IDisposable
        {
            private readonly ATOLogger _logger;
            private readonly string _stage;
            private readonly Stopwatch _watch;
            private bool _disposed;

            public ATOStageTimer(ATOLogger logger, string stage)
            {
                _logger = logger;
                _stage = stage;
                _watch = Stopwatch.StartNew();
                _logger.Detail("Begin " + stage);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _watch.Stop();
                _logger.Info(_stage + " took " + _watch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms");
            }
        }
    }

    /// <summary>
    /// Compact build metrics consumed by the NDMF console report. / 供 NDMF 控制台报告使用的紧凑构建指标。
    /// </summary>
    internal sealed class ATOBuildReport
    {
        private readonly List<string> _warnings = new List<string>();
        private readonly List<ATOAtlasReportRow> _atlases = new List<ATOAtlasReportRow>();
        private readonly ATOPlatform _platform;
        private readonly ATOQualityPreset _preset;
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public int RendererCount { get; private set; }
        public int MaterialCount { get; private set; }
        public int TextureCount { get; private set; }
        public int IslandCount { get; private set; }
        public int AtlasCount => _atlases.Count;
        public long OriginalPixels { get; private set; }
        public long GeneratedPixels { get; private set; }
        public int DeduplicatedTextures { get; set; }
        public int DeduplicatedMaterials { get; set; }
        public int FallbackCount { get; set; }
        public bool Finished { get; private set; }
        public IReadOnlyList<string> Warnings => _warnings;
        public IReadOnlyList<ATOAtlasReportRow> Atlases => _atlases;

        public ATOBuildReport(ATOPlatform platform, ATOQualityPreset preset)
        {
            _platform = platform;
            _preset = preset;
        }

        public void SetAnalysis(BuildSnapshot snapshot)
        {
            if (snapshot == null) return;
            RendererCount = snapshot.Renderers.Count;
            MaterialCount = snapshot.MaterialUses.Count;
            TextureCount = snapshot.Textures.Count;
            IslandCount = snapshot.Islands.Count;
            for (int i = 0; i < snapshot.Textures.Count; i++)
            {
                TextureAssetInfo texture = snapshot.Textures[i];
                if (texture != null) OriginalPixels += (long)texture.Width * texture.Height;
            }
        }

        public void AddWarning(string warning)
        {
            if (string.IsNullOrEmpty(warning)) return;
            _warnings.Add(warning);
        }

        public void AddAtlas(string name, int width, int height, int islandCount, long sourcePixels, long outputPixels,
            float utilization)
        {
            _atlases.Add(new ATOAtlasReportRow(name, width, height, islandCount, sourcePixels, outputPixels, utilization));
            GeneratedPixels += outputPixels;
        }

        public void Finish()
        {
            Finished = true;
            _watch.Stop();
        }

        public string Overview()
        {
            double reduction = OriginalPixels <= 0
                ? 0d
                : (1d - GeneratedPixels / (double)OriginalPixels) * 100d;
            return string.Format(
                CultureInfo.InvariantCulture,
                "Platform={0}; Preset={1}; Renderers={2}; Materials={3}; Textures={4}; Islands={5}; Atlases={6}; " +
                "Pixels={7}->{8} ({9:F1}%); SourceDedup={10}; MaterialDedup={11}; Fallbacks={12}; Warnings={13}; Time={14:F1}ms",
                _platform, _preset, RendererCount, MaterialCount, TextureCount, IslandCount, AtlasCount,
                OriginalPixels, GeneratedPixels, reduction, DeduplicatedTextures, DeduplicatedMaterials, FallbackCount,
                _warnings.Count, _watch.Elapsed.TotalMilliseconds);
        }
    }

    internal readonly struct ATOAtlasReportRow
    {
        public readonly string Name;
        public readonly int Width;
        public readonly int Height;
        public readonly int IslandCount;
        public readonly long SourcePixels;
        public readonly long OutputPixels;
        public readonly float Utilization;

        public ATOAtlasReportRow(string name, int width, int height, int islandCount, long sourcePixels, long outputPixels,
            float utilization)
        {
            Name = name;
            Width = width;
            Height = height;
            IslandCount = islandCount;
            SourcePixels = sourcePixels;
            OutputPixels = outputPixels;
            Utilization = utilization;
        }
    }

    internal static class ATOReportPrinter
    {
        public static void Print(ATOBuildReport report, ATOLogger logger)
        {
            logger.Info("Build report: " + report.Overview());
            if (report.Atlases.Count == 0 && report.Warnings.Count == 0) return;

            StringBuilder details = new StringBuilder();
            details.AppendLine("[ATO] Details / 详细信息");
            for (int i = 0; i < report.Atlases.Count; i++)
            {
                ATOAtlasReportRow atlas = report.Atlases[i];
                details.Append("  ").Append(atlas.Name)
                    .Append(" ").Append(atlas.Width).Append("x").Append(atlas.Height)
                    .Append(" islands=").Append(atlas.IslandCount)
                    .Append(" utilization=").Append(atlas.Utilization.ToString("P1", CultureInfo.InvariantCulture))
                    .Append(" sourcePixels=").Append(atlas.SourcePixels)
                    .Append(" outputPixels=").Append(atlas.OutputPixels)
                    .AppendLine();
            }

            if (report.Warnings.Count > 0)
            {
                details.AppendLine("  Warnings / 警告:");
                for (int i = 0; i < report.Warnings.Count; i++) details.Append("    - ").AppendLine(report.Warnings[i]);
            }

            logger.Detail(details.ToString());
        }
    }
}

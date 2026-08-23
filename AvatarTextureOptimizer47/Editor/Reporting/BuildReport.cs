using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Reporting
{
    /// <summary>EN: One generated atlas statistic. ZH: 一条生成图集统计信息。</summary>
    internal sealed class AtlasStatistic
    {
        public string Name;
        public int Width;
        public int Height;
        public int IslandCount;
        public float Utilization;
        public long BeforeBytes;
        public long AfterBytes;
        public readonly List<string> Sources = new List<string>();
    }

    /// <summary>EN: Build telemetry and user-facing diagnostics. ZH: 构建遥测与面向用户的诊断信息。</summary>
    internal sealed class AtoBuildReport
    {
        public readonly Dictionary<string, double> StageMilliseconds = new Dictionary<string, double>();
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<AtlasStatistic> Atlases = new List<AtlasStatistic>();
        public int RendererCount;
        public int MaterialCount;
        public int SourceTextureCount;
        public int DeduplicatedTextureCount;
        public int ProcessedTextureCount;
        public int FallbackTextureCount;
        public int IslandCount;

        public IDisposable Measure(string stage) => new TimingScope(this, stage);

        public void Log(string message, bool verbose = true)
        {
            Details.Add(message);
            if (verbose) UnityEngine.Debug.Log("[ATO] " + message);
        }

        public void Warn(string message, Object context = null)
        {
            Warnings.Add(message);
            UnityEngine.Debug.LogWarning("[ATO] " + message, context);
            ErrorReport.ReportError(new AtoNdmfMessage(ErrorSeverity.NonFatal, "Avatar Texture Optimizer", message, context));
        }

        public void Error(string message, Object context = null)
        {
            UnityEngine.Debug.LogError("[ATO] " + message, context);
            ErrorReport.ReportError(new AtoNdmfMessage(ErrorSeverity.Error, "Avatar Texture Optimizer", message, context));
        }

        public void PublishSummary()
        {
            var before = Atlases.Sum(x => x.BeforeBytes);
            var after = Atlases.Sum(x => x.AfterBytes);
            var saved = before > 0 ? 1d - (double)after / before : 0d;
            var summary = $"Processed {RendererCount} renderers, {MaterialCount} materials, " +
                          $"{SourceTextureCount} source textures and {IslandCount} islands. " +
                          $"Generated {Atlases.Count} atlases; estimated texture reduction {saved:P1}. " +
                          $"Fallbacks: {FallbackTextureCount}.";
            ErrorReport.ReportError(new AtoNdmfMessage(ErrorSeverity.Information,
                "Avatar Texture Optimizer report", summary, null, BuildDetails()));
            UnityEngine.Debug.Log("[ATO] " + summary + "\n" + BuildDetails());
        }

        private string BuildDetails()
        {
            var lines = new List<string>();
            lines.AddRange(StageMilliseconds.OrderBy(x => x.Key).Select(x => $"Stage {x.Key}: {x.Value:F1} ms"));
            foreach (var atlas in Atlases)
            {
                lines.Add($"{atlas.Name}: {atlas.Width}x{atlas.Height}, islands={atlas.IslandCount}, " +
                          $"utilization={atlas.Utilization:P1}, sources=[{string.Join(", ", atlas.Sources)}]");
            }
            if (Warnings.Count > 0) lines.AddRange(Warnings.Select(x => "Warning: " + x));
            if (Details.Count > 0) lines.AddRange(Details.Select(x => "Detail: " + x));
            return string.Join("\n", lines);
        }

        private sealed class TimingScope : IDisposable
        {
            private readonly AtoBuildReport _owner;
            private readonly string _stage;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            public TimingScope(AtoBuildReport owner, string stage) { _owner = owner; _stage = stage; }
            public void Dispose()
            {
                _watch.Stop();
                _owner.StageMilliseconds[_stage] = _owner.StageMilliseconds.TryGetValue(_stage, out var value)
                    ? value + _watch.Elapsed.TotalMilliseconds : _watch.Elapsed.TotalMilliseconds;
                UnityEngine.Debug.Log($"[ATO] Stage {_stage} completed in {_watch.Elapsed.TotalMilliseconds:F1} ms");
            }
        }
    }

    /// <summary>EN: Foldable NDMF console message. ZH: 可折叠的 NDMF 控制台消息。</summary>
    internal sealed class AtoNdmfMessage : IError
    {
        private readonly string _title;
        private readonly string _message;
        private readonly string _details;
        private readonly List<ObjectReference> _references;
        public AtoNdmfMessage(ErrorSeverity severity, string title, string message, Object context, string details = null)
        {
            Severity = severity;
            _title = title;
            _message = message;
            _details = details;
            _references = context == null ? new List<ObjectReference>() : new List<ObjectReference> { ObjectRegistry.GetReference(context) };
        }
        public ErrorSeverity Severity { get; }
        public ObjectReference[] References => _references.ToArray();
        public void AddReference(ObjectReference obj) { if (obj != null) _references.Add(obj); }
        public VisualElement CreateVisualElement(nadena.dev.ndmf.ErrorReport report)
        {
            var root = new VisualElement();
            root.Add(new Label(_title) { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            root.Add(new Label(_message) { style = { whiteSpace = WhiteSpace.Normal } });
            if (!string.IsNullOrEmpty(_details))
            {
                var foldout = new Foldout { text = "Details / 详细信息", value = false };
                foldout.Add(new Label(_details) { style = { whiteSpace = WhiteSpace.Normal } });
                root.Add(foldout);
            }
            return root;
        }
        public string ToMessage() => _title + ": " + _message + (string.IsNullOrEmpty(_details) ? "" : "\n" + _details);
    }
}

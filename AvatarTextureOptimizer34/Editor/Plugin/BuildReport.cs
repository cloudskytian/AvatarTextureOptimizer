// AvatarTextureOptimizer - BuildReport
// EN: Build report: summary printed to the NDMF console with collapsed per-atlas details.
// CN: 构建报告：汇总输出到 NDMF 控制台，图集细节折叠展示。
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.localization;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Plugin
{
    public sealed class BuildReport
    {
        public int IslandCount;
        public int AtlasCount;
        public long AtlasAreaPx;
        public double Utilization;
        public long OriginalAreaPx;
        public double SavingsPct;
        public int TextureCount;
        public int AnimationClipCount;
        public readonly List<(string name, int w, int h, int srcCount, int islands, float util)> AtlasLines =
            new List<(string, int, int, int, int, float)>();

        private readonly AvatarTextureOptimizer _component;

        public BuildReport(AvatarTextureOptimizer component)
        {
            _component = component;
        }

        public void Fill(AtoBuildState state, PackingResult packing, List<Texture2D> generated)
        {
            long islandPx = 0;
            long originalPx = 0;
            int islands = 0;
            foreach (var g in state.UvGroups)
            {
                foreach (var island in g.islands)
                {
                    foreach (var kv in island.scales)
                    {
                        var s = kv.Value;
                        if (s.skip) continue;
                        originalPx += (long)kv.Key.width * kv.Key.height;
                    }
                }
            }
            foreach (var atlas in packing.atlases)
            {
                AtlasAreaPx += (long)atlas.width * atlas.height;
                AtlasLines.Add((atlas.Name ?? "?", atlas.width, atlas.height, atlas.sourceTextureCount,
                    atlas.islands.Count, atlas.Utilization));
                Utilization += atlas.Utilization;
                islands += atlas.islands.Count;
                foreach (var pi in atlas.islands)
                {
                    islandPx += (long)(pi.rect.width * pi.rect.height);
                    var s = pi.island.scales.TryGetValue(pi.tex, out var sc) ? sc : null;
                    if (s != null)
                        originalPx += (long)s.targetW * s.targetH;
                }
            }
            IslandCount = islands > 0 ? islands : state.UvGroups.Count;
            AtlasCount = packing.atlases.Count;
            if (AtlasCount > 0) Utilization /= AtlasCount;
            OriginalAreaPx = originalPx > 0 ? originalPx : 1;
            double optimizedPx = islandPx > 0 ? islandPx : AtlasAreaPx;
            SavingsPct = 100.0 * (1.0 - optimizedPx / OriginalAreaPx);
        }

        public void Write()
        {
            var sb = new StringBuilder();
            sb.AppendLine(I18n.T("report.title"));
            sb.AppendLine(I18n.T("report.summary", IslandCount, AtlasCount,
                AtoLog.Bytes(AtlasAreaPx * 4), AtoLog.Pct((float)Utilization),
                AtoLog.Bytes(OriginalAreaPx * 4), AtoLog.Pct((float)SavingsPct)));
            sb.AppendLine($"Textures: {TextureCount} | Animation clips: {AnimationClipCount}");
            if (AtoLog.Detailed)
            {
                foreach (var (name, w, h, src, il, util) in AtlasLines)
                {
                    sb.AppendLine(I18n.T("report.atlas", name, w, h, src, il, AtoLog.Pct(util)));
                }
            }
            else
            {
                sb.AppendLine(I18n.T("ui.details"));
            }
            Debug.Log(sb.ToString());
            AtoLog.FlushDetails("=== AvatarTextureOptimizer details ===");
        }
    }

    /// <summary>EN: NDMF error report integration. / CN: NDMF 错误报告集成。</summary>
    public sealed class AtoSimpleError : SimpleError
    {
        private readonly string _message;
        private readonly ErrorSeverity _severity;
        private static Localizer _localizer;

        private static Localizer MakeLocalizer()
        {
            if (_localizer != null) return _localizer;
            _localizer = new Localizer("en", () => new List<(string, Func<string, string>)>
            {
                ("en", key => key == "ATOError" ? _message : key)
            });
            return _localizer;
        }

        public AtoSimpleError(ErrorSeverity severity, string message)
        {
            _severity = severity;
            _message = message;
            _localizer = MakeLocalizer();
        }

        public override Localizer Localizer => _localizer;
        public override string TitleKey => "ATOError";
        public override string[] TitleSubst => System.Array.Empty<string>();
        public override ErrorSeverity Severity => _severity;
    }
}

// NDMF-console report: overall summary + collapsible per-atlas details + stage timings
// (spec: timings, atlas sources, island counts, sizes, utilization, savings).
// NDMF 控制台报告：总览 + 可折叠的图集明细 + 阶段耗时（需求书：耗时/来源/岛数/尺寸/利用率/优化量）。

using System;
using System.Text;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOReport
    {
        internal static void Emit(AtoSession s)
        {
            var loc = ATOL10n.NdmfLocalizer;

            // summary / 总览
            string summary = $"{ATOL10n.Get("report.textures", Lang())} {s.texInfos.Count}   " +
                             $"{ATOL10n.Get("report.materials", Lang())} {s.atlasedMaterials}   " +
                             $"{ATOL10n.Get("report.islands", Lang())} {s.islands.Count}   " +
                             $"{ATOL10n.Get("report.saved", Lang())} {VramSaved(s):F1} MB" +
                             (s.component.generateAtlas ? "" : $"\n{ATOL10n.Get("report.noAtlasMode", Lang())}");
            ErrorReport.ReportError(loc, ErrorSeverity.Information, "report.title", summary);

            // per-atlas details / 每图集明细
            foreach (var atlas in s.atlases)
            {
                long used = 0;
                foreach (var p in atlas.placements) used += (long)p.rect.width * p.rect.height;
                float util = (float)used / ((long)atlas.pageW * atlas.pageH);
                var sources = new StringBuilder();
                int n = 0;
                foreach (var t in atlas.textures)
                {
                    if (n++ > 0) sources.Append(", ");
                    if (n > 8) { sources.Append("..."); break; }
                    sources.Append(t.name);
                }

                ErrorReport.ReportError(loc, ErrorSeverity.Information, "report.atlasLine",
                    $"ATO_{atlas.typeGroupKey}", atlas.pageW, atlas.pageH,
                    atlas.normalPageScale < 1f || atlas.maskPageScale < 1f ? "(+reduced pages)" : "",
                    atlas.placements.Count, atlas.textures.Count, $"{util:P1}", sources.ToString());
            }

            // stage timings / 阶段耗时
            var sb = new StringBuilder();
            foreach (var (stage, ms) in ATOLog.StageTimings) sb.Append($"{stage}: {ms:F0}ms  ");
            if (sb.Length > 0)
                ErrorReport.ReportError(loc, ErrorSeverity.Information, "report.timings", sb.ToString());

            // warnings / 警告
            foreach (var w in s.warnings)
                ErrorReport.ReportError(loc, ErrorSeverity.Information, "report.details", w);
        }

        internal static void EmitCancelled()
        {
            ErrorReport.ReportError(ATOL10n.NdmfLocalizer, ErrorSeverity.Information,
                "report.cancelled", (object[])Array.Empty<string>());
        }

        private static string Lang() => null; // current language / 当前语言

        /// <summary>Rough VRAM delta: source textures vs final outputs (bpp estimates).
        /// 粗略显存差：源贴图 vs 最终产物（位率估算）。</summary>
        internal static float VramSaved(AtoSession s)
        {
            float before = 0f;
            foreach (var kv in s.texInfos)
            {
                if (kv.Value.whitelisted) continue;
                before += EstimateBytes(kv.Key);
            }

            float after = 0f;
            var counted = new System.Collections.Generic.HashSet<Texture2D>();
            foreach (var t in MaterialPatcher.Replacement.Values)
                if (t != null && counted.Add(t))
                    after += EstimateBytes(t);

            return (before - after) / (1024f * 1024f);
        }

        private static float EstimateBytes(Texture2D t)
        {
            float bpp;
            switch (t.format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.BC4: bpp = 4f; break;
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC7:
                case TextureFormat.BC5:
                case TextureFormat.ASTC_4x4: bpp = 8f; break;
                case TextureFormat.ASTC_5x5: bpp = 5.12f; break;
                case TextureFormat.ASTC_6x6: bpp = 3.56f; break;
                case TextureFormat.ASTC_8x8: bpp = 2f; break;
                default: bpp = 32f; break;
            }

            float bytes = t.width * (long)t.height * bpp / 8f;
            return t.mipmapCount > 1 ? bytes * 1.333f : bytes;
        }
    }
}

using System.Linq;
using System.Text;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Converts build state into user-facing logs.
    /// 将构建状态转换为面向用户的日志输出。
    /// </summary>
    internal static class AtoReporting
    {
        public static void EmitSummary(AtoSessionState session)
        {
            var report = session.Report;
            var summary = new StringBuilder();
            summary.AppendLine(AtoLocalization.Translate("Logs:Summary"));
            summary.AppendLine($"[ATO] Avatar: {session.Component.gameObject.name}");
            summary.AppendLine($"[ATO] Renderers: {report.RendererCount}");
            summary.AppendLine($"[ATO] Material slots: {report.MaterialSlotCount}");
            summary.AppendLine($"[ATO] Unique materials: {report.MaterialCount}");
            summary.AppendLine($"[ATO] Texture usages: {report.TextureCandidateCount}");
            summary.AppendLine($"[ATO] Unique textures: {report.UniqueTextureCount}");
            summary.AppendLine($"[ATO] Animation clips: {report.AnimationClipCount}");
            summary.AppendLine($"[ATO] UV groups: {session.ScanResult.UvGroups.Count}");
            summary.AppendLine($"[ATO] Planned texture type groups: {session.Plan.TextureTypeGroups.Count}");
            summary.AppendLine($"[ATO] UV islands: {report.UvIslandCount}");
            summary.AppendLine($"[ATO] Planned atlases: {report.PlannedAtlasCount}");
            summary.AppendLine($"[ATO] Executed generated textures: {report.ExecutedTextureCount}");
            summary.AppendLine($"[ATO] Executed generated atlases: {report.ExecutedAtlasCount}");
            summary.AppendLine($"[ATO] Executed cloned meshes: {report.ExecutedMeshCount}");
            summary.AppendLine($"[ATO] Executed cloned materials: {report.ExecutedMaterialCount}");
            summary.AppendLine($"[ATO] Whitelist hits: {report.WhitelistHitCount}");
            summary.AppendLine($"[ATO] Safe fallbacks: {report.UnsupportedCount}");
            summary.AppendLine($"[ATO] Potential duplicate groups: {report.PotentialDuplicateGroupCount}");
            summary.AppendLine($"[ATO] Total source bytes: {EditorUtility.FormatBytes(report.TextureSourceBytes)}");
            summary.AppendLine($"[ATO] Total elapsed: {session.TotalTimer.Elapsed.TotalMilliseconds:F2} ms");

            if (report.StageTimesMs.Count > 0)
            {
                summary.AppendLine("[ATO] Stage timings:");
                foreach (var pair in report.StageTimesMs.OrderBy(p => p.Key, System.StringComparer.OrdinalIgnoreCase))
                {
                    summary.AppendLine($"[ATO]   - {pair.Key}: {pair.Value:F2} ms");
                }
            }

            AtoLog.Info(summary.ToString().TrimEnd());

            if (session.Component.DebugLogging)
            {
                EmitDetails(session);
            }
        }

        public static void EmitDetails(AtoSessionState session)
        {
            var report = session.Report;
            var details = new StringBuilder();
            details.AppendLine(AtoLocalization.Translate("Logs:Details"));

            if (report.WarningLines.Count > 0)
            {
                details.AppendLine("[ATO] Warnings:");
                foreach (var line in report.WarningLines)
                {
                    details.AppendLine($"[ATO]   - {line}");
                }
            }

            if (report.DetailLines.Count > 0)
            {
                details.AppendLine("[ATO] Candidate details:");
                foreach (var line in report.DetailLines)
                {
                    details.AppendLine($"[ATO]   - {line}");
                }
            }

            if (session.ScanResult.UvGroups.Count > 0)
            {
                details.AppendLine("[ATO] UV groups:");
                foreach (var group in session.ScanResult.UvGroups.Take(16))
                {
                    details.AppendLine($"[ATO]   - {group.Key} | hasData={group.HasData} | min={group.Min} | max={group.Max} | span={group.Span} | inUnitSquare={group.InUnitSquareAlready} | translatable={group.CanTranslateIntoUnitSquare} | animAreaScale={group.AnimatedAreaScaleFactor:F3}");
                }
            }

            if (session.ScanResult.DuplicateGroups.Count > 0)
            {
                details.AppendLine("[ATO] Duplicate texture groups:");
                foreach (var group in session.ScanResult.DuplicateGroups)
                {
                    details.AppendLine($"[ATO]   - Group {group.Fingerprint.Substring(0, Mathf.Min(24, group.Fingerprint.Length))}..., members={group.Members.Count}");
                    foreach (var member in group.Members.Take(6))
                    {
                        details.AppendLine($"[ATO]       * {member.TexturePath} | {member.RendererPath} | {member.MaterialProperty}");
                    }
                }
            }

            if (session.Plan.TextureTypeGroups.Count > 0)
            {
                details.AppendLine("[ATO] Texture type groups:");
                foreach (var group in session.Plan.TextureTypeGroups.Take(16))
                {
                    details.AppendLine($"[ATO]   - {group.Key} | property={group.MaterialProperty} | semantic={group.Semantic} | members={group.Members.Count} | atlases={group.Atlases.Count} | wrap={group.WrapModeU}/{group.WrapModeV} | filter={group.FilterMode}");
                    foreach (var atlas in group.Atlases.Take(4))
                    {
                        details.AppendLine($"[ATO]       * {atlas.Name} | size={atlas.Width}x{atlas.Height} | items={atlas.Items.Count} | util={atlas.EstimatedUtilization:P1}");
                    }
                }
            }

            AtoLog.Info(details.ToString().TrimEnd());
        }

        public static string GetCurrentBuildPlatformLabel()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    return AvatarTextureOptimizerTargetPlatform.Android.ToString();
                case BuildTarget.iOS:
                    return AvatarTextureOptimizerTargetPlatform.IOS.ToString();
                default:
                    return AvatarTextureOptimizerTargetPlatform.PC.ToString();
            }
        }
    }
}

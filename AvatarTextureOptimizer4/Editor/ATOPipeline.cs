// Avatar Texture Optimizer (ATO)
// Main pipeline orchestration. Each stage is a focused, independently-reviewable unit.
// 主管线编排。每个阶段都是可独立审查的聚焦单元。

using System;
using System.Diagnostics;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// The full optimization pipeline, executed in order:
    ///   scan -> animations -> whitelist/eligibility -> texture dedup -> UV islands ->
    ///   quality scaling -> atlas pack/build/remap (or direct resize) -> material/texture/slot dedup ->
    ///   animation reference rewrite -> post settings -> report -> self-removal.
    /// 完整优化管线，按顺序执行：
    ///   扫描 -> 动画 -> 白名单/资格 -> 贴图去重 -> UV 岛 -> 质量缩放 -> 图集装箱/构建/重映射（或直接缩放）
    ///   -> 材质/贴图/槽去重 -> 动画引用改写 -> 后处理设置 -> 报告 -> 移除自身。
    /// </summary>
    public static class ATOPipeline
    {
        public static void Run(ATOBuildContext build)
        {
            var sw = Stopwatch.StartNew();

            // ---- Stage 0: scan renderers, material slots, textures / 扫描渲染器、材质槽、贴图 ----
            using (var p = new ATOProgress("Scan"))
            {
                build.progress = p;
                ATOAvatarScanner.Scan(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 1: analyze animations / 分析动画 ----
            using (var p = new ATOProgress("Animation analysis"))
            {
                build.progress = p;
                ATOAnimationAnalyzer.Analyze(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 2: resolve whitelist & eligibility / 解析白名单与资格 ----
            using (var p = new ATOProgress("Eligibility & whitelist"))
            {
                build.progress = p;
                ATOEligibility.Resolve(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 3: texture dedup / 贴图去重 ----
            using (var p = new ATOProgress("Texture dedup"))
            {
                build.progress = p;
                ATOTextureDeduplicator.Deduplicate(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 4: build UV islands (per mesh+channel) / 构建 UV 岛 ----
            using (var p = new ATOProgress("UV island extraction"))
            {
                build.progress = p;
                ATOIslandBuilder.BuildAll(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 5: quality scaling of islands / 岛质量缩放 ----
            using (var p = new ATOProgress("Quality scaling"))
            {
                build.progress = p;
                ATOIslandScaler.ScaleAll(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 6: atlasing or direct resize / 图集化或直接缩放 ----
            using (var p = new ATOProgress("Atlas packing"))
            {
                build.progress = p;
                if (build.profile.generateAtlas)
                {
                    ATOAtlasPacker.Pack(build, p);
                    p.ThrowIfCancelled();
                    ATOAtlasBuilder.BuildAll(build, p);
                    p.ThrowIfCancelled();
                    ATOUVRemapper.Apply(build, p);
                }
                // Whole-texture scaling applies to UV-mates of whitelisted textures (and
                // everything in no-atlas mode). / 整图缩放用于白名单贴图的同 UV 贴图（以及无图集模式下的全部贴图）。
                ATODirectResizer.Resize(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 7: material/texture/slot dedup + animation rewrite / 材质/贴图/槽去重 + 动画改写 ----
            using (var p = new ATOProgress("Dedup & remap"))
            {
                build.progress = p;
                if (build.profile.dedupTextures) ATOTextureDeduplicator.DeduplicateFinal(build, p);
                if (build.profile.dedupMaterials || build.profile.mergeOpaqueSlots)
                {
                    ATOMaterialDeduplicator.Deduplicate(build, p);
                    ATOMaterialSlotMerger.Merge(build, p);
                }
                ATOAnimationRewriter.Apply(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Stage 8: post settings (mip streaming, compression, read/write, clamp) / 后处理设置 ----
            using (var p = new ATOProgress("Texture settings"))
            {
                build.progress = p;
                ATOTextureSettingsApplier.Apply(build, p);
                p.ThrowIfCancelled();
            }

            // ---- Custom extension stages (advanced users / third-party). / 自定义扩展阶段。 ----
            using (var p = new ATOProgress("Custom stages"))
            {
                build.progress = p;
                ATOStageRegistry.RunAll(build);
            }

            // ---- Stage 9: report + self removal / 报告 + 移除自身 ----
            using (var p = new ATOProgress("Report"))
            {
                build.progress = p;
                ATOBuildReportWriter.Write(build);
                ATOSelfRemoval.Remove(build);
            }

            sw.Stop();
            build.report.totalTimeMs = sw.Elapsed.TotalMilliseconds;
            ATOLogger.Info($"Total ATO processing time: {sw.Elapsed.TotalSeconds:F2}s");
        }
    }
}

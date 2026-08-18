// Copyright (c) fosa. Licensed under the MIT License.
// The NDMF pass: validates the component, runs the pipeline, applies results to the avatar and
// removes itself. Everything that can fail is contained so a build never dies half-applied.
// NDMF pass：校验组件、运行管线、将结果应用到 Avatar 并移除自身。
// 所有可能失败的环节都被包裹，使构建绝不会在半应用状态下中断。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Executes texture optimization during the NDMF build.
    /// 在 NDMF 构建过程中执行贴图优化。
    /// </summary>
    public sealed class ATOPass : Pass<ATOPass>
    {
        /// <inheritdoc />
        public override string QualifiedName => "net.fosa.avatar-texture-optimizer.optimize";

        /// <inheritdoc />
        public override string DisplayName => "Optimize Avatar Textures";

        /// <inheritdoc />
        protected override void Execute(BuildContext context)
        {
            var component = FindComponent(context);
            if (component == null) return;

            var logger = new ATOLogger
            {
                Verbose = component.Settings.verboseLogging,
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (!Validate(context, component, logger)) return;

                var platform = DetectPlatform();
                var settings = component.Settings.Resolve(platform);

                if (!settings.enabled && platform != ATOPlatform.PC)
                {
                    // A disabled override means "use the shared settings".
                    // 未启用的覆盖意味着「使用共享设置」。
                    settings = component.Settings.shared;
                }

                logger.Info($"Starting optimization for platform {platform}");

                using (var pipeline = new OptimizationPipeline(logger, ReportProgress))
                {
                    var result = pipeline.Run(context.AvatarRootObject, settings);

                    if (result.Cancelled)
                    {
                        ErrorReport.ReportError(
                            ATOLocalization.Localizer,
                            ErrorSeverity.NonFatal,
                            "ato.error.cancelled");
                        return;
                    }

                    RepointAnimationReferences(context, result, logger);
                    PersistAssets(context, result, logger);
                    LogSummary(logger, result, stopwatch);
                }
            }
            catch (Exception e)
            {
                // A texture optimizer must never break a build. Report and leave the avatar
                // exactly as it was.
                // 贴图优化器绝不能破坏构建。报告错误并使 Avatar 保持原样。
                logger.Error($"Optimization failed: {e}");

                // The second parameter is additionalStackTrace, not a message; passing a
                // description there would corrupt the reported stack trace.
                // 第二个参数是 additionalStackTrace 而非消息；
                // 在此传入描述文本会破坏报告中的堆栈信息。
                ErrorReport.ReportException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();

                // The component is build-time only and must not ship on the uploaded avatar.
                // 该组件仅在构建期使用，不得随上传的 Avatar 一同发布。
                if (component != null) Object.DestroyImmediate(component);
            }
        }

        /// <summary>
        /// Locates the single optimizer component and rejects invalid setups.
        /// 定位唯一的优化器组件并拒绝无效配置。
        /// </summary>
        private static AvatarTextureOptimizer FindComponent(BuildContext context)
        {
            var root = context.AvatarRootObject;
            if (root == null) return null;

            var components = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            return components.Length == 0 ? null : components[0];
        }

        private static bool Validate(
            BuildContext context, AvatarTextureOptimizer component, ATOLogger logger)
        {
            var root = context.AvatarRootObject;
            var components = root.GetComponentsInChildren<AvatarTextureOptimizer>(true);

            if (components.Length > 1)
            {
                ErrorReport.ReportError(
                    ATOLocalization.Localizer,
                    ErrorSeverity.Error,
                    "ato.error.multiple-components");
                logger.Error(
                    $"Found {components.Length} Avatar Texture Optimizer components; " +
                    "exactly one is required");
                return false;
            }

            // The component must sit on the avatar root, which is where the descriptor lives.
            // 组件必须位于 Avatar 根节点上，即描述符所在之处。
            if (component.gameObject != root)
            {
                ErrorReport.ReportError(
                    ATOLocalization.Localizer,
                    ErrorSeverity.Error,
                    "ato.error.no-descriptor");
                logger.Error(
                    "Avatar Texture Optimizer must be on the avatar root next to the " +
                    "VRCAvatarDescriptor");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines the build platform from NDMF's platform provider.
        /// 通过 NDMF 的平台提供者确定构建平台。
        /// </summary>
        private static ATOPlatform DetectPlatform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    return ATOPlatform.Android;
                case BuildTarget.iOS:
                    return ATOPlatform.iOS;
                default:
                    return ATOPlatform.PC;
            }
        }

        /// <summary>
        /// Shows progress and returns false when the user cancels.
        /// 显示进度，用户取消时返回 false。
        /// </summary>
        private static bool ReportProgress(string stageKey, float fraction)
        {
            var title = ATOLocalization.Tr("ato.progress.title");
            var info = ATOLocalization.Tr(stageKey);

            // Returns true when the cancel button was pressed, so the sense is inverted.
            // 按下取消按钮时返回 true，因此此处含义取反。
            return !EditorUtility.DisplayCancelableProgressBar(title, info, fraction);
        }

        /// <summary>
        /// Repoints animation object curves at the surviving materials after deduplication.
        /// Without this an animation that swaps to a merged-away material would assign a
        /// material that is no longer saved as an asset, and the swap would render as missing.
        /// 在材质去重后，将动画对象曲线重定向到存活的材质。
        /// 否则，切换到已被合并掉的材质的动画会赋值一个不再被保存为资产的材质，
        /// 该切换会显示为丢失。
        /// </summary>
        private static void RepointAnimationReferences(
            BuildContext context, OptimizationResult result, ATOLogger logger)
        {
            var mapping = result.MaterialDeduplication;
            if (mapping == null || mapping.Count == 0) return;

            var hasMerge = false;
            foreach (var kv in mapping)
            {
                if (kv.Key != kv.Value)
                {
                    hasMerge = true;
                    break;
                }
            }

            if (!hasMerge) return;

            try
            {
                var asc = context.Extension<AnimatorServicesContext>();
                if (asc == null) return;

                asc.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    if (obj is Material mat && mapping.TryGetValue(mat, out var rep))
                    {
                        return rep;
                    }

                    return obj;
                });

                logger.Detail("Repointed animation references after material dedup");
            }
            catch (Exception e)
            {
                // Never fail the build over this; the worst case is a redundant material.
                // 绝不因此导致构建失败；最坏情况只是多出一个冗余材质。
                logger.Warning($"Could not repoint animation references: {e.Message}");
            }
        }

        /// <summary>
        /// Registers every generated asset with NDMF so it survives the build.
        /// 将所有生成的资产注册到 NDMF，使其在构建后得以保留。
        /// </summary>
        private static void PersistAssets(
            BuildContext context, OptimizationResult result, ATOLogger logger)
        {
            var saver = context.AssetSaver;
            if (saver == null)
            {
                logger.Warning("No asset saver available; generated assets may not persist");
                return;
            }

            var count = 0;

            foreach (var tex in result.GeneratedTextures)
            {
                if (tex == null) continue;
                saver.SaveAsset(tex);
                count++;
            }

            foreach (var mesh in result.GeneratedMeshes)
            {
                if (mesh == null) continue;
                saver.SaveAsset(mesh);
                count++;
            }

            foreach (var mat in result.GeneratedMaterials)
            {
                if (mat == null) continue;
                saver.SaveAsset(mat);
                count++;
            }

            logger.Detail($"Persisted {count} generated assets");
        }

        private static void LogSummary(
            ATOLogger logger, OptimizationResult result, Stopwatch stopwatch)
        {
            stopwatch.Stop();

            if (result.OptimizedTextureCount == 0)
            {
                logger.Info(ATOLocalization.Tr("ato.report.noop"));
                return;
            }

            var saved = FormatBytes(result.SavedBytes);
            var percent = result.OriginalBytes > 0
                ? $"{100.0 * result.SavedBytes / result.OriginalBytes:F1}%"
                : "0%";

            var summary = ATOLocalization.Tr(
                "ato.report.summary",
                result.OptimizedTextureCount,
                result.Atlases.Count,
                saved,
                percent);

            foreach (var atlas in result.Atlases)
            {
                logger.Detail(ATOLocalization.Tr(
                    "ato.report.atlas",
                    atlas.Index,
                    atlas.Width,
                    atlas.Height,
                    CountIslands(atlas),
                    $"{atlas.Utilization * 100f:F1}%"));
            }

            logger.Info($"{summary} in {stopwatch.ElapsedMilliseconds} ms");
            UnityEngine.Debug.Log(logger.BuildReport(summary));
        }

        private static int CountIslands(AtlasResult atlas)
        {
            var n = 0;
            foreach (var g in atlas.Groups) n += g.Islands.Count;
            return n;
        }

        /// <summary>
        /// Formats a byte count for humans.
        /// 将字节数格式化为易读形式。
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024L) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}

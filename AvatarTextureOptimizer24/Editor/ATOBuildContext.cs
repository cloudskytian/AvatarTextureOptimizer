// ============================================================================
// ATOBuildContext.cs — 单次构建的共享状态 / Shared state for one build
// (EN) Holds the NDMF BuildContext, the resolved ATO settings, whitelist, and
//      the report for a single avatar build.
// (ZH) 保存单次 Avatar 构建的 NDMF BuildContext、解析后的设置、白名单与报告。
// ============================================================================

using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class ATOBuildContext
    {
        public BuildContext Ndmf;
        public AvatarTextureOptimizer Component;
        public GameObject AvatarRoot;

        // 解析后的生效设置 / resolved effective settings (after platform override)
        public ATOQualitySettings Quality;
        public ATOAtlasSettings Atlas;
        public ATOCompressionSettings Compression;
        public ATODedupSettings Dedup;
        public List<ATOPlatformOverride> PlatformOverrides;
        public HashSet<Object> Whitelist;
        public string Language;

        public ATOReport Report = new ATOReport();

        // 各阶段结果 / per-stage results
        public ATOCollectResult Collect;
        public ATOIslandResult Islands;
        public ATOPackResult Pack;

        /// <summary>(EN) Resolve effective settings for the current build target platform. (ZH) 按当前构建平台解析生效设置。</summary>
        public void ResolveForPlatform(ATOBuildPlatform platform)
        {
            // 从组件拷贝基础设置 / copy base settings from component
            Quality = Component.quality;
            Atlas = Component.atlas;
            Compression = Component.compression;
            Dedup = Component.dedup;
            PlatformOverrides = Component.platformOverrides;
            Whitelist = new HashSet<Object>(Component.whitelist ?? new List<Object>());
            Language = ATOLocalization.ResolveLanguage(Component);

            // 应用平台 override / apply platform override
            foreach (var ov in PlatformOverrides)
            {
                if (ov == null || !ov.enabled || ov.platform != platform) continue;
                if (ov.compression != null) Compression = ov.compression;
                if (ov.atlas != null) Atlas = ov.atlas;
            }
        }

        /// <summary>(EN) Detect current build target platform. (ZH) 检测当前构建目标平台。</summary>
        public static ATOBuildPlatform DetectPlatform()
        {
            switch (UnityEditor.EditorUserBuildSettings.activeBuildTarget)
            {
                case UnityEditor.BuildTarget.Android: return ATOBuildPlatform.Android;
                case UnityEditor.BuildTarget.iOS: return ATOBuildPlatform.iOS;
                default: return ATOBuildPlatform.PC;
            }
        }
    }
}

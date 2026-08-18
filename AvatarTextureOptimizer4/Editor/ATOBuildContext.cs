// Avatar Texture Optimizer (ATO)
// Per-avatar build state shared across the single pass.
// 单次烘焙中共享的 Avatar 级构建状态。

using System;
using System.Collections.Generic;
using UnityEngine;
using nadena.dev.ndmf;

namespace NetFosa.ATO
{
    /// <summary>
    /// The central state object for one avatar build. One instance per pass execution.
    /// 单次 Avatar 烘焙的中央状态对象，每次 pass 执行创建一份。
    /// </summary>
    public sealed class ATOBuildContext
    {
        public BuildContext ndmf;                 // NDMF build context / NDMF 构建上下文
        public GameObject avatarRoot;
        public ATOAvatarOptimizer component;      // the mounted component / 挂载的组件
        public ATOOptimizationProfile profile;    // effective profile (platform override resolved) / 生效配置（已解析平台覆写）
        public ATOPlatform platform;              // active build platform / 当前构建平台
        public ATOCompressionSettings compression;
        public ATOAdvancedSettings advanced;

        public ATOProgress progress;
        public ATOBuildReport report = new ATOBuildReport();
        public readonly ATOAnimationData anim = new ATOAnimationData();

        public readonly List<ATORendererRef> renderers = new List<ATORendererRef>();
        public readonly List<ATOUvSpace> uvSpaces = new List<ATOUvSpace>();
        public readonly List<ATOTextureRef> textures = new List<ATOTextureRef>();
        public readonly List<ATOAtlas> atlases = new List<ATOAtlas>();

        // Whitelist resolution. / 白名单解析。
        public readonly HashSet<UnityEngine.Object> whitelistObjects = new HashSet<UnityEngine.Object>();
        public readonly HashSet<Texture> whitelistTextures = new HashSet<Texture>();
        public readonly HashSet<Material> whitelistMaterials = new HashSet<Material>();
        public readonly HashSet<Mesh> whitelistMeshes = new HashSet<Mesh>();

        // Animation remap to apply at the very end. / 最后统一应用的动画改写映射。
        public readonly ATOAnimationRemap animRemap = new ATOAnimationRemap();

        // Base material clones (original -> clone) and their asset-path remap for
        // material-property animation curves that bind by path. / 基础材质克隆（原 -> 克隆）
        // 及其资产路径重映射（供按路径绑定的材质属性动画曲线使用）。
        public readonly Dictionary<Material, Material> baseMaterialClone = new Dictionary<Material, Material>();
        public readonly Dictionary<string, string> materialPathRemap = new Dictionary<string, string>();

        public ATOBuildContext(BuildContext ndmfContext, ATOAvatarOptimizer comp, ATOPlatform activePlatform)
        {
            ndmf = ndmfContext;
            avatarRoot = ndmfContext.AvatarRootObject;
            component = comp;
            platform = activePlatform;
            compression = comp.compression;
            advanced = comp.advanced;
            profile = ResolveProfile(comp, activePlatform);
        }

        /// <summary>
        /// Resolve the effective optimization profile for the active platform.
        /// 解析当前平台生效的优化配置。
        /// </summary>
        public static ATOOptimizationProfile ResolveProfile(ATOAvatarOptimizer comp, ATOPlatform activePlatform)
        {
            var general = comp.general.profile;
            ATOPlatformOverride ov = null;
            switch (activePlatform)
            {
                case ATOPlatform.PC: ov = comp.platform.pc; break;
                case ATOPlatform.Android: ov = comp.platform.android; break;
                case ATOPlatform.iOS: ov = comp.platform.ios; break;
            }
            if (ov != null && ov.enabled) return ov.profile;
            return general;
        }

        public void EnsureWhitelistResolved()
        {
            foreach (var o in component.whitelist.whitelist)
            {
                if (o == null) continue;
                whitelistObjects.Add(o);
                if (o is Texture t) whitelistTextures.Add(t);
                else if (o is Material m) whitelistMaterials.Add(m);
                else if (o is Mesh me) whitelistMeshes.Add(me);
            }
        }

        public bool IsWhitelisted(UnityEngine.Object o)
        {
            if (o == null) return false;
            if (whitelistObjects.Contains(o)) return true;
            if (o is Texture t) return whitelistTextures.Contains(t);
            if (o is Material m) return whitelistMaterials.Contains(m);
            if (o is Mesh me) return whitelistMeshes.Contains(me);
            return false;
        }
    }

    /// <summary>
    /// Final build report shown on the NDMF console. / 展示在 NDMF 控制台上的最终报告。
    /// </summary>
    public sealed class ATOBuildReport
    {
        public int rendererCount;
        public int materialSlotCount;
        public int textureCountBeforeDedup;
        public int textureCountAfterDedup;
        public int islandCount;
        public int islandCountSkipped;
        public int atlasCount;
        public int whitelistedTextureCount;
        public long originalBytes;
        public long optimizedBytes;
        public double totalTimeMs;
        public readonly List<ATOAtlas> atlases = new List<ATOAtlas>();
        public readonly List<ATOTextureRef> optimizedTextures = new List<ATOTextureRef>();
        public readonly List<string> warnings = new List<string>();
        public readonly List<ATOIslandQualityResult> islandQuality = new List<ATOIslandQualityResult>();

        public double SavedPercent => originalBytes <= 0 ? 0 : (1.0 - (double)optimizedBytes / originalBytes) * 100.0;
    }
}

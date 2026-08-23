// -----------------------------------------------------------------------------
// ATOBuildState.cs — everything one bake needs, shared across stages.
// ATOBuildState.cs — 单次烘焙的全部共享状态。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Resolved per-bake settings (platform-aware). / 解析后的本次烘焙设置。</summary>
    internal sealed class ATOSettings
    {
        public AvatarTextureOptimizer component;
        public ATOPlatform platform;
        public ATOQualityParams quality;
        public int minDensity, maxDensity;
        public bool generateAtlas;
        public int minPadding;
        public bool npotAtlases;
        public int maxAtlasSize;
        public ATOFormatSet formats;
        public ATOMipSettings mips;
        public bool dedupMaterials, dedupTextures;
    }

    /// <summary>GPU scratch pool for one bake; disposed in the pass's finally block
    /// (prevents leaks, keeps memory bounded). / 单次烘焙的 GPU 资源池；finally 中统一释放。</summary>
    internal sealed class ATOGpuPool : IDisposable
    {
        private readonly List<RenderTexture> _rts = new List<RenderTexture>();
        private readonly List<Texture2D> _temps = new List<Texture2D>();
        private readonly List<Material> _mats = new List<Material>();

        public RenderTexture GetRT(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGB32,
            bool linear = true)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, fmt,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            _rts.Add(rt);
            return rt;
        }

        public void Dispose()
        {
            foreach (var rt in _rts) if (rt != null) RenderTexture.ReleaseTemporary(rt);
            _rts.Clear();
            foreach (var t in _temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
            _temps.Clear();
            foreach (var m in _mats) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            _mats.Clear();
        }
    }

    /// <summary>Per-build state stored via context.GetState. / 通过 GetState 保存的构建态。</summary>
    internal sealed class ATOBuildState
    {
        public ATOSettings settings;
        public nadena.dev.ndmf.IAssetSaver assetSaver;
        public ATOReport report = new ATOReport();
        public ATOProgress progress;
        public ATOGpuPool gpu;

        /// <summary>Optimized texture per source TexInfo (atlas layers & whole-scaled).
        /// 每个源贴图对应的优化产物（图集层或整图缩放）。</summary>
        public readonly Dictionary<TexInfo, Texture2D> textureToOptimized =
            new Dictionary<TexInfo, Texture2D>();

        // Populated by collection / 采集结果
        public readonly List<RendererInfo> renderers = new List<RendererInfo>();
        public readonly Dictionary<Texture2D, TexInfo> texBySource = new Dictionary<Texture2D, TexInfo>();
        public readonly List<TexInfo> textures = new List<TexInfo>();
        public readonly List<UvGroupInfo> uvGroups = new List<UvGroupInfo>();
        public readonly Dictionary<Material, MaterialAnalysis> materialAnalysis =
            new Dictionary<Material, MaterialAnalysis>();
        public readonly List<PackUnit> packUnits = new List<PackUnit>();
        public readonly List<AtlasResult> atlases = new List<AtlasResult>();

        /// <summary>Materials→clones created during rebind / 重绑定期间创建的材质克隆映射。</summary>
        public readonly Dictionary<Material, Material> materialClones = new Dictionary<Material, Material>();

        /// <summary>Cloned meshes per renderer (renderers sharing a mesh may get different
        /// atlas layouts, so clones are per-renderer). / 每渲染器的网格克隆（共享网格的渲染器可能布局不同，故按渲染器克隆）。</summary>
        public readonly Dictionary<RendererInfo, Mesh> meshClones = new Dictionary<RendererInfo, Mesh>();

        /// <summary>AAO UV evacuation records for the bridge / AAO UV 搬移记录。</summary>
        public readonly List<(SkinnedMeshRenderer smr, int origCh, int savedCh)> uvEvacuations =
            new List<(SkinnedMeshRenderer, int, int)>();

        /// <summary>Collector→later-stage stash / 采集到后续阶段的暂存。</summary>
        public readonly ATOCollectorStash stash = new ATOCollectorStash();

        public TexInfo GetOrCreateTex(Texture2D tex)
        {
            if (tex == null) return null;
            if (texBySource.TryGetValue(tex, out var info)) return info;
            info = new TexInfo { source = tex };
            texBySource[tex] = info;
            textures.Add(info);
            return info;
        }
    }
}

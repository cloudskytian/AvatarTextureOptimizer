using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Shared working data for all stages. / 各阶段共享的工作数据。
    /// Built by the scan stage, consumed and extended by later stages. / 由扫描阶段构建，后续阶段消费与扩展。
    /// </summary>
    internal sealed class AtoContext
    {
        /// <summary>NDMF build context. / NDMF 构建上下文。</summary>
        public BuildContext Ndmf;

        /// <summary>ATO build state (settings, progress, report). / ATO 构建状态（设置、进度、报告）。</summary>
        public AtoBuildState State;

        /// <summary>Avatar root. / Avatar 根。</summary>
        public GameObject AvatarRoot;

        /// <summary>All processed renderers. / 全部被处理的渲染器。</summary>
        public List<AtoRendererData> Renderers = new List<AtoRendererData>();

        /// <summary>All texture records, keyed by texture. / 全部贴图记录。</summary>
        public Dictionary<Texture2D, AtoTextureRecord> Textures = new Dictionary<Texture2D, AtoTextureRecord>();

        /// <summary>All UV groups. / 全部 UV 组。</summary>
        public List<AtoUvGroup> UvGroups = new List<AtoUvGroup>();

        /// <summary>All type groups. / 全部类型组。</summary>
        public List<AtoTypeGroup> TypeGroups = new List<AtoTypeGroup>();

        /// <summary>Resolved whitelist objects (user whitelist + transitively referenced). / 解析后的白名单对象（用户白名单+传递引用）。</summary>
        public HashSet<Object> WhitelistObjects = new HashSet<Object>();

        /// <summary>Textures explicitly whitelisted or treated as whitelisted, with reasons. / 显式白名单或被视作白名单的贴图及原因。</summary>
        public Dictionary<Texture2D, string> WhitelistedTextures = new Dictionary<Texture2D, string>();

        /// <summary>Animation analysis results (from the animations stage). / 动画分析结果（来自动画阶段）。</summary>
        public AtoAnimationInfo Animations = new AtoAnimationInfo();

        /// <summary>Folder path (absolute) where generated assets are written. / 生成资产的输出文件夹（绝对路径）。</summary>
        public string OutputFolder;

        /// <summary>Whether TexTransTool is installed (its plugin was registered). / TexTransTool 是否已安装（其插件已注册）。</summary>
        public bool TttDetected;

        /// <summary>Global object remapper (dedupe/atlas/material replacements). / 全局对象重映射器（去重/图集/材质替换）。</summary>
        public AtoObjectRemapper Remapper = new AtoObjectRemapper();

        /// <summary>Shared raw pixel cache across stages. / 跨阶段共享的原始像素缓存。</summary>
        public AtoPixelCache PixelCache = new AtoPixelCache();

        /// <summary>
        /// Placement registry: each island's shared UV origin + rotation, fixed by the first type
        /// group that packs it and reused by all later groups (the shared-position invariant). /
        /// 放置注册表：每个岛的共享 UV 原点与旋转，由第一个装箱它的类型组确定，后续组复用（共享位置不变式）。
        /// </summary>
        public Dictionary<AtoIsland, AtoPlacedIsland> PlacedIslands = new Dictionary<AtoIsland, AtoPlacedIsland>();

        public AtoContext(BuildContext ndmf, AtoBuildState state)
        {
            Ndmf = ndmf;
            State = state;
            AvatarRoot = ndmf.AvatarRootObject;
        }

        /// <summary>
        /// Register a texture as whitelisted (treated as whitelist). / 将贴图登记为白名单（视作白名单处理）。
        /// </summary>
        public void WhitelistTexture(Texture2D texture, string reason)
        {
            if (texture == null) return;
            if (WhitelistedTextures.TryGetValue(texture, out var existing))
            {
                WhitelistedTextures[texture] = existing + "; " + reason;
            }
            else
            {
                WhitelistedTextures[texture] = reason;
            }
            if (Textures.TryGetValue(texture, out var record))
            {
                record.Whitelisted = true;
                record.WhitelistReason = WhitelistedTextures[texture];
            }
        }

        public bool IsWhitelisted(Texture2D texture) =>
            texture != null && WhitelistedTextures.ContainsKey(texture);

        /// <summary>Report a warning: console + NDMF entry + count. / 报告警告：控制台 + NDMF 条目 + 计数。</summary>
        public void Warn(string message)
        {
            State.WarningCount++;
            State.Note(message);
            AtoLog.Warn(message);
            var entry = new AtoConsoleEntry(message, ErrorSeverity.Warning);
            if (State.Component != null) entry.AddReference(ObjectRegistry.GetReference(State.Component));
            ErrorReport.ReportError(entry);
        }

        public void Error(string message)
        {
            State.ErrorCount++;
            State.Note(message);
            AtoLog.Error(message);
            var entry = new AtoConsoleEntry(message, ErrorSeverity.Error);
            if (State.Component != null) entry.AddReference(ObjectRegistry.GetReference(State.Component));
            ErrorReport.ReportError(entry);
        }
    }
}

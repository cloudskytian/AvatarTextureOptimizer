// ============================================================================
// ATO - per-build context (NDMF extension context)
// ATO - 单次构建上下文（NDMF 扩展上下文）
//
// Holds all mutable per-build state. It is activated for the duration of the
// pipeline pass and deactivated automatically by NDMF afterwards. Keeping
// state in an extension context (instead of statics) makes concurrent
// previews / unit tests safe.
// 保存全部按构建变化的可变状态。它在管线 Pass 期间被激活，之后由 NDMF 自动
// 停用。把状态放在扩展上下文中（而非静态变量）可保证并发预览/单元测试安全。
//
// Fatal validation problems (multiple components, missing descriptor) are
// reported by throwing ATOPipelineFatalException from the pass; NDMF routes
// it through the plugin's OnUnhandledException -> ErrorReport.ReportException,
// so the VRChat build fails with a visible, attributed error.
// 致命校验问题（多组件、缺少描述符）由 Pass 抛出 ATOPipelineFatalException 上
// 报；NDMF 将其经插件 OnUnhandledException -> ErrorReport.ReportException 汇
// 入错误报告，VRChat 构建会以可见且归属明确的错误失败。
// ============================================================================

#region

using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>Fatal, user-fixable configuration error. Aborts the ATO build
    /// and is surfaced in the NDMF error report (failing VRChat builds).
    /// 致命且可修复的配置错误。中止 ATO 构建，并显示在 NDMF 错误报告中（使
    /// VRChat 构建失败）。</summary>
    public class ATOPipelineFatalException : System.Exception
    {
        public ATOPipelineFatalException(string message) : base(message)
        {
        }
    }

    public class ATOContext : IExtensionContext
    {
        // ---- inputs 输入 -------------------------------------------------
        /// <summary>The ATO component driving this build. 驱动本次构建的组件。</summary>
        public ATOComponent Component;
        /// <summary>Logger for this build. 本次构建日志器。</summary>
        public ATOLog Log;
        /// <summary>Progress/cancel session. 进度/取消会话。</summary>
        public ATOBuildSession Session;

        // ---- whitelist 白名单 ---------------------------------------------
        /// <summary>Objects directly whitelisted by the user + contributors.
        /// 用户+贡献者直接白名单对象。</summary>
        public HashSet<Object> WhitelistObjects = new(ObjectIdentityEqualityComparer.Instance);
        /// <summary>Reasons per whitelisted texture (for the report).
        /// 每个白名单贴图的原因（报告用）。</summary>
        public Dictionary<Texture, string> WhitelistedTextures = new();

        // ---- analysis results (filled by later stages) --------------------
        /// <summary>All renderers considered (enabled or animation-enabled).
        /// 参与处理的全部渲染器（启用或被动画启用）。</summary>
        public List<Renderer> Renderers = new();
        /// <summary>Material instances (incl. animation-swapped).
        /// 材质实例（含动画切换的）。</summary>
        public List<Material> Materials = new();
        /// <summary>Optimizable texture instances after dedup.
        /// 去重后的可优化贴图实例。</summary>
        public List<Texture2D> Textures = new();

        // ---- analysis / animation 分析/动画 --------------------------------
        public net.fosa.AvatarTextureOptimizer.Editor.Analysis.ATOAnalysis Analysis;
        public net.fosa.AvatarTextureOptimizer.Editor.Analysis.ATOAnimationScan Anim;

        // ---- final report 最终报告 ----------------------------------------
        public int TotalIslands;
        public int TotalAtlases;
        public long OriginalTextureBytes;
        public long OptimizedTextureBytes;

        // ------------------------------------------------------------------
        /// <summary>Validates the component placement rules:
        /// 1) at most one ATOComponent under the avatar root,
        /// 2) its host object must carry a VRCAvatarDescriptor.
        /// 校验组件挂载规则：1) Avatar 下至多一个 ATOComponent；2) 挂载对象必
        /// 须带 VRCAvatarDescriptor。</summary>
        /// <param name="avatarRoot">Avatar root object. Avatar 根对象。</param>
        /// <param name="outComponent">The active component, when any.
        /// 存在的活动组件（如有）。</param>
        /// <exception cref="ATOPipelineFatalException">Violated a hard rule.
        /// 违反硬性规则时抛出。</exception>
        public ATOComponent Validate(GameObject avatarRoot)
        {
            var all = avatarRoot.GetComponentsInChildren<ATOComponent>(true);
            if (all.Length == 0)
            {
                Log.Info(ATOLogMask.Analysis,
                    "No ATO component found on this avatar - nothing to do. " +
                    "Avatar 上未找到 ATO 组件 - 无事可做。");
                return null;
            }

            if (all.Length > 1)
            {
                var names = string.Join(", ", System.Array.ConvertAll(
                    all, c => $"\"{c.gameObject.name}\""));
                throw new ATOPipelineFatalException(
                    "ATO: multiple ATO components found under the avatar (" + names +
                    "). Only one ATO component is allowed per avatar. Build aborted. " +
                    "在 Avatar 下发现多个 ATO 组件（" + names + "）。每个 Avatar 只允" +
                    "许挂载一个 ATO 组件，构建中止。");
            }

            var component = all[0];
            if (!component.Active)
            {
                Log.Info(ATOLogMask.Analysis,
                    "ATO component is disabled - nothing to do. ATO 组件已禁用 - 无事可做。");
                return null;
            }

            if (component.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
            {
                throw new ATOPipelineFatalException(
                    "ATO: the ATO component host object \"" + component.name +
                    "\" has no VRCAvatarDescriptor. Place the ATO component on the " +
                    "same object that carries the avatar descriptor. Build aborted. " +
                    "ATO 组件挂载对象 “" + component.name + "” 上没有 " +
                    "VRCAvatarDescriptor。请将其与 Avatar 描述符放在同一对象上。构" +
                    "建中止。");
            }

            return component;
        }
    }
}

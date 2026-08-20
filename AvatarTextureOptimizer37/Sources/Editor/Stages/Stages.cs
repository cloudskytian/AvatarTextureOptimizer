// ============================================================================
// ATO - pipeline stages (implementations arrive per milestone)
// ATO - 管线阶段（实现随里程碑逐步落地）
//
// Stage contract 阶段约定：
//  - every stage checks cancellation via ctx.Session.Check(...) at safe points;
//    每个阶段在安全点通过 ctx.Session.Check(...) 检查取消；
//  - stages 1..6 only compute an in-memory PLAN (no Unity-object mutation);
//    阶段 1..6 只计算内存中的 PLAN（不改动任何 Unity 对象）；
//  - stage 7 (Apply) is the ONLY stage that mutates Unity objects, applied
//    atomically per avatar; 阶段7（Apply）是唯一改动 Unity 对象的阶段，按 Avatar
//    原子应用；
//  - every stage releases its GPU/CPU resources in finally blocks.
//    每个阶段都在 finally 中释放其 GPU/CPU 资源。
// ============================================================================

#region

using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Stages
{
    /// <summary>Stage 1: scan renderers/materials/textures/animation, dedup
    /// textures, build UV-island / type-group / UV-group structures.
    /// 阶段1：扫描渲染器/材质/贴图/动画，贴图去重，构建 UV 岛/类型组/UV 组。</summary>
    public static class AnalysisStage
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            ctx.Session.Check("Analyze 分析");
            Analysis.AnalysisStageImpl.Execute(ctx, context);
        }
    }

    /// <summary>Stage 2: per-island quality-targeted UV scaling (binary
    /// search, uniform-then-biaxial, pure-color shortcut, shape-key/animated
    /// scale area clamping).
    /// 阶段2：岛级质量目标 UV 缩放（二分搜索、先均匀后双轴、纯色短路、形态键/
    /// 动画缩放面积钳制）。</summary>
    public static class QualityStage
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            ctx.Session.Check("Quality 质量缩放");
            Quality.QualityStageImpl.Execute(ctx, context);
        }
    }

    /// <summary>Stage 3: rasterize islands (Burst, 4px), BLF packing with the
    /// candidate atlas pool (POT/NPOT), texture-group + UV-group constraints.
    /// 阶段3：岛光栅化（Burst，4px）、候选图集池 BLF 装箱、贴图组+UV 组约束。</summary>
    public static class PackStage
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            ctx.Session.Check("Pack 装箱");
            Packing.PackStageImpl.Execute(ctx, context);
        }
    }

    /// <summary>Stage 4: compose atlas pages (GPU pull-push edges), remap mesh
    /// UVs, update material + animation texture references.
    /// 阶段4：合成图集页（GPU 边缘 pull-push）、重映射网格 UV、更新材质+动画
    /// 贴图引用。</summary>
    public static class AtlasStage
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            ctx.Session.Check("Atlas 图集合成");
            Atlas.AtlasComposer.Compose(ctx);
            Atlas.FinalTextureResolver.Resolve(ctx);
        }
    }

    /// <summary>Stage 5: texture import settings (safe compression formats,
    /// mipmap+mipstreaming binding, platform overrides, NPOT filtering) and
    /// material texture-slot-only updates (no other shader parameters are
    /// ever touched).
    /// 阶段5：纹理导入设置（安全压缩格式、Mipmap+MipStreaming 绑定、平台
    /// Override、NPOT 过滤）与仅贴图槽位的材质更新（绝不改动其他着色器参
    /// 数）。</summary>
    public static class ImportStage
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            ctx.Session.Check("Import 导入参数");
            Import.ImportStageImpl.Execute(ctx, context);
        }
    }

    /// <summary>Stage 6: content+parameter dedup of materials and
    /// textures/atlas pages; opaque sub-mesh material-slot merge with
    /// animation reference remapping.
    /// 阶段6：材质与贴图/图集页的内容+参数去重；不透明子网格材质槽合并（动
    /// 画引用重映射）。</summary>
    public static class DedupStage
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            ctx.Session.Check("Dedup 去重");
            Dedup.MaterialDedup.Plan(ctx);
        }
    }

    /// <summary>Stage 7: the single atomic application of all planned
    /// mutations to Unity objects (meshes, materials, textures, animation
    /// references) + ATO component self-removal.
    /// 阶段7：将所有计划改动原子地应用到 Unity 对象（网格、材质、贴图、动画
    /// 引用）+ ATO 组件自移除。</summary>
    public static class ApplyStage
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            ctx.Session.Check("Apply 应用");
            Apply.ApplyStageImpl.Execute(ctx, context);
        }
    }

    /// <summary>Stage 8: console report (summary default, details collapsed)
    /// + final [ATO] log dump.
    /// 阶段8：控制台报告（默认摘要，细节折叠）+ 最终 [ATO] 日志转储。</summary>
    public static class ReportStage
    {
        public static void Execute(ATOContext ctx, BuildContext context, bool verbose)
        {
            ctx.Session.Check("Report 报告");
            Report.ReportStageImpl.Execute(ctx, context, verbose);
        }
    }
}

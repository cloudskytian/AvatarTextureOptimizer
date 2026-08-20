#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Generate ATO i18n JSON files from a single key table (keeps en/zh-cn in sync).
用法: python3 tools/gen_i18n.py
输出: Editor/Resources/ATO/i18n/{en,zh-cn}.json (JsonUtility-compatible entry list)
"""
import json, os, sys

# key: (en, zh-cn) — every entry MUST be a 2-tuple
KEYS = {
    # ---- stages ----
    "stage.scan": ("Scan avatar (renderers/materials/textures)", "扫描 Avatar（渲染器/材质/贴图）"),
    "stage.animations": ("Analyze animations & animators", "分析动画与 Animator"),
    "stage.dedupeTextures": ("Deduplicate identical textures", "去重相同贴图"),
    "stage.islands": ("Extract UV islands & groups", "提取 UV 岛与分组"),
    "stage.quality": ("Compute target-quality scaling", "计算目标质量缩放"),
    "stage.packing": ("Pack islands into atlases", "装箱生成图集"),
    "stage.compose": ("Compose atlas textures", "图集合成"),
    "stage.meshes": ("Rewrite meshes & UVs", "重写网格与 UV"),
    "stage.references": ("Update material/texture references", "更新材质/贴图引用"),
    "stage.import": ("Apply texture import settings", "应用贴图导入参数"),
    "stage.dedupeAssets": ("Deduplicate materials/atlases & merge slots", "去重材质/图集并合并材质槽"),
    "stage.removeSelf": ("Remove ATO components", "移除 ATO 组件"),
    "stage.report": ("Build final report", "生成最终报告"),
    # ---- report ----
    "report.title": ("ATO: Avatar Texture Optimizer", "ATO：Avatar 贴图优化器"),
    "report.summary": ("Summary: {0} textures, {1} UV groups, {2} islands, {3} atlas(es). Texture bytes: {4} -> {5} ({6}%). Total time: {7} ms.",
                       "摘要：{0} 张贴图，{1} 个 UV 组，{2} 个岛，{3} 张图集。贴图体积：{4} -> {5}（{6}%）。总耗时：{7} ms。"),
    "report.warningCount": ("{0} warning(s), {1} error(s).", "{0} 条警告，{1} 条错误。"),
    "report.details": ("Details (per atlas):", "细节（按图集）："),
    "report.atlasLine": ("[{0}] {1}: {2} source texture(s), {3} island(s), {4}x{5}, utilization {6}%, saved {7}%.",
                         "[{0}] {1}：{2} 张来源贴图，{3} 个岛，{4}x{5}，利用率 {6}%，相对原贴图节省 {7}%。"),
    "report.textureLine": ("Texture {0}: {1} -> {2} ({3}%), reason: {4}.", "贴图 {0}：{1} -> {2}（{3}%），原因：{4}。"),
    # ---- warnings ----
    "warn.whitelisted": ("Texture {0} is whitelisted (or treated as whitelisted): all optimization skipped.",
                         "贴图 {0} 处于白名单（或被视作白名单）：跳过所有优化。"),
    "warn.stAnimated": ("Texture property {0}.{1} has animated ST transform: treated as whitelist.",
                        "贴图属性 {0}.{1} 存在动画 ST 变换：视作白名单处理。"),
    "warn.stNonIdentity": ("Texture property {0}.{1} has non-identity ST scale/offset: treated as whitelist.",
                           "贴图属性 {0}.{1} 的 ST 缩放/平移非默认值：视作白名单处理。"),
    "warn.uvWrapCrossing": ("UV islands on {0} cross wrap seams or repeat: renderer treated as whitelist.",
                            "{0} 的 UV 岛跨 wrap 缝或依赖 repeat：渲染器视作白名单处理。"),
    "warn.unclassifiedShader": ("Shader property {0}.{1} could not be classified safely: texture treated as whitelist.",
                                "着色器属性 {0}.{1} 无法安全分类：贴图视作白名单处理。"),
    "warn.aaoEvacuationFailed": ("No free UV channel to evacuate for {0}: renderer treated as whitelist.",
                                 "{0} 无可用疏散 UV 通道：渲染器视作白名单处理。"),
    "warn.tooLargeForAtlas": ("UV group {0} does not fit the largest atlas ({1}px): atlas generation skipped, scaled texture kept.",
                              "UV 组 {0} 无法装入最大图集（{1}px）：跳过图集化，保留缩放后的整图。"),
    "warn.densityExceeded": ("Island {0} keeps density above max ({1} px/m): quality takes priority, consider raising max density.",
                             "岛 {0} 的密度仍高于上限（{1} px/m）：质量优先，可考虑提高密度上限。"),
    "warn.grayscaleFallback": ("Grayscale atlas {0} contains multi-channel data: saved as {1} instead of single-channel format.",
                               "灰度图集 {0} 含多通道数据：以 {1} 保存（非单通道格式）。"),
    "warn.npotFormatExcluded": ("NPOT atlases: format {0} excluded for platform {1}.", "NPOT 图集：平台 {1} 不支持格式 {0}，已剔除。"),
    "warn.tttDetected": ("TexTransTool detected: ATO runs after TTT (its atlases are re-processed).",
                         "检测到 TexTransTool：ATO 将在 TTT 之后运行（会对其图集再处理）。"),
    "warn.blendshapeUvUnsupported": ("Blend shape UV deltas on {0} cannot be read; ignored (positions only).",
                                     "{0} 的形态键 UV 增量无法读取；已忽略（仅处理顶点位置）。"),
    "warn.animatedMaterialSlot": ("Material slot {0} on {1} is animated with independent materials: slot merge disabled.",
                                  "{1} 的材质槽 {0} 被动画独立切换：禁用该槽合并。"),
    # ---- errors ----
    "error.multipleRoots": ("Multiple AtoAvatarRoot components on avatar {0}: only one is allowed. Bake aborted.",
                            "Avatar {0} 上存在多个 AtoAvatarRoot 组件：只允许一个。烘焙已中止。"),
    "error.noVrcDescriptor": ("AtoAvatarRoot on {0} has no VRCAvatarDescriptor: bake aborted.",
                              "{0} 上的 AtoAvatarRoot 所在对象没有 VRCAvatarDescriptor：烘焙已中止。"),
    "error.cancelled": ("ATO bake cancelled by user.", "ATO 烘焙已被用户取消。"),
    "error.internal": ("ATO internal error: {0}", "ATO 内部错误：{0}"),
    # ---- UI / inspector ----
    "ui.atlasSection": ("Atlas", "图集"),
    "ui.qualitySection": ("Target Quality", "目标质量"),
    "ui.densitySection": ("Pixel Density", "像素密度"),
    "ui.mipSection": ("Mipmaps & Streaming", "Mipmap 与 Streaming"),
    "ui.platformSection": ("Platform Overrides", "平台覆盖"),
    "ui.whitelistSection": ("Whitelist", "白名单"),
    "ui.languageSection": ("Localization", "本地化"),
    "ui.logSection": ("Logging", "日志"),
    "ui.advanced": ("Advanced Options", "高级选项"),
    "ui.presetUltra": ("Ultra", "极高"),
    "ui.presetHigh": ("High", "高"),
    "ui.presetMedium": ("Medium", "中"),
    "ui.presetLow": ("Low", "低"),
    "ui.presetCustom": ("Custom (near lossless)", "自定义（近无损）"),
    "ui.paramMsSsim": ("MS-SSIM", "MS-SSIM"),
    "ui.paramDeltaE": ("ΔE00 mean", "ΔE00 均值"),
    "ui.paramIou": ("Cutout IoU", "Cutout 轮廓 IoU"),
    "ui.paramAlphaRmse": ("Blend α RMSE", "Blend α RMSE"),
    "ui.paramNormalMean": ("Normal angle mean", "法线角度均值"),
    "ui.paramNormalP95": ("Normal angle p95", "法线角度 p95"),
    "ui.paramGrayRmse": ("Gray RMSE", "灰度 RMSE"),
    "ui.padding": ("Min padding", "最小 padding"),
    "ui.npot": ("Experimental NPOT", "实验性 NPOT"),
    "ui.generateAtlases": ("Generate atlases", "生成图集"),
    "ui.mipstreaming": ("Mipmaps + MipStreaming", "Mipmap + MipStreaming"),
    "ui.whitelist": ("Whitelist objects", "白名单对象"),
    "ui.language": ("Language", "语言"),
    "ui.languageAuto": ("Auto (NDMF)", "Auto（跟随 NDMF）"),
    "ui.logLevel": ("Log level", "日志级别"),
    "ui.logSummary": ("Summary", "摘要"),
    "ui.logNormal": ("Normal", "常规"),
    "ui.logVerbose": ("Verbose", "详细"),
    "ui.enablePc": ("PC override", "PC 覆盖"),
    "ui.enableAndroid": ("Android override", "Android 覆盖"),
    "ui.enableIos": ("iOS override", "iOS 覆盖"),
    "ui.compression": ("Compression", "压缩"),
    "ui.compressionOpaque": ("Opaque", "不透明"),
    "ui.compressionTransparent": ("Transparent", "透明"),
    "ui.compressionNormal": ("Normal map", "法线贴图"),
    "ui.compressionGray": ("Grayscale", "灰度"),
    # ---- common ----
    "common.enabled": ("Enabled", "启用"),
    "common.disabled": ("Disabled", "禁用"),
    "common.unknown": ("Unknown", "未知"),
    "common.cancelling": ("Cancelling...", "取消中..."),
}


def render(code, entries):
    strings = [{"key": k, "value": entries[k]} for k in sorted(entries)]
    return json.dumps({"code": code, "strings": strings}, ensure_ascii=False, indent=2)


def main():
    # Validate: every entry must be a 2-tuple of non-empty strings
    for k, v in KEYS.items():
        if not isinstance(v, tuple) or len(v) != 2 or not all(isinstance(x, str) and x for x in v):
            print("BAD ENTRY:", k, repr(v))
            sys.exit(1)
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Editor", "Resources", "ATO", "i18n")
    os.makedirs(out_dir, exist_ok=True)
    en = {k: v[0] for k, v in KEYS.items()}
    zh = {k: v[1] for k, v in KEYS.items()}
    with open(os.path.join(out_dir, "en.json"), "w", encoding="utf-8") as f:
        f.write(render("en", en))
    with open(os.path.join(out_dir, "zh-cn.json"), "w", encoding="utf-8") as f:
        f.write(render("zh-cn", zh))
    print("generated", len(KEYS), "keys ->", out_dir)


if __name__ == "__main__":
    main()

# AvatarTextureOptimizer — AgentTeam 记忆

> 本文件是本项目的唯一记忆载体。上下文丢失时以本文件为准。

## 项目身份

- 名称：AvatarTextureOptimizer
- 包名：`net.fosa.avatar-texture-optimizer`
- 命名空间：`FOSA.AvatarTextureOptimizer` / `FOSA.AvatarTextureOptimizer.Editor`
- 形态：开源 NDMF 工具（不是完整 Unity 工程）。把本目录放到 `Packages/` 即可。
- 阶段：0.1.0 开发版。配置字段无迁移。
- 日志前缀：`[ATO]`，组件上 `debugLog` 可关。

## AgentTeam 结论

### Coder 共识
- 单一 `ATOOptimizePass` 编排内部阶段，便于进度条与取消。
- 分析只读；回写统一克隆持久资产（网格 / 材质 / clip）。
- 质量评估：Blit 解码 + Burst SSIM + CPU CIEDE2000 / 法线角 / IoU。无 Compute Shader 也能跑。
- 图集先写 PNG 再走 `TextureImporter`，才能做平台 override / MipStreaming。
- AAO 只用反射，asmdef 不引用 AAO。
- 不启用 `AnimatorServicesContext`（MA 已克隆控制器；VirtualClip 不是必要依赖）。

### 对用户设计的修正（已落地）
1. 法线岛旋转 90°：像素旋转 + RG swizzle；网格 tangent 不重算。
2. 副图集：同一套 layout，UV 归一化，分辨率可以更低。
3. Padding = `max(ceil(长边/128), 用户最小 padding)`。
4. 动画备选贴图共享 layout、各自一张图集；含备选的 UV 组单独成队。
5. 额外弱约束 `AfterPlugin("net.rs64.tex-trans-tool")`。
6. 质量=1（Lossless）跳过缩放。

### Reviewer 共识（已处理）
- 禁止改持久 AnimationClip：`ATOApply.EnsureTempClip` 先克隆再改，并回写 AnimatorController / Override / BlendTree。
- `ValidateMount` 在无 VRC define 时也存在，避免编辑器编译失败。
- 白名单字段显式 `UnityEngine.Object`。
- 透明/多通道灰度/NPOT+PVRTC 在导入阶段 fallback + NDMF warning。

### QA 共识（独立通读后同时通过）
- 流水线完整：Scan → Shader → Anim → Whitelist → DedupTex → Islands → Quality → Groups → Pack/Whole → Apply → DedupMat → Import → AAO → Cleanup → Report。
- 未安装 AAO / lilToon / MA / TTT 可编译（弱约束 + 反射 + 动态着色器分析）。
- 取消抛 `ATOCanceledException`，清进度条、释放 RT/解码缓存，磁盘 `Assets/ATO_Generated/` 保留。
- i18n：`en-US.json` + `zh-Hans.json`，Auto 跟 NDMF。
- 已知限制见下方，不阻塞 0.1.0 交付。

## 已知限制（0.1.0）

- 本环境没有 Unity，无法在这里对模型实机烘焙。用户同步进工程后才能验证观感。
- BLF 全扫描在 8192 大图 + 大量岛时会慢。后续可加粗网格加速。
- 解码缓存按烘焙生命周期持有，超大 Avatar 可能吃内存；结束后会 `Dispose`。
- 副类型整图降分辨率的“低于主色则缩”只预留了设计，0.1.0 先共用 layout 分辨率。
- 暂无 NDMF 预览。
- GPU 质量评估目前是 Blit 解码；SSIM/ΔE 在 Burst/CPU。后续可把 ΔE 也搬到 Hidden shader。

## 第三方 API（已读源码，禁止猜测）

| 库 | 版本 | 使用方式 |
|---|---|---|
| NDMF 1.14.4 | `Plugin<T>` / `Pass<T>` / `ExportsPlugin` / `BuildPhase.Optimizing` / `AfterPlugin` / `BeforePlugin` / `BuildContext` / `AssetSaver.SaveAsset` / `ErrorReport` / `IError` / `INDMFEditorOnly` / `ObjectRegistry` / `Localizer` / `LanguagePrefs` |
| MA 1.18.2 | `QualifiedName = "nadena.dev.modular-avatar"` |
| AAO 1.9.17 | `QualifiedName = "com.anatawa12.avatar-optimizer"`；`UVUsageCompabilityAPI` 在 InitializeOnLoad 注册 |
| lilToon 2.3.4 | 属性/UVMode/`_TransparentMode`/`_Cutoff`/`_UseBumpMap` 等按源码 |
| VRC SDK 3.10.4 | `VRCAvatarDescriptor` 层 + 子级 Animator |

## 质量挡位

| 挡位 | MS-SSIM | ΔE00 | 行为 |
|---|---|---|---|
| Lossless | 1 | 0 | 跳过缩放 |
| Ultra | 0.99 | 1.0 | 评估 |
| **High 默认** | 0.97 | 2.0 | 评估 |
| Medium | 0.94 | 3.5 | 评估 |
| Low | 0.90 | 6.0 | 评估 |
| Custom | 默认全 1 | 独立存储，不被覆盖 |

## 进度

- [x] 通读依赖源码
- [x] 可行性与设计修正
- [x] 包骨架 / Runtime 组件 / 扩展 API
- [x] Editor 基础设施（日志/i18n/进度/插件）
- [x] 分析（网格/动画/着色器/白名单/去重/岛）
- [x] 质量评估（Burst SSIM + CIEDE2000 + 法线 + alpha）
- [x] 图集（4px 光栅 / BLF / 合成 / 渗色）
- [x] 回写（网格/材质/动画/槽合并/导入/AAO）
- [x] Inspector + en/zh
- [x] Reviewer 审查与修复
- [x] QA 通读
- [x] README.md
- [x] git 提交
- [x] zip 交付

## 同步进 Unity 后怎么验

1. 把本目录放到 `Packages/net.fosa.avatar-texture-optimizer`。
2. 等 Burst / NDMF / VRC 编译通过。
3. 在带 `VRCAvatarDescriptor` 的根上 Add Component。
4. NDMF Bake 一个简单 Avatar（一张主色 + 一张法线）。
5. 看 Console `[ATO]` 与 NDMF 报告：岛数、图集尺寸、利用率。
6. 对比优化前后观感；有问题先读 `[ATO]` 日志再下结论。

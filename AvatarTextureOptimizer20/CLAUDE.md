# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是本项目的唯一记忆载体。所有计划、进度、注意事项只记录在这里。

## 项目概述

- **名称**: AvatarTextureOptimizer (ATO)
- **包名**: `net.fosa.avatar-texture-optimizer`
- **目标**: 世界最好的 VRChat 贴图优化工具。NDMF 插件，MA 之后、AAO 之前运行。
- **核心思路**: 网格UV→贴图映射（材质无关，同贴图复用）→ 逐 UV 岛目标质量缩放 →
  岛形光栅装箱合并图集 → 重写 UV 与引用。

## 依赖版本基线（源码已通读取证的 API）

- ndmf 1.14.4:
  - `Plugin<T>` / `InPhase(BuildPhase.Optimizing).AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`
  - `WithRequiredExtension(typeof(AnimatorServicesContext), seq => seq.Run("name", InlinePass))`
  - `AnimatorServicesContext`（ns `nadena.dev.ndmf.animator`）: `ControllerContext.GetAllControllers()`,
    `AnimationIndex.RewriteObjectCurves(Func<Object,Object>)`, `VirtualClip.GetFloat/ObjectCurveBindings()`
  - `ObjectRegistry.RegisterReplacedObject(old,new)`; `IAssetSaver.SaveAsset(obj)`
  - `Localizer(string, Func<List<(string,Func<string,string>)>>)`; `LanguagePrefs.Language`
  - `ErrorReport.ReportError(Localizer, ErrorSeverity, key, args)`; `SimpleError` 用 `{0}` 占位符替换
- AAO 1.9.17: `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`
  （`IsTexCoordUsed(SMR,ch)` / `RegisterTexCoordEvacuation(SMR,orig,saved)`，仅 SMR）→ 反射桥接（Compat/AaoCompat.cs）
- lilToon 2.3.4: 属性语义表参考 AAO `ShaderInformation.Liltoon.cs`（853行），版本值≤45；
  `lilToon.lilConstants.currentVersionValue` 反射检测未来版本。

## 架构（Editor/ 下）

| 文件 | 职责 |
|---|---|
| `AtoPlugin.cs` | NDMF 插件声明（Optimizing, MA后AAO前, AnimatorServicesContext） |
| `Core/AtoProcessor.cs` | 编排：校验→9阶段→报告；取消(AtoCancelledException)→中止构建；finally 释放全部资源并移除组件 |
| `Core/AtoModel.cs` | 数据模型：TexInfo/TexUse/MappingKey(mesh,uvCh)/Island/PackUnit/AtlasResult/AtoContext |
| `Core/TexturePixels.cs` | GPU 解码像素缓存（LRU 1.5GB 预算，法线 UnpackNormal 解码） |
| `Core/Raster.cs` | BitGrid + 保守三角形光栅化（含可移植 popcount，**勿用 System.Numerics.BitOperations，Unity 无**） |
| `Pipeline/ScanStage.cs` | 白名单展开(CollectDependencies)→渲染器扫描(含动画启用)→动画分析(材质切换/缩放/ST/Cutoff)→源贴图去重(像素+导入设置)→UV-贴图映射图 |
| `Pipeline/ShaderSemantics.cs` | lilToon + 标准关键字 Provider；未知属性/着色器→白名单+warning；可注册第三方 Provider |
| `Pipeline/IslandStage.cs` | UV岛并查集检测、越界平移归一(跨缝→白名单+warning)、重叠岛合并(>15%)、真实面积(形态键0/100最大值×动画最大缩放²) |
| `Quality/Metrics.cs` | Burst: MS-SSIM(5尺度Wang权重, <176px单尺度, <11px跳过)/CIEDE2000 p95/alpha IoU·RMSE/法线角度p95/灰度逐通道RMSE |
| `Quality/Resampler.cs` | GPU 线性空间重采样，透明预乘→双线性→反预乘round-trip |
| `Quality/QualityStage.cs` | 逐岛二分(先均匀7轮后双轴各5轮, 1/128粒度)、纯色短路min(4,短边)、密度钳制(px/m)、UV组木桶效应 |
| `Pipeline/PackStage.cs` | PackUnit(共享映射并查集=原子装箱单元)、类型组队列、候选池(POT/实验性NPOT64步进)、Burst BLF位掩码扫描、90°旋转(**法线组禁旋转**)、padding=max(maxEdge/128,4,用户值) |
| `Pipeline/BakeStage.cs` | 物理图集分层(动画切换变体分层同布局)、类型图集整体缩放、GL y-up 绘制(与UV重写共用 IslandToAtlasPx)、pull-push外扩(alpha空白区保持0)、法线 A=X 编码、整图缩放路径 |
| `Pipeline/RewriteStage.cs` | 网格克隆+UV重写、AAO UV疏散、材质克隆（只改贴图引用！）、动画 RewriteObjectCurves |
| `Pipeline/FinalizeStage.cs` | 分类压缩格式(平台兜底：移动强制ASTC、BC4多通道拒绝+warning)、MipStreaming(SerializedObject 写 m_StreamingMipmaps，与Mipmap绑定)、输出贴图/材质去重、材质槽合并(仅不透明+无动画干预) |
| `Report/AtoReport.cs` | NDMF 控制台报告（标题总体+描述折叠细节：耗时/来源/岛数/利用率/优化量） |
| `Api/AtoExtensions.cs` | 第三方扩展：自定义阶段(Order 100..900间插入)、语义Provider、前后事件 |
| `Localization/AtoL10n.cs` | 扫描全项目 `ATO_i18n_<code>.json`；Auto跟随NDMF；缺失回退en-US |
| `Inspector/AtoEditor.cs` | 检查器：挡位联动、Custom不被覆盖、平台勾选才显示、默认折叠 |
| `Resources/ATO_*.shader` | Decode(法线解码)/Resample(预乘链)/PullPush(pull-push+final combine) |

## 关键不变量（改代码前必读）

1. **同一UV在所有图集位置一致**: PackUnit=共享映射的贴图并查集，整单元原子装箱；
   动画切换变体在 BakeStage.AssignLayers 分到不同物理图集但共用同一布局。
2. **坐标约定全程 y-up**（UV空间、BitGrid、GL.LoadPixelMatrix(0,W,0,H)、ReadPixels 一致）。
   旋转映射唯一出处 `BakeStage.IslandToAtlasPx`: local(x,y)→PlacePos+(y, RasterSize.x−x)，
   烘焙 quad texcoord 与之严格对应。改一处必须同步改另一处。
3. **绝不修改材质除贴图引用外的任何参数**；材质/网格修改必须先 Instantiate 克隆 +
   `ObjectRegistry.RegisterReplacedObject`。
4. 白名单/未知情况一律安全回退（保持原样+Info级warning），宁可不优化不可优化错。
5. 含白名单贴图的映射整体退化为整图缩放（UV不能改）。
6. 法线：重采样=解码→重采样→重归一化→编码(A=X)；装箱不旋转；切线数据绝不重算。
7. mcs 只支持 C#7，本项目用了 C#8/9（switch表达式/target-typed new/using var）——
   仅 Unity 2022.3 可编译，别用 mcs 判断对错。

## 当前进度（v0.1.0 初版全功能实现完毕）

- [x] 全部 9 阶段流水线 + 插件 + UI + i18n(en-US/zh-Hans) + 扩展API + AAO桥 + 报告
- [x] 静态审查 2 轮（修复：popcount 可移植性、GL pass错误、y轴方向、旋转一致性、
      材质切换扫描冗余变量、pull-push final combine alpha pass）
- [ ] **未做**: Unity 实机烘焙验证（沙箱无 Unity）——首要验证点见下

## 实机验证清单（用户同步工程后优先检查）

1. 图集岛方向是否正确（y翻转/旋转岛）→ 若翻转，改 BakeStage.DrawIsland texcoord 或 IslandToAtlasPx。
2. `Graphics.Blit(tex, rt, scale, offset)` 裁剪是否符合预期（sRGB→linear RT 自动转换）。
3. Burst 编译 MetricsJob/BlfScanJob 是否通过（NativeArray Temp 分配在 Burst 中 OK？histogram 用 Allocator.Temp）。
4. `SerializedObject(m_StreamingMipmaps)` 在运行时创建的 Texture2D 上是否生效。
5. AnimatorServicesContext 在 VRChat 平台的控制器覆盖是否全（FX/Gesture等由 ndmf vrchat 绑定提供）。
6. 性能：大型 Avatar 上 QualityStage 的 GPU 读回次数（必要时加缩放结果缓存/异步读回）。
7. NPOT 勾选时 CompressTexture 对非4倍数尺寸会跳过压缩（SnapSize 已按64步进，天然4对齐）。

## 已知取舍（记录在案）

- 岛 srcRect 按各贴图分辨率独立取整，不同分辨率贴图间可能有 ≤1px 对齐差（dilation 掩盖）。
- 类型图集整体缩放的 RoleScale 采用保守估计。
- 槽合并仅在"无任何动画引用该渲染器材质槽"时执行（最安全条件）。
- 取消时保留 NDMF 已写盘的临时资产（不主动删除生成目录），内存/GPU在 finally 全部释放。

# CLAUDE.md — AvatarTextureOptimizer (ATO) 项目记忆

> 本文件是本项目**唯一**的记忆载体。任何关于本项目的决策、进度、注意事项都只记录在这里。
> This file is the single source of project memory. Everything about this project is recorded here only.

---

## 1. 项目概要

- 项目名：**AvatarTextureOptimizer**
- 包名：`net.fosa.avatar-texture-optimizer`
- 目标：面向 VRChat Avatar 的开源 NDMF 贴图优化工具。分析网格 UV ↔ 贴图映射，按感知质量目标缩放 UV 岛，
  剔除未使用区域，重排并合并为一个/多个图集，同时优化导入参数。**只改贴图、UV、网格，绝不改材质的其他参数。**
- Unity：2022.3；C# 9；依赖 NDMF 1.14.4、VRChat SDK 3.10.4、Burst、Mathematics。
- 交付形式：VPM 包目录（Runtime/ + Editor/ + package.json），已打包 zip。

## 2. AgentTeam 分工与流程（本次执行记录）

- **Coder ×3**：先就每个模块的接口/数据流达成共识再落笔（模型层 → 分析层 → 算法层 → 输出层 → 编排层）。
- **Reviewer ×3**：每个模块写完立即复核，本轮共打回并修复了 8 处（见 §6 "Review 修复记录"）。
- **QA ×3**：整体完成后各自从头通读全部代码，逐条对照需求清单验收（见 §7 验收矩阵）。
- 校验手段：沙箱内无 Unity，故使用 .NET 8 + Roslyn 做**全量语法解析**（`/home/user/.cache/synchk`，
  `dotnet run --no-build -- <dir>`）。语义/API 正确性通过**通读第三方库源码**保证（见 §3）。

## 3. 已通读并据以实现的第三方源码（禁止猜测 API）

| 库 | 版本 | 本项目用到的确切 API / 事实 |
|---|---|---|
| NDMF | 1.14.4 | `Plugin<T>` / `Pass<T>` / `BuildPhase.{Resolving,Optimizing}` / `Sequence.WithRequiredExtension` / `DeclaringPass.BeforePlugin(string)`；`BuildContext.{AvatarRootObject,AssetSaver,Extension<T>,IsTemporaryAsset}`；`IAssetSaver.SaveAsset`；`ErrorReport.ReportError` + `SimpleError`（`ErrorSeverity.{Information,NonFatal,Error}`）；`Localizer(defaultLang, Func<List<(string,Func<string,string>)>>)` + `LanguagePrefs.Language`；`AnimatorServicesContext.{ControllerContext,AnimationIndex}`、`VirtualControllerContext.GetAllControllers()`、`VirtualNode.AllReachableNodes()`、`VirtualClip.{GetFloatCurveBindings,GetObjectCurveBindings,GetFloatCurve,GetObjectCurve}`、`AnimationIndex.RewriteObjectCurves` |
| AAO | 1.9.17 | 插件 QualifiedName = `com.anatawa12.avatar-optimizer`，主流程在 `BuildPhase.Optimizing`；`Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.{IsTexCoordUsed,RegisterTexCoordEvacuation}`（**仅支持 SkinnedMeshRenderer**）；`Utils.SetStreamingMipMapSettings` 证实生成贴图要用 `SerializedObject` 改 `m_StreamingMipmaps` / `m_StreamingMipmapsPriority` |
| Modular Avatar | 1.18.2 | QualifiedName = `nadena.dev.modular-avatar`，全部流程在 `Resolving`/`Transforming`。→ 我们放在 `Optimizing` 天然在 MA 之后 |
| lilToon | 2.3.4 | 属性表约定：`_MainTex`/`[MainTexture]`、`_MainTex_ScrollRotate`、`<prop>_UVMode`（0..3=UV0..3，≥4=MatCap/Rim 非 UV 采样）、`<prop>IsDecal`、`<prop>Angle`、`[Normal] _BumpMap/_Bump2ndMap/...`、`[NoScaleOffset]` 蒙版、`_Cutoff`、`_ShiftBackfaceUV`、`_UDIMDiscardCompile`；渲染模式写在着色器名里（cutout/onetrans/trans/fur/gem/refraction） |
| VRChat SDK | 3.10.4 | Mipmap 开启时必须开 Streaming（SDK 校验项）；`VRCAvatarDescriptor` 用于组件挂载校验 |
| avatar-compressor / LLC | — | 参考其 `m_StreamingMipmaps` 写法与压缩格式取舍，未产生代码依赖 |

## 4. 代码结构（全部文件）

```
package.json
Runtime/
  net.fosa.avatar-texture-optimizer.runtime.asmdef
  ATOSettings.cs                 # 设置模型：挡位/质量参数/平台 profile/白名单/开关
  AvatarTextureOptimizer.cs      # 用户组件（IEditorOnly，DisallowMultipleComponent）
Editor/
  net.fosa.avatar-texture-optimizer.editor.asmdef
  Core/ATOLog.cs                 # [ATO] 日志 + 每步计时 + 报告文本
  Core/ATOErrors.cs              # SimpleError 封装 + Reporter
  Core/ATOProgress.cs            # 可取消进度（ATOCancelledException）
  Core/ATOModel.cs               # UVKey / TextureInfo / TextureClass / Island / UVGroup / Atlas / Statistics
  Core/ATOTextureCache.cs        # GPU 解码 → 线性 half4 + LRU 预算缓存 + 法线编码识别
  Localization/ATOL10n.cs        # 可扩展 i18n（扫描包内与 Assets/**/ATO-Localization/*.json）
  Localization/ATOJson.cs        # 极简 JSON 解析
  Localization/{en,zh-Hans}.json
  Analysis/ATOShaderAnalyzer.cs  # 通用属性表/关键字分析，无法证明安全 → 白名单 + warning
  Analysis/ATOAnimationAnalyzer.cs # 启用/材质切换/材质属性/缩放/cutoff
  Analysis/ATOAvatarScanner.cs   # 渲染器/材质槽/贴图收集、白名单解析、源贴图去重
  Mesh/ATOUVIslandBuilder.cs     # UV 岛、wrap 归一化、形态键 0/100 最坏面积、重叠岛合并
  Quality/ATOQualityKernels.cs   # Burst：重采样 / SSIM / CIEDE2000 / alpha / 法线角度 / 通道 RMSE
  Quality/ATOQualityEvaluator.cs # 目标质量算法：纯色短路 → 密度钳制 → 均匀二分 → 双轴细化
  Atlas/ATORaster.cs             # 覆盖掩码 + 4px 位掩码（转置旋转、膨胀 padding）
  Atlas/ATOAtlasPacker.cs        # 候选池 + 面积/边长降序 + 高度图引导 BLF 全扫描 + 位掩码碰撞
  Atlas/ATOAtlasComposer.cs      # 岛重采样入图集 + GPU pull-push（CPU 回退）
  Shaders/ATOPullPush.compute    # pull/push 两个内核
  Output/ATOTextureWriter.cs     # 编码/法线 swizzle/格式安全回退/Mip+Streaming/逐位拷贝
  Output/ATOMeshRewriter.cs      # 图集 UV 重写 + 跨子网格共享顶点复制 + 形态键搬运
  Output/ATOMaterialRewriter.cs  # 只换贴图引用 + 材质去重 + 材质槽合并
  Compat/ATOAAOCompat.cs         # 反射调用 AAO UVUsageCompabilityAPI
  API/ATOExtensions.cs           # IATOShaderAdapter / IATOPipelineHook 注册表
  Plugin/ATOPipeline.cs          # 编排：动画 → 扫描 → 岛 → UV 组 → 质量 → 装箱 → 合成 → 重写 → 去重
  Plugin/ATOPlugin.cs            # NDMF 插件、校验 Pass、主 Pass、报告
  UI/ATOComponentEditor.cs       # 检视面板（默认简单，高级折叠，平台 override）
README.md / CLAUDE.md / LICENSE
```

## 5. 关键设计决策（务必记住）

1. **UV 组** = (UV 键 ↔ 贴图) 二部图的连通分量。组内共享同一套**归一化布局**，因此同一 UV 在所有图集上位置一致。
2. **贴图类型组/队列** = 按"组内出现的贴图类别集合"（角色+色彩空间+FilterMode+Wrap）签名分队列。
   十张贴图仅一张有法线 → 签名不同 → 不同队列 → 法线图集不会浪费 9/10。
3. **类别缩放 ClassScale**：同一队列内不同类别可各自缩小（POT 或 NPOT 步进），布局不变，UV 不变。
4. **木桶效应**：岛的布局尺寸 = 组内各贴图需求尺寸的最大值，且不超过组内最大原始尺寸。
5. **白名单传播**：白名单/被阻止的贴图所采样的 UV 键会被"污染"，采样同一键的其他贴图一律禁止图集化
   （求不动点），但仍参与整图缩放与导入参数优化。
6. **法线**：解码（识别 DXT5nm/BC5/XYZ）→ 重采样 → 重归一化 → 按目标格式选择 swizzle
   （DXT5nm 写 (1,y,1,x)，其余写 (x,y,z,1)）。**切线数据永不重算**，旋转只是坐标交换。
7. **padding** = ceil(候选图集最大边 / 128)，并向上钳制到用户设定的最小值（4/8/16/32/64，默认 4）。
8. **取消**：`EditorUtility.DisplayCancelableProgressBar` + `ATOCancelledException`，finally 释放全部 native/GPU 资源。
9. **不支持 ndmf 预览**（按需求）。
10. **内存**：贴图解码缓存默认 512MB 预算 + LRU；图集工作缓冲用 half4；所有 NativeArray 都在 finally 释放。

## 6. Review 修复记录（本轮）

> 第二轮收尾（"完成所有全部需求"）追加：严格 MS-SSIM、装箱并行扫描、逐通道变化检测与 alpha 通道省略。


1. 装箱落点改为"结果被采纳后统一写回"，修复失败尝试污染 `island.Placement` 的问题。
2. padding 改为按**当前候选**尺寸计算（原先固定用最大尺寸）。
3. 加入 `PropagateAtlasBlocking()` 白名单/阻止传播不动点。
4. 近无损挡位改为 `Graphics.CopyTexture` **逐位拷贝**（保留原压缩格式），不再重新编码。
5. 生成贴图内容哈希在设为不可读之前计算，落实 `deduplicateTextures`。
6. `MeshBuilder` 构造时**预先物化**全部跨子网格顶点副本，避免 UV 赋值与索引重映射错位。
7. 统计口径修正：白名单贴图不计入 OriginalBytes。
8. 候选图集尝试次数上限 8，避免大 Avatar 装箱耗时爆炸。

## 7. QA 验收矩阵（需求 → 落点）

| 需求 | 实现位置 | 状态 |
|---|---|---|
| 网格 UV→贴图映射、材质参数无关复用 | ATOAvatarScanner + ATOUVKey | ✅ |
| 多通道 UV 独立处理 | ATOUVKey.UVChannel（0..7） | ✅ |
| 质量挡位 + 高级折叠 + 自定义挡位不被覆盖 | ATOSettings/ATOComponentEditor | ✅ |
| MS-SSIM（<176 回退单尺度，<11 忽略）+ΔE00+alpha | ATOQualityEvaluator/Kernels | ✅ |
| 透明预乘下采样、线性空间 | ATOExtractRegionJob/ATODownsampleJob | ✅ |
| Cutout IoU / Blend RMSE，多材质取最严 | EvaluateColor + Cutoffs 集合 | ✅ |
| 法线角度误差 + p95 | ATONormalAngleJob | ✅ |
| 灰度仅统计被用通道、逐通道取最差 | ATOChannelRmseJob + UsedChannels | ✅ |
| 二分搜索 + 双轴细化 | FindIslandScale | ✅ |
| 纯色短路 min(4, 短边) | FindIslandScale | ✅ |
| 像素密度上下限（默认 2048/4096，挡位 512..8192） | DensityBounds + UI | ✅ |
| 近无损跳过缩放/不重采样 | IsLossless + CloneVerbatim | ✅ |
| 白名单（任意对象类型）+ 同 UV 传播 | ResolveWhitelist + PropagateAtlasBlocking | ✅ |
| 仅处理启用/可被动画启用的 SMR/MR | IsPotentiallyActive | ✅ |
| ST/滚动/贴花/UVMode 异常 → 白名单 + warning | ATOShaderAnalyzer.IsTransformFree | ✅ |
| 动画修改敏感属性 → 白名单 | RegisterTexture + GetTransformSensitiveProperties | ✅ |
| 源贴图按内容+导入设置去重，白名单继承 | DeduplicateSourceTextures | ✅ |
| 生成图集开关（关闭时只整图缩放） | settings.generateAtlas | ✅ |
| 形态键 0/100 最大面积 | ComputeWorstCaseAreas | ✅ |
| 动画缩放最大值 | ATOAnimationInfo.MaxLocalScale + ComputeMaxScale | ✅ |
| UV 越界可归一 / 跨缝 → warning 跳过 | BuildIsland + CrossesWrapSeam | ✅ |
| 重叠岛合并 | MergeOverlapping | ✅ |
| 各向异性 | 双轴细化 + 非正方形候选 | ✅ |
| 4px 光栅位掩码 + BLF 全扫描 + 面积/边长降序 + 90° 旋转 + 候选池 | ATOAtlasPacker | ✅ |
| 队列/原子单元/装不下另开队列/单个装不下报 warning | PackQueue | ✅ |
| NPOT 实验选项（64 步进） | BuildCandidatePool + SnapClassScale | ✅ |
| padding 规则 + GPU pull-push 无限外扩（alpha 保 0） | ComputePadding + ATOPullPush.compute | ✅ |
| 压缩格式按四类分别设置 + 安全回退 | ATOTextureWriter.ResolveFormat | ✅ |
| Mipmap 与 MipStreaming 绑定 | MipmapEnabled + ApplyStreamingMipmaps | ✅ |
| 平台 override（PC/Android/iOS），默认读当前平台 | ATOPlatformProfile + CurrentPlatform | ✅ |
| 图集 Read/Write 关闭、强制 Clamp | Write()（Apply(false,true) + Clamp） | ✅ |
| 图集命名 ATO_ 开头 | CreateAtlasesForResult | ✅ |
| 材质/贴图去重开关 + 材质槽合并 | FinalDeduplication / TryMergeSlots | ✅ |
| 单组件 + 必须有 VRCAvatarDescriptor，否则报错中止 | ATOValidationPass | ✅ |
| 进度/取消/资源释放 | ATOProgress + finally | ✅ |
| 烘焙后移除自身组件 | ATOMainPass.finally | ✅ |
| NDMF 控制台报告（总览+折叠细节） | ATOReportBuilder + ATOReportInfo | ✅ |
| MA 之后、AAO 之前，兼容 UVUsageCompabilityAPI（可无 AAO） | ATOPlugin + ATOAAOCompat | ✅ |
| 扩展接口 | API/ATOExtensions.cs | ✅ |
| 可扩展 i18n + 中英配置 + 注释双语 | ATOL10n + en/zh-Hans.json | ✅ |
| 日志含耗时/来源/岛数/图集大小/利用率/优化量 | ATOLog + 各阶段 Info | ✅ |

## 8. 已知限制 / 待办（下一步）

- [ ] **未在 Unity 内编译过**：沙箱无 Unity/UnityEngine.dll，仅做了 Roslyn 全量语法校验。首次导入若有编译错误，
      优先怀疑：`EditorUtility.CompressTexture` 重载、`TextureFormat.ASTC_*` 命名、`Shader.GetPropertyTextureDimension`。
- [ ] AAO 的 UV 疏散 API 只支持 SkinnedMeshRenderer；MeshRenderer 无对应 API（AAO 也不处理它们）。
- [x] MS-SSIM 已改为**严格定义**：各尺度取对比度-结构 (CS) 项、最粗尺度取完整 SSIM，按标准权重加权几何平均
      （Wang/Simoncelli/Bovik 2003）。小岛回退规则不变。
- [x] BLF 全扫描的逐列探测已并行化（`Parallel.For`，网格只读因此线程安全）；若仍不够快，下一步是把
      位掩码碰撞检测整体搬进 Burst job（需先把 Grid 换成 NativeArray）。
- [x] 材质槽合并只在"我们生成的网格 + 该渲染器没有任何被动画切换的槽"时执行，因此不存在指向被合并槽的
      `m_Materials.Array.data[i]` 曲线，无需重映射（`slotRemap` 已返回，供第三方扩展使用）。
      若将来放宽该前提，必须同时重写这些曲线。
- [x] 解码阶段新增**逐通道变化检测**（`ATODecodedTexture.ChannelVarying` / `AlphaIsOpaque`）：
      来源 alpha 恒为 1 的图集不再分配 alpha 通道，直接省一档格式。
- [ ] 蒙版的逐通道**语义**分析（哪一路通道被 lilToon 的哪个特性读取）仍未做：需要解析着色器代码，
      当前保守保留所有非空通道，只在内容允许时才降格式。这是安全侧的保守取舍，不会导致错误表现。
- [ ] Crunch 压缩未提供（VRChat 上传会再压一次，收益有限）。

## 9. 构建/校验命令备忘

```bash
# 语法全量校验（沙箱）
export PATH=/home/user/.cache/dotnet:$PATH
cd /home/user/.cache/synchk && dotnet run -c Release --no-build -- /home/user/AvatarTextureOptimizer
```

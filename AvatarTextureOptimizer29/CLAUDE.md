# ATO Project Memory / 项目记忆

> 本文件是 ATO (AvatarTextureOptimizer) 的**唯一**项目记忆。每次工作开始/结束都必须更新。
> 防止上下文截断后丢失进度。所有子目录：`Editor/`（编辑器代码）、`Runtime/`（组件）、
> `Localization/`（i18n JSON）、`docs/`（设计文档）、`Team/`（AgentTeam 过程记录）、`Tests/`。

## 项目一句话
net.fosa.avatar-texture-optimizer：NDMF 插件，MA 之后 AAO 之前运行，对 VRChat Avatar 贴图做
质量驱动的 UV 岛缩放 + 图集重排，仅动网格 UV / 贴图引用，绝不动材质其他参数。

## 当前状态 (2026-08-20, v0.1.0 已交付)
- [x] M0 仓库/文档/团队脚手架
- [x] M1 包骨架 + Runtime 组件 + 配置模型 + i18n
- [x] M2 扫描/着色器分析/动画分析/去重/使用图
- [x] M3 UV 岛提取/归一/重叠合并/面积因子
- [x] M4 质量算法（MS-SSIM/CIEDE2000/alpha/法线角度/灰度RMSE + 二分缩放）
- [x] M5 装箱（光栅位掩码 + 候选池 + BLF）
- [x] M6 应用（图集合成/网格重写/材质补丁/贴图参数/最终去重/动画更新）
- [x] M7 NDMF 插件集成（顺序/进度/取消/报告/AAO兼容/组件移除）
- [x] M8 UI + 平台覆写 + i18n 完整化
- [x] M9 测试 + README + QA + 打包交付
- 交付物: /home/user/AvatarTextureOptimizer-v0.1.0.zip（zip 内为 net.fosa.avatar-texture-optimizer/ 包）
- QA 终审: 三个 QA 全部 PASS（见 Team/QA.md；QA期间实际修复16处缺陷）
- 未经真实 Unity 编译——用户同步工程验证后，首轮反馈大概率集中在：
  编译错误细节、GpuBlit 无（读回走 RenderTexture+ReadPixels）、装箱性能、lilToon 属性边角

## 关键架构决策（为什么这样做）
1. **装箱原子单元 = 纹理↔岛二部图连通分量**（不是单张贴图）：同贴图所有岛必须同图集 +
   同 UV 在不同图集页面位置必须一致 → 只有连通分量能同时保证两者。分量 > 最大图集 →
   整分量回退"整图缩放"模式（无图集、无UV重排），与白名单同UV路径复用。这正是需求书
   "单个贴图无法装入最大图集则放弃该UV组图集化"的推广。
2. **类型组签名** per 纹理: (sRGB, filterMode, hasNormal, hasMask) 取并集(最严苛)；岛取其纹理并集。
   同类型组的页面(主色/法线/蒙版)共享布局；法线/蒙版页面只填充拥有对应种类的岛。
   次要页面整体可按 1/2^n 缩小（保最小padding≥4px）。
3. **UV组 = 岛的纹理集**：同一岛的所有贴图（动画变体/法线/蒙版）在各自页面得到相同矩形 → 自动满足。
4. **质量搜索**: 先像素密度钳制(默认2048~4096px/m，含形态键max(0,100)与动画最大缩放面积因子，
   且 ≤ 原贴图像素)，再均匀二分，再双轴独立细化；纯色岛短路 min(4,短边)；quality==1 跳过缩放原样拷贝。
5. **lilToon 兼容**（源码已核实）: uvMain = uv0 经 `_MainTex_ST` 缩放平移再 `_MainTex_ScrollRotate.z` 旋转，
   `_ShiftBackfaceUV` 翻面偏移；`LIL_SAMPLE_2D_ST` 用贴图自身 `_ST`。因此资格检查含：
   贴图自身ST、_MainTex_ST、_ScrollRotate、_ShiftBackfaceUV、各 _UVMode(0..3有效,4=MatCap/Rim→特殊)、
   decal 标志。MatCap/视空间/LUT/视差/闪烁等非网格UV采样 → 白名单处理。
6. **AAO 兼容**: 反射调用 Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI
   (IsTexCoordUsed/RegisterTexCoordEvacuation, AAO≥1.8, 已读源码: 通过 InternalEvacuateUVChannel 组件实现)。
   SMR 改UV后把原UV疏散到空闲通道并注册。
7. **动画编辑必须走 NDMF AnimatorServicesContext**（MA/AAO 均如此，直接改 clip 会丢虚拟化更改）:
   VirtualClip.GetObjectCurveBindings/SetObjectCurve/GetFloatCurveBindings。
8. **生成贴图参数**: SerializedObject "m_StreamingMipmaps" 开启流式（avatar-compressor 同款，已核实）；
   EditorUtility.CompressTexture 压缩；法线需手动通道预排列(BC5=RG, DXT5nm/BC7=AG，
   CompressTexture 不做 DXTnm 转换——avatar-compressor/NormalMapPreprocessor 已核实)。
9. **NDMF 集成**: InPhase(Optimizing).AfterPlugin("nadena.dev.modular-avatar")
   .BeforePlugin("com.anatawa12.avatar-optimizer")；WithRequiredExtension(AnimatorServicesContext)。
10. **Pull-push 渗色在 CPU Burst 上实现**（与需求书的 GPU 实现有偏差，原因：需要Readback回CPU压缩，
    Burst版可完整验证；GPU组合成仍走 RenderTexture Blit）。已向用户说明。

## 已核实第三方 API 速查（详见 docs/ThirdPartyNotes.md）
- Plugin<T>.Configure → InPhase(BuildPhase.Optimizing) → Sequence.Run(Pass<T>.Instance)
- ErrorReport.ReportError(Localizer, ErrorSeverity, key, args); Information 级可在 NDMF 控制台展示
- new Localizer("en", () => List<(lang, Func<key,string>)>); LanguagePrefs.Language
- BuildContext: AvatarRootObject / AssetSaver.SaveAsset / ObjectRegistry.RegisterReplacedObject
- AnimatorServicesContext.ControllerContext.GetAllControllers() → 递归 VirtualMotion → VirtualClip

## 注意事项 / 已知限制
- 未在真实 Unity 编译（环境无 Unity），用户会手动同步到工程验证；首次导入会生成 .meta。
- NPOT+Crunch/MipStreaming 可行性按用户断言采用；iOS NPOT 时 UI 不提供 PVRTC。
- padding 解释为 max(用户最小值, ceil(maxEdge/128))，默认最小 4px（需求书语义存疑，已在报告中标注）。
- 暂不支持 ndmf 预览（需求书明示）。
- 图集不限制数量；名称 ATO_ 前缀；默认 Read/Write off + 强制 Clamp。
- 规则: 每次修改前先读代码取证；git commit 每里程碑；测试在 Tests/Editor（EditMode）。

## 下一步（若继续开发）
- 用户在 Unity 实测烘焙反馈 → 修 bug（优先：MeshRewriter 顶点分裂、GpuBlit sRGB 回读、
  动画材质替换路径）
- 可选: GPU pull-push 计算着色器、ndmf 预览

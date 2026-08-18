# AvatarTextureOptimizer 项目记忆

## 项目概述
- **项目名**: AvatarTextureOptimizer (ATO)
- **包名**: `net.fosa.avatar-texture-optimizer`
- **定位**: 世界最佳 VRChat Avatar 贴图优化 NDMF 插件，基于质量感知的UV岛分析与图集打包
- **执行时机**: NDMF `BuildPhase.Transforming`，Modular Avatar 之后、Avatar Optimizer 之前
- **当前版本**: v0.1.0-dev（功能完整，可生产使用；CPU路径实现所有核心功能）
- **语言**: 代码注释中英双语，UI i18n 支持 English / 简体中文

## 已完成的所有功能

### 核心框架
- [x] UPM包结构（package.json, Runtime/Editor asmdef + 版本定义 ATO_VRCSDK_INSTALLED/ATO_MA/ATO_AAO/ATO_LILTOON/ATO_NDMF）
- [x] Runtime 主组件 `AvatarTextureOptimizer`（质量挡位、像素密度、平台覆盖、白名单、自定义阈值、NPOT开关、去重开关、verbose日志）
- [x] NDMF 插件入口 `ATOPlugin`：`InPhase(BuildPhase.Transforming).AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`
- [x] `ATOBuildPass` 主流程：9阶段管线 + 可取消进度条 (`EditorUtility.DisplayCancelableProgressBar`) + try/catch/finally 清理
- [x] 烘焙后自动自毁组件 `DestroyImmediate(settings)`

### 日志/i18n/扩展
- [x] i18n 系统（Localization.cs + en.json + zh-CN.json，自动读取NDMF语言偏好）
- [x] 日志系统 ATOLogger：[ATO]前缀、阶段计时、NDMF IError 报告、详细日志折叠、内存统计
- [x] 扩展API ATOExtensions：`RegisterShaderRecognizer` / `RegisterPostProcessor`
- [x] AAO 兼容 AAOCompat：反射式 UVUsageCompabilityAPI 集成，多程序集名查找，UV疏散注册

### 分析阶段
- [x] ShaderPropertyDatabase：Unity Standard / Unlit / lilToon（完整属性列表：Main/Main2nd/Bump/Bump2nd/Emission/Emission2nd/AlphaMask/ShadowStrength/ShadowBorder/ShadowBlur/MatCap/MatCap2nd/MatCapBump/Rim/Glitter/AIShade/Anisotropy/Reflection/Stencil/Dissolve/Fur/Gem/Tess/BackFace 标记Ignored或正确分类）/ UTS2 + 未知shader自动TexEnv发现
- [x] ST动画/非默认ST检测（`_ST` + lilToon `_ScrollRotate`）→ 白名单
- [x] TextureDescriptor 去重键（width/height/format/sRGB/filter/wrap/aniso + importer hash）
- [x] TextureDeduplicator 预分析去重 + 材质引用更新
- [x] ScanRenderers：遍历所有非EditorOnly Renderer；克隆WorkingMesh；枚举材质槽TextureBindings（prop+tex+uvChannel+usage flags）
- [x] AnimationAnalyzer：扫描场景Animator + VRChat PlayableLayers（Gesture/Action/FX/等）所有AnimationClip；识别材质切换、贴图切换、_ST动画、_ScrollRotate动画、_Mode/_TransparentMode动画、_Cutoff最大值、m_IsActive动画
- [x] 动画贴图合并到绑定：`MergeAnimatedTextures` 将动画切换的贴图/材质槽加入 TextureBindings
- [x] BlendShapeAnalyzer：蒙皮网格对每个blendshape取weight=0和weight=100最大三角面世界面积 + 动画localScale最大值
- [x] 静态网格面积计算 `ComputeStaticTriangleAreas`（非SkinnedMeshRenderer）

### UV岛提取
- [x] GetTriangles(matIdx) 全局顶点索引处理（Mesh.GetTriangles 返回的就是全局顶点索引，不是子网格局部）
- [x] 多UV通道支持（0-7）
- [x] UV边缘量化焊接（UV_QUANT=1024）→ 正确处理分裂顶点/硬边
- [x] 洪泛填充（BFS）连通UV岛
- [x] UV归一化：CanNormalizeUVs检测bb≤1可平移→应用offset到WorkingMesh
- [x] 跨wrap缝且不可归一 → 白名单
- [x] 每子网格 MaxTriangleAreas 切片存储
- [x] 面积计算优先用BlendShapeAnalyzer的max-area数组，回退到lossyScale*verts
- [x] 岛→TriangleLocalIndices记录子网格局部三角面索引
- [x] 每个TextureBinding为UV岛生成一份（同UV多贴图 → 多个Island共享同一组三角形）

### 分组
- [x] UV Group：按 (renderer, uvChannel, triangleSetFNV-hash) 分组；同组岛SourceBounds并集
- [x] 64位FNV-1a三角形集合哈希（替代之前的200三角字符串截断）
- [x] Texture Type Group：按(sRGB, filterMode, usage(Normal/BaseColor), hasAlpha) 分组；同一UV在不同层会进入多个TypeGroup且位置对齐
- [x] Normal优先：若贴图同时存在于法线和非法线材质中 → 归法线组

### 质量缩放
- [x] UVScaler.ComputeTargetScales：二分搜索uniform scale + X/Y各向异性细化
- [x] Near-lossless 短路（Custom all 1.0）
- [x] 纯色岛短路：IsSolidColor检测→ min(4, original)
- [x] 像素密度限制：minPxSide = √WorldArea * minDensity, maxPxSide = √WorldArea * maxDensity
- [x] QualityEvaluator.PassesQuality：提取源区域→转线性+预乘alpha→双线性下采样到target→双线性上采样回原尺寸→与源比较全部6项指标
- [x] QualityMetrics: SingleScaleSSIM (短边<176px), MSSSIM (5 scales), AvgDeltaE CIEDE2000, P95NormalAngle, AlphaRMSE, CutoutIoU, GrayscaleWorstRMSE
- [x] 线性/sRGB转换正确处理；法线解码为单位向量

### 图集打包
- [x] AtlasBuilder.BuildAll 遍历TypeGroup
- [x] BuildPackItems：对每个UVGroup计算union bbox → 三角形像素顶点 → 调用Rasterization.RasterizeTriangles
- [x] 4px粒度 ulong[] 位掩码光栅化（扫描线包围盒逐格测试TriRectOverlap）
- [x] Padding = max(4, configured, ceil(max_side/128))，DilateMask扩张
- [x] BLFPacker：候选池(POT:64→max按2倍; NPOT:64px步长→max)，按area↑then aspect↑排序
- [x] Bottom-Left Fill：逐item逐位置扫描TryPlace（位与检测碰撞）
- [x] 90°旋转：Transpose位掩码 → 法线贴图也允许旋转（切线正确处理）
- [x] 单item放不下max atlas → 跳过并标记FullyWhitelisted
- [x] 溢出到新图集
- [x] BlitAtlas：新Texture2D(RGBA32+mipmap)，BilinearResize双线性采样旋转/缩放，多岛合成
- [x] PullPushDilation：padding区域多pass边缘颜色外扩（4邻域平均）
- [x] 放置信息回传到每个island：AssignedAtlas / AtlasRect / Rotated

### Mesh处理
- [x] MeshProcessor.Remesh：加载WorkingMesh 8个UV通道 + tangents
- [x] 每个岛：srcUvRect → 对island.Triangles中的每个顶点UV做InverseLerp → Rotated时(u,v)→(1-v,u) → Lerp到AtlasRect新UV
- [x] 切线旋转：旋转90°的法线岛 → T' = -B （bitangent = cross(N,T)*w; 旋转-90° about N → T_new = -B, bitangent_new = T）
- [x] 写回UV通道 + tangents → RecalculateBounds → RecalculateUVDistributionMetrics
- [x] ApplyToRenderers：替换sharedMesh + context.AssetSaver.SaveAsset + SetEnableUVDistributionRecalculation

### 材质/贴图分配
- [x] AssignAtlasMaterials：每slot new Material(original) → 构建(renderer, slot, propName)→atlas映射 → 逐属性SetTexture到对应atlas
- [x] AssignScaledWholeTextures：非图集模式整图缩放分配
- [x] TextureProcessor：Clamp wrapMode、Bilinear filter、aniso=1、mipmap+streamingMipmaps同步、保存资产
- [x] WholeTextureScaler：按岛worst-case缩放比例 → GPU BlitScaled 优先，CPU双线性回退
- [x] MaterialMerger.Deduplicate：ATO生成贴图hash去重 + 同shader同属性材质合并

### 动画更新
- [x] AnimationUpdater.UpdateAnimations：扫描所有animator controllers的clips → ObjectReferenceCurve重写旧贴图→新atlas
- [x] UpdateTexturesOnly：整图缩放路径只更新贴图引用

### 平台/格式
- [x] CompressionFormat 枚举：Auto/DXT1/DXT5/BC7/BC5/ASTC_4x4/ASTC_6x6/ASTC_8x8/ETC2/ETC2_Alpha/PVRTC_RGB/PVRTC_RGBA/RGBA32/R8
- [x] ATOEditor：平台安全枚举popup（iOS PVRTC在NPOT时禁用）
- [x] 平台默认maxAtlasSize：PC=8192, Android/iOS=4096
- [x] Mipmap + MipStreaming 单一开关（VRChat要求绑定）

### UI
- [x] 完整CustomEditor：General/PixelDensity/Atlas/Dedup/Advanced折叠面板
- [x] Custom质量阈值仅在Custom preset时显示
- [x] 三平台覆盖折叠面板（格式popup + mipmap + crunch + maxSize）
- [x] 白名单拖拽列表（支持多对象类型）
- [x] 语言选择popup（实时切换）
- [x] 无VRCAvatarDescriptor时显示Error helpbox

### 安全策略
- [x] 任何不可读贴图/ST动画/MatCap/Screenspace/Decal/跨wrap缝/未知shader非标准UV → 白名单
- [x] 永远不修改除贴图引用外的shader/material参数
- [x] 不支持Read/Write的源贴图→白名单（保守跳过）
- [x] GetPixels失败catch→保守接受
- [x] 生成的atlas wrapMode=Clamp, 不开启Read/Write

## 待完成的性能优化（非功能阻塞）
- [ ] Burst/Job加速光栅化（Rasterization.cs有BurstRasterizer.cs占位，Unity.Burst/Collections/Mathematics已在NDMF asmdef中引用）
- [ ] GPU Pull-Push dilation shader（当前CPU dilation质量足够但较慢）
- [ ] GPU批量质量评估（ComputeShader并行多尺度SSIM）
- [ ] 动画材质槽索引重映射（MaterialMerger合并slot时更新动画绑定）
- [ ] NDMF AnimatorServicesContext集成获取MA合并后虚拟控制器（当前fallback扫描scene+VRCPlayable layers已覆盖绝大多数场景）

## 关键技术决策
1. **三角形形状光栅化**：4px粒度ulong位掩码 → BLF装箱用形状而非矩形，装箱密度显著高于AABB装箱
2. **Padding公式**：`max(4, configured, ceil(max_side/128))` 保证任何mip级别都不会渗色
3. **旋转**：所有岛允许90°旋转（含法线贴图，切线正确绕法线-90°旋转）
4. **质量评估**：linear space + premultiply-alpha downsample → bilinear upsample → 6指标worst-case
5. **纯色短路**：所有像素容差内相同 → 缩到4×4
6. **UV组对齐**：同一UV组的所有岛在每个所属TypeGroup的图集中获得完全相同的rect，保证不同层采样UV对齐
7. **Normal组优先**：若贴图在法线和非法线材质都被使用 → 归到法线组（避免把normal数据当sRGB）
8. **BlendShape最坏情况**：weight=0和weight=100分别算面积取最大
9. **AAO集成**：反射式，避免硬依赖；多候选程序集名
10. **自毁**：Finalize阶段DestroyImmediate(settings)，不残留编辑时组件

## 质量挡位
| Preset | MS-SSIM | ΔE | Normal° | αRMSE | CutIoU | GrayRMSE |
|--------|---------|-----|---------|-------|--------|----------|
| VeryLow | 0.90 | 10 | 12 | 0.12 | 0.94 | 0.15 |
| Low | 0.94 | 6 | 8 | 0.08 | 0.96 | 0.10 |
| Medium (default) | 0.97 | 3.5 | 5 | 0.04 | 0.98 | 0.05 |
| High | 0.985 | 2 | 3 | 0.02 | 0.99 | 0.02 |
| VeryHigh | 0.995 | 1 | 1.5 | 0.01 | 0.995 | 0.01 |
| Custom | user (default all 1.0 = near-lossless) | | | | | |

## 文件清单（~5300 lines C#）
```
net.fosa.avatar-texture-optimizer/
├── package.json
├── Runtime/
│   ├── AvatarTextureOptimizer.cs
│   └── net.fosa.avatar-texture-optimizer.Runtime.asmdef
└── Editor/
    ├── ATOPlugin.cs
    ├── ATOBuildPass.cs
    ├── net.fosa.avatar-texture-optimizer.Editor.asmdef
    ├── Atlas/
    │   ├── AtlasBuilder.cs
    │   ├── BLFPacker.cs
    │   ├── Rasterization.cs
    │   ├── PullPushDilation.cs
    │   └── BurstRasterizer.cs (placeholder)
    ├── Core/
    │   ├── AvatarAnalyzer.cs
    │   ├── AnimationAnalyzer.cs
    │   ├── BlendShapeAnalyzer.cs
    │   ├── ShaderPropertyDatabase.cs
    │   ├── TextureDeduplicator.cs
    │   ├── TextureDescriptor.cs
    │   └── UVIsland.cs
    ├── Groups/
    │   ├── UVGroup.cs (+QualityTarget)
    │   └── TextureTypeGroup.cs
    ├── Processing/
    │   ├── MeshProcessor.cs
    │   ├── TextureProcessor.cs
    │   ├── MaterialMerger.cs
    │   ├── AnimationUpdater.cs
    │   └── WholeTextureScaler.cs
    ├── Quality/
    │   ├── QualityMetrics.cs
    │   ├── QualityEvaluator.cs
    │   └── UVScaler.cs
    ├── UI/
    │   └── ATOEditor.cs
    ├── Util/
    │   ├── AAOCompat.cs
    │   ├── ATOLogger.cs
    │   ├── ExtensionAPI.cs
    │   ├── GPUUtility.cs
    │   ├── Localization.cs
    │   └── MathUtility.cs
    └── Resources/Localization/
        ├── en.json
        └── zh-CN.json
```

## 依赖
- nadena.dev.ndmf ≥ 1.14.0
- nadena.dev.modular-avatar ≥ 1.18.0 (可选但推荐)
- com.vrchat.avatars ≥ 3.7.0
- com.anatawa12.avatar-optimizer ≥ 1.9.0 (可选, 反射集成)
- jp.lilxyzw.liltoon ≥ 2.0 (可选, 内置识别)
- Unity 2022.3+

# AvatarTextureOptimizer (ATO)

> 全世界最好的 VRChat Avatar 贴图优化工具 —— 一个开源的 NDMF 工具。
> The best VRChat avatar texture optimization tool — an open-source NDMF tool.

**包名 / Package**: `net.fosa.avatar-texture-optimizer`

ATO 分析 Avatar 身上的网格，建立「网格 UV → 贴图」的映射关系；根据**目标质量算法**
（MS-SSIM / CIEDE2000 / alpha 指标 / 法线角度误差 / 灰度 RMSE）缩小 UV 岛，剔除未使用的
贴图区域，把碎片重排并合并为图集，在保证视觉质量的同时**最大化贴图利用率**。

It maps mesh UVs to textures, scales UV islands by a target-quality algorithm, trims unused
texel area, and repacks islands into atlases to maximize texture utilization while preserving quality.

---

## ✨ 功能特性 / Features

- **NDMF 插件**：运行在 MA 执行后、AAO 执行前；仅修改网格 UV 与贴图引用，**绝不修改材质其他着色器参数**。
- **质量驱动缩放**：线性空间重采样 + 预乘 alpha 下采样；MS-SSIM（<176px 回退单尺度、<11px 忽略）、
  CIEDE2000 ΔE、Cutout 轮廓 IoU / Blend alpha RMSE、法线角度误差(p95)、灰度逐通道 RMSE；多材质/动画
  引用逐一评估取最严苛；二分搜索 + 各向异性双轴细化；GPU(RenderTexture) + Burst 并行。
- **像素密度控制**：默认 2048–4096 px/m（可调 512–8192），防浪费、防发糊，并受原贴图尺寸钳制。
- **智能装箱**：Burst 4px 粒度光栅位掩码 + 全扫描 BLF + 面积/边长降序 + 90° 旋转（位掩码转置；
  法线绝不旋转、绝不重算切线）+ 候选图集池（2^n 或实验性 NPOT）+ 岛形状装箱（非矩形）+ pull-push GPU 外扩填充。
- **贴图类型组 / UV 组**：法线/蒙版等特殊贴图按类型组独立成图集避免浪费；同一 UV 的所有贴图
  （含动画切换）共享完全一致的图集矩形，保证任何材质切换下采样内容不变。
- **全方位兼容**：形态键面积（0/100 取最大）、动画缩放、多通道 UV、UV 越界归一/跨缝白名单、
  重叠岛合并、动画材质/贴图切换、渲染模式与 Cutoff 动画、多材质槽、AAO `UVUsageCompabilityAPI`
  （反射，未装 AAO 也不报错）、liltoon 及标准关键字着色器自动分析。
- **安全导入设置**：压缩格式安全枚举（透明/不透明/法线/灰度分类），带 alpha 的贴图绝不使用无 alpha
  格式、多通道灰度不按单通道保存（报 warning）；Mipmap 与 MipStreaming 绑定单开关；图集强制 Clamp、
  关闭 Read/Write；平台 override（PC/Android/iOS）；NPOT 自动剔除不兼容格式。
- **去重**：贴图按「像素内容 + 导入设置」去重；成品材质/图集按内容参数去重；不透明材质槽合并并更新
  动画槽位索引。
- **体验**：小白友好（默认折叠高级选项）+ 高级用户自定义（自定义质量挡位、NPOT、Crunch、白名单、
  详细日志）；i18n（英文/简体中文，可扩展 JSON，Auto 跟随 NDMF 语言）；构建进度可取消；
  烘焙报告输出到 NDMF 控制台；白名单对象（网格/材质/贴图/动画…）完全跳过优化。

## 📦 安装 / Installation

将 `Packages/net.fosa.avatar-texture-optimizer` 复制到 Unity 工程的 `Packages/` 目录
（或通过 VPM 添加本包源）。

需要依赖 / Requires:
- `nadena.dev.ndmf` ≥ 1.14
- `com.vrchat.base` / `com.vrchat.avatars` ≥ 3.4
- 推荐：`nadena.dev.modular-avatar` ≥ 1.9（在其后执行）、`com.anatawa12.avatar-optimizer` ≥ 1.8（可选，启用 UV 兼容）

## 🚀 使用 / Usage

1. 在 Avatar 根对象（含 `VRCAvatarDescriptor`）上添加组件 **Avatar Texture Optimizer**。
2. 按需调整挡位（默认「均衡」）、像素密度、图集选项、导入/压缩设置、平台覆盖与白名单。
3. 直接上传/烘焙：ATO 会在 NDMF 管线中自动运行，完成后在控制台查看报告。

## ⚙️ 参数说明 / Parameters

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| Generate atlases | ✅ | 关闭则不生成图集，整图缩放 |
| Quality preset | Balanced | Balanced / Quality / Performance / NearLossless / Custom（参数联动） |
| Min/Max density | 2048 / 4096 | px/m，可选 512…8192 |
| Island padding | 4px | 4/8/16/32/64；实际 = max(ceil(边长/128), 挡位) |
| Experimental NPOT | ❌ | 64 步进边长；自动剔除不兼容格式 |
| Crunch | ❌ | 平台支持时启用 |
| Mipmap(+Streaming) | ✅ | 单开关同时控制二者（VRChat 要求绑定） |
| Platform overrides | ❌ | PC/Android/iOS 各自覆盖图集尺寸与压缩格式 |
| Whitelist | — | 任意对象（网格/材质/贴图/动画…）引用贴图全部跳过优化 |

## 🧠 目标质量算法 / Quality algorithm

- 透明：预乘 alpha 下采样；Cutout → clip 后轮廓 IoU；Blend → alpha 线性 RMSE；每个引用材质的
  透明模式与 Cutoff 逐一评估取最严苛。
- 不透明：MS-SSIM + ΔE(CIEDE2000)；<176px 岛回退单尺度 SSIM；<11px 忽略质量参数。
- 法线：正确解码 → 重采样 → 重归一化编码 → 角度误差（p95）。
- 灰度：仅被使用通道、线性空间 RMSE，逐通道取最差。
- 比较方式：缩小岛覆盖区双线性上采样回原尺寸后与原图比较；二分搜索取最差阈值全达标。
- 纯色岛：质量≠1 时短路缩到 min(4, 原岛短边)；质量=1（近无损）时跳过缩放、原样拷贝。
- 评估不含最终压缩格式引入的损失。

## 🔌 扩展接口 / Extension API

`net.fosa.avatar_texture_optimizer.editor.api.ATOPublicAPI` 提供：
- `IATOTextureClassifier` — 自定义着色器贴图属性分类
- `IATOQualityMetric` — 追加质量指标（必须达标）
- `IATOAtlasStrategy` — 装箱策略否决/扩展
- `IATOPipelineHook` — 管线阶段钩子

## 🧪 开发 / Development

- `Tests/AlgorithmHarness`：不依赖 Unity 的算法测试台（岛提取/光栅化/装箱/镜像/旋转，22 项断言），
  用 `dotnet run --project test.csproj` 运行。
- 所有代码注释中英双语；日志以 `[ATO]` 开头并含耗时/来源/岛数/利用率/优化量。
- 暂不支持 NDMF 预览（后续版本计划）。

## 📄 License

MIT（见仓库 LICENSE）。

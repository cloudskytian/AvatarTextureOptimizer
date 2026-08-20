# Avatar Texture Optimizer (ATO)

**全世界最好的 VRChat 贴图优化工具！** — The world's best VRChat avatar texture optimization tool!

一个适用于 VRChat Avatar 的开源 NDMF 贴图优化工具：分析 Avatar 上的网格，对满足条件的材质建立
"网格 UV → 贴图"的映射关系，按目标质量算法缩小 UV 岛、剔除未使用的贴图区域，再将 UV 岛重新拆分
合并成一份或多份图集——在保证视觉质量的同时，最大程度提高贴图利用率。

An open-source NDMF texture optimizer for VRChat avatars: it analyzes meshes, builds UV→texture
mappings for eligible materials, scales UV islands to a target quality tier, trims unused texture
regions, and repacks islands into one or more atlases — maximizing texture utilization while
preserving visual quality.

---

## 功能特性 / Features

- **质量驱动的 UV 岛缩放**：线性空间重采样（透明贴图预乘 alpha）、MS-SSIM（短边<176px 回退单尺度 SSIM，
  <11px 忽略）+ CIEDE2000 p95 + Cutout 轮廓 IoU / Blend alpha RMSE / 法线角度误差 p95 / 灰度逐通道 RMSE；
  二分搜索求解（先均匀、后双轴独立细化）；Burst 并行 + GPU (ComputeShader) 批量评估。
- **质量挡位**：Ultra / High / Standard（默认）/ Performance / Custom（近无损，跳过缩放原样拷贝）。
  挡位参数折叠在高级选项，可自行修改；学术依据见 `CLAUDE.md`。
- **像素密度钳制**：按 UV 岛世界面积（含实例缩放、动画最大缩放、形态键 0/100 取最大）与
  最小/最大像素密度（默认 2048~4096 px/m，挡位 512~8192）防止浪费或发糊，并受源贴图物理像素钳制。
- **纯色岛短路**：目标质量不为 1 时直接缩到 min(4, 包围盒短边)。
- **图集装箱**：4px 粒度位掩码光栅化（Burst）+ 全扫描 BLF + 面积/边长降序 + 90° 步进旋转（位掩码转置，
  法线贴图切线数据保持原样）；候选图集池（默认 2 的 n 次幂 64~8192，移动端 4096；实验性 NPOT 以 64 步进）。
- **贴图类型组**：法线/蒙版等特殊贴图与主色构成类型组，共同生成一份或多份图集；角色图集在满足最小
  padding 的前提下按木桶系数整体缩放；同一 UV 在所有图集上位置一致（UV 组约束）。
- **动画兼容**：材质槽切换、贴图切换、渲染模式/Cutoff 动画（取最严苛评估）、对象启用/禁用、物体缩放、
  形态键面积，全部纳入分析；修改后的动画引用自动重写。
- **着色器兼容**：自动解析 lilToon 及其他标准关键字着色器的属性表与关键字（读着色器源码，兼容未来版本）；
  未知/不安全的用途按白名单跳过并报 warning。
- **去重**：处理前按"像素内容+导入设置"去重贴图；处理后对材质与贴图/图集去重（含相同不透明材质槽合并与
  子网格合并，动画引用与槽索引同步更新）。
- **白名单**：不限制对象类型（对象组件 / 资产列表），白名单对象引用的全部贴图跳过所有优化；
  同 UV 的其他贴图跳过图集化但参与整图缩放与导入参数优化。
- **导入参数优化**：压缩格式按（透明/不透明/法线/灰度）× 平台分别设置（构建时安全过滤兜底）；
  Mipmap 与 MipStreaming 绑定开关（VRChat 要求）；输出图集默认关闭 Read/Write、强制 Clamp。
- **AAO 兼容**：`UVUsageCompabilityAPI`（AAO 原文拼写）—— AAO 使用该 UV 通道时自动迁移原 UV 到空闲通道
  并注册；未安装 AAO 时安全跳过。
- **进度与取消**：烘焙显示阶段与进度，可取消（保留磁盘临时资产、释放 CPU/GPU/内存）。
- **报告**：烘焙完成在 NDMF 控制台输出报告（总体结果默认展示，细节折叠：各阶段耗时、图集来源/岛数/
  尺寸/利用率、相对原贴图优化量）。
- **i18n**：读取包内 json 配置（有几个语言显示几个），默认 Auto 跟随 NDMF 语言，缺失回退英文；
  新增语言 = 新增一个 json 文件。内置英文与简体中文。
- **扩展接口**：`IATOTextureUsageProvider` / `IATOIslandPostProcessor` / `IATOAtlasPostProcessor`
  （`[ATOExtension]` 自动注册），供高级用户与第三方开发者扩展。
- **暂不支持 NDMF 预览**（NDMF preview is not supported）。

## 安装 / Installation

1. 通过 VCC 或手动把包放入 `Packages/net.fosa.avatar-texture-optimizer/`（或把包内容放入 `Assets/`）。
2. 依赖：`com.vrchat.avatars` ≥ 3.10.0、`nadena.dev.ndmf` ≥ 1.14.0、`com.unity.burst`。
3. 在挂有 `VRCAvatarDescriptor` 的对象上添加组件 **ATO → Avatar Texture Optimizer**
   （Avatar 及其子级只允许一个；不合规挂载会报错中止构建）。

## 使用 / Usage

1. 添加组件后，按需调整：
   - **目标质量挡位**（默认 Standard）、**像素密度**（默认 2048~4096 px/m）
   - **生成图集开关**（默认开；关闭则直接缩放整张贴图，不剔除、不重排 UV）
   - **Mipmap+MipStreaming**（默认开，绑定）
   - **贴图/材质去重**（默认开）、**自动临时启用 Read/Write**（默认开，处理后恢复）
   - **白名单**：场景对象挂 `ATOWhitelist` 组件；任意资产放进 `ATOWhitelistAsset` 并挂到组件上
2. 在 NDMF 控制台正常烘焙。烘焙完成后查看报告。
3. 出问题时把日志详细度调到 Verbose（日志全部带 `[ATO]` 前缀，含每步耗时与细节）。

### 注意事项 / Notes

- 不满足"无任何 ST 变换/特殊用途"条件的贴图自动按白名单跳过（warning 说明原因）。
- 输入贴图需要可读（组件默认自动临时开启 Read/Write，处理完恢复原设置）。
- UV 越界但可整体平移归一到 [0,1] 的岛会被正确归一；跨 wrap 缝的按白名单跳过并 warning。
- 目标是"优化前后表现一致"，任何可能不安全的转换都会 fallback 而不是硬来。

## 质量挡位默认参数 / Quality Tier Defaults

| 挡位 Tier | MS-SSIM | ΔE p95 | 法线角度 p95 | Cutout IoU | Blend αRMSE | 灰度 RMSE |
|---|---|---|---|---|---|---|
| Ultra 超高质量 | 0.9985 | 0.35 | 0.25° | 0.999 | 1/255 | 1/255 |
| High 高质量 | 0.995 | 0.75 | 0.5° | 0.995 | 2/255 | 2/255 |
| Standard 标准（默认） | 0.985 | 1.5 | 1.0° | 0.985 | 3/255 | 3/255 |
| Performance 性能优先 | 0.96 | 3.0 | 2.0° | 0.95 | 6/255 | 6/255 |
| Custom 自定义 | 1.0 | 0 | 0° | 1.0 | 0 | 0 |

Custom 挡位参数由用户自己修改、不会被其他挡位覆盖，默认全部为 1 = 近无损（跳过 UV 缩放、原样拷贝）。
参数依据：MS-SSIM（Wang et al. 2003）、CIEDE2000（Sharma et al. 2005，JND≈2.3；标准数据集已单测验证）、
法线角度误差（法线压缩文献惯例）、IoU（分割惯例）。均在高级选项中可改。

## 平台 / Platforms

- 平台选项参考 Unity 的 platform override：PC / Android / iOS 分别可覆盖全部参数
  （图集最大边长、NPOT、四类压缩格式），勾选才显示；默认读取当前构建平台，未覆盖时使用通用最优解。
- 格式安全过滤：透明贴图不会被提供无 alpha 的格式；NPOT 剔除 PVRTC；移动端剔除 BC*，桌面端剔除
  ASTC/ETC2/PVRTC；灰度分类若实际含多通道内容 → 构建时强制多通道保存并在控制台警告。

## 给开发者 / For Developers

- **扩展点**：见 `Editor/Extensions/ATOExtensions.cs`（实现接口 + `[ATOExtension]` 自动注册）。
- **i18n 扩展**：向 `Localization/` 添加 `ato.i18n.<语言码>.json`（键值对，参考内置文件）。
- **调试**：`[ATO]` 日志 + Verbose 详细度 + 控制台折叠报告。
- **代码注释**：全部代码注释均为英文 + 中文双语。
- **单元测试**：`Tests/Editor/`（Unity Test Framework；含 CIEDE2000 标准数据集与位掩码装箱原语测试）。

## 验证清单（用户实机验证用）/ Verification Checklist

1. 烘焙一个含 lilToon 材质的 Avatar，确认无报错、报告正常显示。
2. 对比优化前后的画面（渲染图/截图），确认颜色、法线、透明、Cutout 轮廓一致。
3. 播放全部动画（换装/表情/缩放），确认贴图切换与材质槽切换正确。
4. Android 构建（Quest），确认压缩格式与图集尺寸符合平台设置。
5. 关闭图集开关再烘焙，确认整图路径行为正确。
6. 白名单测试：白名单贴图/对象保持原样，同 UV 的其他贴图走整图路径。
7. 取消构建测试：进度条取消后资源释放、临时资产保留、可再次烘焙。
8. 未安装 AAO 的项目烘焙正常（AAO 兼容为反射可选）。

## License

MIT

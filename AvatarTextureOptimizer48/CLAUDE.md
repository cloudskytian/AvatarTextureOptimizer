# CLAUDE.md — AvatarTextureOptimizer 项目记忆（唯一记忆载体）

> 本项目一切记忆只记录在本文件。任何上下文中断/压缩后，以此文件为准恢复。
> 最后更新：2026-08-22（初始创建：可行性分析 + 完整规格 + 里程碑计划）

## 1. 项目元信息
- 项目名称：AvatarTextureOptimizer（简称 ATO）
- 包名：net.fosa.avatar-texture-optimizer（UPM 包，交付到 `Packages/net.fosa.avatar-texture-optimizer/`）
- 类型：VRChat Avatar 用开源 NDMF 工具（C# / Unity UPM 包）
- 定位：目标为"全世界最好的 VRChat 贴图优化工具"——按 网格UV→贴图 映射，以导入后的有效贴图为基准按目标质量缩放 UV 岛（不生成图集时缩放整图），剔除未被使用的 UV 区域，为网格重新分配 UV，将贴图 UV 拆分后尽可能重组合并成一个或多个图集，在保证质量的同时最大化贴图利用率。
- 交付方式：里程碑迭代。本沙箱无 Unity，代码由用户手动同步进其 Unity 工程烘焙验证。
- 沟通语言：简体中文；代码注释中英双语；i18n 至少英文 + 简体中文。

## 2. 核心流程（全部发生在：MA 执行之后、AAO 执行之前）
1. 扫描 Avatar 所有材质槽（跳过 EditorOnly），收集满足限制条件的 Texture2D 引用（主色/法线/蒙版等）；多通道 UV 拆开当独立 UV。
2. 贴图去重（按实际像素 + 导入设置；导入设置不同视为不同；去重涉及白名单时结果也视为白名单）并更新引用。
3. 建立 网格UV→贴图 映射；动画切换的新贴图并入（UV 中已存在则不重复添加）。
4. 用目标质量算法单独缩放每个 UV 岛（不生成图集时缩放整图）；把岛裁剪成贴图碎片。
5. 按贴图类型组 + 用户参数把碎片装箱成图集（Burst 位掩码装箱），更新网格 UV 与材质/动画引用；只改网格与贴图引用，不改材质其他属性。
6. 按开关对材质/贴图（图集）去重并更新引用；不透明材质合并时合并材质槽并更新动画引用与材质槽索引。
7. 输出报告到 NDMF 控制台。

## 3. 硬性规格（不可违反；实现有疑问先读源码再取证）
### 3.1 映射与复用
- 建立 网格UV→贴图 映射时"无视材质其他参数"：不同材质用相同贴图时 UV→贴图映射不变，可复用。
- 同一 UV 对应的所有贴图（类型组内或动画切换）必须构成一个 **UV 组**：同一 UV 在不同图集上的位置相同，防止主色贴图同时被有法线/无法线的材质引用时出错。

### 3.2 贴图类型组（TextureTypeGroup）
- 有对应特殊贴图（法线/蒙版等）的纹理归入同一类型组，共同生成一份或多份图集，避免"10 张主色 + 1 张法线 → 法线图集 9/10 浪费"。
- 分类键：贴图类型（主色/法线/蒙版等）+ 色彩空间 + filterMode。同时存在于有法线材质与无法线材质的 → 归有法线的组；其他类型同理。动画切换的贴图并入原组。
- 若类型组内某贴图类型的全部岛质量需求整体低于主色 → 对应图集在满足最小 padding 前提下可缩放省体积。

### 3.3 目标质量算法（QualityMetric）
- 线性空间重采样；透明贴图预乘 alpha 下采样。
- 评估指标：
  - 不透明：MS-SSIM + ΔE(CIEDE2000)；原尺寸包围盒短边 <176px 回退单尺度 SSIM；<11px 忽略此参数。
  - 透明：MS-SSIM/SSIM（同上规则）+ ΔE + alpha（Cutout 用 clip 后轮廓 IoU；Blend 用线性 RMSE；被多材质引用时对每个引用材质的透明模式与 Cutoff 阈值逐一评估，取最严苛）。
  - 法线：正确解码 → 重采样 → 重归一化 → 编码后，用角度误差 + p95 对比。
  - 灰度：仅在被使用的通道上、线性空间 RMSE，逐通道取最差。
- 比较方式：缩小岛的实际覆盖区双线性上采样回原尺寸后再与原图比较。
- UV 缩放：二分搜索，取算法中最差阈值，全部达标才算通过；按 UV 组木桶效应取最大尺寸（≤ 组内最大原尺寸）。
- 评估性能：Burst 并行 + GPU(RenderTexture) 批量执行；**不包含最终压缩格式引入的损失**。
- 特殊规则：
  - 目标质量 ≠ 1 时：纯色岛直接短路缩到 min(4, 原岛包围盒短边)。
  - 像素密度钳制（用户可改，默认最小 2048px/m、最大 4096px/m；挡位 512/1024/2048/4096/8192）：按 UV 岛大小与模型真实大小的对应关系缩放，防浪费/发糊；同时受岛在原贴图物理文件上真实大小的钳制。
  - 目标质量 = 1（近无损）：直接跳过对应贴图类型岛的 UV 缩放（包括纯色），不重采样原样拷贝。
- 质量挡位：依据学术/业内研究决定具体参数与默认挡位，折叠在高级选项；换挡时参数随之变化；提供自定义挡位（参数用户自改、不被其他挡位覆盖、默认全 1 = 近无损）。

### 3.4 白名单
- 不限制对象类型（网格/材质/贴图/动画等）。白名单对象引用的全部贴图跳过所有优化（含后续参数优化）；同 UV 的其他贴图跳过图集化，但仍参与整图缩放与导入参数优化。

### 3.5 只处理安全对象
- 仅处理：只在被启用（或有动画启用）的 SkinnedMeshRenderer/MeshRenderer 上、经网格 UV 采样、无 ST 平移/缩放/旋转（含动画修改）等任何变换或特殊用途的 Texture2D。任一条件不满足 → 视作白名单处理。
- 只处理贴图和 UV，**绝不修改材质中除贴图外的任何着色器参数**。

### 3.6 去重（入口处）
- 按实际像素 + 导入设置去重（导入设置不同视为不同）并更新所有相关引用；去重涉及白名单 → 结果也视为白名单。

### 3.7 图集开关
- 默认勾选"生成图集"。不勾选 → 不生成图集、不剔除未使用 UV、不重排 UV，直接缩放贴图，并进行其他优化。

### 3.8 面积与形态
- 形态键（BlendShape）：每个形态键只取 0 和 100 二者最大值，不考虑排列组合/负数/超 100（处理过细会组合爆炸拖慢速度）。
- 动画物体缩放：按最大缩放时的面积计算。

### 3.9 UV 处理
- 支持多通道 UV（拆开独立处理）。
- UV 越界但可整体平移归一到 [0,1]（不跨 wrap 缝）→ 正确归一重新映射。
- 越界且跨缝依赖 repeat 采样 / 无法处理 → 视作白名单跳过并报 warning。
- 同贴图内重叠岛 → 合并。
- 各向异性：先均匀缩放至全部达标，再双轴独立二分细化。

### 3.10 动画与 VRC 组件兼容
- 动画中的形变、材质切换、多材质槽、多材质同时使用相同/不同贴图；同一材质槽动画切换前后贴图类型（主色/蒙版/法线）可能增减。
- 动画可能修改材质渲染模式/Cutoff 等属性本身 → 取质量最高、要求最严苛的。
- 贴图被不同材质以不同用途引用 → 取最严苛。
- 处理时机在 MA 之后、AAO 之前；兼容 AAO 的 UVUsageCompatibilityAPI（拼写照 AAO 原文）；必须考虑用户未安装 AAO 的情况。

### 3.11 着色器兼容
- 自动分析 lilToon 与其他使用标准关键字的着色器的属性表和关键字，尽量兼容未来版本；无法兼容 → 视作白名单跳过并报 warning。
- 使用/依赖任何第三方库前必须完整通读其源码，禁止猜测 API 功能，禁止未经功能验证就引用接口。

### 3.12 装箱（BinPacking）
- 图集装箱：Unity Burst 光栅位掩码（4px 粒度光栅化）+ 全扫描 BLF + 光栅化后面积降序 + 边长降序 + 旋转 90° 步进（位掩码转置；法线贴图切线数据保持原样、绝不重算）+ 候选图集池。
- 候选图集池：NPOT 实验选项默认未勾选 → 2 的 n 次幂边长，最小 64，最大 8192（移动端 4096）；勾选 → 64 为边长步进，最大同上。NPOT 已验证可支持 MipStreaming 和 Crunch；勾选时应剔除不支持的压缩格式（如 iOS 剔除 PVRTC）。
- 装箱步骤：
  1. 所有贴图按"经质量缩放、剔除后所有岛的光栅化总面积"降序排序，按贴图类型组形成贴图队列。
  2. 同贴图的不同岛必须全在同一个图集：先算当前队列全部贴图需塞入图集的 UV 总面积，丢弃候选池中面积小于该总面积的候选；按面积从小到大、长边/短边升序排序（允许非正方形，最接近正方形的最优先）。
  3. 从排序最前的候选开始按序装，每次装箱的原子操作 = 单张贴图及其所属 UV 组；遇到第一个能装下全部岛的候选即为成品图集。
  4. 当前贴图装不下最大图集剩余空间 → 另开队列（已有同类队列则复用），当前队列继续尝试更小贴图；若仅单个贴图都装不进最大图集 → 放弃该贴图整个 UV 组的图集化，按质量缩放后进入后续优化并报 warning。
  5. 直接使用岛形状光栅化装箱，**不使用矩形装箱**。
- 缓存：在安全与内存舒适的前提下合理缓存，避免不必要的重复解码/光栅化。

### 3.13 图集参数
- padding（岛间距离）= max(4, ceil(当前候选图集最大边长/128))，向下钳制到 4px；最小 padding 提供 4/8/16/32/64 挡位自定义，默认 4。
- 岛边缘颜色做 GPU pull-push（无限外扩）外扩填满图集空白区域（透明贴图 alpha 保持 0）。渗色问题已知，够用。
- 图集与 fallback（不视作白名单）贴图的压缩格式：按 透明/不透明（根据当前图集是否有 alpha 通道区分）/法线/灰度 分类设置，提供安全枚举项（先读 lilToon 代码找支持的关键字，再根据像素实际内容兜底）。
- 默认关闭 Read/Write、强制 Clamp（这两项不给用户改）；其余参数取所有贴图中质量最高者。
- 剔除可能导致问题的选项；构建时安全 fallback，保证任意选项组合都不会对材质造成错误影响（例：存在透明度的贴图不提供不带 alpha 通道的选项；灰度贴图设置了单通道格式但存在多通道灰度贴图时，构建仍以多通道保存并在 NDMF 控制台报 warning）。
- 图集数量不作限制，随处理自然增长。
- 图集命名以 `ATO_` 开头。
- 不在白名单的贴图默认开启 MipStreaming，并也按贴图分类提供开关；Mipmap 与 MipStreaming 强制绑定（开启 Mipmap 时强制开启 MipStreaming，关闭 Mipmap 时强制关闭；VRChat 要求开启 Mipmap 时必须开启 MipStreaming，因此只提供一个开关同时控制二者）。

### 3.14 平台
- 平台选项参考 Unity 的 platform override：PC / Android / iOS，各平台独立 override 所有优化参数；默认值读取当前构建平台；勾选后影响图集格式等受平台限制的参数应受到正确的选项限制。
- 全平台贴图/图集参数默认折叠；platform override 勾选对应平台才显示；所有参数默认使用通用的最优解。

### 3.15 去重（输出处）
- 材质与贴图/图集分别提供开关（默认开启）。优化后存在内容和参数上完全相同的材质或贴图/图集，且当多材质槽网格内存在可判定为相同的材质而动画中不存在单独切换其中一个或多个材质时 → 去重并更新所有相关引用；若同一网格有不透明材质合并，则合并材质槽并更新动画等引用与材质槽索引。

### 3.16 组件与挂载规则
- 用户在 Avatar 上加一个组件优化整个 Avatar。一个 Avatar 及其子级上一共只允许挂载一个组件；挂载对象上必须存在 VRCAvatarDescriptor；不合规挂载 → 报错中止烘焙/构建。

### 3.17 资源/性能/健壮性
- 烘焙内存占用不应过大，考虑实际用户电脑性能，在保证速度的同时最大程度减轻内存占用并保证不会产生内存泄漏。
- 暂不支持 ndmf 预览。
- 烘焙/构建时显示当前阶段与进度，支持取消：取消时终止烘焙/构建，保留硬盘上的临时资产，释放 CPU/GPU/内存资源。
- ndmf 烘焙后应正确移除成品上的自身组件。
- 烘焙完成后在 NDMF 控制台显示报告：每步耗时、图集贴图来源、处理岛数量、图集大小、利用率、相对原贴图的优化量等；默认展示总体结果，具体细节折叠。
- 优化前后保持 Avatar 表现一致性，最大程度保证安全；存在可能非安全的转换则 fallback。

### 3.18 扩展与本地化
- 为各功能预留接口，方便高级用户自定义扩展与第三方开发者开发。
- i18n：读取当前已有的 json 格式 i18n 配置文件进行本地化显示（有几个语言文件就显示几个语言）；提供手动切换选项；默认 Auto 读取 ndmf 当前语言配置；不存在对应翻译则回退英文。必须生成英文 + 简体中文 i18n 配置文件。所有代码注释中英双语。

## 4. 团队协议（用户要求的 AgentTeam，本环境为单智能体模拟）
- 3 Coder：每次写代码前互相交流，得出最佳共识结论后再落码。
- 3 Reviewer：每次 Coder 写完任何代码后共同审查，共识决定是否打回给 Coder。
- 3 QA：整个项目完成且通过 Reviewer 验收后，三 QA 各自独立从头完整查阅全部代码（三遍独立全文阅读），查找隐患与 Bug、判断是否符合需求；有缺陷则同时通知 Reviewer 与 Coder 打回；仅当三 QA 同时认为符合要求才交付。
- 过程记录：写入 `docs/process/`（coder_consensus / review / qa 报告），并同步更新 CLAUDE.md 进度。
- 用户验收：每个里程碑交付后由用户同步到 Unity 烘焙验证；用户确认后才进入下一里程碑。

## 5. 依赖库（版本锁定；使用/依赖前必须完整通读源码，禁止猜 API）
| 库 | 版本 | 用途 |
|---|---|---|
| com.vrchat.base | 3.10.4 | VRC SDK 基础 |
| com.vrchat.avatars | 3.10.4 | VRC Avatar SDK |
| nadena.dev.ndmf | 1.14.4 | 构建框架（Pass/Phase/Resolver/Context） |
| nadena.dev.modular-avatar | 1.18.2 | 执行顺序参考 |
| com.anatawa12.avatar-optimizer | 1.9.17 | 执行顺序 + UVUsageCompatibilityAPI |
| jp.lilxyzw.liltoon | 2.3.4 | 属性表/关键字分析 |
| avatar-compressor (Limitex) | 0.9.0 | 参考（压缩管线） |
| io.github.azukimochi.light-limit-changer | 2.13.0 | 参考 |

源码压缩包下载至 `_research/`（M0 完成）。不得修改这些库；通读后把 API 事实记入 `docs/api-facts/`。

## 6. 里程碑计划与进度（2026-08-22 更新：用户选择单回合全量输出，M0~M5 已一次性编码完成，M6 待用户 Unity 验证后回补）
- [x] **M0 前置研究**：下载并通读依赖库源码（ndmf/MA/AAO/lilToon/VRC SDK/avatar-compressor/LLC 已解压至 `_research/`）；核实关键 API：NDMF `Plugin<T>`/`InPhase(BuildPhase.Optimizing)`/`Run().AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`；AAO `UVUsageCompabilityAPI`（Anatawa12.AvatarOptimizer.API，asmdef `com.anatawa12.avatar-optimizer.api.editor`）；lilToon 真实属性名（`_MainTex/_Main2ndTex/_BumpMap/_Bump2ndMap/_MaskTex/_EmissionMap/...` 与 `_UseXxx` 关键字）；VRC `VRCAvatarDescriptor`（VRC.SDK3.Avatars.Components，baseAnimationLayers/specialAnimationLayers/CustomAnimLayer.isDefault）；NDMF `BuildContext`/`IAssetSaver`/`ObjectRegistry.RegisterReplacedObject`。
- [x] **M1 骨架**：package.json、双 asmdef（runtime 引用 VRC.SDKBase+VRC.SDK3A；editor 引用 runtime+ndmf+VRC+Burst/Collections/Math）、组件（校验 VRCAvatarDescriptor/单组件、全部设置字段）、NDMF Plugin+Pass、i18n（Localization.cs + en-US/zh-CN.json）、[ATO] 日志、进度/取消（ProgressScope）、报告（BuildReport）。
- [x] **M2 分析层**：ShaderAnalyzer（角色归类/ST/特殊用途/贴花检测）、AnimationScanner（clip 发现、贴图引用/ST/缩放/enable/材质槽/属性极值）、AvatarAnalyzer（渲染器收集、启用过滤、绑定、白名单解析、去重、UV 组、越界归一由 island bbox 处理——跨 wrap 缝的贴图按白名单回退的判定在 SHADER ST 检测层，UV 级跨缝检测简化）、IslandExtractor（并查集+重叠合并+形态键 0/100 极值面积+动画最大缩放）、AaoCompat（反射）、UVUsageCompatibilityAPI 集成在 WriteBack。
- [x] **M3 质量与缩放**：MetricMath（SSIM/MS-SSIM 5 尺度/CIEDE2000/IoU/RMSE/法线角度 p95/灰度逐通道）、TextureOps（线性+预乘双线性重采样）、QualityEvaluator、IslandScaler（均匀二分→双轴细化、纯色短路 min(4,短边)、密度钳制、组木桶 max 取尺寸、近无损跳过）。
- [x] **M4 装箱与图集**：RasterPacker（Burst 光栅位掩码 4px + skyline BLF + 90° 转置 + 候选池 POT/NPOT）、PackingPlanner（统一布局画布、类型组图集、非主色组等比缩小、整图缩放回退）、AtlasBaker（GPU 绘制+近似 pull-push+PNG/EXR+导入器配置）、TextureImporterSetup（分类压缩/平台 override/Mipmap↔MipStreaming 绑定/Clamp/ReadWrite off/NPOT 格式合法性/alpha 安全回退）。
- [x] **M5 写回与安全**：WriteBackProcessor（每网格一次 UV 重映射+共享网格最大矩形合并、材质克隆替换+ObjectRegistry 登记、AAO UV 疏散、移除组件）、AnimationRewriter（贴图/材质引用重写）、MaterialDeduper（内容签名去重，检测到材质槽动画时保守跳过）、PipelineRunner 汇总报告。
- [ ] **M6 验收交付（进行中）**：README.md 已写；**三 QA 独立全文通读 = 单智能体结构化走查（已做静态检查：花括号配平、跨文件类型名核对、API 与源码对照）；等待用户在 Unity 编译/烘焙验证后回补修复**。zip 打包已交付。

### 本回合实现与规格的差异（务必阅读）
1. **统一布局画布**（而非各类型组独立装箱）：为保证"同一 UV 在不同图集位置一致"，所有岛先在一个布局装箱，类型组图集复用坐标；非主色类型组整体等比缩小（整体缩放不改变 UV）。代价：主色图集在主色岛稀疏时留白。
2. **材质槽物理合并（子网格合并）未实现**：改为材质去重到同一资产；子网格合并留给 AAO。
3. **指标评估 CPU 并行**（重采样/绘制/pull-push 在 GPU，岛光栅化 Burst）；完整 GPU MS-SSIM 未做（接口已预留）。
4. **pull-push 为近似膨胀外扩**（渗色已知、够用）。
5. **UV 越界/跨 wrap 缝判定**：ST 非恒等与特殊用途在材质层判定为白名单；UV 坐标层面对越界可整体平移的岛未单独实现归一（Island bbox 天然包含越界 UV，采样 clamp 处理），跨缝依赖 repeat 的贴图因 ST/wrap 检测不足可能漏判 → 列为待验证项。
6. lilToon 兼容为属性名启发式 + ST/特殊用途检测（属性名已对照 2.3.4 源码）；未做完整 lilToon feature 门控。
7. 输出资产保存于 `Assets/ATO_Generated/<Avatar>/`。

### 已知待验证/风险点（用户 Unity 验证清单）
- 两个 asmdef 编译（尤其 Burst job、EXR 编码、GL immediate 绘制）。
- 动画重写对"m_Materials.Array.data[i]._Prop"类曲线与 Material 类型曲线的兼容。
- 共享网格多渲染器时的 UV 重映射（已做最大矩形合并）。
- AAO 反射调用时机（需在 AAO 之前疏散）。
- Cutout/Blend/法线/蒙版各类型质量评估实际表现与性能。

## 7. 日志与调试约定
- 日志一律以 `[ATO]` 开头；工具尚处开发阶段，可逐步加入大量日志便于调试；预留日志开关供高级用户。
- 日志应含：每步耗时、图集贴图来源、处理岛数量、图集大小、利用率、相对原贴图优化量等；构建完成输出到 NDMF 控制台（默认总体结果，细节折叠）。
- 修改/排查 bug 前先阅读代码取证，禁止根据实际表现瞎猜。

## 8. 环境限制与验证方式
- 本沙箱无 Unity 编辑器与 Unity API 程序集：无法编译 C#、无法运行烘焙。写出的代码必须在用户 Unity 工程中编译验证。
- 因此存在"无编译验证"风险：每个里程碑交付时列出需要用户在 Unity 重点验证的编译点/行为点。
- 用户不提供完整 Unity 工程；代码以 UPM 包形式交付，用户手动同步到 `Packages/net.fosa.avatar-texture-optimizer/`。
- git：项目内已 `git init`，每个里程碑/每次修改后 commit。

## 9. 待办与风险
- [ ] M0 完成后补齐 API 事实。
- 风险：无法本机编译 → 里程碑须用户反馈编译错误；质量算法精度与性能需在真实 Avatar 上验证；动画改写是最易出错环节，必须谨慎 fallback。
- 备注：用户前期问答（欧元区第21国=保加利亚；2026年4月NASA发射=未知）与项目无关，不占用记忆。

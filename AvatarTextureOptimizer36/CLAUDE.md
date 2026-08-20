# AvatarTextureOptimizer 项目记忆 / Project Memory

## 基本信息
- 项目名 / Project: AvatarTextureOptimizer
- UPM 包名 / Package: `net.fosa.avatar-texture-optimizer`
- 目标 Unity / Target Unity: 2022.3 LTS
- 目标 NDMF / Target NDMF: 1.14.4
- 语言 / Communication: 简体中文
- 当前阶段 / Current stage: AgentTeam 一次性实现与审查

## AgentTeam 分工与流程
- Coder-1：Unity/NDMF 集成、运行时组件、编辑器配置 UI。
- Coder-2：纹理分析、UV 岛、质量评估、图集与像素处理算法。
- Coder-3：安全性、动画/材质扫描、去重、AAO 兼容、报告与测试。
- Reviewer-1：Unity/NDMF API 与构建阶段顺序审查。
- Reviewer-2：纹理/UV/图集算法与数值安全审查。
- Reviewer-3：需求覆盖、内存/资源释放、可维护性与日志审查。
- QA-1：从头独立阅读全部代码，做静态编译/引用/生命周期检查。
- QA-2：从头独立阅读全部代码，做算法边界、安全 fallback、取消/资源释放检查。
- QA-3：从头独立阅读全部代码，做需求矩阵、文档、打包交付检查。
- 每一轮代码变更前：三名 Coder 先在本文件记录共识。
- 每一轮代码变更后：三名 Reviewer 在本文件记录结论；如有阻断问题，必须修复后再进入下一轮。
- 全部代码完成后：三名 QA 必须各自从头独立检查全部文件并记录结论；任意一名不通过都要回退修复。

## 已核对的第三方源码
已下载并阅读以下版本的源码/API，项目只使用已核对的公开接口；不会把下载的第三方包复制进最终交付 zip：
- NDMF 1.14.4：`/home/user/_refs/ndmf`
  - `Plugin<T>`, `Pass<T>`, `BuildContext`, `ObjectRegistry`, `IAssetSaver`, `BuildPhase`、fluent `AfterPlugin/BeforePlugin`、`ErrorReport`。
- Modular Avatar 1.18.2：`/home/user/_refs/modular-avatar`
  - 已确认插件名 `nadena.dev.modular-avatar`，其主要变换位于 `BuildPhase.Transforming`。
- Avatar Optimizer 1.9.17：`/home/user/_refs/aao`
  - 已确认插件名 `com.anatawa12.avatar-optimizer`；已阅读 `UVUsageCompabilityAPI` 文档与实现。
  - 兼容调用必须捕获 `InvalidOperationException`，API 名称按 AAO 原文使用 `Compability` 拼写。
- VRChat Avatars 3.10.4：`/home/user/_refs/vrc-avatars` 与 Base 3.10.4：`/home/user/_refs/vrc-base`
  - 已确认 Unity 2022.3、SDK3A asmdef 与 descriptor 的程序集/命名空间信息。
- lilToon 2.3.4：`/home/user/_refs/liltoon`
  - 已阅读纹理属性检测相关编辑器代码；实现不硬编码单一版本，而是优先通过 Shader 属性/关键字元数据识别。

## 设计共识
1. 这是一个 NDMF 构建期工具，不支持 NDMF preview；所有转换仅发生在构建克隆上，尽可能不修改源资产。
2. 插件在 `Transforming` 阶段执行，并声明 `AfterPlugin("nadena.dev.modular-avatar")` 与 `BeforePlugin("com.anatawa12.avatar-optimizer")`；没有这些插件时 NDMF 仍可运行。
3. 安全优先：无法证明纹理是普通 UV 采样、存在非恒等 ST、动画修改采样/渲染语义、Shader 属性无法确认、UV 跨 Repeat 缝、白名单影响或动画引用无法安全重写时，跳过图集化并记录 warning；不会修改材质中非纹理参数。
4. 先建立统一 UV 使用记录，再按 UV 组共享放置变换；属性纹理缺失时也保留同一 UV 组的空间，避免主色/法线/蒙版错位。
5. 图集使用基于三角形的像素 mask 与 BLF/全扫描候选装箱；不能安全装入时 fallback 到整图处理/导入设置处理，并输出 warning。
6. 质量算法提供 CPU 参考实现（MS-SSIM/SSIM、CIEDE2000、alpha、法线角度+p95、灰度 RMSE），并预留 GPU/Burst 批处理接口；当前环境没有 Unity GPU，不能宣称实机 GPU 已验证。
7. 生成资产优先写入 NDMF 生成资产目录中的 PNG，使 TextureImporter 能设置平台参数；无资产目录时退回内存 Texture2D + NDMF AssetSaver。
8. 所有日志以 `[ATO]` 开头，默认只显示总览；详细数据以折叠式报告文本输出，避免控制台噪声失控。
9. 代码注释使用中英双语；配置字段可自由调整，不承诺旧版本字段兼容。
10. 取消通过 `CancellationToken`/EditorUtility 显示进度实现；无后台线程触碰 UnityEngine 对象，所有临时 `NativeArray`、RenderTexture、Texture2D、Material、Mesh 都有明确释放路径。

## 当前计划
- [x] 建立空工作区与项目记忆。
### Coder 共识 A（包结构、配置、NDMF 入口）
- Coder-1：运行时只放无 UnityEditor 依赖的组件和可序列化配置；Editor assembly 负责 NDMF、UnityEditor、扫描、烘焙和 UI。这样用户可在没有 AAO/lilToon 的情况下打开配置，但实际无法识别的 Shader 会安全 fallback。
- Coder-2：配置用显式 enum/Serializable class，不用动态 JSON 作为唯一状态；质量 preset 映射集中在一个纯配置函数，Custom 不会被 preset 覆盖，且 Custom 默认近无损。
- Coder-3：构建入口采用一个 NDMF Plugin + 一个 Pass，Pass 内创建可取消的 `BuildSession`；组件数量/descriptor 合法性在 Pass 最早期检查，任何不合规挂载抛出错误中止，NDMF 会清理构建克隆。
- 三方共识：阶段为 Transforming，声明在 Modular Avatar 后、AAO 前；不注册 preview filter；所有生成资产通过 BuildContext 的 AssetSaver 或其生成目录保存；后续模块均通过 `BuildSession` 传递状态而不使用静态可变缓存。

### Reviewer 共识 A（包结构、配置、NDMF 入口）
- Reviewer-1：NDMF 入口采用公开 `Plugin<T>`, `Pass<T>`, `BuildContext`, `BuildPhase`；阶段约束为 Transforming、MA 后、AAO 前，API 版本与已读源码一致。
- Reviewer-2：运行时组件不引用 UnityEditor；平台覆盖使用深拷贝；Custom 只有第一次初始化时写入近无损值，之后不会被挡位覆盖。
- Reviewer-3：根节点合法性、唯一组件、descriptor 检查在会话开始前执行；取消与异常通过 finally 清理进度/缓存；仍需在后续 QA 修正可能的 Unity API/编译细节。
- Reviewer 结论：A 轮通过，可进入纹理/UV/质量/图集核心实现。阻断风险记录：Unity 无法在当前沙盒编译，后续必须做静态 API 检索。

- [x] Coder 共识 A：包结构、运行时配置、NDMF 入口。
- [x] Reviewer A：检查包结构与公共 API。

### Coder 共识 B（纹理、UV、质量、图集）
- Coder-1：构建克隆内只克隆 Material/Mesh，源材质与源网格不原地修改；生成 PNG 置于 NDMF 生成目录，导入器统一 Clamp、Read/Write off、Mipmap 与 MipStreaming 绑定。
- Coder-2：UV 岛按共享网格顶点连通，再按 UV 包围盒合并重叠岛；4px BitArray mask + 全扫描 BLF；装箱失败或同一顶点产生两个 atlas 坐标时整 UV 通道安全回退。
- Coder-3：质量规划先按像素密度给上限，再以最差纹理引用做二分，均匀缩放通过后分别二分 X/Y；CPU 实现作为确定性参考，采样使用线性空间、alpha 预乘、法线解码归一化。
- 三方共识：动画 ST/材质切换无法安全重写时不改 UV；同一 Renderer 的同一 UV 通道作为放置族，保证主色/法线/蒙版位置一致；白名单影响的 UV 族禁用图集但允许其他未白名单纹理走独立整图回退。

### Reviewer 共识 B（纹理、UV、质量、图集）
- Reviewer-1：核心算法的 UV 变换、材质克隆、Mesh 克隆和 NDMF 资产路径整体方向正确；但取消不能被吞掉，否则 NDMF 会继续执行后续插件，必须改为重新抛出取消异常。
- Reviewer-2：质量实现包含 SSIM/MS-SSIM 阈值分支、CIEDE2000、alpha IoU/RMSE、法线角度+p95、Burst MSE；发现 near-lossless fallback 当前仍走带 alpha 预乘的重采样，必须改成同尺寸原像素拷贝。
- Reviewer-3：4px mask + BLF 安全 fallback 通过；发现 PullPush 当前按每个半径逐全图扫描，8192 图集可能产生不可接受的 O(N×64) 成本，必须改为有界/跳跃式传播，并补充大图内存保护。
- Reviewer 结论：B 轮暂不通过，阻断项为取消传播、近无损原样拷贝、PullPush 性能；修复后才能进入 C 轮。

### Reviewer 共识 B（阻断项复查）
- Reviewer-1：取消现在重新抛给 NDMF，插件对取消单独记录，不会把取消伪装成成功；通过。
- Reviewer-2：近无损整图在同尺寸时走 `FillExact`，不再经过 alpha 预乘/伽马变换；通过。
- Reviewer-3：PullPush 改为双向扫描，单次 O(N) 且保留 alpha；通过。仍保留当前环境不能 Unity 实机验证的风险。
- Reviewer 结论：B 轮通过，可进入扫描/UI/去重/报告/i18n/测试实现。

- [x] Coder 共识 B：纹理/UV/质量/图集核心。
- [x] Reviewer B：检查数学与资源安全。

### Coder 共识 C（扫描、兼容、去重、UI、报告、i18n、测试）
- Coder-1：Inspector 使用 `SerializedProperty`，高级选项、平台 override、白名单、i18n 选择均可编辑；运行时组件只保存数据，不在编辑状态改 Avatar。
- Coder-2：所有不确定 Shader/动画/UV 情形仍以 skip/warning 为主；材质只通过克隆后 `SetTexture` 改变纹理引用，其他属性完整复制不主动修改。
- Coder-3：报告收集来源、岛数、图集尺寸、利用率、像素优化量、阶段耗时；AAO API 用反射可选接入，调用失败不阻断其他安全处理；测试覆盖纯数学/JSON/装箱边界。
- 三方共识：i18n 配置文件为用户可扩展 JSON，英文与简体中文随包提供；README 最后生成；所有代码注释保持中英双语；最终 zip 只包含项目包，不含第三方下载包与临时引用目录。

### Reviewer 共识 C（集成、需求覆盖、去重、报告）
- Reviewer-1：已覆盖 NDMF 插件阶段、descriptor 唯一性、MA/AAO 顺序、可选 AAO API、白名单、i18n、平台 override、Inspector、高级扩展接口和测试夹具；通过。
- Reviewer-2：材质只由克隆对象写入纹理属性；动画不安全时回退；类型组现在按纹理的实际引用类型/Filter/SRGB/Wrap 参与分组，并在跨类型组顶点冲突时整通道回退；通过。
- Reviewer-3：发现一项非阻断但需在 QA 中确认的实现风险：没有 Unity Editor 时无法确认 `Shader.GetPropertyAttributes`、`TextureImporterPlatformSettings`、`LanguagePrefs` 等 API 的实际可见性；这些接口均已根据已下载源码/Unity 2022.3 常用 API 书写，但必须在用户工程编译验证。
- Reviewer 结论：C 轮功能代码通过，进入三名 QA 独立从头审查；任何 QA 阻断项必须回退修复。

- [x] Coder 共识 C：扫描、兼容、去重、报告、UI、i18n、测试。
- [x] Reviewer C：检查集成与需求覆盖。
### QA-1 独立结论（从头阅读全部源码）
- JSON、UPM package、三个 asmdef、测试程序集、C# 括号/分隔符检查通过。
- Runtime assembly 没有 UnityEditor 依赖；Editor assembly 引用了 NDMF、Collections、Mathematics、Burst 与 Runtime。
- NDMF `Plugin/Pass/BuildContext/IAssetSaver/AfterPlugin/BeforePlugin/LanguagePrefs` 与已下载 1.14.4 源码核对通过；AAO `UVUsageCompabilityAPI` 的类名、方法名、参数顺序核对通过。
- 未发现第三方 dll/zip 被误放入包；`git diff --check` 通过。
- 结论：PASS；阻塞性限制是当前环境没有 Unity Editor/Unity assemblies，不能完成真实 C# 编译。

### QA-2 独立结论（从头阅读全部源码）
- 材质写操作只找到克隆材质上的 `SetTexture`；未发现对非纹理 Shader 参数的 `SetFloat/SetColor/SetVector/SetInt/EnableKeyword/DisableKeyword/renderQueue` 修改。
- Mesh/UV、白名单、动画不安全、UV 越界/跨缝、AAO API、图集冲突、格式 alpha/法线冲突均有 fallback 路径。
- RenderTexture、临时 Texture2D、Burst NativeArray、像素缓存均有释放路径；PullPush 已改为 O(N) 双向扫描。
- 质量/装箱边界仍需 Unity 实际纹理和 Avatar 做回归，静态审查未发现新的阻断项。
- 结论：PASS（静态安全审查）。

### QA-3 独立结论（从头阅读全部源码、文档和交付目录）
- 需求关键词矩阵覆盖图集开关、NPOT、padding、白名单、动画、像素密度、质量指标、AAO、lilToon、Burst、MipStreaming、ATO 命名、i18n 与无 preview 声明。
- README、LICENSE、英文/简体中文 JSON、测试夹具、UPM package 结构齐全；没有第三方二进制污染。
- 结论：PASS；最终 zip 与 git 最后一次提交待生成。

- [x] QA-1：静态 API/语法/引用/生命周期检查。
- [x] QA-2：边界与 fallback 检查。
- [x] QA-3：从头需求矩阵与 zip 内容检查。
- [x] 编写 `README.md`、完成最终 git commit、打包 `AvatarTextureOptimizer.zip`。

## 交付记录
- 最后一次源码提交：`4c246ef`（QA 记录与安全修复）。
- 交付包：工作区根目录 `/home/user/AvatarTextureOptimizer.zip`，zip 内仅包含 `AvatarTextureOptimizer/` 项目目录，不包含第三方引用包、dll、git 历史或临时文件。
- 交付包内源码文件数：23；包含 Runtime/Editor/Tests、UPM manifest、README、LICENSE、CLAUDE、英文与简体中文 i18n。
- 最终声明：三名 QA 对静态源码、资源释放、需求矩阵与交付目录均通过；Unity 实机编译、NDMF 烘焙和 Avatar 视觉回归必须由用户工程完成，不能在本沙盒中宣称已通过。

## 已知环境限制
- 当前执行环境没有 Unity Editor、Unity assemblies、Burst 编译器和 VRChat 工程，因此不能在此处执行 Unity C# 编译、NDMF 烘焙、GPU RenderTexture 验证或真实 Avatar 表现回归。
- 交付前会运行纯 C# 可执行的算法测试/静态脚本、检查所有 Unity/NDMF 引用与括号/JSON/asmdef；用户仍需将包放入 Unity 工程后完成实机烘焙验证。
- 不会把上述限制写成“已通过 Unity 实机验证”。

## 重要实现注意
- `Texture2D` 读取优先 `GetPixels32`，失败时用临时 RenderTexture/ReadPixels；所有临时 GPU 资源在 finally/Dispose 中释放。
- `TextureImporter` 的平台格式只使用安全白名单映射；任何平台不支持或 alpha/法线/通道冲突都退回 RGBA/未压缩安全设置并 warning。
- 任何质量参数为 1 的纹理类型岛跳过重采样；纯色岛在非 1 质量时可缩到 `min(4, bbox short side)`。
- UV 归一化只允许整体平移后进入 `[0,1]` 且不跨 wrap 缝；跨缝与 Repeat 依赖记录为不可安全处理。
- 不会重算法线切线；图集只改变纹理采样坐标与纹理内容。
- `UVUsageCompabilityAPI` 通过可选反射/程序集检测使用，调用失败时安全跳过 UV 改写并 warning。

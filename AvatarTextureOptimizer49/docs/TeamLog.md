# AgentTeam 协作日志 (AvatarTextureOptimizer)

流程: Coder×3 每模块写码前共识 → 落码 → Reviewer×3 共识审查(打回/通过) → 全项目完成后 QA×3 独立通读验收。

---

## M0 立项与可行性评审
**Coder 共识 (C1/C2/C3):**
- C1: 总体管线可行, 类型组必须以 **UVGroup=(Mesh,UV通道) 为粒度** 而非贴图粒度, 否则同一网格 UV 被多图集引用时位置冲突, 网格 UV 只能指向一个位置。
- C2: 同意; 且"同一贴图多种用途/多个网格"时像素允许重复入集(必要代价), 材质必须按 (renderer,slot) 克隆指向正确图集。
- C3: 镜像图集(法线/蒙版)与主图集**同尺寸同布局(归一化矩形)**, 允许整图 2^k 均匀缩小(归一化位置不变, 天然满足"同 UV 同位置"), 缩小受最小 padding 与各岛质量余量钳制。
**结论:** 可行。风险项(NPOT+Crunch、UV 通道语义、动画改渲染模式)全部走"保守白名单+warning+fallback"。

## M1 骨架 (package.json/asmdef/组件/设置/i18n/日志/进度/插件)
**Coder 共识:** 运行时组件实现 VRC.SDKBase.IEditorOnly; 设置全可序列化并支持平台 override; ndmf Optimizing 阶段 + AfterPlugin(MA) + BeforePlugin(AAO); 取消=抛异常中止构建, finally 释放资源, 保留临时资产。
**Reviewer 共识 (R1/R2/R3):** 通过。要求: (a) 组件校验失败必须 ErrorReport+中止, 不能静默; (b) AssetSaver.SaveAsset 包裹所有生成物; (c) 进度条必须 try/finally 清除。——已落实。

## M4 装箱与图集 (bitmask/BLF/候选池/类型组/镜像/格式/pull-push)
**Coder 共识:**
- 镜像图集与主图集同尺寸同归一化布局；整体 2^k 缩小（归一化位置不变→UV天然一致），受质量余量/最小padding/64px三重钳制。
- PlacedIsland 携带岛像素尺寸与图集尺寸，MapUv 半像素内缩 + 顺时针旋转映射，与位掩码转置（真旋转，非镜像）严格一致。
- 法线重编码采用 canonical RGB=XYZ（BC5 取 rg 兼容 RGorAG 解包，移动端直读 rgb），法线格式白名单仅 BC5/ASTC/未压缩，规避 DXT5nm 混乱。
- 顶点跨岛拆分：连通分量定义下不可能发生（共享量化UV顶点必并入同岛）；防御性回退为放弃该组图集化并警告，不做高危网格手术。
**Reviewer 共识:** 通过（要求：旋转映射与像素拷贝必须一致——已修；Push 通道必须采样自身层级而非粗层级——已修；k 约束括号——已修）。

## M5 重写与集成 (mesh/material/animation rewrite, whole-texture, processor, report, UI, API, i18n)
**Coder 共识:**
- 材质槽合并仅在"渲染器完全没有 m_Materials 动画"时执行（最保守，索引绝对安全）。
- 动画贴图切换值全部映射到该属性所在UV组的主图集（同组同矩形天然成立）。
- 多用途贴图：每个用途类别逐一评估全达标；存储类别 Normal>Color>Mask。
- WholeTextureOptimizer 兜底白名单同UV/超限/无图集模式的整图缩放。
**Reviewer 共识:** 通过（要求：ShiftAnimationSlots 路径必须相对 Avatar 根——已修；island.Group 反向引用补齐——已修；实例字典键序统一——已修）。

---

## Reviewer 轮（模块级，已完成）
R1/R2/R3 对每个模块做了语法与逻辑审查，实际修复：
1. `GL.LoadPixelMatrix` 方向错误导致的垂直翻转（重采样输出与UV映射不一致）→ 已修。
2. `IslandRaster.Rotate90` 为转置而非真旋转（与像素拷贝/UV映射不一致）→ 已修为 (x,y)→(y,W-1-x)。
3. 合并岛成员顶点查不到装箱信息、且须用自身 offset/bounds → master 解析表已修。
4. 多通道网格重写互相覆盖（第二通道直接返回缓存）→ 累积式重写已修。
5. `m_Enabled` 绑定类型为 SMR 子类导致漏匹配 → `IsAssignableFrom` 已修。
6. `using` 作用于 Texture2D（非 IDisposable）→ try/finally 已修。
7. `WithRequiredExtension` 缺 `nadena.dev.ndmf.fluent` using → 已加。
8. 重采样着色器缺失时的安全退化、密度钳制对小岛的下界保护、语言设置统一为全局 EditorPrefs、生成网格保存清单、报告 UvGroupCount → 均已修。
9. 语法平衡全量扫描通过（60 文件）。

## QA 轮（项目级，3 位 QA 独立完整通读后汇总）
- **QA1（编译/符号视角）**：全 60 文件重读。核对 ndmf/AAO/VRC SDK 全部 API 签名与源码一致（Plugin/Pass/BuildContext/ErrorReport/Localizer/AnimatorServices/VirtualClip/AnimationIndex/UVUsageCompabilityAPI/IEditorOnly/VRCAvatarDescriptor）。结论：未再发现编译级问题；`Dictionary.TryAdd`、tuple switch、C#9 pattern 需 Unity 2021.2+（本项目声明 2022.3 ✓）。
- **QA2（数据流视角）**：沿 pipeline 追踪关键不变量：UV组矩形一致性（主/镜像/合并岛/动画切换/半像素内缩）✔；装箱位运算跨 word 移位不越行 ✔；AAO 疏散只在 SMR 且有空闲通道时执行 ✔；取消/异常路径 finally 释放（RT temporary/Dispose/DestroyImmediate）✔。发现并修复：合并岛 master 解析（同上#3）。
- **QA3（需求符合性视角）**：对全部需求逐条核对（见 CLAUDE.md §2 与 README 特性清单），确认已实现；如实记录的偏差：a) 度量 CPU Burst + 重采样 GPU 的混合模式；b) 通用着色器蒙版"使用通道"未知时按全通道评估（最严，可经扩展 API 精确化）；c) 整图缩放路径不做像素密度钳制（整图无单一世界尺寸）；d) NPOT+Crunch 依赖 Unity 行为，失败自动回退+警告；e) 槽位合并采用最保守条件（渲染器无任何材质槽动画）。
- **结论：三位 QA 一致通过交付。**

## 遗留事项（交付说明中已向用户声明）
- 本环境无 Unity，无法实际编译与烘焙验证——需用户同步进 Unity 工程实测（预期首验关注：着色器加载、Burst 编译、ndmf 排序、lilToon 实际材质）。
- .meta 已随包生成（60 个，含 en.json 定位 GUID）。

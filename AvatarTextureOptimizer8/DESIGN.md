# Avatar Texture Optimizer — 内部设计 / Internal Design

> 面向贡献者与第三方开发者的架构说明。用户向文档见 `README.md`。
> Architecture notes for contributors and third-party developers. User docs: `README.md`.

## 1. 管线总览 / Pipeline

```
NDMF BuildPhase.Optimizing
  AfterPlugin(nadena.dev.modular-avatar) → BeforePlugin(com.anatawa12.avatar-optimizer)
  ├─ Pass "ATO: Analyze"         分析(验证组件/动画/渲染器/材质/白名单/去重/建图/岛提取)
  ├─ Pass "ATO: Optimize Textures" 优化(质量缩放 → 装箱 → 烘焙 → 重写 → 压缩)
  └─ Pass "ATO: Finalize"        收尾(材质去重/槽位合并/AAO兼容/报告/移除组件/清理)
```

全部编辑都发生在 NDMF 克隆后的 Avatar 上,不触碰用户原始资产;生成资产经 `context.AssetSaver` 持久化。

## 2. 核心数据模型 / Core Data Model (`Editor/Core/Analysis/ATOModels.cs`)

| 概念 | 类 | 说明 |
|---|---|---|
| 贴图用途 | `TextureUsage` | 一次"材质在某网格区域用某通道采样某贴图"的记录:角色(Color/Normal/Mask)、UV 通道、是否有 ST/滚动/旋转/视差等变换、蒙版使用的通道位、alpha 模式与全部 Cutout 阈值(含动画关键帧,取最严) |
| 岛集合 | `IslandSetData` | 一个 (mesh, submesh, uvChannel) 的全部 UV 岛 + 归一化后 UV 数组 |
| UV 岛 | `UvIsland` | UV 连通分量(按量化 UV 位置并查集),含三角形/顶点/包围盒/世界面积(形态键 0/100 取大 × 动画最大缩放)/像素覆盖掩码 |
| 贴图节点 | `TextureNode` | 一张贴图跨全部用途的最严要求汇总(角色、sRGB、filterMode、alpha 要求、NoAtlas/Atlased 状态) |
| UV 组 | `UvGroup` | 岛↔贴图二部图的**连通分量** = 装箱原子单位(保证"同张贴图的所有岛在同一图集"且"同岛多贴图同布局") |
| 类型组签名 | `UvGroupSignature` | (颜色层有无/sRGB/filterMode, 法线层有无, 蒙版层有无, 线性颜色层) —— 决定哪些组件可共享图集池,解决"10 张贴图共用一张法线图集浪费 9/10"问题 |

**平行图集与动画变体**:共享任一岛的多张贴图(动画切换材质/贴图)通过贪心图着色分配到不同 **layer**;同族各 layer 图集共享岛布局(同 UV 在所有图集上位置相同,规范硬性要求)。着色最小化图集数量。

**白名单污染传播**(不动点):岛上存在白名单/未处理贴图 → 该岛封禁 → 岛上所有贴图 `NoAtlas` → 这些贴图的其他岛也封禁……直到稳定。受污染组件整体回退**整图缩放**(不改 UV),其余优化照常。

## 3. 质量算法 / Quality (`Editor/Core/Quality/`)

- 评估链:源像素(GPU RenderTexture 读取回,纹素精确)→ 线性空间+预乘 alpha 面积平均下采样 → 双线性上采样回原尺寸 → 与原图比较(仅覆盖像素参与)。
- 指标门限(全部达标才通过,取最差):
  - sRGB 颜色:MS-SSIM(5 尺度 Wang2003 权重;bbox 短边<176px 回退单尺度 SSIM;<11px 忽略)+ 平均 CIEDE2000 + alpha(Cutout→逐阈值 clip 轮廓 IoU;Blend→线性 RMSE;多材质引用/动画修改取最严)
  - 线性颜色:SSIM + RGB RMSE
  - 法线:解码(RG/AG 兼容)→向量平均下采样→重归一化→编码,角度误差均值+p95
  - 灰度蒙版:仅被使用通道(lilToon 通道表)线性 RMSE,逐通道取最差
- 搜索:均匀二分(1/128 步长)找最小通过缩放 → x/y 轴独立二分细化(各向异性)。
- 钳制:像素密度(默认 2048..4096 px/m,挡位 512..8192)上下限;s≤1(永不放大超过原文件);密度上限强制下调并记录。
- 短路:纯色岛直接缩到 min(4, bbox 短边);质量=1(近无损)时跳过一切缩放,原样拷贝。
- 多分辨率组:决策以组内**最大**贴图为基准;评估时逐贴图换算有效缩放(小贴图等效更狠缩放,必须按更严苛比例评估)。
- 全部指标为 Burst 作业(`MetricsJobs.cs`);重采样在作业内完成,GPU 负责源读取回与结果上传。

## 4. 装箱 / Packing (`Editor/Core/Packing/`)

- 岛光栅化:三角形 SAT 保守光栅化到 4px 格位掩码(`RasterizeJob`),按质量决策后的目标像素尺寸在"虚拟贴图"空间进行。
- 候选图集池:POT(64..8192,移动端 4096)或实验性 NPOT(64px 步进);按待装队列总光栅面积过滤,面积升序、长宽比升序(最接近正方形优先)排序,逐个尝试,第一个能装下的即成品。
- 放置:全扫描 BLF(Burst `BlfScanJob`,位字与移位重叠测试)+ 旋转 90°(位掩码转置;切线保持原样,内容与 UV 一致转置,渲染结果不变)。
- padding = max(用户选项, ceil(最大边/128)),下限 4px;占位掩码按 ceil(pad/2)/4 格膨胀写入,保证岛间距 ≥ padding。
- 原子性:整个组件(=同张贴图的全部岛 + 共享这些岛的贴图)全有或全无;失败回滚占位。
- 单贴图超过最大图集 → 该组件放弃图集化,整图缩放 + warning。
- 辅助层(法线/蒙版)整体 POT 缩放:取所有岛仍通过自身阈值的最大降幅(≥4px/padding 约束)。

## 5. 烘焙与重写 / Baking (`Editor/Core/Baking/`)

- 图集拼装:Burst 面积下采样直接写入图集子矩形(支持转置);法线走解码→向量下采样→重归一化→编码管线(切线绝不重算)。
- pull-push 渗色填充空白(已知轻微渗色,够用);透明图集空白区 alpha 强制 0;是否有 alpha 仅由有效像素判定。
- 贴图参数:强制 Clamp、关闭 Read/Write、Mip+MipStreaming 绑定为单开关(VRC 规则,经 SerializedObject 设置)、压缩格式按 透明/不透明/法线/灰度 × 平台安全枚举 + 构建时兜底(alpha 内容不得选无 alpha 格式;多通道灰度不得选单通道格式;平台不支持自动回退并报 NDMF 警告)。
- 网格重写:克隆网格,逐岛把归一化 UV 映射进图集矩形(旋转时按转置映射);只改 UV,顶点/法线/切线/蒙皮全保留。
- 材质重写:仅 SetTexture 替换贴图引用(非临时资产先克隆);材质其余参数零改动。
- 动画重写:经 NDMF `AnimatorServicesContext.AnimationIndex.RewriteObjectCurves` 全量映射材质/贴图对象引用;材质槽合并时同步重写 `m_Materials.Array.data[i]` 绑定索引。

## 6. 兼容性 / Compatibility

- **AAO**:反射调用 `UVUsageCompabilityAPI`(AAO≥1.8)。我们对某通道重写 UV 前查询 `IsTexCoordUsed`,被使用则把原 UV 备份到空闲通道并 `RegisterTexCoordEvacuation`,AAO 自己负责用后清除。未安装 AAO 自动跳过。
- **lilToon**:属性级精确表(提炼自 lilToon 2.3.4 源码,含 `_UseXXX` 开关、`_UVMode`、ST/ScrollRotate、decal 族标志、蒙版通道语义)。表外属性/无法分析着色器 → 白名单 + NDMF 警告。
- **其他着色器**:标准属性表(Standard 系)+ ShaderUtil 通用分析;未知属性保守白名单。

## 7. 扩展接口 / Extension API (`Editor/Core/ATOApi.cs`)

`net.fosa.ato.api.ATOExtensions` 静态事件:
- `IslandScaleModifier` — 二分前钳制岛缩放
- `WhitelistProvider` — 动态白名单决策
- `WhitelistObjectsProvider` — 贡献白名单对象
- `AtlasesBaked` — 烘焙完成通知(只读)

在 NDMF 构建前注册(如 `[InitializeOnLoadMethod]`)。

## 8. 已知限制 / Known Limitations

- 无 NDMF 预览(规范声明暂不支持)。
- VRChat 正式构建对话框中取消按钮由平台决定是否可见;手动烘焙(NDMF "Build" )可取消。
- SMR 世界面积用 renderer 变换近似(蒙皮姿态不参与),形态键与缩放动画按规范取最大值。
- 灰度通道语义表以 lilToon/Standard 为准,未知着色器保守全通道。
- 装箱 BLF 为贪心启发式;极端岛形利用率可能非最优(候选池+多族开放缓解)。

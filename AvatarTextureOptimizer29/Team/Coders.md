# Coder 组过程记录（每次写码前的三人共识）

## M0 项目定义
- A: 数据模型用 纹理↔岛 二部图，装箱原子=连通分量。B: 同意+类型组按纹理并集签名。
- C: 动画必须走 VirtualClip；lilToon uvMain 受 _MainTex_ST 影响。共识=按此实施（见 docs/Plan.md）。

## M1 骨架/配置
- A: 配置全部放 Runtime（组件上序列化），Editor 只读。B: 平台覆写用三份 AtoPlatformSettings。
- C: 枚举值集合按需求书逐条核对（512..8192 密度档、padding 4..64、NPOT 实验开关）。共识通过。

## M2 扫描分析
- A: ShaderCatalog = lilToon 精选表(从 lts.shader 提取) + Unity Shader API 通用启发式兜底。
- B: 资格检查含: 贴图ST/uvMain依赖(_MainTex_ST/_ScrollRotate/_ShiftBackfaceUV)/UVMode/decal/视差。
- C: 动画分析两用: ①找新增贴图(材质切换/贴图属性object曲线) ②找动画改ST/cutoff/rendermode。
  共识: MaterialSnapshot 逐键合并, 冲突(非恒定值)→取最严苛或失格。
- 裁决: 未识别着色器 → 尝试通用表(属性名启发+Normal标志+MainTexture标志)，仍无法归类→白名单+warning。

## M3 UV 岛
- A: 岛按"UV边接缝"连通（用UV坐标边哈希而非顶点索引，兼容接缝顶点复制）。
- B: 越界: 全分量 bbox ≤1 才可整数平移归一，否则白名单+warning；跨缝=三角形UV跨度>1。
- C: 面积因子: 形态键逐键 max(base,100)三角形面积，跨键取max；缩放动画逐祖先轴max，面积=最大两两乘积比。
  共识通过。

## M4 质量
- A: 指标全部 Burst IJobParallelFor；MS-SSIM 用覆盖mask加权高斯窗（窗口跨界不贡献）。
- B: 上采样比较用双线性（与运行时采样一致）；比较域: 颜色=sRGB luma(MS-SSIM)/Lab(ΔE)，
  alpha线性RMSE，法线解码后角度，灰度逐通道线性RMSE。
- C: 二分先均匀（6次）后每轴2次细化；每次评估岛实际覆盖区而非整包围盒。共识通过。
- 裁决: 解码缓存 GPU 一次性 Readback 到 RGBA32（法线记录源布局），之后全 CPU Burst，避免逐岛GPU往返。

## M5 装箱
- A: 光栅4px粒度 ulong 行位掩码；岛掩码=三角形光栅化+padding/2 膨胀（ceil到4px格）。
- B: BLF: 逐y扫描，首个 (mask AND atlas)==0 的位置；旋转90°=掩码转置，择优(更低y，再更低x)。
- C: 候选池惰性生成有序流（面积升序、纵横比降序），NPOT 步进64；开新图集前按剩余总面积过滤。
  共识通过。

## M6 应用
- A: 网格重写: per通道新UV数组；顶点仅在与未处理区域冲突时分裂；形态键帧数据随分裂复制；切线不动。
- B: 材质补丁: 仅克隆+换贴图引用；页面纹理按 kind 选择；法线页面贴图需满足目标格式通道布局。
- C: 最终去重: 内容+参数哈希；材质合并仅同网格同最终材质且无动画单独切换该槽；不透明才并槽。
  共识通过。

## M7 集成
- A: Optimizing 相间于 MA 后 AAO 前；进度条 EditorUtility.DisplayCancelableProgressBar 每 ~200ms 刷新。
- B: 取消→抛 OperationCanceledException，finally 清 GPU 资源，AssetSaver 容器自然保留。
- C: AAO 疏散仅 SMR+AAO存在(反射)；报告=Information 级 NDMF 错误条目+Debug日志。共识通过。

## M8 UI
- A: 平台覆写 tab + 默认折叠高级区；语言下拉=目录内json；参数编辑即切 Custom。
- B: 压缩枚举按 (平台,类别,alpha) 过滤；不安全组合禁用。共识通过。

## M9 测试/交付
- A: EditMode 测试覆盖: 光栅化、装箱正确性(无重叠)、CIEDE2000 已知值、MS-SSIM 恒等=1、
  岛提取接缝、UV归一。B: QA 用例见 Team/QA.md。C: README 双语。共识通过。

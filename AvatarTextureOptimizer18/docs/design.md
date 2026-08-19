# AvatarTextureOptimizer 设计文档 / Design Document

## 1. 目标

在保证画质（目标质量算法）的前提下最大化 VRChat Avatar 的贴图利用率：
分析网格 UV → 按质量缩放 UV 岛（或整图）→ 剔除未使用 UV → 约束装箱生成图集 → 更新网格/材质/动画引用。
仅修改贴图与 UV，绝不修改材质其他任何着色器参数。

## 2. 管线位置（NDMF）

Optimizing 相位；`AfterPlugin("nadena.dev.modular-avatar")` + `AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")`
+ `BeforePlugin("com.anatawa12.avatar-optimizer")`。
与 AAO 的兼容：AAO 的 `UVUsageCompabilityAPI`（反射适配）——对 AAO 会使用的 UV 通道，先把原始 UV 备份到空闲通道并
`RegisterTexCoordEvacuation`，AAO 处理完后会删除备份通道（AAO 未安装时自动降级，不做备份）。

## 3. 管线阶段（计划）

1. **Validate**：组件唯一性、VRCAvatarDescriptor 同对象、设置归一化。
2. **ScanMaterialSlots**：收集 SkinnedMeshRenderer/MeshRenderer 全部材质槽（跳过 EditorOnly；无网格警告）。
3. **ScanAnimations**：描述符自定义层 + 子级 Animator 的全部剪辑：
   材质槽切换、材质 float/vector 属性动画（Cutoff/ST/ScrollRotate/UVMode）、贴图属性动画、
   GameObject/Renderer 启停、Transform 缩放、形态键（仅取 0/100 最大值）。
4. **FilterSlots**：仅保留"被启用或有动画启用"的渲染器槽位。
5. **CollectTextures**：按着色器贴图属性表构建 TextureUse（含 UV 通道解析、ST 变换检测、透明模式）；
   分类（法线 > 颜色 > 蒙版 > 灰度；Blend > Cutout > Opaque）；按"实际像素（imageContentsHash）+ 导入设置签名"去重。
6. **ResolveWhitelists**：直接白名单（任意对象类型；GameObject 递归子树）+ 派生传播
   （白名单网格/对象/材质/动画剪辑 → 其贴图 Full）；ST 变换/特殊用途 UV/UVMode 动画/不支持着色器 → Full + warning；
   去重组成员白名单 → 结果白名单。TODO：同 UV 其他贴图 → NoAtlas（UV 组建立后）。
7. **Islands（待实现）**：UV 岛提取（多通道、重叠岛合并、越界归一（wrap 感知：Repeat 平移/Clamp 折叠/Mirror fallback）、
   形态键与动画缩放面积、各向异性）。
8. **QualityScale（待实现）**：GPU（RenderTexture）+ Burst 的目标质量评估（MS-SSIM/SSIM 回退、CIEDE2000、
   Cutout IoU、Blend alpha RMSE、法线角度 p95、灰度逐通道 RMSE），线性空间重采样、预乘 alpha 下采样、
   双线性上采样回原尺寸比对、二分搜索（均匀缩放 → 双轴独立细化）、像素密度钳制、纯色短路。
9. **Packing（待实现）**：Burst 位掩码光栅化（4px 粒度）+ 全扫描 BLF + 面积/边长降序 + 90° 步进旋转（位掩码转置）；
   贴图类型组（法线/蒙版/色彩空间/filterMode 分组，动画切换贴图并入原组）；UV 组（同 UV 跨图集位置一致）；
   候选图集池（POT 64..8192，移动端 4096；NPOT 实验选项 64 步进）；padding = max(选项, ceil(边长/128))；
   同贴图岛必须同图集；装不下最大图集 → 另开队列/放弃该 UV 组图集化 + warning。
10. **Atlases（待实现）**：GPU pull-push 外扩 padding（透明 alpha=0；法线图集重归一化）；ATO_ 命名；
    格式按类别（透明/不透明/法线/灰度，先读 liltoon 关键字再按像素兜底）；Mipmap/MipStreaming 绑定开关；
    Read/Write 关闭、强制 Clamp；其余参数取所有贴图最高质量。
11. **Apply（待实现）**：网格 UV 重写（ObjectRegistry 替换）、材质/动画引用更新（含动画中材质）、
    材质槽合并（动画安全条件下，重写动画路径与槽位索引）、贴图/材质内容与参数去重。
12. **Imports（待实现）**：fallback 贴图（未图集化且非白名单）的导入参数优化（压缩格式/平台覆盖/MipStreaming）。
13. **Report**：NDMF 控制台报告（总耗时、各阶段耗时、图集来源/岛数/大小/利用率/优化量；默认总体，细节 Verbose）。

## 4. 关键设计决策

- **UV 组**：同一 UV 对应的所有贴图（类型组/动画切换）必须同组，保证同 UV 在不同图集上的位置一致；
  装箱以 UV 组为原子（锚定主图集，其余图集同位置）。
- **类型组**：存在对应特殊贴图（法线/蒙版等）的纹理放在同一类型组，共同生成图集，避免 9/10 浪费；
  色彩空间、filterMode 不同占不同组；贴图同时用于有法线与无法线材质 → 归有法线组；动画切换贴图并入原贴图所在组。
- **质量算法**：岛短边 <176px 回退单尺度 SSIM；<11px 忽略 MS-SSIM；贴图被多材质引用时对每个引用材质的
  透明模式与 Cutoff 逐一评估取最严苛；UV 组内按木桶效应取最大尺寸（≤ 组内最大原尺寸）。
- **密度钳制**：默认 min 2048 / max 4096 px/m（挡位 512/1024/2048/4096/8192），同时受岛在原贴图物理大小钳制。
- **目标质量 = 1**：跳过对应贴图类型岛的 UV 缩放（含纯色），原样拷贝。
- **图集关闭**：不生成图集、不剔除未使用 UV、不重排 UV，直接缩放贴图。
- **白名单**：Full（跳过一切含导入参数）/ NoAtlas（跳过图集化，仍整图缩放+导入参数）。
- **安全 fallback**：任意不确定情况（跨 wrap 缝、Mirror 越界、不支持着色器、装不下等）→ 白名单/放弃 + warning；
  构建时对格式选项做平台安全校验（如 iOS 剔除 PVRTC 以外不支持的组合、含 alpha 强制带 alpha 格式等）。
- **取消**：进度条 + 取消按钮；取消抛 ATOCancelledException，NDMF 中止构建，硬盘临时资产保留，资源随栈展开释放。
- **暂不支持 NDMF 预览**（按需求）。
- **扩展接口**：预留 IATOStage/度量/装箱后处理等接口（设计进行中，见 decisions.md）。

## 5. 目录结构

```
Packages/net.fosa.avatar-texture-optimizer/
├── package.json                # VPM 包（vpmDependencies: vrchat base+avatars、ndmf）
├── Runtime/                    # 组件与设置（无第三方依赖）
├── Editor/                     # 全部编辑器/构建逻辑
│   ├── Core/                   # 日志、报告、异常、上下文
│   ├── NDMF/                   # 插件注册、AAO 适配
│   ├── Pipeline/               # 构建主流程与阶段
│   ├── Analysis/               # 槽位/动画/贴图/白名单/着色器表
│   └── Localization/           # i18n 运行时
└── Localization/               # i18n 配置（en-us.json、zh-hans.json，可扩展）
```

## 6. 版本状态

- v0.1.0：全部功能已实现（分析 → 岛提取 → UV/类型组 → 质量缩放 → 装箱 → 图集 → 应用 → 导入设置 → UI → 扩展接口）。
  已在结构性检查与 QA A/B 双审下通过；等待用户在 Unity 中编译与烘焙验证（本环境无 C# 编译器）。
  详见 README.md 与 CLAUDE.md。

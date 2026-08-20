# ATO: Avatar Texture Optimizer / ATO：Avatar 贴图优化器

**net.fosa.avatar-texture-optimizer** — 质量驱动的 VRChat Avatar 贴图优化 NDMF 插件。
Quality-driven texture optimizer for VRChat avatars (NDMF plugin). Runs **after Modular
Avatar and before AAO**, in the `Optimizing` phase.

> 状态 / Status: 0.1.0 开发阶段（字段与行为可能调整，暂不支持 ndmf 预览）。
> Development stage; fields may change; ndmf preview not supported yet.

---

## 它做什么 / What it does

1. **分析** Avatar 上所有被启用（或被动画启用）的 `SkinnedMeshRenderer` / `MeshRenderer`
   （跳过 EditorOnly），建立 **网格 UV ↔ 贴图** 的映射；同一 UV 被多张贴图引用
   （动画换材质/换贴图、法线、蒙版）时自动构成 **UV 组**，保证同 UV 在不同图集页上位置一致。
2. **资格检查**：仅处理「经网格 UV 采样、无任何 ST/平移/缩放/旋转/滚动/贴花等变换」的
   Texture2D；不满足者按 **白名单** 处理（其贴图跳过所有优化，同 UV 的其他贴图仅整图缩放）。
   支持 lilToon（按 2.3.4 源码精选属性表 + 命名规律自动兼容后续版本）与其他标准着色器。
3. **质量缩放**：以导入后有效贴图为基准，逐 UV 岛在目标质量约束下二分搜索最小尺寸：
   - 线性空间面积平均下采样、透明贴图预乘 alpha、双线性上采样回原尺寸比较；
   - 指标：MS-SSIM（短边 <176px 退化为单尺度 SSIM，<11px 忽略）+ ΔE(CIEDE2000)
     mean/p95 + alpha（Cutout 轮廓 IoU / Blend 线性 RMSE，逐引用材质取最严苛）；
     法线贴图解码重归一化后按角度误差 mean/p95；灰度贴图按使用通道逐通道线性 RMSE 取最差；
   - 像素密度钳制（默认 2048~4096 px/m，考虑形态键 max(0,100) 与动画最大缩放面积，
     且永不放大超过原始尺寸）；先均匀二分、后双轴独立细化；
   - 纯色岛直接缩到 min(4, 短边)；目标质量为 1（近无损挡）时跳过缩放原样拷贝。
4. **图集化**（可关闭；关闭则仅整图缩放+参数优化，不改 UV）：
   - **贴图类型组**：(色彩空间, FilterMode, 有/无法线, 有/无蒙版)——避免法线/蒙版图集
     尺寸浪费；次要页面可整体降采样（保 ≥4px padding）；
   - **装箱**：Unity Burst 4px 粒度光栅位掩码 + 全扫描 BLF + 90° 旋转（掩码转置）+
     候选图集池（默认 2^n、64..8192/4096；实验性 NPOT 64px 步进，已按需求书支持
     MipStreaming 与 Crunch）；岛间 padding = max(用户最小值, ceil(边长/128))；
     原子单元 = 纹理↔岛连通分量（同贴图所有岛必在同一图集）；
   - **渗色**：pull-push 无限外扩填满空白（透明图集 alpha 保持 0）；
   - 岛边缘之外未被使用的贴图区域被剔除。
5. **应用**：仅重写网格 UV（顶点按需分裂、形态键/切线/骨骼权重保留、切线绝不重算）、
   仅替换材质上的**贴图引用**（绝不改其他着色器参数）、同步更新动画中的材质与贴图引用；
   AAO 兼容：通过 `UVUsageCompabilityAPI` 将原 UV 疏散到空闲通道（需 AAO ≥ 1.8，
   未安装也正常运行）。
6. **参数优化**：按（平台,类别,是否有alpha）安全压缩格式枚举、Mipmap+MipStreaming
   单开关绑定、强制 Clamp、关闭 Read/Write、图集 Read/Write 与 Wrap 不可修改；
   任何选项组合都有安全回退（例如含 alpha 的贴图不会落到无 alpha 通道格式）。
7. **去重**：处理前按「像素内容+导入设置」去重贴图并更新引用；处理后对最终材质/贴图/
   图集去重；同网格相同**不透明**材质且动画未单独切换时合并材质槽（含子网格与动画索引同步）。
8. **报告**：NDMF 控制台输出总览（处理贴图/材质/岛数、预计节省显存、图集尺寸/利用率/
   来源、各阶段耗时、警告明细），`[ATO]` 前缀日志可调级别。

## 使用 / Usage

1. 依赖：NDMF ≥ 1.14、VRChat Avatars SDK ≥ 3.7（另建议 MA / AAO，均非必需）。
   安装本包（VCC 导入 zip 或放入 `Packages/`）。
2. 在 Avatar 根对象（挂 `VRCAvatarDescriptor` 的对象）上添加组件
   `ATO > Avatar Texture Optimizer`。整个 Avatar 仅允许一个，且必须挂在 Descriptor 对象上，
   违规将在烘焙/构建时报错中止。
3. 常规：图集开关、贴图/材质去重、白名单（任意类型对象，其引用的全部贴图跳过所有优化）。
4. 质量：挡位（近无损/高/均衡(默认)/快速/自定义）。切换挡位会刷新参数；手动编辑任一参数
   自动切「自定义」，其参数不会被其他挡位覆盖（默认值=近无损）。高级折叠区：像素密度、
   最小 padding（4/8/16/32/64，默认 4）、实验性 NPOT、四类贴图的 Mipmap/流式与压缩格式。
5. 平台覆写：PC/Android/iOS 三页签，勾选后显示该平台参数（图集上限 PC 8192 / 移动端 4096，
   移动端仅提供 ASTC，不提供 PVRTC）。未勾选时使用通用最优解。
6. 烘焙（NDMF Manual Bake）或上传构建时自动运行；进度条可取消（保留临时资产、释放资源）。
7. i18n：语言选项默认 Auto（跟随 NDMF 语言），在 `Localization/` 放置更多 json 即可扩展
   新语言（缺失键回退英文）。

## 白名单 / Whitelist

白名单对象类型不限（网格/材质/贴图/动画/GameObject 等），经 `EditorUtility.CollectDependencies`
闭包取其引用的全部 Texture2D，全部跳过优化；与白名单贴图同 UV 的其他贴图跳过图集化，
但仍参与整图缩放与参数优化。

## 第三方开发者 / For developers

- 扩展点：继承 `net.fosa.ato.editor.ATOExtension`，在 `OnStage(stage, ctx)` 接收
  `BeforeScan / AfterAnalysis / AfterQuality / AfterPack / AfterApply / Finish` 六个钩子；
  `ATOExtensionRegistry.Register/Unregister` 注册（低 Priority 先执行；异常会被记录不会中止）。
  `ATOStageContext` 提供 `Build`（NDMF BuildContext）、`Component`、`Platform`、`Warnings`。
- 处理顺序：`Optimizing` 阶段，`AfterPlugin("nadena.dev.modular-avatar")` 且
  `BeforePlugin("com.anatawa12.avatar-optimizer")`（与 MA 1.18.2 / AAO 1.9.17 共存）。
- 质量指标（MS-SSIM/CIEDE2000/IoU/RMSE/法线角度）位于 `Editor/Quality/Metrics.cs`，
  均为 Burst 作业，可独立复用。装箱器 `Editor/Packing/BitmaskPacker.cs` 为纯 C# 可测实现。
- 日志：`[ATO]` 前缀；组件上可调 Silent/Info/Debug/Trace。

## 结构 / Layout

```
Runtime/   组件与配置（序列化字段，开发期可变）
Editor/    Core(日志) L10n(i18n+MiniJson) Analysis(扫描/着色器/动画/去重/使用图)
           UV(岛) Quality(指标+评估) Packing(光栅/候选池/装箱/渗色)
           Apply(图集/网格/材质/参数/最终去重) Pipeline(插件/Pass/报告/进度)
           API(扩展) UI(检查器) AAOCompat
Localization/ en.json zh-hans.json（可扩展）
Tests/Editor/ EditMode 测试（CIEDE2000 参考值、装箱位掩码、候选池、JSON、UV映射）
```

## 已知限制 / Known limitations

- pull-push 渗色在 CPU（托管）实现（每图集页一次），未用 GPU 计算着色器（见 CLAUDE.md 决策10）。
- 质量评估比较区域上限 2048px（更大区域先做面积平均归约，指标语义近似不变）。
- 「装箱时先算剩余总面积再选候选」按需求书实现；padding 语义按 max(最小值, ceil(边/128)) 实现。
- 图集 Mipmap 由 `Apply(true)` 生成（盒式滤波，与导入器默认一致）。
- 本包不含 .meta 文件，首次导入由 Unity 生成。

## License

MIT © fosa

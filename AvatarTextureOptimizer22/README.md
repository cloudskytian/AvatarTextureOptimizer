# AvatarTextureOptimizer (ATO)

**Net.fosa Avatar Texture Optimizer** — 一个适用于 VRChat Avatar 的 NDMF 贴图优化工具。分析 Avatar 网格上的 UV→贴图映射，按**目标质量算法**缩放 UV 岛，把不再使用的贴图区域剔除后重新分配 UV，并将碎片重组合并成一张或多张**图集**，在保证视觉质量的同时最大程度提高贴图利用率。

> 运行时机：Modular Avatar 执行之后、Avatar Optimizer 执行之前（NDMF `Optimizing` 阶段，弱顺序约束保证）。

---

## 核心特性

- **UV→贴图映射与复用**：以"网格 UV ↔ 贴图"为基本单元，即使多个材质引用同一张贴图，映射也只建立一次，完全无视材质其他参数。
- **质量挡位**：内置 Ultra / High / Medium / Low 四个预设挡位（阈值参考学术界与业界研究），以及一个完全由用户自定义、**不会被其他挡位覆盖**的 Custom 挡位（默认全部 1 = 近无损）。具体阈值随挡位变化，折叠在高级选项中。
- **目标质量算法**（全部在线性空间、GPU/Burst 并行评估）：
  - 主色贴图：`MS-SSIM`（5 级 GPU 实现；岛短边 <176px 回退单尺度 SSIM；<11px 忽略）+ `CIEDE2000` 色差
  - 透明贴图：预乘 alpha 下采样；`Cutout` 用 clip 后轮廓 IoU、`Blend` 用线性 RMSE；被多个材质引用时逐一评估，取**最严苛**要求
  - 法线贴图：正确解码 → 重采样 → 重归一化 → 编码后，用**角度误差 p95** 对比
  - 灰度/蒙版贴图：仅在被使用的通道上、线性空间 RMSE，逐通道取最差
  - 缩小后的岛实际覆盖区被双线性上采样回原尺寸后与原图比较；UV 缩放使用**二分搜索**，全部指标达标才算通过，按 UV 组木桶效应取最大尺寸
- **贴图类型组**：有法线/蒙版等伴随贴图的纹理按（类型集合、色彩空间、过滤模式）分组共同装箱，避免"10 张贴图合成一张大图集、法线图集 9/10 被浪费"的问题。
- **UV 组一致性**：同一 UV 对应的所有贴图构成一个 UV 组，保证同一 UV 在不同图集上的位置**完全相同**（防止 UV 被有法线与无法线材质同时引用时出错）。
- **装箱器**：Unity Burst 光栅位掩码（4px 粒度，真实岛形状而非矩形）+ 全扫描 BLF + 面积降序/边长降序 + 旋转 90° 步进（法线贴图切线数据保持原样、绝不重算）+ 候选图集池（实验性 NPOT 选项）+ 类型组队列，图集数量随处理自然增长。
- **图集填充**：GPU pull-push（无限外扩）用岛边缘颜色填满空白区域（透明贴图 alpha 保持 0）。
- **像素密度控制**：默认 2048~4096 px/m（挡位 512~8192），防止浪费或发糊，且受岛在原贴图物理尺寸的钳制。
- **纯色短路**：目标质量 ≠ 1 时纯色岛直接缩到 `min(4, 原岛包围盒短边)`；目标质量 = 1（近无损）时跳过缩放、不重采样原样拷贝。
- **动画兼容**：识别动画中的贴图/材质切换、ST 变换、网格切换、渲染模式/Cutoff 修改、形态键（仅取 0/100 二者最大）、物体缩放（按最大缩放面积）等，无法安全处理的自动视作白名单并报 warning。
- **白名单**：不限制对象类型（网格/材质/贴图/动画/渲染器均可）；白名单对象引用的贴图跳过全部优化。
- **去重**：按实际像素 + 导入设置对贴图去重；对相同材质/贴图/图集去重并更新所有引用；安全时合并相同的不透明材质槽并更新动画引用与槽索引。
- **导入参数**：压缩格式安全枚举（按透明/不透明/法线/灰度分类），PC=BC7/DXT、移动端=ETC2/ASTC，NPOT 时自动剔除 PVRTC；`Mipmap ↔ MipStreaming` 绑定（VRChat 要求）；图集强制 Clamp、默认关闭 Read/Write。
- **平台覆写**：PC / Android / iOS 分别可覆写参数，默认读取当前构建平台，参数按平台正确受限。
- **多通道 UV**、**UV 越界整体平移归一**、**同贴图重叠岛合并**、**各向异性细化缩放**（先均匀后双轴二分）均受支持。
- **AAO 兼容**：通过反射调用 AAO 的 `UVUsageCompabilityAPI`（已通读其源码），重写 UV 前做疏散注册，AAO 未安装时自动降级。
- **i18n**：用户可扩展——`Localization/*.json` 有几个语言就显示几个；Auto 跟随 NDMF 语言，缺失回退英文；随包附带 `en-US` 与 `zh-CN`。
- **日志与报告**：所有日志以 `[ATO]` 开头，含每步耗时、图集来源、岛数量、图集尺寸/利用率、相对原贴图的优化量；构建完成在 NDMF 控制台输出报告，默认展示总体结果、细节可折叠；烘焙显示阶段与进度并支持取消（取消后保留临时资产、释放资源）。
- **扩展接口**：为第三方开发者提供纹理/UV 空间过滤器与分析钩子（见下文）。

---

## 安装

1. 需要 Unity 2022.3+，且工程内已安装：
   - [NDMF ≥ 1.14](https://github.com/bdunderscore/ndmf)
   - VRChat SDK Avatars 3.10+
   - （可选）Modular Avatar、Avatar Optimizer、lilToon
2. 将 `net.fosa.avatar-texture-optimizer` 目录放入工程的 `Packages/` 下，或在 VPM 中添加本包。

---

## 使用方法

1. 在 Avatar 根对象（挂有 `VRCAvatarDescriptor` 的对象）上添加 **Avatar Texture Optimizer** 组件。
   - 一个 Avatar（含子级）只允许挂载一个；不合规挂载会报错中止烘焙。
2. 保持默认设置即可获得高质量结果。也可以：
   - 调整**质量挡位**（Ultra/High/Medium/Low/Custom）；
   - 在高级选项中修改具体阈值（Custom 挡位下可编辑，永不被预设覆盖）；
   - 调整像素密度范围、图集 padding、是否生成图集；
   - 按贴图类别设置压缩格式与 Mipmap；
   - 为 PC/Android/iOS 分别覆写参数；
   - 添加白名单对象。
3. 正常执行 VRChat Avatar 构建（或 NDMF 单步构建）。优化在 MA 之后、AAO 之前自动进行。
4. 构建完成后在 NDMF 控制台查看 [ATO] 报告。

> **注意**：本工具不修改材质内除贴图以外的任何着色器参数，只修改网格 UV 与贴图引用，最大限度保证 Avatar 表现一致。

---

## 质量挡位参考

| 挡位 | MS-SSIM | CIEDE2000 ΔE | alpha RMSE (Blend) | Cutout IoU | 法线角度 | 灰度 RMSE |
|------|---------|--------------|--------------------|------------|----------|-----------|
| Ultra | ≥0.995  | ≤1.0         | ≤0.004             | ≥0.999     | ≤1°      | ≤0.004    |
| High  | ≥0.99   | ≤2.0         | ≤0.008             | ≥0.998     | ≤2°      | ≤0.008    |
| Medium| ≥0.98   | ≤3.0         | ≤0.016             | ≥0.995     | ≤4°      | ≤0.016    |
| Low   | ≥0.96   | ≤5.0         | ≤0.03              | ≥0.99      | ≤8°      | ≤0.03     |
| Custom| 全部 1（近无损），参数由用户自定义，永不被覆盖 | | | | | |

参考依据：CIEDE2000 的 JND ≈ 2.3（Sharma et al. 2005）；MS-SSIM ≥ 0.99 通常视为视觉无损；法线贴图角度误差低于 ~2° 对多数资产不可感知。

---

## 开发者接口

在 `net.fosa.avatar_texture_optimizer.editor.compat` 命名空间提供扩展点（可选，均不强制）：

```csharp
// 否决对个别贴图的优化（如自定义着色器的贴花）
public interface IATOTextureFilter   { bool CanOptimize(Texture2D texture); }
// 否决整个 UV 空间（渲染器+槽+通道）
public interface IATOUVSpaceFilter   { bool CanOptimize(UVSpaceKey space); }
// 分析阶段完成后的钩子
public interface IATOAnalysisHook    { void OnAnalysisComplete(ATOBuildState state); }
```

注册方式（宿主工程）：
```csharp
[InitializeOnLoadMethod]
static void Register() {
    ExtensionRegistry.RegisterTextureFilter(new MyFilter());
    // ...
}
```

### i18n 扩展
在 `Localization/` 目录添加 `*.json`：
```json
{ "locale": "ja-JP", "displayName": "日本語",
  "strings": { "component.name": "アバター テクスチャ オプティマイザー", ... } }
```
加入文件即出现在语言选择器中。

---

## 兼容性

- **NDMF 1.14.x**：在 `Optimizing` 阶段运行，弱顺序约束于 AAO 之前。
- **Avatar Optimizer 1.9.x**：UV 疏散通过反射调用 `UVUsageCompabilityAPI`（读通源码后实现），AAO 未安装时自动跳过。
- **lilToon 2.3.x**：通过标准着色器属性表与关键字分析（`_MainTex`/`_NormalMap`/`_Main2ndTex_UVMode` 等），兼容未来版本；无法分类的属性视作白名单并报 warning。
- **Modular Avatar**：在其执行后运行，兼容其生成的资产。

### 已知限制 / 注意事项
- 暂不支持 NDMF 预览。
- 动画中对贴图/材质的 ST 变换、网格切换等无法安全处理的情况会视作白名单并输出 warning。
- pull-push 填充存在已知的边缘渗色，属可接受范围。
- 极端情况下（岛超过平台最大图集尺寸）放弃该 UV 组的图集化，按质量缩放并报 warning。

---

## License

MIT — 详见仓库 LICENSE。本工具为开源项目，欢迎提交 Issue 与 PR。

# AvatarTextureOptimizer

AvatarTextureOptimizer（ATO）是一个面向 VRChat Avatar 的开源 NDMF 纹理与 UV 优化工具。它以安全 fallback 为第一原则：能够证明转换安全时才重排 UV/生成图集，不能确认 Shader、动画、UV wrap 或组件语义时跳过相关对象并在 `[ATO]` 日志中说明原因。

AvatarTextureOptimizer is an open-source NDMF texture and UV optimizer for VRChat avatars. Safety comes first: UV remapping and atlasing are performed only when the build-time analysis can prove the operation is safe; uncertain shader, animation, wrap or component cases fall back with an `[ATO]` warning.

## 安装 / Installation

1. 将本目录作为 UPM 包放入 Unity 工程的 `Packages/net.fosa.avatar-texture-optimizer`，或通过 VPM 添加。
2. 使用 Unity 2022.3 LTS。
3. 安装 NDMF 1.14.4；VRChat 工程安装 VRChat Avatars SDK 3.10.4。
4. 将 `AvatarTextureOptimizer` 组件挂到带 `VRCAvatarDescriptor` 的 Avatar 根对象上。一个 Avatar 根及其子级只能有一个组件。
5. 首次构建前先保存工程，并准备一个可回滚的测试 Avatar。

1. Add this directory as `Packages/net.fosa.avatar-texture-optimizer`, or install it through VPM.
2. Use Unity 2022.3 LTS.
3. Install NDMF 1.14.4 and VRChat Avatars SDK 3.10.4 for a VRChat project.
4. Add `AvatarTextureOptimizer` to the Avatar object that also contains `VRCAvatarDescriptor`. Only one component is allowed in an avatar hierarchy.
5. Save the project and use a reversible test avatar before the first build.

## 构建阶段 / Build order

ATO 注册在 NDMF `Transforming` 阶段，并声明：

- 在 `nadena.dev.modular-avatar` 之后运行；
- 在 `com.anatawa12.avatar-optimizer` 之前运行；
- 不注册 NDMF preview，因此当前明确不支持 NDMF 预览。

ATO runs in NDMF `Transforming`:

- after `nadena.dev.modular-avatar`;
- before `com.anatawa12.avatar-optimizer`;
- without a preview filter, so NDMF preview is intentionally unsupported for this development release.

## 已实现的处理流程 / Implemented pipeline

1. 检查组件唯一性与 `VRCAvatarDescriptor`。
2. 遍历非 `EditorOnly` 的 MeshRenderer/SkinnedMeshRenderer，读取网格 UV、材质槽和 Shader 纹理属性。
3. 通过 Shader 属性表与标准命名识别主色、法线、蒙版/灰度等纹理；未知或有歧义的 Shader 安全跳过。
4. 扫描 Animator/Animation 依赖的 AnimationClip，发现 ST、tiling/offset、纹理变换、材质切换等不安全动画时回退。
5. 按像素内容与导入设置去重源纹理；若去重结果涉及白名单，结果同样按白名单处理。
6. 按网格顶点连通建立 UV 岛，按 UV 包围盒合并同贴图内重叠岛；支持 UV0–UV7。
7. 允许不跨 Repeat 缝的整体整数平移归一到 `[0,1]`；跨缝、Clamp 越界、无法确认的情况 warning + fallback。
8. 目标质量使用单尺度 SSIM / MS-SSIM 分支、CIEDE2000、Cutout IoU、Blend alpha RMSE、法线角度与 p95、灰度 RMSE；采用最差引用与二分搜索，随后进行 X/Y 各向异性细化。
9. 纯色岛在非近无损挡位下可直接缩到最小；近无损挡位跳过重采样并使用原像素复制。
10. 图集采用 4px RasterMask、面积/长边排序、全扫描 BLF、90° 旋转、候选图集池；同一 Renderer 的同一 UV 通道共用放置族，避免主色/法线/蒙版错位。
11. 生成的图集和 fallback 资产以 `ATO_` 开头，优先保存为 NDMF 生成目录内 PNG，并配置 Clamp、Read/Write off、Mipmap/MipStreaming 绑定和平台格式安全回退。
12. 构建克隆内克隆 Mesh/Material，只设置纹理引用，不修改材质中的其他 Shader 参数。
13. 可选去重生成后的材质；动画材质切换存在时不合并材质槽。
14. 提供白名单、质量挡位、Custom 质量参数、像素密度、padding、NPOT、PC/Android/iOS override、格式枚举、日志和 i18n。
15. 若 AAO 已安装，反射调用 `UVUsageCompabilityAPI` 疏散原 UV；AAO 未安装时不依赖它，API 调用失败则跳过该 UV 改写。

## 质量挡位 / Quality presets

- Economy：更激进地限制尺寸，适合快速预览与移动端测试。
- Balanced：默认挡位，在结构相似度、色差、透明轮廓和法线角度之间折中。
- High：偏向保真，允许更大岛尺寸。
- NearLossless：质量参数为 1，跳过对应类型的 UV 缩放与重采样，整图 fallback 同尺寸时原样复制。
- Custom：默认所有归一化参数为 1；用户修改后不会被其他挡位覆盖。

阈值来自 SSIM/MS-SSIM、CIEDE2000、法线角度误差与 alpha 轮廓比较等业内常见指标的保守组合。它们不是对所有材质/显示设备的数学保证，最终仍应使用 Avatar 实际表现验证。

## 安全行为 / Safety behavior

以下情况会跳过相关纹理或 UV 族，而不是强行处理：

- Shader 属性无法确认，或纹理被用作贴花、反射、Cube、特殊变形用途；
- 任意非恒等纹理 scale/offset，或动画修改 ST/tiling/offset/纹理变换；
- 动画材质切换无法证明所有变体纹理等价；
- UV 跨 Repeat 缝、Clamp 越界、同一网格顶点需要两个 atlas 位置；
- 纹理/材质/动画/对象在白名单中；
- 图集超过平台最大尺寸、无法装入形状 mask、GPU 读回或生成资产写入失败；
- AAO API 拒绝原 UV 疏散；
- alpha/法线/灰度格式请求与实际像素内容不兼容。

这些 fallback 可能减少优化收益，但不会为了追求体积而冒险改变 Avatar 表现。

## 性能与资源 / Performance and resources

- 不可读纹理使用临时 RenderTexture + ReadPixels 回退；所有临时 RenderTexture 和 Texture2D 都在 finally 中释放。
- 热点均方误差循环使用 Burst `IJobParallelFor`；纹理读取路径可利用 GPU Blit。
- 单次构建的像素缓存有 256 MB 上限，不跨构建保留；生成图集直接写入 Texture2D 原始数据，避免再复制一整张 managed buffer。
- Pull-push 使用双向扫描，alpha 保持原值。
- 点击进度条取消会重新抛出取消信号，中止 NDMF 构建；NDMF 生成目录中的临时资产按照组件选项保留。

## 扩展 / Extensions

Editor 程序集公开：

- `IATOShaderResolverExtension`：为第三方 Shader 提供纹理属性、纹理类型和 UV 通道；
- `IATOBuildExtension`：在分析前、构建后接入自定义逻辑；
- `ATOExtensionRegistry`：注册/注销扩展；
- `ATOTexturePropertyDescriptor`：描述纹理属性。

第三方扩展应在自己的 Editor 程序集中引用 ATO Editor assembly，并对所有异常自行处理；ATO 会捕获扩展异常并安全继续。

## i18n / 本地化

`Editor/Resources/i18n/en.json` 和 `zh-Hans.json` 是随包提供的配置。文件使用 Unity `JsonUtility` 可解析的数组结构：

```json
{
  "language": "fr",
  "entries": [
    { "key": "title", "value": "Avatar Texture Optimizer" }
  ]
}
```

将更多 JSON 放在相同 i18n 目录即可被发现；组件的 Language 设为 Auto 时读取 NDMF 当前语言，找不到翻译回退英文。

## 验证说明 / Verification note

本仓库交付的是可放入 Unity 工程的 UPM 包，不是完整 Unity 工程。当前沙盒没有 Unity Editor、Unity assemblies、Burst 编译器、GPU 或实际 VRChat Avatar，因此这里完成的是源码级检查、算法测试夹具、依赖源码/API 核对和包结构验证；不能诚实地声称已经完成 Unity 实机编译、NDMF 烘焙或视觉回归。

This repository is a UPM package rather than a full Unity project. The current execution sandbox has no Unity Editor, Unity assemblies, Burst compiler, GPU or real VRChat avatar. Source-level checks, algorithm test fixtures, dependency API inspection and package validation are included, but Unity compilation, NDMF baking and visual regression still need to be run by the user in their project.

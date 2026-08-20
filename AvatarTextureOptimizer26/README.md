# Avatar Texture Optimizer (ATO)

开源 NDMF 工具：分析 VRChat Avatar 网格 UV，按目标质量缩放 UV 岛，再装箱成图集，在保证观感的同时尽量提高贴图利用率。

An open-source NDMF tool that analyzes VRChat avatar mesh UVs, scales UV islands to a target quality, and packs atlases to maximize texture utilization.

**Package:** `net.fosa.avatar-texture-optimizer`  
**Version:** 0.1.0 (development)  
**Unity:** 2022.3+  
**Requires:** NDMF ≥ 1.14, VRChat Avatars SDK ≥ 3.7

---

## 给小白 / For users

### 安装
1. 把本文件夹放到 Unity 项目的 `Packages/net.fosa.avatar-texture-optimizer/`（或用 VPM 添加本地包）。
2. 确认已安装 **NDMF**、**VRChat Avatars SDK**。建议同时安装 Modular Avatar。Avatar Optimizer（AAO）、lilToon 为可选兼容。
3. 打开带 `VRCAvatarDescriptor` 的 Avatar 根对象，菜单 **Add Component → Fosa → Avatar Texture Optimizer**。
4. **一个 Avatar 及其子级只能挂一个组件**，且必须挂在带 `VRCAvatarDescriptor` 的对象上。
5. 点击 VRChat / NDMF 的 Bake 或上传构建。进度条可取消；取消会终止本次烘焙，磁盘临时资产保留，内存会释放。

### 常用选项
| 选项 | 默认 | 含义 |
| --- | --- | --- |
| Generate Atlases | 开 | 关：不剔除未使用 UV、不重排 UV，只整图缩放 |
| Quality Preset | High（PC）/ Medium（移动端覆盖） | 质量挡位。Custom 不会被其它挡位覆盖，默认全 1（近无损） |
| Min/Max px/m | 2048 / 4096 | 按模型真实面积限制像素密度，避免浪费或发糊 |
| Min Padding | 4 | 岛间距下限；实际 padding = max(该值, ceil(图集长边/128)) |
| Whitelist | 空 | 任意对象。其引用的全部 Texture2D **跳过所有优化** |
| NPOT | 关 | 实验性非 2 次幂图集（64px 步进）。会剔除 PVRTC 等不支持格式 |
| 平台覆盖 | 关 | 勾选 PC / Android / iOS 后才显示该平台参数 |
| Language | Auto | 跟随 NDMF 语言；可手动切到已有 json（en-us / zh-hans） |

高级质量阈值（MS-SSIM、CIEDE2000、法线角度…）默认折叠。切换挡位会改这些数字；**Custom 不会被覆盖**。

### 安全规则（你不用记，工具会兜底）
- 只改 **网格 UV** 和 **贴图引用**，不改材质其它着色器参数。
- 有 Tiling/Offset/旋转（含动画）、贴花、非 Texture2D、跨 wrap 缝的 UV → 当白名单并在 NDMF 控制台 warning。
- 透明贴图不会被保存成无 alpha 格式。
- 图集强制 Clamp、关闭 Read/Write，名称以 `ATO_` 开头。
- 开 Mipmap 时强制 Mip Streaming（VRChat 要求，二者绑定同一个开关）。
- 无法分析的着色器 → 白名单 + warning。
- 烘焙后会从成品上移除本组件。

暂不支持 NDMF Preview。

---

## 给开发者 / For developers

### 处理时机
NDMF `BuildPhase.Transforming`：

- After `nadena.dev.modular-avatar`
- After `nadena.dev.modular-avatar.late-transform-stages`
- After `net.rs64.tex-trans-tool`（未安装也安全）
- Before `com.anatawa12.avatar-optimizer`（AAO 实际在 Optimizing 阶段，本插件已先于它）

需要 `AnimatorServicesContext`。

### 管线
1. 校验组件  
2. 收集启用中 / 动画可启用的 `SkinnedMeshRenderer` + `MeshRenderer`（跳过 EditorOnly）  
3. 按像素 + 导入设置去重贴图并更新引用  
4. 着色器分析（lilToon 关键字 + 标准 TexEnv）+ 动画（材质/贴图切换、Cutoff、缩放、启用）  
5. UV 岛、重叠合并、可平移越界归一  
6. 目标质量二分缩放（先均匀后各向异性；GPU 重采样 + Burst 指标）  
7. 类型组 + UV 组装箱：4px Burst 光栅、全扫描 BLF、90° 转置、候选图集池  
8. 写回网格 UV / 材质贴图引用 / 动画 Object Curve  
9. 材质与贴图去重；移除组件；NDMF 报告  

### 扩展 API

```csharp
using Net.Fosa.AvatarTextureOptimizer;

public class MyExt : IAtoExtension
{
    public string Id => "vendor.myext";
    public bool ShouldProcessTexture(Texture2D t, Material m, string prop) => true;
    public AtoTextureKind ClassifyTexture(Texture2D t, Material m, string prop)
        => AtoTextureKind.Unknown; // keep analyzer result
    public void OnAfterOptimize(GameObject avatarRoot, IReadOnlyList<Texture2D> atlases) { }
}

// Editor InitializeOnLoad:
AtoExtensionRegistry.Register(new MyExt());
```

### i18n
在 `Editor/I18n/Languages/` 添加 json：

```json
{
  "language": "ja-jp",
  "strings": { "opt.generateAtlas": "アトラスを生成" }
}
```

缺 key 回退英文。Auto 读取 `nadena.dev.ndmf.localization.LanguagePrefs.Language`。

### 日志
所有日志以 `[ATO]` 开头。组件上勾选 Verbose Log 输出逐步耗时、图集来源、岛数量、利用率。总结果写到 NDMF 控制台（Information），细节在 description / Console。

### AAO
通过反射调用 `UVUsageCompabilityAPI`（AAO 原文拼写）。未安装 AAO 时跳过。仅 `SkinnedMeshRenderer`。重排 UV 前若该通道被 AAO 使用，会把原 UV 疏散到空闲通道。

### 已知设计说明
- **法线 90° 装箱**：网格切线绝不重算；旋转岛时会旋转法线贴图的切线空间 XY，避免光照错误。
- 质量评估不含最终压缩格式损失（按需求）。
- 形态键只取 0 与 100 的较大位移，不做排列组合。
- 本仓库是 **Unity 包而不是完整工程**。请导入你的 Avatar 工程后 Bake 验证。

### 第三方源码
实现前已阅读并按真实 API 使用（未猜测）：

- NDMF 1.14.4
- Modular Avatar 1.18.2
- Avatar Optimizer 1.9.17（含 `UVUsageCompabilityAPI`）
- lilToon 2.3.4
- VRChat Base/Avatars 3.10.4
- avatar-compressor 0.9.0、Light Limit Changer 2.13.0（参考，未硬依赖）

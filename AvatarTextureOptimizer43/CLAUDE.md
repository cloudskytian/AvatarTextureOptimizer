# AvatarTextureOptimizer — AgentTeam 记忆

包名：`net.fosa.avatar-texture-optimizer`  
版本：**0.1.2**（开发中，配置字段可随意改，不做版本兼容）  
Unity：2022.3 / NDMF 1.14.4 / MA 1.18.2 / AAO 1.9.17 / lilToon 2.3.4 / VRC SDK 3.10.4

## 可行性结论

**可行。** 主流程（MA 后 / AAO 前，只改 UV 与贴图引用）与 NDMF/AAO 真实 API 对齐。必须修正的设计：

1. **含法线的 UV 组禁止 90° 旋转**（切线绝不重算）。
2. **同一 `(Mesh, UV channel)` 必须共享布局**；FilterMode 无法同图集则放弃图集化。
3. **AAO `UVUsageCompabilityAPI` 用反射**（asmdef autoReferenced=false）。
4. **Runtime 禁止引用 UnityEditor**；平台 Auto 在 Editor 管线解析。
5. **白名单同 UV 兄弟禁止改 UV**，只做整图缩放 + 导入参数。
6. **PhysBone 动态缩放未分析**。形态键只取 0/100。

## 已读第三方 API（禁止再猜）

- NDMF 1.14.4：`Plugin<T>` / `Pass<T>` / `InPhase(Optimizing)` / `AfterPlugin` / `BeforePlugin` / `WithRequiredExtension` / `ErrorReport` / `AssetSaver.SaveAsset` / `AssetSaver.CurrentContainer` / `ObjectRegistry` / `AnimatorServicesContext` / `AnimationIndex.RewriteObjectCurves` / `LanguagePrefs` / `Localizer` / `WellKnownPlatforms.VRChatAvatar30`
- MA QualifiedName `nadena.dev.modular-avatar`
- AAO QualifiedName `com.anatawa12.avatar-optimizer`；API `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`
- lilToon 2.3.4 `lts.shader`：`_TransparentMode`、`_Use*` 开关、`_UVMode`、`_Cutoff`、`_MainTex` 默认 UV0

## 已完成（到 0.1.2）

- UPM 包、NDMF 插件、组件、平台覆盖、i18n、检查器、扩展 API
- 着色器分析（lilToon `_Use*` 跳过 + 标准关键字 + 第三方分析器 + 烘焙缓存）
- 动画扫描、去重写回 `texRemap`、白名单同 UV 不改 UV
- UV 岛、越界归一、重叠合并、形态键 0/100、动画缩放
- 质量二分（MS-SSIM / CIEDE2000 / 法线 / IoU / RMSE），法线解码重采样重归一化
- 类型组装箱 `AtoAtlasBuilder`：平行图集、子网格匹配、副图集缩小、面积降序
- Burst 4px 形状光栅（≥16 三角），失败 CPU
- GPU 重采样 / pull-push，失败 CPU；透明预乘走 CPU
- **PNG + TextureImporter 导出**（Mip 与 MipStreaming 绑定、Clamp、关 RW）；失败回退子资源 CompressTexture
- AAO UV evacuation 反射
- 材质/贴图去重、不透明槽合并 + 动画槽索引
- 烘焙后销毁组件、可取消进度、`[ATO]` 日志、NDMF 报告
- `AtoApi.AtlasCreated`

## 明确不做 / 本环境做不到

- Unity 实机编译与烘焙（无 Unity）——**用户同步后必须完整烘焙验证**
- NDMF Preview（需求明确暂不支持）
- PhysBone 缩放、形态键排列组合
- 若 NDMF `CurrentContainer` 无路径，Importer 导出会失败并走子资源回退（此时 VRChat 性能扫描可能看不到 streamingMipmaps importer 标志）

## 铁律

1. 先读代码取证，禁止猜 API
2. 改完必须能在 Unity 完整烘焙
3. git commit + 更新本文件
4. 日志 `[ATO]`，verbose 开关
5. 只改贴图引用和网格 UV
6. 非安全转换必须 fallback + warning

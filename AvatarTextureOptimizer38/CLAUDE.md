# AvatarTextureOptimizer — Agent Memory

> 关于此项目的一切记忆只记录到本文件。All project memory lives only in this file.

## 项目状态 / Status

- **阶段 / Phase**: v0.1.0 完整实现交付 / full implementation delivery
- **包名 / Package**: `net.fosa.avatar-texture-optimizer`
- **Unity**: 2022.3（VRChat 当前 LTS）
- **NDMF 相位**: `BuildPhase.Transforming`，`AfterPlugin` Modular Avatar（含 late-transform）与 TexTransTool；AAO 在 `Optimizing`，天然在我之后。

## AgentTeam 共识（Coder ×3）

1. **不要实现 `IEditorOnly` 导致 Resolving 误删。** NDMF `RemoveEditorOnlyPass` 只删 `EditorOnly` 标签物体。组件实现 `INDMFEditorOnly`（上传时由 VRC SDK 剥离），并在 Pass 结束 `DestroyImmediate` 自身。
2. **法线 90° 装箱**：网格切线绝不重算。旋转岛时同步旋转法线贴图像素的切线空间 XY（以及 DXT5nm 解码/重编码）。这是用户原文「切线数据保持原样」的安全实现，否则光照会错。
3. **UV 组 vs 类型组**：用并查集把「同 UV」「同原贴图的所有岛必须同图集」连通。几何布局只算一次；类型组只决定往哪张图集盖像素。共享 UV 的不同类型组输出**同尺寸同岛位**的平行图集。
4. **AAO 未安装**：`UVUsageCompabilityAPI` 用反射调用，asmdef **不**硬引用 AAO。
5. **生成贴图**：写入 `Assets/_ATO_Generated/<avatar>/` 以便 TextureImporter（MipStreaming/Crunch/平台格式）生效；同时 `AssetSaver.SaveAsset` 保证构建克隆可序列化。取消时保留磁盘文件、释放 CPU/GPU/内存。
6. **lilToon**：按属性表 + 关键字分析；`_ST`/`ScrollRotate`/Decal/MatCap/Cube/POM 等变形用途一律白名单 + warning。未知着色器走标准关键字 + 属性启发式，失败则白名单。
7. **质量挡位**（学术依据：Wang MS-SSIM / CIEDE2000 / 法线角误差惯例）：
   - NearLossless：目标质量=1，跳过 UV 缩放（含纯色）
   - Ultra / High(默认) / Medium / Low：见 `QualityParameters`
   - Custom：默认全 1，不被其它挡位覆盖
8. **装箱**：4px Burst 位掩码光栅 + 全扫描 BLF + 面积降序 + 边长降序 + 90° 转置，不用矩形装箱。

## 已完成工作 / Done

- 完整阅读并按源码使用：NDMF 1.14.4、MA 1.18.2、AAO 1.9.17 UVUsageCompabilityAPI、lilToon 2.3.4 属性/关键字、VRC SDK 3.10.4、avatar-compressor 0.9.0、LLC 2.13.0。
- Runtime 组件 + 平台覆盖 + 质量参数 + 扩展接口。
- Editor：NDMF Plugin/Pass、完整管线、UV 岛、质量评估（Burst+GPU）、图集、应用、去重、i18n、Inspector、Compute/Shader。
- 英文/简体中文 i18n；注释中英双语。
- README.md（用户 + 第三方扩展）。

## 未完成 / Out of scope（需求明确排除或环境限制）

- NDMF 预览（需求：暂不支持）
- 本沙箱无 Unity Editor，无法在此对模型做真实烘焙验证。用户需同步进工程验证。
- 版本迁移（需求：开发阶段可不考虑）

## 关键注意 / Pitfalls

- 组件必须挂在带 `VRCAvatarDescriptor` 的物体上；整棵 Avatar 只允许一个。
- 只改网格 UV 与贴图引用，绝不改材质其它着色器参数。
- 白名单对象上引用的贴图跳过**所有**优化；同 UV 的其它贴图跳过图集化，但仍可整图缩放与导入参数优化。
- 去重若命中白名单，去重结果也视为白名单。
- 开启 Mipmap 时强制 MipStreaming（VRC 要求），只暴露一个开关。
- 图集强制 Clamp、关闭 Read/Write，用户不可改。
- 日志前缀 `[ATO]`，可用组件/Prefs 开关详细日志。
- 扩展：`ShaderAnalyzerRegistry` / `AtoHookRegistry`。

## 管线步骤 / Pipeline

1. Validate → 2. Whitelist → 3. Renderers+Animation → 4. Shader analyze
5. Texture dedup → 6. UV-texture map → 7. Islands (wrap, overlap, multi-UV)
8. Area (blendshape 0/100, max scale) → 9. Quality scale
10. Type/UV groups → 11. Atlas or whole-tex scale → 12. Rewrite mesh/mat/anim
13. Dedup mat/tex + opaque slot merge → 14. Importer → 15. AAO evacuate
16. Strip component → 17. NDMF report

## 依赖（不修改第三方）

- nadena.dev.ndmf >= 1.14.4
- com.vrchat.avatars >= 3.7.0
- 可选：AAO、lilToon、MA（相位约束为弱依赖）

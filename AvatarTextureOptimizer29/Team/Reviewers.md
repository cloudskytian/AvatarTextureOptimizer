# Reviewer 组过程记录（每批代码的审查结论）

## R-M1 组件/配置/asmdef
- R1: Runtime asmdef 需 precompiled VRC DLL（照 AAO 模式）→ 已修正: overrideReferences+VRCSDK3A.dll+VRCSDKBase.dll。
- R2: 枚举命名统一 Ato 前缀 → 通过。R3: 组件需 [DisallowMultipleComponent]+IEditorOnly → 已加。
- 共识: 通过（修正后）。

## R-M2 扫描/着色器/动画
- R1: lilToon 表需覆盖 lts 系列全部纹理属性（已对账 shader Properties 提取清单）。通过。
- R2: 动画 float 曲线对 ST 的修改检查 binding 名须含 "material." 前缀两种形态（GameObject 绑定无前缀）→ 已修。
- R3: 去重哈希必须含导入设置（sRGB/mip/filter/wrap/压缩格式）→ 已实现 AtoTextureIdentity。
- 共识: 通过（修正后）。

## R-M3 岛提取
- R1: UV 边哈希需量化 (1e-6) 防浮点抖动 → 已加。R2: 重叠合并传递闭包 → 已用 union-find。
- R3: 归一平移必须逐分量统一（同一 mesh+channel 整体平移）→ 已实现。共识: 通过。

## R-M4 质量
- R1: CIEDE2000 常数与分支按 Sharma 2005 标准实现 → 对拍测试已加（Tests/MetricsTests）。
- R2: MS-SSIM 权重和=1 校验；短边<176 单尺度；<11 忽略 → 已实现 IsRegionEligible。
- R3: 预乘alpha下采样后比较需再除alpha(避免黑边) → ResamplePremultiplied 已处理。共识: 通过。

## R-M5 装箱
- R1: 位掩码膨胀必须在"图集分辨率"坐标系做（岛掩码随候选尺寸重算）→ 设计如此，通过。
- R2: BLF 需验证旋转不越界 + 转置后字长处理 → Tests/PackingTests 已覆盖。共识: 通过。

## R-M6 应用
- R1: 顶点分裂必须同步分裂 boneWeights/colors/所有uv通道/形态键 → MeshRewriter.SplitVertex 已处理。
- R2: 法线图集通道布局: PC DXT5/BC7→AG, BC5→RG, 移动 ASTC→RGB(unpack RGorAG 兼容 a=1) → 已实现。
- R3: 槽位合并须同步更新动画 m_Materials 索引与 renderer.sharedMaterials → FinalDedup 已处理。
- 共识: 通过（R2 备忘: ASTC 法线走 RGB 布局+alpha=1，UnpackNormalMapRGorAG 兼容）。

## R-M7 集成
- R1: finally 必须 ClearProgressBar + 释放 RT → ATOPass 已做。R2: 取消异常须报 Information 而非 Error → 已做。
- R3: 组件移除要在所有 stage 之后 → ATOPass 末尾 DestroyImmediate。共识: 通过。

## R-M8 UI / R-M9 测试与文档
- R1: 语言切换即时生效（Repaint）；R2: 平台覆写未勾选时隐藏参数区 → 已实现；R3: README 含扩展API文档。
- 共识: 通过。

# 需求核对表 / Requirements Checklist

24 条需求 → 实现位置映射（截至 v0.1.0，QA-E/F 验收版）。
Mapping of the 24 requirements to implementation locations (v0.1.0, QA-verified).

| # | 需求 | 实现 | 状态 |
|---|------|------|------|
| 1 | 网格 UV→贴图映射，无视材质其他参数 | `Stage1_Discovery` + `Stage2_UV`（UV 组=渲染器+子网格+通道；类型组键不含材质参数） | ✅ |
| 2 | 目标质量算法（线性重采样/预乘 alpha/MS-SSIM/ΔE2000/alpha 最严/法线角度/灰度 RMSE/二分+双轴/纯色短路/质量=1 跳过） | `Stage3_Quality` + `QualityJobs`(Burst) | ✅（GPU 批量为 CPU-Burst 路径，见偏差） |
| 3 | 缩岛（有图集）/缩整图（无图集）、剔除未用 UV、重组图集 | `Stage3`/`Stage5b_WholeTexture`/`Stage4`+`Stage6` | ✅ |
| 4 | 类型组、同 UV 组跨图集共位、非主色 plane 整平面缩放 | `Stage4`（typeKey 分键、岛全局矩形登记、别名队列、**同键同尺寸不变量**）、`Stage5_Bake`(planeScale) | ✅ |
| 5 | 装箱：4px 位掩码 Burst 光栅 + 全扫描 BLF + 面积/边长降序 + 90°转置 + POT/NPOT 候选池 + 贴图队列原子 + 放不进则整组放弃 | `Stage4_Packing` + `RasterJobs` | ✅ |
| 6 | padding=ceil(maxSide/128) 钳 4 倍数、档位 4/8/16/32/64、pull-push 外扩（alpha 保 0） | `Stage4.PadPx` + `Stage5_Bake.PullPushFill` | ✅（CPU 金字塔版，见偏差） |
| 7 | 白名单（任意对象展开、去重桶感染、同 UV 组跳过图集化） | `Stage1`（展开+感染）+ `Stage4`（整组跳过） | ✅（口径见 README 偏差 4） |
| 8 | 安全限制（无 ST/旋转/动画/特殊用途、仅启用渲染器、仅 Texture2D、多通道 UV；只改贴图） | `ShaderAnalysis`(R_* 原因码)、`Stage1` | ✅ |
| 9 | 贴图去重（像素+导入设置）并更新引用 | `Stage1`(FNV-1a 双哈希) + `Stage7b`(产物字节哈希) | ✅ |
| 10 | 图集开关（默认开；关→整图缩放+其他优化） | `ATOPlugin` 分支 + `Stage5b` | ✅ |
| 11 | 形态键 0/100 最大、动画缩放最大 | `Stage2.ComputeWorldAreas` | ✅ |
| 12 | 越界整数归一、跨缝白名单、重叠岛合并、各向异性、木桶效应 | `Stage2`(TryNormalize/MergeOverlapping) + `Stage3`(uni=木桶) | ✅ |
| 13 | 动画兼容（材质切换/多槽/render-mode/Cutoff 取最严） | `Stage1b_AnimationScan` + `Stage7c_Clips`(克隆改写) | ✅ |
| 14 | lilToon + 标准关键字自动分析，无法兼容→白名单+warning | `ShaderAnalysis` | ✅ |
| 15 | 压缩格式安全枚举 + 平台 override（折叠/勾选显示/默认当前平台）+ iOS 无 PVRTC | `Stage5_Bake.ConfigureImporter` + `ATOInspector` | ✅ |
| 16 | MipStreaming⇔Mipmap 绑定单开关分贴图分类；图集关 R/W、强制 Clamp、取源最高质量 | `Stage5_Bake` / `Stage5b` | ✅ |
| 17 | 材质/贴图去重开关 + 相同不透明槽合并 | `Stage7b_Dedup` | ⚠️ 偏差：引用统一，拓扑合并委托 AAO |
| 18 | 图集命名 ATO_、数量不限 | `Stage5_Bake`(`ATO_Atlas_*`) / `Stage5b`(`ATO_Whole_*`) | ✅ |
| 19 | 组件规则：每 Avatar 一个、须挂 Descriptor 对象、违规报错中止 | `ATOPlugin` 校验 + `ATOInspector` 红字 | ✅ |
| 20 | 内存友好、可取消（保留磁盘资产、释放资源）、构建阶段进度 | `ImageCache`(LRU 768MB+finally 释放)、`CancelCheck`、各阶段 finally | ✅ |
| 21 | 烘焙后移除自身；NDMF 报告（总体默认、细节折叠）；日志含耗时/来源/利用率/优化量 | `Stage8_Report` + `ATOLog.StageTimes` | ✅ |
| 22 | MA 后 AAO 前；兼容 AAO `UVUsageCompabilityAPI`；无 AAO 可工作 | `ATOPlugin`(Optimizing+BeforePlugin) + `AAOCompat`(反射查询避让) | ✅ |
| 23 | 扩展接口、i18n 可扩展（Auto 跟随 NDMF、缺译回退英文）、en+中文、双语注释 | `ATOAPI` / `ATOL10n` / `i18n/*.json` | ✅ |
| 24 | 表现一致、不安全一律 fallback；暂不支持预览 | `Stage6` 安全锁存（skip⇄blocked 不动点）、各处 `R_*` 回退 | ✅ |

## 偏差汇总 / Documented deviations

1. 质量评估与 pull-push：Burst 加速的 CPU 实现（GPU 批量管线规划中）。
2. 蒙版质量门：使用通道**合并 RMSE**（近似逐通道最差，判据略宽）。
3. 需求 17 槽合并：材质去重后相同槽共享引用；不合并子网格拓扑、不重映射动画槽索引（委托 AAO MergeSkinnedMesh）。
4. 白名单口径：白名单感染整个 PackingGroup 跳过图集化；组内非白名单贴图仍整图缩放，白名单贴图本身完全不动。
5. NPOT 为实验开关（Unity 对 NPOT+压缩会自动降级）。
6. 不支持 NDMF 预览。

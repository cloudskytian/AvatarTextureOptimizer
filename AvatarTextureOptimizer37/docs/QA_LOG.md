# QA 验收日志 / QA Acceptance Log

> 规则：Coder 彻底完成整项目且通过 Reviewer 验收后，QA×3 **各自独立、从头完整**
> 阅读全部代码，分别输出验收报告（需求符合性 / 隐患与 Bug / 边界与资源安全）。
> 只有 3 个 QA 同时 PASS 才能交付；任何 FAIL → 同时通知 Reviewer 与 Coder 打回。
> Rule: after the project is fully complete and reviewer-approved, 3 QAs each
> independently re-read ALL code from scratch and report. Only 3/3 PASS
> allows delivery; any FAIL bounces back to reviewers + coders.

## 轮次 / Rounds

（项目完成后在此记录）

## QA-1 需求符合性验收（2026-08-21，独立全量审阅 CLAUDE.md §7 全部条目 vs 代码）

### 结论：FAIL → 修复后 PASS
发现 6 处不符合/风险，全部修复：
1. 禁用（且未被动画启用）渲染器被错误纳入处理 → 已按规范过滤。
2. 白名单对象类型回退缺失（一般 Component）→ 已加（白名单其 GO 上渲染器贴图）。
3. 装箱循环未严格按规范（首个装下全部队列的候选才胜出；padding 未随候选边长动态计算）→ 已重写主循环（含动态 padding = max(min, ceil(maxEdge/128), 4)、边长次级排序）。
4. 优化后贴图/图集去重开关未落地 → 已实现（采样哈希+尺寸+类别，RegisterReplacedObject 重绑定，冗余贴图销毁）。
5. MeshRenderer 的 AAO UV 通道冲突未检测（API 仅支持 SMR）→ 反射检测 RemoveMeshByMask/RemoveMeshByUVTile（字段/枚举按 AAO 源码校正：materials 为 internal 数组、uvChannel 为 UVChannel 枚举、RemoveAnyTile 门控；失败保守取 0..3）。
6. Inspector 格式选项未按平台限制 → 仅显示当前平台安全枚举（Auto + SafeFormats）。

### 逐项核对通过 / Verified（34 项）
白名单语义/去重传播/图集开关/形态键0-100/动画最大缩放/多通道UV/越界归一/重叠岛合并/各向异性双轴/动画兼容（切换+Cutoff+渲染模式+多槽）/lilToon+标准+扩展分析器/质量算法（MS-SSIM 176/11 回退、ΔE2000、alpha IoU/RMSE 最严、法线 p95、灰度逐通道、上采样回比、二分最严阈值、木桶）/纯色短路/密度默认与档位/质量档联动+自定义不覆盖/UV组同位/装箱细节（4px 光栅、BLF、面积+边长排序、90° 旋转、候选池、队列规则、放弃+warning）/NPOT 与 PVRTC 剔除/压缩安全枚举+fallback 警告/Mipmap+MipStreaming 绑定+默认开/平台 override 默认当前构建平台/图集 Read/Write 关+Clamp 强制/危险选项剔除+安全 fallback/图集数量不限/ATO_ 前缀/材质与贴图去重开关+槽合并+动画重映射/单组件+描述符校验+报错中止/内存策略/无预览/进度+取消（原子 Apply 前取消=未改）/自移除/报告（摘要+折叠细节）/MA 后 AAO 前/UVUsageCompabilityAPI 兼容+无 AAO/[ATO] 日志+耗时+开关/i18n JSON 可扩展+en/zh-Hans/扩展 API 5 接口/一致性安全（共享资产克隆+重绑定）。

## QA-2 隐患与 Bug 验收（2026-08-21，独立全量代码审阅）

### 结论：FAIL → 修复后 PASS
1. 装箱队列面积误用包围盒（应光栅化单元面积）→ 修正（Cells*16）。
2. border padding 取整向下 → ceil((pad+3)/4)。
3. MaterialDedup / TextureDeduplicator 缺 `using System.Linq`（`.ToList()`/`.Distinct()` 编译错误）→ 补。
4. **报告阶段访问已销毁组件**（Apply 已 DestroyImmediate，Unity fake-null 导致 verbose 细节永远跳过）→ 捕获 verbose 标志前置。
5. Linq 使用全量扫描（逐文件核对 usings）→ 无其他缺失。

## QA-3 边界与资源安全验收（2026-08-21，独立全量代码审阅）

### 结论：FAIL → 修复后 PASS
1. NPOT 候选池规模爆炸（8000+ 候选 × 每候选重建掩码）→ 池裁剪（最小 256 + 最大 16）。
2. pull-push BFS 用 `Queue<(int,int)>`（8192² 页可达数百 MB 元组对象）→ 扁平 int[] 环形队列。
3. SampleHasAlpha / SampledKey 全图 GetPixels（8K 贴图 256MB × 多次）→ 4 条带采样。

### 边界场景核对通过 / Verified
- 空 Avatar / 全白名单 / 单材质单岛 / 多 UV 通道 / 无 AAO / 移动端上限 4096 / NPOT 过滤 / 取消路径（原子 Apply 前取消=未改+ErrorReport）/ 共享资产零改动（克隆+RegisterReplacedObject）/ 内存：区域解码按组释放+finally DisposeAll+条带采样 / 无泄漏（进度窗口 Detach、日志器 Dispose）。

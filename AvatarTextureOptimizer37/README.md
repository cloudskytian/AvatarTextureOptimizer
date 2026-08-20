# ATO — Avatar Texture Optimizer

> 面向 VRChat Avatar 的开源 NDMF 贴图优化工具 / An open-source NDMF texture optimizer for VRChat avatars.
> 包名 / Package: `net.fosa.avatar-texture-optimizer` · 运行位置 / Runs: Modular Avatar 之后、Avatar Optimizer (AAO) 之前

---

## 它做什么 / What it does

在 NDMF 烘焙/构建时，ATO 对 Avatar 上**满足安全条件**的贴图做质量感知的优化：

1. **分析**：遍历启用（或被动画启用）的 `SkinnedMeshRenderer`/`MeshRenderer`（跳过 EditorOnly），识别主色/法线/蒙版/自发光贴图，检测 ST 变换、UV 滚动旋转、特殊用途（渐变/抖动/MatCap 等）、repeat 跨缝等风险——任何一条不满足即**白名单化**（跳过+警告），绝不盲目处理。
2. **去重**：处理前按"实际像素 + 导入设置"对贴图去重并更新全部引用（材质+动画）；去重组含白名单则整组白名单。
3. **UV 岛提取**：多通道 UV 独立处理；同贴图重叠岛合并；越界可整体平移归一的岛自动归一；跨 wrap 缝的贴图白名单化。
4. **质量缩放**：以"目标质量档位"为基准，对每个 UV 岛二分搜索最大可用缩放——指标全部达标才通过：MS-SSIM（<176px 回退单尺度 SSIM，<11px 忽略）+ ΔE2000 + alpha（Cutout 用 clip 后轮廓 IoU / Blend 用线性 RMSE，多材质引用逐一评估取最严）+ 法线角度 p95 + 灰度逐通道最差 RMSE。先均匀缩放、后双轴独立细化（各向异性防浪费）；纯色岛直接缩到最小；形态键只取 0/100 最大面积；动画缩放取最大面积；像素密度（默认 2048–4096 px/m，档位 512–8192）限制预算，绝不超采样原图。
5. **图集打包**：4px 粒度光栅化 + 全扫描 BLF + 90° 旋转步进 + 候选图集池（POT 64–8192，移动端 4096；实验性 NPOT 步进 64 自动剔除 PVRTC 等不支持格式）。同一贴图的所有岛必在同一图集页；共享 UV 的贴图（含动画切换、跨类型组）构成 **UV 组**，所有图集页对同一 UV 保持**相同归一化位置**，防止有/无法线材质切换时错位。类型组（色彩空间×过滤模式×是否带法线/蒙版/自发光）共同生成主图集 + **镜像图集**（法线/蒙版/自发光各自一页，布局相同、尺寸可按质量上限缩小省体积）。装不下 → 开新页；单贴图装不进最大图集 → 放弃图集化改整图缩放 + 警告。
6. **合成与重映射**：图集页合成（边缘 pull-push 无限外扩填充空白，透明页空白 alpha 保持 0；法线重采样后重归一化、切线数据绝不重算），UV 重映射回网格（旋转折叠进 UV 映射），材质**仅贴图槽位**更新——其他任何着色器参数一律不动。
7. **导入参数**：按不透明/透明（按图集是否含 alpha 区分）/法线/灰度四类提供**安全压缩格式枚举**（平台×NPOT×通道需求过滤，不安全自动回退+控制台警告）；Mipmap 与 MipStreaming 绑定（VRChat 要求）单一开关控制，默认开启；图集强制 Clamp + 关闭 Read/Write。
8. **去重与合并**：优化后内容+参数相同的材质/贴图/图集去重并更新引用（含动画）；同网格**不透明**材质相同且动画未单独切换时合并子网格与材质槽，动画槽索引同步重映射。
9. **兼容与安全**：
   - AAO `UVUsageCompabilityAPI`（SMR 通道撤离；MR 反射检测 RemoveMeshByMask/RemoveMeshByUVTile）；未安装 AAO 完全可用。
   - 共享资产安全：需要修改的网格/材质/贴图**先克隆再改**，经 NDMF ObjectRegistry 重绑定全部引用——用户源资产零改动。
   - 白名单 UV 组保持原 UV 不重映射（白名单贴图映射不被破坏）。
   - 原子 Apply：阶段 1–6 只算内存 PLAN，阶段 7 一次性写入；**任何时点取消 = Avatar 保持原样**（临时资产保留磁盘，释放 CPU/GPU/内存，NDMF 报错提示）。
   - 烘焙/构建后自动移除自身组件；控制台输出报告（默认摘要，verbose 显示每图集来源/岛数/尺寸/利用率/优化量，完整日志写入临时文件）。
10. **i18n**：`i18n/*.json` 扁平键值，**加文件即加语言**；默认 Auto 跟随 NDMF 语言，缺失回退英文；控制台日志恒英文（`[ATO]` 前缀+耗时）。

## 安装 / Installation

1. 通过 VPM 或把本包放入工程 `Packages/`（依赖：NDMF ≥1.14.4、VRC SDK ≥3.7、Unity 2022.3+）。
2. 在 Avatar 根（带 `VRCAvatarDescriptor` 的对象）上添加 **ATO/Avatar Texture Optimizer** 组件。每个 Avatar 只允许一个；挂载对象必须带描述符，否则报错中止构建。
3. 按需配置（见下），直接 NDMF Process 或构建 Avatar 即可。

## 使用 / Usage

- **质量档位**：近无损(1.0) / 高(0.95) / **中(0.90，默认)** / 低(0.80) / 极限(0.70) / 自定义。档位决定 SSIM/ΔE2000/alpha/法线/灰度阈值（联动）；自定义档的阈值参数独立保存、不被其他档位覆盖（默认全近无损）。
- **像素密度**：min/max px/m（512/1024/2048/4096/8192，默认 2048/4096）——按"模型真实大小"估算岛世界尺寸（网格包围盒/UV 跨度），控制目标像素预算。
- **图集**：开关（默认开；关闭=不剔 UV、不重排、整图直接缩放）；最小岛间距（4/8/16/32/64，实际 padding = max(所选, ceil(图集最大边/128))）；实验性 NPOT。
- **Mipmap**：每类别一个开关（= Mipmap + MipStreaming 绑定），默认开。
- **压缩格式**：每类别安全枚举（Auto + 当前平台安全格式）。
- **平台 Override**：勾选后按 PC/Android/iOS 覆盖格式/Mipmap/NPOT；默认值读取当前构建平台。
- **白名单**：任意对象（网格/材质/贴图/动画/组件/游戏对象…）。白名单对象引用的全部贴图跳过**所有**优化；与其同 UV 的贴图跳过图集化、保留整图缩放+导入优化。
- **去重开关**：材质去重（默认开）、贴图/图集去重（默认开）。
- **语言**：Auto（跟随 NDMF）或已加载语言。
- **日志**：verbose + 类别掩码（高级用户）。

## 已知行为与取舍 / Known behavior & tradeoffs

- SSIM 在 2048 分辨率上限内计算（大区域性能）；其余指标全原尺寸比较。
- 近无损档下 UV 组采用成员最小 px/UV（木桶）保持布局一致，个别高分辨率成员可能轻微重采样（放大上限 2x）。
- 形态键面积系数按 submesh 0 近似；材质关键字比较使用已知关键字表（未覆盖的关键字差异不会导致**误**合并，只会漏合并——安全方向）。
- 90° 旋转按规范执行（位掩码转置；法线像素随布局转置、切线数据不重算）。
- 质量评估为 CPU 密集（大岛多时较慢）；内存采用按组解码+释放+条带采样，典型 Avatar 峰值可控（8K 单图整图缩放会有约 256MB 一次性峰值）。
- 尚不支持 NDMF 预览（预览系统未接入）。

## 第三方开发 / Third-party development

公开 API 程序集：`net.fosa.avatar-texture-optimizer.api`（引用它即可，勿引用 editor 实现）。

| 接口 | 用途 |
|---|---|
| `IATOShaderAnalyzer` | 为自定义着色器提供贴图属性表（角色/UV 通道/ST/开关属性/特殊用途）与透明模式解析；内置分析器之后查询，第一个成功者生效 |
| `IATOWhitelistContributor` | 构建开始时贡献白名单对象 |
| `IATOQualityMetric` | 追加自定义质量指标（与内置指标"与"关系，全部达标才通过） |
| `IATOAtlasPacker` | 替换装箱器（须遵守 UV 组同位与同贴图同页约束） |
| `IATOTexturePostProcessor` | 图集/缩放贴图保存前的像素后处理 |

注册方式：`ATOApiRegistry.Register(...)`（如 `[InitializeOnLoad]` 中），或提供公开无参实现类自动发现。

## 目录结构 / Layout

```
package.json
i18n/en.json, zh-Hans.json          # 语言文件（可新增）
Sources/Runtime/                    # ATOComponent + 枚举 + 质量参数
Sources/Api/                        # 公开扩展接口（autoReferenced）
Sources/Editor/
  ATOPlugin.cs                      # NDMF 插件注册（Optimizing 相位，MA 后 AAO 前）
  ATOPipelinePass.cs                # 8 阶段管线
  Stages/                           # 阶段入口
  Analysis/                         # 白名单/着色器/动画扫描、去重、岛提取、分组
  Quality/                          # 解码/光栅化、指标、二分缩放
  Packing/                          # 候选池、BLF、装箱主循环
  Atlas/                            # 合成（主图+镜像页）、UV 重映射、动画改写、最终贴图解析
  Import/                           # 格式安全、导入计划
  Dedup/                            # 材质去重/槽合并、生成贴图去重
  Apply/                            # 原子应用（共享资产克隆+重绑定）
  Report/                           # 控制台报告
  Interop/                          # AAO 反射互操作
  I18n/                             # JSON i18n
  UI/                               # 检查器 UI、进度/取消窗口
docs/                               # 计划、Coder 共识、Reviewer/QA 日志
CLAUDE.md                           # 项目记忆
```

## 许可 / License

MIT（见 package.json；如需更换请修改）。

# AgentTeam 共识记录 / AgentTeam Decision Log

> 流程：Coder A/B 先讨论达成共识 → 落码 → Reviewer A/B 共同审查达成共识后放行/打回。
> 项目全部完成后 QA A/B 独立通读全部代码，双通过才打包 zip 交付。

## 共识记录（按时间倒序）

### 2026-08-19 — 阶段 1：脚手架 + 分析管线（Coder A/B 共识，Reviewer A/B 通过）

1. **包结构**：VPM 包 `Packages/net.fosa.avatar-texture-optimizer`；Runtime asmdef 零第三方依赖
   （组件+设置），Editor asmdef 引用 NDMF + VRC SDK（vpmDependencies 保证存在）。
2. **排序**：Optimizing 相位，AfterPlugin 两个 MA 限定符（主 + late-transform-stages，后者为 Transforming 相位，
   添加无害且防未来相位变化）、BeforePlugin AAO。
3. **AAO 兼容**：反射适配 `UVUsageCompabilityAPI`（程序集 com.anatawa12.avatar-optimizer.api.editor），
   AAO 缺席自动降级；不引用 AAO 程序集（保证可选依赖）。
4. **动画扫描**：直接读取描述符层 + 子级 Animator 控制器（不经 NDMF 虚拟化——分析只读）；默认层跳过；
   记录 clipRefs 供白名单动画传播。
5. **去重键**：importKey（全部导入设置含三平台覆盖）+ pixelKey（imageContentsHash 优先，兜底像素/文件哈希）；
   哈希碰撞时精确比对（像素或文件字节）。
6. **白名单级别**：Full / NoAtlas 两级（NoAtlas 用于"同 UV 其他贴图"，待 UV 组阶段实现）。
7. **liltoon 表**：取证 2.3.4 lts.shader 全部贴图属性；UVMode/ScrollRotate/NoScaleOffset/特殊用途全建模。
8. **i18n**：Localization/*.json 单层映射 + 极简解析器（无第三方依赖）；Auto 读 NDMF LanguagePrefs；
   zh-hans/en-us 命名与 NDMF 一致。
9. **错误报告**：NDMF SimpleError + Localizer（loader 指向 ATOLocalization.Raw）。
10. **取消语义**：进度条取消 → ATOCancelledException → NDMF 中止；临时资产保留磁盘；资源随栈展开释放。
11. **注释规范**：所有注释中英双语。
12. **防御**：EditorOnly 双保险（NDMF 已删 + 自检）；未挂载组件静默跳过。

### 待议（下阶段共识）

- 岛提取的 wrap 感知归一（Repeat 平移 / Clamp 折叠 / Mirror fallback）的边界情况。
- UV 组锚定装箱与"同贴图岛同图集"约束冲突时的回退顺序。
- GPU 质量评估的 RenderTexture 批处理细节（MS-SSIM 金字塔、ΔE2000 归约）。
- 材质槽合并的动画路径重写方案（NDMF ObjectPathRemapper / animator 工具）。
- 导入设置修改（reimport）与 AAO 读取时机的同步策略。

### 2026-08-19 — 阶段 2：核心算法与全功能（Coder A/B 共识，Reviewer A/B 通过）

1. **UV 映射数学**：UV' = contentOrigin + rot((uv−uvMin)·scale·texSize)/atlasSize。
   同岛全部贴图必须**分辨率一致**才能共享 UV 映射（否则 UV 冲突）→ 不一致时全岛 NoAtlas 回退 + warning。
2. **旋转约定**：rotation ∈ {0,1,2,3} = 内容视觉逆时针 r*90°（与 BitMask.Rotate90 一致）；
   装箱、图集写入（ContentToLocal）、网格 UV 重写（LocalToContent）三处共用 IslandTransform。
3. **装箱原子单位 = 贴图连通簇**：共享贴图的岛并查集合并，保证"同一张贴图的所有岛在同一图集"；
   簇按总面积降序；队列=图集组（同类型组各图集统一尺寸 → UV 组归一化位置一致）；试放于掩码副本，全成功才提交。
4. **密度语义**：> max → 缩放上限（防浪费）；< min → 下限 1（防发糊）；岛物理像素钳制恒成立（scale ≤ 1 从不放大）。
5. **质量评估架构**：GPU（RenderTexture）做贴图线性化+预乘解码、图集 pull-push、编码；
   Burst（CPU 并行）做逐岛指标归约与二分搜索（确定性优先，GPU 指标归约留作后续优化）。
6. **ΔE 统计量**：取均值（业内常用；p95 备选记录在案）。
7. **各向异性**：先均匀二分达标 → X 轴二分 → Y 轴二分（先 X 后 Y 的顺序已记录，可调）。
8. **材质克隆策略**：每材质一个克隆（贴图连通簇保证同贴图→同图集，克隆内容全局一致）；
   动画切换材质同样克隆（use.sourceMaterial 记录归属）；ObjectRegistry 1:1 注册；
   同一贴图被多个图集替换时不注册，由 AnimationBindingRemapper 按属性重写动画曲线。
9. **槽位合并安全条件**：槽位与子网格必须一一对应（连续索引、数量相等）且动画绑定全在临时资产上；否则跳过+warning。
10. **fallback 贴图策略**：整图缩放或导入副本绝不修改用户源资产；DXT5nm 法线回退时先解码为 xyz。
11. **AAO 疏散**：被 AAO 使用的通道备份到空闲通道（7 往下找）+ RegisterTexCoordEvacuation；无空闲通道 → 中止构建。
12. **MA 设为依赖**（vpmDependencies），保证动画控制器被 NDMF 克隆（临时资产），绑定重写安全；
    非临时资产仍走安全回退。
13. **取消语义**：ATOCancelledException → OperationCanceledException 交给 NDMF；硬盘临时资产保留。
14. **图集候选池**：仅生成正方形边长（POT 2^n / NPOT 64 步进），按面积升序+纵横比升序排序（规格只定义了边长）。

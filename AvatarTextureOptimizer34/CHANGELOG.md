# Changelog

## [0.1.0] - 2026-08-20

- 首个可同步至 Unity 工程验证的完整版本（开发阶段，配置字段可随意变更，无版本兼容性承诺）
- 完整 NDMF 管线：MA 之后 / AAO 之前，阶段化处理、进度条、可取消、报告输出
- UV 岛分析（Burst 连通域、重叠合并、形态键 0/100、动画缩放、越界归一、多通道 UV、wrap 跨缝白名单）
- 动画分析（材质/贴图切换、ST、渲染模式、Cutoff、启用、缩放）
- 贴图去重（像素+导入设置）、解码 LRU 缓存
- 着色器属性表自动分析（liltoon/标准/通用）+ 名称模式规则表
- 质量评估：MS-SSIM/SSIM、CIEDE2000、alpha（Cutout IoU/Blend RMSE）、法线角度误差、灰度 RMSE；二分搜索（均匀→双轴）；像素密度钳制；纯色短路；近无损跳过
- GPU 度量 compute shader 路径（自检后启用）+ CPU Burst 路径
- 图集：Burst 4px 掩码光栅化 + BLF 装箱 + 旋转转置 + 候选池（POT/NPOT）+ padding + 模板布局（UV 组同位）
- pull-push GPU compute（CPU 扩张 fallback）
- 资产写入：压缩格式安全枚举（透明/不透明/法线/灰度 × 平台）、Mipmap⇔MipStreaming 绑定、Clamp/只读强制
- 引用重写（网格/材质/动画）、材质贴图去重、材质槽合并、AAO UVUsageCompabilityAPI 反射兼容
- i18n（en/zh-CN JSON，Auto 读 ndmf 语言，可手动切换）、扩展接口、[ATO] 日志与构建报告

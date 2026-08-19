# Modular Avatar 1.18.2 — 源码精读笔记

> 来源：`/home/user/_deps/ma`（精确版本 1.18.2）。

## 关键结论
- MA 有两个 plugin：`PluginDefinition`（QualifiedName `"nadena.dev.modular-avatar"`）、`LateTransformPluginDefinition`（`"nadena.dev.modular-avatar.late-transform-stages"`）。
- MA 主要工作在 `Resolving`（PlatformFilter、ResolveObjectReferences、克隆 animator）与 `Transforming`（几乎全部逻辑）。
- **MA 在 Optimizing 阶段也有一个 pass：`GCGameObjectsPluginPass`**（清理 MA 产生的垃圾 GameObject）。它与我同阶段，若无约束则相对顺序不确定。
- MA 用 `AnimatorServicesContext`（NDMF 扩展）克隆/修改动画控制器。

## 对我的顺序要求
```csharp
InPhase(BuildPhase.Optimizing)
    .AfterPlugin("nadena.dev.modular-avatar")          // 完全在 MA 之后（含其 Optimizing GC pass）
    .BeforePlugin("com.anatawa12.avatar-optimizer")    // 在 AAO 之前
    .Run(...)
```
- 两个插件名缺失时 NDMF 均安全（幽灵 pass）。MA 与 AAO 均未安装也能跑。

## 备注
- MA 在 Transforming 已合并 armature/动画（MergeAnimator/MergeArmature），因此我看到的 Avatar 是 MA 处理后的结果——符合「MA 后」预期。
- 动画修改应走 NDMF `AnimatorServicesContext`（见 NDMF.md §9/§5），与 MA 的做法一致。

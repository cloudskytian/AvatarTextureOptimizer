# NDMF AnimatorServices — 动画处理笔记

> 来源：`/home/user/_deps/ndmf/Editor/API/AnimatorServices`。

## 关键 API（用于 M2 动画分析 + M5 引用更新）
- `AnimatorServicesContext`（IExtensionContext）：`ctx.Extension<AnimatorServicesContext>()` 激活。
  - `ControllerContext`（VirtualControllerContext）、`AnimationIndex`、`ObjectPathRemapper`。
- `AnimationIndex`（核心）：
  - `GetPPtrReferencedObjects()` / `GetPPtrReferencedObjectsWithBinding()` → 找到动画中通过对象引用（PPtr）引用的对象（**贴图、材质都属于 PPtr 引用**）。
  - `RewriteObjectCurves(Func<Object,Object> mapping)` / `(Func<EditorCurveBinding,Object,Object>)` → **重写动画里的对象引用**（去重贴图/材质后更新引用用这个）。
  - `RewritePaths(Func<string,string?>)` / `Dictionary<string,string?>` → 重写路径（对象移动/重命名时）。
  - `GetClipsForObjectPath(path)`、`GetClipsForBinding(binding)`、`EditClipsByBinding(bindings, action)`。
  - `ClipsWithObjectCurves`。
- `ObjectPathRemapper`：`GetVirtualPathForObject`、`ReplaceObject(old,new)`、`RecordObjectTree`。
- 虚拟对象树：`VirtualClip`/`VirtualMotion`/`VirtualState`/`VirtualLayer`/`VirtualAnimatorController`/`VirtualBlendTree`。

## 用法约定（与 MA/AAO 一致）
1. 在需要改动画的 pass 里 `WithRequiredExtension(typeof(AnimatorServicesContext))`（或直接 `ctx.Extension<AnimatorServicesContext>()`）。
2. 改材质/贴图引用：`AnimationIndex.RewriteObjectCurves(old -> new)`。
3. 合并材质槽改索引：动画对 `m_Materials.Array.data[i]` 是 PPtr 曲线，可用 `RewriteObjectCurves` 按材质对象映射；对"槽位索引"本身无独立曲线（Unity 动画引用材质用 PPtr 而非索引），故合并槽需保证同名材质 PPtr 映射一致。
4. 扫描"动画切换材质/贴图"：遍历 `GetPPtrReferencedObjects()` 得到的 Material/Texture，合并进收集结果并去重。

## 结论
- M2 用 AnimationIndex 收集动画里 PPtr 引用的 Material/Texture + ObjectPathRemapper 定位 renderer。
- M5 去重/合并后用 `RewriteObjectCurves` 更新动画引用；`ObjectPathRemapper.ReplaceObject` 更新路径。
- 这保证"动画中的材质/贴图引用"被正确处理，满足需求。

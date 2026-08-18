# AvatarTextureOptimizer — 项目记忆

## 目标
VRChat Avatar 开源 NDMF 贴图优化工具。包名 `net.fosa.avatar-texture-optimizer`。

## 状态
代码侧功能已按需求闭环。仓库可打 zip 交给用户同步进 Unity 工程烘焙。

## 装箱（重要）
- 类型组排队；**装箱原子 = UV 组**（同一 UV 的全部贴图）。
- 先用组内面积最大的源贴图岛做 BLF 布局，再复制到同组所有角色岛。
- 每角色各生成一张 `ATO_{Role}_` 图集，归一化 UV 一致。
- 次级图集可整体均匀缩小（归一化 UV 不变）。
- 装不进最大图集则放弃该 **整个 UV 组** 的图集化。

## 安全
- AAO 疏散在 remap UV 之前。
- Instantiate 网格/材质/动画片段与 AnimatorController。
- 只改 UV + 贴图引用。
- 组件与 VRCAvatarDescriptor 同物体，子树仅一个。

## 提交
用户要 zip 交付；不含 `.git`。

# Offline verification harness / 离线校验工具

**EN.** This folder is named with a trailing `~` so Unity ignores it. It is not part of the shipped
package. It exists so that a change to Avatar Texture Optimizer can be checked without opening Unity:

* `build.csproj` compiles the whole package against **real Unity reference assemblies**
  (UnityEngine 2021.3 modules + UnityEditor) and the **real NDMF 1.14.4 sources**, so every NDMF and
  Unity API call is type checked for real rather than against a hand written stub.
* `Tests/` executes the algorithms that can run outside Unity:
  * CIEDE2000 against the official Sharma / Wu / Dalal (2005) verification data (21 pairs)
  * the bit mask shape packer (nesting, rotation, padding, snapshot rollback, overlap refusal)
  * the reference-space to atlas-space UV mapping, including the 90 degree rotation convention that
    has to agree exactly with `Hidden/ATO/IslandBlit`
  * the candidate atlas pool (ordering, power of two and NPOT constraints, padding rule)

Run `./verify.sh`. The first run downloads about 400 MB of reference assemblies into `.verify/`.

**Known limitation.** The reference assemblies are Unity 2021, so NDMF and Burst report a handful of
errors for Unity 2022 only APIs. The script deliberately reports only errors inside the ATO package.

---

**ZH.** 本目录以 `~` 结尾，Unity 会忽略它，它不属于交付给用户的包。它的存在是为了不打开 Unity
也能校验对 Avatar Texture Optimizer 的改动：

* `build.csproj` 用**真实的 Unity 参考程序集**（UnityEngine 2021.3 模块 + UnityEditor）与
  **真实的 NDMF 1.14.4 源码**编译整个包，因此每一处 NDMF 与 Unity API 调用都会被真正做类型检查，
  而不是对着手写的桩来检查。
* `Tests/` 执行可以脱离 Unity 运行的算法：
  * CIEDE2000 对照官方 Sharma / Wu / Dalal（2005）验证数据（21 组）
  * 位掩码形状装箱器（凹槽嵌套、旋转、padding、快照回滚、拒绝重叠）
  * 参考空间到图集空间的 UV 映射，包含必须与 `Hidden/ATO/IslandBlit` 完全一致的 90 度旋转约定
  * 候选图集池（排序、二次幂与 NPOT 约束、padding 规则）

运行 `./verify.sh`。首次运行会向 `.verify/` 下载约 400 MB 参考程序集。

**已知限制。** 参考程序集是 Unity 2021，因此 NDMF 与 Burst 会针对 Unity 2022 专有 API 报出少量错误。
脚本刻意只报告 ATO 包内部的错误。

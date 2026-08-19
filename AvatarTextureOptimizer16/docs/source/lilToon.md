# lilToon 2.3.4 — 源码精读笔记

> 来源：`/home/user/_deps/liltoon`（精确版本 2.3.4）。65 个 shader + Editor C#。

## 1. 着色器命名（用于检测 liltoon）
- 主 shader：`"lilToon"`、`"Hidden/lilToonCutout"`、`"Hidden/lilToonOutline"`、`"Hidden/lilToonFur"`、`"Hidden/lilToonGem"`、`"Hidden/lilToonRefraction"`；另有 `ltsl_*`（Lite）、`ltsmulti_*`（multi）。
- 检测方式：`shader.name` 以 `lilToon`/`_lil`/`Hidden/lilToon` 开头（实现时以 `lilShaderUtils`/`lilConstants` 为准再定）。

## 2. 贴图属性命名（Shader/lts.shader 的 Properties）
- 主色：`_MainTex`、`_Main2ndTex`（2nd 贴花）、`_Main3rdTex`（3rd 贴花）、`_OutlineTex`、`_EmissionMap`、`_Emission2ndMap`。
- 法线：`_BumpMap`、`_Bump2ndMap`、`_MatCapBumpMap`、`_MatCap2ndBumpMap`（声明带 `[Normal]` 属性）。
- MatCap：`_MatCapTex`、`_MatCap2ndTex`（RGB）。
- 各种 mask/灰度：`_AlphaMask`、`_DissolveMask`、`_DissolveNoiseMask`、`_Main2ndDissolveMask`、`_EmissionBlendMask`、`_Emission2ndBlendMask`、`_GlitterColorTex`、`_GlitterShapeTex`、`_AudioLinkMask`、`_AnisotropyShiftNoiseMask` 等。
- `_Ramp`（Shadow Ramp，灰度）。

## 3. 关键常量（Editor/lilConstants.cs）
- `mainTexCheckWords = {"mask","shadow","shade","outline","normal","bumpmap","matcap","rimlight","emittion","reflection","specular","roughness","smoothness","metallic","metalness","opacity","parallax","displacement","height","ambient","occlusion"}`。
- 用途：属性名包含这些词 → **不是主色贴图**（法线/mask/特殊用途）。这是我做「主色 vs 特殊」判定的权威依据。
- 注意 `emittion` 是 liltoon 原样的拼写（含 typo）。

## 4. 判定贴图类型组的思路（M2 实现）
1. 检测 shader 是否 liltoon（按名字）。
2. 对每个 2D 贴图属性，按其属性名归类：
   - 含 `bump`/`normal` → 法线组；
   - 含 `mask`/`shadow`/`shade`/`outline` 等（mainTexCheckWords）→ 蒙版/灰度组；
   - 否则 → 主色组。
3. 结合材质实际槽位 + `[Normal]` 声明 + 关键字开关（如 `_UseBumpMap`/`sNormalMap`、`sCutout`、`sTransparent`）确定渲染模式（Cutout/Transparent/Opaque）与 Cutoff 值。
4. 非法线/非标准的其他 shader：走「标准关键字」路径（Unity 惯例 `_MainTex`/`_BumpMap`/`_MetallicGlossMap`/`_MaskMap`/`_OcclusionMap` 等）。

## 5. 待实现时再读的细节
- `lilShaderUtils.cs` / `lilToonPreset.cs`：精确的属性→类型映射与 render mode 关键字表。
- 法线编码：liltoon 采样标准法线（`[Normal]`），具体 DXT5nm/BC5/BC7 由贴图资源导入格式决定（Unity 侧处理，非 liltoon 侧）。

## 6. 结论
- liltoon 属性名高度一致，可用「名字约定 + mainTexCheckWords + [Normal] 属性」自动归类，满足「自动分析 liltoon 属性表」需求。
- 不兼容/无法判定的 shader → 白名单 + warning（符合需求）。

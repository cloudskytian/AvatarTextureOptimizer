# AvatarTextureOptimizer — Agent Memory

## Project
- Name: AvatarTextureOptimizer
- Package: `net.fosa.avatar-texture-optimizer`
- Namespace: `Net.Fosa.AvatarTextureOptimizer`
- Goal: Open-source NDMF tool that is the best VRChat avatar texture optimizer.
- This is a **Unity package**, not a full Unity project. The user imports it into their own project.

## AgentTeam consensus (Coder ×3 / Reviewer ×3 / QA ×3)

### Feasibility
The requested pipeline is feasible. It is large, but each stage is well-defined and can be implemented as a real bake-time NDMF pass.

### Design corrections vs original spec
1. **Normal-map 90° packing**: Mesh tangents are never recalculated (agreed). Island bitmask may be transposed. **Tangent-space normal XY must be remapped** when the island is rotated 90°, otherwise lighting breaks. This is a required safety transform of *pixels*, not mesh tangents.
2. **Pass order**: NDMF `BuildPhase.Transforming`, `AfterPlugin("nadena.dev.modular-avatar")` **and** `AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")`, also `AfterPlugin("net.rs64.tex-trans-tool")` so TTT (if present) does not undo atlases. AAO runs in `BuildPhase.Optimizing`, so Transforming already runs first. `BeforePlugin("com.anatawa12.avatar-optimizer")` is extra documentation, not required for ordering.
3. **AAO UVUsageCompabilityAPI** (spelling is AAO's): only accepts `SkinnedMeshRenderer`. MeshRenderer has no evacuation. AAO is optional — use reflection, never a hard reference.
4. **Quality = 1**: skip UV-island (and solid-color) scaling and copy texels as-is; atlas generation still allowed.
5. **Missing plugins in AfterPlugin**: NDMF creates phantom plugin phases; safe when TTT/AAO are not installed.
6. **Do not modify any material property except Texture references** (and slot merging when opaque materials collapse).
7. **Whitelist same-UV companions**: skip atlas, still allow whole-texture scale + importer params.

### Quality preset rationale
- Wang et al. 2004 MS-SSIM; CIEDE2000 (Sharma 2005); typical game normal-map angular error.
- NearLossless (all thresholds 1 / skip scale)
- Ultra / High (PC default) / Medium (mobile default) / Low / Custom (user-owned, never overwritten, defaults all 1)

### Third-party APIs actually read (do not guess)
- NDMF 1.14.4: `Plugin<T>`, `Pass<T>`, `BuildPhase`, `Sequence.AfterPlugin/BeforePlugin/Run/WithRequiredExtension`, `BuildContext`, `ErrorReport.ReportError(Localizer,...)`, `ErrorSeverity`, `LanguagePrefs.Language`, `Localizer`, `AnimatorServicesContext`, `AnimationIndex.RewriteObjectCurves`, `ObjectRegistry.RegisterReplacedObject`, `AssetSaver`
- AAO 1.9.17: `UVUsageCompabilityAPI.IsTexCoordUsed` / `RegisterTexCoordEvacuation`; plugin id `com.anatawa12.avatar-optimizer`; AAO main work is Optimizing phase
- MA 1.18.2: plugin ids `nadena.dev.modular-avatar` and `nadena.dev.modular-avatar.late-transform-stages`
- lilToon 2.3.4: `_MainTex`, `_BumpMap`, `_NORMALMAP`, `_UseBumpMap`, `_MainTex_ST`, `_MainTex_ScrollRotate`, `_Cutoff`, transparent mode, multi keyword table in `lilMaterialUtils`
- VRChat 3.10.4: `VRCAvatarDescriptor` lives in SDK3A runtime (DLL); detect by type name to keep runtime optional

## Current plan
1. Package skeleton (asmdef, package.json, i18n, component, plugin)
2. Analysis: shaders, animation, mesh UV islands, whitelist, texture dedup
3. Quality scaling (Burst + GPU RT)
4. Atlas (Burst 4px bitmask + BLF + candidate pool)
5. Apply meshes/materials/animations, AAO evacuate, material slot merge
6. Inspector, platform override, report, extensions
7. README.md + zip

## Progress
- [x] Feasibility + third-party source read
- [x] Package skeleton
- [x] Runtime component + settings
- [x] NDMF plugin / pass / pipeline
- [x] Shader / animation / island analysis
- [x] Quality evaluator
- [x] Atlas packer
- [x] Apply + dedup + AAO compat
- [x] Inspector + i18n + report
- [x] README
- [x] git + zip

## Notes
- Logs start with `[ATO]`. Default summary on NDMF console; details folded in description + Debug.Log when verbose.
- No NDMF preview in this version.
- One component per avatar, must sit on the same object as VRCAvatarDescriptor.
- Generated atlas names start with `ATO_`.
- Burst jobs use 4px raster cells.
- Cancel via `EditorUtility.DisplayCancelableProgressBar`; keep temp assets, release CPU/GPU/memory.
- All comments bilingual (EN / 中文).

## Not done / limits
- Cannot execute Unity bake in this sandbox (no Unity Editor). User must import and bake locally.
- Compute-shader MS-SSIM is approximated by GPU resample + Burst metrics (still matches the requested GPU RT + Burst split).

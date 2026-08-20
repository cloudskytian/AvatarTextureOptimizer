// ATOAvatarScanner.cs — Avatar 扫描器 / Avatar scanner.
// 说明：遍历 Avatar 上全部渲染器（跳过 EditorOnly 与不可能被启用的对象），构建材质槽（含动画切换的材质）、
// 贴图用途、白名单集合与贴图信息注册表。白名单不限制对象类型；白名单对象引用的全部贴图跳过所有优化。
// Note: scans all renderers on the avatar (skipping EditorOnly and never-enableable objects), builds material slots
// (incl. animation-swapped materials), texture usages, whitelist sets and the texture info registry.
// The whitelist is type-agnostic; all textures referenced by whitelisted objects skip every optimization.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>扫描结果。/ Scan results.</summary>
    internal sealed class ATOAvatarScanResult
    {
        public ATOAvatarTextureOptimizer component;                  // 组件 / the component
        public List<ATORendererInfo> renderers = new List<ATORendererInfo>(); // 全部渲染器 / all renderers
        public Dictionary<Texture2D, ATOTextureInfo> textures = new Dictionary<Texture2D, ATOTextureInfo>(); // 贴图注册表 / texture registry
        public HashSet<GameObject> whitelistedObjects = new HashSet<GameObject>(); // 白名单对象 / whitelisted objects
        public HashSet<Object> whitelistedAssets = new HashSet<Object>();         // 白名单资产 / whitelisted assets
        public HashSet<Texture2D> whitelistedTextures = new HashSet<Texture2D>(); // 白名单贴图 / whitelisted textures
        public HashSet<Material> whitelistedMaterials = new HashSet<Material>();  // 白名单材质 / whitelisted materials
        public HashSet<Mesh> whitelistedMeshes = new HashSet<Mesh>();             // 白名单网格 / whitelisted meshes
        public ATOAnimationData animation;                            // 动画数据 / animation data
        public int totalSlots;                                        // 槽总数（统计）/ total slots (stats)
        public List<string> scanWarnings = new List<string>();        // 扫描警告 / scan warnings
    }

    /// <summary>Avatar 扫描器。/ Avatar scanner.</summary>
    internal static class ATOAvatarScanner
    {
        /// <summary>扫描整个 Avatar。/ Scan the whole avatar.</summary>
        public static ATOAvatarScanResult Scan(GameObject avatarRoot, ATOAvatarTextureOptimizer component)
        {
            var result = new ATOAvatarScanResult { component = component };

            // 1. 动画扫描 / animation scan
            var tAnim = new ATOLog.StageTimer("Animation scan");
            result.animation = ATOAnimationAnalyzer.Scan(avatarRoot);
            tAnim.Detail($"{result.animation.clips.Count} clips").Stop();

            // 2. 白名单收集 / whitelist collection
            CollectWhitelists(avatarRoot, component, result);

            // 3. 渲染器扫描 / renderer scan
            var tRen = new ATOLog.StageTimer("Renderer scan");
            var renderers = new List<Renderer>();
            foreach (var mr in avatarRoot.GetComponentsInChildren<MeshRenderer>(true))
                renderers.Add(mr);
            foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                renderers.Add(smr);

            foreach (var renderer in renderers)
            {
                var info = ScanRenderer(avatarRoot, renderer, result);
                if (info != null)
                {
                    result.renderers.Add(info);
                    result.totalSlots += info.slots.Count;
                }
            }
            tRen.Detail($"{result.renderers.Count} renderers, {result.totalSlots} slots").Stop();

            // 4. 材质分析与贴图注册 / material analysis & texture registry
            var tMat = new ATOLog.StageTimer("Material analysis");
            AnalyzeMaterials(result);
            tMat.Detail($"{result.textures.Count} textures").Stop();

            // 5. 白名单扩散（白名单对象引用的全部贴图）/ whitelist propagation (textures referenced by whitelisted objects)
            PropagateWhitelist(result);

            return result;
        }

        /// <summary>收集白名单（组件 + 白名单资产 + 白名单组件）。/ Collect whitelists (component + whitelist assets + whitelist components).</summary>
        private static void CollectWhitelists(GameObject avatarRoot, ATOAvatarTextureOptimizer component, ATOAvatarScanResult result)
        {
            // 白名单资产 / whitelist assets
            if (component.whitelistAssets != null)
            {
                foreach (var asset in component.whitelistAssets)
                {
                    if (asset == null) continue;
                    foreach (var target in asset.targets)
                    {
                        if (target == null) continue;
                        result.whitelistedAssets.Add(target);
                        if (target is Texture2D t) result.whitelistedTextures.Add(t);
                        else if (target is Material m) result.whitelistedMaterials.Add(m);
                        else if (target is Mesh me) result.whitelistedMeshes.Add(me);
                        else if (target is GameObject go) result.whitelistedObjects.Add(go);
                        else if (target is AnimationClip c) WhitelistClipContents(c, result);
                    }
                }
            }

            // 白名单组件（对象或其子树）/ whitelist components (object or subtree)
            foreach (var wl in avatarRoot.GetComponentsInChildren<ATOWhitelist>(true))
            {
                if (wl == null) continue;
                result.whitelistedObjects.Add(wl.gameObject);
                if (wl.includeChildren)
                {
                    foreach (var child in wl.gameObject.GetComponentsInChildren<Transform>(true))
                        result.whitelistedObjects.Add(child.gameObject);
                }
            }
        }

        /// <summary>白名单动画内容（其中引用的材质与贴图）。/ Whitelist animation-clip contents (materials & textures it references).</summary>
        private static void WhitelistClipContents(AnimationClip clip, ATOAvatarScanResult result)
        {
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (frames == null) continue;
                foreach (var f in frames)
                {
                    if (f.value is Material m) result.whitelistedMaterials.Add(m);
                    else if (f.value is Texture2D t) result.whitelistedTextures.Add(t);
                }
            }
        }

        /// <summary>扫描单个渲染器。/ Scan one renderer.</summary>
        private static ATORendererInfo ScanRenderer(GameObject avatarRoot, Renderer renderer, ATOAvatarScanResult result)
        {
            var go = renderer.gameObject;

            // EditorOnly 跳过 / skip EditorOnly
            if (IsEditorOnly(go))
            {
                ATOLog.Verbose($"Skip renderer {go.name}: EditorOnly");
                return null;
            }

            var relPath = GetRelativePath(avatarRoot.transform, go.transform);

            // 启用性判定：默认启用 或 动画可能启用 / enabledness: default active, or possibly enabled via animation
            var defaultActive = go.activeInHierarchy && renderer.enabled;
            var mayEnable = defaultActive || MatchesAnySuffix(result.animation.mayBeActivePaths, relPath) ||
                            MatchesAnySuffix(result.animation.mayBeEnabledRendererPaths, relPath);
            if (!mayEnable)
            {
                ATOLog.Verbose($"Skip renderer {relPath}: never enabled");
                return null;
            }

            // 网格 / mesh
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else if (renderer is MeshRenderer mr2)
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            if (mesh == null)
            {
                ATOLog.Verbose($"Skip renderer {relPath}: no mesh");
                return null;
            }

            var info = new ATORendererInfo
            {
                renderer = renderer,
                mesh = mesh,
                skinned = renderer is SkinnedMeshRenderer,
                editorOnly = false,
                mayBeEnabled = mayEnable,
                path = relPath,
            };

            // 动画缩放面积系数（沿祖先链累积）/ animated scale area factor (accumulate along ancestor chain)
            info.maxAnimScaleFactor = ComputeScaleFactor(avatarRoot.transform, go.transform, result.animation);

            // 材质槽：当前材质 + 动画引用的材质 / slots: current materials + animation-referenced materials
            var baseMaterials = renderer.sharedMaterials;
            result.animation.slotMaterialsByPath.TryGetValue(relPath, out var animMats);
            for (int i = 0; i < baseMaterials.Length; i++)
            {
                var slot = new List<Material>();
                if (baseMaterials[i] != null) slot.Add(baseMaterials[i]);
                if (animMats != null)
                {
                    foreach (var m in animMats)
                        if (m != null && !slot.Contains(m)) slot.Add(m);
                }
                if (slot.Count == 0) slot.Add(null);
                info.slots.Add(slot);
            }

            // 渲染器白名单 → 其全部材质与贴图白名单 / whitelisted renderer → its materials & textures whitelisted
            var rendererWhitelisted = result.whitelistedObjects.Contains(go) || result.whitelistedMeshes.Contains(mesh);
            foreach (var slot in info.slots)
            {
                foreach (var m in slot)
                {
                    if (m == null) continue;
                    if (rendererWhitelisted) result.whitelistedMaterials.Add(m);
                }
            }

            if (rendererWhitelisted)
                ATOLog.Verbose($"Renderer {relPath} whitelisted");

            return info;
        }

        /// <summary>分析全部材质并注册贴图。/ Analyze all materials and register textures.</summary>
        private static void AnalyzeMaterials(ATOAvatarScanResult result)
        {
            // 每渲染器路径的动画属性与贴图切换 / animated props & texture swaps per renderer path
            foreach (var renderer in result.renderers)
            {
                result.animation.floatPropsByPath.TryGetValue(renderer.path, out var floatProps);
                var whitelistedRenderer = result.whitelistedObjects.Contains(renderer.renderer.gameObject);

                foreach (var slot in renderer.slots)
                {
                    foreach (var material in slot)
                    {
                        if (material == null) continue;
                        var usages = ATOMaterialAnalyzer.Analyze(material, null);

                        // 动画属性标记（场景路径绑定 → 槽内所有具备该属性的材质）/ animated prop flags (scene-path → all slot materials having the prop)
                        if (floatProps != null)
                        {
                            foreach (var fp in floatProps)
                            {
                                if (fp.EndsWith("_ST", StringComparison.Ordinal))
                                {
                                    var texProp = fp.Substring(0, fp.Length - 3);
                                    foreach (var u in usages)
                                    {
                                        if (u.propertyName == texProp)
                                        {
                                            u.animatedST = true;
                                            u.whitelisted = true;
                                            u.whitelistReason = $"ST of {texProp} is animated";
                                        }
                                    }
                                }
                                else if (IsCutoffProp(fp))
                                {
                                    // Cutoff 被动画修改：采样当前值与中间档位（IoU 在中间阈值最严苛）取最严/
                                    // animated cutoff: sample the current value plus mid-range thresholds (IoU is strictest at mid-range), strictest wins
                                    foreach (var u in usages)
                                    {
                                        u.animatedCutoff = true;
                                        var current = u.cutoffSamples != null && u.cutoffSamples.Length > 0 ? u.cutoffSamples[0] : 0.5f;
                                        var samples = new List<float> { current, 0.25f, 0.5f, 0.75f };
                                        u.cutoffSamples = samples.ToArray();
                                    }
                                }
                                else if (fp.StartsWith("_", StringComparison.Ordinal))
                                {
                                    // 可能是关键字动画（渲染模式可能被修改）→ 最严苛评估 / possibly keyword animation → strictest evaluation
                                    foreach (var u in usages)
                                    {
                                        u.alphaUsage |= ATOAlphaUsage.Cutout | ATOAlphaUsage.Blend;
                                    }
                                }
                            }
                        }

                        // 动画贴图切换 / animated texture swaps
                        foreach (var kv in result.animation.animatedTexturesByPath)
                        {
                            if (kv.Key.path != renderer.path) continue;
                            foreach (var u in usages)
                            {
                                if (u.propertyName == kv.Key.prop)
                                {
                                    foreach (var t in kv.Value)
                                    {
                                        var nu = u.Clone();
                                        nu.texture = t;
                                        usages.Add(nu);
                                    }
                                }
                            }
                        }

                        var matWhitelisted = result.whitelistedMaterials.Contains(material);
                        foreach (var u in usages)
                        {
                            if (whitelistedRenderer || matWhitelisted || result.whitelistedTextures.Contains(u.texture))
                            {
                                u.whitelisted = true;
                                u.whitelistReason = whitelistedRenderer ? "Renderer whitelisted"
                                    : matWhitelisted ? "Material whitelisted" : "Texture whitelisted";
                            }

                            // 不可读且未启用自动 RW → 白名单 / unreadable without auto-RW → whitelist
                            if (!u.whitelisted && !u.texture.isReadable)
                            {
                                u.whitelisted = true;
                                u.whitelistReason = "Texture is not readable (enable Read/Write or turn on auto RW)";
                                if (result.scanWarnings.Count < 50)
                                    result.scanWarnings.Add($"{u.texture.name}: not readable, skipped");
                            }

                            if (u.whitelisted) result.whitelistedTextures.Add(u.texture);

                            // 贴图注册表 / texture registry
                            if (!result.textures.TryGetValue(u.texture, out var texInfo))
                            {
                                texInfo = new ATOTextureInfo
                                {
                                    texture = u.texture,
                                    width = u.texture.width,
                                    height = u.texture.height,
                                    isSRGB = GetIsSRGB(u.texture),
                                    filterMode = u.texture.filterMode,
                                };
                                result.textures[u.texture] = texInfo;
                            }
                            texInfo.usages.Add(u);
                            u.isSRGB = texInfo.isSRGB;
                            renderer.usages.Add(u);
                        }
                    }
                }
            }
        }

        /// <summary>白名单扩散：白名单材质引用的贴图、白名单对象子树内渲染器的材质与贴图。/ Whitelist propagation.</summary>
        internal static void PropagateWhitelist(ATOAvatarScanResult result)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var renderer in result.renderers)
                {
                    var go = renderer.renderer.gameObject;
                    if (result.whitelistedObjects.Contains(go))
                    {
                        foreach (var slot in renderer.slots)
                            foreach (var m in slot)
                            {
                                if (m != null && result.whitelistedMaterials.Add(m)) changed = true;
                            }
                    }
                }
                foreach (var texInfo in result.textures.Values)
                {
                    if (texInfo.whitelisted) continue;
                    foreach (var u in texInfo.usages)
                    {
                        if (result.whitelistedMaterials.Contains(u.material) || result.whitelistedTextures.Contains(u.texture))
                        {
                            texInfo.whitelisted = true;
                            texInfo.whitelistReason = "Referenced by whitelisted object";
                            result.whitelistedTextures.Add(u.texture);
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }

        // ---------- 工具函数 / utilities ----------

        /// <summary>对象是否为 EditorOnly。/ Whether an object is EditorOnly.</summary>
        public static bool IsEditorOnly(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
                if (t.CompareTag("EditorOnly")) return true;
            return false;
        }

        /// <summary>相对路径（从 Avatar 根）。/ Relative path from the avatar root.</summary>
        public static string GetRelativePath(Transform root, Transform target)
        {
            var sb = new StringBuilder();
            var t = target;
            while (t != null && t != root)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.Length > 0 ? sb.ToString() : target.name;
        }

        /// <summary>后缀匹配（处理嵌套 Animator 的相对路径差异）。/ Suffix matching (handles nested-animator path differences).</summary>
        public static bool MatchesAnySuffix(HashSet<string> paths, string relPath)
        {
            if (paths == null || paths.Count == 0) return false;
            foreach (var p in paths)
            {
                if (p == relPath) return true;
                if (relPath.EndsWith("/" + p, StringComparison.Ordinal)) return true;
                if (p.EndsWith("/" + relPath, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>沿祖先链累积动画缩放面积系数。/ Accumulate animated scale area factor along the ancestor chain.</summary>
        private static float ComputeScaleFactor(Transform root, Transform target, ATOAnimationData anim)
        {
            float factor = 1f;
            var t = target;
            while (t != null && t != root)
            {
                var path = GetRelativePath(root, t);
                if (anim.maxScaleFactorByPath.TryGetValue(path, out var f))
                    factor *= Mathf.Max(1f, f);
                t = t.parent;
            }
            return factor;
        }

        /// <summary>是否为 Cutoff 属性。/ Whether a property is a cutoff property.</summary>
        private static bool IsCutoffProp(string prop)
        {
            return prop == "_Cutoff" || prop == "_AlphaCutoff" || prop == "_CutoffAlpha" || prop == "_SubpassCutoff";
        }

        /// <summary>读取贴图 sRGB 设置。/ Read the texture's sRGB setting.</summary>
        public static bool GetIsSRGB(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return true;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer == null || importer.sRGBTexture;
        }

        /// <summary>
        /// 贴图导入设置快照（用于去重与报告）。/ Texture import settings snapshot (for dedup & reporting).
        /// </summary>
        public static string GetImportSettingsSnapshot(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path))
                return $"mem:{texture.filterMode}:{texture.wrapMode}:{texture.anisoLevel}";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return "unknown";
            var fmt = importer.GetPlatformTextureSettings("Standalone");
            return $"{importer.sRGBTexture}|{importer.filterMode}|{importer.wrapMode}|{importer.mipmapEnabled}|{importer.streamingMipmaps}|{importer.textureType}|{importer.maxTextureSize}|{importer.textureCompression}|{fmt.format}|{importer.crunchedCompression}";
        }
    }
}

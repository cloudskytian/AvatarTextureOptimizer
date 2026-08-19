// Analysis Pass - Analyzes materials, animations, shaders to build UV-Texture mapping
// 分析Pass - 分析材质、动画、着色器以建立UV-贴图映射关系

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer.Editor.Analysis;
using net.fosa.avatar_texture_optimizer.Editor.Compatibility;
using net.fosa.avatar_texture_optimizer.Runtime;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.avatar_texture_optimizer.Editor.Core.Passes
{
    /// <summary>
    /// Analyzes the avatar to build a complete UV-to-texture mapping, considering
    /// animations, material swaps, shader properties, blend shapes, etc.
    /// 分析Avatar以构建完整的UV到贴图映射关系，考虑动画、材质切换、着色器属性、形态键等。
    /// </summary>
    public class AnalysisPass : Pass<AnalysisPass>
    {
        public override string DisplayName => "ATO: Analysis / 分析";

        protected override void Execute(BuildContext context)
        {
            var sw = Stopwatch.StartNew();
            var atoCtx = context.GetState<ATOBuildContext>();
            if (!atoCtx.IsValid) return;

            var component = atoCtx.Component;
            var root = context.AvatarRootObject;

            ATOLog.Info("Starting analysis phase...");
            ATOLog.Info("开始分析阶段...");

            // Step 1: Build whitelist set
            // 步骤1：构建白名单集合
            var whitelistSw = Stopwatch.StartNew();
            BuildWhitelist(atoCtx, component, root);
            whitelistSw.Stop();
            atoCtx.StageTimings["Analysis:Whitelist"] = whitelistSw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Whitelist built: {atoCtx.WhitelistObjects.Count} objects, {whitelistSw.ElapsedMilliseconds}ms");

            // Step 2: Analyze all renderers and material slots
            // 步骤2：分析所有渲染器和材质槽
            var rendererSw = Stopwatch.StartNew();
            AnalyzeRenderers(atoCtx, root);
            rendererSw.Stop();
            atoCtx.StageTimings["Analysis:Renderers"] = rendererSw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Renderers analyzed: {atoCtx.Renderers.Count}, {rendererSw.ElapsedMilliseconds}ms");

            // Step 3: Analyze shaders for each unique material
            // 步骤3：分析每个唯一材质的着色器
            var shaderSw = Stopwatch.StartNew();
            AnalyzeShaders(atoCtx);
            shaderSw.Stop();
            atoCtx.StageTimings["Analysis:Shaders"] = shaderSw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Shaders analyzed: {atoCtx.ShaderAnalysisResults.Count} materials, {shaderSw.ElapsedMilliseconds}ms");

            // Step 4: Analyze animations for material/texture changes
            // 步骤4：分析动画中的材质/贴图变化
            var animSw = Stopwatch.StartNew();
            AnalyzeAnimations(atoCtx, root);
            animSw.Stop();
            atoCtx.StageTimings["Analysis:Animations"] = animSw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Animations analyzed, {animSw.ElapsedMilliseconds}ms");

            // Step 5: Build UV-to-Texture mapping
            // 步骤5：建立UV到贴图的映射
            var mappingSw = Stopwatch.StartNew();
            BuildUVTextureMapping(atoCtx);
            mappingSw.Stop();
            atoCtx.StageTimings["Analysis:UVMapping"] = mappingSw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"UV-Texture mapping built: {atoCtx.UVTextureMap.Count} UV keys, {mappingSw.ElapsedMilliseconds}ms");

            // Step 6: Collect all unique textures
            // 步骤6：收集所有唯一贴图
            var texSw = Stopwatch.StartNew();
            CollectAllTextures(atoCtx);
            texSw.Stop();
            atoCtx.StageTimings["Analysis:Textures"] = texSw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Textures collected: {atoCtx.AllTextures.Count}, {texSw.ElapsedMilliseconds}ms");

            sw.Stop();
            atoCtx.StageTimings["Analysis"] = sw.Elapsed.TotalMilliseconds;
            ATOLog.Info($"Analysis complete: {sw.ElapsedMilliseconds}ms");
        }

        private void BuildWhitelist(ATOBuildContext atoCtx, AvatarTextureOptimizerComponent component, GameObject root)
        {
            if (component.whitelist == null) return;

            foreach (var obj in component.whitelist)
            {
                if (obj == null) continue;
                atoCtx.WhitelistObjects.Add(obj);

                // If it's a texture, add its instance ID
                if (obj is Texture2D tex)
                {
                    atoCtx.WhitelistedTextureIds.Add(tex.GetInstanceID());
                }
                // If it's a mesh, whitelist all textures on its materials
                else if (obj is Mesh mesh)
                {
                    // Will be handled during renderer analysis
                }
                // If it's a material, whitelist all textures on it
                else if (obj is Material mat)
                {
                    WhitelistAllTexturesInMaterial(atoCtx, mat);
                }
                // If it's a GameObject, recursively whitelist all renderers' textures
                else if (obj is GameObject go)
                {
                    var renderers = go.GetComponentsInChildren<Renderer>(true);
                    foreach (var r in renderers)
                    {
                        if (r.sharedMaterials != null)
                        {
                            foreach (var m in r.sharedMaterials)
                            {
                                if (m != null) WhitelistAllTexturesInMaterial(atoCtx, m);
                            }
                        }
                    }
                }
                // If it's an animation controller/clip, we whitelist textures referenced in it
                else if (obj is AnimationClip clip)
                {
                    // Handled during animation analysis
                }
            }
        }

        private void WhitelistAllTexturesInMaterial(ATOBuildContext atoCtx, Material mat)
        {
            var shader = mat.shader;
            if (shader == null) return;

            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++)
            {
                if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                {
                    var propName = shader.GetPropertyName(i);
                    var tex = mat.GetTexture(propName) as Texture2D;
                    if (tex != null)
                    {
                        atoCtx.WhitelistedTextureIds.Add(tex.GetInstanceID());
                    }
                }
            }
        }

        private void AnalyzeRenderers(ATOBuildContext atoCtx, GameObject root)
        {
            // Find all SkinnedMeshRenderer and MeshRenderer
            // 查找所有SkinnedMeshRenderer和MeshRenderer
            var skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);

            foreach (var smr in skinnedRenderers)
            {
                // Skip EditorOnly tagged objects
                if (smr.gameObject.CompareTag("EditorOnly")) continue;

                if (smr.sharedMesh == null) continue;

                var info = new RendererInfo
                {
                    Renderer = smr,
                    SharedMesh = smr.sharedMesh,
                    SharedMaterials = smr.sharedMaterials,
                    IsActive = smr.gameObject.activeInHierarchy && smr.enabled
                };

                // Check if the renderer is in whitelist
                if (atoCtx.WhitelistObjects.Contains(smr) || atoCtx.WhitelistObjects.Contains(smr.gameObject))
                {
                    // Whitelist all textures on this renderer
                    foreach (var mat in info.SharedMaterials)
                    {
                        if (mat != null) WhitelistAllTexturesInMaterial(atoCtx, mat);
                    }
                }

                atoCtx.Renderers.Add(info);
            }

            foreach (var mr in meshRenderers)
            {
                if (mr.gameObject.CompareTag("EditorOnly")) continue;

                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var info = new RendererInfo
                {
                    Renderer = mr,
                    SharedMesh = mf.sharedMesh,
                    SharedMaterials = mr.sharedMaterials,
                    IsActive = mr.gameObject.activeInHierarchy && mr.enabled
                };

                if (atoCtx.WhitelistObjects.Contains(mr) || atoCtx.WhitelistObjects.Contains(mr.gameObject))
                {
                    foreach (var mat in info.SharedMaterials)
                    {
                        if (mat != null) WhitelistAllTexturesInMaterial(atoCtx, mat);
                    }
                }

                atoCtx.Renderers.Add(info);
            }
        }

        private void AnalyzeShaders(ATOBuildContext atoCtx)
        {
            var analyzedMaterials = new HashSet<Material>();

            foreach (var rendererInfo in atoCtx.Renderers)
            {
                if (rendererInfo.SharedMaterials == null) continue;

                foreach (var mat in rendererInfo.SharedMaterials)
                {
                    if (mat == null || analyzedMaterials.Contains(mat)) continue;
                    analyzedMaterials.Add(mat);

                    var result = ShaderAnalyzer.Analyze(mat, atoCtx);
                    atoCtx.ShaderAnalysisResults[mat] = result;

                    if (!result.IsCompatible)
                    {
                        ATOLog.Warning($"Shader '{result.ShaderName}' on material '{mat.name}' is not compatible: {result.IncompatibilityReason}. Treating as whitelist.");
                        ATOLog.Warning($"材质'{mat.name}'上的着色器'{result.ShaderName}'不兼容：{result.IncompatibilityReason}。视为白名单处理。");
                        WhitelistAllTexturesInMaterial(atoCtx, mat);
                    }
                }
            }
        }

        private void AnalyzeAnimations(ATOBuildContext atoCtx, GameObject root)
        {
            atoCtx.AnimationAnalysis = AnimationAnalyzer.Analyze(root, atoCtx);

            // Add any new textures found in animations to whitelist if needed
            foreach (var texChange in atoCtx.AnimationAnalysis.TextureChanges)
            {
                foreach (var tex in texChange.PossibleTextures)
                {
                    if (tex != null && atoCtx.WhitelistedTextureIds.Contains(tex.GetInstanceID()))
                    {
                        // If the original texture is whitelisted, the replacement should be too
                        ATOLog.Info($"Animation texture '{tex.name}' whitelisted (original is whitelisted).");
                    }
                }
            }
        }

        private void BuildUVTextureMapping(ATOBuildContext atoCtx)
        {
            foreach (var rendererInfo in atoCtx.Renderers)
            {
                if (rendererInfo.SharedMaterials == null) continue;

                for (int slotIdx = 0; slotIdx < rendererInfo.SharedMaterials.Length; slotIdx++)
                {
                    var mat = rendererInfo.SharedMaterials[slotIdx];
                    if (mat == null) continue;

                    if (!atoCtx.ShaderAnalysisResults.TryGetValue(mat, out var shaderResult))
                        continue;

                    if (!shaderResult.IsCompatible) continue;

                    var uvKey = new UVKey
                    {
                        MeshInstanceId = rendererInfo.Renderer.GetInstanceID(),
                        UvChannel = 0
                    };

                    if (!atoCtx.UVTextureMap.TryGetValue(uvKey, out var mapping))
                    {
                        mapping = new UVTextureMapping();
                        atoCtx.UVTextureMap[uvKey] = mapping;
                    }

                    // Add material reference
                    mapping.MaterialReferences.Add(new MaterialReference
                    {
                        Renderer = rendererInfo.Renderer,
                        MaterialSlotIndex = slotIdx,
                        Material = mat
                    });

                    // Add texture usages from shader analysis
                    foreach (var texProp in shaderResult.TextureProperties)
                    {
                        if (texProp.HasSTTransform || texProp.IsDecalOrSpecial)
                        {
                            // Has ST transform or special purpose → treat as whitelist
                            var tex = mat.GetTexture(texProp.PropertyName) as Texture2D;
                            if (tex != null)
                            {
                                atoCtx.WhitelistedTextureIds.Add(tex.GetInstanceID());
                                ATOLog.Warning($"Texture '{tex.name}' on '{mat.name}.{texProp.PropertyName}' has ST transform or special usage. Whitelisted.");
                            }
                            continue;
                        }

                        var texture = mat.GetTexture(texProp.PropertyName) as Texture2D;
                        if (texture == null) continue;

                        // Check whitelist
                        bool isWhitelisted = atoCtx.WhitelistedTextureIds.Contains(texture.GetInstanceID());

                        // Determine transparency mode
                        var transMode = ShaderAnalyzer.GetTransparencyMode(mat, shaderResult);
                        float cutoff = 0.5f;
                        if (mat.HasProperty("_Cutoff"))
                            cutoff = mat.GetFloat("_Cutoff");

                        var usage = new TextureUsage
                        {
                            Texture = texture,
                            ShaderPropertyName = texProp.PropertyName,
                            Role = texProp.Role,
                            SourceMaterial = mat,
                            TransparencyMode = transMode,
                            Cutoff = cutoff
                        };

                        // Check animation render mode changes for worst case
                        if (atoCtx.AnimationAnalysis != null)
                        {
                            foreach (var rmChange in atoCtx.AnimationAnalysis.RenderModeChanges)
                            {
                                if (rmChange.Material == mat)
                                {
                                    // Take the most strict transparency mode
                                    foreach (var mode in rmChange.PossibleModes)
                                    {
                                        if (IsStricterTransparency(mode, transMode))
                                        {
                                            usage.TransparencyMode = mode;
                                        }
                                    }
                                    foreach (var c in rmChange.PossibleCutoffs)
                                    {
                                        // Take the cutoff that requires highest quality
                                        usage.Cutoff = Mathf.Max(usage.Cutoff, c);
                                    }
                                }
                            }
                        }

                        mapping.TextureUsages.Add(usage);
                    }
                }
            }

            // Add animation-discovered textures
            if (atoCtx.AnimationAnalysis != null)
            {
                foreach (var texChange in atoCtx.AnimationAnalysis.TextureChanges)
                {
                    foreach (var tex in texChange.PossibleTextures)
                    {
                        if (tex == null) continue;

                        // Find the UV key for the material's renderer
                        foreach (var kvp in atoCtx.UVTextureMap)
                        {
                            foreach (var matRef in kvp.Value.MaterialReferences)
                            {
                                if (matRef.Material == texChange.Material)
                                {
                                    // Check if already exists
                                    bool exists = kvp.Value.TextureUsages.Any(u =>
                                        u.Texture == tex && u.ShaderPropertyName == texChange.PropertyName);

                                    if (!exists)
                                    {
                                        kvp.Value.TextureUsages.Add(new TextureUsage
                                        {
                                            Texture = tex,
                                            ShaderPropertyName = texChange.PropertyName,
                                            Role = DetermineTextureRole(texChange.PropertyName),
                                            SourceMaterial = texChange.Material,
                                            FromAnimation = true
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private bool IsStricterTransparency(TransparencyMode a, TransparencyMode b)
        {
            // Cutout is stricter than opaque (requires alpha quality evaluation)
            // Blend is stricter than cutout (requires continuous alpha evaluation)
            int StrictnessLevel(TransparencyMode m)
            {
                switch (m)
                {
                    case TransparencyMode.Opaque: return 0;
                    case TransparencyMode.Cutout: return 1;
                    case TransparencyMode.Blend: return 2;
                    case TransparencyMode.Premultiply: return 3;
                    case TransparencyMode.Additive: return 3;
                    default: return 0;
                }
            }
            return StrictnessLevel(a) > StrictnessLevel(b);
        }

        private TextureRole DetermineTextureRole(string propertyName)
        {
            if (propertyName.Contains("MainTex") || propertyName.Contains("_Color"))
                return TextureRole.MainColor;
            if (propertyName.Contains("Bump") || propertyName.Contains("Normal"))
                return TextureRole.NormalMap;
            if (propertyName.Contains("Mask"))
                return TextureRole.Mask;
            if (propertyName.Contains("Emission"))
                return TextureRole.Emission;
            if (propertyName.Contains("Occlusion"))
                return TextureRole.Occlusion;
            if (propertyName.Contains("Metallic"))
                return TextureRole.Metallic;
            if (propertyName.Contains("Roughness") || propertyName.Contains("Smoothness") || propertyName.Contains("Gloss"))
                return TextureRole.Roughness;
            if (propertyName.Contains("AlphaMask"))
                return TextureRole.AlphaMask;
            if (propertyName.Contains("Detail"))
                return TextureRole.Detail;
            return TextureRole.Other;
        }

        private void CollectAllTextures(ATOBuildContext atoCtx)
        {
            var textureSet = new Dictionary<int, TextureInfo>();

            foreach (var kvp in atoCtx.UVTextureMap)
            {
                foreach (var usage in kvp.Value.TextureUsages)
                {
                    if (usage.Texture == null) continue;
                    int id = usage.Texture.GetInstanceID();

                    if (textureSet.ContainsKey(id)) continue;

                    bool isWhitelisted = atoCtx.WhitelistedTextureIds.Contains(id);

                    var texInfo = new TextureInfo
                    {
                        Texture = usage.Texture,
                        OriginalTexture = usage.Texture,
                        Width = usage.Texture.width,
                        Height = usage.Texture.height,
                        IsWhitelisted = isWhitelisted,
                        PrimaryRole = usage.Role,
                        HasAlpha = TextureHelper.HasAlphaChannel(usage.Texture),
                        IsNormalMap = usage.Role == TextureRole.NormalMap,
                        IsGrayscale = usage.Role == TextureRole.Mask || usage.Role == TextureRole.Occlusion
                                      || usage.Role == TextureRole.Metallic || usage.Role == TextureRole.Roughness,
                        IsLinear = usage.Role == TextureRole.NormalMap || usage.Role == TextureRole.Mask,
                        WrapMode = usage.Texture.wrapMode,
                        FilterMode = usage.Texture.filterMode
                    };

                    textureSet[id] = texInfo;
                }
            }

            atoCtx.AllTextures = textureSet.Values.ToList();
        }
    }
}

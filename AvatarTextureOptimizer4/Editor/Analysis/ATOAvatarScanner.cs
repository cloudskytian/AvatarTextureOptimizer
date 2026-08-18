// Avatar Texture Optimizer (ATO)
// Scans the avatar for renderers, material slots, and referenced textures.
// 扫描 Avatar 的渲染器、材质槽与引用的贴图。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 0: enumerate SMR/MR, their material slots, and the textures each material samples.
    /// 阶段 0：枚举 SMR/MR、其材质槽以及每个材质采样的贴图。
    /// </summary>
    public static class ATOAvatarScanner
    {
        public static void Scan(ATOBuildContext build, ATOProgress progress)
        {
            var renderers = new List<Renderer>();
            build.avatarRoot.GetComponentsInChildren(true, renderers);

            var rendererList = new List<Renderer>();
            foreach (var r in renderers)
            {
                if (r is not (SkinnedMeshRenderer or MeshRenderer)) continue;
                if (r.gameObject.CompareTag("EditorOnly")) continue; // skip EditorOnly / 跳过 EditorOnly
                if (r is MeshRenderer mr && mr.GetComponent<MeshFilter>() == null) continue;
                rendererList.Add(r);
            }

            progress.Begin(rendererList.Count);

            int rendererId = 0;
            int slotCount = 0;
            foreach (var r in rendererList)
            {
                var isSkinned = r is SkinnedMeshRenderer;
                var mesh = isSkinned ? ((SkinnedMeshRenderer)r).sharedMesh : r.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null) { progress.Advance(1, $"{r.name}: no mesh"); continue; }

                // Clone materials so we never mutate the user's shared assets, and persist
                // clones immediately so animation curve paths can be remapped.
                // 克隆材质以免修改用户共享资产，并立即持久化克隆以便动画曲线路径可重映射。
                var slots = CloneMaterials(build, r.sharedMaterials);
                r.sharedMaterials = slots;

                var rr = new ATORendererRef
                {
                    rendererId = rendererId++,
                    renderer = r,
                    isSkinned = isSkinned,
                    path = ATOUtil.GetRelativePath(build.avatarRoot.transform, r.transform),
                    sourceMesh = mesh,
                    workingMesh = mesh,
                    slots = slots,
                    enabled = r.enabled,
                    animatedEnabled = false,
                };
                build.renderers.Add(rr);
                build.report.rendererCount++;

                for (int slot = 0; slot < rr.slots.Length; slot++)
                {
                    var mat = rr.slots[slot];
                    slotCount++;
                    if (mat == null) continue;
                    CollectMaterialTextures(build, rr, slot, mat, fromAnimation: false);
                }

                progress.Advance(1, r.name);
            }

            build.report.materialSlotCount = slotCount;
            build.report.textureCountBeforeDedup = build.textures.Count;
            ATOLogger.Info($"Scanned {build.renderers.Count} renderers, {slotCount} material slots, {build.textures.Count} texture refs.");
        }

        /// <summary>
        /// Clone materials (per original), persist the clones, and record path remaps.
        /// 克隆材质（按原资产）、持久化克隆并记录路径重映射。
        /// </summary>
        public static Material[] CloneMaterials(ATOBuildContext build, Material[] materials)
        {
            var result = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                var m = materials[i];
                if (m == null) continue;
                if (build.baseMaterialClone.TryGetValue(m, out var existing))
                {
                    result[i] = existing;
                    continue;
                }
                var clone = new Material(m) { name = m.name + "_ato" };
                build.baseMaterialClone[m] = clone;
                try
                {
                    build.ndmf.AssetSaver.SaveAsset(clone);
                    var origPath = UnityEditor.AssetDatabase.GetAssetPath(m);
                    var clonePath = UnityEditor.AssetDatabase.GetAssetPath(clone);
                    if (!string.IsNullOrEmpty(origPath) && !string.IsNullOrEmpty(clonePath))
                        build.materialPathRemap[origPath] = clonePath;
                }
                catch (System.Exception e)
                {
                    ATOLogger.Warn($"Failed to persist material clone '{clone.name}': {e.Message}");
                }
                result[i] = clone;
            }
            return result;
        }

        /// <summary>
        /// Collect every atlasable Texture2D reference of a material into the build state.
        /// 把材质中每个可图集化的 Texture2D 引用收集进构建状态。
        /// </summary>
        public static void CollectMaterialTextures(ATOBuildContext build, ATORendererRef rr, int slotIndex,
            Material mat, bool fromAnimation)
        {
            if (mat == null || mat.shader == null) return;
            var props = ATOShaderPropertyAnalyzer.Analyze(mat);

            foreach (var kvp in props)
            {
                var propName = kvp.Key;
                var category = kvp.Value;
                if (category == ATOTextureCategory.Other) continue; // non-atlasable by classification / 分类为不可图集化

                if (!mat.HasProperty(propName)) continue;
                var tex = mat.GetTexture(propName) as Texture2D;
                if (tex == null) continue;

                // ST-transform check happens in eligibility stage. / ST 变换检查在资格阶段进行。

                var usage = new ATOTextureUsage
                {
                    material = mat,
                    propertyName = propName,
                    category = category,
                    uvChannel = ResolveUvChannel(propName, mat),
                    alphaMode = ResolveAlphaMode(mat),
                    cutoff = ResolveCutoff(mat),
                    fromAnimation = fromAnimation,
                    renderer = rr,
                };

                var texRef = FindOrCreateTexRef(build, tex);
                texRef.usages.Add(usage);
                rr.usedUvChannels.Add(usage.uvChannel);
            }
        }

        /// <summary>
        /// Resolve the UV channel a property samples. Heuristic: primary props -> UV0,
        /// "2nd"/"3rd" props -> UV1/UV2. Some shaders expose explicit channel selectors.
        /// 解析属性采样的 UV 通道。启发式：主属性 -> UV0，"2nd"/"3rd" -> UV1/UV2。部分着色器暴露显式通道选择。
        /// </summary>
        private static int ResolveUvChannel(string propName, Material mat)
        {
            var n = propName.ToLowerInvariant();
            // Explicit selector, if the shader provides one (e.g. lilToon UV channel props). / 显式选择器（若着色器提供）。
            foreach (var suffix in new[] { "Uv", "UV", "UvSet", "UvChannel", "UvIndex" })
            {
                if (mat.HasProperty(propName + suffix))
                {
                    return Mathf.Clamp(Mathf.RoundToInt(mat.GetFloat(propName + suffix)), 0, ATOConstants.MaxUvChannels - 1);
                }
            }
            if (n.Contains("2nd") || n.Contains("second")) return 1;
            if (n.Contains("3rd") || n.Contains("third")) return 2;
            return 0;
        }

        /// <summary>
        /// Resolve the transparency mode from render queue and shader keywords.
        /// 从渲染队列与着色器关键字解析透明模式。
        /// </summary>
        public static ATOAlphaMode ResolveAlphaMode(Material mat)
        {
            if (mat.HasProperty("_Mode"))
            {
                int mode = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (mode == 1) return ATOAlphaMode.Cutout;      // Standard cutout
                if (mode >= 2) return ATOAlphaMode.Blend;        // Standard fade/transparent
            }
            var shader = mat.shader;
            if (shader != null)
            {
                var sname = shader.name.ToLowerInvariant();
                // lilToon cutout variants / lilToon cutout 变体
                if (sname.Contains("cutout") || mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHATEST"))
                    return ATOAlphaMode.Cutout;
                if (sname.Contains("trans") || mat.IsKeywordEnabled("_ALPHABLEND_ON")
                    || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") || mat.IsKeywordEnabled("_ALPHAMODULATE_ON"))
                    return ATOAlphaMode.Blend;
            }
            switch (mat.renderQueue)
            {
                case (int)UnityEngine.Rendering.RenderQueue.AlphaTest: return ATOAlphaMode.Cutout;
                case (int)UnityEngine.Rendering.RenderQueue.Transparent: return ATOAlphaMode.Blend;
                case (int)UnityEngine.Rendering.RenderQueue.Overlay: return ATOAlphaMode.Blend;
                default: return ATOAlphaMode.Opaque;
            }
        }

        private static float ResolveCutoff(Material mat)
        {
            if (mat.HasProperty("_Cutoff")) return mat.GetFloat("_Cutoff");
            if (mat.HasProperty("_AlphaClipThreshold")) return mat.GetFloat("_AlphaClipThreshold");
            return 0.5f;
        }

        private static ATOTextureRef FindOrCreateTexRef(ATOBuildContext build, Texture2D tex)
        {
            foreach (var t in build.textures)
                if (t.texture == tex) return t;

            var tr = new ATOTextureRef
            {
                texture = tex,
                sourceAsset = tex,
                assetPath = UnityEditor.AssetDatabase.GetAssetPath(tex),
                width = tex.width,
                height = tex.height,
                isSRGB = IsSrgb(tex),
                filterMode = tex.filterMode,
                wrapMode = tex.wrapMode,
                importFingerprint = ATOUtil.ImportFingerprint(tex),
                hasAlpha = DetectAlpha(tex),
            };
            build.textures.Add(tr);
            return tr;
        }

        private static bool IsSrgb(Texture2D t)
        {
            var path = UnityEditor.AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return true; // runtime textures assumed sRGB color / 运行时贴图默认当作 sRGB 颜色
            var imp = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
            return imp == null || imp.sRGBTexture;
        }

        private static bool DetectAlpha(Texture2D t)
        {
            try
            {
                var px = t.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                    if (px[i].a < 255) return true;
            }
            catch (UnityException)
            {
                // not readable; assume alpha present unless format says otherwise / 不可读；按格式判断
                return HasAlphaFormat(t.format);
            }
            return false;
        }

        private static bool HasAlphaFormat(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.RGBA32:
                case TextureFormat.ARGB32:
                case TextureFormat.BGRA32:
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                    return true;
                default:
                    return false;
            }
        }
    }
}

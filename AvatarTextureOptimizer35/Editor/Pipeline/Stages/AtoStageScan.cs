using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: scan the avatar — renderers, material slots, texture slots, UV groups, type groups,
    /// whitelist resolution, output folder, TTT detection. / 阶段：扫描 —— 渲染器、材质槽、贴图槽、
    /// UV 组、类型组、白名单解析、输出目录、TTT 检测。
    /// </summary>
    internal sealed class AtoStageScan : IAtoStage
    {
        public string I18nKey => "scan";

        public void Run(AtoContext ctx)
        {
            var state = ctx.State;
            var settings = state.Settings;

            AtoSlotFactory.Reset();

            // ---- output folder ----
            var containerPath = AssetDatabase.GetAssetPath(ctx.Ndmf.AssetContainer);
            if (string.IsNullOrEmpty(containerPath))
            {
                ctx.Error("ATO: cannot resolve the asset container path; using 'Assets/ATO' as fallback.");
                containerPath = "Assets/ATO";
                if (!AssetDatabase.IsValidFolder(containerPath)) AssetDatabase.CreateFolder("Assets", "ATO");
            }
            ctx.OutputFolder = containerPath + "/ATO";
            if (!AssetDatabase.IsValidFolder(ctx.OutputFolder))
            {
                AssetDatabase.CreateFolder(containerPath, "ATO");
            }

            // ---- whitelist resolution ----
            WhitelistResolver.Resolve(ctx, settings.whitelist);

            // ---- TTT detection ----
            ctx.TttDetected = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a =>
                {
                    var name = a.GetName().Name;
                    return name != null &&
                           (name.Contains("tex-trans-tool") || name.StartsWith("net.rs64"));
                });
            if (ctx.TttDetected)
            {
                ctx.Warn(state.Tr("warn.tttDetected"));
            }

            // ---- density validation ----
            if ((int)settings.minPixelDensity > (int)settings.maxPixelDensity)
            {
                ctx.Warn($"ATO: minPixelDensity ({settings.minPixelDensity}) > maxPixelDensity ({settings.maxPixelDensity}); swapping them.");
                (settings.minPixelDensity, settings.maxPixelDensity) = (settings.maxPixelDensity, settings.minPixelDensity);
            }

            // ---- collect renderers ----
            var allRenderers = ctx.AvatarRoot.GetComponentsInChildren<Renderer>(true);
            var rendererCount = 0;
            foreach (var renderer in allRenderers)
            {
                state.SetProgress($"renderer {rendererCount + 1}/{allRenderers.Length}: {renderer.name}",
                    (float)rendererCount / Mathf.Max(1, allRenderers.Length));

                if (renderer is not (SkinnedMeshRenderer or MeshRenderer)) continue;
                if (renderer.gameObject.CompareTag("EditorOnly")) continue; // defensive; NDMF already removed them. / 防御性；NDMF 已移除。
                if (ctx.WhitelistObjects.Contains(renderer.gameObject)) continue;

                var data = new AtoRendererData
                {
                    Renderer = renderer,
                    Mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh :
                        renderer.GetComponent<MeshFilter>()?.sharedMesh,
                    EffectivelyEnabled = renderer.enabled,
                };

                if (data.Mesh == null)
                {
                    AtoLog.Verbose($"[ATO] skipping {renderer.name}: no mesh.");
                    continue;
                }
                if (!data.Mesh.isReadable)
                {
                    // We cannot read UVs/triangles — treat as whitelist. / 无法读取 UV/三角形——视作白名单。
                    ctx.Warn($"[ATO] {renderer.name}: mesh '{data.Mesh.name}' is not readable; renderer treated as whitelist.");
                    continue;
                }

                // ---- material slots ----
                var sharedMaterials = renderer.sharedMaterials;
                for (var slot = 0; slot < sharedMaterials.Length; slot++)
                {
                    var material = sharedMaterials[slot];
                    var materialSlot = new AtoMaterialSlot
                    {
                        Index = slot,
                        RendererData = data,
                        Initial = material,
                    };
                    if (material != null) materialSlot.AnimatedOptions.Add(material);
                    data.Slots.Add(materialSlot);
                    if (material != null) AtoSlotFactory.RegisterMaterialTextures(ctx, data, slot, material);
                }

                if (data.Slots.Count == 0) continue;

                ctx.Renderers.Add(data);
                rendererCount++;
            }

            // ---- build type groups ----
            BuildTypeGroups(ctx);

            AtoLog.Info($"[ATO] scan: {ctx.Renderers.Count} renderer(s), {ctx.UvGroups.Count} UV group(s), " +
                        $"{ctx.Textures.Count} texture(s), {ctx.TypeGroups.Count} type group(s).");
            state.TextureCount = ctx.Textures.Count;
            state.UvGroupCount = ctx.UvGroups.Count;
        }

        /// <summary>
        /// Build type groups: key = (kind signature of the whole UV group × sRGB × filterMode). /
        /// 构建类型组：键 = （整个 UV 组的类型签名 × sRGB × filterMode）。
        /// Shared with the animations stage (new slots may join later). / 与动画阶段共享（后续新槽可能加入）。
        /// </summary>
        public static void BuildTypeGroups(AtoContext ctx)
        {
            ctx.TypeGroups.Clear();
            foreach (var uvGroup in ctx.UvGroups) uvGroup.TypeGroups.Clear();

            var groupByKey = new Dictionary<AtoTypeGroupKey, AtoTypeGroup>();

            foreach (var uvGroup in ctx.UvGroups)
            {
                // Kind signature = sorted distinct kinds of ALL slots in the UV group. / 类型签名 = UV 组全部槽的类型去重排序。
                var kinds = uvGroup.Slots.Select(s => s.Usage.Kind).Distinct().OrderBy(k => (int)k).ToArray();
                var signature = string.Join("|", kinds.Select(k => k.ToString()));

                foreach (var slot in uvGroup.Slots)
                {
                    var key = new AtoTypeGroupKey(signature, slot.Usage.Srgb, slot.Texture.filterMode);
                    if (!groupByKey.TryGetValue(key, out var group))
                    {
                        group = new AtoTypeGroup(key);
                        groupByKey[key] = group;
                        ctx.TypeGroups.Add(group);
                    }
                    group.Slots.Add(slot);
                    group.UvGroups.Add(uvGroup);
                    uvGroup.TypeGroups.Add(group);

                    if (slot.Usage.Kind == AtoTextureKind.Tangent) group.ContainsTangentData = true;
                    if (slot.Usage.HasBlend || slot.Usage.CutoutThresholds.Count > 0) group.HasAlpha = true;
                }
            }
        }
    }
}

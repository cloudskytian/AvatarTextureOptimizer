using System.Collections.Generic;
using Fosa.Ato.Editor.Analysis;
using Fosa.Ato.Editor.i18n;
using Fosa.Ato.Editor.Util;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 02: For every material slot of every renderer, find eligible textures and build the
    /// per-slot list of TextureUsages. A texture is ineligible (whitelist-equivalent) when its shader
    /// cannot be classified OR it uses ST tiling/offset OR the renderer/material/texture is whitelisted.
    /// Multi-UV channels are treated as independent UV sets.
    /// 阶段 02：遍历每个渲染器的每个材质槽，找出合格贴图并建立每槽 TextureUsage 列表。着色器无法分类、
    /// 使用 ST、或在白名单内的贴图视为不合格（等同白名单）。多通道 UV 视作独立 UV。
    /// </summary>
    internal sealed class Stage02MaterialMapping : IStage
    {
        public string Name => "ATO/02 Mapping materials to textures";
        public float Weight => 3f;

        public void Run(AtoPipeline p)
        {
            var renderers = p.GetState<CollectState>().Renderers;
            float i = 0;
            foreach (var r in renderers)
            {
                p.Progress.ThrowIfCancelled();
                p.Progress.Stage(Name, i++ / Mathf.Max(1, renderers.Count));

                using (var so = new MaterialArray(r.sharedMaterials))
                {
                    for (int slot = 0; slot < so.Materials.Length; slot++)
                    {
                        var mat = so.Materials[slot];
                        if (mat == null || mat.shader == null) continue;
                        if (p.Whitelist.Contains(mat)) { MarkSlotWhitelisted(p, r, slot, so, "material in whitelist"); continue; }

                        if (!ShaderPropertyAnalyzer.TryGetProperties(mat.shader, out var props))
                        {
                            AtoLog.Warn(Localizer.T("warn.unknownShader", mat.shader.name, mat.name));
                            p.Report.SkippedCount++;
                            MarkSlotWhitelisted(p, r, slot, so, "unknown shader");
                            continue;
                        }

                        var list = new List<TextureUsage>();
                        foreach (var prop in props)
                        {
                            if (mat.GetTexture(prop.Name) is not Texture2D tex || tex == null) continue;
                            if (p.Whitelist.Contains(tex)) { list.Add(MkUsage(p, tex, prop, mat, whitelisted: true)); continue; }

                            // ST transform (animated ST is checked in stage 03) / ST 变换（动画在阶段03再查）
                            if (ShaderPropertyAnalyzer.HasStTransform(mat, prop))
                            {
                                AtoLog.Warn(Localizer.T("warn.transform", tex.name, mat.name));
                                p.Report.SkippedCount++;
                                list.Add(MkUsage(p, tex, prop, mat, whitelisted: true));
                                continue;
                            }

                            int channel = ShaderPropertyAnalyzer.GetUvChannel(mat, prop);
                            var usage = MkUsage(p, tex, prop, mat, whitelisted: false);
                            usage.AtlasAllowed = true;
                            (list).Add(usage);
                            // Store UV channel alongside usage via the UvChannel bucket / 将通道存入桶
                            p.GetState<ChannelState>().Record(tex, prop.Name, channel);
                        }
                        p.SlotTextures[new MaterialSlotRef(r, slot)] = list;
                    }
                }
            }
            p.Report.TextureCount = p.Usages.Count;
        }

        private static TextureUsage MkUsage(AtoPipeline p, Texture2D tex,
            ShaderPropertyAnalyzer.PropertyInfo prop, Material mat, bool whitelisted = false)
        {
            var alphaMode = MaterialTransparency.Detect(mat);
            var u = new TextureUsage
            {
                Texture = tex,
                ImportHash = TextureIO.ImportHash(tex),
                Kind = prop.Kind,
                SRGB = prop.Kind == TextureKind.Color || prop.Kind == TextureKind.Emission,
                Filter = tex.filterMode,
                ShaderPropertyName = prop.Name,
                HasAlphaChannel = TextureUtil.HasAlpha(tex),
                Whitelisted = whitelisted,
                Alpha = alphaMode,
                Cutoff = MaterialTransparency.Cutoff(mat),
            };
            if (p.Usages.TryGetValue(tex, out var existing))
            {
                // If referenced both with and without a special map, take the stricter group.
                // 若同时以有无特殊贴图引用，归到更严格（有特殊贴图）的组
                if (prop.Kind != TextureKind.Color && existing.Kind == TextureKind.Color)
                    existing.Kind = prop.Kind;
                if (u.SRGB) existing.SRGB = true;
                // Strictest transparency mode + highest cutoff across referencing materials.
                // 跨引用材质取最严格透明模式与最高 cutoff
                existing.Alpha = MaterialTransparency.Strictest(existing.Alpha, alphaMode);
                existing.Cutoff = Mathf.Max(existing.Cutoff, u.Cutoff);
                if (existing.HasAlphaChannel || u.HasAlphaChannel) existing.HasAlphaChannel = true;
                return existing;
            }
            p.Usages[tex] = u;
            return u;
        }

        private static void MarkSlotWhitelisted(AtoPipeline p, Renderer r, int slot, MaterialArray so, string why)
        {
            var list = new List<TextureUsage>();
            var mat = so.Materials[slot];
            if (mat == null || mat.shader == null) { p.SlotTextures[new MaterialSlotRef(r, slot)] = list; return; }
            if (ShaderPropertyAnalyzer.TryGetProperties(mat.shader, out var props))
            {
                foreach (var prop in props)
                {
                    if (mat.GetTexture(prop.Name) is Texture2D tex && tex != null)
                        list.Add(MkUsage(p, tex, prop, mat, whitelisted: true));
                }
            }
            p.SlotTextures[new MaterialSlotRef(r, slot)] = list;
            AtoLog.VIf(p.Settings.VerboseLogging, $"Slot [{r.name}/{slot}] whitelisted: {why}");
        }

        private ref struct MaterialArray
        {
            public readonly Material[] Materials;
            public MaterialArray(Material[] m) => Materials = m;
            public void Dispose() { }
        }
    }

    internal sealed class ChannelState
    {
        // Map (texture, property) -> UV channel. / （贴图，属性）-> UV 通道
        public readonly Dictionary<(Texture2D, string), int> ChannelOf = new();
        public void Record(Texture2D t, string prop, int ch) => ChannelOf[(t, prop)] = ch;
        public int Get(Texture2D t, string prop) => ChannelOf.TryGetValue((t, prop), out var c) ? c : 0;
    }
}

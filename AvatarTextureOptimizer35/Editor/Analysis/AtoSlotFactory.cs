using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Shared factory for texture slots / records / UV groups, used by the scan stage AND the
    /// animations stage (materials & textures swapped in by animations need the same treatment). /
    /// 贴图槽/记录/UV 组的共享工厂，供扫描阶段与动画阶段共用（动画切换进来的材质与贴图需要同样处理）。
    /// </summary>
    internal static class AtoSlotFactory
    {
        /// <summary>Analysis cache shared across the build. / 整个构建共享的分析缓存。</summary>
        private static readonly Dictionary<(Material, string), (AtoTextureUsage usage, string unsafeReason)> AnalysisCache =
            new Dictionary<(Material, string), (AtoTextureUsage, string)>();

        /// <summary>Reset the analysis cache (new build). / 重置分析缓存（新构建）。</summary>
        public static void Reset() => AnalysisCache.Clear();

        /// <summary>
        /// Get (or create) the texture record. / 获取（或创建）贴图记录。
        /// </summary>
        public static AtoTextureRecord GetOrCreateRecord(AtoContext ctx, Texture2D texture)
        {
            if (ctx.Textures.TryGetValue(texture, out var record)) return record;
            record = new AtoTextureRecord { Texture = texture };
            ctx.Textures[texture] = record;
            if (ctx.WhitelistedTextures.TryGetValue(texture, out var reason))
            {
                record.Whitelisted = true;
                record.WhitelistReason = reason;
            }
            return record;
        }

        /// <summary>
        /// Analyze (cached) a (material, property) texture usage. / 分析（带缓存）一个（材质, 属性）的贴图用法。
        /// </summary>
        public static (AtoTextureUsage usage, string unsafeReason) Analyze(AtoContext ctx, Material material,
            string propertyName, Texture2D texture)
        {
            if (AnalysisCache.TryGetValue((material, propertyName), out var analyzed)) return analyzed;
            var usage = ShaderAnalyzer.Analyze(material, propertyName, texture, out var unsafeReason);
            analyzed = (usage, unsafeReason);
            AnalysisCache[(material, propertyName)] = analyzed;
            return analyzed;
        }

        /// <summary>
        /// Create (or reuse) a texture slot for (renderer, slotIndex, material, property, texture)
        /// and register it into the UV group. Whitelists unsafe usages. / 为（渲染器, 槽, 材质, 属性, 贴图）
        /// 创建（或复用）贴图槽并登记到 UV 组；不安全用法白名单化。
        /// </summary>
        public static AtoTextureSlot GetOrCreateSlot(AtoContext ctx, AtoRendererData data, int slotIndex,
            Material material, string propertyName, Texture2D texture)
        {
            var analyzed = Analyze(ctx, material, propertyName, texture);
            if (analyzed.usage == null)
            {
                ctx.WhitelistTexture(texture, $"{material.name}.{propertyName}: {analyzed.unsafeReason}");
                return null;
            }

            var record = GetOrCreateRecord(ctx, texture);

            // Reuse an existing slot for the same (renderer slot, material, property). / 复用同一（渲染器槽, 材质, 属性）的既有槽。
            foreach (var existing in record.Slots)
            {
                if (existing.Material == material && existing.PropertyName == propertyName &&
                    existing.AssignedSlots.Contains((data.Renderer, slotIndex)))
                {
                    return existing;
                }
            }

            var slot = new AtoTextureSlot
            {
                Material = material,
                PropertyName = propertyName,
                Texture = texture,
                Usage = analyzed.usage,
            };
            slot.AssignedSlots.Add((data.Renderer, slotIndex));
            record.Slots.Add(slot);

            var channel = slot.Usage.UvChannel;
            if (!data.UvGroups.TryGetValue(channel, out var uvGroup))
            {
                uvGroup = new AtoUvGroup
                {
                    Renderer = data.Renderer,
                    Mesh = data.Mesh,
                    Channel = channel,
                };
                data.UvGroups[channel] = uvGroup;
                ctx.UvGroups.Add(uvGroup);
            }
            uvGroup.Slots.Add(slot);

            return slot;
        }

        /// <summary>
        /// Register all texture slots of a material on a renderer slot (used for materials swapped
        /// in by animations). / 为渲染器槽注册材质的全部贴图槽（用于动画切换进来的材质）。
        /// </summary>
        public static void RegisterMaterialTextures(AtoContext ctx, AtoRendererData data, int slotIndex, Material material)
        {
            if (material == null) return;
            var shader = material.shader;
            if (shader == null) return;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var propertyName = shader.GetPropertyName(i);
                if (material.GetTexture(propertyName) is Texture2D texture)
                {
                    GetOrCreateSlot(ctx, data, slotIndex, material, propertyName, texture);
                }
            }
        }
    }
}

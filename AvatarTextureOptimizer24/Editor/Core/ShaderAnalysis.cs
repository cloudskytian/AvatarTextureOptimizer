// ============================================================================
// ShaderAnalysis.cs — 着色器/材质贴图属性分析 / Shader & material texture analysis
// (EN) Analyzes a material to discover which textures it references, classify
//      their usage (main color / normal / mask / gray), the UV channel they
//      sample, and whether any ST transform (scale/offset/scroll/rotate) applies.
//      Keyword-driven so it adapts to future lilToon and standard shaders.
// (ZH) 分析材质引用了哪些贴图，分类其用途（主色/法线/蒙版/灰度）、采样 UV 通道、
//      以及是否存在 ST 变换（缩放/平移/滚动/旋转）。基于关键字驱动，兼容未来
//      lilToon 与标准着色器版本。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOShaderAnalysis
    {
        /// <summary>(EN) Extension point: register a custom material analyzer for third-party shaders. (ZH) 扩展点：为第三方着色器注册自定义材质分析器。</summary>
        public interface IMaterialAnalyzer
        {
            /// <summary>(EN) Whether this analyzer handles the given material. (ZH) 该分析器是否处理此材质。</summary>
            bool Handles(Material material);
            /// <summary>(EN) Produce slot-texture entries. (ZH) 产出材质槽贴图条目。</summary>
            void Analyze(Material material, List<ATOSlotTexture> results);
        }

        private static readonly List<IMaterialAnalyzer> _analyzers = new List<IMaterialAnalyzer>();

        public static void RegisterAnalyzer(IMaterialAnalyzer analyzer) => _analyzers.Add(analyzer);

        /// <summary>(EN) Analyze a material's texture references. (ZH) 分析材质的贴图引用。</summary>
        public static List<ATOSlotTexture> AnalyzeMaterial(Material material)
        {
            var results = new List<ATOSlotTexture>();

            // 第三方自定义分析器优先 / third-party analyzers first
            foreach (var a in _analyzers)
            {
                if (a.Handles(material))
                {
                    a.Analyze(material, results);
                    return results;
                }
            }

            if (material == null || material.shader == null)
                return results;

            // 标准关键字分析 / standard keyword analysis
            var names = material.GetTexturePropertyNames();
            foreach (var name in names)
            {
                var tex = material.GetTexture(name) as Texture2D;
                if (tex == null) continue; // 非 2D 贴图（cubemap 等）跳过，交由白名单逻辑 / skip non-2D

                var usage = ClassifyUsage(name, tex);
                if (usage == ATOTextureUsage.Other)
                    continue; // 无法分类 → 上层按白名单处理 / unclassifiable → whitelist upstream

                var entry = new ATOSlotTexture
                {
                    Ref = new ATOTextureRef { Texture = tex, Usage = usage },
                    PropertyName = name,
                    UvChannel = GuessUvChannel(name),
                    HasTransform = HasAnyTransform(material, name),
                    SpecialPurpose = IsSpecialPurpose(name),
                };
                results.Add(entry);
            }

            return results;
        }

        // ---------------------------------------------------------------------
        // 用途分类 / usage classification (keyword-driven)
        // ---------------------------------------------------------------------
        private static ATOTextureUsage ClassifyUsage(string name, Texture2D tex)
        {
            var n = name.ToLowerInvariant();

            // 法线贴图：属性带 [Normal] 标记 或 名称含 bump/normal 或 导入类型为 NormalMap
            // Normal: [Normal] attribute, or name contains bump/normal, or import type NormalMap
            if (HasShaderPropertyFlag(name, tex, "Normal") || n.Contains("bump") || n.Contains("normal"))
                return ATOTextureUsage.NormalMap;

            // 蒙版 / mask
            if (n.Contains("mask"))
                return ATOTextureUsage.Mask;

            // 主色 / main color (albedo/base/diffuse/emission)
            if (n == "_maintex" || n == "_basemap" || n.Contains("maintex") || n.Contains("basemap")
                || n.Contains("albedo") || n.Contains("diffuse") || n.Contains("emission"))
                return ATOTextureUsage.MainColor;

            // 灰度 / grayscale (single channel import or data-map keywords)
            if (IsSingleChannel(tex) || n.Contains("metallic") || n.Contains("roughness")
                || n.Contains("occlusion") || n.Contains("smoothness") || n.Contains("ao") || n.Contains("_gloss"))
                return ATOTextureUsage.Grayscale;

            return ATOTextureUsage.Other;
        }

        // ---------------------------------------------------------------------
        // UV 通道猜测 / UV channel heuristic
        // lilToon: _MainTex→UV0, _Main2ndTex→UV1, _Main3rdTex→UV2
        // ---------------------------------------------------------------------
        private static int GuessUvChannel(string name)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("3rd")) return 2;
            if (n.Contains("2nd")) return 1;
            return 0;
        }

        // ---------------------------------------------------------------------
        // ST 变换检测 / ST transform detection
        // 覆盖：标准 _ST 缩放/平移 + lilToon 的 _Xxx_ScrollRotate (scroll/angle/rotate)
        // ---------------------------------------------------------------------
        private static bool HasAnyTransform(Material material, string propName)
        {
            // 标准 ST 缩放与平移 / standard ST scale & offset
            var scale = material.GetTextureScale(propName);
            var offset = material.GetTextureOffset(propName);
            if (scale != Vector2.one || offset != Vector2.zero)
                return true;

            // lilToon scroll/rotate: <Prop>_ScrollRotate (float4: x,y scroll; z angle; w rotation)
            if (material.HasProperty(propName + "_ScrollRotate"))
            {
                var sr = material.GetVector(propName + "_ScrollRotate");
                if (sr != Vector4.zero) return true;
            }

            // angle 属性 / angle property
            if (material.HasProperty(propName + "Angle"))
            {
                if (material.GetFloat(propName + "Angle") != 0f) return true;
            }

            return false;
        }

        // ---------------------------------------------------------------------
        // 特殊用途（贴花等）/ special purpose (decal etc.)
        // ---------------------------------------------------------------------
        private static bool IsSpecialPurpose(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("decal") || n.Contains("matcap") || n.Contains("parallax") || n.Contains("heightmap");
        }

        // ---------------------------------------------------------------------
        // 辅助 / helpers
        // ---------------------------------------------------------------------
        private static bool HasShaderPropertyFlag(string name, Texture2D tex, string flag)
        {
            // 通过导入类型兜底 / fall back to import type
            if (IsNormalMapImport(tex)) return true;
            // ShaderUtil.GetShaderPropertyAttribute 需要 shader + property 组合，此处仅做导入类型与命名兜底
            return false;
        }

        private static bool IsNormalMapImport(Texture2D tex)
        {
            if (tex == null) return false;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer != null && importer.textureType == TextureImporterType.NormalMap;
        }

        private static bool IsSingleChannel(Texture2D tex)
        {
            if (tex == null) return false;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer != null && importer.textureType == TextureImporterType.SingleChannel;
        }
    }
}

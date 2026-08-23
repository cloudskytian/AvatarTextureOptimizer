using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>EN: Exact serialized-content material deduplication without changing shader parameters. ZH: 不修改 Shader 参数的精确序列化内容材质去重。</summary>
    internal static class MaterialDeduplicator
    {
        public static void Deduplicate(BuildContext context, BuildPlan plan, AtoBuildReport report)
        {
            if (!plan.Component.settings.deduplicateMaterials) return;
            var canonical = new Dictionary<string, Material>(StringComparer.Ordinal);
            var replacements = new Dictionary<Material, Material>();
            foreach (var material in plan.Materials.Values.Select(x => x.Working).Where(x => x != null).Distinct())
            {
                var hash = Hash(material);
                if (!canonical.TryGetValue(hash, out var first)) canonical[hash] = material;
                else replacements[material] = first;
            }
            if (replacements.Count == 0) return;

            foreach (var renderer in plan.Renderers.Select(x => x.Renderer))
            {
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                    if (materials[i] != null && replacements.TryGetValue(materials[i], out var replacement)) materials[i] = replacement;
                renderer.sharedMaterials = materials;
            }
            context.Extension<AnimatorServicesContext>().AnimationIndex.RewriteObjectCurves(obj =>
                obj is Material material && replacements.TryGetValue(material, out var replacement) ? replacement : obj);
            SerializedReferenceRewriter.Rewrite(context.AvatarRootObject, replacements);
            foreach (var pair in replacements) ObjectRegistry.RegisterReplacedObject(pair.Key, pair.Value);
            foreach (var record in plan.Materials.Values)
                if (replacements.TryGetValue(record.Working, out var replacement)) record.Working = replacement;
            report.Log($"Deduplicated {replacements.Count} optimized material(s).");
        }

        private static string Hash(Material material)
        {
            var builder = new StringBuilder();
            using (var serialized = new SerializedObject(material))
            {
                var iterator = serialized.GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyPath == "m_Name" || iterator.propertyPath == "m_ObjectHideFlags") continue;
                    builder.Append(iterator.propertyPath).Append('=').Append(Value(iterator)).Append('\n');
                }
            }
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static string Value(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "1" : "0";
                case SerializedPropertyType.Float: return property.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Color: return property.colorValue.ToString();
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue != null ? property.objectReferenceValue.GetInstanceID().ToString() : "null";
                case SerializedPropertyType.Enum: return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString("R");
                case SerializedPropertyType.Rect: return property.rectValue.ToString();
                case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return property.quaternionValue.ToString();
                case SerializedPropertyType.Hash128: return property.hash128Value.ToString();
                case SerializedPropertyType.Character: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ArraySize: return property.intValue.ToString(CultureInfo.InvariantCulture);
                default: return property.propertyType.ToString();
            }
        }
    }
}

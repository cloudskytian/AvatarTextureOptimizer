using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal static class AtlasLayoutAnalyzer
    {
        public static bool TryCreate(UvGroupRecord group, out AtlasGroupLayout layout, out string failure)
        {
            layout = new AtlasGroupLayout(); failure = null;
            var schemas = new List<List<Tuple<string, TextureTypeKey>>>();
            foreach (var material in group.Slot.Materials.Distinct())
            {
                var layers = new List<AtlasLayerBinding>();
                foreach (var property in group.Bindings.Where(value => value.Material == material)
                             .GroupBy(value => value.PropertyName).OrderBy(value => value.Key, StringComparer.Ordinal))
                {
                    var first = property.First();
                    var key = new TextureTypeKey(first.Kind, TextureFingerprint.IsSrgb(first.Texture),
                        first.Texture.filterMode, first.Texture.anisoLevel, first.Texture.mipMapBias);
                    if (property.Any(value => !key.Equals(new TextureTypeKey(value.Kind,
                            TextureFingerprint.IsSrgb(value.Texture), value.Texture.filterMode,
                            value.Texture.anisoLevel, value.Texture.mipMapBias))))
                    {
                        failure = "animated textures change type, color space, or effective sampling state"; return false;
                    }
                    var layer = new AtlasLayerBinding { PropertyName = property.Key, Key = key,
                        Initial = property.FirstOrDefault(value => value.IsInitialValue) };
                    layer.AnimatedValues.AddRange(property.Where(value => value.IsAnimatedValue));
                    layers.Add(layer);
                }
                layers = layers.OrderBy(value => value.Key.Kind).ThenBy(value => value.Key.Srgb)
                    .ThenBy(value => value.Key.FilterMode).ThenBy(value => value.PropertyName, StringComparer.Ordinal).ToList();
                layout.MaterialLayers[material] = layers;
                schemas.Add(layers.Select(value => Tuple.Create(value.PropertyName, value.Key)).ToList());
            }
            if (schemas.Count == 0 || schemas[0].Count == 0) { failure = "material has no atlas texture layers"; return false; }
            if (schemas.Any(schema => schema.Count != schemas[0].Count || !schema.SequenceEqual(schemas[0])))
            {
                failure = "animated material states have incompatible texture property or layer layouts"; return false;
            }
            layout.LayerKeys.AddRange(schemas[0].Select(value => value.Item2));
            layout.Signature = string.Join(";", layout.LayerKeys.Select(KeyString));
            return true;
        }

        private static string KeyString(TextureTypeKey key) => key.Kind + ":" + (key.Srgb ? "sRGB" : "Linear") +
            ":" + key.FilterMode + ":aniso=" + key.AnisoLevel + ":bias=" +
            key.MipMapBias.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}

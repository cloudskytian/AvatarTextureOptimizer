using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal enum AnimatedTextureResolution
    {
        Unmapped,
        Resolved,
        Ambiguous
    }

    /// <summary>
    /// Resolves an animation keyframe against its pre-fingerprint Texture2D identity. A renderer property curve is
    /// shared by every animated material state in the slot, so a destructive UV rewrite may only use one output when
    /// every matching state is mapped. / 按像素去重前的 Texture2D 身份解析动画帧；破坏性 UV 改写必须完整且唯一。
    /// </summary>
    internal static class AnimatedTextureResolver
    {
        internal static AnimatedTextureResolution Resolve(MaterialSlotRecord slot, string property,
            Texture source, IReadOnlyDictionary<TextureBindingRecord, Texture2D> replacements,
            out Texture2D replacement)
        {
            replacement = null;
            if (slot == null || string.IsNullOrEmpty(property) || source == null || replacements == null)
                return AnimatedTextureResolution.Unmapped;

            var candidates = slot.Bindings.Where(value => value != null && value.IsAnimatedValue &&
                value.PropertyName == property && value.OriginalTexture == source).ToArray();
            if (candidates.Length == 0) return AnimatedTextureResolution.Unmapped;

            var mapped = candidates.Where(replacements.ContainsKey).ToArray();
            if (mapped.Length == 0) return AnimatedTextureResolution.Unmapped;
            var outputs = mapped.Select(value => replacements[value]).Distinct().ToArray();
            if (mapped.Length != candidates.Length || outputs.Length != 1 || outputs[0] == null)
                return AnimatedTextureResolution.Ambiguous;

            replacement = outputs[0];
            return AnimatedTextureResolution.Resolved;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Rewrites animation clip material references to generated materials when safely possible.
    /// 在安全可判定时，把动画中的材质引用改写到生成后的材质。
    /// </summary>
    internal static class AtoAnimationRewriter
    {
        public static void RewriteMaterialReferences(AtoSessionState session)
        {
            if (session?.Component == null || session.MaterialRewriteMap.Count == 0)
            {
                return;
            }

            foreach (var clipRecord in session.ScanResult.AnimationClips)
            {
                var clip = clipRecord.Clip;
                if (clip == null)
                {
                    continue;
                }

                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    if (!TryParseMaterialSlot(binding.propertyName, out var slotIndex))
                    {
                        continue;
                    }

                    var path = binding.path ?? string.Empty;
                    var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    var changed = false;
                    for (var i = 0; i < keyframes.Length; i++)
                    {
                        if (keyframes[i].value is not Material original)
                        {
                            continue;
                        }

                        var mapKey = BuildKey(path, slotIndex, original);
                        if (!session.MaterialRewriteMap.TryGetValue(mapKey, out var rewritten) || rewritten == null || rewritten == original)
                        {
                            continue;
                        }

                        keyframes[i].value = rewritten;
                        changed = true;
                    }

                    if (changed)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, keyframes);
                        session.Report.AddDetail($"Animation rewrite: {clip.name} | {binding.path} | {binding.propertyName}.");
                    }
                }
            }
        }

        public static string BuildKey(string relativePath, int slotIndex, Material original)
        {
            return $"{relativePath}|slot{slotIndex}|mat{original.GetInstanceID()}";
        }

        private static bool TryParseMaterialSlot(string propertyName, out int slotIndex)
        {
            slotIndex = -1;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            const string prefix = "m_Materials.Array.data[";
            var start = propertyName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            start += prefix.Length;
            var end = propertyName.IndexOf(']', start);
            if (end < 0)
            {
                return false;
            }

            return int.TryParse(propertyName.Substring(start, end - start), out slotIndex);
        }
    }
}

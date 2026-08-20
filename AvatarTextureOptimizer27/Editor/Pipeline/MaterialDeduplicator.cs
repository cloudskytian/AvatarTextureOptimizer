using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class MaterialDeduplicator
    {
        public static void Run(GameObject root, List<Renderer> renderers, List<AnimationClip> clips, AnimationImpact anim,
            Net.Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer comp, BakeReport report)
        {
            if (!comp.optimizeMaterials && !comp.optimizeTextures) return;
            using (AtoLog.Time("Dedup materials"))
            {
                var map = new Dictionary<string, Material>();
                foreach (var r in renderers)
                {
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    var isolated = anim.IsolatedMaterialSlotSwitches;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        string key = Fingerprint(m);
                        if (map.TryGetValue(key, out var canon) && canon != m)
                        {
                            if (isolated.Contains(i)) continue;
                            mats[i] = canon;
                            changed = true;
                        }
                        else map[key] = m;
                    }
                    if (changed) r.sharedMaterials = mats;
                }
                AtoLog.Info($"Unique materials after dedup={map.Count}");
            }
        }

        static string Fingerprint(Material m)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "null");
            var so = new SerializedObject(m);
            var it = so.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.propertyType == SerializedPropertyType.ObjectReference)
                    sb.Append('|').Append(it.objectReferenceValue ? it.objectReferenceValue.GetInstanceID() : 0);
                else if (it.propertyType == SerializedPropertyType.Float)
                    sb.Append('|').Append(it.floatValue.ToString("R"));
                else if (it.propertyType == SerializedPropertyType.Color)
                    sb.Append('|').Append(it.colorValue);
                else if (it.propertyType == SerializedPropertyType.Vector4)
                    sb.Append('|').Append(it.vector4Value);
            }
            return sb.ToString();
        }
    }
}

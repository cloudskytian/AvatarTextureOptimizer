using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Dedup identical materials / textures after bake. Merge opaque slots when safe.
    /// 烘焙后对相同材质/贴图去重；安全时合并不透明材质槽并重映射动画索引。
    /// </summary>
    public static class AtoPostDedup
    {
        public static void Apply(GameObject root, AtoPlatformOverride settings, AtoReport report)
        {
            var matMap = new Dictionary<Material, Material>();
            if (settings.deduplicateMaterials)
            {
                var canon = new Dictionary<string, Material>();
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        var key = MaterialKey(m);
                        if (canon.TryGetValue(key, out var c) && c != m)
                        {
                            matMap[m] = c;
                            mats[i] = c;
                            changed = true;
                        }
                        else canon[key] = m;
                    }
                    if (changed) r.sharedMaterials = mats;
                }
            }

            var slotMaps = new Dictionary<Renderer, int[]>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!settings.deduplicateMaterials) continue;
                if (AtoAnimationRemapper.HasPerSlotMaterialSwitch(root, r))
                {
                    report.Detail($"skip slot merge (per-slot anim) {r.name}");
                    continue;
                }
                if (TryMergeOpaqueSlots(r, report, out var map))
                    slotMaps[r] = map;
            }

            if (matMap.Count > 0)
                AtoAnimationRemapper.RemapTexturesAndMaterials(root, null, matMap, report);
            if (slotMaps.Count > 0)
                AtoAnimationRemapper.RemapMaterialSlots(root, slotMaps, report);
        }

        static bool TryMergeOpaqueSlots(Renderer r, AtoReport report, out int[] oldToNew)
        {
            var mats = r.sharedMaterials;
            oldToNew = new int[mats.Length];
            if (mats.Length < 2) return false;

            var keep = new List<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                int found = -1;
                if (m != null && IsOpaque(m))
                {
                    for (int k = 0; k < keep.Count; k++)
                        if (keep[k] == m) { found = k; break; }
                }
                if (found >= 0) oldToNew[i] = found;
                else
                {
                    oldToNew[i] = keep.Count;
                    keep.Add(m);
                }
            }
            if (keep.Count == mats.Length) return false;
            r.sharedMaterials = keep.ToArray();
            // Also collapse mesh submeshes that became unused? Unsafe without remapping triangles.
            // 不合并 submesh 三角形，仅缩短材质槽数组；多余 submesh 仍指向最后槽可能出错。
            // Safer: only merge if consecutive trailing duplicates or we keep mesh subMeshCount.
            // If mesh subMeshCount > keep.Count, pad materials back to subMeshCount using last.
            var mesh = AtoAvatarScanner.GetMesh(r);
            if (mesh != null && mesh.subMeshCount > keep.Count)
            {
                while (keep.Count < mesh.subMeshCount)
                    keep.Add(keep[keep.Count - 1]);
                r.sharedMaterials = keep.ToArray();
                report.Detail($"merged opaque refs on {r.name} but kept {keep.Count} slots for submeshes");
                return true;
            }
            report.Detail($"merged opaque slots on {r.name} {mats.Length}->{keep.Count}");
            return true;
        }

        static bool IsOpaque(Material m)
        {
            var a = AtoShaderAnalyzer.Analyze(m);
            return a.Blend == AtoBlendMode.Opaque;
        }

        public static string MaterialKey(Material m)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "");
            if (m.shader == null) return sb.ToString();
            int n = m.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                var name = m.shader.GetPropertyName(i);
                sb.Append('|').Append(name).Append('=');
                switch (m.shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        sb.Append(t != null ? t.GetInstanceID() : 0);
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        sb.Append(m.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        sb.Append(m.GetFloat(name).ToString("G9"));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        sb.Append(m.GetVector(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        sb.Append(m.GetInt(name));
                        break;
                }
            }
            foreach (var k in m.shaderKeywords.OrderBy(x => x)) sb.Append('#').Append(k);
            return sb.ToString();
        }
    }
}

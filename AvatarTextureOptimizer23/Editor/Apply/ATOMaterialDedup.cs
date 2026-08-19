using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Deduplicates identical materials / textures after optimization.
    /// Opaque materials on the same mesh may merge slots when animation never switches them independently.
    /// 优化后对完全相同的材质/贴图去重。
    /// 同一网格上的不透明材质，若动画从不单独切换其中某一个，则可合并材质槽。
    /// </summary>
    internal static class ATOMaterialDedup
    {
        public static void Run(ATOContext ctx)
        {
            if (ctx.Settings.enableTextureDedup)
                DedupTextures(ctx);
            if (ctx.Settings.enableMaterialDedup)
                DedupMaterials(ctx);
        }

        private static void DedupTextures(ATOContext ctx)
        {
            var groups = new Dictionary<string, Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in ctx.TextureRemap)
            {
                var tex = kv.Value;
                if (tex == null) continue;
                var key = Fingerprint(ctx, tex);
                if (groups.TryGetValue(key, out var survivor) && survivor != tex)
                    remap[tex] = survivor;
                else
                    groups[key] = tex;
            }
            if (remap.Count == 0) return;
            foreach (var kv in new List<KeyValuePair<Texture2D, Texture2D>>(ctx.TextureRemap))
            {
                if (remap.TryGetValue(kv.Value, out var s))
                    ctx.TextureRemap[kv.Key] = s;
            }
            foreach (var ri in ctx.Renderers)
            {
                foreach (var mat in ri.Renderer.sharedMaterials)
                    ReplaceTexturesOnMaterial(mat, remap);
            }
            ctx.Log.Info($"Post-atlas texture dedup: {remap.Count}");
        }

        private static void ReplaceTexturesOnMaterial(Material mat, Dictionary<Texture2D, Texture2D> remap)
        {
            if (mat == null || mat.shader == null) return;
            var n = mat.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (mat.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var prop = mat.shader.GetPropertyName(i);
                var t = mat.GetTexture(prop) as Texture2D;
                if (t != null && remap.TryGetValue(t, out var s))
                    mat.SetTexture(prop, s);
            }
        }

        private static void DedupMaterials(ATOContext ctx)
        {
            var groups = new Dictionary<string, Material>();
            var remap = new Dictionary<Material, Material>();
            foreach (var ri in ctx.Renderers)
            {
                foreach (var mat in ri.Renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    var key = MaterialFingerprint(mat);
                    if (groups.TryGetValue(key, out var survivor) && survivor != mat)
                        remap[mat] = survivor;
                    else
                        groups[key] = mat;
                }
            }

            if (remap.Count == 0) return;

            var independentlySwitched = FindIndependentlySwitchedSlots(ctx);

            foreach (var ri in ctx.Renderers)
            {
                var mats = ri.Renderer.sharedMaterials;
                var changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && remap.TryGetValue(mats[i], out var s))
                    {
                        mats[i] = s;
                        changed = true;
                    }
                }
                if (changed) ri.Renderer.sharedMaterials = mats;

                // Merge consecutive equal opaque slots if animation never switches one of them alone.
                // 若动画从不单独切换，则合并连续且相同的不透明槽。
                TryMergeOpaqueSlots(ctx, ri, independentlySwitched);
            }

            RemapAnimMaterials(ctx, remap);
            ctx.Log.Info($"Material dedup remapped {remap.Count}");
        }

        private static HashSet<string> FindIndependentlySwitchedSlots(ATOContext ctx)
        {
            var set = new HashSet<string>();
            var clips = new HashSet<AnimationClip>();
            foreach (var ac in ATOAnimationAnalyzer.CollectControllers(ctx))
                ATOAnimationAnalyzer.CollectClips(ac, clips);
            foreach (var clip in clips)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (b.propertyName.IndexOf("m_Materials", System.StringComparison.Ordinal) >= 0)
                        set.Add(b.path + "|" + b.propertyName);
                }
            }
            return set;
        }

        private static void TryMergeOpaqueSlots(ATOContext ctx, ATORendererInfo ri, HashSet<string> switched)
        {
            var mats = ri.Renderer.sharedMaterials;
            if (mats.Length < 2 || ri.Mesh == null) return;
            var path = AnimationUtility.CalculateTransformPath(ri.Renderer.transform, ctx.Build.AvatarRootTransform);

            var map = new int[mats.Length];
            var compact = new List<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                var key = path + $"|m_Materials.Array.data[{i}]";
                if (switched.Contains(key))
                {
                    map[i] = compact.Count;
                    compact.Add(mats[i]);
                    continue;
                }
                var merged = false;
                if (mats[i] != null && IsOpaque(mats[i]))
                {
                    for (int j = 0; j < compact.Count; j++)
                    {
                        if (compact[j] == mats[i])
                        {
                            map[i] = j;
                            merged = true;
                            break;
                        }
                    }
                }
                if (!merged)
                {
                    map[i] = compact.Count;
                    compact.Add(mats[i]);
                }
            }
            if (compact.Count == mats.Length) return;

            // Rebuild submeshes. / 重建子网格。
            var mesh = ri.Mesh;
            var combined = new List<int>[compact.Count];
            for (int i = 0; i < compact.Count; i++) combined[i] = new List<int>();
            for (int sm = 0; sm < mesh.subMeshCount && sm < map.Length; sm++)
            {
                combined[map[sm]].AddRange(mesh.GetTriangles(sm));
            }
            mesh.subMeshCount = compact.Count;
            for (int i = 0; i < compact.Count; i++)
                mesh.SetTriangles(combined[i], i);
            ri.Renderer.sharedMaterials = compact.ToArray();
            ctx.Log.Detail($"Merged opaque slots on '{ri.Renderer.name}' {mats.Length} → {compact.Count}");

            RemapAnimSlotIndices(ctx, path, map);
        }

        private static void RemapAnimSlotIndices(ATOContext ctx, string path, int[] map)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var ac in ATOAnimationAnalyzer.CollectControllers(ctx))
                ATOAnimationAnalyzer.CollectClips(ac, clips);
            foreach (var clip0 in clips)
            {
                var clip = ATOApply.EnsureTempClip(ctx, clip0);
                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var b in bindings)
                {
                    if (b.path != path) continue;
                    var n = b.propertyName;
                    var open = n.LastIndexOf('[');
                    var close = n.LastIndexOf(']');
                    if (open < 0 || close <= open) continue;
                    if (!int.TryParse(n.Substring(open + 1, close - open - 1), out var idx)) continue;
                    if (idx < 0 || idx >= map.Length) continue;
                    if (map[idx] == idx) continue;
                    var nb = b;
                    nb.propertyName = n.Substring(0, open + 1) + map[idx] + "]";
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    AnimationUtility.SetObjectReferenceCurve(clip, b, System.Array.Empty<ObjectReferenceKeyframe>());
                    AnimationUtility.SetObjectReferenceCurve(clip, nb, keys);
                }
            }
        }

        private static void RemapAnimMaterials(ATOContext ctx, Dictionary<Material, Material> remap)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var ac in ATOAnimationAnalyzer.CollectControllers(ctx))
                ATOAnimationAnalyzer.CollectClips(ac, clips);
            foreach (var clip0 in clips)
            {
                var clip = ATOApply.EnsureTempClip(ctx, clip0);
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys == null) continue;
                    var ch = false;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (keys[i].value is Material m && remap.TryGetValue(m, out var s))
                        {
                            keys[i].value = s;
                            ch = true;
                        }
                    }
                    if (ch) AnimationUtility.SetObjectReferenceCurve(clip, b, keys);
                }
            }
        }

        private static bool IsOpaque(Material m)
        {
            return ATOGenericShaderAnalyzer.GuessAlphaMode(m) == ATOAlphaMode.Opaque;
        }

        private static string Fingerprint(ATOContext ctx, Texture2D tex)
        {
            var dec = ATOTextureUtil.Decode(ctx, tex);
            return $"{ATOTextureUtil.PixelHash(dec.Pixels):X16}|{tex.width}x{tex.height}|{tex.format}|{tex.filterMode}|{tex.wrapMode}";
        }

        private static string MaterialFingerprint(Material mat)
        {
            var sb = new StringBuilder();
            sb.Append(mat.shader != null ? mat.shader.name : "?");
            if (mat.shader == null) return sb.ToString();
            var n = mat.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                var name = mat.shader.GetPropertyName(i);
                var type = mat.shader.GetPropertyType(i);
                sb.Append('|').Append(name).Append('=');
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var t = mat.GetTexture(name);
                        sb.Append(t != null ? t.GetInstanceID() : 0);
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        sb.Append(mat.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        sb.Append(mat.GetVector(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        sb.Append(mat.GetFloat(name).ToString("G9"));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        sb.Append(mat.GetInt(name));
                        break;
                }
            }
            foreach (var kw in mat.shaderKeywords)
                sb.Append('#').Append(kw);
            return sb.ToString();
        }
    }
}

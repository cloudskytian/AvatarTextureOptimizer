// English: Rewrite texture references only. Dedup materials / textures. Merge opaque slots when safe.
// 中文：只改贴图引用。材质/贴图去重。安全时合并不透明材质槽。
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOMaterialApply
    {
        public static void ApplyTextures(ATOState state)
        {
            var cloned = new Dictionary<Material, Material>();
            foreach (var info in state.Renderers)
            {
                if (info.Materials == null) continue;
                var mats = (Material[])info.Materials.Clone();
                var changed = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;
                    Material clone;
                    if (!cloned.TryGetValue(mat, out clone))
                    {
                        clone = RewriteMaterial(state, mat);
                        cloned[mat] = clone;
                    }

                    if (clone != mat)
                    {
                        mats[i] = clone;
                        changed = true;
                    }
                }

                if (!changed) continue;
                info.Renderer.sharedMaterials = mats;
                info.Materials = mats;
            }

            // Animation object curves: rewrite materials and textures.
            if (state.Anim != null)
            {
                state.Anim.AnimationIndex.RewriteObjectCurves((obj) =>
                {
                    var mat = obj as Material;
                    if (mat != null)
                    {
                        Material clone;
                        if (cloned.TryGetValue(mat, out clone)) return clone;
                        clone = RewriteMaterial(state, mat);
                        cloned[mat] = clone;
                        return clone;
                    }

                    var tex = obj as Texture2D;
                    if (tex != null)
                    {
                        Texture2D repl;
                        if (state.TextureReplace.TryGetValue(tex, out repl) && repl != null) return repl;
                    }

                    return obj;
                });
            }

            foreach (var kv in cloned)
            {
                if (kv.Key != kv.Value) state.MaterialReplace[kv.Key] = kv.Value;
            }

            state.Log.Info("materials rewritten=" + cloned.Count);
        }

        private static Material RewriteMaterial(ATOState state, Material src)
        {
            if (src == null || src.shader == null) return src;
            Material dst = null;
            var count = ShaderUtil.GetPropertyCount(src.shader);
            for (var i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(src.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var prop = ShaderUtil.GetPropertyName(src.shader, i);
                var tex = src.GetTexture(prop) as Texture2D;
                if (tex == null) continue;
                Texture2D repl;
                if (!state.TextureReplace.TryGetValue(tex, out repl) || repl == null || repl == tex) continue;
                if (dst == null)
                {
                    dst = Object.Instantiate(src);
                    dst.name = src.name + "_ATO";
                    dst.shaderKeywords = src.shaderKeywords;
                    state.Generated.Add(dst);
                }

                dst.SetTexture(prop, repl);
            }

            return dst != null ? dst : src;
        }

        public static void DedupAssets(ATOState state)
        {
            if (state.Component.deduplicateTextures) DedupTextures(state);
            if (state.Component.deduplicateMaterials) DedupMaterials(state);
        }

        private static void DedupTextures(ATOState state)
        {
            var groups = new Dictionary<string, Texture2D>();
            var map = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in state.TextureReplace)
            {
                var t = kv.Value;
                if (t == null) continue;
                var key = t.width + "x" + t.height + "|" + t.format + "|" + t.filterMode + "|" + t.wrapMode + "|" +
                          t.mipmapCount + "|" + ContentHash(state, t);
                Texture2D keep;
                if (!groups.TryGetValue(key, out keep)) groups[key] = t;
                else if (keep != t) map[t] = keep;
            }

            if (map.Count == 0) return;
            foreach (var info in state.Renderers)
            {
                if (info.Materials == null) continue;
                foreach (var mat in info.Materials)
                {
                    if (mat == null || mat.shader == null) continue;
                    var count = ShaderUtil.GetPropertyCount(mat.shader);
                    for (var i = 0; i < count; i++)
                    {
                        if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                        var prop = ShaderUtil.GetPropertyName(mat.shader, i);
                        var tex = mat.GetTexture(prop) as Texture2D;
                        Texture2D repl;
                        if (tex != null && map.TryGetValue(tex, out repl)) mat.SetTexture(prop, repl);
                    }
                }
            }

            if (state.Anim != null)
            {
                state.Anim.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    var tex = obj as Texture2D;
                    Texture2D repl;
                    if (tex != null && map.TryGetValue(tex, out repl)) return repl;
                    return obj;
                });
            }

            state.Log.Info("post texture dedup merges=" + map.Count);
        }

        private static void DedupMaterials(ATOState state)
        {
            var groups = new Dictionary<string, Material>();
            var map = new Dictionary<Material, Material>();
            foreach (var info in state.Renderers)
            {
                if (info.Materials == null) continue;
                foreach (var mat in info.Materials)
                {
                    if (mat == null) continue;
                    var key = MaterialFingerprint(mat);
                    Material keep;
                    if (!groups.TryGetValue(key, out keep)) groups[key] = mat;
                    else if (keep != mat) map[mat] = keep;
                }
            }

            if (map.Count == 0) return;

            foreach (var info in state.Renderers)
            {
                if (info.Materials == null) continue;
                var mats = (Material[])info.Materials.Clone();
                var changed = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    Material repl;
                    if (mats[i] != null && map.TryGetValue(mats[i], out repl))
                    {
                        mats[i] = repl;
                        changed = true;
                    }
                }

                if (changed)
                {
                    TryMergeOpaqueSlots(state, info, mats);
                    info.Renderer.sharedMaterials = mats;
                    info.Materials = mats;
                }
            }

            if (state.Anim != null)
            {
                state.Anim.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    var mat = obj as Material;
                    Material repl;
                    if (mat != null && map.TryGetValue(mat, out repl)) return repl;
                    return obj;
                });
            }

            state.Log.Info("post material dedup merges=" + map.Count);
        }

        /// <summary>
        /// Merge consecutive identical opaque slots when animation never switches them independently.
        /// 当动画不会单独切换其中某个槽时，合并相同的不透明材质槽。
        /// </summary>
        private static void TryMergeOpaqueSlots(ATOState state, ATORendererInfo info, Material[] mats)
        {
            if (info.AnySlotAnimatedIndependently) return;
            if (info.Mesh == null || mats == null || mats.Length < 2) return;
            var mapOldToNew = new int[mats.Length];
            var compact = new List<Material>();
            for (var i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                var merged = -1;
                if (mat != null && IsOpaque(mat))
                {
                    for (var j = 0; j < compact.Count; j++)
                    {
                        if (compact[j] == mat)
                        {
                            merged = j;
                            break;
                        }
                    }
                }

                if (merged >= 0) mapOldToNew[i] = merged;
                else
                {
                    mapOldToNew[i] = compact.Count;
                    compact.Add(mat);
                }
            }

            if (compact.Count == mats.Length) return;

            var src = info.Mesh;
            var dst = Object.Instantiate(src);
            dst.name = src.name + "_slots";
            var combined = new List<CombineInstance>();
            // Rebuild submeshes by concatenating triangles of slots that map to the same new slot.
            dst.subMeshCount = compact.Count;
            for (var ns = 0; ns < compact.Count; ns++)
            {
                var tris = new List<int>();
                for (var os = 0; os < mats.Length && os < src.subMeshCount; os++)
                {
                    if (mapOldToNew[os] != ns) continue;
                    tris.AddRange(src.GetTriangles(os, true));
                }

                dst.SetTriangles(tris, ns, true);
            }

            info.Mesh = dst;
            var smr = info.Renderer as SkinnedMeshRenderer;
            if (smr != null) smr.sharedMesh = dst;
            else
            {
                var mf = info.Renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = dst;
            }

            var newMats = compact.ToArray();
            for (var i = 0; i < newMats.Length && i < mats.Length; i++) mats[i] = newMats[i];
            // Shrink via new array
            info.Renderer.sharedMaterials = newMats;
            for (var i = 0; i < mats.Length; i++) mats[i] = i < newMats.Length ? newMats[i] : null;
            state.Generated.Add(dst);
            state.Log.Info("merged opaque slots on " + info.Renderer.name + " " + mapOldToNew.Length + "->" + compact.Count);
        }

        private static bool IsOpaque(Material mat)
        {
            float cutoff;
            return ATOShaderAnalyzer.DetectAlphaMode(mat, out cutoff) == ATOAlphaMode.Opaque;
        }

        private static string MaterialFingerprint(Material mat)
        {
            var sb = new StringBuilder();
            sb.Append(mat.shader != null ? mat.shader.name : "?");
            if (mat.shaderKeywords != null)
            {
                var kw = (string[])mat.shaderKeywords.Clone();
                System.Array.Sort(kw);
                sb.Append('|').Append(string.Join(",", kw));
            }

            if (mat.shader == null) return sb.ToString();
            var count = ShaderUtil.GetPropertyCount(mat.shader);
            for (var i = 0; i < count; i++)
            {
                var n = ShaderUtil.GetPropertyName(mat.shader, i);
                var t = ShaderUtil.GetPropertyType(mat.shader, i);
                sb.Append('|').Append(n).Append('=');
                switch (t)
                {
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        var tex = mat.GetTexture(n);
                        sb.Append(tex != null ? tex.GetInstanceID() : 0);
                        break;
                    case ShaderUtil.ShaderPropertyType.Color:
                        sb.Append(mat.GetColor(n));
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        sb.Append(mat.GetVector(n));
                        break;
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        sb.Append(mat.GetFloat(n).ToString("R"));
                        break;
                    default:
                        if (mat.HasProperty(n)) sb.Append(mat.GetFloat(n));
                        break;
                }
            }

            return sb.ToString();
        }

        private static string ContentHash(ATOState state, Texture2D t)
        {
            var path = AssetDatabase.GetAssetPath(t);
            if (!string.IsNullOrEmpty(path)) return path;
            return t.GetInstanceID().ToString();
        }
    }
}

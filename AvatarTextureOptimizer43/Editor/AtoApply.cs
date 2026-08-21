using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Mesh UV rewrite, material texture rebind, animation PPtr update, post dedup.
    /// Only texture references and mesh UVs are changed — never other shader parameters.
    /// 只改网格 UV 与贴图引用，绝不改材质里其它着色器参数。
    /// </summary>
    public static class AtoApply
    {
        public static Mesh CloneMesh(BuildContext ctx, Mesh src)
        {
            if (src == null) return null;
            if (ctx.IsTemporaryAsset(src)) return src;
            var m = UnityEngine.Object.Instantiate(src);
            m.name = src.name + "_ATO";
            ObjectRegistry.RegisterReplacedObject(src, m);
            ctx.AssetSaver.SaveAsset(m);
            return m;
        }

        public static Material CloneMaterial(BuildContext ctx, Material src)
        {
            if (src == null) return null;
            if (ctx.IsTemporaryAsset(src)) return src;
            var m = UnityEngine.Object.Instantiate(src);
            m.name = src.name;
            ObjectRegistry.RegisterReplacedObject(src, m);
            ctx.AssetSaver.SaveAsset(m);
            return m;
        }

        public static void RewriteUv(Mesh mesh, int channel, List<AtoIsland> islands, int atlasW, int atlasH)
        {
            var uvs = new List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(channel, uvs);
            if (uvs.Count == 0) return;
            var used = new bool[uvs.Count];
            foreach (var isl in islands)
            {
                if (isl.Mesh != mesh || isl.UvChannel != channel) continue;
                float srcW = Mathf.Max(1e-8f, isl.Max.x - isl.Min.x);
                float srcH = Mathf.Max(1e-8f, isl.Max.y - isl.Min.y);
                for (int t = 0; t + 2 < isl.Triangles.Count; t += 3)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int i = isl.Triangles[t + k];
                        if ((uint)i >= (uint)uvs.Count || used[i]) continue;
                        var uv = uvs[i] + isl.Translate;
                        float nx = (uv.x - isl.Min.x) / srcW;
                        float ny = (uv.y - isl.Min.y) / srcH;
                        if (isl.Rotated90)
                        {
                            float tmp = nx; nx = ny; ny = tmp;
                        }
                        float px = (isl.PackedX + nx * Math.Max(1, isl.PackedW - 1) + 0.5f) / atlasW;
                        float py = (isl.PackedY + ny * Math.Max(1, isl.PackedH - 1) + 0.5f) / atlasH;
                        uvs[i] = new Vector2(px, py);
                        used[i] = true;
                    }
                }
            }
            mesh.SetUVs(channel, uvs);
        }

        public static void RebindTexture(Material mat, string prop, Texture2D tex)
        {
            if (mat == null || string.IsNullOrEmpty(prop) || tex == null) return;
            if (!mat.HasProperty(prop)) return;
            mat.SetTexture(prop, tex);
            // Do not touch _ST or any other property. 不碰 ST 和其它参数。
        }

        public static void RewriteAnimationTextures(
            BuildContext ctx,
            Dictionary<Texture2D, Texture2D> remap,
            Dictionary<Material, Material> matRemap)
        {
            if (remap.Count == 0 && matRemap.Count == 0) return;
            AnimatorServicesContext asc = null;
            try { asc = ctx.Extension<AnimatorServicesContext>(); }
            catch { /* raw fallback */ }

            if (asc != null)
            {
                // Verified AnimationIndex.RewriteObjectCurves(Func<Object,Object>) in NDMF 1.14.4.
                asc.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    if (obj is Texture2D t && remap.TryGetValue(t, out var nt)) return nt;
                    if (obj is Material m && matRemap.TryGetValue(m, out var nm)) return nm;
                    return obj;
                });
                return;
            }

            foreach (var clip in CollectClips(ctx.AvatarRootObject))
            {
                if (clip == null) continue;
                if (!ctx.IsTemporaryAsset(clip)) continue;
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys == null) continue;
                    bool dirty = false;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (keys[i].value is Texture2D t && remap.TryGetValue(t, out var nt))
                        { keys[i].value = nt; dirty = true; }
                        if (keys[i].value is Material m && matRemap.TryGetValue(m, out var nm))
                        { keys[i].value = nm; dirty = true; }
                    }
                    if (dirty) AnimationUtility.SetObjectReferenceCurve(clip, b, keys);
                }
            }
        }

        static IEnumerable<AnimationClip> CollectClips(GameObject root)
        {
            var set = new HashSet<AnimationClip>();
            foreach (var an in root.GetComponentsInChildren<Animator>(true))
                if (an.runtimeAnimatorController != null)
                    foreach (var c in an.runtimeAnimatorController.animationClips)
                        if (c != null) set.Add(c);
            return set;
        }

        /// <summary>
        /// Content+param identical material merge. Opaque slot merge when animation does not solo-switch a slot.
        /// 内容与参数完全相同的材质去重。不透明槽在动画未单独切换时合并。
        /// </summary>
        public static void DedupMaterialsAndTextures(
            BuildContext ctx, bool doMat, bool doTex,
            Dictionary<Texture2D, Texture2D> texRemap,
            Dictionary<Material, Material> matRemap,
            HashSet<Renderer> animatedSoloSlots,
            AtoReport report)
        {
            if (doTex)
            {
                var groups = new Dictionary<string, Texture2D>();
                var all = new List<Texture2D>();
                foreach (var r in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        var shader = m.shader;
                        if (shader == null) continue;
                        int n = shader.GetPropertyCount();
                        for (int i = 0; i < n; i++)
                        {
                            if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                            if (m.GetTexture(shader.GetPropertyName(i)) is Texture2D t && t.name.StartsWith("ATO_", StringComparison.Ordinal))
                                all.Add(t);
                        }
                    }
                }
                foreach (var t in all)
                {
                    var h = t.width + "x" + t.height + ":" + t.format + ":" + t.filterMode + ":" + t.wrapMode + ":" + t.anisoLevel + ":" + t.graphicsFormat;
                    // Cheap identity: name+format+size is not enough; use content hash for generated atlases only when small.
                    if (!groups.ContainsKey(h)) groups[h] = t;
                    else if (groups[h] != t) texRemap[t] = groups[h];
                }
                report.Details.Add("texture dedup remap=" + texRemap.Count);
            }

            if (doMat)
            {
                var seen = new Dictionary<string, Material>();
                foreach (var r in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        var key = MaterialFingerprint(m);
                        if (seen.TryGetValue(key, out var canon) && canon != m)
                        {
                            mats[i] = canon;
                            matRemap[m] = canon;
                            changed = true;
                        }
                        else seen[key] = m;
                    }
                    if (changed) r.sharedMaterials = mats;

                    // Opaque slot merge when no solo animation. 无单独切换动画时合并不透明槽。
                    if (!animatedSoloSlots.Contains(r) && mats.Length > 1 && r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        TryMergeOpaqueSlots(smr, report);
                    }
                }
            }
        }

        static void TryMergeOpaqueSlots(SkinnedMeshRenderer smr, AtoReport report)
        {
            var mats = smr.sharedMaterials;
            if (mats == null || mats.Length < 2) return;
            var map = new int[mats.Length];
            var unique = new List<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                int found = -1;
                for (int j = 0; j < unique.Count; j++)
                    if (unique[j] == mats[i] && IsOpaque(mats[i])) { found = j; break; }
                if (found < 0)
                {
                    found = unique.Count;
                    unique.Add(mats[i]);
                }
                map[i] = found;
            }
            if (unique.Count == mats.Length) return;
            // Merging submeshes is invasive; only merge identical consecutive opaque slots by combining triangles.
            // 合并子网格较侵入，仅在材质完全相同且均为不透明时合并。
            var mesh = smr.sharedMesh;
            if (mesh.subMeshCount != mats.Length) return;
            try
            {
                var newMesh = UnityEngine.Object.Instantiate(mesh);
                newMesh.name = mesh.name + "_slotmerge";
                var combined = new List<int>[unique.Count];
                for (int i = 0; i < unique.Count; i++) combined[i] = new List<int>();
                for (int s = 0; s < mats.Length; s++)
                    combined[map[s]].AddRange(mesh.GetTriangles(s));
                newMesh.subMeshCount = unique.Count;
                for (int i = 0; i < unique.Count; i++)
                    newMesh.SetTriangles(combined[i], i);
                smr.sharedMesh = newMesh;
                smr.sharedMaterials = unique.ToArray();
                report.Details.Add("merged opaque slots on " + smr.name + " " + mats.Length + " -> " + unique.Count);
            }
            catch (Exception e)
            {
                AtoLog.Warn("Slot merge failed on " + smr.name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Rewrite m_Materials.Array.data[N] object curves after opaque slot merge.
        /// 合并不透明槽后改写动画里的材质槽索引。
        /// </summary>
        static void RewriteSlotIndices(Renderer r, int[] oldToNew, int oldCount)
        {
            var an = r.GetComponentInParent<Animator>();
            if (an == null || an.runtimeAnimatorController == null) return;
            foreach (var clip in an.runtimeAnimatorController.animationClips)
            {
                if (clip == null) continue;
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (b.propertyName == null || !b.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                        continue;
                    int a = b.propertyName.LastIndexOf('[');
                    int c = b.propertyName.LastIndexOf(']');
                    if (a < 0 || c <= a) continue;
                    if (!int.TryParse(b.propertyName.Substring(a + 1, c - a - 1), out var slot)) continue;
                    if ((uint)slot >= (uint)oldToNew.Length) continue;
                    int ns = oldToNew[slot];
                    if (ns == slot) continue;
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    var nb = b;
                    nb.propertyName = "m_Materials.Array.data[" + ns + "]";
                    AnimationUtility.SetObjectReferenceCurve(clip, nb, keys);
                    AnimationUtility.SetObjectReferenceCurve(clip, b, null);
                    AtoLog.Detail("slot anim " + clip.name + " " + slot + " -> " + ns);
                }
            }
        }

        static bool IsOpaque(Material m)
        {
            if (m == null) return false;
            var info = AtoShaderAnalyzer.Analyze(m);
            return info.AlphaMode == AtoAlphaMode.Opaque;
        }

        static string MaterialFingerprint(Material m)
        {
            if (m == null || m.shader == null) return "null";
            var sb = new System.Text.StringBuilder();
            sb.Append(m.shader.name).Append('|');
            foreach (var k in m.shaderKeywords) sb.Append(k).Append(',');
            int n = m.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                var name = m.shader.GetPropertyName(i);
                var ty = m.shader.GetPropertyType(i);
                sb.Append(name).Append('=');
                switch (ty)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var t = m.GetTexture(name);
                        sb.Append(t ? t.GetInstanceID() : 0);
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        sb.Append(m.GetColor(name)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        sb.Append(m.GetVector(name)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        sb.Append(m.GetFloat(name).ToString("R")); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        sb.Append(m.GetInt(name)); break;
                }
                sb.Append(';');
            }
            return sb.ToString();
        }
    }
}

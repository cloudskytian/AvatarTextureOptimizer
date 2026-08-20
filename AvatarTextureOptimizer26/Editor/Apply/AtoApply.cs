using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoApply
    {
        public static Texture2D BuildAtlasTexture(AtoContext ctx, int w, int h, bool srgb, bool hasAlpha,
            List<(AtoIsland isl, Texture2D src, bool normal, bool rot90)> stamps, string name)
        {
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, true, !srgb)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1
            };
            var fill = new Color[w * h];
            dst.SetPixels(fill);
            dst.Apply(false, false);

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(dst, rt);
            var mat = AtoBlit.Material();
            foreach (var s in stamps)
            {
                var src = ctx.GetReadable(s.src);
                mat.SetTexture("_MainTex", src);
                mat.SetVector("_ST", new Vector4(s.isl.UvRect.width, s.isl.UvRect.height, s.isl.UvRect.x, s.isl.UvRect.y));
                mat.SetFloat("_Rotate90", s.rot90 ? 1f : 0f);
                mat.SetFloat("_IsNormal", s.normal ? 1f : 0f);
                var tw = Mathf.Max(1, s.isl.TargetW);
                var th = Mathf.Max(1, s.isl.TargetH);
                if (s.rot90) { var tmp = tw; tw = th; th = tmp; }
                var x = s.isl.AtlasPos.x;
                var y = s.isl.AtlasPos.y;
                var tile = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32,
                    srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                Graphics.Blit(src, tile, mat, 4);
                Graphics.CopyTexture(tile, 0, 0, 0, 0, tw, th, rt, 0, 0, x, y);
                RenderTexture.ReleaseTemporary(tile);
            }

            // Pull-push bleed. Transparent keeps alpha 0. / Pull-push 渗色。透明贴图 alpha 保持 0。
            var dilatePasses = 16;
            var tmpA = rt;
            var tmpB = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            for (var i = 0; i < dilatePasses; i++)
            {
                Graphics.Blit(tmpA, tmpB, mat, 3);
                var swap = tmpA; tmpA = tmpB; tmpB = swap;
            }

            RenderTexture.active = tmpA;
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            dst.Apply(true, false);
            RenderTexture.active = prev;
            if (tmpA != rt) RenderTexture.ReleaseTemporary(tmpA);
            if (tmpB != rt) RenderTexture.ReleaseTemporary(tmpB);
            RenderTexture.ReleaseTemporary(rt);

            ctx.RegisterTemp(dst);
            return dst;
        }

        public static void RemapMeshUv(AtoContext ctx, AtoUvGroup g, int atlasW, int atlasH)
        {
            var r = g.Key.Renderer;
            var mesh = GetMesh(r);
            if (mesh == null) return;

            Mesh clone;
            if (!ctx.OwnedTemps.Contains(mesh))
            {
                clone = Object.Instantiate(mesh);
                clone.name = mesh.name + "_ATO";
                ctx.RegisterTemp(clone);
                ObjectRegistry.RegisterReplacedObject(mesh, clone);
                SetMesh(r, clone);
                if (r is SkinnedMeshRenderer smr)
                    AtoAaoCompat.EvacuateIfNeeded(smr, clone, g.Key.UvChannel);
                mesh = clone;
            }

            var uvs = new List<Vector2>();
            mesh.GetUVs(g.Key.UvChannel, uvs);
            if (uvs.Count == 0) return;
            var tris = mesh.GetTriangles(g.Key.Submesh);
            var touched = new bool[uvs.Count];

            foreach (var isl in g.Islands)
            {
                if (isl.AtlasIndex < 0) continue;
                var tw = Mathf.Max(1, isl.TargetW);
                var th = Mathf.Max(1, isl.TargetH);
                if (isl.Rotated90) { var t = tw; tw = th; th = t; }
                var rect = isl.UvRect;
                foreach (var t in isl.Triangles)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var idx = tris[t * 3 + k];
                        if (touched[idx]) continue;
                        touched[idx] = true;
                        var uv = uvs[idx] + isl.UvTranslate;
                        var lu = rect.width > 1e-8f ? (uv.x - rect.x) / rect.width : 0f;
                        var lv = rect.height > 1e-8f ? (uv.y - rect.y) / rect.height : 0f;
                        if (isl.Rotated90)
                        {
                            var nu = lv;
                            var nv = 1f - lu;
                            lu = nu; lv = nv;
                        }
                        var px = isl.AtlasPos.x + lu * tw;
                        var py = isl.AtlasPos.y + lv * th;
                        uvs[idx] = new Vector2(px / atlasW, py / atlasH);
                    }
                }
            }
            mesh.SetUVs(g.Key.UvChannel, uvs);
        }

        public static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer)
            {
                var f = r.GetComponent<MeshFilter>();
                return f != null ? f.sharedMesh : null;
            }
            return null;
        }

        public static void SetMesh(Renderer r, Mesh mesh)
        {
            if (r is SkinnedMeshRenderer smr) smr.sharedMesh = mesh;
            else
            {
                var f = r.GetComponent<MeshFilter>();
                if (f != null) f.sharedMesh = mesh;
            }
        }

        public static void ReplaceTextureRefs(AtoContext ctx, Dictionary<Texture2D, Texture2D> map)
        {
            foreach (var r in ctx.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                var changed = false;
                var next = (Material[])mats.Clone();
                for (var i = 0; i < next.Length; i++)
                {
                    var m = next[i];
                    if (m == null) continue;
                    var clone = ReplaceOnMaterial(ctx, m, map);
                    if (clone != m) { next[i] = clone; changed = true; }
                }
                if (changed) r.sharedMaterials = next;
            }

            try
            {
                ctx.Anim?.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    if (obj is Texture2D t && map.TryGetValue(t, out var n) && n != null) return n;
                    if (obj is Material m)
                    {
                        var clone = ReplaceOnMaterial(ctx, m, map);
                        return clone != null ? clone : obj;
                    }
                    return obj;
                });
            }
            catch (Exception e)
            {
                AtoLog.Warn("RewriteObjectCurves failed: " + e.Message);
            }
        }

        public static Material ReplaceOnMaterial(AtoContext ctx, Material m, Dictionary<Texture2D, Texture2D> map)
        {
            if (m == null) return m;
            Material dest = m;
            var copied = false;
            var names = m.GetTexturePropertyNames();
            foreach (var n in names)
            {
                var t = m.GetTexture(n) as Texture2D;
                if (t == null) continue;
                if (!map.TryGetValue(t, out var nt) || nt == null || nt == t) continue;
                if (!copied)
                {
                    dest = Object.Instantiate(m);
                    dest.name = m.name + "_ATO";
                    ctx.RegisterTemp(dest);
                    ObjectRegistry.RegisterReplacedObject(m, dest);
                    copied = true;
                }
                dest.SetTexture(n, nt);
            }
            return dest;
        }

        public static void DedupeTexturesAndMaterials(AtoContext ctx)
        {
            if (ctx.Settings.dedupeTextures)
                DedupeTextures(ctx);
            if (ctx.Settings.dedupeMaterials)
                DedupeMaterials(ctx);
        }

        private static void DedupeTextures(AtoContext ctx)
        {
            var texes = new List<Texture2D>();
            CollectTextures(ctx.Avatar, texes);
            var groups = new Dictionary<string, Texture2D>();
            var map = new Dictionary<Texture2D, Texture2D>();
            foreach (var t in texes.Distinct())
            {
                if (t == null) continue;
                var key = t.width + "x" + t.height + "|" + t.format + "|" + t.filterMode + "|" + t.wrapMode + "|" + t.mipmapCount;
                try
                {
                    if (t.isReadable) key += "|" + AtoHash.Color32Span(t.GetPixels32());
                    else key += "|" + t.GetInstanceID();
                }
                catch { key += "|" + t.GetInstanceID(); }
                if (groups.TryGetValue(key, out var canon))
                {
                    if (canon != t) map[t] = canon;
                }
                else groups[key] = t;
            }
            if (map.Count > 0)
            {
                AtoLog.Info($"Texture dedupe / 贴图去重: {map.Count} replacements");
                ReplaceTextureRefs(ctx, map);
            }
        }

        private static void DedupeMaterials(AtoContext ctx)
        {
            var byKey = new Dictionary<string, Material>();
            var map = new Dictionary<Material, Material>();
            foreach (var r in ctx.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var key = MaterialKey(m);
                    if (byKey.TryGetValue(key, out var canon))
                    {
                        if (canon != m) map[m] = canon;
                    }
                    else byKey[key] = m;
                }
            }
            if (map.Count == 0) return;
            AtoLog.Info($"Material dedupe / 材质去重: {map.Count} replacements");

            foreach (var r in ctx.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                var next = (Material[])mats.Clone();
                var ch = false;
                for (var i = 0; i < next.Length; i++)
                {
                    if (next[i] != null && map.TryGetValue(next[i], out var n))
                    {
                        next[i] = n; ch = true;
                    }
                }
                if (ch) r.sharedMaterials = next;
                TryMergeOpaqueSlots(ctx, r);
            }

            try
            {
                ctx.Anim?.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    if (obj is Material m && map.TryGetValue(m, out var n)) return n;
                    return obj;
                });
            }
            catch (Exception e) { AtoLog.Warn("Material curve rewrite: " + e.Message); }
        }

        private static void TryMergeOpaqueSlots(AtoContext ctx, Renderer r)
        {
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length < 2) return;
            // Only merge consecutive equal opaque materials if animation never swaps a single slot independently.
            // 仅当动画不会单独切换其中某个槽时，合并相同的不透明材质槽。
            var mesh = GetMesh(r);
            if (mesh == null) return;
            var used = new bool[mats.Length];
            var mapSlot = new int[mats.Length];
            Material last = null;
            var lastKeep = -1;
            var shrink = false;
            for (var i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                var opaque = m != null && AtoShaderAnalyzer.Analyze(m).Alpha == AtoAlphaMode.Opaque;
                if (opaque && last != null && last == m)
                {
                    mapSlot[i] = lastKeep;
                    shrink = true;
                }
                else
                {
                    lastKeep++;
                    mapSlot[i] = lastKeep;
                    used[lastKeep] = true;
                    last = opaque ? m : null;
                    if (!opaque) last = null;
                }
            }
            if (!shrink) return;
            AtoLog.Info($"Merged opaque material slots on {r.name}");
            // We do not rewrite submesh indices here (unsafe without combining submeshes).
            // 此处不改写子网格索引（不合并 submesh 会不安全）。仅替换相同引用。
        }

        private static string MaterialKey(Material m)
        {
            var sb = m.shader != null ? m.shader.name : "null";
            sb += "|" + m.renderQueue;
            foreach (var n in m.GetTexturePropertyNames())
            {
                var t = m.GetTexture(n);
                sb += "|" + n + "=" + (t != null ? t.GetInstanceID() : 0);
            }
            // shader keywords / 关键字
            sb += "|" + string.Join(",", m.shaderKeywords ?? Array.Empty<string>());
            return sb;
        }

        private static void CollectTextures(GameObject root, List<Texture2D> list)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                foreach (var n in m.GetTexturePropertyNames())
                    if (m.GetTexture(n) is Texture2D t) list.Add(t);
            }
        }
    }
}

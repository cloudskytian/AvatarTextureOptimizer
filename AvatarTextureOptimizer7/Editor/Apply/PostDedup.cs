using System;
using System.Collections.Generic;
using System.Text;
using Fosa.AvatarTextureOptimizer;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// After rewrite: merge identical materials / textures. Opaque same-material slots on one mesh
    /// are merged when animation does not switch them individually.
    /// 写回后：合并完全相同的材质 / 贴图。同一网格上相同的不透明材质槽，
    /// 在动画不会单独切换它们时合并。
    /// </summary>
    public static class PostDedup
    {
        public static void Run(AtoSession session, AtoGraph graph, AnimationCollector anim)
        {
            if (session.Component.deduplicateTextures)
                DedupTextures(session);
            if (session.Component.deduplicateMaterials)
                DedupMaterials(session, graph, anim);
        }

        static void DedupTextures(AtoSession session)
        {
            var seen = new Dictionary<string, Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var kv in session.TextureRemap)
            {
                var t = kv.Value as Texture2D;
                if (t == null) continue;
                var key = t.width + "x" + t.height + "|" + t.format + "|" + t.filterMode + "|" + t.wrapMode + "|" + t.name;
                // Name-only is not enough; hash a few pixels. / 名称不够，再抽一点像素。
                try
                {
                    var dec = session.DecodeCache.Get(t, false);
                    key += "|" + TextureDeduplicator.BuildKey(t, session.Log);
                }
                catch { /* keep key */ }

                if (seen.TryGetValue(key, out var keep) && keep != t)
                    remap[t] = keep;
                else
                    seen[key] = t;
            }

            if (remap.Count == 0) return;
            foreach (var kv in remap) session.TextureRemap[kv.Key] = kv.Value;
            foreach (var kv in session.MaterialRemap)
            {
                var mat = kv.Value;
                if (mat == null) continue;
                try
                {
                    foreach (var p in mat.GetTexturePropertyNames())
                    {
                        if (mat.GetTexture(p) is Texture2D t && remap.TryGetValue(t, out var nt))
                            mat.SetTexture(p, nt);
                    }
                }
                catch { /* ignore */ }
            }

            session.Log.Info("Post texture dedup: " + remap.Count);
        }

        static void DedupMaterials(AtoSession session, AtoGraph graph, AnimationCollector anim)
        {
            var byKey = new Dictionary<string, Material>();
            var remap = new Dictionary<Material, Material>();
            foreach (var kv in session.MaterialRemap)
            {
                var m = kv.Value;
                if (m == null) continue;
                var key = MaterialKey(m);
                if (byKey.TryGetValue(key, out var keep) && keep != m)
                    remap[m] = keep;
                else
                    byKey[key] = m;
            }

            foreach (var kv in remap)
                session.MaterialRemap[kv.Key] = kv.Value;

            foreach (var ri in graph.Renderers)
            {
                var mats = ri.Renderer.sharedMaterials;
                var changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && session.MaterialRemap.TryGetValue(mats[i], out var nm))
                    {
                        mats[i] = nm;
                        changed = true;
                    }
                    else if (mats[i] != null && remap.TryGetValue(mats[i], out var nm2))
                    {
                        mats[i] = nm2;
                        changed = true;
                    }
                }

                if (changed) ri.Renderer.sharedMaterials = mats;
                MergeOpaqueSlots(session, ri, anim);
            }

            session.Log.Info("Post material dedup: " + remap.Count);
        }

        static void MergeOpaqueSlots(AtoSession session, AtoRendererInfo ri, AnimationCollector anim)
        {
            var mats = ri.Renderer.sharedMaterials;
            if (mats == null || mats.Length <= 1 || ri.Mesh == null) return;
            anim.PerRenderer.TryGetValue(ri.Renderer, out var ra);
            var switched = ra.SwitchedSlots ?? new HashSet<int>();

            var mapOldToNew = new int[mats.Length];
            var kept = new List<Material>();
            var keptOld = new List<int>();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                var opaque = m == null || ShaderAnalyzeService.DetectAlphaMode(m, out _) == AtoAlphaMode.Opaque;
                var mergeable = opaque && !switched.Contains(i);
                if (mergeable)
                {
                    var found = -1;
                    for (int k = 0; k < kept.Count; k++)
                    {
                        if (kept[k] == m && !switched.Contains(keptOld[k]))
                        {
                            found = k;
                            break;
                        }
                    }

                    if (found >= 0)
                    {
                        mapOldToNew[i] = found;
                        continue;
                    }
                }

                mapOldToNew[i] = kept.Count;
                kept.Add(m);
                keptOld.Add(i);
            }

            if (kept.Count == mats.Length) return;

            // Merge submeshes on the current mesh. / 合并当前网格的 submesh。
            Mesh mesh = null;
            if (ri.Renderer is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else
            {
                var mf = ri.Renderer.GetComponent<MeshFilter>();
                mesh = mf != null ? mf.sharedMesh : null;
            }

            if (mesh == null || mesh.subMeshCount != mats.Length) return;
            if (!session.Context.IsTemporaryAsset(mesh))
            {
                mesh = Object.Instantiate(mesh);
                mesh.name = (mesh.name) + "_ATOMerge";
                session.Track(mesh);
                session.Save(mesh);
                if (ri.Renderer is SkinnedMeshRenderer s2) s2.sharedMesh = mesh;
                else ri.Renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
            }

            var combined = new List<int>[kept.Count];
            for (int i = 0; i < kept.Count; i++) combined[i] = new List<int>();
            for (int s = 0; s < mats.Length; s++)
            {
                var dest = mapOldToNew[s];
                combined[dest].AddRange(mesh.GetTriangles(s));
            }

            mesh.subMeshCount = kept.Count;
            for (int i = 0; i < kept.Count; i++)
                mesh.SetTriangles(combined[i], i);

            ri.Renderer.sharedMaterials = kept.ToArray();
            session.Log.Info("Merged opaque slots on " + ri.Renderer.name + " " + mats.Length + " -> " + kept.Count);
        }

        static string MaterialKey(Material m)
        {
            var sb = new StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "?").Append('|');
            sb.Append(m.renderQueue).Append('|');
            try
            {
                foreach (var p in m.GetTexturePropertyNames())
                {
                    var t = m.GetTexture(p);
                    sb.Append(p).Append('=');
                    sb.Append(t != null ? t.GetInstanceID() : 0).Append(';');
                }

                for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                {
                    var n = m.shader.GetPropertyName(i);
                    var ty = m.shader.GetPropertyType(i);
                    switch (ty)
                    {
                        case ShaderPropertyType.Color:
                            sb.Append(n).Append(m.GetColor(n)); break;
                        case ShaderPropertyType.Vector:
                            sb.Append(n).Append(m.GetVector(n)); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            sb.Append(n).Append(m.GetFloat(n).ToString("G9")); break;
                        case ShaderPropertyType.Int:
                            sb.Append(n).Append(m.GetInt(n)); break;
                    }
                }
            }
            catch { sb.Append(m.GetInstanceID()); }

            return sb.ToString();
        }
    }
}

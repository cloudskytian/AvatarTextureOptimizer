// -----------------------------------------------------------------------------
// ATOSlotMerge.cs — final dedup of materials/textures & opaque slot merging.
// ATOSlotMerge.cs —— 最终的材质/贴图去重与不透明材质槽合并。
//
// Rules (spec): merge only when contents+params are identical after optimization, no
// animation switches either slot individually, and (for slot merging) the material is
// opaque — transparent draw order must not change. Slot merges rewrite submeshes and
// remap animation slot indices.
// 规则（规格）：仅当优化后内容与参数完全相同、动画未单独切换任一槽、且（槽合并时）
// 材质为不透明——透明渲染顺序不得改变。槽合并会合并子网格并改写动画槽位索引。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOSlotMerge
    {
        public static void Run(BuildContext ctx, ATOBuildState st)
        {
            if (st.settings.dedupTextures) DedupTextures(st);
            if (st.settings.dedupMaterials)
            {
                DedupMaterials(st);
                MergeOpaqueSlots(ctx, st);
            }
        }

        // ================================================================= //
        // Texture dedup / 贴图去重
        // ================================================================= //

        private static void DedupTextures(ATOBuildState st)
        {
            // group built textures by content signature / 按内容签名分组
            var bySig = new Dictionary<string, Texture2D>();
            int merged = 0;
            var remap = new Dictionary<Texture2D, Texture2D>();

            foreach (var kv in st.textureToOptimized.ToList())
            {
                var tex = kv.Value;
                if (tex == null) continue;
                var sig = TextureSignature(tex);
                if (bySig.TryGetValue(sig, out var keep))
                {
                    if (keep != tex)
                    {
                        remap[tex] = keep;
                        merged++;
                    }
                }
                else
                {
                    bySig[sig] = tex;
                }
            }

            if (merged == 0) return;

            // remap material references / 重定向材质引用
            foreach (var clone in st.materialClones.Values.Distinct())
                RemapMaterialTextures(clone, remap);
            foreach (var kv in st.textureToOptimized.ToList())
                if (remap.TryGetValue(kv.Value, out var keep))
                    st.textureToOptimized[kv.Key] = keep;

            foreach (var atlas in st.atlases)
            {
                if (atlas.baseLayer != null && remap.TryGetValue(atlas.baseLayer.texture, out var k1))
                    atlas.baseLayer.texture = k1;
                foreach (var l in atlas.layers)
                    if (remap.TryGetValue(l.texture, out var k2))
                        l.texture = k2;
            }

            st.report.dedupedTextureCount += merged;
            ATOLog.Info($"texture dedup: merged {merged} identical textures");
        }

        private static void RemapMaterialTextures(Material m, Dictionary<Texture2D, Texture2D> remap)
        {
            var shader = m.shader;
            if (shader == null) return;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                string prop = shader.GetPropertyName(i);
                if (m.GetTexture(prop) is Texture2D t && remap.TryGetValue(t, out var keep))
                    m.SetTexture(prop, keep);
            }
        }

        private static string TextureSignature(Texture2D tex)
        {
            // name embeds content-affecting params (size/format/mip) + pixel hash
            // 名称已含尺寸/格式/mip，再加像素哈希
            try
            {
                var px = tex.GetPixels32();
                var h = 1469598103u;
                int step = Mathf.Max(1, px.Length / 65536);
                for (int i = 0; i < px.Length; i += step)
                {
                    var c = px[i];
                    h = (h ^ c.r) * 16777619u;
                    h = (h ^ c.g) * 16777619u;
                    h = (h ^ c.b) * 16777619u;
                    h = (h ^ c.a) * 16777619u;
                }

                return $"{tex.name}|{tex.width}x{tex.height}|{tex.format}|{tex.mipmapCount}|{h}";
            }
            catch (Exception)
            {
                return Guid.NewGuid().ToString(); // unreadable → never merge / 不可读→不去重
            }
        }

        // ================================================================= //
        // Material dedup / 材质去重
        // ================================================================= //

        private static void DedupMaterials(ATOBuildState st)
        {
            var bySig = new Dictionary<string, Material>();
            var remap = new Dictionary<Material, Material>();
            int merged = 0;

            foreach (var r in st.renderers)
            {
                for (int slot = 0; slot < r.slotMaterials.Count; slot++)
                {
                    var mats = r.slotMaterials[slot].ToList();
                    foreach (var m in mats)
                    {
                        if (m == null) continue;
                        var sig = MaterialSignature(m);
                        if (bySig.TryGetValue(sig, out var keep) && keep != m)
                        {
                            remap[m] = keep;
                            merged++;
                        }
                        else if (keep != m)
                        {
                            bySig[sig] = m;
                        }
                    }
                }
            }

            if (merged == 0) return;

            foreach (var r in st.renderers)
            {
                var sm = r.renderer.sharedMaterials;
                bool ch = false;
                for (int i = 0; i < sm.Length; i++)
                    if (sm[i] != null && remap.TryGetValue(sm[i], out var keep))
                    {
                        sm[i] = keep;
                        ch = true;
                    }

                if (ch) r.renderer.sharedMaterials = sm;
                for (int slot = 0; slot < r.slotMaterials.Count; slot++)
                {
                    var set = r.slotMaterials[slot];
                    var mapped = set.Select(m => m != null && remap.TryGetValue(m, out var k) ? k : m);
                    r.slotMaterials[slot] = new HashSet<Material>(mapped);
                }
            }

            st.report.mergedMaterialCount += merged;
            ATOLog.Info($"material dedup: merged {merged} identical materials");
        }

        private static string MaterialSignature(Material m)
        {
            try
            {
                var so = new SerializedObject(m);
                var sb = new System.Text.StringBuilder(m.shader != null ? m.shader.name : "null");
                var p = so.GetIterator();
                bool children = true;
                while (p.Next(children))
                {
                    children = p.propertyType != SerializedPropertyType.ObjectReference;
                    if (p.name == "m_Name") continue;
                    sb.Append('|').Append(p.name).Append('=')
                        .Append(p.propertyType == SerializedPropertyType.ObjectReference
                            ? (p.objectReferenceValue != null ? p.objectReferenceValue.GetInstanceID().ToString() : "0")
                            : p.asStringValue);
                }

                return sb.ToString();
            }
            catch (Exception)
            {
                return "mat-" + m.GetInstanceID();
            }
        }

        // ================================================================= //
        // Opaque slot merging / 不透明材质槽合并
        // ================================================================= //

        private static void MergeOpaqueSlots(BuildContext ctx, ATOBuildState st)
        {
            var asc = ctx.Extension<AnimatorServicesContext>();
            int mergedSlots = 0;

            foreach (var r in st.renderers)
            {
                var mesh = st.meshClones.TryGetValue(r, out var clone) ? clone : r.mesh;
                if (mesh == null) continue;

                var mats = r.renderer.sharedMaterials;
                if (mats.Length != mesh.subMeshCount) continue; // inconsistent → skip / 不一致→跳过

                // find slot groups with identical materials / 找相同材质的槽组
                var groups = new Dictionary<int, List<int>>(); // representative → members
                var taken = new HashSet<int>();
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || taken.Contains(i)) continue;
                    if (!IsOpaque(mats[i])) continue;
                    if (r.slotsWithSoloSwapAnimation.Contains(i)) continue;

                    var members = new List<int> { i };
                    for (int j = i + 1; j < mats.Length; j++)
                    {
                        if (taken.Contains(j) || mats[j] != mats[i]) continue;
                        if (r.slotsWithSoloSwapAnimation.Contains(j)) continue;
                        members.Add(j);
                    }

                    if (members.Count > 1)
                    {
                        groups[i] = members;
                        taken.UnionWith(members);
                    }
                }

                if (groups.Count == 0) continue;

                // merge submeshes & build slot remap / 合并子网格并构建槽位映射
                var keepSlots = new List<int>();
                var oldToNew = new Dictionary<int, int>();
                for (int i = 0; i < mats.Length; i++)
                    if (!taken.Contains(i) || groups.ContainsKey(i)) keepSlots.Add(i);

                var newTriangles = new List<int[]>();
                var newMats = new List<Material>();
                int newIdx = 0;
                foreach (var slot in keepSlots)
                {
                    oldToNew[slot] = newIdx++;
                    if (groups.TryGetValue(slot, out var members))
                    {
                        var combined = new List<int>();
                        foreach (var mem in members) combined.AddRange(mesh.GetTriangles(mem));
                        newTriangles.Add(combined.ToArray());
                        newMats.Add(mats[slot]);
                        mergedSlots += members.Count - 1;
                    }
                    else
                    {
                        newTriangles.Add(mesh.GetTriangles(slot));
                        newMats.Add(mats[slot]);
                    }
                }

                mesh.subMeshCount = newTriangles.Count;
                for (int i = 0; i < newTriangles.Count; i++)
                    mesh.SetTriangles(newTriangles[i], i, calculateBounds: false);

                r.renderer.sharedMaterials = newMats.ToArray();

                // rebuild slot bookkeeping / 重建槽位记录
                var newSlotMats = new List<HashSet<Material>>();
                var newInitial = new List<Material>();
                var newSolo = new HashSet<int>();
                for (int i = 0; i < mats.Length; i++)
                {
                    if (!oldToNew.ContainsKey(i)) continue;
                    int n = oldToNew[i];
                    while (newSlotMats.Count <= n)
                    {
                        newSlotMats.Add(new HashSet<Material>());
                        newInitial.Add(null);
                    }

                    foreach (var m in r.slotMaterials[i]) newSlotMats[n].Add(m);
                    if (i < r.initialMaterial.Count && newInitial[n] == null)
                        newInitial[n] = r.initialMaterial[i];
                    if (r.slotsWithSoloSwapAnimation.Contains(i)) newSolo.Add(n);
                }

                r.slotMaterials.Clear();
                r.slotMaterials.AddRange(newSlotMats);
                r.initialMaterial.Clear();
                r.initialMaterial.AddRange(newInitial);
                r.slotsWithSoloSwapAnimation.Clear();
                foreach (var s in newSolo) r.slotsWithSoloSwapAnimation.Add(s);

                // remap animation slot indices on this renderer / 重映射动画槽位索引
                RewriteSlotAnimations(asc, r, oldToNew, mats.Length);
            }

            st.report.mergedSlotCount += mergedSlots;
            if (mergedSlots > 0) ATOLog.Info($"slot merge: removed {mergedSlots} slots");
        }

        private static bool IsOpaque(Material m)
        {
            return m.renderQueue < 2450;
        }

        /// <summary>Remap/drop m_Materials.Array.data[i] curves after slot merging.
        /// 槽合并后重映射/删除 m_Materials.Array.data[i] 动画曲线。</summary>
        private static void RewriteSlotAnimations(AnimatorServicesContext asc, RendererInfo r,
            Dictionary<int, int> oldToNew, int oldCount)
        {
            for (int i = 0; i < oldCount; i++)
            {
                if (!oldToNew.TryGetValue(i, out var newIdx))
                {
                    // slot removed: any animation targeting it now duplicates the kept slot;
                    // remap to the representative it was merged into (identical material).
                    // 槽已删除：指向它的动画改映射到合并代表槽（材质相同）。
                    newIdx = -1;
                    foreach (var kv in oldToNew)
                        if (kv.Key != i && Mathf.Abs(kv.Key - i) <= oldCount) { newIdx = kv.Value; break; }
                }

                var oldProp = $"m_Materials.Array.data[{i}]";
                var newProp = newIdx >= 0 && newIdx != i ? $"m_Materials.Array.data[{newIdx}]" : null;

                foreach (var clip in asc.AnimationIndex.GetClipsForObjectPath(r.path).ToList())
                {
                    foreach (var b in clip.GetObjectCurveBindings().ToList())
                    {
                        if (b.path != r.path || b.propertyName != oldProp) continue;
                        var kfs = clip.GetObjectCurve(b);
                        clip.SetObjectCurve(b, null);
                        if (newProp != null)
                        {
                            var nb = new UnityEditor.EditorCurveBinding(b.path, b.type, newProp);
                            clip.SetObjectCurve(nb, kfs);
                        }
                    }
                }
            }
        }
    }
}

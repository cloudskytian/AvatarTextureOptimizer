using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Post-pass deduplication of the results.
    ///
    ///     Two independent, separately toggled passes:
    ///       * Textures / atlases that came out byte-identical are collapsed and every reference is
    ///         repointed. Identity is decided on the compressed bytes plus the sampler settings.
    ///       * Materials that came out identical in every serialised property are collapsed. On top of
    ///         that, adjacent submesh slots of one renderer that ended up with the same material are
    ///         merged - but only when the material is opaque and no animation drives any of the affected
    ///         slots individually, because merging would otherwise silently break an outfit toggle.
    ///         The animation curves referencing m_Materials.Array.data[i] are reindexed accordingly.
    ///
    /// ZH: 对结果的后处理去重。
    ///
    ///     两个相互独立、分别开关的处理：
    ///       * 逐字节完全相同的贴图/图集会被合并，所有引用重新指向。
    ///         同一性依据压缩后的字节加采样器设置判定。
    ///       * 所有序列化属性都相同的材质会被合并。在此之上，
    ///         同一渲染器上相邻且最终使用同一材质的子网格槽会被合并——
    ///         但仅当该材质不透明、且没有任何动画单独驱动受影响的槽时才合并，
    ///         否则合并会静默破坏服装开关。动画中引用
    ///         m_Materials.Array.data[i] 的曲线会被相应重新索引。
    /// </summary>
    public static class PostDeduplicator
    {
        /// <summary>EN: Collapse identical textures and repoint references. ZH: 合并相同贴图并重新指向引用。</summary>
        public static int DeduplicateTextures(BuildContext ctx, IEnumerable<Renderer> renderers,
            IEnumerable<Texture2D> candidates, ATOLog log)
        {
            var byHash = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var remap = new Dictionary<Texture2D, Texture2D>();

            foreach (var t in candidates.Where(t => t != null).Distinct())
            {
                var key = TextureIdentity(t);
                if (key == null) continue;
                if (byHash.TryGetValue(key, out var keep))
                {
                    if (keep != t) remap[t] = keep;
                }
                else byHash[key] = t;
            }

            if (remap.Count == 0) return 0;
            RepointTextures(ctx, renderers, remap);
            log.Detail($"Output dedup: {remap.Count} duplicate textures collapsed");
            return remap.Count;
        }

        private static string TextureIdentity(Texture2D t)
        {
            try
            {
                var data = t.GetRawTextureData<byte>();
                var hash = new Hash128();
                hash.Append(data);
                return $"{t.width}x{t.height}:{t.format}:{t.mipmapCount}:{t.wrapMode}:{t.filterMode}:{t.anisoLevel}:{hash}";
            }
            catch
            {
                // EN: Raw data is unavailable for some formats; treat the texture as unique.
                // ZH: 某些格式无法取得原始数据；此时把该贴图视为唯一。
                return null;
            }
        }

        private static void RepointTextures(BuildContext ctx, IEnumerable<Renderer> renderers,
            Dictionary<Texture2D, Texture2D> remap)
        {
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || m.shader == null) continue;
                    var n = m.shader.GetPropertyCount();
                    for (int p = 0; p < n; p++)
                    {
                        if (m.shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;
                        var name = m.shader.GetPropertyName(p);
                        if (m.GetTexture(name) is Texture2D t && remap.TryGetValue(t, out var nt))
                        {
                            m.SetTexture(name, nt);
                            dirty = true;
                        }
                    }
                }
                if (dirty) r.sharedMaterials = mats;
            }

            var asc = ctx.Extension<AnimatorServicesContext>();
            asc.AnimationIndex.RewriteObjectCurves(obj =>
                obj is Texture2D t && remap.TryGetValue(t, out var nt) ? nt : obj);

            foreach (var dead in remap.Keys) Object.DestroyImmediate(dead, true);
        }

        /// <summary>EN: Collapse identical materials and optionally merge material slots. ZH: 合并相同材质，并在安全时合并材质槽。</summary>
        public static int DeduplicateMaterials(BuildContext ctx, IEnumerable<Renderer> renderers,
            AnimationFacts anim, ATOLog log)
        {
            var byKey = new Dictionary<string, Material>(StringComparer.Ordinal);
            var remap = new Dictionary<Material, Material>();
            var rendererList = renderers.ToList();

            foreach (var r in rendererList)
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || remap.ContainsKey(m)) continue;
                var key = MaterialIdentity(m);
                if (byKey.TryGetValue(key, out var keep)) { if (keep != m) remap[m] = keep; }
                else byKey[key] = m;
            }

            int collapsed = 0;
            foreach (var r in rendererList)
            {
                var mats = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && remap.TryGetValue(mats[i], out var keep)) { mats[i] = keep; dirty = true; collapsed++; }
                if (dirty) r.sharedMaterials = mats;
            }

            if (remap.Count > 0)
            {
                var asc = ctx.Extension<AnimatorServicesContext>();
                asc.AnimationIndex.RewriteObjectCurves(obj =>
                    obj is Material m && remap.TryGetValue(m, out var keep) ? keep : obj);
                log.Detail($"Output dedup: {remap.Count} duplicate materials collapsed ({collapsed} slot references updated)");
            }

            MergeSlots(ctx, rendererList, anim, log);
            return remap.Count;
        }

        private static string MaterialIdentity(Material m)
        {
            var sb = new StringBuilder();
            sb.Append(m.shader != null ? m.shader.name : "<null>").Append('|');
            sb.Append(m.renderQueue).Append('|');
            foreach (var kw in m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal)) sb.Append(kw).Append(',');
            sb.Append('|');

            if (m.shader != null)
            {
                int n = m.shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    var name = m.shader.GetPropertyName(i);
                    switch (m.shader.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Color: sb.Append(name).Append('=').Append(m.GetColor(name)); break;
                        case ShaderPropertyType.Vector: sb.Append(name).Append('=').Append(m.GetVector(name)); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range: sb.Append(name).Append('=').Append(m.GetFloat(name).ToString("R")); break;
                        case ShaderPropertyType.Int: sb.Append(name).Append('=').Append(m.GetInteger(name)); break;
                        case ShaderPropertyType.Texture:
                            var t = m.GetTexture(name);
                            sb.Append(name).Append('=').Append(t != null ? t.GetInstanceID().ToString() : "0")
                              .Append(m.GetTextureScale(name)).Append(m.GetTextureOffset(name));
                            break;
                    }
                    sb.Append(';');
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// EN: Merge duplicate adjacent material slots of one renderer into a single submesh.
        ///     Refused whenever any affected slot is individually animated, or the material is not
        ///     opaque, because slot order determines transparent draw order.
        /// ZH: 把同一渲染器上重复且相邻的材质槽合并为单个子网格。
        ///     只要任一受影响的槽被动画单独驱动，或材质不是不透明的，就拒绝合并，
        ///     因为槽顺序决定了透明物体的绘制顺序。
        /// </summary>
        private static void MergeSlots(BuildContext ctx, List<Renderer> renderers, AnimationFacts anim, ATOLog log)
        {
            var root = ctx.AvatarRootObject;
            var reindex = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);

            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                if (mats.Length < 2) continue;

                var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(root, r.gameObject);
                if (path != null && anim.IndividuallyAnimatedSlots.ContainsKey(path))
                {
                    log.Trace($"Slot merge skipped on '{path}': animation drives individual slots");
                    continue;
                }

                if (mats.Any(m => m == null || m.renderQueue >= (int)RenderQueue.AlphaTest))
                {
                    log.Trace($"Slot merge skipped on '{r.name}': non-opaque material present");
                    continue;
                }

                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : (r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
                if (mesh == null || mesh.subMeshCount != mats.Length) continue;

                var order = new List<Material>();
                var map = new Dictionary<int, int>();
                for (int i = 0; i < mats.Length; i++)
                {
                    int found = order.IndexOf(mats[i]);
                    if (found < 0) { order.Add(mats[i]); found = order.Count - 1; }
                    map[i] = found;
                }
                if (order.Count == mats.Length) continue;

                var clone = Object.Instantiate(mesh);
                clone.name = mesh.name + " (ATO merged)";
                var combined = new List<int>[order.Count];
                for (int i = 0; i < combined.Length; i++) combined[i] = new List<int>();
                for (int i = 0; i < mats.Length; i++) combined[map[i]].AddRange(mesh.GetTriangles(i));

                clone.subMeshCount = order.Count;
                for (int i = 0; i < order.Count; i++) clone.SetTriangles(combined[i], i);

                if (r is SkinnedMeshRenderer s2) s2.sharedMesh = clone;
                else if (r.TryGetComponent<MeshFilter>(out var mf2)) mf2.sharedMesh = clone;
                r.sharedMaterials = order.ToArray();

                if (path != null) reindex[path] = map;
                log.Detail($"Merged material slots on '{r.name}': {mats.Length} -> {order.Count}");
            }

            if (reindex.Count > 0) ReindexAnimations(ctx, reindex, log);
        }

        private static void ReindexAnimations(BuildContext ctx, Dictionary<string, Dictionary<int, int>> reindex, ATOLog log)
        {
            var asc = ctx.Extension<AnimatorServicesContext>();
            foreach (var controller in asc.ControllerContext.GetAllControllers())
            foreach (var clip in controller.AllReachableNodes().OfType<VirtualClip>())
            {
                foreach (var b in clip.GetObjectCurveBindings().ToList())
                {
                    if (!b.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal)) continue;
                    if (!reindex.TryGetValue(b.path, out var map)) continue;

                    int open = b.propertyName.IndexOf('[');
                    int close = b.propertyName.IndexOf(']', open + 1);
                    if (!int.TryParse(b.propertyName.Substring(open + 1, close - open - 1), out var old)) continue;
                    if (!map.TryGetValue(old, out var neu) || neu == old) continue;

                    var curve = clip.GetObjectCurve(b);
                    clip.SetObjectCurve(b, null);
                    var nb = new UnityEditor.EditorCurveBinding
                    {
                        path = b.path,
                        type = b.type,
                        propertyName = $"m_Materials.Array.data[{neu}]",
                    };
                    clip.SetObjectCurve(nb, curve);
                    log.Trace($"Reindexed animation binding '{b.path}' slot {old} -> {neu}");
                }
            }
        }
    }
}

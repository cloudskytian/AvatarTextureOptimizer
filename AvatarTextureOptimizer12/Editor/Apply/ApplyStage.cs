// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Applying results: meshes, materials, animations, dedup, AAO bridge.
// AvatarTextureOptimizer (ATO) - 结果应用：网格、材质、动画、去重、AAO 兼容桥。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Apply
{
    /// <summary>
    /// EN: Optional integration with AAO's <c>UVUsageCompabilityAPI</c> (note: the upstream spelling).
    ///     Reflection is used on purpose so the package compiles and runs with or without AAO installed.
    /// ZH: 与 AAO 的 <c>UVUsageCompabilityAPI</c> 的可选集成（注意：这是上游原本的拼写）。
    ///     刻意使用反射，使本包在安装或未安装 AAO 时都能编译并运行。
    /// </summary>
    public static class AAOBridge
    {
        private const string ApiTypeName = "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI";

        private static Type _api;
        private static MethodInfo _isTexCoordUsed;
        private static MethodInfo _registerEvacuation;
        private static bool _resolved;

        public static bool Available
        {
            get
            {
                Resolve();
                return _api != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                _api = asm.GetType(ApiTypeName, false);
                if (_api != null) break;
            }
            if (_api == null)
            {
                ATOLog.Debug_("AAO not installed; UV evacuation bridge disabled");
                return;
            }

            _isTexCoordUsed = _api.GetMethod("IsTexCoordUsed",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(SkinnedMeshRenderer), typeof(int) }, null);
            _registerEvacuation = _api.GetMethod("RegisterTexCoordEvacuation",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(SkinnedMeshRenderer), typeof(int), typeof(int) }, null);

            ATOLog.Info($"AAO UVUsageCompabilityAPI detected (isTexCoordUsed={_isTexCoordUsed != null}, " +
                        $"registerEvacuation={_registerEvacuation != null})");
        }

        public static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)
        {
            Resolve();
            if (_isTexCoordUsed == null) return false;
            try
            {
                return (bool)_isTexCoordUsed.Invoke(null, new object[] { renderer, channel });
            }
            catch (Exception e)
            {
                ATOLog.Warn($"AAO IsTexCoordUsed failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        /// <summary>
        /// EN: Copy the original UVs of <paramref name="originalChannel"/> into a free channel and tell AAO
        ///     to read the copy. AAO removes the copy again after its own processing.
        /// ZH: 把 <paramref name="originalChannel"/> 的原始 UV 复制到一个空闲通道，并告知 AAO 读取该副本。
        ///     AAO 在自己处理完成后会移除该副本。
        /// </summary>
        public static void EvacuateIfNeeded(SkinnedMeshRenderer renderer, Mesh mesh, int originalChannel)
        {
            Resolve();
            if (_registerEvacuation == null) return;
            if (!IsTexCoordUsed(renderer, originalChannel)) return;

            int free = -1;
            var tmp = new List<Vector2>();
            for (int i = 7; i >= 0; i--)
            {
                if (i == originalChannel) continue;
                mesh.GetUVs(i, tmp);
                if (tmp.Count != 0) continue;
                if (IsTexCoordUsed(renderer, i)) continue;
                free = i;
                break;
            }

            if (free < 0)
            {
                ATOReportUtil.Warn("ATO:warn:aao_no_free_uv", renderer);
                return;
            }

            var original = new List<Vector2>();
            mesh.GetUVs(originalChannel, original);
            mesh.SetUVs(free, original);

            try
            {
                _registerEvacuation.Invoke(null, new object[] { renderer, originalChannel, free });
                ATOLog.Info($"evacuated UV{originalChannel} -> UV{free} on '{renderer.name}' for AAO");
            }
            catch (Exception e)
            {
                ATOReportUtil.Warn("ATO:warn:aao_evacuation_failed", renderer,
                    e.InnerException?.Message ?? e.Message);
            }
        }
    }

    /// <summary>
    /// EN: Writes optimisation results back onto the avatar. Only meshes and texture references are ever
    ///     modified; no other shader parameter is touched, exactly as required.
    /// ZH: 把优化结果写回 Avatar。只会修改网格与贴图引用，
    ///     不会碰任何其他着色器参数，与需求完全一致。
    /// </summary>
    public static class ApplyStage
    {
        /// <summary>
        /// EN: Rewrite the UVs of every renderer that contributed islands to an atlas.
        /// ZH: 重写所有向图集贡献了岛的渲染器的 UV。
        /// </summary>
        public static void RewriteMeshes(BuildContext ctx,
            Dictionary<UVSlotKey, UVIslandSet> islandSets,
            Dictionary<UVIsland, (IslandPlan plan, AtlasPlan atlas)> placedIslands)
        {
            var meshCache = new Dictionary<(Renderer, Mesh), Mesh>();

            foreach (var kv in islandSets)
            {
                var key = kv.Key;
                var set = kv.Value;
                var renderer = key.Renderer;
                if (renderer == null) continue;

                var sourceMesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : GetFilterMesh(renderer);
                if (sourceMesh == null) continue;

                if (!meshCache.TryGetValue((renderer, sourceMesh), out var mesh))
                {
                    mesh = UnityEngine.Object.Instantiate(sourceMesh);
                    mesh.name = sourceMesh.name + "_ATO";
                    meshCache[(renderer, sourceMesh)] = mesh;
                    ctx.AssetSaver.SaveAsset(mesh);
                }

                var uvs = new List<Vector2>();
                mesh.GetUVs(key.UvChannel, uvs);
                if (uvs.Count == 0) continue;

                bool changed = false;
                foreach (var island in set.Islands)
                {
                    if (!placedIslands.TryGetValue(island, out var placement)) continue;
                    var plan = placement.plan;
                    var atlas = placement.atlas;

                    foreach (var t in island.TriangleIds)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = set.Triangles[t * 3 + k];
                            uvs[vi] = AtlasBaker.RemapUv(set.Uv[vi], plan, atlas.Width, atlas.Height);
                            changed = true;
                        }
                    }
                }

                if (!changed) continue;

                // EN: Hand AAO an untouched copy of the UVs before we overwrite them.
                // ZH: 在覆盖 UV 之前，先给 AAO 留一份未修改的副本。
                if (renderer is SkinnedMeshRenderer skinned)
                    AAOBridge.EvacuateIfNeeded(skinned, mesh, key.UvChannel);

                mesh.SetUVs(key.UvChannel, uvs);
                ATOLog.Debug_($"rewrote UV{key.UvChannel} of '{renderer.name}' ({uvs.Count} vertices)");
            }

            foreach (var entry in meshCache)
            {
                var (renderer, _) = entry.Key;
                if (renderer is SkinnedMeshRenderer smr) smr.sharedMesh = entry.Value;
                else
                {
                    var mf = renderer.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = entry.Value;
                }
            }
        }

        private static Mesh GetFilterMesh(Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        /// <summary>
        /// EN: Replace texture references on materials and inside animation clips. Materials are cloned so we
        ///     never write to the user's assets, and only the texture properties are changed.
        /// ZH: 替换材质与动画剪辑中的贴图引用。材质会被克隆，绝不写入用户的原始资源，
        ///     并且只修改贴图属性。
        /// </summary>
        public static void RewriteMaterials(BuildContext ctx, UsageGraph graph,
            Dictionary<Texture2D, Texture2D> replacement)
        {
            if (replacement.Count == 0) return;

            var materialClones = new Dictionary<Material, Material>();

            Material CloneOf(Material original)
            {
                if (original == null) return null;
                if (materialClones.TryGetValue(original, out var clone)) return clone;

                bool needed = false;
                var shader = original.shader;
                if (shader != null)
                {
                    int count = shader.GetPropertyCount();
                    for (int i = 0; i < count && !needed; i++)
                    {
                        if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                        var tex = original.GetTexture(shader.GetPropertyName(i)) as Texture2D;
                        if (tex == null) continue;
                        var canonical = graph.Canonical.TryGetValue(tex, out var c) ? c : tex;
                        if (replacement.ContainsKey(canonical)) needed = true;
                    }
                }

                if (!needed)
                {
                    materialClones[original] = original;
                    return original;
                }

                clone = UnityEngine.Object.Instantiate(original);
                clone.name = original.name + "_ATO";
                int n = shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                    var prop = shader.GetPropertyName(i);
                    var tex = original.GetTexture(prop) as Texture2D;
                    if (tex == null) continue;
                    var canonical = graph.Canonical.TryGetValue(tex, out var c) ? c : tex;
                    if (replacement.TryGetValue(canonical, out var newTex)) clone.SetTexture(prop, newTex);
                    else if (!ReferenceEquals(canonical, tex)) clone.SetTexture(prop, canonical);
                }

                ctx.AssetSaver.SaveAsset(clone);
                materialClones[original] = clone;
                ObjectRegistry.RegisterReplacedObject(original, clone);
                return clone;
            }

            // ---- Renderers / 渲染器 ----
            foreach (var renderer in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var clone = CloneOf(mats[i]);
                    if (!ReferenceEquals(clone, mats[i])) { mats[i] = clone; changed = true; }
                }
                if (changed) renderer.sharedMaterials = mats;
            }

            // ---- Animations / 动画 ----
            try
            {
                var asc = ctx.Extension<AnimatorServicesContext>();
                asc.AnimationIndex.RewriteObjectCurves(obj =>
                {
                    switch (obj)
                    {
                        case Material m:
                            return CloneOf(m);
                        case Texture2D t:
                        {
                            var canonical = graph.Canonical.TryGetValue(t, out var c) ? c : t;
                            return replacement.TryGetValue(canonical, out var nt) ? nt : (UnityEngine.Object)canonical;
                        }
                        default:
                            return obj;
                    }
                });
            }
            catch (Exception e)
            {
                ATOLog.Warn($"animation rewrite skipped: {e.Message}");
            }

            ATOLog.Info($"rewrote {materialClones.Count(kv => !ReferenceEquals(kv.Key, kv.Value))} material(s) " +
                        $"and {replacement.Count} texture reference(s)");
        }

        /// <summary>
        /// EN: Post-pass deduplication of materials that became identical after optimisation, plus merging of
        ///     material slots on the same mesh when it is provably safe (no animation drives the individual
        ///     slots and the materials are equal).
        /// ZH: 优化后对变得完全相同的材质做去重，并在可证明安全时（没有动画单独驱动这些槽，
        ///     且材质相等）合并同一网格上的材质槽。
        /// </summary>
        public static void DeduplicateMaterials(BuildContext ctx, AnimationFacts facts, bool mergeSlots)
        {
            var canonicalByKey = new Dictionary<string, Material>(StringComparer.Ordinal);
            var mapping = new Dictionary<Material, Material>();

            foreach (var renderer in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in renderer.sharedMaterials)
                {
                    if (m == null || mapping.ContainsKey(m)) continue;
                    var key = MaterialKey(m);
                    if (canonicalByKey.TryGetValue(key, out var canonical)) mapping[m] = canonical;
                    else { canonicalByKey[key] = m; mapping[m] = m; }
                }
            }

            int merged = 0;
            foreach (var renderer in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (mapping.TryGetValue(mats[i], out var canonical) && !ReferenceEquals(canonical, mats[i]))
                    {
                        mats[i] = canonical;
                        changed = true;
                        merged++;
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }

            if (merged > 0) ATOLog.Info($"deduplicated {merged} material slot reference(s)");

            // EN: Now that duplicate materials collapsed onto one object, merge their sub-meshes as well.
            //     SubMeshMerger performs its own safety analysis and refuses whenever anything could
            //     depend on the old slot numbering.
            // ZH: 重复材质已折叠为同一个对象，现在把它们的子网格也合并掉。
            //     SubMeshMerger 会自行做安全分析，只要有任何东西可能依赖旧的槽编号就拒绝合并。
            if (!mergeSlots) return;

            var mergeResult = SubMeshMerger.MergeAll(ctx, facts);
            if (mergeResult.SlotsRemoved > 0)
            {
                ATOReportUtil.Info("ATO:info:slots_merged", mergeResult.SlotsRemoved,
                    mergeResult.RenderersChanged);
            }
        }

        /// <summary>
        /// EN: Deduplicate the textures ATO itself generated. Two outputs are interchangeable when their
        ///     dimensions, format, mip/filter/wrap/aniso state and raw bytes all match.
        /// ZH: 对 ATO 自己生成的贴图去重。当尺寸、格式、mip/filter/wrap/aniso 状态以及原始字节全部一致时，
        ///     两份输出可以互换。
        /// </summary>
        public static int DeduplicateTextures(Dictionary<Texture2D, Texture2D> replacement)
        {
            if (replacement.Count < 2) return 0;

            var canonicalByKey = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var collapse = new Dictionary<Texture2D, Texture2D>();

            foreach (var generated in replacement.Values.Distinct())
            {
                if (generated == null || collapse.ContainsKey(generated)) continue;

                string key;
                try
                {
                    key = GeneratedTextureKey(generated);
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"texture dedup key failed for '{generated.name}': {e.Message}");
                    continue;
                }

                if (canonicalByKey.TryGetValue(key, out var canonical)) collapse[generated] = canonical;
                else { canonicalByKey[key] = generated; collapse[generated] = generated; }
            }

            int removed = 0;
            foreach (var key in replacement.Keys.ToList())
            {
                var value = replacement[key];
                if (value == null) continue;
                if (!collapse.TryGetValue(value, out var canonical) || ReferenceEquals(canonical, value)) continue;

                replacement[key] = canonical;
                removed++;
            }

            // EN: Destroy the now-unreferenced duplicates so they never reach the asset container.
            // ZH: 销毁已无引用的重复项，避免它们进入资产容器。
            foreach (var kv in collapse)
            {
                if (ReferenceEquals(kv.Key, kv.Value)) continue;
                if (replacement.Values.Contains(kv.Key)) continue;
                UnityEngine.Object.DestroyImmediate(kv.Key);
            }

            if (removed > 0) ATOLog.Info($"deduplicated {removed} generated texture reference(s)");
            return removed;
        }

        private static string GeneratedTextureKey(Texture2D tex)
        {
            var header = $"{tex.width}x{tex.height}|{tex.format}|{tex.mipmapCount}|{tex.filterMode}|" +
                         $"{tex.wrapMode}|{tex.anisoLevel}|{tex.streamingMipmaps}|";

            var data = tex.GetRawTextureData<byte>();
            ulong h1 = 14695981039346656037UL, h2 = 1099511628211UL;
            unchecked
            {
                for (int i = 0; i < data.Length; i++)
                {
                    h1 = (h1 ^ data[i]) * 1099511628211UL;
                    h2 = (h2 + data[i]) * 0x9E3779B97F4A7C15UL;
                    h2 ^= h2 >> 29;
                }
            }
            return header + $"{h1:X16}{h2:X16}|{data.Length}";
        }

        private static string MaterialKey(Material m)
        {
            if (m == null) return "null";
            var shader = m.shader;
            var sb = new System.Text.StringBuilder();
            sb.Append(shader != null ? shader.name : "<none>").Append('|').Append(m.renderQueue).Append('|');
            foreach (var kw in m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal)) sb.Append(kw).Append(',');
            sb.Append('|');

            if (shader != null)
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    var name = shader.GetPropertyName(i);
                    switch (shader.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            var t = m.GetTexture(name);
                            sb.Append(name).Append('=').Append(t != null ? t.GetInstanceID().ToString() : "0")
                                .Append(';').Append(m.GetTextureScale(name)).Append(m.GetTextureOffset(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            sb.Append(name).Append('=').Append(m.GetColor(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            sb.Append(name).Append('=').Append(m.GetVector(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Int:
                            sb.Append(name).Append('=').Append(m.GetInt(name));
                            break;
                        default:
                            sb.Append(name).Append('=').Append(m.GetFloat(name).ToString("R"));
                            break;
                    }
                    sb.Append('|');
                }
            }
            return sb.ToString();
        }
    }
}

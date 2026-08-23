using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Builds material variants that point at the atlases (only texture references change — no
    /// other shader parameter is ever modified), rewrites renderer slots and animation curves
    /// (material swaps and texture swaps), then performs post-optimization dedup of identical
    /// materials/textures and merges identical opaque material slots when animations allow it.
    /// / 构建指向图集的材质变体（仅修改贴图引用，绝不改其他着色器参数），重写渲染器槽位与动画曲线，
    /// 最后进行材质/贴图去重与可判定安全的材质槽合并。
    /// </summary>
    internal class MaterialRewriter
    {
        // inputs / 输入
        private readonly Dictionary<string, RendererInfo> _renderersByPath;
        private readonly Dictionary<(Mesh mesh, int channel), UvGroup> _groups;
        private readonly Dictionary<(UvGroup, Texture2D), BuiltAtlas> _atlasOf;
        private readonly Dictionary<Texture2D, Texture2D> _dedupMap;
        private readonly WholeTextureOptimizer _whole;
        private readonly AtoSettings _settings;

        // state / 状态
        private readonly GameObject _root;
        private readonly Dictionary<(Material, Mesh), Material> _variants =
            new Dictionary<(Material, Mesh), Material>();

        internal readonly List<Material> GeneratedMaterials = new List<Material>();
        /// <summary>Meshes created by slot merging (for asset saving). / 槽位合并产生的网格。</summary>
        internal readonly List<Mesh> GeneratedMeshes = new List<Mesh>();
        internal readonly List<(string from, string to)> MergeLog = new List<(string, string)>();

        internal MaterialRewriter(GameObject root, Dictionary<string, RendererInfo> renderersByPath,
            Dictionary<(Mesh, int), UvGroup> groups,
            Dictionary<(UvGroup, Texture2D), BuiltAtlas> atlasOf,
            Dictionary<Texture2D, Texture2D> dedupMap,
            WholeTextureOptimizer whole,
            AtoSettings settings)
        {
            _root = root;
            _renderersByPath = renderersByPath;
            _groups = groups;
            _atlasOf = atlasOf;
            _dedupMap = dedupMap;
            _whole = whole;
            _settings = settings;
        }

        /// <summary>Resolve a texture to its final asset (dedup → atlas / whole-texture / original). / 解析贴图最终资产。</summary>
        internal Texture ResolveTexture(Texture2D tex, UvGroup group, TexCategory cat)
        {
            if (tex == null) return null;
            tex = _dedupMap.TryGetValue(tex, out var canon) ? canon : tex;

            if (group != null && group.atlasEligible &&
                _atlasOf.TryGetValue((group, tex), out var atlas))
                return atlas.Texture;

            if (_whole.Replacements.TryGetValue(tex, out var replaced)) return replaced;
            return tex;
        }

        /// <summary>Build (or reuse) the variant of `mat` for a given mesh context. / 为材质构建（或复用）某网格上下文的变体。</summary>
        internal Material Variant(Material mat, Mesh mesh)
        {
            if (mat == null) return null;
            var key = (mat, mesh);
            if (_variants.TryGetValue(key, out var cached)) return cached;

            var variant = Object.Instantiate(mat);
            variant.name = mat.name + "_ATO";
            GeneratedMaterials.Add(variant);

            foreach (var slot in ShaderAnalyzer.Analyze(mat).slots)
            {
                var tex = mat.GetTexture(slot.property) as Texture2D;
                if (tex == null) continue;
                _groups.TryGetValue((mesh, slot.uvChannel), out var group);
                var resolved = ResolveTexture(tex, group, slot.category);
                if (!ReferenceEquals(resolved, tex))
                    variant.SetTexture(slot.property, resolved);
            }

            _variants[key] = variant;
            return variant;
        }

        /// <summary>Apply variants to renderer slots and rewrite all animations. / 应用变体并重写全部动画。</summary>
        internal void Apply(AnimatorServicesContext asc)
        {
            // ---- renderer slots / 渲染器槽位 ----
            foreach (var kv in _renderersByPath)
            {
                var info = kv.Value;
                if (info.renderer == null) continue;
                var slots = info.renderer.sharedMaterials;
                var result = new Material[slots.Length];
                bool changed = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    var m = slots[i];
                    if (m == null) { result[i] = null; continue; }
                    result[i] = Variant(m, info.mesh);
                    changed |= !ReferenceEquals(result[i], m);
                }
                if (changed) info.renderer.sharedMaterials = result;
            }

            // ---- animations: material &amp; texture swaps / 动画：材质与贴图切换 ----
            asc.AnimationIndex.RewriteObjectCurves((binding, obj) =>
            {
                if (obj is Material mat)
                {
                    if (!binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                        return obj;
                    if (!_renderersByPath.TryGetValue(binding.path, out var info)) return obj;
                    return Variant(mat, info.mesh);
                }

                if (obj is Texture2D tex &&
                    binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                {
                    if (!_renderersByPath.TryGetValue(binding.path, out var info)) return obj;
                    var prop = binding.propertyName.Substring("material.".Length);
                    var bracket = prop.IndexOf(']');
                    if (prop.StartsWith("[") && bracket > 0) prop = prop.Substring(bracket + 2);

                    // find the UV channel used by this property on this renderer's materials
                    // 找到该属性在该渲染器材质上的UV通道
                    int channel = 0;
                    bool found = false;
                    foreach (var m in info.slots)
                    {
                        if (m == null) continue;
                        var slot = ShaderAnalyzer.Analyze(m).slots.FirstOrDefault(s => s.property == prop);
                        if (slot != null) { channel = slot.uvChannel; found = true; break; }
                    }
                    if (!found || channel < 0) return obj;

                    _groups.TryGetValue((info.mesh, channel), out var group);
                    return ResolveTexture(tex, group, TexCategory.Color);
                }

                return obj;
            });
        }

        // ------------------------------------------------------------------ post dedup
        /// <summary>
        /// Deduplicate identical generated materials/textures and merge identical opaque material
        /// slots (only when the renderer has no m_Materials animations at all — conservative).
        /// / 去重完全相同的生成材质/贴图；仅在渲染器完全没有材质槽动画时合并相同不透明材质槽（保守）。
        /// </summary>
        internal void PostDedup(AnimatorServicesContext asc, List<RendererInfo> renderers)
        {
            if (!_settings.dedupMaterials) return;

            // ---- material dedup by full serialized content / 按序列化内容去重材质 ----
            var byHash = new Dictionary<string, Material>();
            var matMap = new Dictionary<Material, Material>();
            foreach (var m in GeneratedMaterials)
            {
                var hash = HashMaterial(m);
                if (byHash.TryGetValue(hash, out var canonical))
                {
                    matMap[m] = canonical;
                    MergeLog.Add((m.name, canonical.name));
                }
                else byHash[hash] = m;
            }

            if (matMap.Count > 0)
            {
                foreach (var info in renderers)
                {
                    if (info.renderer == null) continue;
                    var slots = info.renderer.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i] != null && matMap.TryGetValue(slots[i], out var canonical))
                        {
                            slots[i] = canonical;
                            changed = true;
                        }
                    if (changed) info.renderer.sharedMaterials = slots;
                }

                asc.AnimationIndex.RewriteObjectCurves((b, obj) =>
                    obj is Material m && matMap.TryGetValue(m, out var canonical) ? canonical : obj);
                ATOLog.Info($"material dedup: {matMap.Count} duplicates merged / 材质去重合并 {matMap.Count} 个");
            }

            // ---- slot merging / 材质槽合并 ----
            foreach (var info in renderers)
            {
                if (info.renderer == null || info.mesh == null) continue;
                if (info.slotAnimated != null && info.slotAnimated.Any(a => a)) continue;
                if (info.slotSwapMaterials.Count > 0) continue;

                var slots = info.renderer.sharedMaterials;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] == null) continue;
                    if (!IsOpaque(slots[i])) continue;
                    for (int j = i + 1; j < slots.Length; j++)
                    {
                        if (slots[j] == null || !ReferenceEquals(slots[i], slots[j])) continue;
                        if (j >= info.mesh.subMeshCount || i >= info.mesh.subMeshCount) continue;

                        var mergedMesh = MeshRewriter.MergeSlots(info.mesh, i, j);
                        GeneratedMeshes.Add(mergedMesh);
                        ApplyMesh(info, mergedMesh);

                        var newSlots = slots.Where((_, idx) => idx != j).ToArray();
                        info.renderer.sharedMaterials = newSlots;
                        ShiftAnimationSlots(asc, info, j);
                        slots = newSlots;
                        ATOLog.Info($"slot merge: '{info.renderer.name}' slot {j} → {i} / 材质槽合并");
                        j--; // re-scan this index / 重新扫描
                        break;
                    }
                }
            }
        }

        private static void ApplyMesh(RendererInfo info, Mesh mesh)
        {
            if (info.smr != null) info.smr.sharedMesh = mesh;
            else
            {
                var mf = info.renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = mesh;
            }
            info.mesh = mesh;
        }

        private static bool IsOpaque(Material m)
        {
            if (m.renderQueue >= 2450) return false;
            if (m.HasProperty("_Mode") && m.GetFloat("_Mode") > 0f) return false;
            return true;
        }

        /// <summary>Shift m_Materials slot indices above `removed` down by one on this renderer's path. / 将高于被删槽位的动画索引前移。</summary>
        private static void ShiftAnimationSlots(AnimatorServicesContext asc, RendererInfo info, int removed)
        {
            var path = RelativePath(info.renderer);
            var index = asc.AnimationIndex;
            foreach (var clip in index.ClipsWithObjectCurves.ToList())
            {
                foreach (var binding in clip.GetObjectCurveBindings().ToList())
                {
                    if (binding.path != path || !binding.propertyName.StartsWith("m_Materials.Array.data["))
                        continue;
                    var numStr = binding.propertyName.Substring("m_Materials.Array.data[".Length)
                        .TrimEnd(']');
                    if (!int.TryParse(numStr, out var idx) || idx <= removed) continue;

                    var keys = clip.GetObjectCurve(binding);
                    if (keys == null) continue;
                    var nb = EditorCurveBinding.PPtrCurve(binding.path, binding.type,
                        $"m_Materials.Array.data[{idx - 1}]");
                    clip.SetObjectCurve(binding, null);
                    clip.SetObjectCurve(nb, keys);
                }
            }
        }

        private string RelativePath(Renderer r)
        {
            var names = new List<string>();
            var t = r.transform;
            var rootT = _root.transform;
            while (t != null && t != rootT)
            {
                names.Add(t.name);
                t = t.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        internal static string HashMaterial(Material m)
        {
            var so = new SerializedObject(m);
            var sb = new StringBuilder();
            var prop = so.GetIterator();
            while (prop.NextVisible(true))
            {
                switch (prop.type)
                {
                    case "m_Shader":
                        sb.Append(prop.objectReferenceInstanceIDValue);
                        break;
                    default:
                        sb.Append(prop.name).Append('=');
                        if (prop.propertyType == SerializedPropertyType.ObjectReference)
                            sb.Append(prop.objectReferenceInstanceIDValue);
                        else
                            sb.Append(prop.AsStringValue());
                        break;
                }
                sb.Append(';');
            }

            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }
}

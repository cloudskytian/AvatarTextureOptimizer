// ATO — Avatar Texture Optimizer
// Whitelist resolution: a whitelist entry may be a mesh, material, texture, animation or
// GameObject/component. Every texture referenced by a whitelisted object is skipped from
// all optimizations (including parameter optimization). Whitelist "contamination" also
// propagates through texture dedup.
// 白名单解析：白名单条目可以是网格、材质、贴图、动画或 GameObject/组件。
// 白名单对象引用的全部贴图都跳过所有优化（包括参数优化）。白名单"污染"也会经由贴图去重传播。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Resolves whitelist entries into a set of whitelisted textures + usages.
    /// 将白名单条目解析为白名单贴图集合 + 用途。
    /// </summary>
    public static class WhitelistResolver
    {
        private static readonly HashSet<Texture2D> WhitelistedTextures = new HashSet<Texture2D>();
        private static readonly HashSet<Material> WhitelistedMaterials = new HashSet<Material>();

        public static void Resolve(AvatarTextureOptimizer component, ATOAnalysisResult result)
        {
            WhitelistedTextures.Clear();
            WhitelistedMaterials.Clear();
            if (component == null) return;

            var entries = component.whitelist;
            if (entries == null) return;

            foreach (var entry in entries)
            {
                if (entry == null) continue;
                ProcessEntry(entry, result);
            }

            // Apply to all usages. 应用到全部用途。
            foreach (var usage in result.allUsages)
            {
                if (usage.texture == null) continue;
                if (WhitelistedTextures.Contains(usage.texture) ||
                    (usage.material != null && WhitelistedMaterials.Contains(usage.material)))
                {
                    usage.whitelisted = true;
                }
            }
        }

        private static void ProcessEntry(Object entry, ATOAnalysisResult result)
        {
            switch (entry)
            {
                case Texture2D tex:
                    WhitelistedTextures.Add(tex);
                    ATOLog.Verbose($"{ATOI18n.T(ATOI18nKeys.WarnWhitelistSkip, tex.name)}");
                    break;

                case Material mat:
                    WhitelistedMaterials.Add(mat);
                    foreach (var t in EnumerateMaterialTextures(mat)) WhitelistedTextures.Add(t);
                    break;

                case Mesh mesh:
                    // A whitelisted mesh: every texture sampled on renderers using that mesh
                    // is skipped from ALL optimization (global). 白名单网格：使用该网格的渲染器所采样的
                    // 全部贴图跳过所有优化（全局）。
                    foreach (var u in result.allUsages)
                    {
                        if (u.renderer != null && RendererUsesMesh(u.renderer, mesh))
                        {
                            u.whitelisted = true;
                            if (u.texture != null) WhitelistedTextures.Add(u.texture);
                            if (u.material != null) WhitelistedMaterials.Add(u.material);
                        }
                    }
                    break;

                case AnimationClip clip:
                    foreach (var t in EnumerateClipTextures(clip)) WhitelistedTextures.Add(t);
                    break;

                case GameObject go:
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null) continue;
                            WhitelistedMaterials.Add(mat);
                            foreach (var t in EnumerateMaterialTextures(mat)) WhitelistedTextures.Add(t);
                        }
                    }
                    break;

                case Component comp:
                    if (comp is Renderer r)
                    {
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null) continue;
                            WhitelistedMaterials.Add(mat);
                            foreach (var t in EnumerateMaterialTextures(mat)) WhitelistedTextures.Add(t);
                        }
                    }
                    else
                    {
                        foreach (var t in EnumerateComponentTextures(comp)) WhitelistedTextures.Add(t);
                    }
                    break;

                default:
                    ATOLog.Verbose($"[Whitelist] Entry of type {entry.GetType().Name} handled conservatively.");
                    break;
            }
        }

        private static bool RendererUsesMesh(Renderer r, Mesh mesh)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh == mesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf != null && mf.sharedMesh == mesh;
                default: return false;
            }
        }

        private static IEnumerable<Texture2D> EnumerateMaterialTextures(Material mat)
        {
            var seen = new HashSet<Texture2D>();
            if (mat == null) yield break;
            var shader = mat.shader;
            if (shader == null) yield break;
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var t = mat.GetTexture(ShaderUtil.GetPropertyName(shader, i)) as Texture2D;
                if (t != null && seen.Add(t)) yield return t;
            }
        }

        private static IEnumerable<Texture2D> EnumerateComponentTextures(Component comp)
        {
            var seen = new HashSet<Texture2D>();
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();
            while (prop.NextVisible(true))
            {
                if (prop.propertyType == SerializedPropertyType.ObjectReference &&
                    prop.objectReferenceValue is Texture2D t && seen.Add(t))
                    yield return t;
            }
        }

        private static IEnumerable<Texture2D> EnumerateClipTextures(AnimationClip clip)
        {
            var seen = new HashSet<Texture2D>();
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null) continue;
                foreach (var k in curve)
                {
                    if (k.value is Texture2D t && t != null && seen.Add(t)) yield return t;
                }
            }
        }
    }
}

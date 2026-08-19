// WhitelistExpander.cs
// Expands arbitrary whitelist objects (any type) into the set of textures they reference.
// 将任意类型的白名单对象展开为其引用的全部贴图集合。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    internal static class WhitelistExpander
    {
        /// <summary>
        /// Expand one whitelist object into textures (recursive, cycle-safe).
        /// / 将一个白名单对象递归展开为贴图集合(防循环)。
        /// </summary>
        internal static IEnumerable<Texture2D> Expand(UnityEngine.Object obj, BuildContext ctx, List<RendererRecord> renderers)
        {
            var set = new HashSet<Texture2D>();
            var visited = new HashSet<UnityEngine.Object>();
            ExpandInto(obj, ctx, renderers, set, visited, depth: 0);
            return set;
        }

        private static void ExpandInto(UnityEngine.Object obj, BuildContext ctx, List<RendererRecord> renderers,
            HashSet<Texture2D> into, HashSet<UnityEngine.Object> visited, int depth)
        {
            if (obj == null || depth > 6 || !visited.Add(obj)) return;

            switch (obj)
            {
                case Texture2D t2:
                    into.Add(t2);
                    return;
                case Texture t: // cubemap, 3D etc: not our target / 立方体等非目标
                    return;
                case Material mat:
                    foreach (var tex in EnumerateMaterialTextures(mat)) into.Add(tex);
                    return;
                case Renderer r:
                    foreach (var m in r.sharedMaterials)
                        if (m != null)
                            foreach (var tex in EnumerateMaterialTextures(m)) into.Add(tex);
                    return;
                case GameObject go:
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        ExpandInto(r, ctx, renderers, into, visited, depth + 1);
                    foreach (var m in go.GetComponentsInChildren<AnimationClip>(true))
                        ExpandInto(m, ctx, renderers, into, visited, depth + 1);
                    return;
                case AnimationClip clip:
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, b))
                            ExpandInto(kf.value, ctx, renderers, into, visited, depth + 1);
                    return;
                case Mesh mesh:
                    // Textures of renderers using this mesh / 使用该网格的渲染器的贴图
                    foreach (var rec in renderers)
                        if (rec.Mesh == mesh)
                            foreach (var kv in rec.SlotMaterials)
                                foreach (var m in kv.Value)
                                    foreach (var tex in EnumerateMaterialTextures(m)) into.Add(tex);
                    return;
                default:
                    // Unknown type: try its inspector-visible texture fields via SerializedObject. / 未知类型:经 SerializedObject 枚举纹理字段。
                    var so = new SerializedObject(obj);
                    var p = so.GetIterator();
                    bool enterChildren = true;
                    while (p.Next(enterChildren))
                    {
                        enterChildren = p.propertyType != SerializedPropertyType.ObjectReference;
                        if (p.propertyType == SerializedPropertyType.ObjectReference &&
                            p.objectReferenceValue is Texture2D t2)
                            into.Add(t2);
                    }
                    return;
            }
        }

        internal static IEnumerable<Texture2D> EnumerateMaterialTextures(Material mat)
        {
            if (mat == null || mat.shader == null) yield break;
            var sh = mat.shader;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(sh); i++)
            {
                if (ShaderUtil.GetPropertyType(sh, i) != ShaderPropertyType.Texture) continue;
                var tex = mat.GetTexture(ShaderUtil.GetPropertyName(sh, i)) as Texture2D;
                if (tex != null) yield return tex;
            }
        }
    }
}

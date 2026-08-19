// English: Expand the user whitelist (any Object type) to the Texture2D set they reference.
// 中文：把用户白名单（任意对象类型）展开为它们引用的 Texture2D 集合。
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOWhitelist
    {
        public static void Collect(ATOState state)
        {
            var comp = state.Component;
            if (comp.whitelist != null)
            {
                foreach (var o in comp.whitelist)
                {
                    if (o != null) state.WhitelistObjects.Add(o);
                }
            }

            foreach (var o in state.WhitelistObjects)
            {
                CollectFrom(o, state.WhitelistTextures, 0);
            }

            state.Log.Info("whitelist objects=" + state.WhitelistObjects.Count +
                           " textures=" + state.WhitelistTextures.Count);
            state.Report.TexturesWhitelisted = state.WhitelistTextures.Count;
        }

        public static void CollectFrom(Object obj, HashSet<Texture2D> into, int depth)
        {
            if (obj == null || depth > 4 || into == null) return;
            var tex = obj as Texture2D;
            if (tex != null)
            {
                into.Add(tex);
                return;
            }

            var mat = obj as Material;
            if (mat != null)
            {
                CollectMaterial(mat, into);
                return;
            }

            var renderer = obj as Renderer;
            if (renderer != null)
            {
                var mats = renderer.sharedMaterials;
                if (mats != null)
                {
                    for (var i = 0; i < mats.Length; i++) CollectMaterial(mats[i], into);
                }

                return;
            }

            var mf = obj as MeshFilter;
            if (mf != null) return;

            var go = obj as GameObject;
            if (go != null)
            {
                var rs = go.GetComponentsInChildren<Renderer>(true);
                foreach (var r in rs)
                {
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;
                    for (var i = 0; i < mats.Length; i++) CollectMaterial(mats[i], into);
                }

                return;
            }

            var clip = obj as AnimationClip;
            if (clip != null)
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys == null) continue;
                    foreach (var k in keys)
                    {
                        CollectFrom(k.value, into, depth + 1);
                    }
                }
            }
        }

        private static void CollectMaterial(Material mat, HashSet<Texture2D> into)
        {
            if (mat == null || mat.shader == null) return;
            var count = ShaderUtil.GetPropertyCount(mat.shader);
            for (var i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var n = ShaderUtil.GetPropertyName(mat.shader, i);
                var t = mat.GetTexture(n) as Texture2D;
                if (t != null) into.Add(t);
            }
        }
    }
}

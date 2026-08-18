using System.Collections.Generic;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoWhitelist
    {
        public static HashSet<Texture2D> Collect(GameObject root, AvatarTextureOptimizerComponent comp)
        {
            var set = new HashSet<Texture2D>();
            if (comp.whitelist == null) return set;
            foreach (var r in comp.whitelist)
            {
                if (r == null || r.target == null) continue;
                CollectFrom(r.target, set);
            }
            AtoLog.VerboseInfo($"whitelist textures={set.Count}");
            return set;
        }

        public static void CollectFrom(Object obj, HashSet<Texture2D> set)
        {
            if (obj == null) return;
            if (obj is Texture2D t) { set.Add(t); return; }
            if (obj is Material m)
            {
                CollectMaterial(m, set);
                return;
            }
            if (obj is Renderer rend)
            {
                foreach (var mat in rend.sharedMaterials)
                    CollectMaterial(mat, set);
                return;
            }
            if (obj is GameObject go)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in r.sharedMaterials)
                    CollectMaterial(mat, set);
                foreach (var anim in go.GetComponentsInChildren<Animator>(true))
                    CollectFrom(anim.runtimeAnimatorController, set);
                return;
            }
            if (obj is RuntimeAnimatorController ctrl)
            {
                foreach (var clip in ctrl.animationClips)
                    CollectFrom(clip, set);
                return;
            }
            if (obj is AnimationClip clip)
            {
                foreach (var binding in UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    foreach (var key in UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, binding))
                    {
                        if (key.value is Texture2D tex) set.Add(tex);
                        if (key.value is Material mat) CollectMaterial(mat, set);
                    }
                }
            }
        }

        static void CollectMaterial(Material m, HashSet<Texture2D> set)
        {
            if (m == null) return;
            var shader = m.shader;
            if (shader == null) return;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var tex = m.GetTexture(shader.GetPropertyNameId(i)) as Texture2D;
                if (tex != null) set.Add(tex);
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class WhitelistExpander
    {
        public static HashSet<Texture> Expand(IEnumerable<Object> roots)
        {
            var set = new HashSet<Texture>();
            if (roots == null) return set;
            foreach (var o in roots)
            {
                if (o == null) continue;
                if (o is Texture t) set.Add(t);
                if (o is Material m) AddMaterial(m, set);
                if (o is Renderer r)
                {
                    foreach (var mat in r.sharedMaterials)
                        AddMaterial(mat, set);
                }
                if (o is MeshFilter mf && mf.sharedMesh != null) { /* mesh only: no tex */ }
                if (o is AnimationClip clip)
                {
                    foreach (var binding in UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        foreach (var k in UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, binding))
                        {
                            if (k.value is Texture tx) set.Add(tx);
                            if (k.value is Material mat) AddMaterial(mat, set);
                        }
                    }
                }
                if (o is GameObject go)
                {
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    foreach (var mat in r.sharedMaterials)
                        AddMaterial(mat, set);
                }
            }
            AtoLog.Info($"Whitelist textures={set.Count}");
            return set;
        }

        static void AddMaterial(Material m, HashSet<Texture> set)
        {
            if (m == null) return;
            var so = new UnityEditor.SerializedObject(m);
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType == UnityEditor.SerializedPropertyType.ObjectReference && it.objectReferenceValue is Texture t)
                    set.Add(t);
            }
        }
    }
}

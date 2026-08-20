using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Collects every Texture2D referenced by whitelist objects of any type.
    /// 收集白名单对象（任意类型）引用到的全部 Texture2D。
    /// </summary>
    public static class WhitelistCollector
    {
        public static HashSet<Texture2D> Collect(GameObject avatarRoot, List<Object> whitelist, out HashSet<Object> objs)
        {
            objs = new HashSet<Object>();
            var tex = new HashSet<Texture2D>();
            if (whitelist == null) return tex;
            var queue = new Queue<Object>();
            foreach (var o in whitelist)
            {
                if (o == null) continue;
                queue.Enqueue(o);
                objs.Add(o);
            }

            int guard = 0;
            while (queue.Count > 0 && guard++ < 100000)
            {
                var o = queue.Dequeue();
                if (o is Texture2D t) tex.Add(t);
                if (o is Material mat)
                {
                    var sh = mat.shader;
                    if (sh != null)
                    {
                        int n = sh.GetPropertyCount();
                        for (int i = 0; i < n; i++)
                        {
                            if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                            if (mat.GetTexture(sh.GetPropertyName(i)) is Texture2D tt) tex.Add(tt);
                        }
                    }
                }
                if (o is Renderer r)
                {
                    foreach (var m in r.sharedMaterials) if (m != null && objs.Add(m)) queue.Enqueue(m);
                    Mesh mesh = null;
                    if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                    else
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                    }
                    if (mesh != null) objs.Add(mesh);
                }
                if (o is GameObject go)
                {
                    foreach (var c in go.GetComponentsInChildren<Component>(true))
                        if (c != null && objs.Add(c)) queue.Enqueue(c);
                }
                if (o is AnimationClip clip)
                {
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                        if (keys == null) continue;
                        foreach (var k in keys)
                            if (k.value != null && objs.Add(k.value)) queue.Enqueue(k.value);
                    }
                }
            }
            return tex;
        }
    }
}

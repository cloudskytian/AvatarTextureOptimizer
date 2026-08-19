using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Collects every Texture2D referenced by whitelist objects (any type).
    /// 收集白名单对象（任意类型）引用到的全部 Texture2D。
    /// </summary>
    public static class WhitelistResolver
    {
        public static void Collect(AtoSession session)
        {
            var set = session.WhitelistTextures;
            var objs = session.WhitelistObjects;
            objs.Clear();
            if (session.Component.whitelist == null) return;

            foreach (var o in session.Component.whitelist)
            {
                if (o == null) continue;
                objs.Add(o);
                AddFrom(o, set, session.Log);
            }

            session.Log.Info("Whitelist objects=" + objs.Count + " textures=" + set.Count);
        }

        public static void AddFrom(Object o, HashSet<Texture2D> set, AtoLog log)
        {
            if (o == null) return;
            if (o is Texture2D t)
            {
                set.Add(t);
                return;
            }

            if (o is Material mat)
            {
                AddMaterial(mat, set);
                return;
            }

            if (o is Renderer r)
            {
                foreach (var m in r.sharedMaterials) AddMaterial(m, set);
                return;
            }

            if (o is GameObject go)
            {
                foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
                foreach (var m in rend.sharedMaterials)
                    AddMaterial(m, set);
                foreach (var anim in go.GetComponentsInChildren<Animator>(true))
                    AddFrom(anim.runtimeAnimatorController, set, log);
                return;
            }

            if (o is AnimationClip clip)
            {
                AddClip(clip, set);
                return;
            }

            if (o is RuntimeAnimatorController ctrl)
            {
                foreach (var c in ctrl.animationClips) AddClip(c, set);
                return;
            }

            // SerializedObject walk for unknown types. / 未知类型走序列化字段。
            try
            {
                var so = new SerializedObject(o);
                var it = so.GetIterator();
                var enter = true;
                while (it.Next(enter))
                {
                    enter = true;
                    if (it.propertyType == SerializedPropertyType.ObjectReference &&
                        it.objectReferenceValue is Texture2D tex)
                    {
                        set.Add(tex);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        static void AddMaterial(Material mat, HashSet<Texture2D> set)
        {
            if (mat == null) return;
            string[] names;
            try { names = mat.GetTexturePropertyNames(); }
            catch { return; }

            foreach (var n in names)
            {
                if (mat.GetTexture(n) is Texture2D t) set.Add(t);
            }
        }

        static void AddClip(AnimationClip clip, HashSet<Texture2D> set)
        {
            if (clip == null) return;
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (curve == null) continue;
                foreach (var kf in curve)
                {
                    if (kf.value is Texture2D t) set.Add(t);
                    if (kf.value is Material m) AddMaterial(m, set);
                }
            }
        }
    }
}

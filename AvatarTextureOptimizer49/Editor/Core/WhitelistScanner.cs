using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Whitelist expansion: whitelist entries may be ANY object type (mesh, material, texture,
    /// animation, GameObject, ...). All Texture2D assets they reference (transitively) become
    /// fully whitelisted. / 白名单展开：条目不限类型，其引用到的全部 Texture2D 递归进入白名单。
    /// </summary>
    internal static class WhitelistScanner
    {
        internal static HashSet<Texture2D> CollectWhitelistTextures(IEnumerable<Object> entries)
        {
            var set = new HashSet<Texture2D>();
            var seen = new HashSet<Object>();

            foreach (var entry in entries)
            {
                if (entry == null) continue;
                if (entry is Texture2D t2d)
                {
                    set.Add(t2d);
                    continue;
                }

                // CollectDependencies walks references deeply (materials→textures, clips→objects,
                // GameObjects→components→everything). / 依赖收集覆盖所有引用路径。
                Object[] deps;
                try
                {
                    deps = EditorUtility.CollectDependencies(new[] { entry });
                }
                catch
                {
                    deps = new Object[] { entry };
                }

                foreach (var dep in deps)
                {
                    if (dep is Texture2D t) set.Add(t);
                }

                // Materials/GameObject: also pick textures via serialized properties, to catch
                // non-asset references CollectDependencies might skip. / 再经序列化属性兜底一遍。
                CollectSerializedTextures(entry, set, seen);
                if (entry is GameObject go)
                {
                    foreach (var c in go.GetComponentsInChildren<Component>(true))
                        CollectSerializedTextures(c, set, seen);
                }
            }

            return set;
        }

        private static void CollectSerializedTextures(Object obj, HashSet<Texture2D> set, HashSet<Object> seen)
        {
            if (obj == null || !seen.Add(obj)) return;
            var so = new SerializedObject(obj);
            var prop = so.GetIterator();
            while (prop.NextVisible(true))
            {
                if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                var v = prop.objectReferenceValue as Texture2D;
                if (v != null) set.Add(v);
                // follow one level into referenced materials/animations / 再跟一层引用
                else if (prop.objectReferenceValue is Material || prop.objectReferenceValue is AnimationClip)
                    CollectSerializedTextures(prop.objectReferenceValue, set, seen);
            }
        }
    }
}

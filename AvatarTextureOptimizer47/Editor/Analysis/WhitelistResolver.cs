using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Resolves every texture reachable from arbitrary whitelisted Unity objects without mutating them.
    /// ZH: 在不修改对象的前提下解析任意白名单 Unity 对象可达的全部贴图。
    /// </summary>
    internal static class WhitelistResolver
    {
        public static HashSet<Texture2D> Resolve(IEnumerable<Object> roots)
        {
            var textures = new HashSet<Texture2D>();
            var visited = new HashSet<Object>();
            var queue = new Queue<Object>();
            if (roots != null)
                foreach (var root in roots) if (root != null) queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == null || !visited.Add(current)) continue;
                if (current is Texture2D texture) { textures.Add(texture); continue; }
                if (current is Material material)
                {
                    foreach (var property in material.GetTexturePropertyNames())
                        if (material.GetTexture(property) is Texture2D t) queue.Enqueue(t);
                }
                if (current is GameObject gameObject)
                {
                    foreach (var component in gameObject.GetComponents<Component>()) if (component != null) queue.Enqueue(component);
                    foreach (Transform child in gameObject.transform) queue.Enqueue(child.gameObject);
                }

                TryQueueSerializedReferences(current, queue);
            }
            return textures;
        }

        private static void TryQueueSerializedReferences(Object obj, Queue<Object> queue)
        {
            try
            {
                using (var serialized = new SerializedObject(obj))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.NextVisible(true))
                    {
                        if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var referenced = iterator.objectReferenceValue;
                        if (referenced != null && referenced != obj && !(referenced is MonoScript)) queue.Enqueue(referenced);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ATO] Could not inspect whitelist object '{obj.name}': {ex.Message}", obj);
            }
        }
    }
}

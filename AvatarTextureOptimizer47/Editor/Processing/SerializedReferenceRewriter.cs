using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>EN: Rewrites object references on cloned-avatar components without touching project assets. ZH: 仅重写克隆 Avatar 组件上的对象引用，不修改项目资产。</summary>
    internal static class SerializedReferenceRewriter
    {
        public static void Rewrite<T>(GameObject root, IReadOnlyDictionary<T, T> replacements) where T : Object
        {
            if (root == null || replacements == null || replacements.Count == 0) return;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                try
                {
                    using (var serialized = new SerializedObject(component))
                    {
                        var changed = false;
                        var iterator = serialized.GetIterator();
                        while (iterator.Next(true))
                        {
                            if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                            var current = iterator.objectReferenceValue;
                            if (current is T typed && replacements.TryGetValue(typed, out var replacement))
                            {
                                iterator.objectReferenceValue = replacement;
                                changed = true;
                            }
                        }
                        if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ATO] Could not rewrite references on '{component.name}': {ex.Message}", component);
                }
            }
        }
    }
}

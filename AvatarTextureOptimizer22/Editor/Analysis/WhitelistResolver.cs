// AvatarTextureOptimizer
// File: Editor/Analysis/WhitelistResolver.cs
//
// Resolves the user's whitelist. The whitelist accepts ANY object type:
// GameObjects, renderers, materials, textures, meshes, animation clips, ...
// All textures referenced inside whitelisted objects skip all optimization
// (including later import-parameter optimization). Textures sharing the same
// UV with a whitelisted texture skip atlasization but still take part in
// whole-texture scaling and import-parameter optimization.
//
// 解析用户的白名单。白名单接受任意对象类型：GameObject、渲染器、材质、
// 贴图、网格、动画剪辑……白名单对象内引用的全部贴图跳过所有优化（含后续
// 导入参数优化）。与白名单贴图同 UV 的其他贴图跳过图集化，但仍参与整图
// 缩放与导入参数优化。

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    public static class WhitelistResolver
    {
        /// <summary>
        /// Expand the whitelist into concrete sets of textures and renderers.
        /// 将白名单展开为具体的贴图集合与渲染器集合。
        /// </summary>
        public static void Resolve(GameObject avatarRoot, ATOBuildState state)
        {
            var settings = state.Component.Whitelist;
            if (settings == null || settings.Objects == null || settings.Objects.Count == 0) return;

            var stopwatch = new ATOStopwatch("WhitelistResolver.Resolve");
            var visitedObjects = new HashSet<object>();
            var queuedTextures = new HashSet<Texture2D>();

            foreach (var obj in settings.Objects)
            {
                if (obj == null) continue;
                CollectTextures(obj, state, visitedObjects, queuedTextures);
            }

            // Textures referenced by whitelisted animation clips are whitelisted.
            // 白名单动画剪辑引用的贴图被白名单。
            foreach (var obj in settings.Objects)
            {
                if (obj is AnimationClip clip)
                {
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        foreach (var kf in curve)
                        {
                            if (kf.value is Texture2D t2d && t2d != null)
                            {
                                state.WhitelistedTextures.Add(t2d);
                                queuedTextures.Add(t2d);
                            }
                        }
                    }
                }
            }

            // Renderer whitelist: renderers inside whitelisted GameObjects or
            // renderers themselves in the whitelist.
            // 渲染器白名单：白名单 GameObject 内的渲染器或白名单渲染器本身。
            foreach (var obj in settings.Objects)
            {
                switch (obj)
                {
                    case Renderer r:
                        state.WhitelistedRenderers.Add(r);
                        break;
                    case GameObject go:
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                            state.WhitelistedRenderers.Add(r);
                        break;
                }
            }

            ATOLog.Trace($"whitelist: {state.WhitelistedTextures.Count} textures, {state.WhitelistedRenderers.Count} renderers");
        }

        private static void CollectTextures(object obj, ATOBuildState state, HashSet<object> visited, HashSet<Texture2D> queued)
        {
            if (obj == null || !visited.Add(obj)) return;

            switch (obj)
            {
                case Texture2D tex:
                    if (!state.WhitelistedTextures.Add(tex)) return;
                    queued.Add(tex);
                    return;

                case Material mat:
                {
                    var shader = mat.shader;
                    if (shader == null) return;
                    foreach (var prop in ShaderAnalyzer.EnumerateTextureProperties(shader))
                    {
                        if (!mat.HasProperty(prop)) continue;
                        if (mat.GetTexture(prop) is Texture2D t2d && t2d != null)
                        {
                            state.WhitelistedTextures.Add(t2d);
                            queued.Add(t2d);
                        }
                    }
                    return;
                }

                case Renderer r:
                {
                    foreach (var m in r.sharedMaterials)
                        if (m != null) CollectTextures(m, state, visited, queued);
                    return;
                }

                case GameObject go:
                {
                    foreach (var component in go.GetComponents<Component>())
                    {
                        if (component is Renderer renderer)
                            foreach (var m in renderer.sharedMaterials)
                                if (m != null) CollectTextures(m, state, visited, queued);
                    }
                    // Children are handled by the caller for renderers; here we
                    // also descend into children to cover nested objects.
                    // 子级由调用方处理渲染器；这里也下钻子级以覆盖嵌套对象。
                    foreach (Transform child in go.transform)
                        CollectTextures(child.gameObject, state, visited, queued);
                    return;
                }

                case AnimationClip clip:
                {
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        foreach (var kf in curve)
                        {
                            if (kf.value is Texture2D t2d && t2d != null)
                            {
                                state.WhitelistedTextures.Add(t2d);
                                queued.Add(t2d);
                            }
                            else if (kf.value is Material m2 && m2 != null)
                                CollectTextures(m2, state, visited, queued);
                        }
                    }
                    return;
                }

                default:
                    // Generic Object (e.g. a Mesh or arbitrary asset): reflect
                    // over serialized fields to find texture references.
                    // 通用对象（如 Mesh 或任意资产）：反射序列化字段以查找贴图引用。
                    CollectViaSerializedObject(obj, state, visited, queued);
                    return;
            }
        }

        /// <summary>
        /// For arbitrary whitelisted assets, walk serialized properties to find
        /// Texture references (covers custom components and scriptable objects).
        /// 对任意白名单资产，遍历序列化属性以查找贴图引用（覆盖自定义组件
        /// 与 ScriptableObject）。
        /// </summary>
        private static void CollectViaSerializedObject(object obj, ATOBuildState state, HashSet<object> visited, HashSet<Texture2D> queued)
        {
            if (obj is UnityEngine.Object uo && uo != null)
            {
                var so = new SerializedObject(uo);
                var prop = so.GetIterator();
                while (prop.NextVisible(true))
                {
                    if (prop.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        var refValue = prop.objectReferenceValue;
                        if (refValue is Texture2D t2d && t2d != null)
                        {
                            state.WhitelistedTextures.Add(t2d);
                            queued.Add(t2d);
                        }
                        else if (refValue is Material m && m != null)
                            CollectTextures(m, state, visited, queued);
                        else if (refValue is GameObject go)
                            CollectTextures(go, state, visited, queued);
                    }
                }
                so.Dispose();
            }
        }
    }
}

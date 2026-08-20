using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Expands the untyped whitelist into the concrete set of textures that must never be touched.
    /// ZH: 把不限类型的白名单展开成"绝不允许修改"的具体贴图集合。
    /// </summary>
    public sealed class WhitelistResolver
    {
        private readonly ATOLog _log;

        /// <summary>EN: Textures reachable from a whitelisted object. ZH: 从白名单对象可达的贴图。</summary>
        public readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>();

        /// <summary>EN: Materials explicitly whitelisted. ZH: 被显式白名单的材质。</summary>
        public readonly HashSet<Material> Materials = new HashSet<Material>();

        /// <summary>EN: Meshes explicitly whitelisted. ZH: 被显式白名单的网格。</summary>
        public readonly HashSet<Mesh> Meshes = new HashSet<Mesh>();

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public WhitelistResolver(ATOLog log) { _log = log; }

        /// <summary>EN: Walk every whitelist entry and collect reachable assets. ZH: 遍历白名单条目并收集可达资产。</summary>
        public void Resolve(IEnumerable<Object> entries)
        {
            if (entries == null) return;
            var visited = new HashSet<Object>();
            foreach (var e in entries) Visit(e, visited, 0);
            _log.Verbose($"Whitelist resolved: {Textures.Count} textures, {Materials.Count} materials, {Meshes.Count} meshes");
        }

        private void Visit(Object o, HashSet<Object> visited, int depth)
        {
            if (o == null || depth > 8 || !visited.Add(o)) return;

            switch (o)
            {
                case Texture2D t:
                    Textures.Add(t);
                    return;
                case Material m:
                    Materials.Add(m);
                    CollectFromMaterial(m);
                    return;
                case Mesh mesh:
                    Meshes.Add(mesh);
                    return;
                case AnimationClip clip:
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, b))
                        Visit(kf.value, visited, depth + 1);
                    return;
                case RuntimeAnimatorController rac:
                    foreach (var clip in rac.animationClips) Visit(clip, visited, depth + 1);
                    return;
                case Renderer r:
                    foreach (var m in r.sharedMaterials) Visit(m, visited, depth + 1);
                    if (r is SkinnedMeshRenderer smr) Visit(smr.sharedMesh, visited, depth + 1);
                    else if (r.TryGetComponent<MeshFilter>(out var mf)) Visit(mf.sharedMesh, visited, depth + 1);
                    return;
                case GameObject go:
                    foreach (var c in go.GetComponentsInChildren<Component>(true))
                        if (c != null && !(c is Transform)) Visit(c, visited, depth + 1);
                    return;
                case Component comp:
                    if (comp is Renderer cr) { Visit(cr, visited, depth + 1); return; }
                    if (comp is Animator an) { Visit(an.runtimeAnimatorController, visited, depth + 1); return; }
                    // EN: Any other component: scan its serialized object references one level deep.
                    // ZH: 其他任意组件：向下扫描一层其序列化的对象引用。
                    ScanSerialized(comp, visited, depth);
                    return;
                default:
                    ScanSerialized(o, visited, depth);
                    return;
            }
        }

        private void ScanSerialized(Object o, HashSet<Object> visited, int depth)
        {
            try
            {
                var so = new SerializedObject(o);
                var it = so.GetIterator();
                while (it.NextVisible(true))
                    if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue != null)
                        Visit(it.objectReferenceValue, visited, depth + 1);
            }
            catch (Exception e) { _log.Trace($"Whitelist scan of {o} failed: {e.Message}"); }
        }

        private void CollectFromMaterial(Material m)
        {
            if (m == null || m.shader == null) return;
            var n = m.shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (m.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var name = m.shader.GetPropertyName(i);
                if (m.GetTexture(name) is Texture2D t) Textures.Add(t);
            }
        }
    }
}

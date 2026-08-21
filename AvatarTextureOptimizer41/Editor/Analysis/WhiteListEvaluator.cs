using System.Collections.Generic;
using UnityEngine;

// Whitelist evaluation: whitelisted objects (meshes / materials / textures / animation clips / renderers / gameobjects)
// cause every texture they reference to skip ALL optimization.
// 白名单评估：白名单对象（网格/材质/贴图/动画剪辑/渲染器/游戏对象）引用的全部贴图跳过所有优化。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class WhiteListEvaluator
    {
        private readonly HashSet<Object> _whitelist = new HashSet<Object>();
        private readonly Dictionary<Texture2D, bool> _texCache = new Dictionary<Texture2D, bool>();

        public WhiteListEvaluator(IEnumerable<Object> whitelist)
        {
            foreach (var o in whitelist)
                if (o != null) _whitelist.Add(o);
        }

        public bool IsEmpty => _whitelist.Count == 0;

        public bool IsWhitelisted(Object o) => o != null && _whitelist.Contains(o);

        /// <summary>Marks an object whitelisted at runtime (e.g. a dedup result whose member was whitelisted). 运行时标记白名单（如成员被白名单的去重结果）。</summary>
        public void AddWhitelisted(Object o) { if (o != null) _whitelist.Add(o); }

        /// <summary>
        /// A texture is whitelisted if it is whitelisted directly, or any whitelisted mesh/material/renderer/clip
        /// references it. We resolve mesh->materials->textures, clip->object-reference targets, and material->textures.
        /// 贴图白名单判定：贴图本身被白名单，或其被白名单的网格/材质/渲染器/剪辑引用。
        /// </summary>
        public bool IsTextureWhitelisted(Texture2D tex, Texture2D original)
        {
            if (tex == null) return false;
            if (_texCache.TryGetValue(tex, out var cached)) return cached;
            bool result = _whitelist.Contains(tex);
            if (!result && original != null && _whitelist.Contains(original)) result = true;

            if (!result)
            {
                // Directly whitelisted material referencing this texture. 直接白名单的材质引用了该贴图。
                foreach (var o in _whitelist)
                {
                    if (o is Material m)
                    {
                        foreach (var prop in MaterialUtil.EnumerateTextureProperties(m))
                            if (m.GetTexture(prop) == tex) { result = true; break; }
                    }
                    else if (o is Mesh mesh)
                    {
                        // Mesh itself doesn't reference textures; its renderers are tracked separately. 网格本身不引用贴图。
                    }
                    else if (o is AnimationClip clip)
                    {
                        if (ClipReferencesTexture(clip, tex)) { result = true; break; }
                    }
                    else if (o is GameObject go)
                    {
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                            foreach (var m in r.sharedMaterials)
                                if (m != null && MaterialUtil.EnumerateTextureProperties(m).Exists(p => m.GetTexture(p) == tex)) { result = true; break; }
                    }
                    if (result) break;
                }
            }
            _texCache[tex] = result;
            return result;
        }

        private bool ClipReferencesTexture(AnimationClip clip, Texture2D tex)
        {
            foreach (var b in UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!b.propertyName.Contains("_")) continue; // material-ish property. 材质类属性。
                var curve = UnityEditor.AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (curve == null) continue;
                foreach (var kf in curve)
                    if (kf.value == tex) return true;
            }
            return false;
        }
    }

    /// <summary>Small helper to enumerate material texture property names. 枚举材质贴图属性名的辅助类。</summary>
    public static class MaterialUtil
    {
        public static List<string> EnumerateTextureProperties(Material m)
        {
            var list = new List<string>();
            if (m == null || m.shader == null) return list;
            int count = UnityEditor.ShaderUtil.GetPropertyCount(m.shader);
            for (int i = 0; i < count; i++)
            {
                if (UnityEditor.ShaderUtil.GetPropertyType(m.shader, i) == UnityEditor.ShaderUtil.ShaderPropertyType.TexEnv)
                    list.Add(UnityEditor.ShaderUtil.GetPropertyName(m.shader, i));
            }
            return list;
        }
    }
}

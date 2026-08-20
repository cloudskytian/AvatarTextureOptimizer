// AvatarTextureOptimizer - ReferenceRewriter
// EN: Applies the optimized assets: clones materials (only texture references change — never other shader
// parameters), assigns them to slots, and rewrites animation curves (texture & material object references).
// CN: 应用优化资产：克隆材质（只改贴图引用，绝不改其他着色器参数）、赋给槽位，
//     并重写动画曲线（贴图与材质的对象引用）。
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class ReferenceRewriter
    {
        // 旧贴图 → 新贴图
        public static readonly Dictionary<Texture2D, Texture2D> TextureMap = new Dictionary<Texture2D, Texture2D>();
        // 旧材质 → 新材质
        public static readonly Dictionary<Material, Material> MaterialMap = new Dictionary<Material, Material>();

        public static void Clear()
        {
            TextureMap.Clear();
            MaterialMap.Clear();
        }

        /// <summary>
        /// EN: Registers a new texture for an old one. / CN: 为旧贴图登记新贴图。
        /// </summary>
        public static void RegisterTexture(Texture2D oldTex, Texture2D newTex)
        {
            if (oldTex == null || newTex == null) return;
            TextureMap[oldTex] = newTex;
        }

        /// <summary>
        /// EN: Clones a material with updated texture references (only texture properties; all other shader
        /// parameters are copied verbatim — spec: 绝不对材质内除贴图以外的任何其他着色器参数作修改).
        /// CN: 克隆材质并更新贴图引用（仅贴图属性；其余着色器参数原样拷贝——按需求）。
        /// </summary>
        public static Material CloneWithTextures(AtoBuildState state, Material src,
            Dictionary<int, Texture2D> texByPropId)
        {
            if (MaterialMap.TryGetValue(src, out var existing)) return existing;
            var clone = new Material(src) { name = $"ATO_{src.name}", hideFlags = HideFlags.HideAndDontSave };
            foreach (var kv in texByPropId)
            {
                clone.SetTexture(kv.Key, kv.Value);
            }
            MaterialMap[src] = clone;
            return clone;
        }

        /// <summary>
        /// EN: Rewrites all animation clips under the avatar: texture keyframes and material keyframes point to
        /// the optimized assets; material-slot array bindings are remapped (slot merge).
        /// CN: 重写 Avatar 下所有动画片段：贴图/材质关键帧指向优化资产；材质槽数组绑定重映射（槽合并）。
        /// </summary>
        public static void RewriteAnimations(AtoBuildState state, AnimationData anim,
            Dictionary<(Renderer, int), int> slotRemap)
        {
            if (anim == null) return;
            foreach (var clip in anim.clips)
            {
                if (clip == null) continue;
                bool dirty = false;

                // EN: Object reference curves (textures & materials).
                // CN: 对象引用曲线（贴图与材质）。
                var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var binding in objBindings)
                {
                    var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keyframes == null) continue;
                    bool changed = false;

                    // EN: Slot array binding: m_Materials.Array.data[i][.prop]
                    // CN: 槽数组绑定：m_Materials.Array.data[i][.prop]
                    if (binding.propertyName.StartsWith("m_Materials.Array.data["))
                    {
                        int slot = ParseSlot(binding.propertyName);
                        string prop = ParseProp(binding.propertyName);
                        if (slot < 0) continue;
                        var go = ResolvePath(state.Ctx.AvatarRootObject, binding.path);
                        var renderer = go != null ? go.GetComponent(binding.type) as Renderer : null;
                        if (renderer == null) continue;

                        if (slotRemap != null && slotRemap.TryGetValue((renderer, slot), out int newSlot) &&
                            newSlot != slot)
                        {
                            string newPropName = $"m_Materials.Array.data[{newSlot}]" +
                                                 (string.IsNullOrEmpty(prop) ? "" : "." + prop);
                            var newBinding = new EditorCurveBinding
                            {
                                path = binding.path,
                                type = binding.type,
                                propertyName = newPropName
                            };
                            // EN: Remove old binding & re-add under the new slot index.
                            // CN: 移除旧绑定并在新槽索引下重加。
                            AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                            foreach (var kf in keyframes)
                            {
                                if (kf.value is Texture2D tex && TextureMap.TryGetValue(tex, out var newTex))
                                {
                                    kf.value = newTex;
                                    changed = true;
                                }
                                else if (kf.value is Material mat && MaterialMap.TryGetValue(mat, out var newMat))
                                {
                                    kf.value = newMat;
                                    changed = true;
                                }
                            }
                            AnimationUtility.SetObjectReferenceCurve(clip, newBinding, keyframes);
                            dirty |= changed;
                            continue;
                        }

                        // EN: Same slot: just replace values.
                        // CN: 同槽位：仅替换值。
                        foreach (var kf in keyframes)
                        {
                            if (kf.value is Texture2D tex && TextureMap.TryGetValue(tex, out var newTex))
                            {
                                kf.value = newTex;
                                changed = true;
                            }
                            else if (kf.value is Material mat && MaterialMap.TryGetValue(mat, out var newMat))
                            {
                                kf.value = newMat;
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            AnimationUtility.SetObjectReferenceCurve(clip, binding, keyframes);
                            dirty = true;
                        }
                        continue;
                    }

                    // EN: Direct material/texture asset references.
                    // CN: 直接材质/贴图资产引用。
                    foreach (var kf in keyframes)
                    {
                        if (kf.value is Texture2D tex && TextureMap.TryGetValue(tex, out var newTex))
                        {
                            kf.value = newTex;
                            changed = true;
                        }
                        else if (kf.value is Material mat && MaterialMap.TryGetValue(mat, out var newMat))
                        {
                            kf.value = newMat;
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, keyframes);
                        dirty = true;
                    }
                }

                if (dirty)
                {
                    EditorUtility.SetDirty(clip);
                    AtoLog.Detail($"Animation {clip.name} rewritten");
                }
            }
        }

        private static GameObject ResolvePath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var t = root.transform.Find(path);
            return t != null ? t.gameObject : null;
        }

        private static int ParseSlot(string propertyName)
        {
            int start = propertyName.IndexOf('[');
            int end = propertyName.IndexOf(']');
            if (start < 0 || end <= start) return -1;
            return int.TryParse(propertyName.Substring(start + 1, end - start - 1), out int s) ? s : -1;
        }

        private static string ParseProp(string propertyName)
        {
            int end = propertyName.IndexOf(']');
            if (end < 0 || end + 1 >= propertyName.Length) return "";
            return propertyName.Substring(end + 1).TrimStart('.');
        }

        /// <summary>EN: Applies new textures to a slot's material (clone), used by the main pass. / CN: 把新贴图应用到槽位材质（克隆），由主流程调用。</summary>
        public static void ApplySlotTextures(AtoBuildState state, Renderer renderer, int slot,
            Dictionary<int, Texture2D> texByProp)
        {
            if (texByProp.Count == 0) return;
            var mats = renderer.sharedMaterials;
            if (slot >= mats.Length || mats[slot] == null) return;
            var clone = CloneWithTextures(state, mats[slot], texByProp);
            mats[slot] = clone;
            renderer.sharedMaterials = mats;
        }
    }
}

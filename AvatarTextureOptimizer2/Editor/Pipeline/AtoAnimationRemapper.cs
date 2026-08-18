using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Remap animation object-reference curves (textures/materials) and material-slot indices.
    /// 重映射动画中的贴图/材质引用与材质槽索引。
    /// </summary>
    public static class AtoAnimationRemapper
    {
        static readonly Regex SlotRx = new Regex(@"m_Materials\.Array\.data\[(\d+)\]", RegexOptions.Compiled);

        public static void RemapTexturesAndMaterials(GameObject root,
            Dictionary<Texture2D, Texture2D> texMap,
            Dictionary<Material, Material> matMap,
            AtoReport report)
        {
            if ((texMap == null || texMap.Count == 0) && (matMap == null || matMap.Count == 0))
                return;

            var clipMap = CloneClips(root, report);
            foreach (var clip in clipMap.Values)
            {
                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                bool dirty = false;
                foreach (var b in bindings)
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    bool ch = false;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (keys[i].value is Texture2D t && texMap != null && texMap.TryGetValue(t, out var nt))
                        {
                            keys[i].value = nt;
                            ch = true;
                        }
                        else if (keys[i].value is Material m && matMap != null && matMap.TryGetValue(m, out var nm))
                        {
                            keys[i].value = nm;
                            ch = true;
                        }
                    }
                    if (ch)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, b, keys);
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    EditorUtility.SetDirty(clip);
                    report.Detail($"anim remap refs: {clip.name}");
                }
            }
        }

        /// <summary>
        /// oldSlot → newSlot per renderer path. / 按渲染器路径重映射槽索引。
        /// </summary>
        public static void RemapMaterialSlots(GameObject root,
            Dictionary<Renderer, int[]> slotMaps, AtoReport report)
        {
            if (slotMaps == null || slotMaps.Count == 0) return;

            var pathOf = new Dictionary<Renderer, string>();
            foreach (var kv in slotMaps)
                pathOf[kv.Key] = AnimationUtility.CalculateTransformPath(kv.Key.transform, root.transform);

            foreach (var clip in CollectClips(root))
            {
                bool dirty = false;
                var objBinds = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var b in objBinds)
                {
                    var m = SlotRx.Match(b.propertyName);
                    if (!m.Success) continue;
                    int old = int.Parse(m.Groups[1].Value);
                    Renderer rend = null;
                    foreach (var kv in pathOf)
                        if (kv.Value == b.path) { rend = kv.Key; break; }
                    if (rend == null || !slotMaps.TryGetValue(rend, out var map)) continue;
                    if (old < 0 || old >= map.Length) continue;
                    int neu = map[old];
                    if (neu == old) continue;
                    var nb = b;
                    nb.propertyName = SlotRx.Replace(b.propertyName, $"m_Materials.Array.data[{neu}]");
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    AnimationUtility.SetObjectReferenceCurve(clip, b, null);
                    AnimationUtility.SetObjectReferenceCurve(clip, nb, keys);
                    dirty = true;
                }

                var fbinds = AnimationUtility.GetCurveBindings(clip);
                foreach (var b in fbinds)
                {
                    var m = SlotRx.Match(b.propertyName);
                    if (!m.Success) continue;
                    int old = int.Parse(m.Groups[1].Value);
                    Renderer rend = null;
                    foreach (var kv in pathOf)
                        if (kv.Value == b.path) { rend = kv.Key; break; }
                    if (rend == null || !slotMaps.TryGetValue(rend, out var map)) continue;
                    if (old < 0 || old >= map.Length) continue;
                    int neu = map[old];
                    if (neu == old) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    var nb = b;
                    nb.propertyName = SlotRx.Replace(b.propertyName, $"m_Materials.Array.data[{neu}]");
                    AnimationUtility.SetEditorCurve(clip, b, null);
                    AnimationUtility.SetEditorCurve(clip, nb, curve);
                    dirty = true;
                }
                if (dirty)
                {
                    EditorUtility.SetDirty(clip);
                    report.Detail($"anim remap slots: {clip.name}");
                }
            }
        }

        static Dictionary<AnimationClip, AnimationClip> CloneClips(GameObject root, AtoReport report)
        {
            var map = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var src in CollectClips(root))
            {
                if (map.ContainsKey(src)) continue;
                var n = Object.Instantiate(src);
                n.name = src.name + "_ATO";
                map[src] = n;
            }
            if (map.Count == 0) return map;

            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null) continue;
                var nc = Object.Instantiate(a.runtimeAnimatorController);
                nc.name = a.runtimeAnimatorController.name + "_ATO";
                ReplaceClipRefs(nc, map);
                a.runtimeAnimatorController = nc;
            }
            report.Detail($"cloned animation clips={map.Count}");
            return map;
        }

        static void ReplaceClipRefs(Object controller, Dictionary<AnimationClip, AnimationClip> map)
        {
            var so = new SerializedObject(controller);
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = true;
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (it.objectReferenceValue is AnimationClip c && map.TryGetValue(c, out var n))
                    it.objectReferenceValue = n;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        public static HashSet<AnimationClip> CollectClips(GameObject root)
        {
            var set = new HashSet<AnimationClip>();
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null) continue;
                foreach (var c in a.runtimeAnimatorController.animationClips)
                    if (c != null) set.Add(c);
            }
            foreach (var anim in root.GetComponentsInChildren<Animation>(true))
            {
                foreach (AnimationState st in anim)
                    if (st != null && st.clip != null) set.Add(st.clip);
            }
            TryVrcLayers(root, set);
            return set;
        }

        static void TryVrcLayers(GameObject root, HashSet<AnimationClip> set)
        {
#if ATO_VRCSDK3
            var desc = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (desc == null) return;
            void Add(RuntimeAnimatorController c)
            {
                if (c == null) return;
                foreach (var cl in c.animationClips) if (cl != null) set.Add(cl);
            }
            if (desc.customizeAnimationLayers)
            {
                foreach (var l in desc.baseAnimationLayers) Add(l.animatorController);
                foreach (var l in desc.specialAnimationLayers) Add(l.animatorController);
            }
#endif
        }

        /// <summary>
        /// True if any clip independently switches one slot of this renderer.
        /// 若动画单独切换该渲染器某一个材质槽，则不可合并槽。
        /// </summary>
        public static bool HasPerSlotMaterialSwitch(GameObject root, Renderer r)
        {
            var path = AnimationUtility.CalculateTransformPath(r.transform, root.transform);
            foreach (var clip in CollectClips(root))
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (b.path != path) continue;
                    if (SlotRx.IsMatch(b.propertyName) && b.type == typeof(Renderer))
                    {
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                        var distinct = new HashSet<int>();
                        foreach (var k in keys)
                            if (k.value != null) distinct.Add(k.value.GetInstanceID());
                        if (distinct.Count > 1) return true;
                    }
                }
            }
            return false;
        }
    }
}

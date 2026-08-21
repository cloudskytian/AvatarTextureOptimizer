using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Reference updater + asset cloner. NEVER mutates user assets: materials are cloned via `new Material`,
// animation clips and animator controllers via Object.Instantiate when a change is required; the clones
// are wired into the avatar (renderers / animators) and saved by NDMF at build end.
// 引用更新器 + 资产克隆器。绝不改动用户资产：材质用 new Material 克隆，动画剪辑与 AnimatorController
// 在需要修改时用 Object.Instantiate 克隆，并接入 Avatar（渲染器/Animator），构建结束时由 NDMF 保存。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class ReferenceUpdater
    {
        private readonly Dictionary<Material, Material> _materialClones = new Dictionary<Material, Material>();
        private readonly Dictionary<AnimationClip, AnimationClip> _clipClones = new Dictionary<AnimationClip, AnimationClip>();
        private readonly Dictionary<AnimatorController, AnimatorController> _controllerClones = new Dictionary<AnimatorController, AnimatorController>();
        private readonly Dictionary<GameObject, Animator> _animators = new Dictionary<GameObject, Animator>();

        /// <summary>
        /// Returns the working copy of a material (clone on first access), or null.
        /// 返回材质的可写副本（首次访问时克隆），不存在返回 null。
        /// </summary>
        public Material GetWorkingMaterial(Material original)
        {
            if (original == null) return null;
            if (_materialClones.TryGetValue(original, out var c)) return c;
            var clone = new Material(original)
            {
                name = "ATO_" + original.name,
                hideFlags = HideFlags.None,
            };
            _materialClones[original] = clone;
            return clone;
        }

        public bool HasWorkingMaterial(Material original) => _materialClones.ContainsKey(original);

        /// <summary>
        /// Rewrites texture references on the avatar's renderer materials, cloning materials as needed,
        /// and returns the set of renderers whose materials array was replaced.
        /// 重写 Avatar 渲染器材质上的贴图引用（必要时克隆材质），返回材质数组被替换的渲染器集合。
        /// </summary>
        public void RewriteTextures(GameObject root, Dictionary<Texture2D, Texture2D> oldToNew)
            => RewriteTextures(root, oldToNew, null);

        /// <summary>
        /// Rewrites texture references, optionally excluding (material, property) pairs that already got an
        /// atlas assignment (so whole-texture replacement never clobbers atlas assignments).
        /// 重写贴图引用，可排除已赋图集的 (材质, 属性) 对（避免整图缩放覆盖图集赋值）。
        /// </summary>
        public void RewriteTextures(GameObject root, Dictionary<Texture2D, Texture2D> oldToNew, HashSet<(Material, string)> excludeProps)
        {
            if (oldToNew == null || oldToNew.Count == 0) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null || mat.shader == null) continue;
                    foreach (var prop in MaterialUtil.EnumerateTextureProperties(mat))
                    {
                        if (excludeProps != null && excludeProps.Contains((mat, prop))) continue;
                        if (mat.GetTexture(prop) is Texture2D t && oldToNew.TryGetValue(t, out var replacement) && replacement != t)
                        {
                            var working = GetWorkingMaterial(mat);
                            working.SetTexture(prop, replacement);
                            mats[i] = working;
                            changed = true;
                        }
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }
            // Rewrite clip object-reference curves as well. 同时重写剪辑对象引用曲线。
            foreach (var clip in AnimationAnalyzer.CollectClips(root))
                RewriteClip(clip, obj => obj is Texture2D t && oldToNew.TryGetValue(t, out var r) ? r : obj, root);
        }

        /// <summary>
        /// Rewrites object-reference curves of a clip (cloning the clip + its controllers when needed).
        /// 重写剪辑的对象引用曲线（必要时克隆剪辑及其控制器）。
        /// </summary>
        public void RewriteClip(AnimationClip clip, Func<UnityEngine.Object, UnityEngine.Object> map, GameObject root)
        {
            bool needChange = false;
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (curve == null) continue;
                foreach (var kf in curve)
                    if (kf.value != null && map(kf.value) != kf.value) { needChange = true; break; }
                if (needChange) break;
            }
            if (!needChange) return;

            var work = GetWorkingClip(clip);
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(work))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(work, b);
                if (curve == null) continue;
                var newCurve = new ObjectReferenceKeyframe[curve.Length];
                for (int i = 0; i < curve.Length; i++)
                {
                    newCurve[i] = curve[i];
                    if (newCurve[i].value != null) newCurve[i].value = map(newCurve[i].value);
                }
                AnimationUtility.SetObjectReferenceCurve(work, b, newCurve);
            }
            RewireControllers(root);
        }

        public AnimationClip GetWorkingClip(AnimationClip original)
        {
            if (_clipClones.TryGetValue(original, out var c)) return c;
            var clone = UnityEngine.Object.Instantiate(original);
            clone.name = "ATO_" + original.name;
            _clipClones[original] = clone;
            return clone;
        }

        /// <summary>
        /// Points every Animator on the avatar at cloned controllers when their clips changed.
        /// 当剪辑发生变化时，将 Avatar 上所有 Animator 指向克隆控制器。
        /// </summary>
        public void RewireControllers(GameObject root)
        {
            if (_clipClones.Count == 0) return;
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (!(animator.runtimeAnimatorController is AnimatorController ctrl)) continue;
                var working = GetWorkingController(ctrl);
                if (working != ctrl) animator.runtimeAnimatorController = working;
            }
            foreach (var anim in root.GetComponentsInChildren<Animation>(true))
            {
                // Legacy Animation: replace clips in its clip list. 旧版 Animation：替换其剪辑列表。
                foreach (AnimationState state in anim)
                {
                    if (state.clip != null && _clipClones.TryGetValue(state.clip, out var work))
                    {
                        anim.RemoveClip(state.clip);
                        anim.AddClip(work, state.name);
                    }
                }
            }
        }

        private AnimatorController GetWorkingController(AnimatorController original)
        {
            if (_controllerClones.TryGetValue(original, out var c)) return c;
            var clone = UnityEngine.Object.Instantiate(original);
            clone.name = "ATO_" + original.name;
            _controllerClones[original] = clone;
            return clone;
        }

        /// <summary>
        /// Remaps material slot indices referenced by animation property paths (m_Materials.Array.data[i])
        /// after slots were merged. Paths are remapped by renderer absolute path.
        /// 材质槽合并后，重映射动画属性路径（m_Materials.Array.data[i]）中的槽位索引（按渲染器绝对路径）。
        /// </summary>
        public void RemapSlotIndices(GameObject root, Func<string, int, int> remap)
        {
            foreach (var clip in AnimationAnalyzer.CollectClips(root))
            {
                var work = GetWorkingClip(clip);
                bool changed = false;
                var bindings = AnimationUtility.GetCurveBindings(work);
                foreach (var b in bindings)
                {
                    var idx = ParseSlot(b.propertyName);
                    if (idx >= 0)
                    {
                        int newIdx = remap(b.path, idx);
                        if (newIdx != idx)
                        {
                            string newProp = b.propertyName.Replace($"[{idx}]", $"[{newIdx}]");
                            var curve = AnimationUtility.GetEditorCurve(work, b);
                            var nb = new EditorCurveBinding { path = b.path, type = b.type, propertyName = newProp };
                            AnimationUtility.SetEditorCurve(work, b, null);
                            AnimationUtility.SetEditorCurve(work, nb, curve);
                            changed = true;
                        }
                    }
                }
                if (changed) RewireControllers(root);
            }
        }

        private static int ParseSlot(string propertyName)
        {
            const string marker = "m_Materials.Array.data[";
            int i = propertyName.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return -1;
            int start = i + marker.Length;
            int end = propertyName.IndexOf(']', start);
            if (end < 0) return -1;
            return int.TryParse(propertyName.Substring(start, end - start), out int v) ? v : -1;
        }
    }
}

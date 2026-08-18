// AnimationUpdater.cs / AnimationUpdater.cs
// Updates animation clips that reference old textures/materials to reference the new atlases/materials.
// 更新引用旧贴图/材质的动画片段，引用新图集/材质。

using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.Editor.Atlas;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Processing
{
    public static class AnimationUpdater
    {
        /// <summary>
        /// Scan all animator controllers on the avatar and rewrite object reference curves
        /// pointing to old Texture2D / Material objects to the new ones.
        /// 扫描Avatar上所有Animator控制器，把指向旧Texture2D/Material对象的对象引用曲线重写为新对象。
        /// </summary>
        public static void UpdateAnimations(AvatarAnalysisResult analysis, List<AtlasTexture> atlases,
            Dictionary<Texture2D, Texture2D> extraTextureMap = null)
        {
            // Build mapping from old -> new texture/material
            // 构建旧→新贴图/材质映射
            var texMap = new Dictionary<Object, Object>();
            if (extraTextureMap != null)
                foreach (var kv in extraTextureMap)
                    if (kv.Value != null && kv.Value != kv.Key) texMap[kv.Key] = kv.Value;

            // Atlas mappings override (atlas-assigned textures map to their atlas)
            // Atlas映射覆盖（被图集分配的贴图映射到其atlas）
            foreach (var atl in atlases)
            {
                foreach (var pl in atl.Placements)
                    foreach (var isl in pl.group.Islands)
                        if (isl.SourceTexture != null && isl.AssignedAtlas != null && !isl.IsWhitelisted)
                            texMap[isl.SourceTexture] = atl.Texture;
            }

            var matMap = new Dictionary<Object, Object>();

            RewriteAnimationClips(analysis.AvatarRoot, texMap, matMap);
        }

        /// <summary>
        /// Update animations to point to replacement textures (whole-texture scaling path).
        /// 更新动画指向替换贴图（整图缩放路径）。
        /// </summary>
        public static void UpdateTexturesOnly(Dictionary<Texture2D, Texture2D> texMap, GameObject root)
        {
            if (texMap == null || texMap.Count == 0) return;
            var objMap = new Dictionary<Object, Object>();
            foreach (var kv in texMap) if (kv.Value != null && kv.Value != kv.Key) objMap[kv.Key] = kv.Value;
            RewriteAnimationClips(root.transform, objMap, null);
        }

        private static void RewriteAnimationClips(Transform root, Dictionary<Object, Object> texMap, Dictionary<Object, Object> matMap)
        {
            var animators = root.GetComponentsInChildren<Animator>(true);
            var clips = new HashSet<AnimationClip>();
            foreach (var a in animators)
            {
                if (a.runtimeAnimatorController == null) continue;
                foreach (var c in a.runtimeAnimatorController.animationClips)
                    if (c != null) clips.Add(c);
            }
            var animations = root.GetComponentsInChildren<Animation>(true);
            foreach (var a in animations) if (a.clip != null) clips.Add(a.clip);
#if ATO_VRCSDK_INSTALLED
            try
            {
                var desc = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (desc != null)
                {
                    void Add(RuntimeAnimatorController c) { if (c != null) foreach (var cl in c.animationClips) if (cl != null) clips.Add(cl); }
                    foreach (var l in desc.baseAnimationLayers) Add(l.animatorController);
                    foreach (var l in desc.specialAnimationLayers) Add(l.animatorController);
                }
            }
            catch { /* ignore */ }
#endif
            foreach (var clip in clips)
                RewriteClip(clip, texMap, matMap);
        }

        private static void RewriteClip(AnimationClip clip, Dictionary<Object, Object> texMap, Dictionary<Object, Object> matMap)
        {
            if (clip == null) return;
            bool modified = false;
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curves = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                bool curveChanged = false;
                for (int i = 0; i < curves.Length; i++)
                {
                    var kf = curves[i];
                    Object newVal = null;
                    if (kf.value is Texture2D t && texMap != null && texMap.TryGetValue(t, out var nt)) newVal = nt;
                    else if (kf.value is Material m && matMap != null && matMap.TryGetValue(m, out var nm)) newVal = nm;
                    if (newVal != null && kf.value != newVal)
                    {
                        kf.value = newVal;
                        curves[i] = kf;
                        curveChanged = true;
                    }
                }
                if (curveChanged)
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, curves);
                    modified = true;
                }
            }
            if (modified) EditorUtility.SetDirty(clip);
        }
    }
}

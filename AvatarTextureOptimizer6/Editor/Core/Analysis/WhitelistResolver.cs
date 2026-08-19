using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// 白名单解析：把用户白名单对象（不限类型：网格/材质/贴图/动画/GameObject…）展开为
    /// "被引用到的全部贴图"，标记为 Full（跳过所有优化，含导入参数优化）。
    /// </summary>
    public static class WhitelistResolver
    {
        public static void Resolve(IReadOnlyList<UnityEngine.Object> whitelistObjects, GameObject avatarRoot,
            List<TextureInfo> allTextures, ATOLogger logger)
        {
            if (whitelistObjects == null || whitelistObjects.Count == 0) return;

            var textureSet = new HashSet<Texture>();
            var materialSet = new HashSet<Material>();
            var clipSet = new HashSet<AnimationClip>();

            foreach (var obj in whitelistObjects)
            {
                if (obj == null) continue;
                switch (obj)
                {
                    case Texture tex:
                        textureSet.Add(tex);
                        break;
                    case Material mat:
                        materialSet.Add(mat);
                        break;
                    case Mesh mesh:
                        CollectTexturesFromMesh(mesh, avatarRoot, textureSet, materialSet);
                        break;
                    case AnimationClip clip:
                        clipSet.Add(clip);
                        CollectTexturesFromClip(clip, textureSet, materialSet);
                        break;
                    case AnimatorController ac:
                        foreach (var c in ac.animationClips) CollectTexturesFromClip(c, textureSet, materialSet);
                        break;
                    case GameObject go:
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            foreach (var m in r.sharedMaterials) if (m != null) materialSet.Add(m);
                        }
                        foreach (var a in go.GetComponentsInChildren<Animation>(true))
                        {
                            foreach (AnimationState st in a) if (st?.clip != null) CollectTexturesFromClip(st.clip, textureSet, materialSet);
                        }
                        foreach (var anim in go.GetComponentsInChildren<Animator>(true))
                        {
                            if (anim.runtimeAnimatorController is AnimatorController c)
                                foreach (var clip in c.animationClips) CollectTexturesFromClip(clip, textureSet, materialSet);
                        }
                        break;
                    case Renderer r:
                        foreach (var m in r.sharedMaterials) if (m != null) materialSet.Add(m);
                        break;
                    case Component c:
                        // 组件：其宿主上的 Renderer / Animation
                        foreach (var r in c.GetComponents<Renderer>())
                            foreach (var m in r.sharedMaterials) if (m != null) materialSet.Add(m);
                        break;
                }
            }

            foreach (var mat in materialSet)
            {
                if (mat == null) continue;
                foreach (var propName in mat.GetTexturePropertyNames())
                {
                    var t = mat.GetTexture(propName);
                    if (t != null) textureSet.Add(t);
                }
            }

            // 标记
            int marked = 0;
            foreach (var info in allTextures)
            {
                if (info.texture != null && textureSet.Contains(info.texture))
                {
                    info.whitelisted = true;
                    if (info.whitelistLevel < ATOWhitelistLevel.Full)
                    {
                        info.whitelistLevel = ATOWhitelistLevel.Full;
                        marked++;
                    }
                }
            }
            logger.Info($"Whitelist resolved: {textureSet.Count} texture(s) marked whitelisted (affects {marked} TextureInfo).");
        }

        private static void CollectTexturesFromMesh(Mesh mesh, GameObject root, HashSet<Texture> textures, HashSet<Material> materials)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = null;
                if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
                else if (r is MeshRenderer mr) m = mr.GetComponent<MeshFilter>()?.sharedMesh;
                if (m == mesh)
                {
                    foreach (var mat in r.sharedMaterials) if (mat != null) materials.Add(mat);
                }
            }
        }

        private static void CollectTexturesFromClip(AnimationClip clip, HashSet<Texture> textures, HashSet<Material> materials)
        {
            if (clip == null) return;
            try
            {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    foreach (var f in frames)
                    {
                        if (f.value is Texture t) textures.Add(t);
                        else if (f.value is Material m) materials.Add(m);
                    }
                }
            }
            catch (Exception) { }
        }
    }
}

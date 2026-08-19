// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Analysis/WhitelistResolver.cs — 白名单解析 / Whitelist resolution
//
// 需求: 白名单不限制对象类型（包括但不限于网格、材质、贴图、动画）；
//       白名单内对象中引用的全部贴图都跳过所有优化（包括后续参数优化）。
// 实现: 将任意对象展开为 纹理/材质/网格/渲染器/动画片段 四类集合，提供 IsXxx 查询。
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 白名单解析结果 / Whitelist resolution result.
    /// </summary>
    public sealed class Whitelist
    {
        public HashSet<Texture2D> textures = new HashSet<Texture2D>();
        public HashSet<Material> materials = new HashSet<Material>();
        public HashSet<Mesh> meshes = new HashSet<Mesh>();
        public HashSet<Renderer> renderers = new HashSet<Renderer>();
        public HashSet<GameObject> gameObjects = new HashSet<GameObject>();
        public HashSet<AnimationClip> clips = new HashSet<AnimationClip>();
        public HashSet<Shader> shaders = new HashSet<Shader>();

        public bool IsWhitelisted(Object o)
        {
            if (o == null) return false;
            if (o is Texture2D t) return textures.Contains(t);
            if (o is Material m) return materials.Contains(m);
            if (o is Mesh mesh) return meshes.Contains(mesh);
            if (o is Renderer r) return renderers.Contains(r);
            if (o is GameObject go) return gameObjects.Contains(go);
            if (o is AnimationClip c) return clips.Contains(c);
            if (o is Shader s) return shaders.Contains(s);
            if (o is Component comp) return gameObjects.Contains(comp.gameObject);
            return false;
        }
    }

    /// <summary>
    /// 白名单解析器 / Whitelist resolver.
    /// </summary>
    public static class WhitelistResolver
    {
        /// <summary>
        /// 解析用户白名单对象列表 / Resolve the user's whitelist object list.
        /// </summary>
        public static Whitelist Resolve(IEnumerable<Object> objects)
        {
            var wl = new Whitelist();
            if (objects == null) return wl;

            foreach (var o in objects)
            {
                if (o == null) continue;
                AddObject(wl, o);
            }
            return wl;
        }

        private static void AddObject(Whitelist wl, Object o)
        {
            switch (o)
            {
                case GameObject go:
                    if (!wl.gameObjects.Add(go)) return;
                    // 递归子物体 / Recurse children
                    foreach (Transform t in go.transform)
                    {
                        AddObject(wl, t.gameObject);
                    }
                    foreach (var comp in go.GetComponents<Component>())
                    {
                        if (comp != null) AddComponent(wl, comp);
                    }
                    break;
                case Component comp:
                    AddComponent(wl, comp);
                    break;
                case Material mat:
                    if (!wl.materials.Add(mat)) return;
                    foreach (var prop in ShaderAnalyzer.GetTexturePropertyNames(mat))
                    {
                        if (mat.GetTexture(prop) is Texture2D tex) wl.textures.Add(tex);
                    }
                    if (mat.shader != null) wl.shaders.Add(mat.shader);
                    break;
                case Texture2D tex:
                    wl.textures.Add(tex);
                    break;
                case Mesh mesh:
                    wl.meshes.Add(mesh);
                    break;
                case AnimationClip clip:
                    wl.clips.Add(clip);
                    AddClipTextures(wl, clip);
                    break;
                case AnimatorController ac:
                    foreach (var clip in GetAllClips(ac))
                    {
                        wl.clips.Add(clip);
                        AddClipTextures(wl, clip);
                    }
                    break;
                case Shader sh:
                    wl.shaders.Add(sh);
                    break;
                default:
                    // 未知类型直接忽略 / Unknown types ignored
                    break;
            }
        }

        private static void AddComponent(Whitelist wl, Component comp)
        {
            switch (comp)
            {
                case Renderer r:
                    if (!wl.renderers.Add(r)) return;
                    foreach (var mat in r.sharedMaterials)
                    {
                        if (mat != null) AddObject(wl, mat);
                    }
                    if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) wl.meshes.Add(smr.sharedMesh);
                    if (r is MeshRenderer mr)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null) wl.meshes.Add(mf.sharedMesh);
                    }
                    break;
                case MeshFilter mf:
                    if (mf.sharedMesh != null) wl.meshes.Add(mf.sharedMesh);
                    break;
                case Animator anim:
                    if (anim.runtimeAnimatorController is AnimatorController ac)
                    {
                        foreach (var clip in GetAllClips(ac))
                        {
                            wl.clips.Add(clip);
                            AddClipTextures(wl, clip);
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        private static void AddClipTextures(Whitelist wl, AnimationClip clip)
        {
            try
            {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    foreach (var kf in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                    {
                        if (kf.value is Texture2D t) wl.textures.Add(t);
                    }
                }
            }
            catch (System.Exception) { /* 忽略无法读取的 clip / ignore unreadable clips */ }
        }

        private static IEnumerable<AnimationClip> GetAllClips(AnimatorController ac)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var layer in ac.layers)
            {
                Collect(layer.stateMachine, clips);
            }
            return clips;
        }

        private static void Collect(AnimatorStateMachine sm, HashSet<AnimationClip> clips)
        {
            if (sm == null) return;
            foreach (var s in sm.states)
            {
                CollectMotion(s.state.motion, clips);
            }
            foreach (var sub in sm.stateMachines)
            {
                Collect(sub.stateMachine, clips);
            }
        }

        private static void CollectMotion(Motion motion, HashSet<AnimationClip> clips)
        {
            if (motion is AnimationClip c) clips.Add(c);
            else if (motion is BlendTree bt)
            {
                foreach (var child in bt.children) CollectMotion(child.motion, clips);
            }
        }
    }
}

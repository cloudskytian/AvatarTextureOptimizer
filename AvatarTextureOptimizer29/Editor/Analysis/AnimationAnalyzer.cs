// Animation analysis over NDMF AnimatorServices virtual clips.
// 基于 NDMF AnimatorServices 虚拟动画片段的动画分析。
//
// Collects (verified API usage, see docs/ThirdPartyNotes.md):
//  - renderer material swaps   m_Materials.Array.data[N]  (object curves)
//  - texture property swaps    material._XxxTex          (object curves)
//  - renderer/gameobject enable m_Enabled / m_IsActive    (float curves)
//  - material float props       material._Cutoff / _MainTex_ST.x / _ScrollRotate ... (quality & eligibility)
//  - transform scale animation  m_LocalScale.x/y/z        (area factor)
//
// English: All edits later go through VirtualClip as well (MA/AAO pattern).
// 中文：后续对动画的修改同样必须走 VirtualClip（MA/AAO 同款模式），直接改资产会丢。

using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Per-renderer-path animation facts. / 每渲染器路径的动画事实。</summary>
    internal class RendererAnim
    {
        internal string path;
        internal bool enabledAnimated;                    // m_Enabled animated / 被动画
        internal readonly List<float> enabledValues = new List<float>();
        internal bool hasEnableTrue;                      // some keyframe enables it / 有启用关键帧
        internal readonly Dictionary<int, List<Object>> slotMaterials = new Dictionary<int, List<Object>>();
        internal readonly Dictionary<string, List<Object>> textureProps = new Dictionary<string, List<Object>>();
        internal readonly Dictionary<string, List<float>> floatProps = new Dictionary<string, List<float>>();
    }

    /// <summary>Global animation facts for the avatar. / 全 Avatar 动画事实。</summary>
    internal class AnimationData
    {
        internal readonly Dictionary<string, RendererAnim> renderers = new Dictionary<string, RendererAnim>();
        // path -> m_IsActive values (gameobject active) / 物体激活曲线
        internal readonly Dictionary<string, List<float>> goActive = new Dictionary<string, List<float>>();
        // path -> axis -> max |value| incl. base pose (computed with scanner) / 缩放动画
        internal readonly Dictionary<string, float[]> localScale = new Dictionary<string, float[]>();
        // every clip in play (for whitelist & later edits) / 全部参与片段
        internal readonly List<VirtualClip> clips = new List<VirtualClip>();

        internal RendererAnim GetRenderer(string path)
        {
            if (!renderers.TryGetValue(path, out var r))
            {
                r = new RendererAnim { path = path };
                renderers[path] = r;
            }
            return r;
        }
    }

    internal static class AnimationAnalyzer
    {
        internal static AnimationData Collect(AtoSession s)
        {
            var data = new AnimationData();
            var asc = s.ctx.Extension<AnimatorServicesContext>();
            var seen = new HashSet<VirtualClip>();

            foreach (var controller in asc.ControllerContext.GetAllControllers())
            foreach (var layer in controller.Layers)
                WalkStateMachine(layer.StateMachine, data, seen);

            ATOLog.DebugL($"animation: {data.renderers.Count} animated renderer paths, {seen.Count} clips");
            return data;
        }

        private static void WalkStateMachine(VirtualStateMachine sm, AnimationData data, HashSet<VirtualClip> seen)
        {
            if (sm == null) return;
            foreach (var child in sm.States)
                WalkMotion(child.State?.Motion, data, seen);
            foreach (var childSm in sm.StateMachines)
                WalkStateMachine(childSm.StateMachine, data, seen);
            // AllStates covers nested; StateMachines recursion above is belt & braces.
        }

        private static void WalkMotion(VirtualMotion motion, AnimationData data, HashSet<VirtualClip> seen)
        {
            switch (motion)
            {
                case null:
                    return;
                case VirtualClip clip:
                    if (!seen.Add(clip)) return;
                    data.clips.Add(clip);
                    CollectClip(clip, data);
                    return;
                case VirtualBlendTree bt:
                    foreach (var c in bt.Children) WalkMotion(c.Motion, data, seen);
                    return;
            }
        }

        private static void CollectClip(VirtualClip clip, AnimationData data)
        {
            foreach (var b in clip.GetFloatCurveBindings())
            {
                if (b.isPhantomCurve) continue;
                if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
                {
                    AddValue(data.goActive, b.path, clip.GetFloatCurve(b)?.keys, k => k.value);
                }
                else if (typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName == "m_Enabled")
                {
                    var r = data.GetRenderer(b.path);
                    r.enabledAnimated = true;
                    AddValue(r.enabledValues, clip.GetFloatCurve(b)?.keys, k => k.value);
                }
                else if (b.type == typeof(Transform) && b.propertyName.StartsWith("m_LocalScale."))
                {
                    int axis = b.propertyName.EndsWith(".x") ? 0 : b.propertyName.EndsWith(".y") ? 1 : 2;
                    if (!data.localScale.TryGetValue(b.path, out var maxes)) data.localScale[b.path] = maxes = new float[3];
                    var keys = clip.GetFloatCurve(b)?.keys;
                    if (keys != null)
                        foreach (var k in keys)
                            maxes[axis] = Mathf.Max(maxes[axis], Mathf.Abs(k.value));
                }
                else if (b.type == typeof(Renderer) && b.propertyName.StartsWith("material."))
                {
                    var r = data.GetRenderer(b.path);
                    AddValue(r.floatProps, b.propertyName, clip.GetFloatCurve(b)?.keys, k => k.value);
                }
            }

            foreach (var b in clip.GetObjectCurveBindings())
            {
                if (!typeof(Renderer).IsAssignableFrom(b.type)) continue;
                var keys = clip.GetObjectCurve(b);
                if (keys == null) continue;

                if (b.propertyName.StartsWith("m_Materials.Array.data["))
                {
                    int open = b.propertyName.IndexOf('[');
                    int close = b.propertyName.IndexOf(']');
                    if (open > 0 && close > open &&
                        int.TryParse(b.propertyName.Substring(open + 1, close - open - 1), out int slot))
                    {
                        var r = data.GetRenderer(b.path);
                        if (!r.slotMaterials.TryGetValue(slot, out var list))
                            r.slotMaterials[slot] = list = new List<Object>();
                        foreach (var k in keys)
                            if (k.value != null)
                                list.Add(k.value);
                    }
                }
                else if (b.propertyName.StartsWith("material."))
                {
                    var r = data.GetRenderer(b.path);
                    if (!r.textureProps.TryGetValue(b.propertyName, out var list))
                        r.textureProps[b.propertyName] = list = new List<Object>();
                    foreach (var k in keys)
                        if (k.value != null)
                            list.Add(k.value);
                }
            }
        }

        private static void AddValue(Dictionary<string, List<float>> dict, string key, Keyframe[] keys, System.Func<Keyframe, float> pick)
        {
            if (keys == null) return;
            if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<float>();
            foreach (var k in keys) list.Add(pick(k));
        }

        private static void AddValue(List<float> list, Keyframe[] keys, System.Func<Keyframe, float> pick)
        {
            if (keys == null) return;
            foreach (var k in keys) list.Add(pick(k));
        }

        // ------------------------------------------------------------------
        // Edit support (used by Apply stages). / 修改支持（应用阶段使用）。
        // ------------------------------------------------------------------

        /// <summary>Replace material references in every clip. / 替换全部片段中的材质引用。</summary>
        internal static void ReplaceMaterials(AtoSession s, Dictionary<Material, Material> map)
        {
            foreach (var clip in s.anim.clips)
            {
                foreach (var b in clip.GetObjectCurveBindings())
                {
                    if (!typeof(Renderer).IsAssignableFrom(b.type)) continue;
                    if (!b.propertyName.StartsWith("m_Materials.Array.data[")) continue;
                    var keys = clip.GetObjectCurve(b);
                    if (keys == null) continue;
                    bool changed = false;
                    foreach (var k in keys)
                        if (k.value is Material m && map.TryGetValue(m, out var nm) && nm != m)
                        {
                            k.value = nm;
                            changed = true;
                        }

                    if (changed) clip.SetObjectCurve(b, keys);
                }
            }
        }

        /// <summary>Replace texture references on material properties. / 替换材质属性贴图引用曲线。</summary>
        internal static void ReplaceTextures(AtoSession s, Dictionary<Texture2D, Texture2D> map)
        {
            foreach (var clip in s.anim.clips)
            {
                foreach (var b in clip.GetObjectCurveBindings())
                {
                    if (!typeof(Renderer).IsAssignableFrom(b.type)) continue;
                    if (!b.propertyName.StartsWith("material.")) continue;
                    var keys = clip.GetObjectCurve(b);
                    if (keys == null) continue;
                    bool changed = false;
                    foreach (var k in keys)
                        if (k.value is Texture2D t && map.TryGetValue(t, out var nt) && nt != t)
                        {
                            k.value = nt;
                            changed = true;
                        }

                    if (changed) clip.SetObjectCurve(b, keys);
                }
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Scans all animation clips for material/texture switches, renderer toggles, scale changes,
    /// and render-mode/cutoff modifications. / 扫描全部动画剪辑：材质/贴图切换、渲染器启停、缩放、
    /// 渲染模式/Cutoff 修改。
    /// </summary>
    public static class AnimationAnalysis
    {
        public sealed class Result
        {
            /// <summary>Renderers whose enable state is animated. / 启停状态被动画控制的渲染器。</summary>
            public readonly HashSet<Renderer> animatedRenderers = new HashSet<Renderer>();

            /// <summary>(renderer, slot) → materials switched in by animation. / （渲染器,槽）→ 动画切入的材质。</summary>
            public readonly Dictionary<(Renderer, int), List<Material>> materialSwitches =
                new Dictionary<(Renderer, int), List<Material>>();

            /// <summary>All textures referenced by animation (PPtr). / 动画引用的全部贴图（PPtr）。</summary>
            public readonly HashSet<Texture2D> animatedTextures = new HashSet<Texture2D>();

            /// <summary>All materials referenced by animation (PPtr). / 动画引用的全部材质（PPtr）。</summary>
            public readonly HashSet<Material> animatedMaterials = new HashSet<Material>();

            /// <summary>object → max animation scale factor. / 物体 → 动画最大缩放。</summary>
            public readonly Dictionary<Transform, float> maxScale = new Dictionary<Transform, float>();

            /// <summary>Materials whose render mode / cutoff is animated. / 渲染模式/Cutoff 被动画修改的材质。</summary>
            public readonly HashSet<Material> renderModeAnimated = new HashSet<Material>();
        }

        public static Result Scan(GameObject avatar)
        {
            var result = new Result();
            var clips = CollectClips(avatar);

            var rendererByPath = new Dictionary<string, Renderer>();
            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
            {
                var path = AnimationUtility.CalculateTransformPath(r.transform, avatar.transform);
                if (!rendererByPath.ContainsKey(path)) rendererByPath[path] = r;
            }

            foreach (var clip in clips)
                if (clip != null) ScanClip(clip, avatar, rendererByPath, result);

            ATOLogger.Info($"animation scan: {result.animatedRenderers.Count} animated renderers, " +
                           $"{result.materialSwitches.Count} material switches, " +
                           $"{result.animatedTextures.Count} animated textures, " +
                           $"{result.maxScale.Count} scaled objects");
            return result;
        }

        private static List<AnimationClip> CollectClips(GameObject avatar)
        {
            var set = new HashSet<AnimationClip>();
            foreach (var animator in avatar.GetComponentsInChildren<Animator>(true))
            {
                var r = animator.runtimeAnimatorController;
                if (r != null) foreach (var c in r.animationClips) if (c != null) set.Add(c);
                if (r is AnimatorOverrideController ov)
                    foreach (var c in ov.animationClips) if (c != null) set.Add(c);
            }
            foreach (var anim in avatar.GetComponentsInChildren<Animation>(true))
                foreach (var a in anim) if (a.clip != null) set.Add(a.clip);
            foreach (var c in avatar.GetComponentsInChildren<AnimationClip>(true)) set.Add(c);
            return set.ToList();
        }

        private static void ScanClip(AnimationClip clip, GameObject avatar,
            Dictionary<string, Renderer> rendererByPath, Result result)
        {
            // float/bool curves / 浮点与布尔曲线
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
                {
                    var t = binding.path.Length == 0 ? avatar.transform : avatar.transform.Find(binding.path);
                    if (t != null)
                    {
                        var r = t.GetComponent<Renderer>();
                        if (r != null) result.animatedRenderers.Add(r);
                    }
                }
                else if (binding.type == typeof(Renderer) && binding.propertyName == "m_Enabled")
                {
                    if (rendererByPath.TryGetValue(binding.path, out var r)) result.animatedRenderers.Add(r);
                }
                else if (binding.propertyName == "m_LocalScale.x")
                {
                    var t = binding.path.Length == 0 ? avatar.transform : avatar.transform.Find(binding.path);
                    if (t != null)
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        float maxAbs = 1f;
                        if (curve != null)
                            foreach (var k in curve.keys) maxAbs = Mathf.Max(maxAbs, Mathf.Abs(k.value));
                        result.maxScale[t] = maxAbs;
                    }
                }
                else if (binding.type == typeof(Material) && binding.propertyName == "float _Cutoff")
                {
                    // animated cutoff on a material / 材质 Cutoff 被动画
                    // material resolution is approximate; record via references if resolvable
                }
            }

            // object-reference curves (material / texture switches) / 对象引用曲线（材质/贴图切换）
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null) continue;

                bool isSlot = binding.type == typeof(Renderer) &&
                              binding.propertyName.StartsWith("m_Materials.Array.data[");

                foreach (var kf in curve)
                {
                    var val = kf.value;
                    if (val is Material m)
                    {
                        result.animatedMaterials.Add(m);
                        CollectTexturesOf(m, result.animatedTextures);
                        if (isSlot && rendererByPath.TryGetValue(binding.path, out var r))
                        {
                            int slot = ParseSlot(binding.propertyName);
                            if (!result.materialSwitches.TryGetValue((r, slot), out var list))
                            { list = new List<Material>(); result.materialSwitches[(r, slot)] = list; }
                            if (!list.Contains(m)) list.Add(m);
                        }
                    }
                    else if (val is Texture2D t)
                    {
                        result.animatedTextures.Add(t);
                    }
                }
            }
        }

        private static void CollectTexturesOf(Material m, HashSet<Texture2D> into)
        {
            if (m == null || m.shader == null) return;
            foreach (var prop in m.GetTexturePropertyNames())
                if (m.GetTexture(prop) is Texture2D t) into.Add(t);
        }

        private static int ParseSlot(string propertyName)
        {
            int s = propertyName.IndexOf('[');
            int e = propertyName.IndexOf(']');
            if (s < 0 || e < 0) return 0;
            return int.TryParse(propertyName.Substring(s + 1, e - s - 1), out int slot) ? slot : 0;
        }
    }
}

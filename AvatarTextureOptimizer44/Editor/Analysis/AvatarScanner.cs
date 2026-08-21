// AvatarScanner.cs - Collect renderers, descriptor animator layers & clip inventory (post-MA, pre-AAO).
// 采集渲染器、描述符动画层与动画片段清单（MA后、AAO前）。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Fosa.ATO.Editor.Core;
#if ATO_VRCSDK3A
using VRC.SDK3.Avatars.Components;
#endif

namespace Fosa.ATO.Editor.Analysis
{
    /// <summary>Static snapshot of the avatar's render/animation surface. / Avatar渲染与动画面静态快照。</summary>
    public sealed class AvatarScan
    {
        public GameObject root;
        public readonly List<Renderer> renderers = new List<Renderer>();
        public readonly List<AnimationClip> clips = new List<AnimationClip>();
        public readonly Dictionary<Renderer, string> paths = new Dictionary<Renderer, string>();
        public readonly HashSet<string> animatedActivePaths = new HashSet<string>();
        /// <summary>Max |scale| per transform path from animations. / 动画中每个变换路径的最大缩放。</summary>
        public readonly Dictionary<string, float> maxAnimScale = new Dictionary<string, float>();
        /// <summary>Slot material swaps: (renderer path, slot) -> materials. / 材质槽切换。</summary>
        public readonly Dictionary<(string, int), HashSet<Material>> slotSwaps = new Dictionary<(string, int), HashSet<Material>>();
        /// <summary>Named/texture-prop object swaps. / 具名材质与贴图属性的对象切换。</summary>
        public readonly Dictionary<(string path, string prop), HashSet<UnityEngine.Object>> propSwaps = new Dictionary<(string, string), HashSet<UnityEngine.Object>>();
        /// <summary>Animated material float props: (path, propOrNamedProp) -> min/max. / 动画修改的材质浮点属性（含ST）。</summary>
        public readonly Dictionary<(string path, string prop), Vector2> floatProps = new Dictionary<(string, string), Vector2>();
        public readonly HashSet<Material> materialsInAnimations = new HashSet<Material>();
        public readonly HashSet<AnimationClip> whitelistClips = new HashSet<AnimationClip>();

        /// <summary>Is a renderer eligible (enabled now or animated active, not EditorOnly)? / 渲染器是否合格（当前或动画启用，且非EditorOnly）。</summary>
        public bool RendererEligible(Renderer r, string path)
        {
            if (r == null) return false;
            if (IsEditorOnly(r.gameObject)) return false;
            if (r is SkinnedMeshRenderer || r is MeshRenderer) { /* only these two types / 仅这两种 */ }
            else return false;
            if (r.gameObject.activeInHierarchy) return true;
            return animatedActivePaths.Contains(path); // animated on / 动画开启
        }

        private static bool IsEditorOnly(GameObject go)
        {
            // check self and ancestors / 检查自身与祖先
            for (var t = go.transform; t != null; t = t.parent)
                if (t.gameObject.CompareTag("EditorOnly")) return true;
            return false;
        }
    }

    public static class AvatarScanner
    {
        /// <summary>Scan the whole avatar. / 扫描整个Avatar。</summary>
        public static AvatarScan Scan(BuildContext ctx)
        {
            using (ATOLog.Scope("ScanAvatar"))
            {
                var scan = new AvatarScan { root = ctx.AvatarRootObject };
                CollectRenderers(ctx, scan);
                CollectClips(ctx, scan);
                AnimationScanner.AnalyzeClips(scan);
                ATOLog.Detail($"renderers={scan.renderers.Count} clips={scan.clips.Count} slotSwaps={scan.slotSwaps.Count} propSwaps={scan.propSwaps.Count} animatedActive={scan.animatedActivePaths.Count}");
                return scan;
            }
        }

        private static void CollectRenderers(BuildContext ctx, AvatarScan scan)
        {
            foreach (var r in ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                string path = RelativePath(ctx.AvatarRootTransform, r.transform);
                if (!scan.RendererEligible(r, path)) { ATOLog.Detail($"skip renderer (inactive/not animated/EditorOnly): {path}"); continue; }
                scan.renderers.Add(r); scan.paths[r] = path;
            }
        }

        private static void CollectClips(BuildContext ctx, AvatarScan scan)
        {
            var seen = new HashSet<AnimationClip>();
#if ATO_VRCSDK3A
            var desc = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                AddLayers(desc.baseAnimationLayers, seen, scan);
                AddLayers(desc.specialAnimationLayers, seen, scan);
            }
#endif
            // plain Animators on the avatar (rare but possible) / Avatar上的普通Animator（少见但可能存在）
            foreach (var anim in ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true))
            {
                if (anim.runtimeAnimatorController != null) AddController(anim.runtimeAnimatorController, seen, scan);
            }
            scan.clips.AddRange(seen);
        }

#if ATO_VRCSDK3A
        private static void AddLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers, HashSet<AnimationClip> seen, AvatarScan scan)
        {
            if (layers == null) return;
            foreach (var l in layers)
            {
                if (l.animatorController == null) continue;
                AddController(l.animatorController, seen, scan);
            }
        }
#endif

        private static void AddController(RuntimeAnimatorController c, HashSet<AnimationClip> seen, AvatarScan scan)
        {
            // animationClips enumerates every clip incl. blend tree leaves / animationClips 枚举全部片段（含混合树叶）
            foreach (var clip in c.animationClips)
            {
                if (clip != null && seen.Add(clip)) { /* collected / 已收集 */ }
            }
        }

        /// <summary>Path relative to avatar root. / 相对Avatar根的路径。</summary>
        public static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.parent; p != null && p != root; p = p.parent) { sb.Insert(0, "/").Insert(0, p.name); }
            return sb.ToString();
        }
    }
}

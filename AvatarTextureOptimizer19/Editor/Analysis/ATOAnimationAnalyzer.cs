// English: Walk NDMF VirtualClip curves for enable, scale, material/texture swaps, ST, cutoff, blend mode.
// 中文：遍历 NDMF VirtualClip 曲线，收集启用、缩放、材质/贴图切换、ST、Cutoff、混合模式。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class ATOAnimImpact
    {
        public readonly HashSet<string> EnabledPaths = new HashSet<string>();
        public readonly Dictionary<string, Vector3> MaxScale = new Dictionary<string, Vector3>();
        public readonly Dictionary<string, List<MaterialSwap>> MaterialSwaps = new Dictionary<string, List<MaterialSwap>>();
        public readonly HashSet<string> StAnimatedProps = new HashSet<string>(); // path|type|prop
        public readonly List<CutoffAnim> Cutoffs = new List<CutoffAnim>();
        public readonly List<BlendAnim> Blends = new List<BlendAnim>();
        public readonly HashSet<Texture2D> ExtraTextures = new HashSet<Texture2D>();

        public struct MaterialSwap
        {
            public string Path;
            public int Slot;
            public Material Material;
            public Texture2D Texture;
            public string TextureProperty;
        }

        public struct CutoffAnim
        {
            public string Path;
            public float MaxCutoff;
        }

        public struct BlendAnim
        {
            public string Path;
            public bool ForcesCutout;
            public bool ForcesBlend;
        }
    }

    internal static class ATOAnimationAnalyzer
    {
        public static ATOAnimImpact Analyze(ATOState state)
        {
            var impact = new ATOAnimImpact();
            if (state.Anim == null)
            {
                state.Log.Warn("AnimatorServicesContext missing; animation analysis skipped");
                return impact;
            }

            var index = state.Anim.AnimationIndex;
            var remapper = state.Anim.ObjectPathRemapper;
            var clips = new HashSet<VirtualClip>();
            foreach (var clip in index.ClipsWithObjectCurves) clips.Add(clip);

            var xforms = state.Build.AvatarRootObject.GetComponentsInChildren<Transform>(true);
            foreach (var x in xforms)
            {
                if (x == null) continue;
                IEnumerable<string> paths = remapper != null
                    ? remapper.GetAllPathsForObject(x)
                    : new[] { AnimationUtility.CalculateTransformPath(x, state.Build.AvatarRootTransform) };
                foreach (var path in paths)
                {
                    foreach (var clip in index.GetClipsForObjectPath(path))
                        clips.Add(clip);
                }
            }

            state.Log.VerboseInfo("animation clips indexed=" + clips.Count);

            foreach (var clip in clips)
            {
                InspectFloat(clip, impact);
                InspectPPtr(clip, impact, state);
            }

            state.Log.Info("anim enable-paths=" + impact.EnabledPaths.Count +
                           " scale-paths=" + impact.MaxScale.Count +
                           " extra-textures=" + impact.ExtraTextures.Count +
                           " ST-animated=" + impact.StAnimatedProps.Count);
            return impact;
        }

        private static IEnumerable<VirtualClip> CollectAllClips(AnimationIndex index)
        {
            var set = new HashSet<VirtualClip>();
            foreach (var clip in index.ClipsWithObjectCurves) set.Add(clip);
            // Float-only clips are reached via GetClipsForObjectPath of every renderer later;
            // also pull by scanning a dummy empty path plus known roots.
            foreach (var c in set) yield return c;
        }

        public static void AttachRendererCurves(ATOState state, ATOAnimImpact impact)
        {
            if (state.Anim == null) return;
            var index = state.Anim.AnimationIndex;
            var remapper = state.Anim.ObjectPathRemapper;
            foreach (var info in state.Renderers)
            {
                if (info.Renderer == null) continue;
                string path;
                try
                {
                    path = remapper != null
                        ? remapper.GetVirtualPathForObject(info.Renderer.transform)
                        : AnimationUtility.CalculateTransformPath(info.Renderer.transform, state.Build.AvatarRootTransform);
                }
                catch
                {
                    path = AnimationUtility.CalculateTransformPath(info.Renderer.transform, state.Build.AvatarRootTransform);
                }

                foreach (var clip in index.GetClipsForObjectPath(path))
                {
                    InspectFloat(clip, impact);
                    InspectPPtr(clip, impact, state);
                }

                if (impact.EnabledPaths.Contains(path)) info.AnimatedEnable = true;
                Vector3 maxs;
                if (impact.MaxScale.TryGetValue(path, out maxs))
                    info.MaxAbsScale = new Vector3(Mathf.Max(info.MaxAbsScale.x, maxs.x),
                        Mathf.Max(info.MaxAbsScale.y, maxs.y),
                        Mathf.Max(info.MaxAbsScale.z, maxs.z));
            }
        }

        private static void InspectFloat(VirtualClip clip, ATOAnimImpact impact)
        {
            foreach (var b in clip.GetFloatCurveBindings())
            {
                var curve = clip.GetFloatCurve(b);
                if (curve == null || curve.length == 0) continue;
                var prop = b.propertyName ?? "";
                if (prop == "m_IsActive" || prop == "m_Enabled")
                {
                    for (var i = 0; i < curve.length; i++)
                    {
                        if (curve[i].value > 0.5f) impact.EnabledPaths.Add(b.path);
                    }
                }
                else if (prop.StartsWith("m_LocalScale", StringComparison.Ordinal) ||
                         prop.StartsWith("localScale", StringComparison.Ordinal))
                {
                    var max = 0f;
                    for (var i = 0; i < curve.length; i++)
                        max = Mathf.Max(max, Mathf.Abs(curve[i].value));
                    Vector3 cur;
                    if (!impact.MaxScale.TryGetValue(b.path, out cur)) cur = Vector3.zero;
                    if (prop.EndsWith(".x")) cur.x = Mathf.Max(cur.x, max);
                    else if (prop.EndsWith(".y")) cur.y = Mathf.Max(cur.y, max);
                    else if (prop.EndsWith(".z")) cur.z = Mathf.Max(cur.z, max);
                    else
                    {
                        cur.x = Mathf.Max(cur.x, max);
                        cur.y = Mathf.Max(cur.y, max);
                        cur.z = Mathf.Max(cur.z, max);
                    }

                    impact.MaxScale[b.path] = cur;
                }
                else if (IsStProperty(prop))
                {
                    impact.StAnimatedProps.Add(b.path + "|" + b.type.Name + "|" + prop);
                }
                else if (prop.IndexOf("Cutoff", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var max = 0f;
                    for (var i = 0; i < curve.length; i++) max = Mathf.Max(max, curve[i].value);
                    impact.Cutoffs.Add(new ATOAnimImpact.CutoffAnim { Path = b.path, MaxCutoff = max });
                }
                else if (prop.IndexOf("TransparentMode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         prop.IndexOf("_Mode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         prop.IndexOf("BlendMode", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var forcesCutout = false;
                    var forcesBlend = false;
                    for (var i = 0; i < curve.length; i++)
                    {
                        var v = Mathf.RoundToInt(curve[i].value);
                        if (v == 1) forcesCutout = true;
                        if (v >= 2) forcesBlend = true;
                    }

                    impact.Blends.Add(new ATOAnimImpact.BlendAnim
                    {
                        Path = b.path,
                        ForcesCutout = forcesCutout,
                        ForcesBlend = forcesBlend
                    });
                }
            }
        }

        private static void InspectPPtr(VirtualClip clip, ATOAnimImpact impact, ATOState state)
        {
            foreach (var b in clip.GetObjectCurveBindings())
            {
                var keys = clip.GetObjectCurve(b);
                if (keys == null) continue;
                var prop = b.propertyName ?? "";
                var slot = ParseMaterialSlot(prop);
                foreach (var k in keys)
                {
                    if (k.value == null) continue;
                    var mat = k.value as Material;
                    if (mat != null)
                    {
                        List<ATOAnimImpact.MaterialSwap> list;
                        if (!impact.MaterialSwaps.TryGetValue(b.path, out list))
                        {
                            list = new List<ATOAnimImpact.MaterialSwap>();
                            impact.MaterialSwaps[b.path] = list;
                        }

                        list.Add(new ATOAnimImpact.MaterialSwap
                        {
                            Path = b.path,
                            Slot = slot,
                            Material = mat
                        });
                        ATOWhitelist.CollectFrom(mat, impact.ExtraTextures, 0);
                    }

                    var tex = k.value as Texture2D;
                    if (tex != null)
                    {
                        impact.ExtraTextures.Add(tex);
                        List<ATOAnimImpact.MaterialSwap> list;
                        if (!impact.MaterialSwaps.TryGetValue(b.path, out list))
                        {
                            list = new List<ATOAnimImpact.MaterialSwap>();
                            impact.MaterialSwaps[b.path] = list;
                        }

                        list.Add(new ATOAnimImpact.MaterialSwap
                        {
                            Path = b.path,
                            Slot = slot,
                            Texture = tex,
                            TextureProperty = StripMaterialPrefix(prop)
                        });
                    }
                }

                if (slot >= 0)
                {
                    // mark later on renderer
                }

                if (IsStProperty(prop))
                {
                    impact.StAnimatedProps.Add(b.path + "|" + b.type.Name + "|" + prop);
                }
            }
        }

        internal static bool IsStProperty(string prop)
        {
            if (string.IsNullOrEmpty(prop)) return false;
            if (prop.IndexOf("_ST", StringComparison.Ordinal) >= 0) return true;
            if (prop.IndexOf("ScrollRotate", StringComparison.Ordinal) >= 0) return true;
            if (prop.IndexOf("Offset", StringComparison.Ordinal) >= 0 && prop.IndexOf("Tex", StringComparison.Ordinal) >= 0)
                return true;
            if (prop.IndexOf("Scale", StringComparison.Ordinal) >= 0 && prop.IndexOf("Tex", StringComparison.Ordinal) >= 0)
                return true;
            if (prop.EndsWith("Angle", StringComparison.Ordinal)) return true;
            if (prop.IndexOf("IsDecal", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        internal static int ParseMaterialSlot(string prop)
        {
            // m_Materials.Array.data[2]  or  material  (slot 0)
            if (string.IsNullOrEmpty(prop)) return -1;
            const string marker = "m_Materials.Array.data[";
            var i = prop.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) i = prop.IndexOf("Material.Array.data[", StringComparison.Ordinal);
            if (i < 0)
            {
                if (prop == "material" || prop.StartsWith("material.", StringComparison.Ordinal)) return 0;
                return -1;
            }

            var start = prop.IndexOf('[', i) + 1;
            var end = prop.IndexOf(']', start);
            if (start <= 0 || end <= start) return -1;
            int slot;
            return int.TryParse(prop.Substring(start, end - start), out slot) ? slot : -1;
        }

        private static string StripMaterialPrefix(string prop)
        {
            if (string.IsNullOrEmpty(prop)) return prop;
            var i = prop.LastIndexOf('.');
            return i >= 0 ? prop.Substring(i + 1) : prop;
        }
    }
}

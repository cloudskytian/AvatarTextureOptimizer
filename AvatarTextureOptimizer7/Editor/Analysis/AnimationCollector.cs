using System;
using System.Collections.Generic;
using System.Globalization;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Reads NDMF virtual clips for material swaps, texture swaps, ST, cutoff, enable, scale, blendshapes.
    /// 读取 NDMF 虚拟 Clip：材质/贴图切换、ST、Cutoff、启用、缩放、形态键。
    /// </summary>
    public sealed class AnimationCollector
    {
        public struct RendererAnim
        {
            public bool Enables;
            public bool Disables;
            public float MaxScaleSqr;
            public readonly HashSet<int> SwitchedSlots;
            public readonly Dictionary<int, List<Material>> SlotMaterials;
            public readonly Dictionary<string, List<Texture2D>> PropertyTextures;
            public readonly Dictionary<string, bool> AnimatedProperties;
            public bool HasUvTransform;
            public readonly List<float> Cutoffs;
            public readonly HashSet<int> AlphaModeHints;
            public readonly Dictionary<string, float> MaxBlendshapeAbs;

            public RendererAnim(int dummy)
            {
                Enables = false;
                Disables = false;
                MaxScaleSqr = 1f;
                SwitchedSlots = new HashSet<int>();
                SlotMaterials = new Dictionary<int, List<Material>>();
                PropertyTextures = new Dictionary<string, List<Texture2D>>();
                AnimatedProperties = new Dictionary<string, bool>();
                HasUvTransform = false;
                Cutoffs = new List<float>();
                AlphaModeHints = new HashSet<int>();
                MaxBlendshapeAbs = new Dictionary<string, float>();
            }
        }

        public readonly Dictionary<Renderer, RendererAnim> PerRenderer = new Dictionary<Renderer, RendererAnim>();
        public readonly Dictionary<string, RendererAnim> PerPath = new Dictionary<string, RendererAnim>(StringComparer.Ordinal);

        public static AnimationCollector Collect(AnimatorServicesContext anim, Transform avatarRoot, AtoLog log)
        {
            var col = new AnimationCollector();
            if (anim == null)
            {
                log?.Warn("AnimatorServicesContext missing; animation analysis skipped.");
                return col;
            }

            var index = anim.AnimationIndex;
            var remapper = anim.ObjectPathRemapper;
            int clips = 0;

            foreach (var clip in index.ClipsWithObjectCurves)
            {
                clips++;
                foreach (var b in clip.GetObjectCurveBindings())
                {
                    var curve = clip.GetObjectCurve(b);
                    if (curve == null) continue;
                    var animRec = col.Get(b.path);
                    foreach (var kf in curve)
                    {
                        if (kf.value is Material m)
                        {
                            var slot = ParseMaterialSlot(b.propertyName);
                            if (slot >= 0)
                            {
                                animRec.SwitchedSlots.Add(slot);
                                if (!animRec.SlotMaterials.TryGetValue(slot, out var list))
                                {
                                    list = new List<Material>();
                                    animRec.SlotMaterials[slot] = list;
                                }

                                if (!list.Contains(m)) list.Add(m);
                            }
                        }
                        else if (kf.value is Texture2D t)
                        {
                            var prop = ParseMaterialTextureProperty(b.propertyName);
                            if (prop != null)
                            {
                                if (!animRec.PropertyTextures.TryGetValue(prop, out var list))
                                {
                                    list = new List<Texture2D>();
                                    animRec.PropertyTextures[prop] = list;
                                }

                                if (!list.Contains(t)) list.Add(t);
                                animRec.AnimatedProperties[prop] = true;
                            }
                        }
                    }

                    col.Set(b.path, animRec);
                }
            }

            // Float curves: enable, scale, ST, cutoff, blendshapes. / 浮点曲线。
            // We cannot enumerate all bindings cheaply; walk clips via GetClipsForObjectPath after we know renderers.
            // 先记下全部 clip 的 float binding 需要渲染器路径，在 BindRenderers 里补。
            log?.VerboseInfo("Animation object-curve clips: " + clips.ToString(CultureInfo.InvariantCulture));
            col._index = index;
            col._root = avatarRoot;
            return col;
        }

        AnimationIndex _index;
        Transform _root;

        public void BindRenderers(IEnumerable<Renderer> renderers, AtoLog log)
        {
            if (_index == null || _root == null) return;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var path = AnimationUtility.CalculateTransformPath(r.transform, _root);
                // Also empty path if renderer is on root. / 根上渲染器路径为空。
                ScanFloats(path, r);
                if (PerPath.TryGetValue(path, out var rec))
                    PerRenderer[r] = rec;
                else if (PerPath.TryGetValue("", out rec) && r.transform == _root)
                    PerRenderer[r] = rec;
            }
        }

        void ScanFloats(string path, Renderer renderer)
        {
            var rec = Get(path);
            foreach (var clip in _index.GetClipsForObjectPath(path))
            {
                foreach (var b in clip.GetFloatCurveBindings())
                {
                    if (!string.Equals(b.path, path, StringComparison.Ordinal)) continue;
                    var curve = clip.GetFloatCurve(b);
                    if (curve == null || curve.length == 0) continue;
                    rec.AnimatedProperties[b.propertyName] = true;

                    if (b.propertyName == "m_IsActive" || b.propertyName == "m_Enabled")
                    {
                        foreach (var k in curve.keys)
                        {
                            if (k.value > 0.5f) rec.Enables = true;
                            else rec.Disables = true;
                        }
                    }
                    else if (IsScaleProp(b.propertyName))
                    {
                        var maxAbs = 0f;
                        foreach (var k in curve.keys) maxAbs = Mathf.Max(maxAbs, Mathf.Abs(k.value));
                        rec.MaxScaleSqr = Mathf.Max(rec.MaxScaleSqr, maxAbs * maxAbs);
                    }
                    else if (b.propertyName.IndexOf("_ST", StringComparison.Ordinal) >= 0 ||
                             b.propertyName.IndexOf("ScrollRotate", StringComparison.Ordinal) >= 0 ||
                             b.propertyName.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) >= 0 &&
                             b.propertyName.IndexOf("material.", StringComparison.Ordinal) >= 0)
                    {
                        rec.HasUvTransform = true;
                    }
                    else if (b.propertyName.IndexOf("_Cutoff", StringComparison.Ordinal) >= 0)
                    {
                        foreach (var k in curve.keys) rec.Cutoffs.Add(k.value);
                    }
                    else if (b.propertyName.IndexOf("_Mode", StringComparison.Ordinal) >= 0 ||
                             b.propertyName.IndexOf("Transparent", StringComparison.Ordinal) >= 0)
                    {
                        foreach (var k in curve.keys) rec.AlphaModeHints.Add(Mathf.RoundToInt(k.value));
                    }
                    else if (b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    {
                        var name = b.propertyName.Substring("blendShape.".Length);
                        var maxAbs = 0f;
                        foreach (var k in curve.keys) maxAbs = Mathf.Max(maxAbs, Mathf.Abs(k.value));
                        if (!rec.MaxBlendshapeAbs.TryGetValue(name, out var prev) || maxAbs > prev)
                            rec.MaxBlendshapeAbs[name] = maxAbs;
                    }
                }
            }

            Set(path, rec);
            PerRenderer[renderer] = rec;
        }

        static bool IsScaleProp(string p)
        {
            return p == "m_LocalScale.x" || p == "m_LocalScale.y" || p == "m_LocalScale.z" ||
                   p == "localScale.x" || p == "localScale.y" || p == "localScale.z";
        }

        public static int ParseMaterialSlot(string property)
        {
            // m_Materials.Array.data[N]
            if (string.IsNullOrEmpty(property)) return -1;
            const string mark = "m_Materials.Array.data[";
            var i = property.IndexOf(mark, StringComparison.Ordinal);
            if (i < 0) return -1;
            i += mark.Length;
            var j = property.IndexOf(']', i);
            if (j < 0) return -1;
            return int.TryParse(property.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : -1;
        }

        public static string ParseMaterialTextureProperty(string property)
        {
            if (string.IsNullOrEmpty(property)) return null;
            // material._MainTex  or  m_SavedProperties...
            const string prefix = "material.";
            if (property.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = property.Substring(prefix.Length);
                var dot = rest.IndexOf('.');
                return dot >= 0 ? rest.Substring(0, dot) : rest;
            }

            return null;
        }

        RendererAnim Get(string path)
        {
            if (!PerPath.TryGetValue(path ?? "", out var rec))
                rec = new RendererAnim(0);
            return rec;
        }

        void Set(string path, RendererAnim rec) => PerPath[path ?? ""] = rec;
    }
}

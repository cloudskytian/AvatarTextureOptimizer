// ============================================================================
// ATO - animation scanner
// ATO - 动画扫描器
//
// Scans every AnimationClip reachable from the avatar (all Animators +
// VRCAvatarDescriptor layer controllers) and records:
//   - material swaps (object references on m_Materials.Array.data[i])
//   - texture swaps  (object references on material texture properties)
//   - ST animation  (any _ST. float curve on a material property)
//   - cutoff / render-mode animation (strictest values)
//   - transform scale animation (max local scale per GameObject)
//   - game object enable/disable animation (renderer may be "animated on")
// All analysis uses the public AnimationUtility binding APIs (verified
// against AAO's ObjectMapping implementation).
// 扫描 Avatar 可达的全部 AnimationClip（所有 Animator + VRCAvatarDescriptor 各
// 层控制器），记录：材质切换（m_Materials.Array.data[i] 对象引用）、贴图切换
// （材质贴图属性对象引用）、ST 动画、Cutoff/渲染模式动画（取最严）、变换缩放
// 动画（每 GameObject 最大局部缩放）、GameObject 开关动画（渲染器可能被动画
// 启用）。全部使用公开 AnimationUtility binding API（已对照 AAO 实现验证）。
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Analysis
{
    public sealed class ATOSwappedMaterial
    {
        public Renderer Renderer;
        public int Slot;
        public Material SwappedIn;
        public string SourceClip;
    }

    public sealed class ATOSwappedTexture
    {
        public Material Material;
        public string Property;
        public Texture2D SwappedIn;
        public string SourceClip;
    }

    public sealed class ATOAnimationScan
    {
        /// <summary>(material, property) that has ANY non-zero ST animation
        /// (offset or scale != 0/1 at any key). 任意关键帧 ST 非零的材质贴图。</summary>
        public readonly HashSet<(Material, string)> StAnimated = new();
        /// <summary>material -> min/max cutoff. 材质 -> 裁剪阈值范围。</summary>
        public readonly Dictionary<Material, (float min, float max)> Cutoffs = new();
        /// <summary>material -> min/max subpass cutoff. 材质 -> 子通道裁剪范围。</summary>
        public readonly Dictionary<Material, (float min, float max)> SubpassCutoffs = new();
        /// <summary>material -> set of animated _TransparentMode int values.
        /// 材质 -> 动画过的透明模式值集合。</summary>
        public readonly Dictionary<Material, HashSet<int>> TransparentModes = new();
        /// <summary>gameObject -> max |localScale.x * localScale.y|.
        /// GameObject -> 最大 |局部缩放.x*y|。</summary>
        public readonly Dictionary<GameObject, float> MaxScaleArea = new();
        /// <summary>Renderers enabled by animation (m_IsActive animated).
        /// 被动画启用的渲染器。</summary>
        public readonly HashSet<Renderer> AnimationEnabled = new();
        /// <summary>Materials swapped into slots by animation.
        /// 动画切换进材质槽的材质。</summary>
        public readonly List<ATOSwappedMaterial> SwappedMaterials = new();
        /// <summary>Textures swapped into material properties by animation.
        /// 动画切换进材质属性的贴图。</summary>
        public readonly List<ATOSwappedTexture> SwappedTextures = new();
        /// <summary>All clips scanned (for later rewriting).
        /// 全部已扫描 clip（供后续改写）。</summary>
        public readonly HashSet<AnimationClip> Clips = new();
        public int ClipCount;

        public static ATOAnimationScan Scan(GameObject avatarRoot)
        {
            var scan = new ATOAnimationScan();

            var controllers = new HashSet<AnimatorController>();
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (animator.gameObject.CompareTag("EditorOnly")) continue;
                if (animator.controller != null) controllers.Add(animator.controller);
            }

            // VRCAvatarDescriptor layer controllers 描述符各层控制器
            var descriptor = avatarRoot.GetComponentInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                using (var so = new SerializedObject(descriptor))
                {
                    var arr = so.FindProperty("controllers");
                    if (arr != null && arr.isArray)
                    {
                        for (int i = 0; i < arr.arraySize; i++)
                        {
                            var c = arr.GetArrayElementAtIndex(i).objectReferenceValue as AnimatorController;
                            if (c != null) controllers.Add(c);
                        }
                    }
                }
            }

            var st = new ATOScanState(scan);
            foreach (var c in controllers)
            {
                foreach (var clip in CollectClips(c))
                {
                    if (!scan.Clips.Add(clip)) continue;
                    ScanClip(st, clip);
                }
            }
            scan.ClipCount = scan.Clips.Count;
            return scan;
        }

        private static IEnumerable<AnimationClip> CollectClips(AnimatorController controller)
        {
            foreach (var layer in controller.layers)
            {
                foreach (var clip in CollectFromStateMachine(layer.stateMachine))
                {
                    yield return clip;
                }
            }
        }

        private static IEnumerable<AnimationClip> CollectFromStateMachine(AnimatorStateMachine sm)
        {
            foreach (var state in sm.states)
            {
                var m = state.stateMotion;
                if (m is AnimationClip clip) yield return clip;
                else if (m is AnimationClip[] clips)
                {
                    foreach (var c in clips) yield return c;
                }
                else if (m is BlendTree tree)
                {
                    for (int i = 0; i < tree.children.Count; i++)
                    {
                        var child = tree.children[i].motion;
                        if (child is AnimationClip cc) yield return cc;
                        else if (child is AnimationClip[] ccs)
                        {
                            foreach (var c in ccs) yield return c;
                        }
                    }
                }
            }
            foreach (var child in sm.subStateMachines)
            {
                foreach (var clip in CollectFromStateMachine(child)) yield return clip;
            }
        }

        private static void ScanClip(ATOScanState st, AnimationClip clip)
        {
            // ---- float / vector curves 浮点/向量曲线 ----
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                // Transform scale  变换缩放
                if (b.type == typeof(Transform) && b.propertyName == "m_LocalScale")
                {
                    if (AnimationUtility.GetEditorVectorCurve(clip, b, out var curves) && curves != null && curves.Length == 3)
                    {
                        float area = 1f;
                        foreach (var k in curves[0].keys)
                            foreach (var ky in curves[1].keys)
                            {
                                var a = Mathf.Abs(k.value * ky.value);
                                if (a > area) area = a;
                            }
                        if (st.Scan.MaxScaleArea.TryGetValue(GetTarget(st, b), out var prev))
                        {
                            if (area > prev) st.Scan.MaxScaleArea[GetTarget(st, b)] = area;
                        }
                        else st.Scan.MaxScaleArea[GetTarget(st, b)] = Mathf.Max(1f, area);
                    }
                    continue;
                }

                // Game object active  对象开关
                if ((b.type == typeof(Transform) || b.type == typeof(GameObject)) && b.propertyName == "m_IsActive")
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve != null)
                    {
                        bool hasOne = false;
                        foreach (var k in curve.keys) if (k.value > 0.5f) { hasOne = true; break; }
                        if (hasOne)
                        {
                            var t = b.target as Transform;
                            if (t != null)
                            {
                                foreach (var r in t.GetComponents<Renderer>()) st.Scan.AnimationEnabled.Add(r);
                            }
                        }
                    }
                    continue;
                }

                // Material properties  材质属性
                if (b.target is Material mat)
                {
                    if (b.propertyName.Contains("_ST."))
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, b);
                        var vec = AnimationUtility.GetEditorVectorCurve(clip, b, out var vecCurves);
                        bool nonZero = false;
                        if (curve != null)
                        {
                            foreach (var k in curve.keys) if (!Mathf.Approximately(k.value, 0f)) { nonZero = true; break; }
                        }
                        if (vec && vecCurves != null)
                        {
                            foreach (var c in vecCurves)
                                foreach (var k in c.keys)
                                    if (b.propertyName.Contains("Offset") ? !Mathf.Approximately(k.value, 0f) : !Mathf.Approximately(k.value, 1f))
                                    {
                                        nonZero = true;
                                        break;
                                    }
                        }
                        if (nonZero)
                        {
                            // property "_Xxx_ST._Offset.x" -> "_Xxx"
                            // 属性 "_Xxx_ST._Offset.x" -> "_Xxx"
                            int idx = b.propertyName.IndexOf("_ST.", System.StringComparison.Ordinal);
                            if (idx > 0) st.Scan.StAnimated.Add((mat, b.propertyName.Substring(0, idx)));
                        }
                        continue;
                    }

                    if (b.propertyName == "_Cutoff" && AnimationUtility.GetEditorCurve(clip, b, out var c1) && c1 != null)
                    {
                        UpdateRange(st.Scan.Cutoffs, mat, c1);
                    }
                    else if (b.propertyName == "_SubpassCutoff" && AnimationUtility.GetEditorCurve(clip, b, out var c2) && c2 != null)
                    {
                        UpdateRange(st.Scan.SubpassCutoffs, mat, c2);
                    }
                    else if (b.propertyName == "_TransparentMode" && AnimationUtility.GetEditorCurve(clip, b, out var c3) && c3 != null)
                    {
                        if (!st.Scan.TransparentModes.TryGetValue(mat, out var set))
                        {
                            set = new HashSet<int>();
                            st.Scan.TransparentModes[mat] = set;
                        }
                        foreach (var k in c3.keys) set.Add((int) Mathf.Round(k.value));
                    }
                }
            }

            // ---- object reference curves 对象引用曲线 ----
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var frames = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (frames == null || frames.Length == 0) continue;

                if (b.target is Material mat)
                {
                    // texture swap  贴图切换
                    foreach (var f in frames)
                    {
                        if (f.value is Texture2D tex)
                        {
                            st.Scan.SwappedTextures.Add(new ATOSwappedTexture
                            {
                                Material = mat,
                                Property = b.propertyName,
                                SwappedIn = tex,
                                SourceClip = clip.name,
                            });
                        }
                    }
                }
                else if (b.target is Renderer r && b.propertyName.StartsWith("m_Materials.Array.data["))
                {
                    // material slot swap  材质槽切换
                    int bracket = b.propertyName.LastIndexOf(']');
                    int slot = int.Parse(b.propertyName.Substring("m_Materials.Array.data[".Length, bracket - "m_Materials.Array.data[".Length));
                    foreach (var f in frames)
                    {
                        if (f.value is Material m2)
                        {
                            st.Scan.SwappedMaterials.Add(new ATOSwappedMaterial
                            {
                                Renderer = r,
                                Slot = slot,
                                SwappedIn = m2,
                                SourceClip = clip.name,
                            });
                        }
                    }
                }
            }
        }

        private sealed class ATOScanState
        {
            public readonly ATOAnimationScan Scan;
            public ATOScanState(ATOAnimationScan scan) { Scan = scan; }
            public GameObject GetTargetGO(Object t)
            {
                if (t is Transform tr) return tr.gameObject;
                if (t is GameObject go) return go;
                if (t is Component comp && comp.gameObject != null) return comp.gameObject;
                return null;
            }
        }

        private static GameObject GetTarget(ATOScanState st, EditorCurveBinding b)
        {
            return st.GetTargetGO(b.target);
        }

        private static void UpdateRange(Dictionary<Material, (float min, float max)> dict, Material m, AnimationCurve c)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var k in c.keys)
            {
                if (k.value < min) min = k.value;
                if (k.value > max) max = k.value;
            }
            if (!dict.TryGetValue(m, out var prev)) dict[m] = (min, max);
            else dict[m] = (Mathf.Min(prev.min, min), Mathf.Max(prev.max, max));
        }
    }
}

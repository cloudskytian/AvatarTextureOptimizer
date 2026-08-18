// BlendShapeAnalyzer.cs / BlendShapeAnalyzer.cs
// Computes per-triangle world-space areas considering blendshapes at weight 0 and weight 100,
// taking the maximum of the two to bound the worst-case pixel density required.
// Also accounts for animation-driven object scaling.
// 计算考虑morph weight=0和weight=100下每个三角面的世界空间面积，取两者最大值以约束最坏情况下所需的像素密度。
// 同时考虑动画驱动的对象缩放。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    public static class BlendShapeAnalyzer
    {
        /// <summary>
        /// Compute maximum per-triangle area for a mesh, considering bindpose and blendshapes at 0 and 100 weight.
        /// Triangles are enumerated in submesh order (submesh 0 first, then submesh 1, ...), matching
        /// the order returned by mesh.GetTriangles(submesh) concatenated. Each value corresponds to
        /// one triangle in that global ordering.
        /// 计算网格的每三角面最大面积，考虑bindpose和morph 0/100 weight。三角形按子网格顺序枚举（先子网格0，再子网格1...）。
        /// Returns an array of max triangle world areas after applying the renderer transform scale.
        /// 返回应用渲染器变换缩放后，全局顺序下每个三角面的最大世界空间面积数组。
        /// </summary>
        public static float[] ComputeMaxTriangleAreas(SkinnedMeshRenderer smr, Mesh mesh, Transform root)
        {
            Vector3[] baseVerts = mesh.vertices;
            // Build global triangle index list in submesh order matching mesh.GetTriangles concatenation
            // 按子网格顺序构建全局三角面索引列表（与mesh.GetTriangles拼接顺序一致）
            var allTris = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sub = new List<int>();
                mesh.GetTriangles(sub, s);
                allTris.AddRange(sub);
            }
            int triCount = allTris.Count / 3;
            float[] areas = new float[triCount];

            Matrix4x4 localToWorld;
            Vector3 maxScale;
            if (smr != null)
            {
                localToWorld = smr.transform.localToWorldMatrix;
                maxScale = MaxScaleFromAnimator(smr, root);
            }
            else
            {
                localToWorld = root.localToWorldMatrix;
                maxScale = Vector3.one;
            }

            // Apply scale max (don't shrink below 1; animation scale up only increases required resolution)
            // 应用最大缩放（不要缩小到1以下；动画放大会增加所需分辨率）
            float scaleFactor = Mathf.Max(Mathf.Abs(maxScale.x), Mathf.Abs(maxScale.y), Mathf.Abs(maxScale.z));
            scaleFactor = Mathf.Max(scaleFactor, 1f);

            // Compute base areas / 计算基础面积
            for (int t = 0; t < allTris.Count / 3; t++)
            {
                int i0 = allTris[t*3], i1 = allTris[t*3+1], i2 = allTris[t*3+2];
                Vector3 a = localToWorld.MultiplyPoint3x4(baseVerts[i0]) * scaleFactor;
                Vector3 b = localToWorld.MultiplyPoint3x4(baseVerts[i1]) * scaleFactor;
                Vector3 c = localToWorld.MultiplyPoint3x4(baseVerts[i2]) * scaleFactor;
                areas[t] = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }

            // For each blendshape, apply deltas at weight 100 and recompute areas; take max
            // 对每个morph，应用weight=100的delta并重新计算面积；取最大值
            int bsCount = mesh.blendShapeCount;
            if (bsCount == 0) return areas;
            Vector3[] deltaVerts = new Vector3[baseVerts.Length];
            Vector3[] deltaNormals = new Vector3[baseVerts.Length];
            Vector3[] deltaTangents = new Vector3[baseVerts.Length];
            for (int bs = 0; bs < bsCount; bs++)
            {
                System.Array.Copy(baseVerts, deltaVerts, baseVerts.Length);
                mesh.GetBlendShapeFrameVertices(bs, 0, deltaVerts, deltaNormals, deltaTangents);
                float weight = mesh.GetBlendShapeFrameWeight(bs, 0);
                // Frame 0 is typically the 100%-weight frame for most blendshapes
                // 对大多数morph来说第0帧就是weight=100帧

                for (int t = 0; t < allTris.Count / 3; t++)
                {
                    int i0 = allTris[t*3], i1 = allTris[t*3+1], i2 = allTris[t*3+2];
                    Vector3 a0 = baseVerts[i0] + deltaVerts[i0];
                    Vector3 b0 = baseVerts[i1] + deltaVerts[i1];
                    Vector3 c0 = baseVerts[i2] + deltaVerts[i2];
                    Vector3 wa = localToWorld.MultiplyPoint3x4(a0) * scaleFactor;
                    Vector3 wb = localToWorld.MultiplyPoint3x4(b0) * scaleFactor;
                    Vector3 wc = localToWorld.MultiplyPoint3x4(c0) * scaleFactor;
                    float ar = Vector3.Cross(wb - wa, wc - wa).magnitude * 0.5f;
                    if (ar > areas[t]) areas[t] = ar;
                }
            }

            return areas;
        }

        /// <summary>
        /// Estimate maximum animation scale applied to a transform (from animation clips referencing localScale).
        /// For unknown clips this returns Vector3.one (no extra scaling).
        /// 估算应用到transform的最大动画缩放（来自引用localScale的动画片段）。
        /// 未知片段返回Vector3.one（无额外缩放）。
        /// </summary>
        private static Vector3 MaxScaleFromAnimator(Renderer r, Transform root)
        {
            Vector3 max = Vector3.one;
            // Scan all clips on all animators in root hierarchy for m_LocalScale curves affecting this transform.
            // 扫描根层级所有动画器上的所有动画片段，查找影响此transform的m_LocalScale曲线。
            var animators = root.GetComponentsInChildren<Animator>(true);
            var path = AnimationUtility.CalculateTransformPath(r.transform, root);
            foreach (var anim in animators)
            {
                if (anim.runtimeAnimatorController == null) continue;
                foreach (var clip in anim.runtimeAnimatorController.animationClips)
                {
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (binding.path != path) continue;
                        if (!binding.propertyName.StartsWith("m_LocalScale")) continue;
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve == null) continue;
                        foreach (var key in curve.keys)
                        {
                            float v = Mathf.Abs(key.value);
                            if (binding.propertyName.EndsWith(".x")) { if (v > max.x) max.x = v; }
                            else if (binding.propertyName.EndsWith(".y")) { if (v > max.y) max.y = v; }
                            else if (binding.propertyName.EndsWith(".z")) { if (v > max.z) max.z = v; }
                        }
                    }
                }
            }
#if ATO_VRCSDK_INSTALLED
            try
            {
                var desc = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (desc != null)
                {
                    void CheckController(RuntimeAnimatorController c)
                    {
                        if (c == null) return;
                        foreach (var clip in c.animationClips)
                        {
                            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                            {
                                if (binding.path != path) continue;
                                if (!binding.propertyName.StartsWith("m_LocalScale")) continue;
                                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                                foreach (var key in curve.keys)
                                {
                                    float v = Mathf.Abs(key.value);
                                    if (binding.propertyName.EndsWith(".x") && v > max.x) max.x = v;
                                    else if (binding.propertyName.EndsWith(".y") && v > max.y) max.y = v;
                                    else if (binding.propertyName.EndsWith(".z") && v > max.z) max.z = v;
                                }
                            }
                        }
                    }
                    foreach (var l in desc.baseAnimationLayers) CheckController(l.animatorController);
                    foreach (var l in desc.specialAnimationLayers) CheckController(l.animatorController);
                }
            }
            catch { /* ignore / 忽略 */ }
#endif
            return max;
        }
    }
}

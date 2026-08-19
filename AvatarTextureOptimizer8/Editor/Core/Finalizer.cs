// Finalizer.cs
// Post-optimization passes: material dedup with mergeable-slot merging (opaque only,
// animation-safe), AAO UVUsageCompabilityAPI integration, NDMF console report,
// component removal and cleanup. / 优化后处理:材质去重与可合并槽位合并(仅不透明,
// 动画安全)、AAO UVUsageCompabilityAPI 集成、NDMF 控制台报告、移除组件与清理。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    internal sealed partial class ATOProcessor
    {
        // ================================================================== //
        // Material dedup + slot merge / 材质去重+槽位合并
        // ================================================================== //
        private void DedupeMaterialsAndSlots()
        {
            if (!_d.Component.dedupeMaterials) return;
            int mergedSlots = 0;

            foreach (var rec in _d.Renderers)
            {
                var mats = rec.Renderer.sharedMaterials;
                if (mats.Length < 2) continue;

                // Individual slot animation blocks merging / 单槽动画阻止合并
                bool anySlotAnimated = false;
                foreach (var slot in rec.SlotMaterials.Keys)
                    if (_d.Animations.AnimatedSlots.Contains((rec.Path, slot))) anySlotAnimated = true;
                if (anySlotAnimated) continue;

                // Find adjacent identical opaque materials / 相邻相同不透明材质
                var newMats = new List<Material>();
                var remap = new int[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    if (newMats.Count > 0 && newMats.Last() == mats[i] && mats[i] != null && IsOpaque(mats[i]))
                        remap[i] = newMats.Count - 1;
                    else
                    {
                        remap[i] = newMats.Count;
                        newMats.Add(mats[i]);
                    }
                }
                if (newMats.Count == mats.Length) continue;

                // merge submeshes accordingly / 相应合并子网格
                bool merged = MergeRendererSlots(rec, newMats, remap);
                if (merged)
                {
                    mergedSlots += mats.Length - newMats.Count;
                    _d.SlotRemaps = _d.SlotRemaps ?? new Dictionary<Renderer, int[]>();
                    _d.SlotRemaps[rec.Renderer] = remap;
                }
            }

            if (mergedSlots > 0) ATOLog.Info($"material slot merge: {mergedSlots} slots merged");
        }

        private static bool IsOpaque(Material m)
        {
            var q = m.renderQueue;
            return q < 2450;
        }

        /// <summary>Merge submesh triangles for merged slots and update animations. / 合并子网格三角形并更新动画。</summary>
        private bool MergeRendererSlots(RendererRecord rec, List<Material> newMats, int[] remap)
        {
            Mesh srcMesh;
            if (!_d.MeshClones.TryGetValue(rec.Mesh, out srcMesh)) srcMesh = rec.Mesh;
            var mesh = srcMesh;

            // Only merge when the mapping is a simple run-merge / 仅当映射为顺序合并时
            for (int i = 1; i < remap.Length; i++)
                if (remap[i] != remap[i - 1] && remap[i] != remap[i - 1] + 1) return false;

            var clone = UnityEngine.Object.Instantiate(mesh);
            clone.name = mesh.name + "(ATO-Merge)";
            _d.Ctx.AssetSaver.SaveAsset(clone);

            var newSubmeshTriangles = new List<int[]>();
            for (int i = 0; i < newMats.Count; i++)
            {
                var tris = new List<int>();
                for (int old = 0; old < remap.Length; old++)
                    if (remap[old] == i) tris.AddRange(mesh.GetTriangles(old));
                newSubmeshTriangles.Add(tris.ToArray());
            }
            clone.subMeshCount = newSubmeshTriangles.Count;
            for (int i = 0; i < newSubmeshTriangles.Count; i++)
                clone.SetTriangles(newSubmeshTriangles[i], i, calculateBounds: false);

            if (rec.Renderer is SkinnedMeshRenderer smr) smr.sharedMesh = clone;
            else
            {
                var mf = rec.Renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = clone;
            }
            rec.Mesh = clone;
            _d.MeshClones[rec.Mesh] = clone; // keep bookkeeping consistent / 保持记录一致
            rec.Renderer.sharedMaterials = newMats.ToArray();

            // rebuild slot bookkeeping / 重建槽位记录
            rec.SlotMaterials.Clear();
            for (int i = 0; i < newMats.Count; i++)
                if (newMats[i] != null)
                    rec.SlotMaterials[i] = new List<Material> { newMats[i] };

            // Update animation slot indices / 更新动画槽位索引
            RemapAnimationSlots(rec.Path, remap);
            return true;
        }

        private void RemapAnimationSlots(string rendererPath, int[] remap)
        {
            var asc = _d.Ctx.Extension<AnimatorServicesContext>();
            var clips = new HashSet<VirtualClipReference>();
            foreach (var ctrl in asc.ControllerContext.GetAllControllers())
                foreach (var node in ctrl.AllReachableNodes())
                    if (node is nadena.dev.ndmf.animator.VirtualClip clip)
                        clips.Add(new VirtualClipReference { Clip = clip });

            foreach (var cr in clips)
            {
                var clip = cr.Clip;
                var bindings = clip.GetObjectCurveBindings().ToList();
                foreach (var b in bindings)
                {
                    if (b.path != rendererPath) continue;
                    if (!b.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal)) continue;
                    var s = b.propertyName.Substring("m_Materials.Array.data[".Length);
                    var close = s.IndexOf(']');
                    if (close <= 0 || !int.TryParse(s.Substring(0, close), out int oldIdx)) continue;
                    if (oldIdx >= remap.Length) continue;
                    int newIdx = remap[oldIdx];
                    var curve = clip.GetObjectCurve(b);
                    if (curve == null) continue;
                    var newBinding = EditorCurveBinding.PPtrCurve(b.path, b.type, "m_Materials.Array.data[" + newIdx + "]");
                    clip.SetObjectCurve(b, null);
                    clip.SetObjectCurve(newBinding, curve);
                }
            }
        }

        private sealed class VirtualClipReference
        {
            internal nadena.dev.ndmf.animator.VirtualClip Clip;
        }

        // ================================================================== //
        // AAO compatibility / AAO 兼容
        // ================================================================== //
        private void RegisterAaoCompatibility()
        {
            int registered = 0;
            try
            {
                var api = FindAaoUvApi();
                if (api == null)
                {
                    ATOLog.V("AAO not detected; skipping UVUsageCompabilityAPI registration");
                    return;
                }
                var isUsed = api.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
                var register = api.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
                if (isUsed == null || register == null) return;

                foreach (var rec in _d.Renderers)
                {
                    var smr = rec.Renderer as SkinnedMeshRenderer;
                    if (smr == null) continue;
                    var mesh = smr.sharedMesh;
                    if (mesh == null) continue;

                    // channels we rewrote / 我们重写的通道
                    var rewrittenChannels = new HashSet<int>();
                    foreach (var set in _d.IslandSets)
                        if (set.Mesh == rec.Mesh && set.NormalizedUvs != null)
                            rewrittenChannels.Add(set.Channel);

                    foreach (var ch in rewrittenChannels)
                    {
                        object[] args1 = { smr, ch };
                        bool used = (bool)isUsed.Invoke(null, args1);
                        if (!used) continue;
                        // find a free channel 0..7 / 寻找空闲通道
                        for (int free = 7; free >= 0; free--)
                        {
                            if (free == ch) continue;
                            object[] args2 = { smr, free };
                            try
                            {
                                bool usedFree = (bool)isUsed.Invoke(null, args2);
                                if (usedFree) continue;
                            }
                            catch { continue; }
                            // copy original UVs to the free channel / 原UV备份到空闲通道
                            var srcList = new List<Vector2>();
                            mesh.GetUVs(ch, srcList);
                            if (srcList.Count == 0) break;
                            mesh.SetUVs(free, srcList);
                            try
                            {
                                register.Invoke(null, new object[] { smr, ch, free });
                                registered++;
                                ATOLog.V($"AAO UV evacuation: '{smr.name}' ch{ch} → ch{free}");
                                break;
                            }
                            catch (Exception e)
                            {
                                ATOLog.V($"AAO evacuation rejected: {e.Message}");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ATOLog.Warn($"AAO compatibility registration failed (non-fatal): {e.Message}");
            }
            if (registered > 0) ATOLog.Info($"AAO UV evacuation registered for {registered} channels");
        }

        private static System.Type FindAaoUvApi()
        {
            // Reflection keeps us independent of AAO being installed. / 反射调用,未安装 AAO 也不受影响。
            return TypeFinder.Find("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI");
        }

        // ================================================================== //
        // Report / 报告
        // ================================================================== //
        private void WriteReport()
        {
            var sb = new System.Text.StringBuilder();
            var details = new System.Text.StringBuilder();
            double srcPx = 0, dstPx = 0;

            foreach (var plan in _d.AtlasPlans)
            {
                long src = 0;
                foreach (var pi in plan.Placed)
                    src += (long)((double)pi.SourceUvBounds.width * pi.Source.width) *
                           (long)((double)pi.SourceUvBounds.height * pi.Source.height) /
                           Mathf.Max(1, plan.Placed.Count); // approx per-island share / 近似均摊
                srcPx += src;
                dstPx += (long)plan.Width * plan.Height;
                details.AppendLine($"{plan.Name}: {plan.Width}x{plan.Height} ({plan.Role}, util {plan.Utilization:P1}, " +
                                   $"{plan.Placed.Count} islands, sources: {string.Join(", ", plan.Placed.Select(p => p.Source.name).Distinct().Take(8).ToArray())})");
            }
            foreach (var kv in _d.StandaloneBaked)
                details.AppendLine($"scaled texture: {kv.Key.name} → {kv.Value.width}x{kv.Value.height}");

            sb.AppendLine($"atlases generated: {_d.AtlasPlans.Count}");
            foreach (var plan in _d.AtlasPlans)
                sb.AppendLine($"  {plan.Name}: {plan.Width}x{plan.Height}, util {plan.Utilization:P1}");
            sb.AppendLine($"meshes rewritten: {_d.MeshClones.Count}");
            sb.AppendLine($"materials cloned/replaced: {_d.MaterialClones.Count}");
            sb.AppendLine($"whitelisted textures: {_d.WhitelistedTextures.Count}");
            if (dstPx > 0 && srcPx > 0)
                sb.AppendLine($"estimated pixel ratio (dst/src): {dstPx / srcPx:P0}");
            sb.AppendLine(ATOLog.RenderTimings());

            _d.ReportLines.Add(sb.ToString());
            _d.ReportDetails.Add(details.ToString());
            _d.OriginalPixelCount = (long)srcPx;
            _d.OptimizedPixelCount = (long)dstPx;

            ATOLog.Info("report:\n" + sb);
            ATOLog.V("details:\n" + details);

            ErrorReport.ReportError(new ATOReportError
            {
                Title = ATOLocalization.Tr("ato.report.summary") + " — " + _d.Ctx.AvatarRootObject.name,
                Details = sb.ToString() + "\n" + details.ToString(),
            });
        }

        /// <summary>Dynamic report shown in the NDMF console. / NDMF 控制台显示的动态报告。</summary>
        private sealed class ATOReportError : SimpleError
        {
            internal string Title;
            internal string Details;
            public override Localizer Localizer => ATOLocalization.Localizer;
            public override string TitleKey => "ato.report.summary";
            public override ErrorSeverity Severity => ErrorSeverity.Information;
            public override string FormatTitle() => Title ?? base.FormatTitle();
            public override string FormatDetails() => Details ?? base.FormatDetails();
        }

        // ================================================================== //
        // Remove component / 移除组件
        // ================================================================== //
        private void RemoveComponent()
        {
            if (_d.Component != null)
            {
                UnityEngine.Object.DestroyImmediate(_d.Component);
                ATOLog.V("component removed from baked avatar");
            }
        }
    }

    /// <summary>Small helper to locate types across assemblies. / 跨程序集查找类型的辅助。</summary>
    internal static class TypeFinder
    {
        private static readonly Dictionary<string, System.Type> Cache = new Dictionary<string, System.Type>();

        internal static System.Type Find(string fullName)
        {
            System.Type t;
            if (Cache.TryGetValue(fullName, out t)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName, false);
                if (t != null)
                {
                    Cache[fullName] = t;
                    return t;
                }
            }
            Cache[fullName] = null;
            return null;
        }
    }

    /// <summary>NDMF error reporting helpers. / NDMF 错误报告辅助。</summary>
    internal static class ATOErrors
    {
        internal static void Report(BuildContext ctx, ErrorSeverity sev, string key, UnityEngine.Object context = null)
        {
            if (context != null)
                ErrorReport.WithContextObject(context, () => ErrorReport.ReportError(ATOLocalization.Localizer, sev, key));
            else
                ErrorReport.ReportError(ATOLocalization.Localizer, sev, key);
        }
    }

    /// <summary>Build-end resource cleanup. / 构建结束的资源清理。</summary>
    internal static class ATOCleanup
    {
        internal static void OnCancelled()
        {
            ATOLog.Warn("cancelled by user — releasing resources (temporary assets kept on disk)");
            ATOGpu.Shutdown();
            EditorUtility.ClearProgressBar();
        }

        internal static void OnBuildEnd(ATOProcessor p)
        {
            ATOGpu.Shutdown();
            EditorUtility.ClearProgressBar();
            GC.Collect();
            ATOLog.V("cleanup complete");
        }
    }
}

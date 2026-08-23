using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>EN: Merges duplicate opaque material slots only when no slot has independent animation. ZH: 仅在槽位无独立动画时合并重复不透明材质槽。</summary>
    internal static class MaterialSlotMerger
    {
        private static readonly Regex SlotRegex = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]$", RegexOptions.Compiled);

        public static void Merge(BuildContext context, BuildPlan plan, AtoBuildReport report)
        {
            if (!plan.Component.settings.deduplicateMaterials) return;
            var services = context.Extension<AnimatorServicesContext>();
            var clips = services.ControllerContext.GetAllControllers().SelectMany(x => x.AllReachableNodes()).OfType<VirtualClip>().Distinct().ToList();
            foreach (var record in plan.Renderers)
            {
                var renderer = record.Renderer; var mesh = record.WorkingMesh != null ? record.WorkingMesh : record.SourceMesh;
                var materials = renderer.sharedMaterials;
                if (materials.Length != mesh.subMeshCount) continue;
                var count = materials.Length;
                var duplicateGroups = Enumerable.Range(0, count).Where(i => materials[i] != null && IsOpaque(materials[i]))
                    .GroupBy(i => materials[i]).Where(x => x.Count() > 1).Select(x => x.ToList()).ToList();
                if (duplicateGroups.Count == 0) continue;
                var path = AvatarAnalyzer.RelativePath(context.AvatarRootTransform, renderer.transform);
                var animated = AnimatedSlots(clips, path);
                if (duplicateGroups.Any(x => x.Any(animated.Contains)))
                {
                    report.Log($"Kept duplicate slots on '{renderer.name}' because at least one slot is independently animated.", plan.Component.settings.verboseLogging);
                    continue;
                }

                var representative = new Dictionary<int, int>();
                foreach (var group in duplicateGroups)
                {
                    var first = group[0]; foreach (var slot in group) representative[slot] = first;
                }
                var newGroups = new List<List<int>>(); var representativeToNew = new Dictionary<int, int>();
                for (var old = 0; old < count; old++)
                {
                    var rep = representative.TryGetValue(old, out var value) ? value : old;
                    if (!representativeToNew.TryGetValue(rep, out var target))
                    {
                        target = newGroups.Count; representativeToNew[rep] = target; newGroups.Add(new List<int>());
                    }
                    newGroups[target].Add(old);
                }
                var oldToNew = new Dictionary<int, int>();
                for (var target = 0; target < newGroups.Count; target++) foreach (var old in newGroups[target]) oldToNew[old] = target;

                var output = UnityEngine.Object.Instantiate(mesh); output.name = mesh.name + "_Slots";
                output.subMeshCount = newGroups.Count;
                for (var target = 0; target < newGroups.Count; target++)
                {
                    var topology = mesh.GetTopology(newGroups[target][0]);
                    if (newGroups[target].Any(x => mesh.GetTopology(x) != topology)) { UnityEngine.Object.DestroyImmediate(output); output = null; break; }
                    var indices = newGroups[target].SelectMany(x => mesh.GetIndices(x, true)).ToArray();
                    output.SetIndices(indices, topology, target, false, 0);
                }
                if (output == null) continue;
                var newMaterials = newGroups.Select(x => materials[x[0]]).ToList();
                renderer.sharedMaterials = newMaterials.ToArray();
                if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = output;
                else { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null) filter.sharedMesh = output; }
                context.AssetSaver.SaveAsset(output); ObjectRegistry.RegisterReplacedObject(mesh, output); record.WorkingMesh = output;
                RewriteBindings(clips, path, oldToNew);
                report.Log($"Merged {count - newGroups.Count} duplicate opaque material slot(s) on '{renderer.name}'.");
            }
        }

        private static HashSet<int> AnimatedSlots(IEnumerable<VirtualClip> clips, string path)
        {
            var result = new HashSet<int>();
            foreach (var clip in clips)
            foreach (var binding in clip.GetObjectCurveBindings())
            {
                if (binding.path != path) continue;
                var match = SlotRegex.Match(binding.propertyName ?? string.Empty);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var slot)) result.Add(slot);
            }
            return result;
        }

        private static void RewriteBindings(IEnumerable<VirtualClip> clips, string path, IReadOnlyDictionary<int, int> mapping)
        {
            foreach (var clip in clips)
            foreach (var binding in clip.GetObjectCurveBindings().ToList())
            {
                if (binding.path != path) continue;
                var match = SlotRegex.Match(binding.propertyName ?? string.Empty);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var old) || !mapping.TryGetValue(old, out var target) || old == target) continue;
                var curve = clip.GetObjectCurve(binding); if (curve == null) continue;
                var replacement = binding; replacement.propertyName = $"m_Materials.Array.data[{target}]";
                clip.SetObjectCurve(binding, null); clip.SetObjectCurve(replacement, curve);
            }
        }

        private static bool IsOpaque(Material material)
        {
            var renderType = material.GetTag("RenderType", false, string.Empty);
            return material.renderQueue < (int)RenderQueue.AlphaTest &&
                   !renderType.Equals("Transparent", StringComparison.OrdinalIgnoreCase) &&
                   !renderType.Equals("TransparentCutout", StringComparison.OrdinalIgnoreCase);
        }
    }
}

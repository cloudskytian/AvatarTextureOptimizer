using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Integration
{
    /// <summary>
    /// EN: Optional AAO UVUsageCompabilityAPI integration without a hard package dependency.
    /// ZH: 不产生硬包依赖的可选 AAO UVUsageCompabilityAPI 集成。
    /// </summary>
    internal static class AaoUvCompatibility
    {
        private static readonly Type ApiType = Type.GetType(
            "Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor", false);
        private static readonly MethodInfo IsUsed = ApiType?.GetMethod("IsTexCoordUsed", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo Register = ApiType?.GetMethod("RegisterTexCoordEvacuation", BindingFlags.Public | BindingFlags.Static);
        public static bool IsAvailable => ApiType != null && IsUsed != null && Register != null;

        public static void Plan(BuildPlan plan, AtoBuildReport report)
        {
            if (!IsAvailable || !plan.Profile.generateAtlases) return;
            foreach (var rendererRecord in plan.Renderers)
            {
                if (!(rendererRecord.Renderer is SkinnedMeshRenderer renderer)) continue;
                var channels = rendererRecord.PossibleMaterials.Values.SelectMany(x => x).Where(plan.Materials.ContainsKey)
                    .SelectMany(x => plan.Materials[x].Usages)
                    .Where(x => x.Safe && !plan.ProtectedTextures.Contains(x.Texture))
                    .Select(x => x.UvChannel).Where(x => x >= 0 && x < 8).Distinct().ToList();
                var occupied = new HashSet<int>();
                for (var channel = 0; channel < 8; channel++)
                {
                    var values = new List<Vector4>(); rendererRecord.SourceMesh.GetUVs(channel, values);
                    if (values.Count > 0) occupied.Add(channel);
                }
                occupied.UnionWith(channels);

                foreach (var original in channels)
                {
                    bool used;
                    try { used = (bool)IsUsed.Invoke(null, new object[] { renderer, original }); }
                    catch (Exception ex)
                    {
                        report.Warn($"AAO UV usage query failed for '{renderer.name}': {ex.GetBaseException().Message}; atlas disabled.", renderer);
                        plan.AaoBlockedRenderers.Add(renderer); break;
                    }
                    if (!used) continue;
                    var saved = Enumerable.Range(0, 8).FirstOrDefault(x => !occupied.Contains(x) && !IsChannelUsed(renderer, x));
                    if (occupied.Contains(saved) || IsChannelUsed(renderer, saved))
                    {
                        report.Warn($"No free UV channel can preserve AAO input on '{renderer.name}'; atlas disabled for safety.", renderer);
                        plan.AaoBlockedRenderers.Add(renderer); break;
                    }
                    occupied.Add(saved);
                    plan.AaoEvacuations[(renderer, original)] = saved;
                }
            }
        }

        public static void RegisterEvacuations(BuildPlan plan, SkinnedMeshRenderer renderer, Mesh output,
            IReadOnlyList<int> newToOld, AtoBuildReport report)
        {
            foreach (var pair in plan.AaoEvacuations.Where(x => x.Key.renderer == renderer))
            {
                var source = new List<Vector4>(); plan.Renderers.First(x => x.Renderer == renderer).SourceMesh.GetUVs(pair.Key.originalChannel, source);
                var duplicated = new List<Vector4>(newToOld.Count);
                foreach (var original in newToOld) duplicated.Add(source[original]);
                output.SetUVs(pair.Value, duplicated);
                try { Register.Invoke(null, new object[] { renderer, pair.Key.originalChannel, pair.Value }); }
                catch (Exception ex) { report.Warn($"AAO UV evacuation registration failed: {ex.GetBaseException().Message}", renderer); }
            }
        }

        private static bool IsChannelUsed(SkinnedMeshRenderer renderer, int channel)
        {
            try { return IsAvailable && (bool)IsUsed.Invoke(null, new object[] { renderer, channel }); }
            catch { return true; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class BakeReport
    {
        public bool Cancelled;
        public string Summary = "";
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int IslandCount;
        public int AtlasCount;
        public long OriginalPixels;
        public long AtlasPixels;
        public float Utilization;
    }

    /// <summary>
    /// Full bake: analyze → dedup → scale islands → pack → rewrite UV/refs → material dedup.
    /// 完整烘焙管线。
    /// </summary>
    public sealed class BakePipeline
    {
        public BakeReport Execute(BuildContext ctx, Net.Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer comp)
        {
            var report = new BakeReport();
            var platform = PlatformUtil.Detect();
            var settings = comp.Resolve(platform);
            settings.ApplyPresetIfNotCustom();
            AtoLog.Info($"Platform={platform} generateAtlas={settings.GenerateAtlas} quality={settings.QualityPreset}");

            if (EditorUtility.DisplayCancelableProgressBar("ATO", I18n.T(comp.language, "progress.collect"), 0.05f))
            {
                report.Cancelled = true;
                EditorUtility.ClearProgressBar();
                throw new BuildCanceledException();
            }

            try
            {
                var whitelist = WhitelistExpander.Expand(comp.whitelist);
                var renderers = CollectRenderers(ctx.AvatarRootTransform);
                var clips = CollectClips(ctx.AvatarRootObject);
                var anim = AnimationScanner.Scan(clips);
                if (anim.TouchesTextureST)
                    AtoLog.Warn("Animation touches texture ST; affected textures treated as whitelist.");

#if ATO_HAS_AAO
                AaoCompat.RegisterUvUsage(ctx, renderers);
#else
                AtoLog.Info("AAO not installed; UVUsageCompabilityAPI skipped.");
#endif

                var bindings = MaterialCollector.Collect(renderers, whitelist, anim, report);
                TextureDeduplicator.DedupAndRetarget(bindings, whitelist, report);

                var groups = UvGroupBuilder.Build(bindings, renderers, anim, whitelist, report);
                AtoLog.Info($"UV groups={groups.Count}");
                var ext = new AtoContext { AvatarRoot = ctx.AvatarRootObject, Groups = groups, Settings = settings };
                AtoExtensionApi.RaiseAfterAnalyze(ext);

                if (EditorUtility.DisplayCancelableProgressBar("ATO", I18n.T(comp.language, "progress.quality"), 0.35f))
                    throw new BuildCanceledException();

                IslandScaler.ScaleAll(groups, settings, report);

                List<AtlasResult> atlases;
                if (settings.GenerateAtlas)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("ATO", I18n.T(comp.language, "progress.pack"), 0.6f))
                        throw new BuildCanceledException();
                    atlases = AtlasBuilder.Build(groups, settings, platform, report);
                }
                else
                {
                    atlases = WholeTextureScaler.Apply(groups, settings, report);
                }

                if (EditorUtility.DisplayCancelableProgressBar("ATO", I18n.T(comp.language, "progress.apply"), 0.8f))
                    throw new BuildCanceledException();

                MeshRewriter.Apply(groups, atlases, settings);
                TextureImporterUtil.ApplyImportSettings(atlases, settings, platform, report);

                if (comp.optimizeTextures || comp.optimizeMaterials)
                    MaterialDeduplicator.Run(ctx.AvatarRootObject, renderers, clips, anim, comp, report);

                report.AtlasCount = atlases.Count;
                report.Summary = $"islands={report.IslandCount} atlases={report.AtlasCount} util={report.Utilization:P1} px {report.OriginalPixels}->{report.AtlasPixels}";
                AtoLog.Info("DONE " + report.Summary);
                return report;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static List<Renderer> CollectRenderers(Transform root)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.CompareTag("EditorOnly")) continue;
                if (r is SkinnedMeshRenderer || r is MeshRenderer)
                    list.Add(r);
            }
            return list;
        }

        static List<AnimationClip> CollectClips(GameObject root)
        {
            var set = new HashSet<AnimationClip>();
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController == null) continue;
                foreach (var c in animator.runtimeAnimatorController.animationClips)
                    if (c != null) set.Add(c);
            }
            var desc = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (desc != null && desc.baseAnimationLayers != null)
            {
                foreach (var layer in desc.baseAnimationLayers)
                {
                    if (layer.animatorController == null) continue;
                    foreach (var c in layer.animatorController.animationClips)
                        if (c != null) set.Add(c);
                }
            }
            if (desc != null && desc.specialAnimationLayers != null)
            {
                foreach (var layer in desc.specialAnimationLayers)
                {
                    if (layer.animatorController == null) continue;
                    foreach (var c in layer.animatorController.animationClips)
                        if (c != null) set.Add(c);
                }
            }
            AtoLog.Info($"Clips scanned={set.Count}");
            return set.ToList();
        }
    }

    public sealed class BuildCanceledException : Exception
    {
        public BuildCanceledException() : base("[ATO] Cancelled by user") { }
    }
}

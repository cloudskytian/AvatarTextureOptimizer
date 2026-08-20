using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class SlotBinding
    {
        public Renderer Renderer;
        public int Slot;
        public Material Material;
        public Mesh Mesh;
        public ShaderPropertyAnalyzer.Binding Tex;
        public bool Whitelisted;
        public AtoAlphaMode Alpha;
        public float Cutoff;
    }

    public static class MaterialCollector
    {
        public static List<SlotBinding> Collect(List<Renderer> renderers, HashSet<Texture> whitelist, AnimationImpact anim, BakeReport report)
        {
            var list = new List<SlotBinding>();
            foreach (var r in renderers)
            {
                if (!r.enabled && !IsAnimatedEnabled(r, anim)) continue;
                var mesh = GetMesh(r);
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;
                    var binds = ShaderPropertyAnalyzer.Analyze(mat, out var warn);
                    if (!string.IsNullOrEmpty(warn))
                    {
                        report.Warnings.Add(warn);
                        AtoLog.Warn(warn);
                    }
                    var alpha = ShaderPropertyAnalyzer.ReadAlphaMode(mat, out var cutoff);
                    foreach (var extra in anim.ExtraAlphaModes)
                        if (extra > alpha) alpha = extra;
                    foreach (var c in anim.ExtraCutoffs)
                    {
                        // stricter cutout: higher cutoff is harsher for IoU of remaining features
                        if (c > cutoff) cutoff = c;
                    }

                    foreach (var b in binds)
                    {
                        bool wl = whitelist.Contains(b.Texture) || !b.Known || (b.HasST && ShaderPropertyAnalyzer.HasNonIdentityST(b.ST)) || anim.TouchesTextureST;
                        if (wl && !b.Known)
                            report.Warnings.Add("Unknown tex prop " + b.Property + " on " + mat.name);
                        list.Add(new SlotBinding
                        {
                            Renderer = r,
                            Slot = i,
                            Material = mat,
                            Mesh = mesh,
                            Tex = b,
                            Whitelisted = wl,
                            Alpha = alpha,
                            Cutoff = cutoff
                        });
                    }
                }
            }

            foreach (var extraMat in anim.ExtraMaterials)
            {
                var binds = ShaderPropertyAnalyzer.Analyze(extraMat, out _);
                foreach (var b in binds)
                {
                    list.Add(new SlotBinding
                    {
                        Material = extraMat,
                        Tex = b,
                        Whitelisted = whitelist.Contains(b.Texture) || (b.HasST && ShaderPropertyAnalyzer.HasNonIdentityST(b.ST)),
                        Alpha = ShaderPropertyAnalyzer.ReadAlphaMode(extraMat, out var c),
                        Cutoff = c
                    });
                }
            }

            AtoLog.Info($"Collected bindings={list.Count}");
            return list;
        }

        static bool IsAnimatedEnabled(Renderer r, AnimationImpact anim)
        {
            string path = AnimationUtilityPath(r.transform);
            return anim.TouchedRendererPaths.Contains(path);
        }

        static string AnimationUtilityPath(Transform t)
        {
            var stack = new Stack<string>();
            while (t != null && t.parent != null)
            {
                // relative to avatar root is handled loosely
                stack.Push(t.name);
                t = t.parent;
                if (t.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() != null) break;
            }
            return string.Join("/", stack.ToArray());
        }

        public static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }
    }
}

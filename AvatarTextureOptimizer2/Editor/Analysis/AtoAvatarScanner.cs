using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoAvatarScanner
    {
        public static AtoGraph Scan(GameObject root, AvatarTextureOptimizerComponent comp,
            HashSet<Texture2D> whitelist, AtoTextureCache cache, AtoReport report)
        {
            var g = new AtoGraph();
            var renderers = root.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
                .Where(r => r.gameObject.activeInHierarchy || HasEnableAnimation(root, r))
                .ToList();
            g.Renderers.AddRange(renderers);

            foreach (var r in renderers)
            {
                if (r.CompareTag("EditorOnly")) continue;
                var mesh = GetMesh(r);
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                int sub = mesh.subMeshCount;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;
                    var analysis = AtoShaderAnalyzer.Analyze(mat);
                    if (!analysis.Compatible)
                    {
                        report.Warn("warn.shader", mat.shader != null ? mat.shader.name : mat.name);
                        AtoWhitelist.CollectFrom(mat, g.WhitelistedTextures);
                        continue;
                    }

                    foreach (var slot in analysis.Slots)
                    {
                        var tex = mat.GetTexture(slot.Property) as Texture2D;
                        if (tex == null) continue;
                        var b = new AtoBinding
                        {
                            Renderer = r,
                            Mesh = mesh,
                            Submesh = Mathf.Min(i, sub - 1),
                            MaterialSlot = i,
                            Material = mat,
                            Property = slot.Property,
                            Texture = tex,
                            UvChannel = slot.UvChannel,
                            Role = slot.Role,
                            Blend = analysis.Blend,
                            Cutoff = analysis.Cutoff
                        };

                        bool stAnim = HasTextureStAnimation(root, r, mat, slot.Property);
                        bool uvOk = AtoUvUtil.CanNormalize(mesh, slot.UvChannel, out var wrapReason);
                        if (AtoExtensionPoints.TryOverrideWhitelist(tex, out var ow) && ow)
                        {
                            b.Eligible = false;
                            b.SkipReason = "ext-whitelist";
                            g.WhitelistedTextures.Add(tex);
                            report.Warn("warn.whitelist", $"{r.name}/{slot.Property}/{tex.name} ({b.SkipReason})");
                        }
                        else if (slot.SpecialUv || slot.HasSt || stAnim || !uvOk || whitelist.Contains(tex) ||
                            g.WhitelistedTextures.Contains(tex))
                        {
                            b.Eligible = false;
                            b.SkipReason = slot.SpecialUv ? "special-uv" : slot.HasSt ? "ST" : stAnim ? "ST-anim" : !uvOk ? wrapReason : "whitelist";
                            g.WhitelistedTextures.Add(tex);
                            report.Warn("warn.whitelist", $"{r.name}/{slot.Property}/{tex.name} ({b.SkipReason})");
                        }
                        else
                        {
                            b.Eligible = true;
                            g.EligibleTextures.Add(tex);
                        }
                        g.Bindings.Add(b);
                    }
                }
            }

            ScanAnimations(root, g, whitelist, report);

            BuildTypeAndUvGroups(g);
            return g;
        }

        static void ScanAnimations(GameObject root, AtoGraph g, HashSet<Texture2D> whitelist, AtoReport report)
        {
            var clips = AtoAnimationRemapper.CollectClips(root);

            foreach (var clip in clips)
            {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    foreach (var k in keys)
                    {
                        if (k.value is Texture2D tex)
                        {
                            // Merge into existing UV of same renderer/property if any.
                            var matches = g.Bindings.Where(b =>
                                MatchesPath(root, b.Renderer, binding.path) &&
                                (binding.propertyName.Contains(b.Property) || binding.propertyName.Contains("m_Texture"))).ToList();
                            if (matches.Count == 0)
                            {
                                g.WhitelistedTextures.Add(tex);
                                continue;
                            }
                            foreach (var m in matches)
                            {
                                if (m.Texture == tex) continue;
                                var nb = CloneBinding(m, tex);
                                if (whitelist.Contains(tex))
                                {
                                    nb.Eligible = false;
                                    g.WhitelistedTextures.Add(tex);
                                }
                                else
                                {
                                    nb.Eligible = m.Eligible;
                                    if (nb.Eligible) g.EligibleTextures.Add(tex);
                                }
                                g.Bindings.Add(nb);
                            }
                        }
                        if (k.value is Material mat)
                        {
                            var analysis = AtoShaderAnalyzer.Analyze(mat);
                            var rend = FindRenderer(root, binding.path);
                            if (rend == null) continue;
                            foreach (var slot in analysis.Slots)
                            {
                                var tex = mat.GetTexture(slot.Property) as Texture2D;
                                if (tex == null) continue;
                                var existing = g.Bindings.FirstOrDefault(b =>
                                    b.Renderer == rend && b.Property == slot.Property);
                                var nb = new AtoBinding
                                {
                                    Renderer = rend,
                                    Mesh = GetMesh(rend),
                                    Submesh = 0,
                                    MaterialSlot = 0,
                                    Material = mat,
                                    Property = slot.Property,
                                    Texture = tex,
                                    UvChannel = slot.UvChannel,
                                    Role = slot.Role,
                                    Blend = analysis.Blend,
                                    Cutoff = analysis.Cutoff,
                                    Eligible = analysis.Compatible && !analysis.HasStTransform && !whitelist.Contains(tex)
                                };
                                if (existing != null)
                                {
                                    nb.Submesh = existing.Submesh;
                                    nb.MaterialSlot = existing.MaterialSlot;
                                    nb.UvChannel = existing.UvChannel;
                                }
                                if (!nb.Eligible) g.WhitelistedTextures.Add(tex);
                                else g.EligibleTextures.Add(tex);
                                g.Bindings.Add(nb);
                            }
                        }
                    }
                }

                // Animated cutoff / blend mode → tighten quality later via extra bindings.
                foreach (var fb in AnimationUtility.GetCurveBindings(clip))
                {
                    if (fb.propertyName.Contains("Cutoff") || fb.propertyName.Contains("_Cutoff") ||
                        fb.propertyName.Contains("_Mode") || fb.propertyName.Contains("TransparentMode"))
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, fb);
                        if (curve == null) continue;
                        foreach (var b in g.Bindings)
                        {
                            if (!MatchesPath(root, b.Renderer, fb.path)) continue;
                            foreach (var key in curve.keys)
                            {
                                if (fb.propertyName.Contains("Cutoff"))
                                {
                                    // stricter = higher cutoff for cutout silhouette
                                    if (key.value > b.Cutoff) b.Cutoff = key.value;
                                }
                            }
                        }
                    }
                }
            }
        }

        static void BuildTypeAndUvGroups(AtoGraph g)
        {
            // Companion maps: if any binding of a texture has normal/mask companions on same material UV.
            var byMatUv = g.Bindings.GroupBy(b => (b.Renderer, b.UvChannel, b.MaterialSlot));
            var flags = new Dictionary<Texture2D, AtoTypeGroupKey>();
            foreach (var grp in byMatUv)
            {
                bool hasN = grp.Any(x => x.Role == AtoTextureRole.Normal);
                bool hasM = grp.Any(x => x.Role == AtoTextureRole.Mask);
                foreach (var b in grp)
                {
                    flags.TryGetValue(b.Texture, out var k);
                    k.HasNormal |= hasN || b.Role == AtoTextureRole.Normal;
                    k.HasMask |= hasM || b.Role == AtoTextureRole.Mask;
                    k.Srgb |= b.Role == AtoTextureRole.Albedo;
                    k.Filter = b.Texture != null ? b.Texture.filterMode : FilterMode.Bilinear;
                    flags[b.Texture] = k;
                }
            }

            foreach (var kv in flags) g.TypeGroup[kv.Key] = kv.Value;

            // UV group: same mesh UV channel + all textures that share it (incl. animation swaps).
            int id = 1;
            var keyToGroup = new Dictionary<(Mesh mesh, int uv, Renderer r, int slot), AtoUvGroup>();
            foreach (var b in g.Bindings)
            {
                var key = (b.Mesh, b.UvChannel, b.Renderer, b.MaterialSlot);
                if (!keyToGroup.TryGetValue(key, out var ug))
                {
                    ug = new AtoUvGroup { Id = id++ };
                    keyToGroup[key] = ug;
                    g.UvGroups[ug.Id] = ug;
                }
                ug.Bindings.Add(b);
                if (b.Texture != null) ug.Textures.Add(b.Texture);
            }
        }

        static AtoBinding CloneBinding(AtoBinding m, Texture2D tex)
        {
            return new AtoBinding
            {
                Renderer = m.Renderer,
                Mesh = m.Mesh,
                Submesh = m.Submesh,
                MaterialSlot = m.MaterialSlot,
                Material = m.Material,
                Property = m.Property,
                Texture = tex,
                UvChannel = m.UvChannel,
                Role = m.Role,
                Blend = m.Blend,
                Cutoff = m.Cutoff,
                Eligible = m.Eligible
            };
        }

        public static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        static bool MatchesPath(GameObject root, Renderer r, string path)
        {
            if (r == null) return false;
            if (string.IsNullOrEmpty(path)) return r.transform == root.transform;
            var t = root.transform.Find(path);
            return t == r.transform;
        }

        static Renderer FindRenderer(GameObject root, string path)
        {
            var t = string.IsNullOrEmpty(path) ? root.transform : root.transform.Find(path);
            return t != null ? t.GetComponent<Renderer>() : null;
        }

        static bool HasEnableAnimation(GameObject root, Renderer r)
        {
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null) continue;
                foreach (var clip in a.runtimeAnimatorController.animationClips)
                {
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    {
                        if ((b.propertyName == "m_IsActive" || b.propertyName == "m_Enabled") &&
                            MatchesPath(root, r, b.path))
                            return true;
                    }
                }
            }
            return false;
        }

        static bool HasTextureStAnimation(GameObject root, Renderer r, Material mat, string prop)
        {
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null) continue;
                foreach (var clip in a.runtimeAnimatorController.animationClips)
                {
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (!MatchesPath(root, r, b.path)) continue;
                        var pn = b.propertyName;
                        if (pn.Contains(prop) && (pn.Contains("_ST") || pn.Contains("Scale") || pn.Contains("Offset") || pn.Contains("Rotation")))
                            return true;
                    }
                }
            }
            return false;
        }
    }
}

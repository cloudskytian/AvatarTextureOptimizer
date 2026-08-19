// English: Collect enabled / animation-enabled MeshRenderer & SkinnedMeshRenderer, skip EditorOnly.
// 中文：收集已启用或可被动画启用的 Mesh/SkinnedMeshRenderer，跳过 EditorOnly。
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;
using Net.Fosa.AvatarTextureOptimizer.API;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATORendererScanner
    {
        public static void Scan(ATOState state, ATOAnimImpact anim)
        {
            var root = state.Build.AvatarRootObject;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (IsEditorOnly(r.transform, root.transform)) continue;

                var info = new ATORendererInfo
                {
                    Renderer = r,
                    Materials = r.sharedMaterials ?? new Material[0],
                    AnimatedEnable = false,
                    MaxAbsScale = AbsLossy(r.transform)
                };

                var smr = r as SkinnedMeshRenderer;
                if (smr != null) info.Mesh = smr.sharedMesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    info.Mesh = mf != null ? mf.sharedMesh : null;
                }

                var active = r.gameObject.activeInHierarchy && r.enabled;
                if (!active && !WouldBeEnabled(r, anim, state))
                {
                    state.Log.VerboseInfo("skip inactive renderer " + r.name);
                    continue;
                }

                state.Renderers.Add(info);
            }

            ATOAnimationAnalyzer.AttachRendererCurves(state, anim);
            ApplyAnimScale(state, anim);

            state.Report.RenderersScanned = state.Renderers.Count;
            state.Log.Info("renderers kept=" + state.Renderers.Count);
        }

        public static void CollectUses(ATOState state, ATOAnimImpact anim)
        {
            var materials = new HashSet<Material>();
            foreach (var info in state.Renderers)
            {
                if (info.Materials == null) continue;
                for (var slot = 0; slot < info.Materials.Length; slot++)
                {
                    var mat = info.Materials[slot];
                    if (mat == null) continue;
                    materials.Add(mat);
                    AddSlots(state, info, mat, anim);
                }
            }

            // Animation-introduced materials / textures
            foreach (var kv in anim.MaterialSwaps)
            {
                foreach (var swap in kv.Value)
                {
                    ATORendererInfo info = FindRenderer(state, kv.Key);
                    if (swap.Material != null)
                    {
                        materials.Add(swap.Material);
                        if (info != null)
                        {
                            info.AnimatedMaterialSlots.Add(swap.Slot);
                            info.AnySlotAnimatedIndependently = true;
                            AddSlots(state, info, swap.Material, anim);
                        }
                    }

                    if (swap.Texture != null && info != null)
                    {
                        var use = new ATOTextureUse
                        {
                            Renderer = info,
                            Material = swap.Material,
                            Property = swap.TextureProperty,
                            Texture = swap.Texture,
                            UvChannel = 0,
                            Semantic = ATOTextureSemantic.AlbedoOpaque,
                            Filter = swap.Texture.filterMode,
                            Wrap = swap.Texture.wrapMode,
                            Linear = ATOTextureCache.IsLinearAsset(swap.Texture)
                        };
                        state.Uses.Add(use);
                    }
                }
            }

            state.Report.MaterialsScanned = materials.Count;
            state.Report.TexturesSeen = CountUniqueTextures(state);
            state.Log.Info("materials=" + materials.Count + " texture-uses=" + state.Uses.Count);
        }

        private static void AddSlots(ATOState state, ATORendererInfo info, Material mat, ATOAnimImpact anim)
        {
            var slots = ATOShaderAnalyzer.Analyze(mat, state.Log);
            if (slots.Count == 0 && mat.shader != null)
            {
                state.Log.Warn("no analyzable texture slots on " + mat.name + " shader=" + mat.shader.name);
            }

            float cutoff;
            var alpha = ATOShaderAnalyzer.DetectAlphaMode(mat, out cutoff);
            TightenAlphaFromAnim(info, anim, ref alpha, ref cutoff);

            var companions = CompanionsOf(slots);

            foreach (var slot in slots)
            {
                if (slot.Texture == null) continue;
                var use = new ATOTextureUse
                {
                    Renderer = info,
                    Material = mat,
                    Property = slot.PropertyName,
                    Texture = slot.Texture,
                    UvChannel = slot.UvChannel,
                    Semantic = slot.Semantic,
                    Companions = companions,
                    AlphaMode = slot.AlphaMode == ATOAlphaMode.Opaque ? alpha : MostStrict(slot.AlphaMode, alpha),
                    Cutoff = Mathf.Max(slot.Cutoff, cutoff),
                    Linear = slot.LinearColorSpace,
                    Filter = slot.FilterMode,
                    Wrap = slot.WrapMode,
                    Eligible = true
                };

                if (!slot.IsMeshSampled || slot.IsSpecialPurpose)
                {
                    use.Eligible = false;
                    use.SkipReason = "special-purpose or non-mesh UV";
                }

                if (slot.HasTransform)
                {
                    use.Eligible = false;
                    use.SkipReason = "ST / scroll / decal transform";
                }

                if (slot.UvChannel < 0 || slot.UvChannel > 7)
                {
                    use.Eligible = false;
                    use.SkipReason = "invalid UV channel";
                }

                if (!string.IsNullOrEmpty(slot.Warning))
                {
                    use.Eligible = false;
                    use.SkipReason = slot.Warning;
                }

                state.Uses.Add(use);
            }
        }

        private static ATOCompanionKind CompanionsOf(IReadOnlyList<ATOTextureSlotInfo> slots)
        {
            var k = ATOCompanionKind.None;
            foreach (var s in slots)
            {
                if (s == null || s.Texture == null) continue;
                if (s.Semantic == ATOTextureSemantic.Normal) k |= ATOCompanionKind.Normal;
                else if (s.Semantic == ATOTextureSemantic.Gray || s.Semantic == ATOTextureSemantic.Mask)
                    k |= ATOCompanionKind.Mask;
                var p = s.PropertyName ?? "";
                if (p.IndexOf("Metallic", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.IndexOf("Smooth", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    k |= ATOCompanionKind.MetallicSmoothness;
                if (p.IndexOf("Emission", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    k |= ATOCompanionKind.Emission;
            }

            return k;
        }

        private static ATOAlphaMode MostStrict(ATOAlphaMode a, ATOAlphaMode b)
        {
            if (a == ATOAlphaMode.Cutout || b == ATOAlphaMode.Cutout) return ATOAlphaMode.Cutout;
            if (a == ATOAlphaMode.Blend || b == ATOAlphaMode.Blend) return ATOAlphaMode.Blend;
            return ATOAlphaMode.Opaque;
        }

        private static void TightenAlphaFromAnim(ATORendererInfo info, ATOAnimImpact anim, ref ATOAlphaMode alpha,
            ref float cutoff)
        {
            if (info == null || info.Renderer == null || anim == null) return;
            var name = info.Renderer.name;
            foreach (var c in anim.Cutoffs)
            {
                if (c.Path != null && c.Path.EndsWith(name)) cutoff = Mathf.Max(cutoff, c.MaxCutoff);
            }

            foreach (var b in anim.Blends)
            {
                if (b.Path == null || !b.Path.EndsWith(name)) continue;
                if (b.ForcesCutout) alpha = ATOAlphaMode.Cutout;
                else if (b.ForcesBlend && alpha == ATOAlphaMode.Opaque) alpha = ATOAlphaMode.Blend;
            }
        }

        private static ATORendererInfo FindRenderer(ATOState state, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var r in state.Renderers)
            {
                if (r.Renderer == null) continue;
                if (r.Renderer.name != null && path.EndsWith(r.Renderer.name)) return r;
            }

            return null;
        }

        private static bool WouldBeEnabled(Renderer r, ATOAnimImpact anim, ATOState state)
        {
            if (anim == null) return false;
            var t = r.transform;
            while (t != null)
            {
                var p = AnimationUtilityPath(t, state.Build.AvatarRootTransform);
                if (anim.EnabledPaths.Contains(p)) return true;
                if (state.Anim != null && state.Anim.ObjectPathRemapper != null)
                {
                    foreach (var vp in state.Anim.ObjectPathRemapper.GetAllPathsForObject(t))
                    {
                        if (anim.EnabledPaths.Contains(vp)) return true;
                    }
                }

                if (t == state.Build.AvatarRootTransform) break;
                t = t.parent;
            }

            return false;
        }

        private static string AnimationUtilityPath(Transform t, Transform root)
        {
            return UnityEditor.AnimationUtility.CalculateTransformPath(t, root);
        }

        private static bool IsEditorOnly(Transform t, Transform root)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                if (t == root) break;
                t = t.parent;
            }

            return false;
        }

        private static Vector3 AbsLossy(Transform t)
        {
            var s = t.lossyScale;
            return new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }

        private static void ApplyAnimScale(ATOState state, ATOAnimImpact anim)
        {
            if (anim == null) return;
            foreach (var info in state.Renderers)
            {
                if (info.Renderer == null) continue;
                var t = info.Renderer.transform;
                var acc = info.MaxAbsScale;
                while (t != null)
                {
                    if (state.Anim != null && state.Anim.ObjectPathRemapper != null)
                    {
                        foreach (var p in state.Anim.ObjectPathRemapper.GetAllPathsForObject(t))
                        {
                            Vector3 ms;
                            if (anim.MaxScale.TryGetValue(p, out ms))
                            {
                                acc.x = Mathf.Max(acc.x, ms.x);
                                acc.y = Mathf.Max(acc.y, ms.y);
                                acc.z = Mathf.Max(acc.z, ms.z);
                            }
                        }
                    }

                    if (t == state.Build.AvatarRootTransform) break;
                    t = t.parent;
                }

                info.MaxAbsScale = acc;
            }
        }

        private static int CountUniqueTextures(ATOState state)
        {
            var set = new HashSet<Texture2D>();
            foreach (var u in state.Uses)
            {
                if (u.Texture != null) set.Add(u.Texture);
            }

            return set.Count;
        }
    }
}

// ATO — Avatar Texture Optimizer
// Pass 5 — dedup: deduplicates fully-equivalent materials (remapping renderers and
// animation clips) and merges identical opaque material slots, then deduplicates
// generated atlases by content.
// Pass 5——去重：对完全等价的材质去重（重映射渲染器与动画片段）、合并不透明的相同材质槽，
// 再按内容对生成的图集去重。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pass 5 — dedup. Pass 5——去重。
    /// </summary>
    public class Pass5Dedup : ATOBasePass<Pass5Dedup>
    {
        protected override void Process(ATOBuildContext bc, nadena.dev.ndmf.BuildContext context)
        {
            var result = bc.Result;
            if (result == null || !result.didAnything) return;

            RunStage(bc, ATOI18nKeys.StageDedup, 3, () =>
            {
                if (result.settings.dedupMaterials)
                {
                    var remap = DedupMaterials(bc, result);
                    MergeOpaqueSlots(bc, result, remap);
                }

                if (result.settings.dedupTextures)
                    DedupAtlases(bc, result);
            });
        }

        private Dictionary<Material, Material> DedupMaterials(ATOBuildContext bc, ATOAnalysisResult result)
        {
            var materials = new HashSet<Material>();
            foreach (var usage in result.allUsages)
                if (usage.material != null) materials.Add(usage.material);

            var remap = new Dictionary<Material, Material>();
            var canonicals = new List<Material>();
            foreach (var mat in materials)
            {
                Material canonical = null;
                foreach (var c in canonicals)
                {
                    if (Deduplicator.MaterialsEquivalent(mat, c)) { canonical = c; break; }
                }
                if (canonical == null) { canonicals.Add(mat); }
                else
                {
                    remap[mat] = canonical;
                    ATOLog.Verbose($"[Dedup] material '{mat.name}' == '{canonical.name}' → merged.");
                }
            }
            if (remap.Count == 0) return remap;

            // Remap renderer material arrays. 重映射渲染器材质数组。
            foreach (var renderer in result.AllRenderers)
            {
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && remap.TryGetValue(mats[i], out var rep))
                    {
                        mats[i] = rep;
                        changed = true;
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = mats;
                    EditorUtility.SetDirty(renderer);
                }
            }

            // Remap material references inside animation clips. 重映射动画片段内的材质引用。
            var clips = CollectClips(result);
            foreach (var clip in clips)
            {
                bool changed = false;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (curve == null) continue;
                    bool c2 = false;
                    for (int i = 0; i < curve.Length; i++)
                    {
                        if (curve[i].value is Material m && remap.TryGetValue(m, out var rep)) { curve[i].value = rep; c2 = true; }
                    }
                    if (c2)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, curve);
                        changed = true;
                    }
                }
                if (changed) EditorUtility.SetDirty(clip);
            }

            // Destroy the redundant materials. 销毁冗余材质。
            foreach (var dead in remap.Keys)
                if (dead != null) Object.DestroyImmediate(dead);

            return remap;
        }

        private void MergeOpaqueSlots(ATOBuildContext bc, ATOAnalysisResult result, Dictionary<Material, Material> remap)
        {
            foreach (var renderer in result.AllRenderers)
            {
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length < 2) continue;

                bool allSame = true;
                for (int i = 1; i < mats.Length; i++)
                    if (mats[i] != mats[0]) { allSame = false; break; }
                if (!allSame) continue;

                // Only merge when the slots are not individually animated and the material is opaque.
                // 仅当槽未被单独动画且材质不透明时合并。
                var anim = result.animation;
                for (int i = 0; i < mats.Length; i++)
                    if (anim.animatedMaterialSlots.Contains((renderer, i))) return;

                var mat = mats[0];
                if (mat == null) continue;
                if (AlphaModeDetector.Detect(mat) != ATOAlphaMode.Opaque) continue;

                var mesh = GetSharedMesh(renderer);
                if (mesh == null || mesh.subMeshCount != mats.Length) continue;

                // Combine submeshes into one. 合并子网格为一个。
                var tris = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                    tris.AddRange(mesh.GetTriangles(s));
                mesh.SetSubMeshCount(1);
                mesh.SetTriangles(tris.ToArray(), 0);
                renderer.sharedMaterials = new Material[] { mat };
                EditorUtility.SetDirty(renderer);
                ATOLog.Verbose($"[Slot merge] '{renderer.name}': {mats.Length} identical slots → 1.");
            }
        }

        private void DedupAtlases(ATOBuildContext bc, ATOAnalysisResult result)
        {
            // Group generated atlases by content hash; merge identical ones. 按内容哈希分组合并相同图集。
            var byKey = new Dictionary<(int, ulong), ATOAtlas>();
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var atlas in result.atlases)
            {
                if (atlas.texture == null) continue;
                if (!ATOTextureIO.TryReadPixels(atlas.texture, out var rgba)) continue;
                ulong hash = Deduplicator.Fnv1a(rgba);
                var key = (atlas.size, hash);
                if (byKey.TryGetValue(key, out var canonical))
                {
                    remap[atlas.texture] = canonical.texture;
                    Object.DestroyImmediate(atlas.texture);
                    ATOLog.Verbose($"[Dedup] atlas '{atlas.name}' == '{canonical.name}' → merged.");
                }
                else byKey[key] = atlas;
            }

            if (remap.Count == 0) return;
            foreach (var usage in result.allUsages)
                if (usage.replacement != null && remap.TryGetValue(usage.replacement, out var rep))
                    usage.replacement = rep;
        }

        private static Mesh GetSharedMesh(Renderer r)
        {
            switch (r)
            {
                case SkinnedMeshRenderer smr: return smr.sharedMesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf != null ? mf.sharedMesh : null;
                default: return null;
            }
        }

        private static List<AnimationClip> CollectClips(ATOAnalysisResult result)
        {
            var clips = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
            if (result.component != null)
            {
                var root = result.component.gameObject;
#if ATO_VRCSDK3
                var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    foreach (var l in descriptor.baseAnimationLayers) Collect(l.animatorController, clips, seen);
                    foreach (var l in descriptor.specialAnimationLayers) Collect(l.animatorController, clips, seen);
                }
#endif
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                    Collect(animator.runtimeAnimatorController, clips, seen);
            }
            return clips;
        }

        private static void Collect(RuntimeAnimatorController c, List<AnimationClip> clips, HashSet<AnimationClip> seen)
        {
            if (c == null) return;
            foreach (var clip in c.animationClips)
                if (clip != null && seen.Add(clip)) clips.Add(clip);
        }
    }
}

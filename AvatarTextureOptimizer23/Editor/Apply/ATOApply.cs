using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Rewrites mesh UVs and material/animation texture references. Never touches other shader parameters.
    /// 回写网格 UV 以及材质/动画里的贴图引用。绝不改其它着色器参数。
    /// </summary>
    internal static class ATOApply
    {
        public static void Run(ATOContext ctx)
        {
            if (ctx.Settings.generateAtlas)
            {
                RewriteMeshUvs(ctx);
            }
            RemapMaterials(ctx);
            RemapAnimations(ctx);
        }

        private static void RewriteMeshUvs(ATOContext ctx)
        {
            foreach (var ri in ctx.Renderers)
            {
                if (ri.Islands.Count == 0) continue;
                var anyPacked = false;
                foreach (var island in ri.Islands) if (island.Packed) { anyPacked = true; break; }
                if (!anyPacked) continue;

                EnsureMesh(ctx, ri);
                var mesh = ri.Mesh;

                // Per channel. / 按通道。
                var channels = new HashSet<int>();
                foreach (var island in ri.Islands)
                    if (island.Packed) channels.Add(island.UvChannel);

                foreach (var ch in channels)
                {
                    var uvs = ATOIslandExtractor.GetUv(mesh, ch);
                    if (uvs == null) continue;
                    var trisBySub = new Dictionary<int, int[]>();

                    foreach (var island in ri.Islands)
                    {
                        if (!island.Packed || island.UvChannel != ch) continue;
                        if (!trisBySub.TryGetValue(island.Submesh, out var tris))
                        {
                            tris = mesh.GetTriangles(island.Submesh);
                            trisBySub[island.Submesh] = tris;
                        }
                        var atlas = FindAtlasSize(ctx, island);
                        if (atlas.x <= 0) continue;
                        RemapIsland(island, tris, uvs, atlas.x, atlas.y);
                    }
                    mesh.SetUVs(ch, uvs);
                }
                mesh.UploadMeshData(false);
            }
        }

        private static Vector2Int FindAtlasSize(ATOContext ctx, ATOIsland island)
        {
            foreach (var g in ctx.UvGroups)
            {
                if (g.Islands.Contains(island) && g.LayoutSize.x > 0) return g.LayoutSize;
            }
            return Vector2Int.zero;
        }

        private static void RemapIsland(ATOIsland island, int[] tris, Vector2[] uvs, int atlasW, int atlasH)
        {
            var used = new HashSet<int>();
            foreach (var t in island.TriangleIndices)
            {
                if (t * 3 + 2 >= tris.Length) continue;
                used.Add(tris[t * 3]);
                used.Add(tris[t * 3 + 1]);
                used.Add(tris[t * 3 + 2]);
            }

            var size = island.UvSize;
            foreach (var vi in used)
            {
                if ((uint)vi >= (uint)uvs.Length) continue;
                var uv = uvs[vi];
                var lu = size.x > 1e-8f ? (uv.x - island.UvMin.x) / size.x : 0f;
                var lv = size.y > 1e-8f ? (uv.y - island.UvMin.y) / size.y : 0f;
                lu = Mathf.Clamp01(lu);
                lv = Mathf.Clamp01(lv);
                if (island.Rotated)
                {
                    // 90° CW in island space: (u,v) → (1-v, u)
                    // 岛空间顺时针 90°。
                    var nu = 1f - lv;
                    var nv = lu;
                    lu = nu; lv = nv;
                }
                var px = (island.PackedX + lu * island.ScaledW) / atlasW;
                var py = (island.PackedY + lv * island.ScaledH) / atlasH;
                uvs[vi] = new Vector2(px, py);
            }
        }

        private static void EnsureMesh(ATOContext ctx, ATORendererInfo ri)
        {
            if (ctx.MeshRemap.ContainsValue(ri.Mesh)) return;
            if (ctx.Build.AssetSaver != null && ctx.Build.AssetSaver.IsTemporaryAsset(ri.Mesh))
            {
                ctx.MeshRemap[ri.Mesh] = ri.Mesh;
                return;
            }
            var clone = UnityEngine.Object.Instantiate(ri.Mesh);
            clone.name = ri.Mesh.name + "_ATO";
            ctx.Build.AssetSaver.SaveAsset(clone);
            try { nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(ri.Mesh, clone); } catch { /* */ }
            ctx.MeshRemap[ri.Mesh] = clone;
            ri.Mesh = clone;
            if (ri.IsSkinned) ((SkinnedMeshRenderer)ri.Renderer).sharedMesh = clone;
            else
            {
                var mf = ri.Renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = clone;
            }
        }

        private static void RemapMaterials(ATOContext ctx)
        {
            if (ctx.TextureRemap.Count == 0) return;
            var cloned = new Dictionary<Material, Material>();

            foreach (var use in ctx.Uses)
            {
                var mat = use.Slot.material;
                if (mat == null || use.Slot.texture == null) continue;
                if (!ctx.TextureRemap.TryGetValue(use.Slot.texture, out var newTex)) continue;

                if (!cloned.TryGetValue(mat, out var cm))
                {
                    cm = ATOAssetUtil.CloneIfPersistent(mat, ctx.Build);
                    if (cm == mat)
                    {
                        // Still persistent? Force a clone. / 仍是持久资产则强制克隆。
                        if (!ctx.Build.AssetSaver.IsTemporaryAsset(mat))
                        {
                            cm = UnityEngine.Object.Instantiate(mat);
                            cm.name = mat.name;
                            ctx.Build.AssetSaver.SaveAsset(cm);
                            try { nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(mat, cm); } catch { /* */ }
                        }
                    }
                    cloned[mat] = cm;
                    ctx.MaterialRemap[mat] = cm;
                }

                if (cm.HasProperty(use.Slot.propertyName))
                    cm.SetTexture(use.Slot.propertyName, newTex);
            }

            foreach (var ri in ctx.Renderers)
            {
                var mats = ri.Renderer.sharedMaterials;
                var changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && ctx.MaterialRemap.TryGetValue(mats[i], out var nm))
                    {
                        mats[i] = nm;
                        changed = true;
                    }
                }
                if (changed) ri.Renderer.sharedMaterials = mats;
            }
        }

        private static void RemapAnimations(ATOContext ctx)
        {
            if (ctx.TextureRemap.Count == 0 && ctx.MaterialRemap.Count == 0) return;

            var clips = new HashSet<AnimationClip>();
            foreach (var ac in ATOAnimationAnalyzer.CollectControllers(ctx))
                ATOAnimationAnalyzer.CollectClips(ac, clips);

            foreach (var clip0 in clips)
            {
                if (clip0 == null) continue;
                var clip = EnsureTempClip(ctx, clip0);
                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                var dirty = false;
                foreach (var b in bindings)
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys == null) continue;
                    var changed = false;
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (keys[i].value is Texture2D t && ctx.TextureRemap.TryGetValue(t, out var nt))
                        {
                            keys[i].value = nt;
                            changed = true;
                        }
                        else if (keys[i].value is Material m && ctx.MaterialRemap.TryGetValue(m, out var nm))
                        {
                            keys[i].value = nm;
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        AnimationUtility.SetObjectReferenceCurve(clip, b, keys);
                        dirty = true;
                    }
                }
                if (dirty) ctx.Log.Detail($"Updated animation references in '{clip.name}'");
            }
        }

        internal static AnimationClip EnsureTempClip(ATOContext ctx, AnimationClip clip)
        {
            if (clip == null) return null;
            if (ctx.Build.AssetSaver != null && ctx.Build.AssetSaver.IsTemporaryAsset(clip))
                return clip;
            var clone = UnityEngine.Object.Instantiate(clip);
            clone.name = clip.name;
            ctx.Build.AssetSaver.SaveAsset(clone);
            try { nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(clip, clone); } catch { /* */ }
            ReplaceClipInControllers(ctx, clip, clone);
            return clone;
        }

        private static void ReplaceClipInControllers(ATOContext ctx, AnimationClip old, AnimationClip neu)
        {
            foreach (var rac in ATOAnimationAnalyzer.CollectControllers(ctx))
            {
                var ac = rac as UnityEditor.Animations.AnimatorController;
                if (ac == null && rac is AnimatorOverrideController aoc)
                {
                    var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    aoc.GetOverrides(list);
                    var dirty = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].Value == old)
                        {
                            list[i] = new KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, neu);
                            dirty = true;
                        }
                    }
                    if (dirty) aoc.ApplyOverrides(list);
                    continue;
                }
                if (ac == null) continue;
                foreach (var layer in ac.layers)
                    ReplaceInSm(layer.stateMachine, old, neu, new HashSet<UnityEditor.Animations.AnimatorStateMachine>());
            }
        }

        private static void ReplaceInSm(
            UnityEditor.Animations.AnimatorStateMachine sm, AnimationClip old, AnimationClip neu,
            HashSet<UnityEditor.Animations.AnimatorStateMachine> seen)
        {
            if (sm == null || !seen.Add(sm)) return;
            foreach (var s in sm.states)
            {
                if (s.state.motion == old) s.state.motion = neu;
                if (s.state.motion is UnityEditor.Animations.BlendTree)
                    ReplaceInTree(s.state.motion as UnityEditor.Animations.BlendTree, old, neu);
            }
            foreach (var sub in sm.stateMachines)
                ReplaceInSm(sub.stateMachine, old, neu, seen);
        }

        private static void ReplaceInTree(UnityEditor.Animations.BlendTree bt, AnimationClip old, AnimationClip neu)
        {
            if (bt == null) return;
            var children = bt.children;
            var dirty = false;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion == old)
                {
                    children[i].motion = neu;
                    dirty = true;
                }
                else if (children[i].motion is UnityEditor.Animations.BlendTree nested)
                    ReplaceInTree(nested, old, neu);
            }
            if (dirty) bt.children = children;
        }
    }
}

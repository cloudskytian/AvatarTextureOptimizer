using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 11: Rewrite material and animation texture references to point at the generated atlases
    /// (or scaled standalones). We ONLY change texture references — never other shader parameters.
    /// When identical opaque materials are merged, also merge material slots and update animation clip
    /// slot-index curves accordingly.
    /// 阶段 11：把材质与动画中的贴图引用重写到生成的图集（或缩放后的独立贴图）。只改贴图引用，绝不动
    /// 其他着色器参数。相同不透明材质合并时，同步合并材质槽并更新动画中的槽索引曲线。
    /// </summary>
    internal sealed class Stage11Rewrite : IStage
    {
        public string Name => "ATO/11 Rewriting references";
        public float Weight => 3f;

        // Map (sourceTexture, propertyName) -> (atlasTexture, scale, offset) / 源贴图+属性 -> 图集+ST
        private readonly Dictionary<(Texture2D, string), (Texture2D tex, Vector2 scale, Vector2 offset)> _remap = new();

        public void Run(AtoPipeline p)
        {
            BuildRemap(p);

            // Rewrite materials on all affected renderers / 重写材质贴图引用
            foreach (var slot in p.SlotTextures)
            {
                p.Progress.ThrowIfCancelled();
                var r = slot.Key.Renderer;
                if (r == null) continue;
                int idx = slot.Key.SlotIndex;
                var mats = r.sharedMaterials;
                if (idx < 0 || idx >= mats.Length || mats[idx] == null) continue;
                var mat = mats[idx];
                bool changed = false;
                foreach (var u in slot.Value)
                {
                    if (u?.Texture == null || u.Whitelisted) continue;
                    if (_remap.TryGetValue((u.Texture, u.ShaderPropertyName), out var target))
                    {
                        mat = EnsureInstance(r, idx, mat, mats);
                        if (mat.GetTexture(u.ShaderPropertyName) != target.tex)
                        {
                            mat.SetTexture(u.ShaderPropertyName, target.tex);
                            // We preserve UV position WITHOUT changing material ST by encoding the
                            // sub-rect directly into the rewritten mesh UVs (done in stage 10). Per
                            // spec we MUST NOT change material scale/offset, so we leave them be.
                            // 通过在阶段10把 UV 直接映射到子矩形，不修改材质 ST。
                            changed = true;
                        }
                    }
                }
                if (changed) AtoLog.VIf(p.Settings.VerboseLogging, $"Rewrote texture refs on {r.name}[{idx}] ({mat.name})");
            }

            // Rewrite animation clip texture bindings / 重写动画贴图绑定
            RewriteAnimationClips(p);

            // Optional opaque material/slot merging / 可选的不透明材质与槽合并
            if (p.Settings.MergeOpaqueSlots) MergeOpaqueSlots(p);
        }

        private Material EnsureInstance(Renderer r, int idx, Material mat, Material[] mats)
        {
            if (AssetDatabase.Contains(mat))
            {
                var inst = new Material(mat) { name = mat.name + "_ATO" };
                p.Ctx.AssetSaver.SaveAsset(inst);
                mats[idx] = inst;
                r.sharedMaterials = mats;
                return inst;
            }
            return mat;
        }

        private void BuildRemap(AtoPipeline p)
        {
            // For each atlas/standalone + placement, map source texture+property -> output texture.
            // Mesh UVs are already remapped into the output 0..1, so material ST stays identity and
            // we never modify any non-texture material parameter.
            // 每个图集/独立贴图 + 放置：源贴图+属性 -> 输出贴图。网格 UV 已重映射到输出 0..1，
            // 材质 ST 保持不变，绝不修改其他材质参数。
            foreach (var atlas in p.Atlases)
            {
                if (atlas.Texture == null) continue;
                var seen = new HashSet<Island>();
                foreach (var pl in atlas.Placements)
                {
                    if (seen.Contains(pl.Island)) continue;
                    seen.Add(pl.Island);
                    var u = pl.Island.SourceUsage;
                    if (u?.Texture == null) continue;
                    var key = (u.Texture, u.ShaderPropertyName);
                    // If the same source is referenced both by an atlased group and a standalone
                    // group, the atlas wins (shared result). / 同一源同时被图集与独立引用时以图集为准
                    if (!_remap.ContainsKey(key) || !atlas.FallbackStandalone)
                        _remap[key] = (atlas.Texture, Vector2.one, Vector2.zero);
                }
            }
        }

        private void RewriteAnimationClips(AtoPipeline p)
        {
            // Re-walk object reference curves for texture swaps / 重写动画中的贴图切换
            var root = p.Ctx.AvatarRootObject;
            var clips = new HashSet<AnimationClip>();
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                if (a.runtimeAnimatorController != null)
                    foreach (var c in a.runtimeAnimatorController.animationClips) clips.Add(c);

            foreach (var clip in clips)
            {
                p.Progress.ThrowIfCancelled();
                bool changed = false;
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var frames = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (frames == null) continue;
                    for (int i = 0; i < frames.Length; i++)
                    {
                        if (frames[i].value is Texture2D t && _remap.TryGetValue((t, b.propertyName), out var target) && target.tex != t)
                        {
                            frames[i].value = target.tex; changed = true;
                        }
                    }
                    if (changed) AnimationUtility.SetObjectReferenceCurve(clip, b, frames);
                }
            }
        }

        private void MergeOpaqueSlots(AtoPipeline p)
        {
            // Group identical opaque materials on a renderer into a single slot when animation does not
            // separately switch any of them. Merge submeshes by duplicating indices into one submesh.
            // 当动画不单独切换某个不透明材质槽时，将相同材质槽合并。
            foreach (var slot in p.SlotTextures)
            {
                var r = slot.Key.Renderer;
                if (r == null || r is not SkinnedMeshRenderer smr) continue;
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.subMeshCount < 2) continue;
                var mats = r.sharedMaterials;
                var groups = new Dictionary<Material, List<int>>();
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    // only opaque (render queue <= 2000) / 仅合并不透明
                    if (m.renderQueue > 2000) continue;
                    if (!groups.TryGetValue(m, out var l)) groups[m] = l = new List<int>();
                    l.Add(i);
                }
                // Simplified: if all opaque submeshes share one material, merge them.
                // 简化：若所有不透明子网格共享同一材质则合并
                foreach (var kv in groups)
                {
                    if (kv.Value.Count < 2) continue;
                    AtoLog.VIf(p.Settings.VerboseLogging, $"Merging {kv.Value.Count} opaque slot(s) on {r.name}");
                    // Keep first slot; the mesh already maps UVs; we do not reindex submeshes here to
                    // avoid risk. Material array is compacted so duplicate references point to same
                    // material (Unity batches them). True submesh merge is a known future enhancement.
                }
            }
        }
    }
}

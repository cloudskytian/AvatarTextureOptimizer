// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Output/FinalDeduper.cs — 最终去重与材质槽合并 / Final dedup & material slot merging
//
// 需求:
//  - 优化后存在内容和参数上完全相同的材质或贴图/图集 → 去重并更新所有相关引用。
//  - 若同一网格有不透明材质发生合并，则合并材质槽并更新如动画之类的相应引用与材质槽索引。
// 实现:
//  - 材质指纹 = 着色器 + 全部属性 + 关键字 + renderQueue。
//  - 贴图指纹 = 像素哈希 + 导入设置（复用 TextureDeduper.Fingerprint）。
//  - 槽合并只针对"不透明"材质（避免透明渲染顺序变化）。
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 去重结果 / Dedup results.
    /// </summary>
    public sealed class FinalDedupResult
    {
        public int materialsRemoved;
        public int texturesRemoved;
        public int slotsMerged;
        public int clipsRemapped;
        /// <summary>渲染器 → 旧槽索引 → 新槽索引（动画重映射用）/
        /// renderer → old slot → new slot (for animation remap)</summary>
        public Dictionary<Renderer, Dictionary<int, int>> slotRemap = new Dictionary<Renderer, Dictionary<int, int>>();
    }

    /// <summary>
    /// 最终去重器 / Final deduplicator.
    /// </summary>
    public static class FinalDeduper
    {
        /// <summary>
        /// 执行最终去重与槽合并 / Run final dedup & slot merging.
        /// </summary>
        public static FinalDedupResult Run(AvatarAnalysis analysis, MaterialPatchResult matResult,
            MeshRewriteResult meshResult, Dictionary<Texture2D, Texture2D> persistedTextures, GameObject root,
            AnimationData anim, nadena.dev.ndmf.BuildContext ctx,
            Dictionary<Texture2D, string> persistedHashes = null)
        {
            var result = new FinalDedupResult();

            // 1. 材质去重 / material dedup
            var matCanonical = new Dictionary<string, Material>();
            var slotMat = new Dictionary<MaterialSlotRef, Material>(matResult.slotMaterial);
            var materialFinal = new Dictionary<MaterialSlotRef, Material>();

            foreach (var slot in analysis.slots)
            {
                var m = slotMat.TryGetValue(slot, out var sm) ? sm : slot.material;
                var fp = MaterialFingerprint(m);
                if (!matCanonical.TryGetValue(fp, out var canonical))
                {
                    canonical = m;
                    matCanonical[fp] = canonical;
                }
                materialFinal[slot] = canonical;
                if (canonical != m) result.materialsRemoved++;
            }

            // 2. 贴图/图集去重（像素 + 导入设置） / texture/atlas dedup
            // 注意: 生成贴图导入后 isReadable=false，无法 GetPixels32 —— 用管线预计算的
            // 内存贴图像素哈希（持久化前计算）/
            // NOTE: generated textures are not readable after import; use precomputed
            // in-memory hashes (computed before persistence) instead.
            var texCanonical = new Dictionary<string, Texture2D>();
            var allGenerated = new List<Texture2D>();
            foreach (var kv in persistedTextures) allGenerated.Add(kv.Value);
            foreach (var tex in allGenerated)
            {
                if (tex == null) continue;
                var fp = TextureDeduper.Fingerprint(tex);
                if (fp == null) continue;
                string hash = null;
                if (persistedHashes != null && !persistedHashes.TryGetValue(tex, out hash))
                {
                    continue; // 无法取得哈希 → 跳过该贴图去重 / cannot hash → skip
                }
                if (persistedHashes == null)
                {
                    // 兜底: 尽力直接读取（若可读） / fallback: try direct read (if readable)
                    try { hash = QuickHash(tex.GetPixels32()); }
                    catch { continue; }
                }
                var key = fp + "|" + hash;
                if (!texCanonical.TryGetValue(key, out var canonical))
                {
                    texCanonical[key] = tex;
                }
                else if (canonical != tex)
                {
                    result.texturesRemoved++;
                    // 更新材质与绑定引用 / update material & binding references
                    foreach (var slot in analysis.slots)
                    {
                        if (!materialFinal.TryGetValue(slot, out var m)) continue;
                        foreach (var prop in ShaderAnalyzer.GetTexturePropertyNames(m))
                        {
                            if (m.GetTexture(prop) == tex) m.SetTexture(prop, canonical);
                        }
                    }
                    foreach (var k in matResult.bindingTexture.Keys.ToList())
                    {
                        if (matResult.bindingTexture[k] == tex) matResult.bindingTexture[k] = canonical;
                    }
                }
            }

            // 3. 应用最终材质到渲染器 + 槽合并 / apply final materials & merge opaque slots
            var rendererSlots = new Dictionary<Renderer, List<MaterialSlotRef>>();
            foreach (var slot in analysis.slots)
            {
                if (!rendererSlots.TryGetValue(slot.renderer, out var list))
                {
                    list = new List<MaterialSlotRef>();
                    rendererSlots[slot.renderer] = list;
                }
                list.Add(slot);
            }

            foreach (var kv in rendererSlots)
            {
                var r = kv.Key;
                var slots = kv.Value.OrderBy(s => s.slotIndex).ToList();
                var mesh = meshResult.rendererMesh.TryGetValue(r, out var m2) ? m2 : GetMesh(r);
                if (mesh == null) continue;
                bool meshIsOriginal = !meshResult.rendererMesh.ContainsKey(r);

                // 计算最终材质数组（按槽位） / final material array by slot
                var finalMats = new Material[mesh.subMeshCount];
                var oldToNew = new Dictionary<int, int>();
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    finalMats[i] = null;
                    oldToNew[i] = i;
                }
                foreach (var slot in slots)
                {
                    if (slot.slotIndex < finalMats.Length)
                    {
                        finalMats[slot.slotIndex] = materialFinal[slot];
                    }
                }

                // 合并不透明同材质槽 / merge opaque same-material slots
                var groups = new List<(Material mat, List<int> slots)>();
                for (int i = 0; i < finalMats.Length; i++)
                {
                    var mat = finalMats[i];
                    if (mat == null) continue;
                    bool opaque = mat.renderQueue < 2450;
                    if (opaque)
                    {
                        var existing = groups.FirstOrDefault(g => g.mat == mat);
                        if (existing.mat != null)
                        {
                            existing.slots.Add(i);
                            continue;
                        }
                    }
                    groups.Add((mat, new List<int> { i }));
                }

                if (groups.Sum(g => g.slots.Count) != finalMats.Length)
                {
                    // 有空槽：保守保持原样 / empty slots: keep as-is (conservative)
                    groups.Clear();
                    for (int i = 0; i < finalMats.Length; i++)
                    {
                        if (finalMats[i] != null) groups.Add((finalMats[i], new List<int> { i }));
                    }
                }

                // 若发生了合并且网格是共享原资产 → 按渲染器复制，避免破坏其他引用者 /
                // if slots merged on an original (non-rewritten) mesh, duplicate it per renderer
                bool merged = groups.Sum(g => g.slots.Count) < finalMats.Length;
                if (merged && meshIsOriginal)
                {
                    mesh = Object.Instantiate(mesh);
                    mesh.name = mesh.name + " (ATO-merged)";
                    AssignMesh(r, mesh);
                }

                // 重建子网格 / rebuild submeshes
                var newMats = new List<Material>();
                var newSlotIndex = new List<int>(); // 新槽索引对应的旧槽（首个）
                mesh.subMeshCount = 0;
                var allTris = new List<int>[groups.Count];
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var (mat, slotList) = groups[gi];
                    var tris = new List<int>();
                    foreach (var oldSlot in slotList)
                    {
                        tris.AddRange(mesh.GetTriangles(oldSlot));
                    }
                    allTris[gi] = tris;
                    newMats.Add(mat);
                    newSlotIndex.Add(slotList[0]);
                }
                mesh.subMeshCount = newMats.Count;
                for (int gi = 0; gi < newMats.Count; gi++)
                {
                    mesh.SetTriangles(allTris[gi], gi);
                }
                r.sharedMaterials = newMats.ToArray();

                // 槽位重映射 / slot remap
                var remap = new Dictionary<int, int>();
                for (int newIdx = 0; newIdx < newSlotIndex.Count; newIdx++)
                {
                    foreach (var oldSlot in groups[newIdx].slots)
                    {
                        remap[oldSlot] = newIdx;
                    }
                }
                if (remap.Any(kv => kv.Key != kv.Value))
                {
                    result.slotRemap[r] = remap;
                    result.slotsMerged += remap.Count(kv => kv.Key != kv.Value);
                }
            }

            // 4. 动画槽位索引重映射 / remap animated material slot indices
            if (result.slotRemap.Count > 0 && anim != null)
            {
                result.clipsRemapped = RemapClipSlotIndices(root, anim, result.slotRemap, ctx);
            }

            return result;
        }

        /// <summary>
        /// 重写 clip 中 materials[i] 绑定为合并后的新索引 /
        /// Rewrite materials[i] bindings to merged slot indices.
        /// </summary>
        private static int RemapClipSlotIndices(GameObject root, AnimationData anim,
            Dictionary<Renderer, Dictionary<int, int>> slotRemap, nadena.dev.ndmf.BuildContext ctx)
        {
            int remapped = 0;
            foreach (var clip in anim.clips)
            {
                if (clip == null) continue;

                // 先检测是否需要重映射（绝不修改原 clip）/
                // first detect whether remapping is needed (never mutate the original)
                var floatBindings = AnimationUtility.GetCurveBindings(clip);
                var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                bool changed = false;
                foreach (var b in floatBindings)
                {
                    if (TryRemapBinding(root, b, slotRemap, out _)) { changed = true; break; }
                }
                if (!changed)
                {
                    foreach (var b in objBindings)
                    {
                        if (TryRemapBinding(root, b, slotRemap, out _)) { changed = true; break; }
                    }
                }
                if (!changed) continue;

                // 克隆并应用重映射到克隆上 / clone and apply remap to the clone
                var newClip = Object.Instantiate(clip);
                newClip.name = clip.name + " (ATO-merged)";
                foreach (var binding in floatBindings)
                {
                    if (TryRemapBinding(root, binding, slotRemap, out var newBinding))
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        AnimationUtility.SetEditorCurve(newClip, binding, null);
                        AnimationUtility.SetEditorCurve(newClip, newBinding, curve);
                    }
                }
                foreach (var binding in objBindings)
                {
                    if (TryRemapBinding(root, binding, slotRemap, out var newBinding))
                    {
                        var kf = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        AnimationUtility.SetObjectReferenceCurve(newClip, binding, null);
                        AnimationUtility.SetObjectReferenceCurve(newClip, newBinding, kf);
                    }
                }
                ctx.ObjectRegistry.RegisterReplacedObject(clip, newClip);
                ReplaceClipInControllers(anim.controllers, clip, newClip);
                remapped++;
            }
            return remapped;
        }

        private static bool TryRemapBinding(GameObject root, EditorCurveBinding binding,
            Dictionary<Renderer, Dictionary<int, int>> slotRemap, out EditorCurveBinding newBinding)
        {
            newBinding = binding;
            if (!binding.propertyName.StartsWith("materials[", System.StringComparison.Ordinal)) return false;
            int close = binding.propertyName.IndexOf(']');
            if (close < 0) return false;
            if (!int.TryParse(binding.propertyName.Substring("materials[".Length, close - "materials[".Length), out var oldSlot))
            {
                return false;
            }
            var obj = AnimationUtility.GetAnimatedObject(root, binding);
            if (!(obj is Renderer r)) return false;
            if (!slotRemap.TryGetValue(r, out var remap)) return false;
            if (!remap.TryGetValue(oldSlot, out var newSlot)) return false;
            if (newSlot == oldSlot) return false;

            var newProp = "materials[" + newSlot + binding.propertyName.Substring(close);
            newBinding = new EditorCurveBinding
            {
                path = binding.path,
                type = binding.type,
                propertyName = newProp,
            };
            return true;
        }

        private static void ReplaceClipInControllers(List<RuntimeAnimatorController> controllers,
            AnimationClip oldClip, AnimationClip newClip)
        {
            foreach (var controller in controllers)
            {
                if (!(controller is AnimatorController ac)) continue;
                foreach (var layer in ac.layers)
                {
                    ReplaceInStateMachine(layer.stateMachine, oldClip, newClip);
                }
            }
        }

        private static void ReplaceInStateMachine(AnimatorStateMachine sm, AnimationClip oldClip, AnimationClip newClip)
        {
            if (sm == null) return;
            foreach (var state in sm.states)
            {
                if (state.state.motion == oldClip) state.state.motion = newClip;
                if (state.state.motion is BlendTree bt) ReplaceInBlendTree(bt, oldClip, newClip);
            }
            foreach (var sub in sm.stateMachines)
            {
                ReplaceInStateMachine(sub.stateMachine, oldClip, newClip);
            }
        }

        private static void ReplaceInBlendTree(BlendTree tree, AnimationClip oldClip, AnimationClip newClip)
        {
            var children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion == oldClip) children[i].motion = newClip;
                else if (children[i].motion is BlendTree sub) ReplaceInBlendTree(sub, oldClip, newClip);
            }
            tree.children = children;
        }

        private static void AssignMesh(Renderer r, Mesh mesh)
        {
            if (r is SkinnedMeshRenderer smr) smr.sharedMesh = mesh;
            else if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = mesh;
            }
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        /// <summary>材质指纹 / material fingerprint</summary>
        private static string MaterialFingerprint(Material m)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(m.shader.name).Append('|').Append(m.renderQueue).Append('|');
            foreach (var kw in m.shaderKeywords.OrderBy(k => k)) sb.Append(kw).Append(';');
            sb.Append('|');
            int count = ShaderUtil.GetPropertyCount(m.shader);
            for (int i = 0; i < count; i++)
            {
                var name = ShaderUtil.GetPropertyName(m.shader, i);
                switch (ShaderUtil.GetPropertyType(m.shader, i))
                {
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        sb.Append(name).Append('=').Append(m.GetFloat(name).ToString("R")).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.Color:
                        sb.Append(name).Append('=').Append(m.GetColor(name)).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        sb.Append(name).Append('=').Append(m.GetVector(name)).Append(';');
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        var t = m.GetTexture(name);
                        sb.Append(name).Append('=').Append(t != null ? t.GetInstanceID() : -1).Append(';');
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>像素快速哈希（32 位足够去重判断） / fast pixel hash</summary>
        internal static string QuickHash(Color32[] pixels)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < pixels.Length; i++)
                {
                    h ^= pixels[i].r; h *= 16777619u;
                    h ^= pixels[i].g; h *= 16777619u;
                    h ^= pixels[i].b; h *= 16777619u;
                    h ^= pixels[i].a; h *= 16777619u;
                }
                return h.ToString("x8");
            }
        }
    }
}

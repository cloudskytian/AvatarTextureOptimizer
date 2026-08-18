// Avatar Texture Optimizer (ATO)
// Writes the new UVs (scaled + placed, or scaled in place), evacuates AAO channels,
// clones meshes when shared, clones materials when a texture maps to multiple atlases,
// and assigns atlas textures back to materials.
// 写入新 UV（缩放+摆放，或原地缩放）、疏散 AAO 通道、共享网格时克隆、
// 贴图映射到多个图集时克隆材质，并把图集贴图赋回材质。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 6c: apply UV remap + material texture assignment. / 阶段 6c：应用 UV 重映射 + 材质贴图赋值。
    /// </summary>
    public static class ATOUVRemapper
    {
        public static void Apply(ATOBuildContext build, ATOProgress progress)
        {
            // Count mesh sharing so shared meshes can be cloned per renderer. / 统计网格共享情况以按渲染器克隆。
            var meshUse = new Dictionary<Mesh, int>();
            foreach (var rr in build.renderers)
                if (rr.sourceMesh != null)
                    meshUse[rr.sourceMesh] = meshUse.TryGetValue(rr.sourceMesh, out var c) ? c + 1 : 1;

            progress.Begin(build.renderers.Count);

            foreach (var rr in build.renderers)
            {
                if (!rr.EffectiveEnabled) { progress.Advance(1); continue; }

                // Unique mesh per renderer. / 每渲染器独立网格。
                if (rr.sourceMesh != null && meshUse[rr.sourceMesh] > 1)
                {
                    rr.workingMesh = Object.Instantiate(rr.sourceMesh);
                    rr.workingMesh.name = rr.sourceMesh.name + "_ato";
                }

                // Evacuate AAO channels BEFORE rewriting UVs. / 在改写 UV 前疏散 AAO 通道。
                ATOAAOIntegration.Evacuate(build, rr);

                // Write new UVs. / 写入新 UV。
                WriteUvs(build, rr);

                // Assign textures to (possibly cloned) materials. / 给（可能克隆的）材质赋贴图。
                AssignMaterials(build, rr);

                // Apply back. / 回写。
                if (rr.workingMesh != rr.sourceMesh)
                {
                    if (rr.isSkinned) ((SkinnedMeshRenderer)rr.renderer).sharedMesh = rr.workingMesh;
                    else rr.renderer.GetComponent<MeshFilter>().sharedMesh = rr.workingMesh;
                }
                rr.renderer.sharedMaterials = rr.slots;

                progress.Advance(1, rr.renderer.name);
                progress.ThrowIfCancelled();
            }
        }

        private static void WriteUvs(ATOBuildContext build, ATORendererRef rr)
        {
            var mesh = rr.workingMesh;
            // Cache per-channel UV arrays. / 按通道缓存 UV 数组。
            var uvCache = new Dictionary<int, Vector2[]>();
            foreach (var ch in rr.usedUvChannels)
            {
                if (ATOMeshUvAccessor.TryGetUv(mesh, ch, out var uvs)) uvCache[ch] = uvs;
            }

            foreach (var space in build.uvSpaces)
            {
                if (space.meshId != rr.rendererId) continue;
                if (!uvCache.TryGetValue(space.uvChannel, out var meshUv)) continue;

                foreach (var isl in space.islands)
                {
                    var ts = isl.TotalScale;
                    var c = (isl.minUV + isl.maxUV) * 0.5f;
                    bool atlased = isl.placed && isl.atlasIndex >= 0;
                    for (int i = 0; i < isl.uv.Length; i++)
                    {
                        var u = isl.uv[i];
                        var us = c + (u - c) * ts;
                        Vector2 nu;
                        if (atlased)
                        {
                            var off = us - isl.scaledMinUv;
                            off = ATORasterizer.RotateVecCw(off, isl.rotation);
                            nu = isl.placementMinUv + off;
                        }
                        else
                        {
                            nu = us; // scaled in place (fallback) / 原地缩放（兜底）
                        }
                        meshUv[isl.localVertices[i]] = nu;
                    }
                }

                ATOMeshUvAccessor.TrySetUv(mesh, space.uvChannel, meshUv);
            }
        }

        private static void AssignMaterials(ATOBuildContext build, ATORendererRef rr)
        {
            // Map (texture, renderer, uvChannel) -> atlas. / (贴图, 渲染器, UV 通道) -> 图集。
            var atlasByKey = new Dictionary<(ATOTextureRef, int, int), ATOAtlas>();
            foreach (var atlas in build.atlases)
                foreach (var isl in atlas.islands)
                    foreach (var t in atlas.sources)
                    {
                        var key = (t, isl.meshId, isl.uvChannel);
                        if (!atlasByKey.ContainsKey(key)) atlasByKey[key] = atlas;
                    }

            // Record texture->atlas remap for animation rewriting when unambiguous. / 无歧义时记录贴图->图集重映射供动画改写。
            foreach (var tr in build.textures)
            {
                Texture2D single = null; bool multi = false;
                foreach (var atlas in build.atlases)
                {
                    if (!atlas.sources.Contains(tr)) continue;
                    if (single == null) single = atlas.texture;
                    else if (single != atlas.texture) { multi = true; break; }
                }
                if (!multi && single != null)
                    build.animRemap.textureRemap[tr.texture] = single;
            }

            var newSlots = new Material[rr.slots.Length];
            var clonedByOriginal = new Dictionary<Material, Material>();
            for (int slot = 0; slot < rr.slots.Length; slot++)
            {
                var mat = rr.slots[slot];
                if (mat == null) { newSlots[slot] = null; continue; }

                // Determine targets for each texture property of this material on this renderer.
                // 确定该材质各贴图属性在该渲染器上的目标贴图。
                bool needsClone = false;
                var propTargets = new Dictionary<string, Texture2D>();
                foreach (var tr in build.textures)
                {
                    foreach (var u in tr.usages)
                    {
                        if (u.material != mat || u.renderer != rr) continue;
                        Texture2D target;
                        if (atlasByKey.TryGetValue((tr, rr.rendererId, u.uvChannel), out var atlas))
                            target = atlas.texture; // may be null until builder runs / 构建器运行前可能为 null
                        else
                            target = tr.texture; // original / 原始
                        if (propTargets.TryGetValue(u.propertyName, out var existing) && existing != target)
                            needsClone = true;
                        propTargets[u.propertyName] = target;
                    }
                }

                Material targetMat = mat;
                if (needsClone)
                {
                    if (!clonedByOriginal.TryGetValue(mat, out targetMat))
                    {
                        targetMat = new Material(mat) { name = mat.name + "_ato" };
                        clonedByOriginal[mat] = targetMat;
                        build.animRemap.materialCloneByRenderer[mat] = new Dictionary<int, Material>();
                    }
                    build.animRemap.materialCloneByRenderer[mat][rr.rendererId] = targetMat;
                }

                foreach (var kvp in propTargets)
                {
                    if (kvp.Value == null) continue;
                    if (targetMat.HasProperty(kvp.Key))
                    {
                        targetMat.SetTexture(kvp.Key, kvp.Value);
                        // Track atlas references for post-processing reassignment. / 记录图集引用供后处理重赋值。
                        if (kvp.Value.name.StartsWith(ATOConstants.AtlasNamePrefix))
                        {
                            var at = FindAtlas(build, kvp.Value);
                            if (at != null)
                            {
                                var refKey = (targetMat, kvp.Key);
                                if (!at.references.Contains(refKey)) at.references.Add(refKey);
                            }
                        }
                    }
                }

                newSlots[slot] = targetMat;
                if (targetMat != mat)
                {
                    // Record global remap only when unambiguous. / 仅在无歧义时记录全局重映射。
                    if (!build.animRemap.materialRemap.ContainsKey(mat))
                        build.animRemap.materialRemap[mat] = targetMat;
                }
            }
            rr.slots = newSlots;
        }

        private static ATOAtlas FindAtlas(ATOBuildContext build, Texture2D tex)
        {
            foreach (var a in build.atlases)
                if (a.texture == tex) return a;
            return null;
        }
    }
}

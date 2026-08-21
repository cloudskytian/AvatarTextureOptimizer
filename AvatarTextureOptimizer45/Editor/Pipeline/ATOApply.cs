using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace net.fosa.ato
{
    /// <summary>
    /// 应用阶段 / Apply stage.
    ///
    /// 1. 构建独立贴图(整图缩放, 非图集模式/AtlasOnly/装箱回退) / build standalone textures (whole-texture scale);
    /// 2. 最终贴图/图集去重(内容+参数) / final texture/atlas dedup;
    /// 3. 克隆网格、重写UV(含AAO UV疏散), 只改网格与贴图引用, 不动材质其他任何属性
    ///    / clone meshes, rewrite UVs (with AAO UV evacuation); only meshes & texture references change;
    /// 4. 克隆材质(非破坏)并更新贴图引用 / clone materials (non-destructive) and update texture references;
    /// 5. 材质去重 + 不透明材质槽合并(更新子网格与槽索引) / material dedup + opaque slot merging;
    /// 6. 通过 NDMF 动画服务重写动画引用(材质槽/贴图属性 PPtr 曲线) / rewrite animation references via NDMF animator services;
    /// 7. 移除成品上的自身组件 / remove the ATO component from the final avatar.
    /// </summary>
    internal static class ATOApply
    {
        private static readonly Regex SlotRegex = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]$");

        public static void Run(ATOBuildState state, BuildContext ctx, GameObject avatarRoot)
        {
            Profiler.BeginSample("ATO.Apply");
            var timer = new ATOLog.StageTimer();
            timer.Start();
            var cfg = state.config;

            // 1. 独立贴图 / standalone textures
            timer.BeginStep("standaloneTextures");
            BuildStandaloneTextures(state, ctx);
            timer.EndStep();

            // 2. 最终贴图去重 / final texture dedup
            timer.BeginStep("dedupFinal");
            var texRemap = DedupFinalTextures(state);
            timer.EndStep();

            // 3. 网格UV重写 / mesh UV rewrite
            timer.BeginStep("rewriteMeshes");
            RewriteMeshes(state, ctx);
            timer.EndStep();

            // 4. 材质克隆 + 贴图引用 / material cloning + texture refs
            timer.BeginStep("updateMaterials");
            UpdateMaterials(state, ctx, texRemap);
            timer.EndStep();

            // 5. 材质去重 + 槽合并 / material dedup + slot merging
            timer.BeginStep("dedupMaterialsAndSlots");
            var matRemap = DedupMaterials(state);
            var slotRemap = MergeOpaqueSlots(state, ctx);
            timer.EndStep();

            // 6. 动画引用重写 / animation reference rewriting
            timer.BeginStep("rewriteClips");
            RewriteClips(state, ctx, texRemap, matRemap, slotRemap);
            timer.EndStep();

            timer.End("应用 Apply");
            Profiler.EndSample();
        }

        // ------------------------------------------------------------------
        private static void BuildStandaloneTextures(ATOBuildState state, BuildContext ctx)
        {
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full || tex.dedupOf != null) continue;
                if (!tex.isStandaloneResult) continue;

                var readable = ATOTextureIO.EnsureReadable(tex);
                if (readable == null)
                {
                    ATOLog.Warn($"无法读取贴图, 保持原引用 / cannot read '{tex.source.name}', keeping the original reference");
                    tex.result = tex.source;
                    continue;
                }

                float s = tex.wholeScale;
                int nw = Mathf.Max(1, Mathf.RoundToInt(tex.width * s));
                int nh = Mathf.Max(1, Mathf.RoundToInt(tex.height * s));

                var newTex = new Texture2D(nw, nh, TextureFormat.RGBA32, false, !tex.sRGB)
                {
                    name = tex.source.name + "_ATO",
                    wrapMode = tex.wrapU,
                    filterMode = tex.filterMode
                };

                if (s >= 0.999f && nw == tex.width && nh == tex.height)
                {
                    // 原样拷贝(仍应用导入参数优化) / 1:1 copy (import params still optimized)
                    try
                    {
                        var srcPixels = readable.GetPixels32();
                        newTex.SetPixels32(srcPixels);
                    }
                    catch
                    {
                        ATOLog.Warn($"贴图拷贝失败 / copy failed for {tex.source.name}");
                        UnityEngine.Object.DestroyImmediate(newTex);
                        tex.result = tex.source;
                        continue;
                    }
                }
                else
                {
                    ScaleWholeTexture(readable, tex, newTex);
                }

                newTex.Apply(false, false);
                tex.result = newTex;
                tex.outputHash = ATOPacker.HashPixels32(newTex.GetPixels32());

                ATOImportConfig.SaveAndConfigure(state, ctx, newTex, tex.category, tex.sRGB, tex.hasAlpha, tex, null, tex.usedChannels);

                var outInfo = new ATOTextureInfo
                {
                    source = newTex,
                    result = newTex,
                    width = nw,
                    height = nh,
                    sRGB = tex.sRGB,
                    filterMode = tex.filterMode,
                    category = tex.category,
                    hasAlpha = tex.hasAlpha,
                    isStandaloneResult = true,
                    outputHash = tex.outputHash
                };
                state.outputTextures.Add(outInfo);

                state.totalOutputPixels += (long)nw * nh;
                ATOLog.InfoVerbose($"独立贴图输出 / standalone output: {tex.source.name} {tex.width}x{tex.height} -> {nw}x{nh}");
                ATOTextureIO.ReleaseReadable(tex);
            }
        }

        private static void ScaleWholeTexture(Texture2D readable, ATOTextureInfo tex, Texture2D dst)
        {
            int sw = readable.width, sh = readable.height;
            var src = readable.GetPixels32();
            int dw = dst.width, dh = dst.height;
            var pixels = new Color32[dw * dh];
            bool premul = tex.hasAlpha;
            float rx = sw / (float)dw, ry = sh / (float)dh;

            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * rx - 0.5f, fy = (y + 0.5f) * ry - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, sw - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, sh - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, sw - 1), y1 = Mathf.Clamp(y0 + 1, 0, sh - 1);
                    float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);

                    var c00 = src[y0 * sw + x0];
                    var c10 = src[y0 * sw + x1];
                    var c01 = src[y1 * sw + x0];
                    var c11 = src[y1 * sw + x1];

                    float a = (c00.a * (1 - tx) + c10.a * tx) * (1 - ty) + (c01.a * (1 - tx) + c11.a * tx) * ty;
                    byte outA = (byte)Mathf.RoundToInt(a);
                    if (outA == 0)
                    {
                        pixels[y * dw + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    byte r, g, b;
                    if (premul)
                    {
                        float rP = (c00.r * c00.a * (1 - tx) + c10.r * c10.a * tx) * (1 - ty)
                                   + (c01.r * c01.a * (1 - tx) + c11.r * c11.a * tx) * ty;
                        float gP = (c00.g * c00.a * (1 - tx) + c10.g * c10.a * tx) * (1 - ty)
                                   + (c01.g * c01.a * (1 - tx) + c11.g * c11.a * tx) * ty;
                        float bP = (c00.b * c00.a * (1 - tx) + c10.b * c10.a * tx) * (1 - ty)
                                   + (c01.b * c01.a * (1 - tx) + c11.b * c11.a * tx) * ty;
                        r = (byte)Mathf.Clamp(Mathf.RoundToInt(rP / 255f / Mathf.Max(a, 1f) * 255f), 0, 255);
                        g = (byte)Mathf.Clamp(Mathf.RoundToInt(gP / 255f / Mathf.Max(a, 1f) * 255f), 0, 255);
                        b = (byte)Mathf.Clamp(Mathf.RoundToInt(bP / 255f / Mathf.Max(a, 1f) * 255f), 0, 255);
                    }
                    else
                    {
                        r = (byte)Mathf.RoundToInt((c00.r * (1 - tx) + c10.r * tx) * (1 - ty) + (c01.r * (1 - tx) + c11.r * tx) * ty);
                        g = (byte)Mathf.RoundToInt((c00.g * (1 - tx) + c10.g * tx) * (1 - ty) + (c01.g * (1 - tx) + c11.g * tx) * ty);
                        b = (byte)Mathf.RoundToInt((c00.b * (1 - tx) + c10.b * tx) * (1 - ty) + (c01.b * (1 - tx) + c11.b * tx) * ty);
                    }

                    pixels[y * dw + x] = new Color32(r, g, b, outA);
                }
            }

            dst.SetPixels32(pixels);
        }

        private static Dictionary<Texture2D, Texture2D> DedupFinalTextures(ATOBuildState state)
        {
            var remap = new Dictionary<Texture2D, Texture2D>();
            if (!state.config.dedupTextures) return remap;

            var groups = state.outputTextures
                .GroupBy(t => $"{t.outputHash}|{t.category}|{t.sRGB}|{t.filterMode}")
                .Where(g => g.Count() > 1);

            foreach (var g in groups)
            {
                var rep = g.First();
                foreach (var t in g.Skip(1))
                {
                    if (t.result == null || rep.result == null || t.result == rep.result) continue;
                    remap[t.result] = rep.result;
                    // 同步来源贴图的最终引用 / sync the source textures' final references
                    foreach (var src in state.textures)
                    {
                        if (src.result == t.result) src.result = rep.result;
                    }

                    ATOLog.Info($"最终贴图去重 / final texture dedup: {t.result.name} -> {rep.result.name}");
                }
            }

            return remap;
        }

        // ------------------------------------------------------------------
        private static void RewriteMeshes(ATOBuildState state, BuildContext ctx)
        {
            foreach (var mi in state.meshes)
            {
                // 收集岛的目标UV(归一化矩形 + 旋转) / collect island target UVs (normalized rect + rotation)
                foreach (var island in mi.islands)
                {
                    ATOAtlas atlas = null;
                    Rect normRect = default;
                    int rot = 0;
                    foreach (var kv in island.perTexture)
                    {
                        if (kv.Value.atlas == null) continue;
                        atlas = kv.Value.atlas;
                        var ar = kv.Value.atlasRect;
                        normRect = new Rect(ar.x / atlas.width, ar.y / atlas.height, ar.width / atlas.width, ar.height / atlas.height);
                        rot = kv.Value.rotation;
                        break;
                    }

                    if (atlas == null) continue; // 独立贴图: UV保持 / standalone: UVs unchanged

                    var uvList = mi.newUVs[island.channel];
                    var verts = new HashSet<int>();
                    int[] tris = mi.mesh.triangles;
                    foreach (var t in island.triangles)
                    {
                        for (int c = 0; c < 3; c++) verts.Add(tris[t * 3 + c]);
                    }

                    var b = island.uvBounds;
                    foreach (var v in verts)
                    {
                        var uv = uvList[v];
                        float lu = b.width > 1e-9f ? (uv.x - b.xMin) / b.width : 0f;
                        float lv = b.height > 1e-9f ? (uv.y - b.yMin) / b.height : 0f;
                        float tu, tv;
                        switch (rot & 3)
                        {
                            case 1: tu = 1f - lv; tv = lu; break;
                            case 2: tu = 1f - lu; tv = 1f - lv; break;
                            case 3: tu = lv; tv = 1f - lu; break;
                            default: tu = lu; tv = lv; break;
                        }

                        uvList[v] = new Vector2(normRect.x + tu * normRect.width, normRect.y + tv * normRect.height);
                    }
                }

                // AAO UV疏散(在改写前, 基于原网格) / AAO evacuation (before rewrite, based on the original mesh)
                if (mi.renderer is SkinnedMeshRenderer smr)
                {
                    foreach (var channel in mi.newUVs.Keys)
                    {
                        ATOAAOCompat.EvacuateIfNeeded(smr, mi.mesh, CreateWorkingCopy(state, ctx, mi), channel);
                    }
                }

                // 克隆网格并写UV, 赋回渲染器 / clone the mesh, write UVs and assign it back to the renderer
                var working = CreateWorkingCopy(state, ctx, mi);
                foreach (var kv in mi.newUVs)
                {
                    working.SetUVs(kv.Key, kv.Value);
                }

                mi.working = working;

                if (mi.renderer is SkinnedMeshRenderer smrAssign)
                {
                    smrAssign.sharedMesh = working;
                }
                else if (mi.renderer is MeshRenderer mrAssign)
                {
                    var filter = mrAssign.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = working;
                }
            }
        }

        private static Mesh CreateWorkingCopy(ATOBuildState state, BuildContext ctx, ATOMeshInfo mi)
        {
            if (mi.working != null) return mi.working;
            var working = UnityEngine.Object.Instantiate(mi.mesh);
            working.name = mi.mesh.name + " (ATO UVs)";
            ctx.AssetSaver.SaveAsset(working);
            ObjectRegistry.RegisterReplacedObject(mi.mesh, working);
            mi.working = working;
            return working;
        }

        // ------------------------------------------------------------------
        private static void UpdateMaterials(ATOBuildState state, BuildContext ctx, Dictionary<Texture2D, Texture2D> texRemap)
        {
            // 最终贴图映射 / final texture mapping
            var finalTex = new Dictionary<ATOTextureInfo, Texture2D>();
            foreach (var tex in state.textures)
            {
                if (tex.dedupOf != null) continue;
                var result = tex.result;
                if (result == null) continue;
                finalTex[tex] = texRemap.TryGetValue(result, out var rep) ? rep : result;
            }

            // 更新材质贴图属性 / update material texture properties
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full || tex.dedupOf != null) continue;
                if (!finalTex.TryGetValue(tex, out var final)) continue;

                foreach (var r in tex.refs)
                {
                    if (r.material == null || string.IsNullOrEmpty(r.property)) continue;
                    if (!state.byMaterial.TryGetValue(r.material, out var matInfo)) continue;
                    var mat = EnsureCloned(state, ctx, matInfo);
                    if (mat != null) mat.SetTexture(r.property, final);
                }
            }

            // 材质槽赋值(此时尚未合并) / assign material slots (before merging)
            foreach (var mi in state.meshes)
            {
                var slots = new Material[mi.slots.Length];
                for (int i = 0; i < mi.slots.Length; i++)
                {
                    var original = mi.slots[i];
                    slots[i] = original == null ? null
                        : (state.byMaterial.TryGetValue(original, out var mi2) ? mi2.current : original);
                }

                mi.renderer.sharedMaterials = slots;
            }
        }

        private static Material EnsureCloned(ATOBuildState state, BuildContext ctx, ATOMaterialInfo matInfo)
        {
            var current = matInfo.current;
            if (current == null) return null;
            bool persistent = EditorUtility.IsPersistent(current);
            if (!persistent || ctx.AssetSaver.IsTemporaryAsset(current)) return current;

            // 克隆材质(非破坏) / clone the material (non-destructive)
            var clone = new Material(current)
            {
                name = current.name + " (ATO)"
            };
            ctx.AssetSaver.SaveAsset(clone);
            ObjectRegistry.RegisterReplacedObject(current, clone);
            matInfo.current = clone;
            ATOLog.InfoVerbose($"克隆材质 / cloned material: {current.name}");
            return clone;
        }

        // ------------------------------------------------------------------
        private static Dictionary<Material, Material> DedupMaterials(ATOBuildState state)
        {
            var remap = new Dictionary<Material, Material>();
            if (!state.config.dedupMaterials) return remap;

            var groups = state.materialInfos
                .Where(m => m.current != null)
                .GroupBy(m => MaterialKey(m.current))
                .Where(g => g.Count() > 1);

            foreach (var g in groups)
            {
                var rep = g.First();
                foreach (var m in g.Skip(1))
                {
                    if (m.current == rep.current) continue;
                    remap[m.current] = rep.current;
                    rep.slotRefs.AddRange(m.slotRefs);
                    ATOLog.Info($"材质去重 / material dedup: {m.current.name} -> {rep.current.name}");
                }
            }

            return remap;
        }

        private static string MaterialKey(Material m)
        {
            string json;
            try
            {
                json = EditorJsonUtility.ToJson(m);
            }
            catch
            {
                json = "";
            }

            var kws = string.Join(",", m.shaderKeywords.OrderBy(k => k));
            return $"{m.shader?.name}|{m.renderQueue}|{kws}|{json}";
        }

        // ------------------------------------------------------------------
        /// <summary>
        /// 合并同一网格内可判定为相同的不透明材质槽 / Merge identical opaque material slots on the same mesh.
        /// 返回: renderer -> (旧槽 -> 新槽) 映射, 供动画引用重写 / returns slot remaps for animation rewriting.
        /// </summary>
        private static Dictionary<Renderer, Dictionary<int, int>> MergeOpaqueSlots(ATOBuildState state, BuildContext ctx)
        {
            var slotRemap = new Dictionary<Renderer, Dictionary<int, int>>();
            if (!state.config.mergeOpaqueSlots) return slotRemap;

            foreach (var mi in state.meshes)
            {
                var slots = mi.renderer.sharedMaterials;
                if (slots == null || slots.Length <= 1) continue;

                // 动画是否单独切换这些槽 / does animation switch any slot individually?
                bool anyAnimated = state.anim.slotBindings.TryGetValue(mi.renderer, out var bySlot) && bySlot.Count > 0;

                // 按材质分组 / group by material
                var groups = new Dictionary<int, List<int>>();
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] == null) continue;
                    int key = slots[i].GetInstanceID();
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
                    list.Add(i);
                }

                var map = new Dictionary<int, int>();
                var newSlots = new List<Material>();
                var newSubmeshMembers = new List<List<int>>();
                int nextSlot = 0;
                var processed = new HashSet<int>();

                for (int i = 0; i < slots.Length; i++)
                {
                    if (processed.Contains(i)) continue;
                    int key = slots[i] == null ? -1 : slots[i].GetInstanceID();
                    var group = slots[i] != null && groups.TryGetValue(key, out var g) ? g : new List<int> { i };

                    bool opaque = slots[i] != null && state.byMaterial.TryGetValue(mi.slots[i], out var info) && info.opaque;
                    bool mergeable = opaque && group.Count > 1 && !anyAnimated;

                    var members = mergeable ? group : new List<int> { i };
                    newSlots.Add(slots[i]);
                    newSubmeshMembers.Add(new List<int>(members));
                    foreach (var mIdx in members)
                    {
                        processed.Add(mIdx);
                        if (mIdx != nextSlot) map[mIdx] = nextSlot;
                    }

                    nextSlot++;
                }

                if (map.Count == 0) continue;

                // 重写网格子网格: 被合并槽的三角形全部并入代表槽 / rewrite submeshes: merged slots' triangles join the representative
                var working = mi.working ?? CreateWorkingCopy(state, ctx, mi);
                int[] srcTris = mi.mesh.triangles;
                var allTris = new List<int[]>(newSubmeshMembers.Count);
                foreach (var members in newSubmeshMembers)
                {
                    var tris = new List<int>();
                    foreach (var mIdx in members)
                    {
                        if (mIdx >= mi.mesh.subMeshCount) continue;
                        var sm = mi.mesh.GetSubMesh(mIdx);
                        tris.AddRange(new ArraySegment<int>(srcTris, sm.indexStart, sm.indexCount));
                    }

                    allTris.Add(tris.ToArray());
                }

                working.subMeshCount = allTris.Count;
                for (int s = 0; s < allTris.Count; s++)
                {
                    working.SetTriangles(allTris[s], s);
                }

                mi.renderer.sharedMaterials = newSlots.ToArray();
                mi.slots = newSlots.ToArray();
                slotRemap[mi.renderer] = map;
                ATOLog.Info($"合并材质槽 / merged material slots on {mi.renderer.name}: {string.Join(",", map.Select(kv => $"{kv.Key}->{kv.Value}"))}");
            }

            return slotRemap;
        }

        // ------------------------------------------------------------------
        private static void RewriteClips(ATOBuildState state, BuildContext ctx,
            Dictionary<Texture2D, Texture2D> texRemap,
            Dictionary<Material, Material> matRemap,
            Dictionary<Renderer, Dictionary<int, int>> slotRemap)
        {
            try
            {
                var asc = ctx.Extension<AnimatorServicesContext>();
                var index = asc.AnimationIndex;

                // 贴图映射 / texture mapping
                var finalTex = new Dictionary<Texture2D, Texture2D>();
                foreach (var tex in state.textures)
                {
                    if (tex.dedupOf != null || tex.result == null) continue;
                    finalTex[tex.source] = texRemap.TryGetValue(tex.result, out var rep) ? rep : tex.result;
                }

                // 材质映射 / material mapping
                var finalMat = new Dictionary<Material, Material>();
                foreach (var mi2 in state.materialInfos)
                {
                    if (mi2.current == null) continue;
                    var m = matRemap.TryGetValue(mi2.current, out var rep2) ? rep2 : mi2.current;
                    if (m != mi2.original) finalMat[mi2.original] = m;
                }

                bool needAny = finalTex.Count > 0 || finalMat.Count > 0 || slotRemap.Count > 0;
                if (!needAny) return;

                foreach (var clip in index.ClipsWithObjectCurves)
                {
                    bool dirty = false;
                    var bindings = clip.GetObjectCurveBindings().ToList();
                    foreach (var binding in bindings)
                    {
                        var curve = clip.GetObjectCurve(binding);
                        if (curve == null || curve.Length == 0) continue;

                        // 材质槽曲线 / material slot curves
                        var slotMatch = SlotRegex.Match(binding.propertyName);
                        if (slotMatch.Success && typeof(Renderer).IsAssignableFrom(binding.type))
                        {
                            int oldSlot = int.Parse(slotMatch.Groups[1].Value);
                            int newSlot = oldSlot;
                            foreach (var kv in slotRemap)
                            {
                                if (kv.Key == null) continue;
                                string path = AnimationUtility.CalculateTransformPath(kv.Key.transform, ctx.AvatarRootTransform);
                                if (path != binding.path) continue;
                                if (kv.Value.TryGetValue(oldSlot, out var ns)) { newSlot = ns; break; }
                            }

                            bool changed = false;
                            for (int i = 0; i < curve.Length; i++)
                            {
                                if (curve[i].value is Material mat && finalMat.TryGetValue(mat, out var rep))
                                {
                                    curve[i].value = rep;
                                    changed = true;
                                }
                            }

                            if (newSlot != oldSlot)
                            {
                                // 移除旧槽曲线, 写入新槽曲线 / remove the old-slot curve, write the new-slot curve
                                clip.SetObjectCurve(binding, null);
                                clip.SetObjectCurve(new EditorCurveBinding
                                {
                                    path = binding.path,
                                    type = binding.type,
                                    propertyName = $"m_Materials.Array.data[{newSlot}]"
                                }, curve);
                                dirty = true;
                            }
                            else if (changed)
                            {
                                clip.SetObjectCurve(binding, curve);
                                dirty = true;
                            }

                            continue;
                        }

                        // 贴图属性曲线 / texture property curves
                        if (binding.type == typeof(Material))
                        {
                            bool changed = false;
                            for (int i = 0; i < curve.Length; i++)
                            {
                                if (curve[i].value is Texture2D t && finalTex.TryGetValue(t, out var rep))
                                {
                                    curve[i].value = rep;
                                    changed = true;
                                }
                            }

                            if (changed)
                            {
                                clip.SetObjectCurve(binding, curve);
                                dirty = true;
                            }
                        }
                    }

                    if (dirty)
                    {
                        ATOLog.InfoVerbose($"动画引用已更新 / animation references updated: {clip.Name}");
                    }
                }
            }
            catch (Exception e)
            {
                ATOLog.Warn($"动画引用更新失败(材质/贴图切换动画可能失效) / animation reference rewriting failed: {e.Message}");
            }
        }
    }
}

// AvatarAnalyzer.cs / AvatarAnalyzer.cs
// Scans the avatar (renderers, materials, meshes, UVs, animations) and builds an initial
// UV->texture mapping for all valid textures.
// 扫描Avatar（渲染器、材质、网格、UV、动画）并为所有合法贴图建立初始的UV->贴图映射。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using net.fosa.avatar_texture_optimizer;
using net.fosa.avatar_texture_optimizer.Editor.Groups;
using net.fosa.avatar_texture_optimizer.Editor.Util;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    /// <summary>
    /// Holds analysis results from the avatar scan.
    /// 保存Avatar扫描的分析结果。
    /// </summary>
    public class AvatarAnalysisResult
    {
        public AvatarTextureOptimizer Settings;
        public Transform AvatarRoot;
        public List<RendererEntry> Renderers = new();
        public List<UVIsland> Islands = new();
        public Dictionary<TextureDescriptor, Texture2D> DeduplicatedTextures = new();
        public Dictionary<Texture2D, Texture2D> OriginalToDeduplicated = new();
        public HashSet<UnityEngine.Object> WhitelistObjects = new();
        public HashSet<Texture2D> WhitelistedTextures = new();
        public List<UVGroup> UvGroups = new();
        public List<TextureTypeGroup> TypeGroups = new();
        public AnimationAnalysisResult Animation = new();
        public bool IsValid = true;

        /// <summary>Per-triangle max world area (populated by BlendShapeAnalyzer). Keyed by (RendererEntry, submeshIdx) -> float[].
        /// 每个三角面的最大世界面积（由BlendShapeAnalyzer填充）。按(RendererEntry, submeshIdx) -> float[]索引。</summary>
        internal Dictionary<(RendererEntry re, int submesh), float[]> MaxTriangleAreas = new();
    }

    /// <summary>
    /// Per-renderer analysis data.
    /// 每个渲染器的分析数据。
    /// </summary>
    public class RendererEntry
    {
        public Renderer Renderer;
        public Mesh SharedMesh;
        public Mesh WorkingMesh;    // a cloned mesh that we will edit / 我们将编辑的克隆网格
        public MaterialEntry[] Materials;
        public SkinnedMeshRenderer Skinned;
        public bool IsEditorOnly;
    }

    /// <summary>
    /// Per-material-slot entry.
    /// 每个材质槽的条目。
    /// </summary>
    public class MaterialEntry
    {
        public Material Material;
        public ShaderDescriptor ShaderDesc;
        public AlphaMode AlphaMode;
        public float Cutoff;
        public List<(TexturePropertyDescriptor prop, Texture2D tex, int uvChannel, TextureUsageFlags usage)> TextureBindings = new();
    }

    /// <summary>
    /// Core analyzer class.
    /// 核心分析器类。
    /// </summary>
    public static class AvatarAnalyzer
    {
        /// <summary>
        /// Run analysis. Returns null on fatal errors.
        /// 执行分析。致命错误时返回null。
        /// </summary>
        public static AvatarAnalysisResult Analyze(BuildContext context, AvatarTextureOptimizer settings, ATOLogger log)
        {
            var result = new AvatarAnalysisResult { Settings = settings, AvatarRoot = context.AvatarRootTransform };

            // Build whitelist set / 构建白名单集合
            if (settings.whitelist != null)
                foreach (var obj in settings.whitelist)
                    if (obj != null) result.WhitelistObjects.Add(obj);

            using (log.Phase("phase.scanTextures"))
            {
                if (!ScanRenderers(context, settings, result, log)) { result.IsValid = false; return result; }
            }

            // Texture deduplication BEFORE animation/island analysis
            // 在动画/岛分析之前先做贴图去重
            using (log.Phase("phase.dedup"))
            {
                ApplyPreDeduplication(result, log);
            }

            using (log.Phase("phase.scanAnimations"))
            {
                ScanAnimations(context, settings, result, log);
            }

            // Merge animation-added textures into material TextureBindings
            // 将动画添加的贴图合并入材质TextureBindings
            MergeAnimatedTextures(result, log);

            using (log.Phase("phase.blendShapes"))
            {
                ComputeBlendShapeMaxAreas(result, log);
            }

            using (log.Phase("phase.extractIslands"))
            {
                ExtractUVIslands(result, log);
            }
            using (log.Phase("phase.buildUVGroups"))
            {
                BuildGroups(result, log);
            }

            return result;
        }

        private static void ApplyPreDeduplication(AvatarAnalysisResult res, ATOLogger log)
        {
            // Collect all referenced textures / 收集所有引用的贴图
            var allTex = new HashSet<Texture2D>();
            foreach (var re in res.Renderers)
                foreach (var me in re.Materials)
                {
                    if (me == null) continue;
                    foreach (var b in me.TextureBindings)
                        if (b.tex != null) allTex.Add(b.tex);
                }

            var map = TextureDeduplicator.Deduplicate(allTex, res.WhitelistObjects);
            TextureDeduplicator.ApplyMap(map, res.Renderers.Select(r => r.Renderer));

            // Update TextureBindings to point to deduplicated textures
            // 更新TextureBindings指向去重后的贴图
            int replaced = 0;
            foreach (var re in res.Renderers)
                foreach (var me in re.Materials)
                {
                    if (me == null) continue;
                    for (int i = 0; i < me.TextureBindings.Count; i++)
                    {
                        var b = me.TextureBindings[i];
                        if (b.tex != null && map.TryGetValue(b.tex, out var rep) && rep != b.tex)
                        {
                            me.TextureBindings[i] = (b.prop, rep, b.uvChannel, b.usage);
                            res.OriginalToDeduplicated[b.tex] = rep;
                            replaced++;
                        }
                    }
                }

            if (replaced > 0)
                log.LogInfo($"[ATO] Pre-dedup replaced {replaced} texture references / 预去重替换了{replaced}个贴图引用");
        }

        private static bool ScanRenderers(BuildContext context, AvatarTextureOptimizer settings, AvatarAnalysisResult res, ATOLogger log)
        {
            var root = context.AvatarRootObject;
            if (!settings.IsValidAvatarRoot())
            {
                log.LogError(ATOLocalization.T("error.noAvatarDescriptor"));
                return false;
            }

            // Get all renderers (not EditorOnly) / 获取所有渲染器（非EditorOnly）
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (r.gameObject.CompareTag("EditorOnly")) continue;

                var entry = new RendererEntry { Renderer = r };
                entry.Skinned = r as SkinnedMeshRenderer;

                Mesh mesh = null;
                if (entry.Skinned != null) mesh = entry.Skinned.sharedMesh;
                else if (r is MeshFilter mf) mesh = mf.sharedMesh;

                if (mesh == null)
                {
                    log.LogInfo($"[ATO] Skipping renderer {r.name} (no mesh) / 跳过渲染器{r.name}（无网格）");
                    continue;
                }
                entry.SharedMesh = mesh;
                // Clone mesh so we can edit UVs / 克隆网格以便编辑UV
                entry.WorkingMesh = UnityEngine.Object.Instantiate(mesh);
                entry.WorkingMesh.name = "ATO_" + mesh.name;

                var mats = r.sharedMaterials;
                entry.Materials = new MaterialEntry[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    var me = new MaterialEntry { Material = mat };
                    entry.Materials[i] = me;
                    if (mat == null) continue;
                    var shader = mat.shader;
                    var desc = ShaderPropertyDatabase.GetDescriptor(shader);
                    me.ShaderDesc = desc;
                    me.AlphaMode = ShaderPropertyDatabase.GetAlphaMode(mat, desc);
                    me.Cutoff = ShaderPropertyDatabase.GetCutoff(mat, desc);

                    // Detect each texture binding / 检测每个贴图绑定
                    bool allWhitelisted = IsWhitelistedObject(r, res.WhitelistObjects) || IsWhitelistedObject(mat, res.WhitelistObjects);

                    foreach (var prop in desc.Textures)
                    {
                        if (prop.Kind == TexturePropertyKind.Ignored) continue;
                        if (!ShaderPropertyDatabase.IsPropertyActive(mat, prop)) continue;

                        var tex = mat.GetTexture(prop.PropertyName) as Texture2D;
                        if (tex == null) continue;

                        int uvChannel = ShaderPropertyDatabase.GetUVChannel(mat, prop);
                        bool hasNonDefaultST = ShaderPropertyDatabase.HasNonDefaultST(mat, prop);

                        var usage = UsageFromKind(prop.Kind);
                        if (prop.IsNormalMap) usage |= TextureUsageFlags.Normal;
                        if (me.AlphaMode != AlphaMode.Opaque) usage |= TextureUsageFlags.Transparent;
                        if (me.AlphaMode == AlphaMode.Cutout) usage |= TextureUsageFlags.IsCutout;

                        // Alpha detection: check importer for alphaIsTransparency / Alpha检测
                        bool hasAlpha = false;
                        try
                        {
                            var tpath = AssetDatabase.GetAssetPath(tex);
                            if (!string.IsNullOrEmpty(tpath))
                            {
                                var ti = AssetImporter.GetAtPath(tpath) as TextureImporter;
                                if (ti != null) hasAlpha = ti.DoesSourceTextureHaveAlpha();
                            }
                        }
                        catch { /* ignore / 忽略 */ }
                        if (hasAlpha) usage |= TextureUsageFlags.HasAlpha;

                        bool texWhitelisted = allWhitelisted || IsWhitelistedObject(tex, res.WhitelistObjects) || hasNonDefaultST;
                        if (texWhitelisted) res.WhitelistedTextures.Add(tex);

                        // Ensure texture is readable / 确保贴图可读
                        if (!tex.isReadable)
                        {
                            try
                            {
                                log.LogWarning(ATOLocalization.T("warning.textureNoReadable", tex.name), tex);
                                res.WhitelistedTextures.Add(tex);
                            }
                            catch { /* ignore / 忽略 */ }
                        }

                        me.TextureBindings.Add((prop, tex, uvChannel, usage));
                    }
                }

                res.Renderers.Add(entry);
            }

            return true;
        }

        private static void MergeAnimatedTextures(AvatarAnalysisResult res, ATOLogger log)
        {
            if (res.Animation == null) return;
            int added = 0;

            // Add animated textures (texture swaps in animation clips) to material bindings
            // 将动画切换的贴图添加到材质绑定
            foreach (var kv in res.Animation.AnimatedTextures)
            {
                var (renderer, slot, propName) = kv.Key;
                var texList = kv.Value;
                var re = res.Renderers.FirstOrDefault(r => r.Renderer == renderer);
                if (re == null) continue;
                if (slot < 0 || slot >= re.Materials.Length) continue;
                var me = re.Materials[slot];
                if (me == null || me.Material == null) continue;

                // Find the prop descriptor matching propName / 找到匹配propName的属性描述符
                TexturePropertyDescriptor prop = null;
                foreach (var p in me.ShaderDesc.Textures)
                    if (p.PropertyName == propName) { prop = p; break; }
                if (prop == null)
                {
                    // Unknown property in shader; skip conservatively (whitelist those textures)
                    // 着色器中未知属性；保守跳过（把那些贴图加入白名单）
                    foreach (var at in texList) if (at != null) res.WhitelistedTextures.Add(at);
                    continue;
                }
                if (prop.Kind == TexturePropertyKind.Ignored) continue;

                // Check ST-animated (already whitelisted) / 检查ST动画（已白名单）
                bool stAnimated = res.Animation.AnimatedST.Contains((renderer, slot, propName));

                foreach (var at in texList)
                {
                    if (at == null) continue;
                    // Check if already bound / 检查是否已绑定
                    bool already = false;
                    foreach (var b in me.TextureBindings)
                        if (b.prop.PropertyName == propName && b.tex == at) { already = true; break; }
                    if (already) continue;

                    var usage = UsageFromKind(prop.Kind);
                    if (prop.IsNormalMap) usage |= TextureUsageFlags.Normal;
                    if (me.AlphaMode != AlphaMode.Opaque) usage |= TextureUsageFlags.Transparent;
                    if (me.AlphaMode == AlphaMode.Cutout) usage |= TextureUsageFlags.IsCutout;
                    try
                    {
                        var tpath = AssetDatabase.GetAssetPath(at);
                        if (!string.IsNullOrEmpty(tpath))
                        {
                            var ti = AssetImporter.GetAtPath(tpath) as TextureImporter;
                            if (ti != null && ti.DoesSourceTextureHaveAlpha()) usage |= TextureUsageFlags.HasAlpha;
                        }
                    }
                    catch { /* ignore */ }

                    bool wl = stAnimated || IsWhitelistedObject(at, res.WhitelistObjects);
                    if (wl) res.WhitelistedTextures.Add(at);

                    int uvCh = ShaderPropertyDatabase.GetUVChannel(me.Material, prop);
                    me.TextureBindings.Add((prop, at, uvCh, usage));
                    added++;
                }
            }

            // Merge animated materials: material switches bring in new material textures
            // 合并动画材质：材质切换会引入新材质贴图
            foreach (var kv in res.Animation.AnimatedMaterials)
            {
                var (renderer, slot) = kv.Key;
                var matList = kv.Value;
                var re = res.Renderers.FirstOrDefault(r => r.Renderer == renderer);
                if (re == null) continue;
                if (slot < 0 || slot >= re.Materials.Length) continue;

                foreach (var animMat in matList)
                {
                    if (animMat == null) continue;
                    // Check if this animMat's textures are already covered
                    // If this new material uses the same shader & same UV layout, merge its textures into the slot
                    // 检查这个animMat的贴图是否已覆盖；若同shader同UV布局则合并其贴图
                    if (re.Materials[slot] == null || re.Materials[slot].Material == null) continue;
                    var curMat = re.Materials[slot].Material;
                    var curDesc = re.Materials[slot].ShaderDesc;
                    if (animMat.shader != curMat.shader)
                    {
                        // Different shader -> whitelist the slot's textures (different UV layout possible)
                        // 不同shader -> 白名单该槽的贴图（可能不同UV布局）
                        foreach (var b in re.Materials[slot].TextureBindings)
                            if (b.tex != null) res.WhitelistedTextures.Add(b.tex);
                        continue;
                    }

                    foreach (var prop in curDesc.Textures)
                    {
                        if (prop.Kind == TexturePropertyKind.Ignored) continue;
                        if (!ShaderPropertyDatabase.IsPropertyActive(animMat, prop)) continue;
                        var at = animMat.GetTexture(prop.PropertyName) as Texture2D;
                        if (at == null) continue;
                        bool already = false;
                        foreach (var b in re.Materials[slot].TextureBindings)
                            if (b.prop.PropertyName == prop.PropertyName && b.tex == at) { already = true; break; }
                        if (already) continue;

                        var usage = UsageFromKind(prop.Kind);
                        if (prop.IsNormalMap) usage |= TextureUsageFlags.Normal;
                        var am = ShaderPropertyDatabase.GetAlphaMode(animMat, curDesc);
                        if (am != AlphaMode.Opaque) usage |= TextureUsageFlags.Transparent;
                        if (am == AlphaMode.Cutout) usage |= TextureUsageFlags.IsCutout;
                        try
                        {
                            var tpath = AssetDatabase.GetAssetPath(at);
                            if (!string.IsNullOrEmpty(tpath))
                            {
                                var ti = AssetImporter.GetAtPath(tpath) as TextureImporter;
                                if (ti != null && ti.DoesSourceTextureHaveAlpha()) usage |= TextureUsageFlags.HasAlpha;
                            }
                        }
                        catch { /* ignore */ }

                        int uvCh = ShaderPropertyDatabase.GetUVChannel(animMat, prop);
                        bool wl = IsWhitelistedObject(at, res.WhitelistObjects);
                        if (wl) res.WhitelistedTextures.Add(at);
                        re.Materials[slot].TextureBindings.Add((prop, at, uvCh, usage));
                        added++;
                    }
                }
            }

            if (added > 0)
                log.LogInfo($"[ATO] Merged {added} animation-referenced textures into bindings / 将{added}个动画引用的贴图合并入绑定");
        }

        private static void ComputeBlendShapeMaxAreas(AvatarAnalysisResult res, ATOLogger log)
        {
            int meshesProcessed = 0;
            foreach (var re in res.Renderers)
            {
                if (re.Skinned == null && !(re.Renderer is MeshFilter)) continue;
                if (re.WorkingMesh == null) continue;
                try
                {
                    float[] areas;
                    if (re.Skinned != null)
                        areas = BlendShapeAnalyzer.ComputeMaxTriangleAreas(re.Skinned, re.WorkingMesh, res.AvatarRoot);
                    else
                    {
                        // Static mesh: compute base areas without blendshapes / 静态网格：计算无blendshape的基础面积
                        areas = ComputeStaticTriangleAreas(re.WorkingMesh, re.Renderer.transform);
                    }
                    // Store per-submesh slices / 存储每个子网格的切片（子网格局部索引）
                    int cursor = 0;
                    for (int s = 0; s < re.WorkingMesh.subMeshCount; s++)
                    {
                        int triCount = (int)re.WorkingMesh.GetIndexCount(s) / 3;
                        var slice = new float[triCount];
                        Array.Copy(areas, cursor, slice, 0, Mathf.Min(triCount, Mathf.Max(0, areas.Length - cursor)));
                        res.MaxTriangleAreas[(re, s)] = slice;
                        cursor += triCount;
                    }
                    meshesProcessed++;
                }
                catch (Exception e)
                {
                    log.LogWarning($"[ATO] Triangle area analysis failed for {re.Renderer.name}: {e.Message} / 三角面面积分析失败：{e.Message}");
                }
            }
            if (meshesProcessed > 0)
                log.LogInfo($"[ATO] Max-area analysis done for {meshesProcessed} meshes / 对{meshesProcessed}个网格完成了最大面积分析");
        }

        /// <summary>
        /// Compute world-space triangle areas for a static (non-skinned) mesh.
        /// 计算静态（非蒙皮）网格的世界空间三角面面积。
        /// </summary>
        private static float[] ComputeStaticTriangleAreas(Mesh mesh, Transform transform)
        {
            Vector3[] verts = mesh.vertices;
            var allTris = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sub = new List<int>();
                mesh.GetTriangles(sub, s);
                allTris.AddRange(sub);
            }
            float scaleMax = MaxAbs3(transform.lossyScale);
            var areas = new float[allTris.Count / 3];
            for (int t = 0; t < allTris.Count / 3; t++)
            {
                int i0 = allTris[t*3], i1 = allTris[t*3+1], i2 = allTris[t*3+2];
                Vector3 a = transform.localToWorldMatrix.MultiplyPoint3x4(verts[i0]) * scaleMax;
                Vector3 b = transform.localToWorldMatrix.MultiplyPoint3x4(verts[i1]) * scaleMax;
                Vector3 c = transform.localToWorldMatrix.MultiplyPoint3x4(verts[i2]) * scaleMax;
                areas[t] = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return areas;
        }

        private static TextureUsageFlags UsageFromKind(TexturePropertyKind k)
        {
            return k switch
            {
                TexturePropertyKind.BaseColor => TextureUsageFlags.BaseColor,
                TexturePropertyKind.Normal => TextureUsageFlags.Normal,
                TexturePropertyKind.Mask => TextureUsageFlags.Mask,
                TexturePropertyKind.Grayscale => TextureUsageFlags.Grayscale,
                _ => TextureUsageFlags.None,
            };
        }

        private static bool IsWhitelistedObject(UnityEngine.Object obj, HashSet<UnityEngine.Object> whitelist)
        {
            if (obj == null) return false;
            if (whitelist.Contains(obj)) return true;
            if (obj is Component c)
            {
                if (whitelist.Contains(c.gameObject)) return true;
                var t = c.transform.parent;
                while (t != null)
                {
                    if (whitelist.Contains(t.gameObject)) return true;
                    t = t.parent;
                }
            }
            return false;
        }

        private static void ScanAnimations(BuildContext context, AvatarTextureOptimizer settings, AvatarAnalysisResult res, ATOLogger log)
        {
            res.Animation = AnimationAnalyzer.Analyze(context, res);
            log.LogInfo($"[ATO] Animation scan: {res.Animation.AnimatedMaterials.Count} material switches, {res.Animation.AnimatedTextures.Count} texture swaps, {res.Animation.AnimatedST.Count} ST animations / 动画扫描：{res.Animation.AnimatedMaterials.Count}个材质切换，{res.Animation.AnimatedTextures.Count}个贴图切换，{res.Animation.AnimatedST.Count}个ST动画");
        }

        private static void ExtractUVIslands(AvatarAnalysisResult res, ATOLogger log)
        {
            // For each renderer/material slot/uvChannel, gather triangles and split into UV islands using
            // edge-adjacency (welding by shared (position, uv) pairs).
            // 对每个渲染器/材质槽/uv通道，收集三角面并通过边邻接拆分为UV岛。
            var sw = Stopwatch.StartNew();

            int totalIslands = 0;
            foreach (var r in res.Renderers)
            {
                var mesh = r.WorkingMesh;
                Vector3[] verts = mesh.vertices;

                // Compute base scale from lossyScale / 从lossyScale计算基础缩放
                var scale = r.Renderer.transform.lossyScale;
                float scaleMax = MaxAbs3(scale);

                for (int matIdx = 0; matIdx < mesh.subMeshCount && matIdx < r.Materials.Length; matIdx++)
                {
                    var matEntry = r.Materials[matIdx];
                    if (matEntry == null || matEntry.Material == null) continue;

                    // Get submesh triangles (these are GLOBAL vertex indices as returned by Mesh.GetTriangles)
                    // 获取子网格三角面（Mesh.GetTriangles返回的是全局顶点索引）
                    var subTris = mesh.GetTriangles(matIdx);
                    if (subTris.Length < 3) continue;

                    // Determine which UV channels are used by this material
                    var channelsUsed = new Dictionary<int, List<(TexturePropertyDescriptor prop, Texture2D tex, TextureUsageFlags usage)>>();
                    foreach (var b in matEntry.TextureBindings)
                    {
                        if (b.uvChannel < 0 || b.uvChannel > 7) continue;
                        if (!channelsUsed.TryGetValue(b.uvChannel, out var list))
                        {
                            list = new List<(TexturePropertyDescriptor, Texture2D, TextureUsageFlags)>();
                            channelsUsed[b.uvChannel] = list;
                        }
                        list.Add((b.prop, b.tex, b.usage));
                    }

                    // Look up precomputed per-triangle max areas (with blendshapes) for this submesh
                    // 查找此子网格预计算的每三角面最大面积（含blendshapes）
                    float[] triMaxAreas = null;
                    res.MaxTriangleAreas.TryGetValue((r, matIdx), out triMaxAreas);

                    foreach (var kv in channelsUsed)
                    {
                        int ch = kv.Key;
                        var bindings = kv.Value;
                        var uvList = new List<Vector2>();
                        mesh.GetUVs(ch, uvList);
                        if (uvList.Count == 0) continue;
                        Vector2[] uvs = uvList.ToArray();

                        var islands = ExtractIslandsForChannel(r, matIdx, ch, uvs, subTris, verts, scaleMax, bindings, res, log, triMaxAreas);
                        res.Islands.AddRange(islands);
                        totalIslands += islands.Count;
                    }
                }
            }

            log.IslandsProcessed = totalIslands;
            log.LogInfo($"[ATO] Extracted {totalIslands} UV islands in {sw.ElapsedMilliseconds} ms / 在{sw.ElapsedMilliseconds}毫秒内提取了{totalIslands}个UV岛");
        }

        private static List<UVIsland> ExtractIslandsForChannel(RendererEntry r, int matIdx, int ch, Vector2[] uvs, int[] tris, Vector3[] verts, float scale,
            List<(TexturePropertyDescriptor prop, Texture2D tex, TextureUsageFlags usage)> bindings, AvatarAnalysisResult res, ATOLogger log, float[] triMaxAreas)
        {
            var islands = new List<UVIsland>();

            // Check for wrap-crossing UVs / 检查跨wrap的UV
            var triPoints = new List<Vector2>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                triPoints.Add(uvs[tris[t]]); triPoints.Add(uvs[tris[t+1]]); triPoints.Add(uvs[tris[t+2]]);
            }
            bool canNormalize = MathUtility.CanNormalizeUVs(triPoints.ToArray(), out var offset);
            bool crossesSeams = false;
            if (canNormalize && (Mathf.Abs(offset.x) > 1e-5f || Mathf.Abs(offset.y) > 1e-5f))
            {
                for (int i = 0; i < uvs.Length; i++) uvs[i] += offset;
                log.LogInfo($"[ATO] Normalized UVs on {r.Renderer.name} ch{ch} by {offset} / 将{r.Renderer.name}通道{ch}的UV归一偏移{offset}");
                // Write back normalized UVs to WorkingMesh / 写回归一化UV到WorkingMesh
                var normList = new List<Vector2>(uvs);
                r.WorkingMesh.SetUVs(ch, normList);
            }
            else if (!canNormalize)
            {
                crossesSeams = true;
                log.LogWarning(ATOLocalization.T("warning.uvWrapCross", r.Renderer.name, ch), r.Renderer);
                foreach (var b in bindings) res.WhitelistedTextures.Add(b.tex);
            }

            // Split into UV islands via edge-adjacency flood fill
            // 通过边邻接洪泛拆分为UV岛
            int triCount = tris.Length / 3;
            var visited = new bool[triCount];

            const float UV_QUANT = 1024f;
            (int, int) QuantizeUV(Vector2 uv) => (Mathf.RoundToInt(uv.x * UV_QUANT), Mathf.RoundToInt(uv.y * UV_QUANT));
            string EdgeKey(Vector2 ua, Vector2 ub)
            {
                var qa = QuantizeUV(ua); var qb = QuantizeUV(ub);
                if (qa.CompareTo(qb) > 0) { var tmp = qa; qa = qb; qb = tmp; }
                return $"{qa.Item1}_{qa.Item2}|{qb.Item1}_{qb.Item2}";
            }

            var edgeMap = new Dictionary<string, List<int>>();
            void AddEdgeUV(int triIdx, Vector2 a, Vector2 b)
            {
                string k = EdgeKey(a, b);
                if (!edgeMap.TryGetValue(k, out var list)) { list = new List<int>(); edgeMap[k] = list; }
                list.Add(triIdx);
            }

            for (int ti = 0; ti < triCount; ti++)
            {
                int i0 = tris[ti*3], i1 = tris[ti*3+1], i2 = tris[ti*3+2];
                AddEdgeUV(ti, uvs[i0], uvs[i1]);
                AddEdgeUV(ti, uvs[i1], uvs[i2]);
                AddEdgeUV(ti, uvs[i2], uvs[i0]);
            }

            for (int seed = 0; seed < triCount; seed++)
            {
                if (visited[seed]) continue;
                var queue = new Queue<int>(); queue.Enqueue(seed); visited[seed] = true;
                var triList = new List<int>();
                var vertSet = new HashSet<int>();
                var subTriIndices = new List<int>(); // indices within subTris (local triangle ids) / subTris内的索引（局部三角面id）

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    triList.Add(cur);
                    subTriIndices.Add(cur);
                    int i0 = tris[cur*3], i1 = tris[cur*3+1], i2 = tris[cur*3+2];
                    vertSet.Add(i0); vertSet.Add(i1); vertSet.Add(i2);

                    void TryNeighbor(Vector2 a, Vector2 b)
                    {
                        string k = EdgeKey(a, b);
                        if (!edgeMap.TryGetValue(k, out var adj)) return;
                        foreach (var nt in adj) { if (!visited[nt]) { visited[nt] = true; queue.Enqueue(nt); } }
                    }
                    TryNeighbor(uvs[i0], uvs[i1]);
                    TryNeighbor(uvs[i1], uvs[i2]);
                    TryNeighbor(uvs[i2], uvs[i0]);
                }

                // Compute BB in UV space / 计算UV空间BB
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var vi in vertSet)
                {
                    if (vi < 0 || vi >= uvs.Length) continue;
                    var uv = uvs[vi];
                    if (uv.x < minX) minX = uv.x;
                    if (uv.y < minY) minY = uv.y;
                    if (uv.x > maxX) maxX = uv.x;
                    if (uv.y > maxY) maxY = uv.y;
                }
                var bb = new Rect(minX, minY, maxX - minX, maxY - minY);

                // Compute world area using blendshape-aware max areas when available
                // 可用时使用考虑blendshape的最大面积计算世界面积
                float worldArea = 0;
                if (triMaxAreas != null)
                {
                    foreach (var ti in subTriIndices)
                        if (ti >= 0 && ti < triMaxAreas.Length)
                            worldArea += triMaxAreas[ti];
                }
                else
                {
                    worldArea = ComputeWorldArea(verts, tris, triList, scale);
                }

                foreach (var b in bindings)
                {
                    bool wl = res.WhitelistedTextures.Contains(b.tex) || crossesSeams;
                    var island = new UVIsland
                    {
                        SourceMesh = r.SharedMesh,
                        RendererEntry = r,
                        Renderer = r.Renderer,
                        UVChannel = ch,
                        MaterialSlot = matIdx,
                        SubmeshIndex = matIdx,
                        Triangles = new List<int>(triList.Count * 3),
                        TriangleLocalIndices = new List<int>(subTriIndices),
                        BoundsUV = bb,
                        WorldArea = worldArea,
                        SourceTexture = b.tex,
                        SourceDescriptor = new TextureDescriptor(b.tex),
                        IsWhitelisted = wl,
                        IsAlpha = (b.usage & TextureUsageFlags.Transparent) != 0 || (b.usage & TextureUsageFlags.HasAlpha) != 0,
                        NeedsNormalRotation = (b.usage & TextureUsageFlags.Normal) != 0,
                        Cutoff = r.Materials[matIdx]?.Cutoff ?? 0.5f,
                    };
                    foreach (var t in triList)
                    {
                        // tris are already global vertex indices from Mesh.GetTriangles(matIdx)
                        // tris已经是Mesh.GetTriangles(matIdx)返回的全局顶点索引
                        island.Triangles.Add(tris[t*3]);
                        island.Triangles.Add(tris[t*3+1]);
                        island.Triangles.Add(tris[t*3+2]);
                    }
                    int tw = b.tex != null ? b.tex.width : 4;
                    int th = b.tex != null ? b.tex.height : 4;
                    island.OriginalPixelSize = new Vector2Int(
                        Mathf.Max(1, Mathf.RoundToInt(bb.width * tw)),
                        Mathf.Max(1, Mathf.RoundToInt(bb.height * th)));
                    islands.Add(island);
                }
            }

            return islands;
        }

        private static float ComputeWorldArea(Vector3[] verts, int[] tris, List<int> triList, float scale)
        {
            float area = 0;
            foreach (var t in triList)
            {
                Vector3 a = verts[tris[t*3]] * scale;
                Vector3 b = verts[tris[t*3+1]] * scale;
                Vector3 c = verts[tris[t*3+2]] * scale;
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }

        private static void BuildGroups(AvatarAnalysisResult res, ATOLogger log)
        {
            // Group islands by UV identity (renderer + uv channel + triangle-set hash).
            // Islands across material slots/tproperties that share the exact same UV islands
            // will be in the same UV group.
            // 按UV身份分组。跨材质槽/属性但共享完全相同UV岛的岛将在同一个UV组中。

            var uvGroupByIslandKey = new Dictionary<(Renderer renderer, int uvChannel, long triHash), UVGroup>();
            int groupId = 0;
            foreach (var island in res.Islands)
            {
                long triHash = TriangleSetHash(island.Triangles);
                var key = (island.Renderer, island.UVChannel, triHash);
                if (!uvGroupByIslandKey.TryGetValue(key, out var grp))
                {
                    grp = new UVGroup { Id = groupId++, SourceBounds = island.BoundsUV };
                    uvGroupByIslandKey[key] = grp;
                    res.UvGroups.Add(grp);
                }
                grp.Islands.Add(island);
                grp.SourceBounds = UnionRect(grp.SourceBounds, island.BoundsUV);
                grp.UsageFlags |= island.IsAlpha ? TextureUsageFlags.HasAlpha | TextureUsageFlags.Transparent : 0;
                if (island.NeedsNormalRotation) grp.UsageFlags |= TextureUsageFlags.Normal;
                if (island.IsWhitelisted) grp.PartiallyWhitelisted = true;
            }

            // Per spec: if ANY island in a UV group is whitelisted, the ENTIRE group skips atlas packing
            // (because same-UV textures must share UV coordinates; if one texture can't be repacked,
            // none of them can). The islands still participate in whole-texture scaling.
            // 按规范：UV组中任一岛白名单 → 整个组跳过图集（但仍参与整图缩放）。
            foreach (var g in res.UvGroups)
            {
                bool anyWL = false;
                bool allWL = true;
                foreach (var i in g.Islands)
                {
                    if (i.IsWhitelisted) anyWL = true;
                    else allWL = false;
                }
                g.PartiallyWhitelisted = anyWL && !allWL;
                g.FullyWhitelisted = anyWL; // any whitelist → skip atlas for whole group
            }

            // Build texture type groups. Per spec: if a texture exists in both normal and non-normal
            // materials, it goes to normal group. So we aggregate flags across all islands of a UV group.
            // 构建贴图类型组。按规范：若贴图同时存在于有法线和无法线材质，则归法线组。
            var tgMap = new Dictionary<TextureTypeGroupKey, TextureTypeGroup>();
            foreach (var g in res.UvGroups)
            {
                bool hasNormal = false, hasAlpha = false, isGrayscale = true;
                FilterMode filter = FilterMode.Bilinear;
                bool sRGB = true;
                bool first = true;
                foreach (var isl in g.Islands)
                {
                    if (isl.NeedsNormalRotation) hasNormal = true;
                    if (isl.IsAlpha) hasAlpha = true;
                    // Grayscale detection: if any island uses non-grayscale usage, group is non-gray
                    // 灰度检测：任一岛使用非灰度用途则组非灰度
                    if ((isl.SourceDescriptor.Filter != FilterMode.Point) && isl.SourceTexture != null)
                    {
                        // grayscale = all R/G/B equal is hard to detect here; base on Kind instead
                    }
                    if (first)
                    {
                        filter = isl.SourceDescriptor.Filter;
                        sRGB = isl.SourceDescriptor.sRGB;
                        first = false;
                    }
                    else
                    {
                        // Use the highest quality filter (Bilinear > Point) / 使用最高质量filter
                        if ((int)isl.SourceDescriptor.Filter > (int)filter) filter = isl.SourceDescriptor.Filter;
                        // If any island is sRGB, group is sRGB / 任一岛sRGB则组sRGB
                        sRGB |= isl.SourceDescriptor.sRGB;
                    }
                }

                // Determine layer usage / 确定层用途
                TextureUsageFlags layerUsage;
                if (hasNormal) layerUsage = TextureUsageFlags.Normal;
                else layerUsage = TextureUsageFlags.BaseColor;
                if (hasAlpha) layerUsage |= TextureUsageFlags.HasAlpha;

                var key = new TextureTypeGroupKey
                {
                    sRGB = sRGB,
                    filterMode = filter,
                    usage = layerUsage,
                    hasAlphaChannel = hasAlpha,
                };
                if (!tgMap.TryGetValue(key, out var tg))
                {
                    tg = new TextureTypeGroup { Key = key, NeedsAlpha = key.hasAlphaChannel };
                    tgMap[key] = tg;
                    res.TypeGroups.Add(tg);
                }
                if (!tg.UvGroups.Contains(g)) tg.UvGroups.Add(g);
                if (!g.TypeGroups.Contains(tg)) g.TypeGroups.Add(tg);
            }

            log.LogInfo($"[ATO] Built {res.UvGroups.Count} UV groups and {res.TypeGroups.Count} texture type groups / 构建了{res.UvGroups.Count}个UV组和{res.TypeGroups.Count}个贴图类型组");
        }

        /// <summary>
        /// Stable 64-bit hash of triangle set (sorted) for identity comparison.
        /// 三角面集合（排序后）的稳定64位哈希用于身份比较。
        /// </summary>
        private static long TriangleSetHash(List<int> tris)
        {
            var sorted = new List<int>(tris);
            sorted.Sort();
            // FNV-1a 64-bit
            const ulong FNV_OFFSET = 14695981039346656037UL;
            const ulong FNV_PRIME = 1099511628211UL;
            ulong hash = FNV_OFFSET;
            hash ^= (ulong)tris.Count;
            hash *= FNV_PRIME;
            // Hash first, sampled, and last to keep cost bounded while distributing well
            int step = Mathf.Max(1, sorted.Count / 64);
            for (int i = 0; i < sorted.Count; i += step)
            {
                hash ^= (ulong)sorted[i];
                hash *= FNV_PRIME;
            }
            hash ^= (ulong)sorted[sorted.Count - 1];
            hash *= FNV_PRIME;
            return unchecked((long)hash);
        }

        private static Rect UnionRect(Rect a, Rect b)
        {
            float xMin = Mathf.Min(a.xMin, b.xMin);
            float yMin = Mathf.Min(a.yMin, b.yMin);
            float xMax = Mathf.Max(a.xMax, b.xMax);
            float yMax = Mathf.Max(a.yMax, b.yMax);
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static float MaxAbs3(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}

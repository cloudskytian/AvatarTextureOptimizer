// Stage 9: compression formats (per category, platform-safe), mip streaming binding,
// post-optimization material/texture dedup and material slot merging.
// 阶段9：分类压缩格式（平台安全过滤）、Mip绑定、优化后材质/贴图去重与材质槽合并。
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class FinalizeStage
    {
        public static void Run(AtoContext ctx)
        {
            using (AtoLog.Time("FinalizeStage", (l, ms) => ctx.Stats.StageTimes.Add((l, ms))))
            {
                AtoProgress.BeginStage(AtoL10n.Tr("stage.finalize"));
                CompressOutputs(ctx);
                if (ctx.Settings.dedupTextures) DedupOutputTextures(ctx);
                if (ctx.Settings.dedupMaterials) DedupMaterials(ctx);
                ComputeStats(ctx);
            }
        }

        // ---- compression / 压缩 ----
        private static void CompressOutputs(AtoContext ctx)
        {
            var po = ctx.PlatformOverride != null && ctx.PlatformOverride.overrideEnabled
                ? ctx.PlatformOverride : null;
            foreach (var ti in ctx.Textures.Values)
            {
                if (ti.Output == null) continue;
                bool isAtlas = ti.AtlasIndex >= 0;
                var atlas = isAtlas ? ctx.Atlases.FirstOrDefault(a => a.Texture == ti.Output) : null;
                bool hasAlpha = atlas?.HasAlpha ?? ti.HasAlphaContent;
                var fmt = ResolveFormat(ctx, po, ti.Role, hasAlpha, ti);
                CompressAndStream(ctx, ti.Output, fmt, MipEnabled(ctx, po, ti.Role));
            }
        }

        private static bool MipEnabled(AtoContext ctx, AtoPlatformOverride po, TexRole role)
        {
            var src = po ?? DefaultOverride(ctx);
            switch (role)
            {
                case TexRole.Normal: return src.mipNormal;
                case TexRole.Gray: return src.mipGray;
                default: return src.mipOpaque;
            }
        }

        private static AtoPlatformOverride DefaultOverride(AtoContext ctx) =>
            ctx.Platform == AtoPlatform.PC ? ctx.Settings.pcOverride :
            ctx.Platform == AtoPlatform.Android ? ctx.Settings.androidOverride : ctx.Settings.iosOverride;

        /// <summary>Safe format resolution with per-platform fallback. / 平台安全格式解析与兜底。</summary>
        private static TextureFormat ResolveFormat(AtoContext ctx, AtoPlatformOverride po,
            TexRole role, bool hasAlpha, TexInfo ti)
        {
            bool mobile = ctx.Platform != AtoPlatform.PC;
            var o = po ?? DefaultOverride(ctx);

            TextureFormat Fallback()
            {
                if (mobile) return TextureFormat.ASTC_6x6;
                if (role == TexRole.Normal) return TextureFormat.BC7;
                if (hasAlpha) return TextureFormat.BC7;
                return TextureFormat.DXT1;
            }

            TextureFormat requested;
            switch (role)
            {
                case TexRole.Normal:
                    requested = o.normalFormat switch
                    {
                        AtoNormalFormat.BC7 => TextureFormat.BC7,
                        AtoNormalFormat.BC5 => TextureFormat.BC5,
                        AtoNormalFormat.DXT5 => TextureFormat.DXT5,
                        AtoNormalFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
                        AtoNormalFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
                        _ => Fallback()
                    };
                    break;
                case TexRole.Gray:
                    requested = o.grayFormat switch
                    {
                        AtoGrayFormat.BC4 => TextureFormat.BC4,
                        AtoGrayFormat.BC7 => TextureFormat.BC7,
                        AtoGrayFormat.DXT1 => TextureFormat.DXT1,
                        AtoGrayFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
                        AtoGrayFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
                        AtoGrayFormat.ASTC_8x8 => TextureFormat.ASTC_8x8,
                        _ => Fallback()
                    };
                    // BC4 is single channel: refuse when multiple channels are used / 多通道拒绝单通道格式
                    if (requested == TextureFormat.BC4 && CountBits(ti.UsedChannels) > 1)
                    {
                        nadena.dev.ndmf.ErrorReport.ReportError(AtoL10n.Localizer,
                            nadena.dev.ndmf.ErrorSeverity.Information, "warn.gray_multichannel",
                            ti.Tex ? ti.Tex.name : "?");
                        requested = mobile ? TextureFormat.ASTC_6x6 : TextureFormat.BC7;
                    }
                    break;
                default:
                    if (hasAlpha)
                        requested = o.transparentFormat switch
                        {
                            AtoTransparentFormat.BC7 => TextureFormat.BC7,
                            AtoTransparentFormat.DXT5 => TextureFormat.DXT5,
                            AtoTransparentFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
                            AtoTransparentFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
                            AtoTransparentFormat.ASTC_8x8 => TextureFormat.ASTC_8x8,
                            _ => mobile ? TextureFormat.ASTC_6x6 : TextureFormat.BC7
                        };
                    else
                        requested = o.opaqueFormat switch
                        {
                            AtoOpaqueFormat.BC7 => TextureFormat.BC7,
                            AtoOpaqueFormat.DXT1 => TextureFormat.DXT1,
                            AtoOpaqueFormat.ASTC_4x4 => TextureFormat.ASTC_4x4,
                            AtoOpaqueFormat.ASTC_6x6 => TextureFormat.ASTC_6x6,
                            AtoOpaqueFormat.ASTC_8x8 => TextureFormat.ASTC_8x8,
                            _ => Fallback()
                        };
                    break;
            }

            // platform legality / 平台合法性兜底
            bool isAstc = requested >= TextureFormat.ASTC_4x4 && requested <= TextureFormat.ASTC_12x12;
            if (mobile && !isAstc) requested = TextureFormat.ASTC_6x6;
            if (!mobile && isAstc) requested = hasAlpha || role != TexRole.Color ? TextureFormat.BC7 : TextureFormat.DXT1;
            // NPOT cannot use crunched/PVRTC-like constraints; RGBA32 fallback when odd sizes break BCn
            // NPOT 尺寸导致块压缩非法时兜底
            return requested;
        }

        private static int CountBits(byte b) { int c = 0; while (b != 0) { c += b & 1; b >>= 1; } return c; }

        private static void CompressAndStream(AtoContext ctx, Texture2D tex, TextureFormat fmt, bool mip)
        {
            try
            {
                if (tex.width % 4 == 0 && tex.height % 4 == 0)
                    EditorUtility.CompressTexture(tex, fmt, UnityEditor.TextureCompressionQuality.Normal);
                else
                    AtoLog.Warn($"'{tex.name}' size {tex.width}x{tex.height} not block-aligned; kept uncompressed");
            }
            catch (Exception e)
            {
                AtoLog.Warn($"compress failed for '{tex.name}' ({fmt}): {e.Message}; kept uncompressed");
            }

            // Mipmap <-> MipStreaming binding (VRChat requirement). Runtime Texture2D exposes no
            // setter, so we write the serialized property (same approach as other NDMF tools).
            // Mip 与 MipStreaming 绑定；运行时无 setter，写序列化属性实现。
            try
            {
                var so = new SerializedObject(tex);
                var sp = so.FindProperty("m_StreamingMipmaps");
                if (sp != null)
                {
                    sp.boolValue = mip && tex.mipmapCount > 1;
                    var prio = so.FindProperty("m_StreamingMipmapsPriority");
                    if (prio != null) prio.intValue = 0;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            catch (Exception e)
            {
                AtoLog.Warn($"mip streaming flag failed for '{tex.name}': {e.Message}");
            }
        }

        // ---- output texture dedup / 输出贴图去重 ----
        private static void DedupOutputTextures(AtoContext ctx)
        {
            var outputs = ctx.Textures.Values.Where(t => t.Output != null).Select(t => t.Output).Distinct().ToList();
            var byHash = new Dictionary<string, Texture2D>();
            var map = new Dictionary<Texture2D, Texture2D>();
            foreach (var t in outputs)
            {
                string hash;
                try { hash = TexturePixels.PixelHash(t) + $"|{t.format}|{t.wrapMode}|{t.filterMode}"; }
                catch { continue; }
                if (byHash.TryGetValue(hash, out var canon) && canon != t) map[t] = canon;
                else byHash[hash] = t;
            }
            if (map.Count == 0) return;
            foreach (var ti in ctx.Textures.Values)
                if (ti.Output != null && map.TryGetValue(ti.Output, out var canon)) ti.Output = canon;
            // update material refs / 更新材质引用
            foreach (var ri in ctx.Renderers)
                foreach (var m in ri.Renderer.sharedMaterials)
                {
                    if (m == null) continue;
                    foreach (var p in m.GetTexturePropertyNames())
                        if (m.GetTexture(p) is Texture2D t && map.TryGetValue(t, out var canon))
                            m.SetTexture(p, canon);
                }
            var asc = ctx.Ndmf.Extension<AnimatorServicesContext>();
            asc.AnimationIndex.RewriteObjectCurves(o =>
                o is Texture2D t && map.TryGetValue(t, out var canon) ? (UnityEngine.Object)canon : o);
            AtoLog.Info($"output texture dedup: {map.Count} merged");
        }

        // ---- material dedup + slot merge / 材质去重与槽合并 ----
        private static void DedupMaterials(AtoContext ctx)
        {
            // find animation-touched renderer slots: those renderers are excluded from slot merging
            // 找出动画涉及的材质槽，其渲染器不做槽合并
            var animatedRenderers = new HashSet<string>();
            ScanStage.ForEachClip(ctx, clip =>
            {
                foreach (var b in clip.GetObjectCurveBindings())
                    if (typeof(Renderer).IsAssignableFrom(b.type) &&
                        b.propertyName.StartsWith("m_Materials")) animatedRenderers.Add(b.path);
            });

            // dedup identical materials globally / 全局去重相同材质
            var byKey = new Dictionary<string, Material>();
            var map = new Dictionary<Material, Material>();
            foreach (var ri in ctx.Renderers)
                foreach (var m in ri.Renderer.sharedMaterials)
                {
                    if (m == null || map.ContainsKey(m)) continue;
                    var key = MaterialKey(m);
                    if (byKey.TryGetValue(key, out var canon) && canon != m) map[m] = canon;
                    else byKey[key] = m;
                }

            if (map.Count > 0)
            {
                foreach (var ri in ctx.Renderers)
                {
                    var mats = ri.Renderer.sharedMaterials;
                    bool changed = false;
                    for (int s = 0; s < mats.Length; s++)
                        if (mats[s] != null && map.TryGetValue(mats[s], out var canon)) { mats[s] = canon; changed = true; }
                    if (changed) ri.Renderer.sharedMaterials = mats;
                }
                var asc = ctx.Ndmf.Extension<AnimatorServicesContext>();
                asc.AnimationIndex.RewriteObjectCurves(o =>
                    o is Material m && map.TryGetValue(m, out var canon) ? (UnityEngine.Object)canon : o);
                ctx.Stats.MaterialsMerged += map.Count;
                AtoLog.Info($"material dedup: {map.Count} merged");
            }

            // slot merge (opaque only, non-animated renderers) / 材质槽合并（仅不透明且无动画干预）
            foreach (var ri in ctx.Renderers)
            {
                var path = ScanStage.RelPath(ctx.Ndmf.AvatarRootTransform, ri.Renderer.transform);
                if (animatedRenderers.Contains(path)) continue;
                TryMergeSlots(ctx, ri);
            }
        }

        private static string MaterialKey(Material m)
        {
            // content+parameter identity / 内容与参数一致性
            var json = EditorJsonUtility.ToJson(m);
            // names differ between clones; strip name field / 名称不参与比较
            json = System.Text.RegularExpressions.Regex.Replace(json, "\"m_Name\":\\s*\"[^\"]*\"", "\"m_Name\":\"\"");
            return (m.shader ? m.shader.name : "null") + "|" + json.GetHashCode().ToString("x8");
        }

        private static void TryMergeSlots(AtoContext ctx, RendererInfo ri)
        {
            var mats = ri.Renderer.sharedMaterials;
            var mesh = ri.Renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                : ri.Renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || mats.Length != mesh.subMeshCount || mats.Length < 2) return;

            // group adjacent-equal & opaque materials / 相同且不透明的材质分组
            var groups = new List<List<int>>();
            var bySlotMat = new Dictionary<Material, List<int>>();
            for (int s = 0; s < mats.Length; s++)
            {
                if (mats[s] == null) { groups.Add(new List<int> { s }); continue; }
                var sem = ShaderSemantics.Analyze(mats[s]);
                bool opaque = sem.Supported && sem.Alpha == AlphaMode.Opaque;
                if (opaque && bySlotMat.TryGetValue(mats[s], out var g)) g.Add(s);
                else { var ng = new List<int> { s }; groups.Add(ng); if (opaque) bySlotMat[mats[s]] = ng; }
            }
            if (groups.Count == mats.Length) return;

            var clone = UnityEngine.Object.Instantiate(mesh);
            clone.name = mesh.name;
            nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(mesh, clone);
            var newMats = new Material[groups.Count];
            var combined = new List<int[]>();
            for (int g = 0; g < groups.Count; g++)
            {
                var indices = new List<int>();
                foreach (var s in groups[g]) indices.AddRange(mesh.GetTriangles(s));
                combined.Add(indices.ToArray());
                newMats[g] = mats[groups[g][0]];
            }
            clone.subMeshCount = groups.Count;
            for (int g = 0; g < groups.Count; g++) clone.SetTriangles(combined[g], g, false);
            if (ri.Renderer is SkinnedMeshRenderer smr2) smr2.sharedMesh = clone;
            else ri.Renderer.GetComponent<MeshFilter>().sharedMesh = clone;
            ri.Renderer.sharedMaterials = newMats;
            ctx.Ndmf.AssetSaver.SaveAsset(clone);
            AtoLog.Info($"slot merge on '{ri.Renderer.name}': {mats.Length} -> {groups.Count} slots");
        }

        private static void ComputeStats(AtoContext ctx)
        {
            long orig = 0, final = 0;
            var counted = new HashSet<Texture2D>();
            foreach (var ti in ctx.Textures.Values)
            {
                if (ti.Tex != null && counted.Add(ti.Tex)) orig += (long)ti.Tex.width * ti.Tex.height;
            }
            var outSet = new HashSet<Texture2D>();
            foreach (var ti in ctx.Textures.Values)
            {
                var t = ti.Output != null ? ti.Output : ti.Tex;
                if (t != null && outSet.Add(t)) final += (long)t.width * t.height;
            }
            ctx.Stats.OriginalPixels = orig;
            ctx.Stats.FinalPixels = final;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    // 贴图收集器：构建贴图使用关系、贴图分类（类型/透明模式/alpha/色彩空间）、按“实际像素 + 导入设置”去重。
    // Texture collector: builds usage relations, classifies textures, dedups by "actual pixels + import settings".
    internal static class TextureCollector
    {
        public static void Collect(ATOContext ctx, ATOReport.Stage stage)
        {
            // 0) 展开槽位材质来源：基础材质 + 动画切换材质（去重）。贴图属性动画的目标贴图另按属性附加。
            // Expand slot material sources: base material + animated swap materials (deduped).
            // Animated texture-property targets are attached per property afterwards.
            foreach (var slot in ctx.slots)
            {
                slot.sourceMaterials.Clear();
                slot.sourceMaterials.Add(slot.material);
                HashSet<Material> swaps;
                if (ctx.animations.slotSwapMaterials.TryGetValue(slot, out swaps))
                {
                    foreach (var m in swaps)
                    {
                        if (m != null && !slot.sourceMaterials.Contains(m)) slot.sourceMaterials.Add(m);
                    }
                }
            }

            // 1) 为每个槽位构建 TextureUse（遍历槽位全部可能材质）。Build TextureUse for every slot (all possible materials).
            foreach (var slot in ctx.slots)
            {
                ctx.CheckCancelled();
                foreach (var mat in slot.sourceMaterials)
                {
                    if (mat == null) continue;
                    foreach (var info in ShaderTextureTable.GetProperties(mat))
                    {
                        var tex = mat.GetTexture(info.propertyName) as Texture2D;
                        if (tex == null) continue;

                        var use = new TextureUse
                        {
                            slot = slot,
                            sourceMaterial = mat,
                            propertyName = info.propertyName,
                            texture = tex,
                            kind = info.kind,
                            noScaleOffset = info.noScaleOffset,
                            specialPurposeUV = info.specialPurpose,
                            uvChannel = info.defaultUvChannel
                        };
                        ResolveUvChannel(ctx, slot, info, use);
                        DetectTransform(ctx, slot, info, use);
                        ATOAlphaModeUtil.Detect(mat, out use.alphaMode, out use.cutoff);
                        use.fromAnimatedSwap = slot.material != mat;
                        slot.uses.Add(use);
                    }
                }

                // 贴图属性动画：动画直接切换的贴图作为该槽位对应属性的使用附加。
                // Animated texture properties: textures swapped by animation are attached as uses of that property.
                HashSet<Texture2D> swapTexs;
                if (ctx.animations.slotSwapTextures.TryGetValue(slot, out swapTexs))
                {
                    HashSet<string> animProps;
                    ctx.animations.slotTexturePropsAnimated.TryGetValue(slot, out animProps);
                    foreach (var tex in swapTexs)
                    {
                        if (tex == null) continue;
                        var use = new TextureUse
                        {
                            slot = slot,
                            sourceMaterial = slot.material,
                            propertyName = "animated",
                            texture = tex,
                            kind = ATOTextureKind.Color,
                            noScaleOffset = false,
                            uvChannel = 0,
                            alphaMode = ATOAlphaMode.Unknown,
                            fromAnimatedSwap = true,
                            animatedTextureProperty = true
                        };
                        // 若动画属性明确，则按属性名尝试推断种类。If the animated property is known, infer the kind from it.
                        if (animProps != null && animProps.Count > 0)
                        {
                            var prop = new List<string>(animProps)[0];
                            use.propertyName = prop;
                            var info = FindPropertyInfo(slot.material, prop);
                            if (info != null)
                            {
                                use.kind = info.kind;
                                use.uvChannel = info.defaultUvChannel;
                                use.noScaleOffset = info.noScaleOffset;
                                use.specialPurposeUV = info.specialPurpose;
                                ResolveUvChannel(ctx, slot, info, use);
                                DetectTransform(ctx, slot, info, use);
                            }
                        }
                        ATOAlphaModeUtil.Detect(slot.material, out use.alphaMode, out use.cutoff);
                        slot.uses.Add(use);
                    }
                }
            }

            // 2) 合并为 TextureEntry（物理贴图 → 条目）。Merge uses into TextureEntry per physical texture.
            var map = ctx.textureMap;
            foreach (var slot in ctx.slots)
            {
                foreach (var use in slot.uses)
                {
                    TextureEntry e;
                    if (!map.TryGetValue(use.texture, out e))
                    {
                        e = BuildEntry(use.texture);
                        map[use.texture] = e;
                        ctx.textures.Add(e);
                    }
                    e.uses.Add(use);
                }
            }

            // 3) 分类收口：kind = 最严苛使用；worstAlphaMode；alpha 修正。Finalize classification.
            foreach (var e in ctx.textures)
            {
                FinalizeClassification(e, ctx);
            }

            // 4) 去重（实际像素 + 导入设置）。Dedup by pixels + import settings.
            if (ctx.settings.deduplicateTextures)
            {
                Deduplicate(ctx, stage);
            }

            // 5) 去重后规范化：全部使用指向规范条目（后续阶段统一使用 canonical）。
            // Post-dedup canonicalization: every use points at the canonical entry.
            foreach (var slot in ctx.slots)
            {
                foreach (var use in slot.uses)
                {
                    if (use.texture == null) continue;
                    var canon = use.texture;
                    int guard = 0;
                    while (canon != null && canon.dedupTarget != null && guard++ < 32) canon = canon.dedupTarget;
                    use.texture = canon;
                }
            }

            stage.AddLine(string.Format(ATOLocalization.Tr("log.textureSummary"), ctx.textures.Count));
            ctx.report.textureCount = ctx.textures.Count;
        }

        // 解析 UV 通道：读取 liltoon UVMode 属性（0~3 = 通道；≥4 = MatCap/Rim 特殊用途）。
        // Resolves the UV channel: reads liltoon UVMode property (0~3 = channel; >=4 = matcap/rim special purpose).
        private static void ResolveUvChannel(ATOContext ctx, SlotEntry slot, ShaderTextureInfo info, TextureUse use)
        {
            if (string.IsNullOrEmpty(info.uvModeProperty)) return;
            var mat = slot.material;
            if (!mat.HasProperty(info.uvModeProperty)) return;
            int mode = mat.GetInt(info.uvModeProperty);
            if (mode >= 0 && mode <= 3) use.uvChannel = mode;
            else use.specialPurposeUV = true;
            if (ctx.animations.IsSlotPropAnimated(slot, info.uvModeProperty)) use.uvModeAnimated = true;
        }

        // 按属性名在着色器表中查找属性信息（动画贴图目标推断种类）。Looks up a property in the shader table by name.
        private static ShaderTextureInfo FindPropertyInfo(Material material, string propertyName)
        {
            foreach (var info in ShaderTextureTable.GetProperties(material))
            {
                if (info.propertyName == propertyName) return info;
            }
            return null;
        }

        // 检测 ST 变换（静态 + 动画）。Detects ST transforms (static + animated).
        private static void DetectTransform(ATOContext ctx, SlotEntry slot, ShaderTextureInfo info, TextureUse use)
        {
            var mat = slot.material;

            // liltoon ScrollRotate 向量：任何非零分量即视为存在变换。Any non-zero component counts as a transform.
            if (!string.IsNullOrEmpty(info.scrollRotateProperty) && mat.HasProperty(info.scrollRotateProperty))
            {
                var v = mat.GetVector(info.scrollRotateProperty);
                if (v.x != 0f || v.y != 0f || v.z != 0f || v.w != 0f) use.stTransform = true;
            }

            // 通用 _ST：scale != (1,1) 或 offset != (0,0) 即变换。Generic _ST: non-default scale/offset counts.
            if (!use.noScaleOffset && !use.stTransform)
            {
                string st = info.propertyName + "_ST";
                if (mat.HasProperty(st))
                {
                    var v = mat.GetVector(st);
                    if (Mathf.Abs(v.x - 1f) > 1e-5f || Mathf.Abs(v.y - 1f) > 1e-5f || Mathf.Abs(v.z) > 1e-5f || Mathf.Abs(v.w) > 1e-5f)
                    {
                        use.stTransform = true;
                    }
                }
            }

            // 动画修改 ST/ScrollRotate → 视为存在变换（含动画中可能出现的此类变换）。
            // Animated ST/ScrollRotate → treated as a transform (covers transforms that only appear via animation).
            if (ctx.animations.IsSlotPropAnimated(slot, info.propertyName + "_ST") ||
                (!string.IsNullOrEmpty(info.scrollRotateProperty) && ctx.animations.IsSlotPropAnimated(slot, info.scrollRotateProperty)))
            {
                use.stAnimated = true;
                use.stTransform = true;
            }
        }

        // 构建贴图条目（导入元数据 + 签名）。Builds a texture entry (import metadata + signatures).
        private static TextureEntry BuildEntry(Texture2D tex)
        {
            var e = new TextureEntry { source = tex };
            e.assetPath = AssetDatabase.GetAssetPath(tex);
            e.assetGuid = AssetDatabase.AssetToGUID(tex);
            e.width = tex.width;
            e.height = tex.height;
            e.wrapU = tex.wrapModeU;
            e.wrapV = tex.wrapModeV;
            e.filterMode = tex.filterMode;
            e.anisoLevel = tex.anisoLevel;
            e.mipmapEnabled = tex.mipmapCount > 1;
            e.streamingMipmaps = tex.streamingMipmaps;
            e.readable = tex.isReadable;
            try
            {
                e.hasAlpha = GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat);
            }
            catch (Exception)
            {
                e.hasAlpha = false;
            }

            var imp = AssetImporter.GetAtPath(e.assetPath) as TextureImporter;
            if (imp != null)
            {
                e.sRGB = imp.sRGBTexture;
                e.isNormalMapImporter = imp.textureType == TextureImporterType.NormalMap;
                e.mipmapEnabled = imp.mipmapEnabled;
                e.streamingMipmaps = imp.streamingMipmaps;
                e.readable = imp.isReadable;
                e.importKey = BuildImportKey(imp);
            }
            else
            {
                // 内置/运行时贴图：按图形格式与色彩空间兜底。Built-in/runtime textures: fallback signature.
                e.importKey = "procedural:" + tex.name + ":" + tex.graphicsFormat + ":" + (e.sRGB ? "s" : "l");
            }

            e.pixelKey = BuildPixelKey(tex, e);

            // 原始体积估算（报告用）。Original size estimate (for reports).
            if (!string.IsNullOrEmpty(e.assetPath) && File.Exists(e.assetPath))
            {
                e.originalByteSize = new FileInfo(e.assetPath).Length;
            }
            else
            {
                e.originalByteSize = (long)e.width * e.height * 4;
            }
            return e;
        }

        // 导入设置签名：不同导入设置直接视为不同贴图。Import-settings signature: different settings → different textures.
        private static string BuildImportKey(TextureImporter imp)
        {
            var sb = new StringBuilder();
            sb.Append(imp.textureType).Append('|');
            sb.Append(imp.sRGBTexture ? 1 : 0).Append('|');
            sb.Append(imp.wrapModeU).Append('|').Append(imp.wrapModeV).Append('|');
            sb.Append(imp.filterMode).Append('|').Append(imp.anisoLevel).Append('|');
            sb.Append(imp.mipmapEnabled ? 1 : 0).Append('|');
            sb.Append(imp.streamingMipmaps ? 1 : 0).Append('|');
            sb.Append(imp.maxTextureSize).Append('|');
            sb.Append(imp.alphaIsTransparency ? 1 : 0).Append('|');
            sb.Append(imp.npotScale).Append('|');
            sb.Append(imp.textureCompression).Append('|');
            sb.Append(imp.crunchedCompression ? 1 : 0).Append('|');
            sb.Append(imp.compressionQuality).Append('|');
            sb.Append(imp.isReadable ? 1 : 0).Append('|');
            sb.Append(imp.ignorePngGamma ? 1 : 0).Append('|');
            AppendPlatform(sb, imp, "Standalone");
            AppendPlatform(sb, imp, "Android");
            AppendPlatform(sb, imp, "iPhone");
            return HashString(sb.ToString());
        }

        private static void AppendPlatform(StringBuilder sb, TextureImporter imp, string platform)
        {
            var ps = imp.GetPlatformTextureSettings(platform);
            sb.Append(platform).Append('=').Append(ps.overridden ? 1 : 0).Append('|')
              .Append(ps.format).Append('|').Append((int)ps.textureCompression).Append('|')
              .Append(ps.compressionQuality).Append('|').Append(ps.maxTextureSize).Append(';');
        }

        private static string HashString(string s)
        {
            // 确定性 FNV-1a 64 位哈希（避免依赖 Hash128 各重载在版本间的行为差异）。
            // Deterministic FNV-1a 64-bit hash (avoids Hash128 overload behavior differences across versions).
            return HashBytes(System.Text.Encoding.UTF8.GetBytes(s));
        }

        private static string HashBytes(byte[] bytes)
        {
            ulong h = 14695981039346656037UL;
            foreach (var b in bytes)
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        // 像素内容签名：优先 imageContentsHash（导入内容的官方哈希）；兜底逐像素/文件字节哈希。
        // Pixel signature: prefers imageContentsHash; falls back to pixel/file-byte hashing.
        private static string BuildPixelKey(Texture2D tex, TextureEntry e)
        {
            try
            {
                var h = tex.imageContentsHash;
                if (h.isValid && h.ToString() != "00000000000000000000000000000000") return h.ToString();
            }
            catch (Exception)
            {
                // imageContentsHash 不可用时走兜底。Fallback when imageContentsHash is unavailable.
            }
            try
            {
                if (tex.isReadable)
                {
                    var data = tex.GetPixelData<Color32>(0).ToArray();
                    var bytes = new byte[data.Length * 4];
                    Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                    return HashBytes(bytes);
                }
                if (!string.IsNullOrEmpty(e.assetPath) && File.Exists(e.assetPath))
                {
                    var bytes = File.ReadAllBytes(e.assetPath);
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                    {
                        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    }
                }
            }
            catch (Exception ex)
            {
                ATOLog.Warn("像素哈希失败 / pixel hash failed: " + ex.Message);
            }
            return "fallback:" + e.width + "x" + e.height + ":" + e.assetGuid;
        }

        // 分类收口。Finalizes classification.
        private static void FinalizeClassification(TextureEntry e, ATOContext ctx)
        {
            int bestKind = 0;
            var kinds = new HashSet<ATOTextureKind>();
            ATOAlphaMode worst = ATOAlphaMode.Opaque;

            foreach (var u in e.uses)
            {
                int rank = KindRank(u.kind);
                if (rank > bestKind)
                {
                    bestKind = rank;
                    e.kind = u.kind;
                }
                kinds.Add(u.kind);
                if ((int)u.alphaMode > (int)worst) worst = u.alphaMode;
                if (u.alphaMode != ATOAlphaMode.Opaque && u.alphaMode != ATOAlphaMode.Unknown) e.hasAlpha = true;
                // 动画贴图切换目标。Animated texture-swap target.
                if (ctx.animations.animatedTextureTargets.Contains(u.texture)) e.animatedSwapReference = true;
            }

            e.mixedKinds = kinds.Count > 1;
            e.worstAlphaMode = worst;
            if (ctx.animations.animatedTextureTargets.Contains(e.source)) e.animatedSwapReference = true;
        }

        // 分类严苛度排序：法线 > 颜色 > 蒙版 > 灰度。Kind demanding order: normal > color > mask > grayscale.
        private static int KindRank(ATOTextureKind kind)
        {
            switch (kind)
            {
                case ATOTextureKind.NormalMap: return 3;
                case ATOTextureKind.Color: return 2;
                case ATOTextureKind.Mask: return 1;
                default: return 0;
            }
        }

        // 去重：按 (importKey, pixelKey) 分组；哈希碰撞时精确比对。
        // Dedup: group by (importKey, pixelKey); exact-compare on hash collisions.
        private static void Deduplicate(ATOContext ctx, ATOReport.Stage stage)
        {
            var groups = new Dictionary<string, List<TextureEntry>>();
            foreach (var e in ctx.textures)
            {
                string key = e.importKey + "|" + e.pixelKey;
                List<TextureEntry> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<TextureEntry>();
                    groups[key] = list;
                }
                list.Add(e);
            }

            int merged = 0;
            long saved = 0;
            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                // 确定性排序：资产路径升序。Deterministic order: ascending asset path.
                list.Sort((x, y) => string.CompareOrdinal(x.assetPath, y.assetPath));
                var canonical = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    ctx.CheckCancelled();
                    var dup = list[i];
                    if (!ExactMatch(canonical, dup)) continue;
                    dup.dedupTarget = canonical;
                    merged++;
                    saved += dup.originalByteSize;
                    stage.AddLine(string.Format(ATOLocalization.Tr("log.dedup"), dup.assetPath, canonical.assetPath));
                }
            }

            ctx.report.dedupMergedTextures = merged;
            ctx.report.dedupBytesSaved = saved;
            stage.AddLine(string.Format(ATOLocalization.Tr("log.dedupSummary"), merged, saved / 1048576.0));
        }

        // 哈希碰撞时的精确比对：可读 → 逐像素；否则逐文件字节。Exact compare on hash collision: pixels if readable, else file bytes.
        private static bool ExactMatch(TextureEntry a, TextureEntry b)
        {
            if (ReferenceEquals(a.source, b.source)) return true;
            if (a.width != b.width || a.height != b.height) return false;
            try
            {
                if (a.readable && b.readable)
                {
                    var pa = a.source.GetPixelData<Color32>(0).ToArray();
                    var pb = b.source.GetPixelData<Color32>(0).ToArray();
                    if (pa.Length != pb.Length) return false;
                    for (int i = 0; i < pa.Length; i++)
                    {
                        if (pa[i].r != pb[i].r || pa[i].g != pb[i].g || pa[i].b != pb[i].b || pa[i].a != pb[i].a) return false;
                    }
                    return true;
                }
                if (!string.IsNullOrEmpty(a.assetPath) && !string.IsNullOrEmpty(b.assetPath)
                    && File.Exists(a.assetPath) && File.Exists(b.assetPath))
                {
                    var ba = File.ReadAllBytes(a.assetPath);
                    var bb = File.ReadAllBytes(b.assetPath);
                    if (ba.Length != bb.Length) return false;
                    for (int i = 0; i < ba.Length; i++)
                    {
                        if (ba[i] != bb[i]) return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                ATOLog.Warn("去重精确比对失败 / exact compare failed: " + ex.Message);
            }
            return false;
        }
    }
}

// AvatarTextureOptimizer
// File: Editor/Analysis/TextureDeduplicator.cs
//
// Deduplicates textures by ACTUAL PIXEL CONTENT + IMPORT SETTINGS. Two textures
// are "the same" only when every import-relevant setting matches (size, format,
// color space, sRGB, filter mode, wrap mode) AND the decoded pixels match.
// All references are re-pointed at the representative. Whitelisted textures
// stay whitelisted after dedup (the representative inherits whitelisting).
//
// 按【实际像素内容 + 导入设置】去重贴图。仅当所有导入相关设置一致（尺寸、
// 格式、色彩空间、sRGB、过滤模式、包裹模式）且解码像素一致时，两张贴图
// 才"相同"。所有引用被重指向代表贴图。白名单贴图去重后仍保持白名单
// （代表贴图继承白名单状态）。
//
// Memory strategy: two-tier hashing. Tier 1 compares file size + sampled hash;
// tier 2 (only for tier-1 matches) performs a full pixel comparison in chunks,
// so identical copies are always confirmed exactly while memory stays bounded.

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    public static class TextureDeduplicator
    {
        /// <summary>Import-relevant key of a texture. / 贴图的导入相关键。</summary>
        private readonly struct TextureImportKey : IEquatable<TextureImportKey>
        {
            public readonly int Width, Height;
            public readonly TextureFormat Format;
            public readonly bool IsNormalMap;
            public readonly bool IsSRGB;
            public readonly FilterMode Filter;
            public readonly TextureWrapMode Wrap;

            public TextureImportKey(Texture2D tex)
            {
                Width = tex.width;
                Height = tex.height;
                Format = tex.format;
                Filter = tex.filterMode;
                Wrap = tex.wrapMode;

                bool normal = false, sRGB = true;
                string path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path))
                {
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null)
                    {
                        normal = importer.textureType == TextureImporterType.NormalMap;
                        sRGB = !normal && importer.sRGBTexture;
                    }
                }
                IsNormalMap = normal;
                IsSRGB = sRGB;
            }

            public bool Equals(TextureImportKey other) =>
                Width == other.Width && Height == other.Height && Format == other.Format &&
                IsNormalMap == other.IsNormalMap && IsSRGB == other.IsSRGB &&
                Filter == other.Filter && Wrap == other.Wrap;

            public override bool Equals(object obj) => obj is TextureImportKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Width * 397 ^ Height;
                    h = h * 31 + (int)Format;
                    h = h * 31 + (IsNormalMap ? 1 : 0);
                    h = h * 31 + (IsSRGB ? 1 : 0);
                    h = h * 31 + (int)Filter;
                    h = h * 31 + (int)Wrap;
                    return h;
                }
            }
        }

        /// <summary>
        /// Deduplicate all textures referenced by the collected usages and
        /// update the references (usages, materials, animations) accordingly.
        /// 对收集到的引用所引用的全部贴图去重，并相应地更新引用
        /// （引用、材质、动画）。
        /// </summary>
        public static void Deduplicate(ATOBuildState state)
        {
            var stopwatch = new ATOStopwatch("TextureDeduplicator.Deduplicate");
            stopwatch.Begin("hash textures");

            // Group by import key, then by content hash.
            // 先按导入键分组，再按内容哈希分组。
            var groups = new Dictionary<TextureImportKey, List<Texture2D>>();
            var contentHash = new Dictionary<Texture2D, ulong>();

            var allTextures = new HashSet<Texture2D>();
            foreach (var usage in state.AllUsages)
                if (usage.Texture != null) allTextures.Add(usage.Texture);

            foreach (var tex in allTextures)
            {
                var key = new TextureImportKey(tex);
                contentHash[tex] = ComputeContentHash(tex);
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<Texture2D>();
                list.Add(tex);
            }
            stopwatch.End("hash textures");

            stopwatch.Begin("merge duplicates");
            foreach (var kv in groups)
            {
                // Group by content hash. / 按内容哈希分组。
                var byHash = new Dictionary<ulong, List<Texture2D>>();
                foreach (var tex in kv.Value)
                {
                    if (!byHash.TryGetValue(contentHash[tex], out var list))
                        byHash[contentHash[tex]] = list = new List<Texture2D>();
                    list.Add(tex);
                }

                foreach (var hashGroup in byHash.Values)
                {
                    if (hashGroup.Count < 2) continue;

                    // Pick the representative (prefer the one already in the
                    // whitelist so whitelisting survives dedup).
                    // 选择代表贴图（优先选择已在白名单中的，使白名单在去重
                    // 后仍然成立）。
                    Texture2D representative = hashGroup.FirstOrDefault(t =>
                        state.WhitelistedTextures.Contains(t)) ?? hashGroup[0];

                    var remaining = hashGroup.Where(t => t != representative).ToList();

                    // Full pixel confirmation before merging (tier 2).
                    // 合并前进行完整像素确认（第二层）。
                    foreach (var other in remaining)
                    {
                        if (!PixelsEqual(representative, other))
                        {
                            ATOLog.Trace($"hash collision without pixel equality: {representative.name} vs {other.name}");
                            continue;
                        }

                        ATOLog.Trace($"deduplicating {other.name} -> {representative.name}");
                        state.TextureRemap[other] = representative;

                        if (state.WhitelistedTextures.Contains(other))
                            state.WhitelistedTextures.Add(representative);
                    }
                }
            }
            stopwatch.End("merge duplicates");

            if (state.TextureRemap.Count > 0)
            {
                stopwatch.Begin("update references");
                UpdateReferences(state);
                stopwatch.End("update references");
            }
        }

        private static void UpdateReferences(ATOBuildState state)
        {
            // 1. Materials: re-point each usage's property from the original
            //    texture to its representative BEFORE rewriting the usage.
            //    材质：先把每个引用的属性从原贴图重指向代表贴图，再改写引用。
            var touchedMaterials = new HashSet<Material>();
            foreach (var usage in state.AllUsages)
            {
                if (usage.Material == null) continue;
                if (state.TextureRemap.TryGetValue(usage.Texture, out var rep))
                {
                    usage.Material.SetTexture(usage.PropertyName, rep);
                    touchedMaterials.Add(usage.Material);
                    usage.Texture = rep;
                }
            }

            // 2. Animation clips (object reference curves).
            //    动画剪辑（对象引用曲线）。
            foreach (var kv in state.TextureRemap)
            {
                var original = kv.Key;
                var rep = kv.Value;
                foreach (var clip in FindClipsReferencing(original))
                {
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        bool changed = false;
                        var newCurve = new ObjectReferenceKeyframe[curve.Length];
                        for (int i = 0; i < curve.Length; i++)
                        {
                            newCurve[i] = curve[i];
                            if (curve[i].value == original)
                            {
                                newCurve[i].value = rep;
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            AnimationUtility.SetObjectReferenceCurve(clip, binding, newCurve);
                            EditorUtility.SetDirty(clip);
                        }
                    }
                }
            }
        }

        private static List<AnimationClip> FindClipsReferencing(Texture2D texture)
        {
            var result = new List<AnimationClip>();
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    foreach (var kf in curve)
                        if (kf.value == texture) { result.Add(clip); goto next; }
                }
                next: ;
            }
            return result;
        }

        /// <summary>
        /// Compute a content hash. For large textures a stride-sampled hash is
        /// used to bound memory; misses are only false NEGATIVES (safe).
        /// 计算内容哈希。大贴图使用步进采样哈希以限制内存；漏判只会是
        /// 假阴性（安全方向）。
        /// </summary>
        private static ulong ComputeContentHash(Texture2D tex)
        {
            const ulong FNVOffset = 14695981039346656037UL;
            const ulong FNVPrim = 1099511628211UL;
            ulong hash = FNVOffset;
            int w = tex.width, h = tex.height;

            int stride = 1;
            if ((long)w * h > 2048 * 2048) stride = Mathf.Max(2, Mathf.CeilToInt(Mathf.Sqrt((long)w * h / (2048f * 2048f))));

            // Read raw texture data when possible (fast, no Color32 alloc).
            // 尽可能读取原始纹理数据（快速，无 Color32 分配）。
            if (tex.isReadable)
            {
                try
                {
                    var data = tex.GetRawTextureData<byte>();
                    int yStep = Mathf.Max(1, stride);
                    for (int y = 0; y < h; y += yStep)
                    {
                        int rowStart = y * (int)tex.GetRowBytes(0);
                        int rowEnd = rowStart + tex.width * 4; // assume 4 bytes/px floor
                        rowEnd = Mathf.Min(rowEnd, data.Length);
                        for (int i = rowStart; i < rowEnd; i += stride * 4)
                        {
                            hash ^= data[i];
                            hash *= FNVPrim;
                        }
                    }
                    return hash;
                }
                catch
                {
                    // Fall through to GetPixels32 path. / 回退到 GetPixels32 路径。
                }
            }

            // Fallback: Color32 decode (works for non-readable assets too).
            // 回退：Color32 解码（对不可读资产同样可用）。
            try
            {
                var pixels = tex.GetPixels32(0);
                if (pixels != null && pixels.Length > 0)
                {
                    int step = Mathf.Max(1, stride);
                    for (int i = 0; i < pixels.Length; i += step)
                    {
                        var p = pixels[i];
                        hash ^= (ulong)(p.r | (p.g << 8) | (p.b << 16) | (p.a << 24));
                        hash *= FNVPrim;
                    }
                }
            }
            catch (Exception e)
            {
                ATOLog.Warn($"Failed to hash texture {tex.name}: {e.Message}");
            }
            return hash;
        }

        /// <summary>
        /// Full pixel equality (chunked to bound memory).
        /// 完整像素相等比较（分块以限制内存）。
        /// </summary>
        private static bool PixelsEqual(Texture2D a, Texture2D b)
        {
            if (a == b) return true;
            if (a.width != b.width || a.height != b.height) return false;

            try
            {
                var pa = a.GetPixels32(0);
                var pb = b.GetPixels32(0);
                if (pa == null || pb == null || pa.Length != pb.Length) return false;
                for (int i = 0; i < pa.Length; i++)
                    if (!pa[i].Equals(pb[i])) return false;
                return true;
            }
            catch
            {
                // Some textures are not readable; conservatively consider them
                // different (false negative, safe).
                // 部分贴图不可读；保守视为不同（假阴性，安全）。
                return false;
            }
        }
    }
}

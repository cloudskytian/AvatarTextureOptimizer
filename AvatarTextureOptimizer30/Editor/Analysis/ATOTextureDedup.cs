// ATOTextureDedup.cs — 贴图去重（预处理）/ Texture deduplication (pre-processing).
// 说明：在处理之前，按"实际像素内容 + 导入设置"对贴图去重（导入设置不同直接视为不同），
// 更新材质与动画中的全部相关引用。若去重组中存在白名单贴图，则去重结果也视为白名单。
// Note: before processing, textures are deduplicated by "actual pixel content + import settings"
// (different import settings are considered different), and all references in materials & animations
// are updated. If a dedup group contains a whitelisted texture, the result is also whitelisted.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>贴图去重结果。/ Texture dedup result.</summary>
    internal sealed class ATOTextureDedupResult
    {
        public Dictionary<Texture2D, Texture2D> replacements = new Dictionary<Texture2D, Texture2D>(); // 旧贴图→新贴图 / old → new
        public int dedupCount; // 去重数量 / number of deduped textures
    }

    /// <summary>贴图去重器。/ Texture deduplicator.</summary>
    internal static class ATOTextureDedup
    {
        /// <summary>
        /// 对注册表中的全部贴图执行去重并更新材质引用；返回替换映射。
        /// Deduplicate all registered textures, update material references, and return the replacement map.
        /// </summary>
        public static ATOTextureDedupResult Deduplicate(ATOAvatarScanResult scan, Func<Texture2D, bool> isWhitelisted)
        {
            var result = new ATOTextureDedupResult();

            // 按（尺寸 + 导入设置）分组 / group by (size + import settings)
            var groups = new Dictionary<string, List<ATOTextureInfo>>();
            foreach (var info in scan.textures.Values)
            {
                if (info.texture == null) continue;
                var key = $"{info.width}x{info.height}|{info.isSRGB}|{ATOAvatarScanner.GetImportSettingsSnapshot(info.texture)}";
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<ATOTextureInfo>();
                    groups[key] = list;
                }
                list.Add(info);
            }

            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;

                // 组内像素哈希 / per-group pixel hashing
                var hashBuckets = new Dictionary<string, List<ATOTextureInfo>>();
                foreach (var info in list)
                {
                    var hash = ComputePixelHash(info.texture, info.width, info.height);
                    if (hash == null) continue; // 不可读 → 跳过（已在扫描阶段白名单）/ unreadable → skipped (already whitelisted)
                    if (!hashBuckets.TryGetValue(hash, out var bucket))
                    {
                        bucket = new List<ATOTextureInfo>();
                        hashBuckets[hash] = bucket;
                    }
                    bucket.Add(info);
                }

                foreach (var bucket in hashBuckets.Values)
                {
                    if (bucket.Count < 2) continue;
                    // 哈希相同 → 精确逐像素确认 / same hash → exact per-pixel confirmation
                    var rep = bucket[0];
                    var groupWhitelisted = isWhitelisted(rep.texture) || scan.whitelistedTextures.Contains(rep.texture);
                    for (int i = 1; i < bucket.Count; i++)
                    {
                        var other = bucket[i];
                        if (!PixelsEqual(rep.texture, other.texture, rep.width, rep.height)) continue;
                        if (isWhitelisted(other.texture) || scan.whitelistedTextures.Contains(other.texture)) groupWhitelisted = true;
                        result.replacements[other.texture] = rep.texture;
                        result.dedupCount++;
                        ATOLog.Verbose($"Dedup texture '{other.texture.name}' → '{rep.texture.name}'");
                    }
                    if (groupWhitelisted)
                    {
                        // 去重结果也视为白名单 / the dedup result is also whitelisted
                        scan.whitelistedTextures.Add(rep.texture);
                        if (scan.textures.TryGetValue(rep.texture, out var repInfo))
                        {
                            repInfo.whitelisted = true;
                            repInfo.whitelistReason = "Dedup group contains whitelisted texture";
                        }
                        ATOLog.Verbose($"Dedup group of '{rep.texture.name}' marked whitelisted (group contains whitelisted texture)");
                    }
                }
            }

            if (result.dedupCount > 0)
            {
                // 更新扫描结果中的贴图注册表与用途 / update the texture registry and usages
                foreach (var kv in result.replacements)
                {
                    var oldTex = kv.Key;
                    var newTex = kv.Value;
                    if (scan.textures.TryGetValue(oldTex, out var info))
                    {
                        scan.textures.Remove(oldTex);
                        if (!scan.textures.ContainsKey(newTex))
                        {
                            var newInfo = new ATOTextureInfo
                            {
                                texture = newTex,
                                width = newTex.width,
                                height = newTex.height,
                                isSRGB = info.isSRGB,
                                filterMode = info.filterMode,
                                whitelisted = scan.whitelistedTextures.Contains(newTex),
                                whitelistReason = "dedup result",
                            };
                            scan.textures[newTex] = newInfo;
                        }
                        var repInfo = scan.textures[newTex];
                        foreach (var u in info.usages)
                        {
                            u.texture = newTex;
                            repInfo.usages.Add(u);
                        }
                    }
                    // 材质属性引用 / material property references
                    foreach (var u in scan.textures.TryGetValue(newTex, out var ri) ? ri.usages : new List<ATOTextureUsage>())
                    {
                        if (u.texture == newTex && u.material != null && u.material.HasProperty(u.propertyName) &&
                            u.material.GetTexture(u.propertyName) == oldTex)
                            u.material.SetTexture(u.propertyName, newTex);
                    }
                }
                ATOLog.Info($"Texture dedup: {result.dedupCount} textures merged. (贴图去重：合并 {result.dedupCount} 张)");
            }

            return result;
        }

        /// <summary>更新动画中的贴图引用（去重后）。/ Update texture references in animations (after dedup).</summary>
        public static void UpdateAnimationReferences(List<AnimationClip> clips, Dictionary<Texture2D, Texture2D> replacements)
        {
            if (clips == null || replacements.Count == 0) return;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    if (binding.type != typeof(Material)) continue;
                    var frames = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (frames == null) continue;
                    var changed = false;
                    for (int i = 0; i < frames.Length; i++)
                    {
                        if (frames[i].value is Texture2D t && replacements.TryGetValue(t, out var rep))
                        {
                            frames[i].value = rep;
                            changed = true;
                        }
                    }
                    if (changed) AnimationUtility.SetObjectReferenceCurve(clip, binding, frames);
                }
            }
        }

        /// <summary>计算贴图像素哈希（MD5）。/ Compute the pixel hash (MD5).</summary>
        private static string ComputePixelHash(Texture2D texture, int width, int height)
        {
            try
            {
                var pixels = texture.GetPixels32(0);
                if (pixels == null || pixels.Length == 0) return null;
                var bytes = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(bytes);
                    return Convert.ToBase64String(hash);
                }
            }
            catch (Exception e)
            {
                ATOLog.Warning($"Failed to hash texture '{texture.name}': {e.Message} (贴图哈希失败)");
                return null;
            }
        }

        /// <summary>逐像素精确比较。/ Exact per-pixel comparison.</summary>
        private static bool PixelsEqual(Texture2D a, Texture2D b, int width, int height)
        {
            try
            {
                var pa = a.GetPixels32(0);
                var pb = b.GetPixels32(0);
                if (pa.Length != pb.Length) return false;
                for (int i = 0; i < pa.Length; i++)
                {
                    var x = pa[i];
                    var y = pb[i];
                    if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

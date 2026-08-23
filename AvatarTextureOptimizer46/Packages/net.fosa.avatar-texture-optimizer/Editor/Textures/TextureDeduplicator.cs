// SPDX-License-Identifier: MIT
// EN: Content based texture deduplication.
// ZH: 基于内容的贴图去重。

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Textures
{
    /// <summary>
    /// EN: Groups textures whose actual pixels AND import settings match, and reports one canonical
    ///     representative per group. If any member of a group is whitelisted the representative is too,
    ///     which is what the specification demands.
    /// ZH: 将实际像素与导入设置都相同的贴图归为一组，并为每组给出一个规范代表。
    ///     若组内任一成员在白名单中，代表也视为白名单——这正是规格的要求。
    /// </summary>
    public sealed class TextureDeduplicator
    {
        private const string Stage = "Dedupe";

        /// <summary>EN: Maps every input texture to its canonical representative. ZH: 将每张输入贴图映射到其规范代表。</summary>
        public readonly Dictionary<Texture2D, Texture2D> Canonical = new Dictionary<Texture2D, Texture2D>();

        /// <summary>EN: Representatives that must be treated as whitelisted. ZH: 必须按白名单处理的代表。</summary>
        public readonly HashSet<Texture2D> WhitelistedCanonicals = new HashSet<Texture2D>();

        /// <summary>
        /// EN: Runs deduplication.
        /// ZH: 执行去重。
        /// </summary>
        /// <param name="textures">EN: Candidate textures. ZH: 候选贴图。</param>
        /// <param name="isWhitelisted">EN: Predicate telling whether a texture is protected. ZH: 判断贴图是否受保护的谓词。</param>
        /// <returns>EN: Number of textures eliminated. ZH: 被消除的贴图数量。</returns>
        public int Run(IEnumerable<Texture2D> textures, Func<Texture2D, bool> isWhitelisted)
        {
            var buckets = new Dictionary<string, List<Texture2D>>(StringComparer.Ordinal);

            foreach (var tex in textures)
            {
                if (tex == null) continue;
                if (Canonical.ContainsKey(tex)) continue;

                string key;
                try
                {
                    key = TextureProbe.ImportSignature(tex) + "|" + ContentHash(tex);
                }
                catch (Exception e)
                {
                    AtoLog.Warning(Stage, $"hashing '{tex.name}' failed ({e.Message}); it will not be deduplicated.");
                    Canonical[tex] = tex;
                    continue;
                }

                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Texture2D>();
                list.Add(tex);
            }

            int removed = 0;
            foreach (var kv in buckets)
            {
                var list = kv.Value;
                // EN: Deterministic representative so repeated builds produce identical output.
                // ZH: 使用确定性的代表，保证重复构建产出完全一致。
                list.Sort((a, b) => string.CompareOrdinal(a.name + a.GetInstanceID(), b.name + b.GetInstanceID()));
                var rep = list[0];
                bool anyWhitelisted = false;
                foreach (var t in list)
                {
                    Canonical[t] = rep;
                    if (isWhitelisted(t)) anyWhitelisted = true;
                }
                if (anyWhitelisted) WhitelistedCanonicals.Add(rep);
                removed += list.Count - 1;
                if (list.Count > 1)
                    AtoLog.Debug_(Stage, $"merged {list.Count} identical textures into '{rep.name}' ({rep.width}x{rep.height})");
            }

            AtoLog.Info(Stage, $"texture dedupe eliminated {removed} duplicate textures.");
            return removed;
        }

        /// <summary>
        /// EN: Hashes the decoded linear pixels. Decoding through the GPU means the source does not need
        ///     to be marked readable and compressed formats are compared after decompression, which is
        ///     what "identical actual pixels" means for the user.
        /// ZH: 对解码后的线性像素求哈希。通过 GPU 解码意味着源贴图无需勾选 Read/Write，
        ///     且压缩格式在解压后比较——这正是用户所理解的“实际像素相同”。
        /// </summary>
        private static string ContentHash(Texture2D tex)
        {
            var rt = GpuTextureUtil.ToLinearRT(tex);
            LinearImage img = null;
            try
            {
                img = GpuTextureUtil.Readback(rt, new RectInt(0, 0, rt.width, rt.height));
                using var sha = SHA256.Create();
                var bytes = new byte[img.Pixels.Length * 16];
                unsafe
                {
                    fixed (byte* dst = bytes)
                    {
                        var src = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                            .GetUnsafeReadOnlyPtr(img.Pixels);
                        Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(dst, src, bytes.Length);
                    }
                }
                var hash = sha.ComputeHash(bytes);
                return $"{tex.width}x{tex.height}:{Convert.ToBase64String(hash)}";
            }
            finally
            {
                img?.Dispose();
                GpuTextureUtil.Release(rt);
            }
        }
    }
}

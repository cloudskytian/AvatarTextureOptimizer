// AvatarTextureOptimizer - ContentDeduper
// EN: Deduplicates materials (content+params) and generated textures, then merges identical material slots when
// animations never switch one of them individually (with animation binding remap).
// CN: 对材质（内容+参数）与生成贴图去重，并在动画不单独切换任一槽位时合并相同材质槽（含动画绑定重映射）。
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class ContentDeduper
    {
        /// <summary>
        /// EN: Pre-write dedup for generated textures: identical content+category+usage collapse into one asset
        /// (import settings differ per category, so the key includes them). Reads in-memory pixels, which are
        /// always readable — post-import textures have Read/Write disabled by design.
        /// CN: 生成贴图的写入前去重：内容+类别+用途相同则合并为同一资产（导入设置按类别不同，故键含类别）。
        ///     读取内存态像素（始终可读）——导入后的贴图按设计关闭了 Read/Write。
        /// </summary>
        public sealed class GeneratedTextureSession
        {
            private readonly Dictionary<string, Texture2D> _byKey = new Dictionary<string, Texture2D>();
            private readonly Dictionary<Texture2D, Texture2D> _assetByMem = new Dictionary<Texture2D, Texture2D>();

            public Texture2D Resolve(Texture2D mem, TextureCategory cat, TextureUsage usage, bool srgb,
                out bool isNew)
            {
                string key = $"{(int)cat}|{(int)usage}|{(srgb ? 1 : 0)}|{ComputePixelHash(mem)}";
                if (_byKey.TryGetValue(key, out var existing))
                {
                    isNew = false;
                    return existing;
                }
                _byKey[key] = mem;
                isNew = true;
                return mem;
            }

            public void RegisterAsset(Texture2D mem, Texture2D asset) => _assetByMem[mem] = asset;

            public bool TryGetAsset(Texture2D mem, out Texture2D asset) => _assetByMem.TryGetValue(mem, out asset);

            public Texture2D AssetFor(Texture2D mem) => _assetByMem.TryGetValue(mem, out var a) ? a : mem;
        }

        private static string ComputePixelHash(Texture2D tex)
        {
            var data = tex.GetRawTextureData<Color32>();
            uint h = 2166136261u;
            int step = Mathf.Max(1, data.Length / 8192);
            for (int i = 0; i < data.Length; i += step)
            {
                Color32 c = data[i];
                h ^= (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                h *= 16777619u;
            }
            return $"{tex.width}x{tex.height}|{tex.mipmapCount}|{h:x8}";
        }

        /// <summary>
        /// EN: Dedups materials in place and merges identical slots when safe.
        /// CN: 就地材质去重，并在安全时合并相同槽位。
        /// </summary>
        public static void DedupMaterials(AtoBuildState state, AnimationData anim,
            out Dictionary<(Renderer, int), int> slotRemap)
        {
            slotRemap = new Dictionary<(Renderer, int), int>();

            // EN: Group renderer slots by identical material content.
            // CN: 按相同材质内容分组渲染器槽位。
            var seen = new Dictionary<Material, Material>();
            foreach (var renderer in state.Renderers)
            {
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    Material canonical = null;
                    foreach (var kv in seen)
                    {
                        if (kv.Key != m && MaterialEditor.AreMaterialsEqual(kv.Key, m))
                        {
                            canonical = kv.Value;
                            break;
                        }
                    }
                    if (canonical != null)
                    {
                        mats[i] = canonical;
                        changed = true;
                    }
                    else if (!seen.ContainsKey(m))
                    {
                        seen[m] = m;
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }

            // EN: Slot merging (only when animations never switch any merged slot individually).
            // CN: 槽位合并（仅当动画从不单独切换任一被合并槽位时）。
            foreach (var renderer in state.Renderers)
            {
                var mats = new List<Material>(renderer.sharedMaterials);
                if (mats.Count <= 1) continue;
                bool[] merged = new bool[mats.Count];
                bool anyMerge = false;
                for (int i = 0; i < mats.Count; i++)
                {
                    if (merged[i] || mats[i] == null) continue;
                    for (int j = i + 1; j < mats.Count; j++)
                    {
                        if (merged[j] || mats[j] == null) continue;
                        if (mats[j] != mats[i]) continue;
                        if (anim != null &&
                            (anim.individuallyAnimatedSlots.Contains((renderer, i)) ||
                             anim.individuallyAnimatedSlots.Contains((renderer, j))))
                            continue;
                        merged[j] = true;
                        slotRemap[(renderer, j)] = i;
                        anyMerge = true;
                        AtoLog.Detail($"Slot merge: {renderer.name} slot {j} -> {i}");
                    }
                }
                if (anyMerge)
                {
                    var newMats = new List<Material>();
                    for (int i = 0; i < mats.Count; i++)
                        if (!merged[i]) newMats.Add(mats[i]);
                    renderer.sharedMaterials = newMats.ToArray();
                }
            }
        }
    }
}

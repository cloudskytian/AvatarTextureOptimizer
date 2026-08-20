// TextureDeduplicator.cs
// Phase 2: Deduplicates textures by actual pixel content AND import settings.
// If any duplicate is whitelisted, the dedup result is also whitelisted.
// 阶段2：按实际像素和导入设置去重贴图。若去重涉及白名单则结果也视为白名单。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEditor;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Deduplicates textures by content hash (actual pixels) and import settings.
    /// Updates all material references to point to the canonical texture.
    /// 按内容哈希和导入设置去重贴图，更新所有引用。
    /// </summary>
    internal sealed class TextureDeduplicator
    {
        private readonly AvatarScanResult _scan;
        private readonly ATOLogger _log;

        internal TextureDeduplicator(AvatarScanResult scan, ATOLogger log)
        {
            _scan = scan;
            _log = log;
        }

        internal int Execute()
        {
            // Group textures by (importHash + contentHash)
            var groups = new Dictionary<string, List<Texture2D>>();
            int processed = 0;

            foreach (var tex in _scan.TextureReferences.Keys.ToList())
            {
                if (tex == null) continue;
                if (_scan.WhitelistedTextures.Contains(tex)) continue;

                string key = ComputeDedupKey(tex);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<Texture2D>();
                    groups[key] = list;
                }
                list.Add(tex);
                processed++;
            }

            int dedupCount = 0;

            foreach (var kvp in groups)
            {
                if (kvp.Value.Count <= 1) continue;

                // Pick the first as canonical (prefer persistent assets)
                var canonical = kvp.Value.FirstOrDefault(t => AssetDatabase.Contains(t)) ?? kvp.Value[0];
                _log.Verbose($"Dedup group ({kvp.Value.Count} textures): canonical={canonical.name}");

                foreach (var dup in kvp.Value)
                {
                    if (dup == canonical) continue;

                    _scan.DedupMapping[dup] = canonical;
                    dedupCount++;

                    // If the duplicate is referenced in scan, update the reference
                    if (_scan.TextureReferences.TryGetValue(dup, out var refr))
                    {
                        _scan.TextureReferences.Remove(dup);
                        if (!_scan.TextureReferences.ContainsKey(canonical))
                        {
                            refr.Texture = canonical;
                            _scan.TextureReferences[canonical] = refr;
                        }
                    }

                    // Check whitelist: if canonical is whitelisted, whitelist the dup too
                    if (_scan.WhitelistedTextures.Contains(canonical))
                    {
                        _scan.WhitelistedTextures.Add(dup);
                    }
                }
            }

            // Update material references on all renderers
            UpdateMaterialReferences();

            return dedupCount;
        }

        private void UpdateMaterialReferences()
        {
            foreach (var renderer in _scan.Renderers)
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;

                    // Clone material if it's shared/persistent
                    if (AssetDatabase.Contains(mat))
                    {
                        mat = new Material(mat);
                        materials[i] = mat;
                        changed = true;
                    }

                    // Replace textures
                    int count = ShaderUtil.GetPropertyCount(mat.shader);
                    for (int p = 0; p < count; p++)
                    {
                        if (ShaderUtil.GetPropertyType(mat.shader, p) == ShaderUtil.ShaderPropertyType.TexEnv)
                        {
                            var name = ShaderUtil.GetPropertyName(mat.shader, p);
                            var tex = mat.GetTexture(name);
                            if (tex is Texture2D t2d && _scan.DedupMapping.TryGetValue(t2d, out var canonical))
                            {
                                mat.SetTexture(name, canonical);
                            }
                        }
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        private string ComputeDedupKey(Texture2D tex)
        {
            // Include import settings
            var importKey = AvatarScanner.ComputeImportHash(tex);

            // Include readable pixel hash if available
            string pixelHash = "";
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    pixelHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                }
                catch { }
            }

            // If readable, compute content hash
            if (tex.isReadable)
            {
                try
                {
                    var data = tex.GetRawTextureData();
                    if (data != null && data.Length > 0)
                    {
                        using var md5 = MD5.Create();
                        var hash = md5.ComputeHash(data.ToArray());
                        pixelHash = BitConverter.ToString(hash).Replace("-", "");
                    }
                }
                catch { }
            }

            return $"{importKey}|{pixelHash}";
        }
    }
}

// WholeTextureScaler.cs
// When atlas generation is disabled, scales whole textures individually
// (no UV remapping, no unused-UV trimming, no UV reordering).
// 当禁用图集生成时，单独缩放整张贴图。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Scales whole textures to the target quality without generating atlases.
    /// 不生成图集时单独缩放整张贴图。
    /// </summary>
    internal sealed class WholeTextureScaler
    {
        private readonly List<TextureTypeGroup> _typeGroups;
        private readonly BuildContext _context;
        private readonly AdvancedSettings _settings;
        private readonly ATOLogger _log;

        internal WholeTextureScaler(List<TextureTypeGroup> typeGroups, BuildContext context,
            AdvancedSettings settings, ATOLogger log)
        {
            _typeGroups = typeGroups;
            _context = context;
            _settings = settings;
            _log = log;
        }

        internal void Execute()
        {
            foreach (var tg in _typeGroups)
            {
                foreach (var tex in tg.PrimaryTextures)
                {
                    if (tex == null) continue;
                    ScaleTexture(tex, tg);
                }
            }
        }

        private void ScaleTexture(Texture2D source, TextureTypeGroup tg)
        {
            // Determine the quality-scaled size from the UV group
            int targetW = source.width;
            int targetH = source.height;

            // Use the maximum target dimension from UV groups in this type group
            foreach (var ug in tg.UVGroups)
            {
                if (ug.TargetDimension > 0 && ug.TargetDimension < Mathf.Max(targetW, targetH))
                {
                    float scale = (float)ug.TargetDimension / Mathf.Max(source.width, source.height);
                    targetW = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
                    targetH = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
                }
            }

            // If no scaling needed, skip
            if (targetW >= source.width && targetH >= source.height) return;
            if (!source.isReadable) return;

            try
            {
                // Use TextureScale for bilinear downscale
                var scaledTex = new Texture2D(targetW, targetH, source.format, source.mipmapCount > 1);
                scaledTex.name = "ATO_" + source.name;

                // Render texture-based scaling
                var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
                var prevRT = RenderTexture.active;
                RenderTexture.active = rt;

                var tempTex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                tempTex.SetPixels32(source.GetPixels32());
                tempTex.Apply();

                Graphics.Blit(tempTex, rt);
                scaledTex.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                scaledTex.Apply();

                RenderTexture.active = prevRT;
                RenderTexture.ReleaseTemporary(rt);

                // Update material references
                UpdateTextureReferences(source, scaledTex);
                try { _context.AssetSaver.SaveAsset(scaledTex); } catch { }

                _log.Verbose($"Scaled texture {source.name}: {source.width}×{source.height} → {targetW}×{targetH}");
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to scale texture {source.name}: {ex.Message}");
            }
        }

        private void UpdateTextureReferences(Texture2D oldTex, Texture2D newTex)
        {
            var renderers = _context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null) continue;
                    if (AssetDatabase.Contains(mat))
                    {
                        mat = new Material(mat);
                        materials[i] = mat;
                        changed = true;
                    }
                    int count = ShaderUtil.GetPropertyCount(mat.shader);
                    for (int p = 0; p < count; p++)
                    {
                        if (ShaderUtil.GetPropertyType(mat.shader, p) == ShaderUtil.ShaderPropertyType.TexEnv)
                        {
                            var name = ShaderUtil.GetPropertyName(mat.shader, p);
                            if (mat.GetTexture(name) == oldTex)
                                mat.SetTexture(name, newTex);
                        }
                    }
                }
                if (changed)
                    renderer.sharedMaterials = materials;
            }
        }
    }
}

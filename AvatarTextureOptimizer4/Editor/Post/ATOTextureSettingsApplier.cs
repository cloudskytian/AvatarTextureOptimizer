// Avatar Texture Optimizer (ATO)
// Post-processing: persist generated atlases, apply compression/mip-streaming/read-write/clamp
// via TextureImporter (best-effort PNG re-import), and configure remaining non-whitelisted
// textures' Mip Streaming.
// 后处理：持久化生成的图集，尽力通过 TextureImporter（PNG 重导入）应用压缩/mip 流式/禁读写/Clamp，
// 并配置其余非白名单贴图的 Mip Streaming。

using System.IO;
using UnityEditor;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 8: texture settings. / 阶段 8：贴图设置。
    /// </summary>
    public static class ATOTextureSettingsApplier
    {
        public static void Apply(ATOBuildContext build, ATOProgress progress)
        {
            progress.Begin(build.atlases.Count + build.textures.Count);

            foreach (var atlas in build.atlases)
            {
                ApplyAtlas(build, atlas);
                progress.Advance(1, atlas.name);
                progress.ThrowIfCancelled();
            }

            // Mip Streaming for remaining non-whitelisted textures (original imported assets).
            // 其余非白名单贴图（原始导入资产）的 Mip Streaming。
            foreach (var tr in build.textures)
            {
                ApplySourceTexture(build, tr);
                progress.Advance(1);
            }
        }

        private static void ApplyAtlas(ATOBuildContext build, ATOAtlas atlas)
        {
            var tex = atlas.texture;
            if (tex == null) return;

            // In-memory configuration. / 内存内配置。
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            try { build.ndmf.AssetSaver.SaveAsset(tex); } catch (System.Exception) { }

            // Best-effort PNG re-import for compression + streaming. / 尽力 PNG 重导入以应用压缩 + 流式。
            Texture2D loaded = TryReimport(build, tex, atlas.category, atlas.hasAlpha, atlas.width, atlas.height);
            if (loaded != null)
            {
                atlas.texture = loaded;
                foreach (var (mat, prop) in atlas.references)
                    if (mat != null && mat.HasProperty(prop))
                        mat.SetTexture(prop, loaded);
                // Update remap for animation rewriting. / 更新重映射供动画改写。
                var keys = new System.Collections.Generic.List<Texture>();
                foreach (var kvp in build.animRemap.textureRemap)
                    if (kvp.Value == tex) keys.Add(kvp.Key);
                foreach (var k in keys) build.animRemap.textureRemap[k] = loaded;
                ATOLogger.Debug($"Atlas '{atlas.name}' re-imported with compression settings.");
            }
        }

        private static Texture2D TryReimport(ATOBuildContext build, Texture2D tex, ATOTextureCategory category,
            bool hasAlpha, int width, int height)
        {
            try
            {
                var container = build.ndmf.AssetContainer;
                var containerPath = container != null ? AssetDatabase.GetAssetPath(container) : null;
                if (string.IsNullOrEmpty(containerPath))
                {
                    ATOLogger.Debug("No asset container path; skipping texture re-import. / 无资产容器路径，跳过贴图重导入。");
                    return null;
                }
                var dir = Path.GetDirectoryName(containerPath);
                if (string.IsNullOrEmpty(dir)) return null;

                var path = Path.Combine(dir, tex.name + ".png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) return null;
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = category != ATOTextureCategory.NormalMap && category != ATOTextureCategory.Mask && category != ATOTextureCategory.Grayscale;
                imp.mipmapEnabled = true;
                imp.streamingMipmaps = true; // VRChat requires streaming with mipmaps / VRChat 要求 mipmap 与流式绑定
                imp.crunchedCompression = false;
                imp.textureCompression = TextureImporterCompression.Compressed;
                imp.maxTextureSize = Mathf.Clamp(Mathf.Max(width, height), ATOConstants.MinAtlasSize, 8192);
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.isReadable = false;
                imp.npotScale = TextureImporterNPOTScale.None;
                imp.alphaIsTransparency = hasAlpha;
                var choice = ATOAtlasFormat.ChoiceFor(build, category, hasAlpha);
                var format = ATOAtlasFormat.ToImporterFormat(build, choice, hasAlpha);

                // Safety fallback: multi-channel grayscale/mask atlases must not be saved as
                // single-channel formats even if the user picked one. / 安全兜底：多通道灰度/遮罩图集
                // 即使用户选了单通道格式，也应以多通道保存并告警。
                if ((category == ATOTextureCategory.Grayscale || category == ATOTextureCategory.Mask)
                    && choice.format == ATOCompressionFormat.BC4 && IsMultiChannel(tex))
                {
                    format = TextureImporterFormat.BC7;
                    build.report.warnings.Add($"Grayscale/mask atlas '{atlas.name}' has multi-channel content; saved as BC7 instead of single-channel. / 灰度/遮罩图集 '{atlas.name}' 含多通道内容，已改用 BC7（多通道）保存。");
                    ATOLogger.Warn(build.report.warnings[build.report.warnings.Count - 1]);
                }

                imp.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    overridden = true,
                    format = format,
                    textureCompression = TextureImporterCompression.Compressed,
                    maxTextureSize = imp.maxTextureSize,
                });
                imp.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            catch (System.Exception e)
            {
                ATOLogger.Warn($"Texture re-import failed for '{tex.name}': {e.Message}");
                return null;
            }
        }

        private static void ApplySourceTexture(ATOBuildContext build, ATOTextureRef tr)
        {
            if (tr.skipAllOptimization || tr.texture == null) return;
            if (build.animRemap.textureRemap.ContainsKey(tr.texture)) return; // replaced by atlas / 已被图集替换
            var path = AssetDatabase.GetAssetPath(tr.texture);
            if (string.IsNullOrEmpty(path)) return;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;
            var choice = ATOAtlasFormat.ChoiceFor(build, tr);
            if (imp.mipmapEnabled != choice.mipStreaming || imp.streamingMipmaps != choice.mipStreaming)
            {
                imp.mipmapEnabled = choice.mipStreaming;
                imp.streamingMipmaps = choice.mipStreaming;
                imp.SaveAndReimport();
            }
        }

        /// <summary>True when the texture's RGB channels carry distinct data. / 贴图 RGB 通道携带不同数据时返回真。</summary>
        private static bool IsMultiChannel(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;
            var px = tex.GetPixels32();
            int step = Mathf.Max(1, px.Length / 4096);
            for (int i = 0; i < px.Length; i += step)
            {
                var c = px[i];
                if (Mathf.Abs(c.r - c.g) > 2 || Mathf.Abs(c.g - c.b) > 2 || Mathf.Abs(c.r - c.b) > 2)
                    return true;
            }
            return false;
        }
    }
}

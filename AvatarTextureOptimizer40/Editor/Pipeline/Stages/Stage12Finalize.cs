using System.Collections.Generic;
using Fosa.Ato.Editor.i18n;
using Fosa.Ato.Editor.Util;
using Fosa.Ato.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 12: Finalize. Apply safe per-platform compression / mip streaming / import settings to
    /// generated atlases, validate user choices (e.g. no alpha-less format when atlas has alpha;
    /// single-channel request with multi-channel content => save multi-channel + warning), then
    /// deduplicate identical materials and textures/atlases (when toggled on) and update references.
    /// 阶段 12：最终化。为生成的图集应用安全的平台压缩/MipStreaming/导入设置并校验用户选项（有 alpha 不
    /// 给无 alpha 格式；单通道请求但内容多通道则按多通道保存并警告）；按开关对相同材质与贴图/图集去重
    /// 并更新引用。
    /// </summary>
    internal sealed class Stage12Finalize : IStage
    {
        public string Name => "ATO/12 Finalizing & dedup";
        public float Weight => 2f;

        public void Run(AtoPipeline p)
        {
            foreach (var atlas in p.Atlases)
            {
                p.Progress.ThrowIfCancelled();
                ApplyPlatformSettings(p, atlas);
            }

            if (p.Settings.DeduplicateMaterials) DedupMaterials(p);
            if (p.Settings.DeduplicateTextures) DedupTextures(p);

            p.Progress.Stage(Name, 1f);
        }

        private static void ApplyPlatformSettings(AtoPipeline p, AtlasResult atlas)
        {
            if (atlas.Texture == null) return;
            var path = AssetDatabase.GetAssetPath(atlas.Texture);
            if (string.IsNullOrEmpty(path)) return;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;

            var platform = p.CurrentPlatform;
            bool mip = p.Settings.DefaultMipStreaming;
            var cls = atlas.Kind switch
            {
                TextureKind.Normal => p.Settings.Normal,
                TextureKind.Mask or TextureKind.Data => p.Settings.Grayscale,
                TextureKind.Emission => p.Settings.Opaque,
                _ => AnySourceAlpha(atlas) ? p.Settings.Transparent : p.Settings.Opaque,
            };

            // Platform override takes precedence / 平台覆盖优先
            var ov = p.Settings.GetOverride(platform);
            if (ov is { Enabled: true })
            {
                mip = atlas.Kind switch
                {
                    TextureKind.Normal => ov.Normal.MipmapAndStreaming,
                    TextureKind.Mask or TextureKind.Data => ov.Grayscale.MipmapAndStreaming,
                    _ => AnySourceAlpha(atlas) ? ov.Transparent.MipmapAndStreaming : ov.Opaque.MipmapAndStreaming,
                };
            }

            imp.isReadable = false;
            imp.mipmapEnabled = mip;
            imp.streamingMipmaps = mip; // VRChat: bound together / 与 mipmap 绑定
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.anisoLevel = StrictestAniso(atlas);

            // Format override / 格式覆盖
            var ps = new TextureImporterPlatformSettings
            {
                name = p.CurrentPlatform switch { AtoPlatform.Android => "Android", AtoPlatform.iOS => "iPhone", _ => "Standalone" },
                overridden = true,
                textureCompression = TextureImporterCompression.CompressedHQ,
                compressionQuality = cls.CompressionQuality * 50,
                crunchedCompression = cls.Crunch && SupportsCrunch(p.CurrentPlatform),
                format = SafeFormat(p, atlas, platform),
            };
            imp.SetPlatformTextureSettings(ps);

            // Safety: if the atlas has alpha but user picked an alpha-less format, TextureImporter
            // will ignore; we still warn. / 安全：有 alpha 却选无 alpha 格式时警告
            if (AnySourceAlpha(atlas) && IsAlphaLessFormat(ps.format))
                AtoLog.Warn(Localizer.T("warn.formatFallback", atlas.Name, ps.format));

            // Grayscale single-channel vs actual multi-channel / 灰度单通道与多通道兜底
            if (atlas.Kind is TextureKind.Mask or TextureKind.Data && ps.format == TextureImporterFormat.Alpha8 &&
                !IsActuallySingleChannel(atlas.Texture))
            {
                ps.format = TextureImporterFormat.RGBA32;
                imp.SetPlatformTextureSettings(ps);
                AtoLog.Warn(Localizer.T("warn.singleChannelMismatch"));
            }

            imp.SaveAndReimport();
            atlas.OutputBytes = TextureIO.EstimateBytes(atlas.Width, atlas.Height, atlas.Texture.format, mip);
        }

        private static bool SupportsCrunch(AtoPlatform p) => p == AtoPlatform.PC || p == AtoPlatform.Android;

        private static TextureImporterFormat SafeFormat(AtoPipeline p, AtlasResult atlas, AtoPlatform platform)
        {
            bool hasAlpha = AnySourceAlpha(atlas);
            if (platform == AtoPlatform.Android)
                return hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6;
            if (platform == AtoPlatform.iOS)
                return hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6; // NPOT strips PVRTC
            // PC
            return atlas.Kind switch
            {
                TextureKind.Normal => TextureImporterFormat.BC5,
                TextureKind.Mask or TextureKind.Data => TextureImporterFormat.BC4,
                _ => hasAlpha ? TextureImporterFormat.BC7 : TextureImporterFormat.DXT1,
            };
        }

        private static bool IsAlphaLessFormat(TextureImporterFormat f) =>
            f is TextureImporterFormat.DXT1 or TextureImporterFormat.BC4 or TextureImporterFormat.Alpha8;

        private static bool IsActuallySingleChannel(Texture2D t)
        {
            // Heuristic: check a sample of pixels for r==g==b. / 启发式：采样判断 r==g==b
            var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(t, rt);
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tmp = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
                RenderTexture.active = prev;
                var px = tmp.GetPixels32();
                int step = Mathf.Max(1, px.Length / 2048);
                for (int i = 0; i < px.Length; i += step)
                {
                    var c = px[i];
                    if (Mathf.Abs(c.r - c.g) > 2 || Mathf.Abs(c.g - c.b) > 2) { Object.DestroyImmediate(tmp); return false; }
                }
                Object.DestroyImmediate(tmp);
                return true;
            }
            finally { RenderTexture.ReleaseTemporary(rt); }
        }

        private static int StrictestAniso(AtlasResult a)
        {
            int max = 1;
            foreach (var pl in a.Placements)
                if (pl.Island.SourceTexture != null)
                    max = Mathf.Max(max, pl.Island.SourceTexture.anisoLevel);
            return max;
        }

        private static bool AnySourceAlpha(AtlasResult a)
        {
            foreach (var pl in a.Placements)
                if (pl.Island.SourceUsage is { HasAlphaChannel: true }) return true;
            return false;
        }

        private static void DedupMaterials(AtoPipeline p)
        {
            var root = p.Ctx.AvatarRootObject;
            var map = new Dictionary<Material, Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                p.Progress.ThrowIfCancelled();
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (!map.TryGetValue(m, out var canonical))
                    {
                        foreach (var kv in map)
                        {
                            if (MaterialsEqual(kv.Key, m)) { canonical = kv.Key; break; }
                        }
                        if (canonical == null) { map[m] = m; continue; }
                    }
                    if (canonical != m) { mats[i] = canonical; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        private static bool MaterialsEqual(Material a, Material b)
        {
            if (a == null || b == null || a.shader != b.shader) return false;
            if (a.renderQueue != b.renderQueue) return false;
            var pn = a.GetTexturePropertyNames();
            foreach (var n in pn) if (a.GetTexture(n) != b.GetTexture(n)) return false;
            var spn = a.shader.GetPropertyCount();
            for (int i = 0; i < spn; i++)
            {
                if (a.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Float &&
                    a.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Range) continue;
                string n = a.shader.GetPropertyName(i);
                if (!Mathf.Approximately(a.GetFloat(n), b.GetFloat(n))) return false;
            }
            return true;
        }

        private static void DedupTextures(AtoPipeline p)
        {
            // Atlases are named uniquely; we dedup by content hash post-import.
            var byHash = new Dictionary<Hash128, Texture2D>();
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var a in p.Atlases)
            {
                if (a.Texture == null) continue;
                var path = AssetDatabase.GetAssetPath(a.Texture);
                if (string.IsNullOrEmpty(path)) continue;
                var hash = AssetDatabase.GetAssetDependencyHash(path);
                if (byHash.TryGetValue(hash, out var canon)) remap[a.Texture] = canon;
                else byHash[hash] = a.Texture;
            }
            if (remap.Count == 0) return;
            foreach (var r in p.Ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    foreach (var pn in m.GetTexturePropertyNames())
                    {
                        if (m.GetTexture(pn) is Texture2D t && remap.TryGetValue(t, out var to))
                        { m.SetTexture(pn, to); changed = true; }
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }
            AtoLog.VIf(p.Settings.VerboseLogging, $"Deduplicated {remap.Count} generated atlas texture(s).");
        }
    }
}

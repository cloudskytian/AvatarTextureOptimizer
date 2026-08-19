// Stage5b_WholeTexture — whole-texture path (no atlas): convergent uniform-ish downscale / 整图路径（不出图集）：收敛重缩放
// Handles textures that never entered an atlas plane: every texture when atlasing is off, plus
// whitelist-group / abandoned-group textures when atlasing is on. Whitelisted textures themselves are
// never touched (whitelist = skip ALL optimization). Per-axis scale comes from Stage3's
// wholeTextureScale (max over usage islands, never upscales). Resample runs in linear light with
// premultiplied alpha for Albedo (alpha = transparency there); Normal planes get renormalized.<br>
// 处理所有未进图集平面的贴图：图集关闭时的全部贴图，以及图集开启时白名单组/放弃组的贴图。
// 白名单贴图本身绝不动（白名单=跳过全部优化）。各轴缩放取 Stage3 的 wholeTextureScale（按使用岛取最大、不放大）。
// 重采样在线性空间：主色预乘 alpha（其 alpha 即透明度语义），法线重归一化。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class Stage5b_WholeTexture
    {
        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            // ---- collect whole-texture candidates / 收集整图处理候选 ----
            var todo = new List<TextureInfo>();
            foreach (var info in pipe.textures)
            {
                if (info.whitelisted) continue;                       // whitelist: skip ALL optimization / 白名单：跳过一切优化
                if (pipe.settings.generateAtlas && InAnyAtlas(pipe, info)) continue; // already baked into an atlas / 已入图集
                todo.Add(info);
            }
            if (todo.Count == 0)
            {
                ATOLog.V("whole-texture path: nothing to do");
                return;
            }
            Stage5_Bake.EnsureFolder(Stage5_Bake.TempDir);

            int done = 0, rescaled = 0;
            foreach (var info in todo)
            {
                done++;
                pipe.CancelCheck(progress, ATOL10n.T("ato.stage.wholescale"), (float)done / todo.Count);
                try
                {
                    if (ProcessOne(pipe, info)) rescaled++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    var msg = ATOL10n.T("ato.warn.whole_failed", info.source != null ? info.source.name : "?", e.Message);
                    ATOLog.Warn(msg); pipe.warnings.Add(msg);
                }
            }
            ATOLog.Info(ATOL10n.T("ato.log.wholescale_done", rescaled, todo.Count));
            ATOEvents.Raise("wholescale", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("wholescale", pipe);
        }

        private static bool InAnyAtlas(ATOPipeContext pipe, TextureInfo info)
        {
            foreach (var k in pipe.atlasPlaneOf.Keys)
                if (ReferenceEquals(k.Item1, info)) return true;
            return false;
        }

        // ---------------------------------------------------------------- one texture
        /// <summary>Rescale one texture by its recorded usage scale. True when a replacement was produced. / 按记录缩放单张贴图。</summary>
        private static bool ProcessOne(ATOPipeContext pipe, TextureInfo info)
        {
            var tex = info.source;
            if (tex == null) return false;
            var s = pipe.wholeTextureScale.TryGetValue(info, out var got) ? got : Vector2.one;
            s.x = Mathf.Min(s.x, 1f); s.y = Mathf.Min(s.y, 1f);           // never upscale / 绝不放大
            int dw = Mathf.Clamp(Mathf.RoundToInt(info.width * s.x), 1, info.width);
            int dh = Mathf.Clamp(Mathf.RoundToInt(info.height * s.y), 1, info.height);
            if (dw == info.width && dh == info.height) return false;      // scale ≈ 1: keep original / 缩放≈1：保留原图

            var lin = ImageCache.GetLinear(tex, info.sRGB, out int tw, out int th);
            if (lin == null || tw <= 0 || th <= 0) return false;

            bool isAlbedo = info.classes.Contains(TexClass.Albedo);
            bool isNormal = info.classes.Contains(TexClass.Normal) || info.isNormalMap;

            var orig = new NativeArray<float>(lin, Allocator.TempJob);
            NativeArray<float> small = default;
            try
            {
                if (isAlbedo) // alpha == transparency here → premultiply before filtering / alpha 即透明度：先预乘再滤波
                    new QualityJobs.PremultiplyJob { buf = orig, unpremultiply = false }.Schedule(tw * th, 64).Complete();
                small = QualityJobs.Resample(orig, tw, th, dw, dh, Allocator.TempJob);
                if (isNormal)
                    new QualityJobs.RenormalizeJob { buf = small }.Schedule(dw * dh, 64).Complete();
                if (isAlbedo)
                    new QualityJobs.PremultiplyJob { buf = small, unpremultiply = true }.Schedule(dw * dh, 64).Complete();
            }
            finally
            {
                orig.Dispose();
            }

            // back to stored byte domain (sRGB re-encode for sRGB textures) / 回到存储域（sRGB 重新编码）
            var colors = new Color[dw * dh];
            bool srgb = info.sRGB;
            for (int i = 0; i < dw * dh; i++)
            {
                int o = i * 4;
                float r = small[o], g = small[o + 1], b = small[o + 2], a = small[o + 3];
                if (srgb) { r = LinearToSrgb(r); g = LinearToSrgb(g); b = LinearToSrgb(b); }
                colors[i] = new Color(Sat(r), Sat(g), Sat(b), Sat(a));
            }
            small.Dispose();

            var nt = new Texture2D(dw, dh, TextureFormat.RGBA32, mipChain: false, linear: !srgb)
            {
                name = "ATO_" + tex.name,
            };
            nt.SetPixels(colors);
            nt.Apply(false, false);
            byte[] png = nt.EncodeToPNG();
            Object.DestroyImmediate(nt);

            string path = $"{Stage5_Bake.TempDir}/ATO_Whole_{Sanitize(tex.name)}_{dw}x{dh}_{Mathf.Abs(tex.GetInstanceID()) & 0xFFFF:X4}.png";
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            ConfigureWholeImporter(pipe, path, info, dw, dh);
            var outTex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (outTex == null) { ATOLog.Error("failed to reimport " + path); return false; }

            pipe.wholeTexReplacement[info] = outTex;
            ATOLog.V($"whole {tex.name}: {tw}x{th} → {dw}x{dh} (scale {s.x:F2},{s.y:F2})");
            return true;
        }

        // ---------------------------------------------------------------- importer
        /// <summary>Whole textures keep original wrap/filter/sRGB; mips follow the bound per-class switch. / 整图保持原 wrap/filter/sRGB；Mipmap 跟随分类绑定开关。</summary>
        private static void ConfigureWholeImporter(ATOPipeContext pipe, string path, TextureInfo info, int dw, int dh)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter imp)) return;
            var cls = info.classes.Contains(TexClass.Albedo) ? TexClass.Albedo
                : info.classes.Contains(TexClass.Normal) || info.isNormalMap ? TexClass.Normal : TexClass.Mask;
            imp.textureType = cls == TexClass.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = info.sRGB;
            imp.alphaIsTransparency = info.alphaIsTransparency;
            imp.wrapMode = info.wrapMode;                     // whole textures keep original wrap / 整图保留原 wrap
            imp.filterMode = info.filterMode;                 // keep original filter / 保留原过滤
            bool mips = pipe.settings.mips == null || (cls switch
            {
                TexClass.Albedo => pipe.settings.mips.albedo,
                TexClass.Normal => pipe.settings.mips.normal,
                _ => pipe.settings.mips.mask,
            });
            imp.mipmapEnabled = mips;                         // VRC rule: mips ⇔ streaming bound / Mipmap与流送绑定
            imp.streamingMipmaps = mips;
            imp.isReadable = false;
            imp.maxTextureSize = Mathf.Max(32, Mathf.NextPowerOfTwo(Mathf.Max(dw, dh)));
            imp.textureCompression = TextureImporterCompression.Compressed;
            imp.SaveAndReimport();
        }

        private static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
        private static float Sat(float v) => Mathf.Clamp01(v);

        private static string Sanitize(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }
    }
}

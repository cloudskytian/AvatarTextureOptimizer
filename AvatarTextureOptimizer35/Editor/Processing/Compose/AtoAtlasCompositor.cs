using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Atlas composition: resamples each island's content from its source texture into the atlas
    /// (premultiplied resampling; normal maps decoded→resampled→renormalized→re-encoded;
    /// near-lossless copies raw pixels), rotates content consistently with the packing rotation,
    /// then fills the empty space via pull-push dilation (alpha stays 0 for transparent atlases). /
    /// 图集合成：把每个岛的内容从其来源贴图重采样进图集（预乘重采样；法线 解码→重采样→重归一化→编码；
    /// 近无损直接拷贝原始像素），内容旋转与装箱旋转一致，再用 pull-push 外扩填充空白（透明图集 alpha 保持 0）。
    /// </summary>
    internal static class AtoAtlasCompositor
    {
        /// <summary>
        /// Compose one atlas. Returns the produced texture (saved as PNG in the output folder). /
        /// 合成一张图集。返回产出的贴图（以 PNG 保存到输出目录）。
        /// </summary>
        public static Texture2D Compose(AtoContext ctx, AtoAtlas atlas, AtoQualityEvaluator evaluator,
            bool nearLossless)
        {
            var width = atlas.Width;
            var height = atlas.Height;
            var pixels = new Color32[width * height];

            foreach (var placed in atlas.Placed)
            {
                var island = placed.Island;
                var texture = atlas.SourceByIsland[island];
                var eval = evaluator.Prepare(texture, island, island.UvGroup);
                try
                {
                    var size = placed.GetPixelSize(width, height);
                    var pw = size.x;
                    var ph = size.y;

                    Color32[] content;
                    if (nearLossless && pw == eval.BboxWidth && ph == eval.BboxHeight)
                    {
                        // Copy as-is, no resampling. / 原样拷贝，不重采样。
                        content = eval.CropPixels;
                    }
                    else
                    {
                        content = AtoIslandScaler.ResampleToPixels(eval, pw, ph);
                    }

                    var rect = placed.GetPixelRect(width, height);
                    WriteRotated(pixels, width, height, rect.x, rect.y, pw, ph, content, placed.Rotation);
                }
                finally
                {
                    eval.Dispose();
                }
            }

            // Pull-push dilation fills the empty space. / pull-push 外扩填充空白。
            var native = new NativeArray<Color32>(pixels, Allocator.TempJob);
            var scratch = new NativeArray<Color32>(pixels.Length, Allocator.TempJob);
            try
            {
                if (atlas.Group.HasAlpha)
                {
                    AtoBurstKernels.Dilate(native, scratch, width, height);
                }
                else
                {
                    AtoBurstKernels.DilateOpaque(native, scratch, width, height);
                }
                native.CopyTo(pixels);
            }
            finally
            {
                native.Dispose();
                scratch.Dispose();
            }

            // ---- create texture + save ----
            var texture2D = new Texture2D(width, height, TextureFormat.RGBA32, true, false)
            {
                name = atlas.Name,
            };
            texture2D.SetPixels32(pixels);
            texture2D.Apply(false, false);

            var imported = AtoAssetIO.SaveTexturePng(ctx, texture2D, atlas.Name);
            UnityEngine.Object.DestroyImmediate(texture2D);

            atlas.Result = imported;
            atlas.Utilization = ComputeUtilization(atlas, width, height);

            return imported;
        }

        private static float ComputeUtilization(AtoAtlas atlas, int width, int height)
        {
            long used = 0;
            foreach (var placed in atlas.Placed)
            {
                var size = placed.GetPixelSize(width, height);
                used += (long)size.x * size.y;
            }
            return (float)((double)used / ((double)width * height));
        }

        /// <summary>
        /// Write content into the atlas rect with the packing rotation. The rotation family is
        /// (a,b)→(a,b) | (b,a) | (1−a,1−b) | (1−b,1−a); the mesh UV rewrite uses the SAME mapping,
        /// so the sampled appearance is preserved (tangent data is never recomputed — rotation is
        /// disabled for tangent groups). / 按装箱旋转把内容写入图集矩形。旋转族为
        /// (a,b)→(a,b) | (b,a) | (1−a,1−b) | (1−b,1−a)；网格 UV 重写使用同一映射，采样外观保持一致
        /// （切线数据绝不重算——切线组禁用旋转）。
        /// </summary>
        private static void WriteRotated(Color32[] dest, int destWidth, int destHeight,
            int destX, int destY, int contentWidth, int contentHeight, Color32[] content, int rotation)
        {
            switch (rotation)
            {
                case 0:
                    for (var y = 0; y < contentHeight; y++)
                    {
                        Array.Copy(content, y * contentWidth, dest, (destY + y) * destWidth + destX, contentWidth);
                    }
                    break;
                case 1: // transpose: dest(x,y) = src(y,x). / 转置：dest(x,y) = src(y,x)。
                    for (var y = 0; y < contentHeight; y++)
                    {
                        for (var x = 0; x < contentWidth; x++)
                        {
                            dest[(destY + x) * destWidth + destX + y] = content[y * contentWidth + x];
                        }
                    }
                    break;
                case 2: // 180°: flip both. / 180°：双向翻转。
                    for (var y = 0; y < contentHeight; y++)
                    {
                        for (var x = 0; x < contentWidth; x++)
                        {
                            dest[(destY + contentHeight - 1 - y) * destWidth + destX + contentWidth - 1 - x] =
                                content[y * contentWidth + x];
                        }
                    }
                    break;
                case 3: // transpose + flip. / 转置+翻转。
                    for (var y = 0; y < contentHeight; y++)
                    {
                        for (var x = 0; x < contentWidth; x++)
                        {
                            dest[(destY + contentWidth - 1 - x) * destWidth + destX + contentHeight - 1 - y] =
                                content[y * contentWidth + x];
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Asset IO for generated textures: PNG/EXR saving into the output folder. / 生成贴图的资产 IO：
    /// 以 PNG/EXR 保存到输出目录。
    /// </summary>
    internal static class AtoAssetIO
    {
        /// <summary>
        /// Save a Texture2D as PNG into the output folder and import it. / 把 Texture2D 以 PNG 存到输出目录并导入。
        /// </summary>
        public static Texture2D SaveTexturePng(AtoContext ctx, Texture2D texture, string name)
        {
            var path = ctx.OutputFolder + "/" + Sanitize(name) + ".png";
            var bytes = texture.EncodeToPNG();
            if (bytes == null || bytes.Length == 0)
            {
                ctx.Error($"ATO: failed to encode {name} to PNG.");
                return null;
            }
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}

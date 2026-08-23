// SPDX-License-Identifier: MIT
// EN: Composes the final atlas texture from island crops, then dilates the edges with pull-push.
// ZH: 从岛裁剪合成最终图集贴图，然后用 pull-push 外扩边缘。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Net.Fosa.AvatarTextureOptimizer.Editor.Textures;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// EN: Renders islands into an atlas sized render texture.
    /// ZH: 将岛渲染进一张图集尺寸的 RenderTexture。
    /// </summary>
    public static class AtlasComposer
    {
        private const string Stage = "Atlas";
        private static Material _islandBlit;
        private static Material _pullPush;

        /// <summary>
        /// EN: Draws every placed island of <paramref name="islands"/>, reading from
        ///     <paramref name="source"/> which must already be in the group's reference resolution.
        /// ZH: 绘制 <paramref name="islands"/> 中所有已放置的岛，从 <paramref name="source"/> 读取；
        ///     该源必须已经处于该组的参考分辨率。
        /// </summary>
        public static RenderTexture Compose(RenderTexture source, IReadOnlyList<UvIsland> islands,
            int atlasIndex, Vector2Int atlasSize, bool premultiplyAlpha, RenderTexture target = null)
        {
            EnsureMaterials();

            bool ownsTarget = target == null;
            var atlas = target ?? GpuTextureUtil.GetTemp(atlasSize.x, atlasSize.y);
            var prevActive = RenderTexture.active;
            var prevSrgb = GL.sRGBWrite;
            GL.sRGBWrite = false;

            try
            {
                RenderTexture.active = atlas;
                // EN: When ATO owns the target it starts empty; when several UV groups accumulate into a
                //     shared atlas the caller clears it once and we must not clear again.
                // ZH: 当目标由 ATO 自己分配时它初始为空；多个 UV 组累积进共享图集时，
                //     由调用方清空一次，这里绝不能再清。
                if (ownsTarget) GL.Clear(true, true, new Color(0, 0, 0, 0));

                int drawn = 0;
                foreach (var island in islands)
                {
                    // EN: -2 is the "tentatively placed into the atlas currently being built" marker.
                    // ZH: -2 是“已暂定放入当前正在构建的图集”的标记。
                    if (atlasIndex != -2 && island.AtlasIndex != atlasIndex) continue;
                    if (atlasIndex == -2 && island.AtlasIndex != -2) continue;

                    var region = new RectInt(island.Bounds.x, island.Bounds.y, island.Bounds.width, island.Bounds.height);
                    var islandSize = island.ScaledSize;
                    var crop = GpuTextureUtil.Downsample(source, region, islandSize, premultiplyAlpha);
                    try
                    {
                        int w = island.Rotated ? islandSize.y : islandSize.x;
                        int h = island.Rotated ? islandSize.x : islandSize.y;
                        var rect = new Rect(island.AtlasOrigin.x, island.AtlasOrigin.y, w, h);

                        _islandBlit.SetFloat("_ATO_Rotate", island.Rotated ? 1f : 0f);
                        DrawQuad(crop, atlas, rect, atlasSize);
                        drawn++;
                    }
                    finally
                    {
                        GpuTextureUtil.Release(crop);
                    }
                }

                AtoLog.Debug_(Stage, $"atlas {atlasIndex}: composed {drawn} islands into {atlasSize.x}x{atlasSize.y}");
            }
            finally
            {
                GL.sRGBWrite = prevSrgb;
                RenderTexture.active = prevActive;
            }

            return atlas;
        }

        private static void DrawQuad(Texture source, RenderTexture destination, Rect pixelRect, Vector2Int atlasSize)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = destination;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, atlasSize.x, 0, atlasSize.y);
            _islandBlit.SetTexture("_MainTex", source);
            _islandBlit.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0, 0); GL.Vertex3(pixelRect.xMin, pixelRect.yMin, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(pixelRect.xMax, pixelRect.yMin, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(pixelRect.xMax, pixelRect.yMax, 0);
            GL.TexCoord2(0, 1); GL.Vertex3(pixelRect.xMin, pixelRect.yMax, 0);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>
        /// EN: Infinite edge dilation. Builds a pyramid down to 1x1 (pull), then pushes back up, filling
        ///     uncovered texels from the nearest coarser level. Because the fill only ever touches texels
        ///     with zero coverage, island interiors are bit exact.
        /// ZH: 无限边缘外扩。先向下构建到 1x1 的金字塔（pull），再向上推回（push），
        ///     用最近的粗层填补未覆盖像素。由于填充只影响覆盖度为零的像素，岛的内部逐位不变。
        /// </summary>
        public static RenderTexture Dilate(RenderTexture source)
        {
            EnsureMaterials();

            var levels = new List<RenderTexture> { source };
            int w = source.width, h = source.height;
            var prevSrgb = GL.sRGBWrite;
            GL.sRGBWrite = false;

            try
            {
                // EN: PULL
                // ZH: PULL
                while (w > 1 || h > 1)
                {
                    int nw = Mathf.Max(1, w / 2);
                    int nh = Mathf.Max(1, h / 2);
                    var next = GpuTextureUtil.GetTemp(nw, nh);
                    _pullPush.SetVector("_ATO_TexelSize", new Vector4(1f / w, 1f / h, w, h));
                    Graphics.Blit(levels[levels.Count - 1], next, _pullPush, 0);
                    levels.Add(next);
                    w = nw; h = nh;
                }

                // EN: PUSH
                // ZH: PUSH
                for (int i = levels.Count - 2; i >= 0; i--)
                {
                    var fine = levels[i];
                    var coarse = levels[i + 1];
                    var merged = GpuTextureUtil.GetTemp(fine.width, fine.height);
                    _pullPush.SetTexture("_ATO_Coarse", coarse);
                    Graphics.Blit(fine, merged, _pullPush, 1);
                    if (i != 0) GpuTextureUtil.Release(fine);
                    levels[i] = merged;
                }

                for (int i = 1; i < levels.Count; i++) GpuTextureUtil.Release(levels[i]);
                return levels[0];
            }
            finally
            {
                GL.sRGBWrite = prevSrgb;
            }
        }

        private static void EnsureMaterials()
        {
            if (_islandBlit == null)
            {
                var s = Shader.Find("Hidden/ATO/IslandBlit");
                if (s == null) throw new InvalidOperationException("[ATO] Hidden/ATO/IslandBlit shader is missing.");
                _islandBlit = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_pullPush == null)
            {
                var s = Shader.Find("Hidden/ATO/PullPush");
                if (s == null) throw new InvalidOperationException("[ATO] Hidden/ATO/PullPush shader is missing.");
                _pullPush = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            }
        }
    }
}

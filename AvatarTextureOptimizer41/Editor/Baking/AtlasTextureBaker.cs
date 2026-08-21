using UnityEngine;

// Atlas texture baking: samples each island's content from its source texture into the atlas rect
// (with 90° rotation support) via a rect-restricted blit, then runs GPU pull-push to fill empty space.
// 图集贴图烘焙：通过矩形受限 blit 将每个岛的内容从源贴图采样到图集矩形（支持 90° 旋转），
// 再用 GPU pull-push 填充空白。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtlasTextureBaker
    {
        private static Material _blitMat;
        private static readonly int RectId = Shader.PropertyToID("_Rect");
        private static readonly int UvScaleId = Shader.PropertyToID("_UVScale");
        private static readonly int UvOffsetId = Shader.PropertyToID("_UVOffset");

        private static Material BlitMat
        {
            get
            {
                if (_blitMat == null)
                    _blitMat = new Material(Shader.Find("Hidden/ATO/Blit")) { hideFlags = HideFlags.HideAndDontSave };
                return _blitMat;
            }
        }

        /// <summary>
        /// Bakes one atlas to a Texture2D (RGBA32). The caller writes it to an asset and applies
        /// import settings. 烘焙一张图集为 Texture2D（RGBA32）；由调用方写入资产并应用导入设置。
        /// </summary>
        public static Texture2D Bake(AtlasDefinition atlas, ATOBuildContext ctx, RenderTexturePool pool)
        {
            int w = atlas.Width, h = atlas.Height;
            var content = pool.Acquire(w, h, RenderTextureFormat.ARGB32, linear: true);
            var prev = RenderTexture.active;
            RenderTexture.active = content;
            GL.Clear(true, true, new Color(0, 0, 0, 0));
            RenderTexture.active = prev;

            var mat = BlitMat;
            foreach (var kv in atlas.PropertyForUse)
            {
                var use = kv.Key;
                foreach (var rectKv in atlas.IslandRects)
                {
                    var island = rectKv.Key;
                    var rect = rectKv.Value;
                    if (!use.IslandScaleFactors.ContainsKey(island)) continue;

                    var uvRect = IslandScaler.IslandUVRect(island);
                    mat.SetVector(RectId, new Vector4((float)rect.x / w, (float)rect.y / h, (float)rect.width / w, (float)rect.height / h));
                    mat.SetVector(UvScaleId, new Vector4(uvRect.width, uvRect.height, 0, 0));
                    mat.SetVector(UvOffsetId, new Vector4(uvRect.xMin, uvRect.yMin, 0, 0));
                    Graphics.Blit(use.Texture, content, mat, island.Rotation == 1 ? 5 : 4);
                }
            }

            // Pull-push fill. Transparent atlases keep alpha 0 in empty areas (spec: 透明贴图 alpha 保持 0),
            // so the fill is skipped for them — the empty area stays transparent.
            // 外扩填充。透明图集在空白区域保持 alpha 0（规格要求），因此跳过填充——空白保持透明。
            RenderTexture filled = content;
            if (atlas.Bucket.Class != TextureClass.ColorAlpha)
            {
                filled = PullPush.Fill(content, pool);
                pool.Release(content);
            }

            // Readback to Texture2D. 读回 Texture2D。
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "ATO_Atlas" };
            RenderTexture.active = filled;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false, true);
            RenderTexture.active = prev;
            pool.Release(filled);
            return tex;
        }
    }
}

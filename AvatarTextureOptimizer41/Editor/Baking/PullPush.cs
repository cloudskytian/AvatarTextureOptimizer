using UnityEngine;

// GPU pull-push (jump flooding) edge extrapolation: fills atlas empty space with the nearest island
// edge color ("infinite" outward expansion). For atlases larger than 4096 the JFA runs on a capped
// working resolution to keep memory comfortable, then the fill is upscaled and composited.
// GPU pull-push（跳 flood）边缘外扩：用最近的岛边缘颜色填充图集空白（"无限"外扩）。
// 超过 4096 的图集在受限工作分辨率上运行 JFA 以控制内存，再放大填充并合成。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class PullPush
    {
        private const int MaxWorkDim = 4096;

        private static Material _jfaMat;
        private static Material _blitMat;
        private static int _strideId = Shader.PropertyToID("_JFAStride");
        private static int _thresholdId = Shader.PropertyToID("_Threshold");

        private static Material JfaMat
        {
            get
            {
                if (_jfaMat == null)
                    _jfaMat = new Material(Shader.Find("Hidden/ATO/JFA")) { hideFlags = HideFlags.HideAndDontSave };
                return _jfaMat;
            }
        }

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
        /// Fills empty space of `content` (islands with alpha>0, else 0) and returns a NEW RenderTexture
        /// with the extrapolated fill. Transparent content keeps alpha 0 (seeds carry their alpha).
        /// 填充 `content`（岛 alpha>0，其余为 0）的空白，返回外扩填充后的新 RenderTexture。
        /// 透明内容保持 alpha 0（种子携带其 alpha）。
        /// </summary>
        public static RenderTexture Fill(RenderTexture content, RenderTexturePool pool)
        {
            int w = content.width, h = content.height;
            int work = Mathf.Min(MaxWorkDim, Mathf.Max(w, h));
            float scale = (float)work / Mathf.Max(w, h);
            int workW = Mathf.Max(2, Mathf.RoundToInt(w * scale));
            int workH = Mathf.Max(2, Mathf.RoundToInt(h * scale));

            var jfa = JfaMat;
            jfa.SetFloat(_thresholdId, 1f / 255f);

            // Downsample content to working resolution. 降采样内容到工作分辨率。
            var workRT = pool.Acquire(workW, workH, RenderTextureFormat.ARGB32, linear: true);
            Graphics.Blit(content, workRT);

            // JFA pass 0: seeds. Position textures need 4 channels (xy=pos, z=valid).
            // JFA 第 0 遍：种子。位置贴图需要 4 通道（xy=位置，z=有效）。
            var posA = pool.Acquire(workW, workH, RenderTextureFormat.ARGBHalf, linear: true);
            var posB = pool.Acquire(workW, workH, RenderTextureFormat.ARGBHalf, linear: true);
            Graphics.Blit(workRT, posA, jfa, 0);

            // Propagation with halving stride. 步长减半的传播。
            int stride = 1;
            while (stride < Mathf.Max(workW, workH)) stride <<= 1;
            bool src = true;
            for (; stride >= 1; stride >>= 1)
            {
                jfa.SetFloat(_strideId, stride);
                var dst = src ? posB : posA;
                Graphics.Blit(src ? posA : posB, dst, jfa, 1);
                src = !src;
            }
            var posFinal = src ? posB : posA;

            // Final gather: nearest seed color (pass 2 reads _MainTex=pos, _ColorTex=workRT).
            // 最终汇聚：最近种子颜色（pass 2 读 _MainTex=pos、_ColorTex=workRT）。
            jfa.SetTexture("_ColorTex", workRT);
            var filledWork = pool.Acquire(workW, workH, RenderTextureFormat.ARGB32, linear: true);
            Graphics.Blit(posFinal, filledWork, jfa, 2);

            pool.Release(posA);
            pool.Release(posB);

            // Upscale fill to full size and composite with original content.
            // 放大填充到全尺寸并与原内容合成。
            var filledFull = pool.Acquire(w, h, RenderTextureFormat.ARGB32, linear: true);
            Graphics.Blit(filledWork, filledFull); // bilinear upscale. 双线性放大。

            var blit = BlitMat;
            blit.SetTexture("_BlendTex", filledFull);
            var result = pool.Acquire(w, h, RenderTextureFormat.ARGB32, linear: true);
            Graphics.Blit(content, result, blit, 3); // composite pass 3. 合成 pass 3。

            pool.Release(workRT);
            pool.Release(filledWork);
            pool.Release(filledFull);
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato
{
    // ============================================================================
    // GPU 调度器 / GPU dispatchers.
    //  * 全分辨率重采样(compute shader) + 回读, 指标由 Burst 行并行作业计算.
    //    Full-resolution resampling (compute) + readback; metrics run in row-parallel Burst jobs.
    //  * GPU pull-push 岛边缘外扩(JFA); GPU 不可用时回退 CPU-Burst JFA(由 ATOGapFill 统一调度).
    //    GPU pull-push edge dilation (JFA); falls back to CPU-Burst JFA when GPU is unavailable.
    // ============================================================================
    internal static class ATOGpu
    {
        private static ComputeShader _resampleShader;
        private static ComputeShader _pullPushShader;
        private static bool _checked;

        private static bool Supported => SystemInfo.supportsComputeShaders && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        private static ComputeShader Load(string name)
        {
            // 经 AssetDatabase 按名称与包路径查找 / locate by name & package path via AssetDatabase
            return AssetDatabaseSafe.LoadComputeShader(name);
        }

        public static ComputeShader ResampleShader
        {
            get
            {
                if (!_checked) { Check(); }
                return _resampleShader;
            }
        }

        public static ComputeShader PullPushShader
        {
            get
            {
                if (!_checked) { Check(); }
                return _pullPushShader;
            }
        }

        private static void Check()
        {
            _checked = true;
            if (!Supported) return;
            _resampleShader = Load("ATOResampleGPU");
            _pullPushShader = Load("ATOPullPush");
        }

        public static bool ResampleAvailable => ResampleShader != null;

        public static bool PullPushAvailable => PullPushShader != null;

        /// <summary>
        /// GPU 全分辨率重采样: 输入预乘线性RGBA+alpha(+法线), 输出上采样回原尺寸的缓冲.
        /// GPU full-resolution resample: premultiplied-linear RGBA + alpha (+ normals) in,
        /// upsampled-back buffers out.
        /// </summary>
        public static bool ResampleIsland(
            RenderTexture srcTex, RenderTexture srcAlpha,
            RenderTexture srcNormal, RenderTexture mask,
            int srcW, int srcH, float sx, float sy,
            out NativeArray<float> upBuf, out NativeArray<float> upAlphaBuf, out NativeArray<float> upNormalBuf)
        {
            upBuf = default;
            upAlphaBuf = default;
            upNormalBuf = default;
            var shader = ResampleShader;
            if (shader == null) return false;

            int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * sx));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * sy));
            if (dstW == srcW && dstH == srcH)
            {
                // 未缩放 -> 直接回读 / no resize -> read back directly
                return Readback(srcTex, srcAlpha, srcNormal, srcW, srcH, out upBuf, out upAlphaBuf, out upNormalBuf);
            }

            var desc = new RenderTextureDescriptor(srcW, srcH, RenderTextureFormat.ARGBFloat, 0);
            desc.enableRandomWrite = true;
            var dst = RenderTexture.GetTemporary(desc);
            var up = RenderTexture.GetTemporary(desc);
            var dstAlphaDesc = new RenderTextureDescriptor(srcW, srcH, RenderTextureFormat.RFloat, 0);
            dstAlphaDesc.enableRandomWrite = true;
            var dstAlpha = RenderTexture.GetTemporary(dstAlphaDesc);
            var upAlpha = RenderTexture.GetTemporary(dstAlphaDesc);

            RenderTexture dstNormal = null, upNormal = null;
            bool hasNormal = srcNormal != null;
            if (hasNormal)
            {
                var nd = new RenderTextureDescriptor(srcW, srcH, RenderTextureFormat.ARGBFloat, 0);
                nd.enableRandomWrite = true;
                dstNormal = RenderTexture.GetTemporary(nd);
                upNormal = RenderTexture.GetTemporary(nd);
            }

            try
            {
                int k = shader.FindKernel("ATOBilinear");
                shader.SetTexture(k, "_Src", srcTex);
                shader.SetTexture(k, "_SrcAlpha", srcAlpha);
                if (hasNormal) shader.SetTexture(k, "_SrcNormal", srcNormal);
                shader.SetTexture(k, "_Dst", dst);
                shader.SetTexture(k, "_DstAlpha", dstAlpha);
                if (hasNormal) shader.SetTexture(k, "_DstNormal", dstNormal);
                shader.SetTexture(k, "_Up", up);
                shader.SetTexture(k, "_UpAlpha", upAlpha);
                if (hasNormal) shader.SetTexture(k, "_UpNormal", upNormal);
                shader.SetVector("_Params", new Vector4(srcW, srcH, dstW, dstH));
                shader.SetInt("_HasNormal", hasNormal ? 1 : 0);

                // 下采样 / downsample
                shader.SetInt("_Pass", 0);
                shader.Dispatch(k, Mathf.CeilToInt(dstW / 8f), Mathf.CeilToInt(dstH / 8f), 1);

                // 上采样 / upsample
                shader.SetInt("_Pass", 1);
                shader.Dispatch(k, Mathf.CeilToInt(srcW / 8f), Mathf.CeilToInt(srcH / 8f), 1);

                return Readback(up, upAlpha, upNormal, srcW, srcH, out upBuf, out upAlphaBuf, out upNormalBuf);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"GPU 重采样失败, 回退CPU / GPU resample failed, falling back to CPU: {e.Message}");
                return false;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(dst);
                RenderTexture.ReleaseTemporary(up);
                RenderTexture.ReleaseTemporary(dstAlpha);
                RenderTexture.ReleaseTemporary(upAlpha);
                if (dstNormal != null) RenderTexture.ReleaseTemporary(dstNormal);
                if (upNormal != null) RenderTexture.ReleaseTemporary(upNormal);
            }
        }

        private static bool Readback(RenderTexture up, RenderTexture upAlpha, RenderTexture upNormal,
            int w, int h, out NativeArray<float> upBuf, out NativeArray<float> upAlphaBuf, out NativeArray<float> upNormalBuf)
        {
            upBuf = default;
            upAlphaBuf = default;
            upNormalBuf = default;
            int n = w * h;
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = up;
                var cpu = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                cpu.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                cpu.Apply(false, false);
                upBuf = new NativeArray<float>(cpu.GetRawTextureData<float>(), Allocator.TempJob);

                RenderTexture.active = upAlpha;
                var cpuA = new Texture2D(w, h, TextureFormat.RFloat, false, true);
                cpuA.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                cpuA.Apply(false, false);
                upAlphaBuf = new NativeArray<float>(cpuA.GetRawTextureData<float>(), Allocator.TempJob);

                if (upNormal != null)
                {
                    RenderTexture.active = upNormal;
                    var cpuN = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                    cpuN.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    cpuN.Apply(false, false);
                    upNormalBuf = new NativeArray<float>(cpuN.GetRawTextureData<float>(), Allocator.TempJob);
                }

                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(cpu);
                UnityEngine.Object.DestroyImmediate(cpuA);
                return true;
            }
            catch (Exception e)
            {
                RenderTexture.active = prev;
                ATOLog.Warn($"GPU 回读失败 / GPU readback failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// GPU pull-push 外扩: 把岛边缘颜色扩散填充整个图集空白(透明图集alpha=0).
        /// GPU pull-push dilation: spread island-edge colors across empty atlas regions (alpha stays 0 when transparent).
        /// </summary>
        public static bool PullPushFill(RenderTexture atlasRT, bool transparent)
        {
            var shader = PullPushShader;
            if (shader == null) return false;

            int w = atlasRT.width, h = atlasRT.height;
            int cw = Mathf.Max(1, w / 4), ch = Mathf.Max(1, h / 4);

            var stateDesc = new RenderTextureDescriptor(cw, ch, RenderTextureFormat.ARGBInt, 0);
            stateDesc.enableRandomWrite = true;
            var state = RenderTexture.GetTemporary(stateDesc);
            var pong = RenderTexture.GetTemporary(stateDesc);

            try
            {
                int kSeed = shader.FindKernel("ATOSeed");
                shader.SetTexture(kSeed, "_Atlas", atlasRT);
                shader.SetTexture(kSeed, "_State", state);
                shader.SetVector("_Params", new Vector4(w, h, 0, 0));
                shader.SetInt("_W", w);
                shader.SetInt("_H", h);
                shader.SetInt("_CW", cw);
                shader.SetInt("_CH", ch);
                shader.SetInt("_Transparent", transparent ? 1 : 0);
                shader.Dispatch(kSeed, Mathf.CeilToInt(cw / 8f), Mathf.CeilToInt(ch / 8f), 1);

                int kJfa = shader.FindKernel("ATOJFA");
                int kSync = shader.FindKernel("ATOSync");
                shader.SetTexture(kJfa, "_State", state);
                shader.SetTexture(kJfa, "_Pong", pong);
                shader.SetTexture(kSync, "_State", state);
                shader.SetTexture(kSync, "_Pong", pong);

                int maxSide = Mathf.Max(cw, ch);
                int steps = 0;
                for (int s = maxSide / 2; s >= 1; s /= 2)
                {
                    shader.SetInt("_UsePing", s);
                    shader.Dispatch(kJfa, Mathf.CeilToInt(cw / 8f), Mathf.CeilToInt(ch / 8f), 1);
                    shader.Dispatch(kSync, Mathf.CeilToInt(cw / 8f), Mathf.CeilToInt(ch / 8f), 1);
                    steps++;
                }

                int kFin = shader.FindKernel("ATOFinalize");
                shader.SetTexture(kFin, "_Atlas", atlasRT);
                shader.SetTexture(kFin, "_State", state);
                shader.Dispatch(kFin, Mathf.CeilToInt(cw / 8f), Mathf.CeilToInt(ch / 8f), 1);

                ATOLog.InfoVerbose($"GPU pull-push 外扩完成 / GPU pull-push fill done: {w}x{h} ({steps} JFA rounds)");
                return true;
            }
            catch (Exception e)
            {
                ATOLog.Warn($"GPU pull-push 失败, 回退CPU / GPU pull-push failed, falling back to CPU: {e.Message}");
                return false;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(state);
                RenderTexture.ReleaseTemporary(pong);
            }
        }
    }

    /// <summary>AssetDatabase 安全加载辅助 / AssetDatabase-safe shader loading helper.</summary>
    internal static class AssetDatabaseSafe
    {
        public static ComputeShader LoadComputeShader(string name)
        {
#if UNITY_EDITOR
            var guids = UnityEditor.AssetDatabase.FindAssets($"{name} t:ComputeShader");
            foreach (var g in guids)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
                if (path.Contains("net.fosa.avatar-texture-optimizer"))
                {
                    return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                }
            }
#endif
            return null;
        }
    }
}

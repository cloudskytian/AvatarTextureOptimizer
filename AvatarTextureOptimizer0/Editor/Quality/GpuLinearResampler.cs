using System;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    internal enum ATONormalInputEncoding
    {
        Imported = 0,
        EncodedRgb = 1,
        EncodedRgOrAg = 2,
        EncodedAg = 3
    }

    internal sealed class GpuLinearResampler : IDisposable
    {
        private readonly ComputeShader _shader;
        private readonly int _pointKernel;
        private readonly int _linearKernel;

        public GpuLinearResampler()
        {
            _shader = Resources.Load<ComputeShader>("ATOResample");
            if (_shader == null) throw new InvalidOperationException("ATOResample.compute resource is missing.");
            _pointKernel = _shader.FindKernel("ResamplePoint");
            _linearKernel = _shader.FindKernel("ResampleLinear");
        }

        public RenderTexture Resample(Texture source, Rect uvRect, Vector2Int size, bool point, bool inputPremultiplied,
            bool outputStraightAlpha, ATOTextureKind textureKind,
            ATONormalInputEncoding normalInputEncoding = ATONormalInputEncoding.Imported,
            bool rotatePackedRegion = false, int sourceMip = 0)
        {
            if (sourceMip < 0 || source is Texture2D texture && sourceMip >= texture.mipmapCount)
                throw new ArgumentOutOfRangeException(nameof(sourceMip));
            RenderTexture destination = null;
            try
            {
                // Assign the Unity object before invoking any property setter: C# object initializers do not assign
                // the local until every setter succeeds, which would make a throwing setter leak the native object.
                // 先保存 Unity 对象再设置属性；对象初始化器中的 setter 抛异常时局部变量尚未赋值，会泄漏原生对象。
                destination = new RenderTexture(size.x, size.y, 0);
                destination.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                destination.enableRandomWrite = true;
                destination.useMipMap = false;
                destination.autoGenerateMips = false;
                destination.wrapMode = TextureWrapMode.Clamp;
                destination.filterMode = point ? FilterMode.Point : FilterMode.Bilinear;
                destination.name = "ATO_Resample_Temporary";
                if (!destination.Create()) throw new InvalidOperationException("ATO could not allocate a GPU resampling surface.");
                var kernel = point ? _pointKernel : _linearKernel;
                _shader.SetTexture(kernel, "_Source", source);
                _shader.SetTexture(kernel, "_Destination", destination);
                _shader.SetInts("_SourceSize", Mathf.Max(1, source.width >> sourceMip),
                    Mathf.Max(1, source.height >> sourceMip));
                _shader.SetInts("_DestinationSize", size.x, size.y);
                _shader.SetVector("_UvRect", new Vector4(uvRect.x, uvRect.y, uvRect.width, uvRect.height));
                _shader.SetInt("_InputPremultiplied", inputPremultiplied ? 1 : 0);
                _shader.SetInt("_OutputStraightAlpha", outputStraightAlpha ? 1 : 0);
                _shader.SetInt("_TextureKind", (int)textureKind);
                _shader.SetInt("_InputNormalEncoding", (int)normalInputEncoding);
                _shader.SetInt("_RotateSample", rotatePackedRegion ? 1 : 0);
                _shader.SetInt("_SourceMip", sourceMip);
                _shader.Dispatch(kernel, (size.x + 7) / 8, (size.y + 7) / 8, 1);
                return destination;
            }
            catch
            {
                Release(destination);
                throw;
            }
        }

        public NativeArray<float4> Readback(RenderTexture source, Allocator allocator)
        {
            ValidateReadbackSource(source);
            ATOProgress.Checkpoint("Reading linear GPU pixels");
            var request = AsyncGPUReadback.Request(source, 0);
            request.WaitForCompletion();
            if (request.hasError) throw new InvalidOperationException("ATO linear GPU readback failed.");
            var sourcePixels = request.GetData<half4>();
            if (sourcePixels.Length != checked(source.width * source.height))
                throw new InvalidOperationException("ATO linear GPU readback returned an unexpected byte layout.");
            var result = new NativeArray<float4>(sourcePixels.Length, allocator, NativeArrayOptions.UninitializedMemory);
            try
            {
                for (var index = 0; index < sourcePixels.Length; index++)
                {
                    if ((index & 65535) == 0) ATOProgress.Checkpoint("Converting linear GPU pixels");
                    var value = sourcePixels[index];
                    result[index] = new float4((float)value.x, (float)value.y, (float)value.z, (float)value.w);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Copies a linear half-float GPU surface into explicit RGBA32 storage. sRGB encoding is performed here,
        /// rather than delegated to ReadPixels, whose gamma behavior has differed across Unity graphics backends.
        /// / 将线性半浮点 GPU 表面显式编码到 RGBA32，避免依赖不同图形后端下不一致的 ReadPixels Gamma 行为。
        /// </summary>
        public static void CopyToRgba32(RenderTexture source, Texture2D destination, int mip, bool srgb)
        {
            ValidateReadbackSource(source);
            if (destination == null || destination.format != TextureFormat.RGBA32)
                throw new ArgumentException("ATO RGBA32 readback requires an RGBA32 destination.", nameof(destination));
            if (mip < 0 || mip >= destination.mipmapCount)
                throw new ArgumentOutOfRangeException(nameof(mip));
            var expectedWidth = Mathf.Max(1, destination.width >> mip);
            var expectedHeight = Mathf.Max(1, destination.height >> mip);
            if (source.width != expectedWidth || source.height != expectedHeight)
                throw new ArgumentException("ATO RGBA32 readback source and destination mip dimensions do not match.", nameof(source));

            ATOProgress.Checkpoint("Reading RGBA32 GPU pixels");
            var request = AsyncGPUReadback.Request(source, 0);
            request.WaitForCompletion();
            if (request.hasError) throw new InvalidOperationException("ATO RGBA32 GPU readback failed.");
            var sourcePixels = request.GetData<half4>();
            if (sourcePixels.Length != checked(source.width * source.height))
                throw new InvalidOperationException("ATO RGBA32 GPU readback returned an unexpected byte layout.");
            var encoded = new NativeArray<Color32>(sourcePixels.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                for (var index = 0; index < sourcePixels.Length; index++)
                {
                    if ((index & 65535) == 0) ATOProgress.Checkpoint("Encoding RGBA32 GPU pixels");
                    var value = sourcePixels[index];
                    var r = (float)value.x; var g = (float)value.y; var b = (float)value.z; var a = (float)value.w;
                    if (!IsFinite(r) || !IsFinite(g) || !IsFinite(b) || !IsFinite(a))
                        throw new InvalidOperationException("ATO GPU readback contained a non-finite pixel.");
                    encoded[index] = new Color32(EncodeByte(r, srgb), EncodeByte(g, srgb), EncodeByte(b, srgb),
                        EncodeByte(a, false));
                }
                destination.SetPixelData(encoded, mip);
            }
            finally { encoded.Dispose(); }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static byte EncodeByte(float value, bool srgb)
        {
            value = math.saturate(value);
            var encoded = !srgb ? value : value <= 0.0031308f
                ? value * 12.92f
                : 1.055f * math.pow(value, 1f / 2.4f) - 0.055f;
            return (byte)math.clamp((int)math.round(encoded * 255f), 0, 255);
        }

        private static void ValidateReadbackSource(RenderTexture source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("ATO requires asynchronous GPU readback support for deterministic linear pixels.");
            if (source.graphicsFormat != GraphicsFormat.R16G16B16A16_SFloat)
                throw new NotSupportedException("ATO deterministic readback only accepts its linear RGBA16F work surfaces.");
        }

        public void Dispose() { }

        public static void Release(RenderTexture value)
        {
            if (value == null) return;
            value.Release(); UnityEngine.Object.DestroyImmediate(value);
        }
    }
}

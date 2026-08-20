// 编译验证桩：Burst / Jobs / Collections / Mathematics 最小表面 / Compile-check stubs: minimal Burst/Jobs/Collections/Mathematics surface.
// 仅覆盖 ATO 代码使用的成员。Not shipped with the package.

using System;

namespace Unity.Burst
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
    public class BurstCompileAttribute : Attribute
    {
        public FloatMode FloatMode { get; set; }
        public bool CompileSynchronously { get; set; }
        public bool DisableSafetyChecks { get; set; }
    }

    public enum FloatMode { Default = 0, Strict = 1, Deterministic = 2, Fast = 3 }
}

namespace Unity.Jobs
{
    public interface IJob
    {
        void Execute();
    }

    public interface IJobParallelFor
    {
        void Execute(int index);
    }

    public struct JobHandle
    {
        public void Complete() { }
        public static JobHandle CombineDependencies(JobHandle job0, JobHandle job1) => default;
        public bool IsCompleted => true;
    }

    public static class IJobParallelForExtensions
    {
        // 桩环境：同步执行全部索引 / stub environment: execute all indices synchronously
        public static JobHandle Schedule<T>(this T jobData, int arrayLength, int innerloopBatchCount, JobHandle dependsOn = default) where T : struct, IJobParallelFor
        {
            for (int i = 0; i < arrayLength; i++) jobData.Execute(i);
            return default;
        }
        public static void Run<T>(this T jobData) where T : struct, IJobParallelFor => jobData.Execute(0);
    }

    public static class IJobExtensions
    {
        public static JobHandle Schedule<T>(this T jobData, JobHandle dependsOn = default) where T : struct, IJob
        {
            jobData.Execute();
            return default;
        }
        public static void Run<T>(this T jobData) where T : struct, IJob => jobData.Execute();
    }
}

namespace Unity.Collections
{
    public enum Allocator
    {
        Invalid = 0, None = 1, Temp = 2, TempJob = 4, Persistent = 8,
    }

    [Flags]
    public enum NativeArrayOptions
    {
        UninitializedMemory = 0,
        ClearMemory = 1,
    }

    public struct NativeArray<T> : IDisposable where T : struct
    {
        private T[] _data;
        public NativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            _data = new T[length];
        }
        public NativeArray(T[] array, Allocator allocator)
        {
            _data = (T[])array.Clone();
        }
        public NativeArray(NativeArray<T> array, Allocator allocator)
        {
            _data = (T[])array._data.Clone();
        }
        public int Length => _data?.Length ?? 0;
        public bool IsCreated => _data != null;
        public T this[int index] { get => _data[index]; set => _data[index] = value; }
        public void Dispose() { _data = null; }
        public void CopyFrom(NativeArray<T> array) { _data = (T[])array._data.Clone(); }
        public void CopyTo(NativeArray<T> array) { array._data = (T[])_data.Clone(); }
        public void CopyTo(T[] array) { _data.CopyTo(array, 0); }
        public T[] ToArray() => (T[])_data.Clone();
        public NativeArray<T> GetSubArray(int start, int length) => this;
        public Enumerator GetEnumerator() => new Enumerator(_data);
        public struct Enumerator
        {
            private readonly T[] _array;
            private int _index;
            public Enumerator(T[] array) { _array = array; _index = -1; }
            public bool MoveNext() => _array != null && ++_index < _array.Length;
            public T Current => _array[_index];
        }
        public static bool operator ==(NativeArray<T> a, NativeArray<T> b) => ReferenceEquals(a._data, b._data);
        public static bool operator !=(NativeArray<T> a, NativeArray<T> b) => !(a == b);
        public override bool Equals(object obj) => false;
        public override int GetHashCode() => _data?.GetHashCode() ?? 0;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class ReadOnlyAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public class WriteOnlyAttribute : Attribute { }

    public static class NativeSortExtension
    {
        public static void Sort<T>(NativeArray<T> array) where T : struct, IComparable<T> { }
    }
}

namespace Unity.Mathematics
{
    using System.Runtime.CompilerServices;

    public struct float2
    {
        public float x, y;
        public float2(float x, float y) { this.x = x; this.y = y; }
        public static float2 zero => new float2();
        public static float2 operator +(float2 a, float2 b) => new float2(a.x + b.x, a.y + b.y);
        public static float2 operator -(float2 a, float2 b) => new float2(a.x - b.x, a.y - b.y);
        public static float2 operator *(float2 a, float b) => new float2(a.x * b, a.y * b);
        public static float2 operator *(float b, float2 a) => new float2(a.x * b, a.y * b);
        public static float2 operator /(float2 a, float b) => new float2(a.x / b, a.y / b);
    }

    public struct float3
    {
        public float x, y, z;
        public float3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float3(float2 xy, float z) { x = xy.x; y = xy.y; this.z = z; }
        public static float3 zero => new float3();
        public float2 xy => new float2(x, y);
        public float2 yz => new float2(y, z);
        public static float3 operator +(float3 a, float3 b) => new float3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static float3 operator -(float3 a, float3 b) => new float3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float3 operator *(float3 a, float b) => new float3(a.x * b, a.y * b, a.z * b);
        public static float3 operator *(float b, float3 a) => new float3(a.x * b, a.y * b, a.z * b);
        public static float3 operator /(float3 a, float b) => new float3(a.x / b, a.y / b, a.z / b);
    }

    public struct float4
    {
        public float x, y, z, w;
        public float4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public float4(float2 xy, float z, float w) { x = xy.x; y = xy.y; this.z = z; this.w = w; }
        public float4(float3 xyz, float w) { x = xyz.x; y = xyz.y; z = xyz.z; this.w = w; }
        public static float4 zero => new float4();
        public float3 xyz { get => new float3(x, y, z); set { x = value.x; y = value.y; z = value.z; } }
        public float2 xy { get => new float2(x, y); set { x = value.x; y = value.y; } }
        public float this[int index]
        {
            get { switch (index) { case 0: return x; case 1: return y; case 2: return z; default: return w; } }
            set { switch (index) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; default: w = value; break; } }
        }
        public static float4 operator +(float4 a, float4 b) => new float4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
        public static float4 operator -(float4 a, float4 b) => new float4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
        public static float4 operator *(float4 a, float b) => new float4(a.x * b, a.y * b, a.z * b, a.w * b);
        public static float4 operator *(float b, float4 a) => new float4(a.x * b, a.y * b, a.z * b, a.w * b);
        public static float4 operator /(float4 a, float b) => new float4(a.x / b, a.y / b, a.z / b, a.w / b);
        public static bool operator ==(float4 a, float4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        public static bool operator !=(float4 a, float4 b) => !(a == b);
        public static bool4 operator >(float4 a, float b) => new bool4 { x = a.x > b, y = a.y > b, z = a.z > b, w = a.w > b };
        public static bool4 operator <(float4 a, float b) => new bool4 { x = a.x < b, y = a.y < b, z = a.z < b, w = a.w < b };
        public override bool Equals(object obj) => obj is float4 f && f == this;
        public override int GetHashCode() => x.GetHashCode();
    }

    public struct bool4
    {
        public bool x, y, z, w;
    }

    public static class math
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float abs(float x) => Math.Abs(x);
        public static float2 abs(float2 x) => new float2(Math.Abs(x.x), Math.Abs(x.y));
        public static float3 abs(float3 x) => new float3(Math.Abs(x.x), Math.Abs(x.y), Math.Abs(x.z));
        public static float4 abs(float4 x) => new float4(Math.Abs(x.x), Math.Abs(x.y), Math.Abs(x.z), Math.Abs(x.w));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float min(float a, float b) => Math.Min(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float max(float a, float b) => Math.Max(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int min(int a, int b) => Math.Min(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int max(int a, int b) => Math.Max(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float clamp(float v, float lo, float hi) => Math.Min(Math.Max(v, lo), hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int clamp(int v, int lo, int hi) => Math.Min(Math.Max(v, lo), hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 clamp(float2 v, float2 lo, float2 hi) => new float2(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 clamp(float3 v, float3 lo, float3 hi) => new float3(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y), clamp(v.z, lo.z, hi.z));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 clamp(float4 v, float4 lo, float4 hi) => new float4(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y), clamp(v.z, lo.z, hi.z), clamp(v.w, lo.w, hi.w));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float floor(float x) => (float)Math.Floor(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 floor(float2 x) => new float2(floor(x.x), floor(x.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ceil(float x) => (float)Math.Ceiling(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float round(float x) => (float)Math.Round(x);
        public static float2 round(float2 x) => new float2(round(x.x), round(x.y));
        public static float3 round(float3 x) => new float3(round(x.x), round(x.y), round(x.z));
        public static float4 round(float4 x) => new float4(round(x.x), round(x.y), round(x.z), round(x.w));
        public static double sqrt(double x) => Math.Sqrt(x);
        public static float4 max(float4 a, float4 b) => new float4(max(a.x, b.x), max(a.y, b.y), max(a.z, b.z), max(a.w, b.w));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float sqrt(float x) => (float)Math.Sqrt(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float pow(float x, float y) => (float)Math.Pow(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float dot(float2 a, float2 b) => a.x * b.x + a.y * b.y;
        public static float dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float lengthsq(float2 v) => v.x * v.x + v.y * v.y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float lengthsq(float3 v) => v.x * v.x + v.y * v.y + v.z * v.z;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 cross(float3 a, float3 b) => new float3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 normalize(float3 v) { var l = sqrt(lengthsq(v)); return l > 1e-12f ? v / l : new float3(); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float saturate(float x) => clamp(x, 0f, 1f);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 saturate(float3 v) => clamp(v, float3.zero, new float3(1, 1, 1));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 saturate(float4 v) => clamp(v, float4.zero, new float4(1, 1, 1, 1));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool any(bool b) => b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool any(float2 v) => v.x != 0 || v.y != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool any(float3 v) => v.x != 0 || v.y != 0 || v.z != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool any(float4 v) => v.x != 0 || v.y != 0 || v.z != 0 || v.w != 0;
        public static bool any(bool4 v) => v.x || v.y || v.z || v.w;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float lerp(float a, float b, float t) => a + (b - a) * t;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 lerp(float4 a, float4 b, float t) => a + (b - a) * t;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float degrees(float radians) => radians * 57.295779513f;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float radians(float degrees) => degrees * 0.01745329252f;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float acos(float x) => (float)Math.Acos(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float atan2(float y, float x) => (float)Math.Atan2(y, x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float sin(float x) => (float)Math.Sin(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float cos(float x) => (float)Math.Cos(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float exp(float x) => (float)Math.Exp(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 min(float2 a, float2 b) => new float2(min(a.x, b.x), min(a.y, b.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 max(float2 a, float2 b) => new float2(max(a.x, b.x), max(a.y, b.y));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 min(float3 a, float3 b) => new float3(min(a.x, b.x), min(a.y, b.y), min(a.z, b.z));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 max(float3 a, float3 b) => new float3(max(a.x, b.x), max(a.y, b.y), max(a.z, b.z));
    }
}

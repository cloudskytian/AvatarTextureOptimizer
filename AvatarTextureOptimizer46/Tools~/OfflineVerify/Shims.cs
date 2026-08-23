// Build-verification shims only. NOT part of the shipped package.
// These stand in for APIs that exist in the real Unity 2022.3 editor / Collections 2.x package but
// not in the 2021 reference assemblies used for this offline compile check.
namespace UnityEditor
{
    public struct ChangeChildrenOrderEventArgs { public int instanceId; }
}
namespace Unity.Collections
{
    public struct NativeQueue<T> : System.IDisposable where T : unmanaged
    {
        public NativeQueue(Allocator a) { }
        public int Count => 0;
        public bool IsCreated => false;
        public void Enqueue(T v) { }
        public T Dequeue() => default;
        public bool TryDequeue(out T v) { v = default; return false; }
        public void Clear() { }
        public void Dispose() { }
        public ParallelWriter AsParallelWriter() => default;
        public struct ParallelWriter { public void Enqueue(T v) { } }
    }
}

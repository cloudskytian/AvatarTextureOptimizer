// 编译验证桩：UnityEngine 最小 API 表面（仅覆盖 ATO 代码实际使用的成员）/ Compile-check stubs: minimal UnityEngine API surface (only members actually used by ATO code).
// 仅用于语法/类型级编译验证，不做任何运行时语义模拟。Not shipped with the package.

using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public HideFlags hideFlags;
        public static void DestroyImmediate(Object obj) { }
        public static T Instantiate<T>(T original) where T : Object => original;
        public static void DontDestroyOnLoad(Object target) { }
        public int GetInstanceID() => 0;
    }

    public class GameObject : Object
    {
        public Transform transform { get; } = new Transform();
        public bool activeSelf;
        public bool activeInHierarchy;
        public T GetComponent<T>() => default;
        public T GetComponentInChildren<T>(bool includeInactive = false) => default;
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => null;
        public T AddComponent<T>() where T : Component, new() => new T();
        public bool CompareTag(string tag) => false;
    }

    public class Component : Object
    {
        public GameObject gameObject { get; } = new GameObject();
        public Transform transform { get; } = new Transform();
        public T GetComponent<T>() => default;
        public T GetComponentInChildren<T>(bool includeInactive = false) => default;
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => null;
        public bool CompareTag(string tag) => false;
    }

    public class Behaviour : Component
    {
        public bool enabled;
    }

    public class MonoBehaviour : Behaviour { }

    public class Transform : Component
    {
        public Transform parent;
        public Vector3 lossyScale => new Vector3(1, 1, 1);
        public Vector3 localScale = new Vector3(1, 1, 1);
    }

    public class Renderer : Behaviour
    {
        public Material[] sharedMaterials { get; set; }
        public bool isVisible;
    }

    public class MeshRenderer : Renderer { }
    public class SkinnedMeshRenderer : Renderer
    {
        public Mesh sharedMesh { get; set; }
    }

    public class MeshFilter : Component
    {
        public Mesh sharedMesh { get; set; }
    }

    public class Mesh : Object
    {
        public int[] triangles { get; set; }
        public int vertexCount { get; }
        public int subMeshCount { get; set; }
        public Vector3[] vertices { get; }
        public int blendShapeCount { get; }
        public int GetBlendShapeFrameCount(int shapeIndex) => 0;
        public float GetBlendShapeFrameWeight(int shapeIndex, int frameIndex) => 100f;
        public void GetBlendShapeFrameVertices(int shapeIndex, int frameIndex, Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents) { }
        public void GetUVs(int channel, List<Vector2> uvs) { }
        public void SetUVs(int channel, List<Vector2> uvs) { }
        public int[] GetTriangles(int submesh) => null;
        public void SetTriangles(int[] triangles, int submesh) { }
        public void SetTriangles(System.Collections.Generic.List<int> triangles, int submesh) { }
        public void RecalculateUVDistributionMetrics() { }
    }

    public class Texture : Object
    {
        public int width;
        public int height;
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        public int anisoLevel;
    }

    public class Texture2D : Texture
    {
        public bool isReadable;
        public Texture2D(int width, int height) { }
        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain) { }
        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain, bool linear) { }
        public Color32[] GetPixels32(int miplevel = 0) => null;
        public void SetPixels32(Color32[] colors, int miplevel = 0) { }
        public void SetPixels32(int x, int y, int blockWidth, int blockHeight, Color32[] colors, int miplevel = 0) { }
        public void SetPixelData<T>(T[] data, int miplevel) where T : struct { }
        public void Apply(bool updateMipmaps = true, bool makeNoLongerReadable = false) { }
        public byte[] EncodeToPNG() => null;
        public void LoadRawTextureData(byte[] data) { }
        public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps) { }
        public Unity.Collections.NativeArray<T> GetPixelData<T>(int miplevel = 0) where T : struct => default;
    }

    public class Material : Object
    {
        public Shader shader { get; set; }
        public string[] shaderKeywords { get; set; }
        public int renderQueue;
        public int globalIlluminationFlags;
        public bool doubleSidedGI;
        public bool enableInstancing;
        public bool HasProperty(string name) => false;
        public float GetFloat(string name) => 0;
        public int GetInt(string name) => 0;
        public Color GetColor(string name) => default;
        public Vector4 GetVector(string name) => default;
        public Texture GetTexture(string name) => null;
        public void SetTexture(string name, Texture value) { }
        public void SetFloat(string name, float value) { }
        public void SetInt(string name, int value) { }
        public void SetColor(string name, Color value) { }
        public void SetVector(string name, Vector4 value) { }
    }

    public class Shader : Object
    {
        public static Shader Find(string name) => null;
    }

    public class AnimationClip : Object
    {
        public bool legacy;
        public float frameRate;
        public WrapMode wrapMode;
        public void SetCurve(string relativePath, Type type, string propertyName, AnimationCurve curve) { }
        public AnimationCurve GetCurve(string relativePath, Type type, string propertyName) => null;
    }

    public class RuntimeAnimatorController : Object
    {
        public AnimationClip[] animationClips { get; }
    }

    public class Animator : Behaviour
    {
        public RuntimeAnimatorController runtimeAnimatorController { get; set; }
    }

    public class Animation : Behaviour
    {
        public AnimationClip clip { get; set; }
    }

    public class AnimationCurve
    {
        public Keyframe[] keys { get; set; }
    }

    public struct Keyframe
    {
        public float time;
        public float value;
        public Keyframe(float time, float value) { this.time = time; this.value = value; }
    }

    public enum WrapMode { Default = 0, Once = 1, Loop = 2, PingPong = 4, ClampForever = 8 }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2();
        public static Vector2 one => new Vector2(1, 1);
        public static Vector2 Min(Vector2 a, Vector2 b) => new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
        public static Vector2 Max(Vector2 a, Vector2 b) => new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public float magnitude => 0;
        public Vector2 normalized => this;
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3();
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 Cross(Vector3 lhs, Vector3 rhs) => zero;
        public float magnitude => 0;
        public Vector3 normalized => this;
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vector4 zero => new Vector4();
        public static Vector4 one => new Vector4(1, 1, 1, 1);
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
    }

    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
        public static Vector2Int zero => new Vector2Int();
    }

    public struct RectInt
    {
        public int x, y, width, height;
        public RectInt(int x, int y, int width, int height) { this.x = x; this.y = y; this.width = width; this.height = height; }
        public int xMin => x;
        public int yMin => y;
        public int xMax => x + width;
        public int yMax => y + height;
    }

    public static class Mathf
    {
        public const float PI = 3.14159265358979f;
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Abs(float f) => f < 0 ? -f : f;
        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static float Clamp01(float v) => Clamp(v, 0, 1);
        public static float Floor(float f) => (float)Math.Floor(f);
        public static float Ceil(float f) => (float)Math.Ceiling(f);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Pow(float f, float p) => (float)Math.Pow(f, p);
        public static float Round(float f) => (float)Math.Round(f);
        public static int RoundToInt(float f) => (int)Math.Round(f);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
        public static int FloorToInt(float f) => (int)Math.Floor(f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static int NextPowerOfTwo(int value) { int v = value; v--; v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16; return v + 1; }
    }

    public enum FilterMode { Point = 0, Bilinear = 1, Trilinear = 2 }
    public enum TextureWrapMode { Repeat = 0, Clamp = 1, Mirror = 2, MirrorOnce = 3 }
    public enum HideFlags { None = 0, HideAndDontSave = 61, HideInHierarchy = 1, DontSave = 52 }
    public enum TextureFormat { RGBA32 = 4, RGB24 = 3, RGBAFloat = 122, RFloat = 127, R8 = 63 }

    public sealed class TooltipAttribute : Attribute { public string tooltip; public TooltipAttribute(string tooltip) { this.tooltip = tooltip; } }
    public sealed class RangeAttribute : Attribute { public float min, max; public RangeAttribute(float min, float max) { this.min = min; this.max = max; } }
    public sealed class MinAttribute : Attribute { public float min; public MinAttribute(float min) { this.min = min; } }
    public sealed class InspectorNameAttribute : Attribute { public string displayName; public InspectorNameAttribute(string displayName) { this.displayName = displayName; } }
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }
    public sealed class SpaceAttribute : Attribute { public SpaceAttribute(float height = 8) { } }
    public sealed class SerializeField : Attribute { }
    public sealed class SerializableAttribute : Attribute { }
    public sealed class CreateAssetMenuAttribute : Attribute { public string fileName; public string menuName; public int order; }
    public sealed class AddComponentMenuAttribute : Attribute { public string componentMenu; public int componentOrder; public AddComponentMenuAttribute(string menuName, int order = 0) { componentMenu = menuName; componentOrder = order; } }
    public sealed class DisallowMultipleComponent : Attribute { }
    public sealed class HelpURLAttribute : Attribute { public string URL; public HelpURLAttribute(string url) { URL = url; } }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => default;
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception) { }
    }

    public static class SystemInfo
    {
        public static bool supportsComputeShaders => true;
        public static bool supportsAsyncGPUReadback => true;
    }

    public static class Application
    {
        public static bool isBatchMode => false;
    }

    public static class Graphics
    {
        public static void Blit(Texture source, RenderTexture dest) { }
    }

    public class RenderTexture : Texture
    {
        public RenderTexture(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite = RenderTextureReadWrite.Default) { }
        public bool enableRandomWrite { get; set; }
        public bool useMipMap { get; set; }
        public bool autoGenerateMips { get; set; }
        public void Create() { }
        public void Release() { }
        public static RenderTexture active { get; set; }
        public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps) { }
    }

    public enum RenderTextureFormat { ARGBFloat = 11, RFloat = 14 }
    public enum RenderTextureReadWrite { Default = 0, Linear = 1, sRGB = 2 }

    public class ComputeShader : Object
    {
        public int FindKernel(string name) => 0;
        public void SetTexture(int kernelIndex, string name, Texture texture) { }
        public void SetInt(int kernelIndex, string name, int value) { }
        public void SetInts(int kernelIndex, string name, params int[] values) { }
        public void SetVector(int kernelIndex, string name, Vector4 value) { }
        public void Dispatch(int kernelIndex, int threadGroupsX, int threadGroupsY, int threadGroupsZ) { }
    }

}

namespace UnityEngine.Profiling
{
    public static class Profiler
    {
        public static void BeginSample(string name) { }
        public static void EndSample() { }
    }
}

namespace UnityEngine.Rendering
{
    public static class AsyncGPUReadback
    {
        public static Request Request(RenderTexture src, int mipIndex, TextureFormat dstFormat, Action<Request> callback) => default;
        public static Request Request<T>(RenderTexture src, int mipIndex, TextureFormat dstFormat, Action<Request> callback) where T : struct => default;
    }

    public class Request
    {
        public bool hasError => false;
        public bool done => true;
        public void WaitForCompletion() { }
        public T[] GetData<T>() where T : struct => null;
        public Unity.Collections.NativeArray<T> GetDataNative<T>() where T : struct => default;
    }
}

namespace UnityEngine.UIElements
{
    public class VisualElement
    {
        public IStyle style { get; } = new StyleImpl();
        public void Add(VisualElement child) { }
    }
    public interface IStyle
    {
        WhiteSpace whiteSpace { get; set; }
        float fontSize { get; set; }
    }
    public class StyleImpl : IStyle
    {
        public WhiteSpace whiteSpace { get; set; }
        public float fontSize { get; set; }
    }
    public class Label : VisualElement
    {
        public string text;
        public Label(string text) { this.text = text; }
    }
    public class Foldout : VisualElement
    {
        public string text;
        public bool value;
        public Foldout() { }
    }
    public enum WhiteSpace { Normal = 0, NoWrap = 1 }

}

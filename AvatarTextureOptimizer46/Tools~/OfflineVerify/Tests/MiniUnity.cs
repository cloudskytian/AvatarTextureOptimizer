// Test-only minimal stand-ins so the pure algorithms can execute outside Unity.
namespace Unity.Burst
{
    [System.AttributeUsage(System.AttributeTargets.All)]
    public class BurstCompileAttribute : System.Attribute
    {
        public BurstCompileAttribute() { }
        public FloatMode FloatMode { get; set; }
        public FloatPrecision FloatPrecision { get; set; }
    }
    public enum FloatMode { Default, Strict, Deterministic, Fast }
    public enum FloatPrecision { Standard, High, Medium, Low }
}
namespace Unity.IL2CPP.CompilerServices
{
    public enum Option { NullChecks, ArrayBoundsChecks, DivideByZeroChecks }
    [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
    public class Il2CppSetOptionAttribute : System.Attribute { public Il2CppSetOptionAttribute(Option o, object v) { } }
}
namespace Unity.Collections
{
    [System.AttributeUsage(System.AttributeTargets.All)]
    public class ReadOnlyAttribute : System.Attribute { }
}

// ---------------------------------------------------------------------------------------------
// Test-only minimal UnityEngine stand-ins. Only the members the tested algorithms actually touch.
// ---------------------------------------------------------------------------------------------
namespace UnityEngine
{
    public static class Mathf
    {
        public const float Epsilon = 1e-7f;
        public static int Max(int a, int b) => a > b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Abs(float a) => System.Math.Abs(a);
        public static int Abs(int a) => System.Math.Abs(a);
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static int CeilToInt(float v) => (int)System.Math.Ceiling(v);
        public static int FloorToInt(float v) => (int)System.Math.Floor(v);
        public static int RoundToInt(float v) => (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
        public static float Round(float v) => (float)System.Math.Round(v, System.MidpointRounding.AwayFromZero);
        public static float Sqrt(float v) => (float)System.Math.Sqrt(v);
        public static float Pow(float a, float b) => (float)System.Math.Pow(a, b);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public float this[int i] { get => i == 0 ? x : y; set { if (i == 0) x = value; else y = value; } }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float b) => new Vector2(a.x * b, a.y * b);
        public static Vector2 operator +(Vector2 a, Vector2Int b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new Vector2(Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.y, b.y, t));
        public override string ToString() => $"({x:F4}, {y:F4})";
    }

    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
        public static Vector2Int zero => new Vector2Int(0, 0);
        public static Vector2Int one => new Vector2Int(1, 1);
        public int this[int i] { get => i == 0 ? x : y; set { if (i == 0) x = value; else y = value; } }
        public static Vector2Int operator +(Vector2Int a, Vector2Int b) => new Vector2Int(a.x + b.x, a.y + b.y);
        public static Vector2Int operator *(Vector2Int a, int b) => new Vector2Int(a.x * b, a.y * b);
        public override string ToString() => $"({x}, {y})";
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 one => new Vector3(1, 1, 1);
    }

    public struct Vector4 { public float x, y, z, w; }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct RectInt
    {
        public int x, y, width, height;
        public RectInt(int x, int y, int w, int h) { this.x = x; this.y = y; width = w; height = h; }
        public Vector2Int size => new Vector2Int(width, height);
        public int xMax => x + width;
        public int yMax => y + height;
        public override string ToString() => $"[{x},{y} {width}x{height}]";
    }

    public enum FilterMode { Point, Bilinear, Trilinear }
    public enum TextureWrapMode { Repeat, Clamp, Mirror, MirrorOnce }

    public class Object { public string name; public int GetInstanceID() => 0; }
    public class Mesh : Object { }
    public class Texture : Object { public int width, height; }
    public class Texture2D : Texture { }
    public class Shader : Object { }
    public class Material : Object { }
    public class Component : Object { }
    public class Renderer : Component { }
}
namespace UnityEngine.Rendering
{
    public enum ShaderPropertyFlags { None = 0, HideInInspector = 1, PerRendererData = 2, NoScaleOffset = 4, Normal = 8, HDR = 16, Gamma = 32, NonModifiableTextureData = 64, MainTexture = 128, MainColor = 256 }
}
namespace Net.Fosa.AvatarTextureOptimizer.Editor.Core
{
    internal static class AtoLog
    {
        public static void Trace(string s, string m) { }
        public static void Debug_(string s, string m) { }
        public static void Info(string s, string m) { }
        public static void Warning(string s, string m) { }
        public static void Error(string s, string m) { }
    }
}

namespace Net.Fosa.AvatarTextureOptimizer.Api
{
    // Test-only placeholder so Model/AtoModel.cs can be compiled in isolation.
    internal static class ApiPlaceholder { }
}

// 编译验证桩：NUnit 最小表面（仅测试编译用）/ Compile-check stubs: minimal NUnit surface (test compilation only).
using System;

namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Class)]
    public class TestFixtureAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class TestAttribute : Attribute { }

    public static class Assert
    {
        public static void AreEqual(double expected, double actual, double delta) { if (Math.Abs(expected - actual) > delta) throw new Exception($"expected {expected} got {actual}"); }
        public static void AreEqual(double expected, double actual, double delta, string message) { if (Math.Abs(expected - actual) > delta) throw new Exception(message); }
        public static void AreEqual(long expected, long actual) { if (expected != actual) throw new Exception($"expected {expected} got {actual}"); }
        public static void AreEqual(int expected, int actual) { if (expected != actual) throw new Exception($"expected {expected} got {actual}"); }
        public static void AreEqual(string expected, string actual) { if (expected != actual) throw new Exception($"expected {expected} got {actual}"); }
        public static void AreEqual(ulong expected, ulong actual) { if (expected != actual) throw new Exception($"expected {expected} got {actual}"); }
        public static void IsTrue(bool condition) { if (!condition) throw new Exception("expected true"); }
        public static void IsFalse(bool condition) { if (condition) throw new Exception("expected false"); }
    }
}

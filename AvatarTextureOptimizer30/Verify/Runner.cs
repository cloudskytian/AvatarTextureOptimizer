// Runner.cs — 沙箱内测试运行器（验证用，不随包分发）/ In-sandbox test runner (verification only, not shipped).
using System;
using System.Linq;
using System.Reflection;

public static class Runner
{
    public static int Main(string[] args)
    {
        var asm = Assembly.Load("ATO.Verify");
        int passed = 0, failed = 0;
        foreach (var type in asm.GetTypes())
        {
            if (type.GetCustomAttribute(typeof(NUnit.Framework.TestFixtureAttribute)) == null) continue;
            foreach (var method in type.GetMethods())
            {
                if (method.GetCustomAttribute(typeof(NUnit.Framework.TestAttribute)) == null) continue;
                try
                {
                    var instance = Activator.CreateInstance(type);
                    method.Invoke(instance, null);
                    passed++;
                    Console.WriteLine($"PASS {type.Name}.{method.Name}");
                }
                catch (Exception e)
                {
                    failed++;
                    var inner = e is TargetInvocationException tie ? tie.InnerException : e;
                    Console.WriteLine($"FAIL {type.Name}.{method.Name}: {inner.Message}");
                }
            }
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}

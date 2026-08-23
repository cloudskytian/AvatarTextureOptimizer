using System;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using Unity.Mathematics;

internal static class Program
{
    private static int _failures;

    private static void Check(string name, double actual, double expected, double tolerance)
    {
        bool ok = Math.Abs(actual - expected) <= tolerance;
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: got {actual:F4}, expected {expected:F4}");
    }

    private static int Main()
    {
        // Sharma, Wu & Dalal (2005) CIEDE2000 verification data.
        var cases = new (double l1, double a1, double b1, double l2, double a2, double b2, double d)[]
        {
            (50.0000,  2.6772, -79.7751, 50.0000,  0.0000, -82.7485,  2.0425),
            (50.0000,  3.1571, -77.2803, 50.0000,  0.0000, -82.7485,  2.8615),
            (50.0000,  2.8361, -74.0200, 50.0000,  0.0000, -82.7485,  3.4412),
            (50.0000, -1.3802, -84.2814, 50.0000,  0.0000, -82.7485,  1.0000),
            (50.0000, -1.1848, -84.8006, 50.0000,  0.0000, -82.7485,  1.0000),
            (50.0000, -0.9009, -85.5211, 50.0000,  0.0000, -82.7485,  1.0000),
            (50.0000,  0.0000,   0.0000, 50.0000, -1.0000,   2.0000,  2.3669),
            (50.0000, -1.0000,   2.0000, 50.0000,  0.0000,   0.0000,  2.3669),
            (50.0000,  2.4900,  -0.0010, 50.0000, -2.4900,   0.0009,  7.1792),
            (50.0000,  2.5000,   0.0000, 50.0000,  0.0000,  -2.5000,  4.3065),
            (50.0000,  2.5000,   0.0000, 73.0000, 25.0000, -18.0000, 27.1492),
            (50.0000,  2.5000,   0.0000, 61.0000, -5.0000,  29.0000, 22.8977),
            (50.0000,  2.5000,   0.0000, 56.0000,-27.0000,  -3.0000, 31.9030),
            (50.0000,  2.5000,   0.0000, 58.0000, 24.0000,  15.0000, 19.4535),
            (60.2574,-34.0099,  36.2677, 60.4626,-34.1751,  39.4387,  1.2644),
            (63.0109,-31.0961,  -5.8663, 62.8187,-29.7946,  -4.0864,  1.2630),
            (61.2901,  3.7196,  -5.3901, 61.4292,  2.2480,  -4.9620,  1.8731),
            (35.0831,-44.1164,   3.7933, 35.0232,-40.0716,   1.5901,  1.8645),
            (22.7233, 20.0904, -46.6940, 23.0331, 14.9730, -42.5619,  2.0373),
            (36.4612, 47.8580,  18.3852, 36.2715, 50.5065,  21.2231,  1.4146),
            (90.8027, -2.0831,   1.4410, 91.1528, -1.6435,   0.0447,  1.4441),
        };

        Console.WriteLine("=== CIEDE2000 (Sharma/Wu/Dalal 2005 verification data) ===");
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            float got = ColorMath.DeltaE2000(
                new float3((float)c.l1, (float)c.a1, (float)c.b1),
                new float3((float)c.l2, (float)c.a2, (float)c.b2));
            Check($"pair {i + 1}", got, c.d, 0.0015);
        }

        Console.WriteLine();
        Console.WriteLine("=== sRGB round trip through Lab ===");
        // A neutral grey must have a*=b*=0 and a mid lightness.
        var lab = ColorMath.LinearToLab(new float3(0.2140f, 0.2140f, 0.2140f));
        Check("grey L*", lab.x, 53.39, 0.5);
        Check("grey a*", lab.y, 0.0, 0.02);
        Check("grey b*", lab.z, 0.0, 0.02);
        // Identical colours must have zero difference.
        var red = ColorMath.LinearToLab(new float3(1f, 0f, 0f));
        Check("identical dE", ColorMath.DeltaE2000(red, red), 0.0, 1e-4);

        Console.WriteLine();
        PackingTests.Run();
        _failures += PackingTests.Failures;

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }
}

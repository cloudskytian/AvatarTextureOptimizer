using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Public hooks for advanced users / third parties. / 高级用户与第三方扩展点。
    /// </summary>
    public static class AtoExtensionPoints
    {
        public static event Action<AtoGraph> AfterScan;
        public static event Action<List<AtoIsland>> AfterIslands;
        public static event Action<List<AtoAtlasResult>> AfterAtlas;
        public static event Func<Material, AtoShaderAnalyzer.MaterialAnalysis?> OverrideShaderAnalysis;
        public static event Func<Texture2D, bool?> OverrideWhitelist;

        internal static void RaiseAfterScan(AtoGraph g) => AfterScan?.Invoke(g);
        internal static void RaiseAfterIslands(List<AtoIsland> i) => AfterIslands?.Invoke(i);
        internal static void RaiseAfterAtlas(List<AtoAtlasResult> a) => AfterAtlas?.Invoke(a);

        internal static bool TryOverrideShader(Material m, out AtoShaderAnalyzer.MaterialAnalysis analysis)
        {
            analysis = default;
            var r = OverrideShaderAnalysis?.Invoke(m);
            if (r.HasValue) { analysis = r.Value; return true; }
            return false;
        }

        internal static bool TryOverrideWhitelist(Texture2D t, out bool wl)
        {
            wl = false;
            var r = OverrideWhitelist?.Invoke(t);
            if (r.HasValue) { wl = r.Value; return true; }
            return false;
        }
    }
}

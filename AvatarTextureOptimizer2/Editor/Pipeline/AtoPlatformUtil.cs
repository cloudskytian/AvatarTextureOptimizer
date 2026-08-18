using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoPlatformUtil
    {
        public static AtoPlatform Detect(BuildContext ctx)
        {
            try
            {
                var name = ctx?.PlatformProvider?.QualifiedName ?? "";
                if (name.IndexOf("android", System.StringComparison.OrdinalIgnoreCase) >= 0) return AtoPlatform.Android;
                if (name.IndexOf("ios", System.StringComparison.OrdinalIgnoreCase) >= 0) return AtoPlatform.iOS;
                if (name.IndexOf("vrchat", System.StringComparison.OrdinalIgnoreCase) >= 0) return AtoPlatform.PC;
            }
            catch { /* ignore */ }

            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                default: return AtoPlatform.PC;
            }
        }

        public static int MaxAtlasEdge(AtoPlatform p) => p == AtoPlatform.PC ? 8192 : 4096;
    }
}

using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class PlatformUtil
    {
        public static AtoPlatform Detect()
        {
            var g = EditorUserBuildSettings.activeBuildTarget;
            switch (g)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                default: return AtoPlatform.PC;
            }
        }
    }
}

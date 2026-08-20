using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Detect current build platform. / 检测当前构建平台。
    /// </summary>
    public static class AtoPlatformUtil
    {
        public static AtoPlatform Current()
        {
            var t = EditorUserBuildSettings.activeBuildTarget;
            switch (t)
            {
                case BuildTarget.Android: return AtoPlatform.Android;
                case BuildTarget.iOS: return AtoPlatform.iOS;
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                    return AtoPlatform.PC;
                default:
                    return AtoPlatform.PC;
            }
        }

        public static int MaxAtlasEdge(AtoPlatform platform)
        {
            return platform == AtoPlatform.PC ? 8192 : 4096;
        }

        public static bool IsEditorOnly(Transform t)
        {
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }
            return false;
        }

        public static bool HasVrcAvatarDescriptor(GameObject go)
        {
            if (go == null) return false;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                if (c.GetType().Name == "VRCAvatarDescriptor") return true;
            }
            return false;
        }
    }
}

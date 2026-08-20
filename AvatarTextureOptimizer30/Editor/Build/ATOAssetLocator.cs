// ATOAssetLocator.cs — 包内资源定位器 / Package asset locator.
// 说明：通过 asmdef 定位包根路径，供 ComputeShader 等包内资产加载使用（适配 Packages/ 与 Assets/ 安装方式）。
// Note: locates the package root via the asmdef, used to load in-package assets like the ComputeShader
// (works both for Packages/ and Assets/ installations).

using System;
using UnityEditor;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>包内资源定位。/ Package asset locator.</summary>
    internal static class ATOAssetLocator
    {
        private static string _packageRoot;

        /// <summary>查找包内资产的路径（相对包根）。/ Find an in-package asset path (relative to the package root).</summary>
        public static string Find(string relativePath)
        {
            if (string.IsNullOrEmpty(_packageRoot)) _packageRoot = FindPackageRoot();
            if (string.IsNullOrEmpty(_packageRoot)) return null;
            var path = _packageRoot + "/" + relativePath;
            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null ? path : null;
        }

        private static string FindPackageRoot()
        {
            foreach (var guid in AssetDatabase.FindAssets("Fosa.AvatarTextureOptimizer.Editor t:AssemblyDefinitionAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                const string suffix = "/Editor/Fosa.AvatarTextureOptimizer.Editor.asmdef";
                if (path.EndsWith(suffix, StringComparison.Ordinal))
                    return path.Substring(0, path.Length - suffix.Length);
            }
            foreach (var guid in AssetDatabase.FindAssets("Fosa.AvatarTextureOptimizer t:AssemblyDefinitionAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                const string suffix = "/Runtime/Fosa.AvatarTextureOptimizer.asmdef";
                if (path.EndsWith(suffix, StringComparison.Ordinal))
                    return path.Substring(0, path.Length - suffix.Length);
            }
            return null;
        }
    }
}

using System;
using System.IO;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEditor;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>
    /// 生成资产保存器：把生成的贴图/材质保存到 NDMF 临时资产目录（AvatarProcessor.TemporaryAssetRoot），
    /// 命名一律以 ATO_ 开头；构建成功后由 NDMF CleanTemporaryAssets 清理；取消/出错时保留在磁盘。
    /// </summary>
    public static class AssetSaver
    {
        public static string TempRoot
        {
            get
            {
                // AvatarProcessor.TemporaryAssetRoot 是 internal；用反射读取（NDMF 构建成功后由
                // CleanTemporaryAssets 清理该目录；取消/出错时不清理 → 临时资产保留在磁盘，符合需求）
                try
                {
                    var type = typeof(nadena.dev.ndmf.AvatarProcessor);
                    var field = type.GetField("TemporaryAssetRoot",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    if (field != null)
                    {
                        var val = field.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(val)) return val;
                    }
                }
                catch (Exception) { }
                return "Assets/ATO_Generated";
            }
        }

        public static string EnsureFolder(string sub = "")
        {
            var root = TempRoot;
            var target = string.IsNullOrEmpty(sub) ? root : root + "/" + sub;
            if (!AssetDatabase.IsValidFolder(root))
            {
                // 逐级创建
                var parts = root.Split('/');
                string cur = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    cur += "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(cur))
                    {
                        AssetDatabase.CreateFolder(Path.GetDirectoryName(cur), Path.GetFileName(cur));
                    }
                }
            }
            if (!string.IsNullOrEmpty(sub) && !AssetDatabase.IsValidFolder(target))
            {
                AssetDatabase.CreateFolder(root, sub);
            }
            return target;
        }

        private static int _counter;

        public static string NextAssetPath(string extension, string prefix = "ATO_")
        {
            var folder = EnsureFolder();
            return $"{folder}/{prefix}{DateTime.Now:yyyyMMddHHmmss}_{_counter++}{extension}";
        }

        public static Texture2D SaveTexture(Texture2D tex, string path, ATOLogger logger)
        {
            try
            {
                var bytes = tex.EncodeToPNG();
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                return asset;
            }
            catch (Exception e)
            {
                logger.Error($"Failed to save texture asset '{path}': {e.Message}");
                return null;
            }
        }

        public static Material SaveMaterial(Material mat, string path, ATOLogger logger)
        {
            try
            {
                AssetDatabase.CreateAsset(mat, path);
                return AssetDatabase.LoadAssetAtPath<Material>(path);
            }
            catch (Exception e)
            {
                logger.Error($"Failed to save material asset '{path}': {e.Message}");
                return null;
            }
        }

        public static void DeleteGeneratedFolder()
        {
            var root = TempRoot;
            if (AssetDatabase.IsValidFolder(root))
            {
                AssetDatabase.DeleteAsset(root);
            }
        }
    }
}

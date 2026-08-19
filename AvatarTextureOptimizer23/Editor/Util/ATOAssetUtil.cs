using System.IO;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    internal static class ATOAssetUtil
    {
        public static string EnsureTempFolder(BuildContext context, string avatarName)
        {
            var safe = Sanitize(avatarName);
            var folder = $"Assets/ATO_Generated/{safe}";
            EnsureFolder(folder);
            return folder;
        }

        public static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(cur, parts[i]);
                }
                cur = next;
            }
        }

        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Avatar";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        public static T CloneIfPersistent<T>(T obj, BuildContext ctx) where T : Object
        {
            if (obj == null) return null;
            if (ctx != null && ctx.AssetSaver != null && ctx.AssetSaver.IsTemporaryAsset(obj))
                return obj;
            var clone = Object.Instantiate(obj);
            clone.name = obj.name;
            ctx?.AssetSaver?.SaveAsset(clone);
            ObjectRegistry.RegisterReplacedObject(obj, clone);
            return clone;
        }

        public static long EstimateTextureBytes(Texture tex)
        {
            if (tex == null) return 0;
            var w = tex.width;
            var h = tex.height;
            // Rough RGBA32 estimate; used only for reports. / 粗略按 RGBA32 估，仅用于报告。
            long bytes = (long)w * h * 4;
            if (tex.mipmapCount > 1) bytes = bytes * 4 / 3;
            return bytes;
        }
    }
}

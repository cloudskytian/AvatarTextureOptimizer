using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 通用工具：路径解析、动画槽索引解析等（供收集器/分析器/去重共用）。
    /// Common utilities shared across collector / analyzer / dedup.
    /// </summary>
    public static class ATOUtil
    {
        /// <summary>解析动画属性名 "m_Materials.Array.data[i]" 中的槽索引。</summary>
        public static int ParseSlotIndex(string propName)
        {
            const string prefix = "m_Materials.Array.data[";
            if (string.IsNullOrEmpty(propName) || !propName.StartsWith(prefix)) return -1;
            int end = propName.IndexOf(']', prefix.Length);
            if (end < 0) return -1;
            return int.TryParse(propName.Substring(prefix.Length, end - prefix.Length), out var idx) ? idx : -1;
        }

        /// <summary>按动画路径查找 GameObject。</summary>
        public static GameObject FindAtPath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            if (root == null) return null;
            var t = root.transform.Find(path);
            return t != null ? t.gameObject : null;
        }

        /// <summary>计算 Transform 相对根物体的动画路径。</summary>
        public static string GetPath(Transform root, Transform t)
        {
            if (t == null || root == null) return "";
            if (t == root) return "";
            var parts = new System.Collections.Generic.List<string>();
            while (t != root && t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }
    }
}

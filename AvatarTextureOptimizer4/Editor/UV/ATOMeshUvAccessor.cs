// Avatar Texture Optimizer (ATO)
// Safe multi-channel mesh UV read/write.
// 安全的多通道网格 UV 读写。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Reads/writes mesh UVs for channels 0..7 without assuming Vector2 (UV3+ are Vector4-capable).
    /// 读写 0..7 通道的网格 UV，兼容 Vector4（UV3+）。
    /// </summary>
    public static class ATOMeshUvAccessor
    {
        public static bool TryGetUv(Mesh mesh, int channel, out Vector2[] uv)
        {
            uv = null;
            if (mesh == null || channel < 0 || channel >= ATOConstants.MaxUvChannels) return false;
            var list = new List<Vector3>();
            try
            {
                mesh.GetUVs(channel, list);
            }
            catch (System.Exception)
            {
                return false;
            }
            if (list.Count == 0) return false;
            uv = new Vector2[list.Count];
            for (int i = 0; i < list.Count; i++) uv[i] = new Vector2(list[i].x, list[i].y);
            return true;
        }

        public static bool TrySetUv(Mesh mesh, int channel, Vector2[] uv)
        {
            if (mesh == null || uv == null || channel < 0 || channel >= ATOConstants.MaxUvChannels) return false;
            var list = new List<Vector3>(uv.Length);
            for (int i = 0; i < uv.Length; i++) list.Add(new Vector3(uv[i].x, uv[i].y, 0f));
            try
            {
                mesh.SetUVs(channel, list);
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Copy the original UVs of `original` channel to `saved` channel (for AAO evacuation).
        /// 把 `original` 通道的原始 UV 复制到 `saved` 通道（供 AAO 疏散使用）。
        /// </summary>
        public static bool CopyChannel(Mesh mesh, int original, int saved)
        {
            if (!TryGetUv(mesh, original, out var uv)) return false;
            return TrySetUv(mesh, saved, uv);
        }
    }
}

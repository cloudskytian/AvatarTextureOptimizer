using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtoHash
    {
        public static string Bytes(byte[] data)
        {
            using var md5 = MD5.Create();
            var h = md5.ComputeHash(data);
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string Combine(params string[] parts)
        {
            return Bytes(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        }

        public static string Color32Span(Color32[] px)
        {
            // Hash a stride sample for speed, plus length. Full hash is too heavy for 8k.
            // 对像素做步进采样以控制成本，8K 全量哈希过重。
            var n = px.Length;
            var step = Math.Max(1, n / 65536);
            var buf = new byte[(n / step + 1) * 4 + 8];
            buf[0] = (byte)(n & 255);
            buf[1] = (byte)((n >> 8) & 255);
            buf[2] = (byte)((n >> 16) & 255);
            buf[3] = (byte)((n >> 24) & 255);
            var o = 8;
            for (var i = 0; i < n; i += step)
            {
                var c = px[i];
                buf[o++] = c.r; buf[o++] = c.g; buf[o++] = c.b; buf[o++] = c.a;
            }
            Array.Resize(ref buf, o);
            return Bytes(buf);
        }
    }
}

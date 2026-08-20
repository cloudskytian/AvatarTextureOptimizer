// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.ShaderAnalysis
{
    /// <summary>
    /// Heuristically resolves, for each texture property of a shader, which mesh UV
    /// channel (TEXCOORDn) it is sampled with, by parsing the shader source:
    ///
    ///  1. Map appdata/vertex struct fields to TEXCOORDn semantics
    ///     (e.g. `float2 uv : TEXCOORD0;`, `float2 uv2 : TEXCOORD1;`).
    ///  2. Trace the UV variable through the vertex → fragment struct (v2f).
    ///  3. Find `tex2D(_Prop, uvVar)` / `SAMPLE_TEXTURE2D(_Prop, sampler_Prop, uvVar)`
    ///     calls to bind a property to its uvVar.
    ///  4. Map uvVar → TEXCOORDn → UV channel (0..7).
    ///
    /// Defaults to UV0 when a binding cannot be resolved. This is a best-effort parser
    /// covering standard Unity / URP / HDRP / lilToon conventions; anything unresolvable
    /// falls back to UV0 and is flagged (not whitelisted).
    ///
    /// 启发式地解析着色器每个贴图属性采样的网格 UV 通道（TEXCOORDn）：
    ///  1. 将 appdata/vertex 结构体字段映射到 TEXCOORDn 语义。
    ///  2. 沿 vertex → fragment 结构体（v2f）追踪 UV 变量。
    ///  3. 通过 tex2D(_Prop, uvVar) / SAMPLE_TEXTURE2D 调用绑定属性与其 uvVar。
    ///  4. 将 uvVar → TEXCOORDn → UV 通道（0..7）。
    /// 无法解析时回退 UV0 并标记（不白名单）。
    /// </summary>
    public static class ATOUVChannelAnalyzer
    {
        private static readonly Regex TexcoordField =
            new Regex(@"(\w+)\s*:\s*TEXCOORD(\d)", RegexOptions.Compiled);

        private static readonly Regex Tex2D =
            new Regex(@"tex2D(?:lod)?\s*\(\s*(\w+)\s*,\s*(\w+)", RegexOptions.Compiled);

        private static readonly Regex SampleTex2D =
            new Regex(@"SAMPLE_TEXTURE2D\s*\(\s*(\w+)\s*,\s*(\w+)\s*,\s*(\w+)", RegexOptions.Compiled);

        /// <summary>
        /// Resolve the UV channel (0..7) for each texture property name.
        /// 解析每个贴图属性名的 UV 通道（0..7）。
        /// </summary>
        public static Dictionary<string, int> ResolveChannels(Shader shader)
        {
            var result = new Dictionary<string, int>();
            if (shader == null) return result;

            string src = ReadShaderSource(shader);
            if (string.IsNullOrEmpty(src))
            {
                // Fallback: everything UV0. 回退：全部 UV0。
                return result;
            }

            // 1) vertex struct field → texcoord index. 顶点结构体字段 → texcoord 序号。
            var fieldToChannel = new Dictionary<string, int>();
            foreach (Match m in TexcoordField.Matches(src))
            {
                string field = m.Groups[1].Value;
                int ch = int.Parse(m.Groups[2].Value);
                if (ch >= 0 && ch <= 7) fieldToChannel[field] = ch;
            }

            // 2) fragment struct fields often reuse the same names; map by name.
            //    片段结构体字段通常同名；按名字映射。

            // 3) property → uv var. 属性 → uv 变量。
            var propToUvVar = new Dictionary<string, string>();
            foreach (Match m in Tex2D.Matches(src))
                propToUvVar[m.Groups[1].Value] = m.Groups[2].Value;
            foreach (Match m in SampleTex2D.Matches(src))
                propToUvVar[m.Groups[1].Value] = m.Groups[3].Value;

            // 4) resolve. 解析。
            foreach (var kv in propToUvVar)
            {
                int channel = ResolveChannel(kv.Value, fieldToChannel);
                result[kv.Key] = channel;
            }

            return result;
        }

        private static int ResolveChannel(string uvVar, Dictionary<string, int> fieldToChannel)
        {
            // Direct field name match (e.g. "uv" or "uv_MainTex" → contains "uv").
            // 直接字段名匹配（如 "uv" 或 "uv_MainTex"）。
            string norm = uvVar.Replace("_", "").ToLowerInvariant();

            // Standard names. 标准名称。
            if (norm == "uv" || norm == "uv0") return 0;
            if (norm == "uv1" || norm == "uv2nd" || norm == "uvsecond") return 1;
            if (norm == "uv2" || norm == "uv3rd" || norm == "uvthird") return 2;
            if (norm == "uv3") return 3;
            if (norm == "uv4") return 4;
            if (norm == "uv5") return 5;
            if (norm == "uv6") return 6;
            if (norm == "uv7") return 7;

            // Field declared with TEXCOORDn whose name is a prefix of uvVar.
            // 字段声明带 TEXCOORDn 且名称是 uvVar 前缀。
            foreach (var kv in fieldToChannel)
            {
                string f = kv.Key.Replace("_", "").ToLowerInvariant();
                if (norm.StartsWith(f) || f.StartsWith(norm)) return kv.Value;
            }

            return 0;
        }

        private static string ReadShaderSource(Shader shader)
        {
            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader")) return null;

            try { return File.ReadAllText(path); }
            catch { return null; }
        }
    }
}

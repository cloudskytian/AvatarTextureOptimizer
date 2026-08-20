// Material patching: ONLY texture references change (never any other shader parameter).
// Every mutated material is an avatar-local clone (see UsageGraph.ResolveClone).
// 材质补丁：仅修改贴图引用（绝不动其他任何着色器参数）；被修改材质均为 Avatar 本地克隆。

using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    internal static class MaterialPatcher
    {
        /// <summary>source texture -> final replacement (page or whole-scaled).
        /// 源贴图 -> 最终替换（图集页或整图缩放）。</summary>
        internal static readonly Dictionary<Texture2D, Texture2D> Replacement =
            new Dictionary<Texture2D, Texture2D>();

        internal static void Patch(AtoSession s)
        {
            using var _ = ATOLog.Scope("PatchMaterials");
            Replacement.Clear();

            // 1) register atlas pages / 登记图集页
            if (s.component.generateAtlas)
                foreach (var kv in AtlasBuilder.Placement)
                {
                    var (res, kind, slot) = kv.Value;
                    if (res.pages.TryGetValue(kind, out var pages) && slot < pages.Count)
                        Replacement[kv.Key] = pages[slot];
                }

            // 2) whole-image scaled copies / 整图缩放副本
            foreach (var kv in QualityEvaluator.WholeScale)
            {
                if (Replacement.ContainsKey(kv.Key)) continue;
                if (!s.texInfos.TryGetValue(kv.Key, out var ti) || ti.whitelisted) continue;
                Replacement[kv.Key] = WholeScaler.Create(s, kv.Key, kv.Value, ti);
            }

            // 3) apply to renderer materials / 应用到渲染器材质
            int touched = 0;
            foreach (var ri in s.renderers)
            {
                var arr = ri.renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    var m = UsageGraph.ResolveClone(s, arr[i]);
                    if (m == null) continue;
                    if (m != arr[i]) { arr[i] = m; changed = true; }
                    if (ReplaceTextures(s, m)) { touched++; changed = true; }
                }

                if (changed) ri.renderer.sharedMaterials = arr;
            }

            // 4) animations: material refs -> clones; texture refs -> replacements
            //    动画：材质引用换克隆；贴图引用换替换结果
            UsageGraph.ApplyMaterialMap(s);
            AnimationAnalyzer.ReplaceTextures(s, Replacement);

            s.atlasedMaterials = touched;
            ATOLog.Info($"material patch: {touched} materials, {Replacement.Count} texture replacements");
        }

        private static bool ReplaceTextures(AtoSession s, Material m)
        {
            if (m == null || m.shader == null) return false;
            bool changed = false;
            for (int i = 0; i < m.shader.GetPropertyCount(); i++)
            {
                if (m.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                string prop = m.shader.GetPropertyName(i);
                var t = m.GetTexture(prop);
                if (t is Texture2D t2 && Replacement.TryGetValue(t2, out var nt) && nt != t2)
                {
                    m.SetTexture(prop, nt);
                    changed = true;
                    ObjectRegistry.RegisterReplacedObject(t2, nt);
                }
            }

            return changed;
        }
    }

    /// <summary>Whole-image scaler (non-atlas path). / 整图缩放器（非图集路径）。</summary>
    internal static class WholeScaler
    {
        internal static Texture2D Create(AtoSession s, Texture2D src, float scale, TexInfo ti)
        {
            var cp = TexturePixels.Get(src, ti.category == AtoTexCategory.Normal);
            int w = Mathf.Max(1, Mathf.RoundToInt(src.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(src.height * scale));

            bool linear = ti.category == AtoTexCategory.Normal || ti.category == AtoTexCategory.Gray;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32,
                s.settings.GetCategory(ti.category).mipsAndStreaming, linear)
            {
                name = "ATO_" + src.name,
                wrapMode = src.wrapMode,
                filterMode = src.filterMode,
                anisoLevel = src.anisoLevel,
            };

            if (w == src.width && h == src.height)
            {
                tex.SetPixels32(cp.pixels);
            }
            else
            {
                var buf = new Color32[w * h];
                using var srcN = new Unity.Collections.NativeArray<Color32>(cp.pixels, Unity.Collections.Allocator.TempJob);
                using var dstN = new Unity.Collections.NativeArray<Color32>(buf, Unity.Collections.Allocator.TempJob);
                using var sizeN = new Unity.Collections.NativeArray<Unity.Mathematics.int2>(1, Unity.Collections.Allocator.TempJob);
                sizeN[0] = new Unity.Mathematics.int2(w, h);
                var job = new DownsampleJob
                {
                    src = srcN, srcW = src.width, srcH = src.height,
                    region = new Unity.Mathematics.int4(0, 0, src.width, src.height),
                    premultiply = ti.hasAlphaContent,
                    srgb = cp.srgb && ti.category != AtoTexCategory.Normal,
                    dst = dstN, dstSize = sizeN,
                };
                job.Schedule().Complete();
                dstN.CopyTo(buf);

                if (ti.category == AtoTexCategory.Normal)
                    Renormalize(buf, cp.normalLayout);
                tex.SetPixels32(buf);
            }

            tex.Apply(s.settings.GetCategory(ti.category).mipsAndStreaming);
            s.ctx.AssetSaver.SaveAsset(tex);
            ObjectRegistry.RegisterReplacedObject(src, tex);
            ATOLog.DebugL($"whole-scale {src.name} -> {w}x{h}");
            return tex;
        }

        private static void Renormalize(Color32[] buf, NormalLayout layout)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                var c = buf[i];
                float x = layout == NormalLayout.AG ? c.a / 255f : c.r / 255f;
                float y = c.g / 255f;
                var xy = new Unity.Mathematics.float2(x, y) * 2f - 1f;
                float z = Unity.Mathematics.math.sqrt(Unity.Mathematics.math.max(0f, 1f - Unity.Mathematics.math.dot(xy, xy)));
                var n = Unity.Mathematics.math.normalizesafe(new Unity.Mathematics.float3(xy.x, xy.y, z), new Unity.Mathematics.float3(0, 0, 1));
                byte bx = (byte)Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255);
                byte by = (byte)Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255);
                byte bz = (byte)Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255);
                buf[i] = new Color32(bx, by, bz, 255); // RG encoding; swizzle later / 统一RG，后续再转
            }
        }
    }
}

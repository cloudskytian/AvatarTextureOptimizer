using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>
    /// 处理后去重：材质按内容+参数去重、贴图按像素内容去重，并更新渲染器与动画引用。
    /// </summary>
    public sealed class PostDeduplicator
    {
        private readonly ATOLogger _logger;
        private readonly AnimationPatcher _patcher;

        public PostDeduplicator(ATOLogger logger, AnimationPatcher patcher)
        {
            _logger = logger;
            _patcher = patcher;
        }

        /// <summary>去重全部渲染器材质，返回被替换的 (old → new) 供动画修补。</summary>
        public Dictionary<Material, Material> DeduplicateMaterials(IEnumerable<Renderer> renderers, bool enabled)
        {
            var result = new Dictionary<Material, Material>();
            if (!enabled) return result;

            var byKey = new Dictionary<string, Material>();
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    string key = MaterialKey(m);
                    if (byKey.TryGetValue(key, out var canonical))
                    {
                        if (!ReferenceEquals(canonical, m))
                        {
                            mats[i] = canonical;
                            result[m] = canonical;
                            _logger.VerboseLog($"Material dedup: '{m.name}' -> '{canonical.name}'");
                        }
                    }
                    else
                    {
                        byKey[key] = m;
                    }
                }
                r.sharedMaterials = mats;
            }

            foreach (var kv in result) _patcher.MaterialReplacements[kv.Key] = kv.Value;
            if (result.Count > 0) _logger.Info($"Post-dedup: merged {result.Count} material(s).");
            return result;
        }

        /// <summary>贴图去重（像素内容哈希比较）。返回 (old → new)。</summary>
        public Dictionary<Texture, Texture> DeduplicateTextures(IEnumerable<Texture> textures, TextureCache cache, bool enabled)
        {
            var result = new Dictionary<Texture, Texture>();
            if (!enabled) return result;

            var byHash = new Dictionary<int, Texture>();
            foreach (var tex in textures)
            {
                if (tex == null || !(tex is Texture2D t2d)) continue;
                if (!t2d.isReadable)
                {
                    // 跳过不可读（读不了内容就不去重，安全）
                    continue;
                }
                try
                {
                    var px = cache.GetPixels(tex, out _, out _);
                    int hash = QuickHash(px);
                    if (byHash.TryGetValue(hash, out var canonical))
                    {
                        var cpx = cache.GetPixels(canonical, out _, out _);
                        if (cpx.Length == px.Length && PixelsEqual(cpx, px) && !ReferenceEquals(canonical, tex))
                        {
                            result[tex] = canonical;
                            _logger.VerboseLog($"Texture dedup: '{tex.name}' -> '{canonical.name}'");
                        }
                    }
                    else
                    {
                        byHash[hash] = tex;
                    }
                }
                catch (Exception)
                {
                    // 忽略读取失败
                }
            }
            return result;
        }

        private static string MaterialKey(Material m)
        {
            var sb = new System.Text.StringBuilder(128);
            sb.Append(m.shader != null ? m.shader.name : "<null>");
            sb.Append('|');
            if (m.shader != null)
            {
                int count = m.shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    var name = m.shader.GetPropertyName(i);
                    var type = m.shader.GetPropertyType(i);
                    sb.Append(name).Append('=');
                    try
                    {
                        switch (type)
                        {
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Int:
                            case ShaderPropertyType.Range:
                                sb.Append(m.GetFloat(name).ToString("R")); break;
                            case ShaderPropertyType.Color:
                            case ShaderPropertyType.Vector:
                                sb.Append(m.GetVector(name).ToString("R")); break;
                            case ShaderPropertyType.Texture:
                                var t = m.GetTexture(name);
                                sb.Append(t != null ? t.GetInstanceID().ToString() : "0"); break;
                        }
                    }
                    catch (Exception) { }
                    sb.Append(';');
                }
            }
            sb.Append(m.renderQueue);
            return sb.ToString();
        }

        private static int QuickHash(Color32[] px)
        {
            unchecked
            {
                int h = 17;
                int step = Math.Max(1, px.Length / 4096);
                for (int i = 0; i < px.Length; i += step)
                {
                    var c = px[i];
                    h = h * 31 + c.r; h = h * 31 + c.g; h = h * 31 + c.b; h = h * 31 + c.a;
                }
                return h;
            }
        }

        private static bool PixelsEqual(Color32[] a, Color32[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                var ca = a[i]; var cb = b[i];
                if (ca.r != cb.r || ca.g != cb.g || ca.b != cb.b || ca.a != cb.a) return false;
            }
            return true;
        }
    }
}

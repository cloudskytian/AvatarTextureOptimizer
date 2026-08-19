// Stage7b_Dedup — texture/atlas dedup, material dedup, slot unification / 贴图·图集去重、材质去重、槽统一
// Texture dedup: exact byte-hash of exported PNGs (+ dimensions) — generated files are byte-deterministic.
// Material dedup: full content fingerprint (shader + sorted props incl. textures + keywords).
// Slot merge: identical opaque slots already share ONE material after dedup (renderer-level slot
// unification); mesh-topology submesh merging is deliberately NOT done here — AAO runs after us and
// owns that class of optimization (recorded deviation).<br>
// 贴图去重：对导出 PNG 做精确字节哈希（导出内容确定性）；材质去重：完整内容指纹（着色器+排序属性+关键字）。
// 槽合并：去重后相同不透明槽已共享同一材质（渲染器级槽统一）；子网格拓扑合并刻意不做——AAO 在我们之后运行、
// 负责本类优化（已记录偏差）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class Stage7b_Dedup
    {
        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            if (pipe.settings.dedupTextures) TextureDedup(pipe);
            if (pipe.settings.dedupMaterials) MaterialDedup(pipe);
            ATOEvents.Raise("dedup", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("dedup", pipe);
        }

        // ---------------------------------------------------------------- texture dedup
        private static void TextureDedup(ATOPipeContext pipe)
        {
            // outputs: atlas plane textures + whole-texture replacements / 产物：图集平面 + 整图替换
            var outs = new List<Texture2D>();
            foreach (var a in pipe.atlases)
                foreach (var pl in a.planes.Values)
                    if (pl.texture != null) outs.Add(pl.texture);
            foreach (var kv in pipe.wholeTexReplacement)
                if (kv.Value != null) outs.Add(kv.Value);

            var byHash = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var remap = new Dictionary<Texture2D, Texture2D>();
            foreach (var t in outs)
            {
                string h = HashOf(t);
                if (h == null) continue;
                if (byHash.TryGetValue(h, out var keep)) { if (!ReferenceEquals(keep, t)) remap[t] = keep; }
                else byHash[h] = t;
            }
            if (remap.Count == 0) { ATOLog.V("texture dedup: no duplicates"); return; }

            // rewrite replacement tables / 改写替换表
            foreach (var a in pipe.atlases)
                foreach (var pl in a.planes.Values)
                    if (pl.texture != null && remap.TryGetValue(pl.texture, out var keep)) pl.texture = keep;
            var wholeKeys = pipe.wholeTexReplacement.Keys.ToList();
            foreach (var k in wholeKeys)
            {
                var v = pipe.wholeTexReplacement[k];
                if (v != null && remap.TryGetValue(v, out var keep)) pipe.wholeTexReplacement[k] = keep;
            }
            // rewrite cloned materials / 改写克隆材质
            foreach (var nm in pipe.materialReplacements.Values)
                RetargetTextures(nm, remap);

            // delete orphan generated assets / 删除被去重的生成资产
            foreach (var dead in remap.Keys)
            {
                var path = AssetDatabase.GetAssetPath(dead);
                if (!string.IsNullOrEmpty(path) && path.StartsWith(Stage5_Bake.TempDir, StringComparison.Ordinal))
                    AssetDatabase.DeleteAsset(path);
            }
            ATOLog.Info(ATOL10n.T("ato.log.tex_dedup", remap.Count));
        }

        private static void RetargetTextures(Material m, Dictionary<Texture2D, Texture2D> remap)
        {
            if (m == null || m.shader == null) return;
            int cnt = m.shader.GetPropertyCount();
            for (int i = 0; i < cnt; i++)
            {
                if (m.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var pn = m.shader.GetPropertyName(i);
                if (m.GetTexture(pn) is Texture2D t && remap.TryGetValue(t, out var keep)) m.SetTexture(pn, keep);
            }
        }

        /// <summary>Deterministic content hash: dims + PNG file bytes (exports are deterministic). / 确定性内容哈希：尺寸+PNG字节。</summary>
        private static string HashOf(Texture2D t)
        {
            if (t == null) return null;
            var path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); } catch { return null; }
            ulong h = 1469598103934665603UL;
            foreach (var b in bytes) { h ^= b; h *= 1099511628211UL; }
            ulong h2 = 1099511628211UL;
            foreach (var b in bytes) { h2 = h2 * 1099511628211UL + b; }
            return $"{t.width}x{t.height}|{h:x16}|{h2:x16}|{bytes.Length}";
        }

        // ---------------------------------------------------------------- material dedup
        private static void MaterialDedup(ATOPipeContext pipe)
        {
            if (pipe.materialReplacements.Count == 0) return;
            var byFinger = new Dictionary<string, Material>(StringComparer.Ordinal);
            var remap = new Dictionary<Material, Material>();   // clone -> canonical clone
            foreach (var nm in pipe.materialReplacements.Values)
            {
                var f = Fingerprint(nm);
                if (byFinger.TryGetValue(f, out var keep)) { if (!ReferenceEquals(keep, nm)) remap[nm] = keep; }
                else byFinger[f] = nm;
            }
            if (remap.Count == 0) { ATOLog.V("material dedup: no duplicates"); return; }

            // collapse replacement map / 收敛替换映射
            var keys = pipe.materialReplacements.Keys.ToList();
            foreach (var k in keys)
            {
                var v = pipe.materialReplacements[k];
                while (v != null && remap.TryGetValue(v, out var next)) v = next;
                if (v != null) pipe.materialReplacements[k] = v;
            }
            // re-apply renderer slots with canonical materials / 用规范材质重扫槽位
            foreach (var r in pipe.rendererStates.Keys)
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && remap.TryGetValue(mats[i], out var keep)) { mats[i] = keep; changed = true; }
                if (changed) r.sharedMaterials = mats;
            }
            // delete orphaned duplicate materials / 删除重复材质
            foreach (var dead in remap.Keys)
            {
                var path = AssetDatabase.GetAssetPath(dead);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
            }
            // identical opaque slots now reference one shared material (slot unification; no topology change)
            // 相同材质槽已统一为同一引用（槽统一，不改拓扑）
            ATOLog.Info(ATOL10n.T("ato.log.mat_dedup", remap.Count));
        }

        private static string Fingerprint(Material m)
        {
            var sb = new StringBuilder(256);
            sb.Append(m.shader != null ? AssetDatabase.GetAssetPath(m.shader) : "<scene-shader>");
            if (m.shader != null)
            {
                var props = new List<(string name, UnityEngine.Rendering.ShaderPropertyType type)>();
                int cnt = m.shader.GetPropertyCount();
                for (int i = 0; i < cnt; i++) props.Add((m.shader.GetPropertyName(i), m.shader.GetPropertyType(i)));
                props.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                foreach (var (name, type) in props)
                {
                    sb.Append('|').Append(name).Append('=');
                    switch (type)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            var t = m.GetTexture(name);
                            sb.Append(t != null ? AssetDatabase.GetAssetPath(t) + "#" + t.GetInstanceID() : "-");
                            var st = m.GetTextureScale(name); var off = m.GetTextureOffset(name);
                            sb.Append('^').Append(st).Append('^').Append(off);
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color: sb.Append(m.GetColor(name).ToString("R")); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range: sb.Append(m.GetFloat(name).ToString("R")); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector: sb.Append(m.GetVector(name).ToString("R")); break;
                        default: sb.Append('?'); break;
                    }
                }
                sb.Append("|kw:").Append(string.Join(",", m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal)));
                sb.Append("|rq:").Append(m.renderQueue);
            }
            return sb.ToString();
        }
    }
}

// Stage1_Discovery — collect renderers/slots/textures, whitelist, dedup / 收集渲染器/材质槽/贴图、白名单与去重
// Order (spec): skip EditorOnly → only enabled/animation-enabled renderers → collect slots including
// animation material/texture switches → resolve user whitelist → safety disqualification → dedup by
// pixels+import settings (whitelist infects dedup result).<br>
// 顺序（需求）：跳过 EditorOnly → 仅启用或被动画启用的渲染器 → 收集含动画切换的槽 → 解析用户白名单
// → 任一安全条件不满足即贴图级白名单 → 按像素+导入设置去重（白名单感染去重结果）。
using System;
using System.Collections.Generic;
using System.Text;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Fosa.ATO.Editor
{
    internal static class Stage1_Discovery
    {
        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            var root = ctx.AvatarRootObject;
            var comp = root.GetComponentInChildren<AvatarTextureOptimizer>(true);
            var desc = root.GetComponent<VRCAvatarDescriptor>();

            // ---------- user whitelist expansion / 用户白名单展开 ----------
            var userWhitelistTex = new HashSet<Texture2D>();
            foreach (var obj in comp.whitelist)
                if (obj != null) GatherTexturesFromObject(obj, userWhitelistTex);
            ATOLog.V($"user whitelist textures: {userWhitelistTex.Count}");
            pipe.CancelCheck(progress, ATOL10n.T("ato.stage.discovery"), 0.05f);

            // ---------- animation scan / 动画扫描 ----------
            var scan = AnimationScan.Run(root, desc);
            pipe.CancelCheck(progress, ATOL10n.T("ato.stage.discovery"), 0.2f);

            // ---------- renderer collection / 渲染器收集 ----------
            var renderers = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer || r is MeshRenderer) { }
                else continue;
                if (IsEditorOnlyChain(r.gameObject)) continue; // 跳过 EditorOnly
                var state = ComputeAnimState(r, root.transform, scan);
                pipe.rendererStates[r] = state;
                if (state.disabledAlways) continue; // 未被启用且无动画启用 → 跳过
                renderers.Add(r);
            }
            ATOLog.V($"renderers in scope: {renderers.Count}");
            if (renderers.Count == 0) { ATOLog.Warn(ATOL10n.T("ato.warn.no_renderers")); }

            // ---------- slot analysis / 材质槽分析 ----------
            var texUnsafe = new Dictionary<Texture2D, string>();   // global per-texture verdict / 贴图级安全裁决
            var texList = new List<Texture2D>();                  // every texture in scope / 范围内全部贴图
            void NoteUnsafe(Texture2D t, string reason)
            {
                if (t == null) return;
                if (!texUnsafe.ContainsKey(t)) texUnsafe[t] = reason;
                if (!texList.Contains(t)) texList.Add(t);
            }

            int ri = 0;
            foreach (var r in renderers)
            {
                ri++;
                if ((ri & 3) == 0) pipe.CancelCheck(progress, ATOL10n.T("ato.stage.discovery"), 0.2f + 0.6f * ri / renderers.Count);

                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : (r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
                if (mesh == null) { ATOLog.Warn(ATOL10n.T("ato.warn.no_mesh", r.name)); continue; }
                var mats = r.sharedMaterials;
                var path = RelativePath(r.transform, root.transform);

                int slotCount = Mathf.Min(mesh.subMeshCount, mats.Length);
                for (int i = 0; i < slotCount; i++)
                {
                    // material set over animation states / 动画各状态的材质集合
                    var set = new List<Material>();
                    if (mats[i] != null) set.Add(mats[i]);
                    if (scan.slotMaterialSets.TryGetValue(path + "#" + i, out var extra))
                        foreach (var m in extra) if (m != null && !set.Contains(m)) set.Add(m);

                    foreach (var mat in set)
                    {
                        var slots = ShaderAnalysis.Analyze(mat);
                        // alpha semantics incl. animated render-mode & cutoff / 透明语义（含动画 render mode 与 cutoff）
                        CollectAlphaStates(path, mat, scan, out var alphaMode, out var cutoff);

                        foreach (var s in slots)
                        {
                            var texObj = mat.GetTexture(s.property);
                            var tex = texObj as Texture2D;
                            if (texObj != null && tex == null) continue; // not Texture2D → out of scope entirely / 非 Texture2D 不处理
                            if (tex == null) continue;

                            if (!texList.Contains(tex)) texList.Add(tex);
                            bool safe = s.safe;
                            string reason = s.unsafeReason;

                            // animation-driven guard breach / 动画驱动的守卫破坏
                            if (safe && ScanAnimBreaksGuard(path, s.property, scan, out var why)) { safe = false; reason = why; }

                            string code = s.code;
                            if (userWhitelistTex.Contains(tex)) { safe = false; reason = "user whitelist / 用户白名单"; code = ShaderAnalysis.R_USER; }

                            // Only textures with an attestable mesh-UV mapping join UV groups. / 仅具有可证明网格UV映射的贴图才入UV组
                            bool joinsGroup = safe || code == ShaderAnalysis.R_ST || code == ShaderAnalysis.R_ROT
                                              || code == ShaderAnalysis.R_SHIFT || code == ShaderAnalysis.R_ANIM
                                              || code == ShaderAnalysis.R_USER;
                            if (!safe) NoteUnsafe(tex, reason);
                            if (!joinsGroup) continue;

                            {
                                var key = new UVSlotKey { renderer = r, submesh = i, channel = s.uvChannel };
                                if (!pipe.slotRefs.TryGetValue(key, out var list)) pipe.slotRefs[key] = list = new List<MaterialTextureRef>();

                                var reference = list.Find(x => x.material == mat && x.property == s.property);
                                if (reference == null)
                                {
                                    reference = new MaterialTextureRef
                                    {
                                        material = mat, property = s.property, cls = s.cls, uvChannel = s.uvChannel,
                                        alphaMode = alphaMode, cutoff = cutoff, maskChannelMask = s.maskChannelFlags,
                                    };
                                    list.Add(reference);
                                }
                                else
                                {
                                    // strictest alpha semantics across duplicates / 重复的取最严
                                    if ((int)alphaMode > (int)reference.alphaMode) reference.alphaMode = alphaMode;
                                    reference.cutoff = Mathf.Max(reference.cutoff, cutoff);
                                }
                                if (!reference.textures.Contains(tex)) reference.textures.Add(tex);

                                // animation-driven texture switches on this property / 动画贴图切换并入
                                if (scan.propTextureSets.TryGetValue(path + "|" + s.property, out var variants))
                                    foreach (var v in variants) if (v != null && !reference.textures.Contains(v))
                                        { reference.textures.Add(v); if (!texList.Contains(v)) texList.Add(v); }
                            }
                        }

                        // unsafe textures seen on this material (to whitelist them globally) / 收集不安全贴图
                        foreach (var s in slots)
                            if (!s.safe && mat.GetTexture(s.property) is Texture2D bad)
                                NoteUnsafe(bad, s.unsafeReason);
                    }
                }
            }
            pipe.CancelCheck(progress, ATOL10n.T("ato.stage.discovery"), 0.85f);

            // ---------- dedup / 贴图去重 ----------
            var byKey = new Dictionary<string, TextureInfo>(StringComparer.Ordinal);
            var infoOf = pipe.infoOf;
            foreach (var t in texList)
            {
                var info = BuildTextureInfo(t, out var key);
                if (info == null) continue;
                if (!byKey.TryGetValue(key, out var canonical))
                {
                    canonical = info;
                    canonical.dedupKey = key;
                    byKey[key] = canonical;
                    pipe.textures.Add(canonical);
                }
                infoOf[t] = canonical;
            }
            // whitelist marking (incl. infection through dedup) / 白名单标记（经去重传播）
            foreach (var kv in infoOf)
            {
                var info = kv.Value;
                if (userWhitelistTex.Contains(kv.Key)) info.MarkWhitelist("user whitelist / 用户白名单");
            }
            foreach (var kv in texUnsafe) if (infoOf.TryGetValue(kv.Key, out var info)) info.MarkWhitelist(kv.Value);
            // A texture sharing dedup bucket with a whitelisted one inherits whitelist (spec) / 去重桶传染
            foreach (var info in pipe.textures)
            {
                if (info.whitelisted) continue;
                foreach (var t in texList)
                    if (infoOf.TryGetValue(t, out var got) && ReferenceEquals(got, info) && userWhitelistTex.Contains(t))
                    { info.MarkWhitelist("dedup bucket whitelisted / 去重桶内存在白名单"); break; }
            }

            ATOLog.Info(ATOL10n.T("ato.log.discovery_done", pipe.textures.Count, pipe.slotRefs.Count, texList.Count,
                pipe.textures.FindAll(t => t.whitelisted).Count));
        }

        // ---------------------------------------------------------------- helpers
        private static TextureInfo BuildTextureInfo(Texture2D t, out string key)
        {
            key = null;
            var path = AssetDatabase.GetAssetPath(t);
            var imp = path != null ? AssetImporter.GetAtPath(path) as TextureImporter : null;
            if (imp == null) return null; // no importer → cannot safely optimize (whitelisted later via texUnsafe? keep: skip) / 无导入器

            var sz = ImageCache.EffectiveSize(t, imp);
            var info = new TextureInfo
            {
                source = t, width = sz.x, height = sz.y,
                sRGB = imp.sRGBTexture, isNormalMap = imp.textureType == TextureImporterType.NormalMap,
                filterMode = t.filterMode, wrapMode = t.wrapMode,
                mipmapEnabled = imp.mipmapEnabled, mipStreaming = imp.streamingMipmaps,
                maxTextureSize = imp.maxTextureSize,
                compressionKey = imp.textureCompression + "/" + imp.compressionQuality + "/" + imp.crunchedCompression,
                alphaIsTransparency = imp.alphaIsTransparency,
            };

            // pixel hash (FNV-1a over raw bytes) / 像素哈希
            var raw = ImageCache.GetRaw(t, info.sRGB, out _, out _);
            if (raw == null) return null;
            ulong h1 = 1469598103934665603UL, h2 = 1099511628211UL;
            for (int i = 0; i < raw.Length; i++)
            {
                var px = raw[i];
                h1 = (h1 ^ px.r) * 1099511628211UL; h1 = (h1 ^ px.g) * 1099511628211UL;
                h1 = (h1 ^ px.b) * 1099511628211UL; h1 = (h1 ^ px.a) * 1099511628211UL;
                h2 = (h2 + px.r + ((ulong)px.g << 8) + ((ulong)px.b << 16) + ((ulong)px.a << 24)) * 1099511628211UL;
            }
            var sb = new StringBuilder(96);
            sb.Append(sz.x).Append('x').Append(sz.y).Append('|').Append(h1.ToString("x16")).Append(h2.ToString("x16")).Append('|')
              .Append(info.sRGB).Append(info.isNormalMap).Append(info.filterMode).Append(info.wrapMode)
              .Append(info.mipmapEnabled).Append(info.mipStreaming).Append(info.maxTextureSize)
              .Append(info.compressionKey).Append(info.alphaIsTransparency);
            key = sb.ToString();
            return info;
        }

        private static void CollectAlphaStates(string path, Material mat, AnimationScanResult scan,
            out AlphaMode mode, out float cutoff)
        {
            mode = ShaderAnalysis.GetAlphaMode(mat, out cutoff);
            if (scan.floatMaxOfPathProp.TryGetValue(path + "|_Cutoff", out var cMax))
                cutoff = Mathf.Max(cutoff, cMax);
            foreach (var prop in new[] { "_TransparentMode", "_Mode", "_Surface" })
                if (scan.valuesOfPathProp.TryGetValue(path + "|" + prop, out var set))
                    foreach (var v in set)
                    {
                        var m2 = ShaderAnalysis.ModeValueToAlpha(mat, v);
                        if ((int)m2 > (int)mode) mode = m2;
                        if (m2 == AlphaMode.Cutout && scan.floatMaxOfPathProp.TryGetValue(path + "|_Cutoff", out var c2))
                            cutoff = Mathf.Max(cutoff, c2);
                    }
        }

        private static bool ScanAnimBreaksGuard(string path, string property, AnimationScanResult scan, out string reason)
        {
            reason = null;
            foreach (var pp in scan.uvGuardPropsAnimated)
            {
                if (!pp.StartsWith(path + "|", StringComparison.Ordinal)) continue;
                var animatedProp = pp.Substring(path.Length + 1);
                if (ShaderAnalysis.IsUvGuardProperty(property, animatedProp))
                {
                    reason = "animated UV guard prop: " + animatedProp;
                    return true;
                }
            }
            return false;
        }

        private static bool IsEditorOnlyChain(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
                if (t.CompareTag("EditorOnly")) return true;
            return false;
        }

        internal static string RelativePath(Transform t, Transform root)
        {
            if (t == root || root == null) return "";
            var stack = new Stack<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) stack.Push(cur.name);
            return string.Join("/", stack);
        }

        private static RendererAnimState ComputeAnimState(Renderer r, Transform root, AnimationScanResult scan)
        {
            var st = new RendererAnimState();
            bool currentlyOn = r.gameObject.activeInHierarchy && r.enabled;
            bool animEnabled = false;
            var scale = Vector3.one;
            for (var t = r.transform; t != null && t != root.parent; t = t.parent)
            {
                var p = t == root ? "" : RelativePath(t, root);
                if (scan.enabledPaths.Contains(p)) animEnabled = true;
                if (t == root) break;
                var nodeMax = new Vector3(Mathf.Abs(t.localScale.x), Mathf.Abs(t.localScale.y), Mathf.Abs(t.localScale.z));
                if (scan.scaleMaxByPath.TryGetValue(p, out var animMax))
                {
                    if (animMax.x > float.NegativeInfinity / 2) nodeMax.x = Mathf.Max(nodeMax.x, animMax.x);
                    if (animMax.y > float.NegativeInfinity / 2) nodeMax.y = Mathf.Max(nodeMax.y, animMax.y);
                    if (animMax.z > float.NegativeInfinity / 2) nodeMax.z = Mathf.Max(nodeMax.z, animMax.z);
                }
                scale = Vector3.Scale(scale, nodeMax);
            }
            st.disabledAlways = !currentlyOn && !animEnabled;
            st.maxAnimScale = scale;
            return st;
        }

        // ---------------------------------------------------------------- user whitelist expansion
        private static void GatherTexturesFromObject(Object obj, HashSet<Texture2D> set)
        {
            switch (obj)
            {
                case Texture2D t: set.Add(t); return;
                case Material m:
                    foreach (var p in ShaderAnalysis.TextureProps(m))
                        if (m.GetTexture(p.name) is Texture2D mt) set.Add(mt);
                    return;
                case GameObject go:
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        foreach (var sm in r.sharedMaterials)
                            if (sm != null)
                                foreach (var p in ShaderAnalysis.TextureProps(sm))
                                    if (sm.GetTexture(p.name) is Texture2D gt) set.Add(gt);
                    return;
                case AnimationClip clip:
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var k in AnimationUtility.GetObjectReferenceCurve(clip, b) ?? Array.Empty<ObjectReferenceKeyframe>())
                        {
                            if (k.value is Texture2D ct) set.Add(ct);
                            if (k.value is Material cm)
                                foreach (var p in ShaderAnalysis.TextureProps(cm))
                                    if (cm.GetTexture(p.name) is Texture2D ct2) set.Add(ct2);
                        }
                    return;
                default:
                    // generic: walk serialized texture refs / 通用：遍历序列化贴图引用
                    try
                    {
                        var so = new SerializedObject(obj);
                        var it = so.GetIterator();
                        var enter = true;
                        while (it.NextVisible(enter))
                        {
                            enter = false;
                            if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue is Texture2D t2)
                                set.Add(t2);
                        }
                    }
                    catch { /* serialization edge cases are non-fatal / 序列化边界情况不致命 */ }
                    return;
            }
        }
    }
}

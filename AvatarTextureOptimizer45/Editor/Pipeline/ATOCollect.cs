using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace net.fosa.ato
{
    /// <summary>
    /// 收集阶段 / Collection stage.
    ///
    /// 1. 收集 Avatar 上非 EditorOnly 的 SkinnedMeshRenderer/MeshRenderer, 解析动画绑定后,
    ///    仅保留被启用或有动画启用的渲染器及其材质槽;
    /// 2. 解析动画绑定(材质槽切换/贴图属性动画/ST/Cutoff/渲染模式/启用/缩放/形态键);
    /// 3. 收集材质中经网格UV采样、无ST变换、无特殊用途的 Texture2D, 分类(主色/法线/蒙版/灰度);
    /// 4. 白名单判定(白名单对象引用的全部贴图完全跳过; 同UV的其他贴图跳过图集化);
    /// 5. 按实际像素+导入设置对贴图去重并记录引用(若去重组内含白名单则整组视为白名单).
    ///
    /// 1. Collects non-EditorOnly SMR/MR renderers; after resolving animation bindings, keeps only
    ///    renderers that are enabled or animatable-enabled, with their material slots;
    /// 2. Resolves animation bindings (slot switches, texture props, ST, cutoff, render mode, enable, scale, blendshapes);
    /// 3. Collects mesh-UV-sampled Texture2Ds without ST transforms or special usages, classifying them;
    /// 4. Whitelist detection (textures referenced by whitelisted objects skip everything; UV-sharers skip atlasing);
    /// 5. Deduplicates textures by pixels + import settings, recording references (whitelist contaminates the group).
    /// </summary>
    internal static class ATOCollect
    {
        private static readonly Regex SlotRegex = new Regex(@"^m_Materials\.Array\.data\[(\d+)\]$");
        private static readonly Regex StRegex = new Regex(@"^(.+)_ST\.[xyzw]$");

        // 着色器分析缓存 / shader analysis cache
        private static readonly Dictionary<Shader, Dictionary<string, ATOShaderAnalysis.PropInfo>> ShaderCache =
            new Dictionary<Shader, Dictionary<string, ATOShaderAnalysis.PropInfo>>();

        public static void Run(ATOBuildState state, GameObject avatarRoot)
        {
            Profiler.BeginSample("ATO.Collect");
            var timer = new ATOLog.StageTimer();
            timer.Start();
            var cfg = state.config;
            var anim = state.anim;

            // ---------------------------------------------------------------
            // Step 1: 收集候选渲染器 / collect candidate renderers
            // ---------------------------------------------------------------
            timer.BeginStep("collectRenderers");
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsEditorOnly(renderer.gameObject)) continue;

                var mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : (renderer as MeshRenderer)?.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;

                state.meshes.Add(new ATOMeshInfo
                {
                    renderer = renderer,
                    mesh = mesh,
                    slots = (Material[])renderer.sharedMaterials.Clone()
                });
            }

            // ---------------------------------------------------------------
            // Step 2: 解析动画绑定 / resolve animation bindings
            // ---------------------------------------------------------------
            timer.EndStep();
            timer.BeginStep("resolveBindings");
            ResolveBindings(state, avatarRoot);

            // 仅保留被启用或有动画启用的渲染器 / keep only enabled or animatable-enabled renderers
            for (int i = state.meshes.Count - 1; i >= 0; i--)
            {
                var mi = state.meshes[i];
                bool enabledOrAnimated = mi.renderer.enabled || anim.animatedEnabledRenderers.Contains(mi.renderer);
                bool activeOrAnimated = mi.renderer.gameObject.activeInHierarchy || IsActiveOrAnimated(avatarRoot, mi.renderer.gameObject, anim);
                if (!enabledOrAnimated || !activeOrAnimated)
                {
                    ATOLog.InfoVerbose($"跳过未启用渲染器 / skipping disabled renderer: {mi.renderer.name}");
                    state.meshes.RemoveAt(i);
                }
            }

            timer.EndStep();

            // ---------------------------------------------------------------
            // Step 3: 白名单解析 / whitelist resolution
            // ---------------------------------------------------------------
            timer.BeginStep("whitelist");
            var whitelistGOs = new List<GameObject>();
            var whitelistMats = new HashSet<Material>();
            var whitelistTexs = new HashSet<Texture2D>();
            var whitelistClips = new HashSet<AnimationClip>();
            var whitelistMeshes = new HashSet<Mesh>();

            foreach (var obj in cfg.whitelist)
            {
                if (obj == null) continue;
                switch (obj)
                {
                    case GameObject go: whitelistGOs.Add(go); break;
                    case Material m: whitelistMats.Add(m); break;
                    case Texture2D t: whitelistTexs.Add(t); break;
                    case AnimationClip c: whitelistClips.Add(c); break;
                    case Mesh m2: whitelistMeshes.Add(m2); break;
                }
            }

            // 白名单 clip 引用的材质与贴图 / materials & textures referenced by whitelisted clips
            foreach (var clip in whitelistClips)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!binding.isPPtrCurve) continue;
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (curve == null) continue;
                    foreach (var key in curve)
                    {
                        if (key.value is Material m) whitelistMats.Add(m);
                        if (key.value is Texture2D t) whitelistTexs.Add(t);
                    }
                }
            }

            // ---------------------------------------------------------------
            // Step 4: 收集贴图 / collect textures
            // ---------------------------------------------------------------
            timer.EndStep();
            timer.BeginStep("collectTextures");
            foreach (var mi in state.meshes)
            {
                bool underWhitelistGO = whitelistGOs.Any(go => mi.renderer.transform == go.transform || mi.renderer.transform.IsChildOf(go.transform));
                bool meshWhitelisted = whitelistMeshes.Contains(mi.mesh);

                for (int slot = 0; slot < mi.slots.Length; slot++)
                {
                    var slotMaterial = mi.slots[slot];
                    if (slotMaterial == null) continue;
                    if (underWhitelistGO || meshWhitelisted) whitelistMats.Add(slotMaterial);

                    // 材质槽候选材质(含动画切换) / candidate materials incl. animated switches
                    var candidates = new List<Material> { slotMaterial };
                    if (anim.slotBindings.TryGetValue(mi.renderer, out var bySlot) && bySlot.TryGetValue(slot, out var records))
                    {
                        foreach (var rec in records)
                        {
                            var curve = AnimationUtility.GetObjectReferenceCurve(rec.Clip, rec.Binding);
                            if (curve == null) continue;
                            foreach (var key in curve)
                            {
                                if (key.value is Material cm && !candidates.Contains(cm)) candidates.Add(cm);
                            }
                        }
                    }

                    foreach (var mat in candidates)
                    {
                        if (mat == null) continue;
                        if (!state.byMaterial.TryGetValue(mat, out var matInfo))
                        {
                            matInfo = new ATOMaterialInfo { original = mat, current = mat };
                            state.materialInfos.Add(matInfo);
                            state.byMaterial[mat] = matInfo;
                        }

                        matInfo.slotRefs.Add(new ATOTextureRef { renderer = mi.renderer, slotIndex = slot, material = mat });
                        matInfo.animated = matInfo.animated || anim.renderModeRecords.Count > 0 || anim.cutoffRecords.Count > 0;
                        matInfo.opaque = IsOpaque(mat);
                        if (anim.animatedRenderModeMaterials.Contains(mat)) matInfo.opaque = false;

                        CollectMaterialTextures(state, mi, slot, mat, whitelistMats, whitelistTexs);
                    }
                }
            }

            // ---------------------------------------------------------------
            // Step 5: 应用白名单到贴图 / apply whitelist to textures
            // ---------------------------------------------------------------
            foreach (var t in state.textures)
            {
                if (t.skip == ATOSkip.Full) continue;
                bool wl = whitelistTexs.Contains(t.source);
                if (!wl)
                {
                    foreach (var r in t.refs)
                    {
                        if (r.material != null && whitelistMats.Contains(r.material)) { wl = true; break; }
                        if (r.renderer != null && whitelistGOs.Any(go => r.renderer.transform == go.transform || r.renderer.transform.IsChildOf(go.transform)))
                        {
                            wl = true;
                            break;
                        }
                    }
                }

                if (wl)
                {
                    t.skip = ATOSkip.Full;
                    t.skipReason = ATOSkipReason.Whitelist;
                    t.skipDetail = "whitelist";
                    state.skippedFull++;
                }
            }

            timer.EndStep();

            // ---------------------------------------------------------------
            // Step 6: 贴图去重(像素+导入设置) / texture dedup (pixels + import settings)
            // ---------------------------------------------------------------
            timer.BeginStep("dedup");
            DedupTextures(state);
            timer.EndStep();

            timer.End("收集 Collect");
            Profiler.EndSample();
        }

        private static bool IsActiveOrAnimated(GameObject avatarRoot, GameObject go, ATOAnimAnalysis anim)
        {
            if (go.activeInHierarchy) return true;
            var t = go.transform;
            while (t != null)
            {
                string path = AnimationUtility.CalculateTransformPath(t, avatarRoot.transform);
                if (anim.activeRecords.Any(r => r.Binding.path == path)) return true;
                t = t.parent;
            }

            return false;
        }

        private static void ResolveBindings(ATOBuildState state, GameObject avatarRoot)
        {
            var anim = state.anim;
            var renderersByPath = new Dictionary<string, Renderer>();
            foreach (var mi in state.meshes)
            {
                string path = AnimationUtility.CalculateTransformPath(mi.renderer.transform, avatarRoot.transform);
                if (!renderersByPath.ContainsKey(path)) renderersByPath[path] = mi.renderer;
            }

            // 材质槽切换 / material slot bindings
            foreach (var rec in anim.slotBindingRecords)
            {
                if (!renderersByPath.TryGetValue(rec.Binding.path, out var renderer)) continue;
                var m = SlotRegex.Match(rec.Binding.propertyName);
                if (!m.Success) continue;
                int slot = int.Parse(m.Groups[1].Value);
                if (!anim.slotBindings.TryGetValue(renderer, out var bySlot))
                    anim.slotBindings[renderer] = bySlot = new Dictionary<int, List<ATOAnimRecord>>();
                if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = new List<ATOAnimRecord>();
                list.Add(rec);
            }

            // 贴图属性动画 / texture property animations
            foreach (var rec in anim.texturePropRecords)
            {
                anim.texturePropBindings[rec.Binding] = rec.Clip;
                if (!renderersByPath.TryGetValue(rec.Binding.path, out var renderer)) continue;
                var curve = AnimationUtility.GetObjectReferenceCurve(rec.Clip, rec.Binding);
                if (curve == null) continue;
                foreach (var key in curve)
                {
                    if (!(key.value is Texture2D tex)) continue;
                    if (!state.byTexture.TryGetValue(tex, out var ti)) ti = CreateTextureInfo(state, tex);
                    ti.refs.Add(new ATOTextureRef
                    {
                        renderer = renderer,
                        clip = rec.Clip,
                        binding = rec.Binding,
                        property = rec.Binding.propertyName
                    });
                }
            }

            // ST 动画 / ST animations
            foreach (var rec in anim.stRecords)
            {
                if (!renderersByPath.TryGetValue(rec.Binding.path, out var renderer)) continue;
                var m = StRegex.Match(rec.Binding.propertyName);
                if (m.Success) anim.stAnimatedProps.Add((renderer, m.Groups[1].Value));
            }

            // Cutoff 动画 / cutoff animations
            foreach (var rec in anim.cutoffRecords)
            {
                if (!renderersByPath.TryGetValue(rec.Binding.path, out var renderer)) continue;
                var curve = AnimationUtility.GetEditorCurve(rec.Clip, rec.Binding);
                if (curve == null) continue;
                foreach (var mi in state.meshes)
                {
                    if (mi.renderer != renderer) continue;
                    foreach (var mat in mi.slots)
                    {
                        if (mat == null) continue;
                        if (!anim.animatedCutoffs.TryGetValue(mat, out var list))
                            anim.animatedCutoffs[mat] = list = new List<float>();
                        foreach (var key in curve.keys) list.Add(key.value);
                    }
                }
            }

            // 渲染模式动画 / render-mode animations
            foreach (var rec in anim.renderModeRecords)
            {
                if (!renderersByPath.TryGetValue(rec.Binding.path, out var renderer)) continue;
                foreach (var mi in state.meshes)
                {
                    if (mi.renderer != renderer) continue;
                    foreach (var mat in mi.slots)
                    {
                        if (mat != null) anim.animatedRenderModeMaterials.Add(mat);
                    }
                }
            }

            // 渲染器启用 / renderer enabled
            foreach (var rec in anim.enabledRecords)
            {
                if (renderersByPath.TryGetValue(rec.Binding.path, out var renderer))
                    anim.animatedEnabledRenderers.Add(renderer);
            }

            // 物体启用 / GameObject active
            foreach (var rec in anim.activeRecords)
            {
                var t = anim.ResolvePath(avatarRoot, rec.Binding.path);
                if (t != null) anim.animatedActiveObjects.Add(t.gameObject);
            }

            // 缩放动画 / scale animations
            foreach (var rec in anim.scaleRecords)
            {
                var t = anim.ResolvePath(avatarRoot, rec.Binding.path);
                if (t == null) continue;
                if (!anim.scaleBindings.TryGetValue(t, out var list)) anim.scaleBindings[t] = list = new List<EditorCurveBinding>();
                list.Add(rec.Binding);
            }

            // 形态键动画 / blendshape animations
            foreach (var rec in anim.blendShapeRecords)
            {
                if (!renderersByPath.TryGetValue(rec.Binding.path, out var renderer)) continue;
                if (!anim.blendShapeBindings.TryGetValue(renderer, out var byName))
                    anim.blendShapeBindings[renderer] = byName = new Dictionary<string, List<EditorCurveBinding>>();
                string shapeName = rec.Binding.propertyName.Substring("blendShape.".Length);
                if (!byName.TryGetValue(shapeName, out var list)) byName[shapeName] = list = new List<EditorCurveBinding>();
                list.Add(rec.Binding);
            }
        }

        private static void CollectMaterialTextures(ATOBuildState state, ATOMeshInfo mi, int slot, Material mat,
            HashSet<Material> whitelistMats, HashSet<Texture2D> whitelistTexs)
        {
            var shader = mat.shader;
            if (shader == null) return;

            if (!ShaderCache.TryGetValue(shader, out var props))
            {
                props = ATOShaderAnalysis.Analyze(shader);
                ShaderCache[shader] = props;
            }

            if (props == null)
            {
                ATOLog.Warn($"无法分析着色器属性表, 其贴图将跳过优化 / cannot analyze shader '{shader.name}' (material '{mat.name}'), its textures skip optimization");
                MarkMaterialTexturesUnknown(state, mi, slot, mat);
                return;
            }

            int propCount = ShaderUtil.GetPropertyCount(shader);
            for (int p = 0; p < propCount; p++)
            {
                if (ShaderUtil.GetPropertyType(shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string propName = ShaderUtil.GetPropertyName(shader, p);
                if (!mat.HasProperty(propName)) continue;
                var tex = mat.GetTexture(propName) as Texture2D;
                if (tex == null) continue;

                if (!props.TryGetValue(propName, out var propInfo))
                {
                    propInfo = new ATOShaderAnalysis.PropInfo { name = propName, category = ATOTextureCategory.Color, detail = "unlisted prop" };
                }

                if (!state.byTexture.TryGetValue(tex, out var ti)) ti = CreateTextureInfo(state, tex);

                // 记录UV通道 / record the UV channel
                ti.uvChannel = propInfo.uvChannel;

                // 多种用途取最严苛类别(法线>蒙版>灰度>主色) / strictest category wins across usages
                if (CategoryRank(propInfo.category) > CategoryRank(ti.category)) ti.category = propInfo.category;

                if (!propInfo.meshUvSampled)
                {
                    ti.skip = ATOSkip.Full;
                    ti.skipReason = ATOSkipReason.SpecialUsage;
                    ti.skipDetail = $"special usage: {propName}";
                }

                bool stAnimated = state.anim.stAnimatedProps.Contains((mi.renderer, propName));
                if (stAnimated)
                {
                    ti.skip = ATOSkip.Full;
                    ti.skipReason = ATOSkipReason.StTransform;
                    ti.skipDetail = $"ST animated: {propName}";
                }
                else if (ATOShaderAnalysis.HasSTTransform(mat, propName))
                {
                    ti.skip = ATOSkip.Full;
                    ti.skipReason = ATOSkipReason.StTransform;
                    ti.skipDetail = $"ST transform on material: {propName}";
                }

                if (whitelistMats.Contains(mat) || whitelistTexs.Contains(tex))
                {
                    ti.skip = ATOSkip.Full;
                    ti.skipReason = ATOSkipReason.Whitelist;
                    ti.skipDetail = "whitelist";
                }

                ti.refs.Add(new ATOTextureRef
                {
                    renderer = mi.renderer,
                    slotIndex = slot,
                    material = mat,
                    property = propName
                });
            }
        }

        private static int CategoryRank(ATOTextureCategory c)
        {
            switch (c)
            {
                case ATOTextureCategory.Normal: return 4;
                case ATOTextureCategory.Mask: return 3;
                case ATOTextureCategory.Grayscale: return 2;
                default: return 1;
            }
        }

        private static void MarkMaterialTexturesUnknown(ATOBuildState state, ATOMeshInfo mi, int slot, Material mat)
        {
            int propCount = ShaderUtil.GetPropertyCount(mat.shader);
            for (int p = 0; p < propCount; p++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string propName = ShaderUtil.GetPropertyName(mat.shader, p);
                if (!mat.HasProperty(propName)) continue;
                var tex = mat.GetTexture(propName) as Texture2D;
                if (tex == null) continue;
                if (!state.byTexture.TryGetValue(tex, out var ti)) ti = CreateTextureInfo(state, tex);
                ti.skip = ATOSkip.Full;
                ti.skipReason = ATOSkipReason.UnknownShaderUsage;
                ti.skipDetail = $"shader '{mat.shader.name}' unanalyzable";
                ti.refs.Add(new ATOTextureRef { renderer = mi.renderer, slotIndex = slot, material = mat, property = propName });
            }
        }

        private static ATOTextureInfo CreateTextureInfo(ATOBuildState state, Texture2D tex)
        {
            var ti = new ATOTextureInfo { source = tex };
            ti.assetPath = AssetDatabase.GetAssetPath(tex);
            ti.width = tex.width;
            ti.height = tex.height;
            ti.sRGB = ATOTextureIO.IsSRGB(tex);
            ti.filterMode = tex.filterMode;
            ti.wrapU = tex.wrapModeU;
            ti.wrapV = tex.wrapModeV;
            ti.mipmapEnabled = tex.mipmapCount > 1;
            ti.importerKey = BuildImporterKey(tex);
            state.textures.Add(ti);
            state.byTexture[tex] = ti;
            return ti;
        }

        private static string BuildImporterKey(Texture2D tex)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (importer == null) return "importer:null";
            var sb = new StringBuilder();
            sb.Append("srgb=").Append(importer.sRGBTexture).Append(';');
            sb.Append("wrapU=").Append(importer.wrapModeU).Append(";wrapV=").Append(importer.wrapModeV).Append(';');
            sb.Append("filter=").Append(importer.filterMode).Append(';');
            sb.Append("mip=").Append(importer.mipmapEnabled).Append(';');
            sb.Append("stream=").Append(importer.streamingMipmaps).Append(';');
            sb.Append("crunch=").Append(importer.crunchedCompression).Append(':').Append(importer.compressionQuality).Append(';');
            sb.Append("readable=").Append(importer.isReadable).Append(';');
            sb.Append("aniso=").Append(importer.anisoLevel).Append(';');
            sb.Append("npot=").Append(importer.npotScale).Append(';');
            foreach (var target in new[] { BuildTarget.StandaloneWindows64, BuildTarget.Android, BuildTarget.iOS })
            {
                if (importer.GetPlatformTextureSettings(target.ToString(), out int maxSize, out var fmt))
                {
                    sb.Append(target).Append('=').Append(fmt).Append('/').Append(maxSize).Append(';');
                }
            }

            return sb.ToString();
        }

        private static bool IsOpaque(Material mat)
        {
            if (mat == null || mat.shader == null) return false;
            if (mat.renderQueue > 2500) return false;
            if (mat.HasProperty("_Mode"))
            {
                float mode = mat.GetFloat("_Mode");
                if (mode > 1.5f) return false; // Cutout=1, Fade/Transparent=2/3 / cutout & transparent
            }

            foreach (var kw in mat.shaderKeywords)
            {
                if (kw.Contains("ALPHABLEND") || kw.Contains("_FADE") || kw.Contains("_TRANSPARENT")) return false;
            }

            return true;
        }

        private static bool IsEditorOnly(GameObject go)
        {
            var t = go.transform;
            while (t != null)
            {
                if (t.CompareTag("EditorOnly")) return true;
                t = t.parent;
            }

            return false;
        }

        // ============================================================================
        // 去重 / Deduplication
        // ============================================================================
        private static void DedupTextures(ATOBuildState state)
        {
            var groups = new Dictionary<string, List<ATOTextureInfo>>();
            foreach (var ti in state.textures)
            {
                ti.contentHash = HashPixels(ti);
                if (ti.contentHash == null) continue;
                string key = ti.contentHash + "|" + ti.importerKey;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<ATOTextureInfo>();
                list.Add(ti);
            }

            foreach (var kv in groups)
            {
                if (kv.Value.Count <= 1) continue;
                var rep = kv.Value[0];
                bool anyWhitelisted = kv.Value.Any(t => t.skip == ATOSkip.Full);
                ATOLog.InfoVerbose($"去重: {kv.Value.Count} 张相同贴图(内容+导入设置)合并 / dedup: {kv.Value.Count} identical textures merged into {rep.source.name}");

                foreach (var t in kv.Value)
                {
                    if (ReferenceEquals(t, rep)) continue;
                    t.dedupOf = rep;
                    state.byTexture[t.source] = rep;
                    rep.refs.AddRange(t.refs);
                }

                if (anyWhitelisted)
                {
                    foreach (var t in kv.Value)
                    {
                        t.skip = ATOSkip.Full;
                        t.skipReason = ATOSkipReason.Whitelist;
                        t.skipDetail = "dedup group contains whitelisted texture";
                    }
                }
            }
        }

        private static string HashPixels(ATOTextureInfo ti)
        {
            try
            {
                var readable = ATOTextureIO.EnsureReadable(ti);
                if (readable == null)
                {
                    ti.skip = ATOSkip.Full;
                    ti.skipReason = ATOSkipReason.NonReadable;
                    ti.skipDetail = "pixels unreadable";
                    return null;
                }

                var raw = readable.GetRawTextureData<byte>();
                using var md5 = MD5.Create();
                const int chunk = 1 << 20;
                var buf = new byte[Math.Min(chunk, raw.Length)];
                int offset = 0;
                while (offset < raw.Length)
                {
                    int n = Math.Min(chunk, raw.Length - offset);
                    for (int i = 0; i < n; i++) buf[i] = raw[offset + i];
                    offset += md5.TransformBlock(buf, 0, n, null, 0);
                }

                md5.TransformFinalBlock(buf, 0, 0);
                var sb = new StringBuilder();
                foreach (var b in md5.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch (Exception e)
            {
                ATOLog.Warn($"贴图哈希失败 / failed to hash {ti.source.name}: {e.Message}");
                return null;
            }
            finally
            {
                // 哈希完立即释放, 控制内存峰值 / release right away to bound peak memory
                ATOTextureIO.ReleaseReadable(ti);
            }
        }
    }

    /// <summary>
    /// 贴图像素读取工具 / Texture pixel IO helpers.
    /// 不可读的贴图经 GPU 回读到临时 RenderTexture(参考 avatar-compressor 的验证过的做法).
    /// Non-readable textures are read back through a temporary RenderTexture (mirrors avatar-compressor's validated approach).
    /// </summary>
    internal static class ATOTextureIO
    {
        public static bool IsSRGB(Texture2D tex)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            return importer != null && importer.sRGBTexture;
        }

        /// <summary>获取可读贴图(缓存于 ti.readable, 由调用方在合适时机销毁) / Returns a readable texture (cached on ti.readable).</summary>
        public static Texture2D EnsureReadable(ATOTextureInfo ti)
        {
            if (ti.readable != null) return ti.readable;
            var src = ti.source;
            if (src == null) return null;

            if (src.isReadable)
            {
                ti.readable = src;
                ti.readableOwned = false;
                return src;
            }

            // GPU 回读 / GPU readback
            var prevActive = RenderTexture.active;
            var prevSrgb = GL.sRGBWrite;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                ti.sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            try
            {
                GL.sRGBWrite = ti.sRGB;
                Graphics.Blit(src, rt);
                var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                var old = RenderTexture.active;
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                copy.Apply(false, false);
                RenderTexture.active = old;
                GL.sRGBWrite = prevSrgb;
                ti.readable = copy;
                ti.readableOwned = true;
                return copy;
            }
            catch (Exception e)
            {
                ATOLog.Warn($"贴图回读失败 / texture readback failed for {src.name}: {e.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                GL.sRGBWrite = prevSrgb;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>读取贴图像素矩形 / Reads a pixel rect from the texture.</summary>
        public static Color32[] ReadRect(ATOTextureInfo ti, Rect pixelRect)
        {
            var tex = EnsureReadable(ti);
            if (tex == null) return null;
            int x = Mathf.Clamp(Mathf.FloorToInt(pixelRect.x), 0, tex.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(pixelRect.y), 0, tex.height - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt(pixelRect.width), 1, tex.width - x);
            int h = Mathf.Clamp(Mathf.CeilToInt(pixelRect.height), 1, tex.height - y);
            try
            {
                return tex.GetPixels32(x, y, w, h);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>释放可读拷贝(打包/应用阶段会按需重新回读) / Releases the readable copy (re-read on demand later).</summary>
        public static void ReleaseReadable(ATOTextureInfo tex)
        {
            if (tex == null) return;
            if (tex.readableOwned && tex.readable != null)
            {
                UnityEngine.Object.DestroyImmediate(tex.readable);
            }

            tex.readable = null;
            tex.readableOwned = false;
        }

        /// <summary>释放全部可读拷贝(取消/结束时) / Releases all readable copies (on cancel/finish).</summary>
        public static void ReleaseAll(ATOBuildState state)
        {
            if (state == null) return;
            foreach (var tex in state.textures)
            {
                ReleaseReadable(tex);
            }
        }
    }
}
